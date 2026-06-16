using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mx43Sim.Core.Domain;

namespace Mx43Sim.Core.Cfg;

/// <summary>
/// Parser for the Teledyne/Oldham MX43 binary configuration file. The file
/// layout is undocumented but was reverse-engineered from a real export
/// ("Projekt Gotland elnät G15.cfg"). The format is fixed-record, big enough
/// to hold 8 lines x 32 detectors plus 8 analog channels plus on-board
/// relays and the "Strobe/Horn" outputs.
///
/// File layout (all offsets hex):
///   0000..07FF   Header (11 block headers, 0xC0 bytes each)
///   8000..8100   System info (project name, screen size, access)
///   8400..8500   Display bounds (8191 x 2047)
///   8500..8700   Zone list (12 entries: name + 0x2C-byte slot)
///   8700..8A00   Modbus devices (L1M17, L1M18, ...)
///   8A00..9A00   Relays & outputs
///   9A00..A000   Zone index for sensors
///   A000..AFE0   Sensor records (8 x 0xC5 = 197 bytes each in our sample)
///   AFE0..B000   Footer (counters + signature)
///
/// Each sensor record (197 bytes, "0xC5") contains TWO display blocks (a
/// primary and a secondary view) and the live runtime data (measurement,
/// alarm bits).
/// </summary>
public sealed class Mx43CfgParser
{
    private readonly byte[] _data;

    public Mx43CfgParser(byte[] data) { _data = data; }
    public Mx43CfgParser(string path) : this(File.ReadAllBytes(path)) { }

    public Mx43Config Parse()
    {
        var cfg = new Mx43Config();
        ParseHeader(cfg);
        ParseSystemInfo(cfg);
        ParseZones(cfg);
        ParseModulesAndRelays(cfg);
        ParseSensors(cfg);
        return cfg;
    }

    // -------- Header / system info --------

    private void ParseHeader(Mx43Config cfg)
    {
        // The 0x0000..0x07FF area contains 11 block headers. We don't yet
        // know what each block represents; the only stable signal is that
        // every block has a 0x01 at +0x60 and a 0x02 at +0x80. Skip.
    }

    private void ParseSystemInfo(Mx43Config cfg)
    {
        cfg.ProjectName  = ReadUtf16(0x8000, 16).TrimEnd('\0', ' ');
        cfg.ScreenWidth  = int.TryParse(ReadUtf16(0x8020, 16), out var w) ? w : 1000;
        cfg.ScreenHeight = int.TryParse(ReadUtf16(0x8040, 16), out var h) ? h : 1000;
        cfg.AccessLevel  = ReadUtf16(0x8060, 16).TrimEnd('\0', ' ');
    }

    // -------- Zones --------

    private void ParseZones(Mx43Config cfg)
    {
        // Zone list lives at 0x8500..0x8680. Each entry is 0x24 = 36 bytes:
        //   4 bytes header (zone-index u16 + 0F 00 ?)
        //   32 bytes UTF-16 name (16 wchar)
        int off = 0x8500;
        int zoneIdx = 0;
        while (off + 0x24 <= 0x8680)
        {
            string name = ReadUtf16(off + 4, 16).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(name)) { off += 0x24; continue; }
            // Skip non-zone entries (modbus device tags "L1Mxx" live just after).
            if (name.StartsWith("L1M", StringComparison.Ordinal)) { off += 0x24; continue; }
            cfg.Zones.Add(new Zone { Index = zoneIdx, Name = name });
            zoneIdx++;
            off += 0x24;
            if (zoneIdx > 32) break;
        }
    }

    // -------- Modules & Relays --------

    private const int RelayHeaderOnBoard = 0x0100;

    private void ParseModulesAndRelays(Mx43Config cfg)
    {
        // 1. Optional module display names. 0x868C..0x8700 (stride 0x24) may
        //    contain a list of "L1M17" / "L1M18" tags. The presence of this
        //    block determines whether the module is shown to the operator
        //    by its formal name. If empty/absent, modules are still real —
        //    they are announced by the relay headers.
        var nameById = new Dictionary<int, string>();
        for (int off = 0x868C; off + 0x24 <= 0x8700; off += 0x24)
        {
            string tag = ReadUtf16(off + 4, 16).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (!tag.StartsWith("L1M", StringComparison.Ordinal) || tag.Length < 5) continue;
            if (int.TryParse(tag.AsSpan(3), out var id)) nameById[id] = tag;
        }

        // 2. Walk every relay record. The 4-byte header at the start of
        //    each record encodes the source:
        //      header = (0x0100, bitMask)  -> on-board fixed relay
        //      header = (moduleId, bitMask) -> channel on an extension module
        //    bitMask uses bits 0..7 to identify the channel within the source.
        var modulesById = new Dictionary<int, RelayModule>();
        for (int off = 0x8A00; off + 0x2C <= 0x8E00; off += 0x2C)
        {
            ushort moduleId = ReadUInt16(off + 0);
            ushort bitMask  = ReadUInt16(off + 2);
            string name     = ReadUtf16(off + 0x0C, 16).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(name)) continue;

            int bit = BitPosition(bitMask);
            if (bit < 0) continue;   // unknown bitmask, skip

            if (moduleId == RelayHeaderOnBoard)
            {
                var kind = OnBoardRelayKind.Generic;
                if (name.Contains("Strobe", StringComparison.OrdinalIgnoreCase)) kind = OnBoardRelayKind.Strobe;
                else if (name.Contains("Horn", StringComparison.OrdinalIgnoreCase)) kind = OnBoardRelayKind.Horn;
                cfg.OnBoardRelays.Add(new OnBoardRelay { Bit = bit, Name = name, Kind = kind });
            }
            else
            {
                if (!modulesById.TryGetValue(moduleId, out var mod))
                {
                    nameById.TryGetValue(moduleId, out var tag);
                    if (string.IsNullOrEmpty(tag)) tag = $"Module{moduleId}";
                    mod = new RelayModule
                    {
                        Id = moduleId,
                        Tag = tag,
                        DisplayName = name,    // first channel's name as default
                    };
                    modulesById[moduleId] = mod;
                    cfg.Modules.Add(mod);
                }
                mod.Channels.Add(new ModuleChannel { Bit = bit, Name = name });
            }
        }
    }

    /// <summary>Returns the position (0..7) of the lowest set bit, or -1 if none.</summary>
    private static int BitPosition(ushort mask)
    {
        for (int i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) return i;
        return -1;
    }

    // -------- Sensors --------

    private const int SensorRecordSize = 0x74;
    private const int SensorRecordBase = 0xA000;

    private void ParseSensors(Mx43Config cfg)
    {
        int sensorIdx = 0;
        for (int off = SensorRecordBase; off + SensorRecordSize <= 0xAFE0; off += SensorRecordSize)
        {
            int line = (sensorIdx / 32) + 1;
            int det  = (sensorIdx % 32) + 1;
            if (line > 8) break;

            var label = ReadUtf16(off + 0x00, 16).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(label)) { sensorIdx++; continue; }

            var s = new Sensor
            {
                Line = line,
                Detector = det,
                Label = label,
                Range = ReadUInt16(off + 0x28),
                DisplayFormat = ReadUInt16(off + 0x2A),
                Unit = ReadUtf16(off + 0x2C, 5).TrimEnd('\0', ' '),
                ShortGasName = ReadUtf16(off + 0x36, 4).TrimEnd('\0', ' '),
            };

            // Alarm thresholds (16-bit signed, scaled by the same factor as
            // the live measurement). The layout is consistent across gas
            // families within a .cfg — verified against H2-ppm, H2-LEL,
            // CO2 %VOL, CH4 %LEL, C2H6 %LEL, O2 %VOL, CO ppm.
            //
            //   +0x44  Instant. Alarm 1 threshold
            //   +0x46  Instant. Alarm 2 threshold
            //   +0x48  Instant. Alarm 3 threshold   (0 = disabled)
            //   +0x4A  Average  Alarm 1 threshold   (often 0)
            //   +0x4C  Average  Alarm 2 threshold   (often 0)
            //   +0x4E  Average  Alarm 3 threshold   (often 0)
            //   +0x50  Underscale threshold         (e.g. -5 for CH4, -15 for O2)
            //   +0x52  Overscale  threshold         (e.g. 100 for CH4, 265 for O2)
            //   +0x54  Fault      threshold
            //   +0x56  Out-of-range threshold
            //   +0x58  Averaging time alarm 1 (min)
            //   +0x5A  Averaging time alarm 2 (min)
            //   +0x5C  Averaging time alarm 3 (min)
            s.Thresholds.Inst1       = ReadInt16(off + 0x44);
            s.Thresholds.Inst2       = ReadInt16(off + 0x46);
            s.Thresholds.Inst3       = ReadInt16(off + 0x48);
            s.Thresholds.Avg1        = ReadInt16(off + 0x4A);
            s.Thresholds.Avg2        = ReadInt16(off + 0x4C);
            s.Thresholds.Avg3        = ReadInt16(off + 0x4E);
            s.Thresholds.Underscale  = ReadInt16(off + 0x50);
            s.Thresholds.Overscale   = ReadInt16(off + 0x52);
            s.Thresholds.Fault       = ReadInt16(off + 0x54);
            s.Thresholds.OutOfRange  = ReadInt16(off + 0x56);
            s.AveragingTime1         = ReadUInt16(off + 0x58);
            s.AveragingTime2         = ReadUInt16(off + 0x5A);
            s.AveragingTime3         = ReadUInt16(off + 0x5C);

            // Alarm enable/ack/edge bit fields. The default enable mask
            // for a configured channel is 0x07 (Inst1+Inst2+Inst3). The
            // second word at +0x64 is sensor-specific (e.g. 0x16 for
            // 0x07+averaging bits). We use the first word as EnableFlags.
            s.EnableFlags = (AlarmEnable)ReadUInt16(off + 0x62);
            s.AckFlags    = (AlarmAcknowledge)ReadUInt16(off + 0x64);
            s.EdgeFlags   = (AlarmEdge)ReadUInt16(off + 0x66);

            cfg.Sensors.Add(s);
            sensorIdx++;
        }
    }

    // -------- Low-level helpers --------

    private ushort ReadUInt16(int offset)
    {
        if (offset + 2 > _data.Length) return 0;
        return (ushort)(_data[offset] | (_data[offset + 1] << 8));
    }

    private short ReadInt16(int offset) => (short)ReadUInt16(offset);

    private string ReadUtf16(int offset, int registerCount)
    {
        if (offset + registerCount * 2 > _data.Length) return "";
        var sb = new StringBuilder(registerCount);
        for (int i = 0; i < registerCount; i++)
        {
            int o = offset + i * 2;
            ushort v = (ushort)(_data[o] | (_data[o + 1] << 8));
            if (v == 0) break;
            sb.Append(char.IsControl((char)v) ? '?' : (char)v);
        }
        return sb.ToString();
    }
}
