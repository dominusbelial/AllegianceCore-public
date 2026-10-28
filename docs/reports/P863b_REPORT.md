# P863b — surge projection and allocation

2026-10-28, dispatch base `a94c13c7d3ab4c8c02dbc09cdba929212bfd9474`, working-tree implementation; no commits, pushes or remote-host access.
**Claude's rig A/B is still the acceptance test** for TiDi >= 0.95 in every 5 s
window, p95 <= 40 ms and p99 <= 50 ms. These local projection measurements cannot
certify end-to-end rig TiDi.

## Change and measured cause

The initial 400-peer surge profile retained the P863 bottleneck:
`broadcastShipsUpdate` averaged about 10–11 ms per tick. Six unchanged-arm
calibration captures allocated about 964 MB per 200 measured ticks. Missile
fire and reliable source-pose catch-up added roughly 1.5–4.4 ms per 16-missile
volley, plus death/relaunch work. This supported reducing projection allocation
and sharing identical event dependencies before attempting parallel ranking.

`Server.RankedPose.cs` retains the native-mode ranked selection, target-first
promotion, density quota, HEAVY/LIGHT split, deterministic deviation updates,
pose-floor order and turret relays. It writes the selected records directly into
the existing `PooledPosePacket` lease, with common quantized pose values captured
once per ship/pass. Per-viewer heavy relative DTOs, heavy-entry arrays and
deferred HEAVY message encoding are eliminated; selection lists are reused. A lease has one delivery, and the existing
publisher releases it after sending, dropping or superseding it. Only detached
bytes cross that boundary. `ViewerPoseCadence.SelectFloor` is shared with the
reference DTO path; it retains the ordered fair sweep and deadline catch-up.
Port-mode and snapshot/encode-once-disabled paths retain their existing sender.

Native references re-grepped locally: `FedSrv/FedSrv.CPP:3614-3621` (deviation
comparison), `:3923-3950` (ranking and deviation bands), `:4049-4071` (active
turrets), `:4079-4133` (target/own fractions and position reference),
`:4141-4188` (target first, heavy/light assembly and concatenation). This
preserves the port's established deterministic deviation, density adaptation and
2 Hz floor; it does not introduce native's random jitter or modem budget.

Encoded event arrays now share one `BroadcastPacket` wrapper within the open
snapshot. The dictionary uses array reference identity and clears at snapshot
boundaries; it never combines separate messages or changes recipients, lanes or
FIFO order. Missile and projectile fan-outs share the identical reliable source
pose/status within that synchronous fan-out, while retaining each recipient's
visibility/history gate and the existing side-aware export cache. No source
snapshot is reused across separate emissions or simulation mutations. Native
intent: `FedSrv/FedSrv.CPP:5505` (sector fire relay), `:11914-11915` (mission kill),
`:12011-12022` (sector eject), `:12512-12529` (mission missile destruction);
the extra reliable pose dependency remains the port's existing thin-client
adaptation. Death effects and relaunch behavior are unchanged.

Same-sector viewers still consume shared deviation state sequentially. Separate
sectors retain the existing parallelism. Moving same-sector ranking to workers
without proving disjoint candidate/deviation sets would change later viewers'
packets; this task introduces no such behavior change or runtime flag.

## Reproduction and proof

`P863bSurgeBudgetTests` extends the recorded-clash projection seam. It uses
`PCore001.igc`, `m2xspiderw.igc`, 400 ships/viewers on alternating sides, native
ship updates, minUpdateHz=2, immutable snapshot + encode-once, parallelBuild=true
and maxPar=15. File-map random top-ups are replaced with recorded native map
rocks plus deterministic top-ups (`seed=863`); boot treasures are removed.
Server ejection RNG is seeded independently. Both arms receive identical inputs.

After 30 warm-up ticks, 200 measured ticks contain four synchronized bursts:
eight player ejections, two true player deaths/restarts and sixteen missile
launches per burst. Five ticks later those missiles are destroyed and all ten
ships receive their recorded relaunch hull/sector state. Thus each replay covers
40 kills, 64 missile lifecycles and 40 relaunches. This deliberately compresses
the rig's 30 s kill-rate interval into a 10 s capture to expose burst tails.
It runs real server callbacks and the complete projection suffix, including
identity/discovery/status and missile streams. It does not run socket clients,
AI, the simulation update or purchase controllers; the separate showcase gate
exercises the real purchase/relaunch lifecycle. Ships have bare hulls, so random
part/ammo loot is not included in this deterministic transcript.

The reference arm uses the pre-change DTO sender, per-recipient emission
snapshots and event wrappers; the candidate arm uses the production changes.
The common floor selector is shared, making the allocation comparison
conservative about that small refactor. Timing and allocation measurements flush
every snapshot and retain no transcript. Three runs per arm alternate order in
each measurement test. The byte-proof test separately retains and compares every
per-peer packet byte, tick, coalescing key, guarantee flag and all 80,000
per-tick deviations. Explicit message assertions require KILL_SHIP, EJECT,
SHIP_DELETE, FIRE_MISSILE, MISSILE_DESTROYED, EXPLOSION_EVENT and ENTER_LIFEPOD.

An independent copy of the dispatch server's production sources was also built
in scratch, with only the new recorded-event/test-RNG seams added. The same
400-peer transcript on that server has the identical metadata+payload SHA-256
as all three candidate captures:
`7F4342A266BA84DA62ED2F3E3D51D358E767E29B725E96D5667F1E20AC124254`.
This cross-check covers the floor-selector refactor against its actual original
implementation (`dispatch-byte-proof.log`, `digest-{1,2,3}.log`), beyond the
in-process reference arm. These extra digest-only captures also pass 3/3.

The blocked-publisher test compares the old and pooled sender at populations
1, 2, 3 and 80 with zero/enabled floors, then changes every live position,
velocity, orientation, fraction, turn rate and control while publication waits.
All queued bytes must remain identical. Existing P863 and turret/cadence/codec
regressions cover visibility churn, target changes, ordering, active/inactive
relays and span-writer equivalence.

## Local numbers

Intel Core Ultra 9 285HX, 24 logical CPUs, .NET SDK 10.0.400 / runtime
10.0.11, Release JIT. The test process uses workstation/concurrent GC; the server
executable and rig use server GC. These are local projection measurements,
including publisher flush, with no sockets/AI/simulation update. Other real
verification processes ran on the shared host; observed host load ranged from
about 2 to 8. No synthetic load was used.

The following values are medians of three independent replays **per arm** in
`isolated-1.log`; order is before/after, after/before, before/after. Mean/p95 are
per step invocation; event rows each have four samples per replay. Other rows
have 200. Nested event rows must not be added to the full projection total.

| Step | Before mean ms | Before p95 ms | After mean ms | After p95 ms |
|---|---:|---:|---:|---:|
| `step:broadcastShipsUpdate` | 10.978 | 12.218 | 10.007 | 10.539 |
| `step:broadcastShipStates` | 1.327 | 2.136 | 1.345 | 2.179 |
| `step:crossSideExportSweep` | 0.302 | 0.490 | 0.292 | 0.445 |
| `step:crossSideDiscoverySweep` | 0.130 | 0.171 | 0.102 | 0.149 |
| `step:crossSideShipStatusSweep` | 0.367 | 0.656 | 0.325 | 0.681 |
| `step:broadcastMissilesUpdate` | 0.028 | 0.252 | 0.040 | 0.252 |
| `surge:deaths` | 1.591 | 1.658 | 1.741 | 1.827 |
| `surge:missileFire` | 1.232 | 1.861 | 0.669 | 1.130 |
| `surge:missileDestroy` | 0.714 | 0.924 | 0.663 | 0.923 |
| `surge:relaunch` | 1.095 | 1.136 | 1.111 | 1.184 |

Projection wall mean/p95/p99: **16.213 / 21.318 / 24.611 ms before** versus
**13.942 / 15.817 / 21.046 ms after**. Median eight-event-tick mean/max:
**24.350 / 30.055 ms before**, **21.069 / 23.212 ms after**. Death/relaunch CPU
did not improve measurably; their event-wrapper allocation decreased. The
missile-fire reduction and reduced overall GC/allocation are the clear surge
improvements. There is no claimed missile-position or death-math speedup.

Total allocated memory over 200 measured ticks: **950.048 -> 154.708 MiB
(-83.7%)**. Median gen0/gen1/gen2 collection deltas: **57/34/0 -> 9/4/0**.
The profiler's approximate projection-thread attribution puts
`broadcastShipsUpdate` at **457.928 -> 29.934 MiB** and `surgeEvents` at
**17.779 -> 5.484 MiB**; the precise process-wide total above also includes
publisher-worker allocations. These measures must not be added together.

| Run | Before mean / p95 / p99 ms | After mean / p95 / p99 ms | Before / after allocation MiB | Before / after gen0 | Before / after gen1 |
|---|---|---|---|---|---|
| 1 | 16.325/22.735/31.992 | 13.942/15.768/21.046 | 950.051 / 154.793 | 57 / 10 | 31 / 5 |
| 2 | 16.212/21.318/24.611 | 14.162/17.913/22.273 | 950.048 / 154.706 | 57 / 9 | 34 / 4 |
| 3 | 15.786/18.409/23.283 | 13.324/15.816/20.353 | 950.032 / 154.708 | 57 / 8 | 34 / 1 |

All nine paired measurements across the three clean isolated invocations are
retained in their logs. The small extracted three-run table is
[P863b_assets/surge-summary.json](P863b_assets/surge-summary.json).
The separate transcript proof compares **1,485,217 packets / 321,687,118 bytes**
plus all **80,000 deviations**, on every isolated invocation. The initial proof
with distinct missile launchers also passed (1,509,097 packets); the final
fixture intentionally reuses the same launchers across bursts to cover reliable
pose refresh as well as initial catch-up.

Three unchanged `tools/P835Bench` captures also ran with 400 ships, 240 sustained
projectiles and 60 initial mines, 60 warm-up + 500 measured ticks, seed 835.
Those measure simulation separately; the benchmark uses negligible damage to
sustain population, as corrected by P863. No simulation code changed here, and
these are not an extra before/after claim. Debug JIT Mission.Update
mean/p95 by run: **3.42/7.44, 3.55/7.42, 3.44/7.41 ms**; all end with 400 ships
and 240 projectiles. Each capture allocates 453.4 MiB over 500 ticks with
28/20/14 gen0/gen1/gen2 collections. Debug simulation allocation is retained as
context and must not be combined with Release projection allocation.

## Verification

- Final solution build: 0 warnings, 0 errors (`build-final.log`).
- Linux-x64 NativeAOT server publish: succeeds without warnings/errors.
- Six new cases pass **3/3 alone** on final binaries (`final-new-{1,2,3}.log`):
  6/6 each, 33.694 / 33.570 / 33.955 s. An earlier clean set also passed 3/3
  (`isolated-{1,2,3}.log`, 34.848 / 34.265 / 35.095 s). Direct `dotnet vstest`
  avoids concurrent MSBuild node console redirection observed during the early
  combined gates. The final source adds the independent transcript digest to
  the same assertions; the complete final six-case set was rerun three times.
- Affected regression set passes **81/81** initially, then its 75 existing cases
  pass twice more (`affected-{1,2,3}.log`); the new six have their own clean
  isolation above.
- All 91 tool projects build with 0 warnings and 0 errors.
- Exactly **one Fast sweep**: **11,408 pass / 8 fail / 3 skip**, 473.1 s;
  infrastructure 9/9 and timing guard pass. **The Fast gate is not green.**
  Seven main-lane failures are the five path cases, async seating-order case and
  radar-placement case below. The eighth is the known quarantine loot-pickup
  fixture; its runner retry passes, followed by **3/3 manual isolated passes**.
  The runner retains original failure counts; retry passes do not erase them.
- Showcase never-parks: **5/5 passed**, two complete matches each. Test-run
  elapsed seconds: **208.020 / 221.508 / 167.268 / 171.468 / 208.110**.
  All use the same final server DLL SHA-256:
  `2c0695aaf1d6a3ac0de2855861c55a1a7f2afcd5898ee51a7020564a56eff13d`.
  The existing showcase configuration uses port-mode ship updates; the native
  pooled sender is independently exercised by the crowded byte/codec/cadence
  proofs and the blocked-publisher cases.
- Glossary links (266 distinct paths), parity scorecard and `git diff --check`
  pass. Changed/new code passes the private-path/email/secret-pattern scan;
  the parity CSV remains LF. No commits or pushes were made.

Small machine-readable receipts:
[P863b_assets/verification.json](P863b_assets/verification.json).

The radar test's off-screen placement assertion fails 3/3 on the candidate and
2/3 with the **unchanged dispatch server** substituted in the same test binaries
(one baseline invocation passes). It sees a moved NPC instead of the intended
fixed +Z position. This is a pre-existing fixture/timing failure; no radar or NPC
behavior was changed. The three-peer test reproduces the ledgered async logon
order `(0,1,0)` failure in the sweep, then passes 3/3 alone. The five
`PlayerBundlePathsTests` failures are the known worktree-TMPDIR context issue:
simulated outside-repository install directories inherit this checkout's
`local.env`. All nine cases pass 3/3 unchanged through a directory-handle alias
to the same NVMe scratch bytes, supplying the intended lexical path outside the
repository. The original Fast failure is retained; no second sweep is run.
The quarantine `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
expected one treasure-spawn record but received three, including ambient powerup
and cash spawns. Its automatic runner retry and all three subsequent exact-method
isolations pass. These observations are ledgered in `docs/TEST_FLAKES.md`.

Raw logs retained under `.scratch/p863b/`: `build.log`, `build-final.log`, `tools-build.log`,
`aot.log`, `affected-{1,2,3}.log`, `final-new-{1,2,3}.log`,
`isolated-{1,2,3}.log`, `digest-{1,2,3}.log`,
`dispatch-byte-proof.log`, `sector-{1,2,3}.log`, `fast.log`, `fast-results/`,
`showcase-{1,2,3,4,5}.log`, `radar-isolation-{1,2,3}.log`,
`radar-baseline-{1,2,3}.log`, `three-peer-isolation-{1,2,3}.log`,
`paths-isolation-{1,2,3}.log` and `add-part-isolation-{1,2,3}.log`.
Early calibration/diagnostic logs are also retained.
The first concurrent MSBuild invocation redirected Core test output into
`showcase-1.log`; its P730 pass is individually identifiable. The clean direct
VSTest logs above supply the final isolated evidence. Scratch source/build/cache
copies used only to produce this evidence are removed before handoff.

Reproduction commands (external artwork configured by `local.env`):

```sh
source local.env
~/.dotnet/dotnet build AllegianceCore.slnx
DOTNET=~/.dotnet/dotnet tools/build-tools.sh
~/.dotnet/dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/p863b/aot
~/.dotnet/dotnet test tests/Allegiance.Core.Tests -c Release --filter FullyQualifiedName~P863bSurgeBudgetTests --logger 'console;verbosity=detailed'
DOTNET=~/.dotnet/dotnet tools/run-tests.sh --tier fast
~/.dotnet/dotnet test tests/Allegiance.ClientCore.Tests -c Release --filter FullyQualifiedName~P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch
```

Use writable TMPDIR, NUGET_HTTP_CACHE_PATH and XDG_CONFIG_HOME under scratch in a
sandbox with a read-only home. The five showcase invocations and isolated test
runs have individual logs; the Fast tier is invoked exactly once.

## Native-intent classification and modern approach

**Behavior-preserving performance adaptation of implemented rules.** The updated
SIM-0470, SIM-0594, SIM-0647 and SIM-0659 rows retain `implemented`; no row is
closed by a different observable outcome or reclassified n/a. Existing native
intent and packet semantics are preserved, including the port's already-shipped
cadence adaptations. Native ranking, target promotion and death/eject/fire
ordering are **design**. The 1600-byte/s modem budget is an **era constraint**
(`FedSrv/FedSrv.CPP:3830-3845`), already adapted by the port's density quota and
pose floor. Repeated managed DTO/wrapper construction is **port performance
overhead**, not a native rule to preserve. This task removes that overhead while
keeping the actual packets identical. No services, stores, ports, environment
settings or media were added.

The better modern approach here is to retain ordered simulation/ranking and
reliable events while representing immutable fan-out data once and returning
bounded packet leases after publication. That removes allocation and repeated
encoding without changing combat or sending less information. Broader parallel
ranking or different cadence rules would need separate dependency and byte
proofs. The rig A/B must determine whether the remaining seven windows now meet
the stated TiDi and tail-latency targets.
