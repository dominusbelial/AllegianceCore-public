# P655 — movement, state and privilege integrity

Codex lane, 2026-10-14. Changes are left in the working tree; no commit or push.
The source audit is preserved at [HOSTILE_INPUT_AUDIT.md](../reviews/HOSTILE_INPUT_AUDIT.md).
The brief's lane exception assigns rig/221/200v200 verification to Claude; this
report distinguishes that handoff from the local tests actually run.

## Changes and native references

Native paths below are relative to `~/Development/Allegiance/src/`, inspected
locally. The port's additional hostile-client checks are identified explicitly;
they are not represented as checks the native server already performed.

| Finding | Result and provenance |
| --- | --- |
| H-11 | Restore native monotonic payload time (`FedSrv/fsship.cpp:1173-1200`), accept at most one pose per simulation instant, validate velocity against effective hull physics including the fitted afterburner, and enforce a server-time travel budget. Native's 20m tolerance is at `Igc/shipIGC.cpp:3177-3189`; effective cruise/afterburner physics are at `:2922-2957`. The speed and cumulative travel checks harden native's trusted-client assumption. Time must match between the outer and payload timestamps. Credit cannot be recreated by repeated packets or guessed reset cookies. Refill accounts for a full packet-loss gap; ordinary stored credit is bounded. Invalid travel/speed sends the authoritative SHIP_RESET with a fresh cookie. Authoritative SET_CLUSTER transitions rebase the travel anchor (native cookie placement: `FedSrv/fsship.cpp:767`), covering warp, launch, ripcord, teleport and respawn paths. Cumulative correction/replay/same-tick counters are logged in the ship-update summary. |
| H-12 | Docked control, missile, expendable and turret input is ignored; docked ships skip flight and weapon simulation. Native cluster checks: `FedSrv/FedSrv.CPP:4857,4873-4875,5428,5634`. The port keeps Sector for hangar lookup, so StationId must be checked explicitly. Launch propagates the cleared station to boarded children, as native ChangeStation does (`:13165-13173`). Boarded children cannot independently dock again at their old position (`Igc/shipIGC.cpp:271`, native parent update guard). |
| H-13 | Remote POSITIONREQ, pending approvals and auto-accept sweeps validate stage, defection, inactive side, base, capacity and balance before changing sides. Initial selection/timeout auto-pick validates before spawning. Native `CheckPositionRequest`: `FedSrv/fsmission.cpp:4575-4642`. Defection is refused once Started. Configured `games[].maxPlayersPerTeam` defaults to 256 for the 200-per-side lane; `maxTeamImbalance` defaults to native's 1 (`Igc/igc.h:1303`), with 32767 disabling balance. Native authenticated bots bypass balance (`FedSrv/fsmission.cpp:4637-4642`); only a server-authenticated harness peer gets that exception, and capacity still applies. Internal team placement/rebalancing remains distinct from remote request validation, matching native RequestPosition/AddPlayerToSide. Authenticated persistent rank, invitation, ban and squad rules have no equivalent state in this port. |
| H-16 | Names/prefixes are eligibility filters. Spectator visibility, admin commands, harness money/tech grants and harness relocation also require a configured bearer or a transport-verified loopback socket when no secret is configured. A remote client cannot obtain the loopback fallback by naming itself. Credentials are checked at validated logon; authentication bookkeeping retains connection ids, and disconnect clears them. An admin may also authenticate with a secret-prefixed command; the secret-bearing line is consumed. Cross-peer relocation requires an authenticated harness target. This is a port security policy replacing the audited name trust, not a native authentication claim. |
| H-17 | Reject non-finite timescale commands/setters and invalid relocation position/facing before mutation. Non-finite configured scales fall back to 1.0. Reject zero/non-finite facing; optional facing arguments must parse completely. Correct the relocate parser's off-by-one argument counts. These cheats are port extensions. |
| H-29 | CHATBUOY requires Started and finite buoy coordinates before orders or relay. Native stage gate: `FedSrv/FedSrv.CPP:5297`. |
| H-25 | Another player cannot overwrite current/accepted targets. Same-side NPC commands retain their native handling. Direct current/accepted cross-side ship targets must be detected; missile lock and firing recheck visibility, hostility, sector and living/flying state. Native explicitly requires `CanSee(ptarget)` in `Igc/magazineigc.cpp:127-132`; detection includes cloaked ships detected by scanners, so cloak alone is not an unconditional lock ban. The player-slot ownership check hardens native's trusted-client lookup (`FedSrv/FedSrv.CPP:7271-7278,7335-7339`). |
| H-31 | Server and play client use uint wire milliseconds; simulation replay converts to seconds. Restore native monotonic turret input (`FedSrv/fsship.cpp:1208-1218`), with per-connection rate-limited, verbose-gated acceptance/staleness logging. Native sender units: `clintlib/clintlib.cpp:1892`, `Igc/shipIGC.h:1206-1212`; replay window: `Igc/shipIGC.cpp:3353-3386`. |
| H-33 | Sector sends use the actual Sector object, not a reusable sector id. Money/bucket broadcasts carry the source mission, including mission-wide team-money research. Ship/view lookups and admin spectator sector resolution use the requester's mission. Native audiences are the actual sector/mission/side groups (`FedSrv/FedSrv.CPP:6374-6377,7162-7168,7190-7196`), not global numeric-id matches. |
| H-36 | **Preserved.** Native permits an authorized leader's rejected DELPOSITIONREQ without requiring a pending request (`FedSrv/FedSrv.CPP:7484-7516`). Tests prove non-leader rejection fails and the authorized native action works. |
| H-38 | **Preserved.** Native allows any teammate to issue the audited same-side NPC stay/launch/return commands (`FedSrv/FedSrv.CPP:7271-7332`). Tests prove same-side permission and refusal to command an enemy NPC. |
| H-23 | The lobby is in this repository. List the server-observed source address, bind heartbeats to that source and the ownership token, atomically enforce total/per-source caps, strip Unicode format characters, hide expired listings immediately, limit bodies to 16 KiB, and rate-limit all requests plus the stricter registration budget. Forwarded source headers are accepted only from explicitly configured proxy IPs, one hop. This is a port HTTP service, with no native rule to cite. |

## Operator setup

See [GUIDE.md](../GUIDE.md#admin-cheats--lan-only-documented-safety) for the
explicit MCP setup and [LOBBY_APP.md](../LOBBY_APP.md#p655-directory-integrity)
for proxy and listing limits. Configure the same random secret in
`liveSettings.adminCheats.secret` and `liveSettings.testHarness.secret`, and set
`ALLEGIANCE_PRIVILEGE_SECRET` for `allegiance-mcp`. Its probe and bots send the
bearer in LOGONREQ.CDKey. A configured secret is required even on loopback;
without one only actual loopback sockets qualify. The TCP transport is plaintext.

## Regression evidence

The first added audit-packet run failed **11 cases**, with **2 native-permitted
behavior cases passing**, before the corresponding fixes. H-33 independently
failed before its filter changes, and all three initial lobby probes failed
before the directory changes. Later packet-loss and first-post-reposition clock-window positive controls
failed with the initial travel-credit settings, then drove the full-gap refill
and bounded initial latency allowance corrections.

The new tests use real packet codecs and real server handlers with a real
artwork core/map. The server audit regression group contains 28 cases, with another
7 directory cases. Positive controls accompany attack refusals, including native
poses, launch/restored motion, actual missiles/mines and ammo, pre-game side
selection, authenticated admin/harness operations, visible missile targets,
started buoy relay and millisecond turret updates. Existing live fixtures now
launch before flying/firing, establish detected missile/order targets, and use
an authoritative relocation for deliberate server-side teleports. Assertions
on movement, firing, target identity and zero launch corrections remain intact.

Final movement review also reproduced the inactive-afterburner speed allowance
and delayed-coasting rejection before fixing them. The envelope now requires
active, fueled boost (or remaining server-side afterburner power;
`Igc/afterburnerIGC.cpp:128-150`), preserves
authorized launch/boost momentum, and compares replayed velocity at server time.
Axis and diagonal compact-velocity rounding are tolerated and clamped downward;
repeated rounding cannot amplify trusted momentum. The verbose turret flood
regression also failed before its per-peer diagnostic limiter was added; valid
input continues to apply while logs remain bounded.

Final-tree local 30v30 proof (`tools/p655-proof`): **60/60 reached**, **kills 1**,
**shots 468**, **hits 254**, **corrections 0**, no ticker failure. It uses real TCP peers,
normal navigation/combat, and an explicitly configured privilege secret.
These MCP bots use controls-only updates; native-pose validation is exercised
by the own-ship loopback regressions, including zero-reset dock/undock cycles.
Suite and isolation totals are recorded below.

Reproduce the local proof:

```bash
set -a
. ./local.env
set +a
dotnet run --project tools/p655-proof/p655-proof.csproj -- /tmp/p655-live.json
```

## Verification results

New and affected tests, movement/transition controls and final integration
follow-ups passed **3/3 individually: 158 methods, 161 parameter cases,
474 test invocations / 483 passing case executions**. The initial affected-test
set was 154 methods; the final suite follow-ups added four distinct methods.
No-match discovery attempts are excluded; the
two affected methods in files containing multiple classes were rerun using
their actual class names. The extracted per-case outcomes and failing-first
evidence are preserved in [P655_RESULTS.json](P655_RESULTS.json); raw TRX/logs
are under `/tmp/p655` on this machine.

Final build: **0 warnings / 0 errors** (`dotnet build -m:2 -v:q`). Unfiltered
full suites ran sequentially after the individual checks, local proof and build:

| Suite | Passed | Failed in full run | Existing skips |
| --- | ---: | ---: | ---: |
| Core | 4,224 | 3 | 0 |
| ClientCore | 892 | 2 | 1 |
| MonoGame | 2,120 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

All **five** full-run failures passed **3/3 alone after all suites finished**:
`TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies`,
`LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`,
`LiveWinConditionTests.Board_refused_once_mission_over`,
`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`,
and `BotAcceptsAndEngagesOrderLiveTests.ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget`.
These are classified as load flakes by the brief's explicit rule; the full run
is not represented as failure-free. The shutdown case threw ThreadStateException;
the others missed a handshake/receipt or observed asynchronous seating order.
The four skips are the existing opt-in P660 showcase and three unavailable
private Meshy-pack checks. Every new P655 case executed. `git diff --check` passed.

Earlier receipt failures that passed three fresh individual runs are recorded
in [TEST_FLAKES.md](../TEST_FLAKES.md#p655-observation-2026-10-14). Deterministic
fixture failures were fixed before the final pass; no new quarantine traits
were added. The native client dock/undock positive control retains **zero
rejects and zero SHIP_RESETs** across three cycles, while its deliberately
stale-client companion still demonstrates authoritative correction. Warp,
ripcord and carrier relaunch controls passed their individual checks.

Suite reproduction after sourcing `local.env`:

```bash
dotnet build -m:2 -v:q
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
dotnet test tests/Allegiance.Lobby.Tests --no-build
```

For individual repetitions, use `--filter FullyQualifiedName=<method>` on its
project three times; the JSON records each exact method and each run's totals.

## Rig handoff

Not run in the Codex lane: rig/221/200v200 march. Claude should run that lane,
count `corrections-total` / SHIP_RESET traffic, require zero corrections for
legitimate bots and kills > 0, and exercise warp, docking, ripcord and relaunch
at that scale. Local loopback tests and the local 30v30 proof are not evidence
that the rig run happened.

## P655b

The P655 bot-order failure is a regression. The earlier load-flake classification
in this report and `docs/TEST_FLAKES.md` is withdrawn. Claude's review found
3/3 isolated failures on P655 and 2/2 passes on the base. The first local
pre-fix live run here passed, but deterministic packet tests reproduced the
fault without relying on scheduling. Passing isolated runs did not disprove it.

### Exact failure and order trace

P655 added this check immediately after mission-scoped target resolution in
`Server.ShipCommands.cs:1423` (line in WIP `03ff7036`):

```csharp
if (found is not null && found.SideId != ship.SideId
    && (ship.SideId is not { } viewer || !found.SeenBySide(viewer))) return;
```

That check incorrectly required **current detection to select or accept a
command target**. The real flow is:

1. An issuer's command chat passes through `ProcessDatagram`'s chat relay and
   `QueueOrdersFromRelayedChat`. `QueueCommandForPlayer` stores the command and
   exact target in the recipient's queued slot and sends its owner
   `QUEUED_COMMAND_UPDATE(215)`. The existing live scenario queues through the
   same writer directly; the new regression also exercises the issuer packet.
2. MCP `BotManager.HandleOrders` sends `ORDER_CHANGE(CmdAccepted)` with that
   command and target **once**, then bookmarks the command as accepted locally
   (`tools/allegiance-mcp/BotManager.cs:1998-2006`).
3. If detection has not reached the server's visibility state yet, the P655
   check returns before writing `AcceptedShipTarget/AcceptedShipCommandId`.
   Later combat-loop `CmdCurrent` packets can succeed after detection catches
   up, leaving precisely `accepted=-1` with the commanded current target set.
4. `queued=-1` alone does not prove acceptance: the server's existing 15-second
   simulation-time auto-reject also clears it. The live test's captured log
   demonstrates that timeout. The port's explicit own-ship `CmdQueued` accept
   path promotes and clears immediately; the new packet test verifies that
   path too. Queue signaling and timeout behavior were not changed by P655b.

This is an order-slot validation error, independent of name eligibility or
privilege authentication. Removing these two predicate lines restores the
legitimate order handling; no harness grant or privileged bypass is involved.

### Native rules and hostile controls

All native paths here are relative to `~/Development/Allegiance/src/` and were
checked against the local source:

| Rule | Native evidence and resulting behavior |
| --- | --- |
| Order target selection/acceptance does not require current detection | `FedSrv/FedSrv.CPP:7264-7278,7335-7339` checks mission stage, resolves the commanded ship, resolves the mission model, and calls `SetCommand`. `Igc/shipIGC.h:725-790` stores the slot/target without a visibility check. P655b removes the extra visibility gate from own-ship current and accepted slots. |
| A legal queued order can be accepted before the target is detected | `WinTrek/WinTrek.cpp:10652-10656,10682-10696` checks the queued command/model and `LegalCommand`, calls `SetTarget` and `SetAccepted`, then clears the client-local queue. `Igc/shipIGC.h:1604-1647` checks legality, including mission identity and the player Goto/Attack cases, without requiring visibility. The port retains its pre-existing server-owned queued-slot protocol. |
| Missile lock requires visibility independently of command selection | `Igc/magazineigc.cpp:127-132` requires same cluster, hostile side, a valid damageable target and `CanSee`. The port's existing lock and fire-time visibility checks remain. The strengthened packet test proves that hidden-target fire consumes no ammo and creates no missile, including when visibility disappears after full lock; detecting the target then permits an actual missile and consumes one round. |
| Another real player cannot overwrite the recipient's own current/accepted slots | The P655 server ownership check remains before both writes. This hardens native's trusted-client same-side lookup at `FedSrv/FedSrv.CPP:7271-7278,7335-7339`; it is an additional port authority policy, not a claimed native restriction. Both slot-overwrite attempts are tested. Native same-side NPC behavior remains unchanged. |

The prior H-25 table's claim that direct current/accepted enemy targets must be
detected is superseded by these native rules. Target selection and weapon lock
have distinct requirements; accepting an order does not grant a missile lock.

### Regression tests and verification

Added `P655GameStateIntegrityTests.H25_Relayed_order_accepts_and_selects_specific_target_before_detection`
beside the hostile tests. It encodes a teammate's real order chat, verifies the
recipient's queued target and owner notification, sends the same single
`CmdAccepted` as MCP, selects that exact target with `CmdCurrent` while it is
still undetected, and verifies authoritative queued-accept promotion/clearing.
The existing H-25 test now checks native hidden-target selection, both hostile
player-slot overwrites, hidden lock/fire refusal, loss-of-visibility fire-time
refusal, and successful visible-target missile firing.

Before the production fix, both packet tests failed: acceptance stayed `-1`
instead of `CidGoto` (`5`), and the current target stayed null. After removing
the gate, both passed. Raw failing-first evidence is in
`/tmp/p655b/red-h25.{trx,log}`; all subsequent artifacts are in `/tmp/p655b`.

Build passed with **0 warnings / 0 errors** (`dotnet build -m:2 -v:q`).
Every selected method passed **3/3 alone: 59 methods, 62 parameter cases,
177 invocations / 186 passing case executions**, with no isolated skips:

| Isolated coverage | Methods | Cases per repetition |
| --- | ---: | ---: |
| P655 game-state integrity, including the new regression | 26 | 29 |
| P655 directory integrity | 7 | 7 |
| Current/accepted slot, queued accept/reject, quick-chat issuer, MCP translation and live commanded-bot tests | 26 | 26 |

Complete unfiltered suites ran sequentially after the build and isolated checks:

| Suite | Passed | Failed in full run | Existing skips |
| --- | ---: | ---: | ---: |
| ClientCore, run 1 | 894 | 0 | 1 |
| ClientCore, run 2 | 894 | 0 | 1 |
| ClientCore, run 3 | 894 | 0 | 1 |
| Core | 4,226 | 2 | 0 |
| MonoGame | 2,120 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

The original commanded-bot live test passed **3/3 alone and in all three full
ClientCore runs**, without modifying its fixture, deadlines or assertions.
The skips are the existing opt-in P660 showcase and three unavailable private
Meshy-pack checks. Live/loopback tests and the existing LiveTiming lane ran;
there was no test filter on any full suite.

After **all suites finished**, both full-run Core failures passed **3/3 alone**:

| Test | Full-run observation | Fresh isolated runs |
| --- | --- | --- |
| `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` | `SeatThree` could not find a same-side observer for the first connection's assigned ship, before exercising reload. | pass / pass / pass |
| `StationBucketSpawnsConstructorLiveTests.Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds` | Timed out waiting for construction after the Build order. | pass / pass / pass |

These two observations are load flakes under the brief's rule and are recorded
in `docs/TEST_FLAKES.md`; their tests and product paths were not changed. No
additional test failed 3/3 alone, so no additional candidate required comparison
with the base. The base-pass evidence for the original regression above is
Claude's review from the brief; no new local base run is claimed.

Durable per-method outcomes, suite counters, failing-first results and the six
Core failure reruns are in [P655B_RESULTS.json](P655B_RESULTS.json). Raw TRX/logs
are under `/tmp/p655b`. A final build of the working tree also passed with
**0 warnings / 0 errors** (`build-final.log`), and `git diff --check` passed.
No commits or pushes were made. Rig/221/200v200 remains assigned to Claude.

Reproduce the suites after exporting `local.env`:

```bash
dotnet build -m:2 -v:q
for run in 1 2 3; do
    dotnet test tests/Allegiance.ClientCore.Tests --no-build
done
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
dotnet test tests/Allegiance.Lobby.Tests --no-build
```

For each method in the JSON's `isolation` list, run its recorded project three
times with `--no-build --filter FullyQualifiedName=<test>`; theory rows are
included by the method filter. The two integration-failure methods and their
three-run outcomes are recorded separately in `suiteFailureIsolations`.

## P655c

Codex lane, 2026-10-15, starting at `7eb985e3`. No commits or pushes. All
changes remain in the working tree. Raw logs/TRX and repeat scripts are in
`/tmp/p655c`; the final per-run evidence is in `P655C_RESULTS.json`.

### Cause and comparison controls

The reported failure-rate gap reproduced, but the comparison was not running
the same tests. `integ3` (`6c3e54ad`) contains changes from merge `cd7b4a0c`
that this branch lacks:

- Both cross-mission fixtures determine mission-A/B peers from the actual
  ship/mission assignments instead of assuming socket connection order equals
  spawn order.
- Its roster test waits for every mission-A peer's `GAME_OVER_PLAYERS`, whereas
  this branch stops at the server's `Stage == Over` and immediately asserts on
  asynchronous client receipt.

`git diff integ3 HEAD -- tests/Allegiance.Core.Tests/{GameOverPlayersWireTests,ConquestTerritoryLiveTests}.cs`
shows those exact differences. The single-mission roster fixture on both trees
also assumes `p194_0` is spawn slot 0, despite independent logon receive threads.
The captured failures were the immediate mission-A receipt assertion, a named
winner with the losing side ID, and a supposedly mission-B bot receiving the
mission-A GAME_STATE because it was actually seated in mission A.

An additional **15-run immediate-parent control** used `6977b387` (before P655
and P655b), with its original tests: the roster class passed **11/15**, exactly
the original P655 branch's **11/15** here. These tests are unchanged by the
P655 commits. Inspection of the game-over chain found no P655 authorization
check on result delivery or migration: DeclareGameOver sends to mission members,
then rotation uses AssignMigratedShip and the internal TryJoinSide path, not
remote CheckPositionRequest. The new remote capacity/balance/defection checks,
player-slot ownership checks and authenticated privileges remain intact.

This evidence does **not** establish a P655-specific message-drop regression.
It identifies concrete fixture defects and a base-comparison confound; the
previous three-pass evidence was insufficient to establish reliability. The
native send-order improvement below is separate from those fixture corrections.

### Changes and native references

All native paths here are relative to the locally inspected
`~/Development/Allegiance/src/`.

| Rule | Native source and resulting change |
| --- | --- |
| Finalize results, queue GAME_OVER and the full player roster, then reliably flush before reset/reseat | `FedSrv/fsmission.cpp:3225-3263,3427-3454`. DeclareGameOver now finalizes scores and packs both message kinds into a single guaranteed frame to one mission audience before rotation. Previously it enqueued GAME_OVER, finalized scores, then independently enqueued the roster. This closes the observable split-result window without weakening any request checks. |
| Include losing-side players and split the roster into at most 50 players per message | `FedSrv/fsmission.cpp:3094-3141`. The shared result builder preserves the connected real-side roster, creates native-sized chunks in native reverse-chunk order, and includes them after GAME_OVER in the same frame. |
| Side-only elimination shows the result only to the eliminated side and leaves the mission running | `FedSrv/fsmission.cpp:5420-5426`. SendSideOnlyGameOver uses the same result builder with its existing restricted recipient list; no whole-mission rotation occurs. |
| Consume every roster chunk instead of discarding earlier chunks | `clintlib/appmsg.cpp:3048-3111`: GAME_OVER resets player state and GAME_OVER_PLAYERS updates players individually across messages. ClientWorld now starts a fresh roster at GAME_OVER, accumulates chunks, preserves the result through intermission, and clears it on a connection Reset. The port's previous replace-on-arrival behavior lost 50 of 51 players. |
| Send the next-match timing after reset, with existing players connected | `FedSrv/fsmission.cpp:3430-3454,3468-3490,3539-3546`. The existing rotation, preserved individual readiness and P660 intermission/countdown behavior remain; the live regression exercises two consecutive end/result/reseat/countdown/start cycles. |

The existing port roster policy is retained: connected players on real sides
are listed; there is no native old-player history and no time-played inclusion
filter. Empty rosters retain their existing empty notification. These are
explicit existing port differences, not claims of complete scoring parity.

The live fixtures now finish each peer's join before opening the next when
names are assigned spawn-slot roles, assert that peer/ship mapping, and wait
for the full asserted notification set. The cross-mission conquest wait checks
both mission-A peers. SideEliminationLiveTests had the same named-slot defect:
a 10-run intermediate pass missed the eliminated peer's result once; its
three-side and two-side seating now have the same explicit mapping checks, and
its wait includes both survivors' SIDE_INACTIVE notifications. No deadlines
were extended and no routing, scoring, isolation or hostile-case assertion was
removed. Existing LiveTiming traits are unchanged.

### Regression proofs

`P655GameOverDeliveryTests.Results_are_flushed_together_to_the_native_audience_before_reset`
uses actual server handlers, codecs and the send capture seam. Its four cases
cover whole-mission/side-only results with 2 and 51 players. They assert one
result frame per intended recipient, GAME_OVER before every roster chunk,
complete winner/loser counters, computed scores, no other-mission delivery,
and send-before-reset with fresh counters after migration. All four fail on
the starting server (two result frames per recipient), and pass after the
change (`red-valid-batch.trx`, final isolated artifacts).

`P660CountdownTests.Game_over_roster_accumulates_native_chunks_and_resets_for_the_next_result`
fails on the old client with only 1 of 51 players retained, then passes with
all 51, intermission preservation, replacement on the next result and Reset
cleanup (`red-client-chunks.trx`).

`P660NextMatchLiveTests.Two_consecutive_matches_deliver_both_players_results_and_reseat_every_peer`
uses real loopback ConnectionSessions and the existing forced-conquest end hook. Each of two match endings must deliver
both named players' fresh rescue counters to both clients, preserve their
connections/sides, allocate fresh ships, clear the result at the next start,
and complete the intermission/countdown without simulation-stage failures.

### Repeated rates and full suites

Final rates, per-method isolation totals and complete suite counts follow. Class runs include every case, including LiveTiming;
there are no artwork skips in the targeted runs. Original-tree comparisons
use archived sources, separate build/output directories and the same artwork
and native corpus paths. `integ3` uses its own original fixtures; separate
product-only and fixture-only controls distinguish the changes rather than
attributing their combined improvement to server batching.

| Tree / control | Roster class, all-pass runs | Conquest class, all-pass runs |
| --- | ---: | ---: |
| `integ3` `6c3e54ad`, its original fixtures | 14/15 (93.3%) | 15/15 (100%) |
| Immediate pre-P655 parent `6977b387`, original fixtures | 11/15 (73.3%) | Not measured |
| Starting P655/P655b `7eb985e3`, original fixtures | 11/15 (73.3%) | 12/15 (80%) |
| Starting test assembly + fixed server only, original fixtures | 11/15 (73.3%) | Not measured |
| Starting server + corrected fixtures only | 15/15 (100%) | 15/15 (100%) |
| Final code and corrected fixtures | **15/15 (100%)** | **15/15 (100%)** |

Each class run above executes all five roster or all three conquest cases.
The parent roster had five failed cases across four failed runs; starting
roster had four across four runs; starting conquest had three across three.
The fixed-server-only control copies only the final Allegiance.Server.dll into
an archived starting-tree test output. The fixture-only control rebuilds the
starting tree with only the two corrected live fixture source files. No rate
row pools classes or counts a failed invocation as a pass. The initial mixed
class exploration and a measurement spanning a rebuild are excluded from the
rate table; `head-conquest` is the clean archived starting-tree conquest run.

The three-way control makes the conclusion explicit: **the fixture fixes cure
the observed comparison failures; batching alone does not**. It would be
incorrect to report batching or removal of a hostile-client gate as their root
cause. The initial report's "3/3 alone" argument is superseded by these rates
and the actual failure traces.

Additional final verification:

| Coverage | All-pass runs | Passing case executions |
| --- | ---: | ---: |
| SideEliminationLiveTests, final deterministic seating | 10/10 | 50 |
| P660NextMatchLiveTests + P660CountdownTests, all parameter cases | 10/10 | 100 |
| P660 rebuilt-mission late-clock test, both scale cases | 10/10 | 20 |
| Strengthened two-consecutive-match loopback test alone | 10/10 | 10 |
| P655 native result-frame regression, all four cases | 3/3 | 12 |

The intermediate side-elimination matrix was 9/10 (49 passes, one missing
named Charlie peer result); that result is retained in `fixed-side`, not
reclassified as a load flake. The final row uses `final-side` after correcting
its peer-role setup and waiting for survivor notifications. The strengthened
two-match test's final repeats also require exactly one rescue per player in
each result, proving that second-match counters are fresh.

Reproduce from the working tree after exporting `local.env`:

```bash
set -a
source local.env
set +a
dotnet build -m:2 -v:q
# Repeat each of these class filters 15 times, with a distinct TRX filename:
dotnet test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~GameOverPlayersWireTests
dotnet test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~ConquestTerritoryLiveTests
# Repeat the side-elimination and P660 groups 10 times:
dotnet test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~SideEliminationLiveTests
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~P660NextMatchLiveTests|FullyQualifiedName~P660CountdownTests'
dotnet test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~P660_Assembly
```

Archived-tree builds use `NUGET_HTTP_CACHE_PATH=/tmp/p655c/nuget-http` because
the default NuGet HTTP cache is read-only in this sandbox. The initial cache
permission failure was a build setup issue and is not counted as a test run.

Every new/affected method passed **3/3 alone: 21 methods, 29 parameter cases,
63 invocations / 87 passing case executions**, with no skips or failures.
This includes every method in the roster, conquest, side-elimination, P660
countdown/live, late-clock and new result-frame coverage. Independently, all
**29 P655 hostile/positive-control cases passed 3/3 group runs (87 executions)**;
capacity, balance, defection, ownership, authenticated privileges, movement and
missile protections were not removed to obtain the delivery results.

Final build: **0 warnings / 0 errors** (`dotnet build -m:2 -v:q`). Complete
unfiltered suites ran sequentially after the final build and all final checks.

| Full suite | Passed | Failed | Existing skips |
| --- | ---: | ---: | ---: |
| Core | 4,232 | 0 | 0 |
| ClientCore | 896 | 0 | 1 |
| MonoGame | 2,120 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

**7,267 passes, zero failures**, with the existing opt-in P660 showcase and
three unavailable private Meshy-pack cases skipped. All new/affected cases
executed; live/loopback and LiveTiming coverage was included. There were no
full-suite failures requiring isolated follow-ups. Suite TRXs are under
`/tmp/p655c/suites`; full counters and rate/isolation outcomes are preserved in
[P655C_RESULTS.json](P655C_RESULTS.json). `git diff --check` passed.

Full-suite reproduction:

```bash
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
dotnet test tests/Allegiance.Lobby.Tests --no-build
```

Not run in this lane: rig/221/200v200. Claude retains that verification per the
brief. The two-match loopback regression uses forced match endings and is not
a claim that two 200v200 battles were run. No commit or push was made.
