# AIX2 — whole-setup hardware, weighted cores and the shared-machine AIX-S

AIX2 extends the Allegiance Index so it represents the hardware actually used
— how many machines and how many cores — instead of only the server's cores,
and adds a shared-machine (single-player skirmish) variant. Tooling only: no
C# change, no deployment change, no native rule touched.

## Rig card schema 2

`docs/benchmarks/rig-221.json` is now a **list of machines**, each with a
name, role(s) (`server`, `bots`, `client`), CPU, performance cores,
efficiency cores, threads and RAM. The AIX must represent the whole setup, so
the card now lists every machine in the run, not only the server and an
opaque bot-host count.

- `rig-221-server`: AMD Ryzen 9 6900HX, **8 P-cores / 0 E-cores**, 16
  threads, 30 GB.
- `rig-221-bots`: Apple M4 Max, **12 P-cores + 4 E-cores**, 16 threads
  (no SMT), 128 GB, 390 bots.

Schema-1 cards (a `server` object plus `botHosts`) keep working: they convert
on load, mapping `physicalCores`/`cores` to performance cores with **zero
efficiency cores**, because a v1 card reported none. All fixture tests still
feed the loader a schema-1 card through that path.

## Core weighting

**weighted cores = P + `EFFICIENCY_WEIGHT` × E, with `EFFICIENCY_WEIGHT =
0.5`.** Threads/SMT are reported in the card but never counted as cores.

Justification for 0.5: AIX1's calibration measured only P-cores (the 6900HX
server and the shared local fit host); this repository contains **no measured
P-vs-E throughput ratio** for the bot-steering or server-tick workloads on the
M4 Max. Counting an E-core as a full P-core would overstate capacity with no
evidence; counting it as zero would understate a fleet that demonstrably runs
on it. Half credit is the conservative default, stated in the summary line
(`w=0.5`) and in the JSON (`efficiency_weight`). It is a single named
constant: recalibrate only with a cited measurement (a P-only vs P+E bot-host
A/B), never silently. The M4 Max's 4 E-cores add 14 weighted cores to its
12 P-cores; rig-221 totals **22 weighted cores** (8 server + 14 bot host),
matching the number anticipated in the tasking.

## Outputs

The summary line now prints all three scores; `machines` remains a **label
and is never a divisor**:

```
b55: AIX 545 (68.1/server-core, 24.8/total-core) = 394.0 players × Q 0.986 × H 1.97^0.5 × B 1.000
     [rig-221, 2 machines, 22 weighted cores (w=0.5); battle]
```

- **AIX** — unchanged absolute score.
- **AIX/server-core** — AIX divided by the server machine's weighted cores.
  For rig-221 the server has 0 E-cores, so this reproduces the previous
  per-physical-core number exactly: b55 = 544.703 / 8 = **68.088 ≈ 68.1**.
- **AIX/total-core** — AIX divided by the weighted cores of **every** machine
  in the run (b55: 544.703 / 22 = **24.759**).

The JSON carries `server_weighted_cores`, `total_weighted_cores`,
`efficiency_weight`, `machines`, `shared`, plus the legacy
`server_physical_cores`. Weighting affects only the divisors; the numerator is
untouched, so re-scoring cannot move AIX.

## Shared-machine variant AIX-S

A run is **shared** when one card machine holds both the `server` and
`client` roles (single-player skirmish: game client + in-process server +
local bot driver on one PC, P889), or the card sets `"shared": true`. In that
mode the tool — given `--frames` client frame logs — additionally prints
**AIX-S** with its own line and an explicit warning.

**Window rule:** a busy window is good for AIX-S only if the server window is
good (the existing ticks/p95/dilation bar) **and** the client frame time in
that window is good: an aligned `[frameprof]` window (start within one
`WINDOW_SECONDS` = 5 s) with **p95 ≤ `GOOD_CLIENT_P95_MS` = 33.3 ms** and at
least one frame. The constant is two 60 Hz frames — one dropped vsync — i.e.
the 30 fps floor below which control feel visibly degrades; it is stricter
than the server's 40 ms tick bar because a client that renders must also
present. **Missing, unmatched or timestampless client evidence fails closed**:
the window counts as not good and a note names the count.

```
p889: AIX-S 312 (39.0/server-core, 39.0/total-core) = 30.0 players × Q_S 0.913 × H 1.85^0.5
      [skirmish-pc, 1 machine(s), 8 weighted cores; shared server+client, client p95 ≤ 33.3ms; server-only]
  AIX-S is the shared-machine variant; never compare it directly with dedicated AIX.
```

AIX-S divides by the same weighted cores (server and total, which coincide on
a one-machine card). A dedicated-rig invocation ignores `--frames` and never
emits `aix_s`.

### Client frame-time input (P889 must emit this)

The client already measures per-frame wall time — `Game1.SessionPerformance`
records a stopwatch delta per `Update` call and aggregates a session
`session-performance.json` — but nothing timestamped per window exists, so a
minimal line format is defined here (no C# was changed in this task). The
client or the skirmish harness must write one line per 5-second window, with
ISO-8601 UTC timestamps, mirroring the server's `[tickprof]` shape:

```
2026-10-29T16:28:21.017Z [frameprof] window 5s frames=298 frame-wall mean=9.8ms p95=16.4ms
```

`frames=N` is the frames elapsed in the window; `mean`/`p95` are the mean and
95th percentile of per-frame wall milliseconds (the same quantity
`SessionPerformance` already computes). An optional ` sim.timeScale=X` suffix
is tolerated and ignored. Non-5-second windows are ignored with a note;
identical timestamped lines are deduplicated; conflicting ones are an error.

## Re-scored history

Every available batch was re-run through the CLI with the schema-2 card
(`.scratch/aix2/bNN.json|txt`). AIX and AIX/server-core are **bit-identical**
to the AIX1 history for all shared batches (asserted during the rebuild);
only the new `AIX/total-core` column and b56 were added.

| Batch | Date | AIX | AIX/server-core | AIX/total-core | Players source |
|---|---|---:|---:|---:|---|
| b45 | 2026-10-27 | 232.157 | 29.020 | 10.553 | CLI --players |
| b46 | 2026-10-27 | 177.592 | 22.199 | 8.072 | CLI --players |
| b47 | 2026-10-27 | 111.226 | 13.903 | 5.056 | CLI --players |
| b48 | 2026-10-27 | — | — | — | unavailable: no server log |
| b49 | 2026-10-28 | 217.889 | 27.236 | 9.904 | gameover-timing |
| b50 | 2026-10-28 | 507.504 | 63.438 | 23.068 | gameover-timing |
| b51 | 2026-10-28 | 415.127 | 51.891 | 18.869 | gameover-timing |
| b52 | 2026-10-28 | 526.741 | 65.843 | 23.943 | gameover-timing |
| b53 | 2026-10-28 | 536.141 | 67.018 | 24.370 | gameover-timing |
| b54 | 2026-10-28 | 519.777 | 64.972 | 23.626 | gameover-timing |
| b55 | 2026-10-29 | 544.703 | 68.088 | 24.759 | gameover-timing |
| b56 | 2026-10-29 | 470.023 | 58.753 | 21.365 | samples (nearest within 10s) |

**b56** (`~/allegiance-evidence/aix/b56/`, present) is added: a timestamped
profiler capture plus peers samples, no bot logs, so Battle covers only
`kill_rate` (0.916 against the b55 reference; stations/disconnects/stalls
unscored). Its commit/change are not recorded in the capture; the CSV row
leaves the commit empty and says so. The samples supplied are the
`2026-10-29.tz.csv` variant: the capture's log is UTC (`Z`) while the plain
CSV is naive local time (`-05:00` zone implicit); only the zoned file aligns
with the 16:28–19:38Z profiler windows, and with it the players source is
per-window sample matching (393.737), agreeing with the previously supplied
`aix-b56.json` (470.0228 / 58.7528).

## Tests

`scripts/tests/test_allegiance_index.py` grew from 23 to 33 tests; new
fixtures cover: schema-1→2 card conversion (roles, cores, idempotence),
efficiency weighting (12 P + 4 E → 14; 8 + 14 = 22 total), threads never
counting, machines never dividing (same AIX, different total-core, machine
count only a label), the AIX-S window rule with a fixture `[frameprof]` log
(server-good ∧ client-good, `aix_s = aix/2` when one window's client p95 is
bad), the inclusive 33.3 ms boundary, missing frame logs as an error,
fail-closed unmatched windows with a note, dedicated rigs ignoring
`--frames`, the CLI summary line (`/server-core`, `/total-core`, machine and
weighted-core counts) and the CLI printing the AIX-S label with its
non-comparison warning.

## Gate

- New/updated suite: **33/33 OK, three consecutive runs** (`python3 -m
  unittest discover -s scripts/tests`; scratch TMPDIR under `.scratch/aix2/`).
- `python3 scripts/allegiance-index.py` ran on **every history batch**
  (b45–b47, b49–b56; b48 has no input by design) with the schema-2 card; all
  AIX and AIX/server-core values unchanged from AIX1, verified numerically.
- `python3 scripts/parity-scorecard.py`: pass (no parity rows touched).
- `python3 tools/check-glossary-links.py`: pass (GLOSSARY AIX entry updated).
- No C# change, so no solution build or tier run is required by the brief.

## Limits

The total-core divisor mixes architectures; it is a capacity descriptor, not
a normalized throughput claim. `EFFICIENCY_WEIGHT = 0.5` is an unmeasured
default. AIX-S alignment tolerance is one window span, so a client log that
drifts a few seconds still matches but cannot attribute a specific frame
spike to a specific tick. AIX-S is never comparable with dedicated AIX, and
b56's commit is unrecorded, so its row cannot be tied to a revision.
