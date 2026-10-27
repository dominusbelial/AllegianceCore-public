# P861 — conquest GAME_STATE receipt crosses mission rotation

The server sends the correct winning conquest ranking. The failing fixture also
inspected the **next mission's initial 50% ranking**, which arrives after
GAME_OVER. P832e added native join rankings to `CompleteJoinAndLaunch`; rotation
reuses that method. This exposed the fixture's assumption that every GAME_STATE
received after a list reset must describe the winning capture.

Only the test and flake ledger change. No commits, pushes, remote-host access,
production changes, new media, or P844 certification-row edits were made.

## Cause and native evidence

The original join is already drained per peer in `Seat`. `Bot.GameStates`
(`src/Allegiance.Bot/Bot.cs:852,2032`) returns **all** received rankings since
`ResetGameStates`, not the latest ranking. `AllGameStates` flattens those lists
across peers; its old aggregate count could also become satisfied before every
peer received the capture frame. The harness inserts its setup bases directly
through `PlaceStation`, without a station-victory notification. Although those
bases make ownership 3/5 before capture, the cached native initial rankings stay
50/50 until the real capture event; this fixture has no setup 60% GAME_STATE.

The actual event sequence for each of the three peers is:

| Message | Meaning | Side A / B conquest |
|---|---|---|
| STATION_CAPTURE(58) | Existing station changes sides | — |
| GAME_STATE(155) | Winning capture: four of five flag stations | 80 / 20 |
| GAME_OVER(62) | Side A wins | — |
| GAME_STATE(155) | Fresh mission's join rankings after re-seating | 50 / 50 |

The capture and result ordering matches native:

- `FedSrv/fsship.cpp:623-631` sends STATION_CAPTURE reliably before `SetSide`.
  `:686-694` checks victory after re-siding, calls GameOver for a winner, and
  only vacates the station in the non-winning, active-old-side branch.
- `FedSrv/fsmission.cpp:3563-3565` routes capture to the station tally with no
  excluded station. `:3590-3600,3617-3625` counts flag stations for their current
  owner, then writes integer ownership percentages. `:3682-3695` sends the
  updated arrays to real sides when the ranking changes.
- `FedSrv/fsmission.cpp:3435-3438` flushes game-over before reset;
  `:3490` sets the restartable mission back to NotStarted.
- `FedSrv/fsmission.cpp:4324-4339` puts current victory rankings in
  SendMissionInfo. `Igc/missionigc.cpp:3862,3896` initializes conquest to
  `100 / nTeams`. The port's existing immediate rebuild/re-seat policy sends
  those fresh rankings through the same join path.

All native references were re-read in the local read-only native tree.

In the port, `Sector.cs:4642-4659` broadcasts capture, calls `rt.Capture`, and
then `NotifyStationVictory`. The mission recomputes before calling the server
(`Mission.StationVictory.cs:12-16`). `Server.Combat.cs:1503-1542` immediately
encodes the updated GAME_STATE before declaring game-over. The winning branch
does not vacate. `Server.Lobby.cs:484,734-760,998,4924` rotates and reuses the
join burst; this three-player migration fits the synchronous batch of eight.
The extra 50% therefore can be queued in the same tick as the correct 80%.

Snapshot/encode-once publication does not overwrite or reorder these messages:
`Server.BroadcastSnapshot.cs:195-209` assigns `key=0, guaranteed=true`, and
`BroadcastSnapshot.cs:129-138,205-223` preserves these deliveries in append order
when ticks coalesce. GAME_STATE is encoded into owned bytes before submission;
the publisher does not read the later mission counters. Faster receipt, or a
later inspection on a busy host, simply exposes both correct rankings to the
old assertion. Delaying initial join receipt is not the demonstrated cause.

## Change and deterministic regression

The winning-capture proof now waits for **each peer** to receive a ranking and
checks its first post-reset GAME_STATE, after the fixture has drained the original
join. It also waits for GAME_OVER per peer. It retains the authoritative station
ownership, 80/20 counters, zero flag/artifact counters, and winner assertions.
It does not search for a frame with the desired percentage or use the latest
frame, which could hide a wrong capture ranking or select the next mission.

`Capture_rankings_remain_verifiable_after_the_next_mission_rankings_arrive`
shares those assertions and deliberately waits until every client has received
both rankings. An event blocks the existing publisher seam while the actual
capture/rotation and a later tick queue, forcing snapshot coalescing without
CPU load or scheduler luck. The test checks that coalescing occurred, publication
had no failures, every peer's ordered reliable transcript is
`58 → 155 → 62 → 155`, and its actual received conquest values are `[80, 50]`.
The event is always released in `finally` and has a bounded failure timeout.

Before correcting the shared assertion, delayed inspection reproduced the exact
failure: **Expected 80, Actual 50**. The final stalled-publisher regression was
also run with the legacy `foreach (var gs in AllGameStates(seat))` assertion
temporarily restored; it rejects that assertion with the same failure. Final
source was restored and rebuilt afterwards. Raw receipts are under the ignored
worktree directory `.scratch/p861/` (`regression-before/` and `mutant/`).

## Verification

See [validation summary](P861_assets/validation.json) for machine-readable counts.
Raw build/test logs and TRX files remain in the ignored worktree
`.scratch/p861/`; sandbox write permissions prevent using the usual external
evidence directory. Only small summaries are added to the report assets.

- Solution build: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 90 projects built, 0 warnings / 0 errors**.
- Entire ConquestTerritory class, three isolated invocations: **15 passed / 0
  failed / 0 skipped**. Separately, the new regression and affected capture
  method each pass **3/3 exact-method invocations alone** (six receipts).
  The affected method also passes with the snapshot pipeline disabled.
- Entire class, **20 invocations via `xargs -P 4`: 100 passed / 0 failed / 0
  skipped** on final source. Recorded one-minute loads were 24.77–25.18;
  start 1/5/15-minute averages were 25.18/24.35/23.50, end
  24.86/24.32/23.50. No synthetic load.
- Exactly one `tools/run-tests.sh --tier fast --no-build`: **11,196 passed /
  1 failed / 3 optional skips**, 347.5 s; all nine infrastructure checks passed,
  no timing offenders or quarantine retries. Both P861 methods passed in this
  run (48 ms affected method; 41 ms new regression).
  The run remains **FAIL** because the unchanged
  `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`
  missed its observer death latch (`:440`, 2,500 ticks). Afterwards that exact
  method passed **3/3 alone** (1.179 / 1.486 / 1.703 s), meeting the lane rule for
  a load/receipt flake. Its source, assertions, deadlines and tier are unchanged;
  the observation is recorded in TEST_FLAKES. The whole Fast tier was not repeated.
- AOT publish: not applicable; Server/Core source did not change.
- Glossary links (232 paths), parity scorecard validation, and `git diff --check`
  pass. No new domain or architecture was introduced, so the glossary is unchanged.

An initial build/tools attempt encountered a read-only default NuGet HTTP cache.
The completed build and final tools invocation use `NUGET_HTTP_CACHE_PATH` in
worktree scratch. This requires no repository configuration change. The Fast
runner uses `XDG_CONFIG_HOME` in scratch to avoid the known read-only user-profile
issue. Its `TMPDIR` is a `/proc/<launcher>/fd/<directory>` alias to an NVMe scratch
directory, avoiding both RAM-disk scratch and the known PlayerBundlePathsTests
repository-ancestor/local.env detection issue. The runner keeps its shared lock.

The P832f observation and P852 merge-gate ledger entry now identify the resolved
fixture race. No native behavior was ported and no parity status changed.
P844's victory/results certification rows remain untouched. No victory-row note
correction is needed; the existing SIM-0850 join-ranking rule explains the extra
message and can optionally link this regression as additional evidence.

## Native-intent classification

**Test observation bug; native behavior preserved.** Capture re-siding,
conquest recompute, reliable ranking/result order, and native join rankings are
correct. The fix scopes the assertion to the capture's ranking while retaining
the legitimate next-mission ranking.
