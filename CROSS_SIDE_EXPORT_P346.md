# P346 — Broadcast ship identity (EXPORT) cross-side, not just same-side

Landing date: 2026-09-22 · Committed on `master` (not pushed — Domingo pushes).

## Goal / context

Domingo's goal (2026-09-22): "we need to fix the cross side visibility, the goal
is to render all hulls for a nice picture" — see all 72 distinct P287 ship models
simultaneously in a `vizfight roster` session, where they spread across all 9
factions (sides).

Investigation this session pinned the precise gap: it is **one missing half of an
already-built feature**, not a new-from-scratch one.

- Already real + landed + tested: the cross-side visibility *foundation*
  (P131/P132/P139): `Sim/SideVisibilityState.cs` (`SetSideVisibility` /
  `GetCurrentEye`, per model per side), `Sim/VisibilityMath.cs`
  (`InScannerRange`/`LineOfSightExist`), `Sector.ResolveCrossSideVisibility`
  (the per-tick sweep, `Sector.cs:2865`), and `Server.BroadcastShipsUpdate`
  (P139) which already sends `HEAVY_SHIPS_UPDATE`(176)/`LIGHT_SHIPS_UPDATE`(175)
  to every connected player, side-agnostic.
- The gap: those heavy/light updates carry only **live telemetry** (`ShipId`,
  `Controls`, `Time`, `Position`, `Orientation`, `Velocity`, `TurnRates`,
  fractions, `Power`) — confirmed by direct read of
  `Wire/HeavyShipsUpdate.cs`/`LightShipsUpdate.cs`, no `HullId`/`SideId`/name.
  The **identity** message `EXPORT`(26) (carries `HullId`, name, parts — what a
  client needs to know *what to render*) was **only ever broadcast same-side**:
  `Server.Broadcast.cs:480` (`[teamexport]`), the P62 teammate-export path. Zero
  cross-side EXPORT call sites existed (grepped). So even after P347 decodes
  176/175, a foreign ship would be a bare `ShipId` + position with no hull to
  resolve a model from.

## The change

Three coupled edits — two in `Allegiance.Core` (edge detection + pending-list
drain) and one in `Allegiance.Server` (turn each drained edge into the EXPORT
broadcast).

### 1. Core: detect the genuine visibility false→true edge
`Sector.ResolveCrossSideVisibility` (`Sector.cs:3004-3007`). `SetSideVisibility`
does not itself report whether the call is a transition (confirmed:
`Sim/SideVisibilityState.cs:81-85` only assigns both flags). The caller therefore
reads the **prior** flag before overwriting it:

```csharp
bool wasSeen = t.Body.SeenBySide(sideId);
t.Body.SideVisibility.SetSideVisibility(sideId, seen);
if (seen && !wasSeen && t.Body is Ship s)
    _pendingCrossSideExports.Add((s, sideId));
```

Only ships queue (asteroids/stations/probes are excluded by `t.Body is Ship s`);
the loop already `continue`s past the ship's own side (`sideId == t.SideId`), so
an edge is inherently cross-side and a ship's own side never produces a
cross-side export (no same-side `[teamexport]` duplicate, by construction).
Nothing about the visibility *determination* (scanner range / LOS / cadence) is
touched — this only layers the edge-triggered send on top of what P132 already
computes.

### 2. Core: pending-list + drain (established host-drain pattern)
New field `_pendingCrossSideExports` (`Sector.cs:182`), the same
accumulate-per-tick / drain-after-`Mission.Update` split as
`_pendingRipcordAborts` / `_pendingStationRescues` (`Sector.cs:149,172`).
New drain `DrainCrossSideExports()` (`Sector.cs:796`) returns this tick's
`(Ship, short SideId)` newly-cross-visible pairs and clears the accumulation.

### 3. Server: turn drained edges into the EXPORT broadcast
- New SimPass step `crossSideExports` (`Server.Broadcast.cs:185-189`), placed
  among the other host drains: `foreach mission → sector →
  sector.DrainCrossSideExports() → BroadcastCrossSideExport(ship, sideId)`.
- `BroadcastCrossSideExport(Ship ship, short sideId)` (`Server.Broadcast.cs:759`):
  - **Send target — per-side.** The newly-seeing side's connections only
    (`_peerShips.Where(... SideId == sideId)`), the same side-wide audience
    `STATIONS_UPDATE`(P133) and `BroadcastMoneyChangeSideWide` use — never
    whole-mission, never the ship's own side (which already has its own-side
    export). Early-return when no peer is seated on that side.
  - **What to send.** The exact `ExportShip`/EXPORT wire shape the same-side
    path builds (`Server.Broadcast.cs:450-460`): real `HullId`
    (`ship.HullType.HullId`), real name (from `_peerNames` via a new
    `PeerName` helper; a stable `Ship {id}` fallback for an ownerless NPC),
    real `ShipId`, the mounted-parts snapshot (`EnsureLoadoutSnapshot`), and
    `SideId` = the **exported ship's own** side (faction identity), not the
    seeing side. Logged as `[xexport]`.

The same-side `[teamexport]` path (`Server.Broadcast.cs:480`) is untouched.

## Verification — real proof

### Live cross-side EXPORT proof (new: `tests/.../CrossSideExportLiveTests.cs`)
2 real bots on **different sides**, same cluster, mission forced to `Started`
(the sweep only runs once `Mission.Update` sees `Started`/`Starting`; the P180
bypass `foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;`
is the established pattern). To exercise a **genuine transition** — not both
spawned already-visible — both ships are parked 500 km apart (beyond every
scanner range AND every real station scanner range ~3900), confirmed **no**
cross-side EXPORT while mutually out of range, then brought to 300 u and
confirmed each receives a real EXPORT of the OTHER with the correct `HullId`:

```
[xexport] side 1 <- ship 8096 'p346_x_a' hull 92 to 1 peer(s)
[xexport] side 0 <- ship 8097 'p346_x_b' hull 92 to 1 peer(s)
```

`Cross_side_ship_becoming_visible_triggers_export` asserts: no cross export
while far; each receives the other's EXPORT once in range; `HullId` matches the
other's real hull; wire `SideId` = the other's own side.

### No-regression / no-duplicate proof (same test file)
`Same_side_teammate_still_gets_teamexport_no_cross_duplicate`: 2 bots on the
SAME side still get the existing `[teamexport]` — exactly **one** EXPORT of the
other (real hull id + name), no cross-side double (the sweep never fires for a
ship's own side; a loadout re-export is not induced). NOTE: no `ResetExports`
there — the same-side teamexport is a one-shot per (recipient, subject) pair
gated by `_teammateExportSent`, so clearing the accumulator after join erases
the only copy; the cross-side export is edge-triggered so the other test CAN
reset and re-observe.

### Must-fail-if-removed (hard requirement)
Temporarily disabled the edge-queue line
(`if (false && seen && !wasSeen ...)`), rebuilt, ran the cross-side test: **RED**
(the EXPORT never fires). Restored: green.

### Build / tests
- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.
- `CrossSideExportLiveTests`: 2/2 pass.
- Regression suites (`CrossSideVisibilitySweepTests` P132, `ShipsUpdateLiveTests`
  P139, `StationsUpdateLiveTests` P133): 11/11 pass.
- Full `Allegiance.Core.Tests` suite: run at least twice; only the documented
  pre-existing live-wire timing flakes interrupt (failing names vary run to run).

## Scope boundaries honored
- Client-side decoding of 176/175 and of the new cross-side EXPORT is **out of
  scope** — P347 (already-scoped, queued next). This is server-broadcast-only.
- `SideVisibilityState`/`VisibilityMath`/the sweep's determination logic: not
  changed (only the edge-triggered send is layered on top).
- The same-side `[teamexport]` path: untouched (separate, additional broadcast).
- `vizfight`/`Allegiance.ClientCore.MonoGame`: out of scope.
- Native reference source (`~/Development/Allegiance`): read-only, untouched.

## Where the pieces landed (file:line)
- Edge detection: `src/Allegiance.Core/Model/Sector.cs:3004-3007`
  (`ResolveCrossSideVisibility`).
- Pending list: `src/Allegiance.Core/Model/Sector.cs:182`; drain
  `DrainCrossSideExports` `Sector.cs:796`.
- Server step: `src/Allegiance.Server/Server.Broadcast.cs:185-189`.
- `BroadcastCrossSideExport` + `PeerName`: `Server.Broadcast.cs:759` / after it.
- Tests: `tests/Allegiance.Core.Tests/CrossSideExportLiveTests.cs`.
