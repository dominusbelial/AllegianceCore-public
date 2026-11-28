# P1102 — Builder and logging allocation

## Brief items

| Item | Status | Evidence | Time |
|---|---|---|---|
| N3 ripcord byte | done | Base differed at offset 55 in both serial encoders. Both parallel cases passed. After the native correction all four passed. | ~2 min |
| Moving-fleet baseline | done | Three Release runs, 400 moving armed peers, 200 measured ticks each. See table. | ~3 min |
| F5 pooled parallel own pose | done | Warm builder sites measured zero. Pooled/fallback packet type and DTO bytes passed in all four combinations. | ~3 min |
| F6 lazy formatting and audit | done | Gate, output and attempts verified. The 282-call audit and final binding check are below. | ~8 min over two passes |
| Regression tests | done | All requested Fast filters green in three fresh processes each. Both new test methods detected deliberate breaks. | ~5 min |
| Final measurements and report | done | Three alternating dispatch/final pairs, site totals, byte equivalence, deliberate breaks and failure qualifications below. | ~34 min elapsed including gates |
| Overall green light gate | not done | Builds and focused proofs passed. The final Fast sweep exited 1 for three ledgered assertions and three ledgered timing overruns. | 710 s final sweep plus isolated repeats |

## N3 — Correct the serial ripcord byte

Native assigns the current ripcord state in both single-update paths (`FedSrv/fsship.cpp:542,807`). With the original code, the first difference from the independently encoded packet was byte 55, expected 1 and received 0, followed by the CRC. Both serial encode-once settings failed. Both parallel settings passed. The serial fallback now sets the DTO field and the pooled writer sets the byte before sealing. This is a behaviour correction toward native. Ripcord toggles do not independently trigger a send.

## Moving-fleet measurements

| Arm / run | Own packet B/tick | Pose stamp B/tick | Missile log B/tick | Sim thread B/tick | Process B/tick | Projection mean ms | p95 ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| Dispatch 1 | 150400 | 41600 | 96000 | 376354 | 755683 | 11.211 | 11.940 |
| Dispatch 2 | 150400 | 41600 | 96000 | 379438 | 755698 | 11.197 | 11.740 |
| Dispatch 3 | 150400 | 41600 | 96000 | 376407 | 755843 | 11.035 | 11.720 |
| Final 1 | 0 | 0 | 0 | 252338 | 326910 | 10.949 | 11.511 |
| Final 2 | 0 | 0 | 0 | 252330 | 326955 | 10.908 | 11.514 |
| Final 3 | 0 | 0 | 0 | 252321 | 327151 | 11.664 | 12.347 |

At dispatch `dd973eb38`, the three statements cost 376, 104 and 240 bytes per moving, armed peer per tick. All three reached zero after warmup. Median whole-process allocation fell by 428,743 B/tick (56.7%), from 755,698 to 326,955. These totals include the publisher, which also avoids the deferred own-pose encoding arrays. Median projection mean was 11.197 ms on dispatch and 10.949 ms on final code. Corresponding p95 values were 11.740 and 11.514 ms. The fixture measures the broadcast suffix through flush. It does not establish a full simulation tick-time improvement.

The initial three runs before F5/F6, with only the verified serial N3 fix applied, independently measured exactly 150,400 / 41,600 / 96,000 B/tick at the three sites. Process totals were 755,620 / 755,638 / 755,659 B/tick. Three runs immediately after the first F5/F6 implementation and three more after the concatenation audit all measured zero at each site. During overlapping build/test activity the final mean ranged from 14.044 to 15.870 ms, illustrating the host sensitivity of timing. The table uses fresh alternating pairs after all solution/tool builds and both Fast sweeps finished. The first and third pairs ran dispatch first and the second pair ran final first.

**Measurement method.** Each arm used three fresh Release test processes, server GC asserted on, tiered compilation off, runtime processor count fixed at eight. The server resolved seven parallel builders and eight pooled transport workers. Both arms retained the production immutable snapshot, encode-once writer, P863 budget, native ship updates and GotoPlan. The dispatch binary was built from a local export of HEAD. The final binary used the same export with the working-tree source changes. Both included the identical test fixture and source probes. Each arm ran in a fresh process for each pair. To reproduce the inputs, boot `UpdateCadenceTests.Config()` with benchmark seed 1102 and max degree eight, disable initial miners, start the mission and use `UpdateCadenceTests.Seed(server, 400, 400)`. Alternate sides by index and mount the first available core missile launcher at mount zero, amount three, fully mounted. Advance every ship by (1, 2, 3) before each tick. Advance the cadence clock by 1/20 second, call `TestProjectRecordedTick` with snapshot and discovery enabled, then flush. Discard 30 warmup ticks and measure the next 200. Each run asserted 80,000 dirty own-pose builds and 80,000 missile-lock log attempts.

Temporary paired probes used `GC.GetAllocatedBytesForCurrentThread` around own-message capture/packing/storage and around the subsequent pose stamp on the thread executing each builder iteration. The sim-thread log probe bracketed just the missile-lock Write statement. The sim column includes any Parallel.For work done by the caller. Whole-process allocation used `GC.GetTotalAllocatedBytes(true)` around all 200 ticks. The columns overlap. Elapsed samples cover the complete broadcast projection through publisher flush, with p95 at sorted sample 190 of 200. These are local fixed-world measurements with no socket clients or full mission update. The allocation percentages describe this fixture.

## Equivalence and ownership

Workers read the same frozen peer order and dirty predicates. Each dirty worker stages the shared writer’s own packet and a value containing position, forward, up, shield, mining and the first-send latch. The sim thread submits the own packet first using key 12 and the guaranteed bit. After the remaining packets and verbose lines it writes the pose to the peer in the same ordered effects step. Each lease has a single recipient. The slot clears its packet reference after transfer. The existing finally returns any unsubmitted lease when building or applying fails. A shutdown drop releases the packet. The writer seals the message length. The publisher computes the same CRC over those bytes and returns its lease after processing. The serial walk continues to replace the live record so recipients later in that walk can consult the original mate poses. Resume resets only the three geometric fields as before. No new RNG draw or send predicate enters the projection.

## Interpolation audit

A Roslyn syntax walk of every `ConsoleLog.Write` invocation in server and core found 282 calls (252 server, 30 core). There were 260 direct interpolated arguments, 19 additions, two plain variables and one plain literal. The walk inspected 830 interpolation expressions, including nested holes. Roslyn initially resolved all 260 direct interpolations and 12 interpolated additions to the handler. Three unguarded additions still bound to string because they mixed interpolation with a literal or conditional. The probe-inside-rock, same-side NPC status and cluster-warning lines now keep their suffixes inside interpolation. Their text and outer attempt count are preserved. Final binding was 275 handler calls (260 direct, 15 additions) and seven string calls. The four remaining additions already have explicit verbosity guards. The three other string arguments are plain lines. The hole audit covered both overloads. The two tool-side calls also read only existing bot state.

One hole called a potentially mutating method. `EnsurePeerUpdate(from)` can create a peer dictionary entry. The cookie mismatch branch had already ensured that entry for the comparison and the dropped-cookie increment. The increment now retains the record locally and both the native-client check and the diagnostic read it. Counter and state changes still run with logging off. No other hole wrote state, consumed RNG, advanced an enumerator, or invoked a rate-limit ticket. Ticket fields are read after admission. Other calls were current-value formatting, type/name lookup, redaction of a local string, warning-text lookup, capability tests, vector math and mining capacity (core constant and side attributes). Existing explicit verbosity guards still skip their entire call. Calls reaching the new overload always count one attempt and use the captured verbosity decision for both formatting and enqueue. Both overloads share emission and drop accounting.


## Regression evidence

The four moving-ship rows run both builders with encode-once on and off. Each seeds two own-pose streams, moves both ships on successive ticks, arms and then clears ripcord, and checks that a stationary fourth tick produces no own frame. Checks cover distinct per-recipient packets, pooled versus fallback type, key 12, guaranteed delivery and full DTO bytes including CRC. The logger rows compare the existing string overload with an aligned, formatted interpolation, check hole evaluation and count both attempts on the calling thread.

| Deliberate break | Observed failure |
|---|---|
| Original serial writers omit the ripcord field | Both serial rows failed on byte 55. Both parallel rows passed. |
| Restore eager interpolation by running the logging test on the original string overload alone | Quiet row evaluated the hole once, expected zero. |
| Force the changed parallel builder to use the DTO fallback with encode-once enabled | Pooled-parallel row observed ordinary packets for both recipients. |
| Count probe attempts only when verbose output is off | Verbose row observed zero attempts, expected two. |

The last two breaks were in a private local copy. The ordinary tests and build gates use the working tree with the intended implementation.

## Verification

Debug and Release solution builds finished with zero warnings and zero errors. The glossary check resolved 1,201 paths and the parity scorecard validated the new row. Both new test methods were present in Fast discovery. Each Release filter below ran three times in a fresh process with artwork available.

| Filter | Passed each run |
|---|---:|
| Moving own-pose theory alone | 4 |
| Interpolated-log theory alone | 2 |
| ParallelBroadcastByteIdentityTests | 11 |
| P1089TeammateScratchTests | 5 |
| FedPacketGoldenBytesTests | 4 |
| OwnShipCoalesceKeyTests | 2 |
| ConsoleLogNonBlockingTests | 10 |
| ClusterWarningLiveTests | 1 |
| Teleport probe inside an asteroid | 1 |
| BroadcastSnapshotTests | 5 |
| P863b surge packet, lane order and deviation replay | 1 |

All four P1089 hashes matched their pinned digests and their serial transcript. These cover native/port update mode crossed with change-gate on/off, a failed build and retry, peer departure and replacement, packet order, coalescing keys and guarantees.

| Ship updates | Status change gate | Transcript SHA-256 |
|---|---|---|
| Native | On | `336557327C7A267A78D51381A5B87411E396B501B4EB490738EE40D71119F7B5` |
| Native | Off | `4A31E8E95488A8530AE1A7EA0FF2561147EFA8A53D2D74F6371DF913ABCF8876` |
| Port | On | `001D46BD34BC3DD3AACEBAE180BCA4CA9004F1FA080C30E75700BF225EEEC95C` |
| Port | Off | `954D22296E52C14360AEB3C1E86BF5C0E79C2D9C0FDD7C42F260AD50BE9D78FF` |

In an additional Release live-console check, `ConsoleVerbosityLiveTests` passed 3/4, 3/4 and 4/4. Twice the existing stalled-stdout test missed a heavy update at line 283. The exact method passed in three fresh processes on this change (565, 577 and 565 ms) and in three alternating processes built from the dispatched base (558, 533 and 510 ms). The missed-receipt signature already appears in the flake ledger. After the final concatenation edits, the class passed 3/4, 3/4 and 2/4. The stalled-stdout receipt failed in all three class runs. The final class run also measured 1,786 versus 179 lines/s at its strict tenfold threshold. All four live-console methods passed three fresh exact-method Release runs each on final code. The console-volume signature is also ledgered.

### Initial Fast sweep qualifications

The initial sweep discovered 16,236 Fast cases. It ran in 756.0 s and exited 1. All nine infrastructure checks passed. The P928 supplemental golden was left alone.

| Method or cases | Sweep observation | Three isolated Debug runs |
|---|---|---|
| P1091 painted HUD rows, 1920 default | Classic and Modern captured the existing software-renderer RGBA mismatches, with zero layout errors | 19/21 each time. The same two cases failed with the ledgered digest prefixes. |
| P1017 money change while docked | Expected Muted, observed Worse, matching the ledger | Passed 3/3 |
| P877 chased bot wake mine | Pursuer remained at hull 1 after 33.774 s, matching the ledger and exceeding the 13.620 s Fast limit | Passed 3/3 |
| P974 stalled reconnect disposal | Dispose completed before the fixture released the connector (6.777 s) | Passed 3/3. Added this signature to the ledger. |
| PlayerBundlePathsTests, five rows | The fake outside-repository install discovered the checkout’s ancestor local.env through the scratch-local temporary directory. This reproduces the existing fixture obstruction. | 4/9 passed in each scratch-path run. All 9/9 passed in three more runs using an outside-repository path alias for the same scratch storage. |

### Final light gate

Final Debug and Release solution builds each reported zero warnings and zero errors. The first standalone-tool pass exposed three missing pinned KNI shader-pipeline packages. Restoring their exact local archives into the scratch feed allowed the shader tool and the complete `tools/build-tools.sh --drop-bins` sweep to build. The full final-source sweep built all 158 tools. NuGet package and HTTP caches and XDG configuration were scratch-local.

The final Debug Fast run used the outside-repository path alias to the same scratch storage. All nine infrastructure checks passed. Of 16,236 cases, 16,226 passed, seven skipped and three failed in 710.1 s. All 24 named wire, scratch, coalescing and logging regression cases passed in the sweep. All path-resolution cases passed. The complete gate exited 1.

| Final exception | Sweep result | Exact-method repeats and ledger |
|---|---|---|
| P1091 painted HUD rows | Classic and Modern at 1920/default repeated the RGBA mismatch | 19/21 in each of three repeats, with the same two digest mismatches as the initial sweep and prior baseline proofs |
| P796 varied loadout | All assertions passed. Civs 35 and 25 took 14.329 s and 16.146 s | All three civ rows passed in each of three repeats below the calibrated limit. Existing timing entry. |
| SC2 Nanite support selection | All assertions passed. Civ 3 took 14.508 s | Both civ rows passed in each of three repeats below the calibrated limit. Existing timing entry. |
| AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity | The cross-side recipient had three unexpected mining receipts at line 335 | Pass / pass / fail. The failing repeat saw one extra receipt at the same assertion. This matches the existing mining-receipt entry. |

All three timing offenders passed their assertions in the sweep. The calibrated threshold was 13.555 s. The three P796 rows and two SC2 rows all completed below the limit in each isolated run. The two HUD golden failures reproduced in every isolated run. The final sweep used the existing tests, limits and goldens. [Flake ledger](../TEST_FLAKES.md)
