# P743 — parity correctness pass

2026-10-18 dispatch; audited the dispatched `codex/p743` working tree at `a8a9eb24`. No commits or pushes. Only production change: sector-info display grouping; no simulation, server, Core, flight, bot or protocol changes.

## Result

Of 40 previously completed rows, **28 were correct (70.00%)**, **10 partially correct (25.00%)**, **0 wholly wrong**, and **2 untested (5.00%)** under the rule below. Thus 30.00% could not retain an unqualified correctness claim; the demonstrated discrepancy rate is 25.00%, with another 5.00% unverified. These are the dispatched-tree classifications, before the small fix, not success rates inferred from passing tests.

All 23 eligible whole-screen rows were split, together with all 19 eligible commander and all 11 eligible lobby parents: **53 parents → 164 native behavior rows**. Four already-excluded screen rows (video, squads, Zone Club, Zone events) remain excluded. Every child has a native file:line, status and source/test evidence or an explicit missing/untested reason. Larger gaps are left visible. The existing training implementation from TR-1/TR-2 was inspected; Basic Flight is the only enabled lesson, so the old blanket training notes were replaced with mission/flow rows.

## Method and interpretation

Both `done` (main matrix) and `implemented` (simulation matrix) mean completed in the scorecard, and both were eligible. The sampling population was 382 + 325 = 707 completed rows. A deterministic random sample without replacement used `random.Random(743)`, matrices in main/simulation order, and areas alphabetically. Each candidate drew an exponential priority `-log(U)/(weight * recent_factor)`; the lowest priorities filled each area quota. `recent_factor=3` for a P700–P742 marker in the row, otherwise 1. This recent-wave marker is a documented proxy for the October 15–17 work, not a claim that every cited commit landed in that date window. Ten selected rows have that marker. High-weight rows therefore had higher selection probability while every area was represented. The sample mean weight was 4.025 versus 3.427 in the completed population; recent-marker rows were 10/40 versus 144/707 in the population. Independent replay is recorded in [sampling-validation.json](P743_assets/sampling-validation.json). Exact original rows, weights, flags, quotas and baseline hashes are in [sample-audit.json](P743_assets/sample-audit.json).

“Correct” requires the native rule, reachable production behavior and a discriminating test to agree for the row’s claimed scope. “Partially correct” means some real behavior exists but a native branch, condition or flow differs/is missing. “Wrong” would mean the claimed behavior is absent or contradicted as a whole. “Untested” means source plausibly implements the behavior but the cited tests exercise only a binding/asset/helper rather than the production transition. A partial/untested row gets zero completed credit. A broad row cannot be retrospectively narrowed to its passing subset; the split gives that subset its own weight. Pure enum/mapping rows can be verified by source plus consumer tests; complex compose/cancel flows require a transition test.

All native paths below are rooted at `/home/domingo/Development/Allegiance/src/` (`~/Development/Allegiance/src/`). Native citations were read directly from that checkout. Existing adaptations such as authoritative managed wire events are stated where used. Asset presence or a helper-only test is not evidence of an entire screen/loop.

The stratified, unequal-probability sample is a targeted audit, **not an unbiased estimate of all 707 rows or a confidence bound on total parity**. Per-area counts are small. Unweighted correctness is reported separately from the matrix’s weighted implementation score.

## Sample error rate by area

| Area | Sample | Correct | Partial | Wrong | Untested | Correct % | Not certified % |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| callbacks | 3 | 3 | 0 | 0 | 0 | 100.00% | 0.00% |
| commander | 4 | 1 | 3 | 0 | 0 | 25.00% | 75.00% |
| commands | 5 | 4 | 0 | 0 | 1 | 80.00% | 20.00% |
| constants | 3 | 3 | 0 | 0 | 0 | 100.00% | 0.00% |
| fedmsg | 4 | 3 | 1 | 0 | 0 | 75.00% | 25.00% |
| lobby | 2 | 1 | 1 | 0 | 0 | 50.00% | 50.00% |
| parts | 3 | 3 | 0 | 0 | 0 | 100.00% | 0.00% |
| periodic | 2 | 2 | 0 | 0 | 0 | 100.00% | 0.00% |
| screens | 4 | 1 | 3 | 0 | 0 | 25.00% | 75.00% |
| sounds | 5 | 2 | 2 | 0 | 1 | 40.00% | 60.00% |
| updates | 4 | 4 | 0 | 0 | 0 | 100.00% | 0.00% |
| warnings | 1 | 1 | 0 | 0 | 0 | 100.00% | 0.00% |
| **Total** | **40** | **28** | **10** | **0** | **2** | **70.00%** | **30.00%** |

Sample-weighted correctness (descriptive only): 113/161 = 70.19%. Screen-child rows are not added to the random sample denominator.

## Before → after scorecard

Exact output from `python3 scripts/parity-scorecard.py`; partial earns zero credit. Each cell is weighted credit/eligible weight, followed by completed/eligible row count.

| Matrix / area | Before | After |
| --- | --- | --- |
| parity-matrix / commander | 100.00% (81/81); 19/19 rows | 78.39% (63.496/81.000); 35/46 rows |
| parity-matrix / commands | 94.92% (561/591); 176/190 rows | 94.08% (556/591); 175/190 rows |
| parity-matrix / fedmsg | 92.23% (475/515); 115/125 rows | 91.46% (471/515); 114/125 rows |
| parity-matrix / lobby | 91.49% (43/47); 10/11 rows | 65.31% (30.695/47.000); 16/33 rows |
| parity-matrix / parts | 100.00% (153/153); 40/40 rows | 100.00% (153/153); 40/40 rows |
| parity-matrix / screens | 96.63% (86/89); 22/23 rows | 47.39% (42.173/89.000); 35/85 rows |
| parity-matrix / TOTAL | 94.78% (1399/1476); 382/408 rows | 89.18% (1316.364/1476.000); 415/519 rows |
| sim-rules-matrix / callbacks | 54.55% (108/198); 36/66 rows | 54.55% (108/198); 36/66 rows |
| sim-rules-matrix / constants | 98.52% (133/135); 38/39 rows | 98.52% (133/135); 38/39 rows |
| sim-rules-matrix / periodic | 51.06% (24/47); 7/15 rows | 51.06% (24/47); 7/15 rows |
| sim-rules-matrix / sounds | 85.09% (468/550); 173/212 rows | 83.45% (459/550); 170/212 rows |
| sim-rules-matrix / updates | 80.52% (281/349); 69/87 rows | 80.52% (281/349); 69/87 rows |
| sim-rules-matrix / warnings | 100.00% (10/10); 2/2 rows | 100.00% (10/10); 2/2 rows |
| sim-rules-matrix / TOTAL | 79.44% (1024/1289); 325/421 rows | 78.74% (1015/1289); 322/421 rows |
| **Combined** | **87.63% (2423/2765); 707/829 rows** | **84.32% (2331.364/2765.000); 737/940 rows** |

Raw row count: main 438 → 549; simulation 473 → 473. Unweighted percentages are structurally affected by splitting and should not be interpreted as a change in implemented code. Eligible weight remains exactly **1476 + 1289 = 2765**; no new exclusions were used to improve the denominator.

Each parent’s weight is divided equally between its native parts to three decimal places, rounding down and assigning the remainder to the final child. This mechanical allocation preserves its weight without claiming a new relative-priority assessment. Every child is positive. The scorecard accepts Decimal weights; it does not require integer weights. The only gained credit is from newly distinguishing proven parts of an originally partial training row; the large loss comes from removing whole-screen/subsystem credit. [row-splits.json](P743_assets/row-splits.json) contains original baseline parent, reviewed parent and final children.

## The small fix

Native [sectormap.cpp:294-305](/home/domingo/Development/Allegiance/src/WinTrek/sectormap.cpp:294) tests `(aabm & ~c_aabmBuildable) != 0` to decide eligibility, then keys the count/icon maps by the **original** `aabm`. The port incorrectly used the masked value as the key. It now keeps masks `0x01` and `0x09` separate while still excluding plain `0x08`/zero. The regression mixes both special masks, duplicate `0x09`, a plain buildable rock and a zero-mask rock; it asserts exact groups/counts, total rocks, and unchanged He3 sum.

Changed [SectorInfoPaneLayout.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/SectorInfoPaneLayout.cs:133) and [SectorInfoPaneLayoutTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/SectorInfoPaneLayoutTests.cs:80). Updated misleading comments that described the eligibility mask as the native grouping key. Whole sector-info sample S21 remains partially correct because native hover/expand is absent and production helium samples ignore live ore updates; the grouping child now earns full credit independently of those gaps.

## Individual sample checks

Each ID refers to the original row in `sample-audit.json`; line numbers there refer to the dispatched CSV, before splitting. The native, production and test references below are the recheck evidence, not merely a copy of the old matrix’s claim.

### S01 · commander · Investments / buy-in

**partially correct**; original weight 4. Funding amounts and basic gates work; Items omits predecessor pruning and CanBuy only means not complete. Existing tests assert the approximation. Follow up: model predecessor and continued purchasability.

Native: [WinTrek/loadout.cpp:1004-1011](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1004); [WinTrek/loadout.cpp:1095-1139](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1095).

Port: [src/Allegiance.ClientCore.Game/Rendering/InvestmentPaneModel.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/InvestmentPaneModel.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/InvestmentPaneModelTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/InvestmentPaneModelTests.cs:1).

### S02 · commander · Wingman AI (attack/defend lead solving)

**correct**; original weight 4. Attack lead/firing-angle and defend acquisition/return rules have production-tick and live moving-target coverage; this row is the attack/defend/lead subset.

Native: [Igc/shipIGC.cpp:2370-2407](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:2370); [Igc/shipIGC.cpp:2441-2606](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:2441).

Port: [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1); [src/Allegiance.Core/Sim/Ship.NpcUpdates.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.NpcUpdates.cs:1).

Tests read: [tests/Allegiance.Core.Tests/NpcWingmanLeadLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/NpcWingmanLeadLiveTests.cs:1); [tests/Allegiance.Core.Tests/P708ShipUpdateTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P708ShipUpdateTests.cs:1).

### S03 · commander · Drones: constructors

**partially correct**; original weight 4. Builder conversion, successor and abort tests pass the lifecycle subset. Native drilling/building also pins yaw/pitch to zero, roll to one and building throttle to -0.8; port uses IsBuilding reservations without this native control/feedback sequence. Follow up: drilling alignment, spin/throttle and hull-hit feedback; keep lifecycle credit on separate children.

Native: [Igc/shipIGC.cpp:875-895](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:875); [Igc/shipIGC.cpp:2262-2300](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:2262); [Igc/buildingeffectigc.h:294-353](/home/domingo/Development/Allegiance/src/Igc/buildingeffectigc.h:294).

Port: [src/Allegiance.Core/Model/Sector.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Sector.cs:1); [src/Allegiance.Server/Server.Economy.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.Economy.cs:1).

Tests read: [tests/Allegiance.Core.Tests/BuildingConstructionLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/BuildingConstructionLiveTests.cs:1).

### S04 · commander · Team leadership / owner (promote, owner-only cmds)

**partially correct**; original weight 4. HandleLeaderSuccession takes lowest ship ID; native Started succession first calls donation-vote PickNewLeader. Current test explicitly expects first other player. Follow up: use donation preference on leader departure; retain squad exclusion.

Native: [FedSrv/fsmission.cpp:949-990](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:949); [FedSrv/FedSrv.CPP:13981-14024](/home/domingo/Development/Allegiance/src/FedSrv/FedSrv.CPP:13981).

Port: [src/Allegiance.Server/Server.Lobby.cs:2378](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.Lobby.cs:2378).

Tests read: [tests/Allegiance.Core.Tests/LiveTeamLeadershipTests.cs:333](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/LiveTeamLeadershipTests.cs:333).

### S05 · commands · TK_SwapMissile

**correct**; original weight 2. Magazine mount zero uses native cargo grouping/socket mask; real TCP handler test asserts exact ten-byte swap ordering and single authoritative echo.

Native: [WinTrek/WinTrek.cpp:9075-9150](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:9075); [WinTrek/WinTrek.cpp:9604-9608](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:9604); [WinTrek/trekigc.cpp:2620-2719](/home/domingo/Development/Allegiance/src/WinTrek/trekigc.cpp:2620).

Port: [src/Allegiance.ClientCore.Game/Rendering/EquipmentSwapInput.cs:39](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/EquipmentSwapInput.cs:39); [src/Allegiance.ClientCore.Game/Game1.FlightParts.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.FlightParts.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P720EquipmentSwapTests.cs:180](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P720EquipmentSwapTests.cs:180).

### S06 · commands · TK_TargetArtifact

**correct**; original weight 3. Artifact pool is local neutral flag treasure, excluding cash/parts/remote and enemy-owned flags; cycling and nearest fallback are covered.

Native: [WinTrek/WinTrek.cpp:10335-10340](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:10335).

Port: [src/Allegiance.ClientCore.Game/Game1.TargetCommands.cs:35](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.TargetCommands.cs:35).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P642TargetCommandTests.cs:44](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P642TargetCommandTests.cs:44).

### S07 · commands · TK_TargetEnemyBase

**correct**; original weight 4. Enemy stations exclude allies and pedestals; search local first then reachable sectors from arrival mouth, with reverse warp order for previous.

Native: [WinTrek/WinTrek.cpp:10194-10199](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:10194); [Igc/common.cpp:650-743](/home/domingo/Development/Allegiance/src/Igc/common.cpp:650).

Port: [src/Allegiance.ClientCore.Game/Game1.TargetCommands.cs:47](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.TargetCommands.cs:47).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P642TargetCommandTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P642TargetCommandTests.cs:1); [tests/Allegiance.ClientCore.MonoGame.Tests/P587MousePickAndBaseTargetingTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P587MousePickAndBaseTargetingTests.cs:1).

### S08 · commands · TK_TargetEnemy

**correct**; original weight 5. Generic Enemy is native utility hull mask (rescue/miner/builder), not every combat hull; alliance and role table tests discriminate it.

Native: [WinTrek/WinTrek.cpp:10154-10159](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:10154).

Port: [src/Allegiance.ClientCore.Game/Game1.TargetCommands.cs:87](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.TargetCommands.cs:87).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P642TargetCommandTests.cs:116](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P642TargetCommandTests.cs:116).

### S09 · commands · TK_StartChat

**untested**; original weight 5. Binding/rebinding tests only prove map lookup. Source opens chat and gates flight input, but no test exercises StartChat through UpdateFlying with compose/send/focus behavior. Follow up: handler-level compose/send test.

Native: [WinTrek/WinTrek.cpp:9441-9446](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:9441).

Port: [src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:764](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:764).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P625bActionMapBehaviorTests.cs:39](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P625bActionMapBehaviorTests.cs:39).

### S10 · fedmsg · 178 BOARD_NACK

**partially correct**; original weight 4. Six-byte codec and receive dispatch work, but ClientWorld.Apply(BoardNack) always posts generic failure. Native gates feedback on docked/in-game state, releases teleport lockdown and selects turret-specific message plus noTurretsSound. Existing test only expects generic line. Follow up: preserve requested-parent context and branch-specific recovery/feedback.

Native: [Inc/Messages.h:913-915](/home/domingo/Development/Allegiance/src/Inc/Messages.h:913); [clintlib/appmsg.cpp:1899-1910](/home/domingo/Development/Allegiance/src/clintlib/appmsg.cpp:1899); [WinTrek/teleportpane.cpp:276-293](/home/domingo/Development/Allegiance/src/WinTrek/teleportpane.cpp:276).

Port: [src/Allegiance.Core/Wire/BoardNack.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Wire/BoardNack.cs:1); [src/Allegiance.ClientCore/State/ClientWorld.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/State/ClientWorld.cs:1).

Tests read: [tests/Allegiance.Core.Tests/Wire/BoardingWireTests.cs:98](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/Wire/BoardingWireTests.cs:98); [tests/Allegiance.ClientCore.Tests/P638FeedbackWireTests.cs:78](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/P638FeedbackWireTests.cs:78).

### S11 · fedmsg · 38 MISSIONDEF

**correct**; original weight 4. Leader IDs are native shorts at offset 814; independently patched fixture guards neighbor fields; late join receives roster before role replay.

Native: [Inc/Messages.h:294-318](/home/domingo/Development/Allegiance/src/Inc/Messages.h:294); [FedSrv/fsmission.cpp:3943-3974](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:3943).

Port: [src/Allegiance.Core/Wire/MissionDef.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Wire/MissionDef.cs:1); [src/Allegiance.Server/Server.Lobby.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.Lobby.cs:1).

Tests read: [tests/Allegiance.Core.Tests/Wire/P713LobbyWireTests.cs:8](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/Wire/P713LobbyWireTests.cs:8); [tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs:1).

### S12 · fedmsg · 187 RIPCORD_ABORTED

**correct**; original weight 4. Six-byte ship-ID message has independent exact-byte/short-buffer tests and production receive branch applies it to client world.

Native: [Inc/Messages.h:965-967](/home/domingo/Development/Allegiance/src/Inc/Messages.h:965).

Port: [src/Allegiance.Core/Wire/RipcordAborted.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Wire/RipcordAborted.cs:1); [src/Allegiance.ClientCore/Session/ConnectionSession.cs:1438](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/Session/ConnectionSession.cs:1438).

Tests read: [tests/Allegiance.Core.Tests/Wire/RipcordWireTests.cs:148](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/Wire/RipcordWireTests.cs:148); [tests/Allegiance.ClientCore.Tests/ShipStatusAlarmSoundTests.cs:171](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/ShipStatusAlarmSoundTests.cs:171).

### S13 · fedmsg · 46 TEAM_READY

**correct**; original weight 4. Eight-byte side/ready/padding codec and aggregate transitions are exercised over real TCP, including forced ready reversal.

Native: [Inc/Messages.h:386-391](/home/domingo/Development/Allegiance/src/Inc/Messages.h:386); [FedSrv/fsmission.cpp:5449-5471](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:5449).

Port: [src/Allegiance.Core/Wire/TeamReady.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Wire/TeamReady.cs:1); [src/Allegiance.ClientCore/Session/ConnectionSession.cs:1370](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/Session/ConnectionSession.cs:1370).

Tests read: [tests/Allegiance.Core.Tests/LiveTeamLobbyTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/LiveTeamLobbyTests.cs:1); [tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs:1).

### S14 · lobby · Team lobby / team pane

**partially correct**; original weight 4. Join-time role/readiness and side roster work, with late-join and side-change tests. Whole team lobby also includes native auto-accept/team configuration and selected-player controls that this display does not implement. Split retains snapshot credit and marks broader controls partial.

Native: [WinTrek/teamscreen.cpp:452-505](/home/domingo/Development/Allegiance/src/WinTrek/teamscreen.cpp:452); [WinTrek/teamscreen.cpp:1554-1563](/home/domingo/Development/Allegiance/src/WinTrek/teamscreen.cpp:1554); [WinTrek/teamscreen.cpp:2773-2815](/home/domingo/Development/Allegiance/src/WinTrek/teamscreen.cpp:2773); [FedSrv/fsmission.cpp:3943-3974](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:3943).

Port: [src/Allegiance.ClientCore.Game/Rendering/ReadyCheckRoster.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/ReadyCheckRoster.cs:1); [src/Allegiance.Server/Server.Lobby.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.Lobby.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/ReadyCheckRosterTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/ReadyCheckRosterTests.cs:1); [tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs:1).

### S15 · lobby · Ready / force-ready check

**correct**; original weight 5. Non-owner team commander toggles force both ways, clears own AFK, and broadcasts to teammate/late join; host authority gates remain enforced.

Native: [WinTrek/teamscreen.cpp:1554-1559](/home/domingo/Development/Allegiance/src/WinTrek/teamscreen.cpp:1554); [WinTrek/teamscreen.cpp:2773-2793](/home/domingo/Development/Allegiance/src/WinTrek/teamscreen.cpp:2773).

Port: [src/Allegiance.ClientCore/State/LobbyScreenState.cs:18](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/State/LobbyScreenState.cs:18); [src/Allegiance.ClientCore.Game/Game1.Panes.cs:1144](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.Panes.cs:1144).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P713LobbyScreenLiveTests.cs:1).

### S16 · parts · c_eabmWarpBombDual

**correct**; original weight 4. Dual capability 0x02 controls source blast in addition to destination; real domain/live proofs explicitly set dual flag and assert delayed damage.

Native: [Igc/igc.h:772-774](/home/domingo/Development/Allegiance/src/Igc/igc.h:772); [Igc/warpIGC.h:58-73](/home/domingo/Development/Allegiance/src/Igc/warpIGC.h:58).

Port: [src/Allegiance.Core/Model/Sector.cs:3790](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Sector.cs:3790).

Tests read: [tests/Allegiance.Core.Tests/WarpBombTests.cs:167](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/WarpBombTests.cs:167); [tests/Allegiance.Core.Tests/WarpBombTests.cs:424](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/WarpBombTests.cs:424).

### S17 · parts · Weapons (ET_Weapon=1)

**correct**; original weight 5. ET_Weapon identity, mount gate, selection, burst scheduling and real projectile emission are represented; type row is not certification of every weapon visual/audio.

Native: [Igc/igc.h:532](/home/domingo/Development/Allegiance/src/Igc/igc.h:532); [Igc/weaponIGC.h:30-63](/home/domingo/Development/Allegiance/src/Igc/weaponIGC.h:30); [Igc/weaponIGC.cpp:142-348](/home/domingo/Development/Allegiance/src/Igc/weaponIGC.cpp:142).

Port: [src/Allegiance.Core/Sim/Weapon.cs:155](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Weapon.cs:155).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P651WeaponReadinessTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P651WeaponReadinessTests.cs:1); [tests/Allegiance.Core.Tests/CombatWeaponFiringTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/CombatWeaponFiringTests.cs:1).

### S18 · parts · c_habmFighter

**correct**; original weight 4. Native 0x80 fighter bit is preserved; friendly/enemy asset-mask tests exercise positive and mixed fighter/bomber cases.

Native: [Igc/igc.h:733](/home/domingo/Development/Allegiance/src/Igc/igc.h:733).

Port: [src/Allegiance.Core/Data/DataHullTypeIGC.cs:127](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Data/DataHullTypeIGC.cs:127).

Tests read: [tests/Allegiance.Core.Tests/ClusterWarningsTests.cs:36](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/ClusterWarningsTests.cs:36).

### S19 · screens · Side-select screen

**correct**; original weight 4. Active-side authoritative admission is required after elimination, preserving picker until join; roster and side state are not inferred from ship existence.

Native: [WinTrek/teamscreen.cpp:2962-2982](/home/domingo/Development/Allegiance/src/WinTrek/teamscreen.cpp:2962).

Port: [src/Allegiance.ClientCore/State/LobbyScreenState.cs:10](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/State/LobbyScreenState.cs:10); [src/Allegiance.ClientCore.Game/Game1.Panes.cs:923](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.Panes.cs:923).

Tests read: [tests/Allegiance.ClientCore.Tests/P713LobbyScreenStateTests.cs:80](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/P713LobbyScreenStateTests.cs:80).

### S20 · screens · Hangar / loadout screen

**partially correct**; original weight 5. Ordinary buy/launch and pane toggles exist; native boarded launch disembarks first, and per-button hover feedback is not reproduced. Split exposes these gaps.

Native: [WinTrek/hangarscreen.cpp:145-240](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:145).

Port: [src/Allegiance.ClientCore.Game/Game1.Panes.cs:41](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.Panes.cs:41); [src/Allegiance.ClientCore.Game/Rendering/DockedScreenRenderer.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/DockedScreenRenderer.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/ShopNoAutoLaunchTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/ShopNoAutoLaunchTests.cs:1); [tests/Allegiance.ClientCore.MonoGame.Tests/ShopModelTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/ShopModelTests.cs:1).

### S21 · screens · Sector info pane

**partially correct**; original weight 4. Wrong grouping merged resource-only and resource|Buildable masks. P743 fixes the full-mask key with a regression. Native hover/expand remains absent. The live renderer also builds helium samples from static export ast.Ore, ignoring ClientWorld live ore fractions; native sums GetOreSeenBySide. Follow up: feed evolving side-known ore and prove mining/visibility updates, then add hover/collapse behavior.

Native: [WinTrek/sectormap.cpp:196-240](/home/domingo/Development/Allegiance/src/WinTrek/sectormap.cpp:196); [WinTrek/sectormap.cpp:294-305](/home/domingo/Development/Allegiance/src/WinTrek/sectormap.cpp:294); [WinTrek/sectormap.cpp:350-369](/home/domingo/Development/Allegiance/src/WinTrek/sectormap.cpp:350); [Igc/asteroidigc.h:280-285](/home/domingo/Development/Allegiance/src/Igc/asteroidigc.h:280).

Port: [src/Allegiance.ClientCore.Game/Rendering/SectorInfoPaneLayout.cs:133](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/SectorInfoPaneLayout.cs:133); [src/Allegiance.ClientCore.Game/Game1.Rendering.cs:2734](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.Rendering.cs:2734).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/SectorInfoPaneLayoutTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/SectorInfoPaneLayoutTests.cs:1).

### S22 · screens · Game-over screen

**partially correct**; original weight 5. Winner/reason, player counters and Back exist, but native per-side totals and interactive per-column/reverse sorting are absent. Rendering table alone did not justify whole-screen done.

Native: [WinTrek/gameoverscreen.cpp:609-625](/home/domingo/Development/Allegiance/src/WinTrek/gameoverscreen.cpp:609); [WinTrek/gameoverscreen.cpp:840-1009](/home/domingo/Development/Allegiance/src/WinTrek/gameoverscreen.cpp:840).

Port: [src/Allegiance.ClientCore.Game/Rendering/GameOverScreenRenderer.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Rendering/GameOverScreenRenderer.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/GameOverPanelInfoTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/GameOverPanelInfoTests.cs:1); [tests/Allegiance.Core.Tests/P655GameOverDeliveryTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P655GameOverDeliveryTests.cs:1).

### S23 · callbacks · SideBuildingTechChange@4776

**correct**; original weight 3. Equality-gated post-mutation mask callback covers initial station, shared provider, last removal and capture on both sides.

Native: [Igc/sideigc.h:142-168](/home/domingo/Development/Allegiance/src/Igc/sideigc.h:142).

Port: [src/Allegiance.Core/Model/Side.cs:99](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Side.cs:99).

Tests read: [tests/Allegiance.Core.Tests/P691EconomyCallbackTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P691EconomyCallbackTests.cs:1).

### S24 · callbacks · KillAsteroidEvent@4838

**correct**; original weight 3. Only lethal crossing notifies; server bridge removes and broadcasts. Nonlethal, duplicate death and whole-mission live removal are covered.

Native: [Igc/asteroidigc.h:152-164](/home/domingo/Development/Allegiance/src/Igc/asteroidigc.h:152).

Port: [src/Allegiance.Server/ServerHost.cs:776](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/ServerHost.cs:776); [src/Allegiance.Server/Server.Lifecycle.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.Lifecycle.cs:1).

Tests read: [tests/Allegiance.Core.Tests/AsteroidCombatTests.cs:139](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/AsteroidCombatTests.cs:139); [tests/Allegiance.Core.Tests/AsteroidCombatLiveTests.cs:349](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/AsteroidCombatLiveTests.cs:349).

### S25 · callbacks · DestroyTeleportProbe@4890

**correct**; original weight 3. dtRipcord>=0 termination notifies once, even before activation; expiry/damage/ammo/direct removal and owner/enemy criticality have coverage.

Native: [Igc/probeigc.cpp:223-230](/home/domingo/Development/Allegiance/src/Igc/probeigc.cpp:223).

Port: [src/Allegiance.Core/Sim/Probe.cs:605](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Probe.cs:605); [src/Allegiance.Server/ServerHost.cs:650](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/ServerHost.cs:650).

Tests read: [tests/Allegiance.Core.Tests/P692EquipmentLifecycleTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P692EquipmentLifecycleTests.cs:1); [tests/Allegiance.ClientCore.Tests/P692EquipmentLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/P692EquipmentLiveTests.cs:1).

### S26 · constants · c_fcidBaseClusterCost

**correct**; original weight 5. Loaded base cost precedes drone/parent/pod/player accumulation; positive cost scales once by chunk dt/divisor. Negative loaded base is retained.

Native: [Igc/igc.h:104](/home/domingo/Development/Allegiance/src/Igc/igc.h:104); [Igc/clusterIGC.cpp:163-198](/home/domingo/Development/Allegiance/src/Igc/clusterIGC.cpp:163).

Port: [src/Allegiance.Core/Model/Sector.cs:695](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Sector.cs:695).

Tests read: [tests/Allegiance.Core.Tests/P629SectorPenaltyTests.cs:56](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P629SectorPenaltyTests.cs:56).

### S27 · constants · c_fcidLensMultiplier

**correct**; original weight 5. Loaded lens scales asteroid Z in uniform volume and divides boundary Z squared; full random-map clearance was already explicitly out of this constant-consumer scope.

Native: [Igc/common.cpp:2814-2818](/home/domingo/Development/Allegiance/src/Igc/common.cpp:2814); [Igc/common.cpp:2882-2884](/home/domingo/Development/Allegiance/src/Igc/common.cpp:2882); [Igc/shipIGC.cpp:372-377](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:372).

Port: [src/Allegiance.Core/MapGen/MissionMapGenerator.cs:1340](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/MapGen/MissionMapGenerator.cs:1340); [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1).

Tests read: [tests/Allegiance.Core.Tests/P676SimRulesTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P676SimRulesTests.cs:1); [tests/Allegiance.Core.Tests/P650SimRulesTests.cs:92](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P650SimRulesTests.cs:92).

### S28 · constants · c_fcidDownedShield

**correct**; original weight 5. Loaded slot 5 gates absorption/leakage and boarding/capture, without hardcoded override; varied threshold test distinguishes downed from active shield.

Native: [Igc/shieldIGC.cpp:158](/home/domingo/Development/Allegiance/src/Igc/shieldIGC.cpp:158); [Igc/shipIGC.cpp:851](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:851).

Port: [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1); [src/Allegiance.Core/Model/Sector.cs:1723](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Sector.cs:1723).

Tests read: [tests/Allegiance.Core.Tests/P650SimRulesTests.cs:108](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P650SimRulesTests.cs:108).

### S29 · periodic · payday_income_and_gates

**correct**; original weight 5. Started-only loaded payday baseline*(1+civ)+station income; disabled development advances phase without accrual/backpay. Tests cover stage, changed constants and station ownership.

Native: [FedSrv/fsmission.cpp:2330](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:2330); [FedSrv/fsmission.cpp:2514-2552](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:2514).

Port: [src/Allegiance.Core/Model/Mission.cs:684](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Mission.cs:684).

Tests read: [tests/Allegiance.Core.Tests/P676SimRulesTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P676SimRulesTests.cs:1); [tests/Allegiance.Core.Tests/StartingMoneyStationIncomeLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/StartingMoneyStationIncomeLiveTests.cs:1).

### S30 · periodic · ballot_resolution_boundary

**correct**; original weight 3. Strict expiration<now, all-votes-in early resolution and cancellation-before-tally match; production removes ballot after one effect.

Native: [FedSrv/fsmission.cpp:5817-5834](/home/domingo/Development/Allegiance/src/FedSrv/fsmission.cpp:5817).

Port: [src/Allegiance.Core/Model/PendingBallot.cs:148](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/PendingBallot.cs:148); [src/Allegiance.Server/Server.Lobby.cs:1639](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.Lobby.cs:1639).

Tests read: [tests/Allegiance.Core.Tests/P739PeriodicTests.cs:40](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P739PeriodicTests.cs:40).

### S31 · sounds · PlaySoundIf@WinTrek/trekigc.cpp:1341 GetManuveringThrusterExteriorSound

**partially correct**; original weight 3. Tests only select fixed fighter/capital interior waves. Native exterior loop reads per-hull GetManuveringThrusterExteriorSound; port has a shared local bed, lacking per-hull exterior cue/spatial source. Follow up: typed hull IDs and remote exterior loops.

Native: [WinTrek/trekigc.cpp:1337-1344](/home/domingo/Development/Allegiance/src/WinTrek/trekigc.cpp:1337).

Port: [src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:1818](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:1818); [src/Allegiance.ClientCore.Game/Audio/ShipLoopBeds.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Audio/ShipLoopBeds.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P721ShipLoopBedsTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P721ShipLoopBedsTests.cs:1).

### S32 · sounds · PlayNotificationSound@Igc/shipIGC.h:616 salReloadingAmmoSound

**correct**; original weight 2. Successful own ammo-pack reload emits SalReloadingAmmo plus StartReload; event test excludes peer and non-ammo reload, with asset inclusion coverage.

Native: [Igc/shipIGC.h:604-619](/home/domingo/Development/Allegiance/src/Igc/shipIGC.h:604).

Port: [src/Allegiance.ClientCore/State/ClientWorld.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/State/ClientWorld.cs:1); [src/Allegiance.ClientCore.Game/Game1.Audio.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.Audio.cs:1).

Tests read: [tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs:1081](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs:1081); [tests/Allegiance.ClientCore.MonoGame.Tests/P737SoundAssetTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P737SoundAssetTests.cs:1).

### S33 · sounds · PlaySoundEffect@clintlib/appmsg.cpp:677 GetEnemyCapturedSound

**correct**; original weight 4. Otherwise friendly gain uses core EnemyCapturedSound; old friendly loss takes priority, neither friendly is silent. Event snapshots station type before subsequent export.

Native: [clintlib/appmsg.cpp:674-681](/home/domingo/Development/Allegiance/src/clintlib/appmsg.cpp:674).

Port: [src/Allegiance.ClientCore.Game/Audio/StationEventSoundMap.cs:13](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Audio/StationEventSoundMap.cs:13); [src/Allegiance.ClientCore/State/ClientWorld.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore/State/ClientWorld.cs:1).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/P664StationEventSoundTests.cs:100](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P664StationEventSoundTests.cs:100).

### S34 · sounds · PlaySoundEffect@Igc/shipIGC.cpp:1193 collisionSound

**partially correct**; original weight 3. Collision cue and sector/position filtering exist, but native emits once per party (this and pModel); UpdateCollisionSounds intentionally collapses both parties to one shared impact cue. Existing selector test asserts that reduction. Follow up: decide and test native per-party source/instance behavior, including mixer serialization, rather than claiming identical sound semantics.

Native: [Igc/shipIGC.cpp:1193-1200](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:1193).

Port: [src/Allegiance.ClientCore.Game/Game1.Audio.cs:740](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.Audio.cs:740); [src/Allegiance.ClientCore.Game/Audio/CollisionFeedback.cs:47](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Audio/CollisionFeedback.cs:47).

Tests read: [tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs:434](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs:434); [tests/Allegiance.ClientCore.MonoGame.Tests/CollisionFeedbackTests.cs:50](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/CollisionFeedbackTests.cs:50).

### S35 · sounds · PlaySoundEffect@WinTrek/WinTrek.cpp:8315 salAutopilotDisengageSound

**untested**; original weight 3. Control threshold and asset mapping have tests, but neither invokes the manual-input cancellation handler and asserts exactly one disengage cue. Follow up: injected sound sink around the production transition.

Native: [WinTrek/WinTrek.cpp:8295-8316](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:8295).

Port: [src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:3110](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:3110).

Tests read: [tests/Allegiance.ClientCore.MonoGame.Tests/MouseFlightControllerTests.cs:205](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/MouseFlightControllerTests.cs:205); [tests/Allegiance.ClientCore.MonoGame.Tests/P714AnnounceSoundTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.MonoGame.Tests/P714AnnounceSoundTests.cs:1).

### S36 · updates · wingman_missile_evasion_gate

**correct**; original weight 4. Behavior flag, full lock and strict transverse-speed bound are discriminated; production NPC tick fires actual missile, consumes one round and waits reload.

Native: [Igc/shipIGC.cpp:2578-2591](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:2578).

Port: [src/Allegiance.Core/Sim/Ship.NpcUpdates.cs:246](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.NpcUpdates.cs:246).

Tests read: [tests/Allegiance.Core.Tests/P708ShipUpdateTests.cs:172](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P708ShipUpdateTests.cs:172).

### S37 · updates · wingman_nan_repair

**correct**; original weight 5. Negative-power gun, skill-one lead, close strafe/throttle, full-ship stop versus station continue and mine predicate have actual projectile and live order tests.

Native: [Igc/shipIGC.cpp:2608-2678](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:2608); [Igc/common.cpp:121-254](/home/domingo/Development/Allegiance/src/Igc/common.cpp:121).

Port: [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1); [src/Allegiance.Server/Server.ShipCommands.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/Server.ShipCommands.cs:1).

Tests read: [tests/Allegiance.Core.Tests/P676SimRulesTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P676SimRulesTests.cs:1).

### S38 · updates · same_side_miner_arbitration

**correct**; original weight 5. Least ore among physically ready friendly/allied contenders wins; stable sector-order ties, both insertion orders, travel exclusion, Land-only return and mixed players are tested.

Native: [Igc/shipIGC.cpp:2092-2168](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:2092).

Port: [src/Allegiance.Core/Model/Sector.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Model/Sector.cs:1); [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1).

Tests read: [tests/Allegiance.Core.Tests/P652EconomyDroneAiTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P652EconomyDroneAiTests.cs:1).

### S39 · updates · npc_retreat_recheck

**correct**; original weight 4. Retreat pauses accepted work and rechecks after ten seconds; repair restores old order unless miner target moved sectors. Carrier eligibility and real docking paths are covered.

Native: [Igc/shipIGC.cpp:1867-1957](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:1867).

Port: [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1); [src/Allegiance.Server/ServerHost.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Server/ServerHost.cs:1).

Tests read: [tests/Allegiance.Core.Tests/P652EconomyDroneAiTests.cs:357](/home/domingo/allegiance-codex-p743/tests/Allegiance.Core.Tests/P652EconomyDroneAiTests.cs:357).

### S40 · warnings · c_wmCrowdedSector

**correct**; original weight 5. Positive sector cost on player pilot sets crowded bit; authoritative wire rising-edge VO and shared loop/reset covered, including live damage/ejection and disabled stress mode.

Native: [Igc/igc.h:637](/home/domingo/Development/Allegiance/src/Igc/igc.h:637); [Igc/shipIGC.cpp:408-425](/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:408); [WinTrek/WinTrek.cpp:8664](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:8664).

Port: [src/Allegiance.Core/Sim/Ship.cs](/home/domingo/allegiance-codex-p743/src/Allegiance.Core/Sim/Ship.cs:1); [src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:254](/home/domingo/allegiance-codex-p743/src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:254).

Tests read: [tests/Allegiance.ClientCore.Tests/P629ShipWarningTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/P629ShipWarningTests.cs:1); [tests/Allegiance.ClientCore.Tests/P629PenaltyLiveTests.cs](/home/domingo/allegiance-codex-p743/tests/Allegiance.ClientCore.Tests/P629PenaltyLiveTests.cs:1).

## Split coverage

| Area | Original parent | Native parts | Completed / partial / missing | Conserved weight |
| --- | --- | ---: | --- | ---: |
| screens | Main game screen / HUD | 2 | 1 / 1 / 0 | 5 |
| screens | Chat pane | 2 | 0 / 2 / 0 | 5 |
| screens | Hangar / loadout screen | 9 | 4 / 3 / 2 | 5 |
| screens | Game-over screen | 5 | 2 / 1 / 2 | 5 |
| screens | Scoreboard / leaderboard screen | 3 | 0 / 0 / 3 | 4 |
| screens | Game-starting (countdown) screen | 2 | 2 / 0 / 0 | 4 |
| screens | Intro screen | 8 | 3 / 3 / 2 | 2 |
| screens | Splash / loading screen | 2 | 1 / 1 / 0 | 2 |
| screens | Training screen | 11 | 1 / 2 / 8 | 3 |
| screens | Character-info screen | 3 | 0 / 2 / 1 | 4 |
| screens | Selection pane | 2 | 2 / 0 / 0 | 3 |
| screens | Team pane / screen | 5 | 2 / 2 / 1 | 4 |
| screens | Teleport pane | 3 | 2 / 1 / 0 | 3 |
| screens | Loadout panel (shop) | 3 | 1 / 2 / 0 | 5 |
| screens | Investment screen | 4 | 1 / 3 / 0 | 4 |
| screens | Commander / sector map | 3 | 2 / 1 / 0 | 4 |
| screens | Sector info pane | 4 | 2 / 1 / 1 | 4 |
| screens | F-key help overlay(s) | 2 | 1 / 1 / 0 | 4 |
| screens | Mission briefing screen | 2 | 1 / 1 / 0 | 3 |
| screens | Side-select screen | 2 | 2 / 0 / 0 | 4 |
| screens | Server list / lobby browser | 3 | 1 / 2 / 0 | 4 |
| screens | Ready-check (pre-flight) | 2 | 2 / 0 / 0 | 4 |
| commander | Investments / buy-in | 4 | 1 / 3 / 0 | 4 |
| commander | Drones: constructors | 4 | 3 / 1 / 0 | 4 |
| commander | Team leadership / owner (promote, owner-only cmds) | 2 | 1 / 1 / 0 | 4 |
| commander | Wingman AI (attack/defend lead solving) | 2 | 2 / 0 / 0 | 4 |
| lobby | Ready / force-ready check | 2 | 2 / 0 / 0 | 5 |
| lobby | Mission briefing screen | 2 | 1 / 1 / 0 | 4 |
| lobby | Training missions (tutorial) | 11 | 1 / 2 / 8 | 4 |
| lobby | Team lobby / team pane | 3 | 2 / 1 / 0 | 4 |
| screens | in-game key rebinding popup | 3 | 2 / 1 / 0 | 4 |
| commander | Wing / command orders to drones | 3 | 2 / 1 / 0 | 5 |
| commander | Queued command reception (accept/reject/clear) | 2 | 2 / 0 / 0 | 5 |
| commander | Build / research queue | 3 | 2 / 1 / 0 | 5 |
| commander | Drones: miners | 3 | 2 / 1 / 0 | 5 |
| commander | Drones: mine layers | 2 | 1 / 1 / 0 | 4 |
| commander | Ballots / votes | 2 | 2 / 0 / 0 | 4 |
| commander | Alliances | 2 | 2 / 0 / 0 | 4 |
| commander | Team chat | 2 | 1 / 1 / 0 | 5 |
| commander | Quick-chat / orders menu | 2 | 2 / 0 / 0 | 5 |
| commander | ChatBuoy (buoy-targeted orders) | 2 | 2 / 0 / 0 | 4 |
| commander | Commander sector-map overlays | 2 | 2 / 0 / 0 | 4 |
| commander | Stats / scoring (score roster, kill bonus) | 2 | 2 / 0 / 0 | 4 |
| commander | Economic commands (autodonate, donate, request money) | 3 | 3 / 0 / 0 | 4 |
| commander | Sector threat / alerts | 2 | 1 / 1 / 0 | 4 |
| commander | Commander drone view / follow | 2 | 2 / 0 / 0 | 3 |
| lobby | Server list / browse lobby | 2 | 1 / 1 / 0 | 4 |
| lobby | Connect to server (host/IP + character) | 2 | 1 / 1 / 0 | 5 |
| lobby | Join game | 2 | 2 / 0 / 0 | 5 |
| lobby | Side select (choose team) | 2 | 2 / 0 / 0 | 5 |
| lobby | AFK / auto-seat handling | 2 | 1 / 1 / 0 | 3 |
| lobby | Kick / ban / mute (moderation) | 3 | 2 / 0 / 1 | 4 |
| lobby | Lobby chat | 2 | 1 / 1 / 0 | 4 |

Wrong or misleading native citations were corrected as part of the decomposition: `gamescreen.cpp` is the lobby browser, not the combat HUD; current-match scoreboard data is not the persistent `leaderboardscreen.cpp` query/search/pagination/profile flow. Those career controls remain missing. Keeping the replacement live display does not justify native leaderboard credit. See the corresponding native child citations in the matrix/manifest.

## Larger follow-ups

1. **Investment list/gates (S01):** add native predecessor and continued-purchasability state, exact station-type builder identity, group tabs and selected prerequisite detail. Native `WinTrek/loadout.cpp:213-240,777-785,1004-1011,1095-1139`. Existing tests currently codify approximations.
2. **Constructor behavior (S03):** preserve tested build/abort/conversion, then add native drilling alignment, roll/throttle and collision hull-hit feedback. Native `Igc/shipIGC.cpp:875-895,2262-2300`; this is beyond a display fix and was not changed here.
3. **Leader departure (S04):** prefer native donation-vote candidate while Started before fallback succession. Native `FedSrv/fsmission.cpp:949-990` and `FedSrv/FedSrv.CPP:13981-14024`. Current explicit “first other player” test must be replaced with competing-candidate tests.
4. **Boarding refusal/recovery (S10):** retain requested parent and native docked/in-game gating, teleport-lockdown release and turret-specific cue/message. Native `clintlib/appmsg.cpp:1899-1910`, `WinTrek/teleportpane.cpp:276-293`.
5. **Screen controls (S14/S20/S21/S22):** complete auto-accept/team configuration, boarded disembark-and-launch, mining/visibility-aware helium samples, hover/expand behavior, game-over team totals and sort headers. Native `WinTrek/teamscreen.cpp:1554-1563,2773-2815`, `WinTrek/hangarscreen.cpp:145-300`, `WinTrek/sectormap.cpp:180-240,350-369`, `Igc/asteroidigc.h:280-285`, `WinTrek/gameoverscreen.cpp:840-1009`.
6. **Exterior turn audio and collision instances (S31/S34):** resolve actual hull exterior SoundID, distinguish inside/outside and use remote spatial sources. Native `WinTrek/trekigc.cpp:1337-1344`; fixed fighter/capital interior asset mapping is insufficient. Also compare per-party collision instances/sources and native mixer serialization before changing the current single-impact cue reduction (`Igc/shipIGC.cpp:1193-1200`).
7. **Production transition tests (S09/S35):** exercise StartChat compose/send/focus and manual-input autopilot cancellation with a cue sink. Native `WinTrek/WinTrek.cpp:9441-9446,8295-8316`; existing binding/threshold/asset tests do not prove these transitions.
8. **Remaining split-screen gaps:** training lessons 1/3/4/5/6/8 and slideshow/free-flight flow (`WinTrek/trainingscreen.cpp:160-255`); intro Help/options/credits/replay (`WinTrek/introscreen.cpp:1799-1876`); career leaderboard/profile service and controls (`WinTrek/leaderboardscreen.cpp:435-670`, `WinTrek/CharInfoScreen.cpp:717-880`); native keymap OK-versus-Cancel persistence (`WinTrek/trekinput.cpp:2447-2465`). Their child rows retain explicit partial/missing status. Additional decomposition also leaves miner optimal travel, layer default search, generic commandee target resolution, native threat-icon presentation, directory/social chat, complete connection retry/abort, automatic seating policy differences and moderator bans as explicit follow-ups in the child rows. Native rules: `Igc/common.cpp:1830-2000`, `Igc/shipIGC.h:2417-2434`, `clintlib/clintlib.cpp:2200-2240`, `WinTrek/sectormap.cpp:1056-1099`, `clintlib/appmsg.cpp:2640-2665`, `WinTrek/introscreen.cpp:1667-1699`, `WinTrek/teamscreen.cpp:2962-2982`, `FedSrv/FedSrv.CPP:6749-6818`.

## Validation and evidence

The mandated single full sweep and its required checks are complete; exact results are appended below. The [validation.json](P743_assets/validation.json) summary records exact invocations, exit codes, case counters, failed-test isolation and artifact checks.

Build initially hit NuGet’s read-only default HTTP cache. Re-running with `NUGET_HTTP_CACHE_PATH=/tmp/p743-nuget-http` resolved it; both successful whole-solution builds reported **0 warnings / 0 errors**. The final build includes the regression test. This is an environment fix; no dependency/build setting was changed.

Every affected `SectorInfoPaneLayoutTests` method ran **3 separate exact-method invocations** (12 methods × 3 = 36 invocations, 39 cases including the two-case selection theory). Raw logs/TRX are outside the repository. Core and Server are unchanged, so the brief’s conditional AOT publish does not apply. Bot/flight/simulation code is unchanged, so its conditional 5× never-parks run does not apply. The required one-sweep run is unfiltered and includes live/loopback tests. Rig-only manual 221/200v200 work was not invoked.

Raw evidence is at `/tmp/allegiance-evidence/P743/`: baseline CSVs and scorecard, build logs, exact test logs/TRX, invocation ledger, and audit-generation scripts. The brief requested `~/allegiance-evidence/P743/`, but this session permits writes only in the workspace and `/tmp`; the permitted outside-repository path is used instead. No raw TRX/log/archive is added under `docs/reports`. Small JSON evidence only is kept in [P743_assets](P743_assets/), below the 2 MB task cap. All changed text files use LF line endings.

## Completed test results

| Check | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests (one sweep) | 4879 | 0 | 0 | 766.01 |
| Allegiance.ClientCore.Tests (one sweep) | 1377 | 3 | 1 | 1667.48 |
| Allegiance.ClientCore.MonoGame.Tests (one sweep) | 2435 | 0 | 3 | 962.77 |
| Affected layout tests (36 isolated invocations) | 39 | 0 | 0 | — |
| local-showcase-opt-in | 1 | 0 | 0 | — |
| lobby-supplement | 19 | 0 | 0 | — |
| scorecard-final-1 | 1 | 0 | 0 | — |
| scorecard-final-2 | 1 | 0 | 0 | — |
| scorecard-final-3 | 1 | 0 | 0 | — |

The normal ClientCore sweep keeps the existing opt-in gate for the local P660 showcase. A separate invocation explicitly set `ALLEGIANCE_LIVE_P660_SHOWCASE=1` and passed that case; it was not left unexecuted. Lobby was also run once as a supplementary assembly check. The final matrix validator test ran three separate exact-method invocations after all matrix edits.

Full-sweep nonpasses, retained as observed:

- `Allegiance.ClientCore.Tests.P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(civA: 25, civB: 3, generated: False)` — Failed: Assert.Equal() Failure: Values differ Expected: 2 Actual:   1
- `Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` — Failed: Assert.Equal() Failure: Values differ Expected: 1053 Actual:   1060
- `Allegiance.ClientCore.Tests.ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` — Failed: Assert.Equal() Failure: Values differ Expected: 1060 Actual:   1052
- `Allegiance.ClientCore.Tests.P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms` — NotExecuted: Set ALLEGIANCE_LIVE_P660_SHOWCASE=1 for the full 15v15 MCP proof
- `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before` — NotExecuted: The private Meshy asset pack is not configured. Set the ALLEGIANCE_MESHY_PACK_PATH environment variable — or add 'ALLEGIANCE_MESHY_PACK_PATH=<path>' to the gitignored local.env at the repo root — to enable the optional Meshy ships. Without it every hull renders native. See docs/P601_MESHY_PRIVATE_PACK.md.
- `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set` — NotExecuted: The private Meshy asset pack is not configured. Set the ALLEGIANCE_MESHY_PACK_PATH environment variable — or add 'ALLEGIANCE_MESHY_PACK_PATH=<path>' to the gitignored local.env at the repo root — to enable the optional Meshy ships. Without it every hull renders native. See docs/P601_MESHY_PRIVATE_PACK.md.
- `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists` — NotExecuted: The private Meshy asset pack is not configured. Set the ALLEGIANCE_MESHY_PACK_PATH environment variable — or add 'ALLEGIANCE_MESHY_PACK_PATH=<path>' to the gitignored local.env at the repo root — to enable the optional Meshy ships. Without it every hull renders native. See docs/P601_MESHY_PRIVATE_PACK.md.

Failed cases were re-run three times each, separately, after all three sweeps finished:

- `Allegiance.ClientCore.Tests.P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(civA: 25, civB: 3, generated: False)` — load flake per brief; exact case passed 3/3 alone. Original full-sweep failure is retained above.
- `Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` — load flake per brief; exact case passed 3/3 alone. Original full-sweep failure is retained above.
- `Allegiance.ClientCore.Tests.ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` — load flake per brief; exact case passed 3/3 alone. Original full-sweep failure is retained above.

These observations are recorded in `docs/TEST_FLAKES.md`; no assertion, timeout, quarantine or gameplay behavior was relaxed.

Final scorecard validation and `git diff --check` pass. Weight conservation, source paths/citation ranges, LF and the evidence-size limit are checked in `validation.json`. No commits or pushes were made.
