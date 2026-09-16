# P238 — Widen the real warp transit-trigger radius to match the visual scale

Stage landing: P-series master, not pushed (Domingo pushes). No side branch.
Follows P237, which scaled the *rendered* warp funnel to ~10x a ship's size
(`WarpRenderer.VisualScaleMultiplier = 9.5`) while deliberately leaving the real
gameplay `Radius` untouched — a scoping choice that produced the live bug this
stage fixes: Domingo flew into the now-huge visible vortex and nothing happened,
because the real transit trigger was still a tiny ~100-unit sphere at the funnel's
centre. Domingo's explicit decision after seeing it live: **widen the real trigger
to match the visual**, not shrink the visual back. That is exactly P238.

---

## 1. One number, not two: `Sector.WarpVisualScale` = 9.5

The whole point of the fix is that the visual scale and the real trigger scale
share a **single source of truth**, so they can never drift apart again (the exact
bug just found live).

- **`Sector.WarpVisualScale = 9.5f`** (`src/Allegiance.Core/Model/Sector.cs`)
  — the shared constant. Lives on `Sector` (in `Allegiance.Core`, the server's
  project) beside its existing warp gameplay constants (`WarpCooldownSeconds`,
  `WarpBombDelaySeconds`). Value cited back to P237's calibration:
  `rim diameter 10 × 190 (rendered ship) = 1900 ⇒ 1900 / (2 × 100) = 9.5`.
- **`WarpRenderer.VisualScaleMultiplier = Sector.WarpVisualScale`**
  (`src/Allegiance.ClientCore.MonoGame/Rendering/WarpRenderer.cs`) — the client
  no longer carries a second literal; it aliases the shared constant
  (`using Allegiance.Core.Model;`). The MonoGame project sees it transitively
  through `Allegiance.ClientCore → Allegiance.Core`.

The raw wire `DataWarpIGC.Radius` (stored in the decoded record, and re-encoded
byte-identically) is **never multiplied** — faithful to native. Only the consuming
logic applies the scale.

## 2. Two gameplay consumers, both widened (decision documented)

Grepping every read of the warp radius, exactly two gameplay consumers exist; the
task asked for a documented decision on the second:

1. **Ship transit trigger** — `Sector.ResolveWarpCollisions`
   (`threshold = WarpVisualScale * warp.Warp.Radius + shipRadius`). The primary
   fix; this is what Domingo hit.
2. **Missile-vs-warp "warp-bomb" trigger** — `Sector.ResolveWarpBombTriggers`
   (`threshold = WarpVisualScale * warp.Warp.Radius + missile.MissileType.Radius`).
   **Widened too**, deliberately: it is the same "real crossable/interactable size
   of the vortex" concept. A missile detonating against the now-huge visible edge
   should register against that large real size, not a tiny leftover hitbox at the
   funnel's centre. Leaving it unscaled would recreate exactly the "silently
   disagree with the visual" inconsistency the task forbids, between two consumers
   of the same radius in the same file.

No other reader of `warp.Radius` is gameplay-sized: `ServerHost.HitWarpEvent`'s
transit uses the destination warp's `Forward`/mass-limit (radius not read for a
size), and autopilot warp selection is not gated by `warp.Radius` (checked — it is
target-range/sensor logic, the task's stated out-of-scope).

## 3. Found and fixed live: the transit-exit ping-pong regression

Widening the trigger exposed a **second** place that silently disagreed with the
visual, exactly the class of bug the task warned about ("nothing else can silently
disagree"): **the transit exit offset.**

`ServerHost.HitWarpEvent` places a crossing ship essentially **at the destination
warp's centre** (native `fsship.cpp:129-132`: offset `shipRadius + 5`). With the
tiny pre-P238 radius that cleared the collision sphere, so the ship emerged and
flew away. With the 9.5x-scaled radius the ship now emerged **deep inside the
destination's ~950-unit sphere**; after the 2s debounce it re-triggered and
**ping-ponged forever** — observed live in the full-suite run (`NpcCrossCluster`
and the cross-cluster miner/ripcord NPC tests all regressed):

    [warp] ship 8096 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
    [warp] ship 8096 crossed from 'Haumea' (1057) to 'Arrokoth' (1051) via warp 1052
    [warp] ship 8096 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
    ... (oscillating forever)

A vortex you cannot leave is not "functionally crossable," so this is in-scope:
**the exit offset is scaled by the same shared constant**
(`exitClearance = Sector.WarpVisualScale * rw.Warp.Radius + shipRadius + 5`),
restoring native's implicit invariant that the ship emerges **outside** the
destination warp's collision volume and can fly away. The exit is along the
destination warp's forward (its funnel mouth), at the rim of the big vortex — the
natural "emerge from the gate" position.

This is why the cross-cluster NPC tests needed more than a trigger tweak; the exit
relocation is the engine fix, and the test-data clearances below are the
accompanying adaptation to the new (intended) geometry.

## 4. Test-data adaptation (honest, documented — not masking)

The three cross-cluster NPC navigation test files encoded the old tiny-warp
assumption in magic numbers (objectives 460 units along the exit, spawn/peer
clearances 620–870) — all inside the new ~950-unit sphere, so those points get
dragged through the warp. Each file gained a small `WarpClearance(warp) =
Sector.WarpVisualScale * warp.Radius + margin` helper, and:

- **NpcCrossClusterLiveTests** — station, peer park, NPC spawn moved beyond the
  scaled sphere (`WarpClearance`).
- **NpcCrossClusterMinerDecisionLiveTests** (2 tests) — the far rock / unload
  station, peer parks, NPC spawns moved beyond the scaled sphere.
- **NpcRipcordTravelLiveTests** (2 tests) — the ripcord anchor, peer parks, NPC
  spawns moved beyond the scaled sphere.

These are corrected geometries for the new design (objectives/spawns must clear a
~950-unit crossable vortex), not hacks: under the new scale a point nearer than
`WarpClearance` to a warp is genuinely in the transit zone, and the tests' own
premises ("must STEER to the warp", "parked peer never transits") require it.

## 5. Live proof — the exact scenario Domingo hit, now crossing

Real in-process `Server` + real loopback `Bot` + real wire, spawning at a distance
**between** the old and new trigger radii (`WarpTransitServerTests.ShipBetweenOldAndScaledRadius_GetsTransited_AndPeerSeesSectorChange`):

    P238-LIVE: warp #1051 radius=100, old trigger=118.7, scaled trigger=968.7, spawn at dist=543.7 (between)
    [warp] ship 8096 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
    P238-LIVE: between-radius spawn TRANSITED 1051 -> 1057 (SET_CLUSTER #2) — the old ~100 u radius would have missed this.

Before P238 this spawn (543.7 > old 118.7) sat outside the real trigger — Domingo's
"fly through the visible edge, nothing happens." After P238 the real transit fires
and the peer sees a fresh `SET_CLUSTER` (1051 → 1057). This is the established
repo live-proof pattern (P40/P41 precedent: in-process server + managed bot is the
standard for server-side/wire proof, matching P157/P172/P158's harnesses).

## 6. Tests added

- `WarpProximityTests.ShipBetweenOldAndScaledRadius_NowTriggersHitWarpEvent` —
  transit geometry: a ship between old (~118) and scaled (~969) radius now triggers
  `HitWarpEvent`. **Must-fail proven**: reverting the Sector.cs scaling reds it.
- `WarpBombTests.Domain_missile_between_old_and_scaled_warp_radius_triggers_warp_bomb`
  — the missile pass at the same between-radii distance arms a warp bomb.
  **Must-fail proven**: reverting the scaling reds it.
- `WarpTransitServerTests.ShipBetweenOldAndScaledRadius_GetsTransited_AndPeerSeesSectorChange`
  — the live proof above. **Must-fail proven**: reverting the scaling reds it
  ("ship NEVER crossed... got 1 SET_CLUSTER, sectors=[1051]").
- Updated `WarpProximityTests.ShipOutsideWarpRadius_NeverTriggersHitWarpEvent` —
  the old "outside" distance is now interior (expected consequence of widening);
  it now asserts outside the scaled radius.

## 7. Build / test status

- Whole-solution build: **0 warnings, 0 errors**.
- Core.Tests: **3065 total** (was 3061; +3 new tests, +1 net adjusted), 3 consecutive
  full-suite runs each **3064 passed / 1 failed**, the single failure being the
  documented pre-existing `LiveTurretGunnerBindingTests` machine-state flake (fails
  even on the pristine tree; not this stage's regression).
- MonoGame.Tests: **216 passed** (2× — the P237 `VisualScaleMultiplier` calibration
  test now exercises the aliased shared constant and stays green).
- ClientCore.Tests: **83 passed**.

## 8. Scope boundary / exclusions

- Funnel visual geometry/rotation/colour (P227/P237): **untouched** beyond the
  constant-reference change in `WarpRenderer`.
- Autopilot scanner/detection range: **not** gated by `warp.Radius` (checked),
  out of scope.
- Native reference (`~/Development/Allegiance`): read-only, untouched.
- Raw wire `DataWarpIGC.Radius`: never multiplied; the decode/encode round-trip is
  byte-faithful.
