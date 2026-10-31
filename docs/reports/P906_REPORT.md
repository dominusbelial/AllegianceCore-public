# P906 — experimental native tick order

Worktree lane: Codex. Changes are uncommitted; no pushes or remote-host contact.
All three switches remain **default off**. All three matrix statuses remain
**partial**; the 12 points are withheld pending Claude's rig verdict.

## Rules and deterministic findings

| Row | Native source | Production path | Verdict and regression |
|---|---|---|---|
| SIM-0062 (4) | `Igc/clusterIGC.cpp:170-231,241-253`; ship phase bodies at `Igc/shipIGC.cpp:1704,2059,2841` | `src/Allegiance.Core/Model/Sector.cs:1010`; `src/Allegiance.Core/Sim/Ship.TickOrder.cs:11` | **Partial; order difference confirmed.** `sim.nativeShipPhases` performs all preplots, all plots, then all parentless executes before ordinary model updates. The ordinary-model witness sees every ship's new velocity. A projectile burnout preceding a ship in the model list kills it before its execute on the legacy path, but after execute with the global phases. `P906TickOrderTests.All_ship_execute_precedes_ordinary_models_only_when_enabled`, `Burnout_blast_before_ship_update_observes_executed_velocity_only_with_global_phases`, `Starting_stage_does_not_execute_ship_phase_passes`. |
| SIM-0063 (5) | `Igc/clusterIGC.cpp:255-345,592-706`; `_Utility/HitTest.cpp:187-202` | `src/Allegiance.Core/Model/Sector.TickOrder.cs:31`; shared responder `src/Allegiance.Core/Model/Sector.cs:2332`; movement deferral in Ship/Projectile/Missile/Chaff/Treasure | **Partial; order difference confirmed.** `sim.nativeCollisionQueue` uses one time queue for the supported contact classes. A rock hit at t=0.5 kills before a later projectile, while legacy combat dispatch kills with the projectile at the tick end. A bounce introduces a projectile contact absent from the initial sweep; tail recalculation finds it. A missile reaches an aleph before a later ship contact and is consumed there. Tests: `Earlier_rock_contact_preempts_later_lethal_round_only_in_time_queue`, `Bounce_recalculates_tail_and_discovers_previously_absent_projectile_contact`, `Earlier_aleph_contact_consumes_missile_before_later_ship_contact`. |
| SIM-0141 (3) | `FedSrv/FedSrv.CPP:4238-4254` | Existing `src/Allegiance.Server/Server.NativeBroadcastOrder.cs:41,83` | **Partial; wire-state difference confirmed.** Existing `sim.nativeBroadcastOrder` remains independent and off. The same-tick own/HEAVY wire pose is from the previous completed tick when on. A fixed-input combat replay has equal authoritative positions and hull fractions across both schedules. This does not assert equivalence once client reactions depend on the changed wire observations. Tests: `P906ProductionOrderTests.Broadcast_boundary_changes_same_tick_wire_pose_but_preserves_fixed_input_combat`; existing `P788cNativeTickOrderTests.Ship_poses_are_projected_once_at_the_selected_update_boundary`. |

Other new fixtures cover inactive/not-started collision guards, repeated/reversed
clocks, newborn lifetime clipping (including creation inside a target), mine
application before final movement, stable simultaneous pickup ordering, defaults,
and independent flag binding to every assembled server mission. The off arms
explicitly pin the old order. Source line numbers above were rechecked against
the read-only native checkout.

## Implementation and limits

The flags are server JSON booleans, bound at every mission assembly, including
rotation/provisioning. No new environment variables, services or ports were
introduced. Boot logs and profiler labels include the new values. The existing
broadcast environment override still takes precedence over its JSON setting.

The collision branch keeps newborn objects out of sweeps before their creation,
skips removed/dead/docked/boarded objects, accumulates mine contacts before
applying the largest hit per ship in reverse ship order, and completes movement
only after contacts and mines. Boundary contacts also support an exactly zero remaining interval, as native
`HitTest::Collide` permits. The public legacy narrow-phase API still requires a
positive step; only the experiment calls the boundary entry. This fixes the
200-client development run's caught zero-duration exception and is covered by
end-of-tick newborn and simultaneous-impact regressions. A kinematic response advances unaffected models
to the contact time and rebuilds the remaining sweep. No iteration ceiling drops
later collisions. Existing collision responders are shared with the off path.
Ordinary station regeneration precedes contacts in this branch. Parallel
broadcast capture/build/publication remains enabled throughout the local proof.

These are ordering experiments, **not full native engine certification**:

- Existing explosion delivery remains synchronous with position projection;
  native's complete post-Move explosion scheduling and all model-lifetime bound
  details are not re-ported by this ordering experiment.
- Existing sector economic passes (mining/construction/ripcord) and deferred
  server site drains retain their port integration boundaries. This lane does
  not re-port every statement of native ship Preplot/Plot or every site callback.
- Aleph contact retains the existing cone versus bounding-sphere approximation;
  docking retains existing geometry/fallback policy. Generic ship/probe bounce
  and ordnance/missile direct-hit coverage are not expanded here.
- Equal-time contacts use the port's deterministic collider ordinal tie order;
  native's exact equal-time permutation is not independently certified.
- Recalculation rebuilds the supported remaining sweep rather than native's
  targeted KD-tree flush/retest. Its contact cost must be judged on a real clash.
- SIM-0141 still retains existing send floors, ranking/cache cadence, detached
  snapshot publication and coalescing adaptations. A native pre-update boundary
  alone does not certify every native network scheduling detail.

These limits are reasons to keep the rows partial beyond any local performance
observation. Rig acceptance alone must not be described as complete native parity
for the unported integration/geometry cases.

## Local 200-client comparison

The standalone tool is `tools/p906-tick-order-proof/`. It runs a separate server
process and 200 real TCP observers on loopback, so process-wide allocation includes
server workers without including client allocation. The Debug/JIT fixed firing-range
fixture has 100 stationary opposing pairs with held server controls, durable hulls, and ammo/energy refilled outside
the timed tick to sustain actual weapon traffic. It excludes the first ten
seconds of battle and measures 400 ticks over twenty nominal simulation seconds.
This is a bounded 100v100 local proof. It does not measure maneuvering fleets,
kill/respawn turnover, or the rig's 200v200 match verdict.

Baseline settings: immutable snapshots, encode-once, parallel build (four
workers), native ship-update model, pooled server transport (four workers),
native GotoPlan and server GC. Interest tiers, receive decoupling and the other
experimental tick-order flags stay off. Crowding is disabled identically in all
arms to keep the firing-range population present. Map/core are XXV_simple and
PCore001; benchmark seed is 906. The firing range uses 100-unit spherical hulls, 24 fixed asteroids, and
random treasure regeneration is disabled; arena warps/stations are removed. File-map top-up uses `Random.Shared`
independently of the benchmark seed: leaving that arena geometry uncontrolled
confounded early development measurements. Reactive client input timing was
also removed from the final fixed-range comparison. The spawner and initial
POSITIONREQ agree on side 0 to avoid the production home-sector reconciliation;
then the range assigns 100 opposing pairs. Manual joins disable the bot's
additional automatic side request. The tool verifies that all 200 ships remain
in the arena, all 100 pairs remain opposed, and no simulation stage fails. The dispatch-base source was reconstructed under
ignored scratch, without changing HEAD or creating a commit.

An additional [fixed-input replay](P906_assets/trace-equivalence.json) compares
HEAD `84597d2c3ebdc441d18494cbf40a4d19d735cc54` with the working tree's off
path: **4,401,600 trace bytes match bit for bit**, covering 200 ships, 1,000
projectiles and 400 production mission updates. SHA-256:
`7B5E31264FBC932385D09E120835FF595D2142EC472760837E7E2367DFACAE4D`.
The trace covers positions, velocities, forward orientations, hull fractions,
controls, energy/ammunition, death/docking state and arena model population.
This certifies the replay's unchanged off-path outcomes; noisy socket timings
are a separate measurement. Idle, dry-gun and uncontrolled-geometry development
runs are excluded from final timing comparisons.

[Six final runs](P906_assets/loopback-summary.json), 400 measured ticks each:

| Arm | Tick mean (ms) | Tick p95 (ms) | All-thread allocation mean (KiB/tick) |
|---|---:|---:|---:|
| base | 16.640 | 25.825 | 926.90 |
| off | 17.083 | 25.935 | 926.19 |
| phases | 17.363 | 27.536 | 931.43 |
| collisions | 14.257 | 21.783 | 921.07 |
| broadcast | 21.350 | 27.520 | 1049.17 |
| off-repeat | 16.257 | 23.317 | 920.78 |

Every arm recorded **2,000 shots, 11,200 damage events, zero kills, zero dead
initial ships, 200 live arena ships, 100 opposed pairs and zero simulation-stage
failures**. Final hull-fraction sum was identical: **151.40645027160645**. Durable
hulls intentionally prevent kill/respawn turnover in this bounded sample.

Against the first working-tree off arm, phase barriers cost +1.6% mean / +6.2%
p95 with +0.6% mean allocation; the collision queue measures -16.5% mean /
-16.0% p95 with -0.6% allocation; broadcast-before-update costs +25.0% mean /
+6.1% p95 with +13.3% allocation. The second off arm is itself 4.8% faster in
mean and 10.1% faster at p95. Host one-minute load at arm start ranged from 2.13
to 11.16; exact samples are retained in the receipt. These unpaired local
samples support a promising collision candidate and an expensive broadcast
candidate, but do not establish a rig win or a significant phase regression.
The dispatch base's mean and p95 both fall between the two working-tree disabled
measurements; there is no resolved disabled-path timing regression here.
The off trace establishes behavioral equivalence; socket timing is comparable
rather than bit-identical. All flags remain off pending the rig's decision.

## Qualification

- Whole solution: build passed with **0 warnings / 0 errors**.
  [Build/configuration checks](P906_assets/build-summary.json).
- Standalone tools: **102/102 build**, zero warnings/errors; the final proof
  revision also builds cleanly against both the dispatch base and working tree.
- Server native AOT: **passed**, including the final implementation, with no
  IL2026/IL3050 or other warnings.
- New tests: **29 cases × 3 = 87 passes**, each method selected alone in three
  separate runs. [Receipts](P906_assets/isolation-summary.json).
- Affected broadcast/collision/sector/expiry cohorts: **387 passes**, three
  repetitions per cohort. [Receipts](P906_assets/affected-summary.json).
- Single Fast sweep: **12,778 passed / 15 failed / 3 optional skips**, 540.1 s;
  nine infrastructure checks pass and six executions exceed ten seconds.
  [Receipt](P906_assets/fast-summary.json). One task regression in P754's private
  reflection signature was repaired by restoring the original wrapper methods;
  its complete cohort passes **24/24** in three isolated runs. The other failures
  and timing cases have three-run isolation/control results in
  [TEST_FLAKES.md](../TEST_FLAKES.md) and
  [receipts](P906_assets/failures-summary.json). Auto-seat/defend failures remain
  open; writable-profile and descriptor-path controls identify sandbox/path
  issues. The full Fast sweep is not repeated, as the brief requests one run.
- Showcase never-parks: **5/5 pass**, serially on the winning baseline with
  all experimental flags off. Each reaches 24/30 objectives, observes real
  shots/hits, and asserts zero simulation-stage failures.
  [Receipts](P906_assets/showcase-summary.json).
- No HUD/training changes; no screenshots or training gate are required.

| Showcase run | Test seconds | Shots | Hits | Kills | Longest stopped with target (s) |
|---|---:|---:|---:|---:|---:|
| 1 | 215.204 | 583 | 1 | 0 | 0.205 |
| 2 | 460.195 | 1154 | 187 | 3 | 0.505 |
| 3 | 221.579 | 288 | 2 | 0 | 0.000 |
| 4 | 228.158 | 778 | 42 | 0 | 0.000 |
| 5 | 196.831 | 537 | 149 | 1 | 0.907 |

The saved P904 and P903 baselines also pass 5/5. P904's test durations were
204.128–242.163 s with a maximum stopped-with-target interval of 1.919 s.
P906 retains that pass rate and remains below the five-second assertion. Run 2
needed 456.5 s of observation to satisfy the unchanged coverage conditions;
showcase duration is not a controlled performance A/B. No assertion was weakened.

Raw local receipts are retained under ignored `.scratch/p906/`: `isolation/`,
`affected/`, `failures/`, `fast/`, `showcase/`, `final-loopback-{arm}/`, and
`trace-{base,off}-final/`, plus `build-solution.log`, `build-tools.log`, `aot.log`,
`base-build.log` and `finish-verification-progress.log`. Superseded development
runs and temporary build copies were removed. Only small summary receipts are retained in
`P906_assets/` (under 2 MB).

## Scorecard

[Before](P906_assets/scorecard-before.txt) and
[after](P906_assets/scorecard-after.txt): simulation headline remains
**2092/2144 = 97.57% weighted; 780/805 = 96.89% unweighted**. The master matrix
remains **1881.500/1881.500 = 100%**. The two new experiments are correctly
classified config-only; no status or weight is changed, and no unrelated parity
row is edited.

## Claude's rig plan

No rig, mini or Mac was contacted. Apply the existing winning settings unchanged:
immutable snapshot + encode-once, P863 clash budget, parallel build, pool
transport, native ship-update model, native GotoPlan and server GC. Explicitly
force GotoPlan on if the deployment's environment still overrides its code
setting. Leave interest tiers and receive decoupling off.

Run the brief's two-match 200v200 A/B separately for each candidate (one baseline
match and one treatment match), with identical map/civ seeds, roster, skill,
staging/finale settings and duration. Change exactly one JSON boolean:

1. `sim.nativeShipPhases`: false → true; collision queue and broadcast order off.
2. `sim.nativeCollisionQueue`: false → true; ship phases and broadcast order off.
3. `sim.nativeBroadcastOrder`: false → true; both new flags off. Ensure its
   existing environment override agrees with the JSON arm.

Capture boot/profiler labels, match/seed/civ identity, measured live populations,
tick mean/p95/max and overruns, all-thread allocation/GC pauses, snapshot
coalescing/encode failures, and shots/damage/kills/never-parks evidence. Keep a
candidate only when tick cost improves or holds with comparable active combat
coverage; fewer live players, idle guns or a shorter fight cannot earn a win.
If host load makes the result inconclusive, counterbalance the order in a fresh
pair. Record keep/reject separately per flag; do not enable combinations or
promote matrix status from these experiments alone.

## Native intent

**Design:** phase barriers and chronological collision dispatch make same-tick
planning, impact precedence and pre-update network observations deliberate.
**Era constraint:** the original implementation expresses this through mutable
linked lists, one thread and KD-tree hit-test state. **Bug:** the experimental
zero-duration sweep exception and the private-signature regression were found and repaired during qualification. **Port divergence:** the
one-ship update and separate class passes are integration adaptations that can
change outcomes; they are not evidence that the native rules were unused.
A modern implementation could plan from immutable per-tick state and parallelize
pure detection, then reduce impacts deterministically on one simulation thread,
with targeted trajectory invalidation after bounces. That preserves the ordering
contract while leaving detached broadcast work parallel.
