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
        int baseReg;
        int measurementReg;
        int alarmReg;
        if (d.IsAnalog)
        {
            int ch = d.Detector - 32;
            baseReg = Mx43AddressMap.AnalogConfigBaseFor(ch);
            measurementReg = Mx43AddressMap.AnalogMeasurementRegFor(ch);
            alarmReg = Mx43AddressMap.AnalogAlarmRegFor(ch);
        }
        else
        {
            baseReg = Mx43AddressMap.ConfigBaseFor(d.Line, d.Detector);
            measurementReg = Mx43AddressMap.MeasurementRegFor(d.Line, d.Detector);
            alarmReg = Mx43AddressMap.AlarmRegFor(d.Line, d.Detector);
        }

        var c = d.Config;
        // Label (2x16 wchar) -> 16 registers
        WriteUtf16(baseReg + 0, c.Label, 16);
        // STATUS
        WriteRegU(baseReg + 16, (ushort)(d.Enabled ? 1 : 0));
        // Gas name (2x20 wchar) -> 20 registers
        WriteUtf16(baseReg + 17, c.Label, 20);   // gas name == label in this .cfg
        // Range
        WriteRegU(baseReg + 37, (ushort)c.Range);
        // Display format
        WriteRegU(baseReg + 38, (ushort)c.DisplayFormat);
        // Unit (2x5 wchar) -> 5 registers
        WriteUtf16(baseReg + 39, c.Unit, 5);
        // Abbreviated gas name (2x6 wchar) -> 6 registers
        WriteUtf16(baseReg + 44, c.ShortGasName, 6);
        // Bar led
        WriteRegU(baseReg + 50, (ushort)c.BarLed);
        // Thresholds
        WriteReg(baseReg + 51, (short)c.Thresholds.Inst1);
        WriteReg(baseReg + 52, (short)c.Thresholds.Inst2);
        WriteReg(baseReg + 53, (short)c.Thresholds.Inst3);
        WriteReg(baseReg + 54, (short)c.Thresholds.Avg1);
        WriteReg(baseReg + 55, (short)c.Thresholds.Avg2);
        WriteReg(baseReg + 56, (short)c.Thresholds.Avg3);
        WriteReg(baseReg + 57, (short)c.Thresholds.Underscale);
        WriteReg(baseReg + 58, (short)c.Thresholds.Overscale);
        WriteReg(baseReg + 59, (short)c.Thresholds.Fault);
        WriteReg(baseReg + 60, (short)c.Thresholds.OutOfRange);
        WriteRegU(baseReg + 61, (ushort)c.AveragingTime1);
        WriteRegU(baseReg + 62, (ushort)c.AveragingTime2);
        WriteRegU(baseReg + 63, (ushort)c.AveragingTime3);
        WriteRegU(baseReg + 64, (ushort)c.Hysteresis);
        WriteRegU(baseReg + 65, (ushort)c.EnableFlags);
        WriteRegU(baseReg + 66, (ushort)c.AckFlags);
        WriteRegU(baseReg + 67, (ushort)c.EdgeFlags);

        // Live values
        WriteReg(measurementReg, d.Measurement);
        WriteRegU(alarmReg, (ushort)d.ActiveAlarms);
    }

    private void WriteUtf16(int startReg, string text, int registerCount)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Truncate or pad with 0 to fit registerCount words.
        int maxChars = registerCount;
        for (int i = 0; i < maxChars; i++)
        {
            int addr = startReg + i;
            ushort v = 0;
            if (i < text.Length)
            {
                char c = text[i];
                v = c < 0x10000 ? (ushort)c : (ushort)'?';
            }
            WriteRegU(addr, v);
        }
    }
}
