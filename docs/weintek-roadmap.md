# Weintek Generator Roadmap

## Near Term

- Validate imported generated EBM macros and offline simulator behavior for both digital and analog configurations; CXOB decompile/full-compile round trips now pass.
- Validate the generated EasyBuilder Address Tag Library CSV imports for both template families.
- Validate generated version 4 Data Sampling workbooks for digital, analog and mixed configurations.
- Export one Data Sampling example with a configured sync-status address before adding USB status monitoring; current generation intentionally preserves the verified `Off` setting.
- Add the one-time `Project-Title` Unicode object at `LW-3300` to the maintained common/header window.
- Drive object colors from generated `DetN-AlarmSeverity` instead of directly from Alarm 1/2/3 bits.
- Use `DisplayFormat` for all numeric objects: live value, range, thresholds and trend axes.
- Bind template Trend Display objects to generated Data Sampling groups in `IMPORT.md` order.

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

The newer `Start.cxob` template contains 32 detector label slots, 32 `ChN` tags and 32 `info-Dn` tags before normalization. The generator rebuilds its variable-length label and `ENHANCEDTAGS_L32` tables by default so short placeholders grow to fit digital and analog config addresses. EasyBuilder removes unreferenced `Det-N` labels during compile, while expanded active `info-Dn` addresses survive. `--preserve-binary-length` retains the old diagnostic mode but can fail strict active-tag validation.

## Longer Term

- Expand generated/patchable structures beyond labels and existing `info-Dn` tags once more EasyBuilder object records are mapped.
- Map and rebuild macro/script payloads so generated macros can be embedded directly in `.cxob` without EasyBuilder GUI import.
- Extend automated validation from the current planner, macro and synthetic relocation tests to anonymized binary template fixtures.
- Add anonymized fixtures for edge cases: O2 decimals, CO2 two decimals, analog channels, 1/2/3 alarm-level detectors, and more than 19 detectors.
- Consider a small Windows-side smoke test using EasyBuilder offline simulator if a CLI or automatable workflow is found.
