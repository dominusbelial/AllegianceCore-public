# P683 — merge P655 into integ6

Resolved the 28 conflict blocks in all 12 conflicted files by editing the
working tree. The merge index is intentionally untouched: no git write commands,
commit, or push were run. Claude can stage these resolutions and commit the merge.

## Resolution decisions

- `Ship.cs`: retain P651's continuous mount progress and load deadlines when
  marking spawn hardware free and transferring a promoted parent's inventory.
  Cargo launcher mounting still resets readiness while preserving refund
  provenance. Keep zero/partial-lock firing, MaxLock scaling, early-request duds,
  load deadlines, and static station/warp targets. Combine these with P655's
  docked/boarded fire refusal and fire-time target visibility, hostility,
  presence and damageability checks. A selected hidden lock-bearing target is
  refused without consuming ammunition, including detection lost after full
  lock; native untargeted dumbfire remains available. Keep P629's warning reset
  before the H-12 docked simulation guard.
- `Server.Dispatch.cs`: retain P673's decoded-command reader and add H-29's
  Started/finite-coordinate gates before any CHATBUOY routing or mutation.
- `Server.ShipCommands.cs` and `Server.Docking.cs`: use actual Sector objects
  and source missions for H-33 audience isolation; keep P663 treasure wire
  timestamps in simulation milliseconds. Preserve all P653d purchase
  preconditions and detailed refusal diagnostics, including unresolved side or
  station, invalid side, dead/boarded/flying state and invalid part amounts.
- `Server.Lifecycle.cs`: retain P682 viewer-cadence retirement alongside P655
  admin/harness authentication and spectator cleanup, before the idempotent
  early return.
- `BotAcceptsAndEngagesOrderLiveTests`: keep P653d's two lifecycle theory cases,
  including Iron Coalition pod 444, lethal damage, real docking/restart,
  multiple bots, successful purchase/launch ACKs, healthy unboarded relaunch,
  and renewed projectile damage. These include the incoming lifecycle test's
  initial purchase, death, rebuy, launch and combat assertions. Preserve P655b's
  specific commanded-target acceptance test.
- `GameOverPlayersWireTests` and `ConquestTerritoryLiveTests`: combine P655c's
  completed joins with baseline assignment-based mission/peer selection. Wait
  for both GAME_OVER and GAME_OVER_PLAYERS at every actual mission-A peer in
  the roster case; retain conquest's complete GAME_STATE/GAME_OVER receipt
  wait and all other-mission exclusion assertions.
- Missile live fixtures retain legal real magazine capacity, visible targets
  and matching positions away from map collision geometry. Purchase tests keep
  every baseline invalid-state case and P655's explicit bearer authentication
  for the privileged suicide positive control.
- `docs/TEST_FLAKES.md`: retain both branches' observation sections and the
  P655b/P655c corrections.

The automatically merged paths were also reviewed: P655c still freezes one
mission result/audience, finalizes scores, sends GAME_OVER followed by native
50-player roster chunks before rotation, and accumulates chunks on the client.
P656c sends current development techs separately from the initial bucket mask.
P671's session/bot keep-alive threads remain. P682 retains next-match purchase
and launch, fresh mission attributes/funding/warnings, and viewer-cache retirement.
P655b continues to permit command acceptance/selection before detection while
retaining recipient ownership checks and missile visibility checks.

No test method from either index side was removed in the six conflicted test
files. No assertion, deadline or quarantine trait was relaxed.

## Integration finding

The first focused Core run passed 140/141 cases. H-25's hidden-target positive
premise failed because P651's equipment-target helper resolves an uninitialized
visibility entry using actual scanners; the fixture's nearby enemy was detected.
The test now explicitly sets the enemy's visibility false before checking hidden
lock/fire refusal, as it already does for the later loss-of-detection check.
The assertions remain unchanged. The corrected focused run passed **141/141**.

The first unfiltered ClientCore pass reported one additional integration
fixture failure: P651's station-target test sent its single early-fire request
after the command echo but before station detection. The captured server trace
shows one visible station at the request, `FIRE_MISSILE requested while launcher
unavailable`, and two visible stations only afterward. P655b permits selecting
a target before detection; H-25 independently rechecks detection at fire time.
The fixture now waits for the actual scanner visibility as well as the command
echo, then explicitly asserts the launcher is still unready and more than 0.5s
before its load deadline. Existing missile creation, dud and eventual full-lock
assertions remain. This is a fixture integration correction, not a load-flake
classification or a relaxation of weapon checks.

## Verification

- Requested `grep -rn '^<<<<<<< \|^>>>>>>> ' src tests docs tools`: no
  matches. Read-only `git diff --check`: clean.
- Solution build (`dotnet build -m:2 -v:q`): **0 warnings / 0 errors**.
- Focused Core: **141/141**; focused ClientCore: **57/57**, no skips.
- Corrected P651 station/warp method: **3/3 runs**, **6/6 cases**, no skips.

Unfiltered assemblies ran sequentially, including their existing LiveTiming
cases. The initial ClientCore run was 953 passed / 1 failed / 1 opt-in skip;
the fixture failure is explained above. After the test-only fixture correction,
ClientCore was rebuilt and rerun in full with
`ALLEGIANCE_LIVE_P660_SHOWCASE=1`. Production code was unchanged between these
passes and the required repetitions.

| Final suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,389 | 0 | 0 |
| ClientCore, showcase enabled | 955 | 0 | 0 |
| ClientCore.MonoGame | 2,143 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

The final suites passed **7,506 tests**, with only the three existing optional
private Meshy-pack skips. The corrected station/warp test also passed in the
final full ClientCore run; no full-run failure remains unresolved.

| Required repetition set | Complete passing runs | Passing case executions | Skips |
| --- | ---: | ---: | ---: |
| `GameOverPlayersWireTests` | **10/10** | 50/50 | 0 |
| `P682*` | **3/3** | 15/15 | 0 |
| `BotAcceptsAndEngagesOrderLiveTests` | **5/5** | 15/15 | 0 |

All requested rates are **100%**. This matches the clean branch results:
P655c's roster class was 15/15, P682's affected methods were 3/3, and P655b's
specific commanded-bot method was 3/3 (and passed its three complete ClientCore
runs). The merged bot class additionally retains P653d's broader pod lifecycle
cases. No baseline branch was rebuilt or rerun for P683; those comparison rates
come from the existing [P655b/P655c report](P655_GAME_STATE_INTEGRITY.md) and
[P682 report](P682_NEXT_MATCH_VISIBILITY.md).

Per-invocation counts, commands, skips and initial failures are retained in
[P683_RESULTS.json](P683_RESULTS.json). Raw TRX files, logs and the sequential
runner are under `/tmp/p683`. `local.env` supplied the existing artwork/native
corpus paths; no rig/221/200v200 or remote-server runs were performed.

Reproduce after sourcing `local.env` and building: run `dotnet test` with
`--no-build` on each of the four test projects sequentially, without a filter;
enable `ALLEGIANCE_LIVE_P660_SHOWCASE=1` for ClientCore. Then repeat the Core
filter `FullyQualifiedName~GameOverPlayersWireTests` ten times, and the ClientCore
filters `FullyQualifiedName~P682` three times and
`FullyQualifiedName~BotAcceptsAndEngagesOrderLiveTests` five times. The evidence
JSON records the full commands used, including TRX output paths.
