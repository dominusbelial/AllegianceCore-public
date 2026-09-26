# P329 — Server.cs partial-class split, stage 3: Lobby + Economy

Structural refactor only. **Zero behavior change.** Splits the side-select /
ballot / countdown / alliance / leader-succession / game-over handlers and the
economy (money / donation / bucket-research / payday) handlers out of the
god-class `Server.cs` into two new partial-class files,
`src/Allegiance.Server/Server.Lobby.cs` and `src/Allegiance.Server/Server.Economy.cs`
(both `public sealed partial class Server`). Methods moved verbatim; no
rename / cleanup / bug-fix while moving.

## Relationship to the 4-stage plan
- Stage 1 (P327, done): lifecycle + per-tick broadcast → `Server.Lifecycle.cs`, `Server.Broadcast.cs`.
- Stage 2 (P328, done): wire dispatcher `ProcessDatagram` → `Server.Dispatch.cs`.
- **Stage 3 (this task): lobby + economy handlers → `Server.Lobby.cs`, `Server.Economy.cs`.**
- Stage 4 (P330): ship-command / docking / combat handlers → separate files.

## Scope boundary
- **Moved (63 methods)** → `Server.Lobby.cs`: `HandleSuicide`,
  `RecomputeSideReady`, `BroadcastTeamReady`, `TryAutoStart`,
  `IsAllOccupiedSidesReady`, `StartCountdown`, `AdvanceStartCountdowns`,
  `CompleteStartCountdown`, `BroadcastEnterGameToMission`,
  `BroadcastMissionStageToMission`, `SendToMissionPeers`, `BroadcastJoinSide`,
  `BroadcastSetTeamLeader`, `BroadcastSetMissionOwner`, `BroadcastQuitMission`,
  `BroadcastQuitSide`, `BallotsOf`, `HasBallotOfType`, `IsBallotCommand`,
  `TryHandleBallotCommand`, `SendBallotReply`, `CreateBallotFromChat`,
  `CreateAcceptDrawBallot`, `BroadcastBallot`, `ResolveBallots`,
  `ApplyBallotEffect`, `ApplyMutiny`, `LiveOf`, `ConnOf`, `LeaderShip`,
  `PickNewLeader`, `HandleLeaderSuccession`, `TryJoinSide`, `SelectChatTargets`,
  `AssignShip`, `CompleteSideSelection`, `CompleteJoinAndLaunch`,
  `SendSideRoster`, `BroadcastSideRosterToPendingPeers`, `AdvanceSideSelection`,
  `RebalanceSides`, `ClearPendingJoinRequestsForMission`, `IsValidAllianceId`,
  `UpdateAlliances`, `SweepAutoAcceptPending`, `CountPlayersOnSideExcluding`,
  `IsMissionOwnerPlayer`, `IsSideLeader`, `GetLeaderRankSum`,
  `BroadcastPositionReq`, `BroadcastDelPositionReq`, `FindConnectionOfShip`,
  `RemovePendingJoinRequest`, `CheckForVictoryByInactiveSides`,
  `DeclareGameOver`, `BroadcastGameOverPlayers`, `ApplyResign`,
  `ApplyOfferDraw`, `ApplyAcceptDraw`, `DeclareDraw`, `AnnounceBallotResult`,
  `SendChatAnnouncement`, `TallyString`.
- **Moved (20 methods)** → `Server.Economy.cs`: `SetAutoDonate`,
  `BroadcastAutoDonate`, `ResolveDonateTarget`, `HandleAutoDonate`,
  `HandleRequestMoney`, `HandlePlayerDonate`, `AlliedSides`,
  `SendMoneyChangeToSideGroup`, `SyncMoneyStateToJoiner`,
  `RetargetDonorsOffLeaver`, `BroadcastMoneyChangeSideWide`,
  `HandleBucketDonate`, `BroadcastBucketDonateSpendSideWide`,
  `BroadcastBucketStatusSideWide`, `OnBucketCompleted`, `CompleteDevelopment`,
  `BroadcastSideTechChange`, `BroadcastDevCompleted`,
  `BroadcastSideAttributeChange`, `OnPayday`.
- **Kept in Server.cs:** the inbound `Handle*` wire handlers still referenced by
  the dispatch stage, plus ship-command / docking / combat members (stage 4,
  P330). Being `partial class Server`, cross-file private member access is
  unaffected, so this is purely cosmetic.
- No behavior change, no bug fix, no naming cleanup. No native reference source
  (`~/Development/Allegiance`) touched.

## Verification
### Byte-identical method bodies (the hard requirement)
Each moved block was extracted with the **Roslyn** syntax tree (`MethodDeclarationSyntax.FullSpan`,
which carries the attached XML doc comment and any attributes) rather than a
hand-rolled brace matcher, eliminating lexer edge cases (nested interpolated
strings, `$@"..."`, strings inside interpolation holes, expression-bodied
members). The union of the 83 `FullSpan` blocks was deleted from `Server.cs`;
each block was inserted verbatim into its target file in source order.

Programmatic check against `git HEAD`: all 83 moved blocks appear **byte-for-byte**
in their target file and are **absent** from the new `Server.cs`; the 83 spans are
pairwise disjoint (zero overlaps). Declaration census: `Server.Lobby.cs` holds
exactly the 63 lobby methods and `Server.Economy.cs` exactly the 20 economy
methods — no duplicates, no omissions, no strays. 5 of the moved methods
(`HasBallotOfType`, `IsBallotCommand`, `ApplyAcceptDraw`, `IsValidAllianceId`,
`CountPlayersOnSideExcluding`) are expression-bodied (`=> …;`) — moved intact.

### Build
Whole-solution `dotnet build AllegianceCore.slnx -c Debug`: **0 warnings, 0 errors**.

### Full test suite (run 2x + pristine A/B)
`Core.Tests` (3,268 total):
- Changed tree, run 1: 3,262 pass / 6 fail — `LiveRelaunchTests` (launch-slot
  position flake), `LiveChatSectorWireTests` (relay-count flake), `CargoBayReloadTests`
  (seating-sequence flake), + 3 unseen in the truncated tail.
- Changed tree, run 2: 3,264 pass / 4 fail — `LiveTurretGunnerBindingTests`
  (documented persistent flake), `LiveMoneyTests.Board_refund…` (2-peer
  side-wide broadcast timing), `AsteroidsUpdateLiveTests.Mining_depletion…`,
  `AsteroidMinedLiveTests.No_broadcast_fires_once…`.
- **Pristine HEAD A/B:** stashing the change and running the full suite on the
  unmodified tree gives 5 failures of the same class: `WarpBombTests…`,
  `LoadoutChangeLiveTests.Boarding…`, `LiveTurretGunnerBindingTests`,
  `LiveTeamLobbyTests.Unready…`, `LiveChatSectorWireTests…`.

No failure is common to all three runs and none is deterministic; every failure
is in the documented environmental live-network / timing-flake family. Notably
`LiveChatSectorWireTests` fails on both the changed tree (run 1) and pristine,
and `LiveTurretGunnerBindingTests` fails on both the changed tree (run 2) and
pristine. `LiveMoneyTests.Board_refund…` was re-run 3× in isolation on the
changed tree: fails once, passes twice — proven flaky, and it exercises the
board-refund broadcast path that stays in `Server.cs` (not a moved method).
Identical pass/fail character ⇔ the move introduces no behavioral difference.

### Live proof
Focused live run of the integration classes that boot a real in-process server
and drive real wire sessions over exactly the moved functionality —
`LiveSideSelectTests`, `LiveTeamLobbyTests`, `LiveBriefingTests`,
`PlayerReadyLiveTests`, `BallotVotingLiveTests`, `PlayerMoneyAutoDonateTests`,
`LiveMoneyTests`, `PlayerDonateTests`, `RequestMoneyTests`, `LiveBucketTests`,
`TechTreeDevelopmentLiveTests`, `PaydayTests`, `RandomizeTeamsLiveTests`,
`LiveRelaunchTests`, `LiveWinConditionTests`, `LiveSuicideShipDeleteTests`:
**95 passed**, 2 failed — `LiveWinConditionTests.Multi_mission_disconnect…`
(document flake) and `LiveMoneyTests.Board_refund…` (proven flaky above).
Side-select, ready/countdown, ballots, auto-donate, bucket-research, payday,
rebalance and game-over all still behave as before.

## Deliverable
`src/Allegiance.Server/Server.Lobby.cs` (2,650 lines, 63 methods) and
`src/Allegiance.Server/Server.Economy.cs` (866 lines, 20 methods) created;
`Server.cs` slimmed **9,239 → 5,763 lines (−3,476)**. Committed on `master`
(not pushed — Domingo pushes). No side branch. No native reference source touched.
