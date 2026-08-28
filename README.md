# AllegianceCore

C# port of the Allegiance simulation data layer. Server-side only — no
rendering, no networking, no COM.

Scope decision: **headless server + bots first, no render client.**

## Build & test

```bash
export DOTNET_ROOT="$HOME/.dotnet"     # NOT /snap/bin/dotnet, which is a broken stub
export PATH="$DOTNET_ROOT:$PATH"
dotnet build
dotnet test
```

SDK 10.0.400. `dotnet --version` must print `10.0.400`; if you see
"Looks like you don't yet have a .NET SDK", your `PATH` is wrong.

```bash
dotnet run --project tools/igcdump -- /home/domingo/Development/Artwork/PCore001.igc
```

## Status

**Container frame: done and verified. Payload decoding: in progress.**

Four payload structs decoded and corpus-verified; the rest remain.

| | |
|---|---|
| corpus | **210 unique** `.igc` files (217 on disk; 7 are byte-identical cross-tree duplicates) |
| acceptance | **210/210 consumed exactly** — no trailing bytes, no overruns |
| wrappers | 99 static cores (`version`+`datasize`), 111 maps (`datasize`) |
| tests | 218 passing |

### The acceptance property

There is no Windows here to diff against the original writer, so the oracle is
**exact byte consumption**: for every file the record stream must land precisely
on end-of-body. A wrong field width or offset anywhere desynchronises everything
after it, so this single property catches nearly every layout error without a
reference implementation.

It was first established by `../allegiance-tools/scan_igc_corpus.py`; this
library reproduces it.

## Implemented

- `IgcObjectType` — the 24 `ObjectType` values (`src/Igc/igc.h:114-160`)
- `IgcRecord` — the 6-byte header: `int16 type`, `int32 size` (payload excludes header)
- `IgcReader` — wrapper detection + record-stream walk
- `IgcFile` — records, wrapper form, version, exactness

**Payload decoders** (`src/Allegiance.Core/Data/`), each a `readonly record
struct` with a `Decode(ReadOnlySpan<byte>)` that throws on a wrong record size:

| type | struct | size | corpus count |
|---|---|---|---|
| 22 `OT_projectileType` | `DataProjectileTypeIGC` | 88 | 9369 |
| 27 `OT_civilization` | `DataCivilizationIGC` | 268 | 952 |
| 29 `OT_hullType` | `DataHullTypeIGC` | 540 + 30·maxWeapons | 38927 |
| 32 `OT_development` | `DataDevelopmentIGC` | 468 | 28500 |

Detection walks each candidate wrapper and takes whichever lands exactly on
end-of-body, rather than guessing from the filename. A wrong wrapper
desynchronises the very first header, so this is unambiguous in practice.

### Corpus-measured corrections to the wiki table

The wiki's `allegiance-igc-file-format` page carries a ⭐ corrections section,
but two further field-level discrepancies surfaced while decoding (neither is
in the wiki's own corrections list; both are already reflected in the decoders):

1. **`DataProjectileTypeIGC.width` is at offset 80, not 78.** After
   `bDirectional` at 76 the float needs 4-byte alignment, so there are three pad
   bytes 77-79. Verified: `width`@80 is a clean float in all 9369 records and
   `ambientSound` is `-1` in every one.
2. **`DataCivilizationIGC` has a pad byte at 159** pushing `gasBaseAttributes`
   to 160-259; the trailing `lifepod`/`civilizationID`/`initialStationTypeID`
   sit at 260/262/264 (the wiki's draft said gas@159 and IDs at 259/261/263).
   Verified: every `lifepod` and `initialStationTypeID` decodes to a real
   hull/station-type ID present in the same core.

## Not implemented

Remaining `Data*IGC` payload structs are not decoded yet: missile/mine/probe/
chaff type (23/24/25/26), part type incl. the seven subtypes (30), station type
(31), drone type (33), and the map-file objects (ship 0, station 1, asteroid 5,
warp 7, treasure 8, cluster 13). Decode them one `ObjectType` at a time,
committing each.

Layouts are specified in the wiki page `allegiance-igc-file-format`.
⚠️ That page carries a **corpus-measured corrections** section; where it
contradicts an earlier section, the corrections win.

## Facts worth keeping

- **Plaintext on disk.** The `munge` decrypt hook is wired only for the Zone
  Club download path; `ALLSRV_STANDALONE` passes NULL
  (`src/FedSrv/fsmission.cpp:164-166`). No decryptor needed.
- **Hull stride is 30**, not 29: `540 + 30 * maxWeapons`. Sizes observed are
  540/570/600/…/780, every delta exactly 30.
- **The version word is a Unix timestamp** — `PCore001.igc` carries
  `990051329` = 2001-05-17, matching
  `iStaticCoreVersion = static_cast<...>(time(0))` (`missionigc.cpp:2363`).
- **Artwork lives in two trees.** A search scoped to the code repo alone finds
  almost none of the corpus.
