# P718 — FEDMSG interface parity wave 2

Eight active FEDMSG rows completed, weight **32**, on `codex/p718`. Changes remain in the working tree; no commits or pushes. Native references throughout this report are relative to **`~/Development/Allegiance/src/`** and were read from that local source tree.

## Scorecard before → after

`python3 scripts/parity-scorecard.py` validates both matrices. The actual checkout starts at 88.39% interface parity, rather than the brief's approximate 88.8%.

| Area | Weighted before → after | Unweighted before → after |
| --- | --- | --- |
| fedmsg | **443/515 (86.02%) → 475/515 (92.23%)** | **107/125 (85.60%) → 115/125 (92.00%)** |
| commander | 81/81 (100.00%) → unchanged | 19/19 (100.00%) → unchanged |
| commands | 533/591 (90.19%) → unchanged | 162/190 (85.26%) → unchanged |
| lobby | 39/51 (76.47%) → unchanged | 9/12 (75.00%) → unchanged |
| parts | 135/153 (88.24%) → unchanged | 34/40 (85.00%) → unchanged |
| screens | 79/91 (86.81%) → unchanged | 19/24 (79.17%) → unchanged |
| **Interface total** | **1310/1482 (88.39%) → 1342/1482 (90.55%)** | **350/410 (85.37%) → 358/410 (87.32%)** |

Main matrix: 438 rows; 28 existing exclusions; denominators and weights unchanged. Simulation matrix unchanged: 473 rows, weighted 772/1333 (57.91%), unweighted 231/437 (52.86%). Both edited CSVs (`parity-matrix.csv`, `fedmsg-wiring.csv`) have Unix LF endings, including normalization of their pre-existing mixed CRLF/LF endings.

## Initial open FEDMSG inventory, by weight

All 18 open rows were `partial`, each weight **4**; no `missing` FEDMSG rows. Equal-weight entries are listed in numeric order. Only the selected eight rows changed status.

| Weight | Native item | Before → after | Native layout |
| ---: | --- | --- | --- |
| 4 | 31 NEW_SHIP_INSTANCE | partial → partial | Inc/Messages.h:262 |
| 4 | 51 CHANGE_TEAM_CIV | **partial → done** | Inc/Messages.h:407 |
| 4 | 86 ASTEROID_DESTROYED | **partial → done** | Inc/Messages.h:613 |
| 4 | 87 FIRE_EXPENDABLE | partial → partial | Inc/Messages.h:619 |
| 4 | 89 TREASURE_SETS | partial → partial | Inc/Messages.h:624 |
| 4 | 92 PROBES_UPDATE | **partial → done** | Inc/Messages.h:644 |
| 4 | 106 SET_WINGID | **partial → done** | Inc/Messages.h:705 |
| 4 | 124 QUIT_MISSION | **partial → done** | Inc/Messages.h:812 |
| 4 | 126 QUIT_SIDE | **partial → done** | Inc/Messages.h:825 |
| 4 | 128 ENTER_GAME | **partial → done** | Inc/Messages.h:831 |
| 4 | 155 GAME_STATE | **partial → done** | Inc/Messages.h:854 |
| 4 | 156 GAIN_FLAG | partial → partial | Inc/Messages.h:861 |
| 4 | 162 ACQUIRE_TREASURE | partial → partial | Inc/Messages.h:871 |
| 4 | 164 ADD_PART | partial → partial | Inc/Messages.h:881 |
| 4 | 182 REQUEST_MONEY | partial → partial | Inc/Messages.h:940 |
| 4 | 188 WARP_BOMB | partial → partial | Inc/Messages.h:969 |
| 4 | 192 RELAUNCH_SHIP | partial → partial | Inc/Messages.h:988 |
| 4 | 194 PINGDATA | partial → partial | Inc/Messages.h:1006 |

## Bundle and previous-wave boundary

These eight rows share the real **`ConnectionSession.OnReceive → ClientWorld.Apply`** path: side selection and roster changes, the enter-game sync barrier, live object updates, and departure back to the lobby. `ENTER_GAME` gates the probe/asteroid handlers; `QUIT_SIDE` resets that scene while retaining mission membership; `QUIT_MISSION` terminates membership; civilization, wing, and victory-counter state describe the same mission. The server already produces the messages, so no simulation or broadcast work is needed.

The requested `P713_INTERFACE_WAVE.md` and any matching `P7*_INTERFACE_WAVE*.md` predecessor reports are **absent from this checkout** (searched under `docs/`). Reviewed the available relevant predecessors, especially `P675_PARITY_WAVE4A.md` and `P678_PARITY_WAVE5.md`, plus P710/P711/P714/P716 sound-wave summaries. P675 already consumes SET_WINGID for command/discovery behavior; this work fills its explicitly remaining assignment-feedback/roster gap. P678's cancellation, request, lock, and auto-accept rows are untouched. GAIN_FLAG and ACQUIRE_TREASURE remain open rather than claiming additional credit from prior sound-only work.

All eight selected rows have active gameplay use and remain applicable. None warrants a whole-row `n/a` exclusion. Native local cached-loadout-file handling is an era-specific subfeature, discussed below, rather than grounds to exclude ENTER_GAME.

## Native rules, intent, and implementation evidence

Shared implementation: `src/Allegiance.ClientCore/State/ClientWorld.Interface.cs`; seven new session dispatch cases in `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1121`; matching world dispatch; reset/next-mission cleanup. Mutation uses the existing session gate. Read-only civilization/counter maps and a departure record are available to UI/model consumers.

| Row | Native rule and intent | Port behavior and evidence |
| --- | --- | --- |
| **51 CHANGE_TEAM_CIV** | **Design**. Two shorts, random-faction bool, native align-2 tail: `Inc/Messages.h:407-411`. Apply civilization and the independent random choice, notify roster: `clintlib/appmsg.cpp:2530-2539`. | `ClientWorld.Interface.cs:30`: `SideCivilizations` records both fields and replaces the side-roster snapshot's civ without losing name/count/color or changing older snapshots. `P718InterfaceMessageTests.Civilization_echo_updates_roster_and_random_choice_without_losing_side_metadata`. |
| **86 ASTEROID_DESTROYED** | **Design**. Sector/asteroid shorts plus bool: `Inc/Messages.h:613-617`. Native fixed size includes align-2 tail padding: `Inc/messagecore.h:77-86,767-774`; producer uses `sizeof` through `BEGIN_PFM_CREATE`, `FedSrv/FedSrv.CPP:12413-12439`. In-game only; terminate the named sector's rock; explode if requested, otherwise detach the building effect before termination: `clintlib/appmsg.cpp:1692-1718`. | Corrected the **port's** 9-byte codec to native 10-byte size with zero tail pad (`Wire/AsteroidDestroyed.cs:35`). `ClientWorld.Interface.cs:65` removes definition/live health, emits the existing asteroid explosion event, preserves a conversion effect on nonexplosive removal, and ignores stale repeats/sector mismatches. `P718InterfaceMessageTests.Asteroid_termination_removes_health_and_preserves_conversion_effect_only`, both flags. Independent C++ width/inheritance probe reports sizeof=10 (`/tmp/p718/layout.cpp`, `layout.txt`). This fixes a port bug; native padding is intentional. Compatibility with the earlier 9-byte port frame is not retained. |
| **92 PROBES_UPDATE** | **Design**. VarItem points to packed 3-byte records: `Inc/Messages.h:636-646`. In-game, own flight cluster, existing probes only, reverse iteration and BytePercentage fraction: `clintlib/appmsg.cpp:584-606`; producer: `FedSrv/fsship.cpp:865`. | `ClientWorld.Interface.cs:52` updates `ProbeEntity.Fraction` using bp/240, scoped to the own pose cluster. Unknown probes and probes in another cluster are untouched; duplicate IDs retain native reverse-order behavior. `P718InterfaceMessageTests.Probe_updates_require_enter_game_and_current_flight_sector_and_iterate_backwards`. |
| **106 SET_WINGID** | **Design**. Ship/wing shorts and commanded bool: `Inc/Messages.h:705-709`. Look up same-side member; assignment of commanded self posts critical text; notify roster: `clintlib/appmsg.cpp:2129-2147`. Ten exact wing names: `Igc/common.cpp:23-35`. | `ClientWorld.MissionEntry.cs:105` ignores unknown/enemy members and invalid wing IDs, applies the authoritative echo, and posts “You have been assigned to wing …” only for commanded self. `SetWingId.WingName` is shared with `TeamPaneLayout`; `Game1.Rendering` supplies live wing IDs to the roster instead of an unavailable message. Tests: `P718InterfaceMessageTests.Wing_echo_notifies_only_commanded_self_and_does_not_create_unknown_or_enemy_roster_members`, `TeamPaneLayoutTests.Build_shows_authoritative_wing_name`. |
| **124 QUIT_MISSION** | **Design**. ZTS parameter, ship short, align-4 reason enum: `Inc/Messages.h:812-818`; removal handler `clintlib/appmsg.cpp:2501-2526`, helper `clintlib/clintlib.cpp:4037-4077`. Remote player deletion; own disconnect clears chats, ships, players, mission: `clintlib/clintlib.cpp:3805-3848`. Producer: `FedSrv/fsmission.cpp:482`. | `ClientWorld.Interface.cs:161` removes remote membership, ready/request entries and per-player replica state; own departure resets the world and retains reason/parameter. Session closes the actual socket, signals loss once, and stops the rest of the departed mission's packet. Tests: `Remote_quit_mission_removes_player_requests_and_ready_without_touching_other_players`, `Own_quit_mission_clears_world_signals_loss_and_stops_remaining_packet_messages`, and real-socket `P718InterfaceLoopbackTests.Own_mission_departure_closes_actual_session_socket_and_delivers_reason_once`. |
| **126 QUIT_SIDE** | **Design**. Same ZTS/ship/reason shape: `Inc/Messages.h:825-829`. `clintlib/appmsg.cpp:2993-3002` delegates to `clintlib/clintlib.cpp:3889-4034`: remove team leadership/ownership, reset team state/money, preserve ready, move to SIDE_TEAMLOBBY (-2, `Igc/igc.h:514`). Own exit resets scene/ship statuses and ballots: `clintlib/clintlib.cpp:3723-3787`; `Igc/missionigc.cpp:3804-3831`. Producer: `FedSrv/fsmission.cpp:1131`. | `ClientWorld.Interface.cs:82` preserves player/ready identity, retains departure reason, clears privileges and team replica state, and posts reason-specific remote departure text. Own exit clears sector contents/poses and transient targeting state while retaining mission and players. Following JOIN_SIDE updates both entity and PlayerInfo side, including prelaunch identity. Tests: `Quit_side_clears_team_privileges_and_money_preserves_ready_and_join_reseats_identity`, `Own_quit_side_clears_sector_contents_but_keeps_mission_players_and_can_reenter`; existing real-server `ClientJoinSideLiveTests`. |
| **128 ENTER_GAME** | **Design**. Header-only sync-complete marker, possibly before countdown ends: `Inc/Messages.h:831-835`; `clintlib/appmsg.cpp:3040-3045`; `clintlib/clintlib.cpp:3851-3857` sets in-game and emits enter-mission. Producer: `FedSrv/fsmission.cpp:804,2264`. **Era constraint** subfeature: native local loadout cache reset/file reload, `clintlib/clintlib.h:1381-1387,1901-1912`. | `ClientWorld.Interface.cs:49`: expose `InGame` without forcing mission stage or clearing equipped parts; clear it on side departure, game over, reset and next mission. Native filesystem loadout preference cache has no counterpart in this ClientWorld and does not imply erasing wire equipment. `P718InterfaceMessageTests.Enter_game_barrier_preserves_stage_and_inventory_game_state_replaces_every_side_and_resets`; wire order is also tested with ENTER_GAME preceding a live-object update. |
| **155 GAME_STATE** | **Design**. Four independent six-byte arrays in conquest/territory/flags/artifacts order: `Inc/Messages.h:854-860`; side-id application: `clintlib/appmsg.cpp:2392-2407`; producers `FedSrv/fsmission.cpp:3684,3868,4326`. | `ClientWorld.Interface.cs:40`: replace all four counters for all six side indices, including zero values; clear on reset/next mission. `P718InterfaceMessageTests.Enter_game_barrier_preserves_stage_and_inventory_game_state_replaces_every_side_and_resets` asserts side 5's four distinct fields and complete zero replacement. Golden TCP fixture pins all 24 array bytes. |

The observer applies the authoritative own-wing echo, consistent with existing flight-part relay handling. Native skips an uncommanded own echo because it eagerly changes the local wing before sending (`clintlib/appmsg.cpp:2140-2143`); the existing join-command path can also set the local wing before sending. Applying the authoritative self echo is idempotent after that assignment and supports direct sends through `SendWingId`; no additional local prediction is added. Final membership and notification conditions match.

Native unchecked/null assertions for stale objects are not reproduced: missing players/probes/asteroids are ignored. In particular, SET_WINGID already explicitly tolerates a missing member (`clintlib/appmsg.cpp:2135-2138`). No native gameplay bug is deliberately copied. No simulation, research/bucket/economy algorithm, sound subsystem, graphics/FSR/minimap pipeline, broadcast pipeline, security, Android, or installer work was performed. The team-pane change only supplies received wing text to the existing layout. Removing a departed player's replicated money/auto-donate entries is membership cleanup, not an economy-rule change.

## Wire and model proof

`P718InterfaceLoopbackTests.Native_bytes_cross_real_tcp_and_dispatch_in_packet_order` has **eight golden-byte cases**, one per selected row. It checks encoder bytes against independent hex, decoder/re-encoder field preservation, native signed IDs/padding/VarItems/ZTS/packed entries/array order, two-message packet order, and two distinct packets over actual ephemeral loopback TCP. The delivered bytes pass through real `ConnectionSession` dispatch; the asteroid/probe cases assert actual state changes after the preceding ENTER_GAME barrier. A ninth real-socket test verifies own-mission departure and socket teardown.

`P718InterfaceMessageTests` adds ten model cases, including both asteroid termination modes. Team-pane tests add three wing-name cases and replace the obsolete unavailable-wing footer assertion with an explicit unknown-wing row assertion. The P675 join-wing fixture now supplies a same-side member and own side rather than trying to consume an enemy's SET_WINGID. The executable dispatch registry moves seven messages from unconsumed to consumed: **91 native consumed + 11 documented unconsumed = 102 supported native messages**, plus 17 port extensions. No unrelated open row receives score credit.

## Validation

Whole-solution build: `dotnet build AllegianceCore.slnx -m:8 -v:minimal`, **0 warnings / 0 errors**. Required artwork and native-source paths are supplied from the existing `local.env`; live loopback uses real sockets.

Every new or affected method passed **3/3 exact-method isolated invocations** after the build:

| Assembly | Methods | Cases per repetition | Isolated invocations | Case passes |
| --- | ---: | ---: | ---: | ---: |
| Core | 5 | 5 | 15 | 15 |
| ClientCore | 31 | 43 | 93 | 129 |
| MonoGame | 20 | 35 | 60 | 105 |
| **Total** | **56** | **83** | **168** | **249** |

Affected coverage includes the new wire/model tests, dispatch registry, existing wing/server relays, lethal asteroid wire broadcast, real-server side switching, probe lifetime/model behavior, game-over world reset, discovery priorities, and command/team-pane UI models. Final full-suite and parking-gate results follow.

**Gate exception:** Core, ClientCore and Lobby each ran twice unfiltered. MonoGame ran **three** full invocations because restarted coordinators survived the attempted interruption. This exceeded the required two-run limit. Every result is retained; no LiveTiming or live/loopback filter was used. The two ClientCore runs and three MonoGame runs overlapped during the coordination error; the remaining runs used a single coordinator.

| Sweep | Assembly | Passed | Failed | Skipped |
| ---: | --- | ---: | ---: | ---: |
| 1 | Core | 4724 | 1 | 0 |
| 1 | ClientCore | 1139 | 0 | 1 |
| 1 | ClientCore.MonoGame | 2245 | 0 | 3 |
| 1 | Lobby | 19 | 0 | 0 |
| 2 | Core | 4724 | 1 | 0 |
| 2 | ClientCore | 1140 | 0 | 0 |
| 2 | ClientCore.MonoGame | 2245 | 0 | 3 |
| 2 | Lobby | 19 | 0 | 0 |
| extra | ClientCore.MonoGame | 2245 | 0 | 3 |

Standalone P700 never-parks gate: **10/10 passed**. Each invocation exercised the actual local 15v15 battle, with gunfire, hits and kills.

| Run | Battle evidence |
| ---: | --- |
| 1 | P700 local 15v15: 98.8s, samples=987, reached=25/30, longest speed<1 with target=2.506s, shots=5212, hits=2722, kills=12 |
| 2 | P700 local 15v15: 95.7s, samples=956, reached=25/30, longest speed<1 with target=2.006s, shots=4348, hits=2505, kills=11 |
| 3 | P700 local 15v15: 95.3s, samples=952, reached=25/30, longest speed<1 with target=3.007s, shots=4984, hits=2766, kills=12 |
| 4 | P700 local 15v15: 97.5s, samples=974, reached=25/30, longest speed<1 with target=2.019s, shots=4890, hits=2398, kills=16 |
| 5 | P700 local 15v15: 97.7s, samples=976, reached=25/30, longest speed<1 with target=2.005s, shots=4748, hits=2833, kills=17 |
| 6 | P700 local 15v15: 95.0s, samples=949, reached=25/30, longest speed<1 with target=2.006s, shots=4555, hits=2120, kills=9 |
| 7 | P700 local 15v15: 95.5s, samples=954, reached=25/30, longest speed<1 with target=2.104s, shots=4645, hits=2823, kills=17 |
| 8 | P700 local 15v15: 94.9s, samples=948, reached=25/30, longest speed<1 with target=2.006s, shots=4881, hits=2702, kills=15 |
| 9 | P700 local 15v15: 95.4s, samples=953, reached=25/30, longest speed<1 with target=2.507s, shots=4842, hits=2758, kills=16 |
| 10 | P700 local 15v15: 97.2s, samples=971, reached=25/30, longest speed<1 with target=2.005s, shots=4655, hits=2253, kills=15 |

No standalone parking run failed, so the brief's conditional base-rate comparison was not needed.


Full-suite failures and their post-suite isolated triplets are retained in [P718_RESULTS.json](P718_RESULTS.json). Classification is described below. All raw test logs and TRXs remain in `/tmp/p718/`; the durable JSON includes every invocation, exact filter/command, counter, failure message, and skip name.


Both full-suite failures passed **3/3 exact-method isolated reruns after all full suites ended**. They meet the brief's load-flake rule and are recorded in `docs/TEST_FLAKES.md`; the raw failed suite results remain in the table above.

| Test | Full-run failure | Final isolation |
| --- | --- | --- |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Core run 1: no `OnPeerJoined` for raw peer within timeout | **3/3 pass** |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Core run 2: notification collection empty at `Assert.Contains` | **3/3 pass** |

The full-suite successes include the new FEDMSG handlers and wire/model tests. The first ClientCore run skipped the existing P660 opt-in showcase; the second enabled it and passed all 1,140 cases. MonoGame's three skips per invocation are the existing unavailable private Meshy-pack checks. No artwork/native-source gate or loopback test was filtered out. No rig / 221 / 200v200 run was attempted.

A diagnostic stack snapshot taken during the first long Core invocation captured a normal broadcast-publisher wait; that invocation completed naturally, including all broadcast replay cases. Interrupting its coordinator did not stop child/coordinator processes as expected. Restarting caused two ClientCore executions and three MonoGame executions to overlap. Both ClientCore runs and every MonoGame run were preserved independently via TRX snapshots. Further automatic launches were blocked, and the remaining Core/Lobby runs and failure-isolation triplets used a single coordinator. This is an execution error and an explicit breach of the MonoGame exactly-two limit, not a waived gate or a product fix.

The scorecard, 231-row P642 audit, semantic matrix-change check (only the eight selected FEDMSG rows), Unix LF checks, and `git diff --check` pass. No commits or pushes were made.

The explicitly enabled local next-match showcase (`ALLEGIANCE_LIVE_P660_SHOWCASE=1`, `P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms`) also passed **3/3 standalone invocations** after full suites and failure isolation. A final whole-solution build completed with **0 warnings / 0 errors** (`/tmp/p718/final-build.log`). The durable invocation ledger includes these three additional passes.


## P738 merge onto master

Merged `codex/p718` onto the current `integ/batch2` file contents after the
P726 and P732 integrations. All 13 files with actual conflict blocks are
resolved. No git commands, commits or pushes were run; the dispatcher retains
responsibility for recording the resolved index/merge.

### Resolution notes

- Preserved all current audio kinds, playback cases and sound-event tests,
  including P717 mine/shield cues, P719 docking/welcome/boarding cues and P732
  voice selection. The audit finds **37 event kinds and 37 playback cases**,
  exactly one case per kind, with no missing or duplicate cue dispatch.
- Combined P718's wing roster behavior with master's commander/AFK metadata,
  teammate-only wing values, complete roster-gathering lock, compact team pane
  and control hints. Unknown wing values display `WING:?`. The incoming
  three-case named-wing test uses the retained `ShipBrief.WingId` argument.
  Native roster fields: `~/Development/Allegiance/src/WinTrek/teamscreen.cpp:452-505`;
  side-scoped wing relay: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:5312-5344`.
- Kept master's commander authorization documentation and the existing shared
  `WingNames` table instead of introducing a second name table. Known
  same-side members receive bounded authoritative wing updates; commanded
  self assignments also notify the joined player before a launch pose exists.
  The new prelaunch regression covers that identity boundary; native joined
  identity setup is `~/Development/Allegiance/src/clintlib/appmsg.cpp:2150-2214`.
  Native wing rule:
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:2129-2147`; names:
  `~/Development/Allegiance/src/Igc/common.cpp:23-35`.
- Fixed an automatic merge outside the marked conflicts: both parents added
  asteroid-destroyed dispatch, producing duplicate switch cases in
  `ConnectionSession` and `ClientWorld`. There is now one dispatch and P718's
  single handler: it requires ENTER_GAME, checks the named sector, removes the
  rock and health, explodes only when requested, and preserves the construction
  effect on nonexplosive conversion. Native rule:
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:1692-1718`.
  The older P703 research fixture now supplies ENTER_GAME before the live
  asteroid message, consistent with the sync barrier at
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:3040-3045` and
  `~/Development/Allegiance/src/clintlib/clintlib.cpp:3851-3857`.
- Retained P718's civilization, probe, departure, enter-game and victory-counter
  handlers and real-TCP golden-byte coverage. Kept master's P675 same-team wing
  fixture, which supplies real player/side identity.
- Unioned `TEST_FLAKES.md`; retained master's P716 report. The two historical
  reports that only quote marker text have no actual conflict blocks.

Reviewed the incoming report and the overlapping P710/P711/P714/P716/P717/
P719/P721/P723/P725 sound reports, plus P732's voice and merge notes. Historical
sound-report ID interpretations remain superseded by the P732 merge appendix;
this merge preserves its corrected catalog and producer IDs.

### Parity reconciliation

Reconciled 31 rows within the marked matrix blocks individually; 24 differed
between the parents. Seven incoming FEDMSG rows
become done: CHANGE_TEAM_CIV, ASTEROID_DESTROYED, PROBES_UPDATE, QUIT_MISSION,
QUIT_SIDE, ENTER_GAME and GAME_STATE. SET_WINGID was already done on master:
its row retains both P713 and P718 evidence, with **no duplicate credit**.
Master's more complete screen/lobby evidence and Selection pane era exclusion
remain. All completed sound rows remain. Both matrices and fedmsg-wiring.csv
use LF endings. `python3 scripts/parity-scorecard.py` validates 438 interface
and 473 simulation rows.

| Final scorecard | Weighted | Complete / eligible |
| --- | ---: | ---: |
| Interface total | 1342/1479 (90.74%) | 358/409 (87.53%) |
| FEDMSG | 475/515 (92.23%) | 115/125 (92.00%) |
| Simulation total | 896/1331 (67.32%) | 279/436 (63.99%) |
| Sounds | 406/592 (68.58%) | 148/227 (65.20%) |

Compared with the preceding P738/P732 certificate, interface credit increases
by 28 and seven rows; simulation and sounds scores are unchanged. See the
[scorecard](P738_P718_assets/scorecard.txt),
[row audit](P738_P718_assets/parity-row-audit.json),
[resolution audit](P738_P718_assets/resolution-audit.json) and
[cue audit](P738_P718_assets/cue-audit.json).

### Merge validation

Whole-solution Release build, `dotnet build AllegianceCore.slnx -c Release
-m:8 --nologo -v:minimal`: **0 warnings / 0 errors**. The initial build's two
duplicate-dispatch errors are retained in `build-initial.log`; the successful
build is `build.log`. Source fingerprints confirm the tested sources did not
change during validation.

The 163 selected methods received three separate exact-method
invocations each (489 invocations). This includes all 56 methods
from the incoming P718 certificate, the new prelaunch regression, retained
sound tests, current commander/AFK/wing UI tests, dispatch/parity checks,
research/construction coverage and live/loopback cases.

| Assembly | Methods | Invocations | Passed cases | Failed cases | Skipped |
| --- | ---: | ---: | ---: | ---: | ---: |
| Core | 8 | 24 | 39 | 0 | 0 |
| ClientCore | 112 | 336 | 452 | 1 | 0 |
| ClientCore.MonoGame | 43 | 129 | 210 | 0 | 0 |

The standalone Release never-parks showcase passed **5/5**. Durations: 97.189, 94.772, 99.335, 96.367, 96.213 seconds. No standalone failure occurred, so no base-rate comparison was needed.

Exactly one broad pass ran for each required assembly, including LiveTiming
and ordinary live/loopback tests, with the P660 showcase opt-in enabled:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4781 | 1 | 0 |
| ClientCore | 1225 | 0 | 0 |
| ClientCore.MonoGame | 2356 | 0 | 3 |

The two Core hygiene tests `MeshyGuardTests` and `PrivatePathGuardTests` were
excluded because they invoke git, which the user explicitly prohibits. No live
or loopback test was excluded. The three MonoGame skips are the existing
optional private Meshy-pack checks, retained by name in the ledger; skip counts
use actual TRX outcomes. No rig / 221 /
200v200 run was attempted.

The raw failures above remain recorded. After all broad suites ended, each
observed failing method/case passed **3/3 fresh isolated invocations**:

- `Allegiance.ClientCore.Tests.P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(civA: 25, civB: 3, generated: False)`: 3/3 passed; classified as a load flake.
- `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`: 3/3 passed; classified as a load flake.

These observations are also recorded in `docs/TEST_FLAKES.md`. No assertion,
deadline, quarantine trait or product behavior was relaxed for them.

Evidence: [summary](P738_P718_assets/summary.json),
[exact invocation ledger](P738_P718_assets/verification.json),
[per-case outcomes](P738_P718_assets/case-results.json),
[raw logs and TRX archive](P738_P718_assets/validation-logs.tar.gz), and
[evidence hashes](P738_P718_assets/evidence-manifest.json). The repeatable
runner is `P738_P718_assets/verify.py`; source and conflict snapshots are
retained beside it. All changes remain as file edits for the dispatcher.
