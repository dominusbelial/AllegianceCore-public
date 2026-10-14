# P660 — automatic next match and mission-clock initialization

Implemented on `codex/p660`, starting at `32ceeccd`. Changes remain in the working tree; no commits or pushes.

## Root cause

`SimPass` already updates every mission, including NotStarted missions. `Mission.Update` already implements native's NotStarted clock advance (`missionigc.cpp:3180-3181`). The missing operation was **initialization at the current simulation time**.

`AssembleGameMission` assembled every rebuilt world with `Mission.LastUpdate == 0`. Rotation then migrated players and recomputed preserved readiness. That could call `StartCountdown` before the first NotStarted update of the rebuilt world. The first Starting update therefore used `now - 0`, exceeding the retained native ten-minute guard (`missionigc.cpp:3148`). Once Starting, the mission never reached the NotStarted branch that could advance its clock, so each subsequent update failed again. Game-over during a transport drain or a mission update can both expose this ordering.

The root fix ports `CmissionIGC::Initialize(Time now)` (`missionigc.cpp:3055-3059`): `Mission.Assemble` accepts an optional `initialTime`, initializes the mission and sector clocks before returning, and the server passes `_simNow` on **every** assembly, including rotation. Sector initialization follows `CclusterIGC::Initialize` (`clusterIGC.cpp:27-37`). Ship placement already uses `Mission.LastUpdate`, so the new ship clocks inherit the correct epoch too. Map generation also receives `_simNow`. Existing standalone callers retain the zero-time default. The ten-minute guard is unchanged.

The clash41 timestamp is a **simulation** timestamp, not a wall-clock timestamp. The regression exercises exactly `2636.1478201210366`, with time dilation off and with the actual auto controller driven to approximately 0.25 scale. NotStarted continues to track the sim clock every tick.

## Design and native evidence

| Behavior | Native rule and port mapping |
| --- | --- |
| Preserve connected users and seats | `FedSrv/fsmission.cpp:2604-2622`, `Vacate` / `Reset(true)`. Reuse the existing migration and join-burst path, fresh ships, previous side, leaders, owner, reset wallets/world exports. Migration now explicitly targets the rotated mission rather than implicitly mission zero. |
| Restart intermission | `fsmission.cpp:3539-3546`: `bAutoRestart` sets `timeStart` and broadcasts native `SET_START_TIME` while NotStarted. The fresh mission is armed **before** migration, so ready flags, debug auto-start, and manual start cannot bypass the timer. Old deadline state is removed. |
| Scoreboard with visible timer | `WinTrek/gameoverscreen.cpp:630-666`, `UpdateCountdownText`: NotStarted auto-restart displays the remaining time; Starting moves a seated player to the team screen. MonoGame retains the result and per-player score snapshot through the intermission, then shows its existing briefing/ready screen. |
| Normal briefing and start | `fsmission.cpp:1926-1976`, `StartCountdown`; `Igc/igc.h`'s 15-second `c_fMissionBriefingCountdown`; existing P188 `Starting -> Started`, `ENTER_GAME`, and stage broadcasts. |
| Absolute client deadline | `clintlib/appmsg.cpp:2702-2710` converts `SET_START_TIME` from server time and replaces the mission's start time. The client now replaces each deadline instead of retaining the first receipt and assuming fifteen wall seconds. |
| Stage-authoritative flight hold | `clintlib/appmsg.cpp:2314-2356` drives mission lifecycle from stage messages. MonoGame holds flight during NotStarted/Starting and releases through its existing Started path. Duplicate Started snapshots preserve a side-only elimination result. |
| Countdown sounds | Native `WinTrek.cpp:11137-11159`, `CheckCountdownSound`. The normal briefing cues run on the ready screen, use the live deadline, and reset for each new mission. |

Dedicated-server policy adaptations, rather than claims of literal native rules: automatic next matches default on; unseated migrated players are assigned to the least populated side; readiness is completed at timer expiry regardless of AFK/ready state. Existing seats are preserved without forced balancing. The established native-shaped `PING` carries a sim-clock sample immediately before `SET_START_TIME`, and periodic samples correct the estimate during both timers. Broadcasting these samples is a port adaptation; no wire layout or new message type was introduced.

Late joiners receive the current intermission or briefing deadline. Timers are mission-scoped. A NotStarted mission remains held until its intermission expires even if every user sends ready. Then readiness aggregates are recomputed and the existing start path runs.

## Configuration

```json
{
  "liveSettings": {
    "autoNextMatch": {
      "enabled": true,
      "intermissionSeconds": 30
    }
  }
}
```

Defaults are enabled/30 seconds, including an absent section. Intermission accepts finite values in `[0, 3600]`; invalid values fail at boot. Both intermission and briefing use **simulation seconds**, matching their server deadlines. At constant 0.25 scale, thirty sim seconds take approximately 120 wall seconds. The client extrapolates with the current scale and integrates scale changes between clock samples.

With `enabled: false`, the existing ready-up flow remains: preserved individual readiness can immediately start the next countdown; unready players remain NotStarted until ready. The older per-game `AutoRestart` remains an independent opt-in when the new policy is disabled. The new policy's intermission takes precedence when enabled. See [configuration guide](../NEXT_MATCH.md), the server example, and README.

## Bots

The existing `RefreshLife` / `ResetNewLife` logic detects the mission generation and fresh ship, restores the original hull/loadout, resets navigation/combat state, and resolves objectives against the new world. It re-readies the fresh ship through the existing path.

A second bug was found: `RestoreFinaleForNextMission` set the finale run's `Rotated` flag permanently, and `UpdateFinales` skipped it forever. The port now retains that run as the finished-match report and registers a new armed run only after **all participating bots** advance beyond their captured generations, resolve objectives, and observe the new Started mission. Station targets are selected from the fresh world's exports; the time gate, recent-kill window, relaunch maturity, and kill baselines restart. The old result is captured before resetting each bot. The generation barrier also works on subsequent rotations, when reset-observed flags are already true.

The regression changes station IDs, resets bots one at a time, and verifies that no run re-arms early, both fresh objectives are selected, and exactly one new run is created.

## Verification

Commands run from the repository root. The fresh worktree needed restore first; the initial restore encountered a read-only NuGet HTTP cache. `NUGET_HTTP_CACHE_PATH=/tmp/p660-nuget-http dotnet restore AllegianceCore.slnx` succeeded without changing package requirements or disabling audit.

Focused real-loopback test matrix: four late-clock rows, plus two unready-player enabled/disabled rows. Two `ConnectionSession` TCP clients remain connected, receive fresh ship IDs on the same sides, observe the timer and Starting stage, and reach Started. Every late-clock row asserts zero `SimPassStageFailures` and no `[simpass]` exception output. The regression also checks the exact initialized sector/mission timestamps and advances the Started simulation beyond the transition.

| Late-clock row | Intermission observed after rotation (sim s) | Briefing began (sim s) | Started plus ten verification ticks (sim s) | Measured wall s | Exceptions |
| --- | ---: | ---: | ---: | ---: | ---: |
| Enabled, dilation off | 0.000 | 8.000 | 23.500 | 0.538 | 0 |
| Enabled, auto dilation ~0.25 | 0.025 | 8.012 | 23.150 | 2.147 | 0 |
| Disabled, dilation off, preserved ready | 0.000 | 0.000 | 15.500 | 0.375 | 0 |
| Disabled, auto dilation ~0.25, preserved ready | 0.000 | 0.000 | 15.137 | 1.429 | 0 |

These loopback tests deliberately pump ticks faster than wall time. The measured wall durations are test execution times, not a production timer's wall duration. The separate unready disabled case remains NotStarted after five sim seconds and starts only after both clients ready. Enabled unready players start automatically.

MCP showcase proof uses the actual `McpTools.Handle("spawn_scenario", ...)` dispatch with `preset: "showcase"`, size scale 1 (15v15 mixed roles), the real m2xspiderw map, two additional human sessions, and the manager's probe. It forces the shared game-over declaration on the simulation thread. Both humans saw a 12-second intermission. **30/30 bots migrated, 2/2 humans remained connected, 30 bots reset/re-readied/resolved their objectives, zero pending bots, and the next finale was armed with empty maturity/recent-kill gates.** Rotation through briefing/start took 27.200 sim seconds; complete scenario execution took 20.992 wall seconds; server exceptions: **0**. This proves restart behavior without waiting thirty minutes for a natural conquest; it does not repeat the 200v200 load run or measure a second finale strike.

Reproduce the focused tests and full MCP proof:

```bash
dotnet test tests/Allegiance.ClientCore.Tests --no-restore --filter 'FullyQualifiedName~P660'
ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-restore --filter 'FullyQualifiedName~P660ShowcaseLiveTests'
```

The Core regression also covers immediate Starting at a late assembly time for scales 1.0 and 0.25. Client unit tests cover intermission/briefing deadline replacement, dilation, duplicate Started messages after elimination, and result clearing. MonoGame checks the timer text and resettable countdown deadline. UI wiring and rendering compile in the full solution; the live tests use the same ConnectionSession/ClientWorld data plane as MonoGame. No desktop screenshot was claimed.

Full suites ran once each, concurrently in separate processes, without filtering out the live timing lane:

| Suite | Passed | Failed | Skipped | Total | Duration |
| --- | ---: | ---: | ---: | ---: | --- |
| Core | 4150 | 4 | 0 | 4154 | 4m30s |
| ClientCore | 877 | 0 | 1 | 878 | 4m05s |
| MonoGame | 1972 | 0 | 3 | 1975 | 4m22s |

ClientCore's skip is the opt-in full showcase, which was run separately with its enable flag. MonoGame's three skips are the optional Meshy pack tests. All P660 tests and the P598/P480/MissionRotation tests passed in the full runs.

Each failed Core test was then run **three times individually**, after all full suites completed:

| Existing test | Isolation results | Full-run failure |
| --- | --- | --- |
| `Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` | pass / pass / pass | No heavy update received yet at the receipt assertion |
| `LiveTeamLobbyToggleTests.Sides_lock_from_owner_relays_and_non_owner_refused` | pass / pass / pass | Relayed message not received yet |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | pass / pass / **fail** | Position already advanced past the narrow expected range |
| `GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side` | pass / pass / pass | Capturer assigned the opposite side by connection ordering |

The carrier fallback remains flaky even in isolation: its third isolated run observed Z `-203.457947` versus expected range `[-182.315002, -178.315002]`; the full-run value was `-217.885437`. This family is already recorded in [TEST_FLAKES](../TEST_FLAKES.md), and the conquest roster test is already marked `Lane=LiveTiming`. These existing tests do not run the new late-clock path; the carrier test boots at time zero and never rotates. No claim is made that the entire Core suite is clean.

Final review tightened duplicate Started handling to preserve side-elimination results and updated the old one-shot countdown test to the native resettable-deadline rule. Relevant tests were rerun against the final build, including the disabled legacy AutoRestart path.

Final focused reruns: **Core 8/8** (late clock, legacy AutoRestart, P598, P480, repeated rotation); **ClientCore 10/10**, including the opt-in full MCP showcase; **MonoGame 13/13** (game-over timer and countdown handlers). The repeat showcase preserved the prior winner/reason in its archived run (`winner=0`, `P660 forced conquest`) and reported 30 active bots, no pending objectives, and a fresh armed finale.

Final required command:

```bash
dotnet build AllegianceCore.slnx --no-restore -m:1
```

**Build succeeded: 0 warnings, 0 errors** (4.00 seconds). `git diff --check` is clean. Branch and HEAD remain `codex/p660` / `32ceeccd`; changes and this report are uncommitted.

The measurements and suite summaries are also retained in [verification excerpt](P660_assets/verification.txt).
