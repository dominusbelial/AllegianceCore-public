# P842 — certify the event-driven mission rules: leadership, squads, ballots

Batch: the 30 `FedSrv mission rules (event-driven)` rows SIM-0493/0494/0495, 0503–0507, 0509, 0510,
0754–0759, 0782, 0794–0799, 0800–0803, 0814, 0815, 0821 (weight 60). Native reference re-grepped at
`~/Development/Allegiance/src/FedSrv/fsmission.cpp` and `FedSrv.CPP` (all line numbers below re-verified
this run). Worktree-only change; nothing committed.

## Result

- **Batch: 28/30 rows `implemented`, 56/60 weighted credit earned (93.3%); before: 0/60.**
  - SIM-0504 stays `partial` — one remaining branch, named below.
  - SIM-0821 is `implemented` with `reachability=test-only` (native body is dead code; earns no
    headline credit by design).
- **Family (`FedSrv mission rules (event-driven)`): 25.93% → 35.42% weighted** (153/590 → 209/590);
  rows 25.0% → 36.11% (63/252 → 91/252). `python3 scripts/parity-scorecard.py` validates; CSV kept LF
  (`grep -c $'\r'` = 0); only this batch's 30 rows were touched.

## Behaviour fixes (each with a must-fail-if-removed test)

| # | Native branch (cite) | Port fix |
|---|---|---|
| 1 | `CFSMission::SetLeader` wing bookkeeping (fsmission.cpp:1533-1541): the new leader goes to the **command wing (0)** and the side group is told via `SET_WINGID(106) {wingID=0, bCommanded=true}`; called from `MutinyBallot::OnPassed` (fsmission.cpp:6139) and `MaintainSquadLeadership` (fsmission.cpp:1717) | New `Server.SetLeader(Mission, Side, Ship)` primitive (Server.Lobby.cs) — wing 0 mutation + side-group SET_WINGID + `LeaderShipId` + whole-mission SET_TEAM_LEADER. `ApplyMutiny` now calls it (the mutiny path previously skipped the wing broadcast entirely). Native's old-leader wing block is commented out upstream (Imago #91) — a no-op, not ported. Test: `Mutiny_passing_transfers_leadership_failing_keeps_it` asserts SET_WINGID to both side peers, `Ship.WingId == 0`, and SET_TEAM_LEADER. |
| 2 | `CFSMission::MaintainSquadLeadership` (fsmission.cpp:1683-1739) — **missing entirely**; native call site `RemovePlayerFromSide`'s pre-start leader-quit tail (fsmission.cpp:1154-1155) | Full transcription in `Server.Squads.cs MaintainSquadLeadership(mission, side, departedLeader)`: bLockTeamSettings guard (fsmission.cpp:1693-1695), leader-vs-squad fit, the leader's preferred squad (fsmission.cpp:1698-1703), teammate scan + `SetLeader` (fsmission.cpp:1704-1720), `SetSideSquad` (fsmission.cpp:1722), boot-everyone when no squad remains (fsmission.cpp:1724-1737). Hooked from `HandleLeaderSuccession`'s remaining-players tail, gated exactly like native (`NotStarted && squad != NA`). The departing leader is excluded from the scans because native runs this *after* the leaver left the side (fsmission.cpp:1143 vs :1154). Native's other call sites (bSquadGame change fsmission.cpp:1889-1892; restart fsmission.cpp:3497-3499) are unreachable in the port (no mid-mission settings change; P439 rotation rebuilds sides). Tests: `PreStartLeaderDeparture_refits_the_squad_to_the_successors_preference`, `PreStartLeaderDeparture_clears_the_squad_and_boots_everyone_when_no_leader_exists`. |
| 3 | `CFSMission::SetSideSquad` lock-name branch (fsmission.cpp:1645-1668): under `bLockTeamSettings` the squad id still changes but `SetSideName` keeps the side name (`szSquadName = m_misdef.rgszName[sid]`, fsmission.cpp:1648) | `RelaySideSquad` now preserves `Side.Name` on the SET_TEAM_INFO(15) relay when `LockTeamSettings` is set. Test: `LockTeamSettings_keeps_the_side_name_when_the_squad_changes`. |
| 4 | `ResignBallot::OnPassed` → `DeactivateSide` (fsmission.cpp:6154-6163 → 5318-5427) | P187's "withdraw every player" substitution removed; `ApplyResign` now calls the P455 faithful `DeactivateSide` port (side inactive fsmission.cpp:5325, ships stripped + force-killed, SIDE_INACTIVE(68) + "<name> is no more." fsmission.cpp:5371-5377, win-by-elimination fsmission.cpp:5378-5419). Test rewritten: `Resign_passing_deactivates_the_side_and_ends_the_mission` (side inactive, mission Over via the 0-alive-sides draw, SIDE_INACTIVE + "is no more." observed, both players force-killed — not withdrawn). |
| 5 | `Ballot::Init(CFSSide*)` active-side guard (fsmission.cpp:5977): only players on **active** sides are enrolled on a mission-wide ballot | `CreateAcceptDrawBallot` now skips ships whose side is not `Active` — a resigned (deactivated) side's dead-but-connected players can no longer be enrolled. Test: `AcceptDraw_excludes_players_of_a_deactivated_side` (3-team scenario: resign → draw offer → the lone eligible voter's yes resolves it immediately; the dead player's vote is refused and does not block). |
| 6 | `Ballot::GetTallyString` exact format (fsmission.cpp:5999-6034) | `TallyString` rewritten byte-faithful: ONE "(" … ")", sides joined by ", ", " N for" / " M against" / " K abstained" space-separated within a side, side NAME + ":" prefix only when the chat target is not CHAT_TEAM (mission-wide AcceptDraw). The old format wrapped each side in its own parens and never named sides. Tests: failing-mutiny announcement asserts `"has been defeated.  ( 1 for 1 against)"`; the draw e2e asserts `"(Alpha: 1 for)"`. |
| 7 | `FedSrvSiteBase::SendChat` sets `cd.oidRecipient = m_groupID` (fsmission.cpp:6041/6050/6059 call shape) | `SendChatAnnouncement` now sets `OidRecipient` = voting side (side-scoped) / NA (mission-wide). Covered by the same announcement tests. |

## Per-row table

Native branches → port sites → verdict → fix/test (full branch lists live in the CSV `notes` column):

| Row | Native body (re-grepped cite) | Port site(s) | Verdict | Fix / pinning test |
|---|---|---|---|---|
| SIM-0493 | `AddBallot` fsmission.cpp:1199-1202 (single PushEnd) | `Server.Lobby.cs BallotsOf` + every ballot-creation call site | equal | BallotVotingLiveTests (mutiny creates, duplicate refused, draw chain) |
| SIM-0494 | `TallyVote` fsmission.cpp:1209-1219 (find by ballotID, first match, cast, break) | `Server.Dispatch.cs` VOTE handler → `PendingBallot.TryCastVote` | equal (port adds a sideId upper-bound guard — design/safety; native indexes the tally arrays unbounded) | `Vote_from_non_eligible_ship_is_ignored`; `Multi_mission_ballot_scopes_per_mission_and_per_side` |
| SIM-0495 | `HasBallots` fsmission.cpp:1225-1233 | `HasBallotOfType` | equal | `Second_mutiny_in_flight_refused_with_private_reply` |
| SIM-0503 | `GetLeader` fsmission.cpp:1452-1459 (lobby-side guard, NA→NULL) | `Side.LeaderShipId` + `Server.LeaderShip` | equal (native's unguarded `GetShipFromID` deref replaced by a null return — design) | LiveTeamLeadershipTests; mutiny tests |
| SIM-0504 | `SetLeaderID` fsmission.cpp:1464-1483 | `Side.LeaderShipId` write + `BroadcastSetTeamLeader` | **partial** — REMAINING: `PlayerScoreObject::StopCommand/StartCommand` (fsmission.cpp:1470,1480); the port's `PlayerScore` tracks no command-time counter (score-stat scope, not leadership behaviour) | LiveTeamLeadershipTests; mutiny tests |
| SIM-0505 | `GetOwner` fsmission.cpp:1486-1492 | `Mission.MissionOwnerSideId` + `IsMissionOwnerPlayer` | equal | P762LobbyModerationTests; LiveTeamLeadershipTests |
| SIM-0506 | `SetOwner` fsmission.cpp:1495-1508 | `MissionOwnerSideId` writes + `BroadcastSetMissionOwner` (SET_MISSION_OWNER(123)) | equal — note: native `SetOwner` is **dead code** (no caller in FedSrv, verified by grep; BT-WOPR leftover); native's null-leader deref is a native bug (unreachable); the port broadcasts at the real owner-change sites | LiveTeamLeadershipTests; resign test |
| SIM-0507 | `SetLeader` fsmission.cpp:1510-1553 | `Server.SetLeader` (new) | **fixed** (fix 1) | `Mutiny_passing_transfers_leadership_failing_keeps_it` |
| SIM-0509 | `SetSideSquad` fsmission.cpp:1612-1670 | `ApplySideSquad` + `RelaySideSquad` | **fixed** (fix 3); boot branch equal (port's SquadGame gate unreachable-equivalent); `SetLobbyIsDirty` (fsmission.cpp:1669) n/a — no persistent lobby mission store | P820cSquadGameServerTests (3 tests incl. new lock-name test) |
| SIM-0510 | `MaintainSquadLeadership` fsmission.cpp:1683-1739 | `MaintainSquadLeadership` (new) + HandleLeaderSuccession hook | **fixed** (fix 2) | 2 new P820c tests |
| SIM-0754 | `CastVote` fsmission.cpp:5847-5865 | `PendingBallot.TryCastVote` | equal (upper-bound guard = design) | `PendingBallot_tally_matches_native_majority_rule`; `Vote_from_non_eligible_ship_is_ignored` |
| SIM-0755 | `GetBallotID` fsmission.cpp:5869-5872 | `PendingBallot.Id` | equal | codec + mutiny tests |
| SIM-0756 | `GetType` fsmission.cpp:5876-5879 | `PendingBallot.Type` | equal | `Second_mutiny_in_flight_refused_with_private_reply` |
| SIM-0757 | `MutinyBallot::MutinyBallot` fsmission.cpp:6089-6096 | `CreateBallotFromChat(Mutiny)` + `BroadcastBallot` (HideToLeader = Type==Mutiny) | equal | `Mutiny_from_non_leader_creates_ballot_and_broadcasts_to_side`; `Mutiny_from_own_leader_refused_no_ballot` |
| SIM-0758 | `ResignBallot::ResignBallot` fsmission.cpp:6146-6152 | `CreateBallotFromChat(Resign)` | equal | resign test |
| SIM-0759 | `OfferDrawBallot::OfferDrawBallot` fsmission.cpp:6166-6172 | `CreateBallotFromChat(OfferDraw)` | equal | draw e2e |
| SIM-0782 | `PickNewLeader` FedSrv.CPP:13981-14024 | `Server.PickNewLeader` | equal (incl. the strict-`>` tie-break) | LiveTeamLeadershipTests (donation-winner succession) |
| SIM-0794 | `Ballot::Init(CFSPlayer*)` fsmission.cpp:5882-5940 | `CreateBallotFromChat` | equal (eligibility = the port's live-peer roster, the `IsPlayer` set; initiator auto-yes after the broadcast) | mutiny tests; multi-mission scoping test |
| SIM-0795 | `Ballot::Init(CFSSide*)` fsmission.cpp:5941-5996 | `CreateAcceptDrawBallot` | **fixed** (fix 5, fsmission.cpp:5977) | draw e2e; `AcceptDraw_excludes_players_of_a_deactivated_side` |
| SIM-0796 | `Ballot::OnPassed` fsmission.cpp:6037-6043 | `AnnounceBallotResult` + `SendChatAnnouncement` | equal (**fix 7**: oidRecipient now set) | mutiny + draw tests |
| SIM-0797 | `Ballot::OnFailed` fsmission.cpp:6046-6052 | same | equal | failing-mutiny announcement assertion |
| SIM-0798 | `Ballot::HasPassed` fsmission.cpp:6055-6071 | `PendingBallot.HasPassed` | equal | `PendingBallot_tally_matches_native_majority_rule` |
| SIM-0799 | `Ballot::AllVotesAreIn` fsmission.cpp:6074-6083 | `PendingBallot.AllVotesAreIn` | equal | same |
| SIM-0800 | `MutinyBallot::OnPassed` fsmission.cpp:6098-6143 | `ApplyMutiny` + `SetLeader` | **fixed** (fixes 1, 7); all other branches already equal (P197 donation transfer verified at fsmission.cpp:6121-6136) | mutiny tests; PlayerMoneyAutoDonateTests |
| SIM-0801 | `ResignBallot::OnPassed` fsmission.cpp:6154-6163 | `ApplyResign` → `DeactivateSide` | **fixed** (fix 4) | `Resign_passing_deactivates_the_side_and_ends_the_mission` |
| SIM-0802 | `OfferDrawBallot::OnPassed` fsmission.cpp:6174-6180 | `ApplyOfferDraw` | equal | draw e2e |
| SIM-0803 | `AcceptDrawBallot::OnPassed` fsmission.cpp:6190-6194 | `ApplyAcceptDraw` → `DeclareDraw` → `DeclareGameOver` (P439) | equal | draw e2e |
| SIM-0814 | `GetTallyString` fsmission.cpp:5999-6034 | `TallyString(Mission, PendingBallot)` | **fixed** (fix 6) | failing-mutiny + draw announcement assertions |
| SIM-0815 | `AcceptDrawBallot::AcceptDrawBallot` fsmission.cpp:6183-6188 | `CreateAcceptDrawBallot` | equal | draw e2e (OT_side initiator, HideToLeader false) |
| SIM-0821 | `Ballot::Cancel` fsmission.cpp:5836-5844 | `PendingBallot.Canceled` + `ResolveBallots` drop | equal-by-structure — native `Cancel` is **dead code** (no caller in FedSrv, verified by grep) and the live native server never emits CANCEL_BALLOT, so the port has no producer either; reachability=`test-only` | `Cancelled_ballot_is_dropped_without_announcing_or_resolving` (new) |

## Native-intent classification

- **Design (kept as designed):** the sideId upper-bound guards the port adds in `TallyVote`/`CastVote`
  (native indexes `m_cAbstaining[sideID]` with no `c_cSidesMax` bound — a latent overrun the port
  deliberately does not reproduce); `GetLeader`'s null return instead of native's unguarded
  `GetShipFromID` deref; `SetOwner`'s null-leader deref (native bug, unreachable — dead code).
- **Native bugs (not ported):** the unbounded sideID indexing above; `SetOwner`'s unguarded leader
  deref. Neither breaks the port; both are noted in the CSV.
- **Era/scope (n/a branches, documented per row):** `SetSideSquad`'s `SetLobbyIsDirty`
  (fsmission.cpp:1669 — no persistent lobby mission store); native `SetOwner` having no caller;
  native `Ballot::Cancel` having no caller (both dead code upstream).

## Modern/better-approach note

The port keeps native's *rules* but not its *shapes*: ballots are a plain per-mission
`List<PendingBallot>` of a pure tally object (`PendingBallot` is unit-testable without a server),
resolution is a single per-tick `ResolveBallots` pass instead of native's iterator-mutation loop
(fsmission.cpp:2585-2592), and leadership is one shared `SetLeader` primitive consumed by mutiny and
squad maintenance — the same factoring native implies (both call `CFSMission::SetLeader`) but the
port now enforces structurally, so a future caller cannot re-introduce the wing-broadcast drift this
certification found. Squads ride signed join-token claims instead of a squad database, which keeps
the boot/refit rules testable headlessly. If further divergence-prone virtual surfaces accumulate,
the next modernisation would be a `LeadershipChange` event on `Side` that the wire layer subscribes
to, removing the last direct `BroadcastSetTeamLeader` calls from policy code.

## Verification (light gate)

- `~/.dotnet/dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors**.
- `tools/build-tools.sh` — all 91 tool projects built, 0 errors.
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained
  -p:PublishAot=true`) — clean, no IL2026/IL3050.
- New/affected tests 3× alone (`BallotVotingLiveTests`, `P820cSquadGameServerTests`,
  `P820bSquadGameWireTests`, `LiveTeamLeadershipTests`, `SideEliminationLiveTests`,
  `LiveSideInactiveTests`, `SetWingIdTests`, `P762LobbyModerationTests`): **3/3 PASS (68 tests each
  run)**, then 19/19 after the final comment-only cite fixes.
- ONE fast-tier run: `tools/run-tests.sh --tier fast` — **PASS, 11077 tests, 321 s**
  (TestResults/tiers-20261027T193250.072342Z/summary.json).
- Showcase never-parks 5×: **not run — bot/flight/sim behaviour on the showcase path is unchanged.**
  Every fix is gated behind paths the showcase never reaches: ballot effects require `#resign/#draw/
  #mutiny` chat (bots send none), `MaintainSquadLeadership`/squad boots require a side squad (set only
  for verified peers with squad claims; bots have none), and the remaining changes are announcement
  text/wire-field only. All changed code paths are exercised by the 3× suites above.
- `scripts/parity-scorecard.py` — validates; CSV LF-clean.

## Files changed

- `src/Allegiance.Server/Server.Lobby.cs` — `SetLeader` primitive; `ApplyMutiny` wing path;
  `ApplyResign` → `DeactivateSide`; native-exact `TallyString`; announcement `OidRecipient`;
  AcceptDraw active-side guard; `MaintainSquadLeadership` hook in `HandleLeaderSuccession`.
- `src/Allegiance.Server/Server.Squads.cs` — `MaintainSquadLeadership` port; lock-name rule in
  `RelaySideSquad`; boot-branch comment parity note.
- `tests/Allegiance.Core.Tests/BallotVotingLiveTests.cs` — rewritten resign test (deactivation
  semantics), mutiny wing assertions, native-tally-format assertions, new cancelled-ballot and
  deactivated-side-exclusion tests.
- `tests/Allegiance.Core.Tests/P820cSquadGameServerTests.cs` — 3 new tests (squad refit, boot-
  everyone, lock-name).
- `docs/parity/sim-rules-matrix.csv` — the 30 batch rows only.
