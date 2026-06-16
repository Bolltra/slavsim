# Test fixtures

The smoke tests in `src/Mx43Sim.Tests` look here for `.cfg` files to
exercise the parser against. Drop the project-specific MX43 `.cfg`
exports into this folder before running `dotnet test`.

**These files are intentionally NOT committed to git** — they typically
contain real customer site configurations (sensor labels, zone names,
relay assignments) that are confidential to PPM Industrial.

## Suggested files

A typical regression set covers at least one of each:

| Scenario | Example |
|---|---|
| Few digital sensors | `ppm1.cfg` (7× CH4 %LEL) |
| Mixed gases | `2024-09-05_Emhart.cfg` (H2 / O2 / CO) |
| Many sensors + free zones | `Config scania.cfg` (28 sensors) |
| Internal extension modules | `Projekt Gotland elnät G15.cfg` (L1M17 + L1M18) |

## Running the tests

```bash
dotnet run --project src/Mx43Sim.Tests
```

The test runner will:

1. Scan this folder for `*.cfg` and parse every one.
2. Validate the address map against the embedded JSON.
3. Spin up a real Modbus TCP server on an ephemeral port and
   round-trip an FC3 read for measurement register 2001.
4. Verify the simulator derives correct alarm bits at every
   threshold crossing.
