using System;
using System.Collections.Generic;
using System.Linq;
using Mx43Sim.Core.Domain;
using Mx43Sim.Core.Modbus;

namespace Mx43Sim.WeintekGenerator;

internal static class WeintekLayout
{
    internal const int LwStride = 100;
    internal const int LwNameOffset = 0;
    internal const int LwStatusOffset = 16;
    internal const int LwFullGasOffset = 17;
    internal const int LwRangeOffset = 37;
    internal const int LwDisplayFormatOffset = 38;
    internal const int LwUnitOffset = 39;
    internal const int LwShortGasOffset = 44;
    internal const int LwAlarm1Offset = 51;
    internal const int LwAlarm2Offset = 52;
    internal const int LwAlarm3Offset = 53;
    internal const int LwMeasurementOffset = 70;
    internal const int LwAlarmBitsOffset = 71;
    internal const int LwScaledIntegerOffset = 72;
    internal const int LwScaleDivisorOffset = 73;
    internal const int LwAlarmLevelCountOffset = 74;
    internal const int LwAlarmSeverityOffset = 75;
}

internal sealed record DetectorPlan(
    int ScreenNo,
    int Line,
    int Detector,
    int AnalogChannel,
    string Label,
    string Unit,
    string ShortGasName,
    int RangeRaw,
    int DisplayFormat,
    int ScaleDivisor,
    int AlarmLevelCount,
    int HighestConfiguredAlarmBit,
    int ConfigRegister,
    int MeasurementRegister,
    int AlarmRegister,
    int LwBase,
    int LwName,
    int LwStatus,
    int LwFullGas,
    int LwRange,
    int LwDisplayFormat,
    int LwUnit,
    int LwShortGas,
    int LwAlarm1,
    int LwAlarm2,
    int LwAlarm3,
    int LwMeasurement,
    int LwAlarmBits,
    int LwScaledInteger,
    int LwScaleDivisor,
    int LwAlarmLevelCount,
    int LwAlarmSeverity);

internal static class DetectorPlanner
{
    internal static DetectorPlan[] Create(Mx43Config config) => config.Sensors
        .OrderBy(sensor => sensor.Index)
        .Select((sensor, index) => Create(sensor, index + 1))
        .ToArray();

    private static DetectorPlan Create(Sensor sensor, int screenNo)
    {
        int lwBase = screenNo * WeintekLayout.LwStride;
        int displayFormat = Math.Clamp(sensor.DisplayFormat, 0, 4);
        var configuredLevels = ConfiguredAlarmLevels(sensor).ToArray();
        int highestConfiguredAlarmBit = configuredLevels.DefaultIfEmpty(0).Max();
        return new DetectorPlan(
            screenNo,
            sensor.Line,
            sensor.Detector,
            sensor.AnalogChannel,
            sensor.Label,
            sensor.Unit,
            sensor.ShortGasName,
            sensor.Range,
            displayFormat,
            (int)Math.Pow(10, displayFormat),
            configuredLevels.Length,
            highestConfiguredAlarmBit,
            Mx43AddressMap.ConfigBaseFor(sensor),
            Mx43AddressMap.MeasurementRegFor(sensor),
            Mx43AddressMap.AlarmRegFor(sensor),
            lwBase,
            lwBase + WeintekLayout.LwNameOffset,
            lwBase + WeintekLayout.LwStatusOffset,
            lwBase + WeintekLayout.LwFullGasOffset,
            lwBase + WeintekLayout.LwRangeOffset,
            lwBase + WeintekLayout.LwDisplayFormatOffset,
            lwBase + WeintekLayout.LwUnitOffset,
            lwBase + WeintekLayout.LwShortGasOffset,
            lwBase + WeintekLayout.LwAlarm1Offset,
            lwBase + WeintekLayout.LwAlarm2Offset,
            lwBase + WeintekLayout.LwAlarm3Offset,
            lwBase + WeintekLayout.LwMeasurementOffset,
            lwBase + WeintekLayout.LwAlarmBitsOffset,
            lwBase + WeintekLayout.LwScaledIntegerOffset,
            lwBase + WeintekLayout.LwScaleDivisorOffset,
            lwBase + WeintekLayout.LwAlarmLevelCountOffset,
            lwBase + WeintekLayout.LwAlarmSeverityOffset);
    }

    private static IEnumerable<int> ConfiguredAlarmLevels(Sensor sensor)
    {
        if (IsConfigured(sensor.Thresholds.Inst1, sensor.EnableFlags, AlarmEnable.Inst1) ||
            IsConfigured(sensor.Thresholds.Avg1, sensor.EnableFlags, AlarmEnable.Avg1)) yield return 1;
        if (IsConfigured(sensor.Thresholds.Inst2, sensor.EnableFlags, AlarmEnable.Inst2) ||
            IsConfigured(sensor.Thresholds.Avg2, sensor.EnableFlags, AlarmEnable.Avg2)) yield return 2;
        if (IsConfigured(sensor.Thresholds.Inst3, sensor.EnableFlags, AlarmEnable.Inst3) ||
            IsConfigured(sensor.Thresholds.Avg3, sensor.EnableFlags, AlarmEnable.Avg3)) yield return 3;
    }

    private static bool IsConfigured(int threshold, AlarmEnable enabled, AlarmEnable flag)
        => threshold != 0 && (enabled & flag) != 0;
}
