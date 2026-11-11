# P994 — batched gunfire

Implementation and validation record, opened before implementation and completed
around the gate. Work stays on `codex/p994` in the working tree: no commits,
pushes, public-host access or remote machine runs.

The default-off `broadcast.gunfireBatch` switch retains legacy 12/213 frames when
off. When on, selected deliveries accumulate in tick order, using the existing
fire audiences and source-pose dependency. Compact records retain exact identity,
lifespan and projectile spawn position. Spawn position deliberately stays float32:
even tiny rounding can reverse an own-echo decision exactly at the 150-unit
boundary. Inherited velocity is encoded independently of forward direction.

The measured 411-peer fixture reduces gunfire bytes **39.31%**, frames **91.67%**,
and total offered egress **15.87%**, with identical shot and hit counts. This
implements the conservative option: 36-byte ordinary projectile records and
25-byte hit records, rather than P992's speculative 20/22-byte design. Exact
spawn coordinates, independent inherited velocity, exact creation lifespan and
typed probe identity account for the extra projectile bytes.

## Layout and error bounds

`GUNFIRE_BATCH = 221` was free in the existing wire constants/catalog and
`docs/parity/fedmsg-wiring.csv`; it follows the same port-extension convention as
213, above native's highest real message ID, 200. It is **not** a native fmid or
binary-compatible native message. Every integer is little endian. The new
`docs/parity/p994-gunfire.csv` tracks the native inspirations and lifespan rule.

| Frame offset | Bytes | Meaning |
|---|---:|---|
| 0 | 2 | `cbmsg`, including header, excluding packet CRC |
| 2 | 2 | fmid = 221 |
| 4 | 2 | variable-item `ib`, always 16 |
| 6 | 2 | variable-item `cb`, exactly `cbmsg - 16` |
| 8 | 4 | emission tick, uint32; exact legacy 12 TimeCreate |
| 12 | 2 | ordered record count |
| 14 | 1 | version = 1 |
| 15 | 1 | reserved = 0 |
| 16 | variable | interleaved tagged projectile/hit records in event order |
| `cbmsg` | 4 | ordinary FedPacket CRC |

Projectile records have these layouts:

| Offset | Bytes | Field |
|---|---:|---|
| 0 | 1 | tag: 0 = narrow velocity, 1 = wide velocity, 2 = exact fallback |
| 1 | 2 | exact projectile type ID |
| 3 | 2 | exact launcher ID |
| 5 | 2 | exact launcher object type; ship/probe IDs remain type-scoped |
| 7 | 12 | exact float32 spawn position XYZ |
| 19 | 4 / 12 | octahedral signed-int16 XY forward / exact float32 XYZ |
| 23 / 31 | 6 / 9 / 12 | XYZ velocity: int16 / signed-int24 / float32 |
| 29 / 32 / 43 | 4 | exact float32 initial lifespan |
| total | **33 / 36 / 47** | tags 0 / 1 / 2 |

Narrow and wide velocity both use **1/256 unit/s** steps: component error at most
`1/512 = 0.001953125 unit/s`, vector error at most `sqrt(3)/512 = 0.003383`.
Narrow representable components span -128 through 127.99609375; wide spans -32768
through 32767.99609375. Forward must already be unit length to within 1e-6 in
squared norm; unusual vectors, velocity overflow/non-finite values, and lifespans
outside [0,16] use exact fallback. Velocities between the renderer's 0.001-unit/s
forward-fallback threshold and 64 units/s also use exact fallback: normalizing
an almost-zero quantized vector could otherwise rotate the visible bolt. At
64 units/s and above, normalized-velocity heading error stays below 0.00014;
below the renderer threshold, quantization retains its forward-vector branch.
Thus compact trajectory drift is bounded by
**0.05413 units over the entire lifetime**, without assuming velocity is parallel
to forward. The lifespan itself is never quantized or inferred from type.

| Hit offset | Bytes | Field |
|---|---:|---|
| 0 | 1 | tag: 3 = compact, 4 = exact fallback |
| 1 | 2 | exact target ID |
| 3 | 2 | exact cluster ID |
| 5 | 2 | exact source ID, including -1 |
| 7 | 1 | exact shield-absorbed flag, 0/1 |
| 8 | 4 | exact float32 target radius |
| 12 | 9 / 12 | signed-int24 position XYZ / exact float32 XYZ |
| 21 / 24 | 4 / 12 | octahedral normal / exact float32 normal |
| total | **25 / 36** | tags 3 / 4 |

Compact hit positions use the **sector's fixed coordinate origin**, with 1/64
unit steps, component error at most **1/128 = 0.0078125 units**, vector error at
most **0.01354 units**. Representable coordinates span -131072 through
131071.984375; rounded values outside the signed-int24 domain, or non-unit
normals, take the exact fallback. No saturation silently moves an effect.

Directions use a two-coordinate octahedral map, round-to-nearest signed int16,
then unfold and normalize. Each square-coordinate error is at most 1/65534.
Unfolded error is bounded by sqrt(6)/65534; the unfolded vector has norm at least
1/sqrt(3). Normalization gives a conservative vector-error bound **0.00014**,
including float arithmetic and the input unit tolerance (about 0.0081 degrees).
The tests cover both poles, axes/folds and 10,000 seeded sphere samples.

At the proof's 250-unit scene depth, 540-pixel height and 50-degree vertical FOV,
one world unit projects to about 2.32 pixels. Worst compact lifetime drift is
0.126 pixels; a hit's maximum position error is 0.032 pixels. Maximum 16-unit bolt
heading displacement is 0.00224 units. Native spark normal error contributes at
most 0.000056 times target radius over its two-second life, 0.0112% of bit size.
The close-ups deliberately magnify the effects; pixel comparisons below retain
their small edge differences rather than claiming bit-identical rendering.

All identity, radius, lifespan, tick and projectile spawn fields have **zero
quantization error**. This also preserves every spatial own-echo decision at the
150-unit boundary; the tests include the immediately adjacent float32 values.
As with any tick batching, transport receipt time can move within the tick;
fixed-clock equivalence tests isolate codec fidelity from network scheduling.

## Consumers and changes

| Change | File:line |
|---|---|
| Versioned frame, allocation-free record encoders, strict decoder and exact escapes | `src/Allegiance.Core/Wire/GunfireBatch.cs:13` |
| Store the original creation lifespan without changing expiry math | `src/Allegiance.Core/Sim/Projectile.cs:42` |
| Default-off JSON setting | `src/Allegiance.Server/ServerConfig.cs:880` |
| Strict environment override, boot receipt, reusable per-peer buffers, shared complete packets | `src/Allegiance.Server/Server.GunfireBatch.cs:104`, `:120`, `:149` |
| Lifecycle ordering boundaries and valid legacy packet tokens for P993 inspection | `src/Allegiance.Server/Server.GunfireBatch.cs:33`, `:65` |
| Preserve the audience helpers; provide the same ship/probe events through an opaque encoder token | `src/Allegiance.Server/Server.Combat.cs:29`, `:98`, `:152` |
| Resolve flag at boot; copy selected record deliveries at the central byte-array send | `src/Allegiance.Server/Server.Lifecycle.cs:188`, `:1038` |
| Begin/seal accumulation even with snapshots disabled | `src/Allegiance.Server/Server.BroadcastSnapshot.cs:153`, `:171` |
| Inspect deferred packet metadata for boundaries without forcing encoding | `src/Allegiance.Server/BroadcastSnapshot.cs:25`, `:38` |
| Seal immediately after the sim step, before rotation and ordinary pose suffix | `src/Allegiance.Server/Server.Broadcast.cs:154` |
| Production-session decoder | `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1929` |
| Expand through existing world Apply handlers | `src/Allegiance.ClientCore/State/ClientWorld.cs:2574` |
| Raw observer Bot retains decoded projectile instances when requested | `src/Allegiance.Bot/Bot.cs:2039` |
| P990 `[bb-other]` lane name; automatic frame accounting still includes the whole frame and CRC | `src/Allegiance.Server/Server.BroadcastTelemetry.cs:614` |
| Packet dump catalog/formatter | `src/Allegiance.Core/Wire/FedMessageDumper.cs:57`, `:195`, `:211` |
| Allocation/timing/sharing test seams and production-like effect seal in replay | `src/Allegiance.Server/Server.BroadcastTestSeams.cs:212` |
| Compose environment forwarding and operator instructions | `docker-compose.yml:116`, `docker/README.md:373` |
| Domain index | `GLOSSARY.md:335` |
| Central frame registry and native-intent ledger | `docs/parity/fedmsg-wiring.csv:138`, `docs/parity/p994-gunfire.csv:1` |
| Client dispatch coverage registration, valid nonempty batch fixture, and 14/114 audited registry counts | `tests/Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests.cs:93`, `:146`, `:285` |
| Seeded real-socket showcase with nonzero hits and kills | `tests/Allegiance.ClientCore.Tests/P700ShowcaseLiveTests.cs:24` |
| Deterministic production-renderer proof | `tools/p994-gunfire-proof/Program.cs:15`, `tools/p994-gunfire-proof/p994-gunfire-proof.csproj:1` |
| Correct a pre-existing dimensional error in the shield proof's normalized-normal dot-product check | `tools/shieldflare-proof/Program.cs:417` |

`Server.FireAudience.cs` is unchanged. The existing helper still makes every
visibility/audience decision and sends every required export/status/pose. Only
its selected effect token is intercepted, immediately copied into a recipient's
reused buffer. Records are encoded **once per simulation event**, not per peer.
The helper's reusable token is a valid legacy 12/213 frame, including position,
identity and CRC, so P993 can inspect the same packet contract when selecting
its audience. It is not transmitted. Constructing it allocates no per-round
object/array; the warmed append test includes token encoding and CRC work.
Equal complete histories share the immutable frame and CRC, with full byte
comparison after hashing; eight identical recipient histories are tested to
require one encoding. Builders retain capacity, prune departed peers and never
retain live projectile objects. Immutable packet arrays outlive their builders,
so asynchronous publication cannot observe next-tick mutation.

The tick is frozen at accumulation start because the server increments TickIndex
before the broadcast suffix. Batches use key zero, guaranteed FIFO, and remain
ordered between snapshots. Source dependencies precede the batch. Intra-tick
gunfire order is retained, with the ordinary seal after sim as requested.
**Lifecycle boundaries are an intentional exception to one frame per peer per
tick:** pending effects flush before sector/reset, death, departure and side
changes. Otherwise a pre-warp round would be applied after SET_CLUSTER cleared
the old view, or hit attribution would read a changed side. Both raw and deferred
packet send paths preserve this ordering; the reseat regression compares the
final projectile, impact and sound buffers to legacy. Native view transition:
`clintlib/appmsg.cpp:1009-1087`, re-read for this task. This preserves the existing
thin client's sector-effect clearing; it introduces no native simulation rule.

Frames larger than ushort `cbmsg` also split **only between records**, tested
with 2,000 shots plus 2,000 hits to eight peers. Ordinary measured combat ticks
use one frame per peer. Direct admin/test emissions outside a tick seal
immediately. A strict one-frame guarantee across world-reset messages would
require wrapping/reordering those other messages; this implementation preserves
their legacy ordering instead.

The legacy OFF branch still uses the original FromProjectile/Encode/Pack calls,
including its historical type-default lifespan. The ON branch carries
`InitialLifespan`, fixing the missing visual override for blast interception and
energy lifespan modifiers without changing legacy bytes or authoritative sim.
Native lifespan evidence: `Igc/weaponIGC.cpp:273-288`, re-read for this task.

| Consumer | How it receives the same input |
|---|---|
| Human client, including both HUD styles | ConnectionSession decodes 221; ClientWorld expands records through the existing 12/213 handlers. Projectiles, sparks, shield flares, hit sounds and rumble retain their existing consumers. |
| Own-shot echo / local tracer | Exact type, typed launcher and spawn coordinates reach the same predicate; foreign/probe rounds retain their classification. |
| MCP shots and hit metrics | Uses ConnectionSession. The real `SampleCombatStats` sees the same own projectile count and exact source/target/cluster/shield tuples. |
| RecentAttackers, asset/miner and station attack alerts | No new bot logic: production sampling is tested against legacy and batched session receives, with both CountOnlyProjectiles settings. Every resulting dictionary and hit counter matches. |
| Raw Allegiance.Bot observer | Expands projectile records only when RetainRawCombatUpdates is enabled, matching legacy observer retention. Legacy 12 still decodes. |
| hullhit-proof, shieldflare-proof, vizfight and other rendering observers | All receive through ConnectionSession; no separate hand-written 12/213 parser was found in tools. Both old decoders remain available. |
| fedmsgdump | Names 221 and uses GunfireBatch.Decode to report tick, records, projectile and hit counts. |

Tests: `tests/Allegiance.Core.Tests/P994GunfireBatchTests.cs`,
`tests/Allegiance.Core.Tests/P994GunfireBatchLiveTests.cs`, and
`tests/Allegiance.ClientCore.Tests/P994BotBatchTests.cs`. OFF gunfire stream SHA256:
`75E96CC95BE876D9BA791D1F9AAA566AB9EE2CBBA2313C066E7496118B45AF33`.
The full per-recipient OFF stream also matches serial versus parallel. Record
building and 1,000 warmed recipient appends each measure **zero allocated bytes**;
packet allocation is per distinct completed audience history, not per round.
The old audience helper's existing pose/dependency allocations remain outside
the new batch builder.

## Local measurements and captures

The P992-style fixture boots the real local server, seeds **411 peers across
three sectors**, arms 36 fighters on two sides, and drives real Mission.Update
plus the real broadcast suffix. It warms for 20 ticks, then measures 100 ticks
(5 sim seconds) at 20 Hz. Seed 994 drives mission dispersion. Spread uses
150-unit rank spacing; packed uses 15. Shooters are replenished each tick to
measure sustained fire, not attrition. The transport sink counts exact offered
bytes even though this large fixture seeds observer peers rather than opening
411 TCP clients. Real socket/bot proof is recorded separately below.

Baseline settings: immutableSnapshot, encodeOnce, parallelBuild, pool transport,
native ship update model, nativeGotoPlan and shipStatusChangeGate ON. The final
repeat cohort uses `DOTNET_gcServer=1`, confirmed by boot's server/concurrent GC
receipt. The normal P863 budget is unchanged. No rig was contacted. Categories
sum exactly to offered egress in every measurement; publisher failures are zero.

| Fixture, 100 ticks | OFF | ON |
|---|---:|---:|
| Spread: real shots / landed hits | 1,800 / 1,800 | 1,800 / 1,800 |
| Packed: real shots / landed hits | 1,800 / 1,800 | 1,800 / 1,800 |
| Gunfire frames, either fixture | 493,200 | 41,100 |
| Gunfire bytes, either fixture | 26,139,600 | 15,864,600 |
| Total offered bytes, either fixture | 64,758,318 | 54,483,318 |
| Gunfire byte reduction | — | **39.31%** |
| Total byte reduction | — | **15.87%** |

Timing/allocation medians from the final three repetitions
(`.scratch/p994/gate-core-{1,2,3}.log`):

| Metric | Spread OFF | Spread ON | Packed OFF | Packed ON |
|---|---:|---:|---:|---:|
| Full fixture tick p95, ms | 28.941 | 20.951 | 21.166 | 20.864 |
| `step:broadcastShipsUpdate` mean, ms | 5.948 | 5.219 | 5.257 | 5.321 |
| Whole-process allocated bytes/tick | 904,924 | 896,452 | 903,940 | 895,845 |
| Projection-thread allocated bytes/tick | 221,985 | 222,240 | 223,008 | 221,400 |

The tick includes simulation, effect building/sealing, projection and publisher
flush. Allocation includes all process threads; projection allocation is the
sum of instrumented broadcast stages. Compact encoding and complete-packet
sharing reduce total allocation by about 8 KB/tick here; the unchanged pose
projection dominates the remaining allocation. Timings are local workstation
observations, not claims about rig CPU improvement. Earlier exploratory runs
overlapped compilation and are excluded from this table. Logs/TRX remain local
under `.scratch/p994/`.

Six reviewed offscreen captures total **311,135 bytes**, below the 1 MB limit:

| Proof | OFF | ON |
|---|---|---|
| Classic HUD, rounds and effects | [Classic OFF](P994_assets/classic_off.png) | [Classic ON](P994_assets/classic_on.png) |
| Modern HUD, rounds and effects | [Modern OFF](P994_assets/modern_off.png) | [Modern ON](P994_assets/modern_on.png) |
| Magnified shield cap and hull debris | [Effects OFF](P994_assets/effects_off.png) | [Effects ON](P994_assets/effects_on.png) |

The proof feeds real encoded frames through ConnectionSession, checks four
projectiles/two impacts/two sound events, then draws production projectile,
native ship, hull-spark, shield-cap and HUD renderers with the same particle seed.
Excluding the intentional OFF/ON label, Classic changes 130/489,600 pixels with
maximum channel difference 4/255; Modern changes 93 pixels, maximum 1/255. The
magnified effects change 421 pixels (0.086%), mean absolute channel difference
0.00245/255; a few antialiased close-up edges differ more (maximum 120/255).
These are raw renders, not retouched media. The separate live hullhit proof
also received eight real impacts through the new decoder and passed.

The live shieldflare tool initially failed its pre-existing placement check:
it dotted a unit normal with a radius-length vector but compared against radius
**squared**, which is dimensionally impossible for the 165-unit hull. The task
corrects that proof calculation to radius, retaining its 0.999 angular threshold.
Its final OFF/ON result is recorded with the gate below.

## Gate

`local.env` is present and sourced for tests/proofs. Package restore uses only
the existing local NuGet cache, with auditing disabled for these offline builds;
no public host is contacted. Builds use `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`. All launched runs have timeout bounds.

| Check | Result |
|---|---|
| Solution build | PASS: **0 warnings / 0 errors**, including the final dispatch-coverage count fix |
| tools/build-tools.sh | PASS, final source: all 131 tool projects, zero warnings/errors |
| New Core tests, three isolated repetitions | PASS: **45 Passed / 0 Failed / 0 Skipped** each; 135 total |
| New production MCP decoder/sampler + seeded showcase tests, three isolated repetitions | PASS: **3 Passed / 0 Failed / 0 Skipped** each; nine total (`client-final-3`, `-4`, `-5`) |
| One complete Fast runner | Completed once, **13,985 Passed / 6 Failed / 3 Skipped**, 13,994 total in 582.7 s; nine infrastructure checks PASS. See qualifications below. |
| Never-parks, five OFF and five ON | PASS: **OFF 5/5, ON 5/5**, zero skips and zero parking violations |
| Real socket gunfire / kill proof | PASS, seed 994: shots/hits/kills **8,083/3,727/31**, **6,668/3,125/25**, **3,008/1,433/7**; 24/30 arrivals in 188.9/188.6/199.7 s |
| Raw Bot projectile receive, batch ON | PASS, one test; valid legacy observer entries from the new frame |
| P542 enemy receive/draw and own-echo de-dup, batch ON | PASS, one test with the opt-in proof enabled; one own round dropped, enemy received/drawn, zero dead/neverposed/stale source counts |
| Existing live hullhit-proof and shieldflare-proof | PASS, OFF and ON; shield cap placement check passes after the proof's units correction |
| Visual captures | PASS, both HUD styles and effects close-ups |
| Compose config | PASS |
| Glossary path check | PASS, 750 paths |
| P994 parity CSV validation | PASS, three rows (including lifecycle ordering) |

The Fast sweep was **not green**. One failure was task-owned: adding 221 to the
dispatch registry also required changing its audited 13/113 port/total counts to
14/114. That test-only correction is made; the complete three-test dispatch
coverage class then passed **3/0/0 three times**. The real decode/apply case for
221 had already passed during the sweep. The full suite was not repeated, in
accordance with the requested single sweep.

All other assertion failures and the timing offender were repeated alone in
three fresh processes and recorded in `docs/TEST_FLAKES.md`:

| Non-task case | Sweep | Three isolated rechecks |
|---|---|---|
| ProfileEditor owns Enter/Backspace | Default user-profile restoration is read-only in this sandbox | **3 pass**, using writable task-local `XDG_CONFIG_HOME`; no profile code changed |
| MissileLock partial/full tip receipt | lock 1.000, partial tip absent, 39.126 s | **3 fail**, 38.380/38.342/38.309 s; existing OPEN receipt family |
| LiveSideSelect silent auto-seat | 32.55 sim s versus [28,31] bound | **3 fail**; existing OPEN boundary/timing family |
| Non-miner idle NPC | travelled 83.531 units | **3 pass**, 2.383/2.313/2.300 s; existing load-sensitive movement family |
| Rejoined roster time | 8.04999924 versus upper bound 8 | **3 pass**, 0.610/0.561/0.638 s; existing stint-timing family |
| P777 NPC warp exit touch rate | Assertions pass, 10.408 s exceeds calibrated 10 s | **3 pass**, 9.131/9.186/9.170 s, all below the unchanged limit |

The three skips are the optional private Meshy pack's presence/catalogue checks;
artwork and native source are available. No new P994 test skipped. Raw receipts
are under `.scratch/p994/fast/`, `.scratch/p994/qualifications/` and
`.scratch/p994/neverparks/`.

The unchanged normal-home P700 never-parks test ran sequentially five times per
arm, using immutable snapshots, encode-once, parallel build, pooled TCP, native
ship updates, native GotoPlan, server GC and SHIP_STATUS gate ON. Only the gunfire
flag differs between arms. Every run met the 24/30 arrival condition and had
nonzero real hit metrics:

| Arm/run | Observation, s | Longest low-speed-with-target, s | Shots | Hits | Kills | Result |
|---|---:|---:|---:|---:|---:|---|
| OFF 1 | 284.6 | 1.914 | 5,298 | 2,094 | 8 | PASS |
| OFF 2 | 393.9 | 1.912 | 4,474 | 2,003 | 13 | PASS |
| OFF 3 | 339.5 | 1.921 | 3,548 | 1,139 | 9 | PASS |
| OFF 4 | 284.5 | 1.412 | 2,139 | 624 | 4 | PASS |
| OFF 5 | 199.7 | 3.306 | 7,799 | 3,754 | 25 | PASS |
| ON 1 | 227.7 | 0.301 | 1,015 | 415 | 3 | PASS |
| ON 2 | 241.5 | 0.000 | 238 | 97 | 0 | PASS |
| ON 3 | 173.2 | 1.906 | 6,516 | 3,319 | 24 | PASS |
| ON 4 | 169.8 | 3.009 | 6,206 | 2,829 | 21 | PASS |
| ON 5 | 170.7 | 1.919 | 4,278 | 2,168 | 15 | PASS |

Observed failure rates are **0/5 OFF versus 0/5 ON**; neither arm crossed the
unchanged five-second parking limit. These functional trials use the existing
unseeded fleet/match timing and do not establish a performance improvement from
their wall times. The separate seed-994 cohort above additionally requires and
observes nonzero kills in all three runs. All launched bounded runs have ended.

## Rig A/B metrics for Claude

Keep immutable snapshots, encode-once, parallel build, pooled transport, native
ship updates, native GotoPlan, P863 tick budget, server GC and the SHIP_STATUS
change gate enabled. Compare the gunfire switch alone, fixed seed and map, spread
and packed fights: bytes and frames by fmid, total transport egress, reconciled
`bb-*` categories, broadcastShipsUpdate time, full tick p50/p95/p99, allocation per
tick, GC counts/pause time, publisher backlog/failures, fire neverposed counts,
own/foreign projectile diagnostics, bot shots/hits/kills/TTK and attacker alerts.

## Native intent

Native array framing and compact spatial values motivate transport shaping;
the server remains authoritative for every collision and damage calculation.

## Modern approach

A port-only, opt-in batch codec preserves existing consumers through expansion
into legacy message objects, with exact fallback for values outside its compact
domain. Legacy decoding remains available for older servers.
