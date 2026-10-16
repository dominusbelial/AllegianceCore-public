# P396 — wire the Leaders and Wing chat targets (their real blockers are already gone)

## Why this task exists

This port's own README has tracked five still-deferred `ChatTarget`s
(`Leaders`, `Admin`, `Ship`, `Allies`, `Wing`) since P33, each with a
stated real reason. Auditing those reasons this session found two are
stale: **Leaders** and **Wing** were deferred because their prerequisite
systems ("a team-leader election," "a wing-assignment mechanism") didn't
exist in this codebase — but both have since landed for unrelated reasons
(P183 team leadership, P146 `WingId`) and nobody circled back to wire the
chat dispatcher to use them. This is real, buildable work sitting on
top of infrastructure that already exists.

**`Allies` and `Admin` are correctly excluded from this task** — do not
add them. `Allies`' native forwarder (`ForwardChatMessageToAllies`) was
commented out by native's own team due to a crash
(`FedSrv.CPP:3352`,`:3452` — "imago fixed crash 7/13/09 removing
FwdChatMsgToAllies"); it was never actually functional even in the real
shipped server. `Admin` is literally `//NYI` in native (`FedSrv.CPP:3485`).
Porting either would mean inventing behavior native doesn't have, not
restoring behavior it does.

## Confirmed native mechanics

**Leaders** (`FedSrv.CPP:3467-3480`, `case CHAT_LEADERS`):
```cpp
int iSide = pfsMission->GetMissionDef()->misparms.nTeams;
while (iSide-- > 0)
{
    CFSPlayer* pfsLeader = pfsMission->GetLeader(iSide);
    if (pfsLeader && (pcd->commandID == c_cidNone))
        g.fm.ForwardMessage(pfsLeader->GetConnection(), pfm, FM_GUARANTEED);
}
```
Sends to **every side's** leader (not just the sender's own side) — a
cross-team "leaders channel." The `commandID == c_cidNone` guard excludes
whatever the non-`c_cidNone` case is used for elsewhere (drone-order
chat-buoy dispatch reuses the same message shape with a real commandID —
this port's Wopr-adjacent research earlier this session touched exactly
this pattern; a plain player leaders-chat message will have
`c_cidNone`, so this guard should be trivially satisfiable — confirm, don't
assume).

**Wing** (`ForwardChatMessageToWing`, `FedSrv.CPP:3327-3345`, called for
the direct chat case at `FedSrv.CPP:3594`:
`ForwardChatMessageToWing(pfsMission, pfsSender, pfm, pcd->oidRecipient, NULL)`):
```cpp
for (ShipLinkIGC* psl = pfsSender->GetIGCShip()->GetSide()->GetShips()->first();
     psl != NULL; psl = psl->next())
{
    IshipIGC* pship = psl->data();
    if ((pship->GetWingID() == wid) && (pship != pshipSkip))
        // forward to pship's connection
}
```
Same-side ships only, whose `WingID` matches `pcd->oidRecipient` (the wing
id carried in the chat message itself), excluding `pshipSkip` (`NULL` for
a direct player wing-chat — nobody is skipped; the skip parameter is used
only by the separate drone-order-echo call sites at `FedSrv.CPP:3566-3571`,
out of scope here).

## Confirmed existing port infrastructure to reuse

- **`IsSideLeader(mission, ship)`** (`Server.Lobby.cs:1217`) — already
  resolves whether a given ship is its side's current leader. `Leaders`
  needs the *reverse* lookup (given a side, find its leader's connection)
  — check whether a direct "get this side's leader ship/peer" accessor
  already exists nearby (P183's own leader-tracking state, however it's
  stored) before writing a new O(peers) scan; reuse the existing storage
  if there is one.
- **`Ship.WingId`** (`Ship.cs:647`, `public short WingId { get; set; } =
  0`) — already a real, settable field. Confirm what (if anything) already
  writes to it (search for existing `WingId =` assignments — `SET_WINGID_P146`)
  and how a chat sender's message would carry the target wing id (the
  wire `ChatMessage`'s `OidRecipient` field, matching native's
  `pcd->oidRecipient` usage above).
- The existing chat-target dispatch switch (`Server.Lobby.cs`, the
  `case (int)ChatTarget.X:` block already handling `Everyone`/`Team`/
  `Individual`/`AllSector`/`FriendlySector`) is the exact pattern to
  extend — two new `case` arms, same shape as the existing ones (a LINQ
  filter over `_peerShips` returning the recipient `ConnectionId[]`), not
  a parallel dispatch mechanism.

## Scope

1. Add `case (int)ChatTarget.Leaders:` — recipients are every side's
   current leader (all sides, not just the sender's), matching native's
   cross-team semantics exactly. If this port's leader-tracking already
   exposes "peer id of this side's leader" directly, use it; otherwise
   resolve via `IsSideLeader` over `_peerShips`.
2. Add `case (int)ChatTarget.Wing:` — recipients are every same-side peer
   ship whose `WingId` matches the message's target wing id
   (`chat.OidRecipient`, mirroring the `AllSector`/`FriendlySector` cases'
   existing use of `chat.OidRecipient` for their own target keys — check
   the exact field name in context). No skip parameter needed for the
   direct player-chat case.
3. Confirm `WingId` is actually assignable by a real, reachable game
   mechanism today (not just a settable-but-never-set field) — if nothing
   currently sets it to a non-zero value, note that plainly: the chat
   routing itself is real, but a wing-chat message would currently only
   ever reach ships still at the default WingId 0 (effectively "no wing
   chat happens" until wing assignment is separately wired) — don't
   silently paper over that gap or pretend it doesn't exist.
4. Wire codec / round-trip tests for both targets, matching the existing
   pattern already used for `AllSector`/`FriendlySector`.

## Explicitly out of scope

- `Allies` and `Admin` — see above, do not implement either.
- `Ship` (parent/child routing) — a separate, not-yet-re-verified
  prerequisite question; not this task.
- Any wing-assignment UI/mechanism if it turns out not to exist yet (point
  3 above) — report the gap, don't build a whole wing-assignment feature
  as a side effect of this chat-routing task.
- P368-P395 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: a real multi-peer test — Leaders: with two
  sides each having an elected leader, a `Leaders`-targeted chat from any
  player reaches both leaders' peers, and no one else. Wing: with two
  same-side ships sharing a `WingId` (however it gets set — even if only a
  live test manually assigns it, per point 3) and a third same-side ship
  on a different (or default) wing, a `Wing`-targeted chat reaches only
  the matching-wing peers.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P396_CHAT_TARGETS_LEADERS_WING.md` in the repo root: the
   leader-lookup mechanism used, the `WingId`-assignability finding from
   point 3, and the live multi-peer verification for both targets.
