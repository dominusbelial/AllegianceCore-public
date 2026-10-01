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
| `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill` | 1/3 | not run | live-wire timing | suspect (P472) |
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
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` | 3/3 | **Yes** | (unchanged) session-view watch latch | real bug (P472) |
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
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` | 3/3 | **unfixed** | **fails 3/3** | **PRODUCTION BUG** — one-shot `SHIP_DELETE SDR_DOCKED` latch is defeated by a same-tick `HEAVY_SHIPS_UPDATE` (client `Apply(HeavyShipsUpdate)` clears `IsGone`); test correct, not modified. See follow-up #1. |
| `ClientCore…JoinFlowLiveTests.EntityTable_TracksARealTickingServer` | 3/3 | 0/20 | clean 3/3 | **stale test** — not a dup entity; server boots P382 initial-miner NPCs; scoped single-ship assert to the OWN ship (unique `IsMine`, no dup own-ship entity). |
| `Core…LiveTurretGunnerBindingTests.Board_binds_gunner_…roundtrips_live` | 3/3 | 0/20 | clean 3/3 | **stale test** — P209 orientation gate now propagates rotation to observers; scenario 7 rewritten to assert the orientation-only change DOES reach the observer carrying the rotated facing. |
| `Core…LiveMoneyTests.Board_refund_…` | 2/3 | 0/20 | clean 3/3 | **test race** — board handler refuses unless both ships docked; test raced the dock. Added dock-precondition wait (+ generous MONEY_CHANGE ceiling). |
| `Core…LiveWinConditionTests.Multi_mission_…` | 2/3 | 0/20 | clean 3/3 | **test race** — spawn-order vs seating-order mismatch; disconnect target now resolved by actual mission/side. |

**Regression counts:** the four fixed tests each 0/20 isolated and clean in all
three full-suite runs. The suite still contains one known red test —
`DockOut_WatcherLatchesGone_…` (documented production bug) — plus a rotating set
of pre-existing live-socket load flakes below the top-5 that remain out of P472's
scope (left ranked above).

---

## Follow-ups

1. **NEW (P472 finding) — PRODUCTION BUG:** `HEAVY_SHIPS_UPDATE` defeats the
   `SDR_DOCKED` `IsGone` latch. When a ship docks, the server sends the one-shot
   `SHIP_DELETE(20)` `SDR_DOCKED` to same-sector watchers, but the same tick's
   periodic `HEAVY_SHIPS_UPDATE(176)` can still include the just-docked ship
   (per-tick candidate-cache staleness, `Sector.GetShipUpdateCandidates`) and the
   client's `Apply(HeavyShipsUpdate)` unconditionally clears `IsGone`
   (`ClientWorld.cs:1325`). Fix: exclude a ship from the ship-update broadcast
   the moment it docks, or do not clear `IsGone` for a ship that just received
   `SDR_DOCKED`. Until fixed, `DockOut_WatcherLatchesGone_UndockReturnsAndClears`
   will fail (correctly) — do not mask it in the test.
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
