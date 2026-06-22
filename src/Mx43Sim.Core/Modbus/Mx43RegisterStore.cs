using System;
using Mx43Sim.Core.Domain;

namespace Mx43Sim.Core.Modbus;

/// <summary>
/// In-memory representation of the MX43 holding-register space. Holds
/// 264 detector slots (256 digital + 8 analog) and exposes them as a single
/// 0..2700 register array indexed by Modbus address (1-based).
///
/// Reads use the Modbus 1-based numbering but the underlying array is
/// 0-based; <see cref="Reg"/> converts.
///
/// Writes are guarded by a simple lock so that the GUI can poke values
/// while the Modbus server is responding to clients.
/// </summary>
public sealed class Mx43RegisterStore
{
    private readonly object _lock = new();
    private readonly short[] _regs;
    private readonly DetectorState[] _detectors;
    private uint _secondCounter;
    private uint _crc32;

    public Mx43RegisterStore()
    {
        // 2704 registers covers all defined addresses with some headroom.
        _regs = new short[2704];
        _detectors = new DetectorState[Mx43AddressMap.TotalDetectors + Mx43AddressMap.AnalogChannels];
        for (int line = 1; line <= Mx43AddressMap.Lines; line++)
        {
            for (int det = 1; det <= Mx43AddressMap.DetectorsPerLine; det++)
            {
                int idx = (line - 1) * Mx43AddressMap.DetectorsPerLine + (det - 1);
                _detectors[idx] = new DetectorState(line, det);
            }
        }
        for (int ch = 1; ch <= Mx43AddressMap.AnalogChannels; ch++)
        {
            int idx = Mx43AddressMap.TotalDetectors + (ch - 1);
            _detectors[idx] = new DetectorState(1, 32 + ch) { Enabled = false };
        }
    }

    public DetectorState[] Detectors => _detectors;

    public uint Crc32
    {
        get { lock (_lock) return _crc32; }
        set { lock (_lock) _crc32 = value; }
    }

    public uint SecondCounter
    {
        get { lock (_lock) return _secondCounter; }
        set { lock (_lock) _secondCounter = value; }
    }

    /// <summary>Convert 1-based Modbus address to 0-based array index, with bounds check.</summary>
    public int Idx(int address) => address - 1;

    public short ReadReg(int address)
    {
        if (address < 1 || address > _regs.Length) return 0;
        lock (_lock) return _regs[address - 1];
    }

    public ushort ReadRegU(int address) => (ushort)ReadReg(address);

    public void WriteReg(int address, short value)
    {
        if (address < 1 || address > _regs.Length) return;
        lock (_lock) _regs[address - 1] = value;
    }

    public void WriteRegU(int address, ushort value) => WriteReg(address, (short)value);

    public short[] ReadRange(int start, int count)
    {
        var result = new short[count];
        lock (_lock)
        {
            if (TryReadConfigRangeLocked(start, result)) return result;

            for (int i = 0; i < count; i++)
            {
                int addr = start + i;
                result[i] = (addr >= 1 && addr <= _regs.Length) ? _regs[addr - 1] : (short)0;
            }
        }
        return result;
    }

    /// <summary>Recompute and write a 32-bit value to two adjacent registers (LSW first). </summary>
    public void Write32(int startReg, uint value)
    {
        WriteRegU(startReg,     (ushort)(value >> 16));   // MSW at first
        WriteRegU(startReg + 1, (ushort)(value & 0xFFFF));
    }

    /// <summary>Re-pack the entire register space from the in-memory detector state.</summary>
    public void Pack(Mx43Config cfg)
    {
        lock (_lock)
        {
            // Reset the entire 2704-register space to 0 first.
            Array.Clear(_regs, 0, _regs.Length);

            foreach (var d in _detectors)
            {
                if (d.Config is null) continue;
                PackOne(d);
            }

            // INFO
            Write32(Mx43AddressMap.InfoCrcMsw, _crc32);
            Write32(Mx43AddressMap.InfoCounterMsw, _secondCounter);
        }
    }

    private void PackOne(DetectorState d)
    {
        // Measurement/alarm registers are linear. Configuration registers
        // are virtual request addresses handled by ReadRange(), otherwise
        // each detector's 68-word text block would spill into the next
        // detector address and produce shifted names.
        int measurementReg;
        int alarmReg;
        if (d.IsAnalog)
        {
            int ch = d.Detector - 32;
            measurementReg = Mx43AddressMap.AnalogMeasurementRegFor(ch);
            alarmReg       = Mx43AddressMap.AnalogAlarmRegFor(ch);
        }
        else
        {
            measurementReg = Mx43AddressMap.MeasurementRegFor(d.Line, d.Detector);
            alarmReg       = Mx43AddressMap.AlarmRegFor(d.Line, d.Detector);
        }
        WriteReg (measurementReg, d.Measurement);
        WriteRegU(alarmReg,       (ushort)d.ActiveAlarms);
    }

    private bool TryReadConfigRangeLocked(int start, short[] result)
    {
        int detectorIndex;
        if (start >= Mx43AddressMap.ConfigBase && start <= Mx43AddressMap.ConfigEnd)
        {
            detectorIndex = start - Mx43AddressMap.ConfigBase;
        }
        else if (start >= Mx43AddressMap.AnalogConfigBase && start <= Mx43AddressMap.AnalogConfigEnd)
        {
            detectorIndex = Mx43AddressMap.TotalDetectors + (start - Mx43AddressMap.AnalogConfigBase);
        }
        else
        {
            return false;
        }

        if (detectorIndex < 0 || detectorIndex >= _detectors.Length) return true;
        var d = _detectors[detectorIndex];
        var c = d.Config;
        if (c is null) return true;

        WriteUtf16(result, 0,  c.Label,        16);
        WriteU16  (result, 16, (ushort)(d.Enabled ? 1 : 0));
        WriteUtf16(result, 17, c.Label,        20);
        WriteU16  (result, 37, (ushort)c.Range);
        WriteU16  (result, 38, (ushort)c.DisplayFormat);
        WriteUtf16(result, 39, c.Unit,         5);
        WriteUtf16(result, 44, c.ShortGasName, 6);
        WriteU16  (result, 50, (ushort)c.BarLed);
        WriteI16  (result, 51, (short)c.Thresholds.Inst1);
        WriteI16  (result, 52, (short)c.Thresholds.Inst2);
        WriteI16  (result, 53, (short)c.Thresholds.Inst3);
        WriteI16  (result, 54, (short)c.Thresholds.Avg1);
        WriteI16  (result, 55, (short)c.Thresholds.Avg2);
        WriteI16  (result, 56, (short)c.Thresholds.Avg3);
        WriteI16  (result, 57, (short)c.Thresholds.Underscale);
        WriteI16  (result, 58, (short)c.Thresholds.Overscale);
        WriteI16  (result, 59, (short)c.Thresholds.Fault);
        WriteI16  (result, 60, (short)c.Thresholds.OutOfRange);
        WriteU16  (result, 61, (ushort)c.AveragingTime1);
        WriteU16  (result, 62, (ushort)c.AveragingTime2);
        WriteU16  (result, 63, (ushort)c.AveragingTime3);
        WriteU16  (result, 64, (ushort)c.Hysteresis);
        WriteU16  (result, 65, (ushort)c.EnableFlags);
        WriteU16  (result, 66, (ushort)c.AckFlags);
        WriteU16  (result, 67, (ushort)c.EdgeFlags);
        return true;
    }

    private static void WriteU16(short[] regs, int offset, ushort value)
    {
        if ((uint)offset < (uint)regs.Length) regs[offset] = (short)value;
    }

    private static void WriteI16(short[] regs, int offset, short value)
    {
        if ((uint)offset < (uint)regs.Length) regs[offset] = value;
    }

    private static void WriteUtf16(short[] regs, int offset, string text, int registerCount)
    {
        if (registerCount <= 0 || offset >= regs.Length) return;
        int maxChars = Math.Min(text?.Length ?? 0, registerCount - 1);
        for (int i = 0; i < maxChars && offset + i < regs.Length; i++)
        {
            char c = text![i];
            regs[offset + i] = (short)(char.IsControl(c) ? '?' : c);
        }
    }

}
