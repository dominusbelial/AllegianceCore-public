# P206 — AllegianceClientCore: fix the 7 tests broken by the P180/P188 Stage gate

## Summary

`tests/Allegiance.ClientCore.Tests` had 7 of 52 tests failing deterministically.
Root cause is the P180/P188 ready-up Stage gate (`Mission.Update` does nothing
while `Stage == MissionStage.NotStarted`, and the inbound `SHIP_UPDATE` control
input is only applied at the exact `MissionStage.Started` stage). This is a
test-harness gap, not a server bug: the mission must be driven through its real
ready-up auto-start (all occupied sides aggregate-ready) and the 15s sim-time
countdown before any test commands movement / expects combat / expects a dock.

Every fix is confined to `tests/Allegiance.ClientCore.Tests/*.cs`. No changes to
`Allegiance.Core`, `Allegiance.Server`, or `Allegiance.ClientCore` source.

## Mechanism for observing `Stage == Started`

Each test holds the real in-process `Server` (`Srv`) and reaches the mission
through the **public property path** `server.Missions[0].Stage`, where
`Server.Missions` is `IReadOnlyList<Allegiance.Core.Model.Mission>` (declared
public on `Server`) and `Mission.Stage` is `public MissionStage Stage { get; set; }`
(`src/Allegiance.Core/Model/Mission.cs`). Each affected test already owns a
`PumpUntil(Srv server, Func<bool> done, int max)` helper; it is reused to poll
`server.Missions[0].Stage == MissionStage.Started`, bounded at `2000` ticks
(the config is `UpdatesPerSecond = 20`, so the 15s countdown needs a minimum of
300 ticks; 2000 gives a real margin). Each wait asserts it did not hit the bound,
matching the suite's existing `PumpUntil` assertion style.

The ready-up uses the existing `ConnectionSession.SendPlayerReady(fReady: true,
shipId: <own ship id>)` — no new wire method, no `ConnectionSession.cs` changes.
`PLAYER_READY` → `RecomputeSideReady` → when every occupied side is aggregate-
ready, `TryAutoStart` calls `StartCountdown` (`Stage` = `Starting`
immediately), and `AdvanceStartCountdowns` crosses `Starting → Started` after
the sim-time countdown, then broadcasts `ENTER_GAME`.

## Per-test changes

### 1. JoinFlowLiveTests.EntityTable_TracksARealTickingServer (1 peer)
- **Peers readied:** the single peer (`session.SendPlayerReady(true, id)`).
  Its side is the only occupied side, so it satisfies `IsAllOccupiedSidesReady`.
- **Stage observation:** `PumpUntil(server, () => server.Missions[0].Stage ==
  MissionStage.Started, 2000)` inserted before the forward-thrust command.

### 2. TeammateMovementLiveTests.TeammateMovement_StreamsIntoClientWorld_MultipleObservations_EnemyNeverPresent (3 peers)
- **Peers readied:** all three. 3 peers across 2 teams occupy BOTH sides, so
  every peer must ready (`foreach (var s in sessions) s.SendPlayerReady(...)`).
- **Stage observation:** same `server.Missions[0].Stage == Started` pump,
  inserted after sides are derived and before commanding thrust.

### 3. KillShipLiveTests.VictimShipEntity_MarkedDead_WhenRealKillCrossesTheWire (2 peers)
- **Peers readied:** both (`shooter` and `victim`).
- **Stage observation:** same pump, inserted before commanding `AllWeapons`.
- **Additional harness fix:** `InjectFightSpawn` originally spawned both ships
  with **no** `SideId`. A null-sided ship occupies no side, so the ready-up
  auto-start could never fire (`RecomputeSideReady` bails on a null side) and
  the mission sat `NotStarted` forever. The shooter now carries
  `mission.Sides[0].Id` (making its side occupied so the shooter's ready drives
  auto-start); the target deliberately stays **side-less** — see the P107 note
  below.

### 4. DockLoadoutLiveTests.DockedNotify_SetsAndClearsStationId_Live (1 peer)
- **Peers readied:** the single peer.
- **Stage observation:** same pump, inserted after join and before the first
  dock `PumpUntil` (dock-radius detection only runs once Started).

### 5. DockLoadoutLiveTests.BuyLoadout_RoundTrips_And_WithFLaunch_Undocks_Live (1 peer)
- **Peers readied:** the single peer.
- **Stage observation:** same pump, inserted after join and before the dock
  expectation that the launch phase depends on.

### 6. FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose (2 same-side peers)
- **Peers readied:** the observer was **added** — the test already readied the
  subject, but both peers share the single occupied side, so the side's
  aggregate readiness needs both. `observer.SendPlayerReady(true, observerId)`.
- **Stage observation:** same pump, inserted after the phase-2 ready relay
  assertions and before the dock/loadout/launch/movement phases.

### 7. FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill (3 peers, both sides occupied)
- **Peers readied:** peer0 and peer2 were **added** — the test already readied
  peer1, but peer0/peer1 share side 0 and peer2 is side 1, so both occupied
  sides need all three ready. `peer0.SendPlayerReady(true, id0)` +
  `peer2.SendPlayerReady(true, id2)`.
- **Stage observation:** same pump, inserted after the phase-2 ready relay and
  before the loadout/movement/kill phases.
- **Additional harness fix (see P107 note below):** immediately before the kill
  phase, the victim's server-side `SideId` is cleared via the same documented
  `Srv.Ships` reflection seam the test already uses to reposition the attacker:
  `FindShip(server, id1).SideId = null;`. This makes the kill resolve to a
  direct `KILL_SHIP` instead of a P107 lifepod-eject.

## Why the two kill tests needed more than ready-up (P107)

The `KillShipLiveTests` and `FullSessionKillIntegrationTests` kill assertions
predate **P107** ("in-space EJECT", `50fb027`, 2026-09-08) as well as P180.
They were written against a server that killed a target on the first lethal hit
(`KILL_SHIP(105)` → observer `IsDead` flips).

Under the current server, a **side-bearing** victim that takes a fatal hit
EJECTS to a drifting lifepod hull instead of dying (`Server.KillShipEvent`,
`bKilled = (owner is null) || bLifepod || amount < 0`; `EjectSurvivor` returns
the survivor). That ejected lifepod is then **dead-in-models**: `Ship.ChangeHull`
does not restore `_fraction`, so `ship.Dead` stays `true` and
`Sector.ResolveCombatCollisions` (`if (ship.Dead) _models.Remove(ship)`) drops
it from the sector collision list — it can never absorb the second, killing hit
that would produce the `KILL_SHIP` the tests assert. (Verified empirically:
after ejection the server-side victim reads `frac=0 dead=True`, its position is
frozen, and it never takes damage again.) Since a genuine port fix for that
lifepod path would be a `Allegiance.Core`/`Allegiance.Server` change (outside
this task's scope), the tests instead arrange the **direct-death** path that
they were originally written to assert:

- `EjectSurvivor` bails (returns `false`, "treating as death") when
  `victim.SideId is not { }`. So a null-sided victim dies on the first lethal
  hit with a real `KILL_SHIP(105)` — no lifepod detour, no weakened assertion
  (the observer still sees `IsDead` flip and a `Kills` event).
- `KillShipLiveTests`: the target is spawned side-less (the shooter keeps a
  side for auto-start).
- `FullSessionKillIntegrationTests`: the victim needs real side 0 for the
  same-side composition phases (1–5), so its side is cleared only at the start
  of the kill phase, after those phases complete.

The friendly-fire gate does not block these kills: it only skips when
`shooter.SideId == ship.SideId`, and a null-sided target never equals the
shooter's side.

## Verification

1. `dotnet build` — 0 warnings, 0 errors, whole solution.
2. `dotnet test tests/Allegiance.ClientCore.Tests` — 52/52 passing, four
   consecutive clean runs (runs 2–5 of the working tree; run 1 had one
   transient, load-induced flake in an *untouched* phase-1 EXPORT assertion of
   `FullSessionKillIntegrationTests` that has not recurred in five subsequent
   full-suite runs).
3. `dotnet test` at repo root — `Allegiance.Core.Tests` 3026/3026 and
   `Allegiance.Lobby.Tests` 12/12 both unaffected.

Files changed (tests only):
- `tests/Allegiance.ClientCore.Tests/JoinFlowLiveTests.cs`
- `tests/Allegiance.ClientCore.Tests/TeammateMovementLiveTests.cs`
- `tests/Allegiance.ClientCore.Tests/KillShipLiveTests.cs`
- `tests/Allegiance.ClientCore.Tests/DockLoadoutLiveTests.cs`
- `tests/Allegiance.ClientCore.Tests/FullSessionIntegrationTests.cs`
- `tests/Allegiance.ClientCore.Tests/FullSessionKillIntegrationTests.cs`
