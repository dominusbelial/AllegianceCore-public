# core2txt — native static-core text round trip (P870/P870b)

A `tools/` dev command that ports the native FreeAllegiance `Core2Text` /
`Text2Core` operators (`AllSrv -Core2Text` / `-Text2Core`,
`FedSrv/FedSrv.CPP:10385-10480`) to a portable .NET CLI. It converts a static
core `.igc` into editable tab-delimited text (one CSV per native table, with the
native headers, comments, column order and `%g` float spelling) and back into a
`.igc`. Useful for modding and the planned new-races expansion.

## Build and register

    tools/build-tools.sh                              # every tools/*/*.csproj
    ~/.dotnet/dotnet build tools/core2txt/core2txt.csproj
    # also listed in AllegianceCore.slnx under /tools/

## Usage

    core2txt Core2Text <core.igc> [outDir]     # .igc -> text folder
    core2txt Text2Core  <textDir> [out.igc]    # text folder -> .igc
    core2txt SelfTest   <core.igc> ...         # byte-identity check per core
    core2txt SelfTest   --all [artworkDir]     # every static core in the corpus

`Core2Text` mirrors native: the folder (default `<core>.txt`) holds one CSV per
object table — `Constants`, `GlobalAttributes`, `ShipsSFX`, `StationsSFX`,
`TypesOfStations`, `TypesOfProjectiles`, `ExpendableMissiles`, `ExpendableMines`,
`ExpendableChaff`, `ExpendableProbes`, `Expendables`, `Weapons`, `Shields`,
`Cloaks`, `Afterburners`, `Packs`, `TypesOfShips`, `Hardpoints`, `Developments`,
`TypesOfDrones`, `Civilizations`, `TypesOfTreasures`. As in native, an optional
`BitsOfTechs.csv` / `BitsOfParts.csv` placed in that folder switches tech/part
masks from raw hex/decimal to the named form (and back).

## Round-trip fidelity and the `.coremeta` sidecar

`igc → text → igc` is byte-identical for all 100 shipped static cores (verified by
`SelfTest --all`). Native itself cannot do that, for three reasons this tool
handles explicitly:

1. **Version word.** `DumpIGCStaticCore` stamps a fresh `time(0)` version each run
   (`missionigc.cpp:2358`). `.coremeta` records the original version word (and the
   exact record-type/id sequence) so `Text2Core` reproduces it. A foreign folder
   without the sidecar imports in native's canonical order with a fresh stamp.
2. **Uninitialised struct memory.** `ImportStaticIGCObjs` allocates payloads with
   `new char[]`, so C++ struct padding and the bytes after a string's NUL keep
   whatever the heap held; the shipped cores carry `0xCC`/`0xCD` there. The codecs
   mark every payload byte that a text column determines and `.coremeta` stores the
   rest as a sparse `offset:HH` list (rendered as exactly the bytes native cannot
   reproduce). A foreign text folder gets zeros in those bytes.
3. **Floats.** `ZString(float)` is `sprintf("%g")` (6 significant digits). The
   codecs use that spelling whenever it parses back to the identical float and an
   exact shortest-round-trip spelling otherwise, so no value loses precision while
   the text stays native-shaped for normal data.

The text remains byte-compatible with native's format: native can read a folder
this tool writes, and this tool reads a folder native writes. Where native's own
writer and reader disagree the reader follows the writer/header so the round trip
is lossless:

- booleans are written `true`/`false` (`ZString(bool)`) and read as booleans,
  where native's reader runs them through `atoi` and always gets 0;
- colour channels are written as floats (`ZString(float)`) and read as floats,
  where native's reader uses `GetInteger()`;
- `PartMask` is a signed short, so its decimal spelling can be negative (e.g.
  `-32768` for a lone `c_pbm16`); the reader accepts a leading sign;
- `CanRescue` / `CanRescueAny` (expendable abilities) prefix-overlap in native, so
  native's reader sets both bits from the writer's `"CanRescueAny"`; the port
  resolves each token to its longest exact name;
- `TypesOfShips`' `ALLOWED MISSILES` / `ALLOWED DISPENSERS` columns are written in
  the header/reader order (magazine then dispenser), where native's writer swaps
  them;
- `GlobalAttributes.csv` has no owning-type column and development/civilization
  ids overlap, so native's development reader can consume a civilization row; the
  port matches the row's `COPY OF NAME`;
- a CRLF file leaves `\r` in native's last field (corrupting the final mask), so
  the importer treats it as the line terminator.

Fields native never takes from the text (hull `pmEquipment[ET_Weapon]`/
`[ET_Pack]`, the `DataObjectIGC.radius` column of a mine) are deliberately not
text-covered: a round trip keeps the original bytes, a foreign folder gets zeros
(native's reader zeroes the hull masks and ignores the mine column).

## Layout

- `CoreTable.cs` — the shared native helpers (`TechBitFromToken`,
  `TechsListToBits`, `PartMaskFromToken`, `PartsListToMask`, `BitsToTechsList`,
  `BitsToPartsList`, `LoadTechBitsList`, `LoadPartsBitsList`, `readCSV`,
  `AbilitiesListToMask`, the damage/global constant comments, `%g` formatting).
  Masks follow `TLargeBitMask` (`zlib/mask.h:167-212`): MSB-first bits, uppercase
  `%02X`, odd-length hex rejected.
- `TypeCodec.cs` — the codec interface, registry, and the verbatim native file
  headers/order (`CoreFiles`).
- `TextCore.cs` — row parsing, the `PayloadBuilder` (tracks text-determined
  bytes), and the whole-core catalog for the projectile "copy of names" column
  and launcher definitions.
- `codecs/` — one codec per object type (constants, projectile, missile/mine/
  chaff/probe, all six part files, hull + hardpoints, station, development,
  drone, civilization, treasure set).
- `StaticCoreRoundtrip.cs` — export/import orchestration + `.coremeta`.
- `StaticCoreVerify.cs` — per-core byte-identity checker and the static-core
  corpus enumerator.
