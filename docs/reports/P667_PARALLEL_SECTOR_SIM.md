# P667 — Intra-sector parallel simulation (`sim.parallelSectorCells`)

Status: code + determinism gate + benchmark landed on `master`, not pushed.
Build: whole-solution 0 warnings / 0 errors. Suite: see the 2× run numbers at
the bottom (measured as-run).

---

## 1. TL;DR

The 200v200 limit is **Cliff B — the crowded single sector** (P631 §2.3): ~387 bots
in one sector drove `step:update` to ~35 ms mean / 56–63 ms p95, dominated by
`sector:1052` (Alderan), with `sim.combat` only 8–9 ms of it (clash38). The lever
is **intra-sector parallel simulation with a deterministic merge**, flagged
`sim.parallelSectorCells` (env `ALLEGIANCE_SIM_PARALLEL_CELLS`), **default OFF**.

This lands that phase-6 flag. It parallelises the **collision-detection phase**
of every detector-based sector pass (combat, ship-asteroid, ship-ship,
ship-station) **and** the read-only qualification scan of the rescue pass, merging
results deterministically so the tick is **byte-identical** to the serial path,
for any worker count (proven by test at threads 1/2/4/8 and flag off vs on).

Measured on this workstation (Intel Core Ultra 9 285HX, 24 cores, 183 GiB RAM)
with a synthetic crowded single sector:

| ships | `step:update` OFF | `step:update` ON (t=8) | ratio |
|---|---|---|---|
| 400 (≈ clash38 scale) | mean 6.93 ms / p95 8.87 | mean 3.30 ms / p95 4.32 | **2.1×** |
| 700 (worst-case ultra-dense) | mean 15.07 ms / p95 21.34 | mean 6.21 ms / p95 7.45 | **2.4×** |

The dominant serial buckets both collapse: `sim.collision` 5.53→2.85 ms and
`sim.rescue` 7.28→1.66 ms at 700 ships. That is the same shape P631 estimated
(~35→≤15 ms is a 2.3× requirement; we exceed it locally on a worse-than-221
density).

---

## 2. Scope-1 deliverable: the profile — where the crowded sector's time goes

Reproduced locally with a synthetic single-sector crowd (real `.cvh` convex hulls
so the detector runs GJK; dense near-static cloud so the O(n²) collision/rescue
phases stay hot, plus an ordnance salvo so combat runs). `tools/P667Bench`
records the built-in per-bucket profiler directly (`TickProfiler.Snapshot`).

Serial bucket means at 700 ships (the worst-case density that stresses every
pairwise pass):

| bucket | serial mean ms | what it is |
|---|---|---|
| `sector:1051` (whole update) | 15.04 | the crowded sector's full tick |
| `sim.rescue` | 7.28 | O(n²) same-side lifepod↔rescue proximity scan (**not a detector**) |
| `sim.collision` | 5.53 | the three detector-based elastic passes (ship-ship/asteroid/station) |
| `sim.combat` | 0.65 | projectile/missile↔target detector pass (small here: no sustained fire) |
| `sim.visibility` | 0.50 | cross-side SeenBySide sweep (throttled every 10 passes) |
| everything else | ≲0.1 | warps, docking, mining, mines, economy, navigation, station regen |

Reading: the crowded sector's cost is almost entirely the **pairwise proximity /
collision detection**, not ship movement (the O(n) model loop is negligible once
counting the profiler attribution) and not resolution (actual hit application is
a small fraction). Two of the three heavy buckets (`sim.collision` + `sim.combat`)
go through `CollisionDetector.Detect`; the third (`sim.rescue`) is an
unaccelerated O(n²) scan.

`sim.combat` reads low only because the synthetic workload doesn't sustain gunfire
(clash38's was 8–9 ms from continuous fire). That pass is detector-based and gets
the same parallel treatment; on a real battle its cost scales down with the same
factor.

---

## 3. The design

### 3.1 Why detection is safe to parallelise (the determinism argument)

Every detector-based pass in `Sector.Update` has the same two-phase shape:

```
build colliders → detector.Detect(dt) → [ordered events] → serial resolve loop
```

`CollisionDetector.Detect` (`src/Allegiance.Core/Collision/CollisionDetector.cs`) is:

1. `SetStep` per collider — writes **only that collider's own** swept AABB
   (independent per collider);
2. `BroadPhase.OverlappingPairs` — reads bounds, yields the deterministic
   index-ordered candidate pair set;
3. `NarrowPhase.TestPair` — a **pure function** of the two colliders' state + `dt`
   (returns a `CollisionTest`; never writes shared state);
4. `CollisionQueue.Sort` — a **total deterministic order** by
   `(TCollision, A.Ordinal, B.Ordinal, HtsA*8+HtsB)`, independent of insertion
   order (and pair keys are unique, so even an unstable sort cannot reorder).

Because steps 1–3 read only per-collider state fixed before the phase, and step 4
fixes the final order regardless of the order events were discovered, **any**
schedule of the pair tests — serial, or across any number of workers — produces
the **same ordered event list**. The serial resolve loops then consume that list
unchanged, so the tick output is byte-identical. This is why the result is
independent of thread count *and* of flag off vs on.

The rescue pass (`ResolveRescueEvents`) is the same pattern under the hood: the
expensive O(n²) **qualification** (side / lifepod↔rescue capability / proximity)
is read-only and embarrassingly parallel; the only state mutation is the
one-rescue-per-ship `done` latch + `_pendingRescues` accumulation. The parallel
branch collects the qualifying `(i,j)` pairs on workers, sorts them back into the
exact serial scan order (ascending `i`, then `j`), then runs the latch serially in
that order — reproducing serial exactly. Ship positions are never mutated during
qualification (rescues only latch and drain later), so the concurrent reads are
safe.

### 3.2 What is parallelised (and what is deliberately not)

**Parallelised (behind the flag):**

| pass | method | parallelised part |
|---|---|---|
| combat | `ResolveCombatCollisions` | `detector.Detect` broad/narrow pair testing |
| ship↔asteroid | `ResolveShipAsteroidCollisions` | same |
| ship↔ship | `ResolveShipShipCollisions` | same |
| ship↔station | `ResolveShipStationCollisions` | same |
| rescue | `ResolveRescueEvents` | read-only O(n²) qualification scan |

**Deliberately serial (determinism / correctness):** the resolve/apply loops of
every pass (they mutate ship health, kill and remove models, fire site events and
explosions in a strict order — the source of gameplay determinism), the O(n)
per-model `Update` movement loop (models self-terminate and spawn into the shared
`_models` list), and every other proximity pass whose per-tick cost is negligible
(<0.1 ms) — parallelising them would add merge complexity for no measured gain.

The flag gate is a new code path selected by `ParallelSectorSim.Enabled`; the
serial path's structure is untouched, so the later `integ3` merge (P629/P651/P653d)
stays small.

### 3.3 Configuration

- Config (JSON `server.json`): `sim.parallelSectorCells` (bool, default false) and
  `sim.parallelSectorCellThreads` (int, optional; default ≈ half the logical
  processors, clamped to `[1, ProcessorCount]`).
- Env override: `ALLEGIANCE_SIM_PARALLEL_CELLS` (env wins), read at boot.
- Boot print + profiler-header label stamp the active value so a measurement run
  is never mistaken for serial.
- The Core `ParallelSectorSim` static holder (`src/Allegiance.Core/Sim/
  ParallelSectorSim.cs`) lets `Sector.Update` consult the flag without a
  `Allegiance.Server` dependency; tests set it directly.

---

## 4. The determinism gate (mandatory) — proof

`tests/Allegiance.Core.Tests/P667ParallelSectorSimDeterminismTests.cs` builds a
crowded single-sector battle (160 ships on two sides with closing velocities, a
salvo of projectiles for combat, **and** 24 lifepod↔rescue clusters so the rescue
latch actually accepts rescues), runs it for 140 ticks with the flag **OFF** and
**ON at worker counts 1 / 2 / 4 / 8**, and captures a **per-tick SHA-256 digest of
the whole world**: every ship's id/position/velocity/fraction/shield/energy/ammo/
liveness/dock-latch, every projectile/missile's position/velocity, every
asteroid's position+ore, every station's center+armor/shield, plus the raised
kill/damage/missile-destroy host-event tallies. It asserts the **five digest
sequences are byte-identical**.

Result (as-run): **140 ticks × 160 ships — serial == parallel (t=1/2/4/8), all
tick digests byte-identical.** This is the flag-off-vs-on *and*
across-thread-count gate. No float-reduction step was relaxed — the parallel path
produces the exact same floats because detection never mutates sim state and the
merge is a pure ordering fix.

---

## 5. Measurement (as-run, this machine)

Machine: Intel Core Ultra 9 285HX, 24 cores / 24 threads, 183 GiB RAM
(the 221 rig is an 8C/16T Ryzen 9 6900HX; the local ratio is representative, the
absolute ms will differ by core count). Synthetic single-sector crowd, real hulls.
OFF vs ON at 8 workers:

| ships | OFF mean / p95 | ON mean / p95 | mean ratio |
|---|---|---|---|
| 400 | 6.93 / 8.87 | 3.30 / 4.32 | 2.1× |
| 700 | 15.07 / 21.34 | 6.21 / 7.45 | 2.4× |

Per-bucket at 700 ships (serial → parallel): `sim.collision` 5.53→2.85,
`sim.rescue` 7.28→1.66, `sim.combat` 0.65→0.10; tick rate rose from ~66 ticks/2.6 s
window to ~160 ticks/2.6 s (i.e. wall time per tick roughly halved) at the same
ships. Reproduce: `ALLEGIANCE_ARTWORK_PATH=<art> dotnet run --project
tools/P667Bench -- <ships>` (tool is dev-only, not in the solution).

---

## 6. Recommended default

**Keep `sim.parallelSectorCells` OFF by default for now.** The parallel detection
is byte-identical (proven) and cost-free when off, but it adds worker-thread
contention to the sim thread that must be validated at the baseline scale
(200v200, `showcase`, Alderan) on 221 before flipping the default — per the
project's A/B-then-flip convention (P603/P604). Operators wanting the win can opt
in now; a follow-up measures 221 and, if the A/B is clean, defaults it on. The
determinism gate makes flipping it back trivial.

---

## 7. Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| One serial tick per sector (all ships + passes on one thread) | `clusterIGC.cpp:97,241-253` | **era constraint** (single-threaded 2000s server) | Keep the *intent* (a deterministic, order-fixed tick) but run the detection phase across bounded workers with a deterministic merge — sizing for today's multicore. |
| Collision `SetBB`/`HitTest::Collide` | `HitTest.cpp:14-206` | design (algorithm) | Unchanged — still the faithful P621/P621b port; only *where* the pair tests run is changed. |
| Deterministic tick (same seed → same output) | `Server.Lifecycle.cs:1138` "Deterministic and fast" | design (replays/tests) | Preserved — the whole point of the merge. |

No gameplay rule was changed; this is purely an execution-site optimisation of the
already-ported detection algorithms.

---

## 8. Modern alternative considered

- **Native:** one thread does all pair tests every tick (O(V²) worst case).
- **Modern options:** (a) a uniform-grid / BVH spatial partition feeding a
  fixed-order deterministic merge (the classic data-parallel game-sim pattern);
  (b) a lock-free per-cell worker pool; (c) a Burst/Job-system style DOTS port.
- **What we chose:** option (a)'s spirit, realised with `Parallel.For` over the
  **already-existing** deterministic broad-phase candidate list + the
  deterministic `CollisionQueue.Sort`. It needs no new spatial acceleration (the
  broad phase already emits the exact candidate set), touches no gameplay logic,
  and keeps the determinism argument airtight because detection is pure and the
  merge is an ordering fix. A dedicated worker pool / job system was rejected for
  now: `Parallel.For` + a `ConcurrentBag` + a deterministic sort gives the win
  with far less merge surface, which is what this flag-gated phase requires.
  The deeper per-sector thread/warp-hand-off work (P631 ph.7) and the Rust sim
  core (P631 §4.i) remain the documented horizons.

---

## 9. Files

- `src/Allegiance.Core/Sim/ParallelSectorSim.cs` — new static flag holder.
- `src/Allegiance.Core/Collision/CollisionDetector.cs` — `Detect(dt, parallel, threads)`.
- `src/Allegiance.Core/Model/Sector.cs` — route the 4 detector passes + rescue
  through the flag; parallel rescue qualification.
- `src/Allegiance.Core/Profiling/TickProfiler.cs` — public `Snapshot()` (diagnostic).
- `src/Allegiance.Server/ServerConfig.cs` — `SimConfig` (`sim.parallelSectorCells`).
- `src/Allegiance.Server/Server.Lifecycle.cs` — boot resolution + env + label.
- `tests/Allegiance.Core.Tests/P667ParallelSectorSimDeterminismTests.cs` — the gate.
- `tools/P667Bench/` — dev-only benchmark (not in the solution).

## 10. Verification (as-run)

- Determinism gate: PASS (off vs on, t=1/2/4/8, byte-identical).
- Combat (flag ON, t=4): kills > 0, no exceptions.
- Whole-solution build: 0 warnings / 0 errors (committed tree).
- Suite (2× `scripts/run-tests.sh`, as-run, final committed tree):
  - Run 1: Lobby 12/12, MonoGame 1977 pass/3 skip, Core 4154/4154 clean.
    ClientCore.MonoGame-Core.Tests clean; **ClientCore 1 FAIL**
    (`SessionLossLiveTests.EstablishedSession_ServerKilled…` — documented flake,
    TEST_FLAKES.md:201; passes 3/3 isolated).
  - Run 2: Lobby/MonoGame/ClientCore-MonoGame clean; **Core 4 FAIL** +
    **ClientCore 1 FAIL**: `StationUnderAttackAlertLiveTests.{Sub_30_percent,
    Defending_side}` (family at TEST_FLAKES.md:104/198; new explicit entry added),
    `LiveRipcordTests.Re_target_activates_to_requester_only` (ripcord family),
    `LiveTreasurePickupTests.Rescue_ship_picking_cash…` (TEST_FLAKES.md:69),
    `ThreePeerSeededSessionTests.SeedThreePeers…` (TEST_FLAKES.md:48). All are
    documented live-network load flakes; none is in code touched by P667 (the
    flag defaults OFF and the serial path is byte-identical), and the affected
    classes pass ≥2/3 in isolation.
  - Net: 2 runs, both affected only by already-documented live-wire flakes; no
    regression attributable to P667.
