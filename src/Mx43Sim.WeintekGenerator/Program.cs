using System;
using System.Globalization;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Mx43Sim.Core.Cfg;
using Mx43Sim.Core.Domain;
using Mx43Sim.Core.Modbus;
using static Mx43Sim.WeintekGenerator.WeintekLayout;

namespace Mx43Sim.WeintekGenerator;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string cfgPath = args[0];
        if (!File.Exists(cfgPath))
        {
            Console.Error.WriteLine($"CFG file not found: {cfgPath}");
            return 2;
        }

        string outputDir = DefaultOutputDir(cfgPath);
        string? templateCxob = null;
        string? cxobOutput = null;
        string? projectTitleOverride = null;
        string? storageKeyOverride = null;
        int samplingIntervalMilliseconds = 1000;
        int preservationFiles = 90;
        int autoSyncMinutes = 60;
        bool allowBinaryExpansion = true;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--output")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value after --output");
                    return 2;
                }
                outputDir = args[++i];
            }
            else if (args[i] == "--template-cxob")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value after --template-cxob");
                    return 2;
                }
                templateCxob = args[++i];
            }
            else if (args[i] == "--cxob-output")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value after --cxob-output");
                    return 2;
                }
                cxobOutput = args[++i];
            }
            else if (args[i] == "--allow-binary-expansion")
            {
                allowBinaryExpansion = true;
            }
            else if (args[i] == "--preserve-binary-length")
            {
                allowBinaryExpansion = false;
            }
            else if (args[i] == "--project-title")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value after --project-title");
                    return 2;
                }
                projectTitleOverride = args[++i];
            }
            else if (args[i] == "--storage-key")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value after --storage-key");
                    return 2;
                }
                storageKeyOverride = args[++i];
            }
            else if (args[i] == "--sampling-interval-ms")
            {
                if (!TryReadPositiveInt(args, ref i, "--sampling-interval-ms", out samplingIntervalMilliseconds)) return 2;
            }
            else if (args[i] == "--history-files")
            {
                if (!TryReadPositiveInt(args, ref i, "--history-files", out preservationFiles)) return 2;
            }
            else if (args[i] == "--sync-minutes")
            {
                if (!TryReadPositiveInt(args, ref i, "--sync-minutes", out autoSyncMinutes)) return 2;
            }
            else
            {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 2;
            }
        }

        if (cxobOutput is not null && templateCxob is null)
        {
            Console.Error.WriteLine("--cxob-output requires --template-cxob.");
            return 2;
        }
        if (templateCxob is not null && !File.Exists(templateCxob))
        {
            Console.Error.WriteLine($"Template CXOB file not found: {templateCxob}");
            return 2;
        }
        if (templateCxob is not null)
            cxobOutput ??= Path.Combine(outputDir, Path.GetFileNameWithoutExtension(cfgPath) + ".patched-template.cxob");

        var cfg = new Mx43CfgParser(cfgPath).Parse();
        var detectors = DetectorPlanner.Create(cfg);
        if (detectors.Length is < 1 or > 32)
        {
            Console.Error.WriteLine($"The current Weintek template supports 1..32 detectors; CFG contains {detectors.Length}.");
            return 2;
        }
        string projectTitle;
        try
        {
            projectTitle = ResolveProjectTitle(cfg.ProjectName, projectTitleOverride);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        string storageKey = string.IsNullOrWhiteSpace(storageKeyOverride)
            ? Path.GetFileNameWithoutExtension(cfgPath)
            : storageKeyOverride.Trim();
        var samplingPolicy = new DataSamplingGenerator.SamplingPolicy(
            samplingIntervalMilliseconds,
            preservationFiles,
            autoSyncMinutes);
        try
        {
            DataSamplingGenerator.ValidatePolicy(samplingPolicy);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(outputDir, "macros"));
        Directory.CreateDirectory(Path.Combine(outputDir, "tags"));

        WritePlanJson(outputDir, cfg, projectTitle, storageKey, samplingPolicy, detectors);
        WriteDetectorCsv(outputDir, detectors);
        WriteTrendChannelsCsv(outputDir, detectors);
        WriteMx43TagCsv(outputDir, detectors);
        WriteLocalTagCsv(outputDir, detectors);
        AddressTagLibraryGenerator.Write(outputDir, detectors);
        DataSamplingGenerator.Write(outputDir, detectors, storageKey, samplingPolicy);
        MacroGenerator.WriteConfigExtractors(outputDir, detectors);
        MacroGenerator.WriteRuntimeSampler(outputDir, detectors);
        MacroGenerator.WriteProjectInitializer(outputDir, projectTitle);
        WriteReadme(outputDir, cfgPath, cfg, projectTitle, storageKey, samplingPolicy, detectors);

        if (templateCxob is not null)
        {
            try
            {
                PatchCxobTemplate(templateCxob, cxobOutput!, detectors, allowBinaryExpansion);
                Console.WriteLine($"Generated template-patched CXOB: {cxobOutput}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                Console.Error.WriteLine($"CXOB patch failed: {ex.Message}");
                return 2;
            }
        }
        WriteImportInstructions(outputDir, projectTitle, storageKey, samplingPolicy, detectors, cxobOutput);

        Console.WriteLine($"Generated Weintek artifacts for {detectors.Length} detector(s): {outputDir}");
        Console.WriteLine("Next step: follow IMPORT.md and full compile the customer CXOB in EasyBuilder Pro.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/Mx43Sim.WeintekGenerator/Mx43Sim.WeintekGenerator.csproj -- <file.cfg> [-o output-dir] [--project-title title] [--storage-key key] [--sampling-interval-ms 1000] [--history-files 90] [--sync-minutes 60] [--template-cxob template.cxob] [--cxob-output out.cxob] [--preserve-binary-length]");
        Console.WriteLine();
        Console.WriteLine("  Default CXOB patching uses the validated variable-length expansion path.");
        Console.WriteLine("  --preserve-binary-length keeps the old conservative mode and may produce partial output with short template slots.");
    }

    private static string DefaultOutputDir(string cfgPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cfgPath)) ?? Directory.GetCurrentDirectory();
        string name = Path.GetFileNameWithoutExtension(cfgPath);
        return Path.Combine(dir, name + ".weintek");
    }

    internal static string ResolveProjectTitle(string cfgProjectName, string? projectTitleOverride)
    {
        string title = projectTitleOverride ?? cfgProjectName;
        if (string.IsNullOrWhiteSpace(title)) title = "MX43";
        title = title.Trim();
        if (title.Length > ProjectTitleLength)
            throw new InvalidOperationException($"Project title must contain at most {ProjectTitleLength} UTF-16 code units: '{title}'.");
        return title;
    }

    private static bool TryReadPositiveInt(string[] args, ref int index, string option, out int value)
    {
        value = 0;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            Console.Error.WriteLine($"{option} requires a positive integer.");
            return false;
        }
        index++;
        return true;
    }

    private static void WritePlanJson(
        string outputDir,
        Mx43Config cfg,
        string projectTitle,
        string storageKey,
        DataSamplingGenerator.SamplingPolicy samplingPolicy,
        DetectorPlan[] detectors)
    {
        var plan = new
        {
            generator = "Mx43Sim.WeintekGenerator",
            projectName = cfg.ProjectName,
            projectTitle,
            dataSampling = new
            {
                storageKey,
                intervalMilliseconds = samplingPolicy.IntervalMilliseconds,
                preservationFiles = samplingPolicy.PreservationFiles,
                autoSyncMinutes = samplingPolicy.AutoSyncMinutes,
            },
            detectorCount = detectors.Length,
            localLwLayout = new
            {
                projectTitle = ProjectTitleLw,
                projectTitleLength = ProjectTitleLength,
                stride = LwStride,
                name = LwNameOffset,
                status = LwStatusOffset,
                fullGas = LwFullGasOffset,
                range = LwRangeOffset,
                displayFormat = LwDisplayFormatOffset,
                unit = LwUnitOffset,
                shortGas = LwShortGasOffset,
                alarm1 = LwAlarm1Offset,
                alarm2 = LwAlarm2Offset,
                alarm3 = LwAlarm3Offset,
                measurement = LwMeasurementOffset,
                alarmBits = LwAlarmBitsOffset,
                scaledInteger = LwScaledIntegerOffset,
                scaleDivisor = LwScaleDivisorOffset,
                alarmLevelCount = LwAlarmLevelCountOffset,
                alarmSeverity = LwAlarmSeverityOffset,
            },
            detectors,
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, "weintek-plan.json"), JsonSerializer.Serialize(plan, options));
    }

    private static void WriteDetectorCsv(string outputDir, DetectorPlan[] detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ScreenNo,Line,Detector,AnalogChannel,Label,Gas,Unit,RangeRaw,DisplayFormat,ScaleDivisor,AlarmLevelCount,HighestConfiguredAlarmBit,ConfigRegister,MeasurementRegister,AlarmRegister,LwBase,LwMeasurement,LwAlarmBits,LwAlarmSeverity");
        foreach (var d in detectors)
        {
            sb.AppendCsv(d.ScreenNo);
            sb.AppendCsv(d.Line);
            sb.AppendCsv(d.Detector);
            sb.AppendCsv(d.AnalogChannel);
            sb.AppendCsv(d.Label);
            sb.AppendCsv(d.ShortGasName);
            sb.AppendCsv(d.Unit);
            sb.AppendCsv(d.RangeRaw);
            sb.AppendCsv(d.DisplayFormat);
            sb.AppendCsv(d.ScaleDivisor);
            sb.AppendCsv(d.AlarmLevelCount);
            sb.AppendCsv(d.HighestConfiguredAlarmBit);
            sb.AppendCsv(d.ConfigRegister);
            sb.AppendCsv(d.MeasurementRegister);
            sb.AppendCsv(d.AlarmRegister);
            sb.AppendCsv(d.LwBase);
            sb.AppendCsv(d.LwMeasurement);
            sb.AppendCsv(d.LwAlarmBits);
            sb.AppendCsv(d.LwAlarmSeverity, last: true);
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDir, "detectors.csv"), sb.ToString());
    }

    private static void WriteTrendChannelsCsv(string outputDir, DetectorPlan[] detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Channel,Label,Gas,Unit,DisplayFormat,ScaleDivisor,AlarmLevelCount,MeasurementRegister,LocalMeasurementLw,AlarmRegister,LocalAlarmBitsLw,LocalAlarmSeverityLw,TemplateTrendTag");
        foreach (var d in detectors)
        {
            sb.AppendCsv(d.ScreenNo);
            sb.AppendCsv(d.Label);
            sb.AppendCsv(d.ShortGasName);
            sb.AppendCsv(d.Unit);
            sb.AppendCsv(d.DisplayFormat);
            sb.AppendCsv(d.ScaleDivisor);
            sb.AppendCsv(d.AlarmLevelCount);
            sb.AppendCsv(d.MeasurementRegister);
            sb.AppendCsv(d.LwMeasurement);
            sb.AppendCsv(d.AlarmRegister);
            sb.AppendCsv(d.LwAlarmBits);
            sb.AppendCsv(d.LwAlarmSeverity);
            sb.AppendCsv(d.ScreenNo <= 32 ? $"Ch{d.ScreenNo}" : "", last: true);
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDir, "trend-channels.csv"), sb.ToString());
    }

    private static void WriteMx43TagCsv(string outputDir, DetectorPlan[] detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Device,Kind,Register,Length,Comment");
        foreach (var d in detectors)
        {
            WriteTagRow(sb, $"info-D{d.ScreenNo}", "MX43", "HoldingRegisterBlock", d.ConfigRegister, Mx43AddressMap.ConfigBlockSize,
                $"Config block for {d.Label}");
            WriteTagRow(sb, $"meas-D{d.ScreenNo}", "MX43", "HoldingRegister", d.MeasurementRegister, 1,
                $"Raw measurement for {d.Label}; divide by scale divisor for display");
            WriteTagRow(sb, $"alarm-D{d.ScreenNo}", "MX43", "HoldingRegister", d.AlarmRegister, 1,
                $"Alarm bitfield for {d.Label}");
        }
        File.WriteAllText(Path.Combine(outputDir, "tags", "mx43-tags.csv"), sb.ToString());
    }

    private static void WriteLocalTagCsv(string outputDir, DetectorPlan[] detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Device,Kind,Register,Length,Comment");
        WriteTagRow(sb, "Project-Title", "cMT", "LW", ProjectTitleLw, ProjectTitleLength, "HMI project title, Unicode one character per word");
        foreach (var d in detectors)
        {
            WriteTagRow(sb, $"Det{d.ScreenNo}-Name", "cMT", "LW", d.LwName, 16, "Detector label");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Status", "cMT", "LW", d.LwStatus, 1, "Detector enabled status");
            WriteTagRow(sb, $"Det{d.ScreenNo}-FullGas", "cMT", "LW", d.LwFullGas, 20, "Full gas text");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Range", "cMT", "LW", d.LwRange, 1, "Raw range from MX43 config");
            WriteTagRow(sb, $"Det{d.ScreenNo}-DisplayFormat", "cMT", "LW", d.LwDisplayFormat, 1, "Decimal places from MX43 config");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Unit", "cMT", "LW", d.LwUnit, 5, "Unit text");
            WriteTagRow(sb, $"Det{d.ScreenNo}-AbbGas", "cMT", "LW", d.LwShortGas, 6, "Abbreviated gas text");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Alarm1", "cMT", "LW", d.LwAlarm1, 1, "Raw alarm 1 threshold");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Alarm2", "cMT", "LW", d.LwAlarm2, 1, "Raw alarm 2 threshold");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Alarm3", "cMT", "LW", d.LwAlarm3, 1, "Raw alarm 3 threshold");
            WriteTagRow(sb, $"Det{d.ScreenNo}-Measurement", "cMT", "LW", d.LwMeasurement, 1, "Raw live measurement");
            WriteTagRow(sb, $"Det{d.ScreenNo}-AlarmBits", "cMT", "LW", d.LwAlarmBits, 1, "Live alarm bits");
            WriteTagRow(sb, $"Det{d.ScreenNo}-ScaledInteger", "cMT", "LW", d.LwScaledInteger, 1, "Raw measurement for numeric objects configured with display decimals");
            WriteTagRow(sb, $"Det{d.ScreenNo}-ScaleDivisor", "cMT", "LW", d.LwScaleDivisor, 1, "1, 10, 100... derived from display format");
            WriteTagRow(sb, $"Det{d.ScreenNo}-AlarmLevelCount", "cMT", "LW", d.LwAlarmLevelCount, 1, "Configured alarm levels from MX43 thresholds");
            WriteTagRow(sb, $"Det{d.ScreenNo}-AlarmSeverity", "cMT", "LW", d.LwAlarmSeverity, 1, "0=normal, 1=yellow, 2=orange, 3=red; Alarm2 is red when only two levels exist");
        }
        File.WriteAllText(Path.Combine(outputDir, "tags", "local-lw-tags.csv"), sb.ToString());
    }

    private static void WriteTagRow(StringBuilder sb, string name, string device, string kind, int register, int length, string comment)
    {
        sb.AppendCsv(name);
        sb.AppendCsv(device);
        sb.AppendCsv(kind);
        sb.AppendCsv(register);
        sb.AppendCsv(length);
        sb.AppendCsv(comment, last: true);
        sb.AppendLine();
    }

    private static void WriteReadme(
        string outputDir,
        string cfgPath,
        Mx43Config cfg,
        string projectTitle,
        string storageKey,
        DataSamplingGenerator.SamplingPolicy samplingPolicy,
        DetectorPlan[] detectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Weintek Generator Output");
        sb.AppendLine();
        sb.AppendLine($"Source CFG: `{Path.GetFileName(cfgPath)}`");
        sb.AppendLine($"Project name: `{cfg.ProjectName}`");
        sb.AppendLine($"HMI title: `{projectTitle}`");
        sb.AppendLine($"History storage key: `{storageKey}`");
        sb.AppendLine($"Detectors: `{detectors.Length}`");
        sb.AppendLine();
        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("- `weintek-plan.json`: machine-readable plan for patching/building a cMT project.");
        sb.AppendLine("- `detectors.csv`: detector order, Modbus addresses and local LW layout.");
        sb.AppendLine("- `trend-channels.csv`: detector measurement/alarm channels intended for trend/data-sampling setup.");
        sb.AppendLine("- `tags/mx43-tags.csv`: review manifest for MX43-side tags (`info-Dn`, `meas-Dn`, `alarm-Dn`).");
        sb.AppendLine("- `tags/local-lw-tags.csv`: review manifest for local cMT LW tags used by generated macros and objects.");
        sb.AppendLine("- `tags/mx43-address-tag-library.csv`: EasyBuilder Address Tag Library import for active MX43 tags.");
        sb.AppendLine("- `tags/local-lw-address-tag-library.csv`: EasyBuilder Address Tag Library import for generated local cMT LW tags.");
        sb.AppendLine($"- `data-sampling.xlsx`: EasyBuilder Data Sampling import for every active detector, with {samplingPolicy.IntervalMilliseconds} ms sampling and USB history.");
        sb.AppendLine("- `macros/config-extractor_*.txt`: reads the 68-register MX43 config block into local LW memory.");
        sb.AppendLine("- `macros/runtime-sampler.txt`: periodically reads live measurements and alarm bits.");
        sb.AppendLine("- `macros/*.ebm`: EasyBuilder Pro 6.10.02 macro imports with startup/periodic metadata.");
        sb.AppendLine("- `macros/11_Project Initializer.ebm`: initializes the HMI title and measurement address index.");
        sb.AppendLine("- `IMPORT.md`: exact EasyBuilder assembly and verification order.");
        sb.AppendLine("- `*.patched-template.cxob`: optional output when `--template-cxob` is used. This is an import template, not a full EasyBuilder compile.");
        sb.AppendLine("- `*.template-report.md`: optional report describing the template's available fixed-width slots.");
        sb.AppendLine("- `*.warnings.txt`: patch notices and warnings. Expansion notices are informational; skipped, missing or truncated fields mean the `.cxob` is partial.");
        sb.AppendLine();
        sb.AppendLine("## Decimal Handling");
        sb.AppendLine();
        sb.AppendLine("MX43 config offset `+38` is `DisplayFormat`. This generator treats it as decimal places:");
        sb.AppendLine();
        sb.AppendLine("- `0`: raw value is displayed as an integer, for example `100`.");
        sb.AppendLine("- `1`: raw value is displayed with one decimal, for example `190` -> `19.0`.");
        sb.AppendLine("- `2`: raw value is displayed with two decimals, for example `50` -> `0.50`.");
        sb.AppendLine();
        sb.AppendLine("Generated macros copy `DisplayFormat` into `DetN-DisplayFormat`. A Weintek numeric object can use fixed decimal places per generated detector, or a template patcher can create the correct numeric object variant per detector.");
        sb.AppendLine();
        sb.AppendLine("## Alarm Severity");
        sb.AppendLine();
        sb.AppendLine("`DetN-AlarmSeverity` is generated as a local color-driving value: `0=normal`, `1=yellow`, `2=orange`, `3=red`. If a detector only has Alarm 1 and Alarm 2 configured, Alarm 2 is treated as severity `3` so it becomes red without duplicating Alarm 2 into Alarm 3. If all three alarm levels exist, Alarm 1/2/3 remain yellow/orange/red.");
        sb.AppendLine();
        sb.AppendLine("## Trend Approach");
        sb.AppendLine();
        sb.AppendLine("`data-sampling.xlsx` samples each contiguous MX43 measurement range through address index 1 and applies each detector's decimal setting. Bind template Trend Display objects to the imported groups in `IMPORT.md` order.");
        sb.AppendLine();
        sb.AppendLine("## CXOB Patch Modes");
        sb.AppendLine();
        sb.AppendLine("Default `.cxob` patching rebuilds mapped variable-length label/tag sections, project lengths and relocation metadata. Generation fails if any required active `info-Dn` tag is missing or has the wrong address after patching.");
        sb.AppendLine();
        sb.AppendLine("`--preserve-binary-length` enables the old diagnostic mode and can fail when required addresses do not fit. EasyBuilder 6.10.02 has successfully decompiled and recompiled digital and analog expanded files. Unreferenced `Det-N` label records are discarded by EasyBuilder, so display names must be driven by referenced template objects/local LW data rather than detached label placeholders.");
        File.WriteAllText(Path.Combine(outputDir, "README.md"), sb.ToString());
    }

    private static void WriteImportInstructions(
        string outputDir,
        string projectTitle,
        string storageKey,
        DataSamplingGenerator.SamplingPolicy samplingPolicy,
        DetectorPlan[] detectors,
        string? patchedCxobOutput)
    {
        DataSamplingGenerator.SamplingGroup[] groups = DataSamplingGenerator.CreateGroups(detectors, storageKey);
        var sb = new StringBuilder();
        sb.AppendLine("# EasyBuilder Assembly");
        sb.AppendLine();
        sb.AppendLine($"Project title: `{projectTitle}`");
        sb.AppendLine($"History storage key: `{storageKey}`");
        sb.AppendLine($"Detectors: `{detectors.Length}`");
        sb.AppendLine($"Data Sampling groups: `{groups.Length}`");
        sb.AppendLine();
        if (patchedCxobOutput is null)
            sb.AppendLine("This bundle contains import artifacts for a maintained cMT template. EasyBuilder Pro 6.10.02.300 must perform the final full compile.");
        else
            sb.AppendLine("The patched CXOB is an import template, not the deployable customer file. EasyBuilder Pro 6.10.02.300 must perform the final full compile.");
        sb.AppendLine();
        sb.AppendLine("## Import Order");
        sb.AppendLine();
        if (patchedCxobOutput is null)
            sb.AppendLine("1. Open the maintained cMT template in EasyBuilder. No patched CXOB was requested for this generation run.");
        else
            sb.AppendLine($"1. Decompile `{patchedCxobOutput}` in EasyBuilder. Enter the template password if requested; the known sample templates use `111111`.");
        sb.AppendLine("2. Import `tags/mx43-address-tag-library.csv`. Replace same-named `info-Dn` tags when EasyBuilder asks.");
        sb.AppendLine("3. Import `tags/local-lw-address-tag-library.csv`. Replace same-named local detector tags when EasyBuilder asks.");
        sb.AppendLine("4. Import macro IDs `5`, `7`, `8`, `9`, `10` and `11` from `macros/*.ebm`, replacing macros with the same IDs.");
        sb.AppendLine("5. Remove old Data Sampling definitions that read MX43 measurements, then import `data-sampling.xlsx` to avoid duplicate logging.");
        sb.AppendLine("6. Bind each Trend Display to the corresponding imported Data Sampling group in the order listed below.");
        sb.AppendLine("7. Ensure the maintained template has one read-only Unicode ASCII display bound to `cMT` `LW-3300`, length 16 words, on the common/header window.");
        sb.AppendLine("8. Full compile to the final customer CXOB and run the offline simulator before deployment.");
        sb.AppendLine();
        sb.AppendLine("## Data Sampling Groups");
        sb.AppendLine();
        sb.AppendLine($"All groups sample every {samplingPolicy.IntervalMilliseconds} ms, synchronize to USB every {samplingPolicy.AutoSyncMinutes} minutes and preserve up to {samplingPolicy.PreservationFiles} customized files. Macro ID 11 initializes `LW-9201` to `2000`, so each configuration base resolves to its live measurement range.");
        sb.AppendLine();
        double backlogHours = 9000d * samplingPolicy.IntervalMilliseconds / 3_600_000d;
        sb.AppendLine($"When USB is absent, EasyBuilder keeps only a finite HMI backlog. The documented USB-mode limit is roughly 9000 records per sampling group before older disconnected-period data can be discarded; at {samplingPolicy.IntervalMilliseconds} ms this is about {backlogHours.ToString("0.##", CultureInfo.InvariantCulture)} hours. `Sync Status Address` remains Off because no validated status-address export has been supplied.");
        sb.AppendLine();
        sb.AppendLine("| Group | Config base | Effective measurement range | Records | USB folder |");
        sb.AppendLine("|---:|---:|---:|---:|---|");
        foreach (DataSamplingGenerator.SamplingGroup group in groups)
        {
            int firstMeasurement = group.Detectors[0].MeasurementRegister;
            int lastMeasurement = group.Detectors[^1].MeasurementRegister;
            sb.AppendLine($"| {group.Number} | {group.BaseConfigRegister} | {firstMeasurement}..{lastMeasurement} | {group.Detectors.Count} | `{group.FolderName}` |");
        }
        sb.AppendLine();
        sb.AppendLine("## Acceptance Checks");
        sb.AppendLine();
        sb.AppendLine($"- The header displays `{projectTitle}`.");
        sb.AppendLine("- Every active detector shows its CFG label, gas, unit, range and thresholds.");
        sb.AppendLine("- Live values and alarm colors update from the simulator.");
        sb.AppendLine("- Data Sampling continues while USB is absent only within the finite HMI backlog; absence does not provide long-term history.");
        sb.AppendLine("- Reinserting USB allows subsequent synchronization; safely remove USB before unplugging it during a write.");
        File.WriteAllText(Path.Combine(outputDir, "IMPORT.md"), sb.ToString());
    }

    private static void PatchCxobTemplate(string templateCxob, string outputCxob, DetectorPlan[] detectors, bool allowBinaryExpansion)
    {
        if (detectors.Length > 32)
            throw new InvalidOperationException("The current cMT template patcher supports at most 32 detector label entries.");

        string tempDir = Path.Combine(Path.GetTempPath(), "mx43-weintek-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(tempDir);
        try
        {
            ExtractGzipTar(templateCxob, tempDir);
            string projectPath = FindProjectPayloadPath(tempDir);
            if (!File.Exists(projectPath)) throw new InvalidOperationException("Template CXOB does not contain a project payload.");

            ProjectPatchResult patch = PatchProjectPayload(File.ReadAllBytes(projectPath), detectors, allowBinaryExpansion);
            byte[] project = patch.Project;
            var warnings = patch.Warnings.ToList();
            TemplateReport report = AnalyzeTemplate(project, detectors);
            File.WriteAllBytes(projectPath, project);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCxob)) ?? Directory.GetCurrentDirectory());
            RepackGzipTar(tempDir, outputCxob);

            string warningPath = Path.ChangeExtension(outputCxob, ".warnings.txt");
            string reportPath = Path.ChangeExtension(outputCxob, ".template-report.md");
            File.WriteAllText(reportPath, RenderTemplateReport(report));
            Console.WriteLine($"Template report written to: {reportPath}");
            if (warnings.Count > 0)
            {
                File.WriteAllLines(warningPath, warnings.Distinct(StringComparer.Ordinal));
                Console.WriteLine($"Template patch warnings written to: {warningPath}");
            }
            else if (File.Exists(warningPath))
            {
                File.Delete(warningPath);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    internal static ProjectPatchResult PatchProjectPayload(byte[] source, DetectorPlan[] detectors, bool allowBinaryExpansion)
    {
        byte[] project = source.ToArray();
        var warnings = new List<string>();
        ValidateProjectStructure(project);
        if (allowBinaryExpansion)
        {
            project = ExpandDetectorLabelsIfNeeded(project, detectors, warnings);
            project = ExpandInfoTagAddressFieldsIfNeeded(project, detectors, warnings);
        }
        else
        {
            warnings.Add("Binary expansion disabled; CXOB patch preserves original project payload length for EasyBuilder password/decompile compatibility.");
        }
        PatchDetectorLabels(project, detectors, warnings);
        PatchInfoTags(project, detectors, warnings);
        ValidateActiveInfoTags(project, detectors);
        ValidateProjectStructure(project);
        return new ProjectPatchResult(project, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateActiveInfoTags(byte[] project, IReadOnlyList<DetectorPlan> detectors)
    {
        TagRecord[] tags = ParseTags(project).ToArray();
        foreach (DetectorPlan detector in detectors)
        {
            string name = $"info-D{detector.ScreenNo}";
            TagRecord? tag = tags.FirstOrDefault(candidate => candidate.Name == name);
            string expected = detector.ConfigRegister.ToString(CultureInfo.InvariantCulture);
            if (tag is null)
                throw new InvalidOperationException($"Template is missing required tag {name}.");
            if (tag.Address != expected)
                throw new InvalidOperationException($"Required tag {name} has address '{tag.Address}', expected '{expected}'.");
        }
    }

    private static string FindProjectPayloadPath(string cxobRoot)
    {
        string rootProject = Path.Combine(cxobRoot, "project");
        if (File.Exists(rootProject)) return rootProject;

        string mt8000Project = Path.Combine(cxobRoot, "mt8000", "project");
        if (File.Exists(mt8000Project)) return mt8000Project;

        return rootProject;
    }

    private static byte[] ExpandInfoTagAddressFieldsIfNeeded(byte[] project, DetectorPlan[] detectors, List<string> warnings)
    {
        var tags = ParseTags(project).ToArray();
        bool needsExpansion = false;
        foreach (var d in detectors)
        {
            var tag = tags.FirstOrDefault(t => t.Name == $"info-D{d.ScreenNo}");
            if (tag is null) continue;
            if (d.ConfigRegister.ToString(CultureInfo.InvariantCulture).Length + 1 > tag.AddressFieldLength)
            {
                needsExpansion = true;
                break;
            }
        }

        if (!needsExpansion) return project;

        byte[] marker = Encoding.ASCII.GetBytes("ENHANCEDTAGS_L32");
        byte[] macroMarker = Encoding.ASCII.GetBytes("MACRO_ID");
        byte[] tagDataMarker = Encoding.ASCII.GetBytes("TAG_DATA");
        int tableStart = IndexOf(project, marker);
        int oldMacroOffset = IndexOf(project, macroMarker);
        int oldTagDataOffset = IndexOf(project, tagDataMarker);
        if (tableStart < 0 || oldMacroOffset <= tableStart || oldTagDataOffset <= oldMacroOffset)
        {
            warnings.Add("Could not expand ENHANCEDTAGS_L32 safely; falling back to fixed-width tag patching.");
            return project;
        }

        var detectorByInfoTag = detectors.ToDictionary(d => $"info-D{d.ScreenNo}", StringComparer.Ordinal);
        using var rebuilt = new MemoryStream();
        rebuilt.Write(marker);
        rebuilt.Write(BitConverter.GetBytes(tags.Length));
        foreach (var tag in tags)
        {
            string address = tag.Address;
            if (detectorByInfoTag.TryGetValue(tag.Name, out var detector))
                address = detector.ConfigRegister.ToString(CultureInfo.InvariantCulture);

            byte[] nameBytes = Encoding.ASCII.GetBytes(tag.Name);
            byte[] addressBytes = Encoding.ASCII.GetBytes(address);
            int nameLen = tag.NameFieldLength;
            int addressLen = Math.Max(tag.AddressFieldLength, addressBytes.Length + 1);
            if (nameLen > byte.MaxValue || addressLen > byte.MaxValue)
                throw new InvalidOperationException($"Tag record too long after expansion: {tag.Name}");

            rebuilt.WriteByte(tag.Flag);
            rebuilt.WriteByte(tag.Class);
            rebuilt.WriteByte(tag.Kind);
            rebuilt.WriteByte((byte)nameLen);
            rebuilt.WriteByte((byte)addressLen);
            WriteNullTerminatedField(rebuilt, nameBytes, nameLen);
            WriteNullTerminatedField(rebuilt, addressBytes, addressLen);
        }

        byte[] newTable = rebuilt.ToArray();
        int oldTableLength = oldMacroOffset - tableStart;
        int delta = newTable.Length - oldTableLength;
        byte[] updated = new byte[project.Length + delta];
        Buffer.BlockCopy(project, 0, updated, 0, tableStart);
        Buffer.BlockCopy(newTable, 0, updated, tableStart, newTable.Length);
        Buffer.BlockCopy(project, oldMacroOffset, updated, tableStart + newTable.Length, project.Length - oldMacroOffset);

        RelocateProject(project, updated, oldMacroOffset, delta, oldMacroOffset, oldTagDataOffset);

        warnings.Add($"Expanded ENHANCEDTAGS_L32 by {delta} byte(s) so info-D addresses can fit this CFG.");
        return updated;
    }

    private static byte[] ExpandDetectorLabelsIfNeeded(byte[] project, DetectorPlan[] detectors, List<string> warnings)
    {
        LabelLibrary? library = ParseLabelLibrary(project);
        if (library is null) return project;
        var labels = ParseDetectorLabelSlots(project).ToArray();
        if (labels.Length == 0) return project;

        bool needsExpansion = false;
        foreach (var d in detectors)
        {
            int required = Encoding.UTF8.GetByteCount(d.Label);
            var slot = labels.FirstOrDefault(l => l.Slot == d.ScreenNo);
            if (slot is not null && required > slot.ValueLength)
            {
                needsExpansion = true;
                break;
            }
        }

        if (!needsExpansion) return project;

        byte[] tagMarker = Encoding.ASCII.GetBytes("ENHANCEDTAGS_L32");
        byte[] macroMarker = Encoding.ASCII.GetBytes("MACRO_ID");
        byte[] tagDataMarker = Encoding.ASCII.GetBytes("TAG_DATA");
        int oldTagTableOffset = IndexOf(project, tagMarker);
        int oldMacroOffset = IndexOf(project, macroMarker);
        int oldTagDataOffset = IndexOf(project, tagDataMarker);
        if (library.EndOffset != oldTagTableOffset || oldMacroOffset <= oldTagTableOffset || oldTagDataOffset <= oldMacroOffset)
        {
            warnings.Add("Could not expand detector labels safely; falling back to fixed-width label patching.");
            return project;
        }

        var detectorByKey = detectors.ToDictionary(d => $"Det-{d.ScreenNo}", StringComparer.Ordinal);
        using var rebuilt = new MemoryStream();
        rebuilt.Write(Encoding.ASCII.GetBytes("LABE_LIB"));
        rebuilt.Write(BitConverter.GetBytes((ushort)library.Records.Count));
        foreach (var record in library.Records)
        {
            string value = detectorByKey.TryGetValue(record.Key, out var detector) ? detector.Label : record.Value;
            byte[] keyBytes = Encoding.ASCII.GetBytes(record.Key);
            byte[] valueBytes = Encoding.UTF8.GetBytes(value);
            byte[] suffixBytes = record.Suffix;
            int valueLength = Math.Max(record.ValueLength, valueBytes.Length);
            if (keyBytes.Length > byte.MaxValue || valueLength > byte.MaxValue || suffixBytes.Length > byte.MaxValue)
                throw new InvalidOperationException($"Label record too long: {record.Key}");

            rebuilt.WriteByte((byte)keyBytes.Length);
            rebuilt.WriteByte((byte)valueLength);
            rebuilt.WriteByte((byte)suffixBytes.Length);
            rebuilt.Write(keyBytes);
            rebuilt.Write(valueBytes);
            for (int i = valueBytes.Length; i < valueLength; i++) rebuilt.WriteByte((byte)' ');
            rebuilt.Write(suffixBytes);
        }

        byte[] newLabels = rebuilt.ToArray();
        int oldLabelsLength = library.EndOffset - library.Offset;
        int delta = newLabels.Length - oldLabelsLength;
        byte[] updated = new byte[project.Length + delta];
        Buffer.BlockCopy(project, 0, updated, 0, library.Offset);
        Buffer.BlockCopy(newLabels, 0, updated, library.Offset, newLabels.Length);
        Buffer.BlockCopy(project, library.EndOffset, updated, library.Offset + newLabels.Length, project.Length - library.EndOffset);

        RelocateProject(project, updated, library.EndOffset, delta, oldMacroOffset, oldTagDataOffset);

        warnings.Add($"Expanded detector label records by {delta} byte(s) so labels can fit this CFG; EasyBuilder validation is still required.");
        return updated;
    }

    private static TemplateReport AnalyzeTemplate(byte[] project, DetectorPlan[] detectors)
    {
        var tags = ParseTags(project).ToArray();
        var labels = ParseDetectorLabelSlots(project).ToArray();
        var warnings = new List<string>();
        var infoTags = tags.Where(t => t.Name.StartsWith("info-D", StringComparison.Ordinal)).ToArray();
        var trendTags = tags.Where(t => t.Name.StartsWith("Ch", StringComparison.Ordinal) && int.TryParse(t.Name.AsSpan(2), out _)).ToArray();

        if (infoTags.Length < detectors.Length)
            warnings.Add($"Template has {infoTags.Length} info-D tags but CFG has {detectors.Length} detectors.");
        if (trendTags.Length < detectors.Length)
            warnings.Add($"Template has {trendTags.Length} Ch trend/value tags but CFG has {detectors.Length} detectors.");
        if (labels.Length < Math.Min(detectors.Length, 32))
            warnings.Add($"Template has {labels.Length} detector label slots but CFG has {detectors.Length} detectors.");

        foreach (var d in detectors)
        {
            var info = infoTags.FirstOrDefault(t => t.Name == $"info-D{d.ScreenNo}");
            if (info is null)
            {
                warnings.Add($"Missing info-D{d.ScreenNo}; config block for '{d.Label}' cannot be patched into this template.");
            }
            else if (d.ConfigRegister.ToString(CultureInfo.InvariantCulture).Length + 1 > info.AddressFieldLength)
            {
                warnings.Add($"info-D{d.ScreenNo} address field is {info.AddressFieldLength - 1} chars; config register {d.ConfigRegister} does not fit.");
            }

            var label = labels.FirstOrDefault(l => l.Slot == d.ScreenNo);
            if (label is null)
            {
                warnings.Add($"Missing Det-{d.ScreenNo} label slot for '{d.Label}'.");
            }
            else if (Encoding.UTF8.GetByteCount(d.Label) > label.ValueLength)
            {
                warnings.Add($"Det-{d.ScreenNo} label slot is {label.ValueLength} chars; '{d.Label}' will be truncated.");
            }
        }

        return new TemplateReport(tags, labels, warnings);
    }

    private static string RenderTemplateReport(TemplateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CXOB Template Report");
        sb.AppendLine();
        sb.AppendLine($"Tag records: `{report.Tags.Count}`");
        sb.AppendLine($"Detector label slots: `{report.Labels.Count}`");
        sb.AppendLine($"info-D tags: `{report.Tags.Count(t => t.Name.StartsWith("info-D", StringComparison.Ordinal))}`");
        sb.AppendLine($"Ch trend/value tags: `{report.Tags.Count(t => t.Name.StartsWith("Ch", StringComparison.Ordinal) && int.TryParse(t.Name.AsSpan(2), out _))}`");
        sb.AppendLine();

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (string warning in report.Warnings) sb.AppendLine($"- {warning}");
            sb.AppendLine();
        }

        sb.AppendLine("## info-D Tags");
        sb.AppendLine();
        sb.AppendLine("| Name | Address | Field chars | Offset |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var tag in report.Tags.Where(t => t.Name.StartsWith("info-D", StringComparison.Ordinal)).OrderBy(t => NaturalNumberSuffix(t.Name)))
        {
            sb.AppendLine($"| `{tag.Name}` | `{tag.Address}` | `{tag.AddressFieldLength - 1}` | `0x{tag.Offset:X}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## Trend/Value Tags");
        sb.AppendLine();
        sb.AppendLine("| Name | Address | Field chars | Offset |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var tag in report.Tags.Where(t => t.Name.StartsWith("Ch", StringComparison.Ordinal) && int.TryParse(t.Name.AsSpan(2), out _)).OrderBy(t => NaturalNumberSuffix(t.Name)))
        {
            sb.AppendLine($"| `{tag.Name}` | `{tag.Address}` | `{tag.AddressFieldLength - 1}` | `0x{tag.Offset:X}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## Detector Label Slots");
        sb.AppendLine();
        sb.AppendLine("| Slot | Key | Current value | Value chars | Offset |");
        sb.AppendLine("|---:|---|---|---:|---:|");
        foreach (var label in report.Labels)
        {
            sb.AppendLine($"| {label.Slot} | `{label.Key}` | `{label.Value.TrimEnd()}` | `{label.ValueLength}` | `0x{label.Offset:X}` |");
        }

        return sb.ToString();
    }

    private static int NaturalNumberSuffix(string name)
    {
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        return int.TryParse(name.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
    }

    private static IEnumerable<TagRecord> ParseTags(byte[] project)
    {
        byte[] marker = Encoding.ASCII.GetBytes("ENHANCEDTAGS_L32");
        int markerOffset = IndexOf(project, marker);
        if (markerOffset < 0) yield break;

        int countOffset = markerOffset + marker.Length;
        if (countOffset + 4 > project.Length) yield break;

        int count = BitConverter.ToInt32(project, countOffset);
        int off = countOffset + 4;
        for (int record = 0; record < count && off + 5 <= project.Length; record++)
        {
            int nameLen = project[off + 3];
            int addrLen = project[off + 4];
            int nameOffset = off + 5;
            int addrOffset = nameOffset + nameLen;
            int next = addrOffset + addrLen;
            if (next > project.Length) yield break;

            yield return new TagRecord(
                off,
                project[off],
                project[off + 1],
                project[off + 2],
                ReadNullTerminatedAscii(project, nameOffset, nameLen),
                ReadNullTerminatedAscii(project, addrOffset, addrLen),
                nameLen,
                addrLen);
            off = next;
        }
    }

    private static IEnumerable<DetectorLabelSlot> ParseDetectorLabelSlots(byte[] project)
    {
        LabelLibrary? library = ParseLabelLibrary(project);
        if (library is null) yield break;
        foreach (var record in library.Records)
        {
            if (!record.Key.StartsWith("Det-", StringComparison.Ordinal) ||
                !int.TryParse(record.Key.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
                slot is < 1 or > 32 || !record.Key.Equals($"Det-{slot}", StringComparison.Ordinal)) continue;
            yield return new DetectorLabelSlot(record.Offset, record.EndOffset, slot, record.Key, record.Value, record.ValueLength, record.Suffix);
        }
    }

    private static LabelLibrary? ParseLabelLibrary(byte[] project)
    {
        byte[] marker = Encoding.ASCII.GetBytes("LABE_LIB");
        int markerOffset = IndexOf(project, marker);
        if (markerOffset < 0 || markerOffset + marker.Length + 2 > project.Length) return null;

        int count = BitConverter.ToUInt16(project, markerOffset + marker.Length);
        int off = markerOffset + marker.Length + 2;
        var records = new List<LabelRecord>(count);
        for (int record = 0; record < count; record++)
        {
            if (off + 3 > project.Length) return null;
            int keyLen = project[off];
            int valueLen = project[off + 1];
            int suffixLen = project[off + 2];
            int keyOffset = off + 3;
            int valueOffset = keyOffset + keyLen;
            int suffixOffset = valueOffset + valueLen;
            int next = suffixOffset + suffixLen;
            if (next > project.Length) return null;

            records.Add(new LabelRecord(
                off,
                next,
                Encoding.ASCII.GetString(project, keyOffset, keyLen),
                Encoding.UTF8.GetString(project, valueOffset, valueLen),
                valueLen,
                project[suffixOffset..next]));
            off = next;
        }
        return new LabelLibrary(markerOffset, off, records);
    }

    private static void ExtractGzipTar(string gzipTarPath, string outputDir)
    {
        using var file = File.OpenRead(gzipTarPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            string name = NormalizeTarPath(entry.Name);
            string target = Path.GetFullPath(Path.Combine(outputDir, name));
            string outputRoot = Path.GetFullPath(outputDir);
            string rootWithSeparator = outputRoot + Path.DirectorySeparatorChar;
            if (!target.Equals(outputRoot, StringComparison.Ordinal) && !target.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe tar entry path: {entry.Name}");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
            if (entry.DataStream is null)
            {
                File.WriteAllBytes(target, Array.Empty<byte>());
                continue;
            }

            using var output = File.Create(target);
            entry.DataStream.CopyTo(output);
        }
    }

    private static string NormalizeTarPath(string path)
    {
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void RepackGzipTar(string sourceDir, string outputCxob)
    {
        string tarPath = Path.Combine(Path.GetTempPath(), "mx43-weintek-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tar");
        try
        {
            if (File.Exists(outputCxob)) File.Delete(outputCxob);
            TarFile.CreateFromDirectory(sourceDir, tarPath, includeBaseDirectory: false);
            using var tar = File.OpenRead(tarPath);
            using var output = File.Create(outputCxob);
            using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
            tar.CopyTo(gzip);
        }
        finally
        {
            try { if (File.Exists(tarPath)) File.Delete(tarPath); }
            catch { /* best effort cleanup */ }
        }
    }

    private static void PatchDetectorLabels(byte[] project, DetectorPlan[] detectors, List<string> warnings)
    {
        var labels = ParseDetectorLabelSlots(project).ToArray();
        if (labels.Length == 0)
        {
            warnings.Add("Template has no Det-N label-library entries; detector labels were not patched and must be driven by referenced template objects.");
            return;
        }

        foreach (var detector in detectors)
        {
            var label = labels.FirstOrDefault(l => l.Slot == detector.ScreenNo);
            if (label is null)
            {
                warnings.Add($"Template has no Det-{detector.ScreenNo} label entry; '{detector.Label}' was not patched.");
                continue;
            }
            int valueOffset = label.Offset + 3 + Encoding.ASCII.GetByteCount(label.Key);
            WriteFixedUtf8(project, valueOffset, label.ValueLength, detector.Label, warnings, $"label Det-{detector.ScreenNo}");
        }
    }

    private static void PatchInfoTags(byte[] project, DetectorPlan[] detectors, List<string> warnings)
    {
        byte[] marker = Encoding.ASCII.GetBytes("ENHANCEDTAGS_L32");
        int markerOffset = IndexOf(project, marker);
        if (markerOffset < 0)
        {
            warnings.Add("Could not locate ENHANCEDTAGS_L32; info-D tags were not patched.");
            return;
        }

        int countOffset = markerOffset + marker.Length;
        if (countOffset + 4 > project.Length)
        {
            warnings.Add("Could not read tag count after ENHANCEDTAGS_L32.");
            return;
        }
        int count = BitConverter.ToInt32(project, countOffset);
        int off = countOffset + 4;
        bool[] seenInfoTags = new bool[Math.Max(detectors.Length, 32) + 1];
        for (int record = 0; record < count && off + 5 <= project.Length; record++)
        {
            int nameLen = project[off + 3];
            int addrLen = project[off + 4];
            int nameOffset = off + 5;
            int addrOffset = nameOffset + nameLen;
            int next = addrOffset + addrLen;
            if (next > project.Length) break;

            string name = ReadNullTerminatedAscii(project, nameOffset, nameLen);
            if (name.StartsWith("info-D", StringComparison.Ordinal) && int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                if (n >= 1 && n < seenInfoTags.Length) seenInfoTags[n] = true;
                if (n >= 1 && n <= detectors.Length)
                {
                    WriteFixedNullTerminatedAscii(project, addrOffset, addrLen, detectors[n - 1].ConfigRegister.ToString(CultureInfo.InvariantCulture), warnings, name);
                }
                else if (n >= 1 && n <= 32)
                {
                    WriteFixedNullTerminatedAscii(project, addrOffset, addrLen, "0", warnings, name);
                }
            }

            off = next;
        }

        for (int n = 1; n <= detectors.Length; n++)
        {
            if (!seenInfoTags[n]) warnings.Add($"Template has no info-D{n} tag; detector {n} cannot be configured by this patched CXOB template.");
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
        => IndexOf(haystack, needle, 0);

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = Math.Max(0, start); i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++) if (haystack[i + j] != needle[j]) break;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static string ReadNullTerminatedAscii(byte[] data, int offset, int length)
    {
        int actual = 0;
        while (actual < length && data[offset + actual] != 0) actual++;
        return Encoding.ASCII.GetString(data, offset, actual);
    }

    private static void WriteFixedNullTerminatedAscii(byte[] data, int offset, int length, string value, List<string> warnings, string field)
    {
        if (length == 0) return;
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length + 1 > length)
        {
            warnings.Add($"Skipped {field}: value '{value}' does not fit in fixed {length - 1}-byte tag address field.");
            return;
        }
        Array.Clear(data, offset, length);
        Array.Copy(bytes, 0, data, offset, bytes.Length);
    }

    private static void WriteFixedUtf8(byte[] data, int offset, int length, string value, List<string> warnings, string field)
    {
        int charCount = value.Length;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > length)
        {
            warnings.Add($"Truncated {field}: '{value}' to {length} byte(s).");
            charCount = 0;
            byteCount = 0;
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (byteCount + rune.Utf8SequenceLength > length) break;
                byteCount += rune.Utf8SequenceLength;
                charCount += rune.Utf16SequenceLength;
            }
        }

        for (int i = 0; i < length; i++) data[offset + i] = (byte)' ';
        Encoding.UTF8.GetBytes(value.AsSpan(0, charCount), data.AsSpan(offset, byteCount));
    }

    private static void WriteNullTerminatedField(Stream output, byte[] value, int fieldLength)
    {
        output.Write(value);
        for (int i = value.Length; i < fieldLength; i++) output.WriteByte(0);
    }

    private static void RelocateProject(
        byte[] original,
        byte[] updated,
        int movedRegionStart,
        int delta,
        int oldMacroOffset,
        int oldTagDataOffset)
    {
        if (delta == 0) return;
        ValidateProjectHeader(original);

        int blockALength = BitConverter.ToInt32(original, 0x0C);
        int blockBLength = BitConverter.ToInt32(original, 0x10);
        int blockBStart = 20 + blockALength;
        if (movedRegionStart < blockBStart)
            throw new InvalidOperationException("Expanded section is outside the relocatable project block.");

        BitConverter.TryWriteBytes(updated.AsSpan(0x10, 4), checked(blockBLength + delta));

        int metadataLength = BitConverter.ToInt32(original, blockBStart);
        int metadataEnd = checked(blockBStart + metadataLength);
        if (metadataLength < 4 || metadataEnd > movedRegionStart || metadataEnd > original.Length)
            throw new InvalidOperationException("Invalid project metadata record before expanded section.");

        // The first block-B record is the project's relocation metadata. Adjust only
        // values in that record that point into the moved tail of the project.
        for (int i = blockBStart; i <= metadataEnd - 4; i++)
        {
            int target = BitConverter.ToInt32(original, i);
            if (target >= movedRegionStart && target < original.Length)
                BitConverter.TryWriteBytes(updated.AsSpan(i, 4), checked(target + delta));
        }

        int newMacroOffset = oldMacroOffset >= movedRegionStart ? oldMacroOffset + delta : oldMacroOffset;
        int newTagDataOffset = oldTagDataOffset >= movedRegionStart ? oldTagDataOffset + delta : oldTagDataOffset;
        if (oldTagDataOffset >= 4 && BitConverter.ToInt32(original, oldTagDataOffset - 4) == oldMacroOffset)
            BitConverter.TryWriteBytes(updated.AsSpan(newTagDataOffset - 4, 4), newMacroOffset);
    }

    private static void ValidateProjectHeader(byte[] project)
    {
        byte[] magic = Encoding.ASCII.GetBytes("MT8000Series");
        if (project.Length < 20 || !project.AsSpan(0, magic.Length).SequenceEqual(magic))
            throw new InvalidOperationException("Project payload does not have an MT8000Series header.");

        int blockALength = BitConverter.ToInt32(project, 0x0C);
        int blockBLength = BitConverter.ToInt32(project, 0x10);
        if (blockALength < 0 || blockBLength < 0 || 20L + blockALength + blockBLength != project.Length)
            throw new InvalidOperationException("Project block lengths do not match the payload size.");
    }

    private static void ValidateProjectStructure(byte[] project)
    {
        ValidateProjectHeader(project);
        LabelLibrary? labelLibrary = ParseLabelLibrary(project);
        var tags = ParseTags(project).ToArray();
        int labelOffset = UniqueMarkerOffset(project, "LABE_LIB");
        int tagOffset = UniqueMarkerOffset(project, "ENHANCEDTAGS_L32");
        int macroOffset = UniqueMarkerOffset(project, "MACRO_ID");
        int tagDataOffset = UniqueMarkerOffset(project, "TAG_DATA");

        if (labelLibrary is null || labelLibrary.Offset != labelOffset || labelLibrary.EndOffset != tagOffset)
            throw new InvalidOperationException("Label library is incomplete or does not end at ENHANCEDTAGS_L32.");
        if (tags.Length == 0 || TagTableEnd(project, tagOffset) != macroOffset)
            throw new InvalidOperationException("Enhanced tag section is incomplete or does not end at MACRO_ID.");
        if (macroOffset >= tagDataOffset || tagDataOffset < 4 || BitConverter.ToInt32(project, tagDataOffset - 4) != macroOffset)
            throw new InvalidOperationException("Macro/TAG_DATA section offsets are inconsistent.");
    }

    private static int UniqueMarkerOffset(byte[] project, string markerText)
    {
        byte[] marker = Encoding.ASCII.GetBytes(markerText);
        int first = IndexOf(project, marker);
        if (first < 0 || IndexOf(project, marker, first + 1) >= 0)
            throw new InvalidOperationException($"Project must contain exactly one {markerText} marker.");
        return first;
    }

    private static int TagTableEnd(byte[] project, int markerOffset)
    {
        int off = markerOffset + "ENHANCEDTAGS_L32".Length;
        if (off + 4 > project.Length) return -1;
        int count = BitConverter.ToInt32(project, off);
        if (count < 0 || count > 100_000) return -1;
        off += 4;
        for (int record = 0; record < count; record++)
        {
            if (off + 5 > project.Length) return -1;
            int next = off + 5 + project[off + 3] + project[off + 4];
            if (next > project.Length) return -1;
            off = next;
        }
        return off;
    }

    private sealed record TagRecord(int Offset, byte Flag, byte Class, byte Kind, string Name, string Address, int NameFieldLength, int AddressFieldLength);

    private sealed record DetectorLabelSlot(int Offset, int EndOffset, int Slot, string Key, string Value, int ValueLength, byte[] Suffix);

    private sealed record LabelRecord(int Offset, int EndOffset, string Key, string Value, int ValueLength, byte[] Suffix);

    private sealed record LabelLibrary(int Offset, int EndOffset, IReadOnlyList<LabelRecord> Records);

    private sealed record TemplateReport(IReadOnlyList<TagRecord> Tags, IReadOnlyList<DetectorLabelSlot> Labels, IReadOnlyList<string> Warnings);

    internal sealed record ProjectPatchResult(byte[] Project, IReadOnlyList<string> Warnings);

    private static void AppendCsv(this StringBuilder sb, object? value, bool last = false)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        bool quote = text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r');
        if (quote)
        {
            sb.Append('"');
            sb.Append(text.Replace("\"", "\"\"", StringComparison.Ordinal));
            sb.Append('"');
        }
        else
        {
            sb.Append(text);
        }
        if (!last) sb.Append(',');
    }
}
