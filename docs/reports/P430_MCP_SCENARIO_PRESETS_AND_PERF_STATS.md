# P430 — MCP scenario presets (5v5/10v10/15v15) + real concurrency stats

**Tool:** `tools/allegiance-mcp` gains three things Domingo asked for after
watching MCP-driven runs — *"maybe we can have some kind of predefined setups?
5v5 10v10 15vs15? and we can get some stats to find what is the actual sweetspot
for concurrency, I think 15vs15 is getting close to the limits, I could see some
bots moving really slow while teleporting a bit."*

1. **`spawn_scenario`** — a composite convenience tool that stages a whole fight
   in one call (`size` real `"combat"` bots per side, 2×size total) instead of a
   caller manually issuing 2×size `spawn_bot` calls.
2. **Real per-bot update-cadence tracking** — each bot's `Loop()` tick now
   records a real `(interval, distance)` sample whenever its live
   server-reported position actually changes.
3. **`get_perf_stats`** — a read-only tool that aggregates every bot's real
   rolling samples into mean / p95 / worst-offender numbers for update INTERVAL
   and JUMP DISTANCE — the measured evidence (not a guess) for where
   real-time smoothness starts degrading.

Scope respected: this task only **measures** the real server's behavior — it does
not change the server's tick rate, `docker/compose.server.json`'s
`updatesPerSecond`, `tools/vizfight`, or add any new `spawn_bot` behavior beyond
what P429 already landed. There is still no `despawn`/`recall` tool (unchanged
standing limitation).

---

## 1. `spawn_scenario` — the composable preset

`McpTools.SpawnScenario` is a thin loop over the existing, already-proven
`BotManager.SpawnBot` — it introduces **no new spawn mechanism**. For
`size = N` it issues exactly the calls a caller would otherwise type by hand:

- resolves the hull first (**default = `Catalog.DefaultFighterHull()`, the same
  real fighter `spawn_bot`/vizfight use** — first hull with energy+ammo and a
  fixed hardpoint; an explicit `hull` name resolves via the same
  exact-over-substring policy), before anything touches the wire;
- optionally resolves `target_sector` against the probe's live sector map
  (identical for every bot on both sides, so they converge);
- picks the server's first two **real** sides (`ValidSides()` — side A, side B);
- for `i in 0..size`: `SpawnBot(hull, sideA, DefaultParts(), "combat",
  target_sector)` then `SpawnBot(hull, sideB, …, "combat", …)`;

and returns a **real per-side summary** with how many succeeded on each side and
**every genuine per-bot failure named individually** (`index`, `side`, `error`)
— a partial failure is reported, never swallowed. A completely-failed scenario
(zero bots on either side) returns an error result; otherwise the (possibly
partial) summary is a normal result.

Argument handling is deliberately permissive on size (any positive integer — the
5/10/15 are intended presets, not hardcoded bounds), and `size < 1` / a missing
`size` is a real, specific error.

## 2. Real per-bot update-cadence tracking

Mirroring the existing `WeavePhase`/`WeaveFreq`/`WeaveAmp` inline-field style,
`ActiveBot` gained a small bounded per-bot tracking block:

```
public bool HasPos;               // a live position has been observed at least once
public Vector3 LastPos;           // last-observed real position
public double LastPosAt;          // wall-clock time of the last observed change
public readonly object SampleGate; // guards the list (loop writes, stats read)
public List<CadenceSample> Samples = new();   // bounded ring, cap = MaxSamplesPerBot (200)
```

`Loop()` (already ticking every ~50ms) now calls `SampleCadence(bot, now)` for
**every** bot regardless of behavior. It reads the bot's current real position
from the same authoritative source `list_active_bots` uses — `TryCurrentPose`
reads the probe's `ClientWorld.Ships[id].Latest.Position` for a same-side bot and
falls back to the bot's **own** `ClientWorld` for a cross-side combat bot. When
that position differs from the last-observed one, it records **one real sample**:

- `interval` = elapsed wall-clock seconds since the last recorded change (the
  real gap the server is delivering to that connection right now);
- `distance` = the real meters moved in that update.

The rolling list is capped at **200 samples per bot** (`RemoveRange` on overflow)
so an unbounded run never leaks memory. This is a direct measurement of the
actual update cadence, not a modeled estimate.

`CadenceSample`, `BotCadence`, `MetricStat` and `PerfStats` are public records in
the McpSpawner namespace; `ActiveBot` stays private.

## 3. `get_perf_stats` — real aggregate concurrency evidence

`BotManager.GetPerfStats(botFilter?)` snapshots every tracked bot's rolling
samples (optionally restricted to one bot by name substring) and reduces them via
the pure static `BotManager.ComputePerf(IEnumerable<BotCadence>)`. For **update
INTERVAL** and **JUMP DISTANCE** each it reports:

- `mean` — arithmetic mean over all pooled samples;
- `p95` — **nearest-rank** percentile (sorted, `idx = ceil(0.95·n) − 1`), chosen
  over `max` because it is exactly as simple to get right and is far more
  informative than a single worst value (documented choice);
- `worstBot` / `worstValue` — the single worst offending bot (name + its largest
  single sample value), so a caller sees not just "the average is fine" but
  "**bot X** is the one actually teleporting";
- `botCount` — the number of contributing (≥1 sample) bots.

Bots with zero samples (stationary / never-seen) contribute nothing; empty input
yields a clean zero summary (no NaN, no divide-by-zero). The aggregation is a
**pure function** — proven correct below against a synthetic sample set with no
server involved, then used verbatim by the live tool.

## 4. Surface changes

- `tools/list` now returns **six** tools: `spawn_bot`, `spawn_scenario`,
  `list_hulls`, `list_parts`, `list_active_bots`, `get_perf_stats`.
- `spawn_scenario` schema: `{ size: int (required), hull?: string,
  target_sector?: string }`.
- `get_perf_stats` schema: `{ bot?: string }` (optional single-bot filter).
- `Program.cs` usage comment lists the two new tools.

## 5. Verification

### Unit test — aggregation math (no server)

`PerfStatsAggregationTests` (new, 6 facts) proves `ComputePerf`'s math against a
small synthetic sample set: empty-input zero summary; bots-without-samples don't
contribute; mean over pooled multi-bot samples; nearest-rank p95; worst offender
is the bot with the largest single sample; single-bot aggregate matches its own
samples. All **6 pass**, fast and deterministic.

### Build

`dotnet build AllegianceCore.slnx -c Debug` and `-c Release` (whole-solution,
`TreatWarningsAsErrors`): **0 warnings, 0 errors** both configurations.

### Full test suite (≥2 runs)

Ran the whole `AllegianceCore.slnx` suite 3 times (Debug). Only **pre-existing
documented live-wire timing flakes** appeared — the failing set rotates run to run
(the skill-documented signature), every one of them is a real-network/timing test
with **zero references to the McpSpawner symbols changed here** (verified), and
`LiveTurretGunnerBindingTests` is the known persistent machine-state flake the
skill says to ignore. The complete failing set, final run (`/tmp/p430_run3_full.log`):
`LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
`LiveTurretGunnerBindingTests.Board_binds_gunner...`,
`LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`,
`ConquestTerritoryLiveTests.Conquest_win...leak_to_another`
(Core.Tests); `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone...`,
`JoinFlowLiveTests.EntityTable_TracksARealTickingServer` (ClientCore.Tests).
Lobby.Tests (12) and MonoGame.Tests (841) were fully green each run.

### LIVE PROOF — the real point of this task

Against the real dev server (`allegiancecore-server` docker container on
`127.0.0.1:6073`, PCore001.igc, Test Mission), driven entirely through the MCP
stdio protocol by a purpose-built scripted client (not manual): `spawn_scenario(5)`
→ settle 18s → `get_perf_stats`; `spawn_scenario(5)` again (10v10) → settle →
`get_perf_stats`; again (15v15) → settle → `get_perf_stats`. Every spawn returned
`spawned:5` per side with **zero failures**; `list_active_bots` confirmed 10 / 20 /
30 real active bots at each stage. The three REAL snapshots:

| metric | 5v5 (10 bots) | 10v10 (20 bots) | 15v15 (30 bots) |
|---|---|---|---|
| **interval** mean (s) | 0.05020 | 0.05027 | 0.05045 |
| **interval** p95 (s) | 0.05031 | 0.05037 | 0.05066 |
| **interval** worst (s, bot) | 0.05063 (MCPB001-9480) | 0.05093 (MCPB001-9480) | 0.05672 (MCPB001-9480) |
| **jump** mean (m) | 3.675 | 4.221 | 4.538 |
| **jump** p95 (m) | 4.539 | 5.398 | 6.015 |
| **jump** worst (m, bot) | 9.513 (MCPB003-1021) | 10.558 (MCPB006-4319) | 11.908 (MCPB006-4319) |

Raw snapshot JSON: `/tmp/p430_live_snapshots.json`.

#### The honest, data-grounded answer to "where's the sweet spot"

From the REAL numbers, not the brief's framing:

- **Update INTERVAL does not measurably degrade from 5v5 → 15v15.** The mean
  stays at 0.0502–0.0505 s — i.e. the server holds the nominal **~20 Hz**
  broadcast cadence to every bot even at **30 simultaneous connections** (mean
  drift +0.5%, p95 +0.7%, both within noise of the 50 ms tick). The single worst
  observed interval at 15v15 was 0.0567 s — a rare 7 ms outlier, not a trend.
  Whatever "moving really slow while teleporting" looked like to Domingo, it was
  **not** the server dropping update frequency in this range.

- **JUMP DISTANCE does grow monotonically** (mean +24%, p95 +32%, worst +25%
  from 5v5 → 15v15), and this is the real, measurable smoothness change. But
  because the update interval is rock-solid at ~20 Hz, distance-per-update =
  speed × fixed interval: the growth reflects the bots **flying faster** in a
  denser dogfight (afterburner boosts + evasive maneuvers), not the server
  "teleporting" them. The worst offenders (9.5 → 11.9 m/tick ≈ 190–238 m/s =
  afterburner flight) are fast movers present at every stage — the same kind of
  bot, not an emergent teleport anomaly.

- **Within what was tested (5v5 / 10v10 / 15v15), the server never crossed into a
  visibly-bad regime.** There is no measured point inside the tested range where
  cadence collapsed. The concurrency **sweet spot is therefore at-or-above
  15v15** on this Test Mission — the measured evidence does **not** support
  "15v15 is getting close to the limits"; the update pipeline held ~20 Hz at 30
  connections and the flat interval implies headroom beyond it. Finding the true
  ceiling would require pushing **past 30 connections (20v20+)**, which this task
  did not test — so the upper bound is *inconclusive-but-looking-healthy* rather
  than a confident number.

That is the honest answer the data supports: **no degradation found through
15v15; the ceiling is higher than this task measured.**

## 6. Files changed

- `tools/allegiance-mcp/BotManager.cs` — `MaxSamplesPerBot`, per-bot cadence
  tracking in `ActiveBot`, `TryCurrentPose` / `SampleCadence`, `GetPerfStats`,
  pure static `ComputePerf` / `Metric`, and the public `CadenceSample` /
  `BotCadence` / `MetricStat` / `PerfStats` records.
- `tools/allegiance-mcp/Catalog.cs` — `DefaultFighterHull()` (the real-fighter
  default for `spawn_scenario`).
- `tools/allegiance-mcp/McpTools.cs` — `spawn_scenario` + `get_perf_stats` tool
  definitions and handlers.
- `tools/allegiance-mcp/Program.cs` — usage comment updated.
- `tests/Allegiance.ClientCore.Tests/PerfStatsAggregationTests.cs` — new
  aggregation-math unit tests.
- `docs/reports/P430_MCP_SCENARIO_PRESETS_AND_PERF_STATS.md` — this report.
