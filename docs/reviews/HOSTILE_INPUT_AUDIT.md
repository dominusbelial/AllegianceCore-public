# Hostile-input audit — client → server surface (review round 3)

- **Base:** `codex/analysis` @ `4c4f47cb` (P646). This audit does not cover the P646b drain carry-over.
- **Brief:** `AUDIT_HOSTILE_INPUT.md`.
- **Date:** 2026-10-14.
- **Scope:** everything a remote client can make the server parse or act on:
  - the TCP framing and greeting (`TcpTransport.cs`);
  - packet unpack and the message cap (`FedPacket.cs`);
  - all 43 message types that `Server.ProcessDatagram` dispatches, plus their decoders;
  - every `Server.*.cs` handler they reach, and the Core/Sim code those handlers call;
  - the HTTP lobby (`src/Allegiance.Lobby`).
- **Already fixed, not re-reported:** partial writes, the 32 MiB hard cap, PING throttling, one LOGONREQ per seated peer, the bounded fair drain, the accounting leak. Finding H-14 is a new bypass of the P646 logon rule, not a repeat of it.

## How this was done

1. **Inspection.** Each wire value was traced from decode to use. Three parallel read-only passes covered:
   - economy and parts;
   - ship commands and floats;
   - chat, lobby and HTTP.

   I then re-checked the cited lines.
2. **Decoder fuzzing (socket-free).** `/home/domingo/.claude/jobs/82c1509b/tmp/input-audit/DecoderFuzz` feeds every one of the 43 client→server decoders 20 k–34 k inputs, **886,919 in total**:
   - empty input, 1 byte, header-only;
   - every length from 0 to fixed+64, filled with 0x00, 0xFF and random bytes;
   - every truncation of a valid encoding, with stale and with corrected cbmsg;
   - +100 trailing bytes;
   - 65,535-byte buffers;
   - byte and ushort boundary mutations (0x0000 / 0x7FFF / 0x8000 / 0xFFFF) at every offset;
   - random structured fuzz.

   For each it records the exception type, allocation and time.
3. **In-process server probes (loopback only).** `/home/domingo/.claude/jobs/82c1509b/tmp/input-audit/ServerProbe`:
   - boots a real `Allegiance.Server.Server` on the XXV_simple map with real artwork;
   - seats real `Allegiance.Bot` clients over 127.0.0.1;
   - injects crafted packets through `Server.ProcessDatagram`, attributed to a seated peer's own `ConnectionId`;
   - counts every outbound byte through the existing `_testSendCapture` seam.

   The transport probes open loopback sockets against an in-process `TcpTransport`. Rerun with `dotnet run --no-build -- <probe>`; the probes are relay, teleport, slowloris, buoy, money, loadout, lobby, ships, board, amplify and defect. A finding marked **[probe]** was reproduced this way; **[inspection]** means it was found by reading the code.

No source file, test, config or commit was changed. The only file written in the worktree is this report.

---

## 1. Coverage table — every client→server message type

Status key:
- **OK**: checked, no finding.
- **H-nn**: see the finding below.
- **ignored**: falls into the rate-limited "unsupported fmid" branch (`Server.Dispatch.cs:1950-1959`).

### 1a. Messages the server dispatches (43 types)

| fmid | Message | Decoder (`src/Allegiance.Core/Wire/`) | Handler | Status |
|---:|---|---|---|---|
| 1 | LOGONREQ | `LogonRequest.cs:116` | `Server.Dispatch.cs:76-176`; `AllocateShipId` `Server.Docking.cs:1706`; `SendSideRoster` `Server.Lobby.cs:3652`; seat `CompleteSideSelection` `Server.Lobby.cs:2805` | H-02, H-14, H-15, H-21, H-28 |
| 3 | LOGOFF | `Logoff.cs:34` | `Server.Dispatch.cs:1528` → `ReleasePeer` `Server.Lifecycle.cs` | H-14 |
| 4 | BUY_LOADOUT | `BuyLoadoutRequest.cs:82` | `ApplyLoadout` `Server.ShipCommands.cs:2496` | H-06, H-07, H-08, H-09, H-20, H-16 |
| 19 | VOTE | `Vote.cs:47` | `Server.Dispatch.cs:402` | OK (ballot looked up in sender's own mission, eligibility enforced); rate limit under H-05 |
| 21 | RANDOMIZE_TEAMS | `RandomizeTeams.cs:41` | `Server.Dispatch.cs:989` → `RebalanceSides` `Server.Lobby.cs:2048` | OK (owner + NotStarted) |
| 23 | CHATMESSAGE | `ChatMessage.cs:105` + `ChatData.cs:61` | `Server.Dispatch.cs:346`; `TryHandleAdminCheat` `Server.AdminCheats.cs:131`; `HandleTimescaleCheat` `Server.Timescale.cs:292`; `TryHandleBallotCommand` `Server.Lobby.cs:1435`; `SelectChatTargets`; `QueueOrdersFromRelayedChat` `Server.ShipCommands.cs:1519`; `TryCommandToDrone` `:1603` | H-05, H-10, H-16, H-17, H-19 |
| 24 | SHIP_UPDATE | `ShipUpdate.cs:50` + `ClientShipUpdate.cs` | `Server.Dispatch.cs:183-300` → `ProcessPeerShipUpdate` `:1975-2050` | H-11, H-12 (all floats checked OK, see §4) |
| 33 | DROP_PART | `DropPart.cs:79` | `Server.ShipCommands.cs:570` | H-08, H-22 |
| 34 | SWAP_PART | `SwapPart.cs:94` | `Server.ShipCommands.cs:357` → `Ship.TrySwapPartWithCargo` `Ship.cs:2758` | H-09, H-22 |
| 35 | PING | `Ping.cs:66` | `Server.Dispatch.cs:38` | OK (P645 throttle; re-encoded); dead duplicate branch H-37 |
| 39 | POSITIONREQ | `PositionReq.cs:48` | `Server.Dispatch.cs:661` → `TryJoinSide` `Server.Lobby.cs:2441` / pending `:2805`, `:3684` | H-13, H-15 |
| 41 | POSITIONACK | `PositionAck.cs:62` | `Server.Dispatch.cs:761` | OK (sender must lead the named side; target must still exist) |
| 42 | DELPOSITIONREQ | `DelPositionReq.cs:58` | `Server.Dispatch.cs:834` | H-05 (verbatim relay); H-36 |
| 43 | LOCK_LOBBY | `LockLobby.cs:46` | `Server.Dispatch.cs:1115` | OK (owner-only); H-05 (verbatim relay) |
| 44 | LOCK_SIDES | `LockSides.cs:48` | `Server.Dispatch.cs:1157` | OK (owner-only); H-05 (verbatim relay) |
| 45 | PLAYER_READY | `PlayerReady.cs:57` | `Server.Dispatch.cs:1457` | H-19, H-05 |
| 47 | FORCE_TEAM_READY | `ForceTeamReady.cs:57` | `Server.Dispatch.cs:891` | OK (own side + leader); H-05 (verbatim relay) |
| 48 | DOCKED | `Docked.cs:53` | `PerformUndock` `Server.Docking.cs:1924` / `PerformTeleport` `:1997` | H-03 (teleport refund), H-27 |
| 51 | CHANGE_TEAM_CIV | `ChangeTeamCiv.cs:63` | `Server.Dispatch.cs:1349` | OK (leader + NotStarted + civ exists); H-05 (verbatim relay) |
| 52 | AUTO_ACCEPT | `AutoAccept.cs:62` | `Server.Dispatch.cs:1292` → `SweepAutoAcceptPending` `Server.Lobby.cs:2296` | OK (own side + leader); H-05 (verbatim relay) |
| 64 | BUCKET_DONATE | `BucketDonate.cs:75` | `HandleBucketDonate` `Server.Economy.cs:510` | OK on money (negative refused, wallet gate, CanBuy, capped); H-24, H-33 |
| 67 | PLAYER_DONATE | `PlayerDonate.cs:73` | `HandlePlayerDonate` `Server.Economy.cs:300` | OK (amount > 0, wallet gate, same or allied side); recipient `+=` overflows only after H-01; H-24 |
| 68 | SIDE_INACTIVE | `SideInactive.cs:83` | `Server.Dispatch.cs:1195` | OK (owner + NotStarted; re-encoded before relay) |
| 70 | FIRE_MISSILE | `FireMissile.cs:128` | `HandleFireMissile` `Server.ShipCommands.cs:868` | H-12, H-25, H-35 |
| 80 | SUICIDE | `Suicide.cs:32` | `HandleSuicide` `Server.Lobby.cs:985` | H-26 |
| 82 | FIRE_EXPENDABLE | `FireExpendableRequest.cs:58` | `HandleFireExpendable` `Server.ShipCommands.cs:680` | H-12 (otherwise ammo-gated, OK) |
| 93 | ORDER_CHANGE | `OrderChange.cs:220` | `HandleOrderChange` `Server.ShipCommands.cs:1069`; `HandlePlayerBuildOrder` `:2147`; `HandleDirectNpcBuildOrder` `:2171` | H-18, H-25, H-38 |
| 99 | ACTIVE_TURRET_UPDATE | `ActiveTurretUpdate.cs:81` | `HandleActiveTurretUpdate` `Server.ShipCommands.cs:202` | H-12, H-31 |
| 100 | INACTIVE_TURRET_UPDATE | `InactiveTurretUpdate.cs:33` | `Server.Dispatch.cs:311-345` | OK (gunner-only, sets StateM=0) |
| 103 | BOARD_SHIP | `BoardShip.cs:48` | `HandleBoardShip` `Server.ShipCommands.cs:141` → `PerformBoard` `Server.Docking.cs:74` | H-03 |
| 104 | VIEW_CLUSTER | `ViewClusterRequest.cs:60` | `HandleViewCluster` `Server.ShipCommands.cs:68`; admin path `RecordSpectateSector` `Server.Spectate.cs:61` | H-16, H-33 |
| 106 | SET_WINGID | `SetWingId.cs:94` | `HandleSetWingId` `Server.ShipCommands.cs:902` | H-24 (self-only and range checks OK) |
| 108 | CHATBUOY | `ChatBuoy.cs:93` + `DataBuoyIGC.cs:54` | `Server.Dispatch.cs:444` | H-10, H-05, H-29 |
| 112 | AUTODONATE | `AutoDonate.cs:70` | `HandleAutoDonate` `Server.Economy.cs:128` | **H-01** |
| 129 | RELOAD | `Reload.cs:121` | `HandleReload` `Server.ShipCommands.cs:443` → `Ship.TryApplyReloadEntry` `Ship.cs:2809` | H-08, H-22, H-35 |
| 157 | START_GAME | `StartGame.cs:40` | `Server.Dispatch.cs:938` | OK (owner + NotStarted + all sides ready) |
| 163 | TREASURE_ACK | `TreasureAck.cs:71` | `HandleTreasureAck` `Server.ShipCommands.cs:2411` | H-08, H-30 |
| 182 | REQUEST_MONEY | `RequestMoney.cs:83` | `HandleRequestMoney` `Server.Economy.cs:228` | OK (amount > 0, forward only); H-24 (no chat budget) |
| 184 | RIPCORD_REQUEST | `RipcordRequest.cs:47` | `HandleRipcordRequest` `Server.Docking.cs:303` | H-24 (gates OK) |
| 190 | PROMOTE (request) | `Promote.cs:108` (`PromoteRequest`) | `HandlePromote` `Server.ShipCommands.cs:283` | OK (own boarded children only); H-33 |
| 195 | REQPINGDATA | `ReqPingData.cs:42` | `HandleReqPingData` `Server.ShipCommands.cs:989` | H-32 |
| 196 | CHANGE_ALLIANCE | `ChangeAlliance.cs:57` | `Server.Dispatch.cs:1409` → `UpdateAlliances` `Server.Lobby.cs:2150` | OK (owner + NotStarted + ids range-checked) |
| 199 | HIGHLIGHT_CLUSTER | `HighlightCluster.cs:63` | `Server.Dispatch.cs:523` | H-05 |

### 1b. Native client-sendable messages the port does not dispatch: ignored

These are the native `DEFINE_FEDMSG(C|CS, …)` entries in `Inc/Messages.h` with no server branch: PROJECTILE_INSTANCE 12, SET_TEAM_INFO 15, NEW_SHIP_INSTANCE 31, MISSIONPARAMS 113, MUTE 117, SET_TEAM_LEADER 121, SET_TEAM_INVESTOR 122, SET_MISSION_OWNER 123, QUIT_MISSION 124, QUIT_SIDE 126, BANDWIDTH 193.

- A client can send them, but they reach only the rate-limited `unsupported fmid` log (`Server.Dispatch.cs:1950-1959`). Nothing decodes them.
- Port codecs exist for 12, 31, 121, 123, 124 and 126, but only as server→client encoders.

### 1c. Port codecs that are server→client only: not reachable from a client

None of these Wire codecs are dispatched by the server. A client that sends one of these fmids gets the same ignore branch as 1b.

LogonAck(2), BuyLoadoutAck(5), ObjectSpotted(6), SetCluster(7), DevCompleted(8), SetBriefingText(9), PlayerInfo(10), ProjectileInstance(12), RankInfo(13), SetStartTime(14), PlayerRescued(16), Ballot(17), CancelBallot(18), ShipDelete(20), Export{Asteroid, BuildingEffect, Cluster, Mine, Probe, Ship, Station, Warp}(26), TreasureSpawn(28), DestroyTreasure(30), NewShipInstance(31), MissionStage(37), MissionDef(38), PositionReqBroadcast(40), TeamReady(46), StationsUpdate(57), StationCapture(58), StationDestroyed(59), MoneyChange(60), DockedNotify(61), GameOver(62), GameOverPlayers(63), BucketStatus(65), SideTechChange(75), SideAttributeChange(76), Eject(77), SingleShipUpdate(78), MissileDestroyed(83), MineDestroyed(84), ProbeDestroyed(85), AsteroidDestroyed(86), FireExpendable(87), TreasureSets(89), ShipStatus(90), ProbesUpdate(92), LeaveShip(94), JoinedMission(95), LoadoutChange(96), TeleportAck(102), KillShip(105), CreateChaff(109), MissileSpoofed(110), EndSpoofing(111), Payday(114), SetMoney(115), AutoDonateUpdate(116), SetTeamLeader(121), SetMissionOwner(123), QuitMission(124), JoinSide(125), QuitSide(126), EnterGame(128), GameState(155), GainFlag(156), AcquireTreasure(162), AddPart(164), EnterLifepod(166), LightShipsUpdate(175), HeavyShipsUpdate(176), ViewClusterReply(177), BoardNack(178), AsteroidsUpdate(179), BuildingEffectDestroyed(181), ShipReset(183), RipcordActivate(185), RipcordDenied(186), RipcordAborted(187), WarpBomb(188), Promote(189), CreateBuckets(191), RelaunchShip(192), PingData(194), ChangeAlliances(197), AsteroidMined(198), ClusterInfo(200), MissileLockUpdate(204), MissileUpdate(205), OwnShipMiningUpdate(206), ExplosionEvent(207), MissileTrackWarningUpdate(208), ScoreRosterUpdate(209), LeadIndicatorUpdate(210), SideRoster(211), OwnShipKillBonusUpdate(212), ProjectileHullHit(213), ClusterWarningUpdate(214), QueuedCommandUpdate(215), CollisionEvent(216), VictimDamaged(217), MineStrength(853), TimescaleUpdate(991).

Helper files with no fmid are reached only through the codecs above: ChatData, ClientShipUpdate, ExpandedPartData, MissileLaunchData, DataBuoyIGC, PartData, PassengerData, ZtsBlob, ServerShipUpdate, Compact/Quant, and others.

### 1d. Layers below and beside the dispatch

| Surface | Code | Status |
|---|---|---|
| TCP accept / greeting | `TcpTransport.cs:455-560` | H-02, H-15 |
| Data framing (type byte + u32 length) | `TcpTransport.cs:733-776` | **H-02**, H-21 |
| Per-peer inbound byte cap (4 MiB) | `ServerHost.cs:1155-1179` | OK (frames over 4 MiB are dropped whole); H-21 |
| Packet CRC + message walk + 256-message cap | `FedPacket.cs:101-138` | OK: CRC is checked before any walk; `cbmsg==0` and overrun are rejected; the cap works; `cbmsg` 1-3 gives a short message that `FedMessageHeader.Read` rejects inside the per-message try. Cost: H-21 |
| Per-message exception barrier | every dispatch branch has its own try/catch | OK. Defence in depth: H-34 |
| Lobby HTTP (`POST /api/servers/register`, `DELETE /api/servers/{id}`, `GET /api/servers[/{id}]`, `/healthz`) | `src/Allegiance.Lobby/LobbyEndpoints.cs:18-48`, `ServerRegistry.cs`, `RateLimiting.cs` | H-23. XSS checked OK: `textContent` only. Tokens are 128-bit GUIDs |
| Game-server → lobby heartbeat | `src/Allegiance.Server/LobbyHeartbeat.cs` | OK (outbound only, 5 s timeout) |

---

## 2. Decoder fuzz results (socket-free, all 43 decoders)

- **Inputs:** 886,919 in total.
- **Crashes:** none. Every rejection is `ArgumentException` or `ArgumentOutOfRangeException`, and the per-message `catch (Exception)` in every dispatch branch catches both.
- **No** `IndexOutOfRangeException`, `OverflowException`, `InvalidCastException`, `NullReferenceException`, stack overflow or hang.
- **Worst single decode:** under 1.7 ms.
- **Worst allocation:** 238 KB, for a 61 KB LOGONREQ (Latin-1 strings are 2 bytes per char).

| Decoder | Valid len | Inputs | Accepted | Max alloc | Max ms | Notes |
|---|---:|---:|---:|---:|---:|---|
| Ping (35) | 16 | 20,363 | 13,397 | 1 KB | 0.21 | |
| LogonRequest (1) | 1138 | 33,827 | 8,966 | 238 KB | 1.68 | strings up to ~64 KB accepted (H-28) |
| ShipUpdate (24) | 48 | 20,747 | 13,356 | 0.9 KB | 0.10 | |
| ActiveTurretUpdate (99) | 24 | 20,459 | 13,527 | 0.9 KB | 0.15 | |
| InactiveTurretUpdate (100) | 4 | 20,222 | 15,884 | 0.9 KB | 0.17 | |
| ChatMessage (23) | 34 | 20,579 | 4,190 | 116 KB | 0.91 | ib/cb out of range → AOORE |
| Vote (19) | 8 | 20,267 | 13,468 | 0.8 KB | 0.13 | |
| ChatBuoy (108) | 47 | 20,735 | 4,449 | 126 KB | 0.61 | ib/cb out of range → AOORE |
| HighlightCluster (199) | 7 | 20,255 | 14,140 | 0.9 KB | 0.16 | |
| Docked (48) | 6 | 20,243 | 14,849 | 0.8 KB | 0.02 | |
| BuyLoadoutRequest (4) | 11 | 20,303 | 3,019 | 59 KB | 0.69 | no part-count cap (H-20) |
| PositionReq (39) | 6 | 20,243 | 14,656 | 0.9 KB | 0.14 | |
| PositionAck (41) | 10 | 20,291 | 13,493 | 0.9 KB | 0.02 | |
| DelPositionReq (42) | 12 | 20,315 | 13,531 | 0.9 KB | 0.17 | |
| ForceTeamReady (47) | 8 | 20,267 | 13,531 | 0.9 KB | 0.11 | |
| StartGame (157) | 4 | 20,222 | 15,991 | 0.9 KB | 0.01 | |
| RandomizeTeams (21) | 4 | 20,222 | 15,939 | 0.9 KB | 0.19 | |
| AutoDonate (112) | 12 | 20,315 | 13,439 | 0.9 KB | 0.17 | |
| RequestMoney (182) | 12 | 20,315 | 13,516 | 0.9 KB | 0.24 | |
| PlayerDonate (67) | 12 | 20,315 | 13,559 | 0.9 KB | 0.15 | |
| LockLobby (43) | 6 | 20,243 | 14,754 | 0.9 KB | 0.25 | |
| LockSides (44) | 6 | 20,243 | 14,695 | 0.9 KB | 0.21 | |
| SideInactive (68) | 10 | 20,291 | 13,408 | 0.9 KB | 0.18 | |
| AutoAccept (52) | 8 | 20,267 | 13,459 | 0.9 KB | 0.02 | |
| ChangeTeamCiv (51) | 10 | 20,291 | 13,477 | 0.9 KB | 0.19 | |
| ChangeAlliance (196) | 8 | 20,267 | 13,460 | 0.9 KB | 0.14 | |
| PlayerReady (45) | 8 | 20,267 | 13,495 | 0.9 KB | 0.15 | |
| Suicide (80) | 4 | 20,222 | 15,990 | 0.8 KB | 0.03 | |
| Logoff (3) | 4 | 20,222 | 15,964 | 0.8 KB | 0.02 | |
| ViewClusterRequest (104) | 10 | 20,291 | 13,498 | 0.9 KB | 0.22 | |
| BoardShip (103) | 6 | 20,243 | 14,809 | 0.9 KB | 0.25 | |
| PromoteRequest (190) | 6 | 20,243 | 14,689 | 0.9 KB | 0.23 | |
| SwapPart (34) | 12 | 20,315 | 13,484 | 0.9 KB | 0.16 | |
| Reload (129) | 10 | 20,291 | 3,377 | 66 KB | 0.58 | capacity pre-sized from cb (H-35) |
| DropPart (33) | 10 | 20,291 | 13,418 | 0.9 KB | 0.01 | |
| SetWingId (106) | 10 | 20,291 | 13,451 | 0.9 KB | 0.17 | |
| TreasureAck (163) | 6 | 20,243 | 14,758 | 0.9 KB | 0.21 | |
| RipcordRequest (184) | 6 | 20,243 | 14,771 | 0.9 KB | 0.01 | |
| BucketDonate (64) | 12 | 20,315 | 13,476 | 0.9 KB | 0.01 | |
| OrderChange (93) | 12 | 20,315 | 13,425 | 0.9 KB | 0.02 | |
| FireExpendableRequest (82) | 6 | 20,243 | 14,815 | 0.9 KB | 0.05 | |
| FireMissile (70) | 32 | 20,555 | 3,983 | 67 KB | 0.26 | capacity pre-sized from cb; ignores `ib` (H-35) |
| ReqPingData (195) | 4 | 20,222 | 15,914 | 0.9 KB | 0.01 | |

**The decoders' one systemic weakness: they accept oversized messages.**
- Every fixed-size decoder checks `buf.Length >= FixedSize && CbMsg == buf.Length`, never `== FixedSize`.
- So a 7-byte HIGHLIGHT_CLUSTER can arrive as 65,535 bytes and still decode.
- That is harmless to the decoder itself, but **several handlers relay the original bytes**, which is H-05.

The float paths are covered separately in §4.

---

## 3. Findings, ranked

The severity scale assumes an internet-exposed server with untrusted players:

| Severity | Meaning |
|---|---|
| **Critical** | Pre-auth or one-player server kill, or unbounded economy minting |
| **High** | Server-wide denial (other players disconnected, sim starved), or game-breaking cheat for one player |
| **Medium** | A cheat or a resource cost with clear bounds, or one that depends on config |
| **Low** / **Info** | Hardening, latent problems, functional bugs |

Classification follows the brief:
- **bug**: the port is wrong, including native checks it dropped;
- **era constraint**: native trusted its client too, and a modern check is needed;
- **design**: a deliberate port simplification that is exploitable.

### Ranked list

| # | Sev | Title | Where | Repro |
|---|---|---|---|---|
| H-01 | Critical | AUTODONATE with a negative amount mints and steals money (int.MaxValue) | `Server.Economy.cs:141` / `:74` | probe |
| H-02 | Critical | Pre-auth memory exhaustion: a 16 MiB body is allocated per frame header, with no data-phase timeout and no connection cap | `TcpTransport.cs:773`, `:524` | probe |
| H-03 | Critical | Repeated BOARD_SHIP refunds the hull price each time (unlimited side money); spawn-hull refunds | `Server.Docking.cs:112-118` | probe |
| H-04 | High | (merged into H-05; number kept so references stay stable) | — | — |
| H-05 | High | Verbatim relay of oversized or unthrottled messages: N-fold amplification disconnects other players | `Server.Dispatch.cs:381,513,560,878,927,1147,1185,1334,1397,1484` | probe |
| H-06 | High | BUY_LOADOUT has no docked / alive / unboarded gate: buy in flight, free refill, pod un-death, no tech gate | `Server.ShipCommands.cs:2496-2535,2742,2776` | probe |
| H-07 | High | BUY_LOADOUT has no per-mount hardpoint check: 255 guns on a 1-hardpoint hull | `Server.ShipCommands.cs:2680`, `Weapon.cs:404` | probe |
| H-08 | High | Part `Amount` and `Ammo` are never clamped: 32,767 missiles, chaff and ammo; giant treasures | `Server.ShipCommands.cs:2680`, `Ship.cs:2570` | probe |
| H-09 | High | BUY_LOADOUT / SWAP_PART emit O(parts²) LOADOUT_CHANGE traffic (262× amplification) | `Server.Combat.cs:507-555`, `Ship.cs:2611` | probe |
| H-10 | High | Chat sender spoofing: CHATMESSAGE / CHATBUOY relay the client's `sidSender` | `Server.Dispatch.cs:381,513` | probe |
| H-11 | High | SHIP_UPDATE teleport chain (~5 km per packet) and unbounded velocity (1000 m/s against a 180 m/s hull) | `Server.Dispatch.cs:2019-2027` | probe |
| H-12 | High | Docked ships keep flying and firing while invulnerable | `Server.Dispatch.cs:217`, `Sector.cs:664`, `Ship.OnUpdate` | probe (movement) / inspection (fire) |
| H-13 | High | Mid-game side switch (defection) with no stage, balance or re-home gate | `Server.Dispatch.cs:745` | probe |
| H-14 | High | LOGOFF on a live socket re-arms LOGONREQ: unlimited ship churn on one connection | `Server.Dispatch.cs:1549-1551` | probe |
| H-15 | High | No player, connection or per-IP cap, no logon deadline, one thread per connection, no authentication | `TcpTransport.cs:472`, `Server.Dispatch.cs:76-176` | probe |
| H-16 | High (config) | Admin spectator, admin cheats and test-harness grants trust the self-declared character name | `Server.Spectate.cs:55`, `Server.cs:1169` | inspection |
| H-17 | Medium (config) | `timescale NaN` and `relocate NaN/Inf` admin cheats poison the sim clock or a ship pose | `Server.Timescale.cs:294`, `ServerConfig.cs:170`, `Server.AdminCheats.cs:227` | probe (clamp) |
| H-18 | High | ORDER_CHANGE BUILD can start unlimited stations from one constructor, instantly | `Server.ShipCommands.cs:2171-2288`, `Sector.cs:398-428` | inspection |
| H-19 | Medium | PLAYER_READY relays a forged ShipId (mark any teammate ready or unready) | `Server.Dispatch.cs:1484` | probe |
| H-20 | Medium | BUY_LOADOUT decoder has no part-count cap (10,920 parts per message), with per-part logging | `BuyLoadoutRequest.cs:91-100` | probe |
| H-21 | Medium | Up to 4 MiB of CRC work per frame runs on the sim thread (~11 ms per frame per attacker connection per tick) | `FedPacket.cs:108`, `ServerHost.cs:1178` | probe (timing) |
| H-22 | Medium | SWAP_PART / DROP_PART / RELOAD skip native range, type and flying checks | `Ship.cs:2758-2775`, `Server.ShipCommands.cs:357/443/570` | inspection |
| H-23 | Medium | Lobby: anyone can list any address or name, fill all 384 slots, and the proxy rate-limit gap stays open | `Allegiance.Lobby/ServerRegistry.cs:121-150`, `RateLimiting.cs:36-46` | inspection |
| H-24 | Medium | No per-peer rate limit on state-changing messages; native chat budget dropped | `Server.Dispatch.cs` (all), `Server.Economy.cs:248` | probe (wing) |
| H-25 | Medium | ORDER_CHANGE overwrites a teammate's target; missiles lock on unseen or cloaked enemies by id | `Server.ShipCommands.cs:1084,1325-1380`, `Ship.cs:3688-3710` | inspection |
| H-26 | Low-Medium | SUICIDE gate relaxed: a lifepod suicides straight to a docked respawn | `Server.Lobby.cs:995-1003` | probe |
| H-27 | Medium | Attacker-sized, unescaped log lines (up to 64 KB each): memory, disk and log forging | `Server.Dispatch.cs:377,470,108,171,1486`, `ConsoleLog.cs:32` | probe (observed) |
| H-28 | Low | CharacterName has no length or charset limit (~64 KB Latin-1 accepted) | `LogonRequest.cs:134` | probe (decoder) |
| H-29 | Low | CHATBUOY has no STAGE_STARTED gate (pre-game self-goto); unvalidated buoy floats relayed | `Server.Dispatch.cs:444-520` | inspection |
| H-30 | Low | TREASURE_ACK can throw after mutating, leaving the offer pending and repeatable | `Server.ShipCommands.cs:2453-2493` | inspection |
| H-31 | Low | ACTIVE_TURRET_UPDATE compares seconds to milliseconds and has no monotonic gate | `Server.ShipCommands.cs:212` | inspection |
| H-32 | Low | REQPINGDATA answers pre-logon peers with the server-wide ship-id roster | `Server.Dispatch.cs:1927`, `Server.ShipCommands.cs:989` | inspection |
| H-33 | Low | Sector, side and money broadcasts filter by id but not by mission (latent cross-mission leak) | `Server.Docking.cs:463`, `Server.Economy.cs:481,609,630`, `Server.ShipCommands.cs:70` | inspection |
| H-34 | Low | `Drain` drops every already-pulled event if one handler throws (defence in depth) | `ServerHost.cs:1214-1253` | inspection |
| H-35 | Info | RELOAD / FIRE_MISSILE pre-size lists from `cb`; FIRE_MISSILE ignores `ib` | `Reload.cs:121-140`, `FireMissile.cs:128-165` | probe (alloc) |
| H-36 | Info | A leader may broadcast DELPOSITIONREQ "Rejected" for any ship | `Server.Dispatch.cs:870-880` | inspection |
| H-37 | Info | Dead `fmid == 35` verbatim-echo branch | `Server.Dispatch.cs:619` | inspection |
| H-38 | Info | ORDER_CHANGE lets any teammate park, launch or recall any same-side NPC | `Server.ShipCommands.cs:1110-1160` | inspection |

Totals: 37 findings: 3 Critical, 13 High (H-16 config-dependent), 10 Medium (H-17 config-dependent; H-26 rated Low-Medium), 7 Low, 4 Info. H-04 is an unused number (merged into H-05).

---

### H-01 — Critical — AUTODONATE with a negative amount mints and steals money

- **Category:** money arithmetic.
- **Where:** `src/Allegiance.Server/Server.Economy.cs:141-142` clamps only the top:
  ```csharp
  if (live.Wallet.Money < req.Amount)
      req.Amount = live.Wallet.Money;
  ```
  Only `-1`, the leader toggle, is special-cased. Any other negative amount reaches `SetAutoDonate`, which then runs at `Server.Economy.cs:74-75`:
  ```csharp
  receiver.Wallet.Money += amount;
  donor.Wallet.Money -= amount;    // fsship.cpp:1336-1338
  ```
- **Input:** AUTODONATE(112) with `sidDonateTo` = a same-side teammate and `amount = -1000`, or `int.MinValue + 1`. The mission must be STAGE_STARTED.
- **Consequence:**
  - **[probe `money`]:**
    - with -1000: donor goes 0 → **+1000** and the teammate goes 0 → **-1000**;
    - with `int.MinValue+1`, the int wraps and the wallets become **-2,147,482,649 / +2,147,482,649**.
  - In general, any teammate can be drained, or the attacker gets about 2³¹ credits.
  - A donor whose own balance is already negative turns a positive request into theft, because the clamp copies that negative balance into Amount.
- **Native:** `FedSrv.CPP:5523` `TrapHackBoot(pfmAutoDonate >= 0)` tests the message *pointer*, not the amount. The same hole exists in native.
- **Classification:** bug (inherited native bug).
- **Fix:**
  - Reject `Amount < -1`.
  - Treat `-1` only as the leader toggle.
  - Require `0 <= Amount <= Wallet.Money` for a gift.
  - Use `checked` or `long` arithmetic in every wallet `+=`/`-=`, including `PlayerDonate`.

### H-02 — Critical — Pre-auth memory exhaustion: 16 MiB per frame header, held forever

- **Category:** memory / transport.
- **Where:** `src/Allegiance.Core/Transport/TcpTransport.cs`:
  - `:138` `MaximumFrame = 1 << 24;`
  - `:765-775`:
    ```csharp
    uint lenRaw = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(1));
    if (lenRaw > maxBodyLen) throw ...;
    var body = new byte[(int)lenRaw];      // allocated before a single body byte arrives
    ReadExactly(s, body);
    ```
  - `:524` `client.ReceiveTimeout = 0;` after the greeting. Data-phase reads never time out. There is no logon deadline and no connection cap (H-15).
- **Input:** a TCP connect, a 1-byte greeting (`01 01000000 'x'`), then a data-frame header `02 00000001` (claims 16,777,216 bytes) plus 3 body bytes. Then stall.
- **Consequence [probe `slowloris`]:**

  | Mode | Heap | Process | Released? |
  |---|---|---|---|
  | ThreadPerPeer, 32 connections | 0 → **512 MiB** after a full GC (16.0 MiB per connection) | threads 10 → 75 | no, still held after 5 s idle (`left=0`) |
  | Pool, 32 connections | 0 → **512 MiB** | working set 47 → 549 MiB | no |

  At ~1,000 connections, about 16 GiB is requested and the server OOMs. No logon is needed. The 4 MiB per-peer inbound cap (`ServerHost.cs:1168`) only applies after the full frame has been read, so it does not help.
- **Native:** DirectPlay did the framing; no equivalent.
- **Classification:** bug (design gap in the TCP transport).
- **Fix:**
  - Use a server-side inbound frame cap far below 16 MiB. Client→server traffic is tiny: the largest legitimate message is about a 1.1 KB LOGONREQ, so **64 KiB** is generous. The client half can keep a larger cap for server→client frames.
  - Read into a pooled or growing buffer as bytes actually arrive, instead of `new byte[lenRaw]` up front.
  - Add an idle or logon deadline (for example, no LOGONREQ within 15 s → drop) and a data-phase read deadline.
  - Cap total and per-IP connections (H-15).

### H-03 — Critical — BOARD_SHIP refund repeats; spawn-hull and teleport refunds mint money

- **Category:** money arithmetic / state machine.
- **Where:** `src/Allegiance.Server/Server.Docking.cs:112-118`:
  ```csharp
  int refund = ShipValue(boarder);               // hull price + parts
  ... side.Money += refund;
  ```
  - `:166` `boarder.ClearLoadout();` clears parts, but **the hull stays**, so every re-board refunds the hull price again.
  - Re-boarding while already boarded is allowed: the carrier is re-resolved, `AddChildShip` is idempotent, and the observer gate is collapsed.
  - The teleport refund path (`:2072-2078`) has the same value-minting shape.
- **Input:** both ships docked, Stage Started. One packet containing 100 × BOARD_SHIP(103) `{sidParent = teammate ship id}`.
- **Consequence:**
  - **[probe `board`]:** side treasury **0 → 50,000** from one 604-byte packet. That is 100 × the hull price of 500, and the hull is the **free spawn hull**.
  - The 256-message cap allows about 128 k credits per packet, unbounded over time.
  - Each board also sends a side-wide MONEY_CHANGE.
  - Reconnecting gives a fresh free hull to refund again. This is native's own refund path applied to a hull that was never paid for.
- **Native:** `SetParentShip` nulls the hull (`Igc/shipIGC.cpp:3629`), so a refund is paid once, and to the **player's** wallet (`FedSrv.CPP:2426-2443`).
- **Classification:** bug (port regression).
- **Fix:**
  - Null or zero the boarder's hull on board, as native does, or refund only on the unboarded → boarded transition.
  - Credit `live.Wallet`, not `Side.Money`.
  - Track purchase cost so free-spawn value is never refunded. Apply the same rule to the teleport refund.

### H-05 — High — Verbatim relay of oversized or unthrottled messages: N-fold amplification disconnects other players

- **Category:** work amplification / server-wide effect.
- **Where:** handlers that relay the client's original bytes (`FedPacket.Pack(new[] { m })`) after a decoder that accepts trailing junk (§2):

  | Message | Relay site | Audience |
  |---|---|---|
  | CHATMESSAGE | `Server.Dispatch.cs:381` | Everyone target = all peers |
  | CHATBUOY | `:513` | chat targets |
  | HIGHLIGHT_CLUSTER | `:560` | side, no gate at all |
  | PLAYER_READY | `:1484` | side, no gate |
  | DELPOSITIONREQ | `:878` | whole mission |
  | FORCE_TEAM_READY | `:927` | whole mission |
  | LOCK_LOBBY | `:1147` | whole mission |
  | LOCK_SIDES | `:1185` | whole mission |
  | AUTO_ACCEPT | `:1334` | whole mission |
  | CHANGE_TEAM_CIV | `:1397` | whole mission |

  - Chat text itself is up to about 65 KB.
  - Native's per-player chat budget (`fsship.h:449-465`, `FedSrv.CPP:3372`) was not ported.
  - All relays are guaranteed, ordered-lane sends, which are never shed (`TcpTransport.cs:1081-1089`).
- **Input:** one packet of 63 × CHATMESSAGE{Everyone, 65,000-char text} (4,096,831 bytes, under the 4 MiB inbound cap), repeated. Alternatively, 65,535-byte HIGHLIGHT_CLUSTERs (7 meaningful bytes, the rest zero).
- **Consequence [probe `relay`, 4 seated peers]:**
  - 65 KB HIGHLIGHT_CLUSTER: 65 KB to each same-side peer and to the sender.
  - Each 65 KB chat: 195 KB to the 3 others.
  - The 4 MiB packet produced **12.3 MB** of guaranteed sends in 40.6 ms of sim-thread time.
  - Server egress scales as attacker upload × recipients.
  - Every recipient's queue grows at the attacker's upload rate. A recipient with a slower downlink hits `QueueOverCapacity`, or the 32 MiB `QueueHardCap` (humans included, `TcpTransport.cs:1152-1155`), and is **disconnected**.
  - One client can clear a server.
- **Classification:** bug (oversized fixed-size messages accepted; native chat budget dropped), plus era constraint (native forwarded without limits).
- **Fix:**
  - Make fixed-size decoders require `CbMsg == FixedSize`.
  - Cap chat text, for example at 255 bytes (native UI limit) or 1 KB.
  - Re-encode relayed messages from decoded fields instead of forwarding `m`. That also fixes H-10 and H-19.
  - Port the chat budget as a per-peer token bucket covering CHAT, CHATBUOY, HIGHLIGHT, PLAYER_READY, the lobby toggles and POSITIONREQ (see H-24).

### H-06 — High — BUY_LOADOUT has no docked / alive / unboarded gate

- **Category:** state machine / cheating.
- **Where:** `ApplyLoadout`, `src/Allegiance.Server/Server.ShipCommands.cs:2496`. The only precondition is `ship.Mission != null`.
  - The tech gate runs only when docked: `:2535` `if (ship.StationId is { } dockId && ship.SideId is { } sd)`.
  - Then `:2742` `ship.SetFraction(1.0f);` and `:2776` `ship.SetShieldFraction(1.0f);`.
- **Input:** BUY_LOADOUT(4) `{HullId = current hull, Parts = current parts}` while **flying**, in combat. Or `HullId = any non-drone hull` while a lifepod is floating in space, or while a boarded gunner.
- **Consequence [probe `loadout`]:**
  - A flying ship (`StationId=null`) was re-fitted.
  - Shield **0.10 → 1.00** from an in-flight same-hull "rebuy" costing 0. That is a free instant full heal, repeatable.
  - By inspection:
    - a floating pod can buy a full fighter in place (escape-pod un-death);
    - flying skips the tech gate, so any civ's hull or part can be bought;
    - `fLaunch=true` calls `PerformUndock` on a docked boarded child.
- **Native:** `FedSrv.CPP:6153-6157` breaks unless the ship is docked (`GetCluster()==NULL && GetStation()!=NULL`) and not a ghost. `:6160` requires side ≥ 0, and `:6172` `TrapHackBoot(GetParentShip()==NULL)`.
- **Classification:** bug (native gates dropped).
- **Fix:** before any mutation, require `StationId != null && ParentShip == null && side >= 0 && !IsLifepodInSpace`. Run the tech gate unconditionally (refuse when there is no station).

### H-07 — High — BUY_LOADOUT has no per-mount hardpoint check: 255 guns on any hull

- **Category:** cheating.
- **Where:**
  - `Server.ShipCommands.cs:2680` `toMount.Add((partType, p.Amount, p.MountId, p.FractionMounted));`. The same-hull path keeps "free-mount behaviour", and the hull-change path only checks `CanMountPart` at hull level (`:2673`).
  - `src/Allegiance.Core/Sim/Weapon.cs:404` treats an out-of-range mount as a **fixed (pilot-fired) gun**: `bool fixed_ = _mountId < _ship.HullType.Hardpoints.Length ? ... : true`.
- **Input:** BUY_LOADOUT `{HullId = current, Parts = 255 × {PartId = cheapest gun, Amount = 32767, MountId = 0..254}}`, which is 1,541 bytes.
- **Consequence [probe `loadout`]:** on hull 92, which has **1 hardpoint**, the ship ended with **255 weapons**, all forward-firing. The cheapest gun's price was 0. Massive DPS, plus a projectile flood that every client must simulate.
- **Native:** `shipIGC.h:1029-1031` `m_myHullType.CanMount(ppt, mountID) && GetMountedPart(et, mountID)==NULL`.
- **Classification:** design (stated simplification) that is exploitable.
- **Fix:** enforce per-mount `CanMount` and an empty slot. Weapons only at mounts below the hull hardpoint count; other equipment only at mount 0; cargo only at -1..-5.

### H-08 — High — Part Amount and Ammo are never clamped

- **Category:** cheating / integer wrap.
- **Where:**
  - `Server.ShipCommands.cs:2680` passes the client `Amount` to `Ship.MountPart` (`Ship.cs:2570`), which stores it.
  - Price is by part type only (`ShipValue`, `Server.Docking.cs:1401`), so ammo is free.
  - RELOAD adds to `Ammo`, a plain `short` with no maximum.
  - The TREASURE_ACK merge is `(short)(existing.Amount + offer.Amount)` (`Server.ShipCommands.cs:2453`).
  - DROP_PART turns `part.Amount` into a treasure amount.
- **Input:** BUY_LOADOUT with `{PartId = missile launcher / chaff dispenser / ammo pack, Amount = 32767}`.
- **Consequence [probe `loadout`]:**
  - Mounted parts held `Amount = 32767`, so about 32,767 missiles or chaff can be bought for the launcher price.
  - RELOAD from such a pack pushes Ammo past MaxAmmo and can wrap `short`.
  - DROP_PART makes 32,767-unit treasures for anyone to pick up. A negative Amount makes "poison" treasures that shrink the picker's stock.
- **Native:** clamps `SetAmount` to `GetAmount(ship)` (`magazineigc.h:107-113`) and `SetAmmo` to `GetMaxAmmo()` (`shipIGC.h:597-601`).
- **Classification:** bug.
- **Fix:** clamp to the part type's capacity on every mount, merge and `SetPartAmount`; clamp `Ammo` in its setter; reject negative amounts at the boundary.

### H-09 — High — BUY_LOADOUT / SWAP_PART: O(parts²) LOADOUT_CHANGE output

- **Category:** work amplification.
- **Where:**
  - `ApplyLoadout` calls `ClearLoadout()` and then `MountPart` up to 255 times.
  - Each `MountPart` raises `LoadoutChangeEvent` (`Ship.cs:2611`).
  - `BroadcastLoadoutChange` (`Server.Combat.cs:507-555`) re-encodes **the whole loadout** each time and sends it guaranteed to the owner and every visible teammate, plus a Console line each time.
- **Input:** after H-07's purchase, repeat the identical 1,539-byte BUY_LOADOUT. It costs 0, since the value is unchanged.
- **Consequence [probe `loadout`]:**
  - One message produced about 202 KB to the owner and 201 KB to teammates, in 12 ms.
  - 10 repeats produced 4.0 MB out (**262× the input**) and 50 ms of sim-thread time.
  - A 4 MiB packet of these is about 2,700 messages, enough to overflow every teammate's 32 MiB hard cap (disconnect) and to stall the tick.
- **Native:** one `QueueLoadoutChange` per `ProcessShipLoadout`.
- **Classification:** bug.
- **Fix:** batch mounts (suppress per-mount events during `ApplyLoadout` and the swap path, then emit one LOADOUT_CHANGE); no-op unchanged requests; per-peer rate limit (H-24).

### H-10 — High — Chat sender spoofing

- **Category:** acting as another player.
- **Where:** `Server.Dispatch.cs:381` (CHATMESSAGE) and `:513` (CHATBUOY) relay `m`, so the wire `ChatData.SidSender` reaches every client. The client resolves the speaker from it, and `SidSender < 0` renders as a system/console message (ClientWorld.cs:3791-3795).
- **Input:** CHATMESSAGE `{SidSender = <victim ship id or -1>, ChatTarget = Everyone, Message = "..."}`.
- **Consequence [probe `lobby`]:** bot1 received `SidSender = 8100` (bot2's ship) from bot0, and `SidSender = -1` for a fake "SERVER: …" announcement. This enables impersonation and fake server notices, for example phishing to another host.
- **Native:** overwrites the field before forwarding, `pfmChat->cd.sidSender = pfsPlayer->GetIGCShip()->GetObjectID();` (`FedSrv.CPP:5249`; buoy `:5301`).
- **Classification:** bug (native check lost).
- **Fix:** re-encode with `SidSender` = the sender's ship id, or patch bytes 8-9 of a copy, before relaying both messages.

### H-11 — High — SHIP_UPDATE: teleport chain and unbounded velocity

- **Category:** cheating / floats.
- **Where:** `Server.Dispatch.cs:2019-2027`:
  ```csharp
  ship.SetOrientation(orientation);
  if (dSq < 400.0f) { ship.Position = position; ship.Velocity = velocity; }
  ```
  - Each accepted update moves the server pose, and the next update in the same packet is judged against the **new** pose.
  - Native's monotonic `su.time > m_timeUpdate` gate (`fsship.cpp:1175`) was not ported.
  - The cookie is predictable: `Cookie+1` (`Server.cs:230`).
  - Velocity is only bounded by the compact encoding (≤1023.9 m/s per axis).
- **Input:** one packet of 255 SHIP_UPDATE(24), each with `time = now`, the matching cookie, and position 19.5 m further along +X than the previous one. Also an update with `velocity = (1000,0,0)` at the current position.
- **Consequence [probe `teleport`]:**
  - The ship moved **4,972 m in zero sim time** from one packet.
  - Velocity 1000 m/s was accepted on a hull with `Speed=180` and was still 990 m/s a tick later.
  - Re-asserting each tick moved the ship 900 m in 1 s.
  - Result: speed hack, teleport, and ram-damage scaling.
- **Classification:** era constraint (the 20 m and velocity trust are native) plus bug (monotonic gate dropped).
- **Fix:**
  - Restore the monotonic time gate.
  - Accept at most one pose per tick per ship.
  - Bound accepted displacement by `maxSpeed(+afterburner) × Δt_server + 20 m`.
  - Clamp accepted velocity to the hull's effective max.

### H-12 — High — Docked ships keep flying and firing while invulnerable

- **Category:** state machine.
- **Where:**
  - SHIP_UPDATE is gated on `Sector is null` (`Server.Dispatch.cs:217`), but the port keeps `Sector` set on docked ships.
  - `Sector.Update` updates every model (`Sector.cs:664-666`).
  - `Ship.OnUpdate` runs movement and `weapon.Update` with no StationId check (`Ship.cs:7276-7357`).
  - FIRE_MISSILE (`Server.ShipCommands.cs:868`), FIRE_EXPENDABLE (`:680`) and ACTIVE_TURRET_UPDATE (`:202`) don't check docking either.
- **Input:** while docked, send a legacy SHIP_UPDATE `{Time=0, throttle=+1, stateM=0x8001}` every tick.
- **Consequence [probe `ships`]:** after a legitimate SUICIDE → docked lifepod at station 1051, 40 ticks of throttle moved the ship **108 m while `StationId` was still set**, at |v| = 30 m/s. A docked ship is not a collision body and is not in the ship broadcast, so it is invisible and untouchable. By inspection it also fires guns, missiles, mines and chaff.
- **Native:** a docked ship has `GetCluster()==NULL`, so all four handlers are no-ops (`FedSrv.CPP:4857,4873,5428,5634`).
- **Classification:** bug.
- **Fix:** gate SHIP_UPDATE, ACTIVE/INACTIVE_TURRET_UPDATE, FIRE_MISSILE and FIRE_EXPENDABLE on `StationId is null` (for turrets: the parent is not docked). Also skip movement and weapons in `Ship.OnUpdate` for docked ships.

### H-13 — High — Mid-game side switch (defection)

- **Category:** state machine / cheating.
- **Where:** `Server.Dispatch.cs:720-745`. The only checks are that the side exists, `LockSides`, and "leaderless or AutoAccept". Then `TryJoinSide(reqLive.Ship, pr.SideId)` runs, which only rewrites `SideId` and leadership (`Server.Lobby.cs:2441-2527`).
- **Input:** POSITIONREQ(39) `{SideId = other side}` during STAGE_STARTED, when that side is empty or set to auto-accept.
- **Consequence [probe `defect`]:**
  - The flying ship switched side 0 → 1 instantly. It was the same ship object, at the same place (moved 0 m), and not docked.
  - The defector then gets the other side's per-side sync (stations, asteroids, probes), keeps its loadout and wallet, and can hop back.
  - There are no team-size or balance limits, including on the initial pending pick.
- **Native:** `CheckPositionRequest` (`fsmission.cpp:4575ff`) refuses this: DPR_NoDefections when started, DPR_TeamFull, DPR_TeamBalance, DPR_SideDefeated and others.
- **Classification:** bug (native gates dropped).
- **Fix:** port `CheckPositionRequest`. Refuse when started (or force a dock/respawn at the new side's base and reset the wallet), and enforce per-team caps and balance.

### H-14 — High — LOGOFF on a live socket re-arms LOGONREQ (bypasses P646 one-logon)

- **Category:** state machine / amplification.
- **Where:** `Server.Dispatch.cs:1549-1551`: `SendPeer(echo); ReleasePeer(from);`. The socket stays open. `ReleasePeer` also calls `_host.RemovePeerAccounting(id)`, so receives already queued lose their byte reservation.
- **Input:** repeat LOGOFF(3) → LOGONREQ(1) → POSITIONREQ(39) on one connection.
- **Consequence [probe `lobby`]:**
  - Three cycles on one live connection seated ships **8102, 8103 and 8104**.
  - Each cycle produces SHIP_DELETE plus a full join burst, JOIN_SIDE and exports to the mission.
  - It is a cheap traffic multiplier and churns ship ids, and the inbound byte accounting under-counts after each cycle.
- **Native:** LOGOFF deletes the player; the connection is not reused.
- **Classification:** bug.
- **Fix:** close the transport connection after the LOGOFF echo, or mark the connection logged-off and drop everything after it. Never remove accounting for a socket that is still open.

### H-15 — High — No connection, player or per-IP caps; no auth; one thread per connection

- **Category:** resource exhaustion / authentication.
- **Where:**
  - `TcpTransport.cs:472` starts a `greet` thread per accepted socket.
  - After the greeting, every peer has a dedicated read thread (`Spawn(ReadDataLoop)`), in pool mode too.
  - `Server.Dispatch.cs:76-176` validates only `FedsrvVer == MsgVer`. CDKey, ZoneTicket and the Steam ticket are ignored, and `Server.Lifecycle.cs:227` says there is no MaxPlayers concept.
  - Each validated logon reserves a ship id and is seated within 30 s, which spawns a ship.
  - Any name without the `mcp-` prefix is marked human (`Dispatch:134`). Humans get no overflow-disconnect and a 60 s grace (`TcpTransport.cs:189-198`), so a fake human that never reads holds up to 32 MiB of send queue.
- **Input:** K sockets that connect silently; K sockets that greet and then idle; or K sockets that each send LOGONREQ (and optionally POSITIONREQ).
- **Consequence [probe `slowloris`]:**
  - 300 silent connections → **300 extra threads** for 10 s.
  - 300 greeted-but-silent connections → **300 threads held indefinitely**, all `joined`, with no logon deadline.
  - By inspection, K LOGONREQs → K seated ships with per-tick broadcast cost growing about O(K²).
  - Duplicate character names are allowed, so players can be impersonated in EXPORT and PlayerInfo.
- **Classification:** design / era constraint. Native had zone or lobby authentication and `nMaxPlayersPerTeam`.
- **Fix:**
  - A maximum on total connections and on connections per IP.
  - A logon deadline.
  - Max players and max per team.
  - Unique names.
  - Real authentication before an internet launch, or at least a lobby-issued join token.
  - Move reads to async I/O so idle sockets don't cost a thread each.

### H-16 — High (config-dependent) — Privilege by self-declared character name

- **Category:** authentication.
- **Where:**
  - `Server.Spectate.cs:55-57` `IsSpectatorAdmin` checks only `AllowNames` (wildcards such as `mcp-*` allowed). The `Secret` gates only chat cheats (`Server.AdminCheats.cs:141`).
  - `Server.cs:1169-1176` `HarnessGrantFor` gives `testHarness.Money` on seat (`Server.Lobby.cs:2963`) and skips the BUY_LOADOUT tech gate (`Server.ShipCommands.cs:2512`) when `name.StartsWith(CharacterNamePrefix)`.
  - The boot banner says "real players NEVER affected" (`Server.Lifecycle.cs:96-99`).
- **Input:** LOGONREQ `CharacterName = "mcp-x"` (or any allow-listed admin name), then VIEW_CLUSTER(104) while flying.
- **Consequence:** when `adminCheats` or `testHarness` is enabled:
  - full-map wallhack (all ships, cloaked ones included);
  - harness money and free tech;
  - the `relocate` cheat on any "mcp-" named human (`AdminCheats.cs:210-215`).
- **Classification:** design.
- **Fix:** refuse to boot with `adminCheats`/`testHarness` on a non-loopback bind unless explicitly forced; require the secret or a server-issued token for spectating as well; never grant on name prefix alone.

### H-17 — Medium (config-dependent) — NaN/Inf through admin cheats

- **Category:** floats.
- **Where:**
  - `Server.Timescale.cs:294` `double.TryParse` accepts "NaN".
  - `ServerConfig.cs:170` `Math.Clamp(v, 0.1, 1.0)` returns NaN for NaN. **[probe `lobby`: `ClampTimeScale(NaN) = NaN`]**
  - `_timeScale = NaN` makes `_simNow += EffectiveSimPeriod` (`Server.Broadcast.cs:86`) NaN permanently. Countdowns, ballots, paydays and expiries stop, and TIMESCALE_UPDATE broadcasts NaN.
  - The `relocate` cheat (`Server.AdminCheats.cs:227-229,273-278`) accepts NaN, Inf and 1e39 coordinates and a zero facing.
- **Input:** chat `timescale NaN`, or `relocate NaN 0 0`, from an admin (or a name-spoofed admin, H-16).
- **Consequence:** the server-wide sim clock or one ship pose is poisoned, and the NaN reaches every client.
- **Classification:** bug.
- **Fix:** check `double.IsFinite` / `float.IsFinite` before clamping or applying, and require a non-zero facing.

### H-18 — High — ORDER_CHANGE BUILD: unlimited instant stations from one constructor

- **Category:** ids from the wire / acting on NPCs.
- **Where:**
  - `Server.ShipCommands.cs:1084` resolves the target among any same-side ships.
  - An NPC target with `CidBuild` goes to `HandleDirectNpcBuildOrder` (`:2171-2288`). Its legality checks cover the hull, rock type, AABM and sector, but **not `target.IsBuilding`** and not the rock already being targeted.
  - `Sector.StartBuildingEffect` (`src/Allegiance.Core/Model/Sector.cs:398-428`) sets `builder.IsBuilding = true` but never checks it, then `_buildingEffects.Add(effect)`.
  - When the effect completes, the builder is removed but not marked dead, so sibling effects survive (`Sector.cs:4266-4280,4365`).
- **Input:** one packet of N × ORDER_CHANGE(93) `{shipID = same-side NPC constructor, commandID = c_cidBuild, objectType = OT_asteroid, objectID = rock_i}`, where each rock is in the constructor's sector with a matching AABM.
- **Consequence:** N stations from one constructor, with no travel. Repeating the same rock stacks duplicates. A docked constructor works too (H-12).
- **Native:** no client-driven drone build order. Native ORDER_CHANGE to a drone only handles stay-docked, launch and return (`FedSrv.CPP:7281-7322`).
- **Classification:** bug (port-added path).
- **Fix:**
  - Refuse when `IsBuilding`, when the rock has a pending effect, or when the target is docked.
  - Abort effects whose builder is gone.
  - Restrict direct NPC build orders to the side commander.

### H-19 — Medium — PLAYER_READY relays a forged ShipId

- **Category:** acting for another player.
- **Where:** `Server.Dispatch.cs:1484` relays `m`. Only `_playerReady[from]` (`:1496`) uses the true sender.
- **Input:** PLAYER_READY(45) `{ShipId = teammate, fReady = true/false}`.
- **Consequence [probe `lobby`]:** the teammate received `ShipId = 8100, fReady = True`, sent by bot0. Any teammate's ready state can be flipped on everyone's roster.
- **Native:** `TrapHackBoot(pfmPlayerReady->shipID == pfsPlayer->GetShipID())` (`FedSrv.CPP:7019`).
- **Classification:** bug.
- **Fix:** drop or kick on mismatch, and re-encode the relay.

### H-20 — Medium — BUY_LOADOUT decoder has no part-count cap

- **Category:** CPU / log amplification.
- **Where:** `src/Allegiance.Core/Wire/BuyLoadoutRequest.cs:91-100`: `nParts = (cb-2)/6`, with no upper bound. Per-part `Console.WriteLine` for unknown, tech-refused or no-hardpoint parts (`Server.ShipCommands.cs:2626,2639,2671`).
- **Input:** a 65,531-byte BUY_LOADOUT with 10,920 × `{PartId = 0x7FFF}`.
- **Consequence [probe `loadout`]:** handled in 11.7 ms of sim-thread time per message, and it floods the 8,192-line log sink, evicting real lines. 64 of these fit in one 4 MiB packet, about 750 ms of tick time.
- **Native:** `TrapHackBoot(cbloadout <= c_cbLoadout)` (`FedSrv.CPP:6180`).
- **Classification:** bug.
- **Fix:** cap parts at hardpoints + 5 cargo slots (or native `c_cbLoadout`) in the decoder, and aggregate the skip logs.

### H-21 — Medium — Up to 4 MiB of CRC work per frame on the sim thread

- **Category:** CPU.
- **Where:**
  - `ServerHost.cs:1178` enqueues frames of up to `MaxBufferedBytesPerPeer` (4 MiB).
  - `Server.Dispatch.cs:29` `FedPacket.Unpack` computes a byte-at-a-time table CRC over the whole frame **on the sim thread** (`FedPacket.cs:108`) before rejecting it.
- **Input:** a continuous stream of 4 MiB data frames with a bad CRC, about one per tick per connection.
- **Consequence [probe `crc`]:** `Unpack` takes 2.9 ms for 1 MiB, **10.7 ms for 4 MiB** and 40 ms for 16 MiB. About five such connections consume the whole 50 ms tick budget: tick overruns and time dilation for everyone.
- **Classification:** bug (design gap).
- **Fix:** with H-02's 64 KiB inbound frame cap the cost is negligible. Also verify the CRC on the per-connection read thread and enqueue only valid packets.

### H-22 — Medium — SWAP_PART / DROP_PART / RELOAD skip native range, type and flying checks

- **Category:** state machine / invariants.
- **Where:**
  - `Ship.cs:2758-2775` `ResolvePart` returns `_cargo[slot]` for a negative mount regardless of `etOld`.
  - A positive `mountNew` 0..127 is accepted for any equipment type with no `CanMount`.
  - The handlers (`Server.ShipCommands.cs:357/443/570`) check stage and unboarded only, so docked ships pass (H-12).
- **Input:** SWAP_PART `{EtOld = Weapon, MountOld = <turret mount>, MountNew = 100}`, which turns a turret gun into a fixed pilot gun (`Weapon.cs:404`). Or SWAP_PART moving a cargo Shield into weapon mount 0. Or DROP_PART while docked, which leaves a treasure next to the station.
- **Native:** `FedSrv.CPP:6466-6483` checks the cluster, `etOld` range, mount ranges, and positive mounts only for ET_Weapon. `6302-6313` and `6515-6516` check the cluster for DROP_PART and RELOAD.
- **Classification:** bug.
- **Fix:** port the TrapHack range and type checks, require flying, and run per-destination `CanMount`.

### H-23 — Medium — Lobby directory: spoofed listings, slot exhaustion, proxy rate-limit gap

- **Category:** remote input (HTTP).
- **Where:** `src/Allegiance.Lobby/ServerRegistry.cs:121-150`.
  - `Address` and `Port` are free text, not tied to the source IP, and registration needs no authentication.
  - `Sanitize` removes only `char.IsControl` (`:192`), so bidi and format characters such as U+202E pass.
  - `LobbySettings.MaxEntries = 384`.
  - `RateLimiting.cs:20-53` allows 30 registrations per minute per `RemoteIpAddress`, and `ForwardedHeaders` is not configured (`:36-46` only notes it). Behind a reverse proxy, every client shares one bucket.
  - DELETE and GET are not rate-limited.
  - Kestrel's default 30 MB body size applies.
  - `List()` returns expired entries until the next sweep.
- **Input:** POST `/api/servers/register` with `{Name: "<copy of a real server name>", Address: "evil.example" or a victim IP}`. Or about 13 IPs × 30 registrations per minute, heartbeating inside 90 s.
- **Consequence:**
  - Phishing listings, or using a victim IP as a reflection target.
  - All 384 slots held, so legitimate servers get 409.
  - The rate limit is ineffective behind a proxy.
- **Classification:** design.
- **Fix:** list the source IP (or challenge-connect to the claimed address); cap listings per IP; configure `ForwardedHeaders` with known proxies; set a small `MaxRequestBodySize`; strip Unicode format characters; filter expired entries in `List()`.

### H-24 — Medium — No per-peer rate limit on state-changing messages; native chat budget dropped

- **Category:** amplification / harassment.
- **Where:** every dispatch branch. The only throttles are PING and the unsupported-fmid log. `Server.Economy.cs:248` documents that native's `DecrementChatBudget` gate for REQUEST_MONEY was deliberately skipped.
- **Input:** 256 messages per packet, repeated, of any of: SET_WINGID (side-wide broadcast each), RIPCORD_REQUEST alternating a sector and -1 (sector-wide ACTIVATE or ABORTED), REQUEST_MONEY, BUCKET_DONATE of 0 (side-wide MONEY_CHANGE + BUCKET_STATUS), PLAYER_DONATE to self, POSITIONREQ to a led side (whole-mission POSITIONREQ(40) broadcast), or a Team command-chat (QUEUED_COMMAND_UPDATE per teammate).
- **Consequence:**
  - **[probe `amplify`]:** 256 × SET_WINGID (2,560 B) produced 512 guaranteed frames, 7,168 B, with only 2 same-side peers. Output scales linearly with side size, about 2.8 B out per byte in per side member.
  - The same queue-overflow disconnect mechanism as H-05 applies, at a lower rate.
- **Classification:** era constraint (DirectPlay and trusted clients), plus bug for the chat budget.
- **Fix:** a per-peer token bucket on all C→S state-changing messages; make unchanged-value requests (wing, ready, lock) no-ops; ignore zero or self donations; coalesce ripcord changes per tick.

### H-25 — Medium — ORDER_CHANGE target overwrite; missile lock on unseen or cloaked enemies

- **Category:** acting on others' objects / ids from the wire.
- **Where:**
  - `Server.ShipCommands.cs:1084`: the target is any same-side ship.
  - `:1325-1380`: `target.CommandTarget = FindShipAnySide(req.ObjectID, mission)`.
  - `Ship.UpdateMissileLock` (`Ship.cs:3688-3710`) requires same sector, enemy side and range, but has **no SeenBySide or cloak check**.
  - Ship ids for all sides leak through REQPINGDATA (H-32).
- **Input:** ORDER_CHANGE `{shipID = teammate, command = c_cmdCurrent, objectID = NA}` to clear a teammate's target. Or `{shipID = own, OT_ship, objectID = <cloaked enemy id>}` followed by FIRE_MISSILE.
- **Native:** the same lookups, but the client only picked targets it could see.
- **Classification:** era constraint.
- **Fix:** only the owner (or the side commander, for the queued slot) may set a ship's command target. Resolve targets only among objects the side has seen, and drop the lock when the target is not seen.

### H-26 — Low-Medium — SUICIDE gate relaxed

- **Category:** state machine.
- **Where:** `Server.Lobby.cs:995-1003`, the comment "Relaxed child/cheat gate … ANY placed ship may suicide".
- **Input:** SUICIDE(80) while flying a lifepod, or mid-combat.
- **Consequence [probe `ships`]:** a floating lifepod's SUICIDE produced `[lifepod] … docked Lifepod at station 1051 'Garrison'`, an instant safe return that skips rescue, oxygen and enemy pod-kill credit. In combat it denies the killer the kill.
- **Native:** suicide only for a boarded child or `CanCheat` (`FedSrv.CPP:6338`).
- **Classification:** bug (the gate collapsed on a premise that no longer holds).
- **Fix:** restore `ParentShip != null || IsAdmin`.

### H-27 — Medium — Attacker-sized, unescaped log lines

- **Category:** memory / disk / log integrity.
- **Where:**
  - Unthrottled `Console.WriteLine` with full attacker strings: chat `Server.Dispatch.cs:377`, buoy `:470`, logon `:108` and `:171` (a version-mismatched peer can resend LOGONREQ without limit), ready `:1486`.
  - `ConsoleLog` (`src/Allegiance.Core/ConsoleLog.cs:32`) bounds the queue at 8,192 **lines**, not bytes.
- **Input:** a 65 KB chat text containing `\n` / ANSI escapes, flooded.
- **Consequence:**
  - **[observed in probe `relay`]:** 65,000-character chat lines were written to the server log.
  - With a stalled stdout consumer (Docker json-file), up to 8,192 × ~130 KB ≈ **1 GiB** is held in memory.
  - Otherwise 10 MB/s of chat becomes about 36 GB/h of log on disk.
  - CR/LF in Latin-1 names and text forges log lines, and the flood evicts real lines (the drop counter rises).
- **Classification:** bug.
- **Fix:** truncate (for example to 128 chars) and escape attacker strings in log lines, rate-limit per peer with the existing ticket mechanism, and bound `ConsoleLog` by characters.

### H-28 — Low — CharacterName has no length or charset limit

- **Category:** strings.
- **Where:** `LogonRequest.cs:134` (`ReadZts` reads up to cb, about 64 KB, as Latin-1, including NUL and control characters). No check in `Server.Dispatch.cs:76-176`.
- **Consequence [decoder fuzz]:** 61 KB names decode fine. The full name flows into ballot text (`Server.Lobby.cs:1497-1518`), team chat (`Server.Combat.cs:1164-1172`), "Welcome {name}" and every log line (H-27). Encoders truncate to `char[25]` on the wire, but the server-side strings do not.
- **Native:** boots when there is no NUL within `c_cbName` (`FedSrv.CPP:5817-5818`).
- **Classification:** bug.
- **Fix:** reject names longer than 24 characters, or containing control or format characters, at LOGONREQ.

### H-29 — Low — CHATBUOY has no stage gate; buoy floats relayed unvalidated

- **Where:** `Server.Dispatch.cs:444-520`.
- **Behaviour:** the self-goto `SetGoto(...)` runs in any stage, and Mission.Update moves ships while Starting. Native requires STAGE_STARTED (`FedSrv.CPP:5297`). A legacy client can move during the briefing countdown, getting past the P180 pre-game hold.
- **Checked by [probe `buoy`]:** bogus `ClusterId` values (32767, -2, -1) cause **0 stage failures** over 40 ticks (no path → no-op), so the cluster id is safe.
- **Remaining problem:** buoy positions with NaN are relayed verbatim to other clients.
- **Classification:** bug.
- **Fix:** gate on Started, and check that buoy floats are finite before relaying (or re-encode, H-05).

### H-30 — Low — TREASURE_ACK can throw after mutating, leaving the offer pending

- **Where:** `Server.ShipCommands.cs:2453/2462` mutate the ship. Then `:2478` `SendSectorWide(ship.Sector!.Id, …)` throws a NullReferenceException when `Sector` is null after a stays-dead death. The dispatch catches it before `:2493` `PendingTreasureOffer = null`.
- **Consequence:** the offer stays pending, so repeated acks mount or merge the part again (duplication). Impact is limited because the ship is dead.
- **Classification:** bug.
- **Fix:** clear the offer first, or check `Sector` before mutating; clear offers on death.

### H-31 — Low — ACTIVE_TURRET_UPDATE time units and monotonic gate

- **Where:** `Server.ShipCommands.cs:212` `double deltaT = gunner.LastUpdate - atu.Time;` compares seconds with a raw uint in milliseconds. SHIP_UPDATE divides by 1000 (`Server.Dispatch.cs:1988`).
- **Consequence:** real clients' turret updates are always "stale". A client that sends seconds passes. There is no monotonic gate, and there is an unthrottled Console line per message.
- **Classification:** bug.
- **Fix:** use `atu.Time / 1000.0`, add the monotonic gate, and rate-limit the log.

### H-32 — Low — REQPINGDATA answers pre-logon peers with the full ship-id roster

- **Where:** the `Server.Dispatch.cs` REQPINGDATA branch calls `HandleReqPingData(from)` with no ship check (`Server.ShipCommands.cs:989`). It lists every ship on the server, all sides and missions, every 5 s.
- **Consequence:** an information leak that feeds H-25. Floods only fill the sender's own queue.
- **Classification:** era constraint.
- **Fix:** require a seated peer, scope the roster to the requester's mission, and drop debounced requests silently.

### H-33 — Low — Broadcast filters ignore the mission (latent)

- **Where:**
  - `Server.Docking.cs:463` `if (kv.Value.Sector.Id == sectorId)` (`SendSectorWide`, used by ripcord, promote, swap, drop, reload);
  - `Server.Economy.cs:481,609,630` (side-id-only money and bucket broadcasts);
  - `Server.ShipCommands.cs:70` and `Server.Spectate.cs:77` (hard-wired `_missions[0]`);
  - `FindSameSideShip` scans all missions.
- **Consequence:** with more than one game configured, traffic and orders cross missions. Harmless while only `_missions[0]` seats players.
- **Classification:** bug.
- **Fix:** add `ReferenceEquals(kv.Value.Ship.Mission, mission)` to every filter.

### H-34 — Low — `Drain` drops already-pulled events when a handler throws

- **Where:** `ServerHost.cs:1214-1253`. Phase 2 calls `handle(evt)` with no try/catch, and Phase 3 (re-queue) runs only on normal exit. `Step("drain", …)` catches the exception, so every event already pulled into `lanes` but not yet handled is **lost**. That includes `PeerLeftEvt`, which leaves a ghost ship, and byte reservations, which never release.
- **Reachability:** every dispatch branch has its own try/catch. The remaining throwers are the logon-path code after decode, `CaptureReceived` (file I/O when capture is on; a full disk is reachable through H-27), and `ReleasePeer`. So this is defence in depth.
- **Classification:** bug (hardening).
- **Fix:** wrap `handle(evt)` per event, or put Phase 3 in a `finally`.

### H-35 — Info — Decoders pre-size lists from `cb`; FIRE_MISSILE ignores `ib`

- **Where:**
  - `Reload.cs:121-140` and `FireMissile.cs:128-165` build `new List<T>(n)` from the declared `cb` before the slice is validated. **[fuzz]:** up to about 66 KB for a 13-byte message. Bounded by the ushort `cb`.
  - FIRE_MISSILE reads entries from offset 32 regardless of `ib`.
- **Classification:** bug (cosmetic).
- **Fix:** validate `ib + cb <= len` first, and read from `ib`.

### H-36 — Info — A leader may broadcast DELPOSITIONREQ "Rejected" for any ship

- **Where:** `Server.Dispatch.cs:860-880`. The message is relayed whole-mission even when no request is pending. Native is the same.
- **Classification:** era constraint.
- **Fix:** relay only when a pending request was actually removed, and re-encode the relay.

### H-37 — Info — Dead `fmid == 35` verbatim-echo branch

- **Where:** `Server.Dispatch.cs:619`. It is unreachable because `Ping.Fmid == 35` is handled at `:38`. If it ever became reachable, it would echo raw bytes with no throttle.
- **Fix:** delete it.

### H-38 — Info — Any teammate can command any same-side NPC

- **Where:** `Server.ShipCommands.cs:1110-1160` (stay-docked, launch, return). This matches native `FedSrv.CPP:7281+`.
- **Consequence:** griefing, for example parking every miner.
- **Classification:** era constraint / design.
- **Fix:** commander-only.

---

## 4. Checked and found OK (positive results)

- **Framing:**
  - The greeting is capped at 4,096 bytes before allocation and has a 10 s timeout.
  - A non-Data frame type closes the connection.
  - `FedPacket.Unpack` verifies the CRC before walking messages and rejects `cbmsg==0`, overrun and partial headers.
  - The 256-message cap works (the existing live test confirms it).
- **Exception containment:** every one of the 43 dispatch branches wraps decode **and** handling in `try { … } catch (Exception)`. The fuzzer found only `ArgumentException` and `ArgumentOutOfRangeException` from decoders.
- **SHIP_UPDATE floats:**
  - The only raw float is `position`, and `IsLegalPosition` (`Server.Dispatch.cs:2053-2057`, `0 < |p|² < 90000²`) rejects NaN, ±Inf and overflow.
  - Orientation is a compact quaternion normalised by `l2 > 0`, with all-zero mapped to identity. **[probe `crc`]:** 2,000,000 random 8-byte slots decoded to **0** non-finite or non-unit orientations.
  - Controls, turn rates and power are sbyte or byte quantised.
  - Velocity is 13-bit/8 × percent, so finite and ≤1023.9 m/s.
  - `CalculateShipPose` iterates at most 7 times (|Δt| ≤ 1.5 s).
  - The same holds for ACTIVE_TURRET_UPDATE.
  - **I found no NaN/Inf path from the wire into the sim or broadcasts**, except the config-gated admin cheats (H-17) and relayed buoy bytes (H-29).
- **ID validation:**
  - POSITIONACK, FORCE_TEAM_READY, AUTO_ACCEPT, CHANGE_TEAM_CIV, START_GAME, RANDOMIZE_TEAMS, SIDE_INACTIVE, CHANGE_ALLIANCE, LOCK_*: each checks own side, leader or owner, stage, and existence or range of ids.
  - VOTE: own mission and eligibility.
  - PROMOTE: own children only.
  - BUCKET_DONATE: own side's bucket, negative refused, capped.
  - PLAYER_DONATE: positive, funded, same or allied side.
  - REQUEST_MONEY: forward only.
  - TREASURE_ACK: hardpoint and PartMask checked.
  - DROP_PART and RELOAD: cargo range checked.
  - AllocateShipId: bounded loop.
  - CHATBUOY: unknown ClusterId is safe (**[probe `buoy`]**, 0 stage failures).
- **Pending side-select:** a bogus side id gets DPR_SideGone and stays pending (`Server.Lobby.cs:2814-2822`).
- **Lobby web UI:** uses `textContent` (no XSS); the ownership token is a 128-bit GUID; `LobbyHeartbeat` is outbound-only.

## 5. Suggested fix order

1. **Transport hardening (H-02, H-15, H-21):**
   - a 64 KiB server-side inbound frame cap;
   - incremental buffering instead of pre-allocation;
   - a logon deadline and a data-phase idle deadline;
   - total and per-IP connection caps;
   - CRC checked off the sim thread.
2. **Economy (H-01, H-03):** amount sign checks; refund once, to the wallet, never for free value; checked arithmetic.
3. **BUY_LOADOUT family (H-06, H-07, H-08, H-09, H-20, H-22):** port native's docked/unboarded/ghost gates and per-mount `CanMount`; clamp amounts; batch LOADOUT_CHANGE; cap part counts.
4. **Relay hygiene (H-05, H-10, H-19, H-24, H-27, H-29):** exact-size fixed decoders; re-encode every relay with server-truth fields; chat length cap; per-peer token bucket (the native chat budget); truncate and escape log strings.
5. **State gates (H-11, H-12, H-13, H-14, H-18, H-26):** monotonic time and per-tick pose acceptance with a displacement bound; gate docked ships out of control and fire; `CheckPositionRequest`; close the connection on LOGOFF; one build per constructor; restore the suicide gate.
6. **Config safety (H-16, H-17):** refuse admin/harness features on a public bind; `IsFinite` checks.
7. The rest (H-23, H-25, H-28, H-30 through H-38).

## 6. Probe reproduction

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
# build the worktree once
dotnet build /home/domingo/allegiance-codex-analysis/src/Allegiance.Server/Allegiance.Server.csproj -m:2
# decoder fuzz (all 43 client→server decoders) and the CRC/orientation micro-probe
cd /home/domingo/.claude/jobs/82c1509b/tmp/input-audit/DecoderFuzz && dotnet build -m:2 && dotnet run --no-build -- 20000 && dotnet run --no-build -- crc
# in-process server probes (loopback only; real artwork at /home/domingo/Development/Artwork)
cd ../ServerProbe && dotnet build -m:2
for p in relay teleport slowloris buoy money loadout lobby ships board amplify defect; do dotnet run --no-build -- $p 2>&1 >/dev/null | grep -v '^\['; done
```

The probe assembly is named `livecombat` so that it can use the existing `InternalsVisibleTo` for the `_testSendCapture` seam. It calls `Server.ProcessDatagram` through reflection, on the same thread that drives `TickSim`.

Docked preconditions:
- **H-12:** reached legitimately, with two SUICIDEs giving a docked lifepod.
- **H-03 / probe `board`:** set as `ServerHost.DockWithStationEvent` sets it, `ship.StationId = station` (`ServerHost.cs:871`). The test hulls at the starting station are not sellable without techs.
