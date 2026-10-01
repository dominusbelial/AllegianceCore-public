# P465c — weapon muzzle (hardpoint) offsets: rounds leave the guns, not the ship centre

Landing date: 2026-10-01 · Landed on `master` (not pushed).

Part 3 of the P465 split. Independent of P465 (sounds) and P465b (visuals).

## Summary

Native fires each round from its weapon hardpoint:
`myPosition = m_ship->GetPosition() + *m_pposition * shipOrientation`
(`src/Igc/weaponIGC.cpp:203`), where `m_pposition = &pht->GetWeaponPosition(mount)`
(`weaponIGC.cpp:123`) is a per-mount offset the hull type builds once at load
from its `.cvh` named frames (`src/Igc/hullTypeIGC.cpp:89-136`). The port fired
every round from the ship centre because the premise "no hull mesh" was stale —
the port already parses `.cvh` frames (`CvhReader`/`CvhTypes`) and ships carry
them as `Model.Hull` (in practice never populated in production — see below).

P465c implements the per-mount offsets faithfully:
- a pure `HullWeaponPositions.Compute(hull, cvh)` reproducing native's scale +
  frame lookup + missing-frame fallback, shared by the server sim and the P462
  client echo so the two cannot drift;
- a per-hull-type `WeaponMuzzleCache` (keyed by `ModelName`, the native
  once-per-hull-type computation) wired into the mission by the server;
- `Weapon.FireWeapon` spawning each round at `ship.Position + offset * shipOrientation`;
- the P462 own-shot local echo using the same offsets.

Rounds are server-simulated and broadcast with their real spawn position, so no
wire change is needed; the client's only touch is the local-echo visual.

## What changed (with citations)

| File | Change |
|---|---|
| `src/Allegiance.Core/Collision/HullWeaponPositions.cs` (**new**) | Pure per-mount weapon-offset computation: scale `0.5·length/radius` (`hullTypeIGC.cpp:62`), frame lookup by `Hardpoint.FrameName`, the HitTest::Load sign flip (`HitTest.cpp:796-801`), missing frame → zero (`hullTypeIGC.cpp:102,132`). |
| `src/Allegiance.Core/Collision/WeaponMuzzleCache.cs` (**new**) | Per-`ModelName` cache (analogue of `HullLandingBayCache`); injectable cvh loader, resolved once per hull type. |
| `src/Allegiance.Core/Collision/HullLandingBay.cs` | Made the shared load-time frame transform `Transform` `internal` so weapon offsets use the same flipped frame space as launch/garage frames. |
| `src/Allegiance.Core/Model/Mission.cs` | Added `WeaponMuzzles` (`WeaponMuzzleCache?`), host-attached like `Sector.LandingBays`. |
| `src/Allegiance.Core/Sim/Weapon.cs` | `FireWeapon` now spawns at `_ship.Position + muzzleOffset * shipOrientation` (`weaponIGC.cpp:203`); updated the class's "no hardpoint geometry" remark. |
| `src/Allegiance.Server/Server.cs` / `Server.Lifecycle.cs` / `Server.Lobby.cs` | Create a shared `_weaponMuzzles` cache (same `<artPath>/<modelName>.cvh` loader as landing bays), pass it through `AssembleGameMission` (incl. the game-over rebuild) and attach `mission.WeaponMuzzles`. |
| `src/Allegiance.ClientCore.MonoGame/Flight/OwnShotEcho.cs` | `OwnShotWeapon` gains a `MuzzleOffset`; `BuildRound` spawns at `ctx.Position + offset * orientation` (P462 sharing). |
| `src/Allegiance.ClientCore.MonoGame/Game1.cs` / `Game1.FlightInput.cs` | Cache per-hull muzzle offsets from the real `.cvh` via the shared `HullWeaponPositions.Compute` and feed them to the echo. |
| `tests/.../HullWeaponPositionsTests.cs` (**new**) | 7 tests: real-hull mount-0 vs native formula, missing-frame→zero, null-model→zero, fired-round spawn = pos+offset·orient, pre-fix centre characterisation, out-of-range→zero, cache keyed by model. |
| `tests/.../OwnShotEchoTests.cs` | +1: local tracer spawns at the muzzle offset, and the de-dup still matches. |

## Scale factor — `GetOriginalRadius`'s port equivalent

Native: `m_scale = 0.5f * m_data->length / r` where `r = pmhb->GetOriginalRadius()`
(`hullTypeIGC.cpp:61-62`). `GetOriginalRadius` is the `.cvh` header radius
(`HitTest::MultiHull`), which the port's `CvhReader` parses as `CvhFile.Radius`
(the "original bounding-sphere radius", `CvhTypes.cs:46-47`). So
`scale = 0.5 * length / model.Radius`, matching the existing `HullLandingBay`
`Reconstruct` (which documents the same identity at `HullLandingBay.cs:52-53`).
Documented in `HullWeaponPositions`'s remarks.

## Orientation operator semantics

Native `Vector * Orientation` (`orientation.h:190`) rotates a local vector into
world space; the port's `Vector3 * Orientation` is `Orientation.Times(v)`, the
same local→world transform (`Orientation.cs:184-192`). The muzzle offset is a
local-space vector, so `offset * shipOrientation` is the correct world offset.
Two native details reproduced exactly:

- the transform is always by the **carrier** ship's orientation — never the
  gunner's. Even when a turret gunner is assigned, native keeps
  `myPosition = GetPosition() + *m_pposition * shipOrientation`
  (`weaponIGC.cpp:203` uses `shipOrientation`, not `*pMyOrientation`);
- turret mounts (non-fixed hardpoints) use the carrier position plus the turret
  frame — `m_positionWeapons[i]` for a turret is the frame position × scale
  (`hullTypeIGC.cpp:120`), so the same formula covers both fixed and turret guns.

## P462 sharing (P462 has landed)

P462 landed at `b0b3a91b`. Its own-shot echo starts local tracers at the ship
pose with no muzzle offset (`OwnShotEcho.BuildRound`). P465c adds a
`MuzzleOffset` to `OwnShotWeapon` and makes the echo spawn at
`ctx.Position + offset * orientation`. Both paths compute the offset from the
**same** `HullWeaponPositions.Compute(hull, cvh)`: the server through
`WeaponMuzzleCache`, the client through `Game1.MuzzleOffsetsFor` (which loads
the same `<artPath>/<modelName>.cvh`). A single shared computation means the two
paths cannot drift. The de-dup rule is unaffected (it matches by type +
proximity radius + window, not exact position) — verified by a new
`OwnShotEchoTests.LocalRound_SpawnsAtMuzzleOffset_NotShipCentre` test.

## Missing-frame fallback

A hardpoint whose `.cvh` frame is absent yields a zero offset (native
`pfd ? position·scale : Vector::GetZero()`, `hullTypeIGC.cpp:102,132`), so that
mount keeps firing from the ship centre — verified by
`Missing_frame_falls_back_to_zero_even_with_a_model` and `Null_model_yields_all_zero_offsets`.

## Fail-first recording (spawn = centre on unfixed code)

Before P465c, `FireWeapon` was literally `myPosition = _ship.Position` with the
comment "+ weapon muzzle offset (none: no hull mesh)". So on the pre-fix code a
fired round's spawn equalled the ship centre. That premise is pinned three ways:
- the existing `CombatWeaponFiringTests.Projectile_travels_along_heading_at_weapon_speed`
  asserts `proj.Position == ship.Position` (centre) for a mission with no cache;
- the new `Without_a_wired_cache_round_spawns_at_ship_centre` characterises the
  same centre behaviour for a bare mission;
- the new `Fired_round_spawns_at_ship_plus_mount_offset_rotated_by_orientation`
  asserts `proj.Position == ship.Position + offset·orientation` **once a cache is
  wired** — the fix.

## Live proof — rounds leave a multi-gun hull at separate points

Harness: `.verify_p465c/` (real `PCore001.igc` + real `.cvh`, real
`WeaponMuzzleCache` + `Weapon.FireWeapon`, headless). Real multi-gun hull chosen
by scanning the core: **Adv Fighter** (`quizfigv2`, length 18.9, 3 max weapons).

```
[proof] mount 0 frame='fwepemt' fixed=True offset=(0, -1.570598, -9.77261)
[proof] mount 1 frame='lwepemt' fixed=True offset=(-0.6980435, -1.0470654, -9.423588)
[proof] ship at (100, 0, 200), identity orientation
[proof] 2 round(s) fired
[proof]   spawn=(100, -1.570598, 190.22739)  d=(0, -1.570598, -9.772614)   ← mount 0
[proof]   spawn=(99.301956, -1.0470654, 190.57642)  d=(-0.6980438, -1.0470654, -9.423584)  ← mount 1
[proof] rounds leave DISTINCT points (not the centre): True
```

Both rounds spawn ~9.4–9.8 units forward of the centre (−z, the identity nose),
offset sideways/down per their hardpoints, at two clearly distinct points. Full
log at `.verify_p465c/p465c_proof.log`.

## Out of scope / named follow-ups

- **Missile/magazine muzzle** (`Sim/Ship.cs:3120`) and **chaff frame**
  (`hullTypeIGC.cpp:83-86`) are untouched and remain follow-ups. They do NOT
  fall out of the same helper trivially: `Ship.cs:3120` is a separate launch
  path (`DataMissileTypeIGC`) with its own frame (`misl`, etc.), and the chaff
  frame is a single hull-level offset (`m_chaff`), not a per-mount array — both
  deserve their own faithful ports rather than reuse of the weapon helper.
- **Cockpit camera placement** from the `cockpt` frame (`hullTypeIGC.cpp:80-82`)
  — separate follow-up, untouched here.
- **Chaff mount-mask decode suspicion** (P435): reading the same hull records,
  no new evidence emerged this task; `CanMountPart`/`GetPartMask` still match
  native exactly, and the `PmEquipment[0]` / `LauncherPartMask` decode remains
  the suspected culprit. Not investigated here — explicitly out of scope.

## Build & test

- `/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors**.
- New tests:
  - `HullWeaponPositionsTests` — **7/7**.
  - `OwnShotEchoTests` (incl. +1 P465c) — **12/12**.
- Full-suite status (multiple runs on this machine):
  - `Allegiance.Core.Tests`: 8 failures in every run, all **pre-existing
    environmental live/socket tests** (`ConquestTerritoryLiveTests`,
    `GameOverPlayersWireTests`, `LiveTurretGunnerBindingTests`,
    `NpcShipFoundationLiveTests`, `WarpBombTests`) — a pristine checkout
    (`git stash -u`) reproduces an overlapping set of 9 live-test failures, so
    none is caused by P465c (no failing test references any P465c symbol). My
    7 new tests pass green in the same runs.
  - `Allegiance.ClientCore.MonoGame.Tests` — **1012/1012** (incl. the +1 P465c).
  - `Allegiance.Lobby.Tests` — **12/12**.
  - `Allegiance.ClientCore.Tests` — 2-3 failures, all the documented
    `JoinFlowLiveTests` / `ShipDeleteGoneLiveTests` live flakes (named in
    P462's report); P465c touches no ClientCore path.
- Net: no P465c regression anywhere; the residual full-suite failures are the
  same environmental live-test flakes this machine exhibits on a pristine tree.
