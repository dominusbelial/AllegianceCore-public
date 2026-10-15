# P491 — armed probes (gun towers) actually fire

## Why this task exists

The shop sells armed probes, and they do nothing. `src/Allegiance.Core/Sim/Probe.cs:39-45`
explicitly defers "autonomous weapon firing" (native `Igc/probeigc.cpp:352-528`),
and the wire codec carries `otTarget`/`oidTarget` without acting on them
(`ExportProbe.cs:32-40`). P479 (`docs/reports/P479_SIDE_GA_RIPCORD_PROBES.md`
§"Armed probes still don't fire") already wired the side-GA scaling into the
probe-projectile seam, ready for this task. Native is at `~/Development/Allegiance/src/`.

Native's loop (`probeigc.cpp` ~352-528) works like this:
- target selection with `ValidTarget`/`GetTarget`;
- a fire-rate gate (`m_nextFire < now`);
- lifespan from `GetProjectileLifespan()`;
- speed is `m_projectileType->GetSpeed()`, times `c_gaSpeedAmmo` when the
  probe has ammo (`:366-368`);
- an `accuracy` dispersion;
- lead aiming;
- ammo decrement.

## Scope

1. Port the targeting-and-firing sequence into `Probe`, faithfully:
   target validity and selection, rate, dispersion, lead, ammo, and the
   side GA via P479's seam. Spawn real projectiles through the same server
   projectile path ships use, so damage, hit broadcast and cross-side
   visibility (P375) come for free.
2. Broadcast the probe's current target on the existing `ExportProbe`
   `otTarget`/`oidTarget` fields where native updates them. Check whether
   clients need anything else to render the shots.
3. A live wire test: deploy a real armed probe from PCore001 (via the P435
   dispenser path), fly an enemy into range, and assert that projectiles
   spawn, hit and do damage, and that ammo decrements.

## Explicitly out of scope

- Teleport probes (P454) and probe visibility rules.
- New probe types.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P491_ARMED_PROBES_FIRE.md`.
