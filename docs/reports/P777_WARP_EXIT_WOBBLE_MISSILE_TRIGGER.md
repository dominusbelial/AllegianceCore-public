# P777 — native's warp-exit wobble (exit half)

Base: master after P776 (this worktree is `pi/p777` at `f46cca2c`, the applied P776 change; the
`pi/p776` worktree carries the same change as `8806f482` on `57b930bd`). No commit/push; all changes
are left in the working tree.

**Scope.** This is the exit half of P777. The missile half (missile vs aleph cone, warp bomb, shared
trigger performance work) is `P777b` and owns `Sector.cs` + `AlephConeHitTest.cs`. P777 touched
neither of those, and none of P775's five files (`Server.Broadcast.cs`, `Server.Lobby.cs`,
`Server.cs`, `Server.Combat.cs`, `TrainingHost.cs`). No client file changed.

Goal: behaviour parity with native for **where and how a ship leaves the aleph, its heading, its
spin, and whether it re-triggers**. Visual fidelity is out of scope.

## 1. Native rule

`CFSShip::HitWarp` (`FedSrv/fsship.cpp:71-136`) is identical for drones and players; the only
player-specific line is `m_warpState = warpNoUpdate` (`:79-82`). Opened and read for this task:

1. **Base frame.** `Orientation alephOrientation = pwarpDest->GetOrientation()` (`:90`). The warp's
   orientation is built as `Orientation o(dataWarp->forward)` (`Igc/warpIGC.cpp:44-46`), i.e. the
   single-argument `Orientation::Set(const Vector& forwardAxis)` (`zlib/orientation.cpp:314-356`):
   `upAxis = cross((-1,0,0), forward)` with a `cross((0,-1,0), forward)` fallback when the forward is
   within ~5° of the x-axis, then the two-argument `Set(forward, up)`
   (`orientation.cpp:358-390`: `right = forward × up`, `up = right × forward`, all normalised).
   (The aleph also spins: `SetRotation(dataWarp->rotation)` at `warpIGC.cpp:47`, applied by
   `CmodelIGC::Update` at `modelIGC.cpp:85-100`. Not modelled — see §10.)
2. **Entry speed.** `v = m_pShip->GetVelocity()`, `speed2 = v.LengthSquared()`,
   `speed = sqrt(speed2)` (`:91-93`). **Wobble happens only if `speed2 > 0`** (`:94`): a stationary
   ship exits with the exact aleph frame and no roll bump.
3. **Error** (`:96-111`), over the **source** warp (the one that was hit) and the collision pose:
   - `dp = pwarp->GetPosition() − m_pShip->GetPosition()`;
   - `t = (dp · v) / speed2`;
   - `d = (dp − t·v).LengthSquared()` — the **squared** distance from the source aleph centre to the
     ship's line of flight;
   - `r = pwarp->GetRadius()`;
   - `error = d/(r*r) + 0.125f` — the comment says "Error ranges from 0.125 to 1.125";
   - `if (m_pShip->GetMass() > 300.0f) error = error * (300.0f / m_pShip->GetMass());` (`:105-108`).
4. **Pitch, then yaw.** `alephOrientation.Pitch(random(-error, error));` then
   `alephOrientation.Yaw(random(-error, error));` (`:113-114`). Both are pre-multiplied rotations
   about the frame's own right and up axes, in that order (`zlib/orientation.cpp:514-560`), in
   radians.
5. **Roll spin.** `SetCurrentTurnRate(c_axisRoll, GetCurrentTurnRate(c_axisRoll) +
   random(pi * 0.5f * error, pi * 1.5f * error));` (`:116-118`, "Must be less than 2.0 * pi"). Always
   positive, **added** to the current roll rate, roll axis only — yaw and pitch rates are untouched.
   The 2π bound is the wire range of `CompactTurnRates` (`char·2π/127`, port
   `Wire/Compact/Quant.cs:43-53`).
6. **Perturbed frame.** `SetOrientation(alephOrientation)` (`:120`),
   `const Vector& backward = alephOrientation.GetBackward()` (`:121`).
7. **Velocity.** `speed = -(speed + pwarp->GetMission()->GetFloatConstant(c_fcidExitWarpSpeed))`,
   then `SetVelocity(backward * speed)` (`:123-127`), `c_fExitWarpSpeed = 15`
   (`Igc/missionigc.cpp:2688`).
8. **Position.** `pwarpDest->GetPosition() + alephOrientation.GetUp()*random(2.0f,5.0f) +
   alephOrientation.GetRight()*random(2.0f,5.0f) − (m_pShip->GetRadius()+5.0f)*backward`
   (`:129-132`); all three axes come from the perturbed frame.
9. **Random.** `random(min,max) = ((float)rand())/RAND_MAX * (max*(1.0f - FLT_EPSILON) - min) + min`
   (`zlib/zmath.h:114-117`). Draw order is pitch, yaw, roll, up offset, right offset.

**Mass.** Native `GetMass()` is hull **plus parts** (`Igc/shipIGC.cpp:1402,1468,1496`). The port's
`Ship.Mass` is hull mass only (`src/Allegiance.Core/Sim/Ship.cs:208`); the new helper is given
`Ship.Mass`, so the `> 300` scaling and the P760 mass gate both inherit that known gap (same as
P760; out of scope).

**Client.** The online native client does not wobble; `WinTrekClient::HitWarpEvent` is offline-only
(`WinTrek/trekigc.cpp:4366-4372`, and its copy uses 2π as the roll upper bound, no mass scaling).
The client takes the server state: `FM_S_SET_CLUSTER` carries a `ServerSingleShipUpdate`
(`Inc/Messages.h:83-87`) exported with position, velocity, orientation **and turn rates**
(`Igc/shipIGC.h:1188-1199` `SetOVTP`; import `shipIGC.cpp:3074-3078`), built by `CFSPlayer::SetCluster`
(`FedSrv/fsship.cpp:742-767`) and applied by `clintlib/appmsg.cpp:990-1040`.

## 2. Port before and after

**Before (`f46cca2c` / `8806f482`), `ServerHost.HitWarpEvent`:**

- exit orientation `Orientation.Set(destForward, (0,1,0))` (port-only world-up roll), **no wobble,
  no roll bump**;
- velocity `backward * -(entrySpeed + FloatConstants[3])` along the **unperturbed** backward;
- position offsets `_rand.Next(20, 51) / 10f` — quantised to 0.1 (31 possible values) instead of
  native's continuous `random(2,5)`;
- `_rand` was `private readonly Random _rand = new()` (`:1453`), unseeded and not injectable; grep
  confirmed it was used **only** in those two offset draws.

**After:**

| Change | Where |
| --- | --- |
| New pure helper `AlephExit` (`internal static` + `internal readonly record struct Result`), allocation-free, injected `Random`; draws in native order (pitch, yaw, roll, up, right) | `src/Allegiance.Core/Model/AlephExit.cs` (new) |
| `HitWarpEvent` keeps the orchestration and calls the helper with the **source** warp (`rw.Warp.Position`, `rw.Warp.Radius`) and the partner (destination) warp; assigns position/velocity; adds the roll delta with `SetTurnRate(AxisRoll, current + delta)` (not clamped — the wire clamps to ±2π); mass gate, `LastArrivalWarpId`, sector move, `PlayerWarpState`, `LastWarpTime`, log line and events unchanged | `src/Allegiance.Server/ServerHost.cs:1521-1600` |
| Injectable exit RNG `internal Random WarpExitRandom { get; set; } = new()` replaces `_rand` | `ServerHost.cs:1452-1456` |
| Benchmark determinism: `_host.WarpExitRandom = new Random(seed ^ 0x57415250)` when `BenchmarkSeed` is set ("WARP" little-endian), a separate stream from `Server._rand` so no other seeded draw shifts | `src/Allegiance.Server/Server.Lifecycle.cs:776-784` |
| Exit base frame is now `Orientation.Set(forward)` (single-argument), as `warpIGC.cpp:45` | `AlephExit.Apply` |
| Comments corrected: the "Zero wobble" and "Forward + world-up" remarks, and the P406/P776 "no re-trigger" block now states that off-centre hits can leave the hull touching the cone natively too, players are covered by the NoUpdate handshake, port NPCs by the port-only cooldown | `ServerHost.cs:1477-1502`, `1584-1591` |
| `InternalsVisibleTo("Allegiance.Server")` added to Core so `ServerHost` can call the internal helper (the brief's design: helper `internal`) | `src/Allegiance.Core/Allegiance.Core.csproj:12-16` |

The helper carries a doc comment citing every native line above. `Error` is returned in `Result` for
logging/telemetry; the `[warp] … crossed` line was deliberately **not** changed (bots and tests grep
`crossed from`), so `Error` is currently only observed by tests. `grep -rn '_rand' src/Allegiance.Server`
now shows no `ServerHost` use.

## 3. Step 0 findings

- `src/Allegiance.Core/Model/AlephConeHitTest.cs` exists and P776 is in the history (`f46cca2c`);
  base is correct.
- **Roll-rate integration.** `Ship.ExecuteMove` delegates to `ShipKinematics.Step`
  (`Ship.cs:5131-5151`). The step ramps each rate toward
  `Controls.TurnAxis(k) · l · MaxTurnRate(k)` by at most
  `maxDelta = TorqueMultiplier(|v|/maxSpeed) · dT/mass · TurnTorque(k)` per step
  (`ShipKinematics.cs:96-115`; native `shipIGC.cpp:2885-2910`, exactly the same clamp). With neutral
  controls the desired roll rate is 0, so the exit spin decays by at most that limit per tick and is
  **never snapped**. Test 5 measures the per-step delta equal to the limit to the last float
  (`0.433286 / 0.429817 / 0.426312` vs the same limits).
- **No NPC/autopilot path overwrites the roll rate on the next tick.** The only `SetTurnRate` callers
  are transitions, not per-tick steering: eject sets roll `2π` on the *new* lifepod
  (`Server.Combat.cs:2340`), ripcord zeroes all three (`Server.Docking.cs:1340-1342`), station
  relaunch zeroes all three (`Station.cs:137`). Steering (dock approach, wingman, dodge) only writes
  `Controls`, which ramps through the torque limit. `ExecuteTurretMove` (`Ship.cs:5290-5291`) sets
  `_turnRates` outright, but it is native's *turret* slew (`shipIGC.cpp:1645-1646`; native's turret
  is a separate object) and never runs for a warping flight ship's own tick. Nothing was changed.
- **Client path (no change expected, none made).** `ServerShipUpdate.WriteModel` writes the three
  turn rates (`ServerShipUpdate.cs:135-137`, `CompactQuant.TurnRate`). `Server.OnShipWarped` builds
  `SET_CLUSTER` with `ServerShipUpdate.FromModel(...)` **after** `HitWarpEvent` set the pose
  (`Server.Combat.cs:2521-2543`). The client reseeds its own-ship predictor on the sector change from
  the server pose, turn rates included (`Game1.Prediction.cs:204-205`
  `_ownSim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates)`, called from the freeze edge
  at `:153` and the resume edge at `:191`).
- **Existing warp tests — hit geometry (d/R off-axis distance and entry speed).** `d` below is the
  off-axis distance, so native `error = (d/R)² + 0.125`.

| Test | d/R | entry speed | asserts exit pose/rates? |
| --- | --- | --- | --- |
| `P650ServerRulesTests.Warp_exit_uses_loaded_slot_and_preserves_entry_speed` | 51.7 (ship at the origin, 5.1 km off the aleph's line; the site is called directly, outside any trigger) | 17 u/s | no — sector + speed only; `error ≈ 2668`, an orientation native can never produce because the trigger never fires out there |
| `P760ServerCallbacksTests.HitWarpEvent_mass_limit_gate_matches_native` | 0 | 0 (parked at the warp) | no (sector) |
| `P760ServerCallbacksTests.Warp_state_handshake_follows_native_update_cycle` | 0 | 0 | no (warp state) |
| `WarpTransitServerTests` (transit / SET_CLUSTER) | 0 | 20 | no |
| `WarpTransitServerTests.ShipExitSpeed_IsEntrySpeedPlusNativeBonus_AfterTransit` | 0 | hull.Speed (Adv Miner 120) | speed only |
| `WarpTransitServerTests.ShipExitPosition_IsAtShipRadiusPlusFive_FromDestinationWarp` | 0 | hull.Speed (120) | distance to the aleph (frame-independent) |
| `P776AlephConeTriggerTests.Ship_reaching_the_aleph_still_warps` (front/back/slow/centre) | 0 / 0 / 0 / 0 | 100 / 100 / 5 / 0 | no (trigger) |
| `P776AlephConeTriggerTests.Fast_ship_crossing_the_rim_is_caught_by_the_sweep` | 0.95 | 1000 | no (trigger) |
| `P776AlephConeTriggerTests.Exited_ship_lingering_in_front_of_the_destination_aleph_does_not_rewarp` | 0 | 30 (Guardian) / 3 | crossing count + sector |
| `P776AlephConeTriggerTests.Two_ships_warping_together_do_not_bounce` | 0.01 (1 u side offset) | 30 | crossing count |
| `P776AlephConeTriggerTests.Npc_exited_slowly_does_not_rewarp` | 0 | 60 | crossing count |
| `P591bLayerPingPongTests`, `P591cDroneRoutingTests`, `NpcCrossClusterLiveTests`, `MultiHopDroneLiveTests` | routed drones aim at the warp centre; measured 0.000 over 9 real transits (test 6) | cruise | no (route/crossings) |
| `WarpProximityTests` | — (trigger only; `RecordingIgcSite`) | — | no |

`P776AlephConeTriggerTests.Test 2/3` hold the ship at the exit spot; with `error = 0.125` the exit is
clear (§4), so they still see exactly one crossing.

## 4. Exit-clearance analysis: what native guarantees, and what it does not

Native positions the exit centre `z = u·sinθ − w·sinφ·cosθ − (r+5)·cosθ·cosφ` along the destination
backward (`z > 0` behind the disc; `θ` pitch, `φ` yaw, `u,w` the up/right offsets) and then orients
the velocity along the perturbed backward. The P776 shape (`AlephConeHitTest`) is the triangle
`(0,0),(R,0),(0,R)` in `(rho,z)`. **Verified by measurement** (test 4, 20,000 seeded draws each,
`R = 100`, `error = 0.125` for a centre hit):

| ship radius r | exits touching | min clearance to the cone (surface − r) | no-wobble clearance |
| --- | --- | --- | --- |
| 5 | 0 / 20000 | 3.762 u | 5 u |
| 10 | 0 / 20000 | 3.634 u | 5 u |
| 25 | 0 / 20000 | 3.518 u | 5 u |
| 50 | 0 / 20000 | 3.098 u | 5 u |

So a **centre hit** (`d = 0`, `error = 0.125`) always exits clear; the worst clearance is ≈3.1-3.8 u
in the sphere/cone model (Claude's arithmetic in the brief predicted ≈3.5 u at r = 10 and 3.3 u at
r = 25 — the same order; the r = 50 case is a little tighter at 3.10 u because the `(r+5)` depth term
shrinks as `cosθcosφ` while the lateral jitter does not).

A **rim hit** (`d/R = 1.0`, `error = 1.125`) **does** leave the hull touching the destination cone,
which is native behaviour (no "fix"):

| ship radius r | exits touching (20,000 draws) |
| --- | --- |
| 5 | 6659 = 33.29 % |
| 10 | 9557 = 47.78 % |
| 25 | 15360 = 76.80 % |
| 50 | 17550 = 87.75 % |

(Claude's brief expected 30-60 % at r = 10; measured 47.8 %. The cone trigger also admits grazes at
`d/R` up to `1 + r/R`, i.e. `error` up to `(1+r/R)² + 0.125` — 1.335 at `R = 100, r = 10` — so the
true worst case is a little higher than the `d/R = 1.0` row above.)

**So the P776 claim "the exit is 5 u in front of the disc, so the hull is clear of the cone" is only
true for near-centre hits.** Natively a *player* is protected by `warpNoUpdate` (≥ 2 s,
`fsship.cpp:79-82,1185-1191`) and by the exit velocity's forward component
`cosθ·cosφ ≥ cos²(1.125) ≈ 0.18`; by the time it re-arms it has drifted out. **Drones never leave
`warpReady`**, but native drones aim at the aleph centre, so `error ≈ 0.125` and they exit clear:
measured below, all 9 real drone transits had `d/R = 0.000` and none touched. The port keeps its
port-only NPC cooldown (`Sector.WarpCooldownSeconds`) unchanged (§10).

## 5. Tests (`tests/Allegiance.Core.Tests/P777WarpExitWobbleTests.cs`, new)

| Test | Pins | On the unfixed base |
| --- | --- | --- |
| `Exit_orientation_is_wobbled_within_native_bounds_and_reproducible` | through `ServerHost.HitWarpEvent`: `d/R = 0.5`, 50 u/s, Fighter hull 121 (mass 30 ≤ 300), `error = 0.375`; `0 < max(|θ|,|φ|) ≤ e`; roll delta in `[π/2·e, 3π/2·e]`; speed `entry+15` along the **perturbed** backward; same seed → bit-identical pose/roll | **fails** (measured: θ = 2.07e-8, φ = −2.24e-8, roll = 0) |
| `Stationary_ship_exits_with_the_exact_aleph_frame` | speed 0 → exact `Orientation.Set(destForward)`, no roll bump, native position on the unperturbed frame; destination warp 2156 (`forward = (1, −0.811, 0.088)`), whose `Set(forward)` up `(0, 0.1074, 0.9942)` differs from the old `Set(forward, worldUp)` up `(0.6260, 0.7779, 0.0548)` — asserted, so the test discriminates | **fails** (the old world-up frame) |
| `Wobble_distribution_matches_native_bounds` | helper, 20,000 seeded draws for `d/R ∈ {0, 0.5, 1.0}` × mass `{100, 600}`: angles within ±e, extremes past ±0.95e, `|mean| < 0.03e`, roll delta in range with mean ≈ π·e (±3 %), mass 600 halves e, offsets in `[2,5)` and continuous (> 1,000 distinct) | **offset continuity fails on the base** (measured: 31 distinct values from `_rand.Next(20,51)/10f`); the helper does not exist there |
| `Exit_clearance_matches_the_native_analysis` | centre hits never touch (r = 5/10/25/50) with min clearance; rim hits do (fraction > 0) | centre part **passes** on both (measured 5.0001 u on the base); rim part **fails** on the base (measured 0/2000 with a seeded `_rand` through `HitWarpEvent`) |
| `Exit_roll_spin_decays_under_neutral_controls` | post-exit roll rate > 0, then each `Ship.ExecuteMove` step moves it toward 0 by at most the torque limit and does not snap it | **fails** (measured post-exit roll rate = 0) |
| `Npc_exit_touch_rate_is_measured` | report-only: 6 routed layer drones, seeded exit RNG, 60 s (then 600 s); counts of transits, exits touching the destination cone, and re-warps inside 2.2 s | report-only (runs on both; the numbers are the point) |

Which tests failed on the unfixed code: **1, 2, the offset-continuity part of 3, the rim part of 4,
and 5** — all demonstrated above by re-running base-compatible copies of those assertions against the
stashed base (the helper-based parts cannot compile on the base). The centre part of 4 and every
item-7 test pass on both.

**Fixture changes: none.** Every warp test listed in §3 that asserts a position, distance, speed,
sector, warp state or crossing count is frame-agnostic or enters on the axis (`d/R ≈ 0`, `error =
0.125`), where the native frame and the old world-up frame both leave the hull clear. No test was
re-seeded, no gate widened, no wobble shrunk.

## 6. Measured distribution numbers (test 3)

20,000 seeded draws per cell; all assertions held. `e` is the native error, `roll` the turn-rate delta.

| d/R | mass | e | θ range | mean θ | φ range | mean φ | roll range | mean roll (π·e) | distinct up/right |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0.0 | 100 | 0.125 | [−0.12499, 0.12499] | 0.000317 | [−0.12500, 0.12492] | −0.000335 | [0.19640, 0.58904] | 0.39323 (0.39270) | 19976 / 19986 |
| 0.0 | 600 | 0.0625 | [−0.06250, 0.06250] | 0.000219 | [−0.06249, 0.06249] | 0.000006 | [0.09823, 0.29451] | 0.19544 (0.19635) | 19972 / 19979 |
| 0.5 | 100 | 0.375 | [−0.37498, 0.37500] | 0.000093 | [−0.37495, 0.37499] | 0.002002 | [0.58911, 1.76712] | 1.17973 (1.17810) | 19976 / 19983 |
| 0.5 | 600 | 0.1875 | [−0.18744, 0.18742] | −0.000389 | [−0.18750, 0.18744] | −0.000409 | [0.29454, 0.88356] | 0.59074 (0.58905) | 19979 / 19977 |
| 1.0 | 100 | 1.125 | [−1.12496, 1.12495] | −0.001392 | [−1.12499, 1.12490] | −0.003872 | [1.76729, 5.30101] | 3.54462 (3.53429) | 19981 / 19984 |
| 1.0 | 600 | 0.5625 | [−0.56245, 0.56248] | −0.001216 | [−0.56248, 0.56237] | −0.000202 | [0.88363, 2.65059] | 1.76484 (1.76715) | 19984 / 19972 |

Test 1 (through the real site, seed 1234, `e = 0.375`): `θ = −0.0757`, `φ = 0.2969`,
roll delta `= 0.9651` (range `[0.5890, 1.7671]`), speed `65.000` along the perturbed backward; the
second run with the same seed reproduced the orientation, position, velocity and roll delta exactly.

## 7. Measured gate numbers

- **Whole-solution build**: `dotnet build AllegianceCore.slnx` → 0 warnings, 0 errors.
- **AOT publish** (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release
  -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot`): success, **0 warnings**,
  no IL2026/IL3050; 14 MB native `Allegiance.Server` produced.
- **Each new/affected class 3× alone**: `P777WarpExitWobbleTests` (8/8), `P776AlephConeTriggerTests`
  (18/18), `WarpProximityTests` (6/6), `WarpTransitServerTests` (4/4), `P650ServerRulesTests` (3/3),
  `P760ServerCallbacksTests` (13/13), `P591bLayerPingPongTests` (3/3), `P591cDroneRoutingTests`
  (6/6), `NpcCrossClusterLiveTests` (7/7), `MultiHopDroneLiveTests` (1/1), `MapGenLiveProofTests`
  (2/2), `MapGenRingLiveProofTests` (3/3), `P616TrackerNativeBugTests` (5/5),
  `ClientWarpApproachDodgeLiveTests` (1/1), `ClientWarpApproachSlowHullLiveTests` (2/2),
  `P560PodWarpLegClosedLoopTests` (1/1), `P699PodHomeLiveTests` (2/2),
  `P539dFairContactLiveTests` (2/2), `P610AlephTrafficLiveTests` (1/1),
  `ShipDeleteGoneLiveTests` (3/3). All three runs green for every class; no flakes.
- **Fast tier** (`tools/run-tests.sh --tier fast`): **9867 tests, 283.0 s, PASS** (Lobby 24,
  MonoGame 3187 passed + 3 not-executed, ClientCore 1607 + 2 quarantine, Core 5034 + 10 quarantine).
- **Showcase never-parks** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`),
  **5/5 passed after**: 110.5 / 109.4 / 108.8 / 109.0 / 108.9 s, longest "speed < 1 with target"
  2.004 / 2.006 / 1.919 / 2.004 / 1.604 s (limit 5 s), reached 28 / 26 / 29 / 26 / 29 of 30 bots.
  **5/5 passed on the base** (109.9 / 109.5 / 110.0 / 108.0 / 108.6 s, longest 2.014 / 2.006 / 2.006 /
  2.607 / 1.905 s, reached 29 / 29 / 29 / 27 / 29). Load-sensitive, so both sides were measured; no
  failure, so no flake adjudication was needed.

### P776 bounce class under wobble (showcase `[warp] … crossed` log)

To attribute crossings in time, a temporary `t=<missionTime>` suffix was added to the
`[warp] … crossed` line for the measurement runs only (base and after) and then removed; the shipped
line is unchanged (prefix preserved). One-past-the-end crossing counts, same runs:

| | crossings (5 runs) | same ship returning to the sector it just left, `< 2.2 s` | same ship returning at any gap |
| --- | --- | --- | --- |
| after (wobble) | 70, 66, 71, 68, 72 (347) | **0** | **0** |
| base (P776, no wobble) | 72, 71, 71, 69, 71 (354) | **0** | 1 (ship 8105: 1062→1052 at 176.9 s, 1052→1062 at 236.4 s — a legitimate 59.5 s return trip) |

**Wobble does not bring the P776 jump-point bounce class back.**

### NPC exits (test 6, seeded `WarpExitRandom`)

Six layer drones routed to Alderan through real alephs, 60 s (brief's window) and then 600 s:

| window | transits | exits touching the destination cone | re-warps within 2.2 s |
| --- | --- | --- | --- |
| first 60 s | 6 | 0 | 0 |
| full 600 s | 9 | 0 | 0 |

All 9 transit hits were on the centre line: measured `error = 0.1250` (`d/R = 0.000`) for every one,
and 6/6 drones laid. So **port NPC exits are centre hits, exit clear, and re-warp zero times** — the
data says the port-only NPC cooldown is not load-bearing for these routes (removing it is Domingo's
call, §10). The brief's bot-entry-spread estimate (≈1.2 % at r = 10 for `EntrySpreadFraction = 0.7`)
was **not** exercised: these drones steer at the warp centre, and bot entry spread is out of scope.

## 8. Matrix

`docs/parity/sim-rules-matrix.csv`, row `SIM-0208` (`HitWarpEvent@4862`): appended to `notes`
(col 16) exactly

```
; P777: exit wobble as native (fsship.cpp:90-132): pitch/yaw random(±error), error = d/R²+0.125 scaled by 300/mass over 300, roll rate += random(π/2·e, 3π/2·e), base frame Orientation(forward) (warpIGC.cpp:45); AlephExit + P777WarpExitWobbleTests.
```

(quoted field, because it contains commas). Status stays **`partial`**: the exit formula now matches
native, but the aleph **spin** (`warpIGC.cpp:47`, `modelIGC.cpp:85-100`) is not modelled, ship mass is
hull-only rather than hull+parts (`shipIGC.cpp:1468,1496`), and the ship is the bounding sphere
`0.5·Length` rather than its convex hull. `SIM-0004` (`c_fcidExitWarpSpeed`) unchanged;
`docs/parity/parity-matrix.csv` has no warp-exit row and none was added. LF endings kept; a full
parse shows all 474 rows have **16 fields**; `git diff --stat docs/parity` = `1 file changed,
1 insertion(+), 1 deletion(-)`.

## 9. Out of scope / follow-ups

- **Aleph spin** — native warps spin about `dataWarp->rotation` (`warpIGC.cpp:47`,
  `modelIGC.cpp:85-100`), so the exit roll base turns with time; the port uses the t = 0 frame. Roll
  only, and the pitch/yaw wobble is symmetric.
- **Ship mass including parts** (`shipIGC.cpp:1468,1496`) — affects the `> 300` scaling and the P760
  mass gate; a separate task.
- **Removing the port-only NPC `WarpCooldownSeconds`** — Domingo's call after the test-6 numbers
  above (0 touches, 0 re-warps in 9 transits).
- **Bot entry spread** (`BotManager.EntrySpreadFraction = 0.7`) and bot exit steering — candidates
  if a future measurement shows off-centre bot hits touching cones. Not measured here.
- The client: no change. Native clients take the server state.

## 10. Process note

This worktree shares the repository's single `refs/stash` with the other pi worktrees. While the base
comparison was running, a parallel agent's `git stash`/`pop` collided with mine: an old unrelated
stash (`P576: park play-client local patches`) landed in this worktree with conflicts while my own
stash was popped elsewhere. The conflicts were discarded back to HEAD, the foreign stash entry was
left intact, and my changes were recovered from the stash commit (`9f2b09d`); this affected only the
process, not the delivered code — the final build, AOT publish, fast tier and every test class above
were run on the restored tree. No file outside this worktree was edited.
