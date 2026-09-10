# P121 — SET_BRIEFING_TEXT (9): join-sync mission briefing

Status: IMPLEMENTED + VERIFIED (build clean, full suite 2575/2575 ×3 consecutive)
Branch: master (764cd04 P120 + P121)
Date: 2026-09-09

## Summary

`FM_S_SET_BRIEFING_TEXT`(9) is the per-player "here is your mission briefing"
message the native server sends to a single joining player during its join-sync
burst (`FedSrv/fsmission.cpp:725-737`, in `TellTheyCanPlay2`). Like P118's
`SET_MONEY` and P119's `CREATE_BUCKETS`/`BUCKET_STATUS`, it rides the same
join-sync flow this arc has already wired — so this task is a small,
self-contained codec plus one send at the real trigger point. This port's own
earlier research (P105) had already confirmed that `SET_BRIEFING_TEXT`'s
fallback path needs **no new infrastructure**: it can reuse the P105
`FindFriendlyStation` helper this port already has.

## The two native branches — and which one this port takes

Native (`fsmission.cpp:725-737`):

```cpp
// tell them their starting sector so they can display the mission briefing
bool bGenerateCivBriefing = m_strStoryText.IsEmpty();
const char* szBriefingText = bGenerateCivBriefing
    ? GetBase(pside)->GetCluster()->GetName() : m_strStoryText;
BEGIN_PFM_CREATE(g.fm, pfmSetBriefingText, S, SET_BRIEFING_TEXT)
  FM_VAR_PARM(szBriefingText, CB_ZTS)
END_PFM_CREATE
pfmSetBriefingText->fGenerateCivBriefing = bGenerateCivBriefing;
g.fm.SendMessages(pfsPlayer->GetConnection(), FM_GUARANTEED, FM_FLUSH);
```

Branch (1) sends a mission-authored custom story text (`m_strStoryText`, a
`MissionDef`-level field) verbatim with `fGenerateCivBriefing = false`; branch
(2) sends just the starting cluster's name with `fGenerateCivBriefing = true`
so the client generates its own civ-flavored copy.

**Finding, confirmed directly (not guessed):** this port does **not** decode a
mission-level custom story-text field. A grep for `StoryText`/`Story` across
`Wire/MissionDef.cs` returns zero hits (and the port's `MissionDef` has no such
consumer anywhere). Therefore **P121 always takes the `fGenerateCivBriefing =
true` branch** — there is no custom story-text field in this port to transmit.
This is the honest simplification, not a silent drop: the custom-text branch is
a real, separate gap, explicitly out of scope for this task (adding `MissionDef`
field decoding for a feature with no current consumer is not this task's job).

Because this port also has no "mission base"/`GetBase(side)` concept (the same
finding P105 recorded), the starting-location lookup reuses **P105's reusable
stand-in `FindFriendlyStation`** (`Server.cs:4750-4759`): first-found friendly
station in deterministic sector-then-station order, `null` when none exists.
Its cluster name (via the owning sector) is the transmitted text.

## What changed

1. `src/Allegiance.Core/Wire/SetBriefingText.cs` (new) — the `SET_BRIEFING_TEXT`(9)
   codec: a single `FM_VAR_ITEM(text)` carried as a `CB_ZTS` blob (reusing the
   port's existing `ZtsBlob` encode/decode) + the `bool fGenerateCivBriefing`
   byte at offset 8. Fixed region = 9 bytes (header 4 + ib/cb pair 4 + bool 1),
   blob appended immediately after — the packed-FEDMESSAGE convention this port
   pins from `BuyLoadoutAck` (P28). The codec encodes/decodes `false` faithfully
   so it stays byte-correct against the reference protocol, even though the
   server never sends it.

2. `src/Allegiance.Server/Server.cs:1300-1341` — wired into the same join-sync
   block P118/P119 already hook (`ProcessDatagram`, inside the
   `live.Ship.SideId` guard where `SET_MONEY`/`CREATE_BUCKETS` fire). It calls
   `FindFriendlyStation(live.Ship)`, resolves the winning station's cluster
   name, and sends one `SET_BRIEFING_TEXT` with `fGenerateCivBriefing = true` to
   that single joining player's connection — per-player, exactly like
   `SET_MONEY`. When no friendly station exists anywhere (the helper's documented
   `null` return, `Server.cs:4758`), it logs and skips rather than fabricating a
   starting location (native's `GetBase(pside)` is always present, so this does
   not arise in a real mission).

3. `src/Allegiance.Bot/Bot.cs` — the Bot decodes `SET_BRIEFING_TEXT` into a
   `SetBriefingTexts` collection so a loopback test can assert real TCP arrival.

4. Tests (11 new, all green):
   - `tests/.../Wire/SetBriefingTextWireTests.cs` — codec round-trip (both flag
     values), exact 9-byte-fixed + blob byte layout, CB_ZTS trailing-space trim,
     wrong-fmid / too-short / mismatched-cbmsg rejection.
   - `tests/.../LiveBriefingTests.cs` — two real loopbacks over an in-process
     server + real TCP bots:
     * positive: joining players on both sides each receive exactly one
       `SET_BRIEFING_TEXT` with `fGenerateCivBriefing = true` and text equal to
       a real friendly station's cluster name (live: side 0 → "Ceto" station
       1051, side 1 → "Quaoar" station 1052), and **not** the other's (exactly
       one per player ⇒ no side-wide/whole-mission leak);
     * negative (verification point 2): a joining player on a fabricated side
       (733) that owns no friendly station anywhere receives **zero**
       `SET_BRIEFING_TEXT` — the honest skip, asserted empirically, not guessed.

## Verification

- Build: 0 warnings, 0 errors (explicit `/home/domingo/.dotnet/dotnet`).
- New tests: 11/11 pass (filtered run).
- Full suite: 2575/2575 passed, 0 failed — three consecutive runs
  (baseline 2564 + 11 new). No `LiveChatSectorWireTests` / `LiveCombatWireTests`
  flakes encountered this cycle.
- No VM used (loopback Bot + in-process server only).

## Next tractable Phase 4 item

Reading the same native function this message was pulled from
(`fsmission.cpp`), the join-sync burst continues immediately after
`SET_BRIEFING_TEXT` with two more per-player messages, neither of which this
port yet has a codec for (grep confirms only name-table mentions in
`FedMessageDumper`):

- `SET_WINGID`(106) — `Messages.h:705-709`: `ShipID shipID`, `WingID wingID`,
  `bool bCommanded`. Tiny fixed layout, same per-player shape as P118/P121; the
  clear next candidate (wing/commander info for each flagged+wung player).
- `GAIN_FLAG`(156) — `Messages.h:861-865`: `SideID sideidFlag`, `ShipID
  shipidRecipient`, `bool bIsTreasureDocked`, `PartID parttypeidDocked`.
  Also small and per-player, but carries flag/tech-treasure state that may need
  a decision about whether this port models flags.

`SET_WINGID` looks the most directly tractable of the two (no treasure-docking
semantics to resolve).

## Citations (all read directly)

Native (`~/Development/Allegiance/src`):
- `Inc/Messages.h` — `SET_BRIEFING_TEXT`(9) def (`FM_VAR_ITEM(text)` +
  `bool fGenerateCivBriefing`), quoted in full above.
- `FedSrv/fsmission.cpp:725-737` — the real trigger (`TellTheyCanPlay2`), the
  `m_strStoryText` branch vs. `GetBase(pside)->GetCluster()->GetName()`
  branch, sent to `pfsPlayer->GetConnection()` only.
- `FedSrv/fsmission.cpp:741-769` — the immediate successors of `SET_BRIEFING_TEXT`
  in the same burst: per-player `SET_WINGID`(106) + `GAIN_FLAG`(156) (the named
  next candidates).
- `Inc/Messages.h:705-709` — `SET_WINGID`(106) def.
- `Inc/Messages.h:861-865` — `GAIN_FLAG`(156) def.

Port:
- `src/Allegiance.Core/Wire/SetBriefingText.cs` — new codec (this task).
- `src/Allegiance.Core/Wire/ZtsBlob.cs` — the reused CB_ZTS encode/decode.
- `src/Allegiance.Server/Server.cs:1300-1341` — the join-sync send (this task).
- `src/Allegiance.Server/Server.cs:4750-4759` — `FindFriendlyStation` (P105),
  the reused helper (null return at :4758).
- `src/Allegiance.Bot/Bot.cs` — `SetBriefingTexts` decode collection.
- `src/Allegiance.Core/Wire/MissionDef.cs` — confirmed no `StoryText`/`Story`.

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.
