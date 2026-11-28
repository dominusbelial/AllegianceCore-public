# P1100 · Seated render snapshot coherence

Base `28debb3c`. Times below are approximate wall-clock intervals for each item. Measurements, builds and qualification overlapped.

## Brief items

| Item | Status | Evidence | Time |
| --- | --- | --- | --- |
| 1. Baseline gate measurements and transition reproduction | done | Three completed local 12-second Small Showcase runs, packet callback wait and hold instrumented at OwnerSite. A receiver forced after the old generation check advanced generation 2 to 3 before the lighting read at replay frame 6561 (54 poses). | 8 min |
| 2. Capture and ownership | done | The production frame path rejects each forced transition. Both skins paint a prepared frame while the receiver owns the gate. | ~15 min |
| 3. Frame, HUD and command equivalence | done | Six paired busy backbuffers, same radar and world hashes, full shared HUD model and ordered packet capture in both skins. | ~5 min |
| 4. Regression and mutation checks | done | Nine Fast cases. Five Update/Draw transitions, two independently rendered skins, endgame, receive exclusion and capture bound. Ten deliberate breaks detected, including a docked turret marker read after capture. | ~9 min |
| 5. Changed gate measurements | done | Three 12-second base windows and three changed windows under Xvfb. Section and outer packet-callback timings below. | ~15 min |
| 6. Report and flake qualification | done | Debug, Release and all 157 tools built. Full Fast completed with four out-of-scope failures, each isolated three times. Counts and ledger evidence below. | ~51 min including overlapping sweeps |

## Measurement boundary

The rendered benchmark seats an ordinary benchmark pilot in seed 729's Small Showcase. It waits for both sides to be visible before each window. Instrumented builds accumulate stopwatch ticks at monitor entry, acquisition and release. Counts exclude the independent driver process. Packet delay starts on the transport callback at the outer owner lock. The inner decode lock is reentrant. Timing only that inner lock would miss receive contention. The initial measurement below used SDL offscreen with software DesktopGL at 960 × 540. The before/after comparisons used X11 under Xvfb at the same resolution.

| Baseline | Frames | Pose hold µs/frame | Radar hold µs/frame | Packet callbacks | Wait µs/packet | Callback hold µs/packet |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 4385 | 59.21 | 33.28 | 1747 | 1.24 | 11.00 |
| 2 | 2523 | 148.45 | 60.79 | 1651 | 2.05 | 14.47 |
| 3 | 2960 | 80.24 | 49.90 | 1499 | 1.21 | 13.06 |

## Receive-gate measurements

Both revisions used separate, initially empty configuration directories for this series. Each cell in the first table is mean wait / hold in microseconds per benchmark frame. Sums of stopwatch ticks were divided by the measured frame count. A dash means zero acquisitions. “Other” covers the remaining sites instrumented in the flight input, commanding, scene, eject and rendering files. Eject-event handling and some other helpers acquire the monitor reentrantly. Their time is already included in the enclosing capture. Adding all rows would count it twice.

| Section | Base 1 | Base 2 | Base 3 | Change 1 | Change 2 | Change 3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Update pose gathering | 0.08 / 196.02 | 0.08 / 179.78 | 0.05 / 131.52 | 0.04 / 36.92 | 0.06 / 49.24 | 0.04 / 54.55 |
| Per-hull eligibility | 0.93 / 1.89 | 0.78 / 1.98 | 0.46 / 1.38 | — | — | — |
| Per-hull eject observation | 0.21 / 4.28 | 0.08 / 3.96 | 0.07 / 2.54 | — | — | — |
| Radar projection | 0.37 / 107.03 | 0.36 / 107.66 | 0.29 / 76.66 | — | — | — |
| Scene geometry | 0.17 / 50.66 | 0.41 / 48.13 | 0.16 / 38.51 | — | — | — |
| Construction | 0.11 / 2.83 | 0.08 / 2.89 | 0.06 / 2.15 | — | — | — |
| Eject events | 0.96 / 7.17 | 0.20 / 6.24 | 0.11 / 4.73 | 0.04 / 2.88 | 0.04 / 2.49 | 0.05 / 3.45 |
| Frame capture | — | — | — | 0.06 / 108.73 | 0.08 / 93.33 | 0.06 / 116.91 |
| Other instrumented sections | 3.34 / 119.22 | 2.68 / 115.10 | 2.19 / 84.37 | 0.45 / 23.00 | 0.38 / 21.05 | 0.40 / 24.80 |

The callback table uses packets as its denominator. The last column measures elapsed callback arrival to completed application. It includes monitor wait and the synchronous callback body. It excludes transport flight and time before callback entry.

| Run | Frames | Callbacks | Wait µs/callback | Hold µs/callback | Arrival to completion µs/callback |
| --- | ---: | ---: | ---: | ---: | ---: |
| Base 1 | 1268 | 1742 | 2.81 | 19.87 | 22.69 |
| Base 2 | 1244 | 1508 | 5.94 | 17.82 | 23.76 |
| Base 3 | 2013 | 1509 | 4.05 | 15.79 | 19.84 |
| Change 1 | 2661 | 1746 | 1.18 | 13.71 | 14.89 |
| Change 2 | 3780 | 1510 | 1.34 | 12.19 | 13.53 |
| Change 3 | 2479 | 1503 | 1.39 | 14.24 | 15.62 |

Across the three windows, median Update pose hold moved from **179.78 to 49.24 µs/frame**. Per-hull eligibility, per-hull eject observation and radar projection acquired the gate zero times on the changed build. The combined capture held it for a median **108.73 µs/frame**. Median callback wait moved from **4.05 to 1.34 µs**, and arrival-to-completion from **22.69 to 14.89 µs**. Live packet timing and host load varied between windows. The receiver exclusion test independently proves that art resolution and prepared painting can finish while another thread owns the gate.

A second Xvfb comparison, taken earlier while other qualification jobs were active, gave base/change medians of 111.87/84.82 µs per frame for pose hold, 1.46/2.24 µs per callback for receive wait and 15.93/21.11 µs per callback for arrival to completion. Radar projection again acquired the gate zero times after the change. The direct structural and allocation gains repeated. The packet timing moved in different directions between series, so these measurements do not establish a general latency improvement.

## Race and ownership

On the base build, replay frame 6561 admitted 54 poses with sector generation 2 and sky seed 371. A worker then took the real session gate, applied SET_CLUSTER and re-exported the viewed cluster under the same ID with seed 1234567. The original lighting call consumed **1234567 with generation-2 poses**, after the receiver had advanced to generation 3. This was a forced interleaving. Its frequency under ordinary packet timing was not measured.

The receive dispatcher owns writes to ClientWorld under ConnectionSession.Gate. The game thread owns the reusable frame and pose buffers. Update keeps the stateful prediction, audio and shot decisions on their original path. It records its reference ship, side, hull, parent, docking/visibility state, rendered sector and both generations with the poses. Remote effect assembly, hull model, texture and scale resolution run after the Update lock. The renderer retains the pose's world-cache object across the frame. A receiver reset publishes a different cache object.

At Draw entry, one outer gate acquisition captures those identity fields again, the sector's sky, the current endgame packet, own side, rank table, player rows and commander names. It captures the visible-sector inputs, camera and HUD inputs together. A rejected pose set contributes no hull pass, target preview or radar contacts. The next Update gathers the new view. A transition **after** capture leaves the completed frame drawable. Its color and depth passes share an admitted-ship set. The starmap, sky, lighting, radar, target preview, scoreboard, briefing and endgame consume captured data. Sorting endgame tables, resolving GPU assets, projecting radar contacts and drawing happen after release. A cold station-art warning joins the captured chat window in the frame that discovered it. After rendering, the game thread persists it only if the originating view still matches. The loadout marker uses the turret occupant captured with the slot display row.

Wire updates, map exports, the rank array and the endgame packet are replaced wholesale by receive. Mutable missile, mine, probe and treasure wrappers become value rows. Sky light vectors are detached on export change. The visible entity collections, ship art keys, map scratch and projectile buffer retain capacity across frames. Native ship, radar and game-over rules remain those already ported from clintlib/appmsg.cpp:1009–1049, WinTrek/radarimage.cpp:582–700 and WinTrek/gameoverscreen.cpp:84–190,877–1009. This change introduces no new gameplay rule or visual flag.

## Equivalence

The busy replay sought to frame 6500, added 48 ships, warmed 60 frames and then submitted 96 projectiles and four explosions. At frame 6740 it had 66 ships, 54 poses and 68 radar contacts. Under Xvfb, each comparison read all 518,400 RGBA pixels with the same settings and artwork. SHA-256 prefixes below identify the matched images. Direct byte comparison found **zero changed pixels** in every pair.

| Replay frame | Classic RGBA | Modern RGBA | Ordered radar contacts, both skins | World state |
| ---: | --- | --- | --- | --- |
| 6561 | `D9DBB40F9EA6` | `60338016DD44` | `FAB5FD4980DB` | `10B77611C8F0` |
| 6621 | `E382010DCC00` | `F7A2B95FAD3B` | `5ECBE38B2188` | `E1B11C0A573E` |
| 6740 | `E300FB3EFE97` | `9F126998F3EC` | `2730AB79D1E4` | `8A043466FD45` |

The working tree includes PNGs from the last compared frame. [Classic](../../.scratch/p1100-classic-6740.png) · [Modern](../../.scratch/p1100-modern-6740.png)

A second proof forwarded the complete HudModel to the real active skin adapter and hashed its JSON. For the same normal replay frame, Classic matched base at `F160914F2CEF`, Modern at `C231DFE9A085`. The corresponding whole-frame RGBA hashes matched at `45EE21B50E76` and `E1985E9D99BE`. Each run started with a fresh HUD configuration so the one-time control hint had the same state. A recording ITransport saw the following four framed datagrams in order in both revisions and skins. The first three are explicit pilot commands. The fourth was emitted by the next normal replay frame.

```text
0A006A00B11F020000003BB353D3
0C005D00B11FFFFFFFFF0102C64D28FF
0600B800FFFFCBC984FE
1000230000000000000000000000000037D76644
```

## Regression proofs

The Fast suite drives native message applications through five transitions, including re-entry with the same sector and reconnect with retained mission identity. For each transition 24 receiver handoffs check the pose identity and retained frame. A separate DesktopGL process performs three cycles through all five between actual GatherRenderPoses and Draw, checking scene admission, contacts and preview. The endgame test reuses the same ship ID with different result, side, commander, rank table and roster. Classic and Modern each compare a control frame with a frame drawn after a receiver replaced the mission. The receiver retains the monitor until the draw returns. A two-second escape detects render-time monitor acquisition. A 250 ms bound on warmed capture detects long blocking work inside capture. The two time bounds detect blocking regressions. They allow for software rendering and loaded CI hosts.

| Deliberate change in scratch build | Observed red check |
| --- | --- |
| Erase side, hull and both generations from the pose identity | All five transition cases admitted equal identities |
| Drop the production view-identity rejection | The replay admitted old poses after a same-ID rotation. The real transition loop also failed at eject |
| Resolve a docked hardpoint occupant on demand after capture | The marker changed from Ada to the next gunner while the captured row still named Ada |
| Keep the live light-direction array | The snapshot independence assertion failed |
| Retain previous endgame players on refill | The second endgame had an extra row |
| Read the new world's sky after rotation | Full-backbuffer comparison failed in both skins |
| Source one semantic command-verb value from the rotated world | Shared HUD comparison failed in both skins |
| Reacquire the receive gate inside radar projection | Both prepared draws waited for the receiver's two-second escape |
| Sleep 350 ms inside snapshot capture | Both warm-capture bounds failed |
| Send wing 3 in place of wing 2 | Both ordered-byte checks failed |

The instrumented baseline is an archive of the base revision. Mutation sources and executables are scratch-only. The retained proof entry point is `hud2-proof --render-snapshot <classic|modern> <native|steady|rotate|transitions> <client-output-dir>`, invoked from the repository root. The native mode renders a normal replay frame, and the steady/rotate pair tests a controlled handoff on the same rendering path.

## Allocation and game-thread CPU

Three paired runs per skin used the same 180-frame crowded replay as the pixel proof, with readback and hashing disabled. The order was base/change, change/base, base/change. Bytes are the game thread's managed allocations. CPU is the Linux game thread's total processor time across the measured frames. It includes software GL submission. Both revisions used the same draw count and scene counts.

| Skin | Base B/frame, median | Change B/frame, median | Difference | Base CPU ms/frame, three runs | Change CPU ms/frame, three runs | Draws/frame, both |
| --- | ---: | ---: | ---: | --- | --- | ---: |
| Classic | 426,823 | 400,717 | −6.1% | 20.17, 19.56, 19.00 | 20.00, 19.33, 19.22 | 746.86 |
| Modern | 413,352 | 387,744 | −6.2% | 14.00, 13.61, 14.00 | 13.56, 13.50, 14.28 | 620.86 |

Allocation decreased in every pair, by about 25–26 KB/frame. Median game-thread CPU moved from 19.56 to 19.33 ms in Classic and from 14.00 to 13.56 ms in Modern. The CPU ranges overlap. Wall-clock frame times on this software renderer are load-sensitive.

## Reproducing the checks

Run from the repository root with the local artwork configuration, the .NET 10 SDK, scratch-local NuGet caches and a writable scratch-local XDG configuration directory. The benchmark ran locally with no remote clients. The instrumented worktrees accumulate monitor entry, acquisition and exit timestamps. Enable counters at the start of each measured window and print totals before combining benchmark runs. Record the outer receive callback in `ConnectionSession.Ownership.cs`, including its decode/apply work. The archive, break builds and per-run raw output are under `.scratch/` in the working tree.

```sh
xvfb-run -a src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame --benchmark skirmish:small --benchmark-seconds 12 --benchmark-exit --window 960x540
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --filter FullyQualifiedName~P1100RenderSnapshotTests
```

The deterministic comparison uses `data/benchmarks/battle.alr` and the busy fixture from [P1092](PARTICLE_BUFFER_REUSE_P1092.md). The lane's `.scratch/shipping-exact.sh` runs both revisions with fresh profile directories and checks entire backbuffers, contact order, world hashes, the HUD and datagrams. `.scratch/instrument.py` names the measured critical sections and `.scratch/control-series.sh` runs both instrumented builds.

## Qualification

Debug and Release solution builds, including the browser client, completed with zero warnings and zero errors. All 157 standalone tool projects built. The glossary check resolved 1,188 paths and the parity scorecard validated the matrices. The browser continues to use its original transit-drain API. The desktop overload lets it reuse caller-owned storage.

The nine new P1100 tests ran three times in fresh test hosts with artwork and software GL. The focused selection of 156 cases, including the nine, passed three times. A second selection of 130 radar, lighting, starmap, effect, directory-chat and loadout cases also passed three times. After the docked marker correction, all 63 loadout model, layout and hardpoint tests passed three more times. The docked hardpoint regression switches the resolved gunner after capture and compares the captured occupant with the current model.

The first Fast sweep exposed a source-shape assertion for the target preview. It now checks the captured sector collection and the prepared-frame draw path. That sweep also reproduced the known in-worktree temporary-directory issue in five player-bundle tests and the pool-sizing fixture's dependency on the available processor count. With all 16 CPUs visible and a temporary-directory alias outside the checkout ancestry pointing to the same scratch storage, both isolated control groups passed three times. Three NPC timing outliers from the first sweep passed three isolated controls. A second complete 16,208-test sweep also saw the ledgered P1017 buy-flow and P1031 treasure observations and an unreceived P847 server payday. Each method passed in three isolated runs. The eight time-limit outliers in that sweep covered six methods in the P796, SC2 support, P814, P1000, P750 and P999 fixtures. All 27 theory and method executions passed on each of three isolated runs, the slowest at 12.80 seconds against the sweep’s 13.885-second limit.

The final `tools/run-tests.sh --tier fast --parallel 2` run executed **16,208 tests in 789.4 s**. Of these, **16,198 passed, four failed and six skipped**. All 19 quarantine cases passed. The runner also flagged four durations above its calibrated 16.393-second limit. Raw results are in `TestResults/tiers-20261127T225941.431171Z/summary.json`.

| Final Fast observation | Isolated controls |
| --- | --- |
| P1091 Classic and Modern 1080p goldens | Both reproduced in all three isolated runs. Base matched the actual pixels from each skin. |
| P994 gunfire batch allocation, 1,760 bytes against zero | Passed three times alone. Same signature as the existing allocation entry. |
| Armed probe delivered no observed damage within 32.228 s | Passed three times alone, each under one second. The same damage timeout is in the ledger. |
| SC2 support, P814 carrier and P999 civ 5 completed above the time limit | All three methods passed every isolated run under the limit. The fourth time-limit failure was the armed probe above. |

The 1920-wide P1091 Classic and Modern goldens failed on this software renderer in three isolated runs and produced identical actual hashes on the unchanged base. The base geometry checker reported zero overlaps. The archived P928 supplemental golden was outside this change. Additional sweep observations and their isolation results are recorded in the [flake ledger](../TEST_FLAKES.md).
