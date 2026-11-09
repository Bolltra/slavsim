using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Mx43Sim.Core.Cfg;

/// <summary>
/// Minimal XLSX reader: extracts a single worksheet as a string[,] grid.
/// Uses only System.IO.Compression + System.Xml — no NuGet dependencies.
///
/// The Mx43 adresslista.xlsx is a small one-sheet workbook that lists the
/// holding-register address assigned to each (line, detector) pair. We
/// don't need formulas, styling or anything fancy — just the cell text
/// in row-major order.
/// </summary>
public static class MiniXlsx
{
    public static string[,] ReadSheet(string path, int sheetIndex = 0)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        // 1. Read shared strings (if any)
        var shared = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry is not null)
        {
            using var s = ssEntry.Open();
            using var xr = XmlReader.Create(s);
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                {
                    shared.Add(xr.ReadElementContentAsString());
                }
            }
        }
        // 2. Find sheet{N}.xml
        var sheetEntry = zip.GetEntry($"xl/worksheets/sheet{sheetIndex + 1}.xml");
        if (sheetEntry is null) throw new InvalidDataException($"sheet{sheetIndex + 1}.xml not found");
        using var s2 = sheetEntry.Open();
        using var xr2 = XmlReader.Create(s2);
        var rows = new Dictionary<int, Dictionary<int, string>>();
        int maxCol = 0, maxRow = 0;
        int curRow = -1, curCol = -1;
        string cellType = "";
        while (xr2.Read())
        {
            if (xr2.NodeType == XmlNodeType.Element)
            {
                switch (xr2.LocalName)
                {
                    case "row": curRow = int.Parse(xr2.GetAttribute("r")!); break;
                    case "c":
                        var ref_ = xr2.GetAttribute("r")!;
                        ParseCellRef(ref_, out curCol, out _);
                        cellType = xr2.GetAttribute("t") ?? "";
                        if (!rows.TryGetValue(curRow, out var rdict))
                            rows[curRow] = rdict = new Dictionary<int, string>();
                        break;
                    case "v":
                        var raw = xr2.ReadElementContentAsString();
                        string val = (cellType == "s" && int.TryParse(raw, out var idx))
                            ? shared[idx]
                            : raw;
                        if (curRow >= 0 && curCol >= 0)
                        {
                            rows[curRow][curCol] = val;
                            if (curRow > maxRow) maxRow = curRow;
                            if (curCol > maxCol) maxCol = curCol;
                        }
                        break;
                    case "is":
                        // Inline string: read <t> child
                        if (xr2.ReadToDescendant("t"))
                        {
                            var t = xr2.ReadElementContentAsString();
                            if (curRow >= 0 && curCol >= 0)
                            {
                                rows[curRow][curCol] = t;
                                if (curRow > maxRow) maxRow = curRow;
                                if (curCol > maxCol) maxCol = curCol;
                            }
                        }
                        break;
                }
            }
        }
        var grid = new string[maxRow + 1, maxCol + 1];
        foreach (var (r, dict) in rows)
            foreach (var (c, v) in dict)
                grid[r, c] = v;
        return grid;
    }

    /// <summary>Write rows of text as a single-sheet .xlsx (numbers stored as numbers).</summary>
    public static void WriteSheet(string path, IReadOnlyList<string[]> rows)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Detektorer\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append("<row r=\"").Append(r + 1).Append("\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                string cellRef = ColName(c) + (r + 1);
                string v = rows[r][c] ?? "";
                if (r > 0 && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    sb.Append("<c r=\"").Append(cellRef).Append("\"><v>").Append(v).Append("</v></c>");
                else
                    sb.Append("<c r=\"").Append(cellRef).Append("\" t=\"inlineStr\"><is><t>")
                      .Append(System.Security.SecurityElement.Escape(v)).Append("</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        Add("xl/worksheets/sheet1.xml", sb.ToString());
    }

    private static string ColName(int c)
    {
        var s = "";
        for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
        return s;
    }

    private static void ParseCellRef(string cellRef, out int col1Based, out int row1Based)
    {
        // A1 -> (1, 1), B12 -> (2, 12)
        int i = 0;
        int col = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            col = col * 26 + (char.ToUpper(cellRef[i]) - 'A' + 1);
            i++;
        }
        int row = int.Parse(cellRef.AsSpan(i));
        col1Based = col;
        row1Based = row;
    }
}
