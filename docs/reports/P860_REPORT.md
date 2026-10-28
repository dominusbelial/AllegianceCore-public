# P860 — root cause of the recurring P480 rotation failure ("station exported to a side that had not seen it")

**Verdict: the port does not leak fog. The failure was a P480 *fixture* bug — a world-attribution
race — and it is now fixed and reproduced deterministically without load.**

`Allegiance.Core.Tests.P480RotationKeepsPlayersLiveTests.
Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect` failed in fast-tier gates with
`station 1052 was exported to a side that had not seen it` (ledgered in
[`docs/TEST_FLAKES.md`](../TEST_FLAKES.md) at P798b, P803, P813c, P848, P842, P852). The item is always
station **1052** — side 1's `Garrison` in Quaoar (1061) on `XXV_simple.igc` — delivered to a peer whose
final side is **0**.

Native-intent classification (bottom of this report): **port behaviour was already native-correct; the
assertion's world attribution was wrong.** No parity row changes.

---

## 1. What the server actually does (no leak)

Three code sites emit `FM_S_EXPORT(26)/OT_station`, and every one is visibility-gated:

| site | gate | native |
|---|---|---|
| join / migration burst (`Server.Lobby.cs`, `CompleteJoinAndLaunch`) | `if (!station.SeenBySide(joinSide)) continue;` after `SeedStartingVisibility()` | `FedSrv/fsmission.cpp:4215-4222` (`if (pstnlink->data()->SeenBySide(pside)) ExportObj(...)`) |
| mid-game identity export (`Server.Research.cs`, `ExportDiscoveredStations`, called from `BroadcastStationsUpdate` with `visible = stations.Where(st => st.SeenBySide(sideId))`) | the side's **current** `SeenBySide` | `ThingSiteImpl::ShowObject`, `FedSrv/FedSrv.CPP:10750-10778` (whole side group, side-scoped) |
| station completion (`OnStationConstructed`) | `peer.Ship.SideId == station.SideId` (own side only) | `Igc/stationIGC.cpp` creation + `FedSrv.CPP:11438` (`assert(pstationNew->SeenBySide(pside))`) |

A station's flag cannot regress: stations are `Predictable`, and
`SideVisibilityState.SetSideVisibility` mirrors native's latch
(`if (m_bPredictable) sv.fVisible(true)`, `FedSrv/FedSrv.CPP:11124-11126`). A side always sees its own
station — the own-side rule `FedSrv.CPP:10904-10912` (`if (pside == pmodel->GetSide()) { sv.fVisible(true); ShowObject(...) }`)
— and every station is explicitly invisible to every *other* side at world creation
(`Igc/common.cpp:3004-3017`, "Make all stations invisible to all sides but their own").

**Measured, not assumed.** A probe line was temporarily added at all three send sites:
`station=<id>/side<n> -> peer <p> seen=<flag> stage=<stage> tick=<t>`. Three campaigns of **40 runs each**
of the P480 class at `xargs -P 4` (host load average 5.8–7.1, others lanes building) plus 40 runs while a
fast-tier sweep ran: **0 failures and 0 sends with `seen=False`**. The gate's failure therefore cannot be a
`SeenBySide` violation at send time. (Probe removed; raw logs in `~/allegiance-evidence/P860/`.)

## 2. What the fixture did

Two facts combine:

1. **The 2nd peer is spawned on the enemy home.** The P480 fixture seats four bots and the default spawner
   picks a side by peer count (`SpawnDefaultShip`: `mission.Sides[_peerShips.Count % mission.Sides.Count]`),
   so peers 1/3 are spawned on side 0 (Ceto) and peers 2/4 on side 1 (Quaoar). The test then calls
   `TryJoinSide` to put them on their final sides — a **side switch that does not relocate the ship**. Peer 2
   (`KA1`) is therefore spawned on **side 1**, receives side 1's own garrison (station **1052**) in its very
   first join burst (legitimate: it was on side 1 and the own-side rule applies), and is then switched to
   side 0.
2. **The rotation rebuilds the same static map**, so the finished mission's station `1052` and the fresh
   mission's station `1052` carry the *same* `StationId`, `SideId` and `ClusterId`. A received
   `EXPORT(26)/OT_station` therefore **cannot be attributed to a world by its content** — only by its
   position in the connection's byte stream.

The fixture cleared the accumulators on the *test thread* (`bot.ResetWorldExports()` before the draw), which
does not order itself against the socket or the (default-on) broadcast publisher. Under load the transport /
publisher / bot receive thread can deliver that pre-rotation `EXPORT` **after** the clear; the assertion then
reads it as part of the rotated mission's burst, where side 0 has (correctly) never seen the enemy garrison.
That is the observed Assert.All payload verbatim: 2 items, `[0] = ExportStation{… StationId = 1052, SideId = 1,
ClusterId = 1061 …}`, `[1]` the fresh own-side station.

The P480 ledger entry for P848 records the tell: the failing run took **0.109 s** against **1.18 s** for
passing runs — the failing instance stopped at the first assertion instead of running the ready-up/countdown
tail, i.e. the accumulator content, not server timing, decided the outcome.

## 3. Native evidence for the boundary

Native orders the world change exactly one way and re-syncs nothing at all:

* `CFSMission::ProcessGameOver` queues `GAME_OVER` to the mission group, flushes it, **then** resets the
  mission — `FedSrv/fsmission.cpp:3437` (`QueueGameoverMessage()` + `SendMessages(...FM_FLUSH)`) …
  `:3490` (`SetStage(bRestartable ? STAGE_NOTSTARTED : STAGE_OVER)`).
* `CmissionIGC::ResetMission` (`Igc/missionigc.cpp:3804-3828`) nukes the clusters and `GenerateMission`
  (`:3830+`) rebuilds the world; no `SendMissionInfo`/object sync is re-sent to the players already in the
  mission, so a native client keeps the objects it already has across a rotation.
* At a **join**, native sends only what the joining side sees (`fsmission.cpp:4208-4250`), and the ports'
  P480 migration burst reproduces that burst for migrated peers — visibility-gated the same way.

So `GAME_OVER(62)` is the boundary between the worlds, and it is the only place in the stream at which the
split is unambiguous. The port re-sends the fresh world's exports (native does not), so the port's *harness*
owns that boundary; the server needed no change.

## 4. Fix

* `src/Allegiance.Bot/Bot.cs`
  * **`ResetWorldExportsOnGameOver()`** — arms a **stream-ordered** boundary: from then on the receive loop
    clears `ClusterExports`/`WarpExports`/`StationExports` *inside* `case GameOver.Fmid`, i.e. at the exact
    position the game-over message occupies in this connection's byte stream (everything the server queued
    before it belongs to the finished world, everything after it to the rotated one). Both are ordinary
    coalesce-key-0 traffic on one TCP stream, so the split survives any transport/publisher delay. It stays
    armed (every game-over is a world boundary); `StopResetWorldExportsOnGameOver()` disarms.
  * `ResetWorldExports()` keeps its caller-thread semantics but its doc now states the hazard and points at
    the ordered boundary.
* `tests/Allegiance.Core.Tests/P480RotationKeepsPlayersLiveTests.cs` — uses the ordered boundary instead of
  the racing thread-side clear, with the native citations inline. The P480 diagnostic failure message from
  the previous WIP run is kept (it is what named the station id).
* `tests/Allegiance.Core.Tests/P860RotationWorldExportBoundaryTests.cs` (new, Fast tier,
  `[Collection("LiveNetwork")]`, 2 cases — see §5).
* **Adjacent defect found while tracing the game-over tick** (deterministic, *not* the flake):
  `src/Allegiance.Server/Server.Broadcast.cs`, SimPass `Step("ballots", …)` enumerated `_missions` while a
  ballot-driven game-over rotates a mission in place (`_missions[idx] = fresh`; `List<T>`'s indexer set bumps
  the version). Every ballot-driven game-over therefore aborted that stage with
  `InvalidOperationException: Collection was modified` — it is in the captured server log of *every* P480 run,
  including the passing ones (`~/allegiance-evidence/P860/p480-baseline-verbose.log`). It now indexes the
  list (indexes and count are stable across a rotation); the stage no longer aborts.
* `docs/TEST_FLAKES.md` — new "P860 root cause" section plus a pointer on each P480 row
  (P798b, P803, P813c, P848, P842, P852).
* `GLOSSARY.md` — new **"Game-over world boundary (P860)"** entry under §3 Server.

## 5. Deterministic reproduction (fails before the fix, no load)

`tests/Allegiance.Core.Tests/P860RotationWorldExportBoundaryTests.cs`:

1. `Stale_export_from_before_the_rotation_is_not_attributed_to_the_new_mission` — the full round-robin-spawn +
   side-switch + `#draw` → `AcceptDraw` → rotation flow. It first asserts the *ordering* deterministically
   (the side-switching peer really does hold side 1's garrison export that its current side has never seen —
   it came in that peer's first join burst, before any switch), then leaves the pre-rotation world in the
   accumulators and lets only the game-over boundary separate the worlds, and finally asserts every
   station export held after the boundary is seen by the recipient's side in the rotated mission.
   **With the boundary disabled it fails with the gated failure verbatim**
   (`docs/reports/P860_assets/prefix-failure.txt`, full log
   `~/allegiance-evidence/P860/prefix-failure-full.log`); with the fix it passes.
2. `Rotation_exports_no_station_to_a_side_that_has_not_seen_it` — the fog-leak invariant itself, checked on
   the server's own send sink (`_testSendCapture`): every `EXPORT(26)/OT_station` the server puts on the wire
   across the rotation belongs to a station the recipient's side has seen in the rotated mission. Independent
   of any bot receive timing.

## 6. Verification

| check | result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | all 90 tool projects built, 0/0 |
| rig AOT publish (`Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | clean, no IL2026/IL3050 |
| P480 class **20×** `xargs -P 4` | **20/20 pass**, host load average **3.98 → 3.99** (1 min) |
| instrumented campaigns before the fix | 3×40 runs at `-P 4` (load 5.8–7.1): 0 failures, 0 unseen-side sends |
| new tests (`P860RotationWorldExportBoundaryTests`) 3× alone | 2/2 each run, ~0.46 s (after the final edit too) |
| `tools/run-tests.sh --tier fast` on the final tree | **PASS — 11,165 tests, 310.0 s, 0 failures, 0 timing offenders** (`tiers-20261028T033636`); P480 1.19 s, P860 cases 0.09 s / 0.09 s |
| the sweep before it (same tree) | FAIL — 2 pre-existing load flakes, neither in a path P860 touches: `P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame` (`TEST_FLAKES.md:2970`) and `ConquestTerritoryLiveTests.Flag_capture_...` (opened by the P852 row; 3/3 alone both before and after this task) |

Raw receipts: `~/allegiance-evidence/P860/` (full logs: `fast-tier-final-pass.log`,
`fast-tier-run-conquest-flake.log`, `prefix-failure-full.log`,
`p480-baseline-verbose.log`, `p480-20x-p4.txt`). Small summaries:
`docs/reports/P860_assets/`.

**Conquest flake, diagnosed while it was in front of us (not fixed — out of P860's
surface, and separately ledgered).** The failing assert is `ConquestTerritoryLiveTests.cs:387`,
i.e. the `Conquest[SideA]` of a *received* `GAME_STATE(155)` (50 instead of 80); the
mission's own `ConquestPercent` two lines earlier was correctly 80. So the port's
capture → `RecomputeConquestAndTerritory` → broadcast chain is intact, and the fixture
hit the same class of bug P860 fixed: `ResetGameStates()` is a caller-thread clear of an
asynchronous accumulator, so the pre-capture 50% `GAME_STATE` that was already in flight
landed after the clear. The robust assertion is "the newest `GameState` per peer" (per-peer
wire order is preserved), exactly as P480 now uses the game-over boundary.

## 7. Native-intent classification

* **Station export gating — already native-correct, no port change.** Port: `SeenBySide`-gated join export,
  per-side identity export, own-side completion. Native: `fsmission.cpp:4215-4222` (join), `ShowObject`
  `FedSrv.CPP:10750-10778` + `10944-10950` (first sight, side group), own-side rule `10904-10912`,
  latch `11124-11126`, creation-time invisibility `Igc/common.cpp:3004-3017`. **No export was ever sent to a
  side that had not seen the station** (measured at all three sites).
* **The failing assertion was wrong, not the wire.** It claimed a per-world property over a stream that mixes
  two worlds whose object ids are identical, with a clear point that is not ordered against the wire. Fixed by
  making the boundary wire-ordered at `GAME_OVER` — native's own order (`fsmission.cpp:3437` → `:3490`) and
  native's own "no re-sync at a reset" model (`missionigc.cpp:3804-3828`, `:3830+`).
* **The `ballots` enumeration defect** has no native counterpart (native never replaces mission objects), so
  it is a pure port robustness fix in the shared game-over path.

## 8. Not done / notes

* The 2 failing `Allegiance.ClientCore.MonoGame.Tests` cases seen during an early, deliberately loaded
  exploratory sweep (`ShopLauncherMountNumberingLiveTests`, `P800HullProofGpuTests`) did not recur in the clean
  gate above (they needed `tools/build-tools.sh` output / were load-sensitive); they are unrelated to P860 and
  the clean gate proves them green.
* `ConquestTerritoryLiveTests.Flag_capture_tips_conquest_…` failed once in that same loaded exploratory sweep
  (already ledgered as suspected-race by P852); it passed in the clean gate. Not investigated further here.
* The `ResetWorldExports()` caller-thread API is kept for phase-to-phase clears inside one world; anything that
  measures a *new* world must use the ordered boundary.
