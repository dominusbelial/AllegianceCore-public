# P1056 — timer-bound flakes: P739 warp-bomb one-ULP bound and P660 countdown

Worktree `<lane worktree>` on branch `hermes/P1056` (linked worktree of the public AllegianceCore
clone). All changes are left in the working tree; no commits, no pushes, no stash. Native
references are relative to the read-only `/srv/corpus/Allegiance/src` checkout (`local.env`
`ALLEGIANCE_NATIVE_SRC_PATH`). Evidence paths are cited as `<lane evidence dir>` and
`<lane worktree>` per the public-repo rule. **No production code changed** — both fixes are in the
tests (item 1's production reading is confirmed correct; item 2's production sampling is native-correct
and the test sampled at the wrong point).

Status: **complete** — items 1–3 done; every count below is from a real run in this lane. Raw run logs
and the Fast-tier `summary.json`/`summary.txt` are in `<lane evidence dir>/P1056/` (outside the repo).

| Item | Status |
| --- | --- |
| 1. P739 warp-bomb deadline (one ULP) | **fixed** (test only) |
| 2. P660 countdown 14.9 s | **fixed** (test only) |
| 3. Proof (20× alone, 3× parallel class runs, fast tier, ledger) | **done** |

## Item 1 — P739 warp bomb: production site, types, native rule, root cause

**Production site.** `Missile.WarpBombTime` (`src/Allegiance.Core/Sim/Missile.cs:24`) is set in
`Sector.ResolveAlephMissileContact` (`src/Allegiance.Core/Model/Sector.cs:4588-4592`):

```
double timeExplosion = now + EffectiveWarpBombDelay;   // Sector.cs:4590
missile.WarpBombTime = timeExplosion;                  // Sector.cs:4591
ScheduleWarpBomb(timeExplosion, warp, missile.MissileType);   // Sector.cs:4592 → :4665
```

`now` is the collision chunk's `timeUpdate` (`Mission.Update` → `sector.Update(timeUpdate)`,
`Mission.cs:1106/1111`); a 1/20 s tick has `deltaT = 0.05 < Mission.TimeIncrement = 0.25`
(`Mission.cs:667`), so `n = 1` and `timeUpdate == the mission clock the test reads as
m.LastUpdate` (`Mission.cs:1122`).

**Types.** `WarpBombTime` is `double?`. `EffectiveWarpBombDelay`
(`Sector.cs:2122-2123`) returns `double` from `Constants.FloatConstants[32]` (`float[]`), i.e. the
loaded float constant 32 (`Igc/igc.h:98`) widened exactly to double. The default is 15.0
(`Sector.cs:2120`, native `Igc/missionigc.cpp:2726,2777`). There is no float loss: a float→double
widening of 5/7/10/15 is exact, and the sum is one double addition on a mission clock of 5–7 s.

**Native rule.** `FedSrvSiteBase::WarpBombEvent` (`FedSrv/FedSrv.CPP:12543-12562`) computes
`Time timeExplosion = pwarp->GetLastUpdate() + m_pfsMission->GetIGCMission()->GetFloatConstant(c_fcidWarpBombDelay);`
and uses it both for the `FM_S_WARP_BOMB` field and for `pwarp->AddBomb(timeExplosion, pmt)`. The
native time type is `class Time` holding `uint32_t m_dwTime` in **integer milliseconds**
(`zlib/ztime.h:20-22`); `Time::operator+(float)` adds `(int)floor(d * 1000 + 0.5)`
(`ztime.h:144-149`), so native's deadline is `lastUpdateMs + round(delay·1000)` — quantised to
1 ms. The blast fires when `plink->data().timeExplosion <= now` in the warp's update
(`Igc/warpIGC.h:56-59`). The port's `ScheduledWarpBomb` consumption applies the same
`<=` comparison on the double deadline (`Sector.cs:4639`), and the wire field is
`(uint)(timeExplosion * 1000.0)` (`Server.Combat.cs:932-935`). The port therefore differs from native
only in the sub-millisecond quantisation of a clock native already keeps in whole milliseconds
(≤ 1 ms, invisible to players and below one 20 Hz tick); no production change is warranted and the
loaded delay, blast scheduling and wire deadline are unchanged.

**Root cause (confirmed).** Claude's reading is correct. The deadline production stores is exactly
`armedAt + (double)delay`, and the old assertion re-derived the delay by subtracting the clock:
`Assert.InRange(captured - armedAt, delay - .1, delay)` (`tests/Allegiance.Core.Tests/P739TimerLiveTests.cs:81`,
pre-change). `(c + d) - c` is not `d` in IEEE 754 when the sum rounds up. Instrumented live runs in
this lane armed at `6.299999999999986`, `5.09999999999999`, `5.699999999999988`, `6.8999999999999835`
and `7.099999999999983` (delay 5) and at `6.8999999999999835` (delay 7) — the accumulated 1/20 s tick
grid, each value 11–19 ULP (≈ 1.0–1.7e-14 s at that magnitude) below its nominal multiple of
0.05 — and the derived value was exactly `delay + 8.88e-16`, i.e.
`5.0000000000000009` / `7.0000000000000009`, outside the inclusive upper bound. Nothing was late:
`captured` was bit-for-bit the production expression. The error is bounded by the ULP of the SUM
(which grows with the mission clock; at 2636 s it is 4.5e-13), not of the delay, so no fixed epsilon
is defensible.

**Change (tests only).** `tests/Allegiance.Core.Tests/TimerBounds.cs` (new) holds the production
expression and the comparison used by both the live test and the deterministic regression:
`WarpBombDeadlineMatches(captured, clock, delay)` is `captured == clock + (double)delay` — exact, and
strictly stronger than the old band (a bomb one 0.05 s tick late **or** early now fails; the live
test's `TimeExplosion`/warp/type/cross-mission assertions are untouched). The live test calls it with
a diagnostic message showing `armedAt`, the production deadline and the re-derived value.

**Deterministic reproduction (no server).** `tests/Allegiance.Core.Tests/P1056TimerBoundsTests.cs`:
- `Re_deriving_the_delay_from_the_deadline_is_one_ulp_over_the_loaded_delay(clock, delay)` with the
  exact clocks that round up — `6.299999999999986` + 5f and `6.8999999999999835` + 7f. Run with the
  old rule in `TimerBounds` it failed at `Assert.True(...)` (`TimerBounds` → `InRange` rejected
  `5.0000000000000009` / `7.0000000000000009`); with the fix it passes. **RED → GREEN**.
- `A_bomb_armed_one_tick_late_is_rejected` / `..._early_is_rejected`: a ±0.05 s deadline is
  rejected, so the exact comparison cannot hide a real late (or early) bomb.

## Item 2 — P660 countdown: where it is armed, where the test sampled, native rule

**Production (correct; native-shaped).** With `AutoNextMatch` disabled the ready-up path starts the
next countdown inside the game-over rotation itself: `RotateMissionAfterGameOver` →
`TryAutoStart(tail.Fresh)` at the end of `FinishPendingRotation` (`Server.Lobby.cs:1232`) →
`StartCountdown(mission, BriefingCountdownSeconds)` (`Server.Lobby.cs:1959`) →
`StartCountdownAt(mission, _simNow + countdownSeconds)` (`Server.Lobby.cs:2097-2098, 2100-2123`).
The absolute target is recorded at the tick the countdown begins. `AdvanceStartCountdowns` completes
it only on a tick with `now > target` (strict; `Server.Lobby.cs:2152-2155`), and
`CompleteStartCountdown` sets `mission.CountdownStart = target` (`Server.Lobby.cs:2227`).
Native: `CFSMission::StartCountdown` sets `timeStart = Time::Now() + fStartCountdown`
(`FedSrv/fsmission.cpp:1926-1938`) and the per-tick start test is `now > timeStart`
(`FedSrv.CPP:8235`). Same absolute-deadline rule, same 15 s.

**Root cause (confirmed).** The test sampled the briefing clock *after* waiting for client
observations: `briefingAt = server.SimNow` (`P660NextMatchLiveTests.cs:130`, pre-change) sat after
the pump that waits for the migrated peers to see their fresh ship (`:113-117`) and, for the enabled
rows, after the server-side pump to `Starting` (`:127`). For `enabled: False` the countdown is armed
**inside `End(server)`** at the rotation clock (`late`), so the peer-reseat pump runs entirely inside
the briefing. Its load-dependent lag — observed 0, 0.0125 and 0.05 s (and up) — was charged to the briefing and
the measured span became `15 − lag`. Instrumented parallel runs (6 processes,
load average ≈ 25): for the disabled rows `briefingAt − late` was 0 in 5 observations, 0.0125 s in 4
(dilation "auto", 0.25 scale → 0.0125 s ticks) and 0.05 s in 1; the observed spans were
`15.00000000005457` and, with one lagging tick, `14.950000000054388`. One further 0.05 s tick of the
same lag gives `14.90000000005457`, i.e. the ledgered `14.900000000054206` signature to within
4e-13 (a different completion-tick ULP); that is the arithmetic behind the `enabled: False,
dilation: "off"` failure. The enabled rows were unaffected
because their briefing start is captured from the server's own `Starting` transition.

**Change (test only).** The briefing clock is now sampled on the server clock at the tick the
countdown is armed, before any client pump: `double briefingAt = enabled ? double.NaN : server.SimNow;`
right after `End`, and `briefingAt = server.SimNow` after the enabled path's server-side pump to
`Starting`. The 15 s span is then deterministic: the completion tick is exactly the first tick past
the target (`SimNow − briefingAt = 15 + one tick`). Bounds `[14.94, 15.06]` are unchanged — nothing
was widened; the old failure was under-measurement, and a genuinely short/long countdown still fails.

**Deterministic reproduction (ordered steps, no busy loop, no synthetic load).**
`P660NextMatchLiveTests.Disabled_ready_up_countdown_is_armed_on_the_rotation_tick_and_completes_one_tick_past_its_deadline`:
boot → seat two loopback peers with preserved ready flags → jump the clock to `2636.1478201210366` →
`End` → assert the mission is `Starting` and `_startCountdownDeadline[fresh] == late + 15` exactly →
tick exactly **three** times (the lagging observation the old sample point represented) and assert
`deadline − SimNow = 14.85` (below the 14.94 floor — this is the failure mechanism) →
`Pump` to `Started` and assert `SimNow − late ∈ [15, 15.06]` and `SimNow − deadline ∈ [0, 0.06]`
(strict expiry, one tick past target). No wall-clock wait enters any assertion.

## Item 3 — proof and ledger

**Build gate.** Whole-solution `dotnet build AllegianceCore.slnx` (with
`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`): **0 warnings / 0 errors**.
`tools/build-tools.sh`: **all 148 tool projects built**, exit 0 (each project's build reported
0 warnings / 0 errors; the script fails on any error). No
production file was touched, so the Server AOT publish and the server/transport suites are not
triggered (the lane rule gates them on `src/Allegiance.Server` or `src/Allegiance.Core` changes).

**New tests 3× alone** (fresh process per invocation):
`P1056TimerBoundsTests` 3/3 runs (6 cases each: 2 round-up + 2 late + 2 early) and
`P660NextMatchLiveTests.Disabled_ready_up_countdown_is_armed_on_the_rotation_tick_and_completes_one_tick_past_its_deadline`
3/3 — 21 case executions, 0 failures. RED→GREEN for the P739 regression is in item 1.

**Each fixed test 20× alone** (fresh `dotnet test` process per run, method filter, no build):

| Fixed test | Runs | Cases per run | Case executions | Failed |
| --- | ---: | ---: | ---: | ---: |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | 20/20 | 3 (delay 5/7/10) | 60 | 0 |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | 20/20 | 4 (enabled × dilation) | 80 | 0 |

Ran one at a time with the host's ambient load at 18–26 (other lanes active). Before the fix the
P739 filter failed 1–2 rows in 2 of the first 3 runs and in 5 of 10 consecutive later runs; the
P660 row was reproduced deterministically instead (its load lag was observed directly, 0–0.05 s, so
its `[14.94, 15.06]` floor was only crossed at ≥ 2 lagging ticks — the 14.9 signature).

**Additional 2-process concurrency check** (both whole classes running at once, all methods; a
supplementary load probe beside the three full sweeps):

| Run | `P739TimerLiveTests` | `P660NextMatchLiveTests` |
| ---: | --- | --- |
| 1 | 5/5 passed, 1 s | 8/8 passed, 24 s |
| 2 | 5/5 passed, 1 s | 8/8 passed, 22 s |
| 3 | 5/5 passed, 1 s | 8/8 passed, 21 s |

15/15 and 24/24 case executions, 0 failures.

**Three full `tools/run-tests.sh --tier fast` runs** (`--no-build`, `--parallel 4` default — the seven
projects run in that bounded pool, so both classes execute concurrently with the other five; this is
the `--parallel` shape the Fast tier uses):

| Sweep | Executions | Seconds | Passed | Failed | Not executed | Calibrated limit | Timing offenders |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 15,550 | 634.6 | 15,541 | 2 | 7 | 13.59 s | 5 |
| 2 | 15,550 | 652.0 | 15,539 | 4 | 7 | 11.87 s | 13 |
| 3 | 15,550 | 627.7 | 15,539 | 4 | 7 | 11.54 s | 6 |

Both task families and their new regressions are green in **every** sweep — 19/19 case executions each
time: `P660NextMatchLiveTests` 8/8 (1.94–4.56 s), `P739TimerLiveTests` 5/5 (warp-bomb rows
0.21–0.26 s), `P1056TimerBoundsTests` 6/6 (< 1 ms). 9/9 infrastructure checks and the timing guard
passed in all three.

The ten failure sightings (eight distinct methods) are **not this task's**; each distinct method
passed **3/3 alone** in fresh processes (ledgered in `docs/TEST_FLAKES.md`):

| Sweep | Case | Symptom | Alone |
| ---: | --- | --- | --- |
| 1 | `Allegiance.Core.Tests.AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | 2 `AsteroidMined` events for asteroid 248 after capacity | pass / pass / pass (1 s each) |
| 1 | `Allegiance.ClientCore.MonoGame.Tests.P1017BuyFlowLiveTests.Money_change_while_docked_flips_need_and_buy_Live` | `Expected: Muted, Actual: Worse` | pass / pass / pass (2 s each) |
| 2 | `Allegiance.ClientCore.Tests.DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | B never latched A's death in 2,500 ticks (ledgered family, out of scope) | pass / pass / pass |
| 2 | `Allegiance.ClientCore.Tests.BS1BenchmarkSharingTests.Timeout_retains_payload_and_retries_without_waiting_forever` | 6.43 s outside `[1.5, 5]` | pass / pass / pass |
| 2 | `Allegiance.Core.Tests.Transport.P1051ResumeAuthenticationTests.Valid_WS_resume_retires_both_prelogon_deadlines_and_restores_human_classification` | `Expected: 8096, Actual: null` | pass / pass / pass |
| 3 | `Allegiance.ClientCore.MonoGame.Tests.P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | 1,472 B (the ledgered Jet-pair allocation class) | pass / pass / pass |
| 3 | `Allegiance.Core.Tests.WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | transited `ShipStatusMessage` absent from the peer collection | pass / pass / pass |
| 3 | `Allegiance.Core.Tests.AsteroidCombatLiveTests.Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate` | friendly fire did not damage the asteroid | pass / pass / pass |
| 3 | `Allegiance.Core.Tests.P817NativeMinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station` | got ore 0 at 30.73 s (chronic slow NPC case) | pass / pass / pass |

Five, thirteen and six Fast timing offenders over the per-sweep limits are also non-task and already
chronic/load-driven (12.1–30.7 s; all **Passed**): `P796VariedLoadoutTests`, `SC2NaniteSupportTests`,
`P814GunnerTests.P814b`, `P750NativeAsteroidTypesTests`, `P999CivStrategyTests`, `P942Research*`,
`P1000CivProfileTests`, `P704TechLoadoutTests`, `P832bProsperityTests`, `SC3CloakPolicyTests`,
`P877MineDamageLiveTests`, `P817NativeMinerDecisionCycleLiveTests`. No bound, tier or quarantine
changed.

**Ledger.** `docs/TEST_FLAKES.md` gains a **P1056** section at the top with both root causes and the
commit-free fix description (file:line), marks the two older "remains unresolved" statements resolved
(tests-only, production unchanged), and records the sweep above.

## Native intent / Modern approach

**Native intent.** Both rules are absolute-deadline comparisons on native's integer-millisecond
`Time` clock: `WarpBombEvent` stores `warp->GetLastUpdate() + float slot 32` (`FedSrv.CPP:12546`) and
the warp's update blasts when `timeExplosion <= now` (`Igc/warpIGC.h:56-59`); the start countdown
stores `timeStart = Time::Now() + fStartCountdown` (`fsmission.cpp:1926-1938`) and starts only when
`now > timeStart` (`FedSrv.CPP:8235`). Neither rule is wall-clock-driven and neither adds a tick of
slack.

**Modern approach.** The port keeps its own double-seconds simulation clock (`_simNow`, fixed
1/20 s period, `Server.Broadcast.cs:138`) and absolute deadlines derived from it: `WarpBombTime` is
one double addition in the collision chunk (`Sector.cs:4590`), and the countdown target is
`_simNow + 15 s` at the arming tick (`Server.Lobby.cs:2097-2098`). Behaviour is identical to native at
native's own 1 ms resolution; the only difference is the sub-millisecond quantisation of a clock the
port keeps as a double. Both flakes came from the **harness**, not from this model: one compared a
non-round-tripping subtraction against the loaded delay, the other sampled the briefing clock from a
client-observation point that lags under load. The assertions now compare production's own
expressions, so a real late bomb or a short/long countdown still fails.

