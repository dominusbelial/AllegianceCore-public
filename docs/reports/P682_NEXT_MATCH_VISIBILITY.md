# P682 — combat after the automatic next match

Base: `integ4`, `1d862740`. Changes left in the working tree; no commit or push.

## Root cause and failing-first evidence

The reproduced failure is the bot's missing **next-match purchase and launch**, rather than missing initialization of the fresh mission's visibility system.

`AssignMigratedShip` creates a fresh default ship and calls `DockInitialLoadout`. That is a real server-side hangar seat. `BotManager.RefreshLife` reset navigation and combat intent, and P598 sent PLAYER_READY, but nothing bought or launched the scenario's original combat loadout. `DriveCombat` then treated a non-lifepod reseated ship as a fighter. `Sector.GetShipUpdateCandidates` excludes docked ships; `BotManager.EngageShipAt` refuses docked own-ship state, and the requested scenario weapons were never repurchased. Counting models in a sector therefore overstates the number of flying, armed ships. This explains how moving client poses and occupied sectors can coexist with very small heavy updates and no gunfire.

The ordinary ranked heavy-update path also admits **uncloaked** cluster ships independently of SeenBySide (`BroadcastShipsUpdatePeer.peerSees`); the visibility flag gates cloak-hidden enemies. The cross-side identity sweep uses the corresponding telemetry gate. Missing initial scanner flags alone would therefore not explain losing ordinary enemy heavy entries. The docked-ship candidate exclusion does.

The real TCP/MCP regression uses `McpTools.Handle("spawn_scenario", ...)`, `BotManager`, its probe, two autonomous combat clients and the P660 automatic restart. It checks purchase/launch, each bot's own ClientWorld enemy identity/sector/live pose, advancing enemy pose timestamps, server scanner visibility, real projectile echoes, enemy hits and credited kills **in both matches**, for both `port` and `native` update modes.

On the unmodified behavior, both modes fought in match 1 and timed out at **match 2 combat hulls bought and launched**. The final conquest version was also rerun with only the new launch call disabled: match 1 recorded 6 shots, 4 enemy hits and 2 credited kills; match 2 timed out at the same launch assertion. Restoring the call makes both matches fight. No enemy-visibility bypass or manual visibility initialization was added.

For repeatability the fixture places the two already-launched combatants 300 units apart away from rocks/stations, holds their contact geometry, and lowers hull/shield health before measuring kills. Target selection, control messages, weapons, projectile collisions, kill credit, exports and pose updates remain production code. The first game's end is accelerated with lethal enemy-base damage, then the same station removal/site/conquest/elimination chain used by projectile impact; it does not invoke `DeclareGameOver` directly. Intermission, reseating, ready-up, countdown and the second start use the production P660 path without reconnecting.

## Change

- A mission-generation/session change marks combat bots as awaiting their next-match launch. After world sync and Started (when the starting-money payday has run), they use the existing docked BUY_LOADOUT(fLaunch) purchase/ACK/retry path to restore their requested scenario loadout. Navigation and combat wait for a successful buy ACK and the authoritative launch/hull state. Briefing cannot exhaust those retries against an unfunded wallet. The bounded retries and grant validation remain shared with pod relaunches.
- Initial next-match purchases do not count as pod cycles or prepare a finale bomber. Finale restoration still supplies the original hull/parts before this purchase.
- Clear ClientWorld's previous mission global attributes and bucket funding on both rotation and reconnect.
- Retire a migrated viewer's update-age cache immediately, releasing old Ship/Sector references while merging its current byte/age window into the existing retired-viewer accounting. Fresh broadcasts allocate fresh viewer state.
- Invalidate sector warning baselines for the rebuilt sides, so identical map assets are reannounced to clients that just cleared their warning state. Re-encode the first mission's CLUSTERINFO after rebuilding, including MapGen rebuilds.

## Native source rules

All references below are relative to `~/Development/Allegiance/src/`, read from the local native checkout.

| Rule | Native file:line |
| --- | --- |
| Mission initialization binds the site and seeds the update clock. | `Igc/missionigc.cpp:3055` |
| Side creation seeds fresh attributes, tech/counters and creates active side objects. | `Igc/missionigc.cpp:3734`, `Igc/missionigc.cpp:3750`, `Igc/missionigc.cpp:3771` |
| ProcessGameOver resets side readiness/activity/money, resets sides and the mission, and arms the auto-restart deadline. | `FedSrv/fsmission.cpp:3493`, `FedSrv/fsmission.cpp:3524`, `FedSrv/fsmission.cpp:3539` |
| Vacate resets players and deletes drones; ResetMission removes clusters and destroys side buckets. GenerateMission builds the next world; StartGame restores civilization attributes before generating it. | `FedSrv/fsmission.cpp:2604`, `Igc/missionigc.cpp:3804`, `Igc/missionigc.cpp:3831`, `FedSrv/fsmission.cpp:1943` |
| Fresh visibility entries start unseen, not current-eyed, with no last spotter. Model SeenBySide/GetCurrentEye read the thing-site flags. | `Igc/igc.h:4664`, `Igc/modelIGC.h:120`, `Igc/modelIGC.h:128` |
| Cluster visibility updates run every ten passes, iterate the mission's sides, always reveal own-side models and test scanners for enemies. | `Igc/clusterIGC.cpp:348`, `Igc/clusterIGC.h:22`, `FedSrv/FedSrv.CPP:10878`, `FedSrv/FedSrv.CPP:10905`, `FedSrv/FedSrv.CPP:10922` |
| A fresh side entry seats the player at its base; BUY_LOADOUT requires a station, purchases the requested loadout, acknowledges whether it fully succeeded and launches by clearing the station. Ready alone is insufficient. | `FedSrv/fsmission.cpp:791`, `FedSrv/FedSrv.CPP:6151`, `FedSrv/FedSrv.CPP:6189`, `FedSrv/FedSrv.CPP:6247`, `FedSrv/FedSrv.CPP:6282` |
| Cluster warnings derive from cluster assets; resetting their port-side baseline makes the fresh client receive that derived state again. | `WinTrek/trekigc.cpp:327` |

## Rebuild-state audit

| State | Finding / disposition |
| --- | --- |
| Mission/sides and per-side eyes | `AssembleGameMission` already calls `Mission.Assemble` on every rebuild, with the current sim time. Assembly creates new sides, gives every sector the fresh side list and mission reference, and creates new visibility holders. The normal sector sweep runs after Starting. Both-match scanner/pose tests exercise this. |
| Stations / asteroids | Rebuilt runtime objects and visibility holders, rather than old-world references. The state regression checks new station/rock object identities. Existing own-side/scanner/sync rules remain intact. |
| Treasures | Sector-owned models belong to the discarded world; new-world treasure population follows the configured map/treasure policy. ClientWorld clears pickups and the join/sector snapshot repopulates them. No persistent per-viewer treasure export cache. |
| Probes / mines | Old sector models are discarded. New sectors start without old deployed probes/mines; client maps reset. Migration clears recipient export/status latches, including cross-side mines. |
| Cluster warnings / graph | Fixed warning-baseline reset and CLUSTERINFO invalidation as described above. |
| P632/P659 ranking / viewer age | Candidate list/deviation belong to fresh sectors/ships. ViewerPoseCadence already detects changed viewer/sector/side on Begin, but retained old references until that next Begin; explicit retirement now removes them at reseat and preserves the byte window. |
| P661/P670 accounting | Transport connections survive rotation. Their queues and transport byte/stall counters must continue across it. They are not reset; viewer-cache retirement uses the existing accounting merge path. The state test checks a nonzero ships-byte window survives retirement. |
| `_peerShips` | Migration replaces each LiveShip with a fresh ship and sector, then uses the normal join burst. The combat regression verifies mission and sector references after restart. |
| Bot event counters | EventLog clears retained records but keeps sequence numbers monotonic. Preserve the existing same-session kill cursor; the end-to-end proof checks that second-match credited kills are counted. |
| P656c CREATE_BUCKETS / P672 roster | Join sync already re-sends bucket/side state and the mission's score-roster baseline is removed at rotation. Neither causes the reproduced missing launch. The newly found client attribute/funding reset omissions are fixed. |
| Finale station objectives | RefreshLife restores the original scenario, marks objectives pending, waits for the fresh graph, and resolves station/sector objectives from the new ClientWorld. The opt-in P660 15v15 MCP proof checks migration and fresh finale rearming; the new purchase path avoids advancing pod maturity or converting a fresh fighter into a strike bomber. |

## Verification

The existing finale status/reset fixture also now checks that a fresh hangar seat cannot route until a successful purchase ACK and launch; it verifies the original bomber role is restored without increasing pod maturity. Its previous immediate-route assertion failed 3/3 under the corrected lifecycle and was updated accordingly.

Final build and test results are recorded in [P682_assets/verification.txt](P682_assets/verification.txt).

| Final check | Result |
| --- | --- |
| Solution build | 0 warnings, 0 errors |
| New/affected regression methods | 3/3 isolated runs each; port and native both see advancing enemy poses and score kills in match 2 |
| P660 lifecycle/countdown methods and opt-in 15v15 MCP showcase | 3/3 isolated runs each; 30/30 bots migrate, 2/2 humans stay connected, finale rearms |
| Core (unfiltered) | 4355 pass; 1 existing quarantined transport shutdown failure, then 3/3 isolated passes |
| ClientCore (final unfiltered run) | 953 pass, 0 failures, 0 skips |
| MonoGame (unfiltered) | 2143 pass, 0 failures, 3 optional Meshy-pack skips |

An intermediate ClientCore run also hit the existing warp-return visibility timing test. It passed 3/3 exact isolated reruns on the final tree and passed in the final full suite. Both timing observations are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md); no timing thresholds or quarantine traits were changed. The deterministic old finale-fixture failure was corrected rather than classified as a flake.

Reproduce the focused loopback checks from the repository root after building:

```sh
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~P682'
ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~P660ShowcaseLiveTests'
```

The checks include each new/changed regression method three times in isolation, both P660 live lifecycle methods and countdown methods three times, and the opt-in 15v15 MCP showcase/finale rotation proof three times. Then Core, ClientCore and MonoGame run once each after a build, sequentially, including live TCP tests. Any suite failures are rerun three times individually and classified using `docs/TEST_FLAKES.md`.

The build uses `NUGET_HTTP_CACHE_PATH=/tmp/p682-nuget-cache`, `-p:UseSharedCompilation=false` and `-nodeReuse:false`: the sandbox cannot write the default NuGet cache or use another sandbox's compiler server. No dependency or repository configuration was changed for this.

This is loopback evidence. The clash44b archive and the rig/221/200v200 validation remain in Claude's lane as instructed; they were not run here.

## P682b — maturity after the second match's initial launch

Base: `integ8`, `86770578`. Changes remain in the working tree; no commit or push.

### Reproduction and cause

The final regression was built and run with both production files restored to their
unmodified `integ8` contents, then with the fix restored. On the original code,
match 1 completes its three pod cycles and launches a verified finale bomber.
Match 2 starts, the server buys and launches the original hull, the bots receive
their ejections, and the server rescues, buys and launches them again. The test
then fails at **“match 2 cycle 1 counted real pod to relaunch; counters=0,0”**.
The server relaunch assertion passed before this counter assertion. The focused
wire/controller tests also fail for partial grants, immediate ejection after a
complete grant, and the retained finale restoration record; the ordinary complete
grant control case passes. See [before/after evidence](P682b_assets/proof.json).

`LaunchNextMission` left `MissionLaunchPending` set until `FBoughtEverything`
was true and the latest own hull still equalled the requested hull. Those tests
were stricter than the existing spawn policy and actual server launch evidence:

- The real catalogue's Stealth Bomber preferred loadout produces a partial parts
  grant in this fixture. The server nevertheless sends `FLaunch=true` and actually
  undocks it. The marker never cleared, so `DriveCombat` returned before
  `UpdatePodState` on every subsequent pod life. Later purchases still happened
  through the initial-launch path, which intentionally excludes pod maturity.
- Even a complete grant can be followed by launch, ejection and docking on the
  receive thread before the next decision pass. Requiring the latest hull to
  remain the purchased fighter/bomber can leave the same marker stuck.

The fix consumes the initial launch once its own purchase ACK confirms launch of
the requested hull or a legitimate successor, and the authoritative own state is
flying that hull or has already become a pod. An ACK alone while still seated in
the combat hull's hangar does not complete launch. Refused ACKs remain rejected;
grant warnings, bounded docked retries and strict finale strike-grant validation
remain in use. The initial purchase never calls `ResetNewLife` as a reinforcement
and never increases maturity. Subsequent observed pod → dock → launch cycles use
the existing normal lifecycle and each increase `CompletedPodCycles` once.

### Rotation audit

| State | Evidence / disposition |
| --- | --- |
| Per-bot pod counter and initial-launch marker | All four loopback configurations check zero cycles on each match's initial launch, then exactly 1, 2 and 3 completed cycles per bot in matches 1 and 2. Maturity counts distinct relaunched bots, so its per-side numerator remains 1/1 after the first cycle rather than increasing to 3/1. Wire tests cover complete/partial grants and immediate ejection/docking, including a refused ACK and an ACK arriving before SET_CLUSTER. |
| Bot identity / run membership | The same MCP bots and TCP sessions survive both rotations. Each match uses a new mission object and a new `FinaleRun`; its existing generation guard rearms only after fresh objectives resolve. Counters are read from those current bots, rather than an old ship-ID set. |
| Kill window and leader baseline | `RegisterFinale` already creates a fresh kill queue and captures fresh `InitialKills`. Every new run, including match 3, starts with zero recent kills. Real server damage/ejection messages in the common combat sector refill the current run's kill window and satisfy the configured combat floor. |
| Selected sides / timestamps | Every new run starts armed with no selected sides and null trigger, strike and game-over timestamps. Matches 1 and 2 select the configured side 0, trigger, launch a verified strike and observe game over. |
| Bomber and escort wings | **Additional bug fixed:** `RestoreFinaleForNextMission` restored the scenario but retained `FinaleOriginal`. `PrepareFinaleRelaunch` therefore returned early on later runs, preventing fresh bomber/escort assignment. Clear that saved record immediately after restoration. The controller regression assigns and restores both roles across two runs; the loopback requires a verified bomber launch in match 2. New-run reservation, assignment and escort counts start at zero. |
| Station objectives | Each run resolves its target side, station and sector against the current mission. This map legitimately reuses station IDs across rotations, so the loopback checks fresh mission/run objects and current station membership rather than assuming IDs change. The existing rearm regression separately replaces the station IDs and requires the new objectives. New-run target-death timestamps start empty. |

The loopback extends the P682 fixture: real `McpTools.Handle("spawn_scenario")`,
`BotManager`, TCP clients and P660 automatic intermission/reseat/ready/countdown.
It uses two real bomber bots, all-tech shop visibility for a fully verified strike
loadout, a 100% relaunch threshold and a one-kill floor. For repeatability it holds
both combatants far apart in the same sector, applies lethal enemy ship damage
through `Ship.ReceiveDamage`, and accelerates pod travel by invoking the production
station-rescue path. It does not set maturity counters, bot pod flags, run trigger
timestamps or finale wings. After the controller triggers and launches the strike,
the existing P682 lethal-base-damage helper follows station removal, site events,
conquest and elimination to game over. The test never calls `DeclareGameOver`.
Both match endings are accelerated this way; match 3 must start automatically
and rearm with empty run state. This is lifecycle/rotation evidence, not a timed
full-map bomber flight or a rig battle.

### Native source rules

All native references below were read locally and are relative to
`~/Development/Allegiance/src/`. Finale maturity and wing selection are MCP policy.

| Native rule | Native file:line |
| --- | --- |
| BUY_LOADOUT requires a docked, non-ghost player with a valid side and no parent. | `FedSrv/FedSrv.CPP:6153`, `FedSrv/FedSrv.CPP:6160`, `FedSrv/FedSrv.CPP:6172` |
| PurchaseShipLoadout returns whether everything was bought; the ACK separately carries bought-everything and actual launch flags. Native computes launch as requested AND fully bought, then clears the station on launch. | `FedSrv/FedSrv.CPP:6189`, `FedSrv/FedSrv.CPP:6247`, `FedSrv/FedSrv.CPP:6258`, `FedSrv/FedSrv.CPP:6282`, `FedSrv/FedSrv.CPP:6291` |
| Ejection swaps the same player's ship to the civilization lifepod; true death can restart the pod docked at a friendly restart station. | `FedSrv/FedSrv.CPP:11917`, `FedSrv/FedSrv.CPP:11942`, `FedSrv/FedSrv.CPP:12034`, `FedSrv/FedSrv.CPP:12052`, `FedSrv/FedSrv.CPP:12064` |
| Rescue resolves a friendly restart station and docks the rescued player. | `FedSrv/FedSrv.CPP:13066`, `FedSrv/FedSrv.CPP:13076` |
| Lethal station damage raises KillStationEvent; game over resets side readiness/activity/money and mission state, then arms automatic restart. | `Igc/stationIGC.cpp:189`, `FedSrv/FedSrv.CPP:12237`, `FedSrv/fsmission.cpp:3493`, `FedSrv/fsmission.cpp:3524`, `FedSrv/fsmission.cpp:3539` |

The port already permits a partially bought loadout to launch in
`Server.ShipCommands.cs` (`ApplyLoadout`, ACK construction and `PerformUndock`).
That differs from native's bought-everything launch gate above. P682b does not
change that server policy: the MCP now honors its own launch ACK plus actual own
state, consistently with the existing partial-grant warning policy at spawn.
It does not claim that native launches partial purchases.

### Verification

Final results are recorded in [the invocation ledger](P682b_assets/results.json),
with concise before/after lifecycle evidence in [proof.json](P682b_assets/proof.json).
Raw build/test logs and TRX files are retained under `/tmp/p682b-validation/`.
The final solution build passed with **0 warnings and 0 errors**.

| Final check | Result |
| --- | --- |
| Solution build | 0 warnings, 0 errors |
| New/affected methods, each three times in isolation | 19 methods / 32 parameter cases × 3 = **96 passed**, zero failures or skips |
| Additional enabled local P660 15v15 MCP showcase | **3/3 isolated passes**, also passed in the full ClientCore run |
| Core, unfiltered | **4,555 passed**, zero failures or skips |
| ClientCore, unfiltered, local P660 15v15 showcase enabled | **1,010 passed**, zero failures or skips |
| MonoGame, unfiltered | **2,186 passed**, zero failures; 3 existing optional Meshy-pack skips |

That is **99 passing focused case executions** and **7,751 passing full-suite
cases**. There were no suite failures to isolate or classify as load flakes, so
`docs/TEST_FLAKES.md` did not need an entry. The 19 directly affected methods ran
before the three sequential suites; the additional isolated showcase repetitions
ran afterward, on the same built production/test tree. The reproduction runner
includes that showcase with the other focused methods before the suites.

Reproduce after building the solution:

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p682b-nuget-cache dotnet build AllegianceCore.slnx \
  -p:UseSharedCompilation=false -nodeReuse:false
python3 docs/reports/P682b_assets/validate.py
```

The runner executes each new/affected method three times in isolation, including
all port/native × pipeline off/on cases, then unfiltered Core, ClientCore and
MonoGame sequentially. It enables the local P660 15v15 showcase. Any suite failure
is rerun three times by exact method for the brief's load-flake classification.
Rig/221/200v200 checks remain in Claude's lane and were not run here.
