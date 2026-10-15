# P452 — minefields are one-shot proximity bombs instead of native attrition fields

## Why this task exists

A gameplay-parity pass found that the port's mines behave nothing like the
original's minefields. In native, a minefield is persistent area denial. It
keeps chewing on every **moving** enemy ship for as long as the ship is
inside it. Damage scales with the ship's speed and size, and the field wears
down as it deals damage. A ship sitting still inside it takes nothing. In the
port, the first enemy ship inside a mine's radius sets off one explosion and
the mine disappears.

- **Native semantics**, `CmineIGC::HandleCollision`
  (`~/Development/Allegiance/src/Igc/mineigc.h:68-145`):
  - Hits only a `OT_ship` on a **different, non-allied side**, only after an
    arm time of `c_dtArmTime = 1.0f` (`:73-77`).
  - Only if the ship is **moving**: `a = |v|² > 1.0` (`:80-82`). A stationary
    ship is immune.
  - Computes the ship's path **inside** the field. It enters at `tCollision`
    and exits at `tExit` from the sphere-intersection quadratic with
    `r = rMe + rHim`, clamped to the tick's `tStop` (`:84-113`). The
    in-field time is `dt = tExit - tCollision`.
  - Damage: `amount = dt · speed² · Power · rHim² · c_damageMultiplier /
    (2 · rMe)`, with `c_damageMultiplier = 1/(5·5·100)` (`:116-118`).
  - Wear: `oldEndurance = Endurance · m_fraction; newEndurance = oldEndurance ·
    exp(−amount/Endurance); damage = old − new; m_fraction = new/Endurance`
    (`:120-128`). The ship takes `damage`, not `amount`.
  - The ship doesn't take it immediately. `AddMineDamage` records the
    **largest** mine hit this tick (`Igc/shipIGC.h:1998-2016`), and
    `ApplyMineDamage` (`shipIGC.h:2017-2035`, called from
    `Igc/clusterIGC.cpp:334`) applies it once through `ReceiveDamage`. It
    also adds a small `c_etMine` visual explosion at most every 0.5 s.
  - The field dies (`KillMineEvent`) when `newEndurance < 1.0f`
    (`mineigc.h:139-142`). Otherwise `SetMineStrength(m_fraction)`. Any number
    of ships can be hit by the same field in the same tick.
  - `rMe` is the mine model radius, `SetRadius(dataMineType->radius)`
    (`Igc/mineigc.cpp:106`). Confirm against the port's `DataMineTypeIGC`
    whether that is `MineRadius` (the mine-specific field, per its comment at
    `src/Allegiance.Core/Data/DataMineTypeIGC.cs:49`) or the base `Radius`,
    and cite the struct offset you rely on.
- **Port**, `Sector.ResolveMineDetonations`
  (`src/Allegiance.Core/Model/Sector.cs:2505-2562`), which describes itself at
  `src/Allegiance.Core/Sim/Mine.cs:26-40`:
  - Arm time and side gate: ok.
  - Point-in-sphere with **no velocity gate**.
  - The **first** qualifying ship sets off an `Explosion` with full `Power`
    and `MineRadius`, resolved through `ResolveExplosion`. That is an area
    blast that native mines never do.
  - The mine is removed and `MineDestroyedEvent` is raised.
- **Already in the port and correct**: `Mine.Fraction` and the exact native
  endurance-decay formula in `Mine.ReduceStrength` (`Sim/Mine.cs:171-225`,
  P96). This task can reuse that decay math directly.
- Mine deploy is already player-reachable. The `N` key sends FIRE_EXPENDABLE
  (`src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs:494`), which
  runs `Server.ShipCommands.cs:754-765` and `Ship.DeployMine`
  (`Sim/Ship.cs:3336`).

## Scope

1. **Replace the single-hit detonation with native attrition** in
   `ResolveMineDetonations`, using a per-tick discretisation of the native
   integral. For every armed mine and every enemy (different-side) living
   ship with `|v|² > 1` whose sphere overlaps the field (`rMine + rShip`,
   ship radius by the established `0.5 × HullType.Length`):
   - Compute the in-field time for this tick. Use the native quadratic
     against the ship's motion over the tick, clamped to `[0, dt]`, or a
     simpler documented equivalent if the port has no swept position. Say
     which.
   - Compute `amount` with native's exact formula and constant.
   - Decay the mine's strength with native's endurance math, sharing the
     `ReduceStrength` computation, and derive the ship's `damage`.
2. **Worst-hit-per-tick application.** Collect each ship's largest mine hit
   in the tick (with its damage type and launcher) and apply it once via
   `Ship.ReceiveDamage`, mirroring `AddMineDamage`/`ApplyMineDamage`. Don't
   create an area `Explosion`.
3. **Field lifetime.** The mine persists across hits and ships. When its
   endurance drops below 1, remove it and raise `MineDestroyedEvent` through
   the existing path. Expiry by `Lifespan` stays unchanged. Several ships in
   the same tick must all take damage.
4. Update the existing mine tests that encode the old single-hit/explosion
   behaviour (`tests/**/MineCombatTests.cs` and any sibling) to the native
   semantics. Don't delete coverage; convert it.

## Explicitly out of scope

- **Enemy-side mine visibility and scan-spotting.** That's P453, the next
  task. Leave the same-side-only mine export alone here.
- The client-side mine visual strength or thinning of the field
  (`SetMineStrength` → thing site). No wire change in this task.
- The `c_etMine` visual micro-explosion (a cosmetic follow-up).
- Mine-vs-weapon-projectile interaction and explosion-vs-mine (`ReduceStrength`
  from blasts, P96). The latter stays as is.
- Station capture's shield gate (P450), station alerts (P451) and teleport
  probes (P454).
- Named follow-ups from the same parity pass, not scoped yet: armed probes
  (gun towers), enemy-spotting reports and the in-flight turret seat switch.

## Verification

- **Tests that fail on the unfixed code first.** Record the failures in the
  report.
  - (a) A **stationary** enemy ship parked inside an armed field takes **zero**
    damage over several ticks, and the mine survives. This fails today
    because the ship detonates it.
  - (b) A moving enemy ship crossing the field takes damage matching the
    native formula within tolerance. Hand-compute the expected value in the
    test from the real `PCore001` mine type's Power/Endurance/radius.
  - (c) Two enemy ships crossing in the same tick both take damage, and the
    field survives with reduced `Fraction`.
  - (d) A faster crossing takes more damage than a slower one over the same
    path length (speed² scaling).
  - (e) Repeated crossings eventually wear the field below endurance 1, and it
    is destroyed with `MineDestroyedEvent`.
  - (f) A same-side ship takes nothing.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Bare `dotnet` is a broken snap stub; always use the full
  path.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive
  clean runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and you must name any you see.

## Deliverables

1. The change plus tests, committed on **`master`** (not a side branch, not
   pushed; Domingo pushes). Check `git branch --show-current` before
   committing.
2. Report doc `docs/reports/P452_MINEFIELD_CONTINUOUS_DAMAGE.md`. Include the
   pre-fix failing test evidence, the discretisation chosen versus native's
   swept collision, the `rMe` field decision with its struct citation, and
   the tests converted from the old semantics.
