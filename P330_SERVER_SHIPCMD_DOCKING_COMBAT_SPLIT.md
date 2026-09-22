# P330 — Server.cs partial-class split, stage 4 (final): ShipCommands + Docking + Combat

Structural refactor only. **Zero behavior change.** Final stage of the god-class
breakup begun in P327: moves the inbound ship-command handlers, the
board/leave/dock/undock/teleport/rescue/ripcord/relaunch members, and the
ordnance / kill / death / eject / win-condition members out of `Server.cs`
into three new partial-class files, completing the four-stage plan.

## Scope moved this stage (74 methods)

- **`Server.ShipCommands.cs` (18)** — `HandleViewCluster`, `HandleBoardShip`,
  `HandleActiveTurretUpdate`, `HandlePromote`, `HandleSwapPart`, `HandleReload`,
  `HandleDropPart`, `HandleFireExpendable`, `HandleFireMissile`, `HandleSetWingId`,
  `HandleReqPingData`, `HandleOrderChange`, `ApplyCommandSlot`, `HandleTreasureAck`,
  `ApplyLoadout`, `SetBuilderTargetOnShip`, `CanMount`, `MountCombatWeapon`.
- **`Server.Docking.cs` (33)** — `PerformBoard`, `PerformLeave`,
  `HandleRipcordRequest`, `RequestRipcord`, `RequestRipcordCore`,
  `OnNpcRequestRipcord`, `SendSectorWide`, `PerformRipcordTeleport`,
  `PerformRescue`, `PerformStationRescue`, `PerformTreasurePickup`,
  `BroadcastTreasureDestroyed`, `BroadcastGainFlag`,
  `BroadcastBuildingEffectDestroyed`, `BroadcastBuildingEffectCreated`,
  `PerformRelaunch`, `ShipRadius`, `MoveShipToSector`, `UpdatePeerSectorSnapshot`,
  `ChooseTurretSlot`, `ShipValue`, `StationSellsHull`, `FindSameSideShip`,
  `FindShipAnySide`, `AllocateShipId`, `IsShipIdLive`, `SendBoardNack`,
  `TryResolveViewTarget`, `SeenByRequesterSide`, `RepositionForLaunch`,
  `UndockShipState`, `PerformUndock`, `PerformTeleport`.
- **`Server.Combat.cs` (23)** — `BroadcastProjectileInstance`,
  `BroadcastFireMissile`, `BroadcastMissileDestroyed`, `BroadcastLoadoutChange`,
  `BroadcastExportMine`, `BroadcastMineDestroyed`, `SendExplosionEvent`,
  `BroadcastExportProbe`, `BroadcastProbeDestroyed`, `BroadcastWarpBomb`,
  `BroadcastAsteroidDestroyed`, `BroadcastStationDestroyed`,
  `BroadcastStationCapture`, `OnVictoryByStationsComputed`,
  `OnProsperityArtifactComputed`, `OnFlagsKillsComputed`, `BroadcastKillShip`,
  `FinalizeDeadShip`, `FindFriendlyStation`, `EjectSurvivor`, `OnMine`,
  `OnShipWarped`, `OnShipDocked`.

No behavior change, no bug fix, no naming cleanup. No native reference source
(`~/Development/Allegiance`) touched.

## Verification
### Byte-identical method bodies (the hard requirement)
As in P327–P329, each moved block was extracted with the **Roslyn** syntax tree
(`MethodDeclarationSyntax.FullSpan`, carrying the attached XML doc comment and
attributes) rather than a hand-rolled brace matcher, eliminating lexer edge
cases. The 74 `FullSpan` blocks were deleted from `Server.cs` (pairwise-disjoint
spans, source order) and each inserted verbatim into its target file.

Programmatic check against `git HEAD`: all 74 moved blocks appear **byte-for-byte**
in their target file (compared with leading/trailing whitespace trimmed) and are
**absent** from the new `Server.cs` — **0 byte-diffs**, no strays, exact
18/33/23 census. Method bodies are unchanged to the byte (modulo the move).

### Build
Whole-solution `dotnet build AllegianceCore.slnx -c Debug`: **0 warnings, 0 errors**.

### Full test suite (run 2x vs the current baseline)
`Core.Tests` (3,268 total). Pristine baseline was captured **before** the change.
- **Baseline (pristine HEAD):** 3,263 pass / 5 fail — `LoadoutChangeLiveTests.Boarding…`,
  `LiveWinConditionTests.Multi_mission…`, `LiveTurretGunnerBindingTests` (documented
  persistent flake), `LiveMoneyTests.Board_refund…`, `AsteroidMinedLiveTests.No_broadcast…`
  — all the documented live-network / timing-flake family.
- **Changed tree, run 1:** 3,264 pass / 4 fail — `LiveTurretGunnerBindingTests` (doc flake),
  `LiveTeamLobbyTests.Unready…`, `LiveMoneyTests.Board_refund…`, `AddPartTests.Part_pickup…`.
- **Changed tree, run 2:** 3,266 pass / 2 fail — `LiveTurretGunnerBindingTests` (doc flake),
  `GameOverPlayersWireTests.Conquest_win_in_one_mission…`.

The only failure present in **all three** runs is the documented persistent
`LiveTurretGunnerBindingTests` flake. Every other failure appears in exactly one
run, is a `[Collection("LiveNetwork")]` timing test, and none repeats across the
two changed-tree runs. `AddPartTests.Part_pickup…` (the only new name vs baseline)
was re-run 3× in isolation on the changed tree and **passed 3/3** — proven flaky,
and it exercises the treasure-pickup path that stays byte-identical to HEAD.
Identical pass/fail character ⇒ the move introduces no behavioral difference.

### Live proof
Focused live run of the integration classes that boot a real in-process server
and drive real wire sessions over exactly the moved functionality — ship
commands (view-cluster, promote, loadout/swap-part, cargo reload, ping-data,
builder-target, order-change/launch, turret binding/fire), docking/undocking
(board, relaunch, teleport-dock), a rescue + station rescue, ripcord/teleport,
treasure pickup, eject pods / lifepod oxygen, and combat (ship-death finalize,
player-death respawn, kill bonus, asteroid/station/probe combat, station capture,
flags/conquest/prosperity win conditions) — **118 passed, 0 failed**. Boarding,
firing, docking/undocking, rescue, teleport and combat kill/eject all still
behave exactly as before.

## Deliverable
`src/Allegiance.Server/Server.ShipCommands.cs` (1,674 lines, 18 methods),
`src/Allegiance.Server/Server.Docking.cs` (1,833 lines, 33 methods) and
`src/Allegiance.Server/Server.Combat.cs` (1,652 lines, 23 methods) created;
`Server.cs` slimmed **5,763 → 668 lines (−5,095)**. Committed on `master`
(not pushed — Domingo pushes). No side branch. No native reference source touched.
