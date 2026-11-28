# P1103 treasure pickup broad phase and discovery membership

Across three 120v120 fights per revision, median late pickup time fell from **1.25 to 0.03 ms/tick**. The changed discovery latch held 427,365–458,162 memberships in an estimated **303–317 KiB** of directory and bitmap storage. Both 500-tick traces and both per-peer packet replays matched the baseline byte for byte.

## Brief items

| Item | Status | Elapsed | Evidence |
|---|---|---|---|
| Baseline measurements | done | ~24 min including setup | Three 120v120 fights completed, 2,454 / 2,614 / 4,212 kills. Per-sector, time and capacity samples are in the CSV. |
| Pickup broad phase | done | ~5 min | Ordered-candidate and differential tests passed individually 3× |
| Compact discovery membership | done | ~3 min | Tuple-set differential passed individually 3× |
| Exactness | done | ~9 min | Both 500-tick traces and both 200-peer packet captures matched byte for byte |
| Fast tests and deliberate breaks | done | ~11 min overlapping other work | Three new proofs passed 3× individually and failed under mutation. Affected runs and P928 exception below. |
| Checked-in supplemental P928 golden | not done | ~35 s for four Debug runs | Tick 33 already diverges on the dispatch base. Both complete revision traces match. |
| Changed measurements and report | done | ~23 min of changed fights, ~9 min reporting alongside other work | Three changed runs, all-window CSV, allocation replay and retained storage estimates below. |
| Debug, Release and tool builds | done | 0:50 / 1:15 / 6:46 | Zero solution warnings or errors. All 158 tool projects built. |
| Green Fast sweep | not done | 10 min + ~5 min isolated repeats | Nine failures outside P1103 and eight timing offenders qualified below. |

## Measurement setup

Dispatch base `dd973eb38`. The instrumented Release baseline started before the production edits. The local showcase uses 120 bots per side, the 13-sector spider map, march staging, two 200-seat teams, plans A, civilizations 25/3 and 400 seconds after the driver acknowledges both fleets. The server resolved 15 builders and 16 pooled senders on 16 logical CPUs. Boot receipts confirmed immutable snapshots, encode-once, parallel build, pooled transport, native ranked ship updates with the built-in P863 budget, native GotoPlan, automatic time dilation and concurrent server GC. Both arms use the same Release bot binary and PCore001 version 990051329. Scratch-only probes nest pickup under economy and sample every sector and the discovery latch every 100 passes. Raw logs and both executables are under `.scratch/`. The harness adapts `tools/p928-allocation-proof/showcase.py` to Release JIT binaries, size scale 8, a single bot worker, native crowding, research income multiplier 5,000 and research time scale 8. The source of the observations is a 16-logical-CPU allocation on an AMD Ryzen 9 9955HX with .NET SDK 10.0.404. The CSV reports every sector at each sample, including empty sectors.

The dispatch-base Debug P928 primary golden passed. Its supplemental case failed at tick 33 in the discrete event hash. The baseline and checked-in row agree on state, continuous hash and 3,091 RNG draws at that tick.

## Local fight measurements

The timing columns are medians of complete five-second window means across profiler ticks 6,000–8,400 (approximately minutes 5–7 after boot). Wall p95 is the median of the reported window p95s. The pickup scope is nested in economy, which is nested in update. The pair bound is the median sum, over sectors, of treasures multiplied by eligible ships at the 24 samples in this interval. Kills count the whole fight. The CSV retains the full run, including startup and initial contact. [Five-second samples and sector populations](TREASURE_PICKUP_BROADPHASE_P1103.csv)

| Run | Kills | Treasures / eligible ships | Pair bound | Pickup ms | Economy ms | Update ms | Wall ms | Wall p95 ms | Allocated MB/window |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Base 1 | 2,454 | 329.5 / 165.5 | 16,225.5 | 1.11 | 1.12 | 2.51 | 6.97 | 9.03 | 142.8 |
| Base 2 | 2,614 | 320.5 / 172 | 16,683 | 1.25 | 1.25 | 2.95 | 7.98 | 11.03 | 136.0 |
| Base 3 | 4,212 | 531.5 / 124 | 28,394 | 1.98 | 1.98 | 3.80 | 8.28 | 10.94 | 143.1 |
| Change 1 | 4,038 | 523.5 / 121 | 27,594 | 0.03 | 0.03 | 1.61 | 5.82 | 7.79 | 147.0 |
| Change 2 | 4,154 | 529.5 / 124.5 | 28,250 | 0.03 | 0.03 | 1.61 | 5.83 | 7.86 | 146.6 |
| Change 3 | 4,099 | 499 / 126.5 | 27,321.5 | 0.03 | 0.03 | 1.66 | 5.96 | 7.99 | 147.3 |

Across the three run medians, pickup fell from **1.25 to 0.03 ms/tick** (97.6%). Update went from 2.95 to 1.61 ms and whole-tick wall mean from 7.98 to 5.83 ms. The matches ran independently. The per-sector counts and kill totals show the different battle loads. Base 3 and change 1 had similar late pair bounds (28,394 and 27,594) and pickup times of 1.98 and 0.03 ms. Other build or test work shared the host during parts of the baseline and first changed run. The fixed workload supplies the same-input comparison. A causal whole-tick saving or a reliable change in collection frequency cannot be isolated from these live runs.

### Discovery storage

All six matches sampled 243 seated connections after admission. The base directory holds one entry per membership. The changed directory holds one entry per object. The last sample is taken every 100 server sweeps. Bitmap expansion counts exclude each row’s first allocation. Directory allocations include first allocation. The base counts reconstruct intervening growth steps from the observed monotone capacities using a separate run of the same Release runtime and the same tuple type. The changed server counts actual directory and row expansions.

| Run | Last sweep | Memberships | Directory capacity | Directory allocations | Objects | Bitmap expansions | Max row bytes¹ | Backing KiB² | Gen2 collections³ |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Base 1 | 9,000 | 327,732 | 672,827 | 17 | — | — | — | 13,141.2 | 4 |
| Base 2 | 8,900 | 338,398 | 672,827 | 17 | — | — | — | 13,141.2 | 4 |
| Base 3 | 8,900 | 438,755 | 672,827 | 17 | — | — | — | 13,141.2 | 4 |
| Change 1 | 8,900 | 427,365 | 4,049 | 10 | 3,519 | 7,038 | 32 | 303.2 | 5 |
| Change 2 | 8,900 | 458,162 | 4,049 | 10 | 3,769 | 7,538 | 32 | 316.9 | 3 |
| Change 3 | 8,900 | 442,698 | 4,049 | 10 | 3,644 | 7,287 | 48 | 310.1 | 3 |

¹ Word payload only. ² At the last sample. A separate allocation replay measured the bytes allocated by the backing-array replacement at each directory capacity. For the changed arm the estimate adds the sampled row payload and a 24-byte array header per object on this 64-bit runtime. Peer-slot bookkeeping and collection objects are excluded. ³ All complete windows including boot. GC events were observed process-wide, so the counts alone do not attribute a collection to a particular allocation.

The CSV includes independent clocks for consecutive profiler windows, sector samples and discovery samples. Column groups on a row share a sample ordinal. Exact boundaries are given by each group’s own clock. In particular a server sweep can precede a sector pass by several ticks. The capture did not record individual tick samples.

The tuple set reached capacity 672,827 in all three fights. Its last resize allocated **13,456,592 bytes** for the new entry and bucket arrays and rehashed the existing memberships. On this runtime it occurred on membership 324,450. The changed latch retained an estimated **303–317 KiB** of directory and bitmap storage at the last samples. Its largest individual bitmap payload was 48 bytes. Changing an object's bitmap copies only that object's words. Directory growth still copies one record per object.

## Why the answers match

**Pickup.** The sector gathers cash, part, flag and powerup references in model order. The grid stores each current endpoint exactly once in a 128-unit cell. For each eligible ship, it bounds the segment from `Position - CollisionVelocity * dt` to `Position` on each axis. The expansion includes the absolute pickup threshold (the scalar test squares it) and the largest absolute component of any treasure's displacement. At the time of a hit the relative separation fits inside the threshold. Moving from that point to the treasure's indexed endpoint costs at most the extra displacement on each axis. Every scalar hit therefore has a candidate cell.

Both endpoints must fit within ±2²⁰ on each axis and the absolute threshold must fit within 2²⁰. In this domain the two relative endpoints are bounded by 2²¹ and relative travel by 2²². Rounding the endpoint differences, travel, product and interpolated sum costs less than one world unit per axis. A four-unit outward margin also covers rounding the radius comparison, drift and cell bounds. Larger magnitudes, non-finite coordinates or radius, and a query over 512 cells take the original scalar path. Fewer than 16 treasures or a single occupied cell also use that path. In particular the scalar rejection uses `>`, preserving its unordered comparison for NaN.

Ships keep their original order. Candidate indices sort ascending before the first scalar test. A callback can move the current ship or any treasure, even on refusal. After a callback the current ship finishes its unvisited suffix with live reads, and the next eligible ship rebuilds the grid. A nested pass gets separate scratch. Only an accepted callback appends to the pending list and marks that treasure done. The empty set needs no lookup. The grid consumes no random numbers. The native references are `Igc/treasureIGC.cpp:239-259` for average trajectory velocity, `Igc/treasureIGC.h:86-87` for player and rescue eligibility, and `FedSrv/FedSrv.CPP:13558-13650` for site decisions and effects.

**Discovery.** An active full-width connection ID owns one slot. Bit `slot % 64` of word `slot / 64` in row `(kind, signed object ID)` is the old tuple's membership. Add returns true only on a zero-to-one transition. Object destruction leaves the row alone, so ID reuse gives the previous answer. Single-peer departure clears its bit across rows before the slot is available for reuse. Migration builds the retiring peers' word masks and clears the cohort in one directory walk. These removals and explicit clears preserve the sweep cache's removal version. At the existing retirement boundary the server can discard empty rows, trim unused trailing words and shrink the directory. Other worlds' set bits survive. Native sighting and join export rules remain at `FedSrv/FedSrv.CPP:10766-10784` and `FedSrv/fsmission.cpp:4181-4247`.

## Transcript evidence

| Before/after replay | Result |
|---|---|
| Independent old pickup pass, 32 seeds × 3 passes | Every timestamp, ship, treasure, answer, callback RNG value and pending pair matched. Cases include moving loot crossing a stationary ship, zero-duration sweeps, shared loot, own/enemy flags, refusal, synchronous movement of a later treasure, NaN and finite overflow inputs. |
| Tuple-set reference | Add answers, pair count and removal version matched across 196 full-width peers, multiple bitmap words, slot recycling, repeated IDs by kind, 8,000 mixed operations, cohort removal, trim and clear. |
| P928 primary, 500 ticks | Entire base and changed CSVs identical, SHA-256 `021fc33782cf0e602be248acee5e807b462c7199ef25f83f776694f93b50c80a`. |
| P928 supplemental, 500 ticks | Entire CSVs identical, SHA-256 `6d613898eac58a6a9d0433a27cebcc19ca5da40022c5d478f03fdbc74f48b65b`. |
| P863 serial projection, floor 0 | 649,988 frames, 54,501,371 payload bytes. Binary comparison passed, SHA-256 `a9f2c6ecd4b62f3bd36061c2871322154f6d0a7d79d29b689bc4e65a1baeeaf4`. |
| P863 parallel projection, floor 2 Hz | 649,988 frames, 73,326,983 payload bytes. Binary comparison passed, SHA-256 `8326e951557e4af8408c8835d6f3f6cd28803dea33403406d38e92e706b3ac9d`. |

For the wire replay the same driver ran against each Release server assembly, using the production projection in `P863ClashBudgetTests.Two_hundred_ships_preserve_every_peer_message_and_deviation`. The scratch driver used seed 1103 for simulation, benchmark and civilizations. Each file orders by peer, preserves delivery order within that peer, and records peer, tick, key, guarantee bit, payload length and full bytes, followed by 25,000 deviation samples. The replay includes late visibility, treasure removal, peer changes and latch invalidation. The checked-in P928 harness generated the two state/event/RNG traces.

| New Fast proof | Deliberate break and observed failure |
|---|---|
| `Seeded_pickups_match_every_callback_answer_draw_and_pending_pair` | Removing the ascending candidate sort changed the first callback in the differential transcript. |
| `Grid_prunes_distant_cells_and_orders_crossing_candidates` | The same deletion returned bucket-chain order instead of ascending, unique indices. |
| `Membership_and_invalidation_match_tuple_set_through_slot_and_id_reuse` | Omitting the word write on single-peer departure left the previous occupant's bit set. A later Add returned false where the tuple set returned true. |

Each restored method passed three times in a fresh Debug host by itself. The deliberate breaks ran against a scratch build and all three tests failed. Separately, removing only treasure drift from the query expansion made both pickup tests miss the crossing treasure.

## Fixed pickup workload

The scratch driver called the production pickup method directly in server-GC Release processes. Seed 1103 placed 350 eligible ships on one plane and 900 treasures on another in a 2,400-unit square. Every ship had horizontal velocity. The vertical gap of 25 exceeded the pickup threshold of 15. Every run had zero pending pickups. After 150 warm-up passes the driver timed 400 passes and read sim-thread allocation outside that loop. Process order was base/change, change/base, base/change.

| Run | Base ms/pass | Change ms/pass | Base bytes/pass | Change bytes/pass |
|---|---:|---:|---:|---:|
| 1 | 1.7079 | 0.2449 | 16,664 | 0 |
| 2 | 1.8013 | 0.2451 | 16,664 | 0 |
| 3 | 1.7630 | 0.2528 | 16,664 | 0 |

This replay holds geometry constant while the live matches below include synchronous callbacks, kills, changing sector populations and other simulation work.

## Gates and flakes

Debug and Release solution builds completed with zero warnings and zero errors. Packages, the local restore feed, HTTP cache and XDG configuration were under the worktree scratch directory. The glossary link check and parity scorecard passed. The original restore cache lacked three KNI shader packages. Local package archives supplied them. The shader project then passed and the complete tool build passed all 158 projects on a fresh invocation (6 min 46 s).

The focused Debug gate used 29 class or method filters, each in three fresh hosts. The 120 selected cases ran 360 times, with 356 passes and the four failures described here. Coverage included pickup and refusal, flag scoring and wire codecs, drop and expiry, treasure visibility and discovery exports, retirement across worlds, acquire and mount handling, P863 per-peer output and both P928 goldens. The three new methods each ran alone. The primary golden passed 3/3. The checked-in supplemental golden failed 3/3 at the same tick as the dispatch base. Its complete base/changed trace comparison above passed.

The P1031 dropper visibility class passed twice after one failure at line 227. The server received a drop command for ship 8097 with an empty mount. The fixture had mounted the weapon on ship 8096. The test assigns roles in spawner order and sends the command using a fixed bot index. Three method-only repeats passed on each revision. The failing command exited before either changed path could process a dropped treasure. [Flake ledger](../TEST_FLAKES.md)

### Full Fast sweep

The single full Debug sweep ran 16,233 cases in 596.9 s, with 16,217 passed, seven skipped and nine failed. All three P1103 cases and all nine runner infrastructure checks passed. Eight other passing cases exceeded the calibrated 13.348 s limit. Every failed or slow method was run individually three more times. The sweep and its performance limit remain red.

| Sweep result | Fresh-host evidence |
|---|---|
| Five `PlayerBundlePathsTests` cases discovered the worktree configuration | All five repeated 3/3 with the in-tree temporary path. Accessing the same scratch directory through an open directory descriptor gave the fixture an outside-repository pathname. All nine class cases then passed 3/3 with the same physical files and binaries. This matches the ledgered temporary-directory ancestry failure. |
| Both 1920×1080 default-scale `P1091HudOverlapTests` RGBA goldens | Same two hash mismatches on each of three runs, other 19 cases passed each time. The Classic digest starts `C100F69A`, as in the existing renderer ledger. |
| `NpcWingmanLeadLiveTests.Wingman_fire_gate_leads_a_laterally_moving_enemy` | Sweep missed the raw-aligned, lead-misaligned refusal sample. Passed 3/3 alone, matching the ledger. |
| `LivePositionRequestTests.Empty_side_request_auto_accepts` | Line 248 received only the old-side leader message. Passed 3/3 alone. Same receipt as P1094. |

All rows of the eight slow methods passed in each isolated invocation. The longest individual case across all three repeats was 6.12 s. The affected methods are the previously ledgered generated research commander, mine-chase damage, nanite support selection, varied-loadout purchases, no-goal prosperity trace, gunner turret preference, generated research ordering and civilization strategy profile reachability tests. [Timing and fixture ledger](../TEST_FLAKES.md)
