using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Mx43Sim.Core.Cfg;
using Mx43Sim.Core.Domain;
using Mx43Sim.Core.Modbus;
using Mx43Sim.Core.Sim;

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
            TestRegisterStore();
            TestModbusServer();
            TestSimulator();
            TestEndToEnd();
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

        var store = new Mx43RegisterStore();
        var sim = new Mx43Simulator(store);
        sim.Load(cfg, null);

        int line = s.Line, det = s.Detector;
        int alarmReg = Mx43AddressMap.AlarmRegFor(line, det);

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

        sim.SetMeasurement(line, det, 110);
        Assert("at 110: alarms + overscale", (ushort)store.ReadRegU(alarmReg), (ushort)(0x07 | 0x80));

        sim.SetMeasurement(line, det, -10);
        Assert("at -10: underscale only", (ushort)store.ReadRegU(alarmReg), (ushort)0x0040);

        sim.SetMeasurement(line, det, 0);
        Assert("back to 0: no alarm", (ushort)store.ReadRegU(alarmReg), (ushort)0);

        return 0;
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
            int startReg = Mx43AddressMap.MeasurementRegFor(s0.Line, s0.Detector);
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

    private static int _failCount = 0;
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
