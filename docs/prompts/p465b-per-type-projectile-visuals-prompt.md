# P465b — per-type projectile visuals (colour, width, texture/model)

Part 2 of the P465 split. It is independent of P465 and P465c.

## Why this task exists

2026-09-29 gameplay-parity pass. Native draws each projectile type from its
own data. Every `DataProjectileTypeIGC` carries:
- `Color` (RGBA)
- `Radius`
- `Rotation`
- `ModelName`
- `TextureName`
- `Width`
- `Directional`

The port decodes all of them (`src/Allegiance.Core/Data/DataProjectileTypeIGC.cs:13-29`).
Find native's projectile rendering and cite it: `ThingSite` for
`OT_projectile`, in `WinTrek/trekigc.cpp` and the `effect/` bolt/particle
code.

The port draws every projectile as one amber tracer:
- `ProjectileTint = (1.0, 0.84, 0.32)` and `ProjectileWidth = 0.35`
  (`src/Allegiance.ClientCore.MonoGame/Game1.cs:235,267`)
- used at `Game1.Rendering.cs:585-654`

Its comment says per-type colour is "not reachable client-side … no static
core". That is stale. The client now loads PCore001 (`Game1.Lifecycle.cs:208`
→ `Rendering/ShopCatalog.cs:57`, `StaticCore.ProjectileTypes`). Every
`ProjectileEntity` already carries `ProjectileTypeId`
(`State/ProjectileEntity.cs:28`).

## Scope

1. Look up `ProjectileTypeId` in the loaded core, cached once.
2. Draw each bolt with its type's real `Color`, `Width` and `Radius`, and
   length by speed as today.
3. If a real `TextureName` exists in the Artwork folder and native uses it
   for bolts, texture the bolt with it. Otherwise document why the flat
   tinted bolt stays.
4. Keep the current amber tracer as the fallback for unknown types or a
   missing core.
5. Update the stale comment.

## Explicitly out of scope

- Sounds (P465), muzzle offsets (P465c), and missile and expendable models,
  which already have their own model sources.
- Any server or wire change.

## Verification

- Unit tests on a pure `ProjectileStyle.For(typeId, core)` helper: real
  PCore001 types map to their real colour and width, and an unknown type
  falls back. These fail first because the helper doesn't exist yet;
  record that.
- A live screenshot showing two different weapons with visibly different
  bolts.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Commit on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P465B_PER_TYPE_PROJECTILE_VISUALS.md`.
