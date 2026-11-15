# R1 side switch reset — P1026

F2 is fixed. Both original F2 proof tests are unskipped with their inputs and assertion meanings unchanged. Changes are left in the working tree; no commit or push was made.

## Root cause and resulting behavior

The reviewed departure helper changed `SideId` while deliberately retaining the ship's station, sector, hull and inventory (`Server.Lobby.cs:2434-2474` before this fix). The join helper only changed sides (`:3879`), and `Server.SideIntegrity.cs:50-52` checked the current side without remembering the last real side. This allowed an enemy to buy and launch from its former base and allowed QUIT_SIDE to bypass no-defections. The purchase lookup (`Server.ShipCommands.cs:2629-2638` before this fix) independently accepted a foreign dock.

The corrected shared departure path is `src/Allegiance.Server/Server.Lobby.cs:2435` (`LeaveSideForSwitch`). QUIT_SIDE, direct auto-accepted POSITIONREQ, pending POSITIONREQ/POSITIONACK, and leader/moderation boots using `MoveToTeamLobby` all use it. During Started/Starting it:

1. Drops a carried flag and promotes the first passenger before valuing the departing pilot's hardware. Turret gunners and observers can inherit the ship; the remaining passengers follow the promoted pilot on their existing side.
2. Runs leader succession and donation reassignment, captures wallet plus full remaining hull/mounted/cargo value, and zeroes the wallet.
3. Broadcasts QUIT_SIDE to the mission, then performs the ship's Reset(false): removes station and sector membership, clears mounted and cargo parts, all command representations (including multiplayer waypoint goals), damage history, ripcord, donation, experience and parent/turret membership, and replaces the hull with a lifepod. It retains the player's mission identity and death/ejection record.
4. Parks the player in SIDE_TEAMLOBBY before the old side's immediate payday, so the leaver cannot receive its own surrender payment. A repeat quit from the lobby cannot pay again.

`src/Allegiance.Core/Sim/Ship.SideDeparture.cs` owns the ship reset. `src/Allegiance.Server/Server.SideDeparture.cs` owns passenger handover and running side re-entry. On joining, the player receives the destination civilization's lifepod at that side's first living restart-capable base, the shared side-visible world snapshot, empty inventory acknowledgement, dock/status synchronization and ENTER_GAME. The peer's sector snapshot follows the destination base. A legal purchase can then launch there. The initial-join snapshot code was extracted without changing its contents so both join paths use it.

`LiveShip.LastSideId` (`Server.Lifecycle.cs:50`) records every real-side join and survives running quits. `Server.SideIntegrity.cs:46` applies both native no-defections branches: a remembered different side is refused even from the lobby; a player still on a real side must first leave it. A player in the lobby may rejoin its last side. Old-player persistence restores this memory across disconnect/relogon, separately from the scoring side. Rotation clears the old-world cache; privileged flush/randomize explicitly clears last side.

Pre-start team switches retain their existing hull, parts, station, sector and position, as this brief explicitly requires. No new setting, environment variable, service, port or numeric limit was introduced.

## Native citations and parity verdict

Citations were re-read against the read-only native source supplied with the lane:

| Rule | Native source | Verdict |
| --- | --- | --- |
| Set last side when joining; clear on departure outside Started | `FedSrv/fsmission.cpp:566,864-865` | Native parity |
| Preserve/restore last side in old-player records | `FedSrv/fsmission.cpp:382,2643` | Native parity |
| Both no-defections branches, including last-side rejoin from lobby | `FedSrv/fsmission.cpp:4583-4590` | Native parity |
| Flush/randomize clears last side; full player reset clears it | `FedSrv/fsmission.cpp:5112,5182`; `FedSrv/fsship.h:434-446` | Native parity |
| First child promoted before departure, flying-sector or docked-side PROMOTE before QUIT_SIDE | `FedSrv/fsmission.cpp:904-921,1130-1139`; `Igc/shipIGC.cpp:3510-3580` | Native parity; departure overload accepts observers while explicit turret promotion retains its existing guard |
| Wallet plus remaining full ship value, zero wallet, reset, lobby, immediate old-side payday | `FedSrv/fsmission.cpp:1103-1145,1777-1787`; `Igc/shipIGC.h:396-406` | Native parity; free-spawn hardware counts at full value here, independently of purchase-refund provenance |
| Reset station/cluster/parts/commands/ripcord/experience and detach parent | `Igc/shipIGC.h:487-540`; `FedSrv/fsship.cpp:162-166` | Native parity during running missions; pre-start placement preserved by explicit brief requirement |
| New side lifepod and restart-base hangar; side-visible world and ENTER_GAME | `FedSrv/fsship.cpp:155-158`; `FedSrv/fsmission.cpp:736,791-831,1433-1447,4170-4250` | Native parity using the port's existing docked-sector representation |
| Old observers remove the departed ship | `FedSrv/fsmission.cpp:1130-1139`; `clintlib/appmsg.cpp:2993-3002`; `clintlib/clintlib.cpp:3889-3920` | QUIT_SIDE carries the native reset to clients; no invented destruction message. Native ChangeCluster does not announce an additional exit when the destination cluster is null (`FedSrv/FedSrv.CPP:13515-13525`). |
| Purchase station belongs to buyer | `FedSrv/FedSrv.CPP:6153-6172,13245-13258`; `Igc/shipIGC.cpp:725-739`; `Igc/shipIGC.h:992-1000` | Explicit port hardening of the dock invariant requested by the brief |

The last row needs precision: re-grep found no blanket own-side equality guard in native BUY_LOADOUT itself. Native requires a dock and real side, restricts hostile docking and handles allied rearm separately. The new lookup explicitly requires the buyer's side and soft-refuses a foreign station without changing hardware or launching. Allied automatic rearm remains on its existing path. This guard is classified as hardening rather than falsely citing a native equality check that is absent.

Updated matrix rows: SIM-0489, SIM-0490, SIM-0543, SIM-0554, SIM-0556 and SIM-0608. `docs/parity/p1026-side-departure.csv` records the narrow rules and verdicts; broad native-body rows retain their existing classifications. GLOSSARY.md indexes the new reset and player-memory files.

## Tests

The two original F2 proofs each passed three individual filtered invocations, **1 passed / 0 failed / 0 skipped** per invocation:

- `R1ReviewTests.A_player_who_switches_sides_mid_game_is_not_left_docked_in_the_old_sides_base`
- `R1ReviewTests.No_defections_cannot_be_bypassed_by_quitting_to_the_team_lobby_first`

`P1026SideDepartureTests` adds **13 cases**: flying/docked switches through both immediate and leader-approved joins; wallet plus full free/paid mounted and cargo value with repeat-quit protection; turret and observer inheritance with remaining crew; last-side rejoin and disconnect/relogon refusal; unchanged pre-start placement; foreign-station same-hull launch refusal; boarded-gunner leader boot; and production server wire replay through ConnectionSession proving old-observer removal, destination hangar synchronization and a legal purchase/launch.

The waypoint reset assertion deliberately reproduced a remaining command representation gap in all four switch cases before `GotoWaypoint = null` was added. The final class is rerun three times against the corrected implementation.

One existing expectation changed: `P866RequestPositionSwitchTests` now expects exactly `250 + surrendered hardware value` instead of only 250. Its authored fixture carries a 1-credit hull, so native now pays 251. The original wallet-only assertion contradicted the corrected native rule; inputs and all flag/leader/side assertions remain intact.

`LiveLogoffTests.An_observing_peer_witnesses_loggedoff_ship_delete` also needed its stated same-sector precondition restored: its mid-game side switch now correctly places the leaver at the new side base. The fixture moves the observer and its peer-sector snapshot into that sector before logoff. Its ship-delete, removal and migration assertions are unchanged. Before this fixture correction the preliminary sweep and all three isolated runs failed; the entire corrected class is repeated three times.


Existing classes run individually three times (P/F/S per invocation):

| Class | P/F/S |
| --- | --- |
| `LiveSideSelectTests` (includes P763 auto-seat flag off/on and deadline cases) | 8/0/0 |
| `LiveTeamLobbyTests` | 8/0/0 |
| `LiveTeamLobbyToggleTests` | 14/0/0 |
| `LivePositionRequestTests` | 16/0/0 |
| `P852MissionRulesCertifyTests` | 14/0/0 |
| `P866RequestPositionSwitchTests` | 1/0/0 |
| `RandomizeTeamsLiveTests` | 8/0/0 |
| `P864dTeamLobbyClientListLiveTests` | 2/0/0 |
| `LivePromoteTests` | 5/0/0 |
| `P655GameStateIntegrityTests` | 35/0/0 |
| `LiveLogoffTests` (corrected same-sector fixture) | 10/0/0 |

The final repeat batches passed **408 / 0 / 0** across 42 individual filtered processes: **45 / 0 / 0** for the new/unskipped cases and **363 / 0 / 0** for existing classes, including the corrected logoff fixture. The first bot/showcase/skirmish batch completed **26 / 1 / 1** in 24m36s:

| Class | P/F/S |
| --- | --- |
| `ClientJoinSideLiveTests` | 2/0/0 |
| `SideSwitchDevMaskLiveTests` | 1/0/0 |
| `P745BSkirmishTests` | 1/0/0 |
| `P557ShowcaseFleetTests` | 17/0/0 |
| `SC4ShowcaseLiveTests` | 1/0/0 |
| `P730ShowcaseLiveTests` | 2/0/0 |
| `P700ShowcaseLiveTests` | 2/1/0 |
| `P660ShowcaseLiveTests` | 0/0/1 (requires explicit `ALLEGIANCE_LIVE_P660_SHOWCASE=1`) |

The P700 swapped-home case missed its 480-second objective-arrival count (same longstanding arrival family already ledgered). Three isolated exact-method reruns finished **fail / pass / pass**, with unchanged inputs and assertions:

| Run | P/F/S | Arrival evidence | Battle evidence |
| --- | --- | --- | --- |
| 1 | 0/1/0 | 23/30 at 480.1 s (requires 24) | 4,942 shots / 1,878 hits / 11 kills; longest parked-with-target 2.004 s |
| 2 | 1/0/0 | 24/30 at 346.7 s | 5,217 shots / 1,919 hits / 14 kills; longest 0.706 s |
| 3 | 1/0/0 | 24/30 at 423.9 s | 3,965 shots / 1,629 hits / 12 kills; longest 1.904 s |

All three traces contained zero QUIT_SIDE broadcasts and zero purchase-precondition refusals. The new departure reset was not exercised by this liveness case. The arrival-count intermittency is recorded in `docs/TEST_FLAKES.md`; it was not hidden by increasing the deadline or weakening the required count.

 The explicitly enabled P660 rotation proof passed: all 30 showcase bots and both human clients migrated, and the new finale re-armed. The supplemental rotation/skirmish batch passed **35/0/0** in 3m44s. Supplemental results with `ALLEGIANCE_LIVE_P660_SHOWCASE=1`:

| Class | P/F/S |
| --- | --- |
| `P660ShowcaseLiveTests` | 1/0/0 |
| `P727SkirmishOptionsTests` | 29/0/0 |
| `P727SkirmishLiveTests` | 5/0/0 |

## Gate

The report was created before gate execution. NuGet audit remained enabled. The first build hit the sandbox's read-only default NuGet HTTP cache; redirecting a copy of that cache into the ignored task scratch directory resolved it. No audit suppression or package change was used. Builds use the lane's build-server workaround.

- Solution build: **0 warnings / 0 errors**, including the final waypoint-reset change.
- `tools/build-tools.sh`: **all 140 projects built**, including the final source pass.
- `tools/check-glossary-links.py`: passed, 867 distinct paths.
- `scripts/parity-scorecard.py` on all registered matrices and the new P1026 CSV: passed.
- Final full fast suite: **14,529 passed / 2 failed / 20 skipped**, 568.3 seconds, run with a 10,800-second bound (exceeds the brief's 90-minute minimum allowance). Infrastructure checks and discovery inventory accounting passed. The overall runner exit was **FAIL**, due to the two existing failures below and five passing executions over the calibrated 11.545-second Fast limit. All task cases passed, including the corrected logoff fixture.

| Final fast project | P/F/S |
| --- | --- |
| Lobby | 144/0/6 |
| Update | 55/0/0 |
| MonoGame | 5471/0/4 |
| ClientCore (including quarantine) | 2450/1/0 |
| Core (including quarantine) | 6409/1/10 |

The final failures were `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` (queued mining reports after capacity) and `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (death notification not latched). Each passed **1/0/0 ×3** in separate exact-method runs; their existing receipt-family entries are supplemented in `docs/TEST_FLAKES.md`.

Two earlier sweeps preceded final command-goal/fixture verification: each finished **14,526/5/20**, in 616.2 and 570.4 seconds. The logoff fixture incompatibility was corrected rather than called a flake. Other observed failures—P994 zero-allocation observation (2,784 bytes), game-over stint timing (8.40/8.25 s against [4,8]), NPC pod rescue and independent bot-flight rollout—each passed **1/0/0 ×3** and are ledgered. All eleven distinct timing offenders across the sweeps also passed **1/0/0 ×3**, with exact sweep and rerun durations in the ledger. No threshold or assertion was relaxed.

Logs, TRX results and reproduction commands are retained under ignored `.scratch/p1026/`. The task brief is excluded from the change set. Docker is not installed, and no compose-affecting change was made. No training or HUD behavior was changed. Every launched build/test process finished before handoff.

**Native intent:** leaving a side surrenders its ship and credits that side; no-defections remembers the last side flown, and passengers retain their ship through promotion.

**Modern approach:** one shared departure path, explicit player memory, shared side-world synchronization and focused wire-level tests, while retaining pre-start behavior and existing purchase-refund provenance.
