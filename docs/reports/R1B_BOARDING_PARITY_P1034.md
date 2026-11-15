# P1034 — R1b F3 boarding parity

Status: implementation complete. Focused and existing live checks pass. The
single full fast sweep failed; corrected fixture and isolated flake results
are recorded below.
No commits or pushes. Scope is F3 only. Root-cause line numbers below refer to
the original base; server file names are under `src/Allegiance.Server/`.

## Findings and changes

1. **Observer permission.** `Server.Docking.cs:126` allowed observer boarding
   unconditionally, despite the existing authenticated admin permission. The gate
   now calls `CanUseAdminCheats` for the boarder's actual peer before any refund,
   detach or hardware mutation. Native parity: `FedSrv/FedSrv.CPP:2405-2406`.
   Default players still board equipped, free turret seats; observer fallback
   requires enabled admin cheats, an allowed name and verified provenance.

2. **Cross-sector board / leave / purchase / launch.**
   `Server.Docking.cs:194-198` inherited the station without its sector;
   `Server.ShipCommands.cs:2657-2664` resolved purchases across the mission while
   `Server.Docking.cs:2288-2326,2364` launched using the stale peer sector.
   The live dispatcher regression starts at a friendly forward hangar that
   cannot sell the requested paid capital hull, boards at home, leaves, buys
   that hull and launches: the pre-fix SET_CLUSTER names the old sector.
   Boarding now aligns model membership and peer routing with the carrier;
   `Server.ShipCommands.cs:150` also used the handler's saved peer record for
   the immediate success status. It now reads the refreshed peer record, so the
   first SHIP_STATUS names the inherited station's sector. A separate wire
   regression fails before this final status correction (expected 1053,
   received 1061) and passes afterward. Native: `FedSrv/FedSrv.CPP:2450-2457`.
   Launch independently resolves the docked station across the mission and
   places the ship and crew in that station's sector. Native parity:
   `FedSrv/FedSrv.CPP:2456-2457` (inherit station),
   `Igc/stationIGC.cpp:372-379` (station pose and cluster).
   The port retains sectors for docked models, whereas native docked ships have
   a null cluster. The retained sector must identify the actual station.

3. **Passenger sector transitions.** `MoveShipToSector`,
   `UpdatePeerSectorSnapshot`, `PerformTeleport`, `OnShipWarped` and
   `PerformRipcordTeleport` updated the parent alone
   (`Server.Docking.cs:674,1592-1640,2530-2541`,
   `Server.Combat.cs:2930-2955`). `MoveShipToSector` now
   recursively updates every passenger's model membership and peer sector;
   `UpdatePeerSectorSnapshot` delegates to it. Warp, launch and successful
   ripcord forward source-ship SET_CLUSTER and treasure snapshots to crew via
   `SendBoardedCluster`; docked boarding and teleport move membership without
   announcing a flight. Native parity: `Igc/shipIGC.h:245-254` and
   `FedSrv/fsship.cpp:745-769,918-929`. P1029 is absent on this base; the ripper's
   own missing SET_CLUSTER/cookie transition remains in that separate story.
   This change exposes a shared passenger-forwarding helper for it to call.

4. **Leave audience.** `Server.Docking.cs:279-280` selected all same-side
   peers across missions. The audience now also requires the identical mission.
   Native parity: `FedSrv/FedSrv.CPP:2312-2316`, which addresses the mission's
   `CFSSide` group. No new audience or wire message.

All four fixes implement native behavior. No new deliberate hardening policy,
limits, settings, services, stores, ports, environment variables or artwork.
The existing `liveSettings.adminCheats` settings retain their defaults.
Parity rows: SIM-0771, SIM-0772, SIM-0618, SIM-0673;
additional source-to-proof mapping in `docs/parity/p1034-boarding.csv`.

## Reproduction and focused tests

`P1034BoardingParityTests` boots the real server with artwork, logs in and seats
peers through production datagram dispatch, and decodes captured wire replies.
Warp/ripcord transition callbacks are invoked deterministically on that booted
server; existing live suites cover the complete collision/countdown paths.
The initial baseline: **1 passed, 9 failed, 0 skipped**; those same 10 cases
passed after the fix. After adding the three edge cases and strengthening the
friendly forward-hangar setup, the final unchanged 13-case class was checked
again against the original server source: **1 passed, 12 failed, 0 skipped**.
Every failure is an expected behavior assertion, with no fixture or compile
errors. The exploit reaches the launch and receives the forward-sector ID
instead of home (1053 instead of 1051 in this receipt). Cases cover the three
unauthorized observer configurations and authorized observer, remote purchase
and launch, passenger warp/ripcord/teleport/launch, and mission isolation.
Extra cases cover immediate docked consistency, full turret refusal without
mutations, and repeated same-sector arrivals with two equipped player gunners.
The final status-flush regression brings the new class to **14 cases**.

Existing suites run with artwork:

- Core: `LiveBoardShipTests`, `LiveTurretGunnerBindingTests`,
  `CarrierLandingTriggerTests`, `LiveRipcordTests`,
  `LiveTeleportProbeRipcordTests`, `NpcRipcordTravelLiveTests`,
  `P1026SideDepartureTests`, `P891BoardedPoseTests`,
  `P760ServerCallbacksTests`, `LiveLogoffTests`:
  **81 passed / 0 failed / 2 skipped**. Both established P760 skips report
  that the map has no enemy station in the same sector.
- ClientCore: `P610AlephTrafficLiveTests`, `P891BoardedLaunchTests`,
  `P803TurretRelayTests`, `ClientWorldAlephTransitTests`,
  `P781OwnShipRipcordFlightTests`, `P781RipcordStateTests`,
  `P781RemoteRipcordReckonTests`: **42 passed / 0 failed / 0 skipped**.
- MonoGame: `P803TurretGunnerLoopbackLiveTests`, `TurretGunnerFireLiveTests`,
  `TurretServerClockLiveTests`, `PromoteTurretKeyLiveTests`,
  `RipcordRequestLiveTests`, `AlephTransitDispatchTests`,
  `P781Game1RipcordTests`: **19 passed / 0 failed / 0 skipped**.

Existing observer fallback / stale-turret-loop live fixtures now explicitly
allow authenticated loopback admins, as required by native. Four economy
fixture methods (five cases) in `P653EconomyLoadoutIntegrityTests`
now equip a real turret on the receiving ship instead of depending on ordinary
observer boarding; all wallet/refund/readiness assertions remain intact. Existing
helper reflection fixtures and `tools/hud17-proof/Program.cs` follow its
instance method signature; the proof component now receives the actual server. The two
`P1017BuyFlowLiveTests` crew recipes give their deliberate observer verified
loopback admin permission while leaving the gunner ordinary. `LiveMoneyTests`
equips a real turret for its ordinary bot's boarding/refund proof. No tests
unskipped; no existing assertions removed or weakened.

## Gate

- Solution build: **0 warnings / 0 errors** with build servers, node reuse and
  shared compilation disabled. A writable task-local NuGet HTTP cache is used;
  vulnerability auditing remains enabled.
- Final focused regressions: **14 passed / 0 failed / 0 skipped** in each of
  three isolated runs (after the final status correction).
- Existing live/path coverage above: **142 passed / 0 failed / 2 skipped**.
- Glossary: **877 paths valid**. Parity scorecard: **passed**.
- Tool build: **all 140 projects built**, no warnings or errors, including a
  final sweep after the product corrections. The mechanically updated HUD-17
  reflection caller also builds separately with **0 warnings / 0 errors**.
- Final baseline recheck: **1 passed / 12 failed / 0 skipped**, as expected;
  both product source files restored byte-for-byte afterward.
- Final product/fixture solution build: **0 warnings / 0 errors**.
- One full fast sweep (`--tier fast --no-build`, 3-hour bound):
  **14,595 passed / 12 failed / 17 skipped**, 611.9 s; infrastructure 9/9.
  Seven failures were old ordinary-observer fixture assumptions (four economy,
  two buy-flow, one bot money test), now corrected as described above. Five
  other tests failed and five passing executions exceeded the calibrated
  11.542 s limit. The failed buy-flow and bot-money cases and the failed layer
  case also exceeded that limit. This was a failed full gate; it was run once.
- Corrected fixtures, each class three times in its own process:
  `P653EconomyLoadoutIntegrityTests` **31/0/0**;
  `LiveMoneyTests` **2/0/0**;
  `P1017BuyFlowLiveTests` **7/0/0** (Passed/Failed/Skipped per run).
  All seven task-related full-sweep failures are resolved, with their original
  assertions intact.
- All five unrelated failures and five passing timing offenders: **three
  isolated method runs each**, all passing, all cases below 11.542 s. The table
  below records P/F/S per run and the slowest individual case. Entries and
  exact full-sweep signatures are also in `docs/TEST_FLAKES.md`.

| Test | Full-sweep observation | Three isolated runs (P/F/S; maximum case seconds) |
| --- | --- | --- |
| `P808IdentityTests.Grants_expire_and_only_one_concurrent_poll_consumes_approval` | SQLite error 5: database locked during two concurrent approval polls | 1/0/0 (0.426s); 1/0/0 (0.429s); 1/0/0 (0.423s) |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | Delay 5 computed 5.0000000000000009 above its inclusive 5 s bound (existing floating-point deadline family) | 3/0/0 (0.468s); 3/0/0 (0.557s); 3/0/0 (0.575s) |
| `P591dMinefieldExportAudienceTests.Opposite_side_peer_does_not_receive_the_deploy_mine_export` | Layer did not finish within 60,000 ticks (30.158 s) | 1/0/0 (0.497s); 1/0/0 (0.492s); 1/0/0 (0.507s) |
| `ChaffTests.Live_firing_mounted_mine_launcher_creates_mine_decrements_ammo_and_broadcasts` | Expected mine export absent from the receipt collection | 1/0/0 (0.400s); 1/0/0 (0.361s); 1/0/0 (0.395s) |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Two queued ASTEROID_MINED receipts after capacity (existing mining receipt family) | 1/0/0 (1.745s); 1/0/0 (1.768s); 1/0/0 (1.723s) |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | Passed in 12.990 s, above the calibrated 11.542 s limit | 1/0/0 (3.099s); 1/0/0 (3.067s); 1/0/0 (3.069s) |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Passed civ 25 in 13.437 s, above the limit | 2/0/0 (3.982s); 2/0/0 (3.995s); 2/0/0 (3.992s) |
| `P1000CivProfileTests.Every_loaded_civ_has_immutable_specs_and_legal_routes` | Passed civ 3 in 13.023 s, above the limit | 10/0/0 (1.899s); 10/0/0 (1.720s); 10/0/0 (1.592s) |
| `P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work` | Passed 400 peers / no index / no coalescing in 13.868 s, above the limit | 8/0/0 (1.368s); 8/0/0 (1.361s); 8/0/0 (1.362s) |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Passed civ 5 in 16.920 s, above the limit | 10/0/0 (4.280s); 10/0/0 (4.288s); 10/0/0 (4.264s) |

Only one full fast sweep was run. Subsequent runs were isolated verification
of the corrections and the mandated unrelated-failure reruns. No unrelated
product fixes, test assertion changes, or additional training changes.
`git diff --check` and added-content hygiene pass. No new media or private paths.
All started build/test processes have exited.

Logs and TRX receipts are retained under ignored `.scratch/p1034/`.
The supplied brief is retained there, outside the change set. No Docker check
is needed because no service/deployment configuration changed.

Native intent: boarding transfers a player's station or turret seat, and crew
share their carrier's cluster and guaranteed source-ship arrival messages.

Modern approach: one recursive server relocation helper keeps model membership
and peer routing consistent; one passenger burst helper forwards the existing
protocol, with deterministic wire regressions at the real dispatcher boundary.
