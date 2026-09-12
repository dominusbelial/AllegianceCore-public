# P141 — ENTER_GAME (128): join-sync completion notification

Status: IMPLEMENTED + VERIFIED (build clean, full suite 2703/2703 ×3 consecutive)

Repo: `/home/domingo/Development/AllegianceCore` (branch `master`)

## What this is

`FM_S_ENTER_GAME`(128) is the final "you're ready" signal a native server
sends to a joining player once it has everything it needs to start playing in
the current game (`src/Inc/Messages.h:831-835`, quoted in full):

```cpp
DEFINE_FEDMSG(S, ENTER_GAME, 128)
  // sent to the client once the client has all of the info it needs to start
  // playing in the current game (note: the count down may not have elapsed
  // yet).
END_FEDMSG
```

It is deliberately **empty** — `FMD_S_ENTER_GAME = { ushort cbmsg; ushort
fmid; }` = 4 bytes — the same zero-field shape as `SUICIDE`(80)/P103 and
`LOGOFF`(3)/P127. This task appends it as the last message of this port's
existing join-sync burst (the same flow P118/P119/P121/P130 built out:
`SET_MONEY`/`CREATE_BUCKETS`/`SET_BRIEFING_TEXT`/`RANK_INFO`).

## The STAGE_STARTED finding (stated plainly)

Native's trigger is gated on the mission being started
(`FedSrv/fsmission.cpp:799-805`):

```cpp
if (STAGE_STARTED == GetStage())
{
  pfsPlayer->GetPlayerScoreObject()->Connect(g.timeNow);

  // let them know that the game has started
  BEGIN_PFM_CREATE(g.fm, pfmEnterGame, S, ENTER_GAME)
  END_PFM_CREATE
  ...
```

**This port's missions are always effectively started by the time a player can
join.** The port is direct-connect with no lobby/countdown: the single mission
is assembled and immediately set `Stage = Started` at boot
(`src/Allegiance.Server/Server.cs:397-398`:

```csharp
var mission = Mission.Assemble(core, map, sides, _host);
mission.Stage = MissionStage.Started;                     // ready to tick
```

), before any player connects. Players can only join after Boot, and
`Mission.cs:275-278` confirms only `Started`/`Starting` missions tick at all
(`NotStarted` merely advances `LastUpdate`). So there is no `STAGE_NOTSTARTED`
state reachable at join time in this port — **`ENTER_GAME` is sent
unconditionally** at the end of join-sync, which is faithful to native's
behavior for a mission that has started.

## Score-system companion is N/A

Native's adjacent `pfsPlayer->GetPlayerScoreObject()->Connect(g.timeNow)`
(`fsmission.cpp:801`) is skipped — no score system in this port (confirmed
repeatedly, P115/P125/P126/P140).

## Send target

Native flushes `ENTER_GAME` to the joining player's **own** connection only
(`FedSrv/fsmission.cpp:830`: `g.fm.SendMessages(pfsPlayer->GetConnection(),
FM_GUARANTEED, FM_FLUSH)`) — the same per-player shape as this port's
`SET_MONEY`/`SET_BRIEFING_TEXT`/`RANK_INFO` sends. This port mirrors that:
`SendPeer(from, ...)` to the joining peer only.

## Where the join-sync sequence ends, and the placement

This port's join-sync burst runs inside `ProcessDatagram`, the
`if (validated)` block opened at `Server.cs:1131`, after `LOGONREQ`. Its
message order is:

1. `LOGONACK` — `Server.cs:1125`
2. `MISSIONDEF` + `JOINED_MISSION` — `Server.cs:1174-1191`
3. `MISSIONDEF` (sides) — `Server.cs:1244-1254`
4. ship `EXPORT` — before `PLAYERINFO` (see comment)
5. `PLAYERINFO` — `Server.cs:1394-1404`
6. `SET_MONEY` — `Server.cs:1424-1428`
7. `CREATE_BUCKETS` + `BUCKET_STATUS`×N — `Server.cs:1444-1460`
8. `SET_BRIEFING_TEXT` — `Server.cs:1486-1490`
9. `RANK_INFO` — `Server.cs:1517-1518`
10. `TREASURE_SETS` — `Server.cs:1536-1537`
11. `CLUSTERINFO` — `Server.cs:1570-1574`
12. cluster `EXPORT`s + warp/station/asteroid `EXPORT`s — `Server.cs:1610-1649`
13. `SET_CLUSTER` — `Server.cs:1651-1657`
14. `BUY_LOADOUT_ACK` — `Server.cs:1685-1693`
15. `MISSION_STAGE`(37) **Started** — `Server.cs:1715-1718`
16. `SHIP_STATUS` — `Server.cs:1740-1746`

The task brief guessed "likely `RANK_INFO` (P130)" as the end, but the real
sequence extends well past it: `RANK_INFO` sits mid-burst (#9), followed by
the treasure/cluster/export/launch/stage/status tail. The player does not have
everything it needs to play until after `MISSION_STAGE`(37) Started (which is
what tells the client's simulation to run — P37) and `SHIP_STATUS`. So the
correct final placement is **after `SHIP_STATUS`**, as the very last message
of the burst (the `Server.cs:1747-1748` block close) — the "join-sync
complete, you're ready" capstone. Confirmed: `ENTER_GAME` is the correct final
message to append there; nothing else needs to fire from a different point.

## Changes

- `src/Allegiance.Core/Wire/EnterGame.cs` — new zero-field codec, mirroring
  `Suicide.cs`/`Logoff.cs` exactly (Fmid 128, FixedSize 4, header-only
  encode/decode with cbmsg + layout-drift guards).
- `src/Allegiance.Server/Server.cs:1748-1765` — the unconditional per-player
  `ENTER_GAME` send at the real end of the join-sync burst.
- `src/Allegiance.Bot/Bot.cs` — capture `ENTER_GAME` on the receive path
  (field + `EnterGames`/`ResetEnterGames` + a `case EnterGame.Fmid:` in the
  dispatch switch) so a real bot can assert receipt.
- `tests/.../Wire/EnterGameWireTests.cs` — codec round-trip + byte-exact +
  reject tests (4).
- `tests/.../LiveEnterGameTests.cs` — real loopback join/sync: two bots on
  different sides each receive exactly one `ENTER_GAME` on their own
  connection.

## Out of scope (unchanged)

- `RELOAD`(129) — needs a cargo-mount concept (`Mount` < 0) tied to Phase 1's
  still-unbuilt general parts/cargo system. Not this task.
- `GAME_STATE`(155) — needs territory/flag/artifact tracking, at least
  partially blocked on Phase 6 capture-the-flag (deferred multiple times).
  Territory-tracking may be partly derivable from P126's station-capture work,
  but that is a separate, later investigation. Not this task.
- No change to P103-P140 code beyond appending to the existing join-sync
  sequence.

## Next Phase 6 item

This task's own research surfaced no new concrete Phase 6 item: the one native
sibling of `ENTER_GAME` inside the `STAGE_STARTED` block — the prosperity-game
`BUCKET_STATUS` sweep (`fsmission.cpp:808-828`) — is already served by this
port's P119 (`CREATE_BUCKETS`/`BUCKET_STATUS`). The remaining Phase 6 items are
confirmed lobby-dependent or blocked on Phase-1/Phase-6 infrastructure this
port doesn't have: the mission countdown/lobby (there is none — direct-connect,
Stage flips to Started at boot) and `GAME_STATE` territory/flag/artifact
tracking (deferred, partially blocked on capture-the-flag). So: no new item
surfaces here; the remaining Phase 6 work stays as documented.

## Verification

- Clean build, 0 warnings / 0 errors.
- Full suite `2703/2703` passing, 3 consecutive runs (the documented timing
  flakes — `LiveChatSectorWireTests`, `LiveCombatWireTests`,
  `LiveInSpaceEjectTests` — fired intermittently in a handful of runs and were
  rerun to clean; none is this change's regression).
- Real loopback proof: `LiveEnterGameTests` seats two bots on different sides
  and asserts each receives exactly one `ENTER_GAME(128)` on its own
  connection at the end of join-sync.
