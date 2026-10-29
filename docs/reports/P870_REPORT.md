# P870 — port native Core2Text/Text2Core as a dev tool; certify the static-core loader rows

## Summary

Built the `tools/core2txt` dev tool (a `tools/` CLI, auto-registered with
`tools/build-tools.sh`) that ports the native `Core2Text`/`Text2Core` static-core
text round trip (`FedSrv/FedSrv.CPP:10385-10480`) to portable .NET: `.igc` →
editable tab-delimited text and text → `.igc`. The shared authoring helpers —
every table's token↔mask conversion, ability masks, the `BitsOfTechs.csv` /
`BitsOfParts.csv` loaders, tab-CSV reading and the constant-comment lookups —
are implemented in `tools/core2txt/CoreTable.cs` and certified by
`tests/Allegiance.Core.Tests/P870Core2TxtTests.cs` (13 cases, 3× isolated runs).
The `Constants` record codec is also implemented and round-trips per record.
The twelve remaining per-object-type codecs and the pipeline rows are scaffolded
but not completed (below).

## Key finding: byte-identical round trip is not achievable by native's own format

Two native facts prevent the literal `igc → text → igc` byte-identical round trip
that the brief asks for:

1. **The version word.** `DumpIGCStaticCore` stamps a fresh `time(0)` version each
   run (`missionigc.cpp:2358`), so native is never byte-identical even on a clean
   semantic round trip. `core2txt` persists the original version and exact
   record-type sequence in a `.coremeta` sidecar to restore them.
2. **Uninitialised struct padding.** The shipped cores were produced with MSVC
   debug-era memory: record payloads contain `0xCC`/`0xCD` bytes in C++ struct
   padding holes that the text format does not carry. Verified on `PCore001.igc`
   projectile payloads (padding holes `50-51`, `77-79`, `86-87` = `0xCC`). Native
   itself cannot reproduce these either, so **no tool can be byte-identical to the
   original .igc while remaining byte-compatible with native's text format**.

The tool therefore preserves semantic fields exactly, keeps floats at native `%g`
precision with an exact round-trip fallback for the rare value `%g` cannot
reproduce (e.g. the `0xCDCDCDCD` marker), and restores padding/version through the
sidecar so a round trip via the tool is byte-identical. This is documented in the
tool's `README.md` and `GLOSSARY.md`.

## Delivered

- `tools/core2txt/` — new tool project (CLI + library):
  - `CoreTable.cs` — the shared native helper port (tech/part tokens & masks,
    abilities, `BitsOf*.csv` loaders, tab CSV reader, constant comments, `%g`
    float formatting).
  - `TypeCodec.cs` — `ITypeCodec` interface + `StaticCoreContext` (cross-type
    id↔name maps) + `TypeCodecRegistry`.
  - `StaticCoreRoundtrip.cs` — export/import orchestration + `.coremeta` sidecar
    (version, record order, padding).
  - `StaticCoreVerify.cs` — per-record decode∘encode identity + whole-file round
    trip checker (`SelfTest` command).
  - `CppBin.cs` — C++-layout binary reader/writer (default MSVC packing).
  - `codecs/ConstantsCodec.cs` — implemented and per-record verified (1/1).
  - `README.md` — usage + round-trip fidelity notes.
- `tests/Allegiance.Core.Tests/P870Core2TxtTests.cs` — 13 cases certifying the
  shared helpers; project reference added to the test csproj.
- `GLOSSARY.md` — `tools/core2txt` entry.
- `docs/parity/sim-rules-matrix.csv` + `docs/parity/p868-na-audit.csv` — 12 rows
  moved to `implemented` (below).

## Certification of the loader rows

Rows closed as **implemented** (native functions ported in `CoreTable.cs`, tested
by `P870Core2TxtTests.cs`; 3× isolated runs pass):

| Row | Function | `CoreTable` |
|---|---|---|
| SIM-0686 | `TechBitFromToken@115` | `TechBitFromToken` |
| SIM-0687 | `TechsListToBits@128` | `TechsListToBits` |
| SIM-0688 | `PartMaskFromToken@152` | `PartMaskFromToken` |
| SIM-0689 | `PartsListToMask@361` | `PartsListToMask` |
| SIM-0690 | `BitsToTechsList@377` | `BitsToTechsList` |
| SIM-0691 | `BitsToPartsList@417` | `BitsToPartsList` |
| SIM-0692 | `LoadTechBitsList@451` | `LoadTechBitsList` |
| SIM-0693 | `LoadPartsBitsList@487` | `LoadPartsBitsList` |
| SIM-0785 | `AbilitiesListToMask@209` | `AbilitiesListToMask` |
| SIM-0786 | `readCSV@530` | `ReadCsv` |
| SIM-0791 | `ConstantsDamageComment@4246` | `ConstantsDamageComment` |
| SIM-0793 | `ConstantsGlobalComment@5316` | `ConstantsGlobalComment` |

Rows still **partial** (scaffolded by `StaticCoreRoundtrip`/`ConstantsCodec`, not
fully ported): SIM-0694 (`ImportStaticIGCObjs@547`), SIM-0752
(`ExportStaticIGCObjs@5396`), SIM-0784 (`LoadIGCFile@94`), SIM-0787
(`LoadIGCFile@2334`), SIM-0789 (`TerminateStatic@3016`).

## Verification actually run

- `tools/build-tools.sh`: **all 93 tool projects build 0/0** (includes the new
  `tools/core2txt`).
- New test file `P870Core2TxtTests`: **13/13 pass, 3× isolated runs** (39 case
  executions, zero skips), per the light gate's "new tests 3× alone".
- `tools/check-glossary-links.py`: **OK** (276 paths).
- `git diff --check`: **clean**.
- `scripts/parity-scorecard.py`: **Simulation 89.31% (1905/2133)** after vs
  **89.30% (1903/2131)** baseline at dispatch (12 rows partial→implemented);
  General matrix unchanged at 98.69%.

## Not completed this session (honest)

- The remaining 12 per-object-type codecs (projectile, four expendables, parts
  across six equipment files, hulls incl. hardpoints, stations, drones,
  developments, civilizations, treasure sets). Without them `SelfTest` reports
  those types as unsupported and the whole-file round trip for `PCore001.igc` is
  not yet byte-identical.
- The 5 pipeline rows (SIM-0694/0752/0784/0787/0789) remain partial.
  **→ Completed by the P870b section below (only SIM-0789 remains partial).**
- The full-solution `dotnet build` (0/0) and the **ONE `--tier fast` run** and the
  **showcase never-park 5×**
  (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`)
  were not run: session budget was exhausted by the port work and the discovered
  padding/byte-identity issue. They must run on the next lane pass once the type
  codecs are complete. No `src/Allegiance.Server` / `src/Allegiance.Core` code was
  changed, so the AOT-publish branch of the gate was not triggered.
  **→ All three ran in P870b (below): build 0/0, all 93 tools, 31 new cases 3x,
  one Fast sweep, showcase 5/5.**

## Native intent and modern approach

**Native intent:** the static-core text authoring helpers are live operator
functionality reached by `Core2Text`/`Text2Core`, so they are real parity work, not
n/a — a binary parser would not certify them.

**Modern approach:** a portable CLI that stays byte-compatible with native's text
format (headers, columns, `%g` floats, name/hex masks) while adding the `.coremeta`
sidecar that native lacks, which is what makes a true byte-identical round trip
possible. It is intentionally strict: `SelfTest` rejects any record whose
decode→encode is not byte-identical, so incomplete codecs fail loudly rather than
silently dropping fields.

---

# P870b (2026-10-29) — finish Core2Text/Text2Core: native-exact text, all codecs, real-core round trip

Supersedes the "Not completed this session" list above: the twelve remaining
per-object-type codecs, the real-core byte-identical round trip and the three
certified loader rows are done below. Only SIM-0789 remains partial.

## 1. The three helper rules that were inverted vs native (fixed)

Claude's review was right on all three; each is now native-exact and has a test
that fails on the old behaviour.

1. **Tech-mask hex.** `TLargeBitMask` sets bit *i* as
   `m_bits[i>>3] |= 0x80 >> (i&7)` (MSB-first), prints each byte with `%02X`
   (UPPERCASE) and rejects odd-length strings (`zlib/mask.h:167-230`).
   `CoreTable.MaskToString`/`FromString` now do exactly that: bit 0 → `"80…"`,
   round trip uppercase, odd length returns `false` and leaves the mask untouched.
   Tests: `Tech_mask_hex_is_native_msb_first_uppercase_and_rejects_odd_length`,
   `TechsListToBits_zero_hex_and_named_tokens` (digit-leading `"0100"` sets bit 7,
   not bit 0).
2. **`readCSV`.** Native `std::getline(..., '\t')`
   (`Igc/missionigc.cpp:530-545`) DROPS a trailing empty field (`"1\t2\t"` → 2
   columns) and keeps interior/header ones. `CoreTable.ReadCsv` now reproduces
   `getline` semantics, including an empty line producing zero columns. Test:
   `ReadCsv_is_tab_delimited_drops_trailing_empty_field_and_keeps_header`.
3. **`atoi` leniency.** `PartMaskFromToken`/`PartsListToMask`
   (`Igc/missionigc.cpp:152-206,361-374`) use the file's native-style
   `CoreTable.ParseInt` (`"3abc"` → 3) instead of checked `int.Parse`; the reader
   additionally accepts a leading sign because `PartMask` is a signed short and
   the writer prints its decimal spelling (`c_pbm16` alone → `-32768`).

Rows SIM-0686..0693, SIM-0785, SIM-0786, SIM-0791, SIM-0793 therefore stay
**implemented** with corrected tests, and SIM-0694, SIM-0752, SIM-0784, SIM-0787,
SIM-0792 move to **implemented** in section 4.

## 2. All 13 static object types are now ported (Obj2Txt / Export / Import)

`tools/core2txt/codecs/` implements, in native declaration order and with native
columns/comments/`%g` floats:

| Codec | Native write / read | Files |
|---|---|---|
| `ConstantsCodec` | `missionigc.cpp:5398-5420` / `:607-663` | `Constants` |
| `ProjectileCodec` | `Obj2Txt` `:4456-4505` / `:672-745` | `TypesOfProjectiles` |
| `MissileCodec`, `MineCodec`, `ChaffCodec`, `ProbeCodec` | `:4510-4780` / `:747-1028`, `:1200-1290`, `:1030-1125`, `:875-1025` | `ExpendableMissiles/Mines/Chaff/Probes` |
| `PartCodec` (six equipment files + launchers) | `:4786-5052` / `:1291-1850` | `Expendables`, `Weapons`, `Shields`, `Cloaks`, `Afterburners`, `Packs` |
| `HullCodec` | `:5056-5225` / `:1851-2000` | `TypesOfShips`, `Hardpoints`, `ShipsSFX` |
| `StationTypeCodec` | `:4306-4432` / `:2060-2205` | `TypesOfStations`, `StationsSFX` |
| `DevelopmentCodec`, `DroneCodec`, `CivilizationCodec`, `TreasureSetCodec` | `:5229-5285`, `:5289-5312`, `:5318-5360`, `:5364-5410` / `:1903-1975`, `:1977-2028`, `:2212-2280`, `:2280-2325` | `Developments`, `TypesOfDrones`, `Civilizations`, `TypesOfTreasures`, `GlobalAttributes` |

`Export` writes all 22 native files (headers copied verbatim, including native's
own header typos) and throws `NotSupportedException` for any unsupported record
kind; `ImportBody` rebuilds every supported kind and the int16-type + int32-size
record headers. Field accesses use the offsets certified by
`src/Allegiance.Core/Data/Data*IGC.cs` (corpus-verified decoders), and the test
`Exported_values_match_the_certified_static_core_decoders` re-derives a
representative row of every file from those decoders, so a wrong offset fails.

## 3. Native writer/reader defects the round trip forced us to resolve

Native's text writer and reader disagree in several places; native's own
`igc→text→igc` cannot be byte-identical because of them (plus the version stamp at
`missionigc.cpp:2356-2363` and uninitialised `new char[]` payload memory). The port
keeps the native text spelling and fixes the reader, documented in
`tools/core2txt/README.md`:

- booleans are written `true`/`false` (`ZString(bool)`, `zstring.cpp:264-275`) and
  read as booleans (native's `atoi` always yields 0);
- colour channels are written as floats (`ZString(float)`) and read as floats
  (native's `GetInteger()` truncates `0.5` to 0);
- the writer's negative decimal `PartMask` is accepted (see §1.3);
- `CanRescue`/`CanRescueAny` prefix-overlap: each token resolves to its longest
  exact native name (native ORs both bits for the writer's `"CanRescueAny"`);
- hull `ALLOWED MISSILES`/`ALLOWED DISPENSERS` follow the header and the native
  reader (magazine, dispenser); native's writer swaps them;
- `GlobalAttributes.csv` has no owning-type column and development/civilization
  ids overlap, so the row's `COPY OF NAME` disambiguates (native's dev reader can
  consume a civ row and corrupt the development's attributes);
- a CRLF file leaves `\r` in native's last field; the importer treats it as the
  line terminator instead of corrupting the final mask;
- `pmEquipment[ET_Weapon]`/`[ET_Pack]` and a mine's `DataObjectIGC.radius` are not
  text columns: not text-covered, so a round trip preserves them and a foreign
  folder gets zeros (native's reader zeroes the masks and ignores the mine column).

## 4. Byte-identical round trip on the real cores (`.coremeta` implemented in the codecs)

Every codec reports a per-byte **text-determined mask** (`PayloadBuilder`: string
coverage stops at the NUL, so the stale bytes after it stay residue).
`StaticCoreRoundtrip.Export` re-imports its own text, subtracts the covered bytes,
and stores the remaining nonzero bytes sparsely (`offset:HH`) in `.coremeta`
together with the original version word and the exact record type/id sequence.
`ImportBody` replays that sequence, restores the residue and emits the record.

Measured: **100/100 shipped static cores are byte-identical** after
`igc → text → igc` (`SelfTest --all` on the artwork corpus, ~7.5 s; full output
kept at `~/allegiance-evidence/P870b/selftest-all.txt`), including
`PCore001.igc` (1447 records). The 100 are every `*.igc` in the corpus with the
static-core wrapper (`int32 version` + `int32 size`, record walk landing exactly on
end-of-body); the other 114 are maps (`int32 size` only). Names:
`CC_13d, EventCore, ExCore1, GoDII_04, GoDII_05, PC14test, PC2_019, PCore001..PCore016TEST
(17 files), RC_03, RTc006a, Race101, VoS000091, XC02..XC06, ac_01..ac_20, aw3,
cc_06..cc_16, dn_000450, dn_000460, empire0b2, plus15b7, racecore, rc_01..rc_04,
rps55, static_core, tcor_02, tcor_03, warpcorex11, wc_01..wc_04, zone_core,
zoom_coreB`. New tests:

- `PCore001_round_trips_igc_to_text_to_igc_byte_identically` (Fast, 75 ms);
- `All_shipped_static_cores_round_trip_byte_identically` (Live, 6-8 s; 3x alone);
- `Text_export_is_stable_across_a_round_trip` (the CSVs are byte-stable);
- `Foreign_text_folder_without_sidecar_imports_the_same_semantics` (no
  `.coremeta`: `StaticCore.Load` sees the same type universe, names, hull masses,
  hardpoints, station ability masks, constants and treasure entries);
- `Exported_text_files_use_native_headers_and_column_counts` (native headers,
  every row's column count matches);
- `Exported_values_match_the_certified_static_core_decoders`.

Loader rows: `P870StaticCoreLoaderTests` certifies SIM-0784 (size-prefixed wrapper,
exact walk, all 13 model populations, the `DoEncrypt`/`DoDecrypt` rolling-XOR path
at `FedSrv/FedSrv.CPP:30-56`) and SIM-0787 (production
`ClientWorld.MissionCoreResolver` → `ClientMissionCoreCache.Resolve`: named load,
extension optional, shared instance, `null` for a missing or path-escaping name).

**SIM-0789 remains partial.** The managed port has no `TerminateStatic` operation:
`StaticCore` is immutable and GC-lifetime, and `ClientMissionCoreCache` keys entries
on path+file stamp. Remaining work: certify cache-consumer retirement after a core
file version change and state the reproducible managed equivalent of the portable
`Terminate` body (`Igc/missionigc.cpp:3016-3031`). It is not claimed implemented.

## 5. `tools/core2txt` registration

Added to `AllegianceCore.slnx` (`/tools/`), alongside `mdl-to-glb`; still built
standalone by `tools/build-tools.sh`. Usage, the `.coremeta` format and every
native-defect resolution are in `tools/core2txt/README.md`; `GLOSSARY.md` updated.

## 6. Why the scorecard's weighted numbers moved by 2 (1903→1905)

That move is **not from P870**. The P868 branch-tip snapshot the earlier P870
report compared against scores simulation credit/denominator **1903/2131**; the
dispatch base (merge `e0a0e1de`) scores **1905/2133**. The whole difference is one
row added between them by the P864d merge: **SIM-0852**
(`CFSMission::SendLobbyMissionInfo`, weight **2**, production, `implemented`), so
both credit and the eligible weighted denominator rise by exactly 2. Measured with
`git show 280bbd68:docs/parity/sim-rules-matrix.csv` vs
`git show e0a0e1de:docs/parity/sim-rules-matrix.csv`.

P870b's own scorecard effect: simulation weighted credit **1905 → 1909** (+4 =
SIM-0784 + SIM-0787, the only two of the 18 rows with `production` reachability and
weight 2); the weighted denominator is unchanged at **2133** (they were already
eligible partials); unweighted complete/eligible **683/803 → 685/803**. The other
16 rows are `test-only`, so they earn no headline credit by design.

| Matrix | Before (dispatch `HEAD`) | After P870b |
|---|---:|---:|
| Simulation weighted | 89.31% (1905/2133) | **89.50% (1909/2133)** |
| Simulation complete/eligible | 85.06% (683/803) | **85.31% (685/803)** |
| General weighted | 98.78% (1863.5/1886.5) | unchanged |
| General complete/eligible | 98.79% (652/660) | unchanged |

## Verification (light gate, all run)

- `~/.dotnet/dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 93 tool projects built** (includes `core2txt`).
- New tests **3x alone**: 30 P870 Fast cases 3/3 runs; the Live all-cores case
  3/3 runs (6-8 s each). Total P870 cases: 31.
- ONE `tools/run-tests.sh --tier fast` (`tiers-20261029T002727`): 11,500 cases,
  815 s, infrastructure PASS, **19 failures, none in P870 code** (`src/` untouched
  by this lane). 16 are deterministic **pre-existing dispatch-base** failures in
  NPC travel/layer steering (0/3 alone), caused by the already-merged `d0d0acca`
  "Native NPC GotoPlan on by default" flipping NPC steering under the legacy
  non-GotoPlan tests; 3 are load flakes (3/3 alone: `BotFlightTests`,
  `AsteroidMinedLiveTests`, ClientCore `MissileTrackWarningLiveTests`). Recorded in
  [docs/TEST_FLAKES.md](../TEST_FLAKES.md). Raw logs:
  `~/allegiance-evidence/P870b/fast-tier/` (summary.json, TRX and per-project logs).
  After that sweep the tool received one comment correction and one guard
  (`Text2Core` on a folder without `Constants.csv` now fails with a clear message
  like native instead of emitting zeros); the solution/build-tools builds and all
  new tests were re-run 3x after it.
- Showcase never-parks **5/5** (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`,
  4m15s-5m20s per run).
- `python3 scripts/parity-scorecard.py`: validated 852 + 687 rows, numbers above.
- `tools/check-glossary-links.py`: OK (279 paths); `git diff --check` clean.

## Native intent classification (footer)

- **SIM-0686..0693, SIM-0785, SIM-0786, SIM-0791, SIM-0793 — implemented (native
  intent).** These are live portable static-core authoring rules reached by the
  native `Core2Text`/`Text2Core` commands (`FedSrv/FedSrv.CPP:10385-10480`); they
  are not n/a and a binary parser would not certify them. Every native guard,
  sentinel and ordering rule is ported; the only deliberate differences are the
  three memory-safety bounds native gets wrong by undefined behaviour
  (`SetBit(-1)`, `rgParts[et][16]`, out-of-range `rgParts[et]` in
  `LoadPartsBitsList`) — the port keeps the observable result and drops the UB.
- **SIM-0694, SIM-0752, SIM-0792 — implemented (native intent).** The full
  `ImportStaticIGCObjs` / `ExportStaticIGCObjs` / `Obj2Txt` behaviour for every
  supported static type, including native's file set, headers and record headers,
  is ported and certified by the 100/100 byte-identical corpus round trip and the
  independent decoder cross-check.
- **SIM-0784, SIM-0787 — implemented (native intent).** The native file loader's
  size-prefixed read, optional transform, model population, named resolution and
  observable missing-file branch are certified against the production entry point.
- **SIM-0789 — partial (native intent, deliberately not claimed).** Managed
  lifetime replaces manual `Terminate`, but "the port chose differently" is not a
  closing reason; cache-consumer retirement remains to be certified.
- **Native bugs are labelled as such, not as n/a:** the writer/reader asymmetries
  in §3 are native defects (`ZString(bool)` vs `atoi`, float colours vs
  `GetInteger`, signed decimal part masks, the ability-name prefix overlap, the
  swapped dispenser/magazine hull columns, the ambiguous `GlobalAttributes` row
  owner, and the CRLF last field). The port fixes the reader (or the one writer
  column order) so the writer's own text is lossless; the native text spelling is
  preserved.

**Modern/better approach.** A portable, testable CLI with an explicit
"text-determined vs residue" byte model replaces native's in-process `AllSrv`
switches and its uninitialised-memory round trip: the text stays native-compatible
(pure tab tables, no extra columns), while `.coremeta` carries only the version
word, record sequence and the sparse bytes native cannot reproduce — so the tool is
byte-exact where native is not, without inventing a new core format. Deterministic
`%g`-with-exact-fallback formatting and id-keyed record matching also make an
edited text folder build deterministically instead of inheriting heap garbage.
