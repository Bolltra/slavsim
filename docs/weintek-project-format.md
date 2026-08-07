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
LABE_LIB
uint16 LE label record count
label records
ENHANCEDTAGS_L32
enhanced tag records
MACRO_ID
macro source and metadata
uint32 LE pointer to MACRO_ID
TAG_DATA
```

The label library is:

```text
ASCII "LABE_LIB"
uint16 LE recordCount
recordCount * {
byte keyLength
byte valueLength
byte suffixLength
keyLength bytes key
valueLength bytes value (the generator writes UTF-8; non-ASCII behavior still
requires EasyBuilder validation)
suffixLength bytes opaque suffix
}
```

The original templates had 49 records, with `Det-1..Det-32` as the final 32
entries. EasyBuilder 6.10.02 rebuilt the libraries to 4 and 20 referenced records
during the verified compile round trips and removed every detached `Det-N`
entry. Structural validation therefore covers the complete counted library;
detector-label patching is only available when exact `Det-N` keys exist and is
not a reliable way to drive screen text unless objects reference those keys.

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

## Address Tag Library CSV

EasyBuilder Pro 6.10.02.300 exports Address Tag Library CSV rows as:

```text
Name,Device,AddressKind,Address,,DataType\r\n
```

The verified files have no header or BOM, use CRLF throughout and end with a
final CRLF. The fifth field is empty. Examples are
`info-D1,MX43,3x,257,,16-bit Signed` and
`Det1-Name,cMT,LW,100,,16-bit Unsigned`.

The generator emits active `info-Dn`, `meas-Dn` and `alarm-Dn` rows separately
from generated local cMT LW rows. It does not emit template-owned UAC, `ChN`,
selector or inactive address-zero placeholders. Existing richer CSV manifests
remain because this import format does not encode block/string lengths or
comments.

## Data Sampling Export

The verified `.xlsx` is a version 4 EasyBuilder Data Sampling settings export,
not historical sampled data. It contains two Volvo-specific raw `MX43`/`3x`
groups at bases `1` and `33`. Address indexes initialized to `2000` make their
effective measurement ranges `2001..2003` and `2033..2048`. Sampling intervals,
storage destination, retention, folder names and filename policy are project
settings that cannot be derived safely from an MX43 CFG alone.

## Remaining Unknowns

- Complete object/window serialization.
- Compiled `script` encoding and macro execution metadata.
- Data Sampling and Trend Display records inside the proprietary project payload.
- Integrity or checksum fields beyond gzip/tar checksums.
- Whether all EasyBuilder versions use the same mapped record layouts.
- A supported non-interactive EasyBuilder compile interface.

These unknowns are why post-patch structural validation is necessary but not
sufficient for a production release. EasyBuilder remains the final compiler and
validator.

## Verified Round Trip

Both corrected expanded outputs were decompiled and fully recompiled with
EasyBuilder Pro 6.10.02.300. The recompiled projects retained:

- Digital `info-D1..D19` addresses `1,2,3,33..48`.
- Analog `info-D1..D8` addresses `257..264`.
- Enhanced-tag record counts and macro/TAG_DATA structural links.

EasyBuilder minimized unused `info-D` address fields and rebuilt both label
libraries, which the parser now accepts. This validates enhanced-tag expansion
and the mapped relocation rules for the two template families, but not runtime
screen wiring, generated macro execution or trend behavior.
