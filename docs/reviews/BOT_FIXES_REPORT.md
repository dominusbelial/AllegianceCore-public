# Bot fixes report

Implemented the 19 items from `FIX_BRIEF.md` on `codex/bot-fixes`, based on
`8f7a3770`. Changes remain in the working tree. No commits, pushes, server-source,
transport-source, or ServerHost edits were made. Snapshot decisions and cross-wing
synchronization remain **off by default**.

All new regression tests are in
`tests/Allegiance.ClientCore.Tests/BotFixRegressionTests.cs`. Test names below
refer to that file unless another file is named. Locations refer to this final
working tree, rather than the original review.

| Brief order / finding | Change and location | Focused proof |
| --- | --- | --- |
| 1 / #7 — pod relaunch | `tools/allegiance-mcp/BotManager.Lifecycle.cs:36` shares the life reset; `:72` buys the stored requested loadout with `fLaunch:true`, at most three attempts spaced five seconds apart, and evaluates acknowledgments. `tools/allegiance-mcp/BotManager.cs:3225` waits for actual undocking before clearing all pod/arrival/path/warp/target latches and releasing the reinforcement. Historical death/docking counts survive the reset. `src/Allegiance.ClientCore/State/ClientWorld.cs:1838` clears the station on `SET_CLUSTER`, matching native launch handling. | `DockedPod_BuysPlayerLoadoutAndLaunches_WithBoundedRetries`, `PodLife_AckUpdatesGrant_LaunchConfirmsNewLife_Twice`, `NewLife_ClearsAllPodTravelAndTargetLatches_ForTwoDeaths`. The real hull probe exercises two deaths, ACK-before-launch ordering, grants, docking counters, and released reinforcements. |
| 2 / #3 — lost/failed bots | `tools/allegiance-mcp/BotManager.Lifecycle.cs:18` checks connectivity and loss before participation; `:21` marks inert, removes under the roster gate, then disposes outside it. Preparation, driving and staging exclude lost peers. `tools/allegiance-mcp/BotManager.cs:638` rolls back failed arena registration. `tools/allegiance-mcp/McpTools.cs:227` reports removed rows; `:1052` includes lost/removed peers in performance status. Status recognizes loss before the loop completes removal. | `LostBot_CannotDriveOrReserveEngagers_AndIsReportedOnRemoval`, `LostBot_DoesNotHoldLivingWing`, `FailedArena_RemovesRegisteredEntry_AndReportsFailure`. |
| 3 / #4 — mission/new-session life | `tools/allegiance-mcp/BotManager.Lifecycle.cs:112` detects ship, session, mission-cookie and mission-generation changes, calls the same reset as pod relaunch, suspends decisions until world/objective resolution succeeds, resolves named sectors against the new map, and rebuilds assigned routes. `src/Allegiance.ClientCore/State/ClientWorld.cs:1443` and `:3423` track reset/rotation generations even if a ship ID repeats. The loop refreshes before preparation, and re-ready refreshes before sending ready. | `MissionRotation_ResolvesNamedObjectiveAndClearsOldArrival_BeforeDriving`, `SameShipId_NewMissionGenerationStillResetsLife`, `ChangedIdentity_WithSameShipId_ResetsArrival` (cookie and session cases). |
| 4 / #10 — bomber bases | `tools/allegiance-mcp/BotManager.cs:2609` acquires enemy stations at the objective/in-place arena, prioritizing defense against a visible recent attacker. `:2764` targets `OT_station`, uses the station position/radius and zero velocity, and runs the existing lead, steer, dodge, friendly-fire and weapon gates. `:2734` restricts travelling bombers to recent attackers; routing keeps the base objective. Miner exclusion remains in ship selection. `tools/allegiance-mcp/BotManager.Lifecycle.cs:163` selects only enemy co-located stations. | `Bomber_SelectsOnlyEnemyStationsInDestination_AndReactsOnlyToVisibleAttackers`; existing `P539CombatDecisionTests` and `P553DodgeDecisionTests` cover the shared lead/fire/dodge laws. Live station damage and missile delivery still require the rig. |
| 5 / #1 — projectile storage | `src/Allegiance.ClientCore/State/ClientWorld.cs:1049` offers own-shot count-only mode; `:2775` does not retain projectile entities in that mode. Counts drain without a list cursor and reset on a new world/mission. Probe, combat and commander sessions enable it. `src/Allegiance.Bot/Bot.cs:66` allows disabling unused projectile/heavy-update raw logs; MCP plain bots disable both. The game client's entity/prune/drain defaults remain unchanged. | `HeadlessProjectiles_StorageStaysEmpty_BeforeAndAfterDrains` applies 10,000 rounds before draining and proves exact own-shot attribution, no recount and empty storage. `PlainBot_DisablesUnusedRawAccumulators` feeds 1,000 packets per accumulator. Existing `ClientWorldProjectileTests` also pass. |
| 6 / #15 — shared spatial index | `tools/allegiance-mcp/BotManager.cs:3995` copies friendly points once per side, builds each sector index outside the receive gate, and shares the resulting map throughout the pass. Queries reuse the sector index, including after a sector transition. Missing-cache/scenario queries use a direct copied scan rather than constructing another grid. `tools/allegiance-mcp/BotSpatialIndex.cs:9` documents the actual use and removes the dead start array and redundant occupant copy. | `SpatialIndices_AreSharedWithinSideAndSector_AndExcludeOtherSides`; existing `P633DecisionLoopTests` cover grid queries and friendly-line behavior. |
| 7 / #14 — unused teammate scan | `tools/allegiance-mcp/BotManager.cs:2739` supplies zero for the selector's unused legacy teammate input; the manager's cross-world teammate scan was removed. | `Selector_DoesNotDependOnUnusedTeammateCount` proves identical choice with zero versus 390 teammates; existing coordinated-selector tests pass. |
| 8 / #18 — scheduling debt | `tools/allegiance-mcp/DecisionMetrics.cs:103` drops all overdue debt and anchors the next start one period from the current time, with a minimum yield. `:84` counts overruns; `tools/allegiance-mcp/McpTools.cs:1051` exposes that count. | `Scheduler_DropsDebtAfterHitch_AndRecordsOverrun` uses a 341 ms hitch followed by fast passes and proves full-period resynchronization, no catch-up starts, and one overrun. Existing cadence tests pass. |
| 9 / #16 — log rollover | `src/Allegiance.ClientCore/State/EventLog.cs:49` adds monotonic kill/chat sequences and bounded-window readers. `tools/allegiance-mcp/BotManager.cs:3113` consumes kills by sequence for kill/TTK accounting. `tools/allegiance-mcp/BotManager.Scenarios.cs:252` also consumes scenario chats by sequence. Clearing an event list does not reset its sequence. | `KillAndChatSequences_SurviveRolloverAndReset_WithoutRecount` counts 1,600 kills in batches across rollover, then a world reset, and checks chat rollover. Existing event-log tests pass. |
| 10 / #2 — failure ownership | `tools/allegiance-mcp/PendingOwnership.cs:4` owns/disposes a peer until successful transfer. `tools/allegiance-mcp/BotManager.cs:201`, `:448`, `:657`, and `:1034` apply it to probe, spawn, combat seating and commander paths. Post-seat and post-registration failures remain covered; spawning entries stay pending until preflight/arena placement succeeds. | `PendingOwnership_DisposesOnFailure_TransfersOnlyOnSuccess` covers combat/plain/commander/probe ownership, rollback and successful transfer. `FailedArena_RemovesRegisteredEntry_AndReportsFailure` checks registry rollback. Real network timeout/refusal injection is deferred. |
| 11 / #5 — arena roles | `tools/allegiance-mcp/BotManager.Lifecycle.cs:180` resolves every arena role to the arena sector. `tools/allegiance-mcp/McpTools.cs:704` uses that policy and disables bomber route planning in arena mode. | `Arena_AllRolesUseArenaSector` (bomber, fighter, scout cases). |
| 12 / #13 — stale orders | `tools/allegiance-mcp/BotManager.Lifecycle.cs:193` clears missing, dead, gone or lifepod ordered targets before autonomous selection. A live target temporarily outside the current sector keeps its order. | `DeadOrderedTarget_IsClearedBeforeAutonomousCombat` (dead, gone, missing cases), followed by successful autonomous selection of another fighter. |
| 13 / #11 — absent sector | `tools/allegiance-mcp/BotManager.Lifecycle.cs:180` preserves an absent/negative target as null; `tools/allegiance-mcp/McpTools.cs:704` passes that nullable target to spawn. A bomber without a travelling objective also engages in place rather than silently acquiring a route. | `MissingTarget_EngagesInPlace` (fighter and bomber cases). |
| 14 / #9 — wing synchronization | `tools/allegiance-mcp/BotManager.cs:4502` aggregates living wings of the same side and goal, compares other route indices, takes the earliest staging time, and computes the widest route across that population. This restores the existing per-route timeout stagger. Production still uses the default-off switch. | `SyncBarrier_ExaminesOtherWingsOfSameGoal_AndTimeoutStaggerWorks` proves the on barrier, independent off behavior, delayed timeout and release after the other wing assembles. Existing `P635MultiRouteTests` also pass. |
| 15 / #8 — staging route | `tools/allegiance-mcp/BotManager.cs:2412` takes the assigned route segment ending at staging rather than requiring the full route to end there. `:2396` falls back to a graph search avoiding the rendezvous, through `src/Allegiance.ClientCore/State/ClusterGraph.cs:73`. | `Staging_UsesAssignedPrefix_AndFallbackAvoidsRendezvous` calls the production path controller on the review's shortcut topology and proves both assigned-prefix and fallback behavior. |
| 16 / #19 — capped cells | `tools/allegiance-mcp/BotSpatialIndex.cs:72` enlarges cell size and recomputes both dimensions until the full grid fits the cell budget. Coordinates are relative to the bounds, preventing large-offset integer overflow; distance checks use doubles. | `SpatialGrid_EnlargesCellsToCap_WithoutDroppingOccupants` uses million-unit bounds with one-unit requested cells, then queries every occupant. |
| 17 / #12, partial finding — granted hull | `tools/allegiance-mcp/BotManager.Lifecycle.cs:183` takes a known world hull first, then the recorded grant, then the request. Steering acceleration/speed, ship radii, docking and dodge use this helper. Requested hull/loadout remain the purchase request. | `ActualHull_UsesWorldGrant_IncludingPod_AheadOfRequestedHull` proves an authoritative pod hull overrides the original fighter request. No station capability or fighter-bay rules were changed: the brief rejected that part of the original finding. |
| 18 / #6 — snapshot aim coherence | `src/Allegiance.ClientCore/State/DecisionSnapshot.cs:109` extrapolates attitude with frozen angular rates and the native axis signs. `tools/allegiance-mcp/BotManager.DecisionFrame.cs:12` scopes one captured generation/time across nested decision helpers, including scenario and standalone flight paths. `tools/allegiance-mcp/BotManager.cs:2177` evaluates position and attitude at that common time. Current-world target/life validation occurs before use; mismatched mission generations are skipped. | `SnapshotAttitude_ExtrapolatesWithNativeAxisSigns`, `SnapshotFrame_IsOneGeneration_EvenWhenWorldPublishesDuringDecision`; the entire new regression set also passes in a separate snapshot-on process. |
| 19 / torn snapshot and age list | `src/Allegiance.ClientCore/State/ClientWorld.cs:141` publishes a new snapshot owning fresh arrays; published storage is never refilled. This guarantees safety for arbitrarily delayed readers rather than relying on an eight-publish grace period. `tools/allegiance-mcp/BotManager.cs:2228` stores age samples in a fixed ring. Snapshot mode stays off by default. | `HeldSnapshot_IsStableBeyondEightPublishes_AndAgeSamplesAreBounded` holds a frame across 20 changing publishes and tests age-ring rollover; `FreshSnapshotPublish_AllocationStaysBoundedAt390Ships` forces 20 fresh 390-ship publishes under a 1 KB/ship ceiling. |

## Validation

- `dotnet build AllegianceCore.slnx --no-restore` was run. Default parallel
  MSBuild exited 1 with “Build FAILED”, **0 warnings / 0 errors**, and no
  diagnostic, matching the earlier review's sandbox behavior.
- `dotnet build AllegianceCore.slnx --no-restore -m:1` succeeded with
  **0 warnings / 0 errors**. The probe runner was separately built with the same
  zero-warning/error result.
- Ordinary `dotnet test` cannot initialize VSTest's communication channel here:
  its own TCP listener fails with `SocketException (13): Permission denied`,
  before any selected unit test executes. Added
  `tests/Allegiance.BotFix.Probes/Program.cs:1` to execute selected xUnit facts
  and inline theories directly, without VSTest or a socket.
- The new regression set passed **37 cases, 0 failures, 0 skips** with snapshots
  enabled. The broader default-mode set, including those 37 cases, passed
  **168 cases, 0 failures, 0 skips**.
- Forced fresh 390-ship snapshot publishes allocated **377 B per ship** in the
  focused probe, below its 1 KB/ship ceiling. The existing decode/apply/publish
  allocation test also passed its 2 KB/ship ceiling. These are allocation
  probes, not rig throughput/GC measurements.
- `git diff --check` passed. The branch/base and protected source paths were
  checked; no commits or pushes were performed.

The restore assets were absent initially. Dependency restore succeeded from the
cached packages with `dotnet restore AllegianceCore.slnx --ignore-failed-sources
-p:NuGetAudit=false -m:1`. This only bypassed unavailable network vulnerability
metadata for the local restore; no repository NuGet/build policy was changed.

To reproduce the socket-free checks:

```sh
dotnet restore tests/Allegiance.BotFix.Probes --ignore-failed-sources -p:NuGetAudit=false -m:1
dotnet build tests/Allegiance.BotFix.Probes --no-restore -m:1
dotnet tests/Allegiance.BotFix.Probes/bin/Debug/net10.0/Allegiance.BotFix.Probes.dll \
  BotFixRegressionTests ClientWorldProjectileTests ClientWorldEventLogTests \
  ClientWorldDockLoadoutTests ClientWorldGameOverBackTests DecisionSnapshotTests \
  DecisionSnapshotAllocationTests P636dSnapshotPublishCostTests \
  P636bCadenceSnapshotTests P633DecisionLoopTests P635MultiRouteTests \
  P637BomberRoutingAndStagingTests P622CombatTargetSpreadTests \
  P539CombatDecisionTests P539bCombatMetricTests P553DodgeDecisionTests \
  P560PodHomeStationTests OrderAcceptTranslateTests
ALLEGIANCE_BOT_DECISION_SNAPSHOT=on \
  dotnet tests/Allegiance.BotFix.Probes/bin/Debug/net10.0/Allegiance.BotFix.Probes.dll
```

## Deliberate choices and outstanding live validation

No brief implementation item was left unfinished. Socket tests, network failure
injection, sustained 390-bot RSS/GC profiling, and the live 200v200 march were not
run in this sandbox; Claude's full suite and rig checks remain necessary.

After three unsuccessful launch requests a bot remains docked and logs a
warning. It never manufactures money, bypasses the purchase rules or requests
server-side NPC undocking. The player sequence was verified against
`src/Allegiance.ClientCore.Game/Game1.Panes.cs:285` and `:417`, and
`src/Allegiance.ClientCore/Session/ConnectionSession.cs:262`:
`BUY_LOADOUT(fLaunch:true)` followed by server ACK/launch confirmation.

Immutable snapshot ownership intentionally trades pooled-array reuse for
correctness in the opt-in path. Snapshot-on receive allocation/GC behavior must
be A/B measured on the rig. The default-off path still performs no publishes.
Friendly grids share only same-side known friendly state; target visibility
continues to come from each bot's own world. Missing grid entries use a direct
scan, so they do not reintroduce per-bot grid construction.

The event log remains bounded to 500 entries per stream. Sequence readers
survive rollover, but an observer falling behind by more than the entire retained
window cannot recover already-evicted events. Removed/lost status history is also
bounded to 500 rows. The source list normally removes a lost bot on its next
loop pass; participation/status checks treat it as inert immediately.
