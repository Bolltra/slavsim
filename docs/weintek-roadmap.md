# Weintek Generator Roadmap

## Near Term

- Validate both `Start.cxob` patch modes in EasyBuilder/offline simulator: length-preserving default for decompile, and `--allow-binary-expansion` for complete labels/addresses.
- Drive object colors from generated `DetN-AlarmSeverity` instead of directly from Alarm 1/2/3 bits.
- Use `DisplayFormat` for all numeric objects: live value, range, thresholds and trend axes.
- Use `trend-channels.csv` to configure a consistent trend/data-sampling page.
- Keep the conservative `.cxob` patcher limited to mapped structures; fail loudly with warnings otherwise.

## Alarm Colors

The display should treat the highest configured alarm level as red:

- One configured alarm: Alarm 1 is the highest severity.
- Two configured alarms: Alarm 2 is red/high severity.
- Three configured alarms: Alarm 1/2/3 remain yellow/orange/red.

This avoids duplicating Alarm 2 into Alarm 3 just to force a red visual state.

## Template Requirements

The older sample `.cxob` template contains:

- 32 detector label slots.
- 32 `ChN` value/trend tags.
- 19 `info-Dn` config-block tags.
- Short fixed-width address fields for some `info-Dn` tags.

The newer `Start.cxob` template contains 32 detector label slots, 32 `ChN` tags and 32 `info-Dn` tags. The generator can rebuild its variable-length detector label records and `ENHANCEDTAGS_L32` table so short placeholders grow to fit digital and analog config addresses when `--allow-binary-expansion` is enabled. The default mode preserves project payload length for password-protected decompile compatibility.

## Longer Term

- Expand generated/patchable structures beyond labels and existing `info-Dn` tags once more EasyBuilder object records are mapped.
- Map and rebuild macro/script payloads so generated macros can be embedded directly in `.cxob` without EasyBuilder GUI import.
- Add automated validation that compares generated tag/register plans against `Mx43AddressMap` and parsed `.cfg` fixtures.
- Add anonymized fixtures for edge cases: O2 decimals, CO2 two decimals, analog channels, 1/2/3 alarm-level detectors, and more than 19 detectors.
- Consider a small Windows-side smoke test using EasyBuilder offline simulator if a CLI or automatable workflow is found.
