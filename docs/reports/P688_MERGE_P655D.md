# P688 — merge P655d into integ8

Resolved the five conflicted files by editing the working tree. No git write commands, staging, commit, or push were performed. The merge index remains unmerged for Claude to stage and commit.

## Resolution

- `ServerConfig.cs` retains P686's settable properties throughout the configuration graph and its recursive JSON guard tests. Omitted team limits remain **256 players/team** and **1 imbalance**; explicit zero and the **32767** imbalance bypass still bind. P655d's explanation of the generated-JSON default failure is retained.
- `TEST_FLAKES.md` retains both histories: P676/P678/P680/P681 observations and P655d's observations.
- Both CSVs were compared by row identity against both merge stages. The result retains all **137 wiring rows** and **438 parity rows**, including the P672 scoreboard-delta addition. The decisions below use code and tests present in this merged tree. The DELPOSITIONREQ evidence now includes P655d's diagnostics as well as P678's completed lobby behavior; affected dispatch citations were refreshed.
- `FedMsgDispatchCoverageTests.cs` retains all three tests. Recounting unique entries yields **84 native consumed + 17 port consumed = 101**, plus **18 native unconsumed**, accounting for all **102 supported native server-sent messages**. Removed the duplicate registry entry for fmid 42 introduced by the automatic merge, and asserted registry uniqueness and the native/port split.
- Fixed an automatic merge defect in `ConnectionSession.cs`: P655d's new `DELPOSITIONREQ` switch case duplicated P678's existing case. One decode now updates both `LastPositionRefusal` for the owning session and `ClientWorld`'s lobby state/events. The P655 refusal test additionally verifies pending-request removal and the result event from that same receipt.
- Retained P655d's case-insensitive duplicate-name refusal before ID allocation/privilege authentication, pending/seated name reservation and disconnect release, unique per-worker probe names, normal auto-pick, and immediate refusal diagnostics. All tests from both sides remain.

## Row decisions

Every status disagreement resolves to **done**, supported by the merged implementations and tests below. All other statuses are retained. The wiring file has the six lobby-message disagreements below; the parity matrix additionally has the other eight disagreements. Wiring row 219 is present only on integ8 and is retained.

| Row | Merged code evidence | Merged test evidence |
| --- | --- | --- |
| 18 CANCEL_BALLOT | `ConnectionSession`, `ClientWorld`: queued/active ballot removal | `P678LobbyMessageTests` |
| 40 POSITIONREQ | `ClientWorld.Lobby`: scoped request set, feedback and sound | `P678LobbyMessageTests`, `P678LobbyRelayLiveTests` |
| 42 DELPOSITIONREQ | `ConnectionSession`, `ClientWorld.Lobby`, `LobbyStatusLayout`: own diagnostics, scoped removal/result and rejection UI | `P655PositionRefusalTests`, `P655ProbeWorkersLiveTests`, `P678LobbyMessageTests`, `P678LobbyStatusTests`, `P678LobbyRelayLiveTests` |
| 43 LOCK_LOBBY | `ClientWorld.Lobby`, `LobbyStatusLayout`: authoritative lock and lobby display | `P678LobbyMessageTests`, `P678LobbyStatusTests`, `P678LobbyRelayLiveTests` |
| 44 LOCK_SIDES | `ClientWorld.Lobby`, `LobbyStatusLayout`: independent side lock and display | `P678LobbyMessageTests`, `P678LobbyStatusTests`, `P678LobbyRelayLiveTests` |
| 52 AUTO_ACCEPT | `ClientWorld.Lobby`, `LobbyStatusLayout`: per-side state and display | `P678LobbyMessageTests`, `P678LobbyStatusTests`, `P678LobbyRelayLiveTests` |
| 6 OBJECT_SPOTTED | `ClientWorld.MissionEntry`: discovery text/criticality | `P675MissionDiscoveryTests`, `P675MissionDiscoveryWireTests` |
| 95 JOINED_MISSION | `ClientWorld.MissionEntry`: shared core and pre-launch identity | `P675MissionDiscoveryTests`, `P675CommandLiveTests` |
| 96 LOADOUT_CHANGE | `ConnectionSession`, `ClientWorld`: hull/parts/passengers and revival | `P648RelaunchVisibilityTests`, `FedMsgDispatchCoverageTests` |
| TK_ViewMissile | `InputActionCatalog`, `Game1.FlightInput`, `Game1.Director`, `Game1.Camera`: discrete camera toggle | `P675CommandActionTests` |
| TK_ViewChase | `InputActionCatalog`, `Game1.FlightInput`, `Game1.Director`: discrete camera toggle | `P675CommandActionTests` |
| TK_TargetCommand | `CommandActionInput`, `ClientWorld.Orders`, `Game1.Orders`: independent accepted target | `P675CommandActionTests` |
| TK_ExecuteCommand | `CommandActionInput`, `ClientWorld.Orders`, `Game1.Orders`: accept legal target and engage | `P675CommandActionTests`, `P675CommandLiveTests` |
| Fuel/ammo packs (ET_Pack=6) | `Ship`: real depletion, loaded debounce and cargo refill | `FuelPoolAndAfterburnerTests`, `AutoReloadOnEmptyTests`, `P650SimRulesTests` |
| 219 SCORE_ROSTER_DELTA (retained addition) | `Server.Broadcast`, `ConnectionSession`, `ClientWorld`: ordered sparse roster patch | `ScoreRosterDeltaTests`, `P672ScoreRosterTrafficTests` |

## Verification

- Build: `NUGET_HTTP_CACHE_PATH=/tmp/p688-nuget-http dotnet build AllegianceCore.slnx --nologo` — **0 warnings / 0 errors**.
- `python3 scripts/parity-scorecard.py` validates **438 parity rows** and **473 simulation-rule rows**. Weighted totals: **1310/1482 (88.39%)** and **586/1333 (43.96%)**. A separate CSV read verifies all 137 wiring rows have eight fields and unique IDs.
- The source-tree marker scan and `git diff --check` pass.
- P686 JSON/default checks: **11 Core cases**, including the real rig-style boot and P655d's four limit-default cases; **1 MCP graph case**; **1 client settings graph case**. All **13 passed, none skipped**. The rig smoke loads PCore001/m2xspiderw, binds pool transport, checks all effective defaults and advances through payday.
- Dispatch/lobby regression group: **18 passed**, including the combined own-refusal/world-update test and the live P678 relay.
- P682 two-match test: **4 cases × 3 invocations = 12 passed**, covering native/port models with the broadcast pipeline off/on.
- P655d identity/same-name checks: **10 cases × 3 invocations = 30 passed**, including real concurrent same-name logons, case-insensitive pending/seated refusal, name release and normal balance/auto-pick.
- P655d worker/refusal checks: **3 cases × 3 invocations = 9 passed**, including the actual two-worker MCP executable with authenticated and unprivileged probes, and immediate side-refusal reporting.

All **82 focused case executions passed with zero skips**. The full suites run unfiltered and sequentially with `ALLEGIANCE_LIVE_P660_SHOWCASE=1`. Final invocation counters and failures/skips are written to [P688_RESULTS.json](P688_RESULTS.json); raw logs and TRX files are in `/tmp/p688-validation/`.

| Assembly | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4552 | 3 | 0 | 539.339 |
| Allegiance.ClientCore.Tests | 1001 | 0 | 0 | 415.705 |
| Allegiance.ClientCore.MonoGame.Tests | 2186 | 0 | 3 | 266.151 |
| Allegiance.Lobby.Tests | 19 | 0 | 0 | 0.945 |

The ClientCore pass includes `P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms`. MonoGame's only skips are the three `MeshyPackTests` requiring the unavailable private asset pack. Artwork/native resources were available; the rig boot and required live scenarios executed.

The full Core suite has **three failures**, preserved above and in the ledger. After **all four full suites finished**, each failing method passed **3/3 exact-method isolated reruns**, with zero skips:

| Core test | Full-suite symptom | Subsequent isolation |
| --- | --- | --- |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | Peer a1's `SET_START_TIME` collection was empty at the immediate assertion. Distinct names and successful countdown start rule out the duplicate-name refusal path. | 3/3 pass |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | B had not received `HEAVY_SHIPS_UPDATE`. | 3/3 pass |
| `Transport.P670SendStallTests.Diagnostic_updates_and_samples_allocate_zero_bytes_after_warmup` | Cleanup threw `ThreadStateException: Thread has not been started` in `TcpTransport.Shutdown` (line 533), rather than failing the allocation assertion. | 3/3 pass |

These observations match the previously recorded receipt/shutdown flake symptoms in [TEST_FLAKES.md](../TEST_FLAKES.md). The raw full Core run remains a failing run; isolated passes do not establish that those races are fixed. No assertions, deadlines, quarantine traits, or transport behavior were changed for them. The P688 observation is added to that history.

All requested checks ran. The final working tree has no conflict markers and passes `git diff --check`. Claude can stage the resolved files and commit the merge.

