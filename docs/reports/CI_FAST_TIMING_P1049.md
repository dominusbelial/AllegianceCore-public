# CI green again — Fast-tier timing offenders + two shared-state flakes (P1049)

Status: all five brief items done; the Fast verdict still FAILs on **pre-existing** families
(chronic civ/loadout timing rows and, under `taskset -c 0,1`, tests that assert CPU-count-derived
pool sizes) — every test this task changed passes everywhere, and the five offenders the brief
names are gone (§8).

## 1. What changed

| File:line | Change |
| --- | --- |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetFlightSymbologyTests.cs:405-478` | Allocation window 10,000 + 100,000 → **4,096 warm-up + 8,192 measured** frames, sized from the renderer's cache cycles (§2); assertion still `Assert.Equal(0, bytes)`. New test-only sensitivity probe `The_measured_window_still_catches_a_periodic_allocation(period: 4096|8191)` (same file, §2.3). |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetTargetLockTests.cs:588-615` | Same window reduction (4,096 + 8,192), assertion unchanged. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetStoresCautionTests.cs:664-701` | Same window reduction, assertion unchanged. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetSkinScaffoldTests.cs:272-311` | Same window reduction, assertion unchanged. |
| `tests/Allegiance.Core.Tests/P968PublisherStreamIndexTests.cs:22-73` | Theory split: `Reliable_backlog_counting_rules_hold_at_100_peers` stays **Fast** (all counting assertions, 9 runs/arm); new `Reliable_backlog_measures_pending_scan_work_at_400_peers` is `[Trait("Tier","Perf")]` (same assertions + the JSON measurement). |
| `tests/Allegiance.Core.Tests/P808ServerEnrollmentTests.cs:49-57` | Bounded (5 s) poll for the listing to disappear after the heartbeat is disposed, then `Assert.Empty` — the assertion itself is unchanged. |
| `src/Allegiance.Lobby/ServerRegistry.cs:214-244` | `Deregister` now runs under the registration gate: a heartbeat refresh's read-modify-write can no longer undo a removal (real ordering bug, §4). |
| `src/Allegiance.Lobby/ServerRegistry.cs:337-370` | `PruneExpired` takes the same gate (re-entrant) for the same reason. |
| `src/Allegiance.Server/LobbyHeartbeat.cs:447-474` | The bounded deregister wait now reports a timeout instead of silently leaving the listing to expire. No behaviour change beyond the log line. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P941InputLayoutTests.cs:328-392` | `Keymap_store_picks_the_layout_from_the_file` and `Reload_without_a_readable_keymap…` use the explicit store path (P1047 constructor) instead of the process-wide `ALLEGIANCE_KEYMAP` variable. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P949FreshProfileDefaultsTests.cs:63-131` | The two persistence tests use the explicit store path for the same reason. |
| `src/Allegiance.ClientCore.Game/Rendering/RadarLabelModel.cs:147-181` | `RangeText`/`RangeLineText` caches are `[ThreadStatic]` (§6). |
| `src/Allegiance.ClientCore.Game/Rendering/HudStringCache.cs:22-64` | All 18 value caches are `[ThreadStatic]`; `Get` takes the slot by `ref`. |
| `src/Allegiance.ClientCore.Game/Rendering/RadarLabelDeclutter.cs:268-283` | `CountTag` cache `[ThreadStatic]`. |
| `src/Allegiance.ClientCore.Game/Rendering/ShipRenderScale.cs:56-111` | `_renderRadiusCache` `[ThreadStatic]`. |
| `src/Allegiance.ClientCore.Game/Rendering/MissileModelSource.cs:135-187` | `_nativeRadiusCache` `[ThreadStatic]`. |
| `src/Allegiance.ClientCore.Game/Rendering/TargetPaneModel.cs:276-291` | The two static scratch lists became per-thread slots. |
| `docs/TEST_FLAKES.md` | P1049 section: P808 and P941 resolved, the allocation-window and cache families recorded. |
| `docs/reports/CI_FAST_TIMING_P1049.md` | This report. |

`docs/testing/TIERS.md` is unchanged: no rule changed (the P968 measurement rows simply carry the existing `Tier=Perf` tag).

## 2. Item 1 — Jet allocation tests: same strictness, a fraction of the work

### 2.1 The cycles that size the window

Every string/int cache in the Jet draw path is bounded and `Clear()`s at its limit, and the
speed-tick label ring wraps at 16 entries. There is **no frame-counter or wall-clock animation**
in the Jet renderers: blinking comes from the model (`HudFrame.Flashing`,
`HudContact.ThreatFlash`), so a fixed model has a fixed frame. The cycles found:

| Cache / periodic path | Bound | file:line |
| --- | --- | --- |
| shared radar range-text + range-line cache (Jet radar scope calls `RadarLabelModel.Resolve`) | 4,096 entries | `Rendering/RadarLabelModel.cs:169-171` (pre-change) |
| console chat-fit cache | 512 | `Rendering/Jet/JetHudRenderer.Console.cs:131` |
| console value-fit cache | 256 | `Rendering/Jet/JetHudRenderer.Console.cs:132` |
| tip wrap cache | 64 | `Rendering/Jet/JetHudRenderer.Console.cs:514` |
| compact tag cache | 16 | `Rendering/Jet/JetHudRenderer.Console.cs:449` |
| inventory stat slots | 40 | `Rendering/Jet/JetHudRenderer.StoresCaution.cs:38-41` |
| stores / quantity int caches | 24 / 40 | `Rendering/Jet/JetHudRenderer.StoresCaution.cs:36-37` |
| speed-tick label ring | 16 | `Rendering/Jet/JetHudRenderer.cs:139,177-178` |
| caution line cache slots | 21+ | `Rendering/Jet/JetHudRenderer.StoresCaution.cs:613` |
| skin switch cadence (HudSkinSwitching only) | 4 frames | `tests/.../Skins/HudSkinSwitchingTests.cs:160` (`(i & 3) == 0`) |

Largest cycle = **4,096**. Warm-up = 4,096 frames (one full cycle, so any rotating cache has
wrapped and everything is at steady state); measured window = **8,192** frames (two cycles),
which also covers 16 cycles of the 512-entry console cache and 512 cycles of the 16-entry ring.
12,288 frames total = 11 % of the old 110,000, and the assertion is still exactly zero.

`HudSkinSwitchingTests.Selection_and_dispatch_allocate_zero_bytes_per_frame` keeps its window:
its model is `HudModelAdapter.Create(null, CameraMode.Command)`, so a frame draws almost
nothing; measured **0.198 s / 0.173 s** on 2 cores (Port / Native), far from the 16.4 s runner
limit and never on the CI offender list. `JetConsoleTests` (2,000 + 20,000) and
`JetAmberPaletteTests` (2,000 + 20,000) were already reduced by P1041.

### 2.2 Fixture sharing

Not needed: model/fixture construction is ~1 ms of a 3.5 s case — the measured frame loop is
99 % of the time (`JetTargetLockTests` 4.16 s at 110,000 frames, 0.52 s at 12,288 frames; the
per-case overhead is unchanged either way).

### 2.3 Sensitivity proof (test-only probe)

`JetFlightSymbologyTests.The_measured_window_still_catches_a_periodic_allocation` draws the same
full-symbology fixture with the same 4,096-frame warm-up and 8,192-frame window, injecting one
16-byte allocation every K frames:

| K | Allocations inside the window | Bytes caught | Result |
| --- | --- | --- | --- |
| 4,096 (largest cycle) | 2 | > 0 | **passed** (asserts bytes > 0) |
| 8,191 (window − 1) | 1 | > 0 | **passed** (asserts bytes > 0) |

The probe lives in the test project only; the renderer has no probe hook.

### 2.4 Before / after on 2 cores (`taskset -c 0,1`, `--no-build`, Flash's own TRX durations)

| Case | Before | After | Runner estimate (×6 CPU factor measured on the CI run: 21.7 s/3.6 s ≈ 6) |
| --- | --- | --- | --- |
| `JetFlightSymbologyTests.Jet_draw_allocates_zero_bytes_per_frame_with_the_full_symbology` | 3.59 s | **0.43 s** | ≈ 2.6 s |
| `JetTargetLockTests.Draw_with_a_locked_leading_ordered_target_allocates_nothing` | 4.16 s | **0.52 s** | ≈ 3.1 s |
| `JetStoresCautionTests.Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing` | 3.29 s | **0.40 s** | ≈ 2.4 s |
| `JetSkinScaffoldTests.Jet_draw_allocates_zero_bytes_per_frame_on_a_fixed_model` | 2.70 s | **0.32 s** | ≈ 1.9 s |

(Numbers are the recorded TRX durations of the before/after cohorts in `.scratch/p1049/before-trivia`
and `.scratch/p1049/after-jet`.) Each case is now well under 10 s even with the CI runner's
≈6× slowdown, so no move to Perf was needed — the cycle analysis does not require a long window.
The two sibling Skins allocation cases were reduced the same way after the first local sweep
flagged one (§7): `JetAmberPaletteTests.Amber_draw_allocates_zero_bytes_per_frame` 13.65 s in the
sweep → **1.2-1.5 s alone on 2 cores**; `JetConsoleTests.The_console_composition_allocates_zero_bytes_per_frame`
4.12 s → **≈1 s**.

## 3. Item 2 — P968 400-peer measurement: exact rules stay Fast, measurement moves to Perf

| Assertion | Before | Now |
| --- | --- | --- |
| `Comparisons`, `Lookups`, `IndexEntries`, `IndexVisits`, `GateAcquisitions`, `Packets` | all 8 rows (100 and 400 peers) in Fast | all of them on the **Fast** 100-peer rows (`Reliable_backlog_counting_rules_hold_at_100_peers`, 9 runs × instrumented/uninstrumented); the same six also still asserted on the **Perf** 400-peer rows |
| JSON measurement lines | Fast | **Perf** (`Reliable_backlog_measures_pending_scan_work_at_400_peers`) |

No assertion was weakened or removed; each row keeps 9 runs per arm. Measured on 2 cores:
Fast 4 cases 0.46-0.51 s; Perf 4 cases ≈4 s.

## 4. Item 3 — P808: why the listing survived heartbeat disposal

Reproduction attempts on the changed tree (bounded parallel runs, no synthetic load):

* 8 parallel processes × 12 iterations of the exact test (96 runs): 0 failures.
* 10 parallel processes × 15 iterations with the 457-case `Skins` suite running concurrently on
  the other cores: **151 logs, 276 deregister probes, every one answered `204`, 0 failures**.

With temporary instrumentation the deregister always completed. The two defects the code shows,
both of which leave a listing behind after a clean shutdown, are:

1. **Registry ordering (real bug, fixed).** `ServerRegistry.RegisterCore` refreshes a heartbeat
   with `var target = _entries.Values.FirstOrDefault(...)` … `_entries[target.Id] = refreshed`.
   `Deregister` did **not** take `_registrationGate`, so a deregister landing inside that
   read-modify-write was undone by the indexer write: the listing came back and no further
   subscribe/heartbeat removes it until the 90 s expiry sweep. `Deregister` and `PruneExpired`
   now run under the same gate as `Register`, making the refresh and the removal mutually
   exclusive.
2. **Test synchronisation (flake, fixed).** `LobbyHeartbeat.Dispose` sends the deregister
   best-effort with a bounded 5 s wait; on a loaded runner the DELETE can still be in flight when
   `Dispose` returns, so the immediate `Assert.Empty` reads the registry before the request is
   processed. The test now polls (100 × 50 ms = the same 5 s bound) for the listing to disappear
   and then asserts it is gone — a rejected or unanswered DELETE still fails the test.
   `Dispose` also logs a timeout instead of silently leaving the listing to expire.

Which side was wrong: both, but the observable production defect is #1. No server/lobby
behaviour changed other than that ordering guarantee.

## 5. Item 4 — P941: the shared keymap path

Confirmed. The colliding tests were `P941InputLayoutTests.Keymap_store_picks_the_layout_from_the_file`
and the two `P949FreshProfileDefaultsTests` persistence tests: all three wrote the **process-wide**
`ALLEGIANCE_KEYMAP` variable (`Environment.SetEnvironmentVariable`) while xUnit runs their classes
in parallel collections. A temporary probe that repeated the *pre-fix* bodies in two parallel
classes reproduced the exact CI signature:

```
Failed ZZP1049KeymapRaceProbeA.Pre_fix_P941_body_repeated
Assert.Equal() Failure: Values differ
Expected: Native
Actual:   Port
```

(the log line immediately above it is the P949 probe's `[keymap] saved bindings -> ...p1049-keymap-b-.../keymap.json`).
Three runs of the probe: run 1 failed the P941 direction (`Native` → `Port`), runs 2 and 3 failed
the P949 direction (`FreshProfile` false) — the collision hits whichever test loses the race.
Probe deleted; the three tests now use `new KeyBindingStore(ProfileStorage.Default, path)`
(P1047 explicit-path constructor, `Rendering/KeyBindingStore.cs:58`), so nothing in the test reads
or writes the shared path. Twelve parallel repetitions of the three fixed tests pass (§8).

## 6. Item 5 — static render caches shared across parallel tests

Fixed (no per-frame allocation; each slot is created once per thread on first use, so the steady
frame still returns the same string instance):

| Static cache used by draw code | file:line | Disposition |
| --- | --- | --- |
| radar range text + range line | `Rendering/RadarLabelModel.cs:169-171` | **fixed** → `[ThreadStatic]` |
| HUD value strings (18 dictionaries) | `Rendering/HudStringCache.cs:23-40` | **fixed** → `[ThreadStatic]` |
| radar declutter count tag | `Rendering/RadarLabelDeclutter.cs:280` | **fixed** → `[ThreadStatic]` |
| ship render-radius cache (`Model` → float) | `Rendering/ShipRenderScale.cs:107` | **fixed** → `[ThreadStatic]` |
| missile native-radius cache (stem → float) | `Rendering/MissileModelSource.cs:184` | **fixed** → `[ThreadStatic]` |
| target-pane occluder / scanner scratch lists | `Rendering/TargetPaneModel.cs:281,287` | **fixed** → per-thread slots |
| TTI m:ss cache | `Rendering/TargetTti.cs:46` | safe: every access is inside `lock (_cache)` (`:94`) |
| mission briefing mdl parse | `Rendering/MissionBriefingGenerator.cs:130` | safe: write-once immutable parse; a racing duplicate parse stores an equal value (reference assignment is atomic) |
| HUD palette casing / Jet palette fallback flags | `Rendering/HudSettings.cs:272`, `Rendering/Skins/JetPaletteIds.cs:49` | safe: write-once diagnostic booleans — a lost race only repeats or skips one log line |
| skin catalogue | `Rendering/Skins/HudSkinResolver.cs:10` | safe: `Lazy<T>` (thread-safe publication) |
| `AssetLoadLog.FrameStamp` | `Rendering/AssetLoadLog.cs:26` | safe: one startup assignment (`Game1.Lifecycle.cs:203`), read-only diagnostics |
| `KeyBindingStore.Instance` | `Rendering/KeyBindingStore.cs:21-22` | shared by design (P625: one live map); tests isolate through explicit paths (item 4) |
| constant tables (`JetHudRenderer.CoverageSet`, `InputLayouts._relocationMap`, `DefaultInputMap._tkNames`, `SoundManager.StationAttackVoices`, `TrainingInputGate.NativeKeys/…`) | — | safe: built once, never mutated |

Proof: `FullyQualifiedName~Skins` **10 runs in a row** on 2 cores — results in §8.

## 7. Item 6 — the next offenders

Two more Skins allocation cases were over the limit in the first local sweep and are part of the
same family, so they got the same cache-cycle window (assertion still exactly 0 B):

| Case | Window before | Window now | Sweep before (8 cores, 4 projects) |
| --- | --- | --- | --- |
| `Skins.JetAmberPaletteTests.Amber_draw_allocates_zero_bytes_per_frame` | 2,000 + 20,000 | **4,096 + 8,192** | 13.65 s (over the 12.73 s local calibration) |
| `Skins.JetConsoleTests.The_console_composition_allocates_zero_bytes_per_frame` | 2,000 + 20,000 | **4,096 + 8,192** | 4.12 s |

Every other Fast case above 8 s in the first sweep (calibrated limit 12.73 s, host load average
7-10 from other lanes, so every number is inflated) belongs to the **chronic civ/loadout scan
family** that is already in `docs/TEST_FLAKES.md` for P1013–P1047 and that the public runner's
own tier summaries do **not** list: `P999CivStrategyTests…(civ: 5)` 26.3 s,
`SC2NaniteSupportTests…(civ: 25)` 21.5 s, `P814GunnerTests.P814b…` 20.8 s,
`P796VariedLoadoutTests…(civ: 35)` 15.1 s, `P1000CivProfileTests…(civ: 3)` 13.8 s,
`SC2NaniteSupportTests…(civ: 3)` 13.0 s, `P750NativeAsteroidTypesTests…` 10.8 s,
`P942ResearchOrderingTests…` 10.0 s, `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`
8.6 s. Their cause is stated in the ledger: each theory row re-parses `PCore001.igc` and rescans
every side's buy gates (`tests/Allegiance.Core.Tests/P999CivStrategyTests.cs:11-12` `File()`/`Core()`
per row). They pass when given a core to themselves (3× alone in §8) — no assertion, tier or
limit was changed for them, and they are outside this task's changed code. Fixing them means
sharing the parsed core between theory rows in 5+ test classes; that is a separate change with
its own gate, so it is left justified rather than half-done here.

## 8. Gate

### 8.1 Build and tools

* `dotnet build AllegianceCore.slnx --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`:
  **0 warnings / 0 errors**.
* `tools/build-tools.sh`: **all 144 tool projects built**, each 0 warnings / 0 errors.
* AOT publish (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): succeeded, native code generated, no IL2026/IL3050.

### 8.2 Changed tests, three times alone (own process each)

| Test | 3 × alone |
| --- | --- |
| `P808ServerEnrollmentTests` | pass / pass / pass (0.51-0.58 s) |
| `P968…counting_rules_hold_at_100_peers` (4 rows) | pass / pass / pass (0.46-0.51 s) |
| `P968…measures_pending_scan_work_at_400_peers` (4 rows, Perf) | pass / pass / pass (≈4 s) |
| `P941InputLayoutTests.Keymap_store_picks_the_layout_from_the_file` | pass / pass / pass (≈60 ms) |
| `P949FreshProfileDefaultsTests.Fresh_profile_is_not_saved_until_the_player_changes_something` | pass / pass / pass (≈53 ms) |
| `P949FreshProfileDefaultsTests.An_existing_keymap_file_is_never_changed_by_loading_it` | pass / pass / pass (≈49 ms) |
| `JetFlightSymbologyTests.Jet_draw_allocates_zero_bytes_per_frame_with_the_full_symbology` | pass / pass / pass (0.69-0.73 s) |
| `JetTargetLockTests.Draw_with_a_locked_leading_ordered_target_allocates_nothing` | pass / pass / pass (0.75-0.77 s) |
| `JetStoresCautionTests.Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing` | pass / pass / pass (0.54-0.56 s) |
| `JetSkinScaffoldTests.Jet_draw_allocates_zero_bytes_per_frame_on_a_fixed_model` | pass / pass / pass (0.57-0.61 s) |
| `JetFlightSymbologyTests.The_measured_window_still_catches_a_periodic_allocation` (2 rows) | pass / pass / pass (≈1 s) |
| `JetAmberPaletteTests.Amber_draw_allocates_zero_bytes_per_frame` + `JetConsoleTests.The_console_composition…` | pass / pass / pass (2-3 s for the pair) |

### 8.3 Fast tier, three runs (`tools/run-tests.sh --tier fast --no-build`)

| Run | Cases | Seconds | Calibrated limit | Failed | NotExecuted | Timing offenders | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 — normal parallelism (8 cores) | 15,326 | 651.6 | 12.19 s | **2** | 7 | **6** | FAIL |
| 2 — `taskset -c 0,1` | 15,326 | 758.9 | 12.64 s | **9** | 7 | **13** | FAIL |
| 3 — `taskset -c 0,1` | 15,326 | 794.5 | 12.65 s | **10** | 7 | **12** | FAIL |

**No task-owned test failed in any run**, and every changed/allocation test passed in all three
(plus the `Skins` filter 10 × 457/457). The failures are pre-existing families, and the 2-core
runs also fail on environment assumptions that are not this task's:

| Failure | Runs | Three times alone | Classification |
| --- | --- | --- | --- |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | 1, 2 | pass / pass / pass (0.77-0.79 s) | ledgered simulation-clock roster family |
| `P739TimerLiveTests.Warp_bomb_notice…(delay: 5)` | 1, 2 | fail / fail / pass (3 rows) | ledgered one-ULP inclusive upper bound (`5.0000000000000009 > 5`) |
| `P889PoolSizingTests` (4 rows) | 2, 3 | **fail 4/4 alone under `taskset -c 0,1`** and **the pristine base tree fails the same 4 rows** with artwork; 0/4 on 8 cores | 2-core affinity: the resolver sizes the pools from `Environment.ProcessorCount` (=2), the assertions expect the 8-core sizes. Not task-caused (base control), not a flake. |
| `ParallelBroadcastByteIdentityTests.Parallel_flag_defaults_on_and_config_binds` | 2, 3 | fail alone under 2 cores, passes 7/7 on 8 cores | same affinity class: the parallel-build default resolves serial on 2 cores |
| `P1017BuyFlowLiveTests.Money_change_while_docked…`, `CommandedDroneBuildKeyLiveTests…`, `ShopLauncherMountNumberingLiveTests…`, `P1023PreLogonTimeoutTests…`, `DeadLatchRespawnLiveTests…`, `LiveCombatWireTests…`, `NpcWingmanAttackLiveTests…` | 2, 3 | pass when run alone on this host (the ledger's rows) | ledgered load-sensitive live/loopback families; host load average 7-10 from other lanes during the 2-core runs |

The timing offenders in all three runs are the chronic civ/loadout scan family (P999 ×several
rows, SC2 ×2, P814, P796 ×3, P1000, P704, P832b, P942, P750) plus, on 2 cores,
`R1LobbyReviewTests.Stale_game_requests…` (27.1 / 18.7 s) — the same rows that
`docs/TEST_FLAKES.md` records for P1013-P1047 — and `LiveCombatWireTests.Real_two_party_combat…`
(32.9 s in run 3). None of them is on a path this task changed; every one passes 3× alone well
below the limit (§7). **The Fast verdict is therefore FAIL on pre-existing families, not green.**
The five offenders the brief lists are gone: the four Jet cases are now 0.3-0.7 s alone, and
`P968…(peers: 400, …)` is a Perf test (below).

### 8.4 Perf tier

`TEST_FILTER=FullyQualifiedName~Reliable_backlog_measures_pending_scan_work_at_400_peers
tools/run-tests.sh --tier perf --no-build`: **PASS**, 4 tests, 8.4 s
(`TestResults/tiers-20261118T003326.328494Z/summary.json`).

### 8.5 Slowest Fast cases on 2 cores (run 3, `taskset -c 0,1`)

1. `LiveCombatWireTests.Real_two_party_combat_produces_a_kill_ship_over_the_wire` 32.9 s
2. `P999CivStrategyTests…(civ: 18)` 24.8 s
3. `P999CivStrategyTests…(civ: 5)` 22.5 s
4. `SC2NaniteSupportTests…(civ: 3)` 22.2 s
5. `P704TechLoadoutTests.Dock_local_masks_are_never_combined…` 20.1 s
6. `R1LobbyReviewTests.Stale_game_requests_do_not_block_new_games_forever` 18.7 s
7. `P796VariedLoadoutTests…(civ: 3)` 17.8 s
8. `P999CivStrategyTests…(civ: 101)` 16.3 s
9. `P832bProsperityTests.Other_goals_keep_the_same_commander_trace_as_absent_goals` 15.9 s
10. `P999CivStrategyTests…(civ: 25)` 14.7 s

Before the change, the same 2-core cohort of the affected cases measured
`JetFlightSymbology` 3.59 s, `JetTargetLock` 4.16 s, `JetStoresCaution` 3.29 s,
`JetSkinScaffold` 2.70 s (and 13.65 s for `JetAmber` in the sweep); after: 0.43 / 0.52 / 0.40 /
0.32 s (and 1.2-1.5 s for `JetAmber`). The CI runner's own "before" numbers are the brief's
table (21.7-27.9 s for the four Jet cases, 15.5-25.0 s for P968 400 peers).

### 8.6 Evidence

Raw logs, TRX and summaries: `<lane evidence dir>/P1049/` (fast tier `fast/`, changed-test
cohorts `alone/`, Skins 10 × `skins10/`, P941 probe `p941-race-1.log`, P808 parallel runs
`p808-repro2/`, base-tree controls `base-p889.log`, `base-p889-art.log`).



## 9. Native intent / Modern approach

**Native intent.** Native's HUD draws its flight symbology, radar labels and radar readouts every
frame from the same pre-formatted strings and the same value-keyed caches, and the server's lobby
deregistration is a roll-call entry that disappears when the host disconnects
(`lobby/server.cpp:401-425`, `lobbyapp.cpp:426-460`): one connect line, one disconnect line, no
zombie row. Nothing in this change alters what is drawn, formatted or listed — the frozen strings
are the native text (`HDG 045`, `SPD / MPS`, `1234 m`), the deregistration still removes the row on
a clean shutdown, and no allocation, counting or listing assertion was relaxed.

**Modern approach.** Fast-tier tests now measure the same rules over the smallest window that
still exercises every periodic path (cache cycles, not habit), the crowded 400-peer publisher
measurement is a Perf budget instead of a unit run, and the shared string/number caches that the
HUD draw path uses are per-thread so parallel client windows (and parallel test collections) cannot
corrupt each other. Both HUD renderers are covered: the Jet tests and the Skins filter exercise
the Jet skin, and the `HudStringCache`/`RadarLabelDeclutter` fixes are in the shared Classic and
Modern draw path; the visual output is unchanged (0 B/frame on a steady model for both styles,
457/457 Skins cases ten times in a row).
