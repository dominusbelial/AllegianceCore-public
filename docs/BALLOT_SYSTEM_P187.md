# BALLOT_SYSTEM_P187 — ballot / voting system (resign, mutiny, offer/accept draw)

Task: port native's general voting mechanism — `BALLOT`(17) / `CANCEL_BALLOT`(18)
/ `VOTE`(19) — driven by the `#resign` / `#draw` / `#mutiny` chat commands, and
the four proposal kinds' effects when a vote passes. Two effects tie onto
systems this session already landed: mutiny transfers leadership (P183), and
an accepted draw ends the game (P178's draw/`GameOver`).

## What changed

- **Three new wire codecs** in `src/Allegiance.Core/Wire/`:
  - `Ballot.cs` (17, the only variable-length member — `BallotText` via the
    `FM_VAR_ITEM`/`ZtsBlob` CB_ZTS convention shared with `QuitMission`/`ChatMessage`).
    Fixed 24 bytes: `VarItem ib/cb@4-7` + `otInitiator short@8` +
    `oidInitiator short@10` + `ballotID short@12` + 2 pad `@14-15` +
    `uint32 timeExpiration@16` (native `Time` = millisecond accumulator,
    ztime.h:20-21, 4-aligned) + `bool bHideToLeader@20` + 3 tail pad `@21-23`.
  - `CancelBallot.cs` (18): 6 fixed bytes (header + `ballotID short@4`).
  - `Vote.cs` (19, the one client-sendable message): 8 fixed bytes (header +
    `ballotID short@4` + `bool bAgree@6` + 1 pad `@7`).
- **`src/Allegiance.Core/Model/PendingBallot.cs`** — the pure tally/state object
  (port of native `Ballot`, fsmission.h:85-153) + the `BallotType` enum
  (RESIGN=1 / OFFERDRAW=2 / ACCEPTDRAW=3 / MUTINY=4). Carries id, type,
  initiator ship, voting-side scope vs whole-mission scope, the excluded
  (offering) side for AcceptDraw, per-side `InFavor`/`Opposed`/`Abstaining`
  tallies sized `c_cSidesMax`=6, the eligible-voter ship list, and the
  expiration. The tally rules are transcribed verbatim from native (below).
- **`Server.cs`** — the per-mission pending-ballot list (`_ballots`) + a
  server-wide `_nextBallotId` counter (port of native `Ballot::s_ballotIDNext`);
  the chat-command interception inside the existing `ChatMessage` handler
  (`TryHandleBallotCommand`); the `VOTE` dispatch; a `ResolveBallots` pass in
  `SimPass` (the port's FedSrv-tick equivalent); and the four OnPassed effects
  (`ApplyMutiny`/`ApplyResign`/`ApplyOfferDraw`/`ApplyAcceptDraw`).
- **`Bot.cs`** — `Ballots`/`CancelBallots` accumulators + `ResetBallotSignals`,
  a `SendVote(ballotId, agree)` send, and `Ballot`/`CancelBallot` decode cases.
- **Tests** — `tests/Allegiance.Core.Tests/BallotVotingLiveTests.cs` (11 tests):
  codec byte-pins, the pure tally rule, both mutiny trigger paths, mutiny
  pass/fail leadership outcome, duplicate-mutiny refusal, non-eligible VOTE
  ignored, the resign per-player removal, the end-to-end draw chain, and
  multi-mission + multi-side BALLOT scoping.

## The pass/fail tally rule — quoted from native, not paraphrased

Resolution is driven by `Ballot::Update` (fsmission.cpp:5817-5834), evaluated
every server tick:

```c
bool Ballot::Update(const Time& now)
{
  if (m_bCanceled)
    return true;

  if (m_timeExpiration < now || AllVotesAreIn())
  {
    if (HasPassed())
      OnPassed();
    else
      OnFailed();

    return true;
  }
  else
    return false;
}
```

i.e. a ballot resolves when its 30-second expiration passes **or** every
eligible voter has voted (`AllVotesAreIn`), whichever first — there is **no
quorum minimum**. The outcome is decided by `Ballot::HasPassed`
(fsmission.cpp:5836-5854):

```c
bool Ballot::HasPassed()
{
  for (SideID sideID = 0; sideID < c_cSidesMax; ++sideID)
  {
    // if there were any votes on this side
    if (m_cAbstaining[sideID] + m_cInFavor[sideID] + m_cOpposed[sideID])
    {
      // if it didn't get a majority, it didn't pass.
      if (m_cInFavor[sideID] <= m_cOpposed[sideID])
      {
        return false;
      }
    }
  }

  return true;
}
```

So the rule is: **a plain majority on every side that saw any vote** — for each
participating side, in-favor must *strictly* exceed opposed; if any
participating side has in-favor ≤ opposed, the whole ballot fails. `AllVotesAreIn`
(fsmission.cpp:5867-5875) is simply *every `m_cAbstaining[side] == 0`*.
`CastVote` (fsmission.cpp:5847-5865) refuses a vote from a ship not in the
eligible list, decrements that side's abstaining count, and bumps
in-favor/opposed. A corollary worth noting: an **expired ballot with zero
votes** (no participating side) *passes* under this logic — the loop never
finds a failing side. In practice the initiator's own vote is auto-recorded in
favor on creation (`CastVote(pfsInitiator, true)`, fsmission.cpp:5939), so a
player-initiated ballot always has at least one vote; an AcceptDraw with no
eligible players on the other sides also vacuously passes.

The `PendingBallot` in this port reproduces these three methods
line-for-line (`ShouldResolve`, `HasPassed`, `AllVotesAreIn`, `TryCastVote`),
exercised directly by `PendingBallot_tally_matches_native_majority_rule`.

## Trigger mechanism (a genuine surprise, confirmed)

Ballots are **not** created by a dedicated message. They are parsed out of
ordinary chat text inside the `FM_CS_CHATMESSAGE` handler
(FedSrv.CPP:5164-5200), gated on `Stage == STAGE_STARTED` and the sender being
seated on a real side. `#resign`/`#draw`/`#mutiny` are matched
case-insensitively (`_strnicmp`) with a length guard (`cbMessage > strlen(...)`),
each refused with a private reply if a ballot of that type is already in
flight (`HasBallots`); `#mutiny` is additionally refused if the sender is the
side's own leader. Whenever any of the three match, the chat is **not
forwarded** (`bForward = false`). This port mirrors that placement: the
interception sits inside the existing `Server.cs` `ChatMessage` branch, ahead
of the normal relay, swallowing the command text whether it creates or refuses.

## Substitutions & divergences from the prompt's assumptions (all verified against the live native source)

1. **`CANCEL_BALLOT` is never emitted by the native server — the prompt's
   "broadcast CANCEL_BALLOT(18) either way" is false.** Grepping the live tree,
   the only `CANCEL_BALLOT` consumers are the *client*
   (clintlib/appmsg.cpp:377-386) and the header/Wopr bindings; the FedSrv
   server never constructs the message. `Ballot::Cancel`
   (fsmission.cpp:5836-5845) leaves an unanswered `// REVIEW: should we send a
   cancelation message here?`, and `Ballot::Update` resolves by calling
   `OnPassed`/`OnFailed` and returning — no cancel send. Accordingly this port
   **does not emit `CANCEL_BALLOT` on resolution** (faithful to native); the
   `CancelBallot` codec is built for wire completeness and round-trip tested
   but is inert. This is the principal correction to the task brief.

2. **Resign-ballot substitution (stated explicitly, not silently dropped).**
   Native `ResignBallot::OnPassed` calls `DeactivateSide` (fsmission.cpp:
   6170-6180), which is out of scope (no side deactivation in this port). The
   buildable approximation: every remaining player on the resigning side is
   withdrawn from the mission — each leaver's ship is removed from its sector
   (stops flying), its side cleared, `QUIT_MISSION` broadcast whole-mission
   (reusing P183's `BroadcastQuitMission`), and the peer dropped from the
   roster; the side's leadership and (if it owned the mission) the
   mission-owner role are released exactly as an emptying side does. The
   `QUIT_MISSION` reason is reused as `QuitSideReason.LinkDead` because native
   emits *nothing* for a voted resignation (it deactivates) — there is no
   faithful reason value to use. The proof test
   `Resign_passing_removes_every_player_on_side` asserts both players vanish
   from the roster and the side/owner roles clear.

3. **Mutiny's donation/money/wing bookkeeping is N/A.** Native
   `MutinyBallot::OnPassed` (fsmission.cpp:6117-6168) also stops the old
   leader's auto-donation, transfers money to the new leader and re-wings
   ships. This port has no donations (P119), money model, or wings/squads, so
   the leadership transfer is reduced to the real, observable core — updating
   `Side.LeaderShipId` and broadcasting `SET_TEAM_LEADER` (reusing P183's
   `BroadcastSetTeamLeader`), with the native re-validation that the initiator
   is still on the side when the vote closes.

4. **AcceptDraw eligibility has no "active side" filter.** Native
   `Ballot::Init(CFSSide*)` skips non-active sides (`pside->GetActiveF()`). This
   port has no active/inactive side concept, so the AcceptDraw ballot's
   eligible voters are *every seated player in the mission whose side is not
   the offering side* — a documented simplification of the active-side gate.

5. **`bHideToLeader` is decode-only.** It is a UI hint (whether the leader sees
   who proposed) with no behavior in this port (no client). It is still written
   faithfully to the wire — `true` for mutiny (KGJV #110), `false` for the
   others.

6. **Offer-draw is voted by the offering side only; accept-draw by the other
   sides.** `OfferDrawBallot` is player-initiated, so its ballot is scoped to
   the proposer's own side; only when it passes is the mission-wide
   `AcceptDrawBallot` created, whose eligible voters are the *other* sides (the
   offering side excluded). This matches the task's stated chain and is proven
   end-to-end by `Draw_end_to_end_offer_then_accept_then_game_over`.

7. **`AcceptDrawBallot` reuses P178's draw outcome, not a second path.**
   `ApplyAcceptDraw` → `DeclareDraw` flips the mission to `Over` and broadcasts
   `GAME_OVER(62)` built by `GameOver.ForMission(mission, NA,
   "The game was declared a draw")` to the whole mission — the same NA-winner
   draw outcome `CheckForVictoryByInactiveSides` already produces (Server.cs:
   1273-1310), exactly the "no winner" case.

## Out-of-scope confirmations

- `DeactivateSide` / real side deactivation — not built; resign uses the
  per-player withdrawal substitution above.
- No second game-over path — accept-draw reuses P178's mechanism.
- `bHideToLeader` — decode-only, no behavior.

## Tests (11 new)

`Ballot_codec_round_trips_exact_bytes`,
`CancelBallot_and_Vote_codecs_round_trip_exact_bytes`,
`PendingBallot_tally_matches_native_majority_rule`,
`Mutiny_from_non_leader_creates_ballot_and_broadcasts_to_side`,
`Mutiny_from_own_leader_refused_no_ballot`,
`Mutiny_passing_transfers_leadership_failing_keeps_it`,
`Second_mutiny_in_flight_refused_with_private_reply`,
`Vote_from_non_eligible_ship_is_ignored`,
`Resign_passing_removes_every_player_on_side`,
`Draw_end_to_end_offer_then_accept_then_game_over`,
`Multi_mission_ballot_scopes_per_mission_and_per_side`.

Verified "must-fail-if-removed": with the chat interception bypassed, 7 of the
11 fail (all live ballot behaviors); the 4 that stay green are the codec/tally
unit tests and the leader-refusal negative test (which asserts no ballot is
created). Full-suite baseline rose 2905 → 2916; three consecutive full-suite
runs were clean.
