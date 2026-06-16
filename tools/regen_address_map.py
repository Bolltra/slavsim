#!/usr/bin/env python3
"""Regenerate the embedded MX43 Modbus address map from the project xlsx.

Run this if Mx43 adresslista.xlsx changes. The output is
src/Mx43Sim.Core/Modbus/mx43_address_map.json and is embedded in the
assembly as an EmbeddedResource.
"""
import json
import sys
from pathlib import Path

try:
    import openpyxl
except ImportError:
    print("openpyxl is required: pip install openpyxl", file=sys.stderr)
    sys.exit(1)

ROOT = Path(__file__).resolve().parent.parent
XLSX = ROOT / "Mx43 adresslista.xlsx"
OUT  = ROOT / "src" / "Mx43Sim.Core" / "Modbus" / "mx43_address_map.json"


def get(sh, r, c):
    v = sh.cell(r, c).value
    return v if v is not None else ""


def block(sh, start_row):
    """Return a list of {line, analog, detectors: [32 ints]} for the block
    starting at start_row (rows start_row..start_row+7)."""
    addrs = []
    for r in range(start_row, start_row + 8):
        line = get(sh, r, 1)
        if not str(line).startswith("Line"):
            continue
        try:
            line_num = int(str(line).split()[1])
        except (IndexError, ValueError):
            continue
        try:
            analog = int(get(sh, r, 3))
        except (TypeError, ValueError):
            analog = 0
        detectors = []
        for c in range(5, 5 + 32):
            try:
                detectors.append(int(get(sh, r, c)))
            except (TypeError, ValueError):
                detectors.append(0)
        addrs.append({"line": line_num, "analog": analog, "detectors": detectors})
    return addrs


def main():
    if not XLSX.exists():
        print(f"ERROR: {XLSX} not found", file=sys.stderr)
        sys.exit(1)
    wb = openpyxl.load_workbook(XLSX, data_only=True)
    sh = wb.active
    addr_map = {
        "measurement": block(sh, 3),   # rows 3..10
        "alarm":       block(sh, 14),  # rows 14..21
        "config":      block(sh, 25),  # rows 25..32
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(addr_map, f, indent=2, ensure_ascii=False)
    print(f"Wrote {OUT} ({len(addr_map['measurement'])} lines x 32 detectors)")


if __name__ == "__main__":
    main()
