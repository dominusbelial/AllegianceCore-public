# P677 — per-viewer interest tiers

`broadcast.interestTiers` / `ALLEGIANCE_BROADCAST_TIERS` is implemented and defaults
to **off**. Changes remain in the working tree; no commit or push. The architecture
file named by the brief (`docs/architecture/SERVER_PIPELINE.md`) is absent from
this checkout, as recorded in the P668/P669 reports.

## Policy and ownership

| Tier | Membership, after existing visibility filtering | Requested default |
| --- | --- | --- |
| T0 | Own ship, current ship command target, enemies inside engagement range | Full broadcast rate (20 Hz in these tests) |
| T1 | Same-side/allied ships and other contacts within `midDistanceRange` | 5 Hz |
| T2 | Remaining distant contacts | 2 Hz, raised to P659's `minUpdateHz` when higher |

The engagement radius is at least `engagementRange` (default 1,500 world units)
and expands to the greatest mounted gun reach on either combatant: effective
projectile lifespan × projectile speed. `midDistanceRange` defaults to 6,000.
Command-target promotion wins over alliance and distance. Existing transmission
gates still apply: the native target gate requires a non-self, non-boarded ship
in the viewed cluster (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4081-4104`),
and the managed cloak visibility filter must allow it. A target classification
does not disclose a hidden cloaked ship or a target in another sector. Own telemetry and target lead indicators retain their existing
unthrottled paths. Status, identity, loadout, visibility, docking and death events
retain their existing transition/dirty delivery; tiers change remote pose rates.

Each broadcast copies ship id, position, side, alliance, command-target id and
mounted weapon reach into immutable scalar records **after simulation and before
viewer selection**. The simulation-side identity dictionary is read-only during
sector-worker selection and cleared when that selection completes. A detachment
guard and mutation test cover these records. Classification reads the copied
values, not mutable poses on publication threads. The publisher receives only
selected detached wire records and viewer references from the P668/P669 path.

The command target remains the first heavy entry. Other candidates are ordered
by tier, then descending `Deviation` within each tier. Full-pose serves reset
deviation; skipped records retain the existing deterministic +1 staleness bump.
The tier path sends full HEAVY poses at its configured rates, replacing the
heavy/light quotas and additive floor sweep. Presence-only LIGHT records cannot
satisfy a pose deadline. The previous ranked/budget/floor path is unchanged when
tiers are off.

The period is `max(1, floor(tickHz / effectiveHz))` broadcast ticks. Rates are
rounded up to that whole-tick cadence and capped at broadcast frequency. T1 is
at least T2; T2 is at least the effective P659 floor. Positive finite rates and
finite ordered radii are validated at boot. New/reappearing identities receive an
immediate pose. Stable viewer/ship phases spread recurring sends across ticks;
a separate monotonic wall-time deadline catches up after a stall. As with P659,
a stalled server cannot guarantee wall-time delivery while it is not ticking.
T0 bypasses cadence gating, so promotion takes effect on the current tick.

Tier mode suppresses the port's duplicate per-tick teammate SINGLE pose batches;
HEAVY is already a first-class remote pose source in ClientWorld and its dead
reckoner. This is necessary to make T1 rates effective. Enabling tiers also
enables immutable snapshots and encode-once, including when their independent
switches are false. Validated malformed environment values still fail boot.
`ALLEGIANCE_BROADCAST_TIERS=0|false` overrides a true JSON tier switch;
`1|true` enables it; whitespace and case are accepted.

All selected heavy records use P669's snapshot-local capture/cache and shared
buffer references. Common per-ship encoding stays O(ships) per tick; viewer
selection, relative-position encoding, record wrappers, packet assembly and CRC
remain proportional to selected viewer–ship pairs. Tier mode does not add a
second per-viewer ship encoder. The tests compare shared/unshared tier output
and serial/parallel output and bound full-heavy encoding counts.

## Native rules and adaptations

Every native reference below was inspected under `~/Development/Allegiance/src/`.
Interest tiers, configurable radii/rates, the pose floor and deterministic
staggering are managed policies, not a claim that native had this feature.

| Native rule | Source |
| --- | --- |
| Larger deviation has priority | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3614-3621`, sort call `:3923` |
| Served deviation resets; lighter/skipped bands accumulate deviation | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3937-3948` (native includes random jitter; the existing managed accumulator is deterministic) |
| Command target occupies the first heavy slot | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4141-4146` |
| Candidate update freshness acceptance | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3767-3775` |
| 1,600 bytes/s nominal budget and fixed 2 heavy + 2 light quota | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3836`, `:3844-3845` |
| Same-side/alliance-group equivalence | `~/Development/Allegiance/src/Igc/igc.h:4297-4303` |
| Energy-weapon lifespan includes side lifespan multiplier | `~/Development/Allegiance/src/Igc/weaponIGC.h:165-171`; projectile speed is read at `~/Development/Allegiance/src/Igc/weaponIGC.cpp:218` |
| LIGHT lacks a pose; HEAVY extends it with kinematics | `~/Development/Allegiance/src/Igc/igc.h:2423-2442` |
| Relative positions use the viewer's position reference | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4127`; `~/Development/Allegiance/src/Igc/igc.h:2243` |
| LIGHT follows HEAVY in the non-guaranteed send | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4183-4196` |
| Dead reckoning replays movement in at most quarter-second chunks | `~/Development/Allegiance/src/Igc/shipIGC.cpp:3270-3285` |
| Native heavy-update acceptance has a ±1.5 s window | `~/Development/Allegiance/src/Igc/shipIGC.cpp:3114-3115` |

**Classification:** native's fixed 2+2 cap and 1.6 kB/s budget are **era
constraints**, rather than gameplay requirements. Tiers preserve target priority
and deviation order while spending a configurable pose rate on each contact.
The existing managed dead-reckoner's 1.5 s hold is a port adaptation informed by
the native acceptance window; the normal 0.2/0.5 s tier gaps stay below it.

## Measurements and validation

### Dead reckoning through TCP

The new `ShipDeadReckonerTests.Interest_tier_TCP_poses_bound_extrapolation_error`
uses the real P448 dead reckoner and the production HEAVY codec over loopback
TCP. A synthetic scout hull flies for 10 seconds at 20 Hz; render poses are
queried at 60 Hz. Updates arrive at 5 Hz or 2 Hz. Truth advances with the actual
Ship flight model, with constant-velocity sub-tick truth between server steps.
The cases cover a sustained climbing turn, throttle changes and yaw reversals.

| Rate | Manoeuvre | Max position error (m) | RMS error (m) | Max seed correction (m) | Max 60 Hz frame step (m) |
| --- | --- | --- | --- | --- | --- |

| 2 Hz | reversal | 5.043 | 1.551 | 5.899 | 5.693 |
| 2 Hz | throttle | 6.356 | 1.634 | 4.803 | 5.128 |
| 2 Hz | turn | 1.632 | 0.703 | 1.204 | 2.341 |
| 5 Hz | reversal | 1.148 | 0.454 | 1.443 | 2.153 |
| 5 Hz | throttle | 1.719 | 0.400 | 3.275 | 2.503 |
| 5 Hz | turn | 0.598 | 0.228 | 0.748 | 2.005 |

The tests bound maximum error to 4 m for the sustained turn and 12 m for control
changes, and the maximum frame step to 14 m. Normal tier gaps continue to move
through the predictor rather than reaching its extrapolation hold. The largest
corrections occur when controls change between packets. This is a measured
predictor/codec check with synchronous loopback receipt, not a GPU capture or a
latency/jitter study; it does not establish that every abrupt manoeuvre is
visually imperceptible. No renderer or prediction tuning was changed.

### Local bot combat

Three paired runs connected 12 real Bot TCP clients: six armed shooters and six
paired target bots in separated firing lanes. Each shooter acquired its command
target through actual HEAVY receipts before firing. Real weapons, projectiles,
collisions, deaths and reliable kill receipts were exercised. Targets started at
25% hull and used a null side for final deaths rather than lifepod replacement;
they held position. This is a controlled target-acquisition/fire regression,
not a claim about long-running autonomous fleet tactics.

| Run | Tiers | Kills | Time to all kills (sim s) | Kills / sim s | Target receipts per shooter |
| --- | --- | --- | --- | --- | --- |
| 1 | off | 6 | 1.250 | 4.800 | 24–24 |
| 1 | on | 6 | 1.250 | 4.800 | 24–24 |
| 2 | off | 6 | 1.250 | 4.800 | 24–24 |
| 2 | on | 6 | 1.250 | 4.800 | 23–24 |
| 3 | off | 6 | 1.250 | 4.800 | 24–24 |
| 3 | on | 6 | 1.250 | 4.800 | 24–24 |

Every shooter and target connection remained on real loopback TCP. Completion
uses server simulation time; wall time is recorded separately in
[the raw combat runs](P677_assets/combat-1.json). The dedicated tick-by-tick tier
test additionally requires every visible command target in heavy slot 0 on every
tick, including an immediate target switch; nearby enemies also appear every
tick. Local combat did not degrade in this fixture. The rig/221/200v200 lane was
not run, per the brief.

### Crowded measurement method

Ubuntu 26.04, Intel Core Ultra 9 285HX (24 logical CPUs), .NET SDK 10.0.400.
The crowded fixture uses actual TCP logon, seating, transport queues and async
receivers, with four publisher workers and four transport send workers. Both
halves of every pair enable snapshots and encode-once; only interest tiers
change. The measured model is **port**. The geometry is one sector, two sides,
a 250-unit grid, no weapons/AI, and every subject advances its pose each tick.

After 20 warm-up ticks, 120 controlled projection ticks are measured at a
requested 20 Hz. Cadence uses the same recorded 50 ms clock in both halves.
The fixture measures the broadcast suffix, not an advancing full-simulation
crowd. Each interval represents six **nominal** seconds; bytes per nominal second
below are a comparison at the requested cadence, not a claim that every measured
iteration met its 50 ms wall-time budget. Projection/publish timing is reported
separately. This distinction matters when the producer exceeds that budget.

The test records every viewer's published packet bytes and actual received TCP
payload bytes, excluding its drain barrier. It asserts that the on interval
reduces total published bytes by at least 20%. Packet totals include all emitted
categories and CRCs, excluding the five-byte transport frame headers and TCP/IP
overhead. Both sides drain through an explicit queue barrier; raw per-viewer
publication and socket totals are retained in the JSON artifacts. Peer identities
and visibility can vary across independently seated sessions, so results use
three paired sessions per population rather than claiming identical cross-run
inputs.

The production `[update-age]` line appends T0/T1/T2 sample counts, p50, p95 and
maximum seconds while tiers are enabled. Samples are remote viewer/ship pairs
**before** that tick's sends, classified using the current tick's tier. Own-ship
telemetry is not in these histograms. These are selection ages, not end-to-end
network ages; transport coalescing and client latency can add age. Bins are 50 ms
upper bounds, with exact maxima. Disconnects preserve the current histogram
window while releasing per-viewer tracking.

### Crowded Release results

Each cell below is the median of three paired Release sessions. Total bytes
cover the 120 measured ticks. Viewer rates use six nominal seconds at 20 Hz;
JSON files retain every viewer's raw bytes. Percentiles are nearest-rank values
computed per run, then medianed across runs.

| Peers | Tiers | Published MiB | TCP payload MiB | Mean / p95 / max viewer KiB per nominal s |
| --- | --- | --- | --- | --- |
| 256 | off | 260.23 | 260.22 | 173.48 / 174.10 / 174.26 |
| 256 | on | 121.11 | 121.11 | 80.74 / 87.07 / 87.65 |
| 512 | off | 1050.45 | 1050.45 | 350.15 / 351.20 / 351.36 |
| 512 | on | 424.16 | 424.16 | 141.39 / 151.09 / 152.11 |

Median paired TCP payload reduction was **53.5% at 256 peers** and
**59.6% at 512 peers**. All sessions retained the seated population
and had zero snapshot coalescings and zero publisher failures. At 256 peers,
off intervals' socket totals were slightly below their publication totals,
consistent with transport stream coalescing; the separate counters show that gap.

| Peers | Tiers | Producer mean / p95 iteration ms | Publish pool mean ms | Full heavy / single encodes per tick | Relative heavy slots per tick |
| --- | --- | --- | --- | --- | --- |
| 256 | off | 14.91 / 18.60 | 4.34 | 256 / 512 | 7470.4 |
| 256 | on | 18.59 / 20.77 | 3.64 | 256 / 256 | 23009.4 |
| 512 | off | 79.23 / 94.75 | 21.50 | 512 / 1024 | 33851.6 |
| 512 | on | 93.23 / 103.32 | 13.57 | 512 / 512 | 81052.4 |

The producer iteration includes pose advancement, interest input capture and the
complete broadcast projection suffix; pool timing overlaps it. Full-heavy
encoding remains one per selected ship per tick, while relative slots expose the
remaining per-viewer work. Disabling duplicate teammate SINGLE batches halves
single-record captures in this fixture. More HEAVY poses are selected because
near enemies become full rate and allies receive 5 Hz rather than only the old
heavy budget/floor. This explains why byte reduction does **not** imply lower
producer time. Both 512-peer intervals exceeded the 50 ms wall-time budget;
512 peers was the tested population ceiling in this session. These numbers do
not establish full-simulation or combat throughput at that population.

| Peers | Tiers | Producer allocations MiB/tick | Publish allocations MiB/tick |
| --- | --- | --- | --- |
| 256 | off | 4.49 | 5.46 |
| 256 | on | 5.41 | 3.37 |
| 512 | off | 16.77 | 21.01 |
| 512 | on | 18.99 | 11.14 |

Allocation counters cover the producer thread and publisher coordinator/workers,
respectively, excluding transport/client receiver allocations. Viewer wrappers,
selection lists, arrays and packet buffers still allocate. Raw pairs and all
12 profiler CSVs are in [P677_assets](P677_assets/); the Debug 256-peer repeats
are also retained separately. Timing observations come from a shared machine.

The following age bands are the median p50/p95/max across the on intervals:

| Peers | Tier | Samples per interval | p50 (s) | p95 (s) | Max (s) |
| --- | --- | --- | --- | --- | --- |
| 256 | T0 | 1,120,320 | 0.05 | 0.05 | 0.05 |
| 256 | T1 | 6,463,200 | 0.15 | 0.20 | 0.20 |
| 256 | T2 | 250,080 | 0.30 | 0.50 | 0.50 |
| 512 | T0 | 2,750,400 | 0.05 | 0.05 | 0.05 |
| 512 | T1 | 27,408,960 | 0.15 | 0.20 | 0.20 |
| 512 | T2 | 1,236,480 | 0.30 | 0.50 | 0.50 |

Both populations reported zero samples older than one second or three seconds,
and zero never-served pairs after warm-up. The 0.05/0.2/0.5 s maxima match full,
5 Hz and 2 Hz selection rates at the replay's 20 Hz clock. The off intervals'
original P659 distance-band histograms remain in the same raw JSON files.

### Compatibility proof

With tiers off, the 105-tick, 144-viewer recorded replay matches the saved
pre-P677 P669 SHA-256 transcript prefixes for all four configurations. These
hashes include peer, tick, coalescing key, guaranteed flag and complete packet
bytes/CRCs. The test compares eager, snapshot and shared-record paths for exact
per-peer bytes and order, then checks the saved baseline. Each configuration ran
three times in its isolated method invocation.

| Model | Parallel | Floor | Saved and current SHA-256 prefix |
| --- | --- | --- | --- |
| port | false | 0 Hz | `E9818924260ED254` |
| port | true | 2 Hz | `1C4519215B2B37DF` |
| native | false | 2 Hz | `3C2919674F7E403C` |
| native | true | 3 Hz | `27B42CC5C6DE7E55` |

The real 12-peer TCP identity replay also passed three times, comparing exact
receipt order within the transport's existing ordered/stream lanes. Cross-lane
interleaving remains scheduling dependent, as documented by P668/P669. The new
tier replay independently requires identical output with sharing off/on and
serial/parallel projection, for both update models. Full-heavy encodes are
bounded by ships × ticks rather than viewers × ships × ticks.

### Verification results

The final Debug solution and Release Core-test builds passed with **0 warnings and 0 errors**. All
**80 selected new/affected methods** ran **three times each in separate
method-filtered invocations**: **348 passed case executions, 0 failures, 0
skips**. This includes real TCP/loopback tests, codecs, snapshot detachment,
publisher coalescing, budgets, the P659 floor, target/deviation order, teammate
state, owner endurance, mining and shield delivery, and all predictor cases.

| Unfiltered assembly | Passed | Failed | Existing skips |
| --- | --- | --- | --- |
| Core | 4,349 | 0 | 0 |
| ClientCore | 939 | 0 | 1 opt-in showcase |
| MonoGame | 2,125 | 0 | 3 optional Meshy-pack checks |

The full assemblies ran sequentially after the build, including their live and
LiveTiming lanes. Core used `P669_MEASURE=1` to include the controlled existing
benchmark path as well as the new tier benchmark. No failure required the
three-run load-flake audit, and no quarantine tags, assertions or test deadlines
were relaxed. The method/case ledger is [isolated.json](P677_assets/isolated.json)
and full-suite outcomes and skip names are in [suites.json](P677_assets/suites.json).

The opt-in 15v15 MCP showcase and full-session client composition/kill test each
passed **3/3 with `ALLEGIANCE_BROADCAST_TIERS=1`**, so the ordinary-suite showcase
skip was explicitly exercised. All six paired Release measurement runs passed:
**3/3 at 256 peers and 3/3 at 512 peers**. Outcomes are in
[enabled.json](P677_assets/enabled.json) and [measurements.json](P677_assets/measurements.json).
Prediction metrics and full identity hashes are retained alongside those ledgers.
No rig, 221 or 200v200 scenario was run. No commit or push was made.

## Recommended default and reproduction

Keep the default **off**. For an explicitly enabled session, start with **T1 =
5 Hz, T2 = max(2 Hz, minUpdateHz)** and full-rate T0, with the default radii.
These are quality rates rather than a byte cap. Full-rate nearby enemies can
increase HEAVY work; in native mode the teammate SINGLE stream was already
suppressed, so the same settings can increase total egress. The crowded port
measurement cannot establish a native-mode bandwidth win or a full-combat CPU
win. Broader rig evidence should precede a default change.

```json
{
  "broadcast": {
    "interestTiers": true,
    "tier1Hz": 5,
    "tier2Hz": 2,
    "minUpdateHz": 2,
    "engagementRange": 1500,
    "midDistanceRange": 6000
  }
}
```

Equivalent enablement: `ALLEGIANCE_BROADCAST_TIERS=1`. The tier switch selects
snapshots and encode-once automatically. To restore the pre-tier stream, use
`ALLEGIANCE_BROADCAST_TIERS=0`; the independent snapshot/encode-once switches
continue to work as before.

The repository's `local.env` supplies this workstation's external artwork and
native source paths. Build with shared compilation disabled: an already-running
compiler server from another sandbox can see this worktree as read-only.

```sh
dotnet build -m:8 -p:UseSharedCompilation=false -v:q
python3 docs/reports/P677_assets/validate.py
```

The validation script runs each selected method three times alone, then the
three unfiltered assemblies sequentially, enabled local lifecycle checks, a
Release build and three paired Release measurements at each population.
Individual reproduction commands:

```sh
dotnet test tests/Allegiance.Core.Tests --no-build --filter 'FullyQualifiedName~InterestTier'
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~Interest_tier_TCP'
dotnet build tests/Allegiance.Core.Tests -c Release -m:8 -p:UseSharedCompilation=false -v:q
P668_PEERS=512 dotnet test tests/Allegiance.Core.Tests -c Release --no-build --filter 'FullyQualifiedName~Crowded_TCP_interest_tiers'
ALLEGIANCE_BROADCAST_TIERS=1 ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms'
```
