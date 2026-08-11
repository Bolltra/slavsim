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

## Current Generator

Run:

```bash
dotnet run --project src/Mx43Sim.WeintekGenerator/Mx43Sim.WeintekGenerator.csproj -- path/to/file.cfg -o out/weintek
```

The displayed HMI title defaults to the 16-character project name stored in the
CFG. Override it when needed:

```bash
--project-title "Customer Site"
```

Generation supports `1..32` detectors. Data Sampling is always emitted with
production defaults, which can be overridden from the CLI:

```bash
--storage-key "Customer-Site" --sampling-interval-ms 1000 --history-files 90 --sync-minutes 60
```

The storage key defaults to the CFG filename stem so projects whose internal
title is only `MX43` do not automatically share a USB folder name.
EasyBuilder limits are enforced: `100..7200000` ms, `1..65535` customized
files and `1..1440` synchronization minutes.

Optional `.cxob` import-template patch:

```bash
dotnet run --project src/Mx43Sim.WeintekGenerator/Mx43Sim.WeintekGenerator.csproj -- path/to/file.cfg -o out/weintek --template-cxob template.cxob --cxob-output out/weintek/patched-template.cxob
```

The default uses the variable-length rebuild that has passed digital and analog
EasyBuilder round trips. Every active `info-Dn` address is verified after the
patch; a missing or incorrect required tag fails generation.

The old conservative mode remains available for diagnostics, but may fail when
short template slots cannot hold the required addresses:

```bash
--preserve-binary-length
```

Generated output:

- `weintek-plan.json`: machine-readable detector and register plan.
- `detectors.csv`: detector order, MX43 Modbus addresses and cMT LW layout.
- `trend-channels.csv`: measurement/alarm channels intended for trend/data-sampling setup.
- `tags/mx43-tags.csv`: review manifest for MX43-side tags (`info-Dn`, `meas-Dn`, `alarm-Dn`).
- `tags/local-lw-tags.csv`: review manifest for local cMT LW tags.
- `tags/mx43-address-tag-library.csv`: EasyBuilder Address Tag Library rows for active MX43 tags, in the verified export format.
- `tags/local-lw-address-tag-library.csv`: EasyBuilder Address Tag Library rows for generated local cMT LW tags, in the verified export format.
- `data-sampling.xlsx`: version 4 EasyBuilder Data Sampling import covering every active detector.
- `macros/config-extractor_*.txt`: Weintek macro text that reads 68-register config blocks and stores label/range/unit/alarm/display-format fields in LW.
- `macros/runtime-sampler.txt`: Weintek macro text that samples live measurement and alarm bits.
- `macros/*.ebm`: EasyBuilder Pro 6.10.02 macro files in the verified export format. Extractor IDs `5,7,8,9` are always emitted; unused ranges are disabled no-op replacements so stale template macros stop polling zero addresses. ID `10` samples runtime values and alarms; ID `11` initializes the project title and Data Sampling index.
- `IMPORT.md`: deterministic EasyBuilder import order and acceptance checklist.
- `*.patched-template.cxob`: only when `--template-cxob` is used; never treat it as the deployable customer file.
- `*.template-report.md`: describes the template's available `info-Dn`, `ChN` and label slots.
- `*.warnings.txt`: patch notices and warnings. Expansion notices are informational; skipped, missing or truncated fields mean the `.cxob` is partial.

The `.cxob` output is a patch of an existing working template. It does not compile a new EasyBuilder project. It patches the stable structures mapped so far:

- Existing detector label-library entries `Det-1..Det-32` when present. EasyBuilder removes detached entries during compile, so referenced screen/local-LW objects remain the authoritative display-name path.
- Existing `info-Dn` MX43 config-tag addresses.
- Variable-length rebuilds of the detector label records and `ENHANCEDTAGS_L32` table by default, including project block lengths, mapped relocation metadata and the macro/TAG_DATA link.

It intentionally does not add new EasyBuilder objects, trend objects, macros or tag-table entries inside the `.cxob` because the `project` payload contains offset references and a separate `script` payload that likely depends on EasyBuilder's compiler.

The `Start.cxob` layout uses `mt8000/project` and has 32 detector labels, 32 `ChN` tags and 32 `info-Dn` tags. The patcher expands its short placeholder fields so digital config registers such as `33..48` and analog config registers `257..264` fit.

The known sample templates use password `111111`; arbitrary templates may use another password. Corrected expanded digital and analog files have completed an EasyBuilder 6.10.02 decompile/full-compile round trip while preserving all active `info-Dn` addresses. Tags, generated macros, Data Sampling and the title object still require import/template wiring before EasyBuilder creates the final customer CXOB. Offline runtime behavior remains an acceptance test rather than an automated Linux test.

## CXOB Strategy

The uploaded `.cxob` file is a `tar.gz` archive containing a `project` payload, runtime binaries and Modbus drivers. The `project` payload contains readable strings, tag names, macros and SVG/XML fragments, but it is still a proprietary binary project representation.

The safest incremental path is:

1. Generate deterministic tags, macros, title initialization and Data Sampling from `.cfg`.
2. Follow generated `IMPORT.md` to assemble those artifacts in either the generated patched template or a separately maintained EasyBuilder template.
3. Full compile the final customer `.cxob` in EasyBuilder Pro.
4. Later, build a patcher that updates the decompiled/template `project` payload directly once the needed structures are mapped.

EasyBuilder Pro 6.10.02 Address Tag Library exports use six comma-separated fields without a header: `Name,Device,AddressKind,Address,,DataType`. The generator emits this verified export format with CRLF, a terminal CRLF and no BOM; partial-library import and duplicate-name behavior still require a manual EasyBuilder test. The existing `mx43-tags.csv` and `local-lw-tags.csv` remain richer review manifests because the import format cannot carry block/string lengths or comments. The received `used_addresses_list.xls` files are usage reports and not tag imports. EasyBuilder exports macros as plain-text `.ebm` files; the generator emits that verified format alongside review-friendly `.txt`. No documented command-line compiler for `.cmtp` -> `.cxob` was found; EasyBuilder Pro owns that supported step.

The verified binary structures and relocation rules are documented in `weintek-project-format.md`.

## Trends

Weintek supports data sampling and trend objects, and the existing test `.cxob` already contains trend-related windows and channel-selection macros. The generator emits stable direct measurement tags and local LW addresses that trend objects can sample.

The generated trend-channel table is a deterministic manifest for configuring Data Sampling and Trend Display objects. Decimal handling for trends should use the same `DisplayFormat` as live detector values.

The generated version 4 workbook groups contiguous MX43 configuration addresses and applies `IDX: 1`; startup macro ID `11` writes `2000` to `LW-9201`, so those bases resolve to the corresponding live measurement registers. Defaults are one-second sampling, USB synchronization every 60 minutes and preservation of 90 customized files. EasyBuilder labels the shared field `day(s)/file(s)`, but Automatic Customized File mode uses a file count, not a day count. Stable folder/file slugs include a deterministic storage-key hash and configuration base, and custom filenames are capped at EasyBuilder's 25-character limit. The original Volvo export established this schema; generated digital, analog and mixed workbooks still require one EasyBuilder import/full-compile acceptance round.

With no USB inserted, live HMI operation continues but history buffering is
finite. EasyBuilder documents roughly 9000 disconnected-period records per
sampling group before older data can be discarded; at the one-second default
this is about 2.5 hours. The verified export has `Sync Status Address: Off`, so
the generator does not invent an unverified monitoring address.

Trend Display objects are proprietary screen objects and are not generated. A
maintained template must bind them to the imported Data Sampling groups in the
order listed in `IMPORT.md`.

## Project Title

Macro ID `11` writes the resolved 16-word Unicode title to `LW-3300..3315` and
initializes Data Sampling index `LW-9201`. The maintained EasyBuilder template
needs one read-only Unicode ASCII display bound to `LW-3300`, length 16 words,
on its common/header window. This one-time object wiring replaces static
`Customer` or `E-Fabrik` headings.

## Alarm Severity

The generator emits `DetN-AlarmSeverity` as a local color-driving value:

- `0`: normal
- `1`: yellow / Alarm 1
- `2`: orange / Alarm 2 when three alarm levels exist
- `3`: red / highest configured alarm level, fault, underscale, overscale or out-of-range

For detectors with only Alarm 1 and Alarm 2 configured, Alarm 2 maps to severity `3`. This avoids the old workaround where Alarm 2 was duplicated into Alarm 3 just to make the Weintek object turn red. For detectors with all three alarm levels configured, Alarm 1/2/3 keep the existing yellow/orange/red behavior.

## Project Boundary

This remains a separate console project under the same repository. GUI integration is intentionally deferred; the generator is not currently included in the simulator application or release executable.
