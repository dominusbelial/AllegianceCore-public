# P438 — client drops JOIN_SIDE(125), so a side switch leaves friend/enemy colour stale

## Why this task exists

Open item carried from P436's live testing: **a friendly ship's
target-selection box renders "orange" instead of blue.** P436 ruled out
its own changes. Root cause traced this session by direct source read:

- The "orange" is P376's **enemy** colour. `TargetSelectionStyle.EnemySelected`
  is `(0.922, 0.484, 0.426)`, a salmon that reads as orange
  (`src/Allegiance.ClientCore.MonoGame/Rendering/TargetSelectionStyle.cs`).
  So the friendly target was being classified **as an enemy**, not
  mis-coloured.
- The classification is
  `isEnemy = !(_session.World.MySideId is { } mySide && targetShip.SideId == mySide)`
  (`src/Allegiance.ClientCore.MonoGame/Game1.Camera.cs:146`). It is wrong
  whenever `ShipEntity.SideId` or `ClientWorld.MySideId` is stale.
- **The client has only one-shot sources for side**:
  - `MySideId`: only from the own-ship dummy EXPORT (`ShipId == -1`),
    `src/Allegiance.ClientCore/State/ClientWorld.cs:1032-1047`.
  - A teammate's `ShipEntity.SideId`: only from the teammate EXPORT,
    which the server latches once per (peer, ship) through
    `_teammateExportSent` (`src/Allegiance.Server/Server.Broadcast.cs:522-539`).
    That EXPORT stamps `SideId = recipientSide` at the moment it is sent.
  - A cross-side ship: the P369 cross-side EXPORT plus its slow self-heal
    (`Server.Broadcast.cs:~950-960`).
- **The server does tell everyone when a side changes, and the client
  throws that message away.** `TryJoinSide` sets `ship.SideId = newSideId`
  (`src/Allegiance.Server/Server.Lobby.cs:1705`), then calls
  `BroadcastJoinSide`, which sends `JOIN_SIDE(125)` to the whole mission
  (`Server.Lobby.cs:574-579`). `Allegiance.ClientCore` has **zero**
  references to `JoinSide`. `ConnectionSession.cs` has no `case JoinSide.Fmid`
  (compare `case SetTeamLeader.Fmid` at `ConnectionSession.cs:823-828` →
  `ClientWorld.Apply(SetTeamLeader)` at `ClientWorld.cs:809,1797`). Only the
  test `Bot` (`src/Allegiance.Bot/Bot.cs:1847-1848`) decodes it.
- **Native handles it.** `clintlib/appmsg.cpp:2909-2915`, `case FM_S_JOIN_SIDE`
  → `AddPlayerToSide(pplayer, sideID)`, which does
  `pPlayerInfo->SetSideID(sideID); … pship->SetSide(pside);` and, when
  it is the local player, `OnQuitSide()`/`OnJoinSide()`
  (`clintlib/clintlib.cpp:4101-4125`). Native is at
  `~/Development/Allegiance/src/`.

The failure: any ship that changes side after a viewer has already
received its EXPORT keeps its old side on that viewer. The ship could be
a teammate, a cross-side ship, or the local player's own ship. Examples
include a lobby side switch, an MCP bot joined and then moved, or an
auto-assign followed by an explicit TryJoinSide. After that, friend/enemy
colouring is wrong on that viewer: the P376 bracket, P372 radar blips,
P373 first-sighting sound, and anything else keyed on SideId.

## Scope

1. **Handle `JOIN_SIDE(125)` in the client**, mirroring native
   `AddPlayerToSide`'s ship-side part:
   - Add `case JoinSide.Fmid:` to `ConnectionSession`'s dispatch, next to
     `SetTeamLeader`, using the same decode→`_world.Apply(...)` shape.
   - Add `ClientWorld.Apply(JoinSide)`. If `Ships` already holds an entity
     for `ShipId`, set its `SideId`. If `ShipId == MyShipId`, also update
     `MySideId`. That covers native's `m_pPlayerInfo` branch for side
     tracking only. Do not port `OnQuitSide`/`OnJoinSide` side effects in
     this task.
   - A `JOIN_SIDE` for a ship the client has no entity for yet must
     **not** create a phantom entity with no identity. The later EXPORT
     will carry the right side. Decide whether to buffer it or just ignore
     it, and document the choice. Mind the P373 `HasRealIdentity` flag:
     do not flip it here, because JOIN_SIDE carries no hull.
   - The lobby's `SIDE_TEAMLOBBY` (-2 in native, check the port's constant)
     can arrive as a SideId. Make sure it doesn't alias to a real side in
     `isEnemy`, and document what happens.
2. **Confirm with a real loopback reproduction** that this was the bug.
   Build a two-client in-process scenario (existing `Bot`/`ConnectionSession`
   loopback test patterns). Viewer A is on side 0. Ship B joins side 1,
   A receives B's EXPORT, then B switches to side 0 via the real
   `TryJoinSide` path. Assert that A's `ClientWorld.Ships[B].SideId` is
   now 0. Run it **against the unfixed code first** and record that it
   fails (still 1) in the report. If it doesn't fail, the root cause is
   something else: stop, write up what you found, and don't ship a
   speculative fix.
3. Also cover the local-player case with a test: after own-ship EXPORT,
   a JOIN_SIDE naming `MyShipId` updates both `MySideId` and the own
   `ShipEntity.SideId`.

## Explicitly out of scope

- Re-architecting the server's `_teammateExportSent`/`_crossSideExportSent`
  latches, or re-sending EXPORT on side change. The native client learns
  side changes from JOIN_SIDE, and the port should too.
- Native `OnQuitSide`/`OnJoinSide` UI side effects (money reset,
  requests list, `ResetShipStatus`). That's a separate follow-up if
  needed.
- `TargetSelectionStyle` colours, and the `MySideId == null ⇒ enemy`
  fallback in `Game1.Camera.cs:146`. Leave both as-is. Note in the report
  whether the fallback could also have produced the symptom.
- The other carried open items: vizfight's `[0,0,0]` position fallback
  (`SpectatorFrame.cs:190`) and the lack of mission rotation after `Over`.
  Those are separate later tasks.

## Verification

- The step-2 loopback test fails on unfixed code and passes after the
  fix, plus the step-3 own-ship test.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Bare `dotnet` on this box is a broken snap stub,
  so always use the full path.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive
  clean runs. Only the pre-existing documented live-wire timing flakes are
  acceptable. Name any flake you see.

## Deliverables

1. The fix plus tests, committed on **`master`** (not a side branch, not
   pushed; Domingo pushes). Run `git branch --show-current` before
   committing.
2. Report doc `docs/reports/P438_CLIENT_JOIN_SIDE_HANDLER.md`. Cover the
   pre-fix failing test evidence, the fix, the unknown-ship and
   `SIDE_TEAMLOBBY` handling decisions, and whether the `MySideId == null`
   fallback is a second contributing path.
