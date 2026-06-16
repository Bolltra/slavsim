using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Mx43Sim.Core.Cfg;

/// <summary>
/// Maps a (line, detector) pair to the Modbus holding-register address
/// used for configuration, measurement and alarms. Mirrored for the 8
/// analog channels.
///
/// The data is generated once from "Mx43 adresslista.xlsx" and embedded
/// in the assembly as an embedded JSON resource. Callers should normally
/// use the default <see cref="Default"/> instance.
/// </summary>
public sealed class AddressList
{
    public int[,] ConfigAddr { get; }     = new int[8, 32];
    public int[,] MeasurementAddr { get; } = new int[8, 32];
    public int[,] AlarmAddr { get; }      = new int[8, 32];
    public int[] AnalogConfigAddr { get; } = new int[8];
    public int[] AnalogMeasurementAddr { get; } = new int[8];
    public int[] AnalogAlarmAddr { get; } = new int[8];

    private static AddressList? _default;
    public static AddressList Default => _default ??= LoadDefault();

    private static AddressList LoadDefault()
    {
        var asm = typeof(AddressList).Assembly;
        // The JSON resource is loaded from the assembly's resources.
        // It was generated from "Mx43 adresslista.xlsx" and committed
        // alongside the parser.
        var resName = "Mx43Sim.Core.Modbus.mx43_address_map.json";
        using var s = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException($"Embedded address map not found: {resName}");
        using var doc = JsonDocument.Parse(s);
        var list = new AddressList();
        Apply(list, doc.RootElement, "measurement", (line, det, v) => list.MeasurementAddr[line, det] = v);
        Apply(list, doc.RootElement, "alarm",       (line, det, v) => list.AlarmAddr[line, det]       = v);
        Apply(list, doc.RootElement, "config",      (line, det, v) => list.ConfigAddr[line, det]      = v);
        return list;
    }

    private static void Apply(AddressList list, JsonElement root, string block,
        Action<int, int, int> sink)
    {
        if (!root.TryGetProperty(block, out var arr)) return;
        Action<int, int, int> analogSink = block switch
        {
            "config"      => (line, _, v) => list.AnalogConfigAddr[line]      = v,
            "alarm"       => (line, _, v) => list.AnalogAlarmAddr[line]       = v,
            "measurement" => (line, _, v) => list.AnalogMeasurementAddr[line] = v,
            _             => (_, _, _)   => { },
        };
        foreach (var lineObj in arr.EnumerateArray())
        {
            int line = lineObj.GetProperty("line").GetInt32() - 1;
            int analog = lineObj.GetProperty("analog").GetInt32();
            analogSink(line, 0, analog);
            int i = 0;
            foreach (var d in lineObj.GetProperty("detectors").EnumerateArray())
            {
                sink(line, i, d.GetInt32());
                i++;
            }
        }
    }

    /// <summary>Build from an explicit JSON file (e.g. for tests or to override defaults).</summary>
    public static AddressList FromJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new AddressList();
        Apply(list, doc.RootElement, "measurement", (line, det, v) => list.MeasurementAddr[line, det] = v);
        Apply(list, doc.RootElement, "alarm",       (line, det, v) => list.AlarmAddr[line, det]       = v);
        Apply(list, doc.RootElement, "config",      (line, det, v) => list.ConfigAddr[line, det]      = v);
        return list;
    }
}
