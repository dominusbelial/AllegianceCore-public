# TEAM_LOBBY (P180) Stage 1 — real ready-up gating on a genuine pre-game phase

Branch `master`, on top of the prior P-stage (P179). Ports the self-contained
piece of native's real team-lobby/ready-up system into the in-mission
`Mission`/`Server`/`FM_C/S/CS` protocol: per-side ready-state tracking, a real
`TEAM_READY`(46) whole-mission broadcast, and an actual pre-game hold until
every occupied side is ready. Distinct from `Allegiance.Lobby` (the separate
server-directory app built earlier this session, native's `FM_L_*` family) —
that protocol layer is untouched. Before this task the port's `Mission.Stage`
was force-set to `Started` at boot (`Server.cs:457`), so the mission was
always already running by the time anyone could join; the `NotStarted` /
`Starting` stages existed (`Sim/IgcEnums.cs:64-70`) but were never assigned.

## Background / relationship to native

- **`Inc/Messages.h:380-397`** defines the three ready-up wire structs:
  `PLAYER_READY`(45, client⇄same-team relay), `TEAM_READY`(46, server →
  whole mission), `FORCE_TEAM_READY`(47, leader-only).
- **`FedSrv/fsmission.cpp:5492-5529`** `CheckForSideAllReady` — a side with
  fewer than `nMinPlayersPerTeam` connected players is forced not-ready; a
  force-readied side is ready regardless; otherwise every connected
  player-piloted ship on the side must have `GetReady() == true`.
- **`fsmission.cpp:5449-5471`** `SetReady` — broadcasts `TEAM_READY` to the
  whole mission group only when the side's aggregate flag actually changes.
- **`fsmission.cpp:5485-5489`** `PlayerReadyChange` — the real trigger point,
  reached whenever a player's own ready flag changes.
- **`fsmission.cpp:2229-2270`** `StartGame` — the `STAGE_STARTED` transition:
  connects score objects (N/A — no score system), sets next payday (already
  handled lazily off the sim clock in this port — **verified** nothing in the
  boot/instantiate sequence reads `Stage`, so payday continuity is unaffected),
  then broadcasts `ENTER_GAME` whole-mission.
- **`clintlib/appmsg.cpp:2297-2312`** — the real client's `PLAYER_READY`
  receipt handler only updates local roster UI; no new client-side behaviour
  to port (this port has no client).

This port has **no** `nMinPlayersPerTeam`-equivalent config (confirmed absent
from `GameConfig`/`ServerConfig`) — so the readiness floor is **at least one
connected player**, per the task's explicit instruction (no new config field
invented). And native genuinely leaves the actual start-trigger decision to an
external admin layer (`AdminGame.cpp`) this port has already ruled N/A — so,
per Domingo's explicit decision in the task, the auto-start substitutes a
deliberate policy: **auto-start once every side with at least one connected
player is ready**. `AdminGame.cpp` and any admin-interface concept are out of
scope.

## What changed

- **`src/Allegiance.Core/Wire/TeamReady.cs`** (new) — the `TEAM_READY`(46)
  codec, modeled on the sibling `PlayerReady` codec: header(4) + `SideID
  iSide` short@4 + `bool fReady`@6 + 1 tail-alignment pad@7 = **8 bytes fixed**
  (natural alignment; no pragma pack encloses it in `Messages.h:386-390`).
- **`src/Allegiance.Core/Model/Side.cs`** — added `public bool Ready`,
  following the direct-field-per-side pattern of `Side.Money` (per-side
  runtime state lives on the side, not in a side-keyed dictionary on
  `Mission`).
- **`src/Allegiance.Server/Server.cs`**
  - Added the per-player ready map `Dictionary<ConnectionId,bool> _playerReady`
    — the same ConnectionId-keyed storage shape as `_peerNames`/`_peerShips`
    for per-peer transient state (this port's analogue of native's per-CFSPlayer
    `GetReady()`). Released in `ReleasePeer` alongside `_peerNames`.
  - Changed the boot default `mission.Stage = MissionStage.Started` →
    `MissionStage.NotStarted` (a real pre-game hold).
  - Extended the existing `PLAYER_READY` relay handler: **after** the existing
    same-side relay (kept — the real client still needs it for its roster UI),
    record the player's ready flag and call the new `RecomputeSideReady` —
    the `PlayerReadyChange` analogue.
  - New `RecomputeSideReady(Ship)` — the `CheckForSideAllReady` port with the
    "≥1 connected player" floor; on a genuine aggregate flip it sets
    `Side.Ready` and calls `BroadcastTeamReady` + `TryAutoStart`.
  - New `BroadcastTeamReady(short,bool)` — `TEAM_READY` to the whole mission
    (every connected peer, the existing `_peerShips` sweep idiom matching
    native's `GetGroupMission()`), only on a change.
  - New `TryAutoStart(Mission)` — when `Stage == NotStarted` and every side
    with ≥1 connected player is ready, transition straight `NotStarted →
    Started` (skip the countdown `Starting` stage) and broadcast `ENTER_GAME`.
  - New `BroadcastEnterGameToMission()` — the whole-mission `ENTER_GAME`
    broadcast at the transition (native `StartGame`'s wire effect).
  - **Guarded the join-sync `ENTER_GAME` send**: now only sent when
    `live.Ship.Mission.Stage == Started` (a late joiner to a running mission);
    suppressed while `NotStarted` (the transition broadcast covers it later).
    The guard only ever suppresses when the mission is NOT yet started, never
    when it already is — so a peer joining a running mission still gets its own
    immediate `ENTER_GAME`.
  - **Added the missing `SHIP_UPDATE` control gate**: if
    `mission.Stage != Started`, log-and-drop the control update instead of
    applying it to `Controls`/`StateM` — a real, necessary new gate (the
    control path previously had zero Stage check).
- **`src/Allegiance.Bot/Bot.cs`** — captured `TEAM_READY`(46) receipts
  (`TeamReadys`/`ResetTeamReadys` + decode case) so loopback tests can observe
  the broadcast; added a `SendSuicide()` helper.

## The test blast radius (the big story of this task)

Changing the boot default to `NotStarted` is exactly the blast-radius risk the
task warned about. A full pre-fix run exposed **145 failing tests across 66
test files** — far more than P178's 5 NPC-AI harnesses, but the task's own
prediction ("most live tests probably never send a PLAYER_READY and expect
immediate control") held emphatically. The failure mechanism was uniform: these
tests seat a peer (or spawn an NPC) and immediately expect flight/combat/money/
orders to work, all of which the pre-existing `mission.Stage != Started` gates
(suicide, board, order-change, ripcord, swap/drop-part, promote, etc.) **plus
the new `SHIP_UPDATE` gate** now refuse because the mission is still
`NotStarted`. This is an expected, necessary consequence of the task, not a
bug.

Per the task's prescribed remedy, every one of these tests "doesn't care about
the ready-up mechanic itself and just needs a flyable ship immediately", so
each was fixed the same way: after `Boot(...)`, start the mission directly —

```csharp
server.Boot(Config());
foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
```

(the `foreach` over all missions handles the multi-mission tests uniformly;
`seat.Server.Boot(...)` fixtures get `foreach (var __m in seat.Server.Missions)
...`). This restores each test's pre-P180 behaviour exactly without touching
the ready-up feature — none of these tests exercised the lobby mechanic.

**Count: 145 tests across 66 files** (the 66th file, `LiveTurretGunnerTests.cs`,
holds both the `LiveTurretGunnerTests` and `LiveTurretGunnerBindingTests`
classes). The affected files are the 66 listed in git status under
`tests/Allegiance.Core.Tests/*LiveTests.cs`, `*Tests.cs` (combat, mining,
bucket, ripcord, board, relaunch, logoff, promote, suicide, money, rank,
teleport, turret, NPC-autopilot/dodge/wingman/order-change, payday, station/
probe/asteroid combat, briefing, highlight, chat, …). Additionally,
`LiveEnterGameTests` was **rewritten** (not just stage-started) to match the
new ENTER_GAME timing: the old test asserted an unconditional per-joiner
ENTER_GAME at join-sync; the new one has both bots ready up and asserts each
receives exactly one ENTER_GAME via the transition broadcast (with none before
both are ready). No pre-existing test asserted `NotStarted` semantics, so
forcing `Started` after boot is behavior-preserving for all 145.

## New tests (tests/Allegiance.Core.Tests/LiveTeamLobbyTests.cs, 7)

1. `TeamReady_round_trips_exact_bytes` — codec round-trip + pins the 8-byte
   layout (fmid 46, iSide@4, fReady@6, tail pad@7).
2. `Mission_boots_not_started_and_control_update_ignored_before_start` — boots
   `NotStarted`; a real `SHIP_UPDATE` control is ignored (Controls/StateM stay
   at defaults).
3. `Both_sides_ready_broadcasts_team_ready_and_autostarts` — each bot readies;
   each sees the whole-mission `TEAM_READY(iSide,true)` for BOTH sides, the
   mission transitions to `Started`, both receive exactly one `ENTER_GAME` at
   that point (not before), and a subsequent control update now applies.
4. `One_side_ready_does_not_autostart` — only one of two sides ready: no
   auto-start, stage stays `NotStarted`, no `ENTER_GAME`.
5. `Unready_broadcasts_team_ready_false` — ready then un-ready causes a second
   whole-mission `TEAM_READY(iSide,false)`; stage stays `NotStarted`.
6. `Late_joiner_to_started_mission_still_gets_enter_game` — a peer joining an
   already-auto-started mission still receives its immediate `ENTER_GAME` via
   the join-sync path (the guard doesn't wrongly suppress it), and the first
   peer's ENTER_GAME isn't doubled.
7. `Existing_stage_gates_refuse_while_not_started` — a real SUICIDE while
   genuinely `NotStarted` is refused and the ship survives.

## Divergences from the prompt's assumptions (verified, not guessed)

- **`nMinPlayersPerTeam` floor**: confirmed absent from this port's configs;
  used "≥1 connected player" as instructed. No new config field.
- **Payday continuity at boot**: verified — payday is scheduled lazily off the
  sim clock, not at boot, and no boot/instantiate code reads `Stage`, so the
  `NotStarted` default doesn't disturb it.
- **Blast radius size**: 145 tests / 66 files is far larger than P178's 5 NPC
  harnesses, but squarely what the task anticipated ("most live tests… expect
  immediate control"); all fixed via the sanctioned "set `Stage = Started`"
  remedy, none by weakening the gate.
- **`LiveEnterGameTests` rewritten** for the new ENTER_GAME timing (above) —
  the one pre-existing test whose *assertion* changed shape, not just its setup.
- **No side-switching/leader/force concepts built**: `FORCE_TEAM_READY`(47),
  `SET_TEAM_LEADER`(121), ballots/voting, lobby locking, `Starting`/countdown,
  `SET_START_TIME`(14), `MISSIONPARAMS`(113), `AdminGame` — all remain out of
  scope exactly as the task scoped them. Stage 1 transitions directly
  `NotStarted → Started`, no countdown.

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (Core / Server / Bot / Lobby /
  Tests, 0 W, 0 E).
- New `LiveTeamLobbyTests` green in isolation (7/7); rewritten
  `LiveEnterGameTests` green (1/1).
- **Must-fail-if-removed proven** (hard requirement):
  - Removing the `RecomputeSideReady(...)` call from the `PLAYER_READY` handler
    makes the 5 ready-up tests fail (`Both_sides_ready`, `One_side_ready`,
    `Unready`, `Late_joiner`, `Join_ready_up`) — they need the TEAM_READY /
    auto-start wiring.
  - Removing the `SHIP_UPDATE` stage gate makes
    `Mission_boots_not_started_and_control_update_ignored_before_start` fail
    (the control gets applied).
  - Reverting the boot default to `Started` makes both
    `Mission_boots_not_started…` and `Both_sides_ready…` fail (they assert
    `NotStarted` at boot). All restored → green.
- **Full suite, 3 consecutive clean runs** (2843 tests each):
  - run 2 = **clean 2843/2843** (the first post-fix full run);
  - run 3 = **clean 2843/2843**;
  - run 4 = **clean 2843/2843**.
  Zero failures, including none of the five documented flakes this cycle.
  (Pre-fix run 1: 145 failures — the blast radius above.)

## What I actually ran

Root build; full-suite run 1 (145-failure baseline); automated 66-file
transform (+`LiveEnterGameTests` rewrite); build; full-suite run 2 (clean);
three must-fail-if-removed probes (recompute wiring, ShipUpdate gate, boot
default) each confirmed red then restored; runs 3 & 4 (clean); fmid/count
checks; git diff review.
