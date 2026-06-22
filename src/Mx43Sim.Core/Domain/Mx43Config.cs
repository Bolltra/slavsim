using System;
using System.Collections.Generic;

namespace Mx43Sim.Core.Domain;

/// <summary>
/// Top-level model for a parsed MX43 .cfg file. Holds every entity declared
/// in the configuration along with the metadata required to bind them to
/// Modbus registers.
/// </summary>
public sealed class Mx43Config
{
    public string ProjectName { get; set; } = "";
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public string AccessLevel { get; set; } = "";
    public int ControllerType { get; set; } = 43;
    public List<Zone> Zones { get; } = new();
    /// <summary>Internal extension modules (8-channel relay cards etc.).</summary>
    public List<RelayModule> Modules { get; } = new();
    /// <summary>On-board fixed relays (5 + Strobe + Horn). Not exposed via Modbus.</summary>
    public List<OnBoardRelay> OnBoardRelays { get; } = new();
    public List<Sensor> Sensors { get; } = new();
}

public sealed class Zone
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// An internal extension module attached to the MX43. The 4-byte
/// header of every relay record carries the module id (e.g. 0x0011
/// for L1M17, 0x00E1 for an unnamed logic-output module). The display
/// name is either "L1Mxx" (auto-generated) or a user-chosen label.
/// </summary>
public sealed class RelayModule
{
    public int Id { get; set; }
    public string Tag { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Channels 0..7 (bit mask of the relay header).</summary>
    public List<ModuleChannel> Channels { get; } = new();
}

public sealed class ModuleChannel
{
    public int Bit { get; set; }      // 0..7 (header byte 2 is 1<<Bit)
    public string Name { get; set; } = "";
}

/// <summary>The 7 fixed on-board relays (relay1..5, Strobe, Horn).</summary>
public sealed class OnBoardRelay
{
    public int Bit { get; set; }      // 0..6 corresponding to header bits 0x01..0x40
    public string Name { get; set; } = "";
    public OnBoardRelayKind Kind { get; set; }
}

public enum OnBoardRelayKind { Generic, Strobe, Horn }

/// <summary>
/// A single detector / sensor declared in the .cfg. The position in the file
/// determines the Line (1..8) and Detector (1..32) which in turn map to a
/// pair of Modbus addresses (configuration + measurement).
/// </summary>
public sealed class Sensor
{
    /// <summary>Modbus line number 1..8.</summary>
    public int Line { get; set; }
    /// <summary>Detector number within line 1..32.</summary>
    public int Detector { get; set; }
    /// <summary>Analog channel number 1..8 for direct 4-20 mA inputs, or 0 for digital line detectors.</summary>
    public int AnalogChannel { get; set; }
    public bool IsAnalog => AnalogChannel is >= 1 and <= 8;
    /// <summary>0-based index across the full 256+8 range (0..255 for digital, 256..263 for analog).</summary>
    public int Index => IsAnalog ? 256 + (AnalogChannel - 1) : (Line - 1) * 32 + (Detector - 1);

    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public string ShortGasName { get; set; } = "";
    public int Range { get; set; }
    public int DisplayFormat { get; set; }
    public int BarLed { get; set; }

    public AlarmThresholds Thresholds { get; set; } = new();
    public AlarmEnable EnableFlags { get; set; } = new();
    public AlarmAcknowledge AckFlags { get; set; } = new();
    public AlarmEdge EdgeFlags { get; set; } = new();

    public int Hysteresis { get; set; }
    public int AveragingTime1 { get; set; }
    public int AveragingTime2 { get; set; }
    public int AveragingTime3 { get; set; }

    public int ZoneIndex { get; set; }
}

public sealed class AlarmThresholds
{
    public int Inst1 { get; set; }
    public int Inst2 { get; set; }
    public int Inst3 { get; set; }
    public int Avg1 { get; set; }
    public int Avg2 { get; set; }
    public int Avg3 { get; set; }
    public int Underscale { get; set; }
    public int Overscale { get; set; }
    public int Fault { get; set; }
    public int OutOfRange { get; set; }
}

[Flags]
public enum AlarmEnable : ushort
{
    None     = 0,
    Inst1    = 1 << 0,
    Inst2    = 1 << 1,
    Inst3    = 1 << 2,
    Avg1     = 1 << 3,
    Avg2     = 1 << 4,
    Avg3     = 1 << 5,
}

[Flags]
public enum AlarmAcknowledge : ushort
{
    None        = 0,
    Inst1Manual = 1 << 0,
    Inst2Manual = 1 << 1,
    Inst3Manual = 1 << 2,
    /// <summary>Bits 3-4 are reserved 'put 0/1 mandatory' in cahier, ignored.</summary>
    NonAmbiguity = 1 << 7,
}

[Flags]
public enum AlarmEdge : ushort
{
    None  = 0,
    Inst1 = 1 << 0,
    Inst2 = 1 << 1,
    Inst3 = 1 << 2,
    Avg1  = 1 << 3,
    Avg2  = 1 << 4,
    Avg3  = 1 << 5,
    /// <summary>1 = rising, 0 = falling.</summary>
}
