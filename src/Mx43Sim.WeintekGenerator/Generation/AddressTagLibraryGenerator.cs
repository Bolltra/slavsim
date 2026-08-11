using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mx43Sim.WeintekGenerator;

internal static class AddressTagLibraryGenerator
{
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static void Write(string outputDir, IReadOnlyList<DetectorPlan> detectors)
    {
        string tagDirectory = Path.Combine(outputDir, "tags");
        File.WriteAllText(
            Path.Combine(tagDirectory, "mx43-address-tag-library.csv"),
            RenderMx43(detectors),
            CsvEncoding);
        File.WriteAllText(
            Path.Combine(tagDirectory, "local-lw-address-tag-library.csv"),
            RenderLocal(detectors),
            CsvEncoding);
    }

    internal static string RenderMx43(IReadOnlyList<DetectorPlan> detectors)
    {
        var rows = new List<string>(detectors.Count * 3);
        foreach (var d in detectors)
        {
            rows.Add(Row($"info-D{d.ScreenNo}", "MX43", "3x", d.ConfigRegister, "16-bit Signed"));
            rows.Add(Row($"meas-D{d.ScreenNo}", "MX43", "3x", d.MeasurementRegister, "16-bit Signed"));
            rows.Add(Row($"alarm-D{d.ScreenNo}", "MX43", "3x", d.AlarmRegister, "16-bit Unsigned"));
        }
        return JoinRows(rows);
    }

    internal static string RenderLocal(IReadOnlyList<DetectorPlan> detectors)
    {
        var rows = new List<string>(1 + detectors.Count * 16)
        {
            Row("Project-Title", "cMT", "LW", WeintekLayout.ProjectTitleLw, "16-bit Unsigned"),
        };
        foreach (var d in detectors)
        {
            rows.Add(Row($"Det{d.ScreenNo}-Name", "cMT", "LW", d.LwName, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Status", "cMT", "LW", d.LwStatus, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-FullGas", "cMT", "LW", d.LwFullGas, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Range", "cMT", "LW", d.LwRange, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-DisplayFormat", "cMT", "LW", d.LwDisplayFormat, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Unit", "cMT", "LW", d.LwUnit, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-AbbGas", "cMT", "LW", d.LwShortGas, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Alarm1", "cMT", "LW", d.LwAlarm1, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Alarm2", "cMT", "LW", d.LwAlarm2, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Alarm3", "cMT", "LW", d.LwAlarm3, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-Measurement", "cMT", "LW", d.LwMeasurement, "16-bit Signed"));
            rows.Add(Row($"Det{d.ScreenNo}-AlarmBits", "cMT", "LW", d.LwAlarmBits, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-ScaledInteger", "cMT", "LW", d.LwScaledInteger, "16-bit Signed"));
            rows.Add(Row($"Det{d.ScreenNo}-ScaleDivisor", "cMT", "LW", d.LwScaleDivisor, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-AlarmLevelCount", "cMT", "LW", d.LwAlarmLevelCount, "16-bit Unsigned"));
            rows.Add(Row($"Det{d.ScreenNo}-AlarmSeverity", "cMT", "LW", d.LwAlarmSeverity, "16-bit Unsigned"));
        }
        return JoinRows(rows);
    }

    private static string Row(string name, string device, string kind, int address, string dataType)
        => string.Join(',', name, device, kind, address.ToString(CultureInfo.InvariantCulture), "", dataType);

    private static string JoinRows(IReadOnlyList<string> rows)
        => rows.Count == 0 ? "" : string.Join("\r\n", rows) + "\r\n";
}
