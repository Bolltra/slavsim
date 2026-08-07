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
- `tags/mx43-tags.csv`: review manifest for MX43-side tags (`info-Dn`, `meas-Dn`, `alarm-Dn`).
- `tags/local-lw-tags.csv`: review manifest for local cMT LW tags.
- `tags/mx43-address-tag-library.csv`: EasyBuilder Address Tag Library rows for active MX43 tags, in the verified export format.
- `tags/local-lw-address-tag-library.csv`: EasyBuilder Address Tag Library rows for generated local cMT LW tags, in the verified export format.
- `macros/config-extractor_*.txt`: Weintek macro text that reads 68-register config blocks and stores label/range/unit/alarm/display-format fields in LW.
- `macros/runtime-sampler.txt`: Weintek macro text that samples live measurement and alarm bits.
- `macros/*.ebm`: directly importable EasyBuilder Pro 6.10.02 macro files with UTF-8 BOM, CRLF and execution metadata. Extractor IDs `5,7,8,9` are always emitted; unused ranges are disabled no-op replacements so stale template macros stop polling zero addresses.
- `*.generated.cxob`: only when `--template-cxob` is used.
- `*.template-report.md`: describes the template's available `info-Dn`, `ChN` and label slots.
- `*.warnings.txt`: patch notices and warnings. Expansion notices are informational; skipped, missing or truncated fields mean the `.cxob` is partial.

The `.cxob` output is a patch of an existing working template. It does not compile a new EasyBuilder project. It patches the stable structures mapped so far:

- Existing detector label-library entries `Det-1..Det-32` when present. EasyBuilder removes detached entries during compile, so referenced screen/local-LW objects remain the authoritative display-name path.
- Existing `info-Dn` MX43 config-tag addresses.
- Optional variable-length rebuilds of the detector label records and `ENHANCEDTAGS_L32` table when `--allow-binary-expansion` is used, including project block lengths, mapped relocation metadata and the macro/TAG_DATA link.

It intentionally does not add new EasyBuilder objects, trend objects, macros or tag-table entries inside the `.cxob` because the `project` payload contains offset references and a separate `script` payload that likely depends on EasyBuilder's compiler.

The `Start.cxob` layout uses `mt8000/project` and has 32 detector labels, 32 `ChN` tags and 32 `info-Dn` tags. With `--allow-binary-expansion`, the patcher can expand its short placeholder fields so digital config registers such as `33..48` and analog config registers `257..264` fit.

Rule of thumb: if EasyBuilder decompile is the goal, start without `--allow-binary-expansion` and use password `111111` when prompted by templates that require it. Repacking `Start.cxob` unchanged and the default length-preserving patch have both been verified to decompile. Corrected expanded digital and analog files have now also completed an EasyBuilder 6.10.02 decompile/full-compile round trip while preserving all active `info-Dn` addresses. Offline runtime behavior still requires validation because macros, objects and Data Sampling configuration are template-owned. Warnings that say a field could not be located, did not fit, or was skipped mean the `.cxob` should be treated as partial.

## CXOB Strategy

The uploaded `.cxob` file is a `tar.gz` archive containing a `project` payload, runtime binaries and Modbus drivers. The `project` payload contains readable strings, tag names, macros and SVG/XML fragments, but it is still a proprietary binary project representation.

The safest incremental path is:

1. Generate deterministic tags and macros from `.cfg`.
2. Import or paste those into an EasyBuilder Pro cMT template.
3. Compile the template to `.cxob` in EasyBuilder Pro.
4. Later, build a patcher that updates the decompiled/template `project` payload directly once the needed structures are mapped.

EasyBuilder Pro 6.10.02 Address Tag Library exports use six comma-separated fields without a header: `Name,Device,AddressKind,Address,,DataType`. The generator emits this verified export format with CRLF, a terminal CRLF and no BOM; partial-library import and duplicate-name behavior still require a manual EasyBuilder test. The existing `mx43-tags.csv` and `local-lw-tags.csv` remain richer review manifests because the import format cannot carry block/string lengths or comments. The received `used_addresses_list.xls` files are usage reports and not tag imports. EasyBuilder exports macros as plain-text `.ebm` files; the generator emits that verified format alongside review-friendly `.txt`. No documented command-line compiler for `.cmtp` -> `.cxob` was found; EasyBuilder Pro owns that supported step.

The verified binary structures and relocation rules are documented in `weintek-project-format.md`.

## Trends

Weintek supports data sampling and trend objects, and the existing test `.cxob` already contains trend-related windows and channel-selection macros. The generator emits stable direct measurement tags and local LW addresses that trend objects can sample.

The generated trend-channel table is a deterministic manifest for configuring Data Sampling and Trend Display objects. Decimal handling for trends should use the same `DisplayFormat` as live detector values.

The supplied Volvo Data Sampling export contains two raw MX43 `3x` groups. It samples bases `1` and `33` through EasyBuilder address indexes initialized to `2000`, producing effective measurement registers `2001..2003` and `2033..2048`. Its intervals, USB retention, folder names and filename policy are template-specific. The analog template has no Data Sampling object, so creating that policy and the actual trend object layout still belongs in EasyBuilder until these settings become explicit generator inputs.

## Alarm Severity

The generator emits `DetN-AlarmSeverity` as a local color-driving value:

- `0`: normal
- `1`: yellow / Alarm 1
- `2`: orange / Alarm 2 when three alarm levels exist
- `3`: red / highest configured alarm level, fault, underscale, overscale or out-of-range

For detectors with only Alarm 1 and Alarm 2 configured, Alarm 2 maps to severity `3`. This avoids the old workaround where Alarm 2 was duplicated into Alarm 3 just to make the Weintek object turn red. For detectors with all three alarm levels configured, Alarm 1/2/3 keep the existing yellow/orange/red behavior.

## Project Boundary

This should remain a separate console project under the same repository for now. It benefits from sharing the existing `.cfg` parser and Modbus address map, while keeping Weintek-specific generation out of the simulator UI and runtime.
