# P621 — collisions use bounding SPHERES where native uses the model's real convex hulls

**Date:** 2026-10-12 · **Branch:** `master` · **Commit:** `4c18d896` (to be amended with suite numbers)

---

## The problem

Domingo, 2026-10-12, in a 195v195 on the 221 server: *"i keep ejecting because of
asteroids, out of nowhere … that did not happen in native, ever."* One of those
ejects:

```
17:00:42.716 [collision] ship 8098 x rock/base sector 1052 pos (-1284.5, 2785.8, -430.9) jiggle(41.34,0)
17:00:42.716 [eject] player ship 8098 ejected -> Lifepod ... vel (-30.3,-141.5,-1.6)
```

The damage formula was already native-faithful (`Sector.cs` ~2244-2292 mirrors
`Igc/shipIGC.cpp:930-990`: elasticity 0.75, `damage2 = |Δv|²/128`, `damage1 = 2·damage2`,
`hp2·damage1/(hp2+damage1)`). **The *when* was not.**

The port built every collider as `HitShape.Sphere` (`hull: null` in the three
collision resolver passes), whereas **native loads every model's `.cvh` collision
file and collides against the convex hulls**:

| native call | cite |
|---|---|
| `CmodelIGC::LoadCVH` → `HitTest::Create(pszFileName, …, htsDefault)` | `src/Igc/modelIGC.cpp:105-118` |
| `HitTest::Create` → `Load` (the `.cvh` read + per-name cache) | `src/_Utility/HitTest.cpp:827-863, 639-825` |
| hull-vs-hull narrow phase `HitTest::Collide` / `HullCollide` / `IntervalCollide` | `src/_Utility/HitTest.cpp:114, 921, 1045` |
| separation normal from convex-hull extremes `CshipIGC::Separate` | `src/Igc/shipIGC.cpp:514-616` |

Spheres are only native's default for effects/mines/warps (`modelIGC.cpp:121-138`).
Asteroids are lumpy and elongated, so a sphere enclosing the whole rock covers a lot
of empty space — the server registered full-speed hits where the player saw open
space. Classification: **design** (native collides against the model shape; the
sphere was a port shortcut, not an era constraint).

---

## What the port already had

The hull *machinery* existed but was never wired into the live collision passes:

- `Collision/CvhReader.cs`, `CvhTypes.cs` — parsed `.cvh` (frames, sub-hulls, radius).
- `Collision/HitShape.cs` — the `c_hts*` constants incl. `ConvexHullMin/Max`.
- `Collision/NarrowPhase.cs` — a full port of `HitTest::Collide/HullCollide/IntervalCollide/DoGilbert(GJK)/Johnson`
  (`CollisionDetectionTests` already proved hull-vs-hull detection against real corpus hulls).
- `Collision/Collider.cs` — `BoundingHull` port: hull sub-shapes, `Scale = desiredRadius/originalRadius`,
  `GetMinExtreme`/`GetMaxExtreme`/`GetCenter` (the primitives `Separate` needs).

The three live passes (`ResolveShipAsteroidCollisions`, `ResolveShipShipCollisions`,
`ResolveShipStationCollisions` in `Model/Sector.cs`) built every collider with
`hull: null` and computed the bounce normal radially.

---

## Changes

### 1. `Collision/CollisionHullCache.cs` (new)
Per-model-name `.cvh` resolver/cache. Faithful to native's module-level
`cachedMultiHulls` (`HitTest.cpp:637,646-662,810-821`) and `UTL::getFile(name, ".cvh", …)`
(`_Utility/Utility.cpp:105-133`, appends `.cvh` to the extension-less model name →
`fig05` → `fig05.cvh`). A model with no `.cvh` resolves `null` → sphere fallback,
exactly native `HitTest::Create` falling back to `htsDefault` (`HitTest.cpp:844-860`).
The parsed file is stored at file scale; each collider applies
`desiredRadius/originalRadius` at construction (`HitTest.cpp:568-581`), so one
cached file serves every differently-sized instance of a model. Attached to every
sector at mission assembly (`Server.Lifecycle.cs`), reused across game-over rebuilds
(`Server.Lobby.cs`).

### 2. `Collision/Separate.cs` (new)
Port of `CshipIGC::Separate` (`shipIGC.cpp:514-616`): derive the separating-plane
normal and displaced positions from the two bodies' **convex-hull extremes** (the
26 probe directions `shipIGC.cpp:479-512`, `GetMinExtreme/GetMaxExtreme`,
`*pNormal = directionBest·o2`, `s = distanceBest − 0.5` clearance). For a sphere
fallback pair it degenerates to the old radial behaviour (verified by the existing
collision test suites passing unchanged).

### 3. `Collision/Collider.cs`
The per-hull support geometry (vertex array + adjacency + octant extremes,
`HullSupport`) is now built **once per `CvhFile`** (`ConditionalWeakTable<CvhFile,…>`)
instead of once per collider per tick — so adding hulls to 390 ships does not
rebuild the support sets every tick.

### 4. `Model/Sector.cs` — the three resolver passes
Each collider now resolves its model's real `.cvh` (`CollisionHulls?.Resolve(name)`)
and the bounce uses `Separate.SeparateColliders` for the normal + displaced positions:

- ships: `HullFor(s.HullType.ModelName)`, desiredRadius `0.5·Length` (native
  `SetRadius(0.5f·GetLength())`, `shipIGC.cpp:1418`);
- asteroids: `HullFor(ast.Data.ModelName)`, desiredRadius `ast.Radius`;
- stations: `HullFor(st.Type.ModelName)`, desiredRadius `st.Radius`
  (native `SetRadius(GetRadius())`, `stationIGC.cpp:244`).

Spheres are retained where native uses them (ordnance/probes/mines; `modelIGC.cpp:121-138`)
— the ordnance/probe colliders in `ResolveCombatCollisions` are untouched.

### 5. Test hardening (one unrelated live test)
`NaniteThreatBonusLiveTests` located the shooter by its exact spawn origin; with
real hull ship-ship collision the friendly target thrusting into it legitimately
collides (native behaviour) and nudges it microns off-origin. Located the ship by
**id** instead (the test's purpose is nan repair-credit). Credit ratio verified
still **exactly 2.000** (bomber 1.000 / fighter 0.500) after the change.

---

## Client prediction (scope 4)

The client's own-ship simulator (`ClientCore/Flight/OwnShipSimulator.cs`, P508) is
documented as *"purely local kinematics … no server collision/bump/clamp knowledge
— a predicted ship may briefly fly through a rock until the server's SHIP_RESET
pulls it back, exactly native's behaviour."* The client does **not** predict its own
collisions/bounces, so no client change is needed — the server's hull bounce rides
real `SHIP_UPDATE`/`SHIP_RESET` to the client (proven by the existing
`Loopback_bounced_ship_velocity_and_position_reach_the_wire` test). `ClientAutopilotDodge`
is autopilot obstacle *avoidance* (sphere heuristic), unrelated to the physics bounce.

---

## Tests (failing-first)

`tests/Allegiance.Core.Tests/P621ConvexHullCollisionTests.cs`:

| test | what it proves | failing-first |
|---|---|---|
| `Cache_resolves_once_and_falls_back_null` | `.cvh` parsed once per name; missing → null (sphere fallback) | cache did not exist pre-P621 |
| `Elongated_asteroid_hulls_let_a_ship_pass_through_empty_sphere_corners` | golden geometry for **4 real `bgrnd*` rocks** (bgrnd03/51, nbgrnd03, drgrock): a ship threading the hull's empty bounding-sphere corner does NOT collide (hull) but DOES with the sphere | documents the fix (detector already supported hulls) |
| `Station_hull_lets_a_ship_pass_through_empty_sphere_corner` | same for the **`dgsup` station** model | documents the fix |
| `Resolver_bounces_off_asteroid_hull_but_not_through_empty_sphere_corner` | through the real `Sector` resolver with a `CollisionHulls` cache: ship transits the elongated rock's empty corner (no bounce, no damage) while flying INTO the hull does collide | **verified failing on the reverted sphere-only resolver** |

Measured output (golden cases are computed from the parsed `.cvh` extents):

```
P621-GOLDEN-ASTEROID[bgrnd03]: radius 150, xHalf 147.9, yHalf 77.5, zHalf 102.4 — altitude 113.8: hull=NO collision, sphere=collides.
P621-GOLDEN-ASTEROID[bgrnd51]: radius 150, xHalf 138.5, yHalf 139.9, zHalf 126.8 — altitude 138.4: hull=NO collision, sphere=collides.
P621-GOLDEN-ASTEROID[nbgrnd03]: radius 150, xHalf 150, yHalf 2.6, zHalf 3.7 — altitude 76.3: hull=NO collision, sphere=collides.
P621-GOLDEN-ASTEROID[drgrock]: radius 150, xHalf 147.9, yHalf 77.5, zHalf 102.4 — altitude 113.8: hull=NO collision, sphere=collides.
P621-RESOLVER-ASTEROID: corner: bounced=False, damaged=False, transited=True; Y=0 into-hull: damaged=True.
P621-GOLDEN-STATION: dgsup — altitude 42.5 (extent 36.1): hull=NO collision, sphere=collides.
```

`nbgrnd03` is a pancake (Y-extent 2.6 vs sphere radius 150) — a dramatic empty-corner
demonstration of the "invisible rock" bug.

**Failing-first confirmed:** with the asteroid resolver reverted to `hull: null`
(the pre-P621 code), `Resolver_bounces_off_asteroid_hull_but_not_through_empty_sphere_corner`
FAILS ("ship threading the empty Y corner must take NO collision damage") because
the sphere catches the near-miss; with the hull it passes.

---

## Full suite (2 consecutive runs, `scripts/run-tests.sh` steps)

`run-tests.sh` as a single invocation exceeds the harness foreground timeout, so its
sequential per-project steps were run individually (same order, `--no-build` on the
final code). Both runs on the final committed code.

| project | run 1 | run 2 |
|---|---|---|
| Allegiance.Lobby.Tests | 12/12 ✅ | 12/12 ✅ |
| Allegiance.ClientCore.MonoGame.Tests | 1776/1779 (3 skipped) ✅ | 1776/1779 (3 skipped) ✅ |
| Allegiance.ClientCore.Tests | 603/603 ✅ | 603/603 ✅ |
| Allegiance.Core.Tests | 3991/3997 — 6 **known load-flakes** (pass in isolation) | 3994/3997 — 3 **known load-flakes** (pass in isolation) |

The Core failures are the live-network timing flakes already ledgered in
`docs/TEST_FLAKES.md` (AutoReloadOnEmpty `:52`, P493EnemySpotting `:217`,
MineCrossSideVisibility `:184`, LiveRipcord `:182`, LiveRelaunch `:40`,
ConquestTerritory `:29`, P591d, CargoBayReload `:53-54`). **Every one passed in
isolation** (e.g. the 6 run-1 failures: 7/7 passed in 722 ms) — none are related to
this change. Whole-solution build: **0 warnings / 0 errors** (`dotnet build -m:8`).

---

## Performance — collision step cost at ~390 players

Microbenchmark (`_P621PerfProbe`, removed after capture) timing
`CollisionDetector.Detect(dt=0.05)` over the **390-ship + 134-rock** Alderan
population, hull vs pre-P621 sphere colliders:

| field | candidate pairs | sphere (before) | hull (after) | Δ |
|---|---|---|---|---|
| sparse (realistic) | 434 | 2.44 ms/tick | 3.75 ms/tick | **+1.31 ms** |
| dense melee (worst case) | 7 177 | 5.16 ms/tick | 20.08 ms/tick | +14.92 ms |

In a real sparse field (ships spread over large sectors — the typical 200v200 load)
the hull collision step costs **~3.8 ms/tick**, a **+1.3 ms/tick** overhead against
the 50 ms tick budget — negligible. The dense-melee worst case is heavier because
the hull narrow phase (GJK) runs on every candidate pair; native keeps this small
with a **spatial (KD-tree) broad phase** — the port's `BroadPhase.cs` is still an
O(n²) pairwise AABB sweep (a pre-existing simplification this task did not change),
so in a dense melee the same pairs reach the (now more expensive) narrow phase. See
"Modern alternative" below.

---

## Live proof (local server, temp worktree binaries)

Showcase `spawn_scenario` on a **local** server (m2xspiderw.igc, Omega/Plato/Alderan,
`target_sector Alderan`, size_scale 1 ≈ 30 showcase bots), observed ~110 s:

- **32 `[warp] … crossed` lines** — bots left home sectors (Omega 1053 / Plato 1060)
  and reached the target Alderan (1052) via warps 2153/2184. **Bots reach the target.** ✅
- **116 resolved collision events** (114 ship↔ship + 2 ship↔asteroid), no crashes,
  no tunnels-through-rocks:

```
[warp] ship 8125 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184   (…32 lines)
[collision] ship 8118 x asteroid 3315 sector 1052 pos (-328.8,-3556.8,168.2) jiggle(31.22,0)
[collision] ship 8119 x asteroid 3315 sector 1052 pos (3.32,-3468.5,149.3) jiggle(14.52,0)
```

The two ship↔asteroid hits carry the native-faithful jiggle (ship party only; rock jiggle 0).
The 195v195 baseline scale and the collision step's before/after cost are covered by the
benchmark above (the same 390+134 population).

---

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| Collide against the model's `.cvh` convex hulls | `modelIGC.cpp:105-118`; `HitTest.cpp:827` | **design** | uses hulls for ship/asteroid/station colliders |
| Bounce off the hull contact normal | `shipIGC.cpp:514-616` (`Separate`) | **design** | `Separate.cs` port |
| Per-model `.cvh` load cache + `.cvh` filename append | `HitTest.cpp:637,810`; `Utility.cpp:105-133` | design | `CollisionHullCache` |
| Spheres for effects/mines/warps | `modelIGC.cpp:121-138` | design | ordnance/probe colliders stay spheres |
| Collision-time ship & rock positions for damage | `shipIGC.cpp:1172-1185` | design | passes `Separate`'s positions |
| `s = distanceBest − 0.5` clearance fudge | `shipIGC.cpp:608` | design | ported verbatim |
| Projectile-vs-hull (weapon fire) | `modelIGC.cpp` Load for ships | design (separate scope) | **not changed** — combat pass still spheres (see below) |

## Modern alternative considered

**Native:** every collidable object is a sphere-or-hull with an analytic/swept test;
the narrow phase is GJK, and the broad phase is a KD tree that only visits
overlapping AABBs.

**Modern option:** a spatial broad phase (uniform grid / BVH / SAP) replacing the
port's O(n²) pairwise sweep, so the (now hull-shaped) narrow phase only touches a
local neighbourhood — the lever that would collapse the dense-melee 20 ms figure.

**What we chose:** keep the existing two-phase structure (sphere-sweep seed → hull
GJK), matching native, and keep the current O(n²) broad phase (unchanged, out of
scope). The realistic sparse-field cost is ~3.8 ms/tick and acceptable; a spatial
broad phase is flagged as the follow-up that would harden the dense-melee worst case.
The hull support sets are cached once per model so per-tick construction stays flat.

## Out of scope (reported)

- **Damage numbers** — already native-faithful (unchanged).
- **Projectile-vs-hull hit tests** — native resolves weapon fire against the target's
  hull (via the ship's `LoadCVH` body). The port's `ResolveCombatCollisions` ordnance
  pass still builds sphere colliders for ships/stations/asteroids (`hull: s.Hull` /
  null). This falls out naturally if/when `Ship.Hull` (the `Model.Hull` hook) is
  populated, and is the natural next step; it was left out of P621 to keep the
  (huge, heavily-tested) combat pass isolated.
- **Visuals** — none (server-side physics only).
