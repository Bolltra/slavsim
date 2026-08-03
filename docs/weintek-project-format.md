# Weintek Project Format Notes

This document separates observed format rules from assumptions. EasyBuilder Pro's
`.cmtp` and compiled `project` formats are proprietary; only behavior reproduced
across the available files is treated as verified here.

## Supported Production Path

The vendor-supported path remains:

1. Maintain an editable `.cmtp` template in the same EasyBuilder Pro version used
   for production.
2. Import or paste generated tags and macro source into that template.
3. Perform a full EasyBuilder Pro compile to `.cxob`.
4. Check compiler diagnostics, offline simulation and a decompile round trip with
   the intended CXOB password.

No documented command-line `.cmtp` compiler or project-authoring API has been
found. Direct `.cxob` patching therefore remains an experimental, validated
optimization rather than the source of truth.

## CXOB Container

Verified against both available template families:

- A `.cxob` is a gzip-compressed tar archive.
- Older cMT archives store the payload at `mt8000/project`; newer archives use
  `project` at the archive root.
- The separate `script` entry is opaque compiled macro material. It is preserved
  byte-for-byte by the patcher.
- Repacking changes tar metadata and ordering but an unchanged repack has still
  decompiled successfully. Logical entry content is therefore more important
  than byte-identical tar metadata for the tested templates.

## Project Header

The compiled project begins with:

| Offset | Type | Meaning |
|---:|---|---|
| `0x00` | 12 ASCII bytes | `MT8000Series` |
| `0x0C` | `uint32 LE` | block A length |
| `0x10` | `uint32 LE` | block B length |
| `0x14` | bytes | block A followed by block B |

The verified size invariant is:

```text
20 + blockALength + blockBLength == projectPayloadLength
```

The first record in block B starts with its own `uint32 LE` byte length and
contains absolute offsets into the later project data. Any variable-length edit
inside block B must update both `blockBLength` and affected pointers in this
metadata record.

## Mapped Sections

The mapped sections occur in this order:

```text
LABE_LIB1
detector label records
ENHANCEDTAGS_L32
enhanced tag records
MACRO_ID
macro source and metadata
uint32 LE pointer to MACRO_ID
TAG_DATA
```

The detector-label region contains 32 sequential records:

```text
byte keyLength
byte valueLength
byte suffixLength
keyLength bytes key
valueLength bytes value (the generator writes UTF-8; non-ASCII behavior still
requires EasyBuilder validation)
suffixLength bytes opaque suffix
```

The enhanced-tag table is:

```text
ASCII "ENHANCEDTAGS_L32"
uint32 LE recordCount
recordCount * {
    byte flag
    byte class
    byte kind
    byte nameFieldLength
    byte addressFieldLength
    nameFieldLength bytes NUL-terminated name
    addressFieldLength bytes NUL-terminated address
}
```

The generator requires these mapped sections to parse completely and end at the
next expected marker. It rejects expansion if the project header, section order,
record boundaries or macro/TAG_DATA pointer are inconsistent.

## Relocation Rules

When a mapped label or enhanced-tag section grows, the generator now:

1. Updates block B's declared length.
2. Adjusts only pointer-like values in block B's first metadata record that
   target the moved project tail.
3. Updates the verified pointer immediately before `TAG_DATA` to the relocated
   `MACRO_ID` marker.
4. Reparses and validates the resulting structure before writing a CXOB.

The previous implementation globally replaced every matching 32-bit integer.
That was unsafe and also missed pointers into a moved section, such as
`TAG_DATA + 0x0C`.

## CMTP Observations

The available `.cmtp` starts with a small versioned framing header followed by a
16-byte-aligned opaque payload. Repeated aligned blocks indicate deterministic
block encoding or encryption. Its serialization and protection scheme are not
mapped, so this project does not attempt to generate or modify `.cmtp` directly.

## Remaining Unknowns

- Complete object/window serialization.
- Compiled `script` encoding and macro execution metadata.
- Data Sampling and Trend Display object records.
- Integrity or checksum fields beyond gzip/tar checksums.
- Whether all EasyBuilder versions use the same mapped record layouts.
- A supported non-interactive EasyBuilder compile interface.

These unknowns are why post-patch structural validation is necessary but not
sufficient for a production release. EasyBuilder remains the final compiler and
validator.
