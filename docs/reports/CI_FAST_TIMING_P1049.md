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


## P1049b

Status: **items 1–3 and all brief gates PASS** (2026-11-18 UTC): 9,826 Fast
cases ×3, 0 failures, 0 timing offenders; P968 Perf passes. The additional full
artwork/window training gate is **FAIL/incomplete**, explicitly detailed below.
All changes stay in the working tree; no commits or pushes. Evidence below uses
`<lane evidence dir>/P1049b/` (logs, TRX, diagnostic source and summaries).

### Fixed-model allocation windows: corrected cycle audit

**Correction to §2:** capacity does not imply a frame cycle. None of these six
fixtures changes its model, viewport, settings or callback inputs between draws.
The largest real steady-state period is **one frame**; there is no cross-frame
ring cursor or renderer-owned clock in these paths. All file references below
are under `src/Allegiance.ClientCore.Game/Rendering/`.

| State/cache | What actually advances with fixed input | Source |
| --- | --- | --- |
| Radar metre/range-line dictionaries (capacity 4,096) | Look up the same contact ranges every frame; hits return before the capacity/clear check. Amber repeats the same 12 contact ranges (only the eligible labels are formatted), not 4,096 successive inputs. | `RadarLabelModel.cs:147-181`; Jet caller `Jet/JetHudRenderer.Radar.cs:1031-1044` |
| Speed-tick slots (16) | `labelSlot` starts at zero **inside each DrawSpeedTape**. The same visible ticks reuse the same slots; this is not a ring advancing across frames. | `Jet/JetHudRenderer.cs:856-868,1880-1885` |
| Flight/target readout strings | Replace only when a rounded model value changes. Fixed speed, throttle, heading, pitch, range, closure, fractions and lock percentage reuse strings. | `Jet/JetHudRenderer.cs:166-201,804-808,1697-1811,1880-1885` |
| Stores/caution/quantity/stat slots (24/40) | Stable resource/row slots and equal values/text reuse the same entries. Modulo maps row index to a slot; it is not a persistent cursor. | `Jet/JetHudRenderer.StoresCaution.cs:36-52,601-622,889-919` |
| Console chat/value fit dictionaries (512/256), compact strings (16), tip wrapping (64) | Keys are text, dimensions, scale and face/kind. Each fixed scene repeats a small key set; hits return before clear. | `Jet/JetHudRenderer.Console.cs:131-132,436-449,514-542,1198-1214` |
| Mission/pod/selected radar range strings | Recompose only on changed seconds/countdown, oxygen or range string. Time is a model value. | `Jet/JetHudRenderer.Console.cs:751-773`; `Jet/JetHudRenderer.Radar.cs:1031-1044` |
| Shared HUD value dictionaries (2,048) | Same keys hit before capacity is checked. They are per-thread, so parallel collections cannot advance this thread's dictionary. | `HudStringCache.cs:22-64` |
| Attitude and per-frame layout/radar scratch | Fixed attitude converges on the first update; furniture/radar counters reset and refill each draw. No state accumulates toward a later allocation. | `Jet/JetFlightGeometry.cs:61-80`; `Jet/JetHudRenderer.cs:261-276,365-397`; `Jet/JetHudRenderer.Radar.cs:191-192,698-728`; `Jet/JetHudRenderer.Console.cs:801-802` |
| Blink/lock animation and primitive buffers | Threat blink is a contact field; lock phase reads `lockState.NowSeconds`, which is fixed. Painter path storage is preallocated. | `Jet/JetHudRenderer.Radar.cs:375,443`; `Jet/JetHudRenderer.cs:1356-1361`; `Jet/JetVectorPainter.cs:185-186` |

`Skins/JetAllocationWindow.cs` records the shared **32 warm-up + 128 measured**
window: conservative repetitions of the one-frame state, rather than thousands
of nonexistent cache cycles. Each fixture's finite key set fits its cache (for
example, Amber has 12 contact ranges versus the 4,096-entry bound). Per-instance
slots populate on first use. An inherited per-thread dictionary could clear once
partway through the first frame; the next frame re-adds the earlier fixed keys,
then every lookup hits. Thus even that initial transition settles within two
frames; 32 warm-up repetitions cover it conservatively before the 128 repeated
steady frames. The
six gates retain `Assert.Equal(0, bytes)` (Amber gets the byte count from its
measurement helper). Amber still draws **Draw + DrawCommand + DrawConsole** every
frame; Console still draws Draw + DrawConsole; the other four still draw their
original full fixtures. No scene, assertion, call or resource gate was removed.

The FlightSymbology and Amber test-only sensitivity theories use **K=1** (largest
real cycle) and **K=127** (window minus one). Phase continues through warm-up:
allocation is at absolute frame multiples of K, not forced at measured frame zero.
There are 128 injected arrays for K=1 and exactly one, at absolute frame 127, for
K=127 inside measured frames 33–160. Both probe classes caught **5,120 B at K=1** and **40 B at K=127**, three times
each. The six uninstrumented gates measured **0 B**, three times each. The
unmodified draw path has no probe hook.

### P968: a reproduced scheduling stall, not indexed counting work

Bounded actual runs (no synthetic CPU load, no remote hosts):

- Three one-core baseline runs (`taskset -c 0`, original four-worker/nine-run
  counting theory): all 12 cases passed; indexed/coalesced row 0.132 / 0.805 /
  0.201 s. This alone did not expose the CI signature.
- Core and MonoGame Fast projects together on `taskset -c 0,1`, using the CI
  filter and no `local.env` or artwork environment: both passed. The original
  indexed/coalesced row took **9.873 s**, versus its three siblings **0.478 /
  0.624 / 0.842 s**. Two individual instrumented repetitions reported
  `PublishLagMs=4434.966` and `4614.926`; their freshness gates took only
  **0.031 / 0.027 ms**, submit gates **0.149 / 0.128 ms**. This reproduces the
  outlying-row signature (~12–21×), although not the exact CI 13.76 s duration.
  Baseline Amber took 5.802 s in the same run.

The publisher already owns a dedicated coordinator thread
(`src/Allegiance.Server/BroadcastSnapshot.cs:122-123`); `BeforeEncode` runs there
before `Parallel.For` (`:269-287`), so the test's release wait does not itself
occupy an encoding pool worker. **Parallel.For's queued replicas are the pool
dependency.** The test caller synchronously waits for Flush; Flush uses
`Monitor.Wait` (`:351-355`), not a spin. A separate bounded process using the real
publisher and 900 pre-encoded deliveries demonstrates the dependency without
CPU load. It explicitly sets pool min/max to one in that isolated process and
restores both in finally: when that worker calls Flush on a four-worker
publisher, completion is false after 2,024 ms with one queued item. Restoring the
pool permits completion at 2,525 ms. With one encoding worker the identical proof
completes in **16 ms**, with no queued item. Both processes drain and exit; their
source and logs are in `starvation-proof/`, `starvation-before.log` and
`starvation-serial.log` under the evidence directory.

**Production boundary:** normal boot already clamps encoding degree into
`1..max(1, max(2, ProcessorCount)-1)` in
`src/Allegiance.Server/Server.Lifecycle.cs:1831-1847`, then supplies that value to
`BroadcastPublisher` in `Server.BroadcastSnapshot.cs:81-82`. On one/two cores this
is one worker, even with an explicit four-worker config/environment request. The
Fast fixture bypassed boot and constructed an unrepresentative four-worker
publisher eighteen times per row. No production change is required for the
few-core failure demonstrated here; the existing production clamp avoids it.
The proof does not claim that arbitrary pool starvation on larger machines is
impossible.

Fast now uses **one run per instrumentation arm, one encoding worker**. This is
the minimum: one instrumented run asserts all six exact operation/packet counters;
one uninstrumented run asserts the same delivery count with diagnostics absent.
Both still assert errors empty, exact stale-frame drops and exactly two published
snapshots. Fixed inputs plus the BeforeEncode barrier make those counts exact,
not statistical. Four flag combinations remain Fast. The separate **Perf** rows
still use **nine runs per arm and four workers**; all assertions and seven logged
timing samples per arm remain unchanged. No tier, threshold, calibration or CI
workflow changed.

### Additional obstruction found by the required two-core gate

The first pair of two-core sweeps both failed the existing
`P889PoolSizingTests.Skirmish_sizing_override_reaches_the_server_config`:
expected two bot workers, actual one. The fixture supplied
`Environment.ProcessorCount` to a pure function while unconditionally expecting
two workers. `SkirmishBudget.ForOptions` correctly clamps workers to available
cores (`src/Allegiance.SinglePlayer/SkirmishOptions.cs:263-272`). The override
fixture now supplies an explicit **16-core** input (three isolated two-core
controls pass: 0.015 / 0.014 / 0.015 s); its exact assertions (one
build worker, three transport workers, two bot workers, five cores per bot worker)
are unchanged. Its separate no-override check still uses the real host core count
and asserts the resulting budget fits. This is a deterministic fixture correction,
not a production sizing change or an assertion relaxation.

The first two-core sweep additionally failed
`P895bUdpOverheadTests.Queued_frames_share_token_and_ack_without_waiting_for_more_frames`
(expected one data packet, actual three, 1.006 s); the second passed it in 0.028 s.
Packet statistics include retransmissions, so delayed acknowledgement is a possible
cause, but the failed execution did not capture a wire/retransmit trace. It is not
claimed fixed; its source and all counting assertions remain unchanged. Three isolated two-core controls passed (0.687 / 0.688 / 0.669 s); the final
three final full sweeps all pass below.

Those initial sweeps remain FAIL in the evidence (`fast-two-core-1/` and
`fast-two-core-2/`), both with **zero timing offenders**. The final three sweeps below include the corrected fixtures; these initial
failures are not hidden by a retry or changed filter.

After that correction, a fresh cohort passed normal and first two-core Fast, but
its second two-core run had **0 failed cases and one timing offender**:
`R1LobbyReviewTests.Stale_game_requests_do_not_block_new_games_forever`, **21.100 s**
against 13.088 s. The same case had taken 0.597 s in the preceding two-core run.
This is the existing R1 timing family recorded in the P1049 ledger, now an actual
obstruction to this brief's requested green proof. Evidence for that cohort is
`final-fast-normal/`, `final-fast-two-core-1/`, `final-fast-two-core-2/`.

The R1 fixture already filled the game store directly, but first signed in 32
prefill owners: 32 account inserts, 32 session inserts and 32 unused HTTP clients.
It now supplies 32 owner DTOs for that direct prefill, still creates **all 128
requests**, and adds an exact 128-row assertion before advancing the same 24
hours. Its final request still creates/signs in a real player and follows the
unchanged authenticated HTTP route and success assertion. No cap, age, count or
authentication assertion was reduced.

Removing those unused identity fixtures alone was insufficient: a later two-core
sweep (`verified-fast-two-core-1/`) still measured **23.897 s** (0 failed cases,
one timing offender). Temporary phase instrumentation then ran the whole Lobby
project 3× alone and Core+MonoGame+Lobby together 3× on two cores, all passing.
It did not reproduce the 20-second spike: combined R1 durations were 0.528 /
0.303 / 1.388 s, including disposal; the clock-jump phases varied 0.176 / 0.038 /
0.883 s. Thus a specific 20-second stack/wait is not claimed captured.

A test-only TimeProvider wrapper established a separate concrete source of
unrelated work: the 24-hour jump fires **2,880** default 30-second periodic
registry callbacks. This scenario tests **GameCreationStore.Create's pruning on
access**, not the registry's background sweep. Its test configuration now puts
that background cadence at 48 hours, beyond the unchanged jump. Registration
still performs its ordinary pruning (`ServerRegistry.cs:118-120`), and the store
still executes its real `Prune` on the final create (`GameCreationStore.cs:143`,
`:182-188`). The same counter probe confirms **zero** periodic callbacks with
this fixture. No production scheduling, clock, limit or calibration changes.
The phase/callback probes were removed from source; logs and diagnostic copies
remain in `r1-profiling*`, `r1-parallel*` and `r1-counting-*` evidence. The final
three full sweeps below test the bounded fixture, rather than waiving the earlier
21.100/23.897-second failures.

The next normal sweep (`complete-fast-normal/`) then found two actual shared-state
failures (zero timing offenders):
`P910TrainingIntegrationTests.Every_exit_restores_preferences_keymap_and_overlay`
(Modern, window-close: saved FireWeapon binding replaced by empty bindings) and
`P757ChatPaneTests.Compose_and_send_flow_through_the_production_handler_and_real_wire`
(StartChat did not activate).

MonoGame's training classes carried `[Collection("Training")]` **without** that
assembly containing a `CollectionDefinition` for Training. The definition already
existed in the headless client test project with `DisableParallelization=true`.
Moreover, the Jet console/palette training harnesses were outside the Training
collection while mutating/restoring `KeyBindingStore.Instance.Map` and process-wide
HUD/graphics settings paths. Saving and restoring in finally does not prevent
concurrent readers from observing the intermediate reset/filter.

MonoGame now links the existing collection definition; both Jet classes join it.
This is test isolation for real process-global state, not a tier/filter/quarantine
change. All cases and assertions still execute. The definition's old comment
incorrectly said other collections could run concurrently; that comment now
matches its existing `DisableParallelization=true` flag. Source witnesses:
`P910TrainingIntegrationTests.cs:150-182` sets `ActionFilter = _ => false` and
asserts the saved binding; `JetConsoleTests.cs:1165-1213` and
`JetAmberPaletteTests.cs:642-690` reset/reapply that same singleton in teardown;
`Game1.FlightInput.cs:1171` reads the live StartChat binding. This explains both
observed signatures. No production input or training behaviour changes.

Because this also changes training test scheduling, the prescribed artwork-backed
`tools/run-training-tests.sh --no-build --repeat 3` was invoked. Both affected Jet
classes also passed three isolated full-class repetitions (26 Amber + 44 Console
cases per repetition; **210/210 passed**, no skips). The full training result is
reported separately below; it is not substituted by the Fast verdict.

The next cohort (`proof-fast-*`) had no case failures, but its first two-core run
exceeded the limit in `CleanCloneSkipGuardTests.Artwork_and_gpu_touching_tests_skip_on_a_clean_clone`:
**15.589 s**. R1 was 0.783 s. This was the same source guard already near the
limit at 13.053 s in an earlier run, not a Jet or P968 regression.

The guard still **reads every test source file**. A conservative text precheck
avoids parsing only when no fact-producing spelling exists: TestArtRoot, Require,
Skip, SDL_VIDEODRIVER or GraphicsDevice. Any backslash forces the original parser
path, preserving decoded literals such as `"\u0053DL_VIDEODRIVER"`. Possible
resource files still follow the same Roslyn/member-graph rules. Empty direct-fact
graphs need no closure work; nonempty graphs retain the original algorithm. The
five-boolean immutable fact record is a readonly value type, avoiding allocations
on repeated unions. The original parsed-type-count assertion remains (>100), and
an additional source-file-count assertion (>100) checks the enumeration too.
No test file is excluded from the text read; no test execution is omitted.

A new Fast fixture accepts an ordinary class and a correctly guarded resource
case, and requires the exact four original diagnostics for missing artwork guard,
plain-fact skip, plain-fact GPU use and an escaped GPU marker. A separate
old-versus-new audit harness compared **all 1,568 repository test source files**:
**0 verdict differences / 0 violations**, with 1,683 old types and 1,176 parsed
candidate types after the precheck. The injected mixed/escaped fixture also gives
identical four diagnostics under both algorithms. Evidence:
`guard-equivalence-final.log` and its source/build log. This establishes verdict
equivalence, rather than weakening the clean-clone policy to meet a budget.

Audit plus regression fixture pass 3× alone on two cores after the final precheck
(the exact durations are in `alone-guard-final/`). The final Fast count is 9,826:
two Amber probe rows and one guard regression row were added to this checkout's
baseline. The earlier value-record/empty-graph-only attempt still exceeded the
limit at 15.794 s while the real-window training proof was running; that attempt
(`green-fast-two-core-1/`) also saw the unchanged P808 enrollment case at 14.089 s.
Those failed timing gates are retained. The final pair of pinned sweeps is run
only after the bounded training process ends, so its continuous window rendering
does not compete with that timing proof.

### Gate and evidence

- Solution build: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 145 projects built, each 0 warnings / 0 errors**.
- Changed tests: **54/54 cases passed**, 30 separate method-filtered processes
  (each of the original ten changed methods three times alone), plus the corrected
  P889 method 3/3 under two-core affinity. Amber was repeated 3/3 after retaining
  the literal `Assert.Equal(0, bytes)` spelling in its fact. R1 also passes 3/3
  alone on two cores after its fixture correction. Exact durations below.
- Full Fast: **PASS ×3**, exact CI filter and no artwork: 9,826 cases each,
  **0 failures / 0 timing offenders**. Normal run then two `taskset -c 0,1` runs.
- P968 Perf tier: **PASS, 4/4 rows**, 9.2 s; standard idle check passed on
  the final attempt (`final-perf-p968/`). The earlier `perf-p968/` invocation
  executed no tests because its unchanged idle check rejected load/CPU=0.88.
- Slowest 15 cases from each two-core sweep: listed below. Run 1 has none above
  6 s; run 2 flags the source audit (8.978 s) and a Python scorecard fixture
  (7.087 s), with source-based causes and the scheduling inference labeled.
- Final solution build: **0 warnings / 0 errors** (`build-final-source.log`).
  Glossary links: **965 paths valid**. Parity matrices validate. No production
  source, CI workflow, Fast limit, calibration, tier or quarantine changed.

| Changed case | Three isolated durations (s), all pass |
| --- | --- |
| `JetAmberPaletteTests.Amber_draw_allocates_zero_bytes_per_frame` | 0.130 / 0.143 / 0.129 |
| `JetAmberPaletteTests.Amber_window_catches_a_periodic_allocation(period: 1)` | 0.134 / 0.123 / 0.126 |
| `JetAmberPaletteTests.Amber_window_catches_a_periodic_allocation(period: 127)` | 0.043 / 0.040 / 0.043 |
| `JetConsoleTests.The_console_composition_allocates_zero_bytes_per_frame` | 0.088 / 0.079 / 0.084 |
| `JetFlightSymbologyTests.Jet_draw_allocates_zero_bytes_per_frame_with_the_full_symbology` | 0.086 / 0.083 / 0.084 |
| `JetFlightSymbologyTests.The_measured_window_still_catches_a_periodic_allocation(period: 1)` | 0.007 / 0.007 / 0.009 |
| `JetFlightSymbologyTests.The_measured_window_still_catches_a_periodic_allocation(period: 127)` | 0.074 / 0.066 / 0.070 |
| `JetSkinScaffoldTests.Jet_draw_allocates_zero_bytes_per_frame_on_a_fixed_model` | 0.080 / 0.077 / 0.075 |
| `JetStoresCautionTests.Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing` | 0.073 / 0.069 / 0.071 |
| `JetTargetLockTests.Draw_with_a_locked_leading_ordered_target_allocates_nothing` | 0.078 / 0.077 / 0.077 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: False, coalesced: False)` | 0.035 / 0.018 / 0.026 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: False, coalesced: True)` | 0.018 / 0.018 / 0.018 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: True, coalesced: False)` | 0.004 / 0.002 / 0.003 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: True, coalesced: True)` | 0.020 / 0.032 / 0.017 |
| `P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work_at_400_peers(indexed: False, coalesced: False)` | 1.933 / 1.872 / 1.943 |
| `P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work_at_400_peers(indexed: False, coalesced: True)` | 2.209 / 2.181 / 2.159 |
| `P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work_at_400_peers(indexed: True, coalesced: False)` | 0.030 / 0.032 / 0.037 |
| `P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work_at_400_peers(indexed: True, coalesced: True)` | 0.060 / 0.050 / 0.065 |
| `CleanCloneSkipGuardTests.Artwork_and_gpu_touching_tests_skip_on_a_clean_clone` | 3.027 / 3.517 / 3.424 |
| `CleanCloneSkipGuardTests.Resource_free_types_and_indirect_resource_violations_keep_their_verdicts` | 0.060 / 0.063 / 0.062 |
| `P889PoolSizingTests.Skirmish_sizing_override_reaches_the_server_config` | 0.015 / 0.014 / 0.015 |
| `R1LobbyReviewTests.Stale_game_requests_do_not_block_new_games_forever` | 0.910 / 0.914 / 0.906 |

### Final Fast sweeps (corrected tree)

| Run | Cases | Passed | Existing skips | Failed | Seconds | Calibrated limit (s) | Timing offenders | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| normal | 9,826 | 9,245 | 581 | 0 | 69.3 | 11.844 | 0 | PASS |
| two-core-1 | 9,826 | 9,245 | 581 | 0 | 88.9 | 11.536 | 0 | PASS |
| two-core-2 | 9,826 | 9,245 | 581 | 0 | 91.9 | 11.535 | 0 | PASS |

Each run uses the default four concurrent projects and the exact CI filter,
with `local.env` absent and artwork/native environment variables unset. The
existing 581 resource skips are unchanged; no test was skipped, quarantined or
retagged by this change. All runner discovery/coverage and infrastructure checks
pass. The two new Amber probe rows and guard regression row are included in the 9,826-case count.

### Slowest 15 cases in each final two-core sweep

**two-core-1**, `taskset -c 0,1`:

| Case (namespace omitted) | Seconds | Above 6 s / cause |
| --- | --- | --- |
| `CleanCloneSkipGuardTests.Artwork_and_gpu_touching_tests_skip_on_a_clean_clone` | 5.407 | — |
| `DiscoveryTests.Broadcast_discovery_collects_a_loopback_responder` | 3.300 | — |
| `Transport.P895UdpTransportTests.Udp_preference_falls_back_to_tcp_without_session_loss` | 3.011 | — |
| `P897ClientTransportTests.Real_discovery_and_session_use_selected_protocol(setting: Auto, advertiseUdp: True, expected: "tcp", blocked: True)` | 3.008 | — |
| `P897ClientTransportTests.Real_discovery_and_session_use_selected_protocol(setting: UDP, advertiseUdp: False, expected: "tcp", blocked: False)` | 3.007 | — |
| `Transport.P895bUdpOverheadTests.Pacer_uses_deadline_and_enqueue_wakes_blocked_worker` | 2.836 | — |
| `ModernHudTests.A_full_screen_of_radar_blocks_overflows_to_the_native_anchor_instead_of_throwing` | 2.621 | — |
| `Transport.TcpTransportLivenessPolicyPoolTests.Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties` | 2.523 | — |
| `Transport.TcpTransportLivenessPolicyThreadPerPeerTests.Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties` | 2.521 | — |
| `Transport.TcpTransportSendDropSafetyThreadPerPeerTests.Human_marked_peer_survives_the_bot_no_write_progress_window` | 2.132 | — |
| `Transport.TcpTransportSendDropSafetyPoolTests.Human_marked_peer_survives_the_bot_no_write_progress_window` | 2.109 | — |
| `FedMsgDispatchCoverageTests.EveryExpectedServerSentMessage_IsConsumed_NotUnknownFmid` | 2.087 | — |
| `Transport.P670SendStallTests.Three_paused_readers_do_not_starve_sixteen_healthy_peers_on_one_worker_and_drop_for_no_progress(batchWrites: True)` | 2.086 | — |
| `Transport.P670SendStallTests.Three_paused_readers_do_not_starve_sixteen_healthy_peers_on_one_worker_and_drop_for_no_progress(batchWrites: False)` | 2.062 | — |
| `Transport.P671KeepAliveTransportTests.SilentPeer_IsDroppedBy_IdleTimeout` | 2.047 | — |

**two-core-2**, `taskset -c 0,1`:

| Case (namespace omitted) | Seconds | Above 6 s / cause |
| --- | --- | --- |
| `CleanCloneSkipGuardTests.Artwork_and_gpu_touching_tests_skip_on_a_clean_clone` | 8.978 | **FLAG** — Whole-repository source reads, candidate Roslyn parsing and member-graph closure share two cores with the other projects. `tests/Allegiance.Core.Tests/CleanCloneSkipGuardTests.cs:45-71,139-160,169-215`. |
| `P674ParityScorecardTests.DoneVisual_rows_get_full_credit_and_appear_in_the_visual_column` | 7.087 | **FLAG** — One Python subprocess, redirected async reads and synchronous waits; scheduling/I/O completion under contention is the inferred cause of the outlier. `tests/Allegiance.Core.Tests/P674ParityScorecardTests.cs:16-32,86-101`. |
| `DiscoveryTests.Broadcast_discovery_collects_a_loopback_responder` | 3.057 | — |
| `DockedTeleportAndChatSnapshotConcurrencyTests.Chat_walk_and_teleport_snapshot_survive_high_concurrency_under_gate` | 3.027 | — |
| `P897ClientTransportTests.Real_discovery_and_session_use_selected_protocol(setting: UDP, advertiseUdp: False, expected: "tcp", blocked: False)` | 3.011 | — |
| `P897ClientTransportTests.Real_discovery_and_session_use_selected_protocol(setting: Auto, advertiseUdp: True, expected: "tcp", blocked: True)` | 3.009 | — |
| `Transport.P895UdpTransportTests.Udp_preference_falls_back_to_tcp_without_session_loss` | 3.009 | — |
| `Transport.P895bUdpOverheadTests.Pacer_uses_deadline_and_enqueue_wakes_blocked_worker` | 2.833 | — |
| `Transport.TcpTransportLivenessPolicyPoolTests.Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties` | 2.524 | — |
| `Transport.TcpTransportLivenessPolicyThreadPerPeerTests.Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties` | 2.521 | — |
| `ModernHudTests.A_full_screen_of_radar_blocks_overflows_to_the_native_anchor_instead_of_throwing` | 2.423 | — |
| `LoadoutV2LayoutTests.Geometry_properties_hold_across_scales_counts_and_scrolls(width: 1600, height: 900, style: Modern)` | 2.295 | — |
| `FedMsgDispatchCoverageTests.EveryExpectedServerSentMessage_IsConsumed_NotUnknownFmid` | 2.266 | — |
| `Transport.TcpTransportSendDropSafetyPoolTests.Human_marked_peer_survives_the_bot_no_write_progress_window` | 2.090 | — |
| `Transport.TcpTransportSendDropSafetyThreadPerPeerTests.Human_marked_peer_survives_the_bot_no_write_progress_window` | 2.085 | — |

### Affected-case timings across the final three sweeps

| Case | Normal (s) | Two cores #1 (s) | Two cores #2 (s) |
| --- | --- | --- | --- |
| `JetAmberPaletteTests.Amber_draw_allocates_zero_bytes_per_frame` | 0.037 | 0.030 | 0.031 |
| `JetConsoleTests.The_console_composition_allocates_zero_bytes_per_frame` | 0.012 | 0.010 | 0.010 |
| `JetFlightSymbologyTests.Jet_draw_allocates_zero_bytes_per_frame_with_the_full_symbology` | 0.030 | 0.014 | 0.013 |
| `JetSkinScaffoldTests.Jet_draw_allocates_zero_bytes_per_frame_on_a_fixed_model` | 0.010 | 0.019 | 0.034 |
| `JetStoresCautionTests.Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing` | 0.025 | 0.022 | 0.016 |
| `JetTargetLockTests.Draw_with_a_locked_leading_ordered_target_allocates_nothing` | 0.024 | 0.032 | 0.033 |
| `P889PoolSizingTests.Skirmish_sizing_override_reaches_the_server_config` | 0.011 | 0.000 | 0.001 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: False, coalesced: False)` | 0.032 | 0.086 | 0.078 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: False, coalesced: True)` | 0.036 | 0.082 | 0.077 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: True, coalesced: False)` | 0.008 | 0.074 | 0.023 |
| `P968PublisherStreamIndexTests.Reliable_backlog_counting_rules_hold_at_100_peers(indexed: True, coalesced: True)` | 0.051 | 0.066 | 0.103 |
| `R1LobbyReviewTests.Stale_game_requests_do_not_block_new_games_forever` | 0.254 | 0.423 | 0.703 |

All six allocation gates again report exactly 0 B. Full evidence is in
`ci-fast-normal/`, `ci-fast-two-core-1/`, `ci-fast-two-core-2/` and
`final-perf-p968/` under `<lane evidence dir>/P1049b/`; the earlier failed
diagnostic sweeps and idle rejection remain alongside them.

### Additional training validation — full gate is not green

The original full command, with artwork configured, passed the headless client
project **126/126** and then ran the MonoGame real-window lessons. Four M2 window
variants completed (Classic/Modern × muted/missing-audio). Both reminder variants
failed with **`M2 9.2: native prompts missing or out of order`**, the same
pre-existing real-window observer family documented in `JET_AMBER_P1041.md` §9.2
and the P945 flake entries. The proof runs in a separate child process and no
production/observer/lesson code changed here. The parent collection fix cannot
change that child's training logic; load can still expose the existing timing
sensitivity. The assertions remain intact.

The full invocation reached its explicit **40-minute process bound** (exit 124)
during the next window, before a complete MonoGame TRX or three full repetitions
could finish. This is **not a passing full training gate** and is not claimed
complete. No window case was filtered out or quarantined to change that verdict.
All children were stopped before the final two pinned Fast runs; those timing
runs did not overlap the training workload. The normal Fast run overlapped the
bounded training attempt and passed as recorded above.

Two additional complete headless training selections (the script's unchanged
filter, artwork, serial runsettings) finish the headless evidence at **126/126
×3, 0 failures / 0 skips**, including
`TR5TrainingMissions810Tests.Mission8_RunsToCompletion_AgainstTheRealEmbeddedHost`
**3/3** (0.234 / 0.402 / 0.147 s). These are supplemental controls, not a
replacement for the incomplete MonoGame/window gate. The global-input readers
and restore routes are also covered by all three final Fast sweeps, with both
Classic and Modern cases passing. Evidence: `training/`, `training-headless/`,
`alone-skin-classes/`; window child logs/captures in the lane's generated
`p940/windows/` and `p931/windows/` evidence.


The no-artwork runs temporarily move the ignored `local.env` into the evidence
scratch directory, unset `ALLEGIANCE_ART`, `ALLEGIANCE_ARTWORK_PATH` and
`ALLEGIANCE_NATIVE_SRC_PATH`, and use exactly
`Requires!=Artwork&Requires!=NativeSource&Requires!=Gpu&Requires!=MeshyPack&Lane!=LiveTiming`.
The original ignored local configuration was restored after the gates.
Every build/test sets `NUGET_HTTP_CACHE_PATH` to task-local scratch. Build commands
use `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`;
`build-tools.sh` receives a temporary DOTNET wrapper adding those switches.

**Native intent.** No native game rule or visual output changes; no new parity CSV
row is needed. The existing rendering and publisher ordering/counting contracts
are asserted unchanged.

**Modern approach.** Measure fixed-state allocation reuse over actual frame
periods; exercise deterministic counts independently of repeated pool scheduling,
and retain crowded parallel timing measurements in Perf.
