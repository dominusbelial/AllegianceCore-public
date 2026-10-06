# P555 — Hull `pmEquipment` decode offset (byte 509 → 510) + treasure-set head alignment

Date: 2026-10-05
Author: Hermes (P555)

## 1. Summary

`DataHullTypeIGC.Decode` read `PartMask pmEquipment[ET_MAX]` at byte **509**,
but native alignment puts it at **510**. Every non-weapon mount mask — shields,
chaff, magazines, dispensers, cloaks, packs, afterburners — was therefore
garbled: a hull's own native preferred equipment usually did **not** mount while
foreign/capital gear often did. This was the mechanism behind P539/P539b's
"invincible bot" (scouts could not mount `Sm Shield 1` but could mount `Lrg
Shield 2`, 3750 strength), and it made P539b's report conclude — circularly, by
re-reading the port's own wrong masks — that "native rejects Sm Shield 1 on Adv
Scout too".

A **second, same-class bug** was found in the audit: `DataTreasureSetIGC` read
`treasureSetID` at 25 and `bZoneOnly` at 29, but native alignment puts them at
26 and 30.

Both are fixed, with corpus-wide failing-first tests.

## 2. Native ground truth

Verified by a standalone C++ replica of the native structs
(`~/Development/Allegiance/src/Igc/igc.h`, default alignment; `g++ offsetof`
probe) and cross-checked against the raw corpus bytes.

`struct DataHullTypeIGC` (igc.h:1767), the post-`textureName` fields:

| Field | Type | Native offset | Port (old) | Port (new) |
|---|---|---|---|---|
| `textureName` | `char[13]` | 496 | 496 | 496 (unchanged) |
| `pmEquipment[ET_MAX=8]` | `PartMask`=short | **510** | 509 ❌ | **510** ✅ |
| `interiorSound` | SoundID | 526 | 526 | 526 (already correct) |
| `exteriorSound` | SoundID | 528 | 528 | 528 |
| `mainThrusterInteriorSound` | SoundID | 530 | 530 | 530 |
| `mainThrusterExteriorSound` | SoundID | 532 | 532 | 532 |
| `manuveringThrusterInteriorSound` | SoundID | 534 | 534 | 534 |
| `manuveringThrusterExteriorSound` | SoundID | 536 | 536 | 536 |

Why 510 not 509: `textureName[13]` occupies 496–508; the following `short`
requires 2-byte alignment, so byte 509 is padding and `pmEquipment` starts at
510. Native `sizeof(DataHullTypeIGC)` = 540 (tail-padded to the struct's 4-byte
alignment), matching the shipped `hardpointOffset` = 540 the record carries
(`missionigc.cpp:1880` sets `hardpointOffset = sizeof(DataHullTypeIGC)`). The
six SoundID fields were already at the native offsets (only `pmEquipment` was
off by one).

`struct DataTreasureSetIGC` (igc.h:2100), the 32-byte head:

| Field | Type | Native offset | Port (old) | Port (new) |
|---|---|---|---|---|
| `name` | `char[25]` | 0 | 0 | 0 |
| `treasureSetID` | short | **26** | 25 ❌ | **26** ✅ |
| `nTreasureData` | short | 28 | (count read at 27, ignored) | 28 |
| `bZoneOnly` | bool | **30** | 29 ❌ | **30** ✅ |
| entries | 4 B each | 32 | 32 | 32 |

Corpus proof the head is native-aligned: for **all 99** treasure-set records the
native count at offset 28 equals the size-derived entry count, and byte 30 is 0.
(The old decoder's "count@27 = 256 × entries" was the off-by-one read, not a
writer quirk.)

## 3. Failing-first tests

`tests/Allegiance.Core.Tests/P555HullEquipmentMaskTests.cs` (hull):
- `PreferredEquipmentMounting_CollapsesToNearZero_AtNativeOffset` — counts, across
  every static core in the corpus, hull/preferred-*equipment*-part pairs that do
  not mount, reading `pmEquipment` from the raw bytes at 509 vs 510; asserts a
  >20× collapse at 510. **Measured 48,990 → 475** (a 103× reduction). Fails on
  the pre-fix code (no collapse), passes at 510.
- `PreferredEquipmentMounts_OnPcore001_ExceptDocumentedAfterburnerQuirks` —
  canonical core: every preferred equipment part mounts except exactly the 3
  genuine native quirks (flagship #73/#74 prefer `Hyper-Jump` mask 0x8000 with
  no afterburner socket; `Mustang` #129 prefers `Ion Booster` 0x0002 into a
  0x0001 socket). **Measured 542 → 3.**
- `ShieldSlots_MatchNativePartMasks_OnNamedHulls` — Scout/Adv Scout/Adv Fighter
  → 0x0001, Bomber → 0x0002, Cruiser/Destroyer → 0x0004.
- `SmShield1_MountsOnAdvScout_AndIsPreferred` — part 21 mounts on the Adv Scout.

`tests/Allegiance.Core.Tests/P555TreasureSetAlignmentTests.cs`:
- `TreasureSetId_ComesFromNativeOffset26_AcrossCorpus` — for every OT_treasureSet,
  decoded `TreasureSetId` equals the raw `short` at offset 26 and `BZoneOnly`
  equals raw byte 30.
- `NativeCountAt28_MatchesSizeDerived_AcrossCorpus` — count at 28 == (size−32)/4
  for all records.

Demonstrated failing-first: with the two decode offsets temporarily reverted,
4/6 P555 tests fail; restored, all pass.

## 4. Corpus before / after

Preferred equipment parts that do not mount on their own hull:

| Scope | @509 (buggy) | @510 (native) |
|---|---|---|
| PCore001 | 542 | **3** |
| Whole corpus (210 files) | 48,990 | **475** |

The 3 residual PCore001 quirks are documented native data (above). The wider
residual across non-representative training/event cores is the same class: hulls
that list preferred gear with no mount socket (e.g. a pack preferred by a hull
whose pack slot is 0x0000), which native itself would skip via `UpgradeShip`
(`FedSrv.CPP:11583,11590`).

## 5. Other decoders audited (same-class bug sweep)

For each, native offsets (`offsetof` via the C++ replica) were compared field by
field against the port decoder. Only the two above had a misalignment.

| Decoder | Audit result |
|---|---|
| DataPartTypeIGC (+ weapon/shield/cloak/afterburner/pack subtypes) | ✅ base 396, subtype tails match native (inventoryLineMDL[13] at the end, subtype floats at aligned 396) |
| DataLauncherTypeIGC | ✅ offsets 0/2/4/6/8/10, size 24 |
| DataProjectileTypeIGC | ✅ consistent with the 88-byte corpus form (native igc.h is 76 B; the corpus is authoritative and cross-validated via weapon→projectile refs) |
| DataExpendableTypeIGC (+ missile/mine/probe/chaff) | ✅ all subtype floats/ints at native-aligned offsets |
| DataDroneTypeIGC | ✅ 364/368/372/376/378/380/382, size 384 |
| DataCivilizationIGC | ✅ gas@160, ttbm@59/109, ids@260/262/264, size 268 |
| DataDevelopmentIGC | ✅ gas@364, ids@464/466, size 468 |
| DataStationTypeIGC | ✅ every sound + textureName@486/builderName@499, size 524 |
| DataTreasureSetIGC | ❌→✅ (fixed: 26/28/30) |
| DataClusterIGC / DataWarpIGC / DataAsteroidIGC / DataStationIGC / DataTreasureIGC | ✅ native-aligned within each record; cross-referenced by the corpus tests |
| DataConstantsIGC | ✅ (unchanged) |

Note on corpus-vs-native divergence: several map/static records (e.g. projectile
88 B vs native 76 B, asteroid 144 B vs native 140 B) reflect the shipped file
format, which is authoritative for parsing; those decoders were tuned to the
real bytes and are cross-validated by the existing corpus tests. The two fixes
here are the only places the port misread the actual bytes.

## 6. Downstream changes

- `src/Allegiance.Core/Data/DataHullTypeIGC.cs` — `pmEquipment` read at 510
  (native). Doc comment updated with the P555 citation.
- `src/Allegiance.Core/Data/DataTreasureSetIGC.cs` — `treasureSetID`@26,
  `bZoneOnly`@30; layout remarks corrected (the old "@25/@27/@29 corpus-measured"
  narrative was the misread).
- `src/Allegiance.Core/Wire/TreasureSets.cs` — comment corrected (the wire record
  is a self-consistent 25/27/29 port layout, intentionally not changed; encode and
  decode agree symmetrically).
- `tools/allegiance-mcp/Catalog.cs` — comments corrected: the P539b native
  preferred loadout now includes `Sm Shield 1` (part 21) plus Counter 1 chaff and
  EWS Probe 1 on the Adv Scout. Behaviour (CanMountPart-gated) auto-corrected by
  the decoder fix; no logic change needed.
- **Bot loadouts** (`Catalog.NativePreferredLoadout`): the Adv Scout now resolves
  the full native preferred loadout — level 0 `{Counter 1 (80), EWS Probe 1 (81),
  PW Gat Gun 1 (33), MRM Seeker 1 (153), Sm Shield 1 (21)}`; shield advances
  21 → 40 → 42 (Sm Shield 1/2/3).
- **Shop / launch loadout** (`Server.ShipCommands.cs:2491` CanMountPart,
  `ShopModel`): mounts now honour the corrected masks — players can buy `Sm
  Shield 1` on scouts/fighters, Med on bombers, Lrg on capitals; chaff now
  mounts (Counter on fighters, Hvy Counter on capitals).

Tests updated:
- `P539bCombatMetricTests` — shield tests now assert `Sm Shield 1` mounts and
  advances (21→40→42).
- `RunHullMatrixPlanningTests.PartsForWeapon...` — Scout's first mountable
  shield is now `Sm Shield 1` (21), not `Lrg Shield 2` (45).
- `ChaffMountMaskVerificationTests` (P492) — the "chaff fits no hull" conclusion
  was itself a symptom of the same bug; rewritten to assert the corrected native
  behaviour (chaff mounts on socketed hulls).
- `LauncherResolutionTests`, `DispenserEquipFireLiveTests` — stale comments
  corrected (chaff now mountable).

## 7. Correction notes

- `docs/reports/P539b_MCP_BOT_REAL_HITS.md` — the "Sm Shield 1 does not mount /
  NOT a port bug" section was circular (it re-read the port's own wrong masks);
  dated correction added: at native offset 510 the Adv Scout's shield socket is
  0x0001 and `Sm Shield 1` mounts.
- `docs/reports/P483_HULL_MATRIX_PARTIAL_GRANTS.md` — mountability examples were
  based on the misdecode; dated correction added (the per-type
  `MountableDefaultPart` fix itself is unchanged).
- `docs/reports/HULL_SWAP_P334.md` — audited; contains only the generic CanMount
  drop-gate policy (unaffected), no stale mask assertions.

## 8. Suite verification (3 runs on the final code, via scripts/run-tests.sh)

| run | Lobby | MonoGame | ClientCore | Core.Tests | failures (all pre-existing ledger load-flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1295/1295 | 507/507 | 3739/3745 | Core 6: AutoReload RELoad129, ShipsUpdate locked-target fractions, WinCondition Board_refused, Ripcord cancel, GameOverPlayers conquest-leak, CargoBay swap-part — each in TEST_FLAKES.md |
| 2 | 12/12 | 1295/1295 | 506/507 | 3744/3745 | ClientCore WarpApproach legacy-200/450 (ledger line 215); Core Conquest leak (ledger 29/175) |
| 3 | 12/12 | 1295/1295 | 506/507 | 3741/3745 | ClientCore FullSessionKill (ledger 33/204); Core StationsUpdate, CountdownStart ping, LiveChatSector wire, AsteroidMined (all verified green in isolation) |

All failures are live-network timing load-flakes already documented in
`docs/TEST_FLAKES.md`; each reproduced set passed 100% in isolation
(FullSessionKill 2/2, ShipsUpdate 3/3, StationsUpdate/AsteroidMined/LiveChat/
CountdownStart 14/14, plus the ShopLauncher/PartInfo suite 55/55). None were
introduced by P555: the P555 corpus tests, the P539b loadout-resolution tests,
and every shop/mount test were green in all three runs. Build: 0 warnings,
0 errors.

## 9. Runtime proof status

The task's designated live-loopback proof (a 5v5 `spawn_scenario` showing Adv
Scouts launching with `Sm Shield 1` and a player buying it in the shop) was not
executed in this session. Its behaviour is covered deterministically instead,
all green: `Catalog.NativePreferredLoadout(13,"Adv Scout",0)` resolves
`Sm Shield 1` (21) (asserted in P539bCombatMetricTests), `CanMountPart` accepts
it on the Adv Scout (P555HullEquipmentMaskTests), and the server's purchase gate
(`Server.ShipCommands.cs:2515`) and the shop mount gate use the same
`CanMountPart`. The live 5v5 run remains the outstanding runtime proof.

## 10. Out of scope

Tech-level design changes (P556) and balancing are explicitly out of scope.
