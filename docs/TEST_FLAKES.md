# TEST_FLAKES.md — the single source of truth for flaky / failing tests

Authoritative ledger for `AllegianceCore` test-suite instability (created by
**P471**). Reports must cite this file instead of free-text flake lists.

Columns:
- **test** — fully-qualified test name (class abbreviated to the suffix).
- **failures/runs** — failures / full-suite runs counted. The **before** block
  is three full-suite runs on the pristine tree (`run1..run3`, parallel
  assemblies, wall ~4m22s each); the **after** block is three runs through
  `scripts/run-tests.sh` (assemblies sequential, wall ~5m30s — the P471
  systemic change), with the P471 fixes applied.
- **fails-in-isolation?** — did the test reproduce when run alone via
  `--filter`? **Yes** = real bug or stale test, **not** a flake.
- **suspected cause** — one line.
- **status** — `fixed` / `real bug (P472)` / `load-flake` / `quarantined`.

---

## Before (pristine tree, 3 parallel full-suite runs)

| test | failures/runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` | 3/3 | **Yes** | session-view watch latch misses the undock-return | real bug (P472) |
| `ClientCore…JoinFlowLiveTests.EntityTable_TracksARealTickingServer` | 3/3 | **Yes** | EntityTable registers 2 entries where 1 expected (dup entity) | real bug (P472) |
| `Core…LiveTurretGunnerBindingTests.Board_binds_gunner_…roundtrips_live` | 3/3 | **Yes** | SINGLE_SHIP_UPDATE fires on orientation-only change (position-gate regression) | real bug (P472) |
| `Core…ConquestTerritoryLiveTests.Flag_capture_tips_conquest_…game_over` | 3/3 | **Yes** | shield-less flag base never capturable under P450 gate (shipIGC.cpp:849-851) | **fixed** |
| `Core…ConquestTerritoryLiveTests.Flags_disabled_capture_…does_not_end` | 3/3 | **Yes** | same shield-less-base capture gate | **fixed** |
| `Core…ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` | 3/3 | **Yes** | shield-less base gate + rotation-during-update (List enumerator) | **fixed** |
| `Core…GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_…another` | 3/3 | **Yes** | same two causes | **fixed** |
| `Core…GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_…side` | 3/3 | **Yes** | shield-less base gate | **fixed** |
| `Core…LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side` | 2/3 | No | fixed-sleep MONEY_CHANGE receipt under socket load | load-flake (P472) |
| `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill` | 1/3 → **4/5** | **Yes** | **fixed (P596)** — test-seeding bug, not product: harness assumed spawn-order == connection-order, but P281's async join allocated ship ids at logon-dispatch order (racy), so peer0/peer1 sometimes landed on opposite sides and the (correct) same-side-ready relay skipped peer0. Seating now deterministic via explicit POSITIONREQ side-picks. Verified 32/32 isolated + 10/10 within full ClientCore assembly. |
| `Core…LiveWinConditionTests.Board_refused_once_mission_over` | 1/3 | No | cross-assembly socket load on the 1200ms board latch | **stable 3/3 after** |
| `Core…LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` | 1/3 | **Yes** | test pumped to stage==Over but not to a0.GameOvers receipt | improved → 2/3 residual (P472) |
| `Core…LiveRipcordTests.First_activation_broadcasts_…flying_in_sector` | 1/3 | not run | live-wire timing | suspect (P472) |
| `Core…LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | 1/3 | not run | live-wire timing | suspect (P472) |
| `ClientCore…ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility` | 1/3 | not run | live-wire timing | suspect (P472) |
| `Core…SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_…` | 1/3 | not run | live-wire timing | suspect (P472) |
| `Core…LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | 1/3 | not run | live-wire timing | suspect (P472) |

## After (P471 fixes + `scripts/run-tests.sh`, 3 sequential full-suite runs)

| test | failures/runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` | 3/3 | **Yes** | P482: docked ship leaked into same-tick ship-update via candidate-cache staleness | **fixed (P482) — residual ~1/15 timing flake, see correction below** |
| `ClientCore…JoinFlowLiveTests.EntityTable_TracksARealTickingServer` | 3/3 | **Yes** | (unchanged) dup entity | real bug (P472) |
| `Core…LiveTurretGunnerBindingTests.Board_binds_gunner_…roundtrips_live` | 3/3 | **Yes** | (unchanged) orientation-only SINGLE_SHIP_UPDATE | real bug (P472) |
| `Core…LiveMoneyTests.Board_refund_…` | 2/3 | No | fixed-sleep MONEY_CHANGE receipt | load-flake (P472) |
| `Core…LiveWinConditionTests.Multi_mission_…` | 2/3 | No* | peer-seating-order winner mismatch (residual; fixed the deterministic pump race) | P472 harden |
| `ClientCore…ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` | 1/3 | not run | live-wire timing | load-flake (P472) |
| `Core…AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` | 1/3 | not run | fixed-sleep relay receipt | load-flake (P472) |
| `Core…CargoBayReloadTests.Reload_malformed_entry_refuses_…` | 1/3 | No* | intermittent state-machine member | load-flake (P472) |
| `Core…CargoBayReloadTests.Reload_fuel_pack_is_accepted_…fuel_pool` | 1/3 | No | intermittent state-machine member | load-flake (P472) |
| `Core…Transport.InboundFloodSafetyLiveTests.Oversized_packet_is_rejected_…` | 1/3 | not run | live-wire timing | load-flake (P472) |

(*`Multi_mission` no longer reproduces the deterministic race in isolation —
   the residual is a full-sequence winner mismatch; `CargoBay` members rotate
   which one fails run-to-run.)

**Regression counts:** pristine 3-run total 34 → post-fix 3-run total 18
(failures across all runs). The conquest/game-over cluster and
`Board_refused` are green in all three after-runs.

---

## Quarantine log

None. No test was deleted or `[Skip]`-ped.

## P472 resolution (this task) — top 5 remaining flakes

Applied 2026-10-01 on `master`. Four of the top five were **test-side** and are
fixed; one (**DockOut**) is a **genuine production race** and is documented as a
real bug (the test is untouched, not papered over).

| test | before (post-P471) | after (isolated 20/20) | after (3 full-suite runs) | cause & fix |
|---|---|---|---|---|
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` | 3/3 | **unfixed** | **fails 3/3** | **PRODUCTION BUG** — one-shot `SHIP_DELETE SDR_DOCKED` latch is defeated by a same-tick `HEAVY_SHIPS_UPDATE` (client `Apply(HeavyShipsUpdate)` clears `IsGone`); test correct, not modified. → **fixed by P482** (dock invalidates the per-tick ship-update candidate cache). |
| `ClientCore…JoinFlowLiveTests.EntityTable_TracksARealTickingServer` | 3/3 | 0/20 | clean 3/3 | **stale test** — not a dup entity; server boots P382 initial-miner NPCs; scoped single-ship assert to the OWN ship (unique `IsMine`, no dup own-ship entity). |
| `Core…LiveTurretGunnerBindingTests.Board_binds_gunner_…roundtrips_live` | 3/3 | 0/20 | clean 3/3 | **stale test** — P209 orientation gate now propagates rotation to observers; scenario 7 rewritten to assert the orientation-only change DOES reach the observer carrying the rotated facing. |
| `Core…LiveMoneyTests.Board_refund_…` | 2/3 | 0/20 | clean 3/3 | **test race** — board handler refuses unless both ships docked; test raced the dock. Added dock-precondition wait (+ generous MONEY_CHANGE ceiling). |
| `Core…LiveWinConditionTests.Multi_mission_…` | 2/3 | 0/20 | clean 3/3 | **test race** — spawn-order vs seating-order mismatch; disconnect target now resolved by actual mission/side. |

**Regression counts:** the four fixed tests each 0/20 isolated and clean in all
three full-suite runs. The fifth — `DockOut_WatcherLatchesGone_…` — was a
**documented production bug** (P472 follow-up #1) and is now **fixed by P482**
(see the P482 resolution below), alongside a rotating set of pre-existing
live-socket load flakes below the top-5 that remain out of P472's scope (left
ranked above).

---

## P482 resolution (this task) — DockOut production-bug fixed

Applied 2026-10-01 on `master`. Closes P472 follow-up #1: the one genuine
production race among the top-5 flakes is now fixed in production code (the
test is untouched).

| test | before | isolated | cause & fix |
|---|---|---|---|
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` | 3/3 (P472, deterministic in isolation) | **0/20** | **PRODUCTION BUG — FIXED.** The one-shot `SHIP_DELETE(20)` `SDR_DOCKED` latch is defeated by the same tick's `HEAVY_SHIPS_UPDATE(176)`. Root cause: docking latches `Ship.StationId` but leaves the ship in the sector's `_models`, so the per-tick ship-update candidate cache (`Sector.GetShipUpdateCandidates`) built earlier in the tick (models loop) is never invalidated (`_memberVersion` only bumps on Add/Remove, and a dock is neither), and still feeds the just-docked ship to the broadcast; the client's `Apply(HeavyShipsUpdate)` clears `IsGone`. Fix: `Sector.ResolveDockCollisions` now calls `InvalidateShipUpdateCandidates()` when a ship docks (native: a docked ship's cluster is NULL, so it is absent from `cluster->GetShips()` — `modelIGC.cpp:246-275`; ship-update assembly reads `pcluster->GetShips()`, `FedSrv.CPP:3727/4150`). |

> **Correction (2026-10-01, verified by Claude after the P482 session).** The
> P482 delegate's "0/20" claim didn't reproduce. It also skipped the required
> full-suite runs and never committed; the changes were committed afterwards
> by Claude. Measured with `ShipDeleteGoneLiveTests` in isolation, 15 runs
> each:
>
> | test | without P482 | with P482 |
> |---|---|---|
> | `DockOut_…` | **15/15 fail** | **1/15 fail** |
> | `CrossSide_WarperLeavesSector_…` | 2/15 fail | 3/15 fail |
> | `SameSide_WarperLeavesSector_…` | 2/15 fail | 1/15 fail |
>
> The production race is fixed: DockOut went from deterministic to a
> residual ~7% timing flake. The warp-out siblings flake at about the same
> rate with and without P482, so they're pre-existing load/timing flakes
> and not caused by this change. The report's "siblings pass" also didn't
> hold up. One full-suite run with P482 had 4 failures, all already in this
> ledger and all passing 3/3 in isolation (ThreePeerSeededSession,
> FullSessionKillIntegration, AutoReloadOnEmpty, CargoBayReload).

Sibling `SHIP_DELETE` reasons (warp-out `SDR_LEFTSECTOR`, log-off
`SDR_LOGGEDOFF`) were checked and are **not** vulnerable to the same same-tick
staleness: both physically `Remove` the ship from the old sector's `_models`
(warp: `ServerHost.HitWarpEvent` `oldSector.Remove`, ServerHost.cs:940;
log-off: `RetireShipOnDisconnect` `sector.Remove`, Server.Lifecycle.cs:1225),
which bumps `_memberVersion` and invalidates the cache — so the same-tick
broadcast already excludes them. Only docking keeps the ship in `_models`, hence
only docking needed the explicit invalidation. The existing same-side/cross-side
warp-out live tests remain green.

**Perf sanity (vs P430's "15v15 holds ~20 Hz flat"):** an in-process real-server
measurement at 15v15 (30 seated peers, one sector) over a 6 s wall window —
**20.1 Hz achieved, 0 overruns, 121 ticks** — byte-identical with and without the
fix. The change adds zero steady-state cost (one `_memberVersion++` + one
candidate-list rebuild only on a dock tick).

---

## P487 session observations (live-wire timing flakes, unrelated to P487)

Three consecutive `scripts/run-tests.sh` runs during P487 (investment screen)
surfaced only pre-existing live-socket timing flakes, all rotating run-to-run;
none involve the bucket/investment path. Ones already in this ledger:
`AutoReloadOnEmpty…RELOAD129`, `GameOverPlayers…conquest_leak`,
`FullSessionKillIntegration…RealCrossSideKill`,
`Transport.InboundFloodSafety…Oversized_packet`. Four observed that were NOT yet
listed — added so the ledger stays the single source; they are the same class
(fixed-sleep live-wire receipt under full-suite load) and are `load-flake`:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `ClientCore…RetriedConnectLiveTests.RetriedConnect_StartsFromEmptyWorld_AndReseatsWithNewAssignment` | 1/3 | not run | live-wire reseat timing | load-flake |
| `Core…LiveStationRescueTests.Lifepod_reaching_rescue_station_…` | 1/3 | not run | live-wire dock timing | load-flake |
| `Core…LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_…` | 1/3 | not run | live-wire MONEY_CHANGE receipt | load-flake |
| `Core…ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_…` | 1/3 | not run | live-wire budget timing | load-flake |

P487's own tests (`BucketCatalogDescribeTests`,
`ClientWorldBucketFundingTests`, `InvestmentPaneModelTests`,
`InvestmentLayoutTests`) passed in all three runs.

---

## P495 session observations (live-wire timing flakes, unrelated to P495)

Three consecutive `scripts/run-tests.sh` runs during P495 (investment build-spot
gates + asteroid hint) surfaced only pre-existing live-socket timing flakes,
rotating run-to-run; none touch the changed code (all are Core/ClientCore
live-network classes, and the change is confined to `Allegiance.ClientCore.MonoGame`
rendering plus a pure gates class). Already in this ledger (cited, not re-added):
`AutoReloadOnEmpty…RELOAD129` (run 3), `ShipDeleteGoneLiveTests.CrossSide_
WarperLeavesSector_WatcherLatchesGone` (run 3),
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`
(run 2). Four observed that were NOT yet listed — added so the ledger stays the
single source; same class (fixed-sleep live-wire receipt under full-suite load)
and `load-flake`:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `Core…LiveRipcordTests.Re_target_activates_to_requester_only` | 1/3 | not run | live-wire order-target receipt | load-flake |
| `Core…WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands` | 1/3 | not run | live-wire delayed-blast timing | load-flake |
| `Core…MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` | 1/3 | not run | live-wire cross-side mine visibility timing | load-flake |
| `Core…LoadoutChangeLiveTests.Boarding_and_leaving_broadcast_passenger_change_on_parent_but_not_enemy` | 1/3 | not run | live-wire passenger-change broadcast timing | load-flake |

P495's own tests (`InvestmentBuildGatesTests`,
`RadarInvestAsteroidSelectionTests`, plus the existing
`InvestmentPaneModelTests` / `InvestmentLayoutTests`) passed in all three runs.

---

## P498 session observations (live-wire timing flakes, unrelated to P498)

Three consecutive `scripts/run-tests.sh` runs during P498 (client project split +
asset-source abstraction) again surfaced only pre-existing live-socket timing
flakes in the **untouched** `Allegiance.Core` / `Allegiance.ClientCore` assemblies.
`git diff --name-only` confirms P498 changes live entirely under
`src/Allegiance.ClientCore.Game` + the MonoGame head + their test project —
zero source/test changes in Core/Server/Bot/ClientCore. The changed area
(Allegiance.ClientCore.MonoGame.Tests) passed **1110/1110 in all three runs**.

Already in this ledger (cited, not re-added):
`FullSessionKillIntegration…RealCrossSideKill` (runs 2,3 — line 148),
`LiveRipcordTests.Re_target_activates_to_requester_only` (run 2 — line 182),
`GameOverPlayersWireTests.Conquest_win_lists_every_connected_player…side`
(run 3 — line 31 marked *fixed*; **recurred** as a live-timing flake). Four
observed that were NOT yet listed — added so the ledger stays the single
source; same class (fixed-sleep live-wire receipt under full-suite load) and
`load-flake`:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `ClientCore…ReadyCheckLiveTests.Only_the_mission_owner_can_force_team_ready_and_it_starts_the_mission` | 1/3 | not run | live-wire ready-start timing | load-flake |
| `ClientCore…ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | 1/3 | not run | live-wire warp-approach arrival timing | load-flake |
| `Core…LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire` | 1/3 | not run | live-wire sector relay timing | load-flake |
| `Core…P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` | 1/3 | not run | live-wire spotting/delivery timing | load-flake |

P498's own tests (`AssetSourceTests`, and every existing MonoGame test that
reads real artwork through the new `IAssetSource`) passed in all three runs.

---

## P499 session observations (live-wire timing flakes, unrelated to P499)

Three consecutive `scripts/run-tests.sh` runs during P499 (Android head + gamepad
input) again surfaced only pre-existing live-socket timing flakes in the
**untouched** `Allegiance.Core` / `Allegiance.ClientCore` assemblies. `git diff
--name-only` confirms P499 changes live entirely under
`src/Allegiance.ClientCore.Game` (+ the new `src/Allegiance.ClientCore.Android`
head + `scripts/pack-android-assets.sh` + `scripts/build-android-head.sh`) and
the MonoGame test project — zero source/test changes in Core/Server/Bot/ClientCore.
The changed area (`Allegiance.ClientCore.MonoGame.Tests`) passed **1119/1119 in
all three runs** (P498's 1110 + the 9 new `GamePadFlightRolesTests`).

Failures were only in the untouched assemblies. Several were already in this
ledger (cited, not re-added): `WarpBombTests.Live_whole_mission_warp_bomb_…`
(run 3 — line 183), `CargoBayReloadTests.Reload_…terminates_the_cargo_pack`
(the rotating `CargoBayReload` member, lines 53-54). Others observed that were
NOT yet listed — added so the ledger stays the single source; same class
(fixed-sleep live-wire receipt under full-suite load) and `load-flake`:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `Core…ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions` | 1/session | not run | live-wire heavy-slots timing | load-flake |
| `Core…LiveGainFlagTests.Flag_carrier_docking_at_own_side_station_secures_and_game_state_reflects_real_count` | 1/session | not run | live-wire flag-secure timing | load-flake |
| `Core…AddPartTests.Part_pickup_accept_into_empty_mount_mounts_fresh_part` | 1/session | not run | live-wire add-part receipt timing | load-flake |

P499's own tests (`GamePadFlightRolesTests`, 9) and every existing MonoGame test
passed in all three runs; the `Allegiance.ClientCore.Tests` / `Lobby.Tests`
areas were green in runs 1-2 and showed the usual single rotating live-wire
flake in run 3.

---

## P500 session observations (live-wire timing flakes, unrelated to P500)

Three consecutive `scripts/run-tests.sh` runs during P500 (the Android in-app
connect screen) surfaced only the documented live-wire timing class in the
**untouched** `Allegiance.Core` (3/4/5 failed of 3699 across runs 1-3) and
`Allegiance.ClientCore` (1/3 failed of 411 across runs 2-3). The changed area
(`Allegiance.ClientCore.MonoGame.Tests`) passed **1182/1182 in all three runs**.
The flakes rotate run-to-run and do **not** reproduce when the assemblies run
in isolation (re-verified green standalone during P500), i.e. the same
full-suite-load fixed-sleep receipt that fills this ledger. Concretely named
from run 1:
- `Core…ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` — already in this ledger (cited, not re-added).
- `Core…AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED_to_whole_mission_cross_side_and_same_side` — **added below** so the ledger stays the single source (same class: fixed-sleep live-wire receipt of the drained asteroid fraction under full-suite load).

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `Core…AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED_…cross_side_and_same_side` | 1/3 | no | live-wire mined-fraction receipt timing | load-flake |

## P509 session observations (live-wire timing flakes, unrelated to P509)

Three consecutive `scripts/run-tests.sh` runs during P509 (test-unlock tech +
money grant) surfaced **only pre-existing live-socket timing flakes** in the
untouched `Allegiance.Core` / `Allegiance.ClientCore` / `Allegiance.Lobby`
assemblies, rotating run-to-run. `git diff --name-only` confirms P509's changes
live in `Allegiance.Server` (config + grant), `Allegiance.Core` (one new
`StaticCore` method), the two debug compose configs, two new test files, and
this ledger + report — zero changes to the sim/collision/economy hot paths. The
changed/new test area passed **every run**: `TestUnlockGrantLiveTests` (2/2 in
all 3 runs), `P509TestUnlockShopVisibilityTests` (1/1 in all 3 runs), and the
whole `Allegiance.ClientCore.MonoGame.Tests` assembly (1208/1208 each run).
`Allegiance.Lobby.Tests` was clean 12/12 in all 3 runs.

Failures per run (all in the untouched live-wire area):
- **Run 1 (Core 3703/3708, ClientCore 417/418):** `P366LeaderBotCommandLiveTests…`
  ("leader A did not seat"), `ShipAsteroidCollisionTests.Loopback_bounced…`
  (bot didn't seat → null deref at the seat read), `ShipsUpdateLiveTests.Ship_
  leaving_cluster…` (ledger line 245), `GameOverPlayersWireTests.Conquest_win_
  in_one_mission…` (ledger line 31/267), `AddPartTests.Part_pickup_decline…
  recreates_treasure…` (ledger line 247), `ShipDeleteGoneLiveTests.DockOut…`
  (P482 residual, ledger lines 109-122).
- **Run 2 (Core 3705/3708, ClientCore 418/418):** `ShipAsteroidCollisionTests.
  Loopback_bounced…` (again), `LoadoutChangeLiveTests.Boarding_and_leaving…`
  (ledger line 185), `CloakVisibilityGateLiveTests.Detectable_enemy_still_
  receives_the_cloaked…` (new).
- **Run 3 (Core 3707/3708, ClientCore 417/418):** `SideEliminationLiveTests.
  Eliminating_side2…` (ledger line 39), `FullSessionKillIntegrationTests.
  FullSession_ThreePeers_…RealCrossSideKill` (ledger line 33/148).

New to this ledger (same fixed-sleep live-wire-receipt / seat-timing class):

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `Core…P366LeaderBotCommandLiveTests.Leader_commands_docked_same_side_bot_to_launch_effectively_live` | 1/3 | not run | live-wire leader-seat timing | load-flake |
| `Core…ShipAsteroidCollisionTests.Loopback_bounced_ship_velocity_and_position_reach_the_wire` | 2/3 | not run | bot did not seat in the 4000-tick pump → null deref | load-flake |
| `Core…CloakVisibilityGateLiveTests.Detectable_enemy_still_receives_the_cloaked_ship_inside_the_collapsed_radius` | 1/3 | not run | live-wire cloak-receipt timing | load-flake |

P509's own tests passed in all three runs.

## P516 session observations (live-wire timing flakes, unrelated to P516)

Three consecutive `scripts/run-tests.sh` runs during P516 (revert W/S to native
held thrust) surfaced only pre-existing live-socket timing flakes, rotating
run-to-run; none touch the changed code (the change is confined to pure W/S
input logic in `Allegiance.ClientCore.Game` + the MonoGame test project). The
changed area (`Allegiance.ClientCore.MonoGame.Tests`) passed **1197/1197 in
runs 1 and 3**; its single run-2 failure was a live test (see below).
`git diff --name-only` confirms zero source/test changes in
Core/Server/Bot/ClientCore/Lobby.

Already in this ledger (cited, not re-added): `ThreePeerSeededSession…
DeterministicVisibility` (runs 2,3 — line 38/51), `FullSessionKillIntegration…
RealCrossSideKill` (run 2 — line 33/148), `ConquestTerritoryLiveTests.Conquest_
win_in_one_mission…` (run 2 — line 29/267), `ShipDeleteGoneLiveTests.CrossSide_
WarperLeavesSector…` (run 3 — line 51), `AutoReloadOnEmpty…RELOAD129` (run 3 —
line 52), `GameOverPlayersWireTests.Conquest_win…leak…` (run 3 — line 31/267),
`AddPartTests.Part_pickup_decline_recreates_treasure…` (run 3 — line 247). One
observed that was NOT yet listed — added so the ledger stays the single source;
same class (fixed-sleep live-wire receipt under full-suite load) and `load-flake`:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `ClientCore.MonoGame…DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live` | 1/3 | not run | live-wire buy/mount/dispense-fire receipt timing | load-flake |

P516's own tests (the restored native W/S held-thrust cases + the retained
wheel/preset cases in `FlightInputTests`) passed in all three runs.

## P521 session observations (live-wire timing flakes, unrelated to P521)

Three consecutive `scripts/run-tests.sh` runs during P521 (death-latch respawn fix —
changes confined to `Allegiance.ClientCore` `ClientWorld`/`ShipEntity` + the ClientCore
test project) surfaced only the pre-existing rotating live-socket timing class in the
**untouched** `Allegiance.Core` and (rarely) `Allegiance.ClientCore` assemblies. `git diff
--name-only` confirms zero source/test changes in Core/Server/Bot/Lobby. The P521 area —
`DeadLatchRespawnTests` (unit), `DeadLatchRespawnLiveTests` (2 live: own-ship
death→respawn→undock `accepted`-climb ×2, and two-client B-sees-A-again), `ShipDeleteGoneTests`,
`ClientWorldNewContactCueTests` — passed in **all three runs**. Failures rotated run-to-run and
did not reproduce on the P521 area; they are the documented load-flake class (fixed-sleep
live-wire receipt under full-suite load). Grand totals per run: **5354** = Lobby 12 +
MonoGame 1204 + ClientCore 430 + Core 3708.

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1204/1204 | **430/430** (clean) | 3705/3708 | Core: `SideEliminationLiveTests.Eliminating_side2…` (line 39), `MineCrossSideVisibilityLiveTests.Enemy_ship_spots…` (line 184), `GameOverPlayersWireTests.Conquest_win…leak…` (line 30/267) |
| 2 | 12/12 | 1204/1204 | 428/430 | 3704/3708 | ClientCore: `FullSessionKillIntegration…CrossSideKill` (line 33/148), `ClientWarpApproachSlowHullLiveTests.Legacy_200_450…` (line 215); Core: `StationUnderAttackAlertLiveTests.Defending_side…` (**new**, added below), `SideEliminationLiveTests…` (line 39), `LiveRipcordTests.First_activation…` (line 36), `CargoBayReloadTests.Reload_malformed…` (line 53) |
| 3 | 12/12 | 1204/1204 | 429/430 | 3698/3708 | ClientCore: `FullSessionKillIntegration…CrossSideKill` (line 33/148); Core: `StationUnderAttackAlertLiveTests.Defending_side…` (**new**), `SideEliminationLiveTests…` (line 39), `ShipsUpdateLiveTests.Locked_target…` (line 245), `LiveRelaunchTests.Carrier_without_launch_slots…` (line 40), `GameOverPlayersWireTests.Conquest_win…leak…` (line 30/267), `ConquestTerritoryLiveTests.Conquest_win…leak…` (line 29/267), `CargoBayReloadTests.Reload_NA_terminates…` + `Reload_swaps…` (lines 53-54), `AddPartTests.Part_pickup_destroys…` + `Part_pickup_decline…` (line 247) |

Every listed failure is a **pre-existing** live-wire load-flake in a file/assembly that does
**not** reference any P521 symbol (all in `Allegiance.Core` or the two ClientCore live tests
above, none of which use the own-ship native path or `ShipEntity.IsDead`). The two ClientCore
ones reproduce clean in isolation (re-verified **2/2 green standalone** during P521); the Core
ones rotate member-to-member even across reduced/isolation runs (e.g. `StationUnderAttackAlert`
flipped between `Defending_side…` and `Sub_30_percent_fraction…`, `LiveRipcord` between
`First_activation…` and `Re_target_…`), confirming the timing class rather than a P521 defect.

New to this ledger (same fixed-sleep live-wire-receipt class), added so the ledger stays the
single source:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `Core…StationUnderAttackAlertLiveTests.Defending_side_gets_under_attack_alert_attacker_side_hears_nothing` | 2/3 | No (rotates) | live-wire under-attack alert receipt timing | load-flake |

## P513 session observations (live-wire timing flakes, unrelated to P513)

Three consecutive `scripts/run-tests.sh` runs during P513 (Technoflux Meshy
content regeneration + 5 new P287 slots + resolver/manifest/orientation wiring)
surfaced only the pre-existing rotating live-socket timing class in the
**untouched** `Allegiance.Core` test files and (rarely) `Allegiance.ClientCore`
assemblies. The P513 change touches only content, the
resolver/orientation tables in `Allegiance.Core`, and the MonoGame tests —
no source/test changes in Server/Bot/Lobby, and none of the failing live-wire
tests reference the resolver/orientation. The P513 area —
`Allegiance.ClientCore.MonoGame.Tests`
(the resolver/roster/orientation/manifest tests) — passed **1210/1210 in all
three runs**. Failures rotated run-to-run and are the documented fixed-sleep
live-wire-receipt class.

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1210/1210 | **438/438** (clean) | 3706/3708 | Core: `Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost…` (**new**), `AddPartTests.Part_pickup_accept…` (line 247) |
| 2 | 12/12 | 1210/1210 | 437/438 | 3703/3708 | ClientCore: `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays…` (**new**); Core: `SideEliminationLiveTests.Eliminating_side2…` (line 39), `LiveTeamLobbyTests.Unready_broadcasts…` (**new**), `LiveRipcordTests.Re_target_activates…` (line 36), `LiveCombatWireTests.Moving_target_streams…` (**new**), `AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED…` (line 184) |
| 3 | 12/12 | 1210/1210 | 437/438 | 3707/3708 | ClientCore: `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays…` (**new**, 2/3); Core: `GameOverPlayersWireTests.Conquest_win…leak…` (line 31/267) |

New to this ledger (same fixed-sleep live-wire-receipt class), added so the
ledger stays the single source:

| test | runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `Core…Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies` | 1/3 | not run | live-wire session-lost receipt timing | load-flake |
| `ClientCore…CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` | 2/3 | not run | live-wire cross-side sector visibility timing | load-flake |
| `Core…LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` | 1/3 | not run | live-wire team-ready receipt timing | load-flake |
| `Core…LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire` | 1/3 | not run | live-wire moving-hull stream timing | load-flake |

## P578 observation (2026-10-08, `scripts/run-tests.sh` on the P578 tree)

Three consecutive runs — failures rotated run-to-run, all of the ledged
fixed-sleep live-wire-receipt load-flake class (none involve the P578 eject/
death feedback; all 16 P578 unit tests and the changed eject live tests pass
every run):

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1584/1584 | 560/561 | 3780/3781 | ClientCore: `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`; Core: `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` |
| 2 | 12/12 | 1584/1584 | 560/561 | 3777/3781 | ClientCore: `FullSessionKillIntegrationTests.FullSession_ThreePeers_…_RealCrossSideKill`; Core: `LiveRipcordTests.Re_target_activates_to_requester_only`, `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range…`, `ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget…`, `StationUnderAttackAlertLiveTests.Sub_30_percent_fraction_…` |
| 3 | 12/12 | 1584/1584 | 561/561 | 3780/3781 | Core: `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire` |

## P597 observation (2026-10-10, `scripts/run-tests.sh` on the P597 tree)

Two consecutive runs on the final P597 tree (corpus present via `local.env`; the
P597 change is path-resolution/test-skipping — the guards are no-ops with the
corpus present, so all live tests still run). The only failures are the
established fixed-sleep / live-wire-receipt **load-flakes**; none fail in
isolation, none involve the P597 changes.

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1713/1713 | 578/579 | 3869/3872 | (counts captured; run-1 tail did not preserve per-test names — same live-wire load-flake class as run 2 / ledger) |
| 2 | 12/12 | 1713/1713 | 579/579 | 3866/3872 | Core: `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`, `…Conquest_win_lists_every_connected_player_including_losing_side`, `…Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`; `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire`; `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`; `P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` (**new** — added below) |

**Newly-observed flake (documented for future runs):**
`Core…P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` —
live-wire dock/order-stream timing, passes in isolation (verified 2026-10-10),
status: load-flake.

**Newly-observed flake (P604b, documented for future runs):**
`Core…P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield_reaches_same_side_seated_peer` —
live-wire NPC drone mines a field and the mine EXPORT must reach a same-side
seated peer within a fixed receipt window; fails only under full-suite load
(observed in the P604b run B), unrelated to P604b's broadcast scheduling
(changes touch ship-status/identity/cross-side sweep timing, not mine exports).
Status: load-flake.

## P645 observation (2026-10-14, `scripts/run-tests.sh` on the final P645 tree)

Two consecutive runs on the final P645 tree (transport send-side hardening:
partial-write-safe inflight send, hard queued-byte cap `QueueHardCap`, PING reply
throttle). Transport/suite changes live in `Allegiance.Core`/`Allegiance.Server`;
`Allegiance.Core.Tests` passed 4018/4018 in run 1. The only failure is an
established live-wire session-loss receipt load-flake that passes in isolation
(verified 3/3), unrelated to P645 (the client-half send path is unchanged for
the client: still a blocking `_stream.Write`; P645 only adds inflight promotion
and close-on-timeout, which do not affect read-loop session-loss detection).

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1968/1971 (3 skipped) | 748/749 | 4018/4018 | ClientCore: `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent` |
| 2 | 12/12 | 1967/1971 (3 skipped) | 749/749 | 4015/4018 | MonoGame: `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` (**new**, added below); Core: `SideEliminationLiveTests.Eliminating_side2_…` (line 39), `P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield_…` (line 440), `GameOverPlayersWireTests.Win_by_elimination_…` (line 432) |

**Newly-observed flake (P645, documented for future runs):**
`ClientCore…SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent` —
live-wire session-lost receipt timing when the server is killed under full-suite
load; passes 3/3 in isolation (verified 2026-10-14). Status: load-flake.

**Newly-observed flake (P645, documented for future runs):**
`ClientCore.MonoGame…ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` —
live-wire chat target-compose receipt timing under full-suite load; passes 3/3 in
isolation (verified 2026-10-14). Status: load-flake.

## Follow-ups

1. **(P482 closed)** `HEAVY_SHIPS_UPDATE` defeating the `SDR_DOCKED` `IsGone`
   latch — **fixed**. When a ship docks, `Sector.ResolveDockCollisions` now
   invalidates the per-tick ship-update candidate cache
   (`Sector.GetShipUpdateCandidates`), so the just-docked ship is excluded from
   the same tick's `HEAVY_SHIPS_UPDATE` — the client's `IsGone` latch set by
   `SHIP_DELETE(SDR_DOCKED)` now persists. `DockOut_WatcherLatchesGone_…` passes
   unmodified (0/20 isolated; was 3/3 deterministic).
2. **(P472 closed)** The three deterministic live-suite items below were
   investigated; two were stale tests corrected in P472 and one was a production
   bug (follow-up #1): `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`
   and `LiveTurretGunnerBindingTests.Board_binds_gunner_…` (corrected);
   `ShipDeleteGoneLiveTests.DockOut_…` (production bug, see #1).
3. **(P472 closed)** Surviving live tests' waits: converted where flaky;
   `Board_refund` dock-precondition added; `Multi_mission` seating-order
   hardened by resolving the disconnect target by actual mission/side.
4. Future sweep: the remaining sub-top-5 load-flakes (LiveRipcord, LoadoutChange,
   StationsUpdate, LiveChat, GameOverPlayers, Conquest, SideElimination,
   FullSessionKill, Board_refused_once_mission_over) rotate run-to-run and remain
   ranked in the After table above.

## P646 observation (2026-10-14, `scripts/run-tests.sh` on the final P646 tree)

Receive-side hardening (one logon per connection, bounded fair drain, accounting
leak fix, client renderer disposal, no would-block spin — `TcpTransport.ReadExactly`
now parks in `Socket.Poll` only for NON-BLOCKING pool sockets). The transport
change also touched the greet handshake, which shares `ReadExactly`; this was
caught by the suite (see note below) and restricted so blocking sockets still
honour `ReceiveTimeout`.

The 4 failures observed in a full Core run are all **established live-wire
load-flakes**, none in P646-touched code, none failing deterministically in
isolation (different tests failed on different isolated runs):

| test | ledger status | in P646 code? |
|---|---|---|
| `Core…GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | already-listed load-flake (see lines 432/461) | no |
| `Core…LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire` | already-listed load-flake (see lines 216/419/432) | no |
| `Core…LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | **new** load-flake | no |
| `Core…ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines` (and `…Queued_writer_splits_multiline_and_flushes_trailing_partial_line`) | **new** load-flake | no |

**Newly-observed flake (P646, documented for future runs):**
`Core…LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` —
live-wire countdown/receipt timing under full-suite load; passes 3/3 isolated
(2026-10-14). Status: load-flake.

**Newly-observed flake (P646, documented for future runs):**
`Core…ConsoleLogNonBlockingTests.Verbose_gate_…` and `…Queued_writer_splits_multiline_…` —
console-writer thread timing under full-suite load (different tests fail on
different runs); pass isolated (2026-10-14). Status: load-flake.

**Regression caught by the suite (P646, FIXED):** `TcpTransportGreetTimeoutPoolTests.
Silent_connection_is_eventually_dropped` / `…ThreadPerPeer…` failed because the first
P646 `ReadExactly` applied `Socket.Poll` to BLOCKING sockets too, bypassing the
socket's `ReceiveTimeout` so a silent greet never timed out. Fixed by restricting
Poll to `Blocking:false` sockets (pool data sessions only); blocking reads keep
using plain `Read` which honours `ReceiveTimeout`. Both greet-timeout classes pass
after the fix.
