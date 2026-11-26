# P1086 — A living fleet for P835Bench

Base `225d20fd`. Work started at 22:01 UTC on 2026-11-26. Elapsed times are approximate and overlap where items shared a run.

## Brief items

| Item | Status | Elapsed | Evidence |
|---|---|---|---|
| 1. Reproduce fleet loss | done | 2 min | Stock replay below. |
| 2. Sustain a living fleet | done | 3 min | Movement, hits and hull checks below. |
| 3. Guard the population | done | 2 min | Live minimum 400 and legacy exit 1. |
| 4. Separate contact and sustained phases | done | 2 min | 20 fresh sweeps, 50 settled sweeps and spotter witness below. |
| 5. Measure both fixtures | done | 17 s for the matrix, ~2 min to tabulate | Twelve processes and 24 phase results below. |
| 6. Document usage and pointers | done | 1 min | Program usage, performance guide, P835 pointer and glossary links checked. |

## Stock replay

A Release build of the base fixture ran 560 updates at 0.053 s with 400 ships,
240 rounds and 60 mines. A scratch observer counted live, undocked, unboarded
ships after each update and recorded the caller of `ReceiveDamage` on each
death. The seed, positions, hull, ordnance and simulation inputs came directly
from the base program.

| Pass | 1 | 5 | 10 | 20 | 23 | 24 | 40 | 60 | 100 | 560 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Flying ships | 400 | 400 | 400 | 399 | 399 | 0 | 0 | 0 | 0 | 0 |
| Ship objects | 400 | 400 | 400 | 400 | 400 | 400 | 400 | 400 | 400 | 400 |

Sector crowding cost was 0.04346 on every sampled pass. The core sums weights
over sector members, including the retained dead hulls, scales by the timestep,
and charges each eligible ship that fraction of its maximum strength.
All 400 final blows came from `Ship.OnUpdate → ReceiveDamage`. Direct rounds,
blasts and physical collisions also deliver damage. A separate stock replay
recorded nine direct round hits on ships and one ship–asteroid damage callback
by pass 60. The stock rounds and
mines have power 0.000001. Mean hull after the first pass was 0.956206, and
0.000419 remained at pass 23. The stock site leaves zero-hull objects in place.
See `src/Allegiance.Core/Model/Sector.cs:1014–1027,2450–2459,3063–3067,3344–3348,4145`
and `src/Allegiance.Core/Sim/Ship.cs:682,5596–5613,9783–9793`.

## Living fixture and measurement boundaries

Both fixtures place alternating sides of bare `fig04` fighters (scanner range
800) in the same 2,200-unit sphere in Alderan. They use the production
`PCore001.igc`, the 13-sector showcase map, seed 835, real collision hulls,
240 replenished low-power rounds and 60 initial mines. Native GotoPlan is on,
and the native ship-phase and collision-queue experiments are off. Mine lifetimes run
normally. The original hull selection, placement and projectile draws are shared.

The default gives the two sides opposite half-yaw inputs at cruise throttle
so ships continue turning within the engagement volume. After each update and
projectile top-up, it counts live, undocked, unboarded sector members and
repairs only those survivors to full hull with `Ship.SetFraction(1)`. A
lethal hit, docking or boarding immediately lowers the count. Ships keep their
positions, velocities, scanner history and identities between passes.
Crowding, hits, blast, collision and death handling all run inside Core.
This fixture measures the cost of sustaining a fighter population. It omits
pilot decisions, firing by equipped ships, death/relaunch churn and network work.

The disposable JIT warm-up runs 60 profiled passes. First contact pools passes
1–10 from 20 newly assembled scenes with the same seed. Sustained warms a separate scene for 60
passes and measures the next 500 without resetting its spotters. The profiler
starts a new histogram for each phase. Due-sweep statistics use only its 20 or
50 firing ticks. Each sample sums that tick’s visibility work across the map.
Each process reports nearest-rank p95 and p99.

In an untimed observer replay of the living 400, all 400 ships changed
position on passes 10, 60 and 560. No ship had an enemy last spotter at spawn.
At pass 10, 398 had one. At pass 60, 387 retained the same spotter from the
preceding sweep. At pass 560, 398 did. The observer counted 19 direct
projectile damage callbacks by pass 60 and 166 by pass 560, plus 20 blast
damage callbacks during the sustained window. The timed runs use the stock
`NullIgcSite`.

The headline timer encloses only `Mission.Update`, including its internal
profiler scopes. A second timer includes the outer profiler and projectile
top-up for historical comparisons. Both end before the census and hull repair.
Allocation and GC are process-wide deltas over the measured pass loops,
including profiler and fixture upkeep. They exclude scene construction,
untimed warm-up and printing. GC triggered during a measured block can still
collect objects from earlier work. Sub-buckets nest and their shares overlap.

## Population guard

The threshold is the ceiling of 95% of requested ships. The bench checks
before each phase and after **every** pass, prints fixed checkpoints and the
minimum, then returns the failing status after both reports. `moving` counts
eligible ships with speed above one unit per second. Projectile counts include
that pass's replenishment. Running the stock inputs with `--legacy-fixture`
produced this excerpt and process exit code 1.

```text
FAIL population guard: warm-up first below 95% at pass=24, minimum live=0, required=380.
population sustained pass=60 ships=0 projectiles=240 mines=60 moving=0
population sustained minLive=0 required=380
P863 population: ships=0 projectiles=240 mines=8 moving=0
FAIL population guard: sustained first below 95% at pass=60, minimum live=0, required=380.
```

## Workstation and reproduction

Intel Core Ultra 9 285HX, 24 logical CPUs, Ubuntu 26.04.1 LTS x64, .NET SDK
10.0.403, runtime 10.0.14. Release JIT, server GC enabled in the project and
verified in each process header. Tiered compilation was disabled so the two
phases run the same optimized code. The commands below assume the external
artwork is configured and the local NuGet cache is populated.

```bash
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/http" NUGET_PACKAGES="$PWD/.scratch/packages"
dotnet build tools/P835Bench -c Release
export DOTNET_TieredCompilation=0
bench=tools/P835Bench/bin/Release/net10.0/P835Bench.dll
for run in 1 2 3; do
    for ships in 200 400; do
        order=(living legacy)
        if [[ $run == 2 ]]; then order=(legacy living); fi
        for fixture in "${order[@]}"; do
            flags=()
            expected=0
            if [[ $fixture == legacy ]]; then flags=(--legacy-fixture); expected=1; fi
            rc=0
            dotnet "$bench" "$ships" 240 60 "${flags[@]}" || rc=$?
            [[ $rc == "$expected" ]] || exit 1
        done
    done
done
```

## Results

At 400 ships the sustained median run took **1.99 ms** per update with the
fleet alive and **0.85 ms** with zero flying ships. Models rose from 0.35 to
0.71 ms/pass and rescue entered the top three at 0.31 ms/pass. Living-fleet
allocation was 88.7 KiB/pass, up from 71.7. At 200 ships the mean rose from
0.68 to 1.18 ms/pass. The 400-ship living sweep p95 was 1.49 ms at first
contact and 1.04 ms after warm-up (medians of three run-level p95s).

The 12 processes ran serially after the builds and full test sweeps. Each table
row is one phase of one process. Update and visibility figures are milliseconds.
The three largest `sim.*` buckets use total bucket time divided by all phase
passes. Raw local output, including every bucket, checkpoint and GC pause, is
under `.scratch/p1086/logs/{living,legacy}-{200,400}-{1,2,3}`.

In all six default runs, every measured pass retained the requested live fleet.
Every printed checkpoint also had the full fleet moving and 240 projectiles.
The sustained phase began with 60 mines and ended with 8 in all 12 processes.
Both legacy fleets were already dead at its start. The 200-ship guard first
tripped on pass 59 (190 required), the 400-ship guard on pass 24 (380 required).
All six legacy runs returned 1 after printing both phases. All six default
runs returned 0.

### First contact

| Ships | Fixture | Run | Update mean / p95 / p99 | Due visibility mean / p95 | Three largest sim buckets, mean ms/pass | KiB/pass | GC 0/1/2 | Min live |
|---:|---|---:|---|---|---|---:|---|---:|
| 200 | living | 1 | 1.05 / 2.11 / 2.49 | 0.90 / 1.00 | models 0.31, combat 0.24, collision 0.14 | 150.7 | 0/0/0 | 200 |
| 200 | living | 2 | 1.06 / 2.13 / 2.65 | 0.92 / 1.00 | models 0.32, combat 0.24, collision 0.13 | 150.7 | 0/0/0 | 200 |
| 200 | living | 3 | 1.05 / 2.12 / 2.46 | 0.91 / 1.00 | models 0.31, combat 0.24, collision 0.13 | 150.7 | 0/0/0 | 200 |
| 200 | legacy | 1 | 1.06 / 2.15 / 2.49 | 0.90 / 0.99 | models 0.32, combat 0.24, collision 0.13 | 150.7 | 0/0/0 | 200 |
| 200 | legacy | 2 | 1.06 / 2.12 / 2.49 | 0.89 / 0.99 | models 0.31, combat 0.24, collision 0.13 | 150.7 | 0/0/0 | 200 |
| 200 | legacy | 3 | 1.06 / 2.15 / 2.51 | 0.91 / 1.00 | models 0.32, combat 0.24, collision 0.13 | 150.7 | 0/0/0 | 200 |
| 400 | living | 1 | 1.91 / 3.54 / 4.04 | 1.50 / 1.49 | models 0.44, combat 0.36, rescue 0.31 | 198.8 | 3/1/1 | 400 |
| 400 | living | 2 | 1.91 / 3.57 / 4.23 | 2.01 / 1.51 | models 0.44, combat 0.35, rescue 0.30 | 198.8 | 3/1/1 | 400 |
| 400 | living | 3 | 1.90 / 3.61 / 3.77 | 1.49 / 1.49 | models 0.44, rescue 0.36, combat 0.35 | 198.8 | 3/1/1 | 400 |
| 400 | legacy | 1 | 1.94 / 3.36 / 4.08 | 1.38 / 1.51 | models 0.44, combat 0.41, rescue 0.34 | 198.8 | 3/1/1 | 400 |
| 400 | legacy | 2 | 1.94 / 3.38 / 4.05 | 1.37 / 1.64 | models 0.44, combat 0.41, rescue 0.34 | 198.8 | 3/1/1 | 400 |
| 400 | legacy | 3 | 1.89 / 3.29 / 5.56 | 1.91 / 3.60 | models 0.44, combat 0.35, rescue 0.30 | 198.8 | 3/1/1 | 400 |

### Sustained

| Ships | Fixture | Run | Update mean / p95 / p99 | Due visibility mean / p95 | Three largest sim buckets, mean ms/pass | KiB/pass | GC 0/1/2 | Min live |
|---:|---|---:|---|---|---|---:|---|---:|
| 200 | living | 1 | 1.18 / 1.78 / 1.96 | 0.66 / 0.76 | models 0.48, combat 0.20, collision 0.15 | 59.6 | 1/1/1 | 200 |
| 200 | living | 2 | 1.18 / 1.78 / 1.98 | 0.82 / 0.76 | models 0.49, combat 0.20, collision 0.13 | 59.6 | 1/1/1 | 200 |
| 200 | living | 3 | 1.16 / 1.76 / 1.94 | 0.65 / 0.76 | models 0.49, combat 0.21, collision 0.13 | 59.6 | 1/1/0 | 200 |
| 200 | legacy | 1 | 0.68 / 1.28 / 1.36 | 0.66 / 0.75 | models 0.31, combat 0.19, visibility 0.07 | 50.6 | 0/0/0 | 0 |
| 200 | legacy | 2 | 0.68 / 1.27 / 1.36 | 0.66 / 0.75 | models 0.31, combat 0.19, visibility 0.07 | 50.6 | 0/0/0 | 0 |
| 200 | legacy | 3 | 0.68 / 1.27 / 1.36 | 0.66 / 0.75 | models 0.31, combat 0.19, visibility 0.07 | 50.6 | 0/0/0 | 0 |
| 400 | living | 1 | 1.99 / 2.74 / 3.08 | 0.90 / 1.14 | models 0.71, rescue 0.31, combat 0.29 | 88.7 | 3/1/0 | 400 |
| 400 | living | 2 | 1.99 / 2.76 / 3.08 | 0.91 / 1.04 | models 0.71, rescue 0.31, combat 0.28 | 88.7 | 3/1/0 | 400 |
| 400 | living | 3 | 1.98 / 2.73 / 3.08 | 0.84 / 1.00 | models 0.71, rescue 0.31, combat 0.28 | 88.7 | 2/1/0 | 400 |
| 400 | legacy | 1 | 0.86 / 1.59 / 1.81 | 0.83 / 0.99 | models 0.35, combat 0.26, mining 0.09 | 71.7 | 2/1/0 | 0 |
| 400 | legacy | 2 | 0.85 / 1.58 / 1.81 | 0.82 / 0.98 | models 0.35, combat 0.26, mining 0.09 | 71.7 | 2/1/0 | 0 |
| 400 | legacy | 3 | 0.85 / 1.57 / 1.76 | 0.82 / 0.97 | models 0.35, combat 0.26, mining 0.08 | 71.7 | 2/1/0 | 0 |

## Implications for earlier measurements

- **P835.** The rig saw `step:update` rise from 6.5 to 25.8 ms and the
  small real-server fight independently showed visibility growth. Those
  measurements used running servers. The local 400-object bucket ranking
  and spotter-cache p95 comparison cannot quantify their cost or saving for
  400 sustained live fighters. P1086 runs the current sticky-spotter path
  in both fixtures. [P835 clash profile](P835_CLASH_PEAK_COST.md)
- **P863.** Negligible ordnance damage preserved the objects while crowding
  exhausted their hulls. The historical local 3.72 to 1.87 ms mean and
  model/blast savings describe that fixture. Their magnitude needs a paired
  before/after with verified live populations before applying it to a living
  fleet. The recorded projection benchmark, packet parity tests and b50 rig
  comparison used separate workloads. [P863 report](P863_REPORT.md)
- **P928.** The NativeAOT allocation drop from 808.7 to 65.5 KiB/pass and
  roughly 22% mean-time improvement still describe that historical A/B.
  The population correction removes the basis for claiming those magnitudes
  at 400 flying ships. P1086 compares fixtures on one revision. The P928
  goldens and real-server capture have independent evidence.
  [P928 report](P928_REPORT.md)

## Verification

- The solution build and the Release benchmark build finished with zero warnings
  and zero errors. `tools/build-tools.sh` also built all 156 tool projects,
  each with zero warnings and zero errors. The local restore used cached
  packages under `.scratch/`.
- The zero-ship, zero-ordnance control completed with a zero population and exit
  code 0. An unknown option returned usage and exit code 2. No test project
  references the P835Bench assembly or its helpers. The existing P928 log parser
  read the sustained timings, allocation and buckets from both fixtures. For
  comparisons to the old wall timer, use the separately printed
  `Update + profiler and salvo upkeep` row.
- The glossary link check passed for all 1,173 paths. Only benchmark code and
  documentation changed.

The additional repository-wide Fast tier covered 16,148 cases in each sweep.
The first sweep recorded one profile-write failure in the read-only user
configuration and five cases above the 10 s limit. With `XDG_CONFIG_HOME` set
to a writable scratch directory, the profile test passed. That sweep recorded
16,141 passes, five skips, two assertion failures and five cases above 10 s.
The assertions were the P817 miner docking deadline and the draw-ballot receipt.
Both passed in the first sweep. The second sweep's timing flags were P750
asteroid types, P999 civ strategy, P777 warp exits, P1000 civ profiles and
the P817 miner. These cases have prior entries in the [flake ledger](../TEST_FLAKES.md).

Each of the six methods flagged in the second sweep then ran three times in
isolation, including all theory rows. All 72 executions passed. The longest
individual case took 9.381 s. Both full sweeps returned a failing status.
