using Mx43Sim.Core.Domain;

namespace Mx43Sim.Core.Modbus;

/// <summary>
/// Constants and helpers derived from the MX43 Modbus address layout
/// (cahier des charges supervision_MX43_v2 GB).
///
/// Layout (all addresses are holding-register / 16-bit Modbus addresses):
///   1..256       DETECTORS CONFIGURATION request addresses (8 x 32)
///   257..264     Analog channel configuration request addresses
///   2001..2256   Detector measurement      (signed, 1 per detector, line-major)
///   2257..2264   Analog channel measurement
///   2301..2556   Activated alarms          (bit-packed, 1 per detector)
///   2557..2564   Analog channel alarms
///   2600..2603   INFO (CRC32, second counter)
///
/// Reading one DETECTORS CONFIGURATION request address returns a virtual
/// 68-register block for that detector. Within that block:
///   +0   DETECTOR LABEL             (2 x 16 wchar = 16 registers)
///   +16  STATUS                      (1 register)
///   +17  Gas name                    (2 x 20 wchar = 20 registers)
///   +37  Range                       (1 register)
///   +38  Display format              (1 register)
///   +39  Unit                        (2 x 5 wchar = 5 registers)
///   +44  Abbreviated gas name        (2 x 6 wchar = 6 registers)
///   +50  Bar led                     (1)
///   +51  Instant. Alarm 1 threshold  (1)
///   +52  Instant. Alarm 2 threshold  (1)
///   +53  Instant. Alarm 3 threshold  (1)
///   +54  Avrged Alarm 1 threshold    (1)
///   +55  Avrged Alarm 2 threshold    (1)
///   +56  Avrged Alarm 3 threshold    (1)
///   +57  Underscale threshold        (1)
///   +58  Overscale threshold         (1)
///   +59  Fault threshold             (1)
///   +60  Out of range threshold      (1)
///   +61  Averaging time alarm 1      (1)
///   +62  Averaging time alarm 2      (1)
///   +63  Averaging time alarm 3      (1)
///   +64  Hysteresis                  (1)
///   +65  Enabled alarm?              (1, bits)
///   +66  Acknowledgement_alarm?      (1, bits)
///   +67  Rising or falling edge?     (1, bits)
///
/// Each entry is 68 holding-registers (or 34 word pairs, file-endian).
/// </summary>
public static class Mx43AddressMap
{
    public const int Lines = 8;
    public const int DetectorsPerLine = 32;
    public const int AnalogChannels = 8;
    public const int TotalDetectors = Lines * DetectorsPerLine; // 256

    public const int ConfigBlockSize = 68; // registers returned per detector configuration request

    // Configuration registers
    public const int ConfigBase = 1;             // 1..256
    public const int ConfigEnd  = ConfigBase + TotalDetectors - 1;

    public const int AnalogConfigBase = 257;     // 257..264
    public const int AnalogConfigEnd  = AnalogConfigBase + AnalogChannels - 1;

    // Measurement registers (signed)
    public const int MeasurementBase = 2001;     // 2001..2256
    public const int MeasurementEnd  = MeasurementBase + TotalDetectors - 1;

    public const int AnalogMeasurementBase = 2257; // 2257..2264
    public const int AnalogMeasurementEnd  = AnalogMeasurementBase + AnalogChannels - 1;

    // Alarm registers (bitfield)
    public const int AlarmBase = 2301;           // 2301..2556
    public const int AlarmEnd  = AlarmBase + TotalDetectors - 1;

    public const int AnalogAlarmBase = 2557;     // 2557..2564
    public const int AnalogAlarmEnd  = AnalogAlarmBase + AnalogChannels - 1;

    // Info
    public const int InfoCrcMsw      = 2600;
    public const int InfoCrcLsw      = 2601;
    public const int InfoCounterMsw  = 2602;
    public const int InfoCounterLsw  = 2603;

    /// <summary>Map (line, detector) to the configuration request address.</summary>
    public static int ConfigBaseFor(int line, int detector)
        => ConfigBase + (line - 1) * DetectorsPerLine + (detector - 1);

    public static int AnalogConfigBaseFor(int channel) => AnalogConfigBase + (channel - 1);

    public static int ConfigBaseFor(Sensor sensor)
        => sensor.IsAnalog ? AnalogConfigBaseFor(sensor.AnalogChannel) : ConfigBaseFor(sensor.Line, sensor.Detector);

    public static int MeasurementRegFor(int line, int detector)
        => MeasurementBase + (line - 1) * DetectorsPerLine + (detector - 1);

    public static int AnalogMeasurementRegFor(int channel) => AnalogMeasurementBase + (channel - 1);

    public static int MeasurementRegFor(Sensor sensor)
        => sensor.IsAnalog ? AnalogMeasurementRegFor(sensor.AnalogChannel) : MeasurementRegFor(sensor.Line, sensor.Detector);

    public static int AlarmRegFor(int line, int detector)
        => AlarmBase + (line - 1) * DetectorsPerLine + (detector - 1);

    public static int AnalogAlarmRegFor(int channel) => AnalogAlarmBase + (channel - 1);

    public static int AlarmRegFor(Sensor sensor)
        => sensor.IsAnalog ? AnalogAlarmRegFor(sensor.AnalogChannel) : AlarmRegFor(sensor.Line, sensor.Detector);

    /// <summary>Inverse: configuration request address -> (line, detector) or null for analog/other.</summary>
    public static (int Line, int Detector)? DetectorFromConfigBase(int baseReg)
    {
        if (baseReg < ConfigBase || baseReg > ConfigEnd) return null;
        int offset = baseReg - ConfigBase;
        int line = (offset / DetectorsPerLine) + 1;
        int det  = (offset % DetectorsPerLine) + 1;
        return (line, det);
    }
}
