using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Mx43Sim.WeintekGenerator;

internal static class DataSamplingGenerator
{
    internal const int MaximumCustomizedFileNameLength = 25;
    internal const int MinimumSamplingIntervalMilliseconds = 100;
    internal const int MaximumSamplingIntervalMilliseconds = 7_200_000;
    internal const int MaximumPreservationFiles = 65_535;
    internal const int MaximumAutoSyncMinutes = 1_440;
    private const int DateFileNamePrefixLength = 9;

    private const string ContentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\" PartName=\"/xl/workbook.xml\"/><Override ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\" PartName=\"/xl/styles.xml\"/><Override ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\" PartName=\"/xl/worksheets/sheet1.xml\"/><Override ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\" PartName=\"/xl/sharedStrings.xml\"/></Types>";
    private const string RootRelationships = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"xl/workbook.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Id=\"rId1\"/></Relationships>";
    private const string WorkbookRelationships = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"styles.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Id=\"rId1\"/><Relationship Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Id=\"rId2\"/><Relationship Target=\"sharedStrings.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Id=\"rId3\"/></Relationships>";
    private const string Workbook = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><workbookPr/><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets><sheet name=\"sheet1\" sheetId=\"1\" r:id=\"rId2\"/></sheets><calcPr fullCalcOnLoad=\"true\"/></workbook>";
    private const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    internal static void Write(string outputDir, IReadOnlyList<DetectorPlan> detectors, string storageKey, SamplingPolicy policy)
        => File.WriteAllBytes(Path.Combine(outputDir, "data-sampling.xlsx"), CreateWorkbook(detectors, storageKey, policy));

    internal static byte[] CreateWorkbook(IReadOnlyList<DetectorPlan> detectors, string storageKey, SamplingPolicy? policy = null)
    {
        policy ??= new SamplingPolicy();
        ValidatePolicy(policy);
        SamplingGroup[] groups = CreateGroups(detectors, storageKey);
        List<SamplingRow> rows = CreateRows(groups, policy);
        rows = rows.Select(row => new SamplingRow(row.Cells
            .Select(cell => cell with { Value = SanitizeXml(cell.Value) })
            .ToArray())).ToList();
        var sharedStrings = new List<string>();
        var sharedStringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SamplingCell cell in rows.SelectMany(row => row.Cells))
        {
            if (sharedStringIndexes.ContainsKey(cell.Value)) continue;
            sharedStringIndexes[cell.Value] = sharedStrings.Count;
            sharedStrings.Add(cell.Value);
        }

        string sharedStringsXml = RenderSharedStrings(rows.Sum(row => row.Cells.Count), sharedStrings);
        string worksheetXml = RenderWorksheet(rows, sharedStringIndexes);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypes);
            WriteEntry(archive, "_rels/.rels", RootRelationships);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
            WriteEntry(archive, "xl/sharedStrings.xml", sharedStringsXml);
            WriteEntry(archive, "xl/styles.xml", Styles);
            WriteEntry(archive, "xl/workbook.xml", Workbook);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", worksheetXml);
        }
        return output.ToArray();
    }

    internal static SamplingGroup[] CreateGroups(IReadOnlyList<DetectorPlan> detectors, string storageKey)
    {
        if (detectors.Count == 0) throw new ArgumentException("At least one detector is required for Data Sampling.", nameof(detectors));
        var detectorGroups = new List<List<DetectorPlan>>();
        foreach (DetectorPlan detector in detectors)
        {
            DetectorPlan? previous = detectorGroups.Count == 0 ? null : detectorGroups[^1][^1];
            if (previous is null || detector.ConfigRegister != previous.ConfigRegister + 1 || detector.MeasurementRegister != previous.MeasurementRegister + 1)
            {
                detectorGroups.Add(new List<DetectorPlan>());
            }
            detectorGroups[^1].Add(detector);
        }

        string storageSlug = Slug(storageKey);
        return detectorGroups.Select((detectorGroup, index) =>
        {
            int groupNumber = index + 1;
            int baseRegister = detectorGroup[0].ConfigRegister;
            string suffix = $"-{ShortHash(storageKey)}-R{baseRegister}";
            string folderName = StableName(storageSlug, suffix, MaximumCustomizedFileNameLength - DateFileNamePrefixLength);
            return new SamplingGroup(groupNumber, baseRegister, detectorGroup.ToArray(), folderName, $"%Y-%m-%d_{folderName}");
        }).ToArray();
    }

    private static List<SamplingRow> CreateRows(IReadOnlyList<SamplingGroup> groups, SamplingPolicy policy)
    {
        var rows = new List<SamplingRow> { Row(('A', "Version: 4")) };
        foreach (SamplingGroup group in groups)
        {
            rows.Add(Row(('A', "Data Sampling"), ('B', "")));
            rows.Add(Row(('A', ""), ('B', "Sample Mode"), ('C', "Time-based"), ('D', $"{policy.IntervalMilliseconds} ms")));
            rows.Add(Row(('A', ""), ('B', ""), ('C', "High Priority: Off")));
            rows.Add(Row(
                ('A', ""), ('B', "Read Address"), ('C', "MX43"), ('D', "3x"),
                ('E', "System Tag: Off"), ('F', "User-defined Tag: Off"),
                ('G', group.BaseConfigRegister.ToString(CultureInfo.InvariantCulture)), ('H', "IDX: 1")));
            rows.Add(Row(('A', ""), ('B', "Data Record")));
            for (int i = 0; i < group.Detectors.Count; i++)
            {
                DetectorPlan detector = group.Detectors[i];
                var cells = new List<SamplingCell>
                {
                    new('A', ""),
                    new('B', ""),
                    new('C', detector.Label),
                    new('D', "16-bit Signed"),
                    new('E', $"Left of decimal Pt. {DigitsLeft(detector)}"),
                    new('F', $"Right of decimal Pt. {detector.DisplayFormat}"),
                    new('G', "Leading zero Off"),
                };
                if (i == 0)
                {
                    cells.AddRange([
                        new('H', "MX43"),
                        new('I', "3x"),
                        new('J', "System Tag: Off"),
                        new('K', "User-defined Tag: Off"),
                        new('L', group.BaseConfigRegister.ToString(CultureInfo.InvariantCulture)),
                        new('M', "IDX: 1"),
                    ]);
                }
                rows.Add(new SamplingRow(cells));
            }
            rows.Add(Row(('A', ""), ('B', "History File"), ('C', "USB Disk")));
            rows.Add(Row(('A', ""), ('B', ""), ('C', "Preservation Limit"), ('D', $"{policy.PreservationFiles} day(s)/file(s)")));
            rows.Add(Row(('A', ""), ('B', ""), ('C', "Sync Status Address: Off")));
            rows.Add(Row(('A', ""), ('B', ""), ('C', "Folder Name"), ('D', group.FolderName)));
            rows.Add(Row(('A', ""), ('B', ""), ('C', "Customized File"), ('D', "Automatic Mode")));
            rows.Add(Row(('A', ""), ('B', ""), ('C', ""), ('D', "File Name"), ('E', group.FileName)));
            rows.Add(Row(('A', ""), ('B', ""), ('C', ""), ('D', "Sort By"), ('E', "File name")));
            rows.Add(Row(('A', ""), ('B', ""), ('C', "Auto Sync Periodically"), ('D', $"{policy.AutoSyncMinutes} min(s)")));
        }
        return rows;
    }

    private static int DigitsLeft(DetectorPlan detector)
    {
        int integerPart = Math.Abs(detector.RangeRaw) / Math.Max(1, detector.ScaleDivisor);
        return Math.Max(1, integerPart.ToString(CultureInfo.InvariantCulture).Length);
    }

    private static string Slug(string value)
    {
        var sb = new StringBuilder();
        bool pendingDash = false;
        foreach (char c in value)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }
        return sb.Length == 0 ? "MX43" : sb.ToString();
    }

    private static string StableName(string slug, string suffix, int maximumLength)
    {
        int prefixLength = Math.Max(1, maximumLength - suffix.Length);
        string prefix = slug[..Math.Min(slug.Length, prefixLength)].TrimEnd('-');
        if (prefix.Length == 0) prefix = "M";
        return prefix + suffix;
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4));
    }

    internal static void ValidatePolicy(SamplingPolicy policy)
    {
        if (policy.IntervalMilliseconds is < MinimumSamplingIntervalMilliseconds or > MaximumSamplingIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(policy), $"Sampling interval must be {MinimumSamplingIntervalMilliseconds}..{MaximumSamplingIntervalMilliseconds} ms.");
        if (policy.PreservationFiles is < 1 or > MaximumPreservationFiles)
            throw new ArgumentOutOfRangeException(nameof(policy), $"History file count must be 1..{MaximumPreservationFiles}.");
        if (policy.AutoSyncMinutes is < 1 or > MaximumAutoSyncMinutes)
            throw new ArgumentOutOfRangeException(nameof(policy), $"Auto-sync interval must be 1..{MaximumAutoSyncMinutes} minutes.");
    }

    private static SamplingRow Row(params (char Column, string Value)[] cells)
        => new(cells.Select(cell => new SamplingCell(cell.Column, cell.Value)).ToArray());

    private static string RenderSharedStrings(int count, IReadOnlyList<string> values)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"");
        sb.Append(count.ToString(CultureInfo.InvariantCulture));
        sb.Append("\" uniqueCount=\"");
        sb.Append(values.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append("\">");
        foreach (string value in values)
        {
            if (value.Length == 0)
            {
                sb.Append("<si><t/></si>");
            }
            else
            {
                if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
                    sb.Append("<si><t xml:space=\"preserve\">").Append(EscapeXml(value)).Append("</t></si>");
                else
                    sb.Append("<si><t>").Append(EscapeXml(value)).Append("</t></si>");
            }
        }
        sb.Append("</sst>");
        return sb.ToString();
    }

    private static string RenderWorksheet(IReadOnlyList<SamplingRow> rows, IReadOnlyDictionary<string, int> sharedStringIndexes)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheetFormatPr defaultRowHeight=\"15\"/><sheetData>");
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            int rowNumber = rowIndex + 1;
            sb.Append("<row r=\"");
            sb.Append(rowNumber.ToString(CultureInfo.InvariantCulture));
            sb.Append("\">");
            foreach (SamplingCell cell in rows[rowIndex].Cells)
            {
                sb.Append("<c r=\"");
                sb.Append(cell.Column);
                sb.Append(rowNumber.ToString(CultureInfo.InvariantCulture));
                sb.Append("\" t=\"s\"><v>");
                sb.Append(sharedStringIndexes[cell.Value].ToString(CultureInfo.InvariantCulture));
                sb.Append("</v></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string SanitizeXml(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[++i]);
            }
            else
            {
                sb.Append(XmlConvert.IsXmlChar(c) ? c : '?');
            }
        }
        return sb.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    internal sealed record SamplingGroup(
        int Number,
        int BaseConfigRegister,
        IReadOnlyList<DetectorPlan> Detectors,
        string FolderName,
        string FileName);

    internal sealed record SamplingPolicy(
        int IntervalMilliseconds = 1000,
        int PreservationFiles = 90,
        int AutoSyncMinutes = 60);

    private sealed record SamplingRow(IReadOnlyList<SamplingCell> Cells);
    private sealed record SamplingCell(char Column, string Value);
}
