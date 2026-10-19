# P728 — human commander in local Skirmish

Changes are left in the working tree. No commit, push, deployment, or rig run was performed.

## Player flow

Skirmish setup now offers **Commander: AI / You**. AI remains the default. With
You, side 0 has no research commander connection or research policy; the enemy
retains its normal research commander. The owned host passes the selected pilot
name privately to its child processes. Friendly bot pilots and the idle probes
vote for that pilot through ordinary `AUTODONATE` messages after the match starts.
The client opts out of its initial donation relationship until leadership is
confirmed. This uses native donation-confidence succession, rather than setting
server leadership or giving the human money directly. Votes repeat after mission
rotation; the bots do not override a later human decision to stop a pilot's income
donations.

Press **I** for team investments from the hangar, flight, or **F3** command view.
Use Up/Down and Space to invest, R for partial investment, S for show-all, H for
hide-completed, and B to close. The selected research item's description is shown.
Within investments, Tab selects a friendly pilot, D gives up to $100, E gives up
to $500, A donates the wallet and future income to that pilot, and X lets the
commander toggle that pilot's income donations. C keeps your own future income. These are rebindable actions.
Server messages remain authoritative for wallets and purchases.

Command view retains friendly click/rectangle selection and right-click orders.
Orders now reach combat pilots as individual command chats, including grouped
recipients. Constructor build and miner mine orders use the same native command
chat route. A remote constructor first receives Goto; once its real ship status reaches the rock's sector, the client sends Build. Changing its order, side, or match cancels that continuation. Combat bots accept the queued command, retain the named ship in the
`OrderedTarget` path, route toward remote command targets, and prioritize the
order over the showcase's autonomous staging. Friendly defend orders acquire
nearby enemies and retain a bounded leash to the protectee. Friendly station goto
uses the existing bay approach planner; warp goto flies through the aleph.
A new target supersedes a previous order even when both use the same command ID.
Dead targets and replacement lifepods release an attack order.

## Native screen and protocol audit

All native paths in this report are relative to
`~/Development/Allegiance/src/`. Bot target ranking and local process orchestration
remain port policy; this task does not port the separate native training/drone-AI
track.

| Audited need | Result and correction | Native evidence |
| --- | --- | --- |
| Reach investments from command view and flight | Removed the dock-only/command-view exclusion; kept the normal docked command acceptance path. Investment input is modal. | `WinTrek/WinTrek.cpp:10025-10033`; `WinTrek/hangarscreen.cpp:201-206` |
| Full and partial investment | Reused real bucket IDs and ordinary `BUCKET_DONATE`; full amount is remaining cost capped by wallet; partial is 10% and leaves at least $1 outstanding. | `WinTrek/loadout.cpp:807-826,883-914`; `clintlib/clintlib.cpp:2544-2561`; server wallet gate `FedSrv/FedSrv.CPP:7137-7166` |
| Research prerequisites and station availability | Replaced “unfinished means buyable” with current development/building techs, station-local techs, team-money exemption, and station successor checks. Show-all exposes locked rows without enabling spending. | `Igc/sideigc.h:224-281`; `WinTrek/loadout.cpp:1004-1011,1095-1106` |
| Native tab visibility | Filters the single scrollable list to native groups 0–5 and mission path gates, including show-all; hides internal group 255 and the legacy synthetic ship bucket while preserving server bucket IDs. | `WinTrek/loadout.cpp:1002,1042-1056`; native hull/part bucket creation is disabled at `Igc/sideigc.cpp:257-310` |
| Attribute upgrade predecessors | Added the native backwards search for an equal-or-cheaper attribute-only development with identical global attributes; later levels stay hidden until their predecessor completes unless show-all is enabled. | `Igc/bucketigc.cpp:36-65`; `WinTrek/loadout.cpp:1009-1011` |
| Drone cap | Uses exact production drone type identity, avoiding the old collision between different drone types sharing a hull. | `WinTrek/loadout.cpp:169-189,1111-1120`; production identity retained by native drones `FedSrv/FedSrv.CPP:2220-2236` |
| Constructor purchases and build spots | Uses known compatible rocks and exact live constructor station-type identity from the existing production sync, including constructors still travelling. | `WinTrek/loadout.cpp:213-240,1124-1131` |
| Live shop after researching while docked | Refreshes station/technology offers and the wallet without discarding the pending loadout. Also includes owned stations' global effect techs. | `WinTrek/load.cpp:1417-1421,1554-1565,1603-1606`; `Igc/sideigc.h:237`; station purchase gate `Igc/stationIGC.h:383-433` |
| New match/side, stable research IDs and costs | Clears the cached investment definitions and docked shop on mission/side changes; seeds costs from civilization base attributes, not accumulated research attributes. | Bucket cost initialization `Igc/bucketigc.cpp:70-74`; native bucket creation `Igc/sideigc.cpp:113-310` |
| Pilot grants and income control | Added reachable $100/$500 gifts, wallet/income donation, and leader-only donation toggle. A stopped donation is respected by the bot policy. | `WinTrek/teampane.cpp:1141-1241`; `clintlib/clintlib.cpp:2564-2581`; `FedSrv/FedSrv.CPP:5525-5658` |
| Human becomes commander | Native first-player leadership is initially held by a probe; ordinary donation votes transfer it to the human after startup and after rotation. | `FedSrv/fsmission.cpp:601-624,720-723`; `FedSrv/FedSrv.CPP:5560-5658` |
| Issue orders to pilots and drones | The previous UI resolved only constructor Build and sent another pilot's `ORDER_CHANGE`. It now sends command chat to the selected recipient; the server queues it and the bot acknowledges its own slot. | `WinTrek/console.cpp:1450-1504`; `WinTrek/WinTrek.cpp:4860-4890`; `FedSrv/FedSrv.CPP:3364-3391,7264-7338` |
| Illegal drone command feedback | Native command chat silently rejects illegal drone orders; its negative reply is commented out. Preserved that behavior and separately retained the port's older direct-order refusal-feedback coverage. | `FedSrv/FedSrv.CPP:3252-3257,3286-3292` |
| Default orders, focus, travel and defence | Build/Mine for appropriate asteroid work, Attack for enemy ships/bases, Defend friendly ships, Goto friendly bases/warps. Existing flight and docking planners execute the headless bot policy. | `Igc/shipIGC.h:2071-2161`; accepted-to-plan `Igc/shipIGC.cpp:1936-1939`; defend acquisition/leash `Igc/shipIGC.cpp:2373-2404,2688-2695`; no-bay docking fallback `Igc/common.cpp:1517-1524` |

The existing client waits for authoritative money messages rather than native's
optimistic local wallet subtraction. This is the established port protocol
contract and prevents double debits. No native purchase/technology gate is bypassed.

The loopback proof also exposed a pre-existing rotation gap: all replacement
ships were assigned their sides during placement, before `TryJoinSide` ran.
Consequently every join saw teammates already present, and neither side elected
a leader. Migration now uses the side to select the home hangar, clears the
prepared ship's side, then joins peers in the existing order with the previous
leader first. This preserves native leadership/ownership across game over
(`FedSrv/fsmission.cpp:601-624,3468-3521`).

The investment model also clamps a saved cursor after completed rows disappear,
so the visible highlight and the item receiving an investment remain consistent.
The native six research groups are presented in one scrollable list; internal
buckets, disabled paths and the legacy synthetic ship bucket are hidden.

## Validation

| Gate | Measured result |
| --- | --- |
| Whole solution: `dotnet build AllegianceCore.slnx --nologo -m:2` | **0 warnings, 0 errors** on the final tree. |
| Server native AOT: `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o /tmp/allegiance-evidence/P728/aot` | Published successfully; **no IL2026 or IL3050**. |
| Core, one full sweep | **4,868 passed / 11 failed / 0 skipped**. Dispositions below. |
| ClientCore, one full sweep | **1,388 passed / 0 failed / 0 skipped**. |
| MonoGame, one full sweep | **2,441 passed / 3 failed / 3 skipped**. Dispositions below. |
| Scripted human commander, standalone | **3/3 passed**, including research, bot purchase, accepted order, `OrderedTarget`, and next match. |
| Showcase never-parks, final bot/flight tree | **5/5 passed**, against **5/5** on the saved pre-order execution baseline; P727 also recorded 5/5. |
| Final investment UI/model checks | **31 methods × 3** standalone invocations, **126 case passes**, no failures. |
| Constructor command chat | Real build/completion and silent illegal-command rejection **3/3 passed** using the new production resolver and sender; the existing direct-order feedback path is also checked. |

New and affected tests were exercised three times per method. Focused coverage
includes native tech gates across civilizations, predecessor ordering, native
group visibility, exact drone identity/caps, filtered selection, shop refresh,
pilot funding/rebinding, remote constructor continuation, default command
resolution, order supersession and defend distances. Existing skirmish lifecycle
(including owner death and one/four workers), next-match leadership, bot hull
upgrade, order acceptance, and local showcase regressions were also repeated
three times. The complete per-invocation ledger, including exploratory failures,
is in [validation.json](P728_assets/validation.json); the smaller overview is
[validation-summary.json](P728_assets/validation-summary.json).

The human proof uses Gigacorp (civilization 18), ordinary income/donation votes,
and development 22, **Patroller**, costing **$5,000 / 120 simulation seconds**.
No debug tech bypass, research grant, hull injection, or wallet injection is used.
Four friendly bots are present; the test observes an eligible bot buying and
launching hull 215 after research. That stock hull's purchase price is **$0**;
this proves the legal technology gate and normal purchase/relaunch path rather
than a positive hull-price debit. Lethal damage triggers the ordinary pod/restart
path. An unarmed stock enemy constructor provides a stable attack target after
the research wait; this workload setup does not change research or purchases.
The command is checked both on the server's accepted target and the worker's
`OrderedTarget`, then the human retains command through game over and next match.
See [human-command-proof.json](P728_assets/human-command-proof.json) for all three
runs and their local host log paths.

The Core sweep's ten socket/timing failures each reached **three consecutive
exact-method standalone passes** with unchanged assertions and production
transport. The eleventh, the crowded 256-peer TCP benchmark, failed three times
alone: its raw peers omitted keepalives during uncontrolled simulation and hit
the existing 30-second transport timeout. The fixture now sends keepalives,
serializes writes, and distinguishes heartbeat replies from drain markers.
All four affected loopback methods passed three times; workload, deadlines and
measurement assertions are unchanged.

MonoGame caught two P728 shortcut-table collisions, fixed by giving pilot funding
its own action group; both guards passed three times afterward. Its existing
team-chat timing case passed three times alone. The three optional Meshy-pack
skips were each exercised three times successfully with the installed pack
explicitly configured. Early focused tests also caught two test-fixture mistakes
(a remote rock in the wrong sector and an assumed rather than captured previous
leader); both were corrected and passed three times. Changing the constructor
test sender exposed an obsolete expectation of negative chat feedback; native
silently rejects an illegal drone command chat, so the corrected test fences
delivery and checks refusal, then separately checks the older direct-order
route's feedback. Both constructor methods passed three times afterward.
An incomplete UI evidence
batch was replaced by the complete `ui-confirm` batch. No second full sweep was
run. All failure/retry dispositions are retained in
[TEST_FLAKES.md](../TEST_FLAKES.md) and the ledger.

No rig/221/200v200 workload was run. Live validation used real local sockets and
the installed artwork/native source; optional local showcase and Meshy checks
were enabled explicitly. NuGet's writable HTTP cache and build scratch were
redirected because the default cache is read-only and the shared `/tmp` volume
briefly exhausted its allocation.

Raw TRX, build logs, exploratory failures, candidate inventory, and local-host
logs are kept outside tracked evidence. This sandbox permits writing only the
workspace and `/tmp`, so the raw archive is `/tmp/allegiance-evidence/P728/`
instead of the requested `~/allegiance-evidence/P728/`. Per-session host logs are
at the `logs` paths recorded by the loopback proof. Only compact JSON summaries
are placed in `docs/reports/P728_assets/`; the task's tracked evidence stays under
2 MB (392,056 bytes at handoff).
