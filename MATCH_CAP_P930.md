# P930 — configurable showcase match cap

Implemented in the working tree on `codex/p930`. No commits, pushes, remote-host
access, bot strategy changes, new services, environment settings for deployment,
or client UI changes. Default cap: **3600 simulation seconds**; `0` disables it;
other values must be finite and in `[60, 86400]`.

## Native mapping and implementation

Native citations were rechecked against the read-only native source during this
lane. The cap is preset orchestration using an existing native game mode;
3600 seconds is a showcase policy, not a change to native defaults.

| Rule | Native reference | Port |
|---|---|---|
| Countdown length in seconds; zero means unlimited | `Igc/igc.h:1162,1305,1526-1528` | `MissionGoals.GameLengthSeconds`; `Mission.CountdownDuration` |
| Start epoch is the scheduled game start | `FedSrv/fsmission.cpp:1931-1936,2339` | `Server.Lobby.cs:2172` assigns `CountdownStart` at STAGE_STARTED |
| Expire strictly after the duration | `FedSrv/fsmission.cpp:2337-2342` | `Mission.Countdown.cs:17`; existing periodic expiry |
| Conquest → territory → prosperity bucket time → artifacts → flags → kills/fewer deaths; unresolved tie draws | `FedSrv/fsmission.cpp:2347-2504` | Existing `Mission.EvaluateCountdown`; unchanged ranking |
| Draw winner is native NA | `FedSrv/fsmission.cpp:3084` | `GameOver.NA == -1` |

`src/Allegiance.Server/ServerConfig.cs:20,35` defines and validates
`showcase.matchCapSeconds`; source-generated JSON handles it, including the
absent-property default. No reflection serialization was added.

`src/Allegiance.Server/Server.Lifecycle.cs:1317,1338` uses the existing
`IsShowcasePreset` predicate, selects the match's authored/rotated goals, then
sets the cloned goals' length to the smaller nonzero authored length and cap.
Applying it before map/world assembly covers boot, P439 fresh rebuilds, file and
generated map rotation, goal rotation, civilization rotation, and any team count.
It consumes no random draws and does not mutate game or rotation configuration.
The native clock, HUD-10 goal advertisement, payday suppression, score result,
and game-over/rotation paths remain the existing production implementation.

`Server.Lobby.cs:733` now preserves `playerCreated` on **every** rebuild. The
previous call passed it only when map rotation existed, which would otherwise
allow a rebuilt human game to acquire showcase goals/cap. Ordinary configured
games, LAN hosts and player-created worlds keep their own countdown lengths.
An enabled cap would also apply to offline skirmish through the existing
`IsShowcasePreset` classification, so Claude's review sets `MatchCapSeconds = 0`
in `SkirmishOptions.CreateConfig` (pinned in `SP6GameSpeedTests`): a human
skirmish keeps open-ended native games. Training does not use that classification.

## Finale interplay and receipts

`Server.Showcase.cs:34-35` clamps an armed research finale to
`min(now + finaleTimeoutSeconds, CountdownStart + effective length)`. This also
respects an authored countdown when the automatic cap is disabled. An earlier
finale still calls `EvaluateCountdown(deadlineReached: true, ...)`, choosing the
actual ranked winner rather than the requesting side.

`Server.Showcase.cs:47-51` gives the periodic countdown path ownership of a
coincident finale/countdown deadline, including the native strict-equality
boundary. `Server.Periodic.cs:47-59` uses ordinary expiry ranking and removes the
armed finale before declaring the result. The existing stage guard and immediate
fresh-mission replacement prevent duplicate game-over delivery. Both deadline
paths pass `stationKillsDecideDeadline`; its P914 preset-only station-kill
criterion remains between native goal criteria and ship kills. Native ranking
and defaults were not changed.

`Server.Lifecycle.cs:1278` appends `cap=<effective seconds>` to matchup details
(and therefore their game-over copy). `cap=0` means disabled/inapplicable;
a shorter authored countdown is reported as the effective cap. On capped
countdown expiry, the existing `[finale] deadline winner` line remains intact and
`Server.Periodic.cs:53` adds, for example:

```text
[finale] cap winner side=-1 by=draw values=0:kills=0/deaths=0,1:kills=0/deaths=0 elapsed=60.05
```

`side=-1` is native NA. A unique winner instead logs its side ID and the actual
criterion. Elapsed time is simulation time since this match's start, formatted
with invariant culture. An earlier standalone research finale retains its
existing deadline receipt and does not emit a cap-ending receipt.

## Bot audit and documentation

A full `tools/allegiance-mcp` search found no reads of `IsCountdownGame`,
`GameLengthSeconds`, or countdown remaining time. Existing goal-specific policies
read conquest/flags/artifacts/kills/prosperity fields, which the cap preserves.
The research snapshot/tactics use `world.SimulationTimeSeconds`
(`BotManager.Research.cs:119,222`); `ResearchCommander.Prosperity.cs:21` switches
only on prosperity. The harness stopwatch drives scheduling and finale maturity
(`BotManager.cs:240`, `BotManager.Finale.cs:825-835`), and turret updates estimate
server simulation time from ping echoes (`BotManager.TurretGunner.cs:212-217`).
None reads the countdown clock. No bot code changed. The two-match loopback test
exercises actual MCP clients, research mode, game-over delivery and migration.

Updated `docs/GUIDE.md`, `GLOSSARY.md`, the existing SIM-0133 parity row and all
three current `tools/p832*.json` showcase examples. Compose contains no showcase
block, so it requires no change. The two existing P832g goal-rotation live tests
explicitly disable the cap to retain their authored-goal/native-victory isolation;
P930 separately covers cap-plus-rotation behavior.

## Verification and evidence

Raw local evidence is retained under [`.scratch/p930/`](.scratch/p930/).
Compact results are in
[`docs/reports/P930_assets/verification.json`](docs/reports/P930_assets/verification.json).
`local.env` was present and sourced for artwork/native paths. No private media
was added. Builds/tests use the configured local .NET installation. NuGet cache,
temporary files, AOT output and profile writes are redirected into task scratch
to respect the sandbox and lane scratch rule. Parking runs explicitly enable
server GC and parallel broadcast building; native ship updates, pooled transport,
immutable snapshots, encode-once and native GotoPlan use the P887 baseline.

The exact new live regression was first run against unchanged server binaries:
`Research_bots_complete_two_capped_matches_and_restart_the_clock` failed after
40 seconds wall time: match 1 was still Started, start `15.55`, current sim time
`180.20000000000672` (164.65 seconds of match play). After implementation, the
same test passed: two separate cap receipts and game-over receipts, both clocks
lasting 60–60.05 simulation seconds, and a new STAGE_STARTED epoch for match 2.
It uses four actual combat bots plus probe/commander connections, 10 ms paced
20 Hz TickSim calls, normal research mode, and no synthetic finale request or
forced game-over. Expected cap length is supplied through ordinary JSON so the
same regression source runs on the base.

New unit tests in `P930MatchCapTests` cover:

- Source-generated defaults/roundtrip, zero/off, fractional and endpoint values,
  and rejection of nonfinite/out-of-range settings.
- Preset-only application; unchanged ordinary explicit clocks; smaller explicit
  and rotated countdowns; configuration immutability.
- Player-created initial assembly and fixed-map rebuild, plus LAN exclusion.
- Four P439 rebuilds with file/generated maps, real civ rotation, goal rotation
  and three sides; every fresh clock starts independently.
- Earlier finale, disabled cap with/without an authored clock, late finale clamp,
  strict equality, simultaneous expiry, one GAME_OVER packet/receipt, ranked
  winner independent of the requesting side, and a true tie with NA/draw.

Light-gate results:

| Check | Result |
|---|---|
| Whole solution build | 0 warnings / 0 errors (`solution-build-final.txt`) |
| Every tool project | 116 projects; all builds 0/0 (`build-tools.txt`) |
| Release linux-x64 self-contained native AOT | Passed; no IL2026/IL3050 (`aot-final.txt`) |
| Final P930 tests, including the two-match Live test | 26/26 × 3; skipped 0 |
| Affected P832g/P926 tests, including Live | 29/29 × 3; skipped 0 |
| Affected P739/P914/P927/P929/P832a tests | 51/51 × 3; skipped 0 |
| Glossary links / parity scorecard / whitespace | Passed |

One final complete Fast-tier run covered **13,100 cases** in 512.4 seconds:
**13,087 passed, 10 failed, 3 skipped**, plus four passing cases over the calibrated
10-second limit. The gate remains **failed/qualified**. The three skips are the
optional private Meshy pack, not missing artwork; new/affected tests skipped zero.
No second complete Fast run was made. A preliminary attempt was interrupted to
include the player-created rebuild fix and redirect read-only profile writes;
its subprocesses drained before the final runner acquired the shared lock. It is
retained separately and is not counted as a completed full suite.

The final run has reproduced five already-ledgered `PlayerBundlePathsTests`
fixture failures: required in-repository temporary directories find the real
ancestor `local.env`. Three separate class runs reproduce exactly five failures
and four passes each. The unchanged three-peer visibility test failed a seating
assertion once and passed 3/3 separate exact-method runs. The writable scratch
profile resolved the preliminary profile-restoration obstruction. See
`docs/TEST_FLAKES.md`; these qualifications do not turn the full gate green.

The remaining failures were an extra queued asteroid-mining receipt, two missing
profiler allocation assertions, and the existing null-target defender assertion.
Mining and all four P838 profiler cases passed 3/3 separate focused invocations.
The defender assertion still failed 3/3 and remains an open pre-existing fixture
failure (also present in earlier baseline controls recorded in the ledger).

Timing-only cases all passed 3/3 below ten seconds in focused invocations:
all-hull GPU proof 3.865–4.250 s; turret-carrier preference 3.713–3.854 s; both
Nanite support civilization cases 3.430–4.433 s; native warp-exit wobble
9.236–9.357 s. No limit, assertion, tier, flight rule or bot policy was changed.

The parking comparison uses dispatch-base binaries and final server binaries on
the same host with the baseline settings above. Final sample run IDs are
3–7; early exploratory IDs 1–2 preceded the creator-flag rebuild correction and
are retained separately. Samples 6–7 were scheduled before their outcomes were
known, to obtain five observations of the final binary. Paired runs overlap;
this is a functional parking rate comparison, not a throughput or arrival-time
benchmark. Host load samples and binary hashes are in the compact evidence.

Final parking result: **5/5 base passes and 5/5 changed passes**,
with zero skipped cases. These are functional checks under shared-host load;
the sample does not establish a performance difference.

| Final run | Base: arrivals / longest target idle | Changed: arrivals / longest target idle |
|---|---|---|
| 3 | 24/30 / 2.006 s | 24/30 / 0.201 s |
| 4 | 24/30 / 4.310 s | 24/30 / 0.000 s |
| 5 | 24/30 / 3.007 s | 24/30 / 1.914 s |
| 6 | 24/30 / 0.806 s | 24/30 / 4.213 s |
| 7 | 24/30 / 2.005 s | 24/30 / 1.913 s |

All five final P700 runs on each version retain the existing arrival, gunfire,
damage, idle-target and zero-sim-exception assertions. The cap loopback test
independently exercises research/countdown mode, since the unchanged P700
fixture uses the ordinary server policy with MCP showcase bots.


## Native intent

**Native design:** a countdown is an ordinary creator-selected winning condition,
measured in game simulation time and resolved by the shared mission's actual
scores. Keeping it in MissionParams makes the server's result, advertised goals
and client clock agree. The ordered comparisons and strict expiry are deliberate
behavior and remain unchanged.

**Era constraint:** there is no relevant legacy platform limitation to replace.
The port already carries the native clock through its modern transport and HUD.
Native had no research showcase or rotating showcase preset; the one-hour default
and the minimum with authored clocks are modern preset policy.

**Bug:** an unattended showcase could depend entirely on research maturity to
arm its only deadline. Resource exhaustion could prevent maturity indefinitely.
This is an orchestration omission in the showcase, not a defect in native timeout
ranking. The player-created flag loss on fixed-map rebuilds was also a port
lifecycle omission; preserving it prevents the cap policy leaking into human games.

**Would a modern approach be better?** Reusing the native countdown is the better
fit here: one authoritative clock, existing client visibility, established ranking,
and ordinary match rotation. An independent wall-clock watchdog could supervise a
frozen process, but would serve a different operational purpose and would not
replace this gameplay clock. Stalemate detection, resource balancing and strategy
changes remain outside this task.
