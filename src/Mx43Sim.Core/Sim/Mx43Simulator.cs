using System;
using System.Collections.Generic;
using System.Linq;
using Mx43Sim.Core.Cfg;
using Mx43Sim.Core.Domain;
using Mx43Sim.Core.Modbus;

namespace Mx43Sim.Core.Sim;

/// <summary>
/// Glue between a parsed Mx43Config, the Modbus register store and the
/// GUI/script layer. Owns the list of 264 detector slots, lets the
/// caller set a simulated measurement, and automatically derives the
/// alarm bits from the configured thresholds.
///
/// The simulation rule is: a measurement crossing an instantaneous
/// threshold latches the corresponding bit. Crossing an underscale or
/// overscale threshold latches those bits. Crossing the out-of-range
/// threshold latches the Fault + OutOfRange bits (MX43 standard
/// behavior). The user can override this by calling SetAlarm directly
/// (e.g. for "force alarm" buttons in the UI).
/// </summary>
public sealed class Mx43Simulator
{
    private readonly Mx43RegisterStore _store;
    private Mx43Config? _config;
    private AddressList? _addresses;

    public Mx43Config? Config => _config;
    public AddressList? Addresses => _addresses;
    public Mx43RegisterStore Store => _store;

    public Mx43Simulator(Mx43RegisterStore store) { _store = store; }

    public void Load(Mx43Config config, AddressList? addresses = null)
    {
        _config = config;
        _addresses = addresses;
        foreach (var d in _store.Detectors) d.Config = null;
        foreach (var s in config.Sensors)
        {
            int idx = (s.Line - 1) * 32 + (s.Detector - 1);
            if (idx >= 0 && idx < _store.Detectors.Length) _store.Detectors[idx].Config = s;
        }
        // Start with a clean measurement at zero and recompute alarms
        // for whatever thresholds the .cfg says.
        foreach (var d in _store.Detectors)
        {
            d.Measurement = 0;
            d.ActiveAlarms = AlarmBits.None;
        }
        Repack();
    }

    public void Repack()
    {
        if (_config is null) return;
        _store.Pack(_config);
    }

    /// <summary>
    /// Set the live measurement for one detector. Recomputes the
    /// active-alarm bits from the configured thresholds and writes both
    /// measurement + alarm registers so the next Modbus read returns
    /// the new values.
    /// </summary>
    public void SetMeasurement(int line, int det, short value)
    {
        int idx = (line - 1) * 32 + (det - 1);
        if (idx < 0 || idx >= _store.Detectors.Length) return;
        var d = _store.Detectors[idx];
        d.Measurement = value;
        d.ActiveAlarms = ComputeAlarms(d);
        int mReg = Mx43AddressMap.MeasurementRegFor(line, det);
        int aReg = Mx43AddressMap.AlarmRegFor(line, det);
        _store.WriteReg(mReg, value);
        _store.WriteRegU(aReg, (ushort)d.ActiveAlarms);
    }

    /// <summary>Override the alarm bits for one detector (e.g. "force alarm").</summary>
    public void SetAlarm(int line, int det, AlarmBits bits)
    {
        int idx = (line - 1) * 32 + (det - 1);
        if (idx < 0 || idx >= _store.Detectors.Length) return;
        var d = _store.Detectors[idx];
        d.ActiveAlarms = bits;
        int reg = Mx43AddressMap.AlarmRegFor(line, det);
        _store.WriteRegU(reg, (ushort)bits);
    }

    public short GetMeasurement(int line, int det)
    {
        int idx = (line - 1) * 32 + (det - 1);
        return idx < 0 || idx >= _store.Detectors.Length ? (short)0 : _store.Detectors[idx].Measurement;
    }

    public AlarmBits GetAlarm(int line, int det)
    {
        int idx = (line - 1) * 32 + (det - 1);
        return idx < 0 || idx >= _store.Detectors.Length ? AlarmBits.None : _store.Detectors[idx].ActiveAlarms;
    }

    /// <summary>
    /// Derive the active alarm bits from the configured thresholds.
    /// MX43 semantics:
    ///   - Alarm 1/2/3 latch when |value| crosses the instantaneous or averaged threshold
    ///   - Underscale / Overscale latch when value reaches or passes the scale threshold
    ///   - OutOfRange latches when value reaches or exceeds OutOfRange threshold
    ///   - Fault latches together with OutOfRange
    /// Only the bits whose corresponding Enable bit is set are returned.
    /// </summary>
    public static AlarmBits ComputeAlarms(DetectorState d)
    {
        if (d.Config is null) return AlarmBits.None;
        var c = d.Config;
        int v = d.Measurement;
        AlarmBits b = AlarmBits.None;

        if (c.Thresholds.Inst1 != 0 && System.Math.Abs(v) >= c.Thresholds.Inst1) b |= AlarmBits.Inst1;
        if (c.Thresholds.Inst2 != 0 && System.Math.Abs(v) >= c.Thresholds.Inst2) b |= AlarmBits.Inst2;
        if (c.Thresholds.Inst3 != 0 && System.Math.Abs(v) >= c.Thresholds.Inst3) b |= AlarmBits.Inst3;
        if (c.Thresholds.Avg1  != 0 && System.Math.Abs(v) >= c.Thresholds.Avg1)  b |= AlarmBits.Inst1;
        if (c.Thresholds.Avg2  != 0 && System.Math.Abs(v) >= c.Thresholds.Avg2)  b |= AlarmBits.Inst2;
        if (c.Thresholds.Avg3  != 0 && System.Math.Abs(v) >= c.Thresholds.Avg3)  b |= AlarmBits.Inst3;

        if (c.Thresholds.Underscale != 0 && v <= c.Thresholds.Underscale) b |= AlarmBits.Underscale;
        if (c.Thresholds.Overscale  != 0 && v >= c.Thresholds.Overscale)  b |= AlarmBits.Overscale;
        if (c.Thresholds.OutOfRange != 0 && System.Math.Abs(v) >= c.Thresholds.OutOfRange)
        {
            b |= AlarmBits.OutOfRange;
            b |= AlarmBits.Fault;
        }

        // Mask by enable flags. The active-alarm register only has
        // Alarm1/2/3 bits; averaged alarms use the same output bits as
        // their instantaneous counterparts.
        var allowed = c.EnableFlags;
        AlarmBits enabled = AlarmBits.None;
        if ((allowed & (AlarmEnable.Inst1 | AlarmEnable.Avg1)) != 0) enabled |= AlarmBits.Inst1;
        if ((allowed & (AlarmEnable.Inst2 | AlarmEnable.Avg2)) != 0) enabled |= AlarmBits.Inst2;
        if ((allowed & (AlarmEnable.Inst3 | AlarmEnable.Avg3)) != 0) enabled |= AlarmBits.Inst3;
        b = (b & enabled) | (b & (AlarmBits.Underscale | AlarmBits.Overscale | AlarmBits.Fault | AlarmBits.OutOfRange));
        return b;
    }
}
