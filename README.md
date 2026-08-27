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

**Container frame: done and verified.** Chunk payload decoding: not started.

| | |
|---|---|
| corpus | **210 unique** `.igc` files (217 on disk; 7 are byte-identical cross-tree duplicates) |
| acceptance | **210/210 consumed exactly** — no trailing bytes, no overruns |
| wrappers | 99 static cores (`version`+`datasize`), 111 maps (`datasize`) |
| tests | 214 passing |

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

Detection walks each candidate wrapper and takes whichever lands exactly on
end-of-body, rather than guessing from the filename. A wrong wrapper
desynchronises the very first header, so this is unambiguous in practice.

## Not implemented

**No `Data*IGC` payload structs are decoded yet.** Records are located and
sized, not interpreted. Next: decode payloads one `ObjectType` at a time, in the
order the format page lists them, committing each.

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
