# AIX1 — Allegiance Index

Implemented a stdlib Python session scorer, fixture tests, b45–b55 history,
a sanitized rig card and fixed b55 Battle reference. The README and performance
page link the definition; `tools/aix-batch.md` is the documented operator hook.
No deployment integration, commits, pushes or remote-host access were performed.

## Definition as implemented

`AIX = Players × Quality × min(4, 40 / median busy-window p95)^k × Battle`.
The unit is effective players at full quality. Divide only by the server's
physical cores (eight for rig-221); report two machines, including its bot host.
The rig card retains CPU, physical cores/threads, RAM, runtime, bot-host cores
and scenario. It contains no IPs or personal hostnames. RAM is 30 GB as supplied,
rather than silently replacing it with the rounded 32 GB in older prose.

`BUSY_MEAN_MS = 5.0`: only five-second windows with mean tick-wall ≥ 5 ms
participate. Good means ticks ≥ 95, window p95 ≤ 40 ms, and dilation ≥ 0.95
when observed. Dilation uses `sim.timeScale` in the profiler label and then
the following `[tidi]` line. Missing dilation remains unknown; JSON counts
coverage. Headroom uses the median of busy-window p95, not a pooled tick p95.
Non-five-second windows are ignored with a note. No busy windows is an error,
not a manufactured zero score. Timestamped repeated windows are deduplicated;
untimestamped pieces are concatenated in CLI order, preserving identical but
independent observations.

Players is the average nearest UTC sample (≤ 10 seconds away) for each busy
window, provided all busy windows match. The fallback is the mean peers in
final `gameover-timing ... winner=... peers=N` summaries, then `--players`.
The zero-peer declaration-start instrumentation and migration substeps do not
count. JSON and the printed breakdown name the source. Samples without a zone
are interpreted as UTC, as specified for these inputs.

## Calibration of k

Four sequential, real in-process showcase runs used PCore001 and
`m2xspiderw.igc`, research economy ×5000 / research time ×8, native travel,
and 150 seconds of observation after spawning each fleet. The server ticked
at 20 Hz. The measurements use the **median of the final 18 five-second
window means** (90 seconds), after the full roster connected. This is the
profiler's median-window-mean convention, not a median over retained raw
per-tick samples. No 5 ms busy filter was applied to calibration: filtering
would remove the small-player points.

| Bots | Connected peers | Median window mean (ms) | Window-mean Q1–Q3 (ms) | Median window p95 (ms) | Shots / hits / kills |
|---:|---:|---:|---:|---:|---:|
| 60 | 63 | 1.515 | 1.428–1.580 | 2.175 | 32 / 0 / 0 |
| 120 | 123 | 2.320 | 2.238–2.438 | 2.865 | 13 / 0 / 0 |
| 240 | 243 | 16.305 | 15.893–17.093 | 18.095 | 1248 / 0 / 0 |
| 300 | 303 | 18.390 | 18.150–18.773 | 20.480 | 1430 / 0 / 0 |

OLS in log space gives **cost = 0.000823103 × peers^1.754279 ms**,
**a = 1.754279**, **R² = 0.925274**, raw reciprocal **k ≈ 0.5700**.
The predeclared acceptance criterion in `fit_exponent` is positive a and
log-space R² ≥ 0.95. This fit fails it: **the implemented default and every
history row use the requested conservative fallback k = 0.5**. The uneven
scaling is visible in the small 63→123 and 243→303 increases versus the
large 123→243 increase. There are only four points, no independent replicate
uncertainty estimate, and zero reported hits/kills: these are opening/travel
captures, not a fitted 390-bot sustained battle capacity. The numerical fit
and phase limitations are both reasons not to present the reciprocal as a
certified capacity exponent.

The baseline was explicitly configured: immutable snapshot and encode once,
parallel build (23 workers), pooled transport (24 workers), native ship
updates, native GotoPlan, and server GC. Logs confirm interest tiers, native
broadcast order and receive decoupling off. Source revision was
`1d11f11487e02dc4a5d766b78b265ec1d0ab04d4`; SDK 10.0.400, Release JIT,
Intel Core Ultra 9 285HX, 24 physical cores / 24 threads. Bots shared the
measurement machine. Spot checks of one-minute system load ranged roughly
4.3–9.9; no synthetic load or other simultaneous showcase run was created.
This is not the rig's Ryzen/AOT environment.

An initial 120-bot attempt hit the default 64-connections-per-IP limit and
was killed by its 240-second process timeout; it never reached the requested
roster and is excluded entirely. The scratch harness raised that connection
limit to 512 for the successful larger runs. Successful larger runs had a
360-second process bound; all runs were sequential, with a 150-second
observation bound and zero simulation failures. The
60-bot point stayed below the original limit (63 peers); its peer count is
inferred from 60 pilots + probe + two commanders, checked against side
recipient counts. Later summaries report peers directly.

Small reproducible evidence: [calibration.json](AIX1_assets/calibration.json),
[fit-harness.txt](AIX1_assets/fit-harness.txt) and
[fit-project.xml](AIX1_assets/fit-project.xml). The preserved harness uses
`IPAddress.Loopback` instead of a literal address. Raw logs are retained in
`.scratch/aix1/fit-{60,120,240,300}.log`; the rejected attempt is
`.scratch/aix1/fit-120-cap-rejected.log`. To reproduce without editing tracked
C# (artwork must be configured as for P700/P730):

```bash
mkdir -p .scratch/aix1/harness
cp docs/reports/AIX1_assets/fit-harness.txt .scratch/aix1/harness/Program.cs
cp docs/reports/AIX1_assets/fit-project.xml .scratch/aix1/harness/aix-fit.csproj
~/.dotnet/dotnet build .scratch/aix1/harness/aix-fit.csproj -c Release
for bots in 60 120 240 300; do
  timeout 360 ~/.dotnet/dotnet \
    .scratch/aix1/harness/bin/Release/net10.0/Allegiance.ClientCore.Tests.dll \
    "$bots" > ".scratch/aix1/fit-$bots.log" 2>&1 || break
done
```

The scratch project uses the existing test friend's assembly name to access
the same internal BotManager/McpTools as the P700/P730 harness. Its assets
contain no game-runtime changes. The fit is independently reproducible from
the four points with the stdlib `fit_exponent` function; tests cover exact
power-law recovery and poor-fit fallback.

The historical cross-check cannot calibrate today's baseline: P600/P605 used
older broadcast paths and often XXV_simple rather than PCore001. Their
121→241 cost ratios give a≈1.85 (P600, 4.59→16.38 ms), a≈1.72 (P605 pool,
4.72→14.18 ms), and a≈1.81 (P605 thread-per-peer, 4.93→16.10 ms).
P603 directly measured ~122 peers at 3.74–3.79 ms parallel versus ~4.55 ms
serial (and says its 240-player run was not completed). The performance
page's later combined P603/P605 retrospective quotes 301 peers at ~46 ms
versus ~83 ms; that is context, not a P603 calibration point. These numbers support superlinear historical fan-out; they do not justify
claiming an exact modern exponent or transferring local costs to the rig.

## History

All available batches were processed by the CLI with the same k, rig card and
b55 Battle reference. Detailed components, coverage, window counts and notes
are in [history-details.json](AIX1_assets/history-details.json); the machine
readable history, including each change description, is
[../benchmarks/aix-history.csv](../benchmarks/aix-history.csv).

| Batch | Date | Commit | Players | Q | H | k | Battle | AIX | AIX/core |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| b45 | 2026-10-27 | 78fc6162 | 394 | 0.5802 | 1.063 | 0.500000 | 0.985 | 232.2 | 29.0 |
| b46 | 2026-10-27 | 78fc6162 | 394 | 0.4827 | 0.981 | 0.500000 | 0.943 | 177.6 | 22.2 |
| b47 | 2026-10-27 | 78fc6162 | 394 | 0.3094 | 0.832 | 0.500000 | 1.000 | 111.2 | 13.9 |
| b48 | 2026-10-27 | 78fc6162 | — | — | — | — | — | — | — |
| b49 | 2026-10-28 | b37b4efe | 394 | 0.5596 | 1.021 | 0.500000 | 0.978 | 217.9 | 27.2 |
| b50 | 2026-10-28 | f12a8fa9 | 394 | 0.9557 | 1.894 | 0.500000 | 0.979 | 507.5 | 63.4 |
| b51 | 2026-10-28 | f12a8fa9 | 394 | 0.9305 | 1.389 | 0.500000 | 0.961 | 415.1 | 51.9 |
| b52 | 2026-10-28 | f12a8fa9 | 394 | 0.9638 | 1.924 | 0.500000 | 1.000 | 526.7 | 65.8 |
| b53 | 2026-10-28 | f12a8fa9 | 394 | 0.9845 | 1.934 | 0.500000 | 0.994 | 536.1 | 67.0 |
| b54 | 2026-10-28 | 48cdb2de | 394 | 0.9608 | 1.914 | 0.500000 | 0.993 | 519.8 | 65.0 |
| b55 | 2026-10-29 | 72a82ee9 | 394 | 0.9861 | 1.966 | 0.500000 | 1.000 | 544.7 | 68.1 |

Every row uses rig-221, eight server physical cores, one bot host, two machines.
b45–b47 use explicit `--players 394` from the supplied scenario card. Their
server logs have no final peer summaries. b49–b55 use final gameover peers=394.
The samples were supplied to every CLI run, but none of the historical
profiler lines has timestamps, so no sample-to-window match is asserted.
b48 appears in batches.csv but has no server or bot capture: its numeric
cells are empty, and it is explicitly unavailable. b54 merges the Oct 28 piece
(1,102 windows) followed by Oct 29 (5,760 windows), for 6,862 total.
The additional b44 input was also run through the CLI: server-only AIX 16.0
(2.0/core), 70/1,347 good busy windows, explicit 394 peers. It lies outside the
requested b45–b55 history.

The rough comparisons, keeping k=0.5:

- **b49:** Q=0.559643, median p95=39.18 ms gives 222.8 before Battle, close to
  the proposed 223. Battle=0.977980 reduces it to 217.9.
- **b50:** Q=0.955686, median p95=21.12 ms gives 518.2 before Battle, close to
  519. Battle=0.979366 reduces it to 507.5.
- **b54:** merged Q=0.960770, median p95=20.90 ms gives 523.7 before Battle,
  versus the rough 536. The supplied two-piece capture and exact good-window
  bar produce that lower unpenalized value; Battle=0.992532 gives 519.8. No
  windows were discarded to approach the estimate.
- **b55:** Q=0.986088, median p95=20.35 ms and Battle=1 give 544.7, matching
  the proposed ≈544 within rounding.

## Battle derivation and evidence limits

Finale `values=side:kills=N/deaths=M` totals are summed across sides. Match
intervals run from the previous gameover to the next scored finale, using
five seconds per retained profiler window. Unscored conquest intervals and
trailing incomplete matches do not enter the kill-rate denominator. In a
capture starting mid-match, the first interval is only the observed excerpt;
use complete captures for valid comparisons. A scoring-only log instead uses
per-player final `kills=` totals over session bot-minutes; `baseKills` provides
station-kill credit. Deaths and scalar score are never substituted for kills.

The b55 reference has **5,325 kills across three scored matches / 58,825
bot-minutes = 0.0905227 kills per bot-minute**. Its bot-host card specifies 390
bots; peers include probes/commanders. With no bot count on the card, peers
are the disclosed approximation for bot count.

Bot logs are streamed. Both workers observe overlapping world kills, so their
cumulative gallery counts are not summed or substituted for final scores.
There are no finale/scoring totals in b45–b47: the kill component is unscored
there. Explicit bot disconnect events are counted; no such events occur in
these supplied captures. Rotation stalls are inferred from nearby `[p598]
re-ready markers using bot uptime (±30 seconds). b55 has eight worker stall
records: four near rotation are excluded, four remain. Its workers cover
about 9,117 seconds on average, giving **59,260.5 incident bot-minutes** and a
stall rate of **0.0000674986**. The difference from server exposure is explicit.

Station evidence uses scoring `baseKills` if available, otherwise distinct
`station-killed` strike outcomes with positive `hitsInWindow`, deduplicated
by match ordinal and target across workers. The supplied strike logs also
call a missing old target at rotation "station-killed", commonly with zero
hits and hull/shield -1. That is not evidence of damage. The b55 reference
therefore has zero verified station kills. A ratio to zero is undefined: the
station component is unscored in this history, with a note. This intentionally
avoids rewarding rotation cleanup as battle activity; richer scoring captures
would be needed to assess actual station destruction consistently.

For kills/stations: `component = min(1, rate/reference)` when the reference is
positive. For stalls/disconnects:
`component = min(1, (1 + 1000×reference_rate)/(1 + 1000×rate))`.
This is one pseudo-incident per 1,000 bot-minutes, a documented smoothing
choice so zero-incident references are usable. Battle is the geometric mean
of the available components, each capped at one. b55 is exactly one. Missing
metrics are unscored and listed, rather than inferred as success. All rows
have partial Battle coverage (stations unscored); b45–b47 additionally lack
kill-rate coverage. A Battle with no comparable metrics is explicitly
unverified at multiplier one. With no bot/finale/scoring evidence, the score
is labelled server-only and Battle is omitted from the printed formula.

Bot and server excerpts sometimes cover different durations: b50's bots span
about 150 minutes while its profiler excerpt spans about 101. Incident rates
use bot capture uptime, not server duration. JSON warns when durations differ
by >10%. This is still unaligned evidence: no precise causal attribution of a
bot stall to one server setting is possible. A/B conclusions require aligned,
complete captures with the same coverage and workload.

## Commands and validation

The batch hook is [tools/aix-batch.md](../../tools/aix-batch.md). An example
against the supplied local evidence (directories must already exist):

```bash
EVIDENCE=~/allegiance-evidence/AIX1/input
OUTPUT=.scratch/aix1
python3 scripts/allegiance-index.py \
  --server "$EVIDENCE/server/b55-prof-2026-10-29.log" \
  --samples "$EVIDENCE/samples/2026-10-29.csv" \
  --bots "$EVIDENCE/bots/b55/err.worker-1.log" "$EVIDENCE/bots/b55/err.worker-2.log" \
  --rig docs/benchmarks/rig-221.json --label b55 --json "$OUTPUT/b55.json"
```

For b54 pass both `--server` pieces chronologically and both samples dates.
For b45–b47 add `--players 394`. Omit bot logs for a profiler-only score.
`--k` and `--reference` permit an explicitly recorded calibration/reference;
never mix them silently between compared runs.

Validation completed:

- CLI ran on every supplied server batch: b44, b45, b46, b47 and b49–b55;
  b54 used both files. b48 has no input to run. Raw CLI outputs and JSON remain
  under `.scratch/aix1/bNN.txt` / `.scratch/aix1/bNN.json`.
- `TMPDIR="$PWD/.scratch/aix1" python3 -m unittest discover -s scripts/tests -v`
  passed **23/23, three separate runs**. Fixtures cover boundary classification,
  headroom cap, server-only/incomplete Battle, UTC samples/fallbacks, split-file
  merging and dilation, timestamp overlap, rotation exclusion, scoring,
  normalization, invalid inputs and good/poor power fits.
- `python3 scripts/parity-scorecard.py` passed, unchanged.
- `python3 tools/check-glossary-links.py` passed (338 distinct paths).
- `git diff --check` passed; the new text files use LF. New files were checked
  for personal paths, email addresses and literal IPv4 addresses; no new media.
- The scratch-only Release calibration project built with **0 warnings /
  0 errors**. No tracked C# files changed. Per the brief's specific gate, no
  whole-solution build, tools build or fast-tier run was required.

Logs: `.scratch/aix1/tests-{1,2,3}.log`, `parity-scorecard.log`,
`glossary-check.log`, `fit-build.log` and `backfill.log`. Scratch is excluded
from git. The reviewable evidence is small JSON/text under
`docs/reports/AIX1_assets/` (well below the 2 MB cap).

## What AIX cannot capture

This score does not measure rendered client smoothness, frame time, WAN
latency, packet loss, update freshness or fairness across scenarios. It does
not prove the local fitted exponent transfers to another CPU, player count
or battle phase. Physical-core normalization does not equalize CPU architecture,
clock, JIT/AOT, heterogeneous cores, shared-host load or memory bandwidth.
Quality weights each busy window equally; omitted idle windows can hide
setup cost. Missing dilation is not proof of full speed. Final peers are a
fallback, not the actual mean during every busy window. Unaligned or partial
Battle evidence limits comparisons. More headroom is an extrapolation, capped
at four; AIX is not a supported-player-count guarantee.

Native intent: this is a modern tooling addition; no native behaviour is
involved and no parity rows or runtime rules were changed.
