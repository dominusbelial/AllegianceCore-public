# P465c — weapon muzzle (hardpoint) offsets: rounds leave the guns, not the ship centre

Part 3 of the P465 split. It is independent of P465 and P465b.

## Why this task exists

2026-09-29 gameplay-parity pass.

**Native** fires each round from its hardpoint:
`myPosition = m_ship->GetPosition() + *m_pposition * shipOrientation`
(`~/Development/Allegiance/src/Igc/weaponIGC.cpp:200`). `m_pposition` is
set as `&pht->GetWeaponPosition(mount)` (`weaponIGC.cpp:123`). The hull
builds these positions at load, in `Igc/hullTypeIGC.cpp:55-121`:
1. `pmhb = HitTest::Load(modelName)`, the `.cvh` hit-box.
2. `m_scale = 0.5 · length / pmhb->GetOriginalRadius()`.
3. For each hardpoint, `m_positionWeapons[i] = pmhb->GetFrame(hd.frameName)->position · m_scale`.
   A missing frame gives zero. Turrets also take a frame orientation.

Missiles and chaff have similar frames. The missile muzzle is a named
follow-up (see below).

**The port** fires every round from the ship centre:
- `var myPosition = _ship.Position; // + weapon muzzle offset (none: no hull mesh)`
  at `src/Allegiance.Core/Sim/Weapon.cs:231` (see also `Weapon.cs:31`).
- The same holds for missiles at `Sim/Ship.cs:3120`.

That comment's premise is stale. The port parses `.cvh` frames
(`src/Allegiance.Core/Collision/CvhReader.cs:109-110`,
`CvhTypes.cs:58-59`, `CvhFile.Frames`). Ships already carry their parsed
hull as `Model.Hull` (`Sim/Model.cs:73-75`). The server already uses cvh
frames for launch slots (`Server.Docking.cs:1107-1171`).
`Hardpoint.FrameName` is decoded (`Data/DataHullTypeIGC.cs:267,319`).

## Scope

1. Compute per-mount weapon positions exactly as native does (scale
   formula plus frame lookup by `FrameName`, zero if the frame is missing),
   once per hull type, cached. Verify `GetOriginalRadius`'s port
   equivalent in the cvh reader, or derive it, and document it.
2. Apply the offset in `Weapon.FireWeapon`:
   `myPosition = _ship.Position + weaponPosition * orientation`. Use the
   same orientation convention native uses. Check the port's
   `Vector * Orientation` operator semantics against native's.
   Turret mounts use the carrier's position plus the turret frame, as
   native does.
3. Rounds are server-simulated and broadcast with their real spawn
   position, so no client change should be needed. Confirm this against
   P462 if it has landed: its local echo must use the same offsets. If
   P462 has landed, expose the per-mount position so both paths share it.

## Explicitly out of scope

- The missile/magazine muzzle (`Ship.cs:3120`) and the chaff frame
  (`hullTypeIGC.cpp:83-86`). Name both as a follow-up unless they fall out
  of the same helper trivially, in which case say so.
- Sounds (P465) and visuals (P465b).
- Named follow-up: the **chaff mount-mask decode suspicion**. P435 found
  that chaff dispensers "fit NO hull" under `CanMountPart`
  (`docs/reports/P435_CHAFF_MINE_PROBE_DISPENSERS.md:120-131`), and the
  shop currently bypasses the gate. The port's `CanMountPart`
  (`Data/DataHullTypeIGC.cs:247-259`) matches native `CanMount`/`GetPartMask`
  (`hullTypeIGC.h:163-176,198-212`) exactly, so the likely culprit is how
  `PmEquipment[0]` or the chaff `LauncherPartMask` is decoded. This task
  reads the same hull records, so if you notice the cause, note it in the
  report. Don't fix it here.
- Cockpit camera placement from the `cockpt` frame, a separate follow-up.

## Verification

- A unit test with a real PCore001 hull and its real `.cvh`: mount 0's
  computed position equals `frame.position · 0.5·length/radius`, and a
  fired round's spawn position equals ship position + offset·orientation.
  This fails on unfixed code first (spawn = centre); record that.
- A test for the missing-frame fallback to zero.
- Live: a multi-gun hull's rounds visibly leave from separate points.
  Screenshot, or log the spawn positions.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Commit on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P465C_WEAPON_MUZZLE_OFFSETS.md`.
