#!/usr/bin/env python3
"""Build and check the universal (self-configuring) Weintek macros.

    python3 tools/weintek_universal.py build   # write the .ebm import files
    python3 tools/weintek_universal.py check   # run the macros against a fake MX43

`check` interprets the macro source itself (the subset of the EasyBuilder
macro language the two macros use) against an in-memory MX43 register model
and the cMT LW area. It proves the logic, not EasyBuilder's compiler: the
macros still have to compile in EasyBuilder Pro.
"""
import ast
import glob
import json
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MACRO_DIR = os.path.join(ROOT, "weintek", "universal", "macros")

# (source file, macro id, name, periodic interval in 100 ms units)
MACROS = [
    ("mx43-scan.txt", 20, "MX43 Scan", 50),
    ("mx43-runtime.txt", 21, "MX43 Runtime", 10),
]


# ----------------------------------------------------------------- build
def render_ebm(macro_id, name, interval, source):
    head = [
        "macro_definition_begin",
        f'    "id": "{macro_id}"',
        f'    "name": "{name}"',
        '    "startup": "true"',
        f'    "periodic": [ "true", "{interval}" ]',
        '    "interlock": [ "false" ]',
        "macro_definition_end",
        "",
    ]
    body = source.replace("\r\n", "\n").rstrip("\n").split("\n")
    return "﻿".encode("utf-8") + "\r\n".join(head + body).encode("utf-8")


def build():
    for fn, macro_id, name, interval in MACROS:
        src = open(os.path.join(MACRO_DIR, fn), encoding="utf-8").read()
        out = os.path.join(MACRO_DIR, f"{macro_id}_{name}.ebm")
        with open(out, "wb") as f:
            f.write(render_ebm(macro_id, name, interval, src))
        print("wrote", os.path.relpath(out, ROOT))
    out = os.path.join(os.path.dirname(MACRO_DIR), "universal-lw-address-tag-library.csv")
    with open(out, "wb") as f:
        f.write("".join(f"{n},cMT,LW,{a},,{t}\r\n" for n, a, t in lw_tags()).encode("utf-8"))
    print("wrote", os.path.relpath(out, ROOT))


U16, S16, FLT = "16-bit Unsigned", "16-bit Signed", "32-bit Float"

# Per-slot fields: LW (slot * 100) + offset.
SLOT_FIELDS = [
    ("Name", 0, U16), ("Status", 16, U16), ("FullGas", 17, U16), ("Range", 37, U16),
    ("DisplayFormat", 38, U16), ("Unit", 39, U16), ("AbbGas", 44, U16),
    ("Alarm1", 51, S16), ("Alarm2", 52, S16), ("Alarm3", 53, S16),
    ("Measurement", 70, S16), ("AlarmBits", 71, U16), ("ScaledInteger", 72, S16),
    ("ScaleDivisor", 73, U16), ("AlarmLevelCount", 74, U16), ("AlarmSeverity", 75, U16),
    ("Used", 76, U16), ("Mx43Address", 77, U16), ("Line", 78, U16), ("DetectorNo", 79, U16),
    ("RangeText", 80, U16), ("Alarm1Text", 84, U16), ("Alarm2Text", 88, U16),
    ("Alarm3Text", 92, U16), ("ValueText", 96, U16),
]

GLOBAL_TAGS = [
    ("Project-Title", 3300, U16), ("MX43-DetectorCount", 3320, U16), ("MX43-ScanState", 3321, U16),
    ("MX43-LinkOk", 3322, U16), ("MX43-FoundTotal", 3323, U16), ("MX43-Overflow", 3324, U16),
    ("MX43-RescanRequest", 3327, U16), ("MX43-ScanCounter", 3328, U16), ("MX43-WorstSeverity", 3330, U16),
]


def lw_tags():
    tags = list(GLOBAL_TAGS)
    for slot in range(1, 33):
        tags += [(f"Det{slot}-{name}", slot * 100 + off, typ) for name, off, typ in SLOT_FIELDS]
        tags += [
            (f"Det{slot}-ValueFloat", 4000 + 2 * (slot - 1), FLT),
            (f"Det{slot}-ValueRawMirror", 4100 + slot - 1, S16),
            (f"Det{slot}-AlarmBitsMirror", 4200 + slot - 1, U16),
            (f"Det{slot}-SeverityMirror", 4300 + slot - 1, U16),
        ]
    return tags


# ----------------------------------------------------------- interpreter
class MacroError(Exception):
    pass


TYPES = {"short": 16, "int": 32, "float": 0, "char": 8, "bool": 8}


def wrap(value, typ):
    if typ == "float":
        return struct.unpack("<f", struct.pack("<f", float(value)))[0]
    bits = TYPES[typ]
    value = int(value) & ((1 << bits) - 1)
    return value - (1 << bits) if value >> (bits - 1) else value


def parse(source):
    """Return (declarations, block tree) for one macro_command main()."""
    lines = []
    for no, raw in enumerate(source.split("\n"), 1):
        code = raw.split("//")[0].strip()
        if code:
            lines.append((no, code))
    if lines[0][1] != "macro_command main()" or lines[-1][1] != "end macro_command":
        raise MacroError("macro must be a single macro_command main()")
    lines = lines[1:-1]
    decls = {}
    i = 0
    while i < len(lines) and lines[i][1].split()[0] in TYPES:
        no, code = lines[i]
        typ, rest = code.split(None, 1)
        for item in rest.split(","):
            m = re.fullmatch(r"\s*([A-Za-z]\w*)(?:\[(\d+)\])?\s*", item)
            if not m:
                raise MacroError(f"line {no}: bad declaration {item!r}")
            if m.group(1) in decls:
                raise MacroError(f"line {no}: redeclaration of {m.group(1)}")
            if len(m.group(1)) > 32:
                raise MacroError(f"line {no}: name longer than 32 characters")
            decls[m.group(1)] = (typ, int(m.group(2)) if m.group(2) else None)
        i += 1
    size = sum((TYPES[t] or 32) // 8 * (n or 1) for t, n in decls.values())
    if size > 4096:
        raise MacroError(f"local variables use {size} bytes (EasyBuilder limit 4096)")
    pos = [i]

    def block(terminators):
        out = []
        while pos[0] < len(lines):
            no, code = lines[pos[0]]
            if any(re.match(t, code) for t in terminators):
                return out
            pos[0] += 1
            if code.split()[0] in TYPES:
                raise MacroError(f"line {no}: declaration after first statement")
            m = re.fullmatch(r"for (\w+) = (.+) to (.+)", code)
            if m:
                body = block([r"next\b"])
                if lines[pos[0]][1] != f"next {m.group(1)}":
                    raise MacroError(f"line {no}: for/next mismatch")
                pos[0] += 1
                out.append(("for", no, m.group(1), m.group(2), m.group(3), body))
                continue
            m = re.fullmatch(r"if (.+) then", code)
            if m:
                arms = [(m.group(1), block([r"else\b", r"end if$"]))]
                while True:
                    no2, c2 = lines[pos[0]]
                    pos[0] += 1
                    if c2 == "end if":
                        break
                    m2 = re.fullmatch(r"else if (.+) then", c2)
                    if m2:
                        arms.append((m2.group(1), block([r"else\b", r"end if$"])))
                    elif c2 == "else":
                        arms.append((None, block([r"end if$"])))
                    else:
                        raise MacroError(f"line {no2}: unexpected {c2!r}")
                out.append(("if", no, arms))
                continue
            m = re.fullmatch(r"(\w+)\((.*)\)", code)
            if m:
                out.append(("call", no, m.group(1), [a.strip() for a in m.group(2).split(",")]))
                continue
            m = re.fullmatch(r"(\w+(?:\[.+?\])?) = (.+)", code)
            if m:
                out.append(("assign", no, m.group(1), m.group(2)))
                continue
            raise MacroError(f"line {no}: cannot parse {code!r}")
        return out

    tree = block([])
    return decls, tree


class Machine:
    """One HMI: the LW area plus a device called MX43."""

    def __init__(self, mx43):
        self.lw = {}
        self.mx43 = mx43
        self.requests = 0

    def run(self, source):
        decls, tree = parse(source)
        self.decls = decls
        self.vars = {k: ([0] * n if n else 0) for k, (t, n) in decls.items()}
        self.err = 0
        self.exec_block(tree)

    # -- expressions
    def ev(self, text, no):
        node = ast.parse(text.replace("<>", "!="), mode="eval").body
        return self.ev_node(node, no)

    def ev_node(self, node, no):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Name):
            if node.id not in self.vars:
                raise MacroError(f"line {no}: undeclared identifier {node.id}")
            if self.decls[node.id][1]:
                raise MacroError(f"line {no}: array {node.id} used without index")
            return self.vars[node.id]
        if isinstance(node, ast.Subscript):
            arr = self.vars[node.value.id]
            idx = self.ev_node(node.slice, no)
            if not 0 <= idx < len(arr):
                raise MacroError(f"line {no}: {node.value.id}[{idx}] out of range")
            return arr[idx]
        if isinstance(node, ast.BinOp):
            a, b = self.ev_node(node.left, no), self.ev_node(node.right, no)
            op = type(node.op)
            if op is ast.Add:
                return a + b
            if op is ast.Sub:
                return a - b
            if op is ast.Mult:
                return a * b
            if op is ast.Div:
                if isinstance(a, float) or isinstance(b, float):
                    return a / b
                return int(a / b)  # truncating integer division
            if op is ast.BitAnd:
                return (a & 0xFFFF) & (b & 0xFFFF)
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            a, b = self.ev_node(node.left, no), self.ev_node(node.comparators[0], no)
            return {ast.Eq: a == b, ast.NotEq: a != b, ast.Lt: a < b, ast.LtE: a <= b,
                    ast.Gt: a > b, ast.GtE: a >= b}[type(node.ops[0])]
        raise MacroError(f"line {no}: unsupported expression")

    def ref(self, text, no):
        m = re.fullmatch(r"(\w+)(?:\[(.+)\])?", text)
        name = m.group(1)
        if name not in self.vars:
            raise MacroError(f"line {no}: undeclared identifier {name}")
        typ, n = self.decls[name]
        if n and not m.group(2):
            raise MacroError(f"line {no}: array {name} used without index")
        return name, typ, (self.ev(m.group(2), no) if m.group(2) else None)

    def store(self, name, typ, idx, value, no):
        if idx is None:
            self.vars[name] = wrap(value, typ)
        else:
            if not 0 <= idx < len(self.vars[name]):
                raise MacroError(f"line {no}: {name}[{idx}] out of range")
            self.vars[name][idx] = wrap(value, typ)

    # -- statements
    def exec_block(self, tree):
        for st in tree:
            kind, no = st[0], st[1]
            if kind == "assign":
                name, typ, idx = self.ref(st[2], no)
                self.store(name, typ, idx, self.ev(st[3], no), no)
            elif kind == "if":
                for cond, body in st[2]:
                    if cond is None or self.ev(cond, no):
                        self.exec_block(body)
                        break
            elif kind == "for":
                name, typ, _ = self.ref(st[2], no)
                value, end = self.ev(st[3], no), self.ev(st[4], no)
                while value <= end:
                    self.store(name, typ, None, value, no)
                    self.exec_block(st[5])
                    value = self.vars[name] + 1
            elif kind == "call":
                self.call(st[2], st[3], no)

    def words_per(self, typ):
        return 2 if typ in ("int", "float") else 1

    def call(self, fn, args, no):
        if fn == "FILL":
            name, typ, idx = self.ref(args[0], no)
            for i in range(self.ev(args[2], no)):
                self.store(name, typ, idx + i, self.ev(args[1], no), no)
            return
        if fn == "GetError":
            name, typ, idx = self.ref(args[0], no)
            self.store(name, typ, idx, self.err, no)
            return
        if fn not in ("GetData", "GetDataEx", "SetData"):
            raise MacroError(f"line {no}: unknown function {fn}")
        if len(args) != 5:
            raise MacroError(f"line {no}: {fn} needs 5 arguments")
        name, typ, idx = self.ref(args[0], no)
        device, dtype = args[1].strip('"'), args[2]
        address, count = self.ev(args[3], no), self.ev(args[4], no)
        if idx is None and count != 1:
            raise MacroError(f"line {no}: scalar with data_count {count}")
        if idx is not None and idx + count > len(self.vars[name]):
            raise MacroError(f"line {no}: {name} too small for {count} items")
        nwords = (count + 1) // 2 if typ in ("char", "bool") else count * self.words_per(typ)
        if device == "cMT" and dtype == "LW":
            if fn == "SetData":
                self.write_lw(name, typ, idx, address, count)
            else:
                self.unpack(name, typ, idx, count, [self.lw.get(address + i, 0) for i in range(nwords)], no)
            self.err = 0
        elif device == "MX43" and dtype == "3x" and fn == "GetDataEx":
            self.requests += 1
            words = self.mx43.read(address, nwords)
            self.err = 0 if words is not None else 1
            if words is not None:
                self.unpack(name, typ, idx, count, words, no)
        else:
            raise MacroError(f"line {no}: unsupported {fn} on {device} {dtype}")

    def items(self, name, idx, count):
        return [self.vars[name]] if idx is None else self.vars[name][idx:idx + count]

    def write_lw(self, name, typ, idx, address, count):
        data = self.items(name, idx, count)
        if typ in ("char", "bool"):
            raw = bytes(v & 0xFF for v in data) + b"\0"
            words = [raw[i] | raw[i + 1] << 8 for i in range(0, len(data), 2)]
        elif typ == "float":
            words = [w for v in data for w in struct.unpack("<HH", struct.pack("<f", v))]
        elif typ == "int":
            words = [w for v in data for w in struct.unpack("<HH", struct.pack("<i", v))]
        else:
            words = [v & 0xFFFF for v in data]
        for i, w in enumerate(words):
            self.lw[address + i] = w

    def unpack(self, name, typ, idx, count, words, no):
        if typ == "float":
            vals = [struct.unpack("<f", struct.pack("<HH", *words[i:i + 2]))[0] for i in range(0, len(words), 2)]
        elif typ == "int":
            vals = [struct.unpack("<i", struct.pack("<HH", *words[i:i + 2]))[0] for i in range(0, len(words), 2)]
        elif typ in ("char", "bool"):
            vals = [b for w in words for b in (w & 0xFF, w >> 8)][:count]
        else:
            vals = words
        for i, v in enumerate(vals):
            self.store(name, typ, None if idx is None else idx + i, v, no)

    # -- helpers for tests
    def text(self, address, words):
        raw = b"".join(struct.pack("<H", self.lw.get(address + i, 0)) for i in range(words))
        return raw.split(b"\0")[0].decode("ascii")

    def name(self, address, words=16):
        return "".join(chr(self.lw.get(address + i, 0)) for i in range(words)).split("\0")[0]

    def s16(self, address):
        return wrap(self.lw.get(address, 0), "short")

    def flt(self, address):
        return struct.unpack("<f", struct.pack("<HH", self.lw.get(address, 0), self.lw.get(address + 1, 0)))[0]


class FakeMx43:
    """MX43 input-register model: 68-word config blocks at request
    addresses 1..264, measurements at 2000+addr, alarms at 2300+addr."""

    def __init__(self, empty="zeros"):
        self.cfg = {}
        self.meas = {}
        self.alarm = {}
        self.crc = 0x1234ABCD
        self.online = True
        self.empty = empty  # "zeros" or "exception" for unconfigured slots
        self.fail_after = None  # drop the link after this many requests

    def add(self, addr, label, rng, fmt=0, unit="ppm", gas="GAS", a=(0, 0, 0), avg=(0, 0, 0), enable=0, status=1):
        b = [0] * 68
        for i, c in enumerate(label[:15]):
            b[i] = ord(c)
        b[16] = status
        for i, c in enumerate(gas[:19]):
            b[17 + i] = ord(c)
        b[37], b[38] = rng, fmt
        for i, c in enumerate(unit[:4]):
            b[39 + i] = ord(c)
        b[51:54] = [v & 0xFFFF for v in a]
        b[54:57] = [v & 0xFFFF for v in avg]
        b[65] = enable
        self.cfg[addr] = b

    def read(self, address, count):
        if self.fail_after is not None:
            self.fail_after -= 1
            if self.fail_after < 0:
                self.online = False
        if not self.online:
            return None
        if 1 <= address <= 264:
            if address in self.cfg:
                return list(self.cfg[address][:count])
            return [0] * count if self.empty == "zeros" else None
        if address == 2600:
            return [self.crc >> 16, self.crc & 0xFFFF, 0, 1][:count]
        if 2001 <= address <= 2264:
            return [self.meas.get(address - 2000, 0) & 0xFFFF]
        if 2301 <= address <= 2564:
            return [self.alarm.get(address - 2300, 0) & 0xFFFF]
        return None


# ------------------------------------------------------------------ check
def check():
    scan = open(os.path.join(MACRO_DIR, "mx43-scan.txt"), encoding="utf-8").read()
    runtime = open(os.path.join(MACRO_DIR, "mx43-runtime.txt"), encoding="utf-8").read()
    failures = []

    def eq(what, actual, expected):
        ok = abs(actual - expected) < 1e-4 if isinstance(expected, float) else actual == expected
        if not ok:
            failures.append(what)
        print(f"  [{'ok' if ok else 'FAIL'}] {what}: {actual!r}" + ("" if ok else f" (expected {expected!r})"))

    def site(empty):
        mx = FakeMx43(empty)
        mx.add(1, "Kylmaskin 1", 1000, 0, "ppm", "NH3", a=(25, 50, 0), enable=0b011)
        mx.add(2, "Syre", 300, 1, "%VOL", "O2", a=(190, 180, 230), enable=0b111)
        mx.add(33, "Koldioxid", 500, 2, "%VOL", "CO2", a=(0, 0, 0), avg=(50, 0, 0), enable=0b001000)
        mx.add(40, "Avstangd", 100, 0, "%LEL", "CH4", a=(20, 40, 0), enable=0b011, status=0)
        mx.add(257, "Analog 1", 100, 0, "%LEL", "CH4", a=(10, 0, 0), enable=0)
        mx.meas.update({1: 60, 2: 209, 33: 5, 40: 0, 257: -3})
        mx.alarm.update({1: 0b011, 2: 0b001, 33: 0b001, 257: 0b100000})
        return mx

    for empty in ("zeros", "exception"):
        print(f"Site scan, unconfigured slots answer with {empty}")
        mx = site(empty)
        hmi = Machine(mx)
        hmi.run(scan)
        hmi.run(runtime)
        eq("detector count LW-3320", hmi.lw[3320], 5)
        eq("scan state LW-3321", hmi.lw[3321], 2)
        eq("link LW-3322", hmi.lw[3322], 1)
        eq("slot addresses LW-3400", [hmi.lw[3400 + i] for i in range(6)], [1, 2, 33, 40, 257, 0])
        eq("slot 1 name", hmi.name(100), "Kylmaskin 1")
        eq("slot 2 unit", hmi.name(239, 5), "%VOL")
        eq("slot 2 divisor / alarm levels", [hmi.lw[273], hmi.lw[274]], [10, 3])
        eq("slot 1 alarm levels", hmi.lw[174], 2)
        eq("slot 3 alarm levels (averaged only)", hmi.lw[374], 1)
        eq("slot 5 alarm levels (not enabled)", hmi.lw[574], 0)
        eq("slot 4 status OFF", hmi.lw[416], 0)
        eq("slot 3 used/addr/line/det", [hmi.lw[376 + i] for i in range(4)], [1, 33, 2, 1])
        eq("slot 5 used/addr/line/det", [hmi.lw[576 + i] for i in range(4)], [1, 257, 0, 1])
        eq("slot 6 unused", hmi.lw.get(676, 0), 0)
        eq("slot 2 range text", hmi.text(280, 4), "30.0")
        eq("slot 2 alarm texts", [hmi.text(284 + 4 * i, 4) for i in range(3)], ["19.0", "18.0", "23.0"])
        eq("slot 1 range text", hmi.text(180, 4), "1000")
        eq("slot 1 value text", hmi.text(196, 4), "60")
        eq("slot 2 value text", hmi.text(296, 4), "20.9")
        eq("slot 3 value text", hmi.text(396, 4), "0.05")
        eq("slot 5 value text (negative)", hmi.text(596, 4), "-3")
        eq("slot 1 raw / alarm bits", [hmi.lw[170], hmi.lw[171]], [60, 3])
        eq("severity slots 1-5", [hmi.lw[100 * s + 75] for s in range(1, 6)], [3, 1, 3, 0, 3])
        eq("severity mirror LW-4300", [hmi.lw[4300 + i] for i in range(5)], [3, 1, 3, 0, 3])
        eq("worst severity LW-3330", hmi.lw[3330], 3)
        eq("float mirror slot 2", hmi.flt(4002), 20.9)
        eq("float mirror slot 3", hmi.flt(4004), 0.05)
        eq("raw mirror slot 5", hmi.s16(4104), -3)
        print(f"  Modbus requests for the first scan: {hmi.requests}")

    print("Reprogrammed MX43 (CRC change) is picked up without touching the HMI")
    mx = site("zeros")
    hmi = Machine(mx)
    hmi.run(scan)
    del mx.cfg[2]
    mx.add(64, "Ny givare", 100, 0, "%LEL", "CH4")
    hmi.run(scan)
    eq("no rescan while CRC is unchanged", [hmi.lw[3400 + i] for i in range(5)], [1, 2, 33, 40, 257])
    mx.crc = 0x0BADF00D
    hmi.run(scan)
    eq("slots after rescan", [hmi.lw[3400 + i] for i in range(6)], [1, 33, 40, 64, 257, 0])
    eq("slot 2 now holds address 33", hmi.name(200), "Koldioxid")
    eq("old slot 6 cleared", hmi.name(600), "")
    eq("scan counter", hmi.lw[3328], 2)

    print("Manual rescan request (LW-3327)")
    mx.add(65, "Extra", 100)
    hmi.lw[3327] = 1
    hmi.run(scan)
    eq("count after request", hmi.lw[3320], 6)
    eq("request flag cleared", hmi.lw[3327], 0)

    print("Link loss never wipes the screen")
    mx = site("exception")
    hmi = Machine(mx)
    hmi.run(scan)
    hmi.run(runtime)
    mx.online = False
    hmi.run(scan)
    hmi.run(runtime)
    eq("link flag", hmi.lw[3322], 0)
    eq("slots kept", hmi.lw[3320], 5)
    eq("last value kept", hmi.lw[270], 209)
    mx.online, mx.crc, mx.fail_after = True, 0x55555555, 100
    hmi.run(scan)
    eq("link lost mid-scan: slots kept", [hmi.lw[3400 + i] for i in range(5)], [1, 2, 33, 40, 257])
    eq("link lost mid-scan: link flag", hmi.lw[3322], 0)
    mx.online, mx.fail_after = True, None
    hmi.run(scan)
    eq("scan repeated after link returns", hmi.lw[3328], 2)

    print("No contact at power-up")
    mx = site("zeros")
    mx.online = False
    hmi = Machine(mx)
    hmi.run(scan)
    hmi.run(runtime)
    eq("state stays 0", hmi.lw.get(3321, 0), 0)
    mx.online = True
    hmi.run(scan)
    eq("found once the MX43 answers", hmi.lw[3320], 5)

    print("More than 32 detectors")
    mx = FakeMx43()
    for a in range(1, 41):
        mx.add(a, f"Det {a}", 100)
    hmi = Machine(mx)
    hmi.run(scan)
    hmi.run(runtime)
    eq("shown", hmi.lw[3320], 32)
    eq("found", hmi.lw[3323], 40)
    eq("overflow flag", hmi.lw[3324], 1)
    eq("slot 32 name", hmi.name(3200), "Det 32")

    plans = sorted(glob.glob(os.path.join(ROOT, "out", "*", "weintek-plan.json")))
    for path in plans:
        plan = json.load(open(path, encoding="utf-8"))
        dets = plan["detectors"]
        print(f"Generator plan {os.path.basename(os.path.dirname(path))} ({len(dets)} detectors)")
        mx = FakeMx43()
        for d in dets:
            mx.add(d["ConfigRegister"], d["Label"], d["RangeRaw"], d["DisplayFormat"], d["Unit"], d["ShortGasName"])
        hmi = Machine(mx)
        hmi.run(scan)
        eq("slot order matches the cfg-based generator",
           [hmi.lw[3400 + i] for i in range(len(dets))], [d["ConfigRegister"] for d in dets])
        eq("divisors match", [hmi.lw[100 * d["ScreenNo"] + 73] for d in dets], [d["ScaleDivisor"] for d in dets])
        eq("names match", [hmi.name(100 * d["ScreenNo"]) for d in dets], [d["Label"][:15] for d in dets])

    print("\nFAILED: " + ", ".join(failures) if failures else "\nAll checks passed.")
    return 1 if failures else 0


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "build":
        build()
    elif len(sys.argv) == 2 and sys.argv[1] == "check":
        sys.exit(check())
    else:
        print(__doc__)
        sys.exit(2)
