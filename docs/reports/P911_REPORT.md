# P911 — Practice Arena and Free Flight AI and combat waves

Implemented the arena site binding, shared native order policy, real mounted wave
recipes and combat-driven completion. No commits, pushes or remote hosts were used.
All changes remain in this worktree. **26 of the 30 scoped rows earn production
credit (115/131 points); four remain partial for the exact gaps below.**

## Production behavior and evidence

The dispatch base already called `IIgcSite.HandlePickDefaultOrder` from
`Ship.DecideWingmanDefaultOrder` (`src/Allegiance.Core/Sim/Ship.cs:7646`, native
`Igc/shipIGC.h:2438-2440`). P911 adds the missing production ServerHost callback
and binds it only from TrainingHost. The policy declines unless the mission is
training and the active lesson is Mission6 or Mission10. Ordinary multiplayer,
showcase and skirmish hosts retain a null callback. No background per-tick arena
scan or training allocation is installed on those hosts.

`TrainingHost.Arena.cs` selects visible live targets, preserves the native
asymmetric miner-distance comparison and >2-credit player gate, recruits an armed
wingman within 1000, shares nearby valid live plans, applies home/Enemy/Mars
restrictions, pursues occupied home or the player sector, and creates hidden
cluster/local patrol buoys. `Ship.GetNpcPlan` reads the actual plan, including a
navigation-created buoy that differs from an accepted target. Typed buoys now
resolve in the native navigator for training. Arena scripts now use the existing
DataBuoyIGC constants (`Igc/igc.h:553-554`: waypoint 0, cluster 1); their previous
numeric labels were reversed. The shared GotoKind mapping is unchanged. Typed
buoy resolution is guarded by Mission.IsTraining, so ordinary multiplayer,
showcase and skirmish retain their prior navigation and wire behavior. Enemy warp
revelation follows `mission6.cpp:392-395` /
`mission10.cpp:388-391`. The real-flight test crosses from 2082 to 2080 without
position nudges. Patrol obstacle parity remains partial as detailed below.

Arena ships bypass the old training position-nudge/control-clearing pass. Attack
and repair orders enter real NPC plans. P909 already mounted most drone equipment;
P911 fixes replacement of occupied negative-tech cargo slots, supplies exact
arena tech additions and five-round mounted/cargo Seeker 2 magazines, and uses
those real recipes through the respawn registry. `RemoveRespawn` removes matching
recipes and releases the creation action's deferred buoy hold. Kill counting now
uses the local IGC death hook (`training/trainingmission.cpp:515-524`), before
victim retirement. The former injectable `TrainingHost.RecordKillCredit` API and
the old injected-completion tests have been removed/replaced.

The Live firing-range tests generate real weapon shots, collisions, damage,
deaths and native respawn waves to exactly 49 credits. A shared test fixture
controls player/enemy positions and aim, and calls the existing `RepairRearm`
action when ammunition drops below five; this is **controlled combat evidence,
not unaided difficulty certification**. It does not inject damage, deaths or kill
credits and introduces no clock/damage multiplier. Fixed 50 ms `StepLight`
frames were already available in the port. Separate live repair evidence uses a
tech -2 scout's real mounted nanite and real projectiles to repair a bomber.
Connected production ClientTrainingSite/TrainingSession tests run both lessons in
Classic and Modern and verify exactly-once death delivery and the victory chat.
No HUD renderer, layout, input UI or media changed; no screenshots are claimed.

## Native comparison

A direct diff of native `Mission6::HandlePickDefaultOrder` (mission6.cpp:82-287)
and `Mission10::HandlePickDefaultOrder` (mission10.cpp:78-283) finds **no behavior
differences**. Every textual difference is: (1) the class qualifier in the method
signature; (2) the class name inside `debugf`; (3) trailing whitespace on the two
lines containing the enemy-target predicate. One shared implementation is used.
The [normalized handler comparison](P911_assets/handler-comparison.json) records
the raw diff and equality hash.

Outside the order handlers, the native files differ in their descriptive header,
include/class/mission-ID names, a commented legacy sector return, and debug text.
The executable mission-creation differences are limited to the intro: Mission6
plays the scan sound first, waits 0.4 s for miner 01, narrates tm_6_01, and waits
2 s for miner 02 before switching to cockpit. Mission10 waits 0.1 s for miner 01,
creates the carrier, then plays the scan sound and switches to cockpit; its miner
02 delay is 0.2 s and it inserts an additional 1 s wait before tm_6_02. These
remain separate scripts. Their universe/loadout and wave recipes are otherwise
equivalent. Source: mission6.cpp:373-466; mission10.cpp:369-462. Both files pass
1030 as the unused legacy `homeStationID` parameter to LoadUniverse (signature:
trainingmission.cpp:651). SetupShipAndCamera instead uses GetStartSectorID
(trainingmission.cpp:139), which both missions return as 2080. The port preserves
that playable start.

## Row audit

Each row below retains its original rule and weight. Evidence is executable code,
not recipe labels or rewritten matrix descriptions. The matrix outside these 30
rows is unchanged. Paths and line numbers are repository-relative; native paths
are relative to the read-only native `src/` tree. The native citations were
re-grepped and recorded in `.scratch/p911/native-citation-audit.json`.

| Row | Rule | Verdict | Native citation | Port citation | Test / exact remaining gap |
|---|---|---|---|---|---|
| TRN-0132 | Mission 6: Practice Arena | implemented | `training/mission6.cpp:373` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0133 | M6 universe | implemented | `training/mission6.cpp:292` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0134 | M6 NPC default orders | partial | `training/mission6.cpp:82` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` — The shared order policy is live; exact native patrol obstacle ordering, non-ship pickable coverage and GetNoHit exclusions remain absent (TRN-0238). |
| TRN-0135 | M6 death restore | implemented | `training/mission6.cpp:60` | `src/Allegiance.ClientCore/Training/Mission6.cs:21; src/Allegiance.ClientCore/Training/TrainingMission.cs:119` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0136 | M6 landing | implemented | `training/mission6.cpp:70` | `src/Allegiance.ClientCore/Training/TrainingMission.cs:208; src/Allegiance.ClientCore.Game/Training/TrainingRuntime.cs:112` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0137 | M6 early waves | implemented | `training/mission6.cpp:569` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0138 | M6 bomber arrival | implemented | `training/mission6.cpp:692-736` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0139 | M6 late waves | implemented | `training/mission6.cpp:733` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0140 | M6 final wave | implemented | `training/mission6.cpp:942-960` | `src/Allegiance.ClientCore/Training/Mission6.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0141 | Mission 10: Free Flight | implemented | `training/mission10.cpp:369` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0142 | M10 universe | implemented | `training/mission10.cpp:288` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0143 | M10 NPC default orders | partial | `training/mission10.cpp:78` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` — The shared order policy is live; exact native patrol obstacle ordering, non-ship pickable coverage and GetNoHit exclusions remain absent (TRN-0238). |
| TRN-0144 | M10 death restore | implemented | `training/mission10.cpp:56` | `src/Allegiance.ClientCore/Training/Mission10.cs:21; src/Allegiance.ClientCore/Training/TrainingMission.cs:119` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0145 | M10 landing | implemented | `training/mission10.cpp:66` | `src/Allegiance.ClientCore/Training/TrainingMission.cs:208; src/Allegiance.ClientCore.Game/Training/TrainingRuntime.cs:112` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0146 | M10 early waves | implemented | `training/mission10.cpp:565` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0147 | M10 bomber arrival | implemented | `training/mission10.cpp:688-732` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0148 | M10 late waves | implemented | `training/mission10.cpp:729` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0149 | M10 final wave | implemented | `training/mission10.cpp:938-956` | `src/Allegiance.ClientCore/Training/Mission10.cs; src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:14; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:257` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0156 | AddRespawn registration | implemented | `training/trainingmission.cpp:287-316` | `src/Allegiance.ClientCore/Training/TrainingMission.cs:361; src/Allegiance.ClientCore/Training/TrainingMission.cs:397; src/Allegiance.ClientCore/Training/CreationActions.cs:142` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:116` |
| TRN-0184 | Default drone order dispatch | implemented | `Igc/shipIGC.h:2438` | `src/Allegiance.Core/Sim/Ship.cs:7646; src/Allegiance.Server/ServerHost.cs:799; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:327` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:188; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0215 | Training friendly support flee | partial | `Igc/shipIGC.cpp:1883` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs:44; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:751; src/Allegiance.Core/Sim/Ship.cs:7834` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:209` — Negative-tech nanite equipment and real repair shots are bound. The cited native line 1883 is a comment in generic retreat; line 1734 explicitly exempts nanite wingmen. No source-confirmed support-without-target restart-base rule has been identified; this claimed rule cannot earn credit. |
| TRN-0223 | Arena initial equipment | implemented | `training/mission6.cpp:316-369` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:157; src/Allegiance.SinglePlayer/Training/TrainingHost.cs:385` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:148; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0231 | Remove respawn recipe | implemented | `training/trainingmission.cpp:318-324` | `src/Allegiance.ClientCore/Training/TrainingMission.cs:361; src/Allegiance.ClientCore/Training/TrainingMission.cs:397; src/Allegiance.ClientCore/Training/CreationActions.cs:142` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:116` |
| TRN-0234 | Arena target selection | implemented | `training/mission6.cpp:94-137; training/mission10.cpp:90-133` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:18; src/Allegiance.Core/Sim/Ship.CommandPlan.cs:10` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:42; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:270; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0235 | Arena nearby attack and assistance | implemented | `training/mission6.cpp:149-181; training/mission10.cpp:145-177` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:47; src/Allegiance.Core/Sim/Ship.CommandPlan.cs:10` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:64; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0236 | Arena sector attack restrictions | implemented | `training/mission6.cpp:183-210; training/mission10.cpp:179-206` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:64; src/Allegiance.Core/Sim/Ship.CommandPlan.cs:10` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:92; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0237 | Arena sector pursuit | implemented | `training/mission6.cpp:213-239; training/mission10.cpp:209-235` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:70; src/Allegiance.Core/Sim/Ship.CommandPlan.cs:10` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:175; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0238 | Arena local patrol points | partial | `training/mission6.cpp:240-273; training/mission10.cpp:236-269` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:77; src/Allegiance.Core/Sim/Ship.CommandPlan.cs:10` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:92; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` — Station/He3/two-random patrol candidates and temporary aggression run. Exact mixed insertion order of native pickables, complete native pickable membership (including missiles, treasures and buoys) and GetNoHit pointer exclusions are not represented; coincident centers retain the original random point instead of invoking native fatal zero normalization. |
| TRN-0239 | Arena hidden patrol buoy | implemented | `training/mission6.cpp:274-286; training/mission10.cpp:270-282` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:104; src/Allegiance.Core/Sim/Ship.CommandPlan.cs:10` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:92; tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:234; tests/Allegiance.ClientCore.MonoGame.Tests/P911TrainingArenaClientTests.cs:21` |
| TRN-0241 | Respawn buoy retention | implemented | `training/trainingmission.cpp:304-314` | `src/Allegiance.ClientCore/Training/TrainingMission.cs:361; src/Allegiance.ClientCore/Training/TrainingMission.cs:397; src/Allegiance.ClientCore/Training/CreationActions.cs:142` | `tests/Allegiance.ClientCore.Tests/P911TrainingArenaTests.cs:116` |

## Scorecard and gates

`python3 scripts/parity-scorecard.py` passes before and after:

| Metric | Before | After |
|---|---:|---:|
| Training weighted production credit | 562/901 (62.38%) | 677/901 (75.14%) |
| Training completed eligible rows | 150/235 (63.83%) | 176/235 (74.89%) |
| Scoped P911 credit | 0/131 | 115/131 |
| Scoped completed rows | 0/30 | 26/30 |

All other scorecard totals are unchanged. The denominator and original native
rules were preserved. The four partial rows retain all 16 unearned points.

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 106 projects |
| Release linux-x64 server NativeAOT publish | PASS, no IL2026 / IL3050 diagnostics |
| New and affected focused tests, 3 isolated runs | PASS: 21 headless + 5 client cases per run, plus the weighted-selection case 3 separate times; 81 total passes, 0 skips |
| `tools/run-training-tests.sh --no-build --repeat 3`, with artwork | PASS: 727 passed / 0 failed / **0 skipped**, all lessons including Nanite |
| Showcase never-parks, 5 runs | PASS 5/5, 0 skips; 3m57s / 3m29s / 3m42s / 3m27s / 5m22s |
| ONE Fast-tier sweep | **FAIL**: 12,859 passed / 11 failed / 3 existing optional skips; 9 infrastructure checks pass; no timing offenders or quarantine flakes |
| Native training inventory / glossary / scorecard / diff checks | PASS: 241 rules / 82 primitive types; 465 glossary paths; CSV LF; exactly 30 scoped matrix rows changed |

Each full training repetition ran both test projects serially without a Tier
exclusion. Headless counts are 93 / 93 / 94 and client counts are 149 each. The
additional weighted-selection regression was added after the first two headless
cohorts, ran 3 times by itself, and is included in the third cohort. Production
behavior did not change during these final repetitions. Client-cohort times are
10m46s / 12m11s / 10m46s. `local.env` was present and artwork was loaded; no missing
artwork skip was accepted. The three Fast skips are the existing optional
`MeshyPackTests` content/catalogue fixtures.

The Fast failures are five bundle-path cases, default-profile restoration,
StartChat compose, SwapPart silent-noop delivery, NPC-defend, missile terminal
position, and resign-ballot state. Every failing method received 3 separate
method-filtered invocations. Compose, SwapPart, missile and ballot controls pass
3/3; profile fails 3/3 at the read-only default path and passes 3/3 with writable
XDG_CONFIG_HOME; the five bundle cases and existing NPC-defend failure persist
3/3. Exact assertions and classifications are in
[TEST_FLAKES.md](../TEST_FLAKES.md#p911-codex-gates--2026-11-01). These controls do
not convert the retained sweep into a green result. Its original summary is in
[P911_assets/validation.json](P911_assets/validation.json).

The full Fast sweep preceded the last arena buoy-constant correction and the
additional selection assertion. Final corrected arena behavior received the
complete repeated training gate and isolated focused runs; a second full Fast
sweep was not substituted for the user's ONE-sweep instruction. Ordinary
multiplayer/showcase/skirmish navigation retains its prior behavior. The recorded
dispatch-base P910 showcase rate is also 5/5; this lane's 5/5 is a behavioral
comparison, not a matched-load throughput claim.

Superseded development runs are retained as diagnostics and are not counted as
final gates. The first training run exposed two scope leaks (legacy waypoint kind
and offscreen motion); both were fixed and their methods pass 3/3 on the final
code. The initial comments had reversed native buoy labels; the final code uses
`DataBuoyIGC.CBuoyWaypoint` / `CBuoyCluster`, confirmed at `Igc/igc.h:553-554`.
An earlier wrapper continued after interruption; a temporary launch guard
prevented its queued duplicate Fast command. The original runner script was
restored exactly, and only one actual Fast result directory exists.

Raw local evidence is retained in the ignored
[task scratch directory](../../.scratch/p911/), including final build/tool/AOT
logs, isolated TRX, native-citation re-greps, and original Fast evidence. Complete
training receipts are in `.scratch/p911/training-native/`; the Fast sweep is in
`.scratch/p911/fast-native/`, and the published NativeAOT output is in
`.scratch/p911/aot/`. The sandbox permits
writes in this worktree rather than the lane's external evidence store, so raw
receipts remain local and are not public report assets. Small public summaries,
the [before](P911_assets/scorecard-before.txt) and
[after](P911_assets/scorecard-after.txt) scorecards, and native handler comparison
are under `P911_assets/` (well below 2 MB). No media was added.


## Non-training performance

`tools/p911-arena-proof/` runs actual server ticks with 30 ordinary idle wingmen,
no training site, and the mandated immutable-snapshot/encode-once/parallel-build,
pool transport, native ship updates, native GotoPlan and server-GC baseline. The
same harness was built against the dispatch base and the working tree in separate
Release processes, alternating base/current three times. Each process warms 200
ticks and records five 1000-tick windows. This local measurement is noisy and is
not a rig A/B or a scalability claim. `No_training_site_has_no_policy_allocation`
also asserts zero allocation in 10,000 unbound production callback dispatches and
checks real ordinary enemy acquisition.

The last three windows of each process (nine windows per lane) have median
**0.3224 ms/tick base vs 0.2730 ms/tick current**. Median allocation counters are
474543 vs 474330 bytes/tick; they include asynchronous broadcast
workers and show no added allocation trend. The unbound callback micro-check
measured a median 1.65 ns/call across three explicit server-GC baseline runs, with
exactly zero allocated bytes. This supports **no
measurable non-training tick regression** at this scale, not literally zero CPU
instructions or a speedup claim. Full samples are in
[P911_assets/performance.json](P911_assets/performance.json); raw receipts are
under `.scratch/p911/perf-final-*.log`. The base is commit
`462af474c263de8e7132682cbc83bb76be95b608` extracted into task scratch; no alternate
worktree or branch was written. No remote host or synthetic load was used.

Reproduce the current lane with `source local.env` followed by
`~/.dotnet/dotnet run -c Release --project tools/p911-arena-proof -- current`.
Build the same tool against the cited base to compare; keep the runtime/config
receipts and alternate the processes. This proof must not be used as a rig budget.

## Remaining limits and native intent

The four partial rows are TRN-0134 and TRN-0143 (full order-policy certification
depends on the patrol obstacle gap), TRN-0238 (native mixed pickable insertion
order, complete native pickable membership (including missiles, treasures and buoys) and NoHit pointer exclusions are not represented;
coincident centers keep the original random point instead of native fatal zero normalization), and TRN-0215.
For TRN-0215, the matrix's cited native line 1883 is a debug comment inside generic
retreat. Native shipIGC.cpp:1734 explicitly exempts nanite wingmen from that
retreat, while :2608-2677 handles repair orders. Real repair equipment/shots are
proved, but a source-confirmed missing-repair-target restart-base rule has not
been identified. It would be incorrect to invent that behavior or close the row
by changing its description. The broad arena AI/difficulty claim remains partial
until these exact issues are resolved. Landing remains active through the existing
native true-return contract; the native one-time hangar-door hint is outside the
scoped landing row's stay-active predicate and is not added by this work.

Native-intent footer: miner preference, initial player protection, team
assistance, sector restrictions and escalating recipes are **design**. Hard-coded
training hull/part/sector IDs, local IGC hooks and single mutable respawn recipes
are **era constraints**. Comparing a new raw target distance against the previous
candidate's weighted distance, and using the ship-to-obstacle vector while
correcting a random waypoint, are probable native **bugs**; P911 preserves those
expressions. A modern design would use a consistent weighted target comparator,
an explicit plan-target type, deterministic injectable randomness, collision-free
patrol sampling, and scoped lifetime handles for buoys. Those alternatives must
be explicit product decisions rather than silent parity changes.

## Claude's review

- **Fixed:** `CreateWaypointAction` defaulted to type 1, which is `c_buoyCluster`; native defaults to `c_buoyWaypoint` (`training/CreateWaypointAction.h:28`). Missions 2, 4 and 8 use that default, and P911 corrected only the arena's explicit values. The default is now `DataBuoyIGC.CBuoyWaypoint`, with `Scripted_waypoint_defaults_to_native_waypoint_type`.
- **Checked against native:** `HandleArenaDefaultOrder` matches `mission6.cpp:82-287` branch by branch apart from the recorded TRN-0238 gaps. Arena tech, `ShipKilled` and `CanSee` also match. `Sector.Mineable` holds every rock, so the patrol and line-of-sight checks see all asteroids.
- **TRN-0215 stays partial:** the row's rule has no confirmed native source. The cited line sits in the miner and constructor retreat path, and native exempts nanite wingmen from retreat.
- **Open:** arena patrol buoys take ids from `TrainingHost.NextId`, narrowed to `short`. A very long Free Flight session could wrap, roughly a day at 20 picks a minute. Not fixed.

