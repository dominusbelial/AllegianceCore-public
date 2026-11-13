# P1002 — gunfire fixture re-baseline

The batching hypothesis is confirmed for both P992 tests and disproved for P990.
P992 now measures delivered records in both wire modes and simulates inside the
recorded tick so ON exercises real batches. P990 fails with batching OFF too;
its test, 30 ns/call bound and 3% bound remain unchanged as the brief requires.
No production source, wire format, default, HUD, P697 or P993 pin changed.

Base: `61a50173`. All measurements use the external artwork corpus, Debug
assemblies, separate test processes and the existing
`ALLEGIANCE_BROADCAST_GUNFIRE_BATCH=1` / `=0` override. Each named test ran three
times alone in each mode against the **unmodified base binaries**. Fixture
edits were compiled only after all 18 base invocations completed. No commits
or pushes.
Raw logs/TRX and the repeat driver remain in ignored `.scratch/p1002/`.
Runtime: .NET SDK **10.0.404**, .NET runtime **10.0.15**, x86-64 Linux container;
host CPU reports AMD Ryzen 9 9955HX. Fixture wall times include publication
capture/decoding overhead and are not a production performance comparison.

## Base reproduction

| Test | ON, Passed / Failed / Skipped | OFF, Passed / Failed / Skipped | Exact first failing assertion, all three ON runs |
|---|---:|---:|---|
| `P992GunfireTrafficTests.Gunfire_fixture_measurement_from_environment` | 0 / 3 / 0 | 3 / 0 / 0 | `Assert.Equal(firing > 0, r.Msgs12 > 0)`: expected `True`, actual `False` |
| `P992GunfireTrafficTests.Loopback_showcase_measures_gunfire_traffic_and_the_audience_radius` | 0 / 3 / 0 | 3 / 0 / 0 | `Assert.Equal(shotDeliveries, firingRun.Msgs12)`: expected `241668`, actual `0` |
| `P990BroadcastLaneFixtureTests.Mixed_lane_overhead_stays_within_30ns_per_call_and_three_percent_of_the_p95_tick` | 0 / 3 / 0 | 0 / 3 / 0 | `Assert.True(perCallNs <= 30)`: actual `31.6`, `32.2`, `32.4` ns/call |

Base totals: **Passed 6 / Failed 12 / Skipped 0**. P992 base assertion locations:
`61a50173:tests/Allegiance.Core.Tests/P992GunfireTrafficTests.cs:544,488`.
P990's first assertion is
`tests/Allegiance.Core.Tests/P990BroadcastLaneTelemetryTests.cs:539`.

### P992 environment measurement — re-shape

All three base runs in each mode produced 1,584 simulated shots, 228,096
shot/recipient pairs and 207,360 hit/recipient pairs. ON reported zero messages
and bytes for IDs 12 and 213 and a bogus zero hit count. OFF reported 228,096
ID-12 messages / 13,229,568 bytes, 207,360 ID-213 messages / 9,953,280 bytes and
1,440 hits. The simulation and audiences did not disappear; the old hit counter
was itself derived from ID-213 frame counts.

`src/Allegiance.Server/Server.Combat.cs:60-68,152-161` substitutes compact records
for legacy frames. `src/Allegiance.Server/Server.Lifecycle.cs:1054` intercepts the
audience token; `src/Allegiance.Server/Server.GunfireBatch.cs:133-169` builds
ID-221 packets. Telemetry counts the outer message header, not embedded records
(`src/Allegiance.Server/BroadcastTelemetry.cs:610-637`). Therefore checking for
ID 12 is the wrong measurement with batching ON.

The fixture now expands published batches, counts shot and hit records, and
checks their totals against the independently observed simulation event pairs.
Actual frame counts/bytes remain separately visible; ID-221 capture totals must
exactly equal ID-221 telemetry. Its original firing/non-firing check remains,
now on delivered shot records. The existing environment override covers both
modes without changing process-global environment inside a test.

### P992 rig-shaped showcase and radius — re-shape

All three base runs per mode produced 1,764 shots, 241,668 shot/recipient pairs,
197,280 hit/recipient pairs, and 149,940 shot pairs within 5,900 units
(**62.0438%**). OFF measured 1,440 hits and exactly 241,668 ID-12 frames /
14,016,744 bytes plus 197,280 ID-213 frames / 9,469,440 bytes. ON's ID-12/213
counters and derived hit count were all zero. No audience-radius regression was
observed; that measurement is based on simulation geometry, independently of
packet encoding (`tests/Allegiance.Core.Tests/P992GunfireTrafficTests.cs:381-409`).

There was a second fixture defect: its simulation update preceded the recorded
tick. `src/Allegiance.Server/Server.GunfireBatch.cs:146` immediately flushes a
single record outside an open tick. The old ON fixture consequently exercised
the admin-send fallback rather than the actual batching workload. Production
opens the snapshot before simulation and seals gunfire before pose projection
(`src/Allegiance.Server/Server.Broadcast.cs:120-158`);
`src/Allegiance.Server/Server.BroadcastSnapshot.cs:151-154` opens the batch.
The existing `TestProjectRecordedTick(..., surge: ...)` seam provides precisely
that scope (`src/Allegiance.Server/Server.BroadcastTestSeams.cs:256-269`). The
fixture now performs `mission.Update` in that callback.

The base ON rig fixture offered 80,595,738 total bytes versus OFF's 81,670,914
(quiet arm 54,521,370 in both modes). The 1,075,176-byte difference agrees
exactly with replacing each 58-byte shot by a 56-byte singleton batch and each
48-byte hit by a 45-byte singleton batch:
`241668 * 2 + 197280 * 3`. This independently confirms the fallback diagnosis.

Delivered records must still equal shots/hits times sector fan-out and the
independent event-pair counts. The quiet arm must send no gunfire in any mode.
ON must combine multiple records, send fewer frames than records, and save
bytes against legacy delivery. OFF retains the exact 58-byte shot and 48-byte
hit frame identities. For this stock weapon the ON byte identity is
`20 * batchFrames + 36 * deliveredShots + 25 * deliveredHits`:
16-byte batch header + four-byte CRC, wide compact projectile record, compact
hit record (`src/Allegiance.Core/Wire/GunfireBatch.cs:16,49-116,141`). This is an
exact layout check, not a relaxed performance bound.

The original **2%** shot/hit tolerances, **60–64%** near-radius interval,
**13.4%** stock dependency share (one decimal place), and **1%** reconciliation
tolerance are unchanged. The 13.4% check uses legacy-equivalent gunfire bytes,
so it still pins the historical calculator's inputs. Actual firing-minus-quiet
bytes reconcile against the active mode's gunfire bytes plus dependency bytes.
Compression cannot preserve a percentage whose denominator was the legacy wire
cost. No statistical re-bound or safety-margin change is involved.

All three final ON repetitions measured: 241,668 delivered shots + 197,280 delivered
hits, 36,579 ID-221 frames / 14,363,628 bytes, maximum 12 records/batch. Gunfire
bytes fall **38.84%** from the legacy 23,486,184 bytes. Dependency remains
2,959,200 + 335,376 + 345,744 = **3,640,320 bytes**. Actual firing delta is
18,026,988 bytes versus 18,003,948 gunfire-plus-dependency bytes (23,040 bytes
of other traffic, within the original 1%). This fixture observation agrees in
direction with the accepted rig result; it is not a new rig measurement.

### P990 mixed-lane overhead — not a batching re-baseline

| Mode / run | Tick p95, ms | Accounting ms/tick | ns/call (bound 30) | Share of p95 (bound 3%) |
|---|---:|---:|---:|---:|
| ON 1 | 4.661 | 0.2229 | 31.6 | 4.783% |
| ON 2 | 5.379 | 0.2272 | 32.2 | 4.224% |
| ON 3 | 4.800 | 0.2284 | 32.4 | 4.759% |
| OFF 1 | 4.542 | 0.2380 | 33.7 | 5.240% |
| OFF 2 | 4.498 | 0.2345 | 33.2 | 5.215% |
| OFF 3 | 4.760 | 0.2280 | 32.3 | 4.791% |

Both bounds are exceeded, although execution stops at the first assertion.
ON median is **32.2 ns/call**, range 31.6–32.4; OFF median is **33.2 ns/call**,
range 32.3–33.7. OFF does not cure the failure.

The timed `Workload` is built from 24 `ShipStatusMessage` frames, eight keyed
exports, a heavy/light split and 16 pose stamps per peer; it never builds,
sends, decodes, or accounts a gunfire batch
(`tests/Allegiance.Core.Tests/P990BroadcastLaneTelemetryTests.cs:452-477`). Its
49 calls/peer and 144 peers are fixed at `:518`. The preliminary tick p95
fixture (`:695-740`) seeds unarmed ships and projects poses without a mission
simulation update; gunfire builders are empty. Batching therefore neither
changes the measured per-call path nor reduces its workload. The ordered parse
is still the largest arm (ON 0.1390–0.1407 ms; OFF 0.1407–0.1454 ms).

The original comparison in `docs/reports/BROADCAST_SLICE_D_P990.md:253-281`
measured 20.8–22.4 ns/call and derived the 30 ns / 3% bounds on different
hardware. `BroadcastTelemetry.cs` is byte-identical to its P990 introduction
(`015019bf`); this investigation does not establish a new telemetry code
regression. It establishes a pre-existing, batching-independent performance
gate failure on this lane. A faster pose projection denominator alone would
also make the percentage gate stricter without slowing the accounting path.

One additional OFF diagnosis with `DOTNET_TieredCompilation=0` passed the
per-call assertion (displayed 30.0 ns/call) but failed the share assertion:
0.2116 ms / 4.987 ms = 4.242%, against 3%. This does not establish a JIT-only
cause and is excluded from the normal-environment gate counts.

**Decision:** leave P990 and both bounds untouched. Investigate runtime/JIT
warm-up and host/build comparability separately, with repeated stable blocks
and a real projection OFF/ON diagnostic comparison before proposing any
performance change. The accepted batching egress savings cannot justify
loosening a benchmark that does not exercise batching. No production fix is
small and demonstrably correct from this evidence.

## Validation

The report and base evidence were written before the final gate.

| Final gate | Passed / Failed / Skipped or build result |
|---|---|
| Solution build | **0 warnings / 0 errors** (11.75 s) |
| All tool projects | **135/135 built, zero warnings/errors**, `tools/build-tools.sh --drop-bins` |
| P992 environment test, ON three times alone | **3 / 0 / 0** |
| P992 showcase/radius test, ON three times alone | **3 / 0 / 0** |
| P992 environment test, OFF three times alone | **3 / 0 / 0** |
| P992 showcase/radius test, OFF three times alone | **3 / 0 / 0** |
| Unchanged P990, ON three times alone | **0 / 3 / 0** |
| Unchanged P990, OFF three times alone | **0 / 3 / 0** |
| One full Fast suite | **FAIL: Passed 14,125 / Failed 4 / Skipped 20**, 523.85 s; two-hour timeout |
| Glossary paths | PASS, 795 distinct paths |
| Whitespace check | PASS |

Final isolated totals: **Passed 12 / Failed 6 / Skipped 0**. The six failures
are the unchanged P990 exception, not a green performance gate. Final P990 ON
costs: 32.1 / 32.7 / 31.6 ns/call, shares 4.505 / 4.323 / 4.834%; OFF:
31.8 / 31.6 / 32.8 ns/call, shares 4.816 / 4.513 / 4.711%. The results again
disprove a batching-specific cause. No bounds were raised, so a ten-run
statistical re-derivation is neither used nor claimed.

The 144-peer environment fixture also repeats identically: ON delivers
228,096 shots and 207,360 hits in 27,216 batches / 13,939,776 bytes
(maximum 16 records/batch); OFF delivers the same counts in legacy frames
(13,229,568 + 9,953,280 bytes). The 411-peer OFF fixture remains at the
historical 14,016,744 + 9,469,440 bytes.

The Fast runner covered all **14,149** discovered Fast cases, plus **9/9**
Python infrastructure checks (0.034 s). Both quarantine lanes passed (2
ClientCore and 17 Core cases); no quarantine retry was needed. The hardware
calibration factor was 1.149708, yielding an **11.497082 s** Fast time limit.
Four executions exceeded it; the runner remains FAIL even where assertions
passed. Isolated follow-up results are recorded below.

| Fast project, including quarantine | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Lobby | 144 | 0 | 0 |
| Update | 55 | 0 | 0 |
| MonoGame | 5,127 | 0 | 17 |
| ClientCore | 2,442 | 2 | 0 |
| Core | 6,357 | 2 | 3 |
| **Total** | **14,125** | **4** | **20** |

The modified P992 measurement methods are Live tests and P990 is Perf; none
executes in Fast. Their offline calculator pins passed in Fast. All production
code and every unrelated failing test are unchanged, so these Fast failures
have no execution path through the fixture edits.

### Fast failure and timing follow-up

Each failed/slow selection ran alone three times after the full suite, with the
same artwork and default ON mode. P660 ran its complete four-case method each
time, including the failing disabled/off case; all other selections ran exactly
one case. Durations are TRX test durations, not process elapsed times.

| Case | Full Fast result | Three isolated results |
|---|---|---|
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` (disabled/off) | Failed: 14.900000000054206 against [14.94, 15.06] at `P660NextMatchLiveTests.cs:134` | **4/0/0; 4/0/0; 4/0/0** (Passed/Failed/Skipped); recurring countdown receipt issue |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | Failed: full lock 1.000 but no partial tip; 37.849 s, also over timing limit | **fail / fail / fail**, 38.652 / 38.593 / 38.587 s; known OPEN partial-tip receipt issue |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` | Failed clearance at `NpcDodgeLiveTests.cs:258`, minRockDist=70.2 | **pass / pass / fail**, 5.874 / 5.808 / 5.775 s; failed repeat minRockDist=68.6, remains OPEN |
| `DropPartTests.DropPart_drops_weapon_sectorwide_with_treasure_then_empty_pack_skips` | `Assert.Empty` at `DropPartTests.cs:340`: subject retained one weapon treasure (TreasureId 3, Amount 0) after empty-pack phase | **pass / pass / pass**, 0.453 / 0.453 / 0.451 s |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` (civ 35) | Passed, slow at 11.725 s | **pass / pass / pass**, 3.476 / 3.286 / 3.286 s |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` (civ 25) | Passed, slow at 11.637 s | **pass / pass / pass**, 3.032 / 3.004 / 2.987 s |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` (civ 5) | Passed, slow at 13.456 s | **pass / pass / pass**, 2.916 / 2.962 / 2.971 s |

Follow-up totals: **Passed 26 / Failed 4 / Skipped 0**, 21 process invocations.
The three passing slow cases are below the original 11.497082 s ceiling in all
isolated runs. P660's outcomes do not erase its original failure. The NPC
failure repeats alone and is not classified as load-only. Missile lock remains
open, consistent with the prior P933/P935/P966/P993/P997 ledger entries.

DropPart's subject-only residual is consistent with a receive/reset race: phase
one waits for observer/enemy treasure, but not the subject's own treasure
(`tests/Allegiance.Core.Tests/DropPartTests.cs:254-261`), then clears every
receive list before phase two (`:325`). A late weapon echo can therefore arrive
in the subject's cleared list (`:340`). This is a diagnosis to investigate,
not a claimed production fix or an empty-pack behavior change. All four
failures and all four timing offenders are recorded in `docs/TEST_FLAKES.md`.
No unrelated assertion or timing bound was changed.

An initial P660 display-name filter selected two cases instead of one (both
passed); that attempt is excluded from the required repeat counts above and
retained in scratch. The complete method was subsequently run three times.
Additional diagnostics outside the required cohorts: the corrected ON smoke
check passed 1/1; the JIT-tiering OFF probe failed 1/1 as described above.

Overall gate: builds and all changed P992 tests **PASS**; unchanged P990 and the
full Fast suite **FAIL** with the qualified evidence above. This report does
not claim a fully green gate. Changes remain uncommitted in the working tree;
the original brief is retained only in ignored scratch storage.

Initial build hit a read-only NuGet HTTP audit cache. Copying the existing cache
to task scratch and setting `NUGET_HTTP_CACHE_PATH` fixed it; NuGet auditing
remains enabled. Build commands use `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`. `tools/build-tools.sh` uses a scratch DOTNET
wrapper to apply those same options without changing the build script.

## Native intent

Native clients simulate gunfire locally (`Igc/weaponIGC.cpp:142-318`);
`PROJECTILE_INSTANCE` is declared CS (`Inc/Messages.h:131-135`). This task ports
no new native rule. The native-shaped OFF wire, stock calculator, sector
fan-out, hit attribution and draw-radius intent remain pinned. No parity CSV
change is needed for test-only measurement corrections.

## Modern approach

Separate simulated events, delivered records and transport frames. Expand
ID-221 records using the production decoder, compare them with independent
simulation events, and reconcile actual bytes in the active mode. Preserve
historical stock arithmetic explicitly. Reject re-baselining where the OFF
control disproves the proposed cause.
