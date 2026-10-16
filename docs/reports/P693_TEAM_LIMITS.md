# P693 — native team limits and explicit side-selection refusals

Base: `integ9`, `9e85c8fa8bce2a9dd607318abe63b703d89cb81f`.
Changes remain in the working tree; no commit or push.

## Native evidence and chosen default

All native references below are under `~/Development/Allegiance/src/`.

| Rule | Native evidence | Port behavior |
| --- | --- | --- |
| Raw `MissionParams` construction initializes imbalance to **1**. | `Igc/igc.h:1303` | Explicit numeric configurations still use the numeric mode. This raw initializer is distinct from the lobby's effective default. |
| Team capacity defaults to **100**. | `Igc/igc.h:1334`; standalone server also caps its calculated default at 100 in `FedSrv/FedSrv.CPP:9038`. | `GameConfig.MaxPlayersPerTeam` defaults to 100, including omitted source-generated JSON fields. Explicit larger capacities remain supported for extended fleet scenarios. |
| Lobby-created missions select **0x7ffe (32766)**. Child server startup selects the same value. | `FedSrv/fslobby.cpp:29-34`; `FedSrv/FedSrv.CPP:9054`. | `GameConfig.MaxTeamImbalance` defaults to 32766, matching an actual lobby-created game. **0x7ffe is rank balance, not a no-limit value.** |
| **0x7fff (32767)** disables imbalance checks. | `FedSrv/fsmission.cpp:4612`; rank branch at `:4646`; botbrawl explicitly uses N/A at `FedSrv/FedSrv.CPP:9122`. | Unlimited balance remains selectable independently of capacity. |
| Numeric balance applies after `STAGE_NOTSTARTED`, against active sides: the effective ceiling is the minimum of capacity and each active side's population plus configured imbalance. | `FedSrv/fsmission.cpp:4608-4623`. | Same stage gate and ceiling, including a literal zero imbalance without coercion to one. |
| Loss of base is checked before team capacity; capacity always refuses at its ceiling. | `FedSrv/fsmission.cpp:4625-4635`. | Same precedence; bot authentication never bypasses capacity. |
| Low-rank newcomers may exceed the numeric balance ceiling by two; authenticated bots bypass that numeric gate. | `FedSrv/fsmission.cpp:4637-4642`; default `MaxNewbRank=4` at `FedSrv/FedSrv.H:246` and `FedSrv/FedSrv.CPP:9640-9642`. | Rank <= 4 receives the native allowance. Bot bypass still requires authenticated harness provenance. |
| Rank mode accepts only a side whose current rank sum equals the lowest active side's sum. The numeric bot exception does not skip this later branch. | `FedSrv/fsmission.cpp:4646-4671`. | Rank mode uses the existing server `PlayerScore.Rank`, checks every active side, and also applies to authenticated MCP bots. |
| Rank sum counts each player's rank with a floor of one. | `FedSrv/fsmission.cpp:1352-1369`; player counting at `:1326-1337`. | Sum includes connected peer ships in the same mission, excludes NPC drones, and floors each rank at one. |
| A refused join replies only to its requester with `FM_CS_DELPOSITIONREQ` and the exact reason. | `FedSrv/fsmission.cpp:4734-4747`; FM 42 and `DPR_TeamFull` / `DPR_TeamBalance` definitions at `Inc/Messages.h:338-344,366-370`. | Sends existing wire `DelPositionReq` with ship, requested side and reason; retains initial pending state for another request. |
| Native client presents a full-team or team-balance explanation. | `WinTrek/teamscreen.cpp:3572-3578`. | Existing client refusal state receives the matching FM; MCP clients now consume it immediately. Native uses this refusal message, so no extra server chat was invented. |

Persistent account-rank loading and the native registry override for `MaxNewbRank`
are outside this patch. Existing port peers start at rank zero; the check uses
any rank already present in `PlayerScore`. Invitations, bans and squad rules remain
outside the existing side-integrity implementation.

## Changes

The observed silence had two causes. Initial selection already emitted the native
refusal, but logged through verbose-gated `ConsoleLog.Write`; combat and plain
MCP bots waited only for a ship assignment. `RefuseSideSelection` now sends the
native FM and one unconditional, sanitized line per refused request:

```text
[sideselect] refused peer <id> '<name>' ship <id> side <id>: TeamBalance
```

Initial selection, seated requests, leader approval and auto-accept rechecks use
the shared refusal path. Refused initial peers remain pending with their choice
cleared. Auto-pick preserves a meaningful non-capacity reason when no side is
eligible instead of unconditionally reporting `TeamFull`.

Combat, idle and weave spawns read their own refusal. For `TeamBalance` or
`TeamFull`, they request server auto-pick once; any subsequent refusal or other
reason returns a specific error. These attempts share the original 15-second
seat deadline. A side-specific commander retries its requested side until the
same deadline, preserving the side whose wallet/orders it manages. Existing
pending-ownership cleanup disposes failed peers.

Plain bots now determine their actual side from their own `PLAYERINFO` snapshot
or latest `JOIN_SIDE`, so a successful fallback is reported accurately. Scenario
and skirmish batches alternate between fleets, starting opposite the probe,
while preserving slot indices, side-specific routes and arena placement plans.
The hull matrix also seats the side opposite the probe first, so its actual
per-side counts remain correct under rank balance. This permits showcase and
matrix spawning with native rank balance instead of requiring
`maxTeamImbalance: 400`.

The MCP lifecycle fixture now chooses its target opposite the bot's confirmed
actual side. Both eligible teams use Iron Coalition so retries retain the
hull-444 pod regression. Its real purchase, damage, death, docking, re-purchase,
launch, re-engagement and cleanup assertions remain intact.

The crowded broadcast fixture deliberately seats 256 real TCP peers. Native's
100-per-team default cannot hold its 128v128 workload. Its pipeline test failed in
three repeated diagnostic runs before the fixture correction. `RunCrowded` now
explicitly requests `(count + 1) / 2` places per team; its workload and assertions are intact.
This is a required fixture capacity setting, not a timing flake or an imbalance
workaround. All three affected benchmark methods passed 3/3 isolated repetitions
after the correction.

The worker regression now lets both probes auto-pick and expects one per side;
forcing both to side zero after start would contradict native rank mode even
with valid harness authentication. Numeric-mode bot-bypass tests explicitly use
numeric imbalance and enough peers to exhaust the native newcomer allowance.

## Verification

The full solution builds with zero warnings and errors. NuGet's HTTP cache was
redirected to `/tmp/p693-nuget-http` because the default cache is outside this
lane's writable roots; vulnerability auditing remains enabled. The final build
uses `-p:UseSharedCompilation=false -nr:false` to avoid a compiler server running
with a different lane's filesystem permissions.

The successful unfiltered Core run used the final Core binaries. SHA-256 checks
confirmed that `Allegiance.Core.Tests.dll` and its Core, Server and Bot dependencies
remained byte-for-byte identical across the later MCP-only rebuild. ClientCore
and MonoGame were run again after those final MCP and lifecycle-fixture changes.

The focused cohort comprises **38 actual test methods**, each passing **3/3**
isolated invocations: **174 passing case executions**, zero failures and zero
skips. Three accidentally generated helper-name filters matched no tests and
are excluded from those counts. Commands and final counts are recorded in
[`P693_RESULTS.json`](P693_RESULTS.json). All tests
use the real projects and loopback TCP/MCP paths; no mocks replace the showcase spawn.

The new loopback showcase runs on an **already started mission**, with default
capacity 100 and imbalance 32766. It invokes the actual MCP `spawn_scenario`
showcase preset at size scale 2: **60/60 bots ready, exactly 30 per side, plus the
probe**. This is the largest standard 15-per-side preset multiple under the
unchanged default 64-connections-per-IP cap (`src/Allegiance.Core/Transport/TcpTransport.cs:195`;
configuration documented at `src/Allegiance.Server/ServerConfig.cs:568-573`).
No imbalance or transport-cap override is supplied. Separate tests submit eight consecutive
same-side requests for each of combat, idle and weave; all eight seat, split 4/4.
Capacity-one scenarios verify fallback to the other side and prompt `TeamFull`
when both sides fill. Server tests assert requester-only FM contents, exactly one
refusal log with verbose logging disabled, and retained retry state for both
full-team and rank-balance refusal. Other checks cover numeric newcomer allowance,
rank-weighted selection, the unlimited sentinel, lobby stage and JSON defaults.

### Final full suites and failure triage

All three suites were unfiltered. `ALLEGIANCE_LIVE_P660_SHOWCASE=1` enabled the
existing real 15v15 next-match showcase in both focused and full ClientCore runs.

| Suite | Passed | Failed in full run | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,561 | 0 | 0 |
| ClientCore | 1,015 | 1 | 0 |
| MonoGame | 2,195 | 0 | 3 |

The MonoGame skips are the existing three unavailable private Meshy-pack cases.
The single final ClientCore failure was the existing `Lane=LiveTiming` legacy
warp-arrival test, already listed as a load flake in `docs/TEST_FLAKES.md`.
After all three full suites finished, every method below passed **3/3** exact-method
isolated reruns:

| Method | Observed full-run symptom | Final isolation |
| --- | --- | --- |
| `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | Final ClientCore run: legacy approach clearance was -32.7 instead of positive. | 3/3 pass |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | First Core run: B had not received `HEAVY_SHIPS_UPDATE`. | 3/3 pass |
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | First Core run: shutdown joined a thread that had not started. | 3/3 pass |

These qualify as load flakes under the brief's explicit rule. No assertions,
deadlines, quarantine tags or product behavior were changed for them. This does
not claim a fix for their underlying timing/shutdown behavior.

The first Core run recorded 4,556 passes and five failures: the two timing/transport
failures above and the three crowded fixtures whose capacity was corrected. The
initial ClientCore runs exposed the matrix ordering and lifecycle target-side
assumptions; those were corrected and passed their required triples and the final
full suite. They are **not** classified as load flakes.

Some canceled shell commands left child verification jobs running. Runs that
overlapped those jobs were excluded from the required isolated counts and rerun
serially. The result ledger checks timestamps: earlier verification finished
before the qualifying later focused runs began, those runs do not overlap, and
the final failure isolations follow all three full suites. Three empty helper-name
filters are also recorded separately and excluded. Temporary process-control
files were removed from the working tree.

`git diff --check` passes. HEAD remains the original `integ9` base; no commit or
push was performed.

Rig / 221 / 200v200 runs are reserved for Claude by the brief and were not run.
