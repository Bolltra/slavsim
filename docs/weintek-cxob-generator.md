# Weintek cMT/CXOB Generator

This branch starts a side project for generating Weintek cMT screen-programming artifacts from an MX43 `.cfg` file.

The intent is to keep the existing simulator as the source of truth for parsing MX43 programming, then reuse that parsed model to generate Weintek tags, macros and eventually a runnable `.cxob` template.

## Why Generate From `.cfg`

The MX43 `.cfg` already contains the detector order, labels, gas names, units, ranges, alarm thresholds, line/detector mapping and display format. The existing simulator parser exposes those fields through `Mx43Config` and `Sensor`.

The important decimal field is `Sensor.DisplayFormat`, read from offset `+0x2A` in the sensor record and exposed in the virtual MX43 Modbus configuration block at offset `+38`.

Observed examples:

- `DisplayFormat = 0`: integer values, for example `%LEL` or `ppm`.
- `DisplayFormat = 1`: one decimal, for example O2 `190` displayed as `19.0`.
- `DisplayFormat = 2`: two decimals, seen on `%VOL` CO2 style channels.

## Current Prototype

Run:

```bash
dotnet run --project src/Mx43Sim.WeintekGenerator/Mx43Sim.WeintekGenerator.csproj -- path/to/file.cfg -o out/weintek
```

Optional conservative `.cxob` template patch:

```bash
dotnet run --project src/Mx43Sim.WeintekGenerator/Mx43Sim.WeintekGenerator.csproj -- path/to/file.cfg -o out/weintek --template-cxob template.cxob --cxob-output out/weintek/generated.cxob
```

By default, the `.cxob` patcher preserves the original `project` payload length. This is safer for password-protected EasyBuilder decompile flows, but fields that are too short are truncated or skipped with warnings.

To allow variable-length binary section rebuilds, add:

```bash
--allow-binary-expansion
```

Generated output:

- `weintek-plan.json`: machine-readable detector and register plan.
- `detectors.csv`: detector order, MX43 Modbus addresses and cMT LW layout.
- `trend-channels.csv`: measurement/alarm channels intended for trend/data-sampling setup.
- `tags/mx43-tags.csv`: MX43-side tags (`info-Dn`, `meas-Dn`, `alarm-Dn`).
- `tags/local-lw-tags.csv`: local cMT LW tags.
- `macros/config-extractor_*.txt`: Weintek macro text that reads 68-register config blocks and stores label/range/unit/alarm/display-format fields in LW.
- `macros/runtime-sampler.txt`: Weintek macro text that samples live measurement and alarm bits.
- `*.generated.cxob`: only when `--template-cxob` is used.
- `*.template-report.md`: describes the template's available `info-Dn`, `ChN` and label slots.
- `*.warnings.txt`: patch notices and warnings. Expansion notices are informational; skipped, missing or truncated fields mean the `.cxob` is partial.

The `.cxob` output is a patch of an existing working template. It does not compile a new EasyBuilder project. It patches the stable structures mapped so far:

- Detector label-library entries `Det-1..Det-32`.
- Existing `info-Dn` MX43 config-tag addresses.
- Optional variable-length rebuilds of the detector label records and `ENHANCEDTAGS_L32` table when `--allow-binary-expansion` is used, including project block lengths, mapped relocation metadata and the macro/TAG_DATA link.

It intentionally does not add new EasyBuilder objects, trend objects, macros or tag-table entries inside the `.cxob` because the `project` payload contains offset references and a separate `script` payload that likely depends on EasyBuilder's compiler.

The `Start.cxob` layout uses `mt8000/project` and has 32 detector labels, 32 `ChN` tags and 32 `info-Dn` tags. With `--allow-binary-expansion`, the patcher can expand its short placeholder fields so digital config registers such as `33..48` and analog config registers `257..264` fit.

Rule of thumb: if EasyBuilder decompile is the goal, start without `--allow-binary-expansion` and use password `111111` when prompted by templates that require it. Repacking `Start.cxob` unchanged and the default length-preserving patch have both been verified to decompile. Earlier expanded files failed with an EasyBuilder password error and were subsequently confirmed to have inconsistent project block lengths and internal relocation pointers; the generator now updates and validates those mapped structures. Expanded output still requires a new EasyBuilder decompile/offline-simulator check because the complete proprietary format is not documented. Warnings that say a field could not be located, did not fit, or was skipped mean the `.cxob` should be treated as partial.

## CXOB Strategy

The uploaded `.cxob` file is a `tar.gz` archive containing a `project` payload, runtime binaries and Modbus drivers. The `project` payload contains readable strings, tag names, macros and SVG/XML fragments, but it is still a proprietary binary project representation.

The safest incremental path is:

1. Generate deterministic tags and macros from `.cfg`.
2. Import or paste those into an EasyBuilder Pro cMT template.
3. Compile the template to `.cxob` in EasyBuilder Pro.
4. Later, build a patcher that updates the decompiled/template `project` payload directly once the needed structures are mapped.

Public documentation shows import/export support for Address Tag Library CSV/Excel, Label Tag Library CSV/Excel/`.lbl`, and macro import/export (`.edm`/macro libraries). The exact Address Tag CSV columns are driver- and version-dependent, so the generated tag CSV files are manifests until matched to a representative export from the target template. Macro Manager imports `.edm`; generated `.txt` files are source for review/paste unless wrapped in an EasyBuilder-exported EDM template. No documented command-line compiler for `.cmtp` -> `.cxob` was found; EasyBuilder Pro owns that supported step.

The verified binary structures and relocation rules are documented in `weintek-project-format.md`.

## Trends

Weintek supports data sampling and trend objects, and the existing test `.cxob` already contains trend-related windows and channel-selection macros. The generator currently emits stable measurement tags and LW addresses that trend objects can sample.

The generated trend-channel table is a deterministic manifest for configuring Data Sampling and Trend Display objects. Decimal handling for trends should use the same `DisplayFormat` as live detector values.

Weintek Trend Display supports sampling channels from Data Sampling objects. The generator can therefore prepare stable per-detector measurement tags, but creating the actual trend object layout still belongs in the EasyBuilder template until the `project` object structures are fully mapped.

## Alarm Severity

The generator emits `DetN-AlarmSeverity` as a local color-driving value:

- `0`: normal
- `1`: yellow / Alarm 1
- `2`: orange / Alarm 2 when three alarm levels exist
- `3`: red / highest configured alarm level, fault, underscale, overscale or out-of-range

For detectors with only Alarm 1 and Alarm 2 configured, Alarm 2 maps to severity `3`. This avoids the old workaround where Alarm 2 was duplicated into Alarm 3 just to make the Weintek object turn red. For detectors with all three alarm levels configured, Alarm 1/2/3 keep the existing yellow/orange/red behavior.

## Project Boundary

This should remain a separate console project under the same repository for now. It benefits from sharing the existing `.cfg` parser and Modbus address map, while keeping Weintek-specific generation out of the simulator UI and runtime.
