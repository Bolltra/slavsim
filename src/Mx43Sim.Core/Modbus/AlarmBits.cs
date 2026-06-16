using System;
using Mx43Sim.Core.Domain;

namespace Mx43Sim.Core.Modbus;

/// <summary>
/// Per-alarm bit positions inside the alarm word at address 2301+offset
/// (or 2557..2564 for analog). The cahier says the word contains
/// 'bit0..bit5' for the 6 alarm types (rising/falling) but the convention
/// from the G15 .cfg has values 7, 22, 63 (0x07, 0x16, 0x3F) which look
/// like the bits for *enabled* alarm 1/2/3. We expose both representations.
/// </summary>
[Flags]
public enum AlarmBits : ushort
{
    None        = 0,
    Inst1       = 1 << 0,
    Inst2       = 1 << 1,
    Inst3       = 1 << 2,
    Avg1        = 1 << 3,
    Avg2        = 1 << 4,
    Avg3        = 1 << 5,
    /// <summary>Bit 6 used in some firmware revisions - underscale.</summary>
    Underscale  = 1 << 6,
    /// <summary>Bit 7 - overscale.</summary>
    Overscale   = 1 << 7,
    /// <summary>Bit 8 - fault.</summary>
    Fault       = 1 << 8,
    /// <summary>Bit 9 - out of range.</summary>
    OutOfRange  = 1 << 9,
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
