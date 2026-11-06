# P939 — generated maps place the per-path special asteroids

**Status:** implemented, working tree only (no commit, no push).
**Scope:** the special-asteroid class selection of `PopulateCluster`, shared by
generated and file maps. Miner rock selection (`c_ttLeastTargeted`),
`PrepareResearchMap` policy, He3 counts and every showcase/bot change are out of
scope and untouched.

---

## 1. The native rule

Native has **one** rule for every map — file or generated:

```
Igc/common.cpp:3052        int offset = IasteroidIGC::NumberSpecialAsteroids(pmp);
Igc/common.cpp:3053-3054   if (offset != 1)
Igc/common.cpp:3054             offset = randomInt(1, offset);
Igc/common.cpp:3055-3058   for (short i = 0; (i < nSpecialAsteroids); i++)
Igc/common.cpp:3057             CreateAsteroid(pmission, pcluster,
Igc/common.cpp:3057                             IasteroidIGC::GetSpecialAsterioid(pmp, offset++), 0.0f);
```

The two helpers key on the mission's three research-path switches:

* `IasteroidIGC::NumberSpecialAsteroids` — `Igc/asteroidigc.cpp:179-188`:
  `c_nSpecialAsteroidTypes[8] = {3, 1, 1, 2, 1, 2, 2, 3}` (line 181), indexed by
  `(bAllowSupremacyPath ? 4 : 0) + (bAllowTacticalPath ? 2 : 0) + (bAllowExpansionPath ? 1 : 0)`
  (183-185).
* `IasteroidIGC::GetSpecialAsterioid(pmp, index)` — `Igc/asteroidigc.cpp:190-229`:
  `0`/`7` → `index % 3`; `1` → 0; `2` → 1; `3` → `index % 2`; `4` → 2;
  `5` → `(index % 2 == 0) ? 0 : 2`; `6` → `1 + (index % 2)`; returns
  `n + nFirstSpecialType` (`:228`), with `nFirstSpecialType = 6` (`:146`).
* The rows are the three special classes, `Igc/asteroidigc.cpp:134-136`:
  index 6 = Uranium `c_aabmSpecial << 0` = **0x10** (`bgrnd51`/`hotrock`),
  index 7 = Silicon `c_aabmSpecial << 1` = **0x20** (`bgrnd52`/`copperrock`),
  index 8 = Carbonaceous `c_aabmSpecial << 2` = **0x40** (`bgrnd53`/`carbonrock`).
* The switches are `MissionParams.bAllowTacticalPath` / `bAllowSupremacyPath` /
  `bAllowExpansionPath` (`Igc/igc.h:1130-1132`), all default `true`
  (`Igc/igc.h:1247-1249`).
* The map maker calls the same function for generated maps:
  `Igc/mapmakerigc.cpp:944-948`.

Two details of that rule matter when porting it:

1. **The offset starts at 1, not 0**, and the `randomInt(1, count)` draw is
   inclusive of both bounds; when the count is 1 **nothing is drawn at all**.
   The draw happens once per `PopulateCluster` call, *before* the loop, even when
   the sector's special count is 0. (The old file-map port drew exactly this, so
   its rocks were already native-correct; see §4.3.)
2. **Generated maps use the same rule.** Old code only cycled the enabled paths
   when a mission research object was present (the file-map top-up), so every
   generated special was the plain `0x10` Uranium class — the P937 finding
   (8 specials, all 0x10, no 0x20/0x40 in a rig batch).

---

## 2. The port change

### 2.1 `src/Allegiance.Core/MapGen/MissionMapGenerator.cs`

**Before** (HEAD, `MissionMapGenerator.cs:1181-1194`):

```csharp
// Special asteroids (c_aabmSpecial).
int nSpecial = ApplyMultiplier(...);
var specialKinds = _fileMapResearch is { } research
    ? new[] { research.AllowExpansionPath, research.AllowTacticalPath, research.AllowSupremacyPath }
        .Select((enabled, index) => (enabled, index)).Where(p => p.enabled).Select(p => p.index).ToArray()
    : [];                                   // ← generated maps: no kinds at all
if (_fileMapResearch is not null && specialKinds.Length == 0) specialKinds = [0, 1, 2];
int specialOffset = specialKinds.Length > 1 ? _rng.Next(1, specialKinds.Length + 1) : 1;
for (int i = 0; i < nSpecial; i++)
    AddAsteroid(clusterId, specialKinds.Length == 0 ? DataAsteroidIGC.AabmSpecial   // ← always 0x10
        : (short)(DataAsteroidIGC.AabmSpecial << specialKinds[(specialOffset + i) % specialKinds.Length]), 0f);
```

**After:**

* `MissionMapGenerator.cs:87` — `_paths` carries the mission's three switches.
  It is seeded from `MapGenParams` in the constructor
  (`MissionMapGenerator.cs:100-103`) and re-assigned from `mission.Research` by
  the file-map top-up (`MissionMapGenerator.cs:1278-1279`), so both call sites
  read the same field and the same source the mission itself is built from.
* `MissionMapGenerator.cs:1194-1205` — the special block becomes native's body:
  the draw (`SpecialAsteroidOffset`) happens once, before the loop, and each rock
  gets `SpecialAsteroidCapability(..., specialOffset + i)`.
* `MissionMapGenerator.cs:1213-1270` — the shared port, one function per native
  helper:
  * `SpecialAsteroidTypeCounts` (1218) = native `c_nSpecialAsteroidTypes`;
  * `SpecialPathCode` (1223) = the `supremacy·4 + tactical·2 + expansion·1` key;
  * `NumberSpecialAsteroids` (1231) — `asteroidigc.cpp:179-188`;
  * `SpecialAsteroidKind` (1239) — `asteroidigc.cpp:190-229`;
  * `SpecialAsteroidCapability` (1256) — the row's `aabmCapabilities`
    (`asteroidigc.cpp:134-136`, `nFirstSpecialType`);
  * `SpecialAsteroidOffset` (1265) — `common.cpp:3052-3054`; a count of 1 returns
    `1` **without a draw**, any other count draws one inclusive value in
    `[1, count]`. The generator passes its own seeded stream so the draw lands in
    native order (one `randomInt` per `PopulateCluster`, nothing else added).

### 2.2 `src/Allegiance.Core/MapGen/MapGenParams.cs:70-85`

Three new `init` properties, the port of `MissionParams.bAllowExpansionPath` /
`bAllowTacticalPath` / `bAllowSupremacyPath` (`Igc/igc.h:1130-1132`), defaulting
`true` like native's `Reset` (`igc.h:1247-1249`).

### 2.3 `src/Allegiance.Server/Server.Lifecycle.cs`

* `ToCoreParams` (1061) takes an optional `ResearchSettings? research` and writes
  the three flags (1096-1098).
* `AssembleGameMission` passes `ResearchSettingsFor(g)` (1369-1370) — the same
  `GameConfig`→`ResearchSettings` source the mission uses, so a generated map and
  a file map of the same game place the same classes.

### 2.4 `src/Allegiance.Server/MapRotationConfig.cs:85-86`

The rotation preflight generates the same way, with the game's research settings.

No RNG change for the file-map path: the old code drew exactly one
`Next(1, count + 1)` per cluster for `count != 1` and none for `count == 1`, which
is what `SpecialAsteroidOffset` does. Generated maps gain that draw only where
native has it (count ≠ 1); with a single enabled path they gain nothing, which
the fingerprint test below proves bit-for-bit.

---

## 3. The table (native, all 8 combinations)

Kinds for type indices 0..5; `count` is `NumberSpecialAsteroids` = the inclusive
upper bound of the cluster's start offset. U = 0x10, Si = 0x20, C = 0x40.

| exp | tac | sup | code | count | kinds for indices 0..5 |
|---|---|---|---|---|---|
| off | off | off | 0 | 3 | U, Si, C, U, Si, C |
| on  | off | off | 1 | 1 | U, U, U, U, U, U (expansion → Uranium only) |
| off | on  | off | 2 | 1 | Si, Si, Si, Si, Si, Si (tactical → Silicon only) |
| on  | on  | off | 3 | 2 | U, Si, U, Si, U, Si |
| off | off | on  | 4 | 1 | C, C, C, C, C, C (supremacy → Carbonaceous only) |
| on  | off | on  | 5 | 2 | U, C, U, C, U, C |
| off | on  | on  | 6 | 2 | Si, C, Si, C, Si, C |
| on  | on  | on  | 7 | 3 | U, Si, C, U, Si, C |

Machine-readable copy: `docs/reports/P939_assets/evidence.json`.

**Does this differ from the pre-change file-map cycling?** No. The old
`specialKinds[(offset + i) % length]` walk over the *enabled* paths is exactly
native's `index % count` table for every combination (for one path the only
element repeats; for two the walk alternates; for three it cycles). Requirement 2
of the brief therefore needs no expectation change, and it was verified
empirically (§4.3): the file-map census is byte-identical before and after for all
8 combinations.

---

## 4. Tests and evidence

New file: `tests/Allegiance.Core.Tests/P939SpecialAsteroidTests.cs` (29 cases).
The native table is transcribed in the test as the oracle (independent of the
port), the way the topology tests pin native-derived values.

### 4.1 Fails on the base (shown)

Run against HEAD with the new test file holding only the base-compilable cases
(the three census assertions plus the reproducibility guard; the API-dependent
cases cannot compile against the base, see below), filtered to
`FullyQualifiedName~P939SpecialAsteroidTests`:

```
Failed Generated_singlering_specials_cover_all_three_native_classes
   generated SingleRing seed=1123059318 has no 0x20 special rock;
   census: sector 0: 0x10,0x10 | sector 1: 0x10,0x10 | sector 2: 0x10,0x10 | sector 3: 0x10,0x10
Failed Generated_pinwheel_specials_cover_all_three_native_classes
   generated PinWheel seed=939 has no 0x20 special rock;
   census: sector 0: 0x10,0x10 | sector 1: 0x10,0x10 | sector 2: 0x10,0x10,0x10,0x10
Failed Generated_special_sequence_per_sector_follows_the_native_all_paths_cycle
   SingleRing sector 0: special sequence U,U is not a native rotation (Si,C / C,U / U,Si)
Passed Same_seed_reproduces_the_same_map_with_the_native_special_rule   (reproducibility guard)
Failed! - Failed: 3, Passed: 1, Total: 4
```

The remaining cases (`Special_table_*`, `Generated_single_path_*`,
`Generated_two_path_*`, `Single_path_maps_keep_the_base_map_exactly`, the two
file-map cases) reference API that does not exist on the base
(`MissionMapGenerator.NumberSpecialAsteroids` / `SpecialAsteroidKind` /
`SpecialAsteroidCapability` / `SpecialAsteroidOffset`,
`MapGenParams.Allow*Path`), so on the base they are build errors rather than
runtime assertions — by definition red there.

### 4.2 Passes after (P939 alone, and 3× in a row — see §7.2)

```
Passed!  - Failed: 0, Passed: 29, Skipped: 0, Total: 29
```

Case list:

| test | what it pins |
|---|---|
| `Special_table_matches_native_for_every_path_combination` (Theory, 8) | count, capability for indices 0..5, offset range and the draw count/range per combination |
| `Special_class_advances_with_the_running_offset` | `offset++` is never reset per rock |
| `Generated_singlering_specials_cover_all_three_native_classes` | 0x10, 0x20 and 0x40 all present with all paths on |
| `Generated_pinwheel_specials_cover_all_three_native_classes` | same, PinWheel (centre doubled count) |
| `Generated_special_sequence_per_sector_follows_the_native_all_paths_cycle` | every sector's sequence is a native rotation from the cluster's drawn offset |
| `Generated_single_path_map_places_only_that_paths_class` (Theory, 3) | expansion → all 0x10, tactical → all 0x20, supremacy → all 0x40, with the class's own `bgrnd5x`/icon row |
| `Generated_two_path_map_alternates_the_two_enabled_classes` (Theory, 3) | only the two enabled classes, alternating from the drawn offset |
| `Single_path_maps_keep_the_base_map_exactly` | the 1-class cases draw nothing: the expansion-only maps of SingleRing seed 1123059318 and PinWheel seed 939 match the pre-change fingerprint hashes exactly (`CE5D9D…`, `54C03D…`) |
| `Same_seed_reproduces_the_same_map_with_the_native_special_rule` | same seed → identical map (SingleRing and PinWheel) |
| `File_map_population_keeps_the_native_special_classes` (Theory, 8) | file maps, all 8 combinations, 4 specials/sector: legal native rotation, single-path combos exactly that class |
| `File_and_generated_paths_agree_on_the_special_classes` | the same settings yield the same class set through both call sites |

### 4.3 File maps produce the same rocks as before (empirical, all 8 combos)

A probe ran the *unchanged* file-map path twice — once against
`HEAD:src/Allegiance.Core/MapGen/MissionMapGenerator.cs`, once against the P939
tree — with a synthetic 3-sector mission, 4 specials a sector, seed 864. Both
runs are byte-identical (`docs/reports/P939_assets/evidence.json`):

```
off/off/off : 0x20,0x40,0x10,0x20 (each sector)
on /off/off : 0x10,0x10,0x10,0x10
off/on /off : 0x20,0x20,0x20,0x20
on /on /off : 0x20,0x10,0x20,0x10
off/off/on  : 0x40,0x40,0x40,0x40
on /off/on  : 0x40,0x10,0x40,0x10
off/on /on  : 0x40,0x20,0x40,0x20
on /on /on  : 0x20,0x40,0x10,0x20
```

The pre-existing file-map tests are unchanged and still pass
(`P864cMissionGenerationTests.Zero_He3_and_special_resource_path_selection_follow_native_file_population`,
`P864cMissionGenerationTests.File_population_gates_lists_independently_preserves_existing_objects_and_counts_imported_He3`,
`P864cDuplicateLogonTests.Server_file_map_boot_runs_native_empty_sector_population`).

---

## 5. Changed expectations (and why)

Generated maps change for every seed whose specials were 0x10 — that is the
point of the task. Totals do not change (same counts, same 80 rocks in the
reference maps); only the class bits, and — when more than one class is enabled —
the RNG stream after the new per-cluster draw.

| where | before | after | why |
|---|---|---|---|
| `P750NativeAsteroidTypesTests.Generated_ring_map_asteroids_carry_native_models_icons_hp` | every special asserted `bgrnd51`/`hotrock` | each class asserted against its own row: 0x10 `bgrnd51`/`hotrock`, 0x20 `bgrnd52`/`copperrock`, 0x40 `bgrnd53`/`carbonrock` (same hp 25000, radius 200..400) | the SingleRing map now carries Si and C rocks; the native rows are `asteroidigc.cpp:134-136` |
| `P750NativeAsteroidTypesTests.Generated_brawl_asteroids_use_native_huge_and_special_rows` | same, `bgrnd51` only | same per-class rows | Brawl's specials cycle all three classes with the default all-on paths |
| `MapGenBrawlTests.Brawl_NegativeSpecialMultiplier_GivesAbsoluteCount`, `Brawl_HasNoExtraValidityConstraint` | counted specials with `cap & AabmSpecial` (0x10) → saw 1 of 3 | counted with the union of the three class bits → sees all 3 | the classes are **distinct** capability bits; `& 0x10` only ever matched Uranium. Count expectations (3) are unchanged. |
| `MapGenGridFamilyTests.EastWest_PopulateCluster_OverrideDrivesPerSectorCounts` | `Special(cid)` with `& AabmSpecial` → 2 of 4 | same helper with the three-class union → 4 | same bit-mask fix; EastWest's per-sector special counts are unchanged |
| `P703ResearchCommanderLiveTests` comment | "the current map generator emits generic special rocks (0x10)" | describes the per-path classes and why the fixture still tops classes up | comment only; the fixture and assertions are unchanged |

What changed in the reference maps (`PCore001`, teams=2, MapSize=0, defaults):

* SingleRing seed 1123059318: **before** 8 specials all 0x10 → **after**
  0x10 ×3, 0x20 ×2, 0x40 ×3; sectors `0x40,0x10 | 0x10,0x20 | 0x20,0x40 | 0x40,0x10`.
* PinWheel seed 939: **before** 8 all 0x10 → **after** 0x10 ×3, 0x20 ×3,
  0x40 ×2 (centre `0x20,0x40,0x10,0x20`).
* Both keep 80 rocks, 36 He3, 36 buildable.

---

## 6. Parity

* `docs/parity/sim-rules-matrix.csv` **SIM-0748** (FedSrv mission rules,
  `CmissionIGC::GenerateMission`) is the row that covers map selection/generation
  including `PopulateCluster` (`Igc/missionigc.cpp:3830-4123`, notes already cite
  `common.cpp:2963-3107`). It stays `implemented`/weight 5. Corrected with this
  production code + tests: `native_cite` now also names
  `Igc/common.cpp:3052-3058; Igc/asteroidigc.cpp:179-229`, `port_site_binding`
  names `MissionMapGenerator.PopulateAsteroids (special class)`, `evidence` adds
  the eight P939 test names, and `notes` records the shared rule, the
  8-combination equivalence and the "draws once only when the class count is not
  1" RNG rule.
* No other row covers the special asteroids: `SIM-0820` is the goal-object row
  (`common.cpp:2895-3001,3090-3095`), `SIM-0001/0002/0010` are the constants the
  generator reads, and a search for `common.cpp:30` / `PopulateCluster` in
  `docs/parity/` returns only SIM-0748.
* Scorecard, before → after (identical):

```
sim-rules-matrix.csv: validated 857 rows
FedSrv mission rules (event-driven) | 98.69% (754/764) | 98.51% (331/336) | visual 0 | replaced 4
callbacks | 98.52% (200/203) | 95.89% (70/73)
constants | 100.00% (135/135) | 100.00% (39/39)
periodic | 93.88% (46/49) | 94.12% (16/17)
session | 100.00% (19/19) | 100.00% (6/6)
```

No row moved status, so no headline credit changes (the row was already
credited); the change is that the credited behaviour is now faithful for
generated maps too.

* `GLOSSARY.md` §11 — the map-generation entry now names the special-asteroid
  rule (`NumberSpecialAsteroids`, `SpecialAsteroidKind`, `SpecialAsteroidCapability`,
  `SpecialAsteroidOffset`, the 0x10/0x20/0x40 classes, generated + file maps,
  the switches' sources). `tools/check-glossary-links.py`: **OK (555 paths)**.

---

## 7. Gate

| gate | result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | see §7.1 |
| AOT publish (`src/Allegiance.Server`, Release linux-x64, `PublishAot=true`) | **exit 0, 0 IL2026/IL3050** |
| new/affected tests 3× alone | 3 × (P939 + P750 + Brawl + Grid + Radial + SeedRepro + Foundation + P864c) — all green, §7.2 |
| ONE `tools/run-tests.sh --tier fast` | §7.3 |
| showcase never-parks 5× vs base rate | §7.4 |

### 7.1 `tools/build-tools.sh`

```
build-tools: all 116 tool projects built      (exit 0)
```

### 7.2 New/affected tests, 3× alone

Command:
`dotnet test tests/Allegiance.Core.Tests --filter "FullyQualifiedName~P939SpecialAsteroidTests|FullyQualifiedName~P750NativeAsteroidTypesTests|FullyQualifiedName~MapGenBrawlTests|FullyQualifiedName~MapGenGridFamilyTests|FullyQualifiedName~MapGenRadialFamilyTests|FullyQualifiedName~MapGenSeedReproTests|FullyQualifiedName~MapGenFoundationTests|FullyQualifiedName~P864cMissionGenerationTests"`

Three serial runs: `Passed! - Failed: 0, Passed: 96, Skipped: 0` in 1 s, three
times in a row (29 of those cases are P939's).

### 7.3 Fast tier

```
tools/run-tests.sh --tier fast
fast: 13129 tests, 534.4s, FAIL
  Allegiance.Update.Tests: 28 passed
  Allegiance.Lobby.Tests: 119 passed
  Allegiance.ClientCore.MonoGame.Tests: 4712 passed, 3 NotExecuted
  Allegiance.ClientCore.Tests: 2160 passed, 1 failed
  Allegiance.Core.Tests: 6085 passed, 2 failed
  quarantine: 19 passed
```

The three failures are pre-existing, out of P939's files, and each was isolated
3× (`docs/TEST_FLAKES.md`, "P939 verification"):

| test | 3× alone | classification |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / pass | existing ledgered receipt-timing failure (P928) |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | existing baseline defect (P928 recorded the same on the pristine tree) |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass / pass / pass | load/timing flake (sweep 31.1 s vs [28, 31]) |

Every map-generation, asteroid and parity test in the sweep passed.

Skipped count with `local.env` present: **0** in the affected cohort and in
P939's own tests (the artwork corpus is configured, so corpus-backed map tests
run); the sweep's 3 `NotExecuted` are the optional Meshy-pack skips in
`Allegiance.ClientCore.MonoGame.Tests`.

### 7.4 Showcase never-parks, 5× vs the base rate

`tests/Allegiance.ClientCore.Tests/P700ShowcaseLiveTests` (both cases: the normal
home set and the P924 swapped-home set), five serial runs of the pair:

| run | normal `Local_showcase_never_parks…` | swapped `P924_swapped_showcase_never_parks…` | pair duration |
|---|---|---|---|
| 1 | pass | pass | 7 m 11 s |
| 2 | **fail** (8 m 02 s — ran the whole 480 s window) | pass | 12 m 06 s |
| 3 | pass | pass | 5 m 45 s |
| 4 | pass | pass | 8 m 58 s |
| 5 | **fail** (8 m 03 s — whole window) | pass | 11 m 39 s |

Then three more runs of the failing case **alone**, serially: **pass / pass /
pass** (4 m 12 s, 4 m 33 s, 3 m 19 s).

Failures only appeared when both cases ran back to back in one host process, and
each one consumed the full 480 s observation window — i.e. the end-of-window
assertion (objective arrival), not the 5 s parking bound, which would abort the
run early. That is the arrival-deadline shape P928 already ledgered for the
swapped case ("reached 17/30 objectives at 480 s").

Why this cannot be P939's: the showcase config boots the **file** map
`m2xspiderw.igc` with `Games[0].MapGen = null` and `showcase.techMode` at its
`"unlocked"` default (`ServerConfig.cs:12`), so neither the generated-map
special-class code nor `PrepareResearchMap` runs; the only generator code the
test can reach is the unchanged file-map top-up, which §4.3 shows is
byte-identical before and after for every path combination (same single draw,
same class sequence). Base control of the same pair on
`HEAD:src/Allegiance.Core/MapGen/MissionMapGenerator.cs` (same host, same load):
**1 failed / 1 passed** — the *swapped* case failed with
`Most showcase ships must reach their objectives`, `reached=17/30` at 480.1 s and
`longest speed<1 with target=0.601s` (no parking; the arrival deadline). So the
base tree fails this pair under the same conditions, with the identical
assertion, and the five-second parking bound itself was never breached in any
run on either tree.

Host load during these runs: `/proc/loadavg` **4.5–5.5** on 24 CPUs with ~5,700
threads — other agents' suites are running on this box, which is exactly the
condition the test is documented as sensitive to (`docs/TEST_FLAKES.md`, P928:
"load-sensitive … compare against the base rate"). P928's recorded base rate was
normal 5/5 and swapped 4/5 (one arrival-deadline failure). No test-only timeout
or parking bound was changed.

---

## 8. Files changed

```
src/Allegiance.Core/MapGen/MissionMapGenerator.cs     special-asteroid port (shared by generated + file maps)
src/Allegiance.Core/MapGen/MapGenParams.cs            MissionParams.bAllow*Path (3 flags, default true)
src/Allegiance.Server/Server.Lifecycle.cs             ToCoreParams carries the game's research settings
src/Allegiance.Server/MapRotationConfig.cs            rotation preflight generates with the same settings
tests/Allegiance.Core.Tests/P939SpecialAsteroidTests.cs   new (29 cases)
tests/Allegiance.Core.Tests/P750NativeAsteroidTypesTests.cs   per-class model/icon rows
tests/Allegiance.Core.Tests/MapGenBrawlTests.cs       special-class mask (union of the 3 bits)
tests/Allegiance.Core.Tests/MapGenGridFamilyTests.cs  special-class mask
tests/Allegiance.ClientCore.Tests/P703ResearchCommanderLiveTests.cs  stale comment
docs/parity/sim-rules-matrix.csv                      SIM-0748 cites/notes/evidence
docs/TEST_FLAKES.md                                   P939 sweep + 3× isolation
GLOSSARY.md                                           §11 map-generation entry
docs/reports/P939_REPORT.md, docs/reports/P939_assets/evidence.json
```

Raw logs (build-tools, AOT publish, fast-tier summary + logs, file-map census
before/after, showcase 5× and the base control) are kept outside the repo at
`~/allegiance-evidence/P939/`; the small machine-readable summary is
`docs/reports/P939_assets/evidence.json` (5.5 kB).

**Consequence for the research showcase (Claude/rig side, out of scope here):**
`PrepareResearchMap` tops a home cluster up to "planned constructions + 1 reserve"
per class; generated homes now already hold the enabled paths' classes, so the
top-up adds fewer rocks than before (P937 §5.1's generated matches will show
`[research-map] … added=` counts dropping). That is the intended effect — the
generator, not the showcase, supplies the branch resources — and it is why a rig
A/B of the showcase matches is worth a look after this lands.

## 9. Native intent (footer)

* **Design.** Native's `NumberSpecialAsteroids`/`GetSpecialAsterioid` pair is a
  deliberate design: the resource classes a map can offer follow the research
  paths the match enables, so a game with only the expansion path never has a
  Silicon or Carbonaceous rock to build on, and a single-path game has exactly
  one class (never a rotating mix). The odd `index % 3` from a 1-based offset and
  the two hard-coded 3s are native's own "hardcoded here, see #92" note
  (`asteroidigc.cpp:139,197`) — an intentional lookup table, not a rounded-off
  approximation.
* **Era constraint.** The 8-entry count table plus the 7-case switch duplicate the
  same information twice (native's comment says so) and were written for three
  fixed classes. That is a 1999-2001 coding convenience, not a behavioural
  constraint.
* **Bug?** No. Nothing in the rule is a defect — the port's old behaviour was the
  defect (it skipped the rule entirely for generated maps and emitted the plain
  0x10 class). The one place worth flagging is native's *own* comment style: the
  count and the kind are maintained twice, and `case 0`/`case 7` share a branch,
  so a fourth path or a fourth class would need edits in two places. The port
  keeps both tables so a divergence would be visible in tests rather than hidden
  by a rewrite.
* **Is a modern approach better?** For the selection itself, no: a general
  "resource class = f(enabled paths)" function would produce different maps than
  native for the same seed, which is exactly what parity forbids. The port does
  take the modern licence in *structure* only — one shared implementation
  (`SpecialAsteroidKind`) for both call sites instead of two copies of the walk,
  and the offset rule expressed once (`SpecialAsteroidOffset`) so the draw count
  is testable with a counting RNG. Map layout, class assignment, and RNG order
  are native's.
