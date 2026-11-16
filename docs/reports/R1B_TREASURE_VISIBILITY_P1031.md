# R1B_TREASURE_VISIBILITY_P1031 — R1b F2: treasure spawns ignored `SeenBySide`

Task: P1031, owner finding **F2** of `R1B_CARBON_REPORT.md`.
Report name: `R1B_TREASURE_VISIBILITY_P1031`.
Base: Claude's master at dispatch (`d15a1482`), branch `hermes/p1031`, worktree
`wt-p1031`. Native reference: `/srv/corpus/Allegiance/src` (read-only).

Evidence (raw logs, outside the repo): `~/allegiance-evidence/P1031/`.
No new media; no config keys added.

---

## 1. Finding F2 — verified, then fixed

### Root cause (pre-fix code, as dispatched)

- `src/Allegiance.Server/Server.Treasure.cs:11-27` `BroadcastGeneratedTreasure`
  gated every peer with `ArtifactKnownToPeer` (`:29-31`), whose expression is
  **`true` for every non-artifact treasure**:
  `treasure.Code != TreasureCode.Flag || treasure.Data.TreasureId != Side.ArtifactSentinelFlag || …`.
  So parts, cash, powerups, development and CTF side flags went to the whole
  sector, carrying the drop's exact `P0`/`V0` (i.e. the unseen enemy ship's
  position and velocity).
- `src/Allegiance.Server/Server.Docking.cs:503-512` `SendTreasureSpawnSectorWide`
  used the same gate for every treasure-spawn site.
- `src/Allegiance.Server/Server.Treasure.cs:47-49` `SendTreasureSnapshot`
  exported **all** treasures when the peer had no side
  (`side is not { } s || t.SeenBySide(s)`), so a team-lobby/null-side peer saw
  every unseen treasure on a SET_CLUSTER/sector-entry snapshot.
- Both creation paths also latched `_crossSideDiscoverySent` for the unseen
  peer, so the legitimate per-tick discovery sweep
  (`src/Allegiance.Server/Server.Broadcast.cs:2286-2307`) could never deliver
  the treasure later when that side legitimately scouted it.

Entry points confirmed in the code: `FM_CS_DROP_PART`
(`Server.ShipCommands.cs:720`), `FM_CS_RELOAD` cargo overflow
(`Server.ShipCommands.cs:2617`), ship-death loot + flag drop
(`Server.Combat.cs:2259,2282,2308,2327,2431`), flag respawn
(`Server.Combat.cs:2434`), side-departure flag drop
(`Server.Lifecycle.cs:2552`), ambient generation
(`Server.Lifecycle.cs:921` → `BroadcastGeneratedTreasure`), and the
join/undock/SET_CLUSTER snapshots (`Server.Lobby.cs:5615`,
`Server.Docking.cs:1705,2474`, `Server.Combat.cs:2971`).

### Reproduction (fails before the fix, unchanged after)

`tests/Allegiance.Core.Tests/P1031TreasureVisibilityTests.cs` — three tests,
unskipped (`[SkippableFact]` + `[Trait("Requires","Artwork")]`), all real
corpus artwork and real loopback TCP where wire messages are involved.

Pre-fix run (`~/allegiance-evidence/P1031/prefix-fail.txt`): **3 failed / 0
passed / 0 skipped**, each on the leak itself, e.g.

```
Dropped_part_treasure_reaches_only_sides_that_can_see_it [FAIL]
  Assert.DoesNotContain() Failure: Filter matched in collection
  Collection: [···, Tuple (2, [10, 0, 33, 0, 161, ···], 1), Tuple (3, [10, 0, 33, 0, 161, ···], 1),
              Tuple (1, [48, 0, 28, 0, 250, ···], 1), Tuple (2, [48, 0, 28, 0, 250, ···], 1),
              Tuple (3, [48, 0, 28, 0, 250, ···], 1), ···]
```

Peer 1 is side 0's observer 6 km away with no scanner in range; `48,0,28,0` is
`FM_S_TREASURE(28)` and it was queued to peer 1 in the same tick as the drop.
The other two tests failed on the ambient path (3 frames including the unseen
side where 2 are legitimate) and on the null-side snapshot (2 `TREASURE(28)`
frames to a team-lobby peer).

Every claim of F2 reproduced; nothing in the finding had to be skipped. One
precision note for the report: the report's `SendTreasureSnapshot` half is the
*null-side* branch only — seated peers were already per-side gated since P797.

---

## 2. Fix

### 2.1 Creation-time side visibility (Core)

`src/Allegiance.Core/Model/Sector.cs:5098-5210` — new
`Sector.RefreshTreasureVisibility(Treasure)`.

Native never broadcasts a created treasure; a `CtreasureIGC` is born unseen and
the throttled cluster sweep exports it on the first sighting
(`Igc/treasureIGC.cpp:167-177`, `FedSrv/FedSrv.CPP:10750-10757`,
`Igc/clusterIGC.cpp:348`, `Igc/clusterIGC.h:22`). The port keeps an instant
creation frame for game feel (P797), so the new object is scored against the
sector's scanner net at creation with the **same** per-target body the sweep
uses: flying ships (docked/boarded excluded, `stationIGC.cpp:282-285`,
`shipIGC.cpp:3632-3643`), probes and live stations; `VisibilityMath.InScannerRange`
with the sector's asteroid occluders; the P856 allied scan-sharing leg
(`FedSrv.CPP:10989-11000`); the native latched flag/last-spotter write
(`FedSrv.CPP:11153-11171`) and the sweep's one-scouting-credit bookkeeping. A
treasure has no side, so the own-side rule never applies and its signature is
`CmodelIGC`'s default 1.0 (`Igc/modelIGC.cpp:32`).

Called from every treasure creation site, so no spawn path can bypass it:

- `src/Allegiance.Core/Sim/Ship.cs:4695` (`CreateDropTreasure` — DROP_PART, cargo
  overflow, TREASURE_ACK decline, death loot),
- `src/Allegiance.Core/Sim/Ship.cs:4747` (`CreateFlagTreasure` — death flag drop,
  side-departure flag drop),
- `src/Allegiance.Core/Model/Mission.Treasure.cs:86` (`GenerateTreasure` —
  ambient/development/cash/powerup/artifact generation),
- `src/Allegiance.Core/Model/Mission.GoalObjects.cs:44` (pedestal flags),
- `src/Allegiance.Server/Server.Combat.cs:2434` (`RespawnCarriedFlag`).

### 2.2 Export gate (Server)

- `src/Allegiance.Server/Server.Treasure.cs:22` `BroadcastGeneratedTreasure`:
  recipient needs `live.Ship.SideId is { } side && side >= 0 && treasure.SeenBySide(side)`.
  `ArtifactKnownToPeer` is removed — its artifact branch is subsumed (an artifact
  is a treasure like any other).
- `src/Allegiance.Server/Server.Docking.cs:509` `SendTreasureSpawnSectorWide`:
  same gate.
- `src/Allegiance.Server/Server.Treasure.cs:38` `SendTreasureSnapshot`:
  no side (team-lobby/null-side) ⇒ return; otherwise only
  `t.SeenBySide(side)` treasures, and the discovery latch is written for each
  sent treasure (instead of only for non-null sides).

The existing per-tick discovery sweep keeps full ownership of later edges: a
peer that was skipped at creation has no latch entry, so when its side's
scanner sees the treasure the sweep delivers it (and latches then). Nothing
double-sends: creation and snapshot both latch exactly what they send.

### 2.3 Native parity verdict

**Native parity.** This removes a port-only leak; the export audience is now the
native one.

| Rule | Native |
|---|---|
| Every treasure is side-visible + predictable | `Igc/treasureIGC.cpp:167-168` (non-static `c_mtCastRay | c_mtSeenBySide | c_mtPredictable`), `:176-177` (static adds `c_mtStatic | c_mtHitable`) |
| Export to a side only when that side sees it | `FedSrv/FedSrv.CPP:10750-10757` (`c_mtPredictable`, active side, Started/Starting) → `:10770-10775` (`ExportObj` to `CFSSide::FromIGC(pside)->GetGroup()`, "Tell everyone on the side about the newly visible static object") |
| Throttled sweep is the trigger | `Igc/clusterIGC.cpp:348` (`(m_nPass++) % c_nPassesPerUpdate == 0`), `Igc/clusterIGC.h:22` (`c_nPassesPerUpdate = 10`) |
| Seen state latches for statics | `FedSrv/FedSrv.CPP:11122-11128` (`if (m_bPredictable) sv.fVisible(true)`) |
| Joining player gets only seen treasures | `FedSrv/fsmission.cpp:4224-4231` (`if (ptlink->data()->SeenBySide(pside)) ExportObj(..., OT_treasure, NULL)`) |
| Treasure radius 5 / no explicit signature | `Igc/treasureIGC.cpp:185`, `Igc/modelIGC.cpp:32` |

Parity rows updated: `docs/parity/sim-rules-matrix.csv` SIM-0845 (broadened
from artifact-only to every treasure; new port cites and evidence), SIM-0135
(ambient regeneration now cites the visibility-gated creation frame) and
SIM-0538 (SendMissionInfo join burst note: snapshot is side-gated and sends
nothing to a null-side peer). `scripts/parity-scorecard.py` validates every
matrix (860 sim rows) with no errors.

### 2.4 Config / limits

No new `server.json` setting or limit constant: the audience is the native rule
itself, not a tunable. Cost is bounded and creation-only:

- one per-side scan per created treasure (not per tick), early-exit on the first
  seeing scanner per side;
- allocations scale with the sector's scanner count (ships/probes/stations);
- the per-tick sweep's cadence and cost are unchanged, and the creation frame's
  per-peer encode is still done once per treasure.

---

## 3. Tests

### New (3, all unskipped)

`tests/Allegiance.Core.Tests/P1031TreasureVisibilityTests.cs`:

1. `Dropped_part_treasure_reaches_only_sides_that_can_see_it` — live loopback:
   three real `Allegiance.Bot` peers, one side-0 observer 6 km from a side-1
   dropper+m mate in the station-less fly sector (1052); mounts a real corpus
   weapon, sends `FM_CS_DROP_PART(33)` through the production dispatcher,
   captures every egress frame with its tick, asserts the observer receives
   **no** `TREASURE(28)` for the drop while the dropper/mate receive it in the
   creation tick, then moves the observer's scanner into range and asserts the
   discovery sweep delivers it exactly once.
2. `Generated_treasure_broadcast_follows_side_visibility` — the
   `TreasureGenerated` → `BroadcastGeneratedTreasure` ambient path: an unseen
   side gets no frame, the seeing side's two peers get one each.
3. `Treasure_snapshot_exports_only_treasures_the_peer_side_has_seen` — the
   post-SET_CLUSTER snapshot: seated peer gets only its side's seen treasures;
   a null-side (team-lobby) peer gets none (the pre-fix all-treasures branch).

### Existing tests run (legit play unaffected)

- `tests/Allegiance.Core.Tests/DropPartTests.cs` (live co-located observer +
  cross-side enemy still receives the in-sight drop; empty-pack skip),
- `LiveTreasurePickupTests` (pickup, wallet, ACQUIRE_TREASURE),
- `AddPartTests` (part offer/accept/decline re-created treasure),
- `P841MissionRulesCertifyTests` (departing flag carrier drop),
- `P832dArtifactExportTests` + `ProsperityArtifactLiveTests` (artifact creation
  gate, artifact win),
- `KillShipLootDropTests` (death loot + bot pickup),
- `LiveGainFlagTests`, `TreasureExpiryTests`, `P663TreasureParityTests`,
  `P626InFlightUtilityLiveTests` (drop-cargo),
- ClientCore: `TreasureDropLiveTests`, `P663TreasureLiveTests` (ambient
  generation / artifact capture-expiry-respawn reaching real clients),
  `P832dArtifactBotTests`, `P758AcquireTreasureClientTests`,
  `ClientWorldTreasureTests`, `P663TreasureMotionTests`,
- join/undock/sector-entry snapshot paths: `ClusterInfoServerTests`,
  `LiveSideSelectTests`, `P775PlayerInfoMomentsTests`, `P843`/`P844`,
  `P1029RepositionSyncTests`, `P1033SquadSideTests`, `P1034BoardingParityTests`,
  `LiveTeleportProbeRipcordTests`, `LiveBucketTests`, `PlayerMoneyAutoDonateTests`.

Results (alone, `--no-build`):

| Batch | Passed | Failed | Skipped |
|---|---|---|---|
| New class, 3× alone | 3 / 3 / 3 (9 total) | 0 | 0 |
| Related Core batch (65 tests) | 65 | 0 | 0 |
| Related ClientCore batch (57 tests) | 57 | 0 | 0 |
| Join/snapshot/boarding batch (75 tests) | 75 | 0 | 0 |

Logs: `~/allegiance-evidence/P1031/related-core.txt`, `related-client.txt`,
`related-join.txt`, `new-tests-3x.txt`.

---

## 4. Gate

- `dotnet build AllegianceCore.slnx --disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false` — **0 Warning(s), 0 Error(s)**
  (`~/allegiance-evidence/P1031/solution-build.txt`).
- `tools/build-tools.sh` — **140 tool projects, all 0 warnings / 0 errors**
  (`build-tools.txt`).
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`) — succeeded, **no IL2026/IL3050**
  (`aot-publish.txt`).
- New tests 3× alone — pass/pass/pass.
- One full fast suite (`tools/run-tests.sh --tier fast --no-build`) — see the
  counts below (`fast-run2.txt`, TRX under the run's results dir).

### Fast suite

One completed sweep (`tools/run-tests.sh --tier fast --no-build`, base + changes):
**14,722 tests, 544.9 s**, calibrated Fast limit 11.519 s (P744 factor 1.152), infrastructure
checks 9/0/0 — **14,710 passed / 5 failed / 7 not executed**. Per project: Lobby 193/0/0,
Update 55/0/0, ClientCore.MonoGame 5,471/0/4 (not executed), ClientCore 2,446/3/0,
Core 6,526/2/3 (not executed), plus the two `Lane=LiveTiming` quarantine lanes 2/0/0 and 17/0/0.

All five failures are ledgered non-task families and are recorded in `docs/TEST_FLAKES.md`
(new "2026-11-16 — P1031" section). Evidence per method:

| Method | Alone | Master A/B |
|---|---|---|
| `WorldPopulationLiveTests.PeersLearnSameSideTeammateShip_AndWorldPopulation_FromRealServer` | 3/3 pass | — (ledgered side-seating race, `:167` `-2` vs `0`) |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` (14.9 vs `[14.94,15.06]`) | 3 pass / 1 fail twice, 4 pass / 1 fail in five | **2/5 failed** on master (14.9, 14.85) — pre-existing |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | 3/3 pass | — (ledgered death-receipt) |
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | 3/3 pass | — (ledgered warp-transit SHIP_STATUS under load) |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | 1 fail in 3, then 7/8 | **1/8 failed** on master (8.45) — pre-existing |

The two tests that did not pass every lone repetition were A/B'd against the master product
code without `git stash`: the eight changed product files were copied aside, `git checkout --`
restored master, the project was rebuilt and the tests re-run, then the copies were restored
(hashes verified) and the tree rebuilt — both fail intermittently on master too. No tier,
limit or assertion was relaxed. Five calibrated-limit offenders (>10 s) all passed.

Logs: `~/allegiance-evidence/P1031/fast-run2.txt`, `client-flakes-alone.txt`,
`core-flakes-alone.txt`, `p660-baseline.txt`, `gameover-baseline-8x.txt`, `gameover-8x.txt`.

---

## 5. Unfinished / notes

- Nothing in F2 was skipped or judged unreal.
- The showcase/skirmish presets were checked through the fast-tier preset tests that
  exercise them (`P914StationDeadlineTests`, `P927DeadlineTests`, `P954GameOverWireTests`,
  `P1027MissionParamsNonFiniteTests`) — all passed in the sweep. The Live-tier
  `P700ShowcaseLiveTests` never-parks proof was attempted once and did not finish inside
  the lane's wall-clock budget for a Live test (it is a real-time five-second observation);
  per the lane rules Live-tier tests run on Claude's mini runner per merged batch, and this
  change touches no flight/sim/placement code that the never-parks rule guards.
- No rig/mini/Mac/VPS host was contacted; no Docker change (no new service,
  store, port or env var).
- The brief file was kept out of the change set (removed from the worktree at
  the end; a copy lives in the evidence dir).

---

**Native intent** — fog of war is authoritative: a treasure object exists
server-side but is exported to a side only once that side's scanner net sees it
(`c_mtSeenBySide | c_mtPredictable`, `Igc/treasureIGC.cpp:167-177`), through
`ShowObject`'s side-group export on the throttled cluster pass
(`FedSrv/FedSrv.CPP:10750-10775`, `Igc/clusterIGC.cpp:348`), and a joining
player receives only the treasures that side has already seen
(`FedSrv/fsmission.cpp:4224-4231`). No creation broadcast exists natively.

**Modern approach** — the port keeps P797's instant `FM_S_TREASURE(28)`
creation frame for game feel (a drop should appear at once for the side that can
see it), but makes it an honest per-side export: `Sector.RefreshTreasureVisibility`
runs the sweep's own target scan for the new object at creation, every creation
site calls it, and the three senders (`BroadcastGeneratedTreasure`,
`SendTreasureSpawnSectorWide`, `SendTreasureSnapshot`) require a real side and
`SeenBySide`. The per-tick `SweepCrossSideDiscoveryExports` remains the single
delivery path for every later sighting edge, with the same per-(peer, treasure)
latch, so no client is told twice and no unseen side learns a drop's position.
