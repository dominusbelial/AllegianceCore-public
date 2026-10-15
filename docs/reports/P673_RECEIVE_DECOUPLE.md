# P673 — receive-worker decoding and deterministic tick application

2026-10-14. Base: `integ3`, `6c3e54ad` (`codex/p673`). Changes remain in the working tree; no commits or pushes. The roster implementation is unchanged. No rig/221/200v200 work was run.

## Implementation

`receive.decouple` defaults to **false**. `ALLEGIANCE_RECEIVE_DECOUPLE=1` / `true` enables it; `0` / `false` overrides a configured enable. The boot banner prints the resolved value. JSON config is covered by the existing source-generated server JSON contract.

```json
"receive": { "decouple": true, "measure": true }
```

`InboundPacket.cs` uses the existing CRC/framing/message-count validation and all 43 inbound codecs. With the flag enabled, each existing transport reader prepares the packet on its own receive thread before the simulation can apply it. Both pool and thread-per-peer transport modes retain their existing ordered reader per peer. There is no new task queue or thread pool.

The simulation consumes decoded objects, runs the existing state-dependent handlers, and advances the world only at tick boundaries. Mission stage, ship ownership, cookies, purchasing, donation/refund, mounting, chat budgets, ping throttling, and log/refusal behavior remain in those handlers. Codec errors are stored per message and refused by the same handler barriers; malformed framing rejects the whole packet. One bad message does not discard later valid messages.

A receive first reserves its original bytes and publishes a placeholder. Its worker then decodes outside the publication locks. A monotonically published completion sequence freezes readiness at the start of `Drain`; work completed later waits for the next tick. An incomplete head defers that peer's entire FIFO lane, including its leave event. The simulation never waits for decoding. A concurrent send-side disconnect cannot overtake an already-published receive, and a late callback for a closed peer cannot recreate its accounting.

P646's **8,192 events/tick**, **256 events/peer/tick**, **4 MiB in-flight bytes/peer**, carry-over, and exception barrier remain. Byte reservations cover pending decoding and deferred commands until application. Prepared objects are retained alongside raw capture bytes, so memory per buffered byte increases; the original byte limit remains unchanged. The deferred-event counter also includes a decoder not ready at the boundary, which can occur without overload. Selection rotates fairly among sorted peer IDs; the selected batch applies in **peer ID, then per-peer arrival sequence** order. This scheduling is common to both flag modes so the same complete tick inputs have the same application order. Cross-peer order formerly followed first arrival/round-robin service; that incidental order is deliberately replaced. The flag controls where decoding occurs.

`receive.measure` is separately opt-in. `[inbound]` records each tick's dispatched messages with readable headers (including codec refusals), with counts and message bytes by fmid and by `(peer,fmid)`, plus drain milliseconds. `[inbound-window]` retains 100 consecutive `tick:messages/bytes/drainMs` bins, including empty ticks. CRC and transport framing bytes are excluded from message-byte totals. This is **application attribution**, not a timestamped network-arrival capture; deferred work is reported when applied. Formatting/output is a separate `step:inboundTrace`, outside the measured drain. Detailed trace output should be enabled only for diagnosis.

## What the aligned traffic is

The local harness uses **390 real TCP peers**, all seated in one sector on `m2xspiderw.igc`, with server/concurrent GC, pool transport, four send workers, parallel native ship updates, and a 50 ms slow-tick threshold. Idle and ordinary-control windows run approximately at 20 simulation ticks/second. Cohort seating separates join work from the measured windows.

The initial messages are **LOGONREQ (1)** and each bot's automatic **POSITIONREQ (39)**. After seating, idle bots send no application replies to the repeated **SCORE_ROSTER_UPDATE (209)** broadcasts. The ordinary moving-input windows deliberately align **SHIP_UPDATE (24)** from peer IDs **1–390**, one 48-byte message per peer every three simulation ticks (18,720 message bytes on an input tick). The trigger is the harness's command cadence. The MCP bot manager likewise sends steering commands from its decision loop; it does not send a roster acknowledgment.

Code evidence: `Allegiance.Bot/Bot.cs` stores a decoded roster and breaks from its switch; `Allegiance.ClientCore/Session/ConnectionSession.cs` applies the roster to the client world. Neither branch sends a reply. The bot's automatic side request is in its LOGONACK branch. The transport delivers DATA frames to `OnReceive`; TCP acknowledgments are handled by the network stack and do not become application fmids in this drain.

**The roster is not a direct application-reply trigger in these local captures.** Its 40-tick burst remains unchanged. An indirect effect through client scheduling or a different rig traffic mix is not established here. The historical spontaneous ~2 s-period P619 stall was not reproduced in ordinary local traffic, so this report does not identify its original rig cause.

## Local A/B results

Both runs retained **390 peers**, received **19,890 packets**, drained **20,280 transport events**, and ended with **0 deferred events / 0 stage failures**. During the 100-tick idle window every peer received the complete **390-entry / 21,848-byte roster**, with **zero inbound packets** in both modes. Three complete 100-tick histograms were retained per run.

Drain statistics below select the nonempty input ticks, rather than diluting them with empty ticks:

| Input shape | Samples per mode | Off median / p95 / max | On median / p95 / max |
| --- | ---: | ---: | ---: |
| Ordinary 390-peer controls | 34 | 0.908 / 1.525 / 10.531 ms | 0.643 / 0.819 / 0.919 ms |
| Synthetic padded 390-peer burst | 5 | 208.267 / 220.558 / 220.558 ms | 2.089 / 2.220 / 2.220 ms |

The five synthetic drain samples were **208.267, 220.558, 190.074, 211.477, 198.261 ms** off, versus **2.193, 1.885, 2.220, 1.859, 2.089 ms** on. The median sum of send-to-reader-publication time plus drain time was **243.90 → 71.86 ms**: the much larger drain reduction is specifically relocation of work outside the simulation, not disappearance of decoding cost.

A before sample, captured with no GC:

```text
[slowtick] #271 wall=218ms (≥50ms) top8[ step:drain=208ms, step:broadcastShipStates=6ms, bss.build=6ms, step:update=1ms, step:broadcastMissileTrackWarning=1ms, sector:1050=1ms, bss.apply=1ms, step:broadcastShipsUpdate=1ms ] bucketSum=224ms unaccounted=-6ms gc(gen0+0,gen1+0,gen2+0,pause+0ms) parallel-worker max=0ms (workers=390)
```

The matching after input tick records `tick=270 messages=1170 bytes=76672440 drainMs=2.193 fmids[24=1170/76672440]`. Profiler tick numbers are one-based; trace ticks are zero-based. The synthetic phase produced **5 slow ticks off / 0 on** at the same 50 ms threshold. Slow ticks elsewhere still occur in broadcast/simulation/GC work; receive decoupling does not remove those. In particular, idle-window drain maxima were only **0.177 ms off / 0.012 ms on**, despite other stages crossing the slow-tick threshold.

Durable evidence: [measurements and histogram bins](P673_RECEIVE_MEASUREMENTS.json), complete [off server trace](P673_OFF_SERVER.log.gz), and complete [on server trace](P673_ON_SERVER.log.gz). The compressed traces preserve every peer/fmid attribution and all `[slowtick]` lines.

The stress phase is explicitly **synthetic**, separate from ordinary control traffic: each peer sends three 64 KiB packets, each containing one SHIP_UPDATE padded to 65,532 message bytes. The existing codec accepts these trailing bytes and the existing transport accepts the packet at its frame ceiling. Thus each aligned burst carries **1,170 commands / 76,672,440 message bytes**. Five bursts keep command application small while making CRC/framing work dominate. This is a capacity test of accepted input, not a claim that ordinary bots send padded updates.

Before each stress tick the harness waits outside the simulation for every real socket reader to publish the same complete burst. Decode cost is relocated to those receive workers; it is not removed from total server CPU, and receive-to-apply latency still includes worker completion and the next tick boundary. State-dependent application can still be expensive: the retained P646 bound counts packets, with at most 256 messages per packet. This change does not promise a hard wall-time bound for arbitrary valid state-changing commands.

Full local traces, observations, fmid/peer attribution, histogram bins and slow-tick lines are retained in the measurement evidence linked above. The helper reproduces the experiment:

```bash
NUGET_HTTP_CACHE_PATH=/tmp/p673-nuget-http dotnet build -m:8

dotnet run --project tools/receive-proof --no-build -- off /tmp/p673-off 390
dotnet run --project tools/receive-proof --no-build -- on /tmp/p673-on 390
```

The helper connects only to its own ephemeral loopback server. Its `observations.json` records every burst, roster counts, packet counts, and stage failures. It fails on incomplete seating/bursts or a simulation-stage exception.

## Regression and suite verification

Build: `NUGET_HTTP_CACHE_PATH=/tmp/p673-nuget-http dotnet build -m:8 -v:q` — **0 warnings, 0 errors**. The temporary HTTP-cache location avoids a read-only default NuGet cache; no audit checks were disabled.

**51 new/affected methods × 3 separate real VSTest invocations = 153 processes / 183 case runs, all passed.** Each method ran alone, with theory rows executed by the real xUnit runner. This includes 30 case runs for the ten new cases. No substitute/reflection test runner was used.

The required unfiltered suites ran sequentially after the build, with `ALLEGIANCE_RECEIVE_DECOUPLE=1` and `ALLEGIANCE_LIVE_P660_SHOWCASE=1`:

| Suite | Passed | Failed in full run | Skipped | Duration |
| --- | ---: | ---: | ---: | ---: |
| Core | 4,312 | 0 | 0 | 322 s |
| ClientCore | 932 | 2 | 0 | 296 s |
| MonoGame | 2,125 | 0 | 3 existing optional-pack skips | 264 s |

The opt-in **local 15v15 MCP showcase passed** in 21.65 s, including match rotation, bot reseating and finale rearming. All local/live tests ran; the only skips were the existing unavailable private-pack cases.

After **all three suites** finished, both ClientCore failures passed **3/3 exact-method isolated reruns**, so they are load flakes under the brief's rule:

- `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`: `Lost` was observed before the test's event-callback flag.
- `RetriedConnectLiveTests.RetriedConnect_StartsFromEmptyWorld_AndReseatsWithNewAssignment`: the test enumerated dictionary keys while the receive thread modified the dictionary. This is the existing quarantined method in `RetryCleanupTests.cs`.

No assertion, deadline, product behavior or quarantine tag was changed for these cases. They are recorded in [TEST_FLAKES](../TEST_FLAKES.md). [P673_RESULTS.json](P673_RESULTS.json) preserves all focused outcomes, suite counts, full-run errors and all six successful isolated reruns.

The new tests cover actual CRC and codec errors (including hostile reload/missile var slices), a valid message after invalid messages, decoded-object reuse, simulation-thread application, fair budget rotation, pending-decode lifecycle ordering, late callbacks, the complete 100-tick histogram, and exact off/on simulation and outbound-byte equality. The simulation comparison exercises 20 boundaries with reverse peer arrival order, steering, conflicting donations, negative gifts, invalid purchases and part requests; it compares position/velocity/orientation bits, controls, wallets, hulls, parts and wire output. It also asserts that enqueuing input cannot mutate the world before a boundary.

The real-socket regression runs both transport modes, 32 peers and 96 aligned 64 KiB packets per mode: off/on commands are identical, with **zero simulation-side packet decodes when enabled**. It records timings without a fragile hardware-dependent speed assertion. The 390-peer helper supplies the crowded timing evidence.

P653 economy/loadout integrity cases, P654 hostile decoder/dispatch cases, duplicate-logon, P646 bounded drain, inbound flow-control and live flood regressions are in the isolation matrix. Boot-based focused cases run with `ALLEGIANCE_RECEIVE_DECOUPLE=1`; socket-free P653 fixtures continue to exercise the common synchronous dispatcher. No `P655`-named test/report or `docs/architecture/SERVER_PIPELINE.md` exists in this base snapshot; the shipped hostile-input protections and the P653/P654 suites were inspected and exercised directly.

## Native rules retained / classification

Native paths below are relative to **`~/Development/Allegiance/src/`**. Scheduling, worker ownership, opt-in metrics and the feature flag are modern port infrastructure; they do not introduce new native gameplay rules.

| Rule | Native file:line | Classification / treatment |
| --- | --- | --- |
| Packet CRC and never-zero checksum | `_Utility/CRC.cpp:162-176`; `_Utility/Messages.cpp:573-580`, `:933-945` | Native wire design. Existing codec unchanged, now called on the receive worker. |
| Pilot update cookie and in-cluster gates | `FedSrv/FedSrv.CPP:4851-4864`; `FedSrv/fsship.cpp:1173-1200` | Native authority design. Still checked against live sim state at application. |
| Ping echo with current server time | `FedSrv/FedSrv.CPP:4897-4911`; client cadence `clintlib/clintlib.cpp:2050-2085` | Native behavior; existing port reply throttle remains on the sim thread. |
| One initial logon, not repeated seating | `FedSrv/FedSrv.CPP:4832` | Native invariant; existing port duplicate-logon rejection/idempotent pending path remains. |
| Donation sentinel and amount handling | `FedSrv/FedSrv.CPP:5523-5534`; player donation `FedSrv/FedSrv.CPP:7213-7262` | Existing P653 fixes remain: native's pointer-vs-amount check is a bug, not restored. Wallet/leader/side checks still run on the sim thread. |
| Docked/alive/unboarded purchasing gates | `FedSrv/FedSrv.CPP:6153-6172` | Native gameplay authority. Existing P653 validation remains at application. |
| Reload and missile variable data | `Inc/Messages.h:843-846`; `Inc/Messages.h:547-557` | Native wire shapes; existing hostile-slice bounds checks remain in the codecs used by both modes. |

No native source, roster code, protocol layout or security threshold was changed.
