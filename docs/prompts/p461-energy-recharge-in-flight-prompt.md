# P461 — ship energy never recharges in flight

## Why this task exists

This came out of the 2026-09-29 gameplay-parity pass (weapons feel). The
coordinator spot-verified it.

- **Native recharges every tick.** `CshipIGC::Update` does
  `m_energy += m_myHullType.GetRechargeRate() * dt;` and clamps it to
  `[0, GetMaxEnergy()]` (`~/Development/Allegiance/src/Igc/shipIGC.cpp:295-303`,
  guarded by `if ((rechargeRate > 0) || (m_energy != 0))`). The recharge
  runs **before** the parts update: cloak first ("Give the cloak the first
  dibs on energy", `shipIGC.cpp:306-310`), then weapons (`:312-340`).
- **The port never recharges.** `src/Allegiance.Core/Sim/Ship.cs` has no
  recharge at all. Here is every write to `Energy`:
  - spawn fill: `Ship.cs:168`
  - energy powerup: `Ship.cs:349`
  - parent copy: `Ship.cs:1990`
  - station refit: `Ship.cs:3778`
  - cloak drain: `Ship.cs:6247`
  - weapon spend: `Weapon.cs:346`
  - server refits: `Server.Docking.cs:577,1132`, `Server.ShipCommands.cs:1911`

  `HullType.RechargeRate` (`Data/DataHullTypeIGC.cs:36,348`) is read only
  inside `Weapon.FireWeapon`'s energy-deficit math (`Weapon.cs:206-207,229,264,324`).
  That math already *assumes* the ship recharges, but nothing ever does.
- **Effect in play:** energy-per-shot guns drain the pool and stay empty
  until the ship docks, and the cloak's drain is permanent too. This is a
  prime suspect in "weapons don't feel as good as the original."

## Scope

1. In `Ship.OnUpdate` (`Ship.cs:6456`), add the native recharge step
   **before** `UpdateCloak((float)dt)` (`Ship.cs:~6520`), to keep native's
   cloak-then-weapons order:
   - Add `RechargeRate * dt` to `Energy`.
   - Clamp to `[0, HullType.MaxEnergy]`.
   - Use the same `(rechargeRate > 0 || energy != 0)` guard.
   - Cite `shipIGC.cpp:295-303` in the comment, in the style of the
     neighbouring P269/P389/P412 comments.
2. Use the base `HullType.MaxEnergy` as the ceiling. Do not add the side
   `c_gaMaxEnergy` multiplier (`shipIGC.cpp:4563`); P467b owns that.
3. Confirm that a lifepod, a docked ship and a boarded turret gunner behave
   as native does. Native's `Update` early-outs apply the same way. Note
   what you found in the report.
4. Confirm the HUD energy gauge (`HudRenderer.cs:272,372`) visibly
   recovers. It's driven by the server's energy fraction, so no client
   change is expected. If it doesn't recover, find out why before changing
   anything.

## Explicitly out of scope

- Side global-attribute scaling of max energy (P467b).
- Any change to `Weapon.FireWeapon`'s math. It's a correct port and
  becomes right once recharge exists.
- Named follow-ups:
  - **out-of-bounds hull damage** (`shipIGC.cpp:385-403`)
  - **crowded-sector damage** (`shipIGC.cpp:405-425`)

  Both are real missing branches of the same native `Update`. Don't
  port them here.

## Verification

- A unit test in the existing Core sim test style:
  1. A ship with an energy-weapon hull fires until `Energy` is near 0.
  2. Advance ticks with no firing.
  3. Assert that `Energy` rose by `RechargeRate·Δt` and clamps at
     `MaxEnergy`.

  It **must fail on unfixed code first**. Record that in the report.
- A cloak test: an active cloak on a ship whose recharge is at least the
  cloak's drain does not end at 0 energy.
- Whole-solution build `/home/domingo/.dotnet/dotnet build` → 0 warnings,
  0 errors. Bare `dotnet` is a broken snap stub on this box.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable; name any
  you see.

## Deliverables

1. The fix and tests, committed on **`master`**. Don't push; Domingo pushes.
   Check `git branch --show-current` before committing.
2. Report `docs/reports/P461_ENERGY_RECHARGE_IN_FLIGHT.md`: the pre-fix
   failing test evidence, the placement relative to cloak and weapons, and
   the lifepod/docked/gunner findings.
