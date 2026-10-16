# P713 — lobby and team-screen interface parity

Base: `f3948b57`, branch `codex/p713`. Changes are left in the working tree; no commit or push.

## Result and scorecard

The shared join/team/ready path now restores a joiner's current roster and roles, allows each team's commander to toggle force-ready, clears the commander's AFK flag on that action, keeps eliminated players in side selection until admission to an active team, and excludes known enemy ready flags from the teammate roster. The team pane displays commander, AFK and wing membership. Commander wing assignments now traverse the existing real wire path and produce the native critical assignment notice.

`python3 scripts/parity-scorecard.py` validates all 438 interface rows and 473 simulation rows. Simulation rows are unchanged.

| Interface area | Weighted before | Weighted after | Complete/eligible before → after |
| --- | --- | --- | --- |
| Commander | 81/81 (100.00%) | 81/81 (100.00%) | 19/19 → 19/19 |
| Commands | 533/591 (90.19%) | 533/591 (90.19%) | 162/190 → 162/190 |
| Fedmsg | 443/515 (86.02%) | 447/515 (86.80%) | 107/125 → 108/125 |
| Lobby | 39/51 (76.47%) | 39/51 (76.47%) | 9/12 → 9/12 |
| Parts | 135/153 (88.24%) | 135/153 (88.24%) | 34/40 → 34/40 |
| Screens | 79/91 (86.81%) | 79/88 (89.77%) | 19/24 → 19/23 |
| **Total** | **1310/1482 (88.39%)** | **1314/1479 (88.84%)** | **350/410 (85.37%) → 351/409 (85.82%)** |

Only SET_WINGID adds implementation credit (+4). The selection-pane correction removes three inapplicable weight units; it adds no implementation credit. The nine previously done rows retain their status with corrected behavior/evidence. Lobby's three open features remain open. Raw [before](P713_assets/scorecard-before.txt) and [after](P713_assets/scorecard-after.txt) scorecards are included.

## Open rows at the start, descending weight

| Weight | Area | Row | Initial status |
| --- | --- | --- | --- |
| 4 | Lobby | Create game / new game wizard | partial |
| 4 | Lobby | Kick / ban / mute (moderation) | partial |
| 4 | Lobby | Training missions (tutorial) | partial |
| 3 | Screens | Training screen | partial |
| 3 | Screens | Selection pane | partial |
| 2 | Screens | Intro screen | partial |
| 2 | Screens | Splash / loading screen | partial |
| 2 | Screens | Video screen (cinematic) | missing |

Existing `na` rows (squads, Zone Club, Zone events) are excluded from both eligibility and this open list.

## Why this bundle

Eleven matrix rows were audited together: four lobby rows, four corresponding screen rows, the two messages carrying their mission/wing state, and the native selection-pane wrapper. They share the existing session → ClientWorld → preflight/team-screen path. Several rows were marked done while the actual first-commander and eliminated-team flows were broken. Fixing those paths is a coherent native interface bundle; the other open features require separate creation, tutorial or cinematic systems.

All native citations below are relative to `~/Development/Allegiance/src/`. `native_intent` is recorded in each updated matrix row. Native behavior here is **design**; the empty pane's framework scaffolding is an **era constraint**. No native bug behavior was copied as a gameplay rule.

| Matrix row | Weight | Native evidence and intent | Port evidence / result |
| --- | --- | --- | --- |
| Lobby: Join game | 5 | `FedSrv/fsmission.cpp:3936-3974`: current mission, then all player records; **design** | `Server.Lobby.cs`, join snapshot; first commander and late joiner receive roles and non-default ready state |
| Lobby: Side select (choose team) | 5 | `WinTrek/teamscreen.cpp:2962-2982`: request an active different side and await admission; **design** | `LobbyScreenState.CanLeaveSideSelection`, used by `Game1.UpdateSideSelect`; an existing eliminated ship cannot bypass selection |
| Lobby: Ready / force-ready check | 5 | `WinTrek/teamscreen.cpp:1554-1559,2773-2793`; `FedSrv/FedSrv.CPP:6987-7015`: own team's commander, checkbox toggle, clear own AFK; **design** | Shared force-action decision drives real input and hint; sends both true and false; readies commander when needed |
| Lobby: Team lobby / team pane | 4 | `WinTrek/teamscreen.cpp:452-505`; `FedSrv/fsmission.cpp:3943-3974`: role and current readiness on the roster; **design** | Current roles on join; `ReadyCheckRoster` keeps known other-side ready entries out of teammate placeholders |
| Screens: Side-select screen | 4 | `WinTrek/teamscreen.cpp:2962-2982`; **design** | Same active-side admission rule as lobby selection; existing active-only layout retained and verified |
| Screens: Ready-check (pre-flight) | 4 | `WinTrek/teamscreen.cpp:1554-1559,2773-2793`; **design** | Hint and keyboard/gamepad/touch action share commander authority and the server's forced-state echo |
| Screens: Team pane / screen | 4 | `WinTrek/teampane.cpp:627-638`; `WinTrek/teamscreen.cpp:452-505`; **design** | Real commander/AFK/wing columns; world gathering stays inside session Gate; obsolete wings-unavailable note removed |
| Screens: Game-starting (countdown) screen | 4 | `clintlib/appmsg.cpp:2314-2346`; `WinTrek/WinTrek.cpp:8129-8137`: display countdown, enter combat at Started; **design** | Existing authoritative Started release retained; countdown layout verified alongside corrected commander and forced-ready display |
| Fedmsg: 38 MISSIONDEF | 4 | `Inc/Messages.h:294-318`; `_Utility/Utility.h:60-64`; `Igc/igc.h:2807`: six short leader IDs at absolute offset 814; **design** | Leader-array codec; join definitions explicitly declare empty roles before player/role snapshots so native role handlers have valid predecessor state |
| Fedmsg: 106 SET_WINGID | 4 | `FedSrv/FedSrv.CPP:5312-5344`; `clintlib/appmsg.cpp:2129-2147`; `Igc/common.cpp:23-35`; `FedSrv/fsmission.cpp:697-709`: commander teammate assignment, corrected commanded bit, own critical notice, ten named wings; **design** | Server restores existing commander branch; client updates known teammates, ignores missing/other-side entities, posts native text for commanded self; team pane reads wing membership. **partial → done** |
| Screens: Selection pane | 3 | `WinTrek/selpane.cpp:17-52,71-100`: child layout wrapper and an empty RowPane whose selection callback only returns true; **era constraint** | No independent selection-screen behavior exists at the inventoried native site. Existing targeting remains separate. **partial → na**, inventory correction only |

The native SET_WINGID receive path skips an uncommanded self echo because native updates self optimistically. The port keeps its established server-confirmed self echo behavior (`ConnectionSession.SendWingId` does not mutate state). This transport/UI adaptation is explicit; commander assignment, membership scope and notice predicates match native. This wave does not add a manual wing selection/retag widget.

## Implementation and regression evidence

- `src/Allegiance.ClientCore/State/LobbyScreenState.cs`: shared admission and force-ready decisions, including missing-role and Started-stage gates.
- `src/Allegiance.Server/Server.Lobby.cs`: initial leader sentinels, player records before role snapshots, per-mission role state and same-side ready state. Snapshot delivery is confined to the joining peer; elections and periodic broadcast code are unchanged.
- `src/Allegiance.Core/Wire/MissionDef.cs`: preserves the 864-byte layout and adds the existing native leader array. Unspecified codec fields keep their legacy defaults; the join producer supplies explicit empty sentinels.
- `src/Allegiance.Server/Server.ShipCommands.cs` and `src/Allegiance.ClientCore/State/ClientWorld.MissionEntry.cs`: commander-assignment branch and native notification, with the existing side-wide relay.
- `Game1.Panes.cs`, `ReadyCheckLayout.cs`, `ReadyCheckRoster.cs`, `TeamPaneLayout.cs`, `Game1.Rendering.cs`: actual screen/input integration and synchronized team-pane gathering.

New wire tests independently check offset 814, signed leader IDs, neighboring bytes and decoding an independently edited native-shaped payload. ClientWorld tests feed real encoded packets through `ConnectionSession.ITransportSite`, covering all ten wing names, role transfer, non-owner commander authority, unchanged state before echo, stage gating, invalid/missing/other-side wing recipients, and eliminated-team admission. Existing roster/layout tests cover enemy-side migration, commander/AFK/wing labels, ASCII safety and countdown hold.

`P713LobbyScreenLiveTests` runs a real loopback server and five real clients, invokes the production MonoGame `ToggleTeamReady` handler without a graphics device (the repository's established headless Game1 pattern), and verifies actual PLAYER_READY / FORCE_TEAM_READY / TEAM_READY receipts, late-join snapshots, native wing assignment text, refused non-commander retagging fenced by a real self-assignment echo, late-join wing membership, forced/unforced state and screen rows. It uses no mock server or transport.

Development failures were retained separately from certification: the loopback test first exposed missing self-election delivery and the server's stale self-only wing branch. A previous command fixture supplied an enemy wing update without establishing the local side; it now supplies the same-side membership native requires. A multi-mission leadership fixture expected no initial role state; it now asserts B receives exactly B's own leader/owner and no A roles. Neither correction weakens cross-mission or command assertions. No receipt deadline, quarantine trait or gameplay assertion was relaxed.

## Verification

The production-revision whole-solution [build](P713_assets/build.log) completed with **0 warnings / 0 errors**. The [build fingerprint](P713_assets/final-build-fingerprint.json) records the final source hashes and build completion time.

The final affected cohort passed **115 methods × 3 exact-method isolated runs = 345 invocations / 384 case passes**, with **0 failures / 0 skips**. The [selected certificate](P713_assets/affected-certificate.json) identifies every invocation and source hash. Earlier overlapping exploratory checks and the pre-wing-snapshot revision are retained in the ledger but excluded from this certificate.

The final never-parks showcase passed **10/10**, with **0 failures**. Every run reached **25/30** objectives and observed real shots, hits and kills; the largest low-speed-with-target interval was **2.806 seconds**, below the unchanged five-second assertion. The [selected runs](P713_assets/showcase-certificate.json) and [per-run metrics](P713_assets/showcase-metrics.json) are included. Three earlier passing attempts started before the final build and are excluded. No baseline rate comparison was required because no showcase attempt failed.

Exactly **two** unfiltered sequential full-solution sweeps completed:

| Sweep | Project | Passed | Failed | Skipped |
| --- | --- | ---: | ---: | ---: |
| 1 | Core.Tests | 4,723 | 3 | 0 |
| 1 | ClientCore.Tests | 1,118 | 1 | 0 |
| 1 | ClientCore.MonoGame.Tests | 2,244 | 0 | 3 |
| 1 | Lobby.Tests | 19 | 0 | 0 |
| 2 | Core.Tests | 4,726 | 0 | 0 |
| 2 | ClientCore.Tests | 1,119 | 0 | 0 |
| 2 | ClientCore.MonoGame.Tests | 2,244 | 0 | 3 |
| 2 | Lobby.Tests | 19 | 0 | 0 |

Raw totals: **16,212 passes / 4 failures / 6 existing optional Meshy-pack skips**. The second sweep was fully clean apart from the same three optional skips. The three first-sweep Core failures each passed **3/3 fresh exact-method isolated invocations after both sweeps** (including both transport modes for the shutdown method). Under the brief’s rule they are load/timing flakes; the isolated passes do not establish their underlying cause or a fix. No transport, loadout or game-over implementation was changed for these observations.

| First-sweep Core failure | Symptom | Post-suite isolation |
| --- | --- | --- |
| `Transport.P670SendStallTests.Departed_peer_bytes_remain_in_host_window_totals` | ThreadPerPeer cleanup: `ThreadStateException` at `TcpTransport.Shutdown:533` | 3/3 methods; 6/6 data cases |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Empty expected loadout-change receipt collection | 3/3 |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | Missing matching game-over roster element | 3/3 |

See the [full validation summary](P713_assets/validation-summary.json), [complete ledger](P713_assets/verification.json), and [flake ledger](../TEST_FLAKES.md#p713-observation-2026-10-16).

The ClientCore failure was the leader-command fixture, not a product regression. It put B first on the other team, then asserted B could not be a team leader; native elects the first member of each empty team (`FedSrv/fsmission.cpp:601-613`). The original method failed **3/3 alone**. The fixture now seeds the other side and seats B as an actual same-side non-leader, retaining the default Started-stage team-balance rule (`FedSrv/fsmission.cpp:4614-4626`). It waits for B to know A's leadership and the same real NPC, so the negative command result cannot pass merely because the target is unknown. No assertion, deadline or configuration limit was relaxed. It passed **3/3 alone** after the correction, passed in sweep 2, and passed another **3/3** isolated runs after both sweeps. The [post-fixture build](P713_assets/build-post-fixture.log) is **0 warnings / 0 errors**; its [fingerprint](P713_assets/post-fixture-build-fingerprint.json) confirms all previously certified production and affected-test sources are unchanged. The existing showcase certificate therefore remains applicable.

The [invocation ledger](P713_assets/verification.json) records commands, raw counters, timings, failures/stacks and TRX hashes. The [runner](P713_assets/verify.py) and [final certification driver](P713_assets/finish_gates.py) record the selected cohort and sequential order. Raw TRX/log files remain under `.verify-p713/` in this working tree; development diagnostic TRX files remain under `/tmp/p713-diagnostic/`.

The suite gate was exactly two unfiltered sequential sweeps of Core, ClientCore, MonoGame and Lobby. The brief's explicit two-sweep gate takes precedence over its lane paragraph's one-pass minimum. Local P496 capture, P539 baseline/reproduction, P542 and P660 opt-ins were enabled; no timing windows were shortened. No remote rig/221 or 200v200 runs were performed. No assertion, deadline or quarantine setting was relaxed; no production change was made to clear suite failures.

## Remaining scope

The open new-game wizard, moderation UI, full training UI/tutorial, intro, loading and video features are unchanged. The selection-pane exclusion is a native-source inventory correction, not a new targeting feature. Changes in `Game1.Rendering.cs` only gather UI team-pane data. The simulation matrix, bucket/economy/research implementation, sound, periodic broadcast pipeline, security, 3D renderer/FSR and platform/installer implementations are unchanged.
