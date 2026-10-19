# SC-6 — research commander attack and repair orders

Base: `a00e842fe424787e4bd0a2b7319fc386b41324b9` (`codex/sc6`). Changes remain in the working tree; no commit or push was made.

## Native behaviour

All native references below are under `~/Development/Allegiance/src/`.

| Rule | Native evidence |
|---|---|
| Attack is command ID **1**, repair **6**; goto **5**, build **9**. | `Igc/igc.h:902`, `Igc/igc.h:907`, `Igc/igc.h:906`, `Igc/igc.h:910` |
| The player selects subjects, verb and target. A selected group sends an individual command to each ship, carrying the target object type and ID. | `WinTrek/console.cpp:1452`, `WinTrek/console.cpp:1477`, `WinTrek/console.cpp:1494` |
| Ship/station targets use guaranteed **CHATMESSAGE**; only a buoy target uses CHATBUOY. The recipient and command ID are the normal chat command fields. | `clintlib/clintlib.cpp:2428`, `clintlib/clintlib.cpp:2434`, `clintlib/clintlib.cpp:2444`, `clintlib/clintlib.cpp:2461` |
| These are player-issued orders, not a leader-only privilege. FedSrv checks the command range and chat budget, suppresses orders on Everyone, relays individual orders to friendly player recipients, and calls CommandToDrone for NPCs. | `FedSrv/FedSrv.CPP:3364`, `FedSrv/FedSrv.CPP:3373`, `FedSrv/FedSrv.CPP:3385`, `FedSrv/FedSrv.CPP:3541`, `FedSrv/FedSrv.CPP:3549`, `FedSrv/FedSrv.CPP:3557` |
| A same-side NPC immediately validates and accepts a real command, releases stay-docked, acknowledges “Affirmative”, and sets its accepted command. | `FedSrv/FedSrv.CPP:3190`, `FedSrv/FedSrv.CPP:3252`, `FedSrv/FedSrv.CPP:3254`, `FedSrv/FedSrv.CPP:3256`, `FedSrv/FedSrv.CPP:3280` |
| A player recipient can accept or reject. Accept requires a legal queued target, sets current and accepted targets, optionally engages autopilot if undocked and not turreted, and clears queued. | `WinTrek/WinTrek.cpp:10650`, `WinTrek/WinTrek.cpp:10680`, `WinTrek/WinTrek.cpp:10683`, `WinTrek/WinTrek.cpp:10696` |
| Attack is legal for wingmen and player pilots against hostile targets, excluding warps/treasures and unavailable docked ship targets. Repair is legal for those pilots against friendly/allied ships or stations. | `Igc/shipIGC.h:1638`, `Igc/shipIGC.h:1680` |
| An attack plan leads the named target, turns/closes and fires under aim/range gates. | `Igc/shipIGC.cpp:2356`, `Igc/shipIGC.cpp:2463`, `Igc/shipIGC.cpp:2599` |
| An accepted legal order supplies the plan before default selection. Repair execution requires a negative-power weapon; it leads the target, steers, closes, dodges/strafe-controls, and fires inside angle/range gates. Full ship hull suppresses repair fire. | `Igc/shipIGC.cpp:1936`, `Igc/shipIGC.cpp:2608`, `Igc/shipIGC.cpp:2626`, `Igc/shipIGC.cpp:2668`, `Igc/shipIGC.cpp:2673` |
| Negative damage restores and clips ship/station hull. | `Igc/shipIGC.cpp:1244`, `Igc/stationIGC.cpp:147` |
| Training demonstrates station attacks and a nanite wingman repairing a bomber under attack. | `training/mission8.cpp:527`, `training/mission8.cpp:606`, `training/mission10.cpp:706` |

The checked native Mission 5 explicitly checks build, mine, goto and attack (`training/mission5.cpp:323`, `:423`, `:512`, `:556`). It has no `c_cidRepair` occurrence in this checkout; repair is demonstrated in Missions 8 and 10 as cited above. Native defines command legality and execution, not this showcase's timing, fleet allocation or tactical ranking.

## Implementation

`ResearchTactics` runs alongside research funding, including after a research plan finishes. It resets with the research epoch and is skipped for a side owned by the human Skirmish commander. Recipients are only this manager's participating, living, undocked, unturreted pilots. Nanite eligibility comes from their actual selected negative-power weapon. Targets come from the ordinary commander and same-side pilot client observations; there is no access to server entities in the production policy. Dead, hidden, unknown, docked, turreted and pod ship contacts are excluded.

The policy assigns strike pilots to enemy stations. Other combat pilots intercept observed enemy bombers within 3,500 metres of a friendly station, otherwise focus local enemy ships. Support pilots repair damaged friendly stations, then damaged local friendly ships, excluding themselves. Local stations precede remote stations; distance and object IDs break ties. These priorities, the threat radius and cadence are showcase policy. The existing legal purchase/refit system continues to supply equipment.

Each research tick sends at most two tactical orders per side. A changed target can supersede after two simulation seconds; an unchanged assignment is refreshed after fifteen. Oldest assignments go first so a large fleet shares the allowance. Ship and station object namespaces remain distinct.

Dispatch is the same `ConnectionSession.SendChat` / **CHATMESSAGE(23)** path used by the human command interface (`src/Allegiance.ClientCore.Game/Game1.Targeting.cs:1110`), with `ChatTarget.Individual`, recipient, command ID and typed target. The existing server relay produces `QUEUED_COMMAND_UPDATE`; `HandleOrders` accepts with a real `ORDER_CHANGE` against the queued slot. Attack uses the existing commanded ship/station engagement and warp routing. Repair now takes priority over autonomous support selection, follows the named ship/station through the existing routing and nanite flight controller, and rechecks friendliness and damage before firing. A healed, captured, docked or dead target releases the local repair order; combat equipment never fires a repair command at a friendly. A repair ship target also clears any old hostile focus target.

SC-1 previously counted only accepted NPC orders. Player-protocol bot commands now count once when the server promotes queued to accepted. Sending or rejecting an order does not increment a counter; replaying acceptance against an empty queued slot does not increment it. NPC counting retains its existing execution gate. Counters are therefore accepted order events, including legitimate renewed assignments, not distinct targets or proof of a hit; execution is separately checked below.

Production files: `tools/allegiance-mcp/ResearchTactics.cs`, `BotManager.ResearchTactics.cs`, `BotManager.Research.cs`, `BotManager.Orders.cs`, `BotManager.Support.cs`, `BotManager.cs`, and `src/Allegiance.Server/Server.ShipCommands.cs`.

## Local research proof

Final qualification results and traces are recorded in the assets linked below after validation completes.

The new local proof uses a naturally played **15v15** research showcase with civs **25/3**, generated four-sector SingleRing, research **×16**, income **×5000**, default support share, finale after one minute and three reserved strike pilots per side. Test unlock and harness tech bypass are disabled. It sends no scripted damage, equipment/tech/money grant, teleport or direct command-state mutation. Each match must meet the original unscaled SC-1 minima **attack ≥5, repair ≥2 on each side**, observe effective nanite healing on each side, and capture actual positive- and negative-power projectiles while their pilots execute accepted named orders. Bounds remain 600 wall seconds and the normal game-over condition.

The separate telemetry loopback test explicitly checks queue → reject → requeue → accept → duplicate accept for attack and repair, using real sockets and the production relay. Fast controller tests cover station siege, bomber interception, local focus, friendly repair ranking, self/hidden/dead/docked/pod exclusion, typed target IDs, start/cadence/fairness, repair supersession and release after healing/capture.

## Validation

Validation results are recorded after the required isolated runs, one fast-tier sweep and never-parks comparison finish.

Reproduction (provide `ALLEGIANCE_ARTWORK_PATH` and `ALLEGIANCE_NATIVE_SRC_PATH` as in `local.env`; place temporary/cache/results directories on disk):

```sh
dotnet build AllegianceCore.slnx
dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o "$TASK_SCRATCH/aot"
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName=Allegiance.ClientCore.Tests.SC6CommanderOrdersLiveTests.Research_showcase_issues_accepts_and_executes_attack_and_repair_on_both_sides'
tools/run-tests.sh --tier fast --no-build --results-dir "$TASK_SCRATCH/fast"
```

`SC6_LIVE_LOG` optionally records the showcase's ordinary commander, queue, acceptance, execution and counter logs. Exact-method invocation results will be preserved in `SC6_assets/validation.json`.

## Scratch and evidence

The requested `~/allegiance-scratch/SC6/` and `~/allegiance-evidence/SC6/` locations are outside this session's writable roots (workspace and `/tmp`). All chosen scratch/build/cache/test-results locations use the disk-backed workspace `.verify-sc6/`, not `/tmp`. The final cleanup removes scratch and generated build directories, retaining only small evidence extracts in `docs/reports/SC6_assets/` under the 2 MB task limit. The external raw-evidence destination cannot be written in this sandbox, so raw logs/TRX are not retained. No rig/221/200v200 run was performed.
