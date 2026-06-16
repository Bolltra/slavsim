"""Dump what the running Mx43Sim server would return for a given
detector's Modbus registers, so we can compare against what the slave
display shows.

Usage:
    python3 tools/dump_registers.py path/to/cfg.cfg 1 1 [--port 502]
    python3 tools/dump_registers.py path/to/cfg.cfg 1 1 --internal
"""
import argparse
import socket
import struct
import sys
from pathlib import Path

# Reuse the C# parser: produce a tiny .NET helper or just inline a Python
# parser. The cleanest is to shell out to the C# tests program. But for
# portability let's just describe the format.

def read_utf16(data, off, max_chars=32):
    chars = []
    for i in range(max_chars):
        if off + i*2 + 1 >= len(data): break
        c = struct.unpack('<H', data[off + i*2:off + i*2 + 2])[0]
        if c == 0: break
        chars.append(chr(c) if 32 <= c < 127 else f'\\u{c:04x}')
    return ''.join(chars)

def read_u16(data, off): return struct.unpack('<H', data[off:off+2])[0]
def read_i16(data, off): return struct.unpack('<h', data[off:off+2])[0]

# Offsets within a sensor record (0x74 bytes)
# Per my analysis of the .cfg file format (different from the Modbus
# register layout that the slave display reads!)
CFG_LABEL      = 0x00  # 16 wchar
CFG_RANGE      = 0x28
CFG_DISPLAYFMT = 0x2A
CFG_UNIT       = 0x2C  # 5 wchar
CFG_SHORTGAS   = 0x36  # 6 wchar
CFG_FIELD1     = 0x40  # unknown
CFG_FIELD2     = 0x42  # unknown
CFG_INST1      = 0x44
CFG_INST2      = 0x46
CFG_INST3      = 0x48
CFG_AVG1       = 0x4A
CFG_AVG2       = 0x4C
CFG_AVG3       = 0x4E
CFG_UNDER      = 0x50
CFG_OVER       = 0x52
CFG_FAULT      = 0x54
CFG_OOR        = 0x56
CFG_AVGT1      = 0x58
CFG_AVGT2      = 0x5A
CFG_AVGT3      = 0x5C
CFG_HYST       = 0x5E
CFG_ENABLE     = 0x62
CFG_ACK        = 0x64
CFG_EDGE       = 0x66

RECORD_SIZE = 0x74
RECORD_BASE = 0xA000

# Mx43 register addresses (per Mx43 adresslista.xlsx and cahier)
CFG_BASE = 1
CFG_PER_LINE = 32  # 32 config-registers per line x 8 lines = 256 total
MEAS_BASE = 2001
ALARM_BASE = 2301

def config_register_addr(line, det):
    return CFG_BASE + (line - 1) * 32 + (det - 1)

def measurement_register_addr(line, det):
    return MEAS_BASE + (line - 1) * 32 + (det - 1)

def alarm_register_addr(line, det):
    return ALARM_BASE + (line - 1) * 32 + (det - 1)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cfg", type=Path)
    ap.add_argument("line", type=int)
    ap.add_argument("det", type=int)
    ap.add_argument("--port", type=int, default=None, help="Connect to running Mx43Sim and dump live")
    args = ap.parse_args()

    with open(args.cfg, 'rb') as f:
        data = f.read()

    # Find the sensor record by line/det
    # In .cfg: sensor index = (line-1)*32 + (det-1), at offset 0xA000 + index*0x74
    sensor_idx = (args.line - 1) * 32 + (args.det - 1)
    rec_off = RECORD_BASE + sensor_idx * RECORD_SIZE
    if rec_off + RECORD_SIZE > len(data):
        print(f"ERROR: sensor out of range (rec_off=0x{rec_off:x}, file size {len(data)})")
        return
    rec = data[rec_off:rec_off + RECORD_SIZE]

    label = read_utf16(rec, CFG_LABEL, 16)
    if not label:
        print(f"L{args.line}D{args.det}: NO SENSOR CONFIGURED at this position")
        return

    print(f"=== L{args.line}D{args.det} (record @ 0x{rec_off:05x}) ===")
    print(f"  Label   : {label!r}")
    print(f"  Range   : {read_u16(rec, CFG_RANGE)}")
    print(f"  Unit    : {read_utf16(rec, CFG_UNIT, 5)!r}")
    print(f"  ShortGas: {read_utf16(rec, CFG_SHORTGAS, 6)!r}")
    print(f"  +0x40   : 0x{read_u16(rec, CFG_FIELD1):04x}")
    print(f"  +0x42   : 0x{read_u16(rec, CFG_FIELD2):04x}")
    print(f"  Inst1/2/3: {read_i16(rec, CFG_INST1)} / {read_i16(rec, CFG_INST2)} / {read_i16(rec, CFG_INST3)}")
    print(f"  Enable  : 0x{read_u16(rec, CFG_ENABLE):04x}")

    print(f"\n--- Modbus register map (1-based) ---")
    print(f"  Config base    : reg {config_register_addr(args.line, args.det)} (1 register)")
    print(f"  Measurement    : reg {measurement_register_addr(args.line, args.det)}")
    print(f"  Alarm bits     : reg {alarm_register_addr(args.line, args.det)}")

    if args.port:
        print(f"\n--- Live read from port {args.port} ---")
        for reg_base, name in [(config_register_addr(args.line, args.det), "config"),
                                (measurement_register_addr(args.line, args.det), "measurement"),
                                (alarm_register_addr(args.line, args.det), "alarm")]:
            try:
                with socket.create_connection(("127.0.0.1", args.port), timeout=2) as s:
                    # FC3 read holding register
                    req = struct.pack(">HHHBBHH", 0, 0, 0, 6, 1, 3, reg_base, 1)
                    s.send(req)
                    resp = s.recv(64)
                    if len(resp) >= 11:
                        v = struct.unpack(">H", resp[9:11])[0]
                        print(f"  {name} reg {reg_base} = 0x{v:04x} ({v})")
                    else:
                        print(f"  {name} reg {reg_base} = (no/short response)")
            except Exception as ex:
                print(f"  {name} reg {reg_base} = ERROR: {ex}")

if __name__ == "__main__":
    main()
