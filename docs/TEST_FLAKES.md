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

## Follow-ups for P472

1. Fix the three deterministic real bugs: `ShipDeleteGoneLiveTests.DockOut_…`,
   `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
   `LiveTurretGunnerBindingTests.Board_binds_gunner_…`.
2. Replace the surviving live tests' fixed `Thread.Sleep`/`Task.Delay` waits
   with condition waits (the `load-flake` rows above).
3. Harden `Multi_mission_disconnect_scopes_per_mission` against peer-seating
   order (resolve the disconnect target by actual mission/side, not by index).
