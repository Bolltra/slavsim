#!/usr/bin/env python3
"""Diagnose a single MX43 .cfg file in detail.

Dumps every sensor record (and surrounding config blocks) as
human-readable text so we can compare against what the COM43 programming
tool shows, and what the slave display expects on the Modbus wire.
"""
import argparse
import os
import struct
import sys
from pathlib import Path

def read_utf16(data, off, max_chars=64):
    """Read a UTF-16LE string. Returns (string, offset_of_next_field, num_wchars_read)."""
    chars = []
    for i in range(max_chars):
        if off + i*2 + 1 >= len(data): break
        c = struct.unpack('<H', data[off + i*2:off + i*2 + 2])[0]
        if c == 0:
            return ''.join(chars), off + (i+1)*2, i+1  # +1 to skip the null wchar
        if 32 <= c < 127:
            chars.append(chr(c))
        else:
            chars.append(f'\\u{c:04x}')
    return ''.join(chars), off + max_chars*2, max_chars

def hexdump(data, off, length):
    return ' '.join(f'{data[off+i]:02x}' for i in range(length) if off+i < len(data))

def dump_sensor_record(data, rec_index, off):
    """Print every meaningful field of one sensor record."""
    print(f"\n  ── Record #{rec_index} @ 0x{off:05x} ──")
    # +0x00: label (16 wchar = 32 bytes)
    label, p, w = read_utf16(data, off + 0x00, 16)
    print(f"  +0x00  Label       ({w:2d} wchar) = {label!r}")
    # +0x20: pad (8 bytes)
    # +0x28: range (u16)
    rng = struct.unpack('<H', data[off+0x28:off+0x2A])[0]
    print(f"  +0x28  Range       = {rng}")
    # +0x2A: display format (u16)
    fmt = struct.unpack('<H', data[off+0x2A:off+0x2C])[0]
    print(f"  +0x2A  DisplayFmt  = {fmt}")
    # +0x2C: unit (5 wchar = 10 bytes)
    unit, p, w = read_utf16(data, off + 0x2C, 5)
    print(f"  +0x2C  Unit        ({w:2d} wchar) = {unit!r}")
    # +0x36: short gas name (variable wchars)
    short, p, w = read_utf16(data, off + 0x36, 6)
    print(f"  +0x36  ShortGas    ({w:2d} wchar) = {short!r}")
    # +0x40..+0x44: type, line? (u16 x 2)
    v40 = struct.unpack('<H', data[off+0x40:off+0x42])[0]
    v42 = struct.unpack('<H', data[off+0x42:off+0x44])[0]
    print(f"  +0x40  Field1      = 0x{v40:04x} ({v40})")
    print(f"  +0x42  Field2      = 0x{v42:04x} ({v42})")
    # +0x44..+0x48: thresholds
    for off2, name in [(0x44,'Inst1'),(0x46,'Inst2'),(0x48,'Inst3'),(0x4A,'Avg1'),(0x4C,'Avg2'),(0x4E,'Avg3'),
                       (0x50,'Underscale'),(0x52,'Overscale'),(0x54,'Fault'),(0x56,'OutOfRange')]:
        v = struct.unpack('<h', data[off+off2:off+off2+2])[0]
        print(f"  +0x{off2:02x}  {name:11s} = {v:5d}")
    # +0x58..: averaging times
    for off2, name in [(0x58,'AvgTime1'),(0x5A,'AvgTime2'),(0x5C,'AvgTime3'),(0x5E,'Hysteresis')]:
        v = struct.unpack('<H', data[off+off2:off+off2+2])[0]
        print(f"  +0x{off2:02x}  {name:11s} = {v}")
    # +0x60: pad (2 bytes), +0x62: enable (u16), +0x64: ack (u16), +0x66: edge (u16)
    print(f"  +0x60  Pad         = 0x{struct.unpack('<H', data[off+0x60:off+0x62])[0]:04x}")
    en = struct.unpack('<H', data[off+0x62:off+0x64])[0]
    ak = struct.unpack('<H', data[off+0x64:off+0x66])[0]
    ed = struct.unpack('<H', data[off+0x66:off+0x68])[0]
    print(f"  +0x62  Enable      = 0x{en:04x} (bits: Inst1={en&1} Inst2={(en>>1)&1} Inst3={(en>>2)&1})")
    print(f"  +0x64  Acknowledge = 0x{ak:04x}")
    print(f"  +0x66  Edge        = 0x{ed:04x}")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cfg", type=Path)
    ap.add_argument("--limit", type=int, default=4, help="Max records to dump in detail")
    args = ap.parse_args()

    with open(args.cfg, 'rb') as f:
        data = f.read()

    print(f"=== {args.cfg.name} ({len(data)} bytes) ===")

    # Project name
    name, _, _ = read_utf16(data, 0x8000, 16)
    print(f"Project name   : {name!r}")

    # Count sensors
    sensor_count = 0
    sensor_offsets = []
    for off in range(0xA000, 0xAFE0, 0x74):
        chars = []
        for j in range(0, 32, 2):
            c = struct.unpack('<H', data[off+j:off+j+2])[0]
            if c == 0: break
            chars.append(chr(c) if 32 <= c < 127 else '?')
        if ''.join(chars).strip():
            sensor_count += 1
            sensor_offsets.append(off)
    print(f"Sensors        : {sensor_count} (offsets: {[hex(x) for x in sensor_offsets[:8]]}{'...' if len(sensor_offsets) > 8 else ''})")

    # Dump 0x8400-0x8500 in detail
    print(f"\n  ── 0x8400-0x8500 (line/zone-mask region) ──")
    for off in range(0x8400, 0x8500, 2):
        v = struct.unpack('<H', data[off:off+2])[0]
        if v != 0:
            bits = bin(v).count('1')
            print(f"  +0x{off:04x}: 0x{v:04x} ({v:5d}, {bits} bits set)")

    # Dump sensors in detail
    print(f"\n  ── First {args.limit} sensor record(s) ──")
    for i, off in enumerate(sensor_offsets[:args.limit]):
        dump_sensor_record(data, i, off)

if __name__ == "__main__":
    main()
