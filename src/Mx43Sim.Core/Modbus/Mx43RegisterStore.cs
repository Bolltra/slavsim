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
        // The Mx43 adresslista.xlsx says there is exactly ONE config
        // register per detector (1..256). The cahier describes a
        // theoretical 68-register block but in practice the slave
        // display reads config as a single 16-bit value (probably a
        // detector index) and looks up labels/gas/thresholds from
        // its own internal table. We therefore only write the
        // measurement and alarm registers, leaving config untouched
        // so the slave can keep whatever it was configured with.
        //
        // If you need to set measurement/alarm live (which is the
        // whole point of the simulator) those work fine via the
        // MeasurementReg / AlarmReg registers below.
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
