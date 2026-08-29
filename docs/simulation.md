# Simulation

Extracted from README.md 2026-08-29 (it had reached 1,096 lines).

## Collision detection — broad & narrow phase

This is where the parsed `.cvh` hulls pay off. `src/Allegiance.Core/Collision/`
now holds a detect-and-report-only pipeline (no damage, no explosions — out of
scope), traced against the C++ in **`COLLISION.md`**. Highlights:

- **`Collider`** — the `BoundingHull`/`BoundingSphere` port: position, velocity,
  orientation, the (multi-)hull, `desiredRadius/originalRadius` scale, and the
  per-step swept AABB (`HitTest.cpp:14-110`). Support functions use the parsed
  vertices and adjacency with a pre-computed octant warm start
  (`HitTest.cpp:311-357,495-549`).
- **`BroadPhase`** — deterministic swept-AABB candidate pairs (the C++ KD tree's
  candidate set; the tree's acceleration is a structural difference, documented in
  COLLISION.md Q1 — the wiki's "sweep-and-prune with KD-tree sectors" is corrected
  there).
- **`NarrowPhase`** — faithful port of `HitTest::Collide`: the analytic sphere-sweep
  closest-approach time, then `HullCollide`/`IntervalCollide` (separating-axis
  bisection) falling back to GJK/`Johnson` for hull pairs.
- **`CollisionQueue` + `CollisionDetector`** — gather once per sector step, sort by
  collision time, report. **`Model` gains an optional `Hull`** and
  `CreateCollider()`; `Ship` supplies its velocity/orientation.

Tests: `CollisionDetectionTests` — head-on analytic timing (spheres and real
`fig01` hulls at `t = 1.80633 s`, within 0.025), parallel near-miss rejection,
ship→station (`dgsup`), multi-hull (`p1_exp`, 6 sub-hulls), determinism, and
ascending-time ordering. Full suite green: **1444 tests**.


## The tick — `Mission.Update` (`CmissionIGC::Update`)

Checklist item on the road from "a runnable world" to "a world that moves": the
simulation cadence, reproduced from `CmissionIGC::Update`
(`missionigc.cpp:3127-3185`). Lives in `Model/Mission.cs`. This task
implements **the tick and only the tick** — no ships, no flight model, no
collision, no combat, no economy, no damage tracks, no networking (each
section below says what is still stubbed). The point proved is *cadence and
ordering*; objects come next.

### The increment constant — found

`c_fTimeIncrement = 0.25f` at `src/Igc/igc.h:5120` (a module-level const float,
four use sites: `missionigc.cpp:3156` plus three in `shipIGC.cpp:3273,3326,3371`).
So the "250 ms" comment is right, but the definition is real and quoted, not
assumed: **0.25**, ported as `Mission.TimeIncrement`.

### The arithmetic (reproduced exactly)

```csharp
double deltaT = now - LastUpdate;                    // missionigc.cpp:3149
int n = 1 + (int)(deltaT / TimeIncrement);           // :3156
for (int i = 1; i <= n; i++)
{
    double timeUpdate = startTime + deltaT * (i / n);// :3163
    foreach (var sector in Sectors) sector.Update(timeUpdate);
    LastUpdate = timeUpdate;                          // :3177
}
```

- **Chunks are NOT fixed 250 ms.** `n = 1 + floor(deltaT / 0.25)`, then the
  interval divides into **n even chunks of `deltaT/n`**, each *at most* the
  increment. `n` is always ≥ 1 for any `deltaT > 0`.
- **Boundary surprise reproduced and asserted:** because `(int)` truncates, at
  exactly one increment (`deltaT == 0.25`) the quotient `1.0` truncates to `1`,
  so `n == 2`, **not** 1. `n == 1` holds only for `deltaT < 0.25`. The test
  pins `{0.239 → n=1}` and `{0.250 → n=2}`.
- **Cadence:** sides update **once per `Update`**; clusters update **once per
  chunk**. All side updates precede the first cluster chunk.

### Decision: `deltaT >= 600` throws (does not clamp)

The C++ `assert (now - m_lastUpdate < 600.0f)` ("never 10 minutes at a
time", `missionigc.cpp:3148`) fails in debug and silently proceeds in release.
We **throw** `InvalidOperationException` because advancing ten minutes in one
call is always a caller bug here — there is no legitimate use to preserve — and
silently clamping would hide it behind a fabricated sim that skipped everything
between. Throwing reproduces the assert's *intent* (fail loudly) in a
language with no build-time asserts. Likewise `now < LastUpdate` (the
`assert (now >= m_lastUpdate)` at `:3129`) throws. Both are documented on the
method and covered by tests.

### The not-started path

Only `Started`/`Starting` missions run the chunked pass. Any other
`MissionStage` (`NotStarted`, `Over`) makes `LastUpdate` jump straight to `now`
(`:3180-3181`) with **zero** side and cluster ticks. `Update` starts the mission
in `NotStarted` (the C++ constructor, `missionigc.h:397`).

### Postcondition

`LastUpdate == now` on every path: the `now == LastUpdate` no-op, the
not-started jump, and the chunked pass (whose last chunk, `i == n`, reduces to
`startTime + deltaT == now` exactly). Asserted in each test; because a
fresh mission's `LastUpdate` starts at 0 and each row ticks from 0, the final
chunk lands on `now` with no floating-point drift.

### Site routing and the one deviation

The only site call the C++ tick makes is `ClusterUpdateEvent`
(`clusterIGC.cpp:357`); the port routes each sector's per-chunk update through
`IIgcSite.ClusterUpdateEvent(sector)` (Sector carries the `IClusterIgc` marker
to stand in for `CclusterIGC`). The C++ *side* update sends nothing to the site
(`sideigc.h:41`), so a recording site would see no side activity and could not
prove "all side updates precede the first cluster chunk". To make that ordering
observable we added **one** C#-only instrumentation hook,
`IIgcSite.SideUpdated(ISideIgc, double now)`, a no-op default that changes
nothing for a real host and is explicitly labelled **not part of the C++
seam** in the interface. `RecordingIgcSite` overrides it to log
`SideUpdated(Side, now)`, so the ordering test watches `SideUpdated(...)` —
`ClusterUpdateEvent(...)` through one `RecordingIgcSite.Calls` trace. This is
the sole deviation from the C++ seam and it is observational, not behavioral.

### Stubs (out of scope, documented on each)

- `Sector.Update(double)` records `UpdateCount` + `LastUpdate`; the fleet,
  collision, combat and explosion passes (`clusterIGC.cpp:97-358`) are future
  work.
- `Side.Update(double)` records `UpdateCount` + `LastUpdate`; bucket/tech work
  (`sideigc.h:41-51`) arrives with the economy task.
- No `DamageTrack`, no payday, no ships, no KD-tree / sweep-and-prune.

Tests: `MissionUpdateTests.cs` — a table-driven `[Theory]` over `deltaT`
pinning the expected chunk count (including the `0.25 → n=2` boundary and the
`deltaT ≥ 600` throw), plus the no-op, backwards-clock, not-started/over
advance, exact-landing/no-drift, and site-ordering cases. **1,412 passing**
(prior 1,383 + 29 new).

### What the tick needs next

With the cadence in place, the next task can:
1. Instantiate the runtime objects (`CreateClusterSite`/`CreateThingSite` are
   called during object construction — see `clusterIGC.cpp:41`/`modelIGC.cpp:117`).
2. Fill the markers with real IGC object implementations (ships, then the
   cluster passes the tick already dispatches into a real per-chunk body).


## The flight model — `Ship.ExecuteMove`

Checklist item after the tick: **make a ship move**. This slice ports the
kinematics of `CshipIGC::ExecuteShipMove` and the state it needs, nothing more.
It lives in `src/Allegiance.Core/Sim/`:

- `Vector3` — the repo's single vector type (below).
- `Orientation` — `zlib/orientation.{h,cpp}` (class doc carries the evidence).
- `ControlData` — `struct ControlData` (`igc.h:1662-1671`): the four joystick
  axes `jsValues[4]` (yaw, pitch, roll, throttle), `Reset()` at throttle −1.
- `ShipControlState` — the movement bits of `ShipControlStateIGC`
  (`igc.h:803-835`), values copied verbatim.
- `Ship` — id, hull type, position, velocity, orientation, controls, turn
  rates, state bits.
- `Ship.ExecuteMove(double timeStart, double timeStop)` — the port itself.

`ExecuteMove` reproduces `CshipIGC::ExecuteShipMove(timeStart, timeStop,
&vel, &ori)` (`shipIGC.cpp:2852-3054`) step for step: torque-ramped turn rates
(`:2885-2910`), `Yaw/Pitch/Roll` + `Renormalize` (`:2912-2917`), the drag term
with double intermediates (`:2927-2935`), the thrust branch — manual
direction buttons, coast/vector-lock, or throttle-cruise (`:2961-3006`) — the
engine clip with the asymmetric side/back multiplier (`:3008-3033`), and the
velocity increment (`:3037`). `GetTorqueMultiplier()` is
`shipIGC.h:542-549` (0.50 at rest, up to 2.50), read from the current speed.

### Vector: one type, not two

The C++ has a single `Vector`. The repo had already adopted
`Collision.Vector3` for positions (README "The IIgcSite seam"). Initially a
second `Sim.Vector3` was tried for the flight math, but because `Sim` is a
child namespace of `Allegiance.Core` and its members use unqualified `Vector3`,
the new type silently re-bound the seam's positional signatures from
`Collision` to `Sim` — changing meaning with no code change at the point of
definition. Rather than ship a latent, subtle rebinding, **the flight
arithmetic was added to the existing `Collision.Vector3`** (component ±, scalar
∗//, dot, cross, length), and the flight model uses that single type. So
`Sim/Vector3.cs` does not exist; `Orientation`/`Ship`/`ControlData` live in
`Sim` and reference `Collision.Vector3`, exactly as the seam does. This
deviates from the task's literal "build `Vector3` in `Sim/`" and is the
deliberate, documented choice.

Division-by-scalar multiplies by the reciprocal — `Vector::operator/`
(`vector.h:158-162`) — which is bit-faithful (a naive `v/s` is not).

### Decisions with evidence (the C++ wins)

- **Handedness: left-handed** when forward is the nose. The stored matrix rows
  are a proper rotation (det +1), so {right, up, backward} is right-handed
  among itself — `right × up = backward` (`orientation.h:83-104`,
  `orientation.cpp:358-390`). But `GetForward() = −row2`, so {right, up,
  forward} has det −1. Proofs: the identity orientation noses at `(0,0,-1)`,
  and `Set(forward, up)` computes `right = forward × up` which for identity
  gives `right×up = (0,0,1) = backward = −forward`. In a right-handed frame
  `right×up` would equal `forward`; it does not. Positive yaw is therefore a
  left-handed rotation about +up, swinging the nose from −z toward −x
  (`orientation.cpp:538`).
- **Rotation order:** three successive **world-axis pre-rotations** in the
  order yaw, then pitch, then roll, with pitch sign flipped
  (`shipIGC.cpp:2912-2914`: `Yaw(rate·dT)`, `Pitch(−rate·dT)`,
  `Roll(rate·dT)`). Each `Yaw/Pitch/Roll` left-multiplies the orientation by a
  fixed rotation about +y/+x/+z respectively, so the composite is
  world-axes yaw∘pitch∘roll with a negated pitch. `Renormalize()`
  (`orientation.h:156`) re-derives the basis from forward & up every step,
  which is what keeps it orthonormal across arbitrarily many steps.
- **No speed clamp.** There is no `if speed > maxSpeed: speed = maxSpeed`
  anywhere in `shipIGC.cpp`'s movement path; the throttle branch merely chooses
  a target of `min(speed, maxSpeed)` for cruise, and the engine clip limits
  acceleration, not instantaneous speed. Reproduced faithfully (a `"fix"`
  would be a wrong model). Measured consequence: sustained manual forward
  thrust at a coarse step (`dT=0.01`) eventually **exceeds** `hull.Speed`,
  because the forward-button force's terminal equilibrium sits above the hull
  number for large steps.
- **Asymmetric side/back multiplier** is real and reproduced: the engine clip
  divides lateral thrust by `sideMultiplier` and (only positive) rearward
  `z` thrust by `backMultiplier` (`shipIGC.cpp:3015-3017`). Backward thrust is
  thus scaled harder — a genuine quirk, kept.
- **Simplifications (documented, not hidden):** no ripcord (`m_pmodelRipcord`
  false, so both guarded blocks always run; drag is still computed and
  subtracted), and no afterburner part (so `thrustRatio == 0` and no AB drag;
  the `afterburnerButtonIGC` bit still gates the throttle branch, exactly as
  in the reference).

### Determinism verification (all in `ShipFlightModelTests.cs`)

- identical inputs ⇒ **bit-identical** outputs, two independently-constructed
  ships through 768 odd-sized steps, compared with no tolerance;
- zero control input + zero velocity ⇒ **nothing moves** (200 steps);
- pure forward thrust from rest ⇒ velocity grows **only** along the facing
  axis (laterals < 1e-4, speed well past 1);
- turn rates never exceed `maxTurnRates` (full-yaw 1000 steps) and converge to
  the target;
- no clamp: starting above `maxSpeed` and coasting stays above after a step;
- orientation orthonormal after **10,000** steps (`Renormalize` doing its job).

### Chunk divergence (measurement, not assertion)

Stepping one interval vs. the mission's even sub-steps legitimately diverges,
because turn rates, `GetTorqueMultiplier()` and the throttle are re-evaluated
per sub-step (this is the same phenomenon as the `dT`-dependent plateau above).
`Measure_single_vs_split_step_divergence` prints:

```
CHUNKDIV deltaV=0.0031761578 deltaForward=0.00088577415 (total=0.0950523 s, 3 chunks) hull.Speed=120
```

i.e. a 0.095 s interval run whole vs. split into 3 chunks differs by
**≈3.2e-3 m/s** in velocity and **≈8.9e-4** in the forward-axis direction on
a hull with `Speed = 120`. Small but nonzero — expected, and a good thing to
know before integration (the mission will run the split form).

### What the next slice needs

Done — see **Ships in sectors — the tick moves them** below. What it took, from
the earlier plan: a `Model` base, `Ship : Model`, `Sector` carrying a model
list walked per chunk, and position integration (`Δpos = velocity·ΔT`, which
`ExecuteShipMove` deliberately does not do — the mission/chunk driver owns it,
cf. `CalculateShip`, `shipIGC.cpp:3273-3286`). Collision, weapons, docking,
ripcord and the economy remain untouched.


## Ships in sectors — the tick moves them

Checklist item after the flight model: **connect the ship into the ticking
mission so a mission tick physically moves ships.** It lives in
`src/Allegiance.Core/` across `Sim` and `Model`:

- **`Sim/Model.cs`** — the `Model` base, the port of the C++
  `ImodelIGC`/`CmodelIGC` abstraction (`modelIGC.h:22`), kept to what this
  slice needs: `Id`, `Position`, and `Update(double now)`. **No class hierarchy
  for objects that do not exist yet** — `Ship` is the only concrete model.
- **`Ship : Model`** — becomes a sector-ticking model. Its `OnUpdate(now, dt)`
  runs `ExecuteMove(now - dt, now)` then integrates `Δpos = velocity·ΔT` (the
  step `ExecuteShipMove` deliberately omits; owner is the mission/chunk driver,
  cf. `CalculateShip`, `shipIGC.cpp:3273-3286`). Velocity/orientation are bit-
  identical to a direct `ExecuteMove` — the wrapper adds no physics.
- **`Sector`** gains a mutable model list, `Add`/`Remove`/`Models`, and its
  `Update` walks the models exactly as the C++ models loop does
  (`clusterIGC.cpp:241-253`). It also gained the **`now > m_lastUpdate`
  guard** (`clusterIGC.cpp:99`) it lacked as a stub.
- **`Mission.PlaceShip(Ship, Sector)`** / `PlaceShip(Ship, short sectorId)` —
  a way to place a ship in a sector at construction; validates the sector
  belongs to the mission, then routes to `Sector.Add`.

### The gate the task flagged (the C++ wins)

Reading `CclusterIGC::Update` (`clusterIGC.cpp:97-253`) directly: the body is
gated on `now > m_lastUpdate` (`:99`), and inside it the **station/ship/cost
block is gated on `STAGE_STARTED` (`:103-231`), but the models loop is NOT** —
`l->data()->Update(now)` at `:241-253` runs unconditionally for every tick of a
live cluster, started or not. So the port runs models on **every** sector tick
whenever `now > LastUpdate`, with no stage check. (A sector does not carry the
mission stage, and the tick only reaches sectors once the mission is
`Started`/`Starting`, so in practice the model loop is still confined to a
running mission — but the reproduction is faithful: the models loop itself has
no gate.) Note the *other* half of the C++ function — ship plot/execute
(`:162-228`), drone launch, cost, and the whole `activeF` collision block
(`:255-358`) — is **out of scope** and deliberately absent.

### Decisions with evidence

- **Where `dt` comes from.** The base `Model.Update` owns the per-model clock
  and guard (both `CmodelIGC::Update` and `CshipIGC::Update` recompute
  `dt = now − m_lastUpdate` and act only when `now > m_lastUpdate`,
  `modelIGC.cpp:79-80`, `shipIGC.cpp:271-275`); it hands the derived model its
  elapsed `dt` in `OnUpdate(now, dt)`, then stores `now`. A backwards or
  repeated `now` is a no-op on a model, matching the C++ guard.
- **Position integrated in `OnUpdate`, not a separate `Move()`.** The C++ keeps
  a separate `Move()` pass (`clusterIGC.cpp:338-346`) inside the out-of-scope
  `activeF` collision block; splitting position out again here would drag that
  block's gate in. Integrating in the model's tick is the minimal honest place
  — the physics (`ExecuteMove`) is untouched and called verbatim.
- **`Sector.Update` gained a guard.** As a recording stub it had none; the C++
  gates everything on `now > m_lastUpdate`. Added with the models walk so a
  repeated/earlier sector tick is a genuine no-op, exactly as in C++.
- **Loop is a plain forward walk.** The C++ captures the successor link
  (`lNext = l->next()`) before `Update` because models may self-terminate; no
  model self-terminates in this slice, so a straight `foreach` over the live
  list is correct and the list is not mutated during iteration.

### Verification (all in `ShipInSectorTests.cs`)

- **The tick does not change the physics:** a ship through a real
  `Mission.Update` (multi-chunk `0.908 → n=4`, and single-chunk `0.093 → n=1`)
  is bit-identical in velocity, orientation and position to a reference that
  replays the same chunked `ExecuteMove` + integration. The wrapper only
  decides cadence.
- **An empty mission ticks harmlessly** — sectors still tick (increment counts,
  advance `LastUpdate`), models list stays empty, nothing touched; a removed
  model is not re-ticked.
- **Sector isolation:** a ship in sector A is untouched when sector B ticks
  on its own (`B.Update`), including sector A's own tick count staying 0.
- **The chunked cadence reaches models:** a model sees **n** updates, not one
  (`deltaT = 1.077 → 5` counts via a test-only observer), and a travelling ship
  is the strongest witness — after `0.903 s` (n=4) it has accelerated and
  displaced along −z.
- **Guard:** a model is a no-op on backwards/repeated time; a forward call runs
  once.
- **⭐ Carry-forward divergence measurement** (report, not assertion) — a ship
  stepped by the mission over `0.905216 s` (4 chunks) vs one big `ExecuteMove`:

```
CHUNKDIV deltaV=1.6417305 deltaPos=3.449893 deltaForward=0.075840645 (total=0.905216 s, 4 chunks) hull.Speed=120
```

The split-vs-single gap is now much larger than the flight model's 0.095 s/3-chunk
figure (Δv≈3.2e-3) because this interval is ~9.5× longer and the divergence
compounds per chunk — Δv≈1.64 m/s, Δpos≈3.45 m, Δforward≈0.076 rad over 0.9 s on a
hull with `Speed = 120`. Expected, and the reason the mission runs the split form:
each sub-step re-evaluates turn rates, torque multiplier and throttle.

Tests: **1435 passing** (prior 1426 + 9 new). `MissionUpdateTests` cadence cases
(including the `0.25 → n=2` boundary) are untouched and still green.
