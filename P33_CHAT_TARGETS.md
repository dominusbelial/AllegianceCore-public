# P33 — Chat targets: Individual, AllSector, FriendlySector (+ Group alias)

Wire the remaining chat `ChatTarget`s that this codebase can genuinely
express, and document precisely why the rest stay unwired. All decisions are
grounded in the real original server's own dispatch function,
`ForwardChatMessage` (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3355-3596`),
which I read line-by-line before writing anything.

## What got wired (scope item 1)

`Server.cs::SelectChatTargets` (`src/Allegiance.Server/Server.cs:779-...`) now
handles seven targets total (was two):

| Target | Value (igc.h) | Real dispatch (FedSrv.CPP) | Behavior here |
|---|---|---|---|
| Everyone | 0 | `:3382-3390` | every peer ship (unchanged) |
| Team | 5 | `:3518-3529` | peers with `SideId == senderSideId` (unchanged) |
| **Individual** | 6 | `:3538-3556` | peer whose `Ship.Id == OidRecipient`, same side only |
| **Individual_NoFilter** | 7 | `:3538-3556` (same block) | identical to Individual (no mute list → collapse) |
| **AllSector** | 10 | `:3428-3441` | peers whose `Sector.Id == OidRecipient`, any side |
| **FriendlySector** | 11 | `:3392-3424` | peers in `Sector.Id == OidRecipient` with same side |
| **Group / GroupNoEcho** | 12 / 13 | `:3531-3536` | alias for Individual (real server rewrites + falls through) |

`OidRecipient` is the target field both the sector and individual rules read —
it was already the exact wire field (`ChatMessage.cs:58,115` ↔ `pcd->oidRecipient`
in the real struct), so this task was pure `SelectChatTargets` logic, zero new
codec work.

### Simplifications, each deliberate

- **Individual same-side-only.** Real dispatch also allows allied sides
  (`pfsShipTo->GetSide() == pfsSender->GetSide() || AlliedSides(...)`,
  `:3550`). This codebase has no alliance system (`AlliedSides` anywhere), so
  same-side-only is the correct reduction — the same precedent `Team` already
  set (`:3522`). Noted in the code comment.
- **Individual_NoFilter collapses to Individual.** The NoFilter bit exists to
  bypass a mute/ignore list; this server has no such list, so both targets have
  identical behavior and are one case, not two paths (`:825-826`).
- **Group/GroupNoEcho → Individual.** The real dispatch rewrites them to
  `CHAT_INDIVIDUAL` and falls through (`:3531-3536`), so a client-sent Group is
  itself just an Individual alias. Copied that fallthrough exactly with
  `goto case (int)ChatTarget.Individual` (`Server.cs:833-836`).

## Verifying the real sector data

`XXV_simple.igc` has 13 sectors. Ran `missiondump PCore001.igc XXV_simple.igc`
against the loaded map to confirm the exact ids/names used in tests/demo rather
than inventing them:

```
XXV_simple.igc  MAP  clusters=13 …
  sector 1053 'Ceto'    (home)
  sector 1056 'Farout'
```

Tests and `tools/chatdemo` look each sector up by id (asserting it exists) and
place ships across 1053 'Ceto' and 1056 'Farout'.

## Tests

New `LiveChatSectorWireTests` (a `LiveNetwork` test, real bots over loopback
TCP, `ShipSpawner` seam to place 5 ships across the two sectors with
deterministic sides). One `[Fact]` covering five phases:
1. **Individual** → only the addressed same-side ship receives; every other
   peer (cross-side peer in same sector, all other-sector bots) does not.
2. **Individual cross-side** → addressed ship is on the other side; nobody
   receives.
3. **AllSector** to Ceto (1053) → both Ceto bots receive, both sides; Farout
   bots do not.
4. **FriendlySector** to Farout (1056) → both Farout Alphas receive; the
   Farout Beta (cross-side) does not; Ceto does not.
5. **Group** → routed as Individual (fallthrough), only the addressed ship
   receives.

Plus the existing `LiveChatWireTests` (Everyone/Team/Wing-drop) already green.

Suite run 3× — all clean:

```
RUN 1: Passed! Failed 0, Passed 1783, Duration 25 s
RUN 2: Passed! Failed 0, Passed 1783, Duration 22 s
RUN 3: Passed! Failed 0, Passed 1783, Duration 25 s
```

## Live proof

`tools/chatdemo` (extended from P25's tool): a real in-process server + 5 real
bots across the two sectors. Relay lines + verbatim reception:

```
[chat] ship 8098 side 0 'ALL HELLO'    target 0  -> 5 peer(s)   Everyone
[chat] ship 8098 side 0 'TEAM HI'      target 5  -> 3 peer(s)   Team (3 × side 0)
[chat] ship 8098 side 0 'WHISP SAME'   target 6  -> 1 peer(s)   Individual → addressed ship 8099
[chat] ship 8098 side 0 'WHISP ENEMY'  target 6  -> 0 peer(s)   Individual cross-side dropped
[chat] ship 8098 side 0 'SECTOR WIDE'  target 10 -> 2 peer(s)   AllSector → both Ceto bots (8096,8097)
[chat] ship 8098 side 0 'FRND FAROUT'  target 11 -> 2 peer(s)   FriendlySector → both Farout Alphas
[chat] target 8 not implemented … 'WING ONLY' -> 0 peer(s)      Wing still dropped
[demo] RESULT PASS (exit 0)
```

Reception per ship:
- 8096 (Ceto, side0): ALL HELLO | TEAM HI | SECTOR WIDE
- 8097 (Ceto, side1): ALL HELLO | SECTOR WIDE          ← no Team, cross-side
- 8098 (Farout, side0, sender): ALL HELLO | TEAM HI | FRND FAROUT (echo)
- 8099 (Farout, side0): ALL HELLO | TEAM HI | WHISP SAME | FRND FAROUT
- 8100 (Farout, side1): ALL HELLO                      ← only broadcast; no
                                                            whisper/team/friendly

## Still deliberately unwired — real reasons (not restated from the brief)

Each read directly in `FedSrv.CPP`:
- **Leaders (1)** — `GetLeader(iSide)` (`:3474`) is a team-leader election
  concept this headless server doesn't implement; lobby-mediated in the real
  game.
- **Admin (2)** — literally marked `//NYI` (`:3485`) in the **shipped real
  server** — never implemented even in the actual game. A finding worth noting.
- **Ship (3)** — routes to a ship's parent/child ships
  (`GetSourceShip()`/`GetChildShips()`, `:3493-3501`); this codebase has no
  docking/carrier/parent-ship model.
- **Allies (4)** — needs an alliance system (`AlliedSides`, `:3443-3463`);
  not implemented anywhere here.
- **Wing (8)** — needs `pship->GetWingID()`
  (`ForwardChatMessageToWing`, `:3327-3353`); `Ship.cs` has no `WingId` field
  and no wing-assignment mechanism.
- **Individual_Echo (9)** — an outbound marker the server sets when echoing
  drone commands (`:3565`), never an inbound client target; no inbound case in
  the real dispatch.
- **NoSelection (14) / Max (15)** — sentinel values, not real targets.

Files touched: `src/Allegiance.Server/Server.cs`,
`src/Allegiance.Core/Sim/IgcEnums.cs` (added `Max = 15`),
`src/Allegiance.Bot/Bot.cs` (doc only),
`src/Allegiance.Server/Properties/AssemblyInfo.cs` (InternalsVisibleTo for
chatdemo), `tests/.../LiveChatSectorWireTests.cs` (new),
`tools/chatdemo/Program.cs`, `README.md`.
