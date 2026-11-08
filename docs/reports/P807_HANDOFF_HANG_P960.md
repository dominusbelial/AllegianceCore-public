# P960 — P807 handoff waits and workstation hang investigation

Base: `cbd96ade`. Changes remain in the working tree; no commits or pushes.

**The historical 24-peer, two-handoff hang was not reproduced, and its exact
trigger remains unconfirmed.** This investigation did reproduce the shared
helper stalling at the first 394-peer handoff's client-receipt wait, captured
its state, and removed that helper's unnecessary simulation work while awaiting
network delivery. The final two-handoff test passes **10/10 alone, 3/3 with 16
processors, and 4/4 across two paired-process batches**, with **zero skips**.
Production server, client, transport, simulation, and timing budgets are unchanged.
This is a bounded harness correction and diagnostic improvement, not a claim
that the earlier two-hour failure's particular trigger has been proved fixed.

Compact receipts: [validation.json](P960_assets/validation.json).
Raw local logs, TRX, baseline source copies, and reproduction scripts are in
`.scratch/p960/` (ignored). External evidence storage is outside this lane's
writable roots, so raw evidence remains in the assigned worktree.

## Reproduction and controls

Artwork was loaded using this worktree's `local.env`. Every reproduction had a
process cap, reduced tick limit, or 30-second wait limit; no 100,000-tick wait
was run to its two-hour failure. The outer process caps also bound a blocked
predicate, tick, or teardown, which a loop's elapsed-time check alone cannot do.

| Original-flow control | Full two-handoff method invocations | Passed / Failed / Skipped |
|---|---:|---:|
| First diagnostic run | 1 | 1 / 0 / 0 |
| Default processors, workstation GC | 10 | 10 / 0 / 0 |
| 16 / 4 processors, workstation GC | 3 / 3 | 6 / 0 / 0 |
| Default processors, `DOTNET_gcServer=1` | 3 | 3 / 0 / 0 |
| Sequential Live settings, server GC | 1 | 1 / 0 / 0 |
| Release, sequential settings, server GC | 1 | 1 / 0 / 0 |
| Two simultaneous processes, 24 / 16 processors, server GC | 20 / 20 | 40 / 0 / 0 |
| 1 / 2 / 48 processors, server GC requested | 3 / 3 / 3 | 9 / 0 / 0 |
| Interest tiers explicitly off, 24 / 16 processors | 3 / 3 | 6 / 0 / 0 |
| Scratch driver using `Thread.Yield()` instead of `Sleep(1)` | 20 | 20 / 0 / 0 |
| Repeated method beside the actual 394-peer test, two processes | 40 | 40 / 0 / 0 |
| Original source with **only** `Pump.max` reduced to 2,000, 24 / 16 processors | 20 / 3 | 23 / 0 / 0 |
| Same-process probes after capped 394-peer setup | 2 | 2 / 0 / 0 |
| Same-process probe after all 394 peers and server migration completed | 1 | 1 / 0 / 0 |
| Original two-handoff flow after 394-peer correctness completed (quiet prefix only) | 1 | 1 / 0 / 0 |
| Original-flow control using `local.env` only, without exported asset paths | 1 | 1 / 0 / 0 |
| Untouched dispatcher test binaries, original default limit, outer 60 s cap | 1 | 1 / 0 / 0 |
| Actual class filter, default settings/GC, only tick cap reduced | 1 | 1 / 0 / 0 |
| Real xUnit case order: quieted 394-peer prefix before original two-handoff flow | 1 | 1 / 0 / 0 |
| **Total original-flow invocations** | **168** | **168 / 0 / 0** |

The 20- and 40-iteration scratch wrappers invoke the entire assertion-bearing
method repeatedly inside one xUnit test; those are method-invocation counts,
not separately discovered xUnit results. Required final qualification below
uses separate test processes/results for every run.

The original-source controls retain the unlocked `oldIds` reads and add no
output, disposal, extra synchronization, or ready-message changes. They passed
before the receipt-wait fix: passing trials cannot be credited to that fix.
The initial same-process 394-peer probes were allowed to continue after a capped
394-peer failure and explicitly recorded that failure; their wrapper result
must not be interpreted as a passing 394-peer benchmark.

The final prefix control used receipt-only polling **only for the 394-peer
prefix**, preserved its full correctness assertions and original non-disposal
of client sessions, then ran the original ticking 24-peer flow. Correctness of
all 394 peers completed; its existing phase budget failed (110.6 versus 78.0 ms);
the following original two-handoff flow passed. The untouched-binary control
copied the dispatcher's existing test output into scratch, verified identical
SHA-256 hashes of the test, Server and Core DLLs, and ran it without altering
its 100,000-tick default, under an outer 60-second cap. It passed in about two
seconds with zero skips. The available dispatcher assembly-info files identify
`cbd96ade`; stale source-version metadata is not established as the explanation.
Neither this control nor the separate file-only `local.env` control exports the
artwork/native-source variables. No main-checkout execution, edit, or build was
performed; only binary reads/copies were used.

The local dispatcher command record subsequently confirmed that the two
historical runs used `dotnet test tests/Allegiance.ClientCore.Tests --no-build
--filter FullyQualifiedName~P807GameOverHandoffTests` with no explicit sequential
settings or GC override in the command. A control with that actual class filter,
default GC, file-based asset configuration, and only the reduced tick cap ran
the two-handoff method first and passed it; the 394-peer method then failed its
initial connection wait. A second control forced 394-peer-first order through
xUnit's case-orderer interface, quieted receipt waits only in that prefix, and
preserved the original two-handoff body/static pumping behavior. The latter
also passed after the prefix completed correctness and failed a timing budget.
These controls cover actual case lifecycle ordering, not only manual nested
method calls. Raw source/results: `original-class-default/`,
`original-class-ordered/`, and `OrderedOriginalClass.cs` in scratch.

A broader Live test-order probe completed 20 tests, then its 180-second
inactivity cap aborted `SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase_instead_of_parking_at_home`
before P807 ran. SC2 deliberately observes **150 + 750 = 900 seconds**
(`SC2SupportPhaseLiveTests.cs:80`); this was an unsuitable diagnostic cap for
that test, not an observed SC2 assertion failure or a completed Live gate.
The cohort was not rerun or counted as passing.

## Captured stalled conditions

No stalled match number/state can honestly be supplied for the historical
24-peer failure: every local attempt of that method passed. The following are
**394-peer controls using the same original helper**, not substitutions for
that missing reproduction.

### Initial connection

The second order probe stopped on connection 331, `P807-p0330`, before game over:

- Condition: `MyShipId is not null && MySideId is not null`.
- Match context 0, 2,223 ticks, 30,003 ms, simulation time 606.700 seconds.
- Server mission `NotStarted`; sides 0/1 held 166/165 peers. Both sides were
  active, unready, and not forced ready.
- Pending rotations, pending side selection, player-ready entries, start
  countdowns, and restart deadlines each had count zero. Auto-seat was 0.2 s.
- Earlier clients had their assigned IDs. The last client remained connected
  with current/joined/side IDs all null, no mission, and no join error.
- The server had assigned ship 8426 on side 0 and logged its entire join burst.
- Telemetry advanced `built=12000` to `12100` while `published=4074` stayed
  fixed and pending snapshots continued coalescing.

Full state/console: `.scratch/p960/order-probe2/captured.log` and `result.trx`.

### First handoff: server finished, clients still Over

Controlled original-helper runs with 16 and 24 processors both timed out on:

```csharp
Enumerable.Range(0, ShowcasePeers).All(i =>
    Read(conns[i], () => conns[i].World.MyShipId is { } id && id != oldIds[i]
        && conns[i].World.GameOverResult is not null))
```

| Captured state | 16 processors | 24 processors |
|---|---:|---:|
| Whole process duration | 49.373 s | 52.004 s |
| Receipt-wait elapsed time | 30,009 ms | 30,027 ms |
| Extra simulation ticks in receipt wait | 1,642 | 1,608 |
| Server simulation time at failure | 335.350 s | 334.550 s |
| Pending rotations / pending seating | 0 / 0 | 0 / 0 |
| Peers on side 0 / side 1 | 197 / 197 | 197 / 197 |
| Connected clients with null current/joined IDs | 394 / 394 | 394 / 394 |

In both cases the server held the fresh `NotStarted` mission and every new
ship; all client worlds still showed `Over` (stage 3), null current/joined IDs,
and their old side. Both server sides were active, unready, and unforced.
No player-ready entries or countdown/restart deadlines existed. It was the
**client receipt** condition, not migration drain or ready/start, that stalled.
The diagnostic prototype's 394-peer match counter was still zero; that method
contains exactly one handoff. Final diagnostics explicitly mark it match 1.

Receipts: `.scratch/p960/original-394-{16,24}/result.trx` and
`.scratch/p960/original-394-comparison.json`. An additional same-process probe
captured the same all-394-null state after migration; the subsequent original
24-peer method still passed (`after-complete-394/captured.log`).

## Mechanism, classification, and remaining uncertainty

**Confirmed harness bug:** the original `Pump` called `TickSim` even after the
server transition was complete and only asynchronous client receipt remained
(`cbd96ade:tests/Allegiance.ClientCore.Tests/P807GameOverHandoffTests.cs:69`,
client waits at `:175` and `:272`). Every such call advances simulation time
and builds more broadcasts. This is visible on the other side in
`src/Allegiance.Server/Server.Broadcast.cs:117,129` and
`src/Allegiance.Server/Server.BroadcastSnapshot.cs:154-171`: the tick creates
and submits another snapshot, not merely a network poll. Pending snapshots
preserve ordered traffic while coalescing streams
(`src/Allegiance.Server/BroadcastSnapshot.cs:118-151`).

The captured controls show why counting more ticks is not a delivery deadline:
roughly 80 extra simulation seconds elapsed during each 30-second handoff
receipt wait, with the server transition already finished. Polling without new
simulation lets the already-offered publication and receive work drain. With
that change all 394 clients pass their identity/result/order/side assertions
in all three final standalone runs; only the separately ledgered timing sample
fails in one run.

This is **bug — test harness**, with no native gameplay replacement. It is not
evidence of a server race in ready/launch flow. Native's loop samples
`Time::Now()`, processes network messages, updates missions with that time, and
sleeps toward its update interval (`FedSrv/FedSrv.CPP:9361-9386`); the original
fixture instead generates a new simulation period whenever its receipt
predicate remains false.

**Still unproved:** the exact connection between this demonstrated helper
failure mode and the two historical 24-peer hangs. Neither their stalled
condition/match nor their workstation-versus-runner trigger was recovered.
No claim that core count, GC, `Read` locking, artwork, shared static state, or a
particular lost ready message caused those failures is warranted. The new
failure snapshot is intended to make a recurrence diagnosable.

## Workstation versus runner, with numbers

This host exposes 24 logical CPUs with all 24 in affinity. The brief supplies
seven recent runner passes on 16 CPUs; the runner was not contacted.

The minimally modified original-source 24-peer controls passed **20/20 at 24
processors**, process median **3.323 s**, range **3.046–3.774 s**; forced 16
processors passed **3/3**, median **3.723 s**, range **3.627–3.753 s**. The
separate original 394-peer handoff controls failed the same receipt predicate
at **both** counts (table above). Thus forcing 16 CPUs does not explain the
historical difference. These local controls vary logical processor count on
this workstation; they do not reproduce the runner's different hardware.

The heavy paired original-flow run observed load **8.18 / 5.19 / 3.92**; its
40 target-method iterations passed while its 394-peer companion stalled at
connection setup. Final paired qualification observed one-minute load
**7.21–7.49** and passed all four independently reported target tests.

## Fix and preserved proof

`tests/Allegiance.ClientCore.Tests/P807GameOverHandoffTests.cs:130` now separates
simulation predicates from receipt predicates:

1. During connect, tick until the server has actually seated that named peer;
   then await its **exact assigned ship and side IDs** without advancing sim
   (`:161-163`). This is stricter than accepting any non-null IDs.
2. Tick to drain pending rotations, then poll for every client's new identity
   without producing more simulation (`:366-368`). The 394-peer test retains
   its result-screen, same-side, wire-order, fresh-mission, and cost assertions.
3. Send the original real `PLAYER_READY` messages and tick until the actual
   server mission reaches `Started` (`:376-380`). No readiness or countdown
   assertion is bypassed; both complete rotations and all 24 peers remain.
4. Check a 30-second wall limit as well as the tick guard. Capture the exact
   predicate, match, iteration count, whether sim was advancing, and elapsed
   time. Print the captured console before cleanup, plus server/side readiness,
   pending rotation cursors, seating, deadlines, publication counters, and each
   client's old/current/joined IDs, side, stage, ready roster, and join error.
   A busy client state lock cannot hang the diagnostic snapshot indefinitely.
   Console snapshots share the synchronized writer's lock with background
   appenders (`CaptureLog`, `:120-124`). Construction precedes console redirection,
   so constructor failure cannot strand the redirect.
5. Read prior identities under each session's gate; own and dispose every
   session, including one whose connect fails before returning to its caller.
   These are defensive fixes, not established historical causes.

The 24-peer fixture uses the current baseline: immutable snapshot, encode-once,
parallel builder, pooled transport, native updates/GotoPlan, tiers off; runs use
server GC. The 394-peer **existing historical tiers-on calibration** stays
explicitly pinned for its unchanged migration budgets; it is not a new rig
performance measurement or a request to change deployment flags.

## Qualification and gates

| Final affected test series | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Two handoffs, ten standalone processes | 10 | 0 | 0 |
| Two handoffs, `DOTNET_PROCESSOR_COUNT=16`, three processes | 3 | 0 | 0 |
| Two paired-process batches, two processes each | 4 | 0 | 0 |
| 394-peer fixture, three standalone processes | 0 | 3 | 0 |
| **Final affected qualification total** | **17** | **3** | **0** |

The final round followed a review-only change to synchronize console snapshots
and construct the server before redirecting output; the complete required
repetition matrix was rerun. All three final 394-peer runs pass correctness
before their budget assertions. Durations are **38.081 / 43.053 / 40.816 s**
including VSTest startup, with 164 migration ticks each. Phase samples
**116.2 / 98.8 / 91.0 ms** exceed unchanged scaled budgets
**76.5 / 77.4 / 76.9 ms**. All three final timing results remain failures. Their worst migration ticks
record **104 / 79 / 90 ms of GC pause**, respectively.

The preceding qualification round was **19 passed / 1 failed / 0 skipped**:
its 394-peer phase samples were **11.7 / 12.2 / 115.6 ms** versus
**78.1 / 76.6 / 78.1 ms**, with durations **38.388 / 39.448 / 42.245 s**.
That failed burst (72/394) logged a 120 ms tick with **101 ms of GC pause**.
Under concurrent Live load an earlier candidate similarly completed correctness,
then failed phase 96.0 versus 77.3 ms. These are the existing timing family,
not delivery hangs; no budget was changed, and the passing earlier samples do
not supersede the final failed samples.

The dispatcher's separate local receipt records declare **229.7 ms**, GC pause
**68.9 ms**, sim work **160.8 ms** over its scaled budget, max phase **54.6 ms**,
and step **55.0 ms**. It was inspected read-only, without building in or changing
the main checkout. This additional declare-budget failure remains out of scope.

- Final whole-solution build: **0 warnings / 0 errors** (`build-review.log`; the earlier `build-final.log` also passes).
- `tools/build-tools.sh`: **all 121 projects built**, no warnings/errors.
- Glossary links: **653 paths valid**; parity scorecard exits successfully;
  `git diff --check` passes. No native rule or parity completion changed.
- One complete Fast tier: **FAIL — 13,510 passed / 5 failed / 3 skipped**,
  13,518 cases in **604.935 s**; all nine infrastructure checks pass. The three
  skips are optional Meshy-pack tests, not missing artwork. Nine passing/failing
  Fast executions exceed the calibrated 10 s limit. Each of the five failed
  methods and seven timing-offender methods (one overlap: 11 methods total)
  ran three times alone afterward. The full tier was run once and not rerun.
- Server/Core unchanged: AOT publish and showcase never-parks comparisons are
  not triggered. No HUD, training, service, port, or production env-var change.


All non-task isolation runs used the same artwork and server-GC setup. There
were **36 passed / 6 failed / 0 skipped** case executions across 33 processes;
theory methods include all their cases on each repeat:

| Unchanged test method / family | Three standalone outcomes | Disposition |
|---|---|---|
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | pass / pass / pass | Existing default-loadout receipt flake. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / fail | Existing missing partial-tip receipt with final lock 1.000; remains intermittent alone. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | pass / fail / fail | Existing expected flying state 3 / observed docked state 2 receipt intermittency. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail / fail / pass | Existing clock/receipt boundary; one sample sees 32.0 outside [28,31], another misses the receipt. |
| `BallotVotingLiveTests.Mutiny_passing_transfers_leadership_failing_keeps_it` | pass / pass / pass | Sweep saw an empty receipt collection; new exact-method ledger entry in the existing ballot family. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass / pass / pass | Functional checks pass; 9.519–16.322 s per case, so timing remains over the Fast limit in isolation. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 3/3 cases, three times | Passing timing offender; isolated cases 3.437–6.547 s. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 2/2 cases, three times | Passing timing offender; isolated cases 3.385–5.771 s. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass / pass / pass | Passing timing offender; isolated cases 3.576–3.821 s. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass | Functional checks pass; 9.601–11.393 s, still crosses the Fast limit alone. |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_a_station_obstacle_and_reaches_its_own_station_goal` | pass / pass / pass | Passing timing offender; isolated cases 8.103–8.804 s. |

The Fast result remains **failed**, and the 394-peer timing exception remains
open. No unrelated assertion, time budget, or production path was changed to
clear them. Full per-case receipts: `.scratch/p960/isolations.json` and the
matching `isolation-*/` directories; summary is included in `validation.json`.

Fast uses a worktree-backed temporary directory exposed through a directory
file-descriptor alias, so path-resolution fixtures do not accidentally discover
this checkout's ancestor `local.env`. Profile writes use a scratch XDG config
directory. Neither workaround changes a test assertion or uses RAM-disk scratch.

Setup correction: the first restore inadvertently retained NuGet's default
public source/audit configuration, and its audit refresh failed on a read-only
HTTP-cache write. Subsequent builds used `NuGetAudit=false`, an empty local
`RestoreSources`, and a worktree-local HTTP cache. No rig, mini, or Mac was
contacted. Public-source network traffic from that first restore was not
captured, so this report does not claim none could have occurred.

## Native intent

Re-verified in the local native source: `FedSrv/fsmission.cpp:3490-3527` returns
a restartable mission to NotStarted, resets side aggregates, recomputes
readiness, and resets the mission. `FedSrv/FedSrv.CPP:6987-7026` handles and
relays the real player-ready message; `FedSrv/fsmission.cpp:5485-5529`
recomputes side readiness. `FedSrv/fsmission.cpp:1926-1976,2231-2239` implements
the countdown/start transition. P960 adds no native gameplay rule.

## Modern approach

Drive simulation only when the predicate needs simulation, allow detached
publication/receive work to drain while waiting for receipt, and make failures
bounded and observable. Preserve the full peer/rotation/ready-up proof and the
original timing budgets. Retain the distinction between the helper failure
that was measured and the historical 24-peer trigger that remains unconfirmed.
