# Weintek Generator Roadmap

## Near Term

- Validate imported generated EBM macros and offline simulator behavior for both digital and analog configurations; CXOB decompile/full-compile round trips now pass.
- Drive object colors from generated `DetN-AlarmSeverity` instead of directly from Alarm 1/2/3 bits.
- Use `DisplayFormat` for all numeric objects: live value, range, thresholds and trend axes.
- Use `trend-channels.csv` to configure a consistent trend/data-sampling page.
- Export representative Address Tag Library CSV files from the exact production EasyBuilder/template version and build versioned import adapters; EBM macro generation is now implemented from verified exports.

## Alarm Colors

The display should treat the highest configured alarm level as red:

- One configured alarm: Alarm 1 is the highest severity.
- Two configured alarms: Alarm 2 is red/high severity.
- Three configured alarms: Alarm 1/2/3 remain yellow/orange/red.

This avoids duplicating Alarm 2 into Alarm 3 just to force a red visual state.

## Template Requirements

The older sample `.cxob` template contains before EasyBuilder normalization:

- 32 detector label slots.
- 32 `ChN` value/trend tags.
- 19 `info-Dn` config-block tags.
- Short fixed-width address fields for some `info-Dn` tags.

The newer `Start.cxob` template contains 32 detector label slots, 32 `ChN` tags and 32 `info-Dn` tags before normalization. The generator can rebuild its variable-length label and `ENHANCEDTAGS_L32` tables so short placeholders grow to fit digital and analog config addresses when `--allow-binary-expansion` is enabled. EasyBuilder removes unreferenced `Det-N` labels during compile, while expanded active `info-Dn` addresses survive. The default mode preserves project payload length for password-protected decompile compatibility.

## Longer Term

- Expand generated/patchable structures beyond labels and existing `info-Dn` tags once more EasyBuilder object records are mapped.
- Map and rebuild macro/script payloads so generated macros can be embedded directly in `.cxob` without EasyBuilder GUI import.
- Extend automated validation from the current planner, macro and synthetic relocation tests to anonymized binary template fixtures.
- Add anonymized fixtures for edge cases: O2 decimals, CO2 two decimals, analog channels, 1/2/3 alarm-level detectors, and more than 19 detectors.
- Consider a small Windows-side smoke test using EasyBuilder offline simulator if a CLI or automatable workflow is found.
