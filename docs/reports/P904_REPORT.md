# P904 — partial sim parity certification

Date: 2026-10-31. Worktree branch: `codex/p904`. No commits, pushes, remote-host contact, stashes or synthetic load.

## Result

Audited all 17 requested rows / 30 weight. Four rows / eight weight are certified in production after three behavioral fixes; 13 remain partial with explicit gaps. No row outside the brief, scorer rule or baseline flag was changed. Full per-row findings are in [the certification matrix](../parity/p904-certification.csv).

The fixes are:

- Shared live `Mission.GetModel` now serves actual chat/drone order resolution and wire ship lookup. Chat orders naming live probes, mines, treasures and buoys now resolve instead of being dropped. Static station records cannot override registry deletion. Native has **eight** supported kinds and returns null for missiles, despite the old remaining-work description naming missiles.
- Payday income is sampled **after movement**, so station changes in that tick affect income. Previously it sampled the previous station state.
- Timeout is resolved before ballot effects; ballots run only in `Started`. Previously a passing draw could replace a timeout winner, and pending ballots resolved in waiting/starting/over missions.

The new suite has 14 cases. Restoring the original chat/ballot code caused five failures (four stage/priority cases and the model resolver); the Started ordinary-draw control passed. The separate station-income regression failed before moving economy after movement. All 14 pass with the fixes. The tests exercise production methods, actual host payouts/packets, real session core acquisition/reset/dispose and the outer simulation pass.

## Per-row verdicts

Native paths below are relative to the read-only native `src/` tree. Citations were re-grepped locally. `P904` test names refer to `tests/Allegiance.Core.Tests/P904CertificationTests.cs`. Port citations and longer remaining-work descriptions are also recorded in the companion CSV.

| Row | Verdict | Native | Port / executable evidence |
|---|---|---|---|
| SIM-0147 | Partial; fails exact roll-call certification | `FedSrv/FedSrv.CPP:7950-8037`; receive reset `:4833`; `_Utility/Messages.cpp:1691-1698` | `TcpTransport.cs:169,184,236,245` uses frame/idle/no-progress timeouts and byte/age backpressure, not >10-second roll calls, >6 absences, or the joining message-count baseline. Existing timeout tests do not prove those rules; no equivalent roll-call test exists. `GetLastComplete` at native `:8025` is only logged, not a separate boot predicate. No lifecycle policy changed. |
| SIM-0469 | Partial; fixed quota not certified | `FedSrv/FedSrv.CPP:3834-3846,3923-3985,4049-4071,4141-4188` | `Server.RankedPose.cs:50` scales heavy count with density and adds a renderer pose floor/source slot; `Server.Broadcast.cs:2737` relays all eligible active turrets. Native uses 2 heavy / 2 light / 2 active, shared sorting and random deviation increments. `UpdateCadenceTests` proves the existing adaptation, not native quota parity. LAN baseline preserved; no incomplete quota flag added. |
| SIM-0156 | Partial; all-site nonzero-offset proof absent | `Igc/igc.h:4760`; `buildingeffectigc.h:142`; `shipIGC.h:1178,1208`; `treasureIGC.cpp:274` | `IIgcSite.cs:34` remains unused identity conversion. `OwnShipServerClock.cs` is a real production offset converter, but it does not certify every building/ship/treasure export. Ship pose packets also use tick indices. `IgcSiteTests` proves identity only; own-ship clock tests prove a subset. |
| SIM-0157 | Partial; all import branches not certified | `Igc/igc.h:4764`; `buildingeffectigc.h:50`; `chaffigc.cpp:48`; `mineigc.cpp:44`; `probeigc.cpp:51`; `shipIGC.cpp:3112,3306`; `treasureIGC.cpp:41` | `ClientWorld.cs:3456,3491,3586` retains several server-domain timestamps; chaff at `:3610` anchors at receipt. `Treasure.cs:31` uses server milliseconds. No conversion binding/test covers every native ship/building/chaff branch. Subset lifetime/clock tests do not complete this row. |
| SIM-0171 | Partial; factory path absent | `Igc/clusterIGC.cpp:39-44,90-94`; `FedSrv/FedSrv.CPP:11181-11216,11228-11232` | `Sector.cs:344` and `Mission.cs` assembly do not call the default `IIgcSite.CreateClusterSite`; `ServerHost` has no override. `Sector.cs:5015` explicitly documents query-built scanners instead of native registered lists. `IgcSiteTests` checks an unattached default shim; it is not host registration/teardown proof. |
| SIM-0517 | **Certified / production** | `FedSrv/fsmission.cpp:2282-2326` | `Mission.cs:1089-1104` → `Server.Economy.cs:1234-1303`. `P904.Periodic_payday_visits_all_sides_preserves_empty_treasury_and_credits_donations`: three sides including an empty side, 3/2 player counts, integer remainders, donation wallet credit, reliable side-scoped PAYDAY and exclusion of a foreign mission with matching side ID. Existing payday/start-income tests supplement it. |
| SIM-0519 | **Certified / production after fixes** | `FedSrv/fsmission.cpp:2328-2593`; caller `FedSrv/FedSrv.CPP:8245-8278` | `Mission.cs:1069-1104`, `Mission.Countdown.cs`, `Server.Periodic.cs:45`, `Server.Broadcast.cs:204-215`. P904 movement-income, economic stage/development/late-phase/treasure and stage/timeout/draw tests cover cross-body ordering. `P739PeriodicTests` covers strict deadline and all six winner priorities, deaths/draw and expired-payday suppression; `P663TreasureParityTests`, `P676SimRulesTests`, payday and ballot tests cover the remaining body. Allied/empty-team victory is actually in `CheckForVictoryByInactiveSides`, not this native body; `P844MissionRulesCertifyTests` separately exercises those production paths. |
| SIM-0523 | Partial; no production producer | `FedSrv/fsmission.cpp:2718-2733,234-236,1954`; `FedSrv/AdminGame.cpp:312-328` | `Mission.cs:850-872` initializes starting techs, but has no lazy per-side altered/new-setting arrays, admin override operation or generation wiring. No production override test. Marked partial/test-only under P903's missing-producer policy; no unused allocator was introduced. |
| SIM-0571 | Partial; whole-body equivalence absent | `FedSrv/fsmission.cpp:5606-5622` | `LobbyHeartbeat.cs:199-303` publishes real HTTP data and retains dirty versions across failed publication. It also refreshes clean leases. Native clears dirty before guaranteed publication, requires cookie-or-disconnected, and refreshes session details on the same coalesced edge. `LobbyHeartbeatTests` / `P866LobbyPublicationTests` prove subsets, not the whole native contract. |
| SIM-0635 | Partial/test-only; same missing factory as SIM-0171 | `FedSrv/FedSrv.CPP:11228-11232,11181-11216`; `Igc/clusterIGC.cpp:90-94` | Same `IIgcSite`/`ClusterSite`/`Sector` evidence. Native allocates host cluster then scanner site and retires host registration on termination. No production factory call or equivalent lifetime certificate. |
| SIM-0699 | Partial/test-only; no canonical production exporter | `Igc/missionigc.cpp:3219-3267,77-91,2318-2328`; `FedSrv/FedSrv.CPP:10140,10473` | Native map dump and Text2Core consume mission export. Port `tools/core2txt/StaticCoreRoundtrip.cs:258` builds offline binary data, preserving original record sequence; no masked/null-buffer/canonical-order mission exporter or runtime dump producer. P870 offline roundtrip tests are not an export-order certificate. Treated as P903, no helper added solely for tests. |
| SIM-0700 | Partial; generic import unimplemented | `Igc/missionigc.cpp:3269-3297,109,2312` | Real `IgcReader` → `StaticCore.Load` / `Mission.Assemble` loads production binary data, but no generic mask/constant replacement/dynamic-object importer covers all timestamps. `P870StaticCoreLoaderTests` and assembly tests prove subsets only. Native advances its pointer only inside the selected-mask branch (`:3280-3293`); that unsafe cursor bug is not a requirement to reproduce memory misuse. |
| SIM-0702 | **Certified / production after fix** | `Igc/missionigc.cpp:3386-3416`; real order callers `FedSrv/FedSrv.CPP:3211,7337` | `Mission.Lookup.cs:11` → `Server.ShipCommands.cs:1603` / `Server.Docking.cs:2136`. `P904.Generic_model_lookup_and_chat_orders_use_live_registries_for_every_native_kind` covers all eight kinds, same ID across types, unclustered ship, deletions, absent IDs and unsupported types, including missile. `P869DispatchTests.Wire_target_resolution_reads_unclustered_mission_ship_and_reflects_registry_removal` covers actual command dispatch. |
| SIM-0703 | Partial/test-only; generic API's callers absent | `Igc/missionigc.cpp:3422-3504`; `AGC/IAGCGameImpl.h:110,217`; `Wopr/AllegianceInterop/igcWrapper.cpp:581-583` | Port has typed registries/core dictionaries, not the generic AGC/Wopr object API. Native `CFSMission.GetBase(side)` is a different station helper, not evidence for this row. No generic-base test. Native also omits buoy/missile and aliases mineType/probeType to dynamic objects at `:3484-3488`; no test-only resolver or broad API was invented. |
| SIM-0788 | Partial; live text-core fallback absent | `Igc/missionigc.cpp:2390-2431,2631-2676`; live callers `FedSrv/fsmission.cpp:164-166`, `clintlib/clintlib.cpp:4212,4221` | `StaticCoreCache.cs:19-47`, `ClientMissionCoreCache.cs:10`, `Server.Lifecycle.cs:780` implement binary/cache loading. P869 cache/boot tests and the new revision-retirement test pass. Missing `<name>/Constants.csv` → version 1337/full text-import fallback is still a real runtime gap. The offline converter does not satisfy it; P903's finding stands. |
| SIM-0789 | **Certified / production managed equivalent** | `Igc/missionigc.cpp:3016-3053`; `Igc/missionigc.h:41-110` | `StaticCoreCache.cs:61-74`, `Mission.Lifetime.cs`, `ClientWorld.MissionEntry.cs:20`, `ClientMissionCoreCache.cs:21`. `P904.Client_revision_change_retires_each_cached_core_at_its_last_session_consumer` loads two old consumers, changes file version, loads a new revision, retires each independently, checks idempotent disposal and fresh reload after last release. P869 mission/session disposal tests supplement it. Immutable managed data needs no native pointer graph destruction; cache ownership must still retire. Only Windows SEH/StackTracer is excluded. |
| SIM-0790 | Partial/test-only; no canonical list exporter caller | `Igc/missionigc.cpp:3187-3217,3241-3263` | Native helper is consumed by mission export and its real dump/tool callers. Port has offline record writing, not canonical masked ExportList/null sizing. No behavioral ExportList test. Original-byte P870 roundtrips do not certify this rule; same no-credit finding as SIM-0699. |

## Reachability and scorecard

No unfinished row was converted to n/a. SIM-0523, SIM-0699, SIM-0703 and SIM-0790 now explicitly say partial/test-only because their required producer/API does not exist; SIM-0635 retains that classification. No test-only resolver/exporter was added. The P868 inventory retains its 199 classifications/citations; four authorized action fields now reference the completed P904 certificates, as its validator requires. The companion CSV records every P904 verdict.

| Sim measure | Before | After |
|---|---:|---:|
| Weighted | 2072/2144 = 96.64% | 2080/2144 = 97.01% |
| Completed / eligible | 770/805 = 95.65% | 774/805 = 96.15% |
| Credit from this brief | 0/30 | 8/30 |

Full outputs: [before](P904_assets/scorecard-before.txt), [after](P904_assets/scorecard-after.txt). Other matrices' headline scores are unchanged. Exactly the 17 authorized sim rows changed.

## Validation

The final solution build passed with **zero warnings and zero errors**. All 101 tools built; the Release `linux-x64 --self-contained -p:PublishAot=true` server publish passed with no warnings (including no IL2026/IL3050). The new production proofs and showcase runs used artwork and the accepted pool/native/parallel/immutable/encode-once/GotoPlan baseline with server GC; interest tiers, native broadcast order and receive decoupling stayed off. Existing branch-specific unit tests retained their own configurations. No performance A/B was run.

| Check | Result |
|---|---|
| Solution build, including final source/comment check | PASS, 0 warnings / 0 errors |
| `DOTNET=~/.dotnet/dotnet tools/build-tools.sh` | PASS, all 101 tool projects |
| Server AOT publish | PASS, no warnings |
| Each new test method three times alone | PASS, 42 case executions; 14 cases per repetition |
| Affected countdown/economy/treasure/ballot/ownership/dispatch/alliance evidence ×3 | 314 pass / one ballot chat-receipt failure; exact ballot method then passes 3/3 alone |
| Additional affected payday methods ×3 alone | PASS, 9/9 |
| ONE Fast run | **FAIL**, 12,737 pass / 12 fail / 3 optional private-pack skips, 12,752 executions in 538.7 s; nine infrastructure checks pass; five executions exceed 10 s |
| All failed/slow methods ×3 alone | Complete; persistent baseline missile-tip and null-defend-target failures remain; path controls pass 21/21; other methods pass. One passing warp-exit isolation still takes 10.078 s |
| Showcase never-parks ×5 serial | PASS, 5/5, zero sim-pass stage failures |
| Native inventories / exclusion audit | PASS, 351 bodies/cases and 200 original/supplemental audit entries; citation bounds/text checked |
| Glossary / authorized row scope / CSV LF / public hygiene / diff | PASS; 17 sim rows and four matching P868 actions; no new media |

The Fast result remains failed. Full classifications and three-run outcomes are in [TEST_FLAKES.md](../TEST_FLAKES.md), including the five path case failures induced by the required scratch TMPDIR, their 21 passing control executions, the two persistent pre-existing assertions, and receipt/timing intermittencies. The path control uses a directory descriptor alias to worktree scratch and writes no files outside the allowed tree. No failing assertion was weakened. An initial isolation helper used namespace directories instead of project names and executed no tests; it was corrected, and only subsequent TRX-bearing standalone runs are counted.

| Showcase | Test duration | Reached | Longest stopped with target | Shots / hits / kills | Starting load average (1/5/15 min) |
|---|---:|---:|---:|---|---|
| 1 | 232.001 s | 24/30 | 0.707 s | 968 / 173 / 1 | 1.21 / 1.61 / 1.86 |
| 2 | 232.660 s | 24/30 | 1.919 s | 658 / 225 / 1 | 3.17 / 1.91 / 1.87 |
| 3 | 209.113 s | 24/30 | 0.000 s | 260 / 1 / 0 | 0.64 / 1.28 / 1.62 |
| 4 | 204.128 s | 24/30 | 0.000 s | 381 / 14 / 0 | 0.88 / 1.13 / 1.49 |
| 5 | 242.163 s | 24/30 | 1.002 s | 1325 / 403 / 3 | 2.15 / 1.34 / 1.47 |

The saved P903 baseline also passed 5/5 (335.425 / 247.538 / 347.380 / 236.239 / 214.849 s). This is a pass-rate comparison under the same accepted settings, not a controlled performance measurement.

Small receipts: [validation](P904_assets/validation.json), [Fast](P904_assets/fast-summary.json), [isolation](P904_assets/isolation-summary.json), [showcase](P904_assets/showcase-summary.json), [native citations](P904_assets/native-citations.json), [old-code failures](P904_assets/mutation-proof.json), [text-fallback experiment](P904_assets/text-fallback.json), [scope/hygiene](P904_assets/scope-check.json). The text experiment exported a full native-format text core successfully, then ran the real server against the text-only asset root: boot rejected the missing binary despite `Constants.csv`, confirming the runtime gap. The summaries are under 2 MB. Raw evidence remains in this worktree’s `.scratch/p904/{gates,results,post-results,fast}/`; the sandbox does not allow the lane’s usual external evidence destination. Generated AOT binaries, text-core data and temporary caches are removed after validation.

## Native intent

- **Design:** type/ID identity, live registries, all-side payday distribution, timeout priority, stage guards, movement-before-economy, mask/order contracts and last-consumer ownership. Their modern equivalents should preserve observable ordering and identity with explicit clock domains, typed lookups and owned immutable data.
- **Era constraints:** modem bandwidth ceilings, DirectPlay roll calls/cookies/scanner host objects and COM/Wopr automation. Modern async transport, HTTP leases and renderer pose floors can be better fits, but they are not automatic proof of exact native rules. The accepted LAN baseline remains intact; fixed modem quotas need a complete default-off experiment and a dense native transcript before any deployment decision. The native text loader is a real runtime capability despite its era-specific file lookup.
- **Bugs:** the port's incomplete/static-record target lookup, pre-movement payday and pre-timeout/ungated ballot sweep were fixed with regressions. Native's masked-import pointer stall and generic-base mineType/probeType aliases are source bugs, not reasons to hide gaps or add unsafe memory behavior. A modern importer should validate lengths, always advance its cursor, and dispatch only selected records; a new generic API should use typed IDs and explicitly document any compatibility aliases.

No rig performance claim, renderer clock redesign, native-equivalent cluster factory, new admin override API, masked mission serializer, runtime text importer or generic AGC/Wopr API was completed. These remain explicit failed certificates and earn zero credit.
