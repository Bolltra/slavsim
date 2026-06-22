using System;
using Mx43Sim.Core.Domain;

namespace Mx43Sim.Core.Modbus;

/// <summary>
/// Per-alarm bit positions inside the alarm word at address 2301+offset
/// (or 2557..2564 for analog). The MX43 cahier defines three alarm bits
/// followed by scale/fault bits; averaged alarms are reported through the
/// same Alarm 1/2/3 bits as instantaneous alarms.
/// </summary>
[Flags]
public enum AlarmBits : ushort
{
    None        = 0,
    Inst1       = 1 << 0,
    Inst2       = 1 << 1,
    Inst3       = 1 << 2,
    /// <summary>Bit 3 - underscale.</summary>
    Underscale  = 1 << 3,
    /// <summary>Bit 4 - overscale.</summary>
    Overscale   = 1 << 4,
    /// <summary>Bit 5 - fault.</summary>
    Fault       = 1 << 5,
    /// <summary>Bit 6 - out of range.</summary>
    OutOfRange  = 1 << 6,
    /// <summary>Bit 7 - non-ambiguity reading.</summary>
    NonAmbiguityReading = 1 << 7,
}

/// <summary>
/// Runtime value for a single detector slot: 0..255 digital + 0..7 analog.
/// Holds both the configuration (loaded from .cfg) and the live simulation
/// state (current measurement + currently-active alarm bits).
/// </summary>
public sealed class DetectorState
{
    public int Line { get; }
    public int Detector { get; }
    public int Index { get; }
    public bool IsAnalog => Detector > 32;

    public Sensor? Config { get; set; }

    /// <summary>Current simulated measurement (signed 16-bit, scaled by DisplayFormat).</summary>
    public short Measurement { get; set; }

    /// <summary>Currently latched alarm bits.</summary>
    public AlarmBits ActiveAlarms { get; set; }

    /// <summary>Bit 0 of the STATUS word — ON/OFF.</summary>
    public bool Enabled { get; set; } = true;

    public DetectorState(int line, int detector)
    {
        Line = line;
        Detector = detector;
        Index = (line - 1) * 32 + (detector - 1);
    }
}
