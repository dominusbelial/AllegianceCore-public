# P838 — GC pause time and allocation in the server tick profiler (the Rust-vs-C# decision gate needs this number)

Task: per-window GC accounting in the tick profiler (`[gcprof]`), per-step allocation attribution,
local measurement (bench + real-server showcase), top allocation sites, extrapolation to the rig,
and the deciding number for the RS1 (wire-compatible Rust server) gate. Scope 1–5 of
`P838_BRIEF.md`. Raw evidence: `~/allegiance-evidence/P838/` (local showcase server log
`p838-local-showcase-server.log`, allocation nettrace `p838-alloc.nettrace`). Summary:
`P838_assets/gc-profile-summary.json`. All line numbers re-grepped at master `1e4c33d4` + this
task's changes.

## 0. The deciding number, up front

**GC pause share at the local clash peak = 3.4 % of tick-wall** (whole-run 4.0 %; 120-bot
showcase, Server GC + concurrent). **Projected rig clash peak ≈ 4 %** (two independent methods:
3.8 % and 3.9 %; honest range ≈ 2–6 %) — against the rig's ~95 ms peak tick-wall that is
**~3–4 ms/tick of GC pause, not the missing ~45 ms**. The 95-vs-50 ms gap is CPU work inside the
`step:` scopes (P835: coverage gap ≈ 0), not GC pauses. What the rig's ~220 MB/s allocation rate
does cost is GC *throughput* overhead (copying, card marking, cache misses) and occasional
single-pause spikes (local worst single pause 9.3 ms; at ~14× the per-tick allocation volume the
rig should expect occasional ~10–30 ms single pauses — visible in p95/max ticks, still not the
mean gap).

**Native-intent classification: measurement-only (no native rule touched).** Nothing in this task
changes sim, wire, cadence or broadcast behaviour; the profiler is a port-side observability tool
with no native counterpart (the native server had no tick profiler; the `[egress]` line it extends
was already a documented port adaptation). No parity row required.

**Modern/better-approach note.** If RS1 is motivated by the GC specifically, the honest comparison
is: pauses are ~4 % of the tick, while ~19 MB/tick of allocation at rig scale drives GC CPU cost
and cache pressure that a pause-free allocator (or Rust) would remove — but the same ~50–70 % of
that allocation sits in ten mechanical sites (§4) that can be cut in C# in days with
behaviour-identity proofs, versus a full wire-compatible rewrite. Do the allocation-site cuts
first; re-measure; only then decide RS1 on the remaining number. .NET's Server GC + concurrent
background mode already keeps gen2 pauses off the sim thread; pooling/reuse (detector + colliders,
weapon LINQ hoisting) is the C#-idiomatic equivalent of Rust's zero-allocation style for the hot
loop.

## 1. Per-window GC line (`[gcprof]`) — `src/Allegiance.Core/Profiling/TickProfiler.cs`

Emitted once per report window immediately after the existing bucket lines (the `[tickprof]`
header + `step:` format are byte-identical to before — rig filter scripts unaffected):

```
[gcprof] window 5s ticks=100 pause=18.0ms (0.180ms/tick, 3.8% of tick-wall) gc gen0=21 gen1=0 gen2=0 alloc=119.5MB (1224.1KB/tick) heapAfterGc=50.8MB worstPause=1.54ms
  [gcprof] top-alloc steps (global counter, incl. parallel workers): step:update=38.9MB, step:broadcastShipsUpdate=29.4MB, step:broadcastShipStates=23.7MB, step:drain=8.3MB, step:broadcastMissiles=3.2MB
```

Implementation, bounded as required (a few counters per window, nothing per object):

- **Pause total / per tick / share** — `GC.GetTotalPauseDuration()` delta between window
  baselines (`TickProfiler.cs:57-66`, baselined in the ctor and re-baselined at each `Report`).
- **Collections per generation** — `GC.CollectionCount(0/1/2)` deltas, same baselines.
- **Bytes allocated per tick** — `GC.GetTotalAllocatedBytes(false)` (global counter, all threads)
  delta ÷ ticks.
- **Worst single pause** — sampled cheaply: `GC.CollectionCount(0)` is compared once per tick
  (`FinishTick`, `TickProfiler.cs:248-263`); only on ticks where a collection actually happened
  (~10/s on the rig, 0 otherwise) is `GC.GetGCMemoryInfo(GCKind.Any)` read, taking the max over
  its `PauseDurations` (the last GC's pauses) and its `HeapSizeBytes` for **heap after GC**.
  **NativeAOT verified**: the rig-shaped AOT publish of `Allegiance.Server`
  (`-p:PublishAot=true -r linux-x64`, clean, no IL2026/IL3050) was run for ~40 s with the profiler
  on and emitted the full line with sane values (`~/allegiance-evidence/P838/` + numbers in the
  JSON summary; `heapAfterGc=23.9MB worstPause=2.14ms`).
- **Per-step allocation attribution** — `ProfilerScope` now captures
  `GC.GetTotalAllocatedBytes(false)` at enter for **`step:`-prefixed scopes only**
  (`TickProfiler.cs:430-445`) and accumulates the delta per window (`RecordAlloc`,
  `TickProfiler.cs:352-368`). Honesty note, per the brief: steps run sequentially on the sim
  thread, so the **global** counter (all threads) is the correct per-step number — the parallel
  broadcast/sector workers' allocations land inside the step that spawned them, exactly as they
  should. Inner buckets stay time-only: the per-thread counter
  (`GC.GetAllocatedBytesForCurrentThread`) would double-count nested scopes and miss the workers
  entirely. Cost: two counter reads per step scope (~20 steps/tick), one `CollectionCount(0)` read
  per tick, zero per object; the disabled path is untouched (default struct still no-op, verified
  by test).

## 2. Local measurement

### 2a. Real-server showcase (server + MCP bot host, this workstation, 24 cores / 184 GB)

`scripts/dev-stack.sh up --local --name p838 --map m2xspiderw.igc` with
`ALLEGIANCE_TICK_PROFILER=1`, then `spawn_scenario {preset:"showcase", count_a:60, count_b:60,
target_sector:"Alderan"}` via the MCP command file — the P835 §2a shape: 120 bots (60/side; the
most this workstation sustains responsively), staged and marched into ONE sector (1052, the rig's
clash sector), fight sustained by death/rebuy. 331 profiler windows (~27.5 min), ships in 1052
held 40–50. Raw log: `~/allegiance-evidence/P838/p838-local-showcase-server.log`.

Windows split by `[tickprof]` tick-wall mean (same splitter as P835):

| bucket | baseline (lower half) | clash peak (top decile) |
|---|---|---|
| tick-wall mean | 4.47 ms | 7.24 ms |
| **GC pause share of tick-wall** | 4.26 % | **3.37 %** |
| pause ms/tick | 0.193 | 0.237 |
| alloc KB/tick | 1165 | 1337 |
| gen0 collections/s | 4.9 | 5.2 |
| worst single pause (max over bucket) | 9.33 ms | 6.64 ms |

Whole run: 6.8 s total pause = **4.03 %** of tick-wall; mean allocation ≈ 24 MB/s. Note the local
server never engages time dilation (`sim.timeScale=1`), so the local "peak" is a mild 7 ms tick —
the extrapolation below handles the rig gap. Top-alloc steps at the peak (per 5 s window):
`step:update` ≈ 38 MB, `step:broadcastShipsUpdate` ≈ 29 MB, `step:broadcastShipStates` ≈ 23.5 MB,
`step:drain` ≈ 8.4 MB, `step:broadcastMissiles` ≈ 3–7 MB.

### 2b. `tools/P835Bench` — rig-scale density attribution (400 ships, 500 ticks, 3 runs)

The bench's measured loop now wraps `Mission.Update` in the server's own `step:update` bucket name
so the per-step attribution fills, and prints the P838 GC block itself (`tools/P835Bench/Program.cs:194-242`).

| metric | run 1 | run 2 | run 3 |
|---|---|---|---|
| alloc KB/tick | 875.0 | 874.7 | 873.9 |
| pause ms/tick | 0.110 | 0.105 | 0.109 |
| pause % of measured wall | 5.0 | 4.4 | 5.3 |
| Mission.Update mean ms | 2.23 | 2.37 | 2.04 |

Per run: gen0=27, gen1=19, gen2=14 (console app = workstation GC — the bench is for *allocation
attribution*; pause-share evidence comes from the real server above). `step:update` carries
424.1 of 427.1 MB (99 %) — in the bench scene everything is `Mission.Update`. At 200 ships:
660 KB/tick, 0.053–0.056 ms/tick pause (3.0–3.4 %) — allocation and pause scale roughly linearly
with ship count, as expected.

## 3. Extrapolation to the rig (method stated, honest)

Inputs: rig `[egress]` ≈ 220 MB/s allocated, ~10 gen0 + ~1.5 gen1 collections/s; P835 peak
tick-wall mean ≈ 85 ms → ≈ 11.8 ticks/s → **≈ 18.6 MB/tick** at the rig peak, i.e. ~14× the local
showcase peak (1.34 MB/tick). Two methods:

1. **Pause per MB allocated**: local peak 0.237 ms/tick ÷ 1.34 MB/tick = 0.177 ms/MB →
   rig ≈ 18.6 × 0.177 ≈ **3.3 ms/tick** → 3.9 % of 85 ms.
2. **Per-collection volume**: local Server GC absorbs ≈ 5.2 MB per gen0 (24 MB/s ÷ 4.9/s) at a
   mean pause ≈ 0.9 ms; the rig's ≈ 22 MB/collection (220 ÷ 10) is 4.2× → ≈ 3.8 ms/collection ×
   0.85 collections/tick ≈ **3.2 ms/tick** → 3.8 %.

Both converge on **≈ 4 %** of the rig peak tick-wall. Caveats that bound the range: the rig's
220 MB/s `[egress]` figure may include transport threads' allocations measured differently;
per-collection pause grows sublinearly with collection volume (.Server GC ephemeral budgets);
gen1/gen2 pauses are a larger share at rig scale (1.5 gen1/s); and the **worst single pause**
scales with volume, so occasional 10–30 ms single pauses at the rig peak are plausible — those
show up in tick p95/max, not the mean. Hence the honest range **≈ 2–6 %**, headline ≈ 4 %.

## 4. Top allocation sites (empirical, `dotnet-trace --profile gc-verbose`, 400-ship bench)

GC/AllocationTick aggregates over the whole capture (warmup + 500 measured ticks, ~560 ticks,
496 MB sampled — matches the bench's own 427 MB measured + warmup). Per-tick figures = sampled
bytes ÷ ~560; at the local showcase the same sites run at ~45 ships (≈ 1.34 MB/tick total).

| # | site | what | MB sampled | KB/tick |
|---|---|---|---|---|
| 1 | `new Dictionary<Geometry,int>()` per `Detect` call — `src/Allegiance.Core/Collision/CollisionDetector.cs:75` | backing `Entry[Geometry,int][]` arrays; 9-field struct key → huge entries, regrown every sector per tick | 112.0 | ~200 |
| 2 | per-model `Collider` rebuild per sector per tick — `src/Allegiance.Core/Model/Sector.cs:2246-2297` (`new CollisionDetector()` :2257; ship/ordnance/probe colliders :2250-2261; asteroids :2286; stations :2297) | `Collider` is a sealed class (~13 fields incl. `Orientation` ref) — ~700 rebuilt per tick | 87.3 | ~156 |
| 3 | `_weapons.FirstOrDefault(w => w.MountId == mount)` per mount per ship per tick + `_weapons.Where(...).OrderBy(...)` per ship per tick — `src/Allegiance.Core/Sim/Ship.cs:9205,9207` | `Func<Weapon,bool>` + `FirstOrDefault`/`ListWhereIterator`/`OrderedIterator` LINQ chain | 82.1 | ~147 |
| 4 | `active.RemoveAll(a => a.SweepMax <= curMin)` per sweep position — `src/Allegiance.Core/Collision/BroadPhase.cs:105` | `Predicate<(int,float)>` allocated per call | 28.7 | ~51 |
| 5 | `_models.ToArray()` models-loop snapshot — `src/Allegiance.Core/Model/Sector.cs:956` | `Model[]` per sector per tick | 26.4 | ~47 |
| 6 | detector grouping arrays per `Detect` — `src/Allegiance.Core/Collision/CollisionDetector.cs:76,93-94,133-134,148` (`groupIds`, `results`, `tested`, `workIds`, `unique`) + `Collider._supports`/`HullSupport[]` (`Collider.cs:78-83`) | fresh arrays + `List<HullSupport>` per detector instance | 36.3 | ~65 |
| 7 | per-ship autopilot/dodge closure display classes (`<>c__DisplayClass646_0/652/673`) — `Ship.cs` navigation region, e.g. the `Any(...)` predicates at `Ship.cs:7579,8532,7722` | closure heap objects per ship per tick | ~29.0 | ~52 |
| 8 | `gunner.Orientation.Copy()` per gunner per tick — `src/Allegiance.Core/Sim/Ship.cs:9211` | `Orientation` is a **class**; one allocation per turret gunner per tick | 16.5 | ~29 |
| 9 | visibility sweep per-10th-pass scratch — `src/Allegiance.Core/Model/Sector.cs:4995-4996,5139,5155-5160` (`targets`, `scannersBySide`, `occluders`, `sideIds`, `scanResults`, the `Add` local-function closure) | one set per sweep, amortised ÷10 | 7.6 | ~14 |
| 10 | `new Projectile(...)` refire churn (`Projectile.cs`, sustained by the bench's salvo) | projectile + its model bookkeeping | 2.4 | ~4 |

Sites 1–8 sum to ≈ 750 KB/tick of the bench's 875 KB/tick. Ranked cut-order for follow-ups
(byte-identity proofs required per P835 house rules): ①2+6 (reuse the `CollisionDetector` +
collider pool across ticks — one object graph, `Clear()` per tick), ①1+4 (pre-sized/reused
grouping dictionary + for-loop instead of `RemoveAll`), ①3+7+8 (hoist the weapon LINQ to index
loops in `Ship.Update`'s weapon block; make `Orientation.Copy` unnecessary or a struct copy),
①5 (reuse a models-snapshot buffer). **Nothing was optimised in this task** — the brief forbids
it beyond trivial one-liners, and none of these qualify; the ranked list is the deliverable.

## 5. 221 recipe — reading GC pause share at the clash peak

The `[gcprof]` line ships with the server build (no config change); enable with one env var:

```bash
# in the rig's server env (or docker-compose environment:), add:
ALLEGIANCE_TICK_PROFILER=1            # log-only, 5 s windows
# optional: ALLEGIANCE_TICK_PROFILER_INTERVAL=5; ALLEGIANCE_SLOW_TICK_MS=250 keeps [slowtick] on

# at the 200v200 clash peak:
grep '\[tickprof\] window' server.log | tail -600   # tick-wall mean/p95/max per 5 s window
grep '\[gcprof\] window'   server.log | tail -600   # one GC line per window
grep '\[gcprof\] top-alloc' server.log | tail -600  # per-step allocation ranking

# pause share at the peak = sum(pause=ms) / Σ(tick-wall mean × ticks) × 100
# over the top-decile windows by [tickprof] tick-wall mean (the P835 splitter).
# The "(x.x% of tick-wall)" field on each [gcprof] line is the per-window share
# already computed against that window's tick-wall sum — averaging it over the
# peak decile is the same number.
# Cross-check allocation against the existing [egress] line: its allocMB per
# window ≈ [gcprof]'s alloc=MB (same global counter, window-aligned).
```

Expected at the rig peak per §3: pause share ≈ 4 % (2–6 %), alloc ≈ 18–19 MB/tick,
`step:update` + `step:broadcastShipsUpdate` + `step:broadcastShipStates` ≈ 80 % of allocation.

## Verification (light gate)

- `dotnet build AllegianceCore.slnx` — 0 warnings / 0 errors.
- `tools/build-tools.sh` — all 90 tool projects (incl. updated `tools/P835Bench`).
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`) — clean, no IL2026/IL3050; the published binary **ran**
  with the profiler on and emitted `[gcprof]` + `top-alloc` with sane values (§1, NativeAOT
  verification).
- New tests 3× alone — `tests/Allegiance.Core.Tests/P838GcProfTests.cs` (4 tests: `[gcprof]`
  emitted with sane values + `[tickprof]` format unchanged; per-step allocation attribution and
  non-step exclusion; disabled-path no-op; bounded overhead of the GC tracking) — 3/3 green, plus
  the affected profiler suites (`TickProfilerTests`, `UpdateCadenceTests`,
  `P706DispatchProfilingTests`) 3× together, 27/27 × 3 green.
- ONE `tools/run-tests.sh --tier fast` — 10,971 tests; single failure =
  `Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact`
  (MonoGame allocation-threshold test, **untouched by this task**; the ledgered P787a load flake,
  `docs/TEST_FLAKES.md:82`) — passed 3/3 isolated (15–17 ms each).
- `docker compose config` not re-validated: no new service, store, port or env var (the
  profiler env vars `ALLEGIANCE_TICK_PROFILER*` are pre-existing).

## Unfinished / handed to follow-ups

- The allocation-site cuts (§4 ranked list) — deliberately NOT done here; each needs a
  byte-identity proof per the parity rules.
- The rig A/B itself runs on Claude's 221 (recipe §5); this task produced the local + projected
  numbers only.
- `Orientation` and `Collider` remain classes; making either a struct is a follow-up with its own
  identity-proof burden.
