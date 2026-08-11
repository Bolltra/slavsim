using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mx43Sim.Core.Modbus;

namespace Mx43Sim.WeintekGenerator;

internal static class MacroGenerator
{
    private static readonly int[] ExtractorMacroIds = [5, 7, 8, 9];

    internal static void WriteConfigExtractors(string outputDir, IReadOnlyList<DetectorPlan> detectors)
    {
        string macroDirectory = Path.Combine(outputDir, "macros");
        foreach (string staleFile in Directory.GetFiles(macroDirectory, "config-extractor_*.txt"))
            File.Delete(staleFile);
        foreach (int id in ExtractorMacroIds)
        {
            foreach (string staleFile in Directory.GetFiles(macroDirectory, $"{id}_*.ebm"))
                File.Delete(staleFile);
        }

        var groups = detectors.Chunk(8).ToArray();
        for (int groupIndex = 0; groupIndex < ExtractorMacroIds.Length; groupIndex++)
        {
            int first = groupIndex * 8 + 1;
            int rangeEnd = first + 7;
            bool enabled = groupIndex < groups.Length;
            var group = enabled ? groups[groupIndex] : Array.Empty<DetectorPlan>();
            int actualLast = enabled ? group.Last().ScreenNo : rangeEnd;
            string source = enabled ? RenderConfigExtractor(group) : RenderEmptyMacro();
            if (enabled)
            {
                File.WriteAllText(
                    Path.Combine(macroDirectory, $"config-extractor_{first:00}_{actualLast:00}.txt"),
                    source);
            }
            int id = ExtractorMacroIds[groupIndex];
            string name = $"Info Extractor {first}-{actualLast}";
            File.WriteAllBytes(
                Path.Combine(macroDirectory, $"{id}_{name}.ebm"),
                EncodeEbm(RenderEbm(id, name, startup: enabled, periodicInterval: enabled ? 600 : null, source)));
        }
    }

    internal static string RenderConfigExtractor(IReadOnlyList<DetectorPlan> detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("macro_command main()");
        sb.AppendLine();
        foreach (var d in detectors)
        {
            sb.AppendLine($"short Det{d.ScreenNo}_Info[{Mx43AddressMap.ConfigBlockSize}]");
            sb.AppendLine($"short Det{d.ScreenNo}_ScaleDivisor");
            sb.AppendLine($"short Det{d.ScreenNo}_AlarmLevelCount");
        }
        sb.AppendLine();
        foreach (var d in detectors)
        {
            sb.AppendLine($"Det{d.ScreenNo}_ScaleDivisor = {d.ScaleDivisor}");
            sb.AppendLine($"Det{d.ScreenNo}_AlarmLevelCount = {d.AlarmLevelCount}");
            sb.AppendLine($"GetData(Det{d.ScreenNo}_Info[0], \"MX43\", \"info-D{d.ScreenNo}\", {Mx43AddressMap.ConfigBlockSize})");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[0], \"cMT\", LW, {d.LwName}, 16) // name");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[16], \"cMT\", LW, {d.LwStatus}, 1) // status");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[17], \"cMT\", LW, {d.LwFullGas}, 20) // full gas name");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[37], \"cMT\", LW, {d.LwRange}, 1) // range, raw");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[38], \"cMT\", LW, {d.LwDisplayFormat}, 1) // display format / decimal places");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[39], \"cMT\", LW, {d.LwUnit}, 5) // unit");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[44], \"cMT\", LW, {d.LwShortGas}, 6) // abbreviated gas name");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[51], \"cMT\", LW, {d.LwAlarm1}, 1) // alarm 1 threshold, raw");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[52], \"cMT\", LW, {d.LwAlarm2}, 1) // alarm 2 threshold, raw");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Info[53], \"cMT\", LW, {d.LwAlarm3}, 1) // alarm 3 threshold, raw");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_ScaleDivisor, \"cMT\", LW, {d.LwScaleDivisor}, 1) // 10^displayFormat from cfg");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_AlarmLevelCount, \"cMT\", LW, {d.LwAlarmLevelCount}, 1) // number of configured alarm output levels");
            sb.AppendLine();
        }
        sb.AppendLine("end macro_command");
        return sb.ToString();
    }

    internal static void WriteRuntimeSampler(string outputDir, IReadOnlyList<DetectorPlan> detectors)
    {
        string source = RenderRuntimeSampler(detectors);
        string macroDirectory = Path.Combine(outputDir, "macros");
        foreach (string staleFile in Directory.GetFiles(macroDirectory, "10_*.ebm"))
            File.Delete(staleFile);
        File.WriteAllText(Path.Combine(macroDirectory, "runtime-sampler.txt"), source);
        File.WriteAllBytes(
            Path.Combine(macroDirectory, "10_Runtime Sampler.ebm"),
            EncodeEbm(RenderEbm(10, "Runtime Sampler", startup: true, periodicInterval: 10, source)));
    }

    internal static void WriteProjectInitializer(string outputDir, string projectTitle)
    {
        string source = RenderProjectInitializer(projectTitle);
        string macroDirectory = Path.Combine(outputDir, "macros");
        foreach (string staleFile in Directory.GetFiles(macroDirectory, "11_*.ebm"))
            File.Delete(staleFile);
        File.WriteAllText(Path.Combine(macroDirectory, "project-initializer.txt"), source);
        File.WriteAllBytes(
            Path.Combine(macroDirectory, "11_Project Initializer.ebm"),
            EncodeEbm(RenderEbm(11, "Project Initializer", startup: true, periodicInterval: null, source)));
    }

    internal static string RenderEbm(int id, string name, bool startup, int? periodicInterval, string macroSource)
    {
        string periodic = periodicInterval is int interval
            ? $"[ \"true\", \"{interval}\" ]"
            : "[ \"false\" ]";
        var lines = new[]
        {
            "macro_definition_begin",
            $"    \"id\": \"{id}\"",
            $"    \"name\": \"{name}\"",
            $"    \"startup\": \"{startup.ToString().ToLowerInvariant()}\"",
            $"    \"periodic\": {periodic}",
            "    \"interlock\": [ \"false\" ]",
            "macro_definition_end",
            "",
        };
        return string.Join("\r\n", lines) + "\r\n" + macroSource.ReplaceLineEndings("\r\n").TrimEnd('\r', '\n');
    }

    internal static byte[] EncodeEbm(string content)
    {
        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] body = Encoding.UTF8.GetBytes(content);
        byte[] result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    internal static string RenderRuntimeSampler(IReadOnlyList<DetectorPlan> detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("macro_command main()");
        sb.AppendLine();
        foreach (var d in detectors)
        {
            sb.AppendLine($"short Det{d.ScreenNo}_Measurement");
            sb.AppendLine($"short Det{d.ScreenNo}_AlarmBits");
            sb.AppendLine($"short Det{d.ScreenNo}_AlarmSeverity");
        }
        sb.AppendLine();
        foreach (var d in detectors)
        {
            int alarm1Severity = d.HighestConfiguredAlarmBit == 1 ? 3 : 1;
            int alarm2Severity = d.HighestConfiguredAlarmBit == 2 ? 3 : 2;
            sb.AppendLine($"Det{d.ScreenNo}_AlarmSeverity = 0");
            sb.AppendLine($"GetData(Det{d.ScreenNo}_Measurement, \"MX43\", \"meas-D{d.ScreenNo}\", 1)");
            sb.AppendLine($"GetData(Det{d.ScreenNo}_AlarmBits, \"MX43\", \"alarm-D{d.ScreenNo}\", 1)");
            sb.AppendLine($"// Highest configured alarm and fault/scale states are red.");
            sb.AppendLine($"if (Det{d.ScreenNo}_AlarmBits & 0x007C) <> 0 then");
            sb.AppendLine($"    Det{d.ScreenNo}_AlarmSeverity = 3");
            sb.AppendLine($"else if (Det{d.ScreenNo}_AlarmBits & 0x0002) <> 0 then");
            sb.AppendLine($"    Det{d.ScreenNo}_AlarmSeverity = {alarm2Severity}");
            sb.AppendLine($"else if (Det{d.ScreenNo}_AlarmBits & 0x0001) <> 0 then");
            sb.AppendLine($"    Det{d.ScreenNo}_AlarmSeverity = {alarm1Severity}");
            sb.AppendLine("end if");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Measurement, \"cMT\", LW, {d.LwMeasurement}, 1) // raw measurement");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_AlarmBits, \"cMT\", LW, {d.LwAlarmBits}, 1) // alarm bits");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_Measurement, \"cMT\", LW, {d.LwScaledInteger}, 1) // keep raw; numeric object should use {d.DisplayFormat} decimal(s)");
            sb.AppendLine($"SetData(Det{d.ScreenNo}_AlarmSeverity, \"cMT\", LW, {d.LwAlarmSeverity}, 1) // 0 normal, 1 yellow, 2 orange, 3 red");
            sb.AppendLine();
        }
        sb.AppendLine("end macro_command");
        return sb.ToString();
    }

    internal static string RenderProjectInitializer(string projectTitle)
    {
        if (projectTitle.Length > WeintekLayout.ProjectTitleLength)
            throw new ArgumentException($"Project title must contain at most {WeintekLayout.ProjectTitleLength} UTF-16 code units.", nameof(projectTitle));

        var sb = new StringBuilder();
        sb.AppendLine("macro_command main()");
        sb.AppendLine();
        sb.AppendLine($"short ProjectTitle[{WeintekLayout.ProjectTitleLength}]");
        sb.AppendLine($"short MeasurementAddressIndex = {WeintekLayout.MeasurementAddressIndexValue}");
        sb.AppendLine();
        for (int i = 0; i < WeintekLayout.ProjectTitleLength; i++)
        {
            int codeUnit = i < projectTitle.Length ? projectTitle[i] : 0;
            sb.AppendLine($"ProjectTitle[{i}] = 0x{codeUnit:X4}");
        }
        sb.AppendLine();
        sb.AppendLine($"SetData(ProjectTitle[0], \"cMT\", LW, {WeintekLayout.ProjectTitleLw}, {WeintekLayout.ProjectTitleLength})");
        sb.AppendLine($"SetData(MeasurementAddressIndex, \"cMT\", LW, {WeintekLayout.MeasurementAddressIndexLw}, 1)");
        sb.AppendLine();
        sb.AppendLine("end macro_command");
        return sb.ToString();
    }

    private static string RenderEmptyMacro() => "macro_command main()\n\nend macro_command\n";
}
