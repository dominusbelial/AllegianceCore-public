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

## P660b

Implemented on 2026-10-14 in `integ/rig-next-match`, HEAD `535b56b3` (the merge of P660 and wave 2). All changes remain in the working tree; no commits or pushes.

### Root cause and native decision

Both failures reproduced on the unmodified integration branch: promotion completed on the server but neither role notice arrived; P641 timed out at replacement. The proposed flight-hold cause was ruled out. Both fixtures explicitly set their server missions to Started and invoke `ConnectionSession` senders directly; they do not run `Game1.Update` or its ReadyCheck transition. Their existing mission setup is valid. The strengthened tests additionally wait for both clients to observe the authoritative Started stage.

The actual integration is wave 2's new PROMOTE/SWAP_PART receive handlers with master's P648 LOADOUT_CHANGE receive support (`f01ac595`). Wave 2 alone predates P648 and ignores those full snapshots. Combining them makes the intermediate site-event broadcasts visible to the client:

- **Promotion:** `Ship.Promote` raises RemovePassenger, HullChange, Mounted, AddPassenger and Removed events before the command's PROMOTE echo. LOADOUT_CHANGE detaches the promoted child while adopting each partial hull/passenger snapshot. When PROMOTE arrives, its client handler can no longer find the old parent, so it returns without either role notice. Subsequent status snapshots can repair seats without proving the command was consumed.
- **Replacement:** `TrySwapPartWithCargo` removes both parts, mounts the spare (raising a Mounted snapshot), then places the displaced weapon in cargo. The observed Mounted snapshot contains only two parts, instead of the complete three-part inventory. It has already removed the spare from the source cargo slot but has not restored the old weapon to cargo. SWAP_PART subsequently finds no source and cannot complete the client inventory. Merely increasing the timeout cannot repair this guaranteed message order.

Native evidence, relative to the reference checkout's `src/`:

| Concern | Native evidence and decision |
| --- | --- |
| Stage authority | `clintlib/appmsg.cpp:2314-2356` sets lifecycle flags from NotStarted/Starting/Started. Retain P660's flight hold and existing Started release. |
| Key eligibility before launch | `WinTrek/WinTrek.cpp:8740-8763` dispatches flight keys only in game, outside override/lockdown; `:9027-9058` excludes hangar/loadout weapon selection; `:9075-9149` requires an unboarded, flying ship for part swaps. Promotion `:9583-9601` requires a real hull/turret; the server's Started gate remains authoritative. |
| Command stage gates | `FedSrv/FedSrv.CPP:6354-6377,6459-6501` permits PROMOTE/SWAP_PART only in Started and broadcasts the dedicated command mutation. Keep both existing server gates. |
| Intermediate loadout events | `FedSrv/FedSrv.CPP:13530-13555` does not enqueue LOADOUT_CHANGE on every low-level loadout event (that forwarding block is commented out). Suppress the port's additional full snapshots during the two commands rather than changing client lifecycle or inventing acknowledgements. |
| Promotion receive | `clintlib/appmsg.cpp:824-849` resolves the old parent before applying promotion and posting the two notices. Preserve that precondition. |
| Inventory receive/export | `clintlib/appmsg.cpp:1430-1456` applies a swap delta; the port continues to apply its own guaranteed echo once instead of native's optimistic local mutation. `Igc/shipIGC.h:804-824` exports every inventory part with its signed mount. |

### Changes and regression coverage

`Server.ShipCommands` scopes loadout-broadcast suppression around the synchronous Promote and cargo-aware swap mutations, with `finally` restoration. `Server.Combat` retains cross-side hull-export invalidation even inside that scope, and ordinary loadout/boarding/relaunch broadcasts remain enabled. The existing command echoes still reach the sector audience. No wire format, flight-hold rule, next-match timer or ready-up behavior changed.

The expanded P641 proof adds a same-side production client and checks both clients through purchase/launch, unmount, mount, replacement and drop. It also checks that an ordinary direct mount still broadcasts after all commands, exercising scope restoration. This exposed a related export omission: `Server.Broadcast.SnapshotParts` exported only equipped parts, so its refresh erased cargo learned from LOADOUT_CHANGE. It now uses the complete inventory export and preserves signed cargo mounts. With the suppression fix alone the expanded proof failed at buy/launch; with the complete export it passes.

The promotion proof retains the actual role-swap, hull, turret-seat wrap and highlight assertions, and waits under the session gates for both exact notices. A status-only seat repair can no longer satisfy the proof. The P641 weapon selection, booster fuel/release and authoritative server cargo/equipment assertions remain intact.

### Verification

External artwork and native source were available through the existing `local.env`. Restore used `NUGET_HTTP_CACHE_PATH=/tmp/p660b-nuget-http dotnet restore AllegianceCore.slnx` and succeeded without changing dependencies.

Both original tests failed before the production fix and passed after it. Each strengthened test then passed **3/3 individually** with no other suite running.

Full suites ran once each, concurrently in separate processes, with **no test filters**, against the final build:

| Suite | Passed | Failed | Skipped | Total | Duration |
| --- | ---: | ---: | ---: | ---: | --- |
| ClientCore | 892 | 1 | 1 | 894 | 4m05s |
| MonoGame | 2120 | 0 | 3 | 2123 | 4m22s |
| Core | 4169 | 1 | 0 | 4170 | 4m28s |

The original two failing live tests pass in the full MonoGame run. All nine enabled P660 ClientCore tests and both late-clock Core rows pass. Ordinary LOADOUT_CHANGE live tests (2/2), cross-side export/respawn tests (5/5), P598 restart tests (3/3) and repeated mission rotation tests (2/2) also pass. The skips are ClientCore's opt-in full showcase and the three optional MonoGame Meshy pack tests; no artwork-backed test was skipped.

Both full-run failures were rerun **three times individually**, after all full suites completed:

| Existing test | Full-run failure | Isolated reruns |
| --- | --- | --- |
| `Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` | Healthy bot had no heavy update at receipt assertion (`had 0, now 0`) | pass / pass / pass |
| `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | `minWarpDist=131.5`, below the test's 150-unit assertion | pass / pass / pass |

The inbound-flood test is the same receipt failure recorded in the original P660 verification above. The warp-arrival test is already listed in [TEST_FLAKES](../TEST_FLAKES.md) and marked `Lane=LiveTiming`. Neither fixture exercises PROMOTE or SWAP_PART. No timing assertions or quarantine filters were relaxed. The full Core and ClientCore runs are **not claimed clean**.

The opt-in full MCP showcase then passed separately (`ALLEGIANCE_LIVE_P660_SHOWCASE=1`). Both human clients observed a **12-second simulation intermission**; **30/30 bots migrated, 2/2 humans remained connected, all 30 bots reset and resolved fresh objectives, no bots were pending, and a fresh finale armed**. Rotation took **27.250 sim seconds**; the test measured **21.295 wall seconds** for the complete scenario; `SimPassStageFailures` was **0**. As in the original proof, game-over is forced through the shared declaration path; this is not a natural conquest/load benchmark.

Reproduction commands (run from the repository root):

```bash
NUGET_HTTP_CACHE_PATH=/tmp/p660b-nuget-http dotnet restore AllegianceCore.slnx
dotnet build AllegianceCore.slnx --no-restore -m:1
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --no-restore --filter 'FullyQualifiedName~PromoteTurretKeyLiveTests|FullyQualifiedName~P641FlightPartsLiveTests'
dotnet test tests/Allegiance.ClientCore.Tests --no-build --no-restore
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --no-restore
dotnet test tests/Allegiance.Core.Tests --no-build --no-restore
ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build --no-restore --filter 'FullyQualifiedName~P660ShowcaseLiveTests'
```

Final build: **0 warnings, 0 errors**. `git diff --check` is clean. The branch and HEAD remain `integ/rig-next-match` / `535b56b3`. Verification summaries and the baseline message-order excerpt are retained in [P660b verification](P660_assets/p660b-verification.txt).
