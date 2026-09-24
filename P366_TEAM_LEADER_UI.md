# P366 — Team leader-only UI: command bot teammates to dock/launch

## Summary

Added the missing CLIENT-side leader-only capability that was confirmed to have
zero presence in `Allegiance.ClientCore.MonoGame/`: when the local player is the
elected **team leader**, they can command an AI **bot teammate on their own side**
to **Dock** or **Launch**. A non-leader sees no such option at all (native's real
gate, not a disabled button). The leadership mechanic itself was already ported
server-side (P183 `SetTeamLeader.cs` / `Side.LeaderShipId` / whole-mission
`SET_TEAM_LEADER(121)`); this stage is a pure client UI addition that USES that
existing state — plus a small server addition to make same-side bots visible to
the leader's client at all (they were invisible before).

Deliverables on `master` (not pushed):
- Server: export same-side NPC (AI bot) identity to the leader's client.
- Client Core: decode `SET_TEAM_LEADER(121)` → `ClientWorld.IsTeamLeader`; a pure
  `LeaderBotCommandResolver` that gates the Dock/Launch command.
- Client MonoGame: a bottom-centre HUD prompt ("DOCK \<bot\> [X]" / "LAUNCH \<bot\>
  [X]") + an `X`-key that sends the real `ORDER_CHANGE(93)`.
- Tests: 11 pure ClientCore tests + 3 live Core.Tests (real leadership + real
  effective Dock/Launch over the wire) + 1 live ClientCore test (real server +
  real `ConnectionSession` feeding a real `ClientWorld`).

---

## 1. Scope item 1 — what the cross-side "Accept" action turned out to be

Investigated `WinTrek.cpp:4375-4379` (the second context menu, in
`ShowPlayerPaneContextMenu`, lines ~4340-4410). For a player on a DIFFERENT side,
gated on `trekClient.GetPlayerInfo()->IsTeamLeader()`:

```cpp
if (trekClient.MyMission()->SideAvailablePositions(trekClient.GetSideID()) > 0
    && trekClient.MyMission()->FindRequest(trekClient.GetSideID(), playerInfo->ShipID()))
```

`contextAcceptPlayer()` sends `POSITIONACK{ shipID, fAccepted=true, iSide }`;
`contextRejectPlayer()` sends `QUIT_SIDE(QSR_LeaderBooted)` if the target is
already on the leader's side, else `POSITIONACK{ fAccepted=false, iSide }`.

**Finding:** this is the leader approving/denying a player's **side-join
request** — a wholly different leader capability from the bot dock/launch. Its
semantics do NOT depend on anything this port lacks: the side-join-request
mechanic exists server-side (`_pendingJoinRequests`, `POSITIONACK` handler
`Server.Dispatch.cs:607-663`, `TryJoinSide` auto-accept). **Decision: explicitly
OUT OF SCOPE for P366.** It is a distinct feature (a "leader approves a join
request" UI, POSITIONACK-based) unrelated to commanding bot teammates; building
it half-right would be worse than excluding it. Documented here so it can be a
future task that reuses the already-present server mechanic.

---

## 2. Scope item 2 — how `IsTeamLeader` state reaches the client

Verified directly (the brief asked to confirm, not assume):

- `Allegiance.Core/Wire/SetTeamLeader.cs` exists (fmid **121**, **8 bytes**:
  header + `SideID` short@4 + `ShipID` short@6) and the server broadcasts it
  **whole-mission** on every leadership assignment/transfer
  (`Server.Lobby.cs:564-570 BroadcastSetTeamLeader`; callers at :910, :1585,
  :1693, :206). The client, however, never decoded it — zero refs to
  `IsTeamLeader`/`TeamLeader` in `Allegiance.ClientCore.MonoGame/` (confirmed).
- The leadership state was therefore **already flowing to the client on the wire
  but discarded**. P366 adds the decode (a pure client addition, as the brief
  predicted — no new server wire work needed).

Note: `PlayerInfo(10)` carries an `fTeamLeader` bit at offset 40, but the port's
`PlayerInfo` encode always sends 0 and the authoritative source of live leadership
is the `SET_TEAM_LEADER(121)` whole-mission broadcast (the server re-announces on
every change). So the client tracks leadership exclusively from `SET_TEAM_LEADER`.

**Late-joiner caveat (documented, not a defect):** `SET_TEAM_LEADER` is sent on
leadership *change*, not re-sent to every newcomer, so a peer who joins an
established side may not know the current leader from that message. This is
harmless for the feature: the gate only matters for the local player, who
*became* the leader through an event that broadcast to them, and a non-leader is
correctly "not leader" either way (absence → false).

---

## 3. What was built

### 3a. Server — make same-side bots visible to the leader's client (required)

The feature cannot work if the leader's client cannot even see its own side's
bots. Confirmed both existing visibility legs skip same-side NPCs:
- P62 same-side teammate EXPORT iterates real **peers** only
  (`Server.Broadcast.cs:427-535`).
- P346 cross-side EXPORT explicitly skips a ship's own side
  (`Server.Broadcast.cs:801 if (ownSide == sideId) continue;`).

So a same-side NPC bot had **no EXPORT → no `ShipEntity` with its real `SideId`**
on the leader's client. `Server.Broadcast.cs` `BroadcastShipStates` now, per
seated peer, exports each same-side NPC in the peer's mission (`SideId`,
`HullId`, `CharacterName`, parts) once per (recipient, NPC) pair, and relays the
NPC's real `SHIP_STATUS(90)` (periodic, `MakeNpcShipStatus`) so the client knows
its **docked state** (drives the Dock/Launch label). State latches mirrored from
the teammate sets, cleaned up on disconnect
(`Server.Lifecycle.cs`). Position still arrives via the unchanged
`HEAVY_SHIPS_UPDATE` machinery.

This is the one server change; everything else is client-only.

### 3b. Client Core

- `ClientWorld` decodes `SET_TEAM_LEADER(121)` into `_leaderShipIdBySideId`
  (per-side leader), exposes `TeamLeaderShipIdFor(side)` and **`IsTeamLeader`**
  (= `LeaderShipIdBySideId[MySideId] == MyShipId`), cleared on `Reset()`.
- `ConnectionSession` routes fmid 121 to `ClientWorld` and gains
  `SendOrderChange(shipId, commandId, command)` — wraps `FM_CS_ORDER_CHANGE(93)`
  exactly as native's `contextDockDrone` does (command=`c_cmdAccepted`,
  objectType=`OT_invalid`=-1, objectID=NA).
- `State/LeaderBotCommand.cs`: pure `LeaderBotCommand` + **`LeaderBotCommandResolver`**,
  the single decision function the UI and the X-key both use. Gates (all mirror
  native `WinTrek.cpp:4349-4368`):
  - `world.IsTeamLeader` — leader-only, else null (a non-leader sees nothing).
  - selected target is a Ship with a live pose, not mine/dead.
  - `ShipEntity.Player == null` — a real peer's `PLAYERINFO` has never attached,
    the same discriminator native's `GetPilotType() < c_ptPlayer` expresses
    (the server sends `PlayerInfo` only for real peers: `Server.Broadcast.cs:524`).
  - `ship.SideId == world.MySideId` — same-side teammate only.
  - Label = "DOCK" (flying) / "LAUNCH" (docked) from the bot's real `SHIP_STATUS`.

### 3c. Client MonoGame

- `Game1.ResolveLeaderBotCommand` → `LeaderBotCommandResolver`, resolved each
  frame in `DrawHud` under the gate lock.
- `HudRenderer` gains a `LeaderBotCommand?` param; draws a compact bottom-centre
  prompt in the HUD accent **only when** a command is offered (a non-leader →
  null → nothing drawn).
- Update: edge-triggered **X** key sends the real `ORDER_CHANGE` via
  `ConnectionSession.SendOrderChange(...CidGoto, CmdAccepted)` — the exact
  command native sends for both dock and launch (WinTrek.cpp:3771-3787); the
  server's already-ported arms (P148/P150/P152 `HandleOrderChange`) apply the
  right effect by the bot's docked state. Native's client-only gate is preserved
  (the server's `ORDER_CHANGE` handler has no leadership check — faithful to
  `FedSrv.CPP:7264+`).

---

## 4. Verification — real proof

### 4a. Pure tests — the UI gate (`LeaderBotCommandTests`, +11)
`IsTeamLeader` derivation (default false; true when our ship announced; false on
transfer-away / other-side leadership / after Reset) and every resolver gate:
leader sees "DOCK" (flying) / "LAUNCH" (docked) for a same-side bot; **non-leader
sees nothing** even with the same-side bot selected; real peer on own side, cross
-side bot, no selection, unknown id all → null.

### 4b. Live, real wire, real leadership, real same-side bot (`Core.Tests`,
`P366LeaderBotCommandLiveTests`, +3)
Real in-process server, **two real connected peers**, leadership established via
the real `TryJoinSide` first-on-empty-side mechanic, real `SpawnNpcShip` bot on
the leader's side:
- `Leader_is_side_leader_and_receives_set_team_leader_but_mate_is_not` —
  `Side.LeaderShipId == leader`, the leader's client observed the real
  `SET_TEAM_LEADER(121)`; the mate is not the leader.
- `Leader_commands_same_side_bot_to_return_to_base_effectively_live` — leader's
  real `ORDER_CHANGE` (Dock) on a flying same-side bot sets the bot's server-side
  `StayDocked=true` (**the bot visibly returns to base**).
- `Leader_commands_docked_same_side_bot_to_launch_effectively_live` — the
  same real command on a docked bot **clears `StayDocked` and undocks it**.

### 4c. Live, real client world (`ClientCore.Tests`,
`LeaderSameSideBotClientLiveTests`, +1)
Real server + **real `ConnectionSession`** (real `ClientWorld`) leader + a second
real non-leader session + a real same-side NPC:
- the leader's `ClientWorld` learns `IsTeamLeader` (from the real `SET_TEAM_LEADER`);
- the leader's `ClientWorld` is shown the same-side bot with its real `SideId` +
  the EXPORT-only identity (`HullId` + `CharacterName`) and no `PlayerInfo`, and
  `LeaderBotCommandResolver` returns the DOCK command for it (the UI "appears");
- the non-leader `ClientWorld` is not leader and the resolver returns null.

### 4d. Must-fail if removed (hard requirement met)
- Disabling the same-side NPC export (commenting the `SendPeer`) makes 4c's
  "leader's ClientWorld must be shown the same-side bot" assertion fail (was
  caught passing spuriously via the default `SideId==0`, then hardened with the
  EXPORT-only `HullId`/`CharacterName` discriminator — genuinely red when the
  export is off).
- Removing the `SET_TEAM_LEADER` decode makes 4 of the 11 pure tests fail.

### 4e. Build / suite
- Whole `AllegianceCore.slnx` build: **0 warnings, 0 errors**.
- `ClientCore.Tests`: **187 passed, 0 failed** (×2 runs).
- `Allegiance.ClientCore.MonoGame.Tests`: **698 passed, 0 failed**.
- `Core.Tests` full suite (3 runs): 3290–3294 passed; each run interrupted only by
  a varying handful of documented/intermittent live-wire flakes
  (LiveChatSectorWireTests, LiveTurretGunnerBindingTests persistent, LiveWinConditionTests,
  ProjectileBroadcastTests — the last CONFIRMED pre-existing by failing on the
  pristine tree with my changes stashed; LiveTreasurePickupTests + CargoBayReloadTests
  both green in isolation → environmental full-suite amplification). None reference
  P366 symbols; all P366 live tests pass deterministically (Core 3/3, ClientCore 12/12).
- No changes to `src/Allegiance.Bot/Bot.cs` (the pre-existing dirty warp-export
  edits are unrelated and untouched).

---

## 5. Out of scope (stated plainly)

- `TeamDirector` AI framework (`Wopr/TeamDirector.cs`) — not a player UI.
- Rebuilding the leadership election/voting — already correct server-side.
- The cross-side "Accept"/"Reject" join-request approval — a separate leader
  feature; excluded with evidence in §1 (the server mechanic exists; the UI is a
  distinct future task).
- Any alliance/diplomacy system.
- P358–P365, P354–P357 — untouched.
