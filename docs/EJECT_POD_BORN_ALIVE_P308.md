# EJECT_POD_BORN_ALIVE_P308 — an ejected lifepod is born ALIVE, not dead

Branch `master`. Found by a read-only `zai`/glm-5.3 review of
`src/Allegiance.Server/Server.cs` (finding C2 of
`allegiance-server-review-glm53.md` in the Domingo-Llm-Wiki). A genuinely new
finding: neither the kimi-k3 nor the jackod-9b-coder review caught it.

## The defect

`Server.EjectSurvivor` (Server.cs:12212) turns the dying ship's hull into a
drifting lifepod with `victim.ChangeHull(pht)` but never restores the hull
fraction. Three facts compound into a permanent bug:

1. `Ship.ReceiveDamage` sets `_fraction = 0.0f` **before** raising
   `KillShipEvent` (Ship.cs:3131-3132, `oldFraction > 0` guard; port of
   `shipIGC.cpp:1316`) — so `EjectSurvivor` always runs on an already-zero-
   fraction ship.
2. `Ship.ChangeHull` (Ship.cs:203) is the port of `CshipIGC::SetBaseHullType`
   (`shipIGC.cpp:1352`), which is documented **not** to reset `m_fraction`
   ("a lifepod is still the dead ship", Ship.cs:194-196; native `shipIGC.cpp:1352-1420`
   never touches `m_fraction`).
3. `Ship.Dead` (Ship.cs:317) is simply `_fraction <= 0.0f` (port of the native
   `GetFraction() <= 0` hull-death check).

So every ejected pod was born `Dead == true` and nothing ever raised its
fraction — the pod was `Dead` forever. That silently disabled everything the
eject mechanic exists for:

- `Sector.ResolveRescueEvents` requires `!s.Dead` (Sector.cs:772) — **rescue
  could never fire on an ejected pod**, the one object it was built for.
- `Sector.GetShipUpdateCandidates` skips dead ships (Sector.cs:410) — no other
  player ever saw the pod in `HEAVY_SHIPS_UPDATE`.
- The combat/mine/explosion passes all skip dead ships (Sector.cs:2069,2174) —
  the pod was invulnerable and could never be destroyed either.
- `Ship.OnUpdate` has no dead-gate, so the pod *did* keep physically drifting —
  a true zombie object, invisible and invulnerable, surviving the P279 oxygen
  countdown then oxygen-dying into the P307 respawn path.

## Why native doesn't exhibit this despite never naming an eject fraction

Grepping native's `KillShipEvent` eject branch (`FedSrv.CPP:11917-12021`):
after `pship->SetBaseHullType(pht)` (FedSrv.CPP:11942) there is **no**
`SetFraction` — native also leaves `m_fraction == 0` on an ejected pod. Native
avoids the "zombie" outcome for a different reason: its rescue is
**collision-based with no dead gate**.

- Rescue detection is `CshipIGC::HandleCollision` case `OT_ship`
  (`shipIGC.cpp:1023-1040`): a friendly ship whose hull has `c_habmLifepod`
  is handed to `RescueShipEvent` purely on collision with a `c_habmRescue`
  ship — there is **no `GetFraction() > 0` predicate** at the rescue site
  (`shipIGC.cpp:1033-1036`). A "dead-fraction" pod is still rescued on bump.
- The port, however, uses a per-tick proximity pass (`Sector.ResolveRescueEvents`)
  that gates on `!s.Dead`, and additionally drops dead ships from
  `GetShipUpdateCandidates` and the combat passes.

Because the port's gates all key on `Dead` (fraction), the faithful fix is to
make the freshly-ejected pod non-dead at the eject site — the single place the
task scope allows, leaving every downstream gate untouched.

## The fix and its native justification for the value

`Ship.cs:318-330` adds `Ship.SetFraction(float value)` — the direct port of
`CshipIGC::SetFraction(float)` (`shipIGC.h:311-318`, `m_fraction = newVal`;
the native also mirrors `m_fractionLastOrder` and calls `RemoveDamage`, neither
of which this port models — fraction is the sole hull-integrity state here,
exactly how `RepairAndRefuel` (Ship.cs:3200) and the respawn slice already
mutate `_fraction`).

`Server.EjectSurvivor` (Server.cs:12241→12259), immediately after
`victim.ChangeHull(pht)`, calls **`victim.SetFraction(1.0f)`** — the pod is
restored to full hull. Value justification: native never names an eject
fraction (above), so the port does not invent a partial number; **full hull is
native's own canonical "make a ship whole" value**, used everywhere a ship
returns to a usable state — `SetFraction(1.0f)` at relaunch/respawn
(`FedSrv.CPP:12759, 12950, 12954`) and `SetFraction(1.0f)` is the inline
definition itself. A freshly ejected, intact pod that must be visible,
targetable, destroyable (to deny rescue) and rescueable is precisely a "whole"
ship.

## Live proof (real in-process server, real bots)

`LiveInSpaceEjectPodAliveTests` (tests/Allegiance.Core.Tests/
LiveInSpaceEjectPodAliveTests.cs) — two real loopback bots, both on side 0 in
a no-station fly sector: the victim (clean fighter clone, no lifepod/rescue
bits) and the rescuer (same base hull cloned with exactly `c_habmRescue`).
The victim takes lethal damage from the rescuer → real eject. Then:

1. **Born alive** — `Assert.False(victimShip.Dead)` and
   `Assert.Equal(1.0f, victimShip.Fraction, 4)` the instant the pod exists
   (hull already swapped to the side's real Lifepod from `PCore001.igc`).
2. **Admitted to the heavy-update gate** —
   `Assert.Contains(victimShip, victimShip.Sector!.GetShipUpdateCandidates())`.
3. **Rescue really fires** — the pod drifts onto the rescuer and
   `ResolveRescueEvents` broadcasts a real `PLAYER_RESCUED`(16) to **both**
   bots with the correct rescuer/rescuee ids, and the pod is placed docked at
   the real friendly side-0 station (where before the fix nothing happened).

**Must-fail-if-removed (hard requirement, exercised):** temporarily commenting
out the `victim.SetFraction(1.0f)` in `EjectSurvivor` makes the test fail at
assertion (1), "ejected lifepod must NOT be born dead" — the pod is born
`Dead`, drops out of (2), and never satisfies (3). Reverted clean afterwards.

## Regression scope / exclusions

- **Out of scope** (unchanged): the P307 sector-membership desync (separate
  root cause), the P309 mission-stage lie, and how/when the pod *eventually*
  truly dies (P279 oxygen expiry, or being destroyed by an enemy — now actually
  possible because the combat pass admits a non-dead pod). The only change is
  the pod's birth state.
- Downstream gates (`ResolveRescueEvents`'s `!s.Dead`, `GetShipUpdateCandidates`,
  combat dead-skips) are **all untouched** — the fix lives entirely at the
  eject site, as scoped.
- Native reference source (`~/Development/Allegiance`) not modified.

## Build / tests

- Whole-solution build: **0 warnings, 0 errors**.
- Related suites green in isolation: `LiveInSpaceEjectPodAliveTests`,
  `LiveRescueTests`, `LiveInSpaceEjectTests`, `LiveLifepodOxygenTests`,
  `LiveStationRescueTests` (14/14).
- Full-suite runs: see landing record (only the documented pre-existing flakes:
  `LiveTurretGunnerBindingTests` timing, `LiveWinConditionTests.Multi_mission...`,
  intermittent `LiveCombatWireTests`).

## Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — new `SetFraction(float)`.
- `src/Allegiance.Server/Server.cs` — `EjectSurvivor` restores the pod to full
  hull after `ChangeHull`.
- `tests/Allegiance.Core.Tests/LiveInSpaceEjectPodAliveTests.cs` — new live
  proof.
- `docs/EJECT_POD_BORN_ALIVE_P308.md` — this writeup.
