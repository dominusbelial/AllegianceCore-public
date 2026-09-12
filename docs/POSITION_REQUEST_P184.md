# POSITION_REQUEST (P184) — player-initiated, leader-gated side-join + FORCE_TEAM_READY

Branch `master`, on top of P183. Ports native's real position-request workflow
(the flow that seats a ship on a side only after the target side's leader
approves it) plus the `FORCE_TEAM_READY(47)` leader ability. Native's genuine
flow, which P183 deliberately deferred: a player asks for a side
(`POSITIONREQ(39)`); if that side already has a leader the request is
broadcast to the leader for approval (`POSITIONREQ(40)` + a pending-request
record), and the leader answers with `POSITIONACK(41)` (accept → seat, reject →
`DELPOSITIONREQ(DPR_Rejected)`); if the side has no leader yet there is no one
to ask, so native auto-accepts. P183 built the `Side.LeaderShipId` /
`Server.TryJoinSide` primitives; this stage wires the four live client
(`C`/`CS`)-direction messages onto them, adds the pending-request bookkeeping,
and amends the P180 readiness recompute with the persistent per-side
force-ready flag that `FORCE_TEAM_READY` toggles.

## Background / relationship to native (verified against the live source)

- **`Inc/Messages.h:320-330`** — `POSITIONREQ(39, C)` = `{SideID iSide}` and
  `POSITIONREQ(40, S)` = `{ShipID shipID, SideID iSide}`: two distinct shapes
  sharing the logical name, exactly the split the prompt prescribed (the port
  models them as `PositionReq` and `PositionReqBroadcast`, following the
  established `FireExpendable(82/87)` two-class precedent).
- **`Inc/Messages.h:331-336`** — `POSITIONACK(41, C)` = `{ShipID shipID, SideID
  iSide, bool fAccepted}`. Its own stale header comment ("everyone gets either a
  `PLAYER_SIDE_CHANGE` or a `DELPOSITIONREQ`") names an fmid that does not
  exist anywhere in the header — the real accept-outcome broadcast is
  `JOIN_SIDE(125)` (P183). Confirmed and coded to.
- **`Inc/Messages.h:338-364`** — the `DelPositionReqReason` enum, all 24 values
  modeled in a `Wire` enum so the codec can name whatever round-trips; this
  port's own logic only ever *sends* `DPR_Rejected`(0), `DPR_Canceled`(1), and
  `DPR_SideGone`(14).
- **`Inc/Messages.h:366-370`** — `DELPOSITIONREQ(42, CS)` = `{ShipID shipID,
  SideID iSide, DelPositionReqReason reason}` — a genuinely client-sendable
  entry point (self-cancel *or* leader-reject), not just a server broadcast.
- **`Inc/Messages.h:392-397`** — `FORCE_TEAM_READY(47, CS)` = `{SideID iSide,
  bool fForceReady}`.
- **`FedSrv.CPP:6658-6707`** (`FM_C_POSITIONREQ`) — validates the side; the
  `NA`-means-pick-one (`PickNewSide`) branch and the drones-team-0 CD-key
  branch are out of scope (no bot-auth in this port); a nonexistent side gets
  `DELPOSITIONREQ(DPR_SideGone)` to the requester **only**; otherwise
  `RequestPosition`.
- **`fsmission.cpp:4729-4774`** (`RequestPosition`) — the accept-or-queue
  decision: `if ((!GetLeader(sideID) && m_misdef.fAutoAcceptLeaders) ||
  bRejoin || m_misdef.rgfAutoAccept[sideID]) AddPlayerToSide(...) else {
  broadcast POSITIONREQ(40) whole-mission + AddJoinRequest }`. `fAutoAccept
  Leaders` is driven by `AUTO_ACCEPT(52)` (unported) and `bRejoin` is a
  boot-rejoin special case (no boot mechanic here) — **both out of scope**;
  this port's single free-pass is "the target side currently has no leader",
  folding `fAutoAcceptLeaders` to always-on for leaderless sides (stated, never
  silent). A leaderless side is by definition empty in this port, and
  `Server.TryJoinSide` (P183) unconditionally seats the first player as leader —
  so the auto-accept fast path is literally `TryJoinSide` on a leaderless side.
- **`fsmission.cpp:4575-4727`** (`CheckPositionRequest`) — ~10 real gates, all
  but "does the side exist" confirmed inapplicable (defections, `LOCK_SIDES`,
  `rgfActive` deactivation, `STAGE_OVER` server-pause, side bans, squad
  invitations, per-team caps + rank/imbalance, squad membership): none of those
  concepts exist in this port. The handler's own side-existence check covers the
  one reachable gate before `RequestPosition` is even reached, so the port adds
  no separate `CheckPositionRequest` port.
- **`fsmission.cpp:4860-4869` / `4938-4960`** (`AddJoinRequest` /
  `RemoveJoinRequest`) — the flat per-mission pending list. `RemoveJoinRequest`
  with a **null** destination side broadcasts `DELPOSITIONREQ(DPR_Rejected)`
  per removed request (via `NotifyPlayerBoot`, fsmission.cpp:4881-4891); with a
  non-null destination (the accept path) it does not — `JOIN_SIDE` covers it.
- **`fsmission.cpp:460-525`** (`RemovePlayerFromMission`) — calls
  `RemoveJoinRequest(pfsPlayer, NULL)`, i.e. a departing player's pending
  request(s) are cleared **and broadcast** on disconnect — the currently-missing
  piece P183's `ReleasePeer` left out; added there.
- **`FedSrv.CPP:6709-6747`** (`FM_C_POSITIONACK`) — defensive leader re-check
  (never trusts the client's belief it is still leader), target-ship-still-in-
  mission check, then accept (re-check request valid + `RemoveJoinRequest` with a
  destination → seat) or reject (`RemoveJoinRequest` with null → broadcast).
- **`FedSrv.CPP:7484-7516`** (`FM_CS_DELPOSITIONREQ`) — own-ship ⇒
  `DPR_Canceled`; else the sender must be the target side's leader and
  `DPR_Rejected`; forward the exact message whole-mission, then
  `RemoveJoinRequest(pfsPlayer, ...)`.
- **`FedSrv.CPP:6987-7015`** (`FM_CS_PLAYER_READY`/`FORCE_TEAM_READY` shared
  block) — `FORCE_TEAM_READY`'s real gate: `pfsPlayer != GetLeader(iSide)` →
  refuse; forward whole-mission then `SetForceReady(iSide, fForceReady)`.
- **`fsmission.cpp:5441-5448`** (`SetForceReady`) and **`5498-5529`**
  (`CheckForSideAllReady`) — the real semantic: force-ready is a **persistent
  per-side flag**; while set, the side is reported ready regardless of
  individual `PLAYER_READY`s; clearing it re-runs the normal per-player check.

## What changed

- **`src/Allegiance.Core/Model/Side.cs`** — added `public bool ForceReady`
  (default `false`), a direct field beside `Ready`, porting
  `m_misdef.rgfForceReady[iSide]`.
- **Wire codecs** (new, all byte-pinned in tests): `PositionReq`(39, 6 B),
  `PositionReqBroadcast`(40, 8 B), `PositionAck`(41, 10 B), `DelPositionReq`(42,
  12 B), `ForceTeamReady`(47, 8 B), plus the full `DelPositionReqReason` enum.
- **`src/Allegiance.Server/Server.cs`**
  - New `_pendingJoinRequests` (`Dictionary<ConnectionId,(ShipId,SideId)>`,
    keyed by the requester's connection — the same per-connection shape as
    `_playerReady`), with `FindConnectionOfShip(mission, shipId)` (the
    mission-scoped ship→connection resolver) and `RemovePendingJoinRequest`.
  - New `BroadcastPositionReq`(40) and `BroadcastDelPositionReq`(42)
    whole-mission helpers routed through the existing mission-scoped
    `SendToMissionPeers` sweep (the P180/P178 multi-mission bug class).
  - Four new dispatch handlers:
    - `POSITIONREQ(39)`: side-missing → `DELPOSITIONREQ(DPR_SideGone)` to the
      requester only; leaderless side → `TryJoinSide` (auto-accept);
      else `BroadcastPositionReq(40)` + record the pending request.
    - `POSITIONACK(41)`: sender must be seated on the named side and be its
      current leader, target ship still in mission; accept → remove pending +
      `TryJoinSide`; reject → remove pending + `BroadcastDelPositionReq(
      DPR_Rejected)`; silent no-op if no matching pending request.
    - `DELPOSITIONREQ(42)`: own ship ⇒ `Canceled`; else leader + `Rejected`;
      relay the exact received bytes whole-mission and remove the matching
      pending request **without** a second broadcast.
    - `FORCE_TEAM_READY(47)`: sender must be the named side's leader; relay the
      exact bytes whole-mission, set `Side.ForceReady`, re-run
      `RecomputeSideReady`.
  - **`RecomputeSideReady`** (P180) amended: check `Side.ForceReady` first —
    if set and ≥1 connected player, ready is `true` unconditionally; else the
    per-player check as before. The mission-scoped `side`/`mission` resolution
    was hoisted (behavior-neutral reorder) so the flag can be consulted.
  - **`ReleasePeer`** — after `HandleLeaderSuccession`, clear any pending
    request(s) for the departing ship and `BroadcastDelPositionReq(DPR_Rejected)`
    per removed request (the `RemovePlayerFromMission` port).
- **`src/Allegiance.Bot/Bot.cs`** — captured `PositionReqs`/`DelPositionReqs`/
  `ForceTeamReadys` (added to `ResetLeadershipSignals`), plus `SendPositionReq`,
  `SendPositionAck`, `SendDelPositionReq`, `SendForceTeamReady` drivers so
  loopback tests can exercise the real wire.

## Stated simplifications (per scope, never silent)

- **Auto-accept free-pass = "target side has no leader"** alone. Native requires
  `!GetLeader(sideID) && m_misdef.fAutoAcceptLeaders`; `fAutoAcceptLeaders` is
  driven by `AUTO_ACCEPT(52)` (unported), so the port folds it to always-on for
  leaderless sides. In this port a leaderless side is empty, so this is exactly
  `TryJoinSide` on an empty side.
- **One pending request per ship** (last request wins) instead of native's
  unbounded per-mission linked list; a `{ShipId, SideId}` pair per request is
  sufficient per the task, and every test's "cleared" assertions confirm it.
- **`CheckPositionRequest` is not ported** as a function — its only reachable
  gate (side still exists) is covered by the handler's existence check and by
  `TryJoinSide` returning `false` for a missing side.
- **Force-ready still subject to this port's `≥1 connected player` floor**
  (P180's stand-in for `nMinPlayersPerTeam`), matching native's
  `CheckForSideAllReady` ordering (floor first, then force-ready, then
  per-player). `nMinPlayersPerTeam` is deliberately not invented.
- **`DELPOSITIONREQ`'s relay is the exact received bytes** (`ForwardMessage`),
  not a freshly constructed message — for both the self-cancel and leader-reject
  sub-cases, so no second broadcast fires.

## Divergences from the prompt's assumptions (verified against live source)

- **Native's `RemoveJoinRequest(pfsPlayer, ...)` in `FM_CS_DELPOSITIONREQ`
  removes the *sender's* (the leader's) own pending requests, not the named
  requester's** — a native quirk. The port instead removes the **matching
  pending request for the named ship+side** (the obviously-intended behavior,
  reaching the same end state as `POSITIONACK`'s reject branch). The prompt's
  "just remove the matching pending request" instruction is coded to, and this
  deviation is documented here — a deliberate correction of a native
  idiosyncrasy, matching the test `Leader_reject_via_delpositionreq_reaches_same_end_state`.
- **`RequestPosition`'s quit-old-side half is folded into the accept.** Native
  removes the requester from their current side immediately when a permission-
  required request is filed (moving them into the lobby); this port has no lobby
  concept, so a requester with a pending request **stays seated on their current
  side** until the leader accepts (at which point `TryJoinSide` quits it) —
  the tests assert "does not seat the requester yet" on a pending request, which
  is the observably correct behavior here.
- Everything else matched the prompt's verified premises: the five structs'
  layouts, the leader-gates, the whole-mission `ForwardMessage` semantics, the
  `ReleasePeer` pending-clear-and-broadcast, and the `FORCE_TEAM_READY`
  forward-then-apply order.

## New tests (tests/Allegiance.Core.Tests/LivePositionRequestTests.cs, 16)

1. `Position_request_codecs_round_trip_exact_bytes` — POSITIONREQ(39)/POSITIONREQ(40).
2. `Ack_del_force_codecs_round_trip_exact_bytes` — POSITIONACK(41)/DELPOSITIONREQ(42)/FORCE_TEAM_READY(47).
3. `Del_position_req_reason_values_match_native` — the enum's native values round-trip.
4. `Empty_side_request_auto_accepts` — leaderless side seats the requester immediately, no POSITIONREQ(40).
5. `Occupied_side_request_broadcasts_and_does_not_seat` — POSITIONREQ(40) whole-mission, requester unseated.
6. `Leader_accept_seats_requester_and_clears_pending` — POSITIONACK accept seats (JOIN_SIDE) and clears; a 2nd accept no-ops.
7. `Leader_reject_broadcasts_and_leaves_unseated` — POSITIONACK reject broadcasts DELPOSITIONREQ(DPR_Rejected), requester unseated, pending cleared.
8. `Non_leader_ack_refused` — a non-leader's POSITIONACK is refused; the real leader can still accept.
9. `Self_cancel_clears_and_foreign_cancel_refused` — DELPOSITIONREQ(DPR_Canceled) by the requester clears; the same from someone else is refused.
10. `Leader_reject_via_delpositionreq_reaches_same_end_state` — direct DELPOSITIONREQ(DPR_Rejected) matches the POSITIONACK path.
11. `Nonexistent_side_request_gets_sidegone_to_requester_only` — DELPOSITIONREQ(DPR_SideGone) to the requester only.
12. `Requester_disconnect_broadcasts_delpositionreq_rejected` — ⭐ must-fail-if-removed ReleasePeer proof.
13. `Force_team_ready_true_auto_starts_even_with_unready_teammate` — ⭐ discriminating force-ready bypass + auto-start.
14. `Force_team_ready_false_reverts_to_normal_check` — un-forcing re-applies the per-player check.
15. `Force_team_ready_non_leader_refused` — a non-leader's FORCE_TEAM_READY is refused.
16. `Multi_mission_position_and_force_scope_per_mission` — POSITIONREQ(40) broadcast and the FORCE_TEAM_READY relay/flag never leak to a same-numeric-side mission B peer (P180/P178 bug class).

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (Core / Server / Bot / Tests,
  0 W, 0 E).
- New `LivePositionRequestTests` green in isolation (16/16).
- **Must-fail-if-removed proven**:
  - Disabling the `ReleasePeer` pending-cleanup broadcast makes
    `Requester_disconnect_broadcasts_delpositionreq_rejected` fail; restored →
    green.
  - Disabling the `RecomputeSideReady` force-ready branch makes
    `Force_team_ready_true_auto_starts_even_with_unready_teammate` **and**
    `Force_team_ready_false_reverts_to_normal_check` fail (the non-leader
    refusal test still passes, as expected — it exercises the gate, not the
    bypass); restored → green.
- **Full suite: 12 runs, ending in 3 consecutive clean (2888 tests each;
  baseline 2872 + 16 new):**
  - runs 1, 4, 5, 10, 11, 12 = **2888/2888 clean** (three consecutive at the end).
  - Intermediate interruptions were ONLY the documented environmental live-
    network flakes, each confirmed intermittent in isolation and with zero
    references to any symbol this task changed: `LiveCombatWireTests.
    Real_Bot_class_observes_its_own_kill_over_the_wire` and `Moving_target_
    streams_its_falling_hull_over_the_wire` (LiveCombatWireTests class),
    `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`
    (known P179 flake), and `LiveSuicideShipDeleteTests.Combat_death_produces_
    ship_delete_to_same_audience` (one of the prompt's six). Rapid back-to-back
    full runs amplify this class (ephemeral-port/socket exhaustion) — spaced
    runs went clean.
- No pre-existing test needed adjustment for the new handlers/broadcasts.

## What I actually ran

Verified the five native structs and all five handlers in `Messages.h` /
`FedSrv.CPP` / `fsmission.cpp`; implemented 5 codecs + the 24-value enum + the
`Side.ForceReady` field; amended `RecomputeSideReady`; added the pending-request
map + two broadcast helpers + the four dispatch handlers; wired `ReleasePeer`;
extended `Bot.cs` captures/drivers; built clean; wrote 16 tests; iterated on
spawn-round-robin/leadership-seeding test bugs (seed leadership on an EMPTY
side before seating teammates, per P183); 2 must-fail-if-removed probes (red
when wiring removed, restored); 12 full-suite runs ending in 3 consecutive
clean; git diff review.
