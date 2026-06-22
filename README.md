# MX43 Simulator

A lightweight Windows application that simulates a Teledyne/Oldham **MX43** gas-detection controller over **Modbus TCP** (port 502). It reads the original `.cfg` configuration exported by the MX43 programming tool and exposes the configured detectors and their alarm state on the standard MX43 register map (per the *cahier des charges*).

## What it does

- Load any `.cfg` exported by the MX43 programming tool.
- Parse it into a structured model (project, zones, modules, relays, sensors, alarm thresholds).
- Run a Modbus TCP server that responds to FC3/FC4/FC6/FC16 reads/writes.
- Let the user set a simulated gas measurement for any detector; the alarm bits are derived automatically from the configured thresholds so the slave display shows the correct colour and value.
- **No external NuGet dependencies** — the parser, the Modbus server and the XLSX-style address list are all hand-rolled in `Mx43Sim.Core.dll`.

## Build

The project targets **.NET 10**. The `net10.0` and `net10.0-windows` targets share the same source. On Windows you can build everything; on Linux you can only build the Core + Tests projects (the WinForms App needs `EnableWindowsTargeting=true`, which is already set in the csproj).

```powershell
# Windows — build the WinForms app
dotnet build src\Mx43Sim.App\Mx43Sim.App.csproj -c Release
src\Mx43Sim.App\bin\Release\net10.0-windows\Mx43Sim.exe
```

```powershell
# Windows — produce a self-contained SINGLE-FILE .exe (no .NET install
# required on the target, no DLLs to keep alongside — just one .exe)
dotnet publish src\Mx43Sim.App\Mx43Sim.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
src\Mx43Sim.App\bin\Release\net10.0-windows\win-x64\publish\Mx43Sim.exe
```

```bash
# Linux — build + run smoke tests against fixtures you drop into tests/fixtures/
dotnet run --project src/Mx43Sim.Tests/Mx43Sim.Tests.csproj
```

### Firewall note

The first time you run the app on a new Windows machine the firewall
prompts you to allow inbound TCP on port 502. Click **Allow access**
or run this once from an elevated prompt:

```powershell
New-NetFirewallRule -DisplayName "MX43 Simulator" -Direction Inbound -LocalPort 502 -Protocol TCP -Action Allow
```

## Project layout

```
src/
  Mx43Sim.Core/                pure .NET 10 library
    Domain/        Mx43Config, Zone, RelayModule, OnBoardRelay, Sensor, AlarmThresholds
    Cfg/           Mx43CfgParser (.cfg binary), AddressList (embedded JSON), MiniXlsx
    Modbus/        Mx43AddressMap, Mx43RegisterStore, Mx43ModbusServer
    Sim/           Mx43Simulator (measurement -> alarm bit derivation)
  Mx43Sim.App/                 WinForms (.NET 10 Windows)
    MainForm.cs    editor + grid + log
    Program.cs     STAThread entry point
  Mx43Sim.Tests/               console smoke + end-to-end tests

tools/
  regen_address_map.py   regenerate the embedded address map from Mx43 adresslista.xlsx

tests/
  fixtures/                  drop your .cfg files here (NOT committed)
  README.md                  explains the fixture convention

Modbus/
  mx43_address_map.json      embedded in Mx43Sim.Core.dll
```

## Register map (1-based Modbus addresses)

Per the *cahier des charges supervision_MX43_v2 GB*:

| Range | Meaning |
|---|---|
| 1..256     | Detector configuration (8 lines × 32 det, 68 regs each) |
| 257..264   | Analog channels 1..8 configuration |
| 2001..2256 | Detector measurements (signed 16-bit) |
| 2257..2264 | Analog channel measurements |
| 2301..2556 | Detector alarm bits (one word per detector) |
| 2557..2564 | Analog channel alarm bits |
| 2600..2603 | INFO: CRC32 + second counter |

Relay state is **not** exposed on the Modbus wire — slave displays do not show relays.

## Alarm bit layout (per the cahier)

| Bit | Meaning |
|---|---|
| 0 | Alarm 1 |
| 1 | Alarm 2 |
| 2 | Alarm 3 |
| 3 | Underscale |
| 4 | Overscale |
| 5 | Fault |
| 6 | Out of range |
| 7 | Non-ambiguity reading |

## Address map regeneration

The per-detector holding-register addresses are generated once from `Mx43 adresslista.xlsx` and embedded in the assembly as `Modbus/mx43_address_map.json`. Run the helper Python script if the address list ever changes:

```bash
pip install openpyxl
python3 tools/regen_address_map.py
```

## Test fixtures

`.cfg` files are intentionally **not** committed to this repository — they
typically contain real customer site configurations. Drop your own
files into `tests/fixtures/` to exercise the parser. See
[`tests/fixtures/README.md`](tests/fixtures/README.md).

## License

Internal PPM Industrial AB tool. Not for redistribution.
