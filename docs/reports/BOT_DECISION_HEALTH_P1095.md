# P1095 skirmish bot decision health

A responsive worker can fail every decision while it continues to serve commands. Skirmish now reports the current worker and bot failure streaks on its existing two-second heartbeat. The host logs one warning when it observes three consecutive failures. Play continues, and a successful decision clears its streak.

## Brief items

Elapsed times are rounded wall times. The gate and proof runs overlapped.

| Item | Status | Evidence | Elapsed |
|---|---|---|---|
| 1. Confirm the blind spot | done | At base `28debb3c`, the host reported bots alive after 52 worker exceptions and again after 8 exceptions in one pilot | 3 min |
| 2. Count and log failures | done | Both existing catch boundaries exercised. Two simultaneous failing pilots advance the worker streak once. Eight consecutive passes produced one diagnostic | 4 min |
| 3. Expose worker health | done | The two-worker host received the worst streak, individual worker reports and the failing pilot. The second worker stayed at zero. Host warning rearmed after recovery | 5 min |
| 4. Preserve behaviour | done | Byte comparison of a controlled heartbeat, two ordered wire packets, pause/resume and clean process exit. Details below | 7 min |
| 5. Fast regression tests | done | Three Fast cases passed individually three times. Ten deliberate breaks failed their intended assertions | 14 min including scratch mutation builds |
| 6. Report and flake notes | done | 157 tools built. Full Fast sweep and three isolated repeats for every unrelated failure and timing overrun are recorded below | 25 min total including overlapping checks |

## Failure accounting

`BotManager.RunDecisionPass` retains the worker's pause lock, parallel preparation, sequential pilot order and two exception boundaries. An exception that escapes preparation or other worker-wide work increments the worker streak. An exception caught in one pilot's tick increments that pilot's streak and allows the next pilot to run. A pass with any number of failures increments the worker streak exactly once. Streaks saturate at the maximum signed 32-bit integer.

A completed worker pass clears its worker streak. A completed due pilot decision clears only that pilot's streak. Paused workers preserve both. A skipped pilot decision at a lower level-of-detail cadence can leave a nonzero pilot streak in a worker whose latest pass completed successfully. Pilot counters live with the roster entries, so removing a pilot also removes it from subsequent health reports.

`get_perf_stats` appends one field. For a healthy worker it is `"decisionFailures":{"consecutiveWorkerPasses":0,"bots":[]}`. For a failing pilot the array includes its name and `consecutiveBotPasses`. The supervisor takes the **maximum** worker streak and concatenates the affected pilot rows. The existing `workers[].report` preserves attribution. The host reads those stats on the original heartbeat and checks the maximum across the worker and pilot counts. It warns once per observed episode at or above three. A subsequent sample below three rearms the warning. Older health packets without the field remain readable.

The worker writes `[bot-decision]` on the first exception and at most once per 30 seconds of monotonic wall time thereafter. The line carries worker number, worker streak, exception type and single-line message, plus pilot name and pilot streak when available. The standalone perpetual-battle driver uses this same decision loop and stderr. In skirmish, the supervisor routes each worker's stderr to its existing worker log. [Operator log reference](../PUBLIC_PERPETUAL_BATTLE.md)

This is port orchestration. It introduces no native game rule. The command encoders, decision policy, scheduling deadlines, skirmish driver framing, rendering and server are outside the diff.

## Baseline and loopback proof

A scratch build of the dispatch worker injected `InvalidOperationException` at the start of an unpaused worker pass and, in a separate window, `FormatException` in the first due pilot's tick. The injections were controlled by files in the private match directory. Each throw wrote a witness before it reached the original catch. Worker two ran ordinary decisions throughout. The host, supervisor and other worker code used their production paths. The fixture started a Small, unlocked match with two workers and nine pilot bots, and exercised focus, both pause transitions and stop. The shipping binaries have no fault switch.

| Observation | Dispatch | P1095 |
|---|---|---|
| Whole-worker failures | 52 witnessed throws. Stats still returned, host alive, no count | First two-worker run sampled worker streaks 40 and 0. Warning emitted at the next available health sample |
| Individual pilot failures | 8 witnessed throws. Stats still returned, host alive, no count | Same run sampled pilot streak 5 with worker streak 0 between due decisions. The host warned on the pilot count |
| Pause, resume and exit | Throw count stayed fixed while paused. Three owned processes exited | Both commands acknowledged, healthy status recovered, driver and both workers exited |

A longer run held the whole-worker fault for 32 seconds. It witnessed **644 failed passes** and exactly **two worker log lines**, at streaks 1 and 601, both carrying `InvalidOperationException: injected worker failure`. The host sampled worker counts 641 and 0. In the following pilot-only window it sampled that pilot at 7 while both worker counts were 0. Recovery was checked by waiting for a new heartbeat with both worker count and failed-pilot list clear. An initial fixed 2.3-second wait sometimes read the preceding heartbeat under load.

The standalone perpetual driver also ran against a local server with two backfill seats per side. Sixteen injected worker-pass exceptions produced one matching `[bot-decision]` line on stderr, zero bytes on stdout, and exit code 0 after its normal termination signal.

For the byte comparison, both binaries received the same clock, stall samples, empty roster, cadence and two worker identities using the deterministic P657b perf fixture. The comparison serialized the normal worker reports, supervisor merge and the driver's existing health envelope with fixed PIDs. Dispatch emitted **6,968 bytes**. P1095 emitted **7,145 bytes**. Removing exactly three occurrences of the new healthy field (two workers and the aggregate) restored every original byte, including property order. SHA-256 of both legacy payloads was `dc33f82f1f189598e124c1d64abb16df6956a2caf87b81de72979508683ab377`. The controlled inputs hold live clocks and process IDs fixed for this comparison.

## Regression and deliberate breaks

The Fast fixture injects read and send exceptions on ordinary bot transports. It feeds a real SET_CLUSTER and queued warp orders to three pilots, runs the normal decision pass, and reads `get_perf_stats`. While the first two pilots fail, the third accepts both queued orders. The test compares both complete FedPackets with the expected ORDER_CHANGE bytes in arrival order. Recovery delivers the first pilot's pending order. A separate test starts the real deadline thread, checks pause/focus/resume while decisions keep failing, then checks thread termination and peer closure. [Fast tests](../../tests/Allegiance.ClientCore.Tests/P1095DecisionHealthTests.cs)

Each mutation was built separately in scratch and had to fail a single filtered test.

| Deliberate break | Observed failure |
|---|---|
| Send an ORDER_CHANGE to the next warp ID | First healthy peer packet contained target 13 where the received order required 12 |
| Leave the worker count at zero on an exception | First failure expected 1, received 0 |
| Retain a streak of one after the first successful pass | Recovery expected 0, received 1 |
| Break out of the pilot loop after the first exception | Second failing pilot was absent |
| Skip the successful pilot's reset | Two pilots remained in the failed list after one recovered |
| Remove rate limiting | Eight diagnostics instead of one |
| Sum worker streaks | First merged sample was 2 instead of 1 |
| Delay the host warning to four | No host warning at three |
| Discard the focus sector while failures continue | Expected sector 17, received the cleared value |
| Skip plain peer disposal | Synthetic transport remained open after worker disposal |

## Gates and flake notes

Both final solution builds, including browser projects, passed with **0 warnings and 0 errors**. The glossary check validated **1,187 paths**, and the parity scorecard completed. The three new cases passed as individual filters in three fresh test hosts apiece. The full P1095 class also passed 3/3 in each of three runs. Five affected existing cases passed individually three times each: stall output, supervisor worker envelopes, idle-count aggregation, paused supervisor coordination and actual worker-death reporting. The cadence and bot-efficiency classes passed **20/20** in each of three additional runs. The existing complete battle, pause, rotation and stop test passed three isolated runs (3 min 9 s, 3 min 3 s and 3 min 4 s). It verified attacks and hits, a 40-second pause with fixed ship poses, resumed simulation, the next mission, released processes and a reusable listener.

Run the new cases against the Debug build with the following command.

```sh
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter FullyQualifiedName~P1095DecisionHealthTests
```

All **157 standalone tool projects** built with zero warnings and zero errors using `tools/build-tools.sh`. The initial offline restore lacked three pinned KNI shader packages. Copying those exact archives from a local cache into the scratch feed allowed the complete script to pass. The NuGet package and HTTP caches, XDG configuration, temporary test roots and proof binaries were scratch-local.

### Full Fast sweep

`tools/run-tests.sh --tier fast --parallel 2` ran all **16,202** discovered Fast cases with local artwork. **16,192 passed, 7 skipped and 3 failed**. All nine infrastructure checks passed. Nine executions exceeded the calibrated **16.474 s** limit, including one of the three assertion failures. All three P1095 cases passed in the sweep. The runner classified both P928 crowded-sector goldens as Live.

| Unrelated assertion failure | Three isolated reruns | Finding |
|---|---|---|
| P1091 Classic, 1920×1080, default scale | Failed 3/3 | Same RGBA hash on dispatch and P1095. Both proof builds reported zero layout errors |
| P1091 Modern, 1920×1080, default scale | Failed 3/3 | Same RGBA hash on dispatch and P1095. Both proof builds reported zero layout errors |
| `AsteroidCombatLiveTests.Lethal_hit_kills_asteroid` | Passed 3/3 | Sweep missed the lethal cross-side hit. This matches the existing asteroid fixture entry |

The two repeatable software-GL pixel mismatches are newly recorded with their expected and observed hashes. The existing ledger also covers the timing-test families below. All nine had three fresh, passing runs below this sweep's limit. [Flake ledger](../TEST_FLAKES.md#p1095-fast-sweep-2026-11-27)

| Timing case | Sweep seconds | Three isolated runs, seconds |
|---|---:|---|
| P800 full hull render | 22.731 | 5.505, 4.645, 4.675 |
| P832b commander trace, other goals | 20.466 | 8.350, 8.188, 7.730 |
| SC2 support selection, civ 25 | 23.582 | 4.968, 4.996, 4.690 |
| P796 legal loadouts, civ 3 | 24.323 | 9.734, 9.452, 9.571 |
| P796 legal loadouts, civ 35 | 23.497 | 5.416, 5.409, 5.531 |
| P814 turret-carrier preference | 22.131 | 5.458, 5.257, 5.112 |
| P796 legal loadouts, civ 25 | 19.442 | 8.230, 7.703, 8.102 |
| SC2 support selection, civ 3 | 19.619 | 6.554, 6.474, 6.421 |
| Lethal asteroid hit | 19.937 | 0.648, 0.613, 0.660 |
