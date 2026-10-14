# P430 — scenario presets (5v5/10v10/15v15) + real concurrency stats for allegiance-mcp

## Why this task exists

Domingo, after watching several MCP-driven runs tonight: *"maybe we can
have some kind of predefined setups? 5v5 10v10 15vs15? and we can get
some stats to find what is the actual sweetspot for concurrency, I
think 15vs15 is getting close to the limits, I could see some bots
moving really slow while teleporting a bit."* Two real, related asks:
a composable scenario-size convenience, and actual measured evidence
for where real-time smoothness starts degrading — not a guess.

This task assumes **P429** (real `"combat"` behavior + `target_sector`
on `spawn_bot`) has already landed — build on it directly; if for any
reason it has not landed when this is picked up, stop and say so rather
than re-deriving combat behavior from scratch.

## Confirmed real building blocks already in this codebase

- `tools/allegiance-mcp/BotManager.cs`'s `SpawnBot` (the single-bot
  path, ~line 154) and its behavior `Loop()` (~line 240-266, ticking
  every 50ms) — the composite tool below is a thin loop over the same
  `SpawnBot` call, not a new spawn mechanism.
- Each `ActiveBot`'s live pose is already read from its own
  `BotClass.World.Ships[shipId].Latest.Position` (the same real,
  server-reported field `ActiveBotView`/`list_active_bots` already
  surfaces) — the real, authoritative source for "did a real update
  just arrive," not a synthetic clock.
- "Teleporting" (a bot's on-screen jump instead of smooth motion) is a
  real symptom of the server's per-tick broadcast falling behind at
  higher connection counts — i.e., the real wall-clock GAP between
  successive real position updates growing, and/or the real DISTANCE
  moved per update growing, for the same reasons vizfight's `updatesPerSecond`
  config and the existing 20Hz tick cadence exist. This task measures
  that directly rather than reasoning about it.

## Scope

### 1. `spawn_scenario` — a composite convenience tool

Args: `size` (integer — the count PER SIDE; support at least 5/10/15,
don't hardcode to only those three exact values if a caller passes
something else reasonable), `hull` (optional, default the same
real-fighter default `spawn_bot` already uses), `target_sector`
(optional, passed through to every spawned bot exactly as P429's
`spawn_bot` already accepts it — the SAME sector value for every bot on
both sides, so they rendezvous). Behavior: spawns `size` `"combat"`
bots on side 0 and `size` on side 1 (reusing `SpawnBot` directly, one
call per bot, same as a caller manually calling `spawn_bot` 2×`size`
times), and returns a real summary (how many succeeded on each side,
any real per-bot failures named individually — don't swallow a partial
failure silently).

### 2. Real per-bot update-cadence tracking

Extend `ActiveBot`'s existing per-bot state (mirror the existing
`WeavePhase`/`WeaveFreq`-style inline fields, don't introduce a
parallel tracking system) with: last-observed position, last-observed
wall-clock time, and a bounded rolling sample list (real interval +
real distance-jumped pairs — cap the list, e.g. last 200 samples per
bot, so long runs don't leak memory). In the existing `Loop()` tick
(~50ms cadence already), for each active bot: read its current real
`Latest.Position`; if it differs from the last-observed position,
record one real sample (elapsed wall-clock seconds since the last
recorded change, and the real distance moved) and update the
last-observed pair. This is a real, direct measurement of the actual
update cadence the server is delivering to that bot's connection right
now — not a modeled estimate.

### 3. `get_perf_stats` — real, aggregate concurrency evidence

A new read-only tool, no arguments (or an optional bot-name/id filter
if trivial): aggregates every active bot's rolling samples into real
summary numbers — count of bots, and for update INTERVAL and JUMP
DISTANCE each: mean, p95 (or max, whichever is simpler to get right
correctly — pick one, document the choice), and the single worst
offender (bot name/id + its worst value), so a caller can see not just
"the average is fine" but "bot X is the one actually teleporting."

## Explicitly out of scope

- Any change to the real server's own tick rate/broadcast logic, or to
  `docker/compose.server.json`'s `updatesPerSecond` — this task
  MEASURES the real behavior, it doesn't tune the server.
- Any change to `tools/vizfight`.
- Any new `spawn_bot` behavior beyond what P429 already added.
- A `despawn`/`recall` tool — unchanged standing limitation.

## Verification — real proof, not just build/test

- **Live proof, required, this IS the actual point of the task**:
  against a real running dev server, run three real scenarios in
  sequence — `spawn_scenario(5)`, wait a real settling period, call
  `get_perf_stats` and record the real numbers; then `spawn_scenario(5)`
  again (bringing the total to 10v10) and repeat; then again to 15v15.
  Report the three real stat snapshots side by side in the deliverable
  doc, and say plainly, from the REAL numbers (not a guess), whether
  interval/jump-distance measurably worsens as concurrency rises, and
  at what point (if any, within what was tested) it crosses into
  visibly-bad territory. This is the actual "sweet spot" answer
  Domingo asked for — it must come from real measured data in this
  run, not an assumption carried over from the brief's own framing.
- A test proving `get_perf_stats`' aggregation math (mean/p95/worst)
  against a small synthetic sample set (pure unit-testable logic, no
  server needed for this part).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. `spawn_scenario` + the per-bot cadence tracking + `get_perf_stats`,
   committed on `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P430_MCP_SCENARIO_PRESETS_AND_PERF_STATS.md`:
   the three real 5v5/10v10/15v15 stat snapshots side by side, and an
   honest, data-grounded answer to "where's the concurrency sweet spot"
   (or "inconclusive within what was tested, here's why" if that's what
   the real numbers actually show — don't force a confident answer the
   data doesn't support).
