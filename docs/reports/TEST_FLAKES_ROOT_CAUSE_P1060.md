# P1060 — recurring test flake root causes

2026-11-20, local time UTC−05:00. Started 15:35; completed 16:31, within the six-hour deadline.
All four requested families have proven harness causes and test-only fixes.
**No product code, native rule, wire behavior, timeout, trait or test tier changed.**
Codex lane: no commits or pushes. Commit-to-be reference: **P1060 working tree**.
Raw evidence is under `<lane evidence dir>` = the ignored `.scratch/p1060/` directory.

## Results and reproducibility

Each isolated invocation is a fresh test process selecting exactly one method.
Reproduction used ordinary test projects and at most three concurrent test processes;
no synthetic CPU load, busy load loop, package installation or external host was used.
Diagnostic repetitions repeat the real test bodies, without changing their windows.
Runtime: .NET 10.0.15; SDK 10.0.404. Artwork/native data came from the lane corpus.

| Family | Before | After |
|---|---:|---:|
| Remote death/respawn, exact method alone | **1/20 failed**; instrumented extension **6/100** | **0/20 failed** |
| Rejoined roster time, exact method alone | **5/20 failed** | **0/20 failed** |
| Jet scaffold, full parallel project | **0/5 failed** | **0/5 failed** |
| Jet symbology, full parallel project | **0/5 failed** | **0/5 failed** |
| Sound template, full parallel project | **0/5 failed** | **0/5 failed** |
| Jet scaffold, diagnostic body repetitions during full parallel Fast project | **7/200 failed** across two processes | **0/100 failed** |
| Jet symbology, same diagnostic | **4/200 failed** | **0/100 failed** |
| Sound template, same diagnostic | **6/200 failed** | **0/100 failed** |

The standard five-run batches did **not** reproduce the three allocation failures;
the diagnostic repetitions did. These are separate observations, not a claim that
five all-green samples alone establish the cause. The full-project batches used the
established clean-runner resource conditions: absent artwork/native paths and EGL
vendor manifest, DISPLAY/WAYLAND_DISPLAY unset. All three target cases execute,
with no resource skip. The baseline assembly uses the original six source files,
compiled with the same Debug configuration into a separate `bin/P1060Baseline`
directory; the fixed assembly is the normal Debug build. Both commands are otherwise
identical. Separately, five complete **artwork-equipped Fast-project** runs before
and five after also passed every requested allocation case (**0/5 each**).
The full clean project has 6,130 baseline / 6,132 fixed cases; the difference is the
two new measurement-guard controls. Historical unrelated failures keep these
project batches red overall; they are listed below.

Every build/test invocation set:

```bash
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1060/nuget-http"
DOTNET="$HOME/.dotnet/dotnet"
```

The isolated commands below ran 20 times before and 20 after, with distinct log/TRX
names and results directories per invocation:

```bash
"$DOTNET" test tests/Allegiance.ClientCore.Tests --no-build --no-restore \
  --filter 'FullyQualifiedName~DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn'
"$DOTNET" test tests/Allegiance.Core.Tests --no-build --no-restore \
  --filter 'FullyQualifiedName~GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster'
```

The full-project command ran five times per revision with clean-runner environment
variables as above, changing only `MONOGAME_DLL` between the baseline/fixed build:

```bash
"$DOTNET" test "$MONOGAME_DLL" --logger 'console;verbosity=normal' \
  --logger "trx;LogFileName=run-$i.trx" --results-directory "$EVIDENCE/$PHASE"
```

The separate artwork-equipped five-run commands, identical before/after:

```bash
"$DOTNET" test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --no-restore \
  --filter 'Tier=Fast' --logger 'console;verbosity=normal' \
  --logger "trx;LogFileName=run-$i.trx" --results-directory "$EVIDENCE/$PHASE"
```

The ignored `repeat.py`, `isolated-allocations.py`, `qualification.json`, per-run
TRX/logs, diagnostic targets and profiler source retain the exact commands and
outcomes. Baseline/fixed labels are `baseline-death` / `fixed-death`,
`baseline-roster` / `fixed-roster`, `baseline-full-clean-v2` / `fixed-full-clean`,
and `baseline-monogame-fast` / `fixed-monogame-fast-v2`.

## 1. Remote death latch and respawn

**Symptom.** “B never saw A's death (IsDead never latched on B) after 2500 ticks.”

**Proven cause.** The observer receives and applies both death messages. The test
then drives another simulation tick while awaiting TCP delivery. That next tick
sends the respawned pod's changed-loadout EXPORT, which clears `IsDead` in
`src/Allegiance.ClientCore/State/ClientWorld.cs:3543`. A test polling once per tick
can miss the entire true interval. The changed-loadout export is sent from
`src/Allegiance.Server/Server.Broadcast.cs:1159`; the server and client agree on the
victim ID and sector. This is an observation race, not a lost kill or server bug.
The pre-death `Assert.False(BravoSeesDead())` also accepted a missing entity as
“alive”; the fix requires an actual flying pose before injecting the kill.

Server send capture and a transparent receive-site wrapper logged every frame's
simulation clock and B's post-apply state. Three failing traces and one passing
trace are retained in `<lane evidence dir>/death-traces/`:

| Trace | Server sends kill, sim s | B applies kill, sim s | B applies respawn EXPORT | Observation |
|---|---:|---:|---:|---|
| `b37efeba11bb4f22baddc20612f822db.log` | 31.60 | 31.65, dead=True | 31.65, dead=False | misses latch; 2,500 ticks |
| `b75ab2830b35426a9933e547551a06aa.log` | 32.00 | 32.00, dead=True | 32.05, dead=False | misses latch; 2,500 ticks |
| `e325d97eec4949d38b4aba62cff342b1.log` | 32.05 | 32.05, dead=True | 32.10, dead=False | misses latch; 2,500 ticks |
| `60651102fc1d4b0eb9bbbab46f6fb538.log` | 31.70 | 31.70, dead=True | 31.75, dead=False | sees latch before next tick |

For the first failure, B receives KILL_SHIP at elapsed 1,169 ms, SHIP_DELETE at
1,170 ms (still dead), docked SHIP_STATUS, then EXPORT at 1,170 ms (alive). The
2500-tick wait expires at 5,012 ms. This rules out a missing send, dropped receive,
stale ship ID, sector rejection, and failure to apply the death. Delayed identity
arrival is not needed to explain the failure; B already knew A in this trace.

**Fix.** `tests/Allegiance.ClientCore.Tests/DeadLatchRespawnLiveTests.cs:88` adds a
receive-only wait. At `:453` the test first confirms B sees A flying; at `:458` it
drains the already-published death frames without advancing simulation. The same
2,500 one-millisecond polls remain. At `:467`, recovery requires both authoritative
server undock and B's visible current-sector pose, instead of merely `!IsDead`;
a docked loadout EXPORT alone can no longer produce a false recovery pass.
After: **20/20 pass**, also satisfying ten isolated repetitions. No limit widened.

## 2. Rejoined roster time

**Symptom.** A roster time such as 8.35 or 9.05 exceeds the estimated [4,8] seconds.
The body actually pumps **three seconds plus two seconds**, not two three-second
stints, before its network-wait overhead.

**Proven cause.** The clock is simulation time. Every TickSim call advances one
period (`src/Allegiance.Server/Server.Broadcast.cs:138`), including calls waiting
for other bots' assignment receipts and socket departures. A stint begins at the
server seat (`Server.Lobby.cs:4252`), before all clients finish receiving their
assignments, rather than at the test's “all seated” observation or the later manual
side-join calls. It does not begin at the TCP accept. Departure accumulation
(`Server.Lifecycle.cs:2623`) and game-over accumulation (`Server.Lobby.cs:1401`)
already match native `Igc/igc.h:5602-5613`, `m_dtPlayed += t - m_timeConnect`.
Neither disconnected time nor duplicate accumulation explains the failures.

The 20-run timestamp diagnostic (`trace-roster/`) includes this failing run 11:

| Boundary | Simulation seconds |
|---|---:|
| First server seat | 0.20 |
| All three assignment receipts observed | 1.95 |
| First departure drained | 5.00 |
| Re-seat | 5.20 |
| Re-seat assignment received | 7.00 |
| Elimination/finalization in PeerLeft drain | 9.00 |
| Deferred GAME_OVER packet publication | 9.05 |

The exact connected total is **(5.00−0.20)+(9.00−5.20) = 8.60 seconds**,
which is exactly the roster. The 0.20-second disconnected interval is excluded.
The initial diagnostic's publication clock was one tick later than finalization;
this identified why the fixed test must capture the drain boundary, not packet-send
time. Wall-time descheduling changes how many ticks the harness pumps, not the
server clock's definition.

**Fix.** `tests/Allegiance.Core.Tests/GameOverPlayersWireTests.cs:465` captures each
seat's clock and the closing drain boundary; `:483` checks the first folded stint,
`:486` deliberately adds a three-second disconnected gap, and `:531` asserts exact
first-plus-second equality. An overwrite, duplicate stint or counted gap fails.
**The old [4,8] limit was wrong for the test's stated intent:** it assumed bounded
TCP receipt overhead while advancing simulation freely. It is replaced by a
zero-tolerance equality, not a larger range. All 800/4,000/900 network-wait limits
remain. After: **20/20 pass**. No server/rule change was necessary.

## 3. Jet allocation pair

**Symptom.** Historical full-suite runs report 4,816/6,368 bytes while the methods
pass alone. The assertion measures **total bytes in the window**; the old ledger's
“B/frame” wording is not the unit of the failing equality.

**Proven cause.** A GC can retire the thread's allocation context and cause its
unused buffer space to appear in GetAllocatedBytesForCurrentThread. The apparent
allocation is GC accounting, not an allocated draw object. It cannot be explained
by another test's ordinary allocations being charged to this thread.

EventPipe first captured the selected 480-case cohort and a full parallel Fast
project. A scratch-only native profiler then enabled exact ObjectAllocated
callbacks and DoStackSnapshot for just the measured thread/window, avoiding
EventPipe's sampling blind spot for small bursts. It uses the existing compiler
and cached diagnostic assemblies, with no package installation. Two full-project
runs with 100 real repetitions of each body produced:

| Body | First process | Second process | Allocated-object callbacks in failing windows |
|---|---:|---:|---:|
| Scaffold | 4/100 failed | 3/100 failed | **zero** |
| Full symbology | 0/100 failed | 4/100 failed | **zero** |
| Sound ASR/cache hit | 3/100 failed | 3/100 failed | **zero** |

One 3,864-byte scaffold window spans 1,386.7229–1,394.3156 ms on native thread 484.
GC 71 suspends for GC preparation at 1,389.1214 ms and resumes at 1,390.9212 ms.
The native log contains no object-allocation callback between Start and Stop.
The second process catches 3,592-byte symbology windows with the same property.
Raw evidence: `gc-window-evidence.txt`, `native-repeats/`, `native-repeats-2/`, and
`native-allocation/1795207976-265003-446.log` /
`native-allocation/1795208510-763779-25.log` under `<lane evidence dir>`.

A separate controlled check executes only GC.Collect between the counter reads:

```text
START GC.Collect only    allocation-context free bytes = 7424
                        no ObjectAllocated callback
STOP  GC.Collect only   allocation-context ptr/limit = 0/0; counter delta = 7424
```

The context offsets come from the running runtime's embedded CDAC descriptor;
this diagnostic safely reads only the current thread's own allocation context.
Evidence: `counter-retirement-context.log` and
`native-allocation/1795208544-956502-32.log`. It proves the exact unused-buffer
accounting mechanism, independently of rendering, audio, caches and JIT tier-up.
A positive control allocates a real byte[16]: both the counter and exact profiler
report **40 bytes**, with this allocation stack:

```text
P1060CounterRetirement.Inspect_one_real_object  [new byte[16]]
System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method
System.Reflection.MethodBaseInvoker.InvokeWithNoArgs
Xunit.Sdk.TestInvoker.CallTestMethod
```

Evidence: `profiler-positive-control.log` and
`native-allocation/1795208671-359606-25.log`. The only other first-use object seen
by the profiler was a diagnostic Stop-method string **after** the measured counter
read; it is explicitly outside the failing intervals. There is no allocating game
call stack to “fix.” Product code, lazy warm-up, shared cache eviction, inline async
continuations and event subscriptions are ruled out by the zero exact callbacks
and the independent counter-only experiment. A further full Fast-project diagnostic
uses the original bodies with **no tracing, EventSource, profiler or allocation listener**;
it reproduces scaffold and sound counter failures (including the original P902
fact at 4,032 bytes), ruling out diagnostic instrumentation as the cause. Its
complete rates are **2/100 scaffold, 0/100 symbology, 2/100 sound failures**,
plus the original sound fact failing at 4,032 bytes. They are recorded in
`unprofiled/unprofiled.trx` and `unprofiled.log`.

**Fix.** `tests/Allegiance.ClientCore.MonoGame.Tests/NoGcAllocationWindow.cs:23`
reserves a short no-GC window around one warmed measurement. Its process-wide
collector state requires the nonparallel `AllocationMeasurement` collection at
`:10`; other project collections keep their normal parallel execution. Jet uses
it at `Skins/JetSkinScaffoldTests.cs:310` and `Skins/JetFlightSymbologyTests.cs:431`.
The 32 warm-up / 128 measured frames and **zero-byte assertions are unchanged**.
The 16 MiB reservation is collector space, not an allowed allocation count.
Reservation failure or an intervening collection fails the test outright; there
is no retry, skipped sample, subtraction or accepted overhead.

The guard's controls (`NoGcAllocationWindowTests.cs`) check an escaping real array
still costs exactly 40 bytes and an explicit collection invalidates the window.
Both target Jet methods pass **10/10 alone** and every before/after full-project
batch. The additional fixed diagnostic passes **100/100 repetitions per body**.
An initial implementation run rejected a measurement because two related,
non-sealed classes lacked their collection annotations; those annotations were
corrected before the five-run qualification. The rejection was not waived.

**Related tests.** P804 lead (`P804ClientLeadTests.cs:480`) and HUD18 range/TTI
(`Hud18TargetPaneTests.cs:299`) use the same susceptible counter and now use this
guard. P828 radar (`P828RadarLabelDeclutterTests.cs:422`) previously chose the best
of five windows, masking this effect. It now measures **one** unchanged 120-frame
window for each option state. All three pass **10/10 alone**. Their algorithms,
string caches, timing assertion and zero-byte limits remain unchanged.

## 4. Sound-template allocation

**Symptom.** Historical 1,472-byte and similar apparent allocations after warm-up,
zero alone. The diagnostics reproduce 2,120-byte counter changes without objects.

**Cause and evidence.** The same allocation-context retirement above is proven by
three failing sound windows in each of two processes, all without ObjectAllocated
callbacks, and the independent 7,424-byte GC-only reproduction. The sound cursor,
PCM and dictionary are per-test instances, so another test cannot invalidate this
cache. The loop has no await or SynchronizationContext pump. Its real cursor/cache
path is `src/Allegiance.ClientCore.Game/Audio/AsrSound.cs:89` / `:114`; neither
requires a product fix.

**Fix.** `tests/Allegiance.ClientCore.MonoGame.Tests/P902SoundTemplateTests.cs:156`
uses the same guarded window, preserving the 100 warm-up / 1,000 measured iterations
and exact zero-byte equality. After: **10/10 alone**, **0/5 failures** in each full
project and artwork Fast-project batch, and **0/100** in the fixed diagnostic.
No allowance, retry, longer timeout, skip or trait demotion was introduced.

## Gates, unrelated observations and cleanup

- Final solution build: **0 warnings / 0 errors**, 16:09 local.
- `tools/build-tools.sh`: **all 148 projects built**, no build failures. An ignored
  DOTNET wrapper supplies the prescribed build-server flags, an empty offline
  restore feed and NuGetAudit=false; all packages already existed in the lane cache.
- Each of the six allocation methods and both new controls: **10 isolated runs,
  all green**. The final escaping-array control was rebuilt and repeated separately.
  Death and roster each passed 20 isolated runs. Logs/TRX are under `alone-*`,
  `final-positive-control`, `fixed-death` and `fixed-roster`.
- Glossary link validation: **1,027 distinct paths pass**. `git diff --check`: pass.
- The initial fresh-tree --no-restore build had no assets files; an offline restore
  from the existing cache established the build. No package was installed.
- An exploratory unfiltered artwork run was interrupted through its exact execution
  session while running unrelated Live training windows. It is not counted as a
  completed qualification. Five full clean projects and five full artwork Fast
  projects per revision supply the completed before/after rates instead.
- Early relocated diagnostic assemblies exposed two P803 source guards' fixed
  parent-depth assumption. These DirectoryNotFound results are diagnostic-layout
  artefacts, not product regressions; the later diagnostic uses the ordinary bin
  directory depth. An initial snapshot launch missing copied dependencies was
  rejected at discovery and is excluded from all rates.
- Temporary tracing, native profiler and repetition sources remain only in ignored
  evidence. The shipping test assembly contains none of those diagnostic types.
- No source under `src/` changed. No media, service, port, environment setting or
  dependency was added. The supplied brief is absent from the change set.

**Final Fast tier, run last:** **FAIL**, exit 1, 16:16–16:24 local.
Command: `tools/run-tests.sh --tier fast --no-build --results-dir <lane evidence dir>/final-fast-qualified`.
The runner records **14,064 passed / 4 failed / 7 not executed**, 14,075 executions,
393.8 seconds for the Fast tier, with **1,571 missing cases** after the Core host
crashed. All **nine infrastructure checks passed**. The calibrated limit is
**11.632 seconds** (factor 1.163); **six timing offenders**, no quarantine flake.
This is a red, incomplete-coverage gate, not an all-green certification.

Core aborted on an unhandled background `InvalidOperationException: not connected`:
`TcpTransport.Send:600 → Bot.Send:1895 → Bot.ITransportSite.OnReceive:2340 →
TcpTransport.ReadDataLoop:1455`. The latest completed class was
`PlayerMoneyAutoDonateTests`; its three isolated class runs report **11 passed / 11 passed / host aborted
after 7 passes**, the last reproducing the identical stack. The active MU1/MU2
fixture in that isolated crash belongs to `Mutual_donation_clears_target_first_then_donates`.
This identifies a reproducible separate disconnect/receive-send race; no Bot or
transport change is made because unrelated product fixes are explicitly out of scope.
The requested remote death test, both Jet tests, sound, all three related allocation
checks and both guard controls passed in the sweep. The requested roster test was
among the unexecuted Core cases; its **20/20 isolated** qualification stands, but
this sweep cannot certify its execution. No second completed Fast sweep was run.
An earlier started sweep was interrupted to add the unprofiled diagnostic control.

Four failed assertions: help-overlay bounds; launcher buy ack still default;
WebSocket resume assignment expected ship 8096 but null; missile warning did not
arm before its deadline. The timing offenders are SC2 support selection (civs 3/25:
12.683/19.946 s), P796 varied loadouts (civs 35/25: 17.831/11.700 s), P814 gunner
preference (15.523 s), and the failed missile-warning case (27.789 s).
Raw summary: `final-fast-qualified/fast/summary.json`; per-project logs/TRX beside it.

**Unrelated observations before the final Fast sweep.** Each method below was
selected in three fresh isolated processes with the same relevant resource setup;
no source or limit was changed. GPU cases retain the clean-runner environment.

| Method | Observation | Three isolated outcomes |
|---|---|---|
| `HelpOverlayTests.ControlsSections_FitThe720pManualBand` | Section group 9 extends to y=636 past the band end y=630; fails all baseline/fixed batches | fail / fail / fail |
| `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` | Existing allocation-counter family, seen in baseline artwork Fast run 5 | pass / pass / pass |
| `Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact` | Allocation assertion in the unprofiled diagnostic project | pass / pass / pass |
| `Skins.JetStoresCautionTests.Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing` | 8,112 apparent bytes in fixed clean full-project run 2; outside the requested Jet pair | pass / pass / pass |
| `P1017BuyFlowLiveTests.Money_change_while_docked_flips_need_and_buy_Live` | Existing Muted/Worse buy-state observation in artwork Fast run 2 | pass / pass / pass |
| `HdrFoundationTests.Gpu_proof_shows_one_transform_float_targets_resize_and_fallback` | NoSuitableGraphicsDeviceException with null GL extension string under absent EGL manifest | fail / fail / fail |
| `SimPaletteTests.Gpu_proof_applies_the_sim_grade_bloom_and_keeps_the_hud_ungraded` | Same clean-runner graphics-device failure | fail / fail / fail |
| Both P803 Classic/Modern source guards | Only the early relocated diagnostic directory broke their parent-depth path lookup | pass / pass / pass each in the normal layout |

The other allocation assertions may share the proven counter weakness, but their
windows were not instrumented and they remain outside this fix's scope. The GPU
and layout failures are reproducible failures, not relabeled as timing flakes.
Raw isolation results: `unrelated-*` and `unrelated-extra-*`; ledger:
`docs/TEST_FLAKES.md`. The final sweep's non-task failures and timing offenders were then run in **three
fresh isolated processes per method**, with all theory rows included:

| Method | Three isolated counts (pass/fail) | Slowest case, runs 1 / 2 / 3 (s) |
|---|---|---|
| Help-overlay bounds | 0/1, 0/1, 0/1 | 0.034 / 0.036 / 0.024 |
| Launcher buy order | 1/0, 1/0, 1/0 | 2.336 / 2.300 / 2.329 |
| Missile-track warning | 1/0, 1/0, 1/0 | 2.273 / 2.332 / 2.309 |
| WebSocket resume authentication | 1/0, 1/0, 1/0 | 4.078 / 4.072 / 4.061 |
| SC2 support selection (both civs) | 2/0, 2/0, 2/0 | 7.518 / 5.498 / 4.207 |
| P796 varied loadouts (all three civs) | 3/0, 3/0, 3/0 | 5.499 / 7.519 / 7.459 |
| P814 gunner preference | 1/0, 1/0, 1/0 | 4.438 / 5.115 / 5.383 |

Every passing isolated case is below the original 11.632-second calibrated limit.
The crash-adjacent class is **pass / pass / aborted**, with the same unhandled
scripted-pose send after disconnect in the third run. The exact active method
`PlayerMoneyAutoDonateTests.Mutual_donation_clears_target_first_then_donates`
passes **three further isolated runs**; that does not negate the reproduced class
abort or establish which teardown ordering is responsible. Raw evidence: `final-isolation-*`,
`final-isolation-summary.json`, and `final-isolation.log`. The red gate, missing
coverage, reproducible layout failure and reproducible background crash remain
open in the ledger; passing isolated samples do not waive them.
No unrelated failure is fixed or waived by this task. All started execution
sessions have ended, including exploratory runs stopped through their specific
owned sessions; no background build server or diagnostic process is left running.

Native intent / Modern approach: native death/respawn and connected-stint accounting
remain unchanged; the interval assertion follows `Igc/igc.h:5602-5613`. Modern test
harnesses synchronize observation at simulation/receive boundaries and protect
strict allocation measurements from GC bookkeeping. There is no new native rule,
so no new parity CSV row or HUD visual capture is required.
