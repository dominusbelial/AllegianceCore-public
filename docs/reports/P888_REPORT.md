# P888 — game-over observation under pool transport

Base: `8e6c1787`. Changes remain in the working tree.

The reported failure is a **test artifact**, not a game-flow ordering defect.
No production source changed. Both transport modes deliver the result and roster
before rotation; the legitimate next match's **MISSION_STAGE(37) Starting** clears
the result. The fixture can advance its entire two-second intermission before a
pool worker/client has drained the join burst and the test observes the hold.

## Frame-order evidence

[Buffered server publication and client receipt transcripts](P888_assets/frame-order.txt)
include both peers in a failing pool run and a passing thread-per-peer run.
The temporary instrumentation used the existing `TestPublicationCapture` hook and
`ConnectionSession.Recording`; it was removed after capture. Original recording
files, logs and instrumented test source remain in `.scratch/p888/` in this worktree.
A first Console-per-message pool capture passed; switching to buffered capture
reproduced the failure without synthetic load. Host load was approximately 2–4
on 24 CPUs during diagnosis.

For each peer, both modes preserve this sequence:

| Packet fmids | Meaning | Published key / lane |
| --- | --- | --- |
| 62,63 | GAME_OVER plus player roster | 0 / ordered, guaranteed |
| 37 (3) | Over | 0 / ordered, guaranteed |
| 38,95 | NotStarted mission definition plus JOINED_MISSION | 0 / ordered, guaranteed |
| 38 | Fresh mission definition | 0 / ordered, guaranteed |
| 37 (0) | NotStarted | 0 / ordered, guaranteed |
| 35,14 (twice) | Clock and intermission deadline | 0 / ordered, guaranteed |
| 37 (1) | Next match Starting | 0 / ordered, guaranteed |
| 35,14 | Briefing deadline | 0 / ordered, guaranteed |
| 37 (2) | Next match Started | 0 / ordered, guaranteed |

A second [complete capture under explicit server GC](P888_assets/frame-order-server-gc.txt)
records **35/14 on the server as well**, alongside the stage, result, roster and
join packets. Every game-flow packet has key=0, guaranteed=true, ordered lane.
The scratch-only reproducer is `.scratch/p888/frame-proof/Program.cs` (project
beside it); it uses the existing capture/recording hooks and unchanged production
source. Results under the deployment baseline:

| Mode | Original accelerated fixture | Real start + paced observation |
| --- | --- | --- |
| pool | Result wait fails; order intact | Result wait passes; order intact |
| thread-per-peer | Passes; order intact | Passes; order intact |

The earlier capture selected 37/38/62/63/95 on the server; its 35/14 receipt packets
are also retained. The new full capture removes the need to infer their send lane.

The failing pool recording has A's NotStarted at 110.961 ms, deadline packets at
110.970/110.989 ms, and Starting at 119.422 ms. B's corresponding times are
105.076, 107.079/107.942, and 115.818 ms. Polled snapshots retain result=true and
rescues=1,1 while the countdown has not arrived; their next observed stage is
Starting with result=false. The thread-per-peer capture has a longer
receipt gap (A's deadline 63.376 ms, Starting 118.569 ms) and passes.
The final timeout snapshot thus describes the next match, not a lost result.

## Root cause and decision

Relevant port paths (line numbers checked in the final tree):

- `tests/Allegiance.ClientCore.Tests/P660NextMatchLiveTests.cs:42-49` (`Pump`) advances 50 ms of simulation then normally sleeps
  only 1 ms. Two simulation seconds therefore consume only 40 sleeps, plus tick
  work, instead of two wall seconds.
- The original two-match test (`P660NextMatchLiveTests.cs:194` before this change) directly assigned Started, bypassing the real
  ready/countdown transition and any Started notification to clients.
- `src/Allegiance.Core/Transport/TcpTransport.cs:2308` can idle-wait for 50 ms; batching and
  receiver scheduling can consume this accelerated observation window. FIFO
  within the ordered lane still holds (`TcpTransport.cs:1824-1847`, `PromoteHeadLocked`).
- `src/Allegiance.ClientCore/State/ClientWorld.cs:5220-5243` sets the result,
  clears the old roster and accumulates the following player chunks.
  `ClientWorld.cs:5055-5058` clears the result at Starting, exactly as the capture
  shows. `ClientWorld.cs:1983-1985` (`IsIntermission`) requires both NotStarted
  and a received countdown deadline (`ClientWorld.cs:5182-5187`).

A controlled intermediate change using the real start transition **alone still
failed** in pool mode. Correcting the observation's tick pacing is necessary;
blaming the direct stage assignment alone would be incorrect. A real 20 Hz
match has two wall seconds to expose this hold; this test compressed it to tens
of milliseconds. The corrected test uses actual ready packets, waits for the
server **and both clients** to report Started, then observes each intermission
at 20 Hz. Later countdown checks keep their accelerated pump.

Native intent, rechecked against the read-only checkout:

- `FedSrv/fsmission.cpp:1750-1762`: SetStage queues MISSION_STAGE to the mission
  with FM_GUARANTEED and FM_FLUSH.
- `FedSrv/fsmission.cpp:2233-2239,2263-2266`: StartGame transitions through the
  start stages and sends ENTER_GAME guaranteed.
- `FedSrv/fsmission.cpp:3063-3085,3138-3142,3435-3438`: queue the game-over summary
  and player roster, then flush them guaranteed before reset.
- `FedSrv/fsmission.cpp:3490,3539-3546`: announce the reset stage and restart time.
- `_Utility/Messages.cpp:556-563`: guaranteed sends use DPNSEND_GUARANTEED,
  PRIORITY_HIGH and COALESCE; NONSEQUENTIAL is for normal traffic.
- `WinTrek/gameoverscreen.cpp:638-645`: Starting leaves the game-over screen.

## Change and regression coverage

`P660NextMatchLiveTests` now follows the real initial start path. Its two-match
result observation uses a 50 ms tick sleep with a bounded 100-tick wait, keeping
the original two-second intermission and every result/roster/reseat assertion.

`TcpTransportPriorityLaneTests` adds a deterministic socket proof: hold the
sender lock, interleave both best-effort and guaranteed coalescing pose traffic
with two matches' stage/deadline/result/player/rotation frames, then release it.
The actual received frames must exactly match every ordered payload in sequence,
followed by the newest pose on each stream key. Both writer modes run with
reliableOneShot OFF and ON. It passes against unchanged production code, as
expected for the test-artifact branch of this task.

`SIM-0528` records this proof and the glossary indexes it. No service, environment
variable, media or HUD change was introduced. No commits or pushes were made;
no remote machine was contacted.

## Repeated tests and gates

Final repetitions use **`DOTNET_gcServer=1`**, verified from the actual server
boot lines in each P660 TRX, with explicit `ALLEGIANCE_TRANSPORT_MODE=pool` or
`thread-per-peer`. Snapshot, encodeOnce, parallelBuild, native ship updates and
native GotoPlan use the unchanged P887 deployment defaults. The P660 filter is
`FullyQualifiedName~P660`, with `ALLEGIANCE_LIVE_P660_SHOWCASE=1`: **12 cases,
no skips**, including the 30-bot showcase rotation and P660 finale regression.
The new transport test selects the corresponding writer-mode class and runs
both reliableOneShot variants. [Final per-run results](P888_assets/repeats.csv):

| Repeat | Pool P660 | Pool order | Thread-per-peer P660 | Thread-per-peer order |
| --- | --- | --- | --- | --- |
| 1 | 11/12 | 2/2 | 12/12 | 2/2 |
| 2 | 12/12 | 2/2 | 12/12 | 2/2 |
| 3 | 12/12 | 2/2 | 12/12 | 2/2 |
| 4 | 11/12 | 2/2 | 12/12 | 2/2 |
| 5 | 12/12 | 2/2 | 12/12 | 2/2 |
| 6 | 12/12 | 2/2 | 12/12 | 2/2 |
| 7 | 12/12 | 2/2 | 12/12 | 2/2 |
| 8 | 12/12 | 2/2 | 12/12 | 2/2 |
| 9 | 12/12 | 2/2 | 12/12 | 2/2 |
| 10 | 12/12 | 2/2 | 12/12 | 2/2 |

The corrected **two-match test passes 10/10 in each mode**; the new ordering
cases pass **20/20 in each mode**. All 20 opt-in P660 showcase rotations pass.
Full P660 case totals are **pool 118/120** and **thread-per-peer 120/120**.
Both remaining failures are the unchanged, previously-ledgered
`Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` fixture:
its observed remaining briefing time falls outside the existing 14.94–15.06 s
bound. The method passed three isolated selections covering **all four parameter
cases** each time (12/12). These failed suite results remain in the table; they
are not counted as clean suite runs.

Earlier [diagnostic repetitions](P888_assets/diagnostic-repeats.csv) used the
embedded test host's workstation-GC default: the corrected scenario passed 20/20,
ordering passed 40/40, and only three occurrences of that same countdown fixture
failed. They are retained separately and do not replace the final server-GC gate.

The single Fast run retained **11,719 passed / 14 failed / 3 optional skips** in
531.5 s, with 9/9 infrastructure checks and six timing violations. New ordering
and corrected two-match checks pass in that run. Eight failures were environment
or setup effects: five path-resolver fixtures inherited the worktree's `local.env`
through TMPDIR, one profile fixture tried to write read-only user preferences,
and my global pool override defeated two explicit legacy-mode config fixtures.
All selected methods pass 3/3 with only that environment corrected.

The remaining failures were rechecked three times: boarded gunner, full session
and reconnect score-roster pass 3/3; missile lock is fail/pass/fail; docking is
fail/pass/pass; the existing wingman baseline failure remains fail/fail/fail.
These mixed/persistent results are retained. The five passing timing-only cases
also passed three isolated selections; warp-exit timing remains near the limit
(9.855/10.028/9.982 s), with one isolation still over ten seconds. Details and
classifications are in [TEST_FLAKES](../TEST_FLAKES.md#p888-verification-observations-2026-10-29),
the [isolation CSV](P888_assets/isolation.csv), and the
[timing CSV](P888_assets/timing-isolation.csv). The full Fast run was not repeated.

The [five never-parks runs](P888_assets/showcase.csv) all pass. Every run reached
24/30 bots at its objective and observed gunfire/hits; the maximum period below
1 m/s while holding a target was 2.015 s, under the five-second limit.

| Gate | Result |
| --- | --- |
| Full solution build | PASS, 0 warnings / 0 errors (final build 2.28 s) |
| `tools/build-tools.sh` | PASS, all 94 projects, 0 warnings / 0 errors |
| New and P660 repeated checks | As above; corrected scenario and new tests all pass |
| One Fast-tier run | FAIL: 11,736 executions, 14 failures, 3 optional skips, 6 timing violations; new checks all pass |
| Showcase never-parks 5× | PASS 5/5, 3m27s–6m43s |
| Glossary links | PASS, 344 distinct paths |
| Parity scorecard | PASS |
| `git diff --check` | PASS |
| Server Native AOT publish | Not applicable: no Server/Core production source changed |

The [validation summary](P888_assets/validation.json) retains machine-readable
receipts. Raw TRX/logs and the capture reproducer remain under `.scratch/p888/`,
including `repeats-server-gc/`, `repeats/`, `fast/`, `showcase/`, `isolation/`,
`environment-isolation/`, `timing-isolation/` and `frame-proof*`.
NuGet HTTP cache and temporary files were redirected into `.scratch/p888/`
to stay inside the writable worktree; package vulnerability checks were retained.

## Native-intent footer

This is **test fidelity and regression coverage**, with no change to native
behavior or production scheduling. Final repetitions explicitly use server GC.
Pool remains the default; immutable snapshot, encodeOnce, parallelBuild,
native ship updates, native GotoPlan and server GC
remain the baseline. Thread-per-peer is explicitly selected only as the requested
comparison. The hot pose stream has **zero code changes and zero added per-frame
operations**; no throughput or rig performance improvement is claimed. AOT publish
is not required because neither Server nor Core production source changed.
