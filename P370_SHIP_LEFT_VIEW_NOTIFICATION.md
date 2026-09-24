# P370 — ships that leave a viewer's sector (dock OR warp out) render as a frozen ghost forever

**Summary.** Live 4-client playtest (2026-09-24) reproduced a ghost-ship bug: when one
player warped a ship from Tellus to Bug Nebula (real server log `[warp] ship 8097 crossed
from 'Omega' (1053) to 'Bug Nebula' (1054) via warp 2154`), a teammate who stayed behind
kept rendering that ship, frozen at its last Tellus pose, forever. Root cause: a ship that
dies, docks, or warps to a different sector is `Remove`d from that sector's `_models`
(Sector.cs `GetShipUpdateCandidates`, documented "a ship dying, warping out or docking all
Remove from `_models`") and simply stops appearing in that sector's future telemetry —
**nothing ever told the peers still in that sector "this ship is gone."** Their client kept
the last `ShipEntity.Latest` pose and drew it indefinitely.

This task builds ONE unified "this ship is no longer in your view" signal that generalises
the already-correct death pattern (`KILL_SHIP(105)` + `SHIP_DELETE(20, SDR_KILLED)`) to the
other two real departure triggers (docking, sector warp-out), and makes the client honor it.

---

## Mechanism chosen — piggyback on the existing SHIP_DELETE(20) broadcast (not a new message)

The death path already drives a working `FM_S_SHIP_DELETE(20)` to the sector audience
(`Server.Combat.BroadcastKillShip`, `SDR_KILLED`). Native Allegiance already defines the two
reasons this gap needs, with exactly the semantics required, in `Messages.h:188-195`:

```
enum ShipDeleteReason {
  SDR_OUTOFRANGE, SDR_KILLED, SDR_LEFTSECTOR, SDR_DOCKED, // client will cache ships when deleted for these reasons
  SDR_LOGGEDOFF,  // client is gone, so waste their ship and everything about it
  ...
}
```

- `SDR_LEFTSECTOR` (2) — "this ship warped to a different sector."
- `SDR_DOCKED` (3) — "this ship docked at a station."
- The "client will cache ships when deleted for these reasons" comment is exactly the
  **comes-back-into-view** requirement: the client keeps the entity but hides it, and
  resurrects it when the ship returns.

And native already has the audience-builder we need: `CFSShip::AnnounceExit(pclusterOld,
sdr)` (`fsship.cpp:196-226`) broadcasts `SHIP_DELETE` for `SDR_DOCKED`/`SDR_LEFTSECTOR` to
`GetGroupSectorFlying(pclusterOld)` — the peers **flying in the sector the ship just left**.

So the cleanest real mechanism — instead of inventing a new wire message — is: **emit the
same `SHIP_DELETE(20)` for warp-out and dock that we already emit for death**, using the
native reasons `SDR_LEFTSECTOR`/`SDR_DOCKED`, to the same sector-flying audience the death
path uses. This follows the exact "same shape for consistency" guidance, requires zero new
wire protocol, and the codec already existed (P103/P128 shipped `SDR_KILLED`/`SDR_LOGGEDOFF`;
adding the two factory methods is trivial).

### Why the sector-flying audience is right (and subsumes P369's bookkeeping)
`BroadcastShipDeparted` notifies every peer whose own ship is **flying in the old sector**
(the `GetGroupSectorFlying` analogue of the death path's `targets`). This automatically
covers both audiences the task names:
- **same-side teammates** in the sector (they got the `[teamexport]` + telemetry), and
- **cross-side peers** who received a cross-side `EXPORT` for the ship — by construction a
  cross-side peer can only have that identity if it has a seated peer in the departed ship's
  sector (P369's `SweepCrossSideExports` gate), i.e. it is in that sector, so the
  sector-flying audience already contains it. A peer who never knew the ship is an inert
  recipient: the client ignores a `SHIP_DELETE` for an unknown id (no phantom entity).
The subject ship's own peer is never told to hide its own vessel (that peer is flying in a
different sector after a warp, or docked after a dock — both excluded by the audience, and
additionally guarded by an explicit reference-identity skip).

---

## What changed

### Server (`src/Allegiance.Core/Wire/ShipDelete.cs`, `src/Allegiance.Server/Server.Combat.cs`)
- `ShipDelete.cs`: added `SdrLeftSector = 2` and `SdrDocked = 3` constants plus
  `ForShipLeftSector(short)` / `ForShipDocked(short)` factories (doc comment updated; the
  codec/layout is unchanged — 12-byte fixed, `ShipId` + 4-byte enum, already validated).
- `Server.Combat.cs`: new private `BroadcastShipDeparted(Ship, Sector?, int sdr)` — the
  `GetGroupSectorFlying(oldSector)` audience (flying peers in the sector being left,
  excluding the subject ship's own peer), emitting one `SHIP_DELETE` to each.
  - `OnShipWarped(...)` now calls it with `SDR_LEFTSECTOR` (native `FedSrv.CPP:13525`).
  - `OnShipDocked(...)` now calls it with `SDR_DOCKED` (native `FedSrv.CPP:13107`,
    `AnnounceExit(pship->GetCluster(), SDR_DOCKED)`).
- Disconnect (`SDR_LOGGEDOFF`) and death (`SDR_KILLED`) broadcasts already existed and are
  unchanged.

### Client (`src/Allegiance.ClientCore/...`)
- `ShipEntity.cs`: new **`IsGone`** (transient "the server told us this ship is no longer in
  our view") and the unified render/target predicate **`IsNotVisible => IsDead || IsGone`**.
  Kept `IsDead` as the permanent death latch; `IsGone` is the transient departure latch that
  a returning ship clears.
- `ClientWorld.cs`:
  - new `Apply(ShipDelete)` — sets `IsGone` (and `IsDead` for `SDR_KILLED`), **only for an
    already-known ship** (guarded on entity existence → no phantom entries for unknown ids).
  - `Apply(KillShip)` also sets `IsGone` (a dead ship is also out of view).
  - presence applies that **clear** `IsGone` (the "comes back" path):
    `Apply(HeavyShipsUpdate)` (176 — carries only ships in the viewer's OWN cluster, so it is
    the sector-correct "back in our view" signal), `Apply(SetCluster)`, `Apply(ExportShip)`.
  - `Apply(SingleShipUpdate)` deliberately does **NOT** clear `IsGone`: this message is sent
    to a peer for same-side teammates in ANY sector (P85 teammate position), so it can arrive
    for a ship that is NOT in our view — clearing on it would un-hide a warped-away teammate
    (found and fixed during live verification; see below).
- `ConnectionSession.cs`: dispatch `ShipDelete.Fmid(20)` → `_world.Apply(ShipDelete.Decode(m))`
  (was previously unhandled — the root of the client ignoring all departures except death).
- `Game1.cs`: converted all six render/target `ship.IsDead` checks to `ship.IsNotVisible`
  (main pose gather `:1945`, teleport-pane list `:2320`, radar contacts `:4352`, autopilot
  readout `:5312`, target-candidate gather `:5567`, autopilot position resolve `:5764`), so a
  docked/warped ship is hidden everywhere a dead ship already was — not just the main render
  loop. Each site keeps its surrounding sector/`Latest` guards.
- `LeaderBotCommand.cs`: deliberately stays `IsDead` (NOT `IsNotVisible`) with an explanatory
  comment — a DOCKED teammate-bot is "gone" from view but must remain commandable so the
  leader can issue LAUNCH; converting it would break P366.

### Comes back into view (requirement 3)
A gone ship is un-hidden (clear `IsGone`) by the exact server signals that resume when the
ship is genuinely back in the viewer's sector: `HeavyShipsUpdate(176)` telemetry (undock or
warp-back re-adds the ship to the sector's update list), a fresh `SET_CLUSTER` (the ship
itself), or a re-established `EXPORT` (a peer who re-gains visibility). Death stays hidden
forever because `IsDead` is never cleared even if a stray identity arrives.

---

## Verification — real proof

Two layers, both against a **real in-process `Allegiance.Server` over the real TCP transport**
with **real `ConnectionSession` clients driving real `ClientWorld`s** (the same data plane the
MonoGame client uses) — exactly the shape of the P369 live harness, but as CI-reproducible
xUnit live tests.

### Deterministic client tests — `tests/.../ShipDeleteGoneTests.cs` (12)
Pin the `ClientWorld` reaction with no network: `SDR_DOCKED`/`SDR_LEFTSECTOR`/`SDR_KILLED`/
`SDR_LOGGEDOFF` mark a known ship gone/hidden (not dead); a delete for an unknown ship makes
no phantom; `KILL_SHIP` latches death; a cross-sector `SingleShipUpdate` does NOT un-hide a
departed teammate; a re-`EXPORT` clears `IsGone` (returns to view); a dead ship stays hidden
even if an identity arrives; wire round-trip of all four reasons.

### Live multi-client tests — `tests/.../ShipDeleteGoneLiveTests.cs` (4, 2 real clients each)
| Case | Proof |
|---|---|
| **Same-side warp-out** | 2 Alpha clients, same sector. Departer slides into a warp and transits; watcher (still in the old sector) latches the departer `IsGone`/`IsNotVisible` promptly; the departer's OWN peer is NOT told to hide its own ship. |
| **Same-side warp-back** (reverse) | After the warp-out, the departer is slid into the destination's return warp and transits back into the watcher's sector; the watcher clears `IsGone` — the ship is visible again, not permanently hidden. |
| **Cross-side warp-out** | Alpha watcher + Bravo departer, same sector; on transit the Alpha watcher (which had the Bravo ship via P369 identity + 176 telemetry) latches it `IsGone`. |
| **Dock-out + undock-return** (reverse) | Watcher + docker (same side, same sector); the docker is slid into a friendly station's `DockRadius`, docks → watcher latches `IsGone`; then `FM_C_DOCKED` undock → telemetry resumes → watcher clears `IsGone`. |

Combined these reproduce the exact reported playtest scenario (a same-side teammate warps
while the other stays and watches) and its reverse, plus docking and undocking, plus the
cross-side pair — for both the "leaves view" and "comes back into view" directions.

### Notable finding corrected during verification
The first client cut cleared `IsGone` on `SingleShipUpdate`. The same-side live warp test
immediately caught the flaw: `BroadcastShipStates` sends same-side teammate `SingleShipUpdate`
to a peer **regardless of the teammate's sector** (P85), so the warped-away teammate kept
arriving at the watcher and un-hiding itself. Switched the "return" clearing to
`HeavyShipsUpdate(176)` (strictly the viewer's own cluster) + `SetCluster` + `ExportShip`;
added the deterministic regression test pinning that a cross-sector `SingleShipUpdate` does
NOT clear `IsGone`.

---

## Build & test

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full `dotnet test` suite run **2×**: only the pre-existing documented live-wire timing
  flakes are expected; none reference the P370 symbols.
- Targeted suites green: `ShipDeleteGoneTests` (12) + `ShipDeleteGoneLiveTests` (4) = 16.

## Files changed

- `src/Allegiance.Core/Wire/ShipDelete.cs` — `SDR_LEFTSECTOR`/`SDR_DOCKED` constants + factories.
- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastShipDeparted` helper; emitted on
  `OnShipWarped` (LEFTSECTOR) and `OnShipDocked` (DOCKED).
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — `IsGone` + `IsNotVisible`.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(ShipDelete)`, `KillShip` sets
  `IsGone`, presence clears on 176/SetCluster/ExportShip (NOT SingleShipUpdate).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — dispatch `ShipDelete`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — six render/target sites `IsDead` → `IsNotVisible`.
- `src/Allegiance.ClientCore/State/LeaderBotCommand.cs` — comment documenting the deliberate
  `IsDead` (not `IsNotVisible`) exception (docked bots must stay commandable).
- `tests/Allegiance.ClientCore.Tests/ShipDeleteGoneTests.cs` — new (12 deterministic).
- `tests/Allegiance.ClientCore.Tests/ShipDeleteGoneLiveTests.cs` — new (4 live, 2 clients each).
