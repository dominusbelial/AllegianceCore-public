# P1033 — R1b F1: squad side identity at initial selection

All three F1 findings are real. This change carries the authenticated joining
peer into initial leader election, checks its squad membership before either
explicit or automatic seating, and includes verified squadless accounts in
squad-change boots. No commits or pushes are made by this lane.

## Root causes and fixes

1. **Initial leader lost its squad.** Before this change,
   `Server.Lobby.cs:4527,4544` called `TryJoinSide` before
   `CompleteJoinAndLaunch` registered `_peerShips[from]` at `:4639`.
   `Server.Squads.cs:91-99` could therefore not resolve the leader's connection.
   Initial seating now passes the joining connection into `TryJoinSideCore` and
   `EstablishSideSquadFromLeader`. Existing side switches and rotation reseats
   retain their registered-leader lookup. Squad establishment happens during
   election, so observers see the correct team info before the join completes;
   it is not re-run on reconnect or ordinary leadership succession.
   The joining peer also receives current side squads with its role snapshot
   after `JOINED_MISSION` resets client state. Its pre-registration election
   broadcast cannot reach it; the old pending-selection snapshot carried NA.
   The unchanged leader reproduction asserts this native `SET_TEAM_INFO` wire
   effect as well as the server's squad ID.
2. **Initial join bypassed membership.**
   `Server.SideIntegrity.cs:108-111` only consulted `ConnOf(ship)` although
   `CheckInitialPositionRequest` deliberately supplies no ship. The shared
   check now uses the supplied peer for initial requests, falling back to the
   registered ship's connection. Both explicit selection and `AutoPickSide`
   use this check. A refused peer stays pending without a ship or launch burst
   and can retry a legitimate selection.
3. **Verified squadless players survived a squad change.**
   `Server.Squads.cs:137-140` treated a null or empty claim list as anonymous.
   `ApplySideSquad` now tests whether a verified identity exists and uses the
   same membership predicate as side selection. Missing claims, an empty list,
   and membership in another squad all mean a verified non-member. Removal
   still uses `MoveToTeamLobby` and the P1026 side-departure/reset path, with
   whole-mission `QUIT_SIDE(SquadChange)`.

Paths above are relative to `src/Allegiance.Server/`; root-cause line numbers
refer to the pre-change tree.

## Native evidence and parity verdict

Re-read from the native corpus for this task:

| Rule | Native source | Verdict |
| --- | --- | --- |
| An unlocked squad game's first team leader establishes its preferred squad | `FedSrv/fsmission.cpp:661-690` (preferred-squad branch at `:684-686`) | Native parity; joining peer is now available at election |
| An occupied squad side rejects non-members with `DPR_WrongSquad`; an empty side requires squad leadership | `FedSrv/fsmission.cpp:4708-4721` | Native parity on initial as well as seated-player requests |
| No squad (`NA`) accepts everyone; a real squad requires a matching membership | `FedSrv/fsship.cpp:1358-1373` | Native parity; no unverified peer is inferred to be a member |
| A changed, non-NA squad boots non-members, including those with no memberships; unchanged squad does nothing | `FedSrv/fsmission.cpp:1617-1641` | Native parity for verified players; existing port exemption below retained |
| The joining leader learns current squad/name on team info | `FedSrv/fsmission.cpp:1580-1590` | Native parity via post-reset join snapshot; the port defers peer registration |
| Leadership claim means leader or assistant leader | `FedSrv/fsship.cpp:1374-1417` | Existing `CanLead` claim representation retained |

**Identity decision.** Verified accounts are authoritative even when `Squads`
is null or empty: these players are non-members of every real squad. Initial
join checks also reject anonymous/bot peers onto a real squad side, matching
the existing seated-player join check and P852; no account is invented from
its callsign. A side without a squad still accepts everyone. Squad-change
boots preserve P820b/P820c's deliberate exemption for peers without a verified
lobby identity, including anonymous bots already seated when a host enables
squad mode. Those peers have no authoritative lobby membership list. This is
an existing port compatibility policy, not a claim of full native parity:
native `SetSideSquad` iterates every player, while native drones are excluded
by `IsPlayer()` at `fsmission.cpp:1632`. Keeping this exemption avoids changing
ordinary anonymous/bot operation; it does not grant initial access to an
already established squad side. An authenticated peer with no squads does
not qualify for the exemption.

`docs/parity/p1033-squad-sides.csv` records these contracts, and the existing
AddPlayerToSide / CheckPositionRequest / SetSideSquad rows are updated with the
new evidence and the retained identity-policy exception. No other R1b finding
is changed.

## Reproduction and focused coverage

`P1033SquadSideTests` boots the real server with artwork, feeds encoded
`LOGONREQ`, `POSITIONREQ`, and `SET_TEAM_INFO` packets into the production
dispatcher, ticks the real initial-selection drain, and flushes the broadcast
publisher before inspecting wire messages. The existing `TestSetIdentity`
seam supplies the trusted lobby claims; it does not bypass either side-selection
or squad-change handling. Token signing/verification remains covered by the
existing squad wire tests.

The same reproduction assertions are run unchanged before and after the fix.
Coverage includes explicit and auto-selected first leaders, rivals with no
leadership rights, null and empty verified memberships, anonymous initial
joiners, valid non-leading members, retry after refusal, squad mode off/on,
auto-pick of another eligible side, empty-side leadership, real squad-change
quit broadcasts, member/anonymous/other-side preservation, and unchanged-squad
idempotence. There are no disabled new tests and no existing tests are unskipped.

Before the product fix: **9 passed / 13 failed / 0 skipped** (22 cases). The
failures were both first-leader cases, all eight initial non-member cases,
auto-pick establishing the other squad's empty side, and both null/empty
verified squad-change cases. Existing valid-member, non-squad, empty-side,
and rival-boot controls passed. Log/TRX: `.scratch/p1033/red.log` and
`.scratch/p1033/red/red.trx`. A SHA-256 of the test file was saved with this
run to verify the final tests are unchanged.

## Configuration and limits

No new settings, services, stores, ports, environment variables, limits, or
media. Existing `GameConfig.SquadGame` defaults to false. Existing identity,
membership claims, side capacity and balance ordering, and P1026 departure
semantics remain in force. Docker is unavailable in the lane; no deployment
configuration changes require Compose validation.

## Validation

Report drafted before the gate. Execution evidence is retained under the
ignored `.scratch/p1033/` directory. Builds use `--disable-build-servers
-nodeReuse:false -p:UseSharedCompilation=false`. The container's default NuGet
HTTP cache is read-only, so a scratch HTTP cache is used with auditing enabled.

| Check | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Before fix: P1033 reproduction | 9 | 13 | 0 |
| After fix: P1033 (first complete green run) | 22 | 0 | 0 |
| P1033 isolated repeat 1 | 22 | 0 | 0 |
| P1033 isolated repeat 2 | 22 | 0 | 0 |
| P1033 isolated repeat 3 | 22 | 0 | 0 |
| Existing core/server regression classes | 124 | 0 | 0 |
| Supplemental client lobby/bot/preset classes | 51 | 0 | 0 |
| Supplemental MonoGame lobby/squad/layout classes | 19 | 0 | 0 |
| One full Fast suite (14,633 cases) | 14,614 | 2 | 17 |

The 124 existing cases were `P820bSquadGameWireTests` (6),
`P820cSquadGameServerTests` (6), `P855SquadGameLoopbackTests` (1),
`P852MissionRulesCertifyTests` (14), `P1026SideDepartureTests` (13),
`LiveSideSelectTests` (8), `LivePositionRequestTests` (16),
`LiveTeamLobbyTests` (8), `LiveTeamLobbyToggleTests` (14),
`LiveTeamLeadershipTests` (12), `LivePlayerCountTests` (2),
`P864dTeamLobbyClientListLiveTests` (2), `LobbyHeartbeatTests` (9), and
`P853HostEditServerTests` (13). Logs/TRX: `.scratch/p1033/focused-*`,
`.scratch/p1033/focused/`, `.scratch/p1033/regression.log`, and
`.scratch/p1033/regression/regression.trx`.

Glossary links: **PASS**, 877 distinct paths. Parity CSV validation: **PASS**.
Test-file SHA-256 against the pre-fix reproduction: **unchanged**.

The supplemental client run covered `P678LobbyRelayLiveTests`,
`ClientJoinSideLiveTests`, `LeaderSameSideBotClientLiveTests`,
`P727SkirmishOptionsTests`, `P557ShowcaseFleetTests`, and `P745BSkirmishTests`.
These exercise real client sessions, normal Allegiance.Bot peers, existing
side switches, and showcase/skirmish fleet and host behavior. MonoGame coverage
was `P713LobbyScreenLiveTests`, `P820SquadScreenModelTests`,
`P820bTeamPaneSquadTests`, and `SideSelectLayoutTests`. Log/TRX:
`.scratch/p1033/client-regression.log`, `.scratch/p1033/monogame-regression.log`,
and `.scratch/p1033/client-regression/`.

Solution build: **PASS, 0 warnings / 0 errors**, 30.82 s
(`.scratch/p1033/build.log`).

Tool builds: **PASS, all 140 projects**, 0 warnings / 0 errors
(`.scratch/p1033/build-tools.log`), through `tools/build-tools.sh` with the same
build-server workaround. Working-tree hygiene and `git diff --check`: **PASS**;
no new media or brief remains in the change set. The commit-only publish guard
is not used as a substitute for inspecting these uncommitted changes.

One full Fast suite ran with a 120-minute timeout and completed in **579.4 s**:
**14,614 passed / 2 failed / 17 skipped**. The infrastructure checks passed
(9 checks); quarantine cases all passed. The calibrated Fast limit was
12.505 s; six executions exceeded it (five passed assertions plus the failed
native-miner case). The full-suite gate is therefore **FAIL**, not all green.
Command: `tools/run-tests.sh --tier fast --no-build --results-dir
.scratch/p1033/fast-suite` with the scratch dotnet wrapper and a 120-minute
`timeout`. Evidence: `.scratch/p1033/fast-suite/summary.json` and
`.scratch/p1033/fast-suite/fast/summary.txt`. All 22 P1033 cases passed in this
sweep, as did the requested squad, side-selection, P852, P1026 and lobby cases.

Both assertion failures match existing ledgered families:
`P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5)`
observed `5.0000000000000009` against the upper bound `5` at
`P739TimerLiveTests.cs:81`;
`P817NativeMinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station`
had ore 0 after its 30 s bound (`MinerDecisionCycleLiveTests.cs:182`). Neither
fixture enables squad mode; the timer/miner code and assertions are unchanged.
The full-sweep native-miner and timer signatures are already recorded in
`docs/TEST_FLAKES.md`; this task does not repair other stories' fixtures.

Every failed or over-limit execution was then repeated **three times alone**
using its exact method plus display-argument filters, one test per process.
All **21/21 passed**, without skips and below the calibrated limit:

| Execution | Full-sweep observation | Isolated runs (Passed/Failed/Skipped) | Isolated seconds |
| --- | --- | --- | --- |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | PASS; over limit (13.436 s) | 1/0/0; 1/0/0; 1/0/0 | 3.067 / 3.117 / 3.112 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | PASS; over limit (13.254 s) | 1/0/0; 1/0/0; 1/0/0 | 3.333 / 3.166 / 3.310 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | PASS; over limit (15.695 s) | 1/0/0; 1/0/0; 1/0/0 | 2.969 / 2.914 / 2.919 |
| `P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work(peers: 400, indexed: False, coalesced: False)` | PASS; over limit (13.408 s) | 1/0/0; 1/0/0; 1/0/0 | 1.293 / 1.279 / 1.295 |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | PASS; over limit (15.115 s) | 1/0/0; 1/0/0; 1/0/0 | 1.389 / 1.392 / 1.388 |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station` | FAIL: ore 0 (30.272 s; also over limit) | 1/0/0; 1/0/0; 1/0/0 | 1.181 / 1.742 / 1.634 |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5)` | FAIL: one-ULP boundary (5.0000000000000009 > 5) | 1/0/0; 1/0/0; 1/0/0 | 0.568 / 0.557 / 0.597 |

Evidence: `.scratch/p1033/isolation/results.json`, individual `result.trx`
files under `.scratch/p1033/isolation/`, and `.scratch/p1033/isolation.log`.
The same observations and repetitions are recorded in `docs/TEST_FLAKES.md`.
There was no second full Fast sweep, no test/limit relaxation, and no unrelated
product fix. Final gate verdict: build/tools and task regressions **PASS**;
full Fast **FAIL with the ledgered assertion/timing observations above**.
All lane-started command, build, and test processes have exited. Changes remain
in the working tree; no commit or push was made.

## Native intent

Squad selection is authoritative during joining and changing a side's squad:
the initial leader gives the side its squad, and verified players cannot enter
or retain a seat on a squad they do not belong to.

## Modern approach

Use the verified lobby connection already available before ship registration;
share the membership predicate across initial joining and squad changes, and
prove both state and native wire effects through the production dispatcher.
