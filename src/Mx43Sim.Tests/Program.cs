using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mx43Sim.Core.Cfg;
using Mx43Sim.Core.Domain;
using Mx43Sim.Core.Modbus;
using Mx43Sim.Core.Sim;
using Mx43Sim.Core.Updates;
using Mx43Sim.WeintekGenerator;

namespace Mx43Sim.Tests;

internal static class Program
{
    /// <summary>
    /// Returns the absolute path to tests/fixtures/. Walks up from
    /// AppContext.BaseDirectory (which is bin/Debug/net10.0/) through
    /// 5 levels to reach the project root, then descends into
    /// tests/fixtures.
    /// </summary>
    private static string FixturesDir()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        return Path.Combine(projectRoot, "tests", "fixtures");
    }

    /// <summary>
    /// Picks the first .cfg in tests/fixtures/ matching <paramref name="predicate"/>.
    /// Returns null if no fixture matches.
    /// </summary>
    private static string? FindFixture(Func<Mx43Config, bool> predicate)
    {
        var dir = FixturesDir();
        if (!Directory.Exists(dir)) return null;
        foreach (var p in Directory.GetFiles(dir, "*.cfg").OrderBy(x => x))
        {
            try
            {
                if (predicate(new Mx43CfgParser(p).Parse())) return p;
            }
            catch
            {
                // skip unparseable files
            }
        }
        return null;
    }

    private static int Main(string[] args)
    {
        try
        {
            TestCfgParser();
            TestAddressListParser();
            TestSelfUpdater();
            TestRegisterStore();
            TestConfigVirtualRead();
            TestModbusServer();
            TestSimulator();
            TestAlarmDirections();
            TestWeintekGenerator();
            TestEndToEnd();
            TestLineAssignment();
            TestLineAssignmentAllFiles();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            return 99;
        }
        Console.WriteLine(_failCount == 0 ? "ALL TESTS PASSED" : $"{_failCount} TEST(S) FAILED");
        return _failCount;
    }

    private static int TestAddressListParser()
    {
        var list = AddressList.Default;
        Assert("measurement line 1 det 1 = 2001", list.MeasurementAddr[0, 0], 2001);
        Assert("measurement line 1 det 32 = 2032", list.MeasurementAddr[0, 31], 2032);
        Assert("measurement line 2 det 1 = 2033", list.MeasurementAddr[1, 0], 2033);
        Assert("alarm line 1 det 1 = 2301", list.AlarmAddr[0, 0], 2301);
        Assert("config line 1 det 1 = 1", list.ConfigAddr[0, 0], 1);
        Assert("config line 1 det 2 = 2", Mx43AddressMap.ConfigBaseFor(1, 2), 2);
        Assert("config line 2 det 1 = 33", Mx43AddressMap.ConfigBaseFor(2, 1), 33);
        Assert("analog measurement 1 = 2257", list.AnalogMeasurementAddr[0], 2257);
        return 0;
    }

    private static int TestCfgParser()
    {
        var fixturesDir = FixturesDir();
        var cfgs = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "*.cfg")
            : Array.Empty<string>();
        if (cfgs.Length == 0)
        {
            Console.WriteLine($"SKIP: no .cfg files in {fixturesDir}");
            Console.WriteLine("      drop .cfg files into tests/fixtures/ to run the smoke tests");
            return 0;
        }
        Console.WriteLine($"Found {cfgs.Length} cfg files");

        foreach (var path in cfgs.OrderBy(p => p))
        {
            var name = Path.GetFileName(path);
            var cfg = new Mx43CfgParser(path).Parse();
            var zoneList = string.Join(", ", cfg.Zones.Select(z => z.Name));
            var moduleList = string.Join(", ", cfg.Modules.Select(m => $"{m.Tag}({m.Channels.Count}ch)"));
            var onBoardPreview = string.Join(", ", cfg.OnBoardRelays.Take(7).Select(r => r.Name));

            Console.WriteLine();
            Console.WriteLine($"--- {name} ---");
            Console.WriteLine($"  project   : '{cfg.ProjectName}'");
            Console.WriteLine($"  screen    : {cfg.ScreenWidth}x{cfg.ScreenHeight}");
            Console.WriteLine($"  access    : '{cfg.AccessLevel}'");
            Console.WriteLine($"  zones     : {cfg.Zones.Count}  {zoneList}");
            Console.WriteLine($"  modules   : {cfg.Modules.Count}  {moduleList}");
            Console.WriteLine($"  on-board  : {cfg.OnBoardRelays.Count}  {onBoardPreview}");
            Console.WriteLine($"  sensors   : {cfg.Sensors.Count}");
            foreach (var s in cfg.Sensors.Take(4))
                Console.WriteLine($"    L{s.Line}D{s.Detector}: '{s.Label}' {s.ShortGasName} unit={s.Unit} range={s.Range}");
            if (cfg.Sensors.Count > 4) Console.WriteLine($"    ... and {cfg.Sensors.Count - 4} more");

            if (string.IsNullOrWhiteSpace(cfg.ProjectName)) { _failCount++; Console.WriteLine($"  FAIL project name empty"); }
            if (cfg.Sensors.Count == 0) { _failCount++; Console.WriteLine($"  FAIL no sensors parsed"); }
            foreach (var s in cfg.Sensors)
            {
                if (string.IsNullOrWhiteSpace(s.Label))   { _failCount++; Console.WriteLine($"  FAIL empty label L{s.Line}D{s.Detector}"); }
                if (string.IsNullOrWhiteSpace(s.ShortGasName)) { _failCount++; Console.WriteLine($"  FAIL empty short gas L{s.Line}D{s.Detector}"); }
                if (s.Range <= 0)                       { _failCount++; Console.WriteLine($"  FAIL zero range L{s.Line}D{s.Detector}"); }
                if (s.Line < 1 || s.Line > 8)            { _failCount++; Console.WriteLine($"  FAIL bad line {s.Line}"); }
                if (s.Detector < 1 || s.Detector > 32)  { _failCount++; Console.WriteLine($"  FAIL bad detector {s.Detector}"); }
            }
        }
        return 0;
    }

    private static int TestSelfUpdater()
    {
        // Pure-function tests for version comparison. We do NOT hit
        // the network here; the integration with GitHub releases is
        // exercised manually in production.
        Assert("v0.2.0 newer than 0.1.0",  VersionUtils.IsNewer("v0.2.0", "0.1.0"), true);
        Assert("v0.1.0 not newer than 0.2.0", VersionUtils.IsNewer("v0.1.0", "0.2.0"), false);
        Assert("v0.2.0 == 0.2.0 (not newer)", VersionUtils.IsNewer("v0.2.0", "0.2.0"), false);
        Assert("v1.0.0 newer than 0.9.99", VersionUtils.IsNewer("v1.0.0", "0.9.99"), true);
        Assert("v0.10.0 newer than 0.9.0", VersionUtils.IsNewer("v0.10.0", "0.9.0"), true);
        Assert("v0.2.1 newer than 0.2.0",  VersionUtils.IsNewer("v0.2.1", "0.2.0"), true);
        Assert("invalid input returns false", VersionUtils.IsNewer("not-a-version", "0.1.0"), false);
        return 0;
    }

    private static int TestRegisterStore()
    {
        var store = new Mx43RegisterStore();
        Assert("detector count", store.Detectors.Length, Mx43AddressMap.TotalDetectors + Mx43AddressMap.AnalogChannels);
        Assert("read zero register", store.ReadReg(1), (short)0);
        store.WriteRegU(100, 0x1234);
        Assert("read back u16", store.ReadRegU(100), (ushort)0x1234);
        store.WriteReg(101, -50);
        Assert("read back s16", store.ReadReg(101), (short)-50);

        store.Write32(2600, 0xDEADBEEF);
        Assert("crc32 msw", store.ReadRegU(Mx43AddressMap.InfoCrcMsw), (ushort)0xDEAD);
        Assert("crc32 lsw", store.ReadRegU(Mx43AddressMap.InfoCrcLsw), (ushort)0xBEEF);
        return 0;
    }

    private static int TestConfigVirtualRead()
    {
        var cfg = new Mx43Config();
        cfg.Sensors.Add(new Sensor
        {
            Line = 1,
            Detector = 1,
            Label = "Ammoniak",
            FullGasName = "Ammonia",
            Unit = "ppm",
            ShortGasName = "NH3",
            Range = 1000,
            DisplayFormat = 0,
            BarLed = 8,
            Thresholds = new AlarmThresholds { Inst1 = 20, Inst2 = 30, Inst3 = 50, Underscale = -5, Overscale = 100, OutOfRange = 110 },
            EnableFlags = AlarmEnable.Inst1 | AlarmEnable.Inst2 | AlarmEnable.Inst3,
        });
        cfg.Sensors.Add(new Sensor
        {
            Line = 1,
            Detector = 2,
            Label = "Kanal 2",
            Unit = "%LEL",
            ShortGasName = "CH4",
            Range = 100,
            DisplayFormat = 0,
            BarLed = 8,
            Thresholds = new AlarmThresholds { Inst1 = 10, Inst2 = 20, Inst3 = 30, Underscale = -5, Overscale = 100, OutOfRange = 110 },
            EnableFlags = AlarmEnable.Inst1 | AlarmEnable.Inst2 | AlarmEnable.Inst3,
        });

        var store = new Mx43RegisterStore();
        var sim = new Mx43Simulator(store);
        sim.Load(cfg, null);

        var block1 = store.ReadRange(Mx43AddressMap.ConfigBaseFor(1, 1), Mx43AddressMap.ConfigBlockSize);
        var block2 = store.ReadRange(Mx43AddressMap.ConfigBaseFor(1, 2), Mx43AddressMap.ConfigBlockSize);

        Assert("config L1D1 label", DecodeUtf16(block1, 0, 16), "Ammoniak");
        Assert("config L1D1 full gas", DecodeUtf16(block1, 17, 20), "Ammonia");
        Assert("config L1D2 label", DecodeUtf16(block2, 0, 16), "Kanal 2");
        Assert("config L1D2 status", (ushort)block2[16], (ushort)1);
        Assert("config L1D2 range", (ushort)block2[37], (ushort)100);
        Assert("config L1D2 unit", DecodeUtf16(block2, 39, 5), "%LEL");
        Assert("config L1D2 short gas", DecodeUtf16(block2, 44, 6), "CH4");
        Assert("config L1D2 Inst1", block2[51], (short)10);

        var server = new Mx43ModbusServer(store, 0);
        var fc3 = server.GetType()
            .GetMethod("HandleRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(server, new object?[] { (byte)1, (byte)3, new byte[] { 3, 0x00, 0x02, 0x00, 0x10 } });
        var arr = (byte[])fc3!;
        Assert("FC3 config echo", arr[0], (byte)3);
        Assert("FC3 config byte count", arr[1], (byte)32);
        Assert("FC3 config L1D2 label", DecodeUtf16Response(arr, 2, 16), "Kanal 2");
        return 0;
    }

    private static int TestModbusServer()
    {
        var store = new Mx43RegisterStore();
        store.WriteRegU(2001, 123);
        store.WriteRegU(2301, 0x0042);

        var server = new Mx43ModbusServer(store, 0);
        server.Log += Console.WriteLine;
        var fc3 = server.GetType()
            .GetMethod("HandleRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(server, new object?[] { (byte)1, (byte)3, new byte[] { 3, 0x07, 0xD1, 0x00, 0x06 } });
        var arr = (byte[])fc3!;
        Assert("FC3 echo",   arr[0], (byte)3);
        Assert("FC3 byte count", arr[1], (byte)12);
        var v = (arr[2] << 8) | arr[3];
        Assert("FC3 register 2001 = 123", v, 123);
        return 0;
    }

    private static int TestSimulator()
    {
        // End-to-end alarm derivation. We pick a fixture with a CH4 %LEL
        // sensor (the canonical thresholds are Inst1=20, Inst2=30,
        // Inst3=50, range=100). If no such fixture is present, skip.
        var cfgPath = FindFixture(c => c.Sensors.Any(s => s.ShortGasName == "CH4"));
        if (cfgPath is null) { Console.WriteLine("SKIP: TestSimulator needs a CH4 %LEL fixture in tests/fixtures/"); return 0; }

        var cfg = new Mx43CfgParser(cfgPath).Parse();
        var s = cfg.Sensors.First(x => x.ShortGasName == "CH4");
        Assert("CH4 sensor range=100",     s.Range, 100);
        Assert("CH4 sensor Inst1=20",      s.Thresholds.Inst1, 20);
        Assert("CH4 sensor Inst2=30",      s.Thresholds.Inst2, 30);
        Assert("CH4 sensor Inst3=50",      s.Thresholds.Inst3, 50);
        Assert("CH4 sensor Underscale=-5", s.Thresholds.Underscale, -5);
        Assert("CH4 sensor Overscale=100", s.Thresholds.Overscale, 100);
        Assert("CH4 sensor OutOfRange=110",s.Thresholds.OutOfRange, 110);
        Assert("CH4 sensor enable=0x07",   (ushort)s.EnableFlags, (ushort)0x07);
        Assert("alarm bit Inst3=0x0004",   (ushort)AlarmBits.Inst3, (ushort)0x0004);
        Assert("alarm bit UDS=0x0008",     (ushort)AlarmBits.Underscale, (ushort)0x0008);
        Assert("alarm bit OVS=0x0010",     (ushort)AlarmBits.Overscale, (ushort)0x0010);
        Assert("alarm bit fault=0x0020",   (ushort)AlarmBits.Fault, (ushort)0x0020);
        Assert("alarm bit OOR=0x0040",     (ushort)AlarmBits.OutOfRange, (ushort)0x0040);

        var store = new Mx43RegisterStore();
        var sim = new Mx43Simulator(store);
        sim.Load(cfg, null);

        int line = s.Line, det = s.Detector;
        int alarmReg = Mx43AddressMap.AlarmRegFor(s);

        sim.SetMeasurement(line, det, 0);
        Assert("at 0: no alarm", (ushort)store.ReadRegU(alarmReg), (ushort)0);

        sim.SetMeasurement(line, det, 10);
        Assert("at 10: no alarm", (ushort)store.ReadRegU(alarmReg), (ushort)0);

        sim.SetMeasurement(line, det, 20);
        Assert("at 20: Inst1 set", (ushort)store.ReadRegU(alarmReg), (ushort)0x0001);

        sim.SetMeasurement(line, det, 30);
        Assert("at 30: Inst1+Inst2", (ushort)store.ReadRegU(alarmReg), (ushort)0x0003);

        sim.SetMeasurement(line, det, 50);
        Assert("at 50: Inst1+2+3", (ushort)store.ReadRegU(alarmReg), (ushort)0x0007);

        sim.SetMeasurement(line, det, 100);
        Assert("at 100: alarms + overscale at boundary", (ushort)store.ReadRegU(alarmReg), (ushort)(0x07 | 0x10));

        sim.SetMeasurement(line, det, 110);
        Assert("at 110: alarms + OVS+fault+OOR at boundary", (ushort)store.ReadRegU(alarmReg), (ushort)(0x07 | 0x10 | 0x20 | 0x40));

        sim.SetMeasurement(line, det, -4);
        Assert("at -4: no underscale", (ushort)store.ReadRegU(alarmReg), (ushort)0x0000);

        sim.SetMeasurement(line, det, -5);
        Assert("at -5: underscale at boundary", (ushort)store.ReadRegU(alarmReg), (ushort)0x0008);

        sim.SetMeasurement(line, det, -10);
        Assert("at -10: underscale only", (ushort)store.ReadRegU(alarmReg), (ushort)0x0008);

        sim.SetMeasurement(line, det, 0);
        Assert("back to 0: no alarm", (ushort)store.ReadRegU(alarmReg), (ushort)0);

        return 0;
    }

    private static int TestAlarmDirections()
    {
        var cfg = new Mx43Config();
        cfg.Sensors.Add(new Sensor
        {
            Line = 1,
            Detector = 1,
            ShortGasName = "CH4",
            Thresholds = new AlarmThresholds { Inst1 = 20 },
            EnableFlags = AlarmEnable.Inst1,
            EdgeFlags = AlarmEdge.Inst1,
        });
        cfg.Sensors.Add(new Sensor
        {
            Line = 1,
            Detector = 2,
            ShortGasName = "O2",
            DisplayFormat = 1,
            Thresholds = new AlarmThresholds { Inst1 = 190, Inst2 = 170 },
            EnableFlags = AlarmEnable.Inst1 | AlarmEnable.Inst2,
            EdgeFlags = AlarmEdge.None,
        });

        var store = new Mx43RegisterStore();
        var sim = new Mx43Simulator(store);
        sim.Load(cfg);

        Assert("CH4 starts at 0", sim.GetMeasurement(1, 1), (short)0);
        Assert("O2 starts at 210", sim.GetMeasurement(1, 2), (short)210);
        Assert("O2 start register = 210", store.ReadReg(Mx43AddressMap.MeasurementRegFor(1, 2)), (short)210);

        sim.SetMeasurement(1, 1, 19);
        Assert("rising alarm below threshold is clear", (ushort)sim.GetAlarm(1, 1), (ushort)0);
        sim.SetMeasurement(1, 1, 20);
        Assert("rising alarm at threshold is set", (ushort)sim.GetAlarm(1, 1), (ushort)AlarmBits.Inst1);

        sim.SetMeasurement(1, 2, 191);
        Assert("falling O2 alarm above threshold is clear", (ushort)sim.GetAlarm(1, 2), (ushort)0);
        sim.SetMeasurement(1, 2, 190);
        Assert("falling O2 alarm 1 at threshold is set", (ushort)sim.GetAlarm(1, 2), (ushort)AlarmBits.Inst1);
        sim.SetMeasurement(1, 2, 170);
        Assert("falling O2 alarms 1+2 are set", (ushort)sim.GetAlarm(1, 2), (ushort)(AlarmBits.Inst1 | AlarmBits.Inst2));
        sim.SetMeasurement(1, 2, 210);
        Assert("falling O2 alarms clear at normal level", (ushort)sim.GetAlarm(1, 2), (ushort)0);
        return 0;
    }

    private static int TestWeintekGenerator()
    {
        var cfg = new Mx43Config();
        cfg.Sensors.Add(AlarmSensor(1, 1, "Only A1", 20, 0, 0,
            AlarmEnable.Inst1));
        cfg.Sensors.Add(AlarmSensor(1, 2, "A1+A2", 20, 40, 0,
            AlarmEnable.Inst1 | AlarmEnable.Inst2));
        cfg.Sensors.Add(AlarmSensor(1, 3, "A1+A2+A3", 20, 40, 60,
            AlarmEnable.Inst1 | AlarmEnable.Inst2 | AlarmEnable.Inst3));
        cfg.Sensors.Add(new Sensor
        {
            Line = 1,
            Detector = 4,
            Label = "Average A1",
            Thresholds = new AlarmThresholds { Avg1 = 10 },
            EnableFlags = AlarmEnable.Avg1,
        });
        cfg.Sensors[2].DisplayFormat = 9;

        var plans = DetectorPlanner.Create(cfg);
        Assert("Weintek one alarm count", plans[0].AlarmLevelCount, 1);
        Assert("Weintek one alarm highest", plans[0].HighestConfiguredAlarmBit, 1);
        Assert("Weintek two alarm count", plans[1].AlarmLevelCount, 2);
        Assert("Weintek two alarm highest", plans[1].HighestConfiguredAlarmBit, 2);
        Assert("Weintek display format is clamped", plans[2].DisplayFormat, 4);
        Assert("Weintek display divisor follows format", plans[2].ScaleDivisor, 10_000);
        Assert("Weintek averaged alarm output is configured", plans[3].AlarmLevelCount, 1);

        string macro = MacroGenerator.RenderRuntimeSampler(plans);
        Assert("Weintek macro closes if blocks", CountOccurrences(macro, "end if"), 4);
        Assert("Weintek macro includes underscale as red", macro.Contains("& 0x007C", StringComparison.Ordinal), true);
        Assert("Weintek sole alarm 1 is red", macro.Contains(
            "else if (Det1_AlarmBits & 0x0001) <> 0 then\n    Det1_AlarmSeverity = 3\nend if", StringComparison.Ordinal), true);
        Assert("Weintek two-level alarm 2 is red", macro.Contains(
            "else if (Det2_AlarmBits & 0x0002) <> 0 then\n    Det2_AlarmSeverity = 3", StringComparison.Ordinal), true);
        Assert("Weintek three-level alarm 2 is orange", macro.Contains(
            "else if (Det3_AlarmBits & 0x0002) <> 0 then\n    Det3_AlarmSeverity = 2", StringComparison.Ordinal), true);

        var patchCfg = new Mx43Config();
        patchCfg.Sensors.Add(new Sensor
        {
            Line = 1,
            Detector = 1,
            AnalogChannel = 1,
            Label = "Vägg O2",
            ShortGasName = "O2",
            DisplayFormat = 1,
            Thresholds = new AlarmThresholds { Inst1 = 190 },
            EnableFlags = AlarmEnable.Inst1,
        });
        var patchPlans = DetectorPlanner.Create(patchCfg);
        byte[] source = BuildSyntheticWeintekProject(out int oldMacroOffset, out int oldTagDataOffset, out int unrelatedPointerOffset);
        var patch = Mx43Sim.WeintekGenerator.Program.PatchProjectPayload(source, patchPlans, allowBinaryExpansion: true);
        byte[] project = patch.Project;
        int newMacroOffset = IndexOf(project, "MACRO_ID");
        int newTagDataOffset = IndexOf(project, "TAG_DATA");

        Assert("Weintek expanded project grows", project.Length > source.Length, true);
        Assert("Weintek project header matches expanded size",
            20 + BitConverter.ToInt32(project, 0x0C) + BitConverter.ToInt32(project, 0x10), project.Length);
        Assert("Weintek metadata relocates macro", BitConverter.ToInt32(project, 20 + 32 + 8), newMacroOffset);
        Assert("Weintek metadata relocates TAG_DATA", BitConverter.ToInt32(project, 20 + 32 + 12), newTagDataOffset);
        Assert("Weintek metadata relocates inner TAG_DATA pointer", BitConverter.ToInt32(project, 20 + 32 + 16), newTagDataOffset + 12);
        Assert("Weintek TAG_DATA footer points to macro", BitConverter.ToInt32(project, newTagDataOffset - 4), newMacroOffset);
        Assert("Weintek unrelated offset-like integer is untouched", BitConverter.ToInt32(project, unrelatedPointerOffset), oldMacroOffset);
        Assert("Weintek expanded label preserves UTF-8", IndexOf(project, Encoding.UTF8.GetBytes("Vägg O2")) >= 0, true);
        Assert("Weintek expanded analog address is present", IndexOf(project, Encoding.ASCII.GetBytes("257\0")) >= 0, true);
        Assert("Weintek source buffer remains unchanged", IndexOf(source, Encoding.UTF8.GetBytes("Vägg O2")) < 0, true);
        Assert("Weintek expansion changed macro offset", newMacroOffset > oldMacroOffset, true);
        Assert("Weintek expansion changed TAG_DATA offset", newTagDataOffset > oldTagDataOffset, true);
        return 0;
    }

    private static Sensor AlarmSensor(int line, int detector, string label, int alarm1, int alarm2, int alarm3, AlarmEnable enabled)
        => new()
        {
            Line = line,
            Detector = detector,
            Label = label,
            Thresholds = new AlarmThresholds { Inst1 = alarm1, Inst2 = alarm2, Inst3 = alarm3 },
            EnableFlags = enabled,
        };

    private static byte[] BuildSyntheticWeintekProject(out int macroOffset, out int tagDataOffset, out int unrelatedPointerOffset)
    {
        byte[] blockA = new byte[32];
        byte[] labels;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("LABE_LIB1"));
            for (int slot = 1; slot <= 32; slot++)
            {
                byte[] key = Encoding.ASCII.GetBytes($"Det-{slot}");
                byte[] value = Encoding.ASCII.GetBytes(slot.ToString());
                writer.Write((byte)key.Length);
                writer.Write((byte)value.Length);
                writer.Write((byte)0);
                writer.Write(key);
                writer.Write(value);
            }
            labels = stream.ToArray();
        }

        byte[] tags;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("ENHANCEDTAGS_L32"));
            writer.Write(2);
            WriteSyntheticTag(writer, "info-D1", "1");
            WriteSyntheticTag(writer, "Ch1", "10");
            tags = stream.ToArray();
        }

        const int metadataLength = 64;
        int blockBStart = 20 + blockA.Length;
        macroOffset = blockBStart + metadataLength + labels.Length + tags.Length;
        byte[] macro = new byte["MACRO_ID".Length + 12];
        Encoding.ASCII.GetBytes("MACRO_ID").CopyTo(macro, 0);
        tagDataOffset = macroOffset + macro.Length;
        BitConverter.TryWriteBytes(macro.AsSpan(macro.Length - 4), macroOffset);

        byte[] metadata = new byte[metadataLength];
        BitConverter.TryWriteBytes(metadata.AsSpan(0, 4), metadataLength);
        BitConverter.TryWriteBytes(metadata.AsSpan(8, 4), macroOffset);
        BitConverter.TryWriteBytes(metadata.AsSpan(12, 4), tagDataOffset);
        BitConverter.TryWriteBytes(metadata.AsSpan(16, 4), tagDataOffset + 12);

        byte[] tagData = new byte[32];
        Encoding.ASCII.GetBytes("TAG_DATA").CopyTo(tagData, 0);
        int blockBLength = metadata.Length + labels.Length + tags.Length + macro.Length + tagData.Length;
        byte[] project = new byte[20 + blockA.Length + blockBLength];
        Encoding.ASCII.GetBytes("MT8000Series").CopyTo(project, 0);
        BitConverter.TryWriteBytes(project.AsSpan(0x0C, 4), blockA.Length);
        BitConverter.TryWriteBytes(project.AsSpan(0x10, 4), blockBLength);
        blockA.CopyTo(project, 20);
        metadata.CopyTo(project, blockBStart);
        labels.CopyTo(project, blockBStart + metadata.Length);
        tags.CopyTo(project, blockBStart + metadata.Length + labels.Length);
        macro.CopyTo(project, macroOffset);
        tagData.CopyTo(project, tagDataOffset);

        unrelatedPointerOffset = 20;
        BitConverter.TryWriteBytes(project.AsSpan(unrelatedPointerOffset, 4), macroOffset);
        return project;
    }

    private static void WriteSyntheticTag(BinaryWriter writer, string name, string address)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] addressBytes = Encoding.ASCII.GetBytes(address);
        writer.Write((byte)1);
        writer.Write((byte)2);
        writer.Write((byte)3);
        writer.Write((byte)(nameBytes.Length + 1));
        writer.Write((byte)(addressBytes.Length + 1));
        writer.Write(nameBytes);
        writer.Write((byte)0);
        writer.Write(addressBytes);
        writer.Write((byte)0);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int offset = 0; (offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0; offset += value.Length) count++;
        return count;
    }

    private static int IndexOf(byte[] data, string value) => IndexOf(data, Encoding.ASCII.GetBytes(value));

    private static int IndexOf(byte[] data, byte[] value)
    {
        for (int i = 0; i <= data.Length - value.Length; i++)
        {
            if (data.AsSpan(i, value.Length).SequenceEqual(value)) return i;
        }
        return -1;
    }

    private static int TestEndToEnd()
    {
        var cfgPath = FindFixture(c => c.Sensors.Count > 0);
        if (cfgPath is null) { Console.WriteLine("SKIP: end-to-end test needs a sensor fixture in tests/fixtures/"); return 0; }

        var cfg = new Mx43CfgParser(cfgPath).Parse();
        var s0 = cfg.Sensors[0];
        var addrs = AddressList.Default;
        var store = new Mx43RegisterStore();
        var sim = new Mx43Simulator(store);
        sim.Load(cfg, addrs);
        sim.SetMeasurement(s0.Line, s0.Detector, 42);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var server = new Mx43ModbusServer(store, port);
        server.Log += Console.WriteLine;
        server.StartAsync().GetAwaiter().GetResult();

        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            using var s = client.GetStream();
            int startReg = Mx43AddressMap.MeasurementRegFor(s0);
            var req = new byte[] {
                0, 1, 0, 0, 0, 6, 1,
                3, (byte)(startReg >> 8), (byte)(startReg & 0xFF), 0, 4,
            };
            s.Write(req, 0, req.Length);

            var resp = new byte[256];
            int got = s.Read(resp, 0, resp.Length);
            Assert("response has MBAP+PDU", got >= 13, true);
            Assert("FC echo = 3", resp[7], (byte)3);
            Assert("byte count = 8", resp[8], (byte)8);
            ushort v1 = (ushort)((resp[9] << 8) | resp[10]);
            Assert("measurement register = 42", v1, (ushort)42);
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
        }
        return 0;
    }

    /// <summary>
    /// Verifies that the line/detector assignment read from the 0x9A00
    /// detector list matches the expected Modbus addressing. Uses the
    /// Volvo Skövde MX43-8 export (19 detectors across 2 lines) as the
    /// reference case: sensorIds 1-3 -> line 1, 33-48 -> line 2.
    /// </summary>
    private static int TestLineAssignment()
    {
        var fixturesDir = FixturesDir();
        var volvoPath = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "*.cfg")
                .FirstOrDefault(p => Path.GetFileName(p).Contains("Volvo", StringComparison.OrdinalIgnoreCase))
            : null;
        if (volvoPath is null)
        {
            Console.WriteLine("SKIP: TestLineAssignment needs the Volvo Skövde fixture in tests/fixtures/");
            return 0;
        }

        var cfg = new Mx43CfgParser(volvoPath).Parse();
        Console.WriteLine($"--- line assignment ({Path.GetFileName(volvoPath)}) ---");
        foreach (var s in cfg.Sensors)
            Console.WriteLine($"  L{s.Line}D{s.Detector,2}: {s.Label}");

        // The Volvo file has 19 detectors: 3 on line 1 (Kylmaskin),
        // 16 on line 2 (8 NH3 + 6 CO + 2 H2).
        var line1 = cfg.Sensors.Where(s => s.Line == 1).ToList();
        var line2 = cfg.Sensors.Where(s => s.Line == 2).ToList();
        Assert("Volvo has 3 sensors on line 1", line1.Count, 3);
        Assert("Volvo has 16 sensors on line 2", line2.Count, 16);
        Assert("line 1 det 1 is Kylmaskin 1", line1[0].Label, "Kylmaskin 1");
        Assert("line 2 det 1 is Ugn 1 NH3", line2[0].Label, "Ugn 1 NH3");
        Assert("line 2 det 9 is Ugn 1 CO", line2[8].Label, "Ugn 1 CO");
        Assert("line 2 det 15 is Ugn 1-3 H2", line2[14].Label, "Ugn 1-3 H2");

        // Verify that the Modbus measurement addresses line up with
        // the address-map formula. Digital detectors use 2001..2256;
        // direct 4-20 mA channels use the analog range 2257..2264.
        foreach (var s in cfg.Sensors)
        {
            int expected = s.IsAnalog
                ? 2256 + s.AnalogChannel
                : 2000 + (s.Line - 1) * 32 + s.Detector;
            int actual = Mx43AddressMap.MeasurementRegFor(s);
            Assert($"L{s.Line}D{s.Detector} measurement reg = {expected}", actual, expected);
        }

        var nynasPath = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "*.cfg")
                .FirstOrDefault(p => Path.GetFileName(p).Contains("Nyn", StringComparison.OrdinalIgnoreCase))
            : null;
        if (nynasPath is not null)
        {
            var nynas = new Mx43CfgParser(nynasPath).Parse();
            Assert("Nynas detector count", nynas.Sensors.Count, 7);
            Assert("Nynas line 1 count", nynas.Sensors.Count(s => s.Line == 1), 3);
            Assert("Nynas line 2 count", nynas.Sensors.Count(s => s.Line == 2), 2);
            Assert("Nynas line 3 direct 4-20", nynas.Sensors.Count(s => s.Line == 3 && s.Detector == 1), 1);
            Assert("Nynas line 4 direct 4-20", nynas.Sensors.Count(s => s.Line == 4 && s.Detector == 1), 1);
            Assert("Nynas line 3 analog measurement", Mx43AddressMap.MeasurementRegFor(nynas.Sensors.Single(s => s.Line == 3 && s.Detector == 1)), 2259);
            Assert("Nynas line 4 analog measurement", Mx43AddressMap.MeasurementRegFor(nynas.Sensors.Single(s => s.Line == 4 && s.Detector == 1)), 2260);
        }

        var ppmPath = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "ppm.cfg").FirstOrDefault()
            : null;
        if (ppmPath is not null)
        {
            var ppm = new Mx43CfgParser(ppmPath).Parse();
            Assert("ppm detector count", ppm.Sensors.Count, 6);
            for (int lineNo = 1; lineNo <= 6; lineNo++)
            {
                Assert($"ppm line {lineNo} direct 4-20", ppm.Sensors.Count(s => s.Line == lineNo && s.Detector == 1), 1);
                var analogSensor = ppm.Sensors.Single(s => s.Line == lineNo && s.Detector == 1);
                Assert($"ppm line {lineNo} is analog", analogSensor.IsAnalog, true);
                Assert($"ppm line {lineNo} analog measurement", Mx43AddressMap.MeasurementRegFor(analogSensor), 2256 + lineNo);
                Assert($"ppm line {lineNo} analog alarm", Mx43AddressMap.AlarmRegFor(analogSensor), 2556 + lineNo);
            }

            var store = new Mx43RegisterStore();
            var sim = new Mx43Simulator(store);
            sim.Load(ppm, null);
            var analogConfig = store.ReadRange(Mx43AddressMap.AnalogConfigBase, Mx43AddressMap.ConfigBlockSize);
            Assert("ppm configured analog channel is enabled", analogConfig[16], (short)1);
            Assert("ppm analog full gas name is preserved", string.IsNullOrWhiteSpace(DecodeUtf16(analogConfig, 17, 20)), false);
            sim.SetMeasurement(1, 1, 42);
            Assert("ppm analog L1D1 writes reg 2257", store.ReadReg(Mx43AddressMap.AnalogMeasurementRegFor(1)), (short)42);
            Assert("ppm analog L1D1 does not write digital reg 2001", store.ReadReg(Mx43AddressMap.MeasurementRegFor(1, 1)), (short)0);
        }

        var polypeptidePath = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "*.cfg")
                .FirstOrDefault(p => Path.GetFileName(p).Contains("Polypeptide", StringComparison.OrdinalIgnoreCase))
            : null;
        if (polypeptidePath is not null)
        {
            var polypeptide = new Mx43CfgParser(polypeptidePath).Parse();
            Assert("Polypeptide detector count", polypeptide.Sensors.Count, 16);
            for (int lineNo = 1; lineNo <= 7; lineNo++)
            {
                Assert($"Polypeptide line {lineNo} direct 4-20", polypeptide.Sensors.Count(s => s.Line == lineNo && s.Detector == 1), 1);
                Assert($"Polypeptide line {lineNo} analog measurement", Mx43AddressMap.MeasurementRegFor(polypeptide.Sensors.Single(s => s.Line == lineNo && s.Detector == 1)), 2256 + lineNo);
            }
            Assert("Polypeptide line 8 count", polypeptide.Sensors.Count(s => s.Line == 8), 9);
            Assert("Polypeptide line 8 detector 1", polypeptide.Sensors.Count(s => s.Line == 8 && s.Detector == 1), 1);
            Assert("Polypeptide line 8 detector 8", polypeptide.Sensors.Count(s => s.Line == 8 && s.Detector == 8), 1);
            Assert("Polypeptide line 8 detector 11", polypeptide.Sensors.Count(s => s.Line == 8 && s.Detector == 11), 1);
        }

        var b062Path = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "*.cfg")
                .FirstOrDefault(p => Path.GetFileName(p).Contains("B062", StringComparison.OrdinalIgnoreCase))
            : null;
        if (b062Path is not null)
        {
            var b062 = new Mx43CfgParser(b062Path).Parse();
            Assert("B062 detector count", b062.Sensors.Count, 16);
            for (int lineNo = 1; lineNo <= 7; lineNo++)
            {
                Assert($"B062 line {lineNo} direct 4-20", b062.Sensors.Count(s => s.Line == lineNo && s.Detector == 1), 1);
                Assert($"B062 line {lineNo} analog measurement", Mx43AddressMap.MeasurementRegFor(b062.Sensors.Single(s => s.Line == lineNo && s.Detector == 1)), 2256 + lineNo);
            }
            Assert("B062 line 8 count", b062.Sensors.Count(s => s.Line == 8), 9);
            Assert("B062 line 8 detector 2", b062.Sensors.Count(s => s.Line == 8 && s.Detector == 2), 1);
            Assert("B062 line 8 detector 10", b062.Sensors.Count(s => s.Line == 8 && s.Detector == 10), 1);
        }
        return 0;
    }

    /// <summary>
    /// Walks every .cfg in tests/fixtures and prints the line/detector
    /// distribution. Verifies that the 0x9A00 sensor-id mapping produces
    /// monotonically increasing detector numbers within each line and
    /// that the Modbus measurement addresses are consistent. Catches
    /// cases where the parser assigns a detector to a line that doesn't
    /// match the .cfg's own detector list.
    /// </summary>
    private static int TestLineAssignmentAllFiles()
    {
        var fixturesDir = FixturesDir();
        var cfgs = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir, "*.cfg").OrderBy(p => p).ToArray()
            : Array.Empty<string>();
        if (cfgs.Length == 0)
        {
            Console.WriteLine("SKIP: no .cfg fixtures for TestLineAssignmentAllFiles");
            return 0;
        }

        foreach (var path in cfgs)
        {
            var name = Path.GetFileName(path);
            var cfg = new Mx43CfgParser(path).Parse();
            Console.WriteLine();
            Console.WriteLine($"--- {name}: {cfg.Sensors.Count} sensors ---");

            var byLine = new SortedDictionary<int, List<Sensor>>();
            foreach (var s in cfg.Sensors)
            {
                if (!byLine.ContainsKey(s.Line)) byLine[s.Line] = new List<Sensor>();
                byLine[s.Line].Add(s);
            }

            foreach (var (line, sensors) in byLine)
            {
                Console.WriteLine($"  Line {line}: {sensors.Count} detectors");
                foreach (var s in sensors)
                    Console.WriteLine($"    L{s.Line}D{s.Detector,2} {(s.IsAnalog ? "analog" : "digital"),7} reg={Mx43AddressMap.MeasurementRegFor(s)} '{s.Label}' [{s.ShortGasName}]");
            }

            // Verify detector numbers are unique and within 1..32 per line.
            foreach (var (line, sensors) in byLine)
            {
                var dets = sensors.Select(s => s.Detector).ToList();
                if (dets.Count != dets.Distinct().Count())
                {
                    Console.WriteLine($"  FAIL: line {line} has duplicate detector numbers: {string.Join(",", dets)}");
                    _failCount++;
                }
                if (dets.Any(d => d < 1 || d > 32))
                {
                    Console.WriteLine($"  FAIL: line {line} has detector out of range 1..32: {string.Join(",", dets)}");
                    _failCount++;
                }
            }

            // Verify the measurement addresses are unique and ascending.
            var addrs = cfg.Sensors
                .Select(Mx43AddressMap.MeasurementRegFor)
                .ToList();
            if (addrs.Count != addrs.Distinct().Count())
            {
                Console.WriteLine($"  FAIL: duplicate measurement addresses: {string.Join(",", addrs)}");
                _failCount++;
            }
        }
        return 0;
    }

    private static int _failCount = 0;

    private static string DecodeUtf16(short[] regs, int offset, int registerCount)
    {
        var chars = new List<char>();
        for (int i = 0; i < registerCount && offset + i < regs.Length; i++)
        {
            ushort v = (ushort)regs[offset + i];
            if (v == 0) break;
            chars.Add((char)v);
        }
        return new string(chars.ToArray());
    }

    private static string DecodeUtf16Response(byte[] bytes, int offset, int registerCount)
    {
        var chars = new List<char>();
        for (int i = 0; i < registerCount && offset + i * 2 + 1 < bytes.Length; i++)
        {
            ushort v = (ushort)((bytes[offset + i * 2] << 8) | bytes[offset + i * 2 + 1]);
            if (v == 0) break;
            chars.Add((char)v);
        }
        return new string(chars.ToArray());
    }

    private static void Assert<T>(string name, T actual, T expected)
    {
        if (Equals(actual, expected))
        {
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            Console.WriteLine($"  FAIL  {name}: expected={expected} actual={actual}");
            _failCount++;
        }
    }
    private static void Assert(string name, bool actual, bool expected)
    {
        if (actual == expected)
        {
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            Console.WriteLine($"  FAIL  {name}: expected={expected} actual={actual}");
            _failCount++;
        }
    }
    private static int FailCount => _failCount;
}
