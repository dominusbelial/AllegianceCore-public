# P777b — missiles vs the aleph cone

**Branch** `pi/p777b` (worktree `/home/domingo/allegiance-pi-p777b`), base `c185229e`
(master after P776 `f46cca2c`). **Files owned/changed in production code:**
`src/Allegiance.Core/Model/Sector.cs`, `src/Allegiance.Core/Model/AlephConeHitTest.cs`
(+ a doc-only rename in `src/Allegiance.Core/Sim/Ship.cs`). No P775 file and no
`ServerHost.cs` was touched.

This is the missile half of P777. Goal 1 is behaviour parity with native (what
triggers a warp bomb, what happens to a missile that reaches an aleph, and the
timing/outcomes); goal 2 is a cheaper warp/missile trigger code, proved with
numbers. Visuals are allowed to differ.

---

## 1. Native rule (cites opened)

All paths under `~/Development/Allegiance/src/`.

### 1.1 A missile collides with an aleph through the **same cone** as a ship

* Missiles are `c_mtDamagable | c_mtHitable | c_mtCastRay | c_mtSeenBySide`
  (`Igc/missileigc.cpp:50-51` model missiles, `:64-66` decal missiles), and
  cast-ray models are tested against the **static** KD tree
  (`Igc/clusterIGC.cpp:266-285`).
* Warps are `c_mtStatic | c_mtHitable | c_mtSeenBySide | c_mtPredictable`
  (`Igc/warpIGC.cpp:33-35`) and are added to that static tree
  (`Igc/clusterIGC.cpp:526-530`).
* The warp's hit shape is the **cone**: `CmodelIGC::LoadWarp` →
  `LoadCVH(NULL, iconName, attributes, c_htsCone)` (`Igc/modelIGC.cpp:127-130`).
  The solid is the base disc of radius `warpDef.radius` through the centre
  (perpendicular to forward) plus a 45° cone behind it with its apex at
  `centre + R·backward` (`BoundingCone::GetExtreme`, `_Utility/HitTest.cpp:254-283`;
  `HitTest::GetExtreme` mapping, `_Utility/HitTest.cpp:878-879`;
  `zlib/orientation.h:83-92`). Nothing of it lies in front of the disc.
* The missile is a **sphere of `dataMissileType->radius`**: decal missiles are
  `c_htsSphere` (`Igc/missileigc.cpp:60-68`), model missiles are scaled to that
  radius (`SetRadius(dataMissileType->radius)`, `Igc/missileigc.cpp:50,72`).
* Sphere vs cone is not sphere-vs-sphere: `c_htsCone = −1 > c_htsSphere = −3`
  (`_Utility/Utility.h:691-696`), so `HitTest::Collide` goes to `HullCollide`
  (`_Utility/HitTest.cpp:197-201`). That test is continuous over the tick, and an
  overlap while separating still collides (`_Utility/HitTest.cpp:166-185`).

**So missiles use the same cone as ships** — the port's old `R + r` sphere for
the warp-bomb trigger was simply the wrong shape.

### 1.2 A warp-bomb missile arms the aleph

`CmissileIGC::HandleCollision` (`Igc/missileigc.cpp:423-513`), inside
`if (!m_bDisarmed)`:

* disarmed = `m_bDisarmed`, set from `dataMissile->bDud` (`:128`) — the port's
  `Missile.IsDud`;
* mines are ignored (`:430-443`);
* `eabm & c_eabmWarpBomb` → `position1 = 0` ("client will not show an
  explosion") and, if the target is `OT_warp`, `pigc->WarpBombEvent(...)`
  (`:459-467`);
* then the **unconditional** `pigc->KillMissileEvent(this, position1)` (`:512`).

`FedSrvSiteBase::WarpBombEvent` (`FedSrv/FedSrv.CPP:12543-12562`):
`timeExplosion = pwarp->GetLastUpdate() + c_fcidWarpBombDelay`,
`FM_S_WARP_BOMB` to `GetGroupRealSides()` (`:12558`), `pwarp->AddBomb(...)`.
Detonation is the warp's own `Update` (`Igc/warpIGC.h:53-88`: pop due bombs,
`CreateExplosion(damageType, Power, BlastRadius, c_etBigShip, timeExplosion,
position, NULL)`, source only for `c_eabmWarpBombDual`). The client is
`clintlib/appmsg.cpp:849-870`.

### 1.3 **Any other armed missile that touches an aleph dies there**

* The warp is **not** `c_mtDamagable` (`Igc/warpIGC.cpp:33-35`), so the damage
  branch (`Igc/missileigc.cpp:468-498`) — including its capture sub-branch — is
  skipped, and control falls through to `KillMissileEvent(this, position1)`
  (`:512`) with `position1 = GetPosition() + GetVelocity()*tCollision` (`:452`),
  the contact point.
* `KillMissileEvent` (`FedSrv/FedSrv.CPP:12512-12529`) sends
  `FM_S_MISSILE_DESTROYED` with that position to `GetGroupRealSides()`; because
  the position is ≠ 0 it then calls `pmissile->Explode(position)` and finally
  `Terminate()`.
* `CmissileIGC::Explode` (`Igc/missileigc.cpp:516-530`): when
  `blastPower != 0` it is a **real** `CreateExplosion(damageType, blastPower ×
  side c_gaDamageMissiles, blastRadius, c_etMissile, lastUpdate, position,
  launcher)`; otherwise only the visual explosion.
* A **capture-capable** missile that hits an aleph therefore **does** explode:
  the capture branch lives inside the damagable branch (`:475-497`), so there is
  no capture exclusion here (unlike the port's ship-impact block).
* The port's P549 comment ("Any other missile touching an aleph does nothing",
  old `Sector.cs:4051-4053`) misread `:459-466` and is corrected by this pass.

### 1.4 Time order

Native processes every collision of a tick in time order
(`Igc/clusterIGC.cpp:288`). The port runs the combat pass before the aleph pass;
a missile that would reach an aleph earlier in the tick than a ship it also
crosses is an edge case left as-is (brief, native rule 4). It is recorded in the
`ResolveAlephCollisions` remarks.

---

## 2. The port before and after

### 2.1 Before (`c185229e`)

The aleph work was **three** things:

| what | where | shape | cost |
| --- | --- | --- | --- |
| ship transit trigger | `ResolveWarpCollisions` (`Sector.cs:3866-3998`), `"sim.warp"` | exact cone, but **sampled** (≤ 16 samples, spacing ≤ `max(r,1)`) | `_models.ToArray()`, `float[]`→`Vector3` parse per ship × warp, `Normalize` per sample (`Touches`), a `Length()` sqrt broad phase |
| warp-bomb trigger | `ResolveWarpBombTriggers` (`:4045-4094`), `"sim.combat"` | end-of-tick **point-in-sphere** `\|m − warp\| ≤ R + r`, warp-bomb-capable missiles only | a second `_models.ToArray()` |
| bomb detonation | `ResolveWarpBombDetonations` (`:4097-...`), `"sim.combat"` | unchanged | unchanged |

Consequences (all fixed here):

* a warp bomb passing ≤ `R + r` **in front of** the disc armed the aleph;
  natively it does not (the solid is empty there);
* a fast bomb crossing the disc between two ticks with both samples farther than
  `R + r` from the centre did **not** arm it; natively it does (continuous test);
* a plain missile flew **through** an aleph untouched; natively it dies and
  explodes at the contact point.

### 2.2 After

One merged, exact, allocation-free pass:

* `AlephConeHitTest.Geometry` (`internal readonly struct`) holds a warp's centre,
  its **unit backward axis** and its radius. `Geometry.From(DataWarpIGC)`
  normalises `Forward` **once** (a zero forward falls back to `(0,0,1)`
  backward, exactly as `Backward()` does today).
* `AlephConeHitTest.Distance(in Geometry, Vector3)` — the exact distance to the
  cone, no normalise, no allocation.
* `AlephConeHitTest.SweptFirstContact(in Geometry, start, end, r, out t)` — the
  exact swept sphere-vs-cone test (design in §3).
* `Sector.ResolveAlephCollisions(now, dt)` replaces both old pass bodies and is
  the **single call site**, under `"sim.warp"`. It builds the per-warp
  `Geometry` into a reusable array (grown only when `_warps.Count` grows; warps
  without a position are skipped), snapshots `_models` into a reusable
  `List<Model>` (cleared, never reallocated, safe against removals during the
  loop) and dispatches per model to `ResolveAlephShip` / `ResolveAlephMissile`.
  No LINQ, no lambda, no `ToArray`, no boxing.
* The **ship** half keeps every gate and its order (`Dead`, `Mission`,
  `WarpReady`, `WarpCooldownSeconds`, the guard release, the layer suppression,
  the dangling-warp skip, the guard block, `HitWarpEvent`, one warp per tick).
  Only the trigger changed: `SweptFirstContact(geom, Position −
  CollisionVelocity·dt, Position, 0.5·HullType.Length, out _)`. The
  `_warps.FirstOrDefault(lambda)` in the guard release became a plain loop.
* The **missile** half skips `IsDud`, sweeps `Position − Velocity·dt → Position`
  with radius `MissileType.Radius`, takes the **earliest** contact over all warps
  (native's time order; `t` gives the contact point) and then:
  * warp bomb: the existing body unchanged (schedule → `Terminate` →
    `WarpBombEvent` → `MissileDestroyedEvent`);
  * otherwise (native rule 3): `missile.Position = contact`; `Terminate`;
    `ResolveExplosion(new Explosion(DamageType, BlastPower × gaMul(DamageMissiles),
    BlastRadius, now, contact, Launcher))` when `BlastPower ≠ 0`; and
    `MissileDestroyedEvent` through the launcher's site (the same route as the
    warp-bomb branch). `gaMul` is built exactly as the combat pass builds it
    (`launcher.GlobalAttributeFactor(GlobalAttributeSet.DamageMissiles)`).
    **No capture exclusion** — native's capture branch is inside the damagable
    branch a warp never reaches.
* `ResolveWarpBombDetonations` stays where it is (`"sim.combat"`), unchanged.

**The missile trigger moved from the `"sim.combat"` profiler bucket to
`"sim.warp"`**, because it is now part of the aleph pass (brief design change 1).
That is the only observable change of the merge; ships and missiles are
independent in this pass, so no outcome changes.

---

## 3. The swept test: design and correctness argument

`f(t) = dist(p(t), cone)` along `p(t) = start + t·(end − start)` is **convex** in
`t`: the cone solid is convex (the hull of a radius-`R` disc and the apex), the
distance to a convex set is a convex function of the point, and `p(t)` is affine.
`DistanceToConeSection` is the exact 3-D distance, not an approximation: the
solid is a body of revolution, so the nearest point lies in the same meridian
half-plane as the query and the 2-D triangle distance equals the 3-D one.

Given convex `f` and `f(0) > r`, the sublevel set `{f ≤ r}` is a single interval
`[t₁, t₂]` and `f` is non-increasing on `[0, t₁]` (if `f'` were positive anywhere
before the first crossing, convexity keeps `f` above `f(0) > r` everywhere after —
contradiction). So:

1. **Rejects** (all cheap and exact):
   * segment-to-centre distance `> R + r` (squared compare) — every point of the
     solid is within `R` of the centre;
   * both endpoints `z < −r` along the unit backward axis — nothing of the solid
     is in front of the disc, and the distance to it is at least `−z`;
   * both endpoints `z > R + r` — nothing is behind the apex, distance ≥ `z − R`.
2. `f(0) ≤ r` → contact at `t = 0`.
3. **Golden-section search** on `[0, 1]` (40-iteration cap, 0.618 ratio) for
   `min f`, with two early exits that keep it fast: any sample `≤ r` ends the
   search, and a **Lipschitz bound** rejects a whole bracket at once
   (`f` changes by at most `|end − start|` per unit `t`, so no unsampled point in
   `[lo, hi]` can be below `best − travel·(hi − lo)`). The bracket is also stopped
   once it is `1e-3` world units wide.
4. If no sample is within `r`, there is no contact.
5. **Bisection** of `f(t) = r` on `[0, t_best]` (30-iteration cap, `1e-4` of the
   travel) gives the **first** contact, because `f` is decreasing on that
   interval. The result is `t` on `[0, 1]`; `start + (end − start)·t` is the
   contact point native calls `position1` (`Igc/missileigc.cpp:452`).

A zero-length segment reduces to `Distance ≤ r`, i.e. exactly `Touches`. The
final "no contact" test is written as a negated `<=` so a non-finite position
reports no contact instead of a phantom one.

This is native's continuous test (`_Utility/HitTest.cpp:114-207`), and it is a
**strict superset** of P776's sampling (measured below). Ships now use it too, so
ships gained exactness at the same time as missiles gained the ability to be
tested at all.

**Tolerance.** Two documented approximations remain: the golden bracket stops at
`1e-3` world units and the bisection at `1e-4` of the travel, so a *grazing*
contact whose true minimum is within ~`1e-3` of `r` may be reported either way.
The differential test isolates exactly that band (0 cases fell in it out of
20,000).

---

## 4. Tests

New files:

* `tests/Allegiance.Core.Tests/P777bMissileAlephConeTests.cs`
* `tests/Allegiance.Core.Tests/P777bAlephTriggerPerfTests.cs`

### 4.1 Pure (new API — cannot run on the unfixed base)

| test | what it pins |
| --- | --- |
| `Swept_first_contact_matches_the_cone_geometry` | `R = 100, r = 2`: miss 90 u→30 u in front of the disc (`ρ = 0`); a 40 u crossing at `ρ = 0.95R` hits with `t = 0.5 − r/40 ± 1e-3`; from `z = 150` to `z = 120` misses and to `z = 90` hits at the apex (`t = (150 − (R+r))/60`); parallel to the disc at `z = −r ∓ 0.01` misses/hits; a segment outside the `R + r` ball never touches |
| `Zero_length_segment_equals_touches` | 2,000 random points: `SweptFirstContact(p, p, r)` == `Touches(...)` and `t = 0` (397 hit / 1,603 miss) |
| `Exact_sweep_agrees_with_brute_force_and_dominates_the_p776_sampler` (`Tier=Perf`) | 20,000 seeded segments (lengths 0-400 u, `r ∈ [0.5, 30]`, unit and non-unit forward) |

Differential numbers (deterministic seed, reproduced identically twice):

```
P777b-DIFF: cases=20000 hits=3339 boundary-band=0 mismatches=0
            p776-sampler-hits=3309 sampler-missed-tunnelling=30
```

* **0 mismatches** against the 4,000-sample brute force, and **0 cases** fell in
  the `|min dist − r| < 1e-3` tolerance band, so the agreement is not being
  hidden by the exclusion.
* Every hit the P776 sampler found, the exact test also found (asserted per
  case).
* The sampler **missed 30** of the 3,339 real hits — those are the tunnelling
  cases a 16-sample walk cannot see.

`Tier=Perf` on the differential test only: 20,000 × 4,001 distance evaluations
take ~10 s in a Debug build, above the Fast tier's 10 s calibration floor
(`docs/testing/TIERS.md`). The other two pure tests are Fast.

### 4.2 Domain (the `WarpBombTests` fixtures: `PCore001.igc` + a real map, a
started mission, a counting `IIgcSite`, and the `AttachWarp` reflection seam).
`AttachWarp` uses `Forward = (0,0,-1)`, i.e. **backward = +Z**: the disc is the
`z = 0` plane, the solid occupies `z ∈ [0,R]`, and `z < 0` is in front of it.

| test | expectation | unfixed base |
| --- | --- | --- |
| `Warp_bomb_passing_90u_in_front_of_the_disc_does_not_trigger` | bomb flying parallel to the disc 90 u in front (`R = 100`, inside the old `R + r` ball): 0 bombs, missile survives | **FAILS** (sphere) |
| `Warp_bomb_crossing_the_disc_triggers_in_one_tick` | `ρ = 0.9R`, 140 u in one 0.05 s tick, both end-of-tick samples > `R + r` from the centre: exactly 1 bomb, missile consumed, 1 destroy event | **FAILS** (no sweep) |
| `Warp_bomb_from_behind_hits_the_cone` | approach along the backward axis from `z = 3R`: 1 bomb | passes (guard) |
| `Plain_missile_reaching_the_aleph_dies_and_explodes_at_the_contact_point` | blast missile into the disc at `ρ = 0.5R`: consumed, 1 `MissileDestroyedEvent`, last `Position` = contact (`x = 50 ± 1`, `z = −r ± 1` — `r = 0.5` for the selected type), the cross-side ship 32 u away takes the exact falloff (`1 − drop = 0.619`), a same-side hull at the same spot is spared (the launcher resolves → friendly-fire gate), 0 bombs | **FAILS** (flew through) |
| `Plain_missile_without_blast_dies_without_damage` | `BlastPower = 0`: consumed, 1 destroy event, victim untouched | **FAILS** (flew through) |
| `Dud_missile_ignores_the_aleph` | `IsDud` flies through the centre to `z = 240`, 0 bombs, 0 destroys | passes (guard) |

Base-run evidence (production files stashed, a temporary probe file holding only
the domain tests): tests 3, 4, 6, 7 and the rewritten item 9 **failed**; tests 5
and 8 passed. The perf class' 0-allocation assert also failed on the base.

### 4.3 Fixture changes (each with its reason)

1. **`WarpBombTests.Domain_non_warp_bomb_missile_in_warp_radius_arms_nothing`**
   (item 9). Kept "arms no bomb"; changed "not consumed" to **consumed + one
   `MissileDestroyedEvent`**, citing native rule 3 (`missileigc.cpp:452,512,516-530`;
   `FedSrv.CPP:12512-12529`) in the doc comment. Reason: native kills any armed
   missile that touches an aleph; the old assertion pinned the bug.
2. **`WarpBombTests.Domain_missile_between_raw_and_visual_warp_radius_does_not_trigger_warp_bomb`**
   — **not changed**. Step 0 finding: the probe sits at `z = 0.5·(raw + visual) =
   35 + r`, which with `R = 20` is **behind the disc and beyond the apex**
   (`z > R`), i.e. on the solid's own axis but outside it by ≈ 15 u, so it still
   does not arm. Verified green on the base **and** on the fixed code. The trigger
   was not widened.
3. **`WarpBombTests.Domain_missile_in_warp_radius_is_consumed_now_and_...`** — not
   changed. Its victim sits at `z = (shipRadius + missileRadius) + 20 ≈ 31 > R =
   20`, beyond the apex, which is why P776's cone already left it alone.
4. **`P649CombatBlastParityTests`** — added an `AlephTrigger(sector, now)` helper
   (reflection: it calls the merged `ResolveAlephCollisions(now, 0f)` when it
   exists and otherwise the old `ResolveWarpBombTriggers(now)`) and used it at the
   four former `ResolveWarpBombTriggers` call sites. Reason: the private method
   those tests reached by name no longer exists; `dt = 0` makes the swept segment
   zero-length, so the parked-missile cases behave exactly as before. No assertion
   changed. (My first attempt at this edit dropped one trigger call; the 3×-alone
   gate caught it — see §6.)
5. **Doc-only renames** of `ResolveWarpCollisions` → `ResolveAlephCollisions` in
   `<see cref>`/`<c>` text: `src/Allegiance.Core/Sim/Ship.cs` (3 sites),
   `tests/.../WarpProximityTests.cs`, `tests/.../NpcCrossClusterLiveTests.cs`,
   `tests/.../MultiHopDroneLiveTests.cs`. No behaviour, no assertion.

### 4.4 Perf (item 12)

`P777bAlephTriggerPerfTests`, `[Trait("Tier","Perf")]`, two populations of a
domain sector with **6 warps (`R = 100`), 400 ships and 200 missiles**:

* 70 % far away (the broad-phase reject path);
* 30 % inside the old `R + r` ball but **not touching** the cone — half 90 u in
  front of the disc, half beside the apex (`ρ = 20, z = R`) — so the cone maths
  runs and no state changes;
* non-zero velocities, so the sweep runs.

Second population: 10 % of the ships sweep **through** the cone every invocation,
so the golden-section + bisection + `HitWarpEvent` path is measured too. The pass
is invoked directly (the models are never ticked), so every iteration is identical
and deterministic — no re-placement is needed, and the mission site's
`HitWarpEvent` is a no-op, so even the crossing population changes no state.

The pass is private, so it is reached by reflection **bound to a delegate**
(`MethodInfo.CreateDelegate`, once) so the per-call path allocates nothing; the
same file runs on the base by falling back to
`ResolveWarpCollisions(now, dt)` + `ResolveWarpBombTriggers(now)`.

Measurement: `Stopwatch` around 5,000 invocations, median of 5 runs;
`GC.GetAllocatedBytesForCurrentThread()` over the same window (the `Stopwatch`
itself is created before the window opens). Only the allocation is asserted
(**0 B**), the timing is printed.

---

## 5. Numbers

### 5.1 Timing and allocation (before/after, same machine, same session)

Machine: 24 logical CPUs, .NET 10 Debug test build. Both rows were measured
**back to back** (base at 19:50:38–19:51:56, after at 19:52:04–19:52:32) on the
same box; the 1-minute load average moved from **8.22 → 4.73** (base) to
**4.67 → 3.91** (after), i.e. the box was *not* idle (other lanes were running
on this shared workstation). An earlier pair taken at load ≈ 1.8 is quoted after
the table for comparison.

| case | base µs/call | after µs/call | base B/call | after B/call |
| --- | --- | --- | --- | --- |
| no-crossing (600 models, 6 warps, 30 % near-miss) | **1339.92** | **540.47** | **24048** | **0** |
| 10 % of ships cross the cone | **1707.04** | **537.92** | **24048** | **0** |

Medians of 5 × 5,000 invocations; base min/max 1327.35/1386.94 and
1692.19/1724.11, after min/max 539.13/543.23 and 536.69/540.79.

Earlier pair (load average 1.82, same procedure): base 1318.17 / 1660.32 µs,
24,048 B/call; after 525.70 / 525.30 µs, 0 B/call. The conclusion is stable
across both sessions: **≈ 2.5× faster on the near-miss population, ≈ 3.2× faster
when ships actually cross, and 24 KB/call → 0 B/call.** The per-tick allocation
was the two `_models.ToArray()` snapshots (600 entries each ≈ 2 × 4.8 KB plus
array headers, 24 KB measured with both passes); the after run is exactly zero
over 25,000 invocations. A third confirmation run after the final
comment-only/NaN-hardening edits printed 545.58 / 536.03 µs, 0 B/call — the same
band.

Note: the *pass* is allocation-free in steady state. When a contact fires a site
event, a warp transit or an explosion, those (pre-existing) event paths allocate;
that is not this pass.

### 5.2 Gate numbers (measured)

* **Whole-solution build**: `dotnet build AllegianceCore.slnx` → 0 warnings,
  0 errors.
* **AOT publish** (`dotnet publish src/Allegiance.Server … -p:PublishAot=true
  -o .scratch/aot`): succeeded, **no IL2026/IL3050**, 14,092,672 B native binary.
* **New/affected classes, 3× alone** (`P777bMissileAlephConeTests` 9/9,
  `P777bAlephTriggerPerfTests` 2/2 (26-28 s each, sequential), `WarpBombTests`
  6/6, and — after the last two comment-only/NaN-hardening edits — 1× each again
  (9/9, 6/6, 2/2) plus `P776AlephConeTriggerTests` 18/18):
  * Items 10/11 — 17 Core classes × 3 runs = 51 runs, all green:
    `P776AlephConeTriggerTests` (18), `WarpProximityTests` (6),
    `WarpTransitServerTests` (4), `P591bLayerPingPongTests` (3),
    `P591cDroneRoutingTests` (6), `P760ServerCallbacksTests` (13),
    `P650ServerRulesTests` (3), `LiveP379DebugTopologyTests` (3),
    `MultiHopDroneLiveTests` (1), `NpcCrossClusterLiveTests` (7),
    `NpcCrossClusterMinerDecisionLiveTests` (2), `MapGenLiveProofTests` (2),
    `MapGenRingLiveProofTests` (3), `P616TrackerNativeBugTests` (5),
    `P739TimerLiveTests` (5), `P649CombatBlastParityTests` (57),
    `AsteroidCombatLiveTests` (3).
  * `P758WarpBombHandlerTests` (ClientCore.Tests): 5/5 × 3.
  * `ShopLauncherMountNumberingLiveTests` (MonoGame.Tests): 1/1 × 3.
* **Fast tier** (`tools/run-tests.sh --tier fast`), two runs:
  * run 1 (box busy, load ≈ 8, right after the 5 showcase runs): **FAIL** —
    two failures, both **before/independent of this change** and both documented
    load flakes (`docs/TEST_FLAKES.md`):
    `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state`
    ("1352 B over 10,000 frames", the P661/P670/HUD-15 full-run allocation
    sampling effect) and
    `CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
    (`Assert.NotSame` at setup, the documented P699c async-spawn-order flake —
    it fails before any shot is fired). Both passed **3/3 alone** immediately
    after, using the exact-class filter.
  * run 2 (idle box): **PASS** — Lobby 24, MonoGame 3190 pass / 3 existing
    Meshy-pack skips, ClientCore 1610, Core 5040, quarantine 12, **9,879 tests in
    280.4 s**.
* **Showcase never-parks** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`)
  **5/5 pass**:

  | run | wall | samples | reached | longest speed<1 with target | shots | hits | kills |
  | --- | --- | --- | --- | --- | --- | --- | --- |
  | 1 | 108.0 s | 1079 | 28/30 | 1.511 s | 2057 | 859 | 9 |
  | 2 | 108.1 s | 1080 | 28/30 | 2.105 s | 2914 | 1428 | 8 |
  | 3 | 107.4 s | 1073 | 27/30 | 1.002 s | 2467 | 1103 | 7 |
  | 4 | 108.2 s | 1081 | 29/30 | 1.906 s | 2564 | 1064 | 7 |
  | 5 | 108.5 s | 1084 | 28/30 | 1.905 s | 3184 | 1287 | 8 |

  The showcase output prints only shots/hits/kills — **it does not print
  `MISSILE_DESTROYED` or warp-bomb counts**, so there is no base/after missile
  count to compare (no logging was added to any Server file).

---

## 6. Notes, limits, and leftovers

* **Step 0.** `src/Allegiance.Core/Model/AlephConeHitTest.cs` exists and P776
  (`f46cca2c`) is in the history — base correct. `Touches` normalises `forward`
  on every call (`Backward()`); the new `Geometry` does it once per warp per tick.
* **Where the existing `WarpBombTests` probes are.** `AttachWarp` faces warps
  `(0,0,-1)`, so **backward is +Z**: a positive `Z` offset from the warp is
  *behind* the disc, a negative one is in front. Both `Domain_missile_between_…`
  (`z = 35 + r` with `R = 20`) and `Domain_missile_in_warp_radius_…`'s victim
  (`z ≈ 31` with `R = 20`) sit **behind the disc, beyond the apex**, i.e. outside
  the solid — that is why they still hold after the change and why neither was
  re-placed. Test 6 needed a site that is *not* inside the cone: a hull 20 u
  straight "behind" the contact at `ρ = 0.5R` would be **inside** the solid
  (`ρ + z = 50 + 20 < 100`) and would warp instead of taking the blast, natively
  too; the fixture therefore puts the victim 32 u from the contact on the
  missile's approach side, laterally clear of its flight line, and asserts the
  clearance with `AlephConeHitTest.Touches`.
* **A missile younger than `dt`.** `Missile` exposes no launch position, so the
  sweep start `Position − Velocity·dt` can lie behind the launch point for a
  missile spawned mid-tick. That can only *over*-detect (by at most one tick's
  travel), never under-detect; the clean fix is a `Missile.LaunchPosition`
  property, which is outside this task's two files.
* **Warp-bomb `MISSILE_DESTROYED` position.** Native sends `0` so the client
  shows no explosion (`missileigc.cpp:463`); the port sends the missile position
  from `Server.Combat.cs` (a P775 file). Visual only, untouched, listed for a
  follow-up.
* **Sphere heuristics left alone** (not native cone decisions): the layer lay-point
  standoff `Server.ShipCommands.cs:~2030`; the opt-in P591b guard release
  `2·(R + r)` in `Sector.cs`; `ClientWorld.TransitAttributionRadius`
  (`ClientWorld.cs:5335`, client heuristic, `ClientWorld.cs` not edited); MCP
  `BotManager.WarpApproachParams` (`tools/allegiance-mcp/BotManager.cs:2546`),
  `IsClearOfAleph` (`:4442`), `ExitClearGoal` (`:4453`), `EntryAimPoint`
  (`:3725`), `EntryQueueYield` (`:3751`); `Game1.Camera.cs:106,197,392`
  (framing); `SpectatorFrame.cs:175`
  and vizfight (display). Correct native sphere uses left as they are:
  `Ship.Equipment.cs:~92` (probe deploy displacement,
  `FedSrv.CPP:4668-4700` uses `GetRadius()`) and `Mission.Treasure.cs:~107`
  (treasure crowd check).
* **Stale text in files this task must not edit** (all `<c>` code text, no
  cref/build impact): `ServerHost.cs:823,1461,1547,1561`;
  `Server.Combat.cs:815,2420`; `Server.Docking.cs:677`;
  `Server.ShipCommands.cs:2023`; `docs/parity/sim-rules-matrix.csv` row
  `SIM-0208` (names `ResolveWarpCollisions`); and `<c>` mentions in
  `WarpTransitServerTests.cs:21`, `P591cDroneRoutingTests.cs:23`,
  `P591bLayerPingPongTests.cs:34`, `ShipDeleteGoneLiveTests.cs:176`. `Sector.cs`
  itself keeps two historical mentions that describe the pre-P777b two-pass
  layout.
* **Follow-ups (out of scope, listed as asked).**
  * **Projectiles (bullets) vs alephs (P777c candidate).** Native projectiles are
    cast-ray and die on the non-damagable aleph
    (`Igc/projectileIGC.cpp:160-230`, `Terminate()` at `:230`); the port's
    projectile path has no aleph test at all.
  * The warp-bomb `MISSILE_DESTROYED` position (above).
  * Native time-ordering across passes (rule 4; noted in the pass remarks).
  * Model-missile convex hulls (the port uses the radius sphere, as native does
    for decal missiles; native scales model missiles to `radius` too, so the sphere
    is native's own approximation).

---

## 7. Parity matrix

Text edits only, in `docs/parity/sim-rules-matrix.csv` (LF, 16 fields per row,
verified by parsing all 474 rows; `git diff --stat docs/parity` = 2 changed
lines):

* `SIM-0207` (`WarpBombEvent@4861`) — appended to `notes`:
  `; P777b: trigger is the swept missile-sphere vs aleph c_htsCone
  (missileigc.cpp:459-467; modelIGC.cpp:127-130; HitTest.cpp:197-201), shared with
  ships (Sector aleph pass, AlephConeHitTest.SweptFirstContact;
  P777bMissileAlephConeTests).` Status stays `implemented`.
* `SIM-0189` (`KillMissileEvent@4843`) — appended to `notes`:
  `; P777b: an armed non-warp-bomb missile touching an aleph is killed at the
  contact point and explodes (missileigc.cpp:452,512,516-530;
  FedSrv.CPP:12512-12529) — previously it flew through.` Status stays
  `implemented`.

`docs/parity/parity-matrix.csv` (`fedmsg,188 WARP_BOMB`, `c_eabmWarpBomb*`) is
unchanged, as instructed.

## Claude review fixups

- A missile already killed earlier in the same aleph pass (by another missile's aleph blast, via
  `Missile.ReceiveDamage`) is skipped, and both `Terminate` calls bail out when the missile is no longer present —
  otherwise it exploded a second time and raised a second destroy event.
- The missile sweep start is clamped to the missile's age (launch = `TimeExpire − Lifespan`), so a missile fired right
  after an aleph exit cannot sweep back onto the disc and die on launch (native never does).
- SIM-0207 note punctuation. Follow-ups noted, not done: the differential test should also assert the reported contact
  is the FIRST crossing; the warp-bomb `MISSILE_DESTROYED` position is non-zero where native sends 0 (Server.Combat.cs).
