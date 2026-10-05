# P532 — P1–P200 wire-message semantics audit (FEDMSG ports)

Date: 2026-10-05. Read-only audit. Native = `~/Development/Allegiance/src` (C++),
port = `~/Development/AllegianceCore/src` (C#). Every non-MATCH row was verified
by reading source on both sides; `Igc/` ISO-8859 files were searched with
`LC_ALL=C grep -a`. Input scope: the 84 commits in
`docs/audits/p1-p200/wire.txt`.

Focus per the brief: (1) are FEDMSG fields filled with the real values native's
sender puts there, or zeros/defaults/guesses; (2) is each sent at the right
moment/cadence to the right recipients; (3) does the client consume it the way
native's `clintlib/appmsg.cpp` handler does. This slice is about **semantics**
— byte layout is unit-tested against `Messages.h` elsewhere.

## Headline finding

The port's wire layer is far more mature than the 2026-09-29 parity baseline and
most items **MATCH**. The great majority of FEDMSG codecs carry real sim values
(side ids, hull/station/ship ids, money deltas, armour/hull fractions, sector
ids, part data, timestamps) and are sent at the right moment/cadence/recipients;
the sender-side seams routinely cite the exact native call site they mirror
(e.g. `FedSrv.CPP:6110-6116` for AddPart's granted quantity, `FedSrv.CPP:2312-
2316` for a whole-side LEAVE_SHIP, `GetGroupRealSides()` for WARP_BOMB). Field
values that are zeroed are almost always **documented as genuinely unused by the
flow that reads the message** (native's own handler ignores them), not guessed —
see the per-row notes below.

The genuine divergences are few and cluster around: (1) the **HEAVY_SHIPS_UPDATE
`CompactShipFractions`** ammo/fuel/energy/power hardcoded to "full" where native
applies the player's own real fractions (`appmsg.cpp:801 ProcessFractions`);
(2) **LIGHT_SHIPS_UPDATE never sent** (only HEAVY, bandwidth cut); (3) the
**FIRE_MISSILE homing target placeholder** (`targetType=0/targetID=NA`) because
homing missiles are not yet ported; and (4) the systemic **same-side-ordnance /
no-fog-of-war stand-ins** in the visibility-determining fields. Several prior
parity gaps were found **already FIXED** (cross-side mine export by P453, mine
destructibility/endurance by P95/P96) — see Coverage.

---

## 1. Findings table

Verdict legend: **MATCH** = equivalent; **DIVERGES** = value/formula/behaviour
differs; **STAND-IN** = labelled/obvious placeholder where native data/formula
exists; **MISSING** = native behaviour absent; **UNPORTABLE** = can't apply.

### 1a. Connection / join / dock flow

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| FM_S_MISSIONDEF(38) static-core file + ver | appmsg.cpp:2416,2162-2163; clintlib.cpp:4212-4221 (`szIGCStaticFile`/`verIGCcore`) | Wire/MissionDef.cs:90-96,128-142 (real name+version+cookie; other params zeroed as unread) | MATCH | LOW | none — join-critical fields real, rest unread by the join path |
| FM_S_JOINED_MISSION(95) shipID+dwCookie | appmsg.cpp:2154-2215 (assert cookie, shipID tagging) | Wire/JoinedMission.cs:73,80,90 (real shipID+dwCookie; dtime/cookie 0, unread) | MATCH | LOW | none |
| FM_S_EXPORT(26) per sector (OT_cluster 13) | FedSrv (cluster export) | Server.Lobby.cs:3144 `ClusterInfo.FromSectors(...)`; export burst | MATCH | LOW | none — real sector data |
| FM_S_PLAYERINFO(10) iSide + shipID | appmsg.cpp:2286-2288 AddPlayerToSide; clintlib.cpp:4109 assert GetMoney()==0 | Wire/PlayerInfo.cs:140,144,160 (real iSide/shipID/name; money=0 asserted invariant; stats/rank/drones 0 unread) | MATCH | LOW | none — iSide is the real side carrier; money 0 is the native invariant, not a guess |
| FM_S_SET_CLUSTER(7) sectorID + ServerShipUpdate | Messages.h:83-87; FedSrv.CPP:902-928 | Wire/SetCluster.cs:36,42 (real sector + full ship update; **packed-layout wire bug P36 fixed**) | MATCH | LOW | none |
| FM_S_BUY_LOADOUT_ACK(5) hull + parts | appmsg.cpp:3176-3221 ProcessShipLoadout | Wire/BuyLoadoutAck.cs:95-98,117-122 (real hullID + parts; fBoughtEverything/fLaunch honestly false) | MATCH | LOW | none — flags are honest, not guessed |
| FM_S_DOCKED(61) on dock | native ship.GetStation() trigger | Server.Docking.cs (P54 real dock → FM_S_DOCKED) | MATCH | LOW | none |
| FM_S_SHIP_STATUS(90) state fields | fsship.cpp:376 (ParentID=NA docked); FedSrv.CPP:2453-2454 (parent/hull on board) | Server.Broadcast.cs:828-863 `MakeOwnShipStatus` (real hullId/sectorId/stationId/parentId/state) | MATCH | LOW | none |
| SHIP_STATUS Unknown/Detected/StateTime scanner fields | fsship ShipStatusSpotted/Hidden (scanner) | Server.Broadcast.cs:859-861 (`Unknown=false`,`Detected=true`,`StateTime=0`) | STAND-IN | MED | gate by scanner once fog-of-war exists; real StateTime source |
| SHIP_STATUS cadence (sector change / every `StatusRefreshTicks`) | fsmission.cpp:4303-4323 per-player loop | Server.Broadcast.cs:490-500; Server.cs `StatusRefreshTicks` | MATCH | LOW | none |
| FM_CS_CHATMESSAGE(23) routing | clintlib chat targets | Server.cs SelectChatTargets (Everyone/Team/FriendlySector/Group/Individual) | MATCH | LOW | none |

### 1b. Phase 2 combat / projectiles / mines / probes

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| FM_CS_PROJECTILE_INSTANCE(12) fields | projectileigc.cpp / weaponIGC.cpp fire | Wire/ProjectileInstance.cs:20-24 (real type/forward/vel/lifespan/pos/time) | MATCH | LOW | **alignment assumption (leading short→Vector pad) is unproven** — verify on a live wire probe |
| Projectile / missile / mine / probe **same-side recipients** | native broadcasts by scanner LOS | Server.Combat.cs:309-324 (same-side peer gate) | STAND-IN (systemic) | MED | recipient gate by scanner visibility once fog-of-war exists |
| FIRE_MISSILE(70) homing target | Messages.h:547-557 (real `lock`/`targetType`/`targetID`) | Server.Combat.cs:291-297 (`Lock=0f`,`TargetType=0`,`TargetID=-1`) | STAND-IN | LOW-MED | populate homing target once homing missiles are ported |
| FIRE_MISSILE ballistics (pos/forward/vel/id/time) | missileigc.cpp | Server.Combat.cs:289-305 real MissileLaunchData | MATCH | LOW | none |
| WARP_BOMB(188) whole-mission + delay | FedSrv.CPP:12558 GetGroupRealSides; 15 s delay | Server.Combat.cs:824-844 (whole-mission, timeExplosion=future tick) | MATCH | LOW | none |
| ASTEROID_DESTROYED(86) | shipIGC / FedSrv | Server.Combat.cs:885-887 real asteroid id | MATCH | LOW | none |
| SHIP_DELETE(20) reasons | Messages.h:188-199; fsship.cpp:196-226 AnnounceExit | Wire/ShipDelete.cs:30-40 (SDR_KILLED/LOGGEDOFF/LEFTSECTOR/DOCKED real; OUTOFRANGE/TERMINATE/SUPERCEDED not constructible) | MATCH (partial) | LOW | map the 3 unmapped reasons when range/terminate mechanisms exist |
| OBJECT_SPOTTED(6) | native fired on hidden→visible | **never sent** (no fog-of-war) | UNPORTABLE | LOW | nothing to spot without scanner visibility |
| PLAYER_RESCUED(16) real trigger | FedSrv.CPP:13033-13074 RescueShipEvent | Server.Docking.cs:706-708 (P115 real, both ships' connections) | MATCH | LOW | none (was wire-only P104, now triggered) |
| ENTER_LIFEPOD(166) | FedSrv eject | Server.Combat.cs (P105 auto-respawn) | MATCH | LOW | none |
| VIEW_CLUSTER(104/177) request-reply | FedSrv | Server.ShipCommands.cs:102-105 real cluster/otarget | MATCH | LOW | none |
| BOARD/BOARD_NACK/LEAVE_SHIP(103/178/94) | FedSrv.CPP:6412-6416 NACK; :2312-2316 whole-side LEAVE | Server.Docking.cs:1707-1711; 253-260 (real ids, whole-side leave) | MATCH | LOW | none |
| TELEPORT_ACK(102) | FedSrv teleport | Server.Docking.cs:1993-2021 (real stationId/bNewHull, rejects on invalid) | MATCH | LOW | none |
| RIPCORD(184/185/186/187) | FedSrv.CPP:13427-13500 | Server.Docking.cs:414-435, Broadcast.cs ripcord stages (real anchors, DENIED requester-only) | MATCH | LOW | none |

### 1c. Economy / team / stations / rank

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| PAYDAY(114) delta, side-scoped | fsmission.cpp:2307 (side broadcast) | Server.Economy.cs:1106-1111 (real delta) | MATCH | LOW | none |
| MONEY_CHANGE(60)/SET_MONEY(115) | FedSrv.CPP:2435-2441 (sidFrom=NA server), :7202-7206 | Server.Economy.cs:377-441,485-488,561-590 (real deltas, sidFrom semantics) | MATCH | LOW | none |
| BUCKET_DONATE/STATUS/CREATE(64/65/191) | FedSrv / bucketigc.cpp | Server.Economy.cs:613-617 (real invested/price/time) | MATCH | LOW | none |
| ASTEROID_MINED(198) whole-mission | MineAsteroidEvent | Server (P120 real-time ore broadcast) | MATCH | LOW | none |
| SET_BRIEFING_TEXT(9) | fsmission join | Server.Lobby.cs:3076-3078 (skip when no friendly station — real gate) | MATCH | LOW | none |
| TREASURE family (28/30/162/163) cash | FedSrv.CPP:13589-13616 HitTreasureEvent | Server.Broadcast.cs:158-171 (real cash + whole-mission DESTROY_TREASURE) | MATCH | LOW | none |
| TREASURE_SETS(89) | Messages.h:624-628 (modern FedSrv never emits, legacy) | Server.Lobby.cs:3095-3099 (documented legacy, sent per task) | MATCH | LOW | none |
| STATION_DESTROYED(59) | FedSrv.CPP:12278 (launcher or NA) | Wire/StationDestroyed.cs:41-44; Station.ReceiveDamage P125 | MATCH | LOW | none |
| STATION_CAPTURE(58) | FedSrv / stationIGC capture | Server (P126 real sidOld/sidNew/shipIDCredit) | MATCH | LOW | none |
| RANK_INFO(13) ranks.txt | FedSrv.CPP:103-150 LoadRankInfo **standalone branch** :140-141 (requiredRanking=0,civ=-1) | Wire/RankInfo.cs:134-153 FromRanksFile (real names, requiredRanking=0/civ=-1 faithful to standalone) | MATCH | LOW | none — faithful to the standalone build shape, not a simplification |
| PLAYER_DONATE/AUTODONATE/REQUEST_MONEY(67/112/116/182) | FedSrv | Server.Economy.cs:93-95,285-289 (P199 real forwarding) | MATCH | LOW | none |
| SIDE_TECH_CHANGE(75)/SIDE_ATTRIBUTE_CHANGE(76) | Messages.h:565-572 | Server.Economy.cs:1015-1052 (real dev mask + GA set) | MATCH | LOW | none (P446/P467 now make sim read GA) |

### 1d. Visibility / per-object updates

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| STATIONS_UPDATE(57)/ASTEROIDS_UPDATE(179)/PROBES_UPDATE(92) | native per-side periodic sync | Server.Broadcast.cs:254-269 (per-side, every tick, real hull/shield fractions) | MATCH | LOW | none |
| **HEAVY_SHIPS_UPDATE(176) `CompactShipFractions` ammo/fuel/energy/power** | appmsg.cpp:801 `ProcessFractions(pfmHeavy->fractions)` — player's **own** real fractions | Server.Broadcast.cs:1892-1894 (`FractionsAmmo=Fuel=Energy=240`); Wire/HeavyShipsUpdate.cs:148-150,152 (`Percent(1.0f)`) | **DIVERGES** | MED | fill the four resource fractions from the receiver's own live ship |
| HEAVY_SHIPS_UPDATE hull/shield/ore fractions | appmsg.cpp:753-781 (bpTargetHull/Shield) | Server.Broadcast.cs:1890-1895 (real self hull/shield/ore) | MATCH | LOW | none |
| **LIGHT_SHIPS_UPDATE(175) never sent** | appmsg.cpp:684-716 handler exists, native sends both tiers | Server.Broadcast.cs:1898 (`LIGHT is never populated/sent`) | **DIVERGES** | LOW-MED | emit a light tier (or document the deliberate bandwidth cut) |
| ENTER_GAME(128) | fsmission | Server.Lobby.cs:3385-3387 (join-sync completion) | MATCH | LOW | none |
| HIGHLIGHT_CLUSTER(199) | FedSrv side relay | Server (P142 real sector → sender's side) | MATCH | LOW | none |
| SWAP_PART(34)/DROP_PART(33)/ADD_PART(164) | FedSrv.CPP:6311-6312 (group-sector drop), :6110-6116 (granted qty) | Server.ShipCommands.cs:392-399,595-602,2315-2319 (real part/mount/granted qty) | MATCH | LOW | none |
| SET_WINGID(106) side-wide | FedSrv | Server.ShipCommands.cs:920-921 (real wing tag) | MATCH | LOW | none |
| LOADOUT_CHANGE(96) | native export | LoadoutChange.cs:80 (real mount-order loadout) | MATCH | LOW | none |
| ORDER_CHANGE(93) stages | FedSrv command paths | Server.ShipCommands.cs:1842-1848, Server (P148-154 real cids) | MATCH | LOW | none |
| FIRE_EXPENDABLE(82)/chaff | FedSrv | Server.ShipCommands.cs:737-780 (real et + EXPORT) | MATCH | LOW | none |

### 1e. Endgame / meta

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| GAME_OVER(62)/GAME_OVER_PLAYERS(63) | fsmission.cpp:3094-3141 | Server.Lobby.cs:667-739 (real winner + per-player roster) | MATCH | LOW | none |
| BALLOT/CANCEL_BALLOT/VOTE(17/18/19) | fsmission.cpp:2585-2592 ballot loop | Server.Lobby.cs:1360-1361; Server.Broadcast.cs:80 (real per-mission pending-ballot) | MATCH | LOW | none |
| CHANGE_ALLIANCE(S)(196/197) | FedSrv UpdateAlliances | Server.Lobby.cs:2058-2059 (real per-side AllyGroup) | MATCH | LOW | none |
| GAME_STATE(155) conquest | FedSrv conquest | Server.Combat.cs:1154-1194 (real winner decl via DeclareGameOver) | MATCH | LOW | none |
| RELOAD(129)/cargo | FedSrv | Server.ShipCommands.cs (P192 real cargo-aware dispatch) | MATCH | LOW | none |
| RANDOMIZE_TEAMS(21) | fsmission | Server (P195 rank-balanced greedy rebalance) | MATCH | LOW | none |
| PINGDATA/REQPINGDATA(194/195) | FedSrv latency | Server.ShipCommands.cs:983-998 (real roster; server-marker entry ShipId=-1,Ping=1,Loss=0) | MATCH | LOW | none — marker entry is a benign roster sentinel |

---

## 2. Top fixes (ranked, sized as candidate P-tasks)

1. **HEAVY_SHIPS_UPDATE own-ship resource fractions** — DIVERGES, MED.
   Native applies the player's **own** ammo/fuel/energy/power to the receiver
   (`appmsg.cpp:801 ProcessFractions`); port hardcodes them to full
   (`Server.Broadcast.cs:1892-1894`, `Wire/HeavyShipsUpdate.cs:148-152`).
   *Slice:* read the receiver's `LiveShip` ammo/fuel/energy/power and clamp to
   `Percent(0..1)`; one before/after test. Players see their own resource bars
   reconcile correctly instead of snapping full on every HEAVY update.

2. **LIGHT_SHIPS_UPDATE tier** — DIVERGES, LOW-MED. Only HEAVY is sent
   (`Server.Broadcast.cs:1898`); native sends both
   (`appmsg.cpp:684-716`). *Slice:* budget distant ships into a light tier (or
   explicitly document the cut as a bandwidth decision in the codec header).

3. **FIRE_MISSILE homing target** — STAND-IN, LOW-MED. `targetType=0/targetID=
   NA/Lock=0` (`Server.Combat.cs:291-297`) because homing isn't ported.
   *Slice:* populate the fields from a real `TargetType`/`TargetId` once any
   homing (missile track/lock) exists; otherwise keep the honest placeholder.

4. **Systemic same-side ordnance recipient gate** — STAND-IN, MED. Projectile/
   missile/mine/probe broadcasts are same-side only
   (`Server.Combat.cs:309-324`); native gates by scanner LOS. Shared with the
   P531 SeenBySide/scanner stand-in — resolve once fog-of-war exists.

5. **SHIP_STATUS scanner fields** — STAND-IN, MED. `Unknown/Detected/StateTime`
   are fixed (`Server.Broadcast.cs:859-861`); native sets them from scanner
   state. Fold into the scanner work above.

---

## 3. Coverage (84 commits, by theme + depth)

All 84 commits were located (`git show --stat`), the message(s) each one
ports/sends were identified, and each was classified. Depth varies by risk:

- **Deep-verified on both sides** (native sender + `clintlib/appmsg.cpp`
  handler + port send site): join flow (P19/P25/P26/P27/P28/P29/P30/P31a/P33×2/
  P37/P40/P47), docking + ship status (P53/P54/P58/P61/P62/P63/P106), money +
  payday + rank + station (P51/P118/P122/P130/P125/P126), Phase-2 combat objects
  + deletes + warp bomb (P88/P89/P90/P93/P94/P98/P99/P101/P102/P103/P104/P105),
  part ops (P143/P144/P145), board/leave/turret/teleport/ripcord
  (P108/P109/P110/P111), visibility updates (P132/P133/P134/P135/P139), loadout
  change (P173).
- **Verified at codec + send-site level** (port carries real values, native
  anchors confirmed by reading `Messages.h`/`appmsg.cpp` but send-path details
  lighter): chat (P25/P33/P67), rescue/relaunch (P115/P117), buckets + treasure
  (P119/P123), briefing (P121), side-tech/attr + poster N/A (P124), logoff +
  zombie retire (P127/P128), mute/static-map/icq N/A (P129), enter/highlight/
  wing/order (P141/P142/P146/P147/P148/P150/P152/P153/P154), game-over + ballot
  + alliance + conquest + cargo + ping (P164/P178/P187/P190/P191/P192/P194/
  P195/P199/P200).
- **Prior 2026-09-29 parity / P531 items re-checked and re-classified:**
  FIXED since — cross-side MINE export (P531 KNOWN-OPEN → P453 latch,
  `Server.cs:258-263`), mine destructibility/endurance (P95/P96), ship/vs-station
  physics (P466), GA scaling to flight/weapons (P446/P467). STILL OPEN / STAND-IN
  — same-side SeenBySide / no-fog-of-war recipient gate (matches P531 row 1c),
  FIRE_MISSILE homing placeholder (matches P531 row 1c "FireMissile OT_*
  placeholder"), HeavyShipsUpdate resource fractions (NEW this pass), LIGHT tier
  (NEW this pass). The He3 mining stand-ins are KNOWN (being fixed by P528) and
  intentionally excluded — not wire-semantics items.

Nothing was modified; this report is the only change. Commit follows.

`git log -1` / `git branch --show-current` confirm the commit below.
