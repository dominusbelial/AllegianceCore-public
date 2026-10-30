# Server performance: the 200v200 baseline

This page explains how the server got from struggling at about 100 players to running 200-vs-200 battles at full speed. It
covers the target, how we measure, each change that moved the numbers, and the settings that came out of it. Those settings
are now **the baseline**: every later change is measured against them.

## The target

We picked a deliberately hard target: **two full teams of about 195 players each (≈ 394 connections) fighting a
complete match unattended**. That includes economy, research, construction, scouting, strike waves, a bomber finale, game over and the next match.
We adopted this "200v200" run as the project's standard test on 2026-10-12.

The server ticks at **20 Hz**, so each tick has a **50 ms budget** (100 ticks per 5 s window means full speed). The
acceptance bar for a busy window is p95 ≤ 40 ms, p99 ≤ 50 ms and time dilation ≥ 0.95. Above that, the game slows down
(see time dilation below) instead of stuttering.

## How we measure

**The rig.** One mini PC runs the server: AMD Ryzen 9 6900HX (8 cores / 16 threads), 32 GB, Docker, Native AOT build. A
separate Apple M4 Max runs the bots over 2.5 Gb wired Ethernet. The bots are the project's `allegiance-mcp` driver: 390
bot pilots in two worker processes, plus probes. Running the bots on their own machine matters: when bots shared a
machine with other work, the battles died in mass "no write progress" disconnects that had nothing to do with the server.

**The match.** The showcase scenario at size scale 13 on `m2xspiderw.igc`. Fleets "march" from home through the alephs and
converge on Alderan. The economy runs, the research commander unlocks tech, and a bounded finale ends each match. Civilisations are picked
at random per match.

**One A/B batch** = deploy one build or setting to the rig, let **2–3 full matches** finish unattended (game over → next
match → game over), and compare with the previous batch. We never cut a match in the middle. Batches are numbered (b39,
b50, b55, …).

**What the server logs** (every 5 s, `ALLEGIANCE_TICK_PROFILER=1`):
- `[tickprof]` — tick count, tick wall time mean / p95 / max, and per-subsystem timings;
- `[gcprof]` — GC pause total and worst, gen0/1/2 counts, allocation, heap after GC;
- `[tidi]` — time dilation, the factor the game slowed to;
- `[overrun]` — ticks that ran over budget;
- egress by message type, update age, and send-queue health.

**What we compare:** median tick, window p95 (median and p90 across windows), worst tick, windows that fell behind (< 95
ticks in 5 s), windows with time dilation, GC pauses > 20 / > 40 ms, allocation rate and heap per match. We also check that
the battle stayed real: kills per match, station kills, research, strikes, bot stall reports, and disconnects. A setting
that makes ticks cheaper by making bots stop fighting is a regression.

**Rules we kept:**
- A new idea goes behind a flag that is off by default.
- It is kept only if the 200v200 A/B improves.
- Behaviour must match the original game. A speed-up that changes what players see or what the rules do does not count.

## How it started vs. now

The workload grew along the way: early runs had no economy, research or finale. So early and late numbers are not the same
load, and the late numbers carry much more game.

| When (2026) | State | Result |
|---|---|---|
| Oct 9 | Original port design: one thread builds every player's update, a writer thread per connection, workstation GC | ~94 players already used one core at 93 % |
| Oct 10 | Tick profiler (P600) | Simulation was only 3–4 % of the tick; **broadcast fan-out was ~92 %**, and O(N²) in players |
| Oct 11 | Parallel per-player build (P603) + send-worker pool transport (P605) | 301 players: **60 → 96 ticks per 5 s**, tick 83 → 46 ms, threads 614 → 342 |
| Oct 11 | Native ship-update model (P604): the original's ranked HEAVY/LIGHT update budget | about **1/5 of the bandwidth** of the port model at 400 players |
| Oct 12 | Server GC (P619) | 392 players: tick **p95 1.2–1.8 s → 65–80 ms** (the port model had a gen2 GC storm every window) |
| Oct 14 | Automatic time dilation (P618) on by default | a 98-minute 200v200 match: mean 31 ms, p95 44 ms, worst 242 ms |
| Oct 27 | Immutable broadcast snapshot (P668) + encode once (P669), rig b42 vs b39 | big-fight peak tick **71–80 → 44 ms**; overruns **22–40 k → ~1 k**; worst time dilation 0.48 → 0.87 |
| Oct 28 | Big-fight tick budget (P863), rig b50 vs b49 | median tick **35.8 → 16.3 ms**; windows over 40 ms 55.6 % → 4.4 %; time-dilated windows 5.6 % → 0.6 % |
| Oct 28–29 | Native NPC travel (`sim.nativeGotoPlan`), rig b52 / b55 | performance neutral (16.2 ms); more station kills; a match ended by conquest before the deadline |
| **Now (b55)** | The baseline below, 3 full matches, 394 connections | **median tick 16.2 ms, window p95 ≈ 20 ms, 0 windows behind, worst GC pause 36 ms**, ~1,500–1,900 kills per match |

At the start, the server could not keep a 300-player game at full speed. Today a 394-connection match uses about a
third of the tick budget at the median.

## The baseline settings

| Setting | Value | Why |
|---|---|---|
| `broadcast.immutableSnapshot` + `broadcast.encodeOnce` | on | Build one read-only world snapshot per tick and encode each message once for everyone who gets it (b42) |
| Big-fight tick budget (P863) | built in | Profiled and cut the cost of crowded-sector ticks (b50); P863b trimmed surge allocation |
| `broadcast.parallelBuild` | true | Builds each player's updates in parallel; output is byte-identical to serial |
| `transport.mode` | `"pool"` | A small pool of send workers instead of a thread per connection; slow peers are skipped, not waited on |
| `broadcast.shipUpdateModel` | `"native"` | The original game's ranked ship-update budget; native behaviour and far less bandwidth |
| `sim.nativeGotoPlan` | true | The original game's NPC travel planning; native behaviour, no cost |
| Server GC (concurrent) | on | Built into the server project |
| `liveSettings.timeDilation` | `"auto"` | Under overload the game slows slightly instead of stuttering |

Since P887 every row is a code default: a fresh server runs the baseline without any settings. The README's "Recommended settings" lists the only extra the 200v200 rig needs (the per-IP connection limit).

## Options that stay off

| Option | Why it is off |
|---|---|
| `broadcast.interestTiers` | Halves update age (0.22 s) but costs +40 % bandwidth and +6–18 ms peak tick |
| `sim.nativeBroadcastOrder` (native parity) | +7 ms median tick on b51; to be re-tested after P863b, adopted if it costs < ~2 ms |
| `transport.protocol: "udp"` | Experimental lossy-link delivery; TCP pool remains default pending rig/WAN A/B. See [UDP transport](UDP_TRANSPORT.md) and [P895 evidence](reports/P895_REPORT.md). |
| `receive.decouple` | Only a local A/B so far (P673), never tested on the rig |
| Spotter cache, parallel sector cells | No gain on the rig; removed (P858) |

## What is still open

- **Heap per match:** the heap floor still rises from match to match without tick cost so far (investigation P884/P884b).
- **Time dilation ≥ 0.95 in every window:** the rare surge windows (≈ 0.6 %) are the remaining gap.

## Allegiance Index

**AIX = Players × Quality × Headroom^k × Battle**, in effective players at
full quality. It is a session comparison with three published numbers: the
absolute **AIX**, **AIX/server-core** (the server machine's weighted cores)
and **AIX/total-core** (the weighted cores of every machine in the run).
The rig card (schema 2) is a **list of machines**, each with roles (`server`,
`bots`, `client`), CPU, performance cores, efficiency cores, threads and RAM.
**Weighted cores = P + w·E with `EFFICIENCY_WEIGHT = 0.5`**; threads/SMT are
reported but never counted. The 0.5 weight is the conservative default because
no P-vs-E throughput ratio for these workloads has been measured; recalibrate
only with a cited measurement. **Machines is a label, never a divisor.**
Schema-1 cards (a `server` object plus `botHosts`) are still accepted and
convert on load with zero efficiency cores. See the
[history CSV](benchmarks/aix-history.csv), [rig card](benchmarks/rig-221.json)
and the [AIX reports](reports/AIX1_REPORT.md).

Example summary line — rig-221 (8 server P-cores + M4 Max 12 P + 4 E =
22 weighted cores): `AIX 545 (68.1/server-core, 24.8/total-core) …
[rig-221, 2 machines, 22 weighted cores (w=0.5); battle]`.

A busy window is a 5-second `[tickprof] window` with tick-wall mean **≥ 5 ms**
(the named `BUSY_MEAN_MS` constant). Quality is the fraction of busy windows
with **ticks ≥ 95, p95 ≤ 40 ms and dilation ≥ 0.95 when reported**. The
profiler label's `sim.timeScale` is used when available, with the following
`[tidi]` line taking precedence. Missing dilation is reported as missing
coverage. This score does not add a p99 condition to the requested bar.
Headroom is **min(4, 40 ms / median busy-window p95)**; it can be below one.

Players is the mean of the nearest UTC `peers` samples, within 10 seconds of
each busy window. Every busy window must have a match. Otherwise the entire
session falls back to final `[gameover-timing] ... winner=... peers=N`
summaries, then explicit `--players N`. Intermediate declaration/migration
lines are excluded. The supplied historical logs have no timestamps: b45–b47
use an explicit 394 from the rig scenario card; b49–b55 use final summaries.
A whole day's peers average is never substituted for a missing timestamp.

**The exponent is calibrated, not a capacity promise.** Four sequential local
showcase measurements use the current baseline (snapshot, encode once,
parallel build, pool transport, native ship updates and native GotoPlan,
server GC; interest tiers, native broadcast order and decoupled receive off).
The power fit uses `log(cost) = log(c) + a × log(peers)`, with `k = 1/a` only
for a positive slope and log-space R² ≥ 0.95. The measured a = 1.7543,
R² = 0.9253 fails that bar, so **k = 0.5** is the fallback used here. The
[report](reports/AIX1_REPORT.md) records the measured points, fit quality,
workload limits and comparison with P600/P603/P605. All history rows share
the same exponent; `--k` permits an explicitly documented recalibration.

**Battle** uses b55 as a fixed reference. Kill rate is the sum of the sides'
finale kills divided by bot-minutes in those completed match intervals;
`[scoring] ... final score ... kills=` is the fallback. Bot incidents use
their own capture uptime, since the supplied bot and server excerpts can
differ in length. Station credit comes from scoring `baseKills`, or a
proxy of distinct damage-backed station strike outcomes per match. A target
that disappears at rotation without recorded hits does not prove a kill.
Bot `[stall]` incidents within ±30 seconds of re-ready rotation markers
(using the bot log's uptime) are excluded. Disconnects are explicit bot
`disconnected` / `disconnect ... reason=` events.

Activity ratios are `min(1, rate/reference)`. Incident ratios are
`min(1, (1 + 1000 × reference rate)/(1 + 1000 × rate))`; the one-incident-per-
1000-bot-minute smoothing makes a zero-incident reference usable. Battle is
the geometric mean of comparable ratios, capped at one. Missing metrics
and zero-reference activity rates are **unscored**, with coverage reported.
The b55 evidence has no verified station kills, so that component is
unscored throughout the history. b45–b47 also lack final kill totals.
Do not compare partially observed Battle scores as though coverage were equal.
With only profiler evidence the output is labelled **server-only**, Battle
is omitted, and its numerical multiplier is one.

Run from the repository root (Python 3.10+; standard library only):

```bash
python3 scripts/allegiance-index.py \
  --server "$EVIDENCE/server/b55-prof-2026-10-29.log" \
  --samples "$EVIDENCE/samples/2026-10-29.csv" \
  --bots "$EVIDENCE/bots/b55/err.worker-1.log" "$EVIDENCE/bots/b55/err.worker-2.log" \
  --rig docs/benchmarks/rig-221.json --label b55 --json "$OUTPUT/aix-b55.json"
```

**Shared-machine variant AIX-S.** When one card machine holds both the
`server` and `client` roles (single-player skirmish: game client + in-process
server + local bot driver on one PC), or the card sets `"shared": true`, pass
`--frames` with the client's `[frameprof]` log and the tool additionally
prints **AIX-S**. A busy window is good for AIX-S only if the server window is
good **and** the client frame time in that window is good: an aligned (within
one 5-second window) `[frameprof]` window with **p95 ≤ `GOOD_CLIENT_P95_MS`
= 33.3 ms** — two 60 Hz frames, one dropped vsync, the 30 fps floor. Missing,
unmatched or timestampless client evidence fails closed (the window counts as
not good) and is noted. The client frame-time input is a log line the client
or skirmish harness writes per 5-second window (the client emits it since
P889 — `ALLEGIANCE_FRAMEPROF=0` turns it off; see below):

```
2026-10-29T16:28:21.017Z [frameprof] window 5s frames=298 frame-wall mean=9.8ms p95=16.4ms
```

ISO-8601 UTC timestamp, then `frames=N` and the mean/p95 of per-frame wall
time in milliseconds (the same stopwatch delta `Game1.SessionPerformance`
already records; an optional `sim.timeScale=` suffix is ignored). AIX-S is a
different measurement on different hardware: **never compare it directly with
dedicated AIX**, and never divide by it when comparing rigs.

`EVIDENCE` and `OUTPUT` name existing local directories. Repeat `--server`
for chronological pieces (b54 needs both dates); repeat `--samples` or
`--bots`, or supply several paths after either option. Timestamped profiler
overlap is deduplicated; untimestamped pieces must be nonoverlapping.
`--reference FILE` selects a reference JSON with `label` and `metrics` like
[the default b55 reference](benchmarks/aix-b55-reference.json).
The [batch hook](../tools/aix-batch.md) documents the operator's deploy-chain
command; this change does not modify deployment.

The supplied history rises from **AIX 217.9 at b49 to 507.5 at b50** after
P863, and **544.7 at b55 (68.1/server-core, 24.8/total-core, two machines)**.
b54, merged across midnight, is 519.8. b56 (2026-10-29, batch metadata not
recorded in the capture) scores 470.0 (58.8/server-core, 21.4/total-core) on
samples-matched players. b48 is explicitly unavailable because no server log
was supplied. Battle is only partly observed; these numbers summarize the
captured workload, not client smoothness, WAN latency, equal battle
intensity or guaranteed maximum player capacity.

## Running on your own hardware (P889)

The baseline above is sized for the 200v200 rig. A player's machine is not the
rig, so since P889 the server **sizes its worker pools from logical cores,
expected players and a host profile** instead of from the core count alone:

| Profile | Meaning | Effect |
|---|---|---|
| `dedicated` (default) | the server owns the box (a dedicated server, the rig) | full-core pools at large player counts, exactly the P887 behaviour |
| `shared` | the server shares one PC with the game client and/or the local bot driver (single-player **skirmish** and **training**) | reserves cores for the renderer and the bots before sizing the server's pools |

```jsonc
// server.json (top level)
"hostProfile": "dedicated",     // or "shared"; env ALLEGIANCE_HOST_PROFILE wins
"expectedPlayers": 20           // env ALLEGIANCE_EXPECTED_PLAYERS wins;
                                // absent = configured capacity (Σ teams × maxPlayersPerTeam)
```

The resolver (`src/Allegiance.Server/PoolSizing.cs`) is only the **default**:
an explicit `broadcast.parallelBuild`, `broadcast.maxDegreeOfParallelism`,
`transport.mode` or `transport.poolWorkers` still wins, and the environment
overrides still beat the config. At boot the server prints the decision and why:

```
[boot] sizing: profile=shared cores=24 players=14 → build=serial (1 worker), transport=thread-per-peer (reserved 6 cores for the client/bot driver) — shared machine: … [resolver]
```

The rules (thresholds chosen from the measurements below, not guessed):

- **Shared reserve:** `cores ≤ 2 → 1`, `≤ 4 → 2`, `≤ 8 → 3`, `≤ 16 → 4`, else 6
  cores are reserved for the renderer + bot driver. Dedicated reserves none.
- **Parallel build:** on from 20 expected players (dedicated) or 48 (shared),
  with one builder per 16 players, capped at `serverBudget − 1`; otherwise the
  serial builder (byte-identical payloads either way).
- **Transport:** a send pool from 12 players (dedicated) or 48 (shared), one
  worker per 4 players capped at the server budget; otherwise one writer thread
  per peer.
- **Skirmish** declares `profile=shared` and `expectedPlayers = team × 2 + 4`
  (both teams plus a commander and a probe seat per side); **training** declares
  `shared` with 2 seats. Small/Medium skirmishes therefore run serial +
  thread-per-peer instead of the rig's 23 builders and 24 send workers.
- **Bot driver:** it is sized from the same shared budget (`SkirmishBudget`):
  at most 4 worker processes, at least 1 per 64 bots, each capped by
  `DOTNET_PROCESSOR_COUNT = available / workers`; the skirmish driver runs
  workstation GC (`DOTNET_gcServer=0`) so it does not build one heap per core
  next to the renderer. The standalone 390-bot driver keeps server GC.

### Measured on a 24-logical-core workstation

Real offscreen spectator skirmishes (offscreen SDL, `--calibrate`, 15 s measured
window after a 5 s warmup, three runs; median of the three). "frame" is the
AIX2 per-Update wall frame time (`[frameprof]`); "tick" is the embedded
server's wall tick. Bot decisions are the driver's effective pass rate.

| Size (bots) | Setting | Frame mean / p95 (ms) | Tick mean / p95 (ms) | Bot decisions (Hz) |
|---|---|---|---|---|
| Small 5v5 (10) | pre-P887 (serial, thread-per-peer) | 24.76 / 147.83 | 1.27 / 1.64 | 20.2 |
| Small 5v5 (10) | P887 (23 build, 24 send) | 24.92 / 148.57 | 1.50 / 2.04 | 20.1 |
| Small 5v5 (10) | **resolver** (serial, thread-per-peer) | **24.79 / 147.79** | **1.30 / 1.59** | 20.1 |
| Small 5v5 (10) | calibrated (serial + 3-worker pool) | 24.76 / 147.97 | 1.23 / 1.59 | 20.1 |
| Medium 15v15 (30) | pre-P887 | 24.91 / 148.13 | 1.69 / 2.09 | 20.1 |
| Medium 15v15 (30) | P887 | 25.28 / 148.42 | 1.82 / 2.63 | 20.1 |
| Medium 15v15 (30) | **resolver** (serial, thread-per-peer) | 25.30 / 148.50 | 1.80 / 2.45 | 20.0 |
| Medium 15v15 (30) | calibrated (serial, thread-per-peer) | **24.90 / 147.97** | **1.66 / 2.11** | 20.1 |
| Large 30v30 (60) | pre-P887 | 25.21 / 146.48 | 2.96 / 3.72 | 20.0 |
| Large 30v30 (60) | P887 | **25.19 / 147.67** | **2.76 / 3.94** | 20.0 |
| Large 30v30 (60) | **resolver** (4 build, 16 send) | 25.20 / 147.84 | 2.88 / 4.03 | 20.2 |
| Dedicated 34 players | resolver (3 build, 9 send) | 25.29 / 148.45 | 1.85 / 2.53 | 20.0 |
| Dedicated 34 players | P887 (23 build, 24 send) | 25.28 / 148.65 | 1.81 / 2.48 | 20.1 |

What the numbers say:

- The **p95 frame time is the same across settings** on this host (~147 ms):
  it is dominated by periodic ~150 ms client-thread stalls (sector/asset work),
  not by the server pools. The steady-state mean frame time is ~25 ms in every
  cell. Client frame time therefore does **not** justify giving the server more
  cores, which is why the shared profile keeps its reserve.
- At 10 players P887's 24 send workers cost ~0.2 ms of tick and doubled tick
  p95 (2.04 vs 1.59 ms) for nothing; serial + thread-per-peer (the resolver)
  matches pre-P887.
- At 34 players the resolver's 3-worker/9-send pools are level with the
  23/24 P887 pools on a **dedicated** server (1.85 vs 1.81 ms) and cost a little
  on a **shared** machine (1.80 vs 1.69 ms), so the shared crossover moved to
  48 seats.
- At 64 players the resolver (4 build / 16 send) is within noise of both
  P887 (2.88 vs 2.76 ms mean) and thread-per-peer; the pool participates
  again without hurting. Bot decisions held at ~20 Hz in every cell.
- Capacity, measured on the server side: a tick of 2.9 ms at 64 players is
  ~2 % of the 50 ms budget, and the rig's 394-player baseline is ~16 ms, so
  **on this 24-core class of machine expect roughly 60–120 local skirmish
  players at full quality** (the limit there is the bot driver and the single
  renderer, not the tick). A dedicated server of the same class is sized for
  its configured capacity; the rig's 16-thread box holds 394.

### What to set on a small dedicated server

```jsonc
// 20 players on a 16-thread box: declare the real size and keep the default profile.
"expectedPlayers": 20,
"hostProfile": "dedicated"
```

The server then starts a 2-builder build and a 5-worker send pool instead of
15 + 16 workers, and prints `[boot] sizing: profile=dedicated cores=16
players=20 → …`. There is nothing else to tune: an explicit
`broadcast.parallelBuild` / `transport.mode` / `transport.poolWorkers` still
overrides the resolver, and `ALLEGIANCE_HOST_PROFILE` /
`ALLEGIANCE_EXPECTED_PLAYERS` override the config.

### Calibrating this machine (`--calibrate`)

The bounded calibration measures the machine instead of trusting the table:

```bash
# in the client: Showcase menu → "Calibrate this machine", or
Allegiance.ClientCore.MonoGame --calibrate medium --calibrate-exit
# client options: --calibrate-seconds 15..90, --calibrate-candidates resolver,serial,p887,pool,
#                 --calibrate-no-save, --calibrate-output FILE
```

It runs one real skirmish per candidate (`resolver`, pre-P887 `serial`, `p887`,
`build+2`, `pool`, `bots-1`) at the chosen size, watches the hottest fight, and
scores `frame p95 × tick p95 × (20 Hz / decisions)` normalised to the 60 Hz
frame / 20 Hz tick budgets. It writes `sizing-profile.json` (per machine and
per game build: the core count and the build's informational version must
match, otherwise the profile is ignored) and prints the candidates, the winner
and the calibrated-vs-resolver change. Explicit config/env still wins over the
profile. On this workstation the calibration chose a 3-worker send pool for
Small (score 0.273 vs the resolver's 0.353) and thread-per-peer for Medium
(0.370 vs 0.450); three post-calibration runs measured Small at 1.23 ms tick /
20.1 Hz (resolver 1.30 ms) and Medium at 1.66 ms / 2.11 ms p95 (resolver
1.80 ms / 2.45 ms) — equal or better at both sizes.

### AIX-S on one machine

The client writes the AIX2 `[frameprof]` frame log by default (one line per 5 s,
`ALLEGIANCE_FRAMEPROF=0` off) so a single-PC skirmish can be scored:

```bash
python3 scripts/allegiance-index.py --server <client log> --frames <client log> \
  --rig docs/benchmarks/p889-shared.json --players 14 --label p889-small
```

AIX-S only scores **busy** 5 s windows (server tick mean ≥ 5 ms). The local
skirmish cells above tick at 1.3–2.9 ms, so the tool reports *"No busy 5s
tickprof windows"* and emits no AIX-S for them — by design, not a missing
input. A larger match (or the rig, whose baseline is 16.2 ms) produces busy
windows and a real AIX-S. The committed shared-machine card is
`docs/benchmarks/p889-shared.json`. These local cells watched the spectator
observer, whose frame p95 is ~147 ms (sector rebuilds/cuts) — above AIX-S's
33.3 ms client bar — so use the real flying client for an AIX-S verdict; the
frame column above is a controlled A/B within the harness.

## Reports

P600, P603, P604, P605, P618, P619, P659, P668, P669, P863 and P863b in `docs/reports/` hold the local measurements and
design for each step. The rig A/B numbers above come from the 200v200 batches.
