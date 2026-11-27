# P1093 — Skirmish shutdown and run files

Base `138e61db`. Work began at 15:27 UTC. Durations in the checklist are elapsed work and verification time. Some checks ran concurrently.

## Brief items

| Item | Status | Evidence | Time |
| --- | --- | --- | --- |
| 1. Reproduce S2 and S4 | done | All four baseline input cases and the ten-run file census passed. See measurements below. | 5 min |
| 2. Stop ownership | done | Headless Ctrl+F10 integration exercised the actual Game1 handler, driver, ticker, delayed restart and port reuse. | 15 min including the additional partial-start ownership proof |
| 3. Run-file cleanup | done | Four successes left zero bytes. Six partial starts left four capped logs and no running unannounced worker. | 14 min including the unannounced-worker control and fix |
| 4. Exactness | done | 11 existing live cases passed three times on the final source, including real bot play, failure reporting, LAN shutdown and local Internet-host deregistration. | 33 min of compatibility runs, including preliminary passes |
| 5. Regression tests | done | 9 cases × 3 isolated invocations passed. Every new case failed on its deliberate break. The ten breaks below produced eleven failing invocations (the drain break was run in both projects). | 31 min including fixture development and final mutation runs |
| 6. Report | done | Timelines, disk census, ownership, deliberate breaks, build gates and Fast failure qualifications are recorded below. | 40 min elapsed from opening the report to final verification |

## Blocking timeline

The baseline used the original Update and Dispose bodies. The harness supplied an isolated working directory and a stand-in executable through the existing driver argument. Each input measurement spans the desktop Update call to `UpdateSkirmish(KeyboardState)` with Ctrl+F10 pressed. It excludes graphics, fixture startup and the rest of the frame. The final assertion allows **100 ms** of wall time, including scheduling and cold JIT.

| Condition | Original exit frame | Final exit frame, three isolated runs | Final owner release from keypress |
| --- | ---: | ---: | ---: |
| Driver ignores EOF for 30 s | 5,016.35 ms | 2.29–2.38 ms | 5,026–5,031 ms |
| Destination rejects log creation | 5,014.86 ms | 2.35–3.18 ms | 18–48 ms |
| Paused, resume request outstanding | 48.28 ms | 2.25–2.51 ms | 522–523 ms |
| Delayed measured tick | 2,049.95 ms | 2.56–4.21 ms | 5,814–6,016 ms |

Originally the child blocked writing 2 MiB to its failed log destination. The final test requires the child to acknowledge its completed write before pressing Ctrl+F10. The final paused case also holds a private-pipe worker alive after driver exit. The baseline tick was held for two seconds. The final test holds it across a 5.2-second wait, verifies that another start is pending and that the old listener still owns the port, then releases the ticker. The next game uses the real bot driver on that port.

The original implementation allowed another five seconds after a kill, five seconds for readers, then an unlimited ticker join. These are source-level waits. The table gives the actual measurements.

Before the change, four successful starts/stops retained **4 directories / 25,944 bytes**. Six more failures before driver readiness brought the total to **10 directories / 12,647,784 bytes**. The census includes each fixture's small mode file.

## Ownership

`Game1.Skirmish.cs` detaches the old session, host, pending startup, cancellation source and pause/seat work, and returns to the existing intro page. It installs a freshly wired client session before the old one shuts down. Ctrl+F10 is checked before seat handling and session locks. Local, LAN, benchmark, calibration and automatic starts all await the retirement task before boot. Exit from the application joins that task.

`SkirmishHost.StopAsync` latches one completion under a lock. Synchronous Dispose joins that same completion. The background owner cancels commands, preserves the LAN departure notification and drain, closes stdin under the writer semaphore, and gives the driver its existing five-second grace period. If necessary it kills the owned tree. It waits for process exit, the owned process group or job, the input closer, both output readers, the workers announced in the private ready message and the ticker. It then disposes the server and finishes the files. A timeout initiates process termination. Only actual completion releases the next start.

## Run-file policy

`SkirmishRunFiles.cs` copies raw stderr in 8 KiB chunks. It saves the first **1,048,576 bytes** to `driver.log`, then drains to EOF. Open, write, flush and close errors at the destination also leave the reader consuming output. No newline is required. A failed stdout protocol handler preserves its existing failure message and drains the remaining pipe.

A successful stop removes the owned directory recursively after readers finish. A failed start or running host retains `driver.log` at the original diagnostic path. If the driver produced no log, the host records the bounded failure description. It removes the config and worker files, marks the directory closed, and keeps the newest **four** closed failures by marker write time (directory name breaks ties). Pruning considers only matching run GUIDs with a failure marker. Live runs and older, unmarked directories are outside the policy. The retained file payload is at most **4 MiB**, excluding filesystem metadata. Storage faults that also prevent cleanup are reported to the diagnostic stream.

## Tests and compatibility

- [P1093SkirmishStopTests](../../tests/Allegiance.ClientCore.MonoGame.Tests/P1093SkirmishStopTests.cs) uses the actual input handler, embedded server and a controllable driver in the existing `skirmish-proof` executable. It exercises repeated stop, a worker tree, a bad log path, paused command cancellation, a held tick beyond the grace period, and a real second game on the first port. It also checks the exact existing failure string and inventories every file after repeated success and partial startup failure. These child-process tests are Live.
- [P1093SkirmishRunFilesTests](../../tests/Allegiance.ClientCore.Tests/P1093SkirmishRunFilesTests.cs) exercises a read-only open and disk-full write/flush/close failures through EOF. It verifies retention order, early-start diagnostics, and active/unrelated directory preservation in Fast.
- All 30 P889 pool-sizing cases and all nine P729 benchmark/replay cases passed in three fresh class runs apiece.
- Eight existing Live methods ran separately three times on the final source, **33/33 case executions**. P727 covered combat, pause/resume, research, rotation, explicit worker death, and owner death at two core budgets. P901 covered three partial-listener failures and four independent guests receiving LAN shutdown. SP5 covered seat swaps across match rotation. P936 covered publication, join and deregistration against its loopback lobby.
- The implementation reuses the existing server tick, bot commands, shared-machine sizing and failure messages. P1093 ports no native gameplay rule.

## Process-exit checks

The slow driver owns a real child and ignores EOF. The paused driver's worker has private pipes and stays alive for another half second after its supervisor exits. The partial-start fixture creates a worker in the supervisor's real process group before sending any ready message. It writes a PID receipt outside the run directory so the test can check that worker after startup throws.

The ready list alone missed the partial-start worker in a failing control run. `ProcessLifetime.cs` also checks the private Unix process group, or terminates the owned Windows job and waits for its active-process count to reach zero. On Linux a reported worker is identified by PID and kernel start time. A zombie has finished executing even when the container's init has yet to reap it. Proofs ran on Linux. The Windows job path received compile coverage.

## Deliberate breaks

Each mutation used a scratch copy of the affected source and a build configuration isolated from Debug and Release. The test runner required a failed test assertion as evidence.

| Deliberate break | Regression observed |
| --- | --- |
| Wait for retirement inside the exit handler | Slow-driver Update took over five seconds and exceeded the 100 ms limit. |
| Start without awaiting the prior stop | The second LAN boot attempted to bind the still-owned port. |
| Omit the reported worker join | A paused-match worker with independent pipes survived the reported completion. |
| End the reader on destination open, write or flush failure | The Fast cases read 0, 8,192 and 8,192 bytes of 2,097,152. The real child blocked before writing its drained receipt. |
| Keep successful directories | The four-success census found old runs. |
| Disable pruning | The Fast retention test found older closed failures alongside the four permitted entries. |
| Remove the byte cap | The failed-start log exceeded 1,048,576 bytes. |
| Trust only the ready list during a partial startup | The PID receipt identified a surviving worker that the driver had not yet announced. |
| Remove the duplicate-stop guard | A repeated exit replaced the original completion task. |
| Drop the location from the driver-exit message | The failed-start assertion rejected the shortened message. |

## Gate and flake notes

The final Fast run finished in **671.5 s** with **16,161 passed, 5 skipped and 2 failed**. Infrastructure and discovery passed. The two failed cases were the docked-buy color (`P1017BuyFlowLiveTests`, expected Muted, received Worse) and the single-round damage observation (`P467E2eGunDamageDevLiveTests`). Both passed three consecutive isolated reruns. The Fast timing guard also flagged five passing cases. Both assertion signatures and all timing families already have entries in the [flake ledger](../TEST_FLAKES.md).

- The first Fast sweep used a literal in-checkout temp path. Five existing `PlayerBundlePathsTests` cases discovered the checkout's ancestor `local.env` from their synthetic outside-repository installations. This matches the fixture obstruction recorded in `docs/TEST_FLAKES.md`. All nine class cases passed in each of three isolated runs with the same scratch directory exposed through a process working-directory alias, removing the repository ancestors from its pathname.
- The first sweep also hit the existing `StationCombatLiveTests.Shield_regenerates_over_real_ticks` missed-shot flake. Its initial shield damage assertion failed. The unchanged method passed three consecutive isolated runs.
- P928's supplemental golden is a Live test. This task leaves its fixture and expected values alone.

To reproduce the new cases, build the Debug solution, make the usual local artwork corpus available, and run each of these project filters. The Live project builds its stand-in executable through a project reference.

```sh
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter FullyQualifiedName~P1093SkirmishRunFilesTests
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --filter FullyQualifiedName~P1093SkirmishStopTests --logger 'console;verbosity=detailed'
```

Both Debug and Release solution builds finished with **0 warnings / 0 errors**. All **156** tool projects built. The glossary check validated **1,177** paths. Dependencies came from the local package cache with scratch-local NuGet and XDG directories.

The first Fast sweep ran 16,168 cases. Ten executions exceeded its 10.694 s calibrated ceiling. Each matching case was rerun three times in isolation. These are previously recorded entries in `docs/TEST_FLAKES.md`.

| Timing case | Sweep time | Three isolated runs |
| --- | ---: | ---: |
| P800 all-hull GPU proof | 11.89 s | 4.15–4.95 s |
| SC2 nanite selection, civ 25 | 11.49 s | 3.02–3.18 s |
| P796 varied loadouts, civ 25 | 11.46 s | 4.89–4.95 s |
| P814 turret carrier preference | 11.68 s | 3.16–3.21 s |
| Travelling NPC dodge | 11.71 s | 7.58–7.72 s |
| P1000 immutable civ profile, civ 3 | 16.19 s | Under 2 s |
| P750 showcase rock models | 11.40 s | 1.44–1.51 s |
| P999 reachable profile, civ 5 | 12.96 s | 2.87–3.00 s |
| P942 research ordering, civ 201 | 12.46 s | 0.77–0.83 s |
| Station shield regeneration | 19.64 s | Passed three times, 2 s each |

The final sweep's 10.000 s ceiling flagged SC2 civ 25 (10.39 s), P750 (11.09 s), P1000 civ 3 (11.57 s), P999 civ 5 (10.40 s) and P777 warp-exit wobble (10.37 s). Each exact case passed three further standalone runs, respectively 2.90–2.93 s, 1.42–1.52 s, 1.70–1.74 s, 2.82–3.03 s and 8.51–9.39 s. All fifteen isolated durations were below the ceiling. Both complete sweeps are recorded as red. The outside-failure repetitions and ledger comparison required by the light gate are complete.
