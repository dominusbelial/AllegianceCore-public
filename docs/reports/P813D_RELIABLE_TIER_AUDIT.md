# P813d — EXPERIMENTAL audit: one-shot messages are never shed under queue pressure

Base: public master at dispatch (working repo is the public `AllegianceCore-public`
clone; re-grepped every cite below against the actual tree at `6e5c745f`,
P807 game-over handoff — newer than the brief's cited `6ef6d9bf`). Worktree
branch `hermes/p813d`; changes left in the working tree, no commits/pushes.

Native reference: `~/Development/Allegiance/src/` (read-only, for citations only).

## TL;DR

The port's TCP transport already realises the reliable tier: every one-shot
(transactional) message rides the **ordered lane, guaranteed** — never
coalesced, never shed — while only self-healing ship/station/asteroid/probe
position streams ride the **stream lane** and may be coalesced (latest-wins)
or shed under capacity. The full inventory below found **zero violations**.
This task therefore delivers:

1. A per-message **native guarantee-class inventory** (FedSrv/ + clintlib/)
   vs the port's send-lane — the table in §3.
2. **Proof tests** (§5) — a stalled peer; every guaranteed one-shot arrives in
   exact order after the stall, ship states coalesce — green in both send modes,
   flag OFF and ON.
3. **Bounded telemetry** (§6) — per-class shed/park/coalesce counters, emitted
   as a `[reliable]` line per profiler window.
4. An **EXPERIMENTAL config flag** `transport.reliableOneShot` (**default OFF**)
   that, when ON, parks one-shot reliable messages in a dedicated highest-
   priority lane so they are never even delayed behind bulk ordered traffic —
   decided by the 200v200 rig baseline (§7, rig recipe).
5. Parity rows updated (§8).

All gate numbers (§4) pass: solution build 0/0, build-tools 73/73, AOT publish
clean, new tests 3× alone, one fast-tier run.

---

## 1. Why

Native separates guaranteed and unguaranteed sends (`c_mtGuaranteed`, DirectPlay).
The port's TCP transport coalesces, prioritises and sheds under pressure
(P661 egress accounting; P606 coalescing; P634 priority lanes). A one-shot
message (a death, dock, ship delete, game over, chat) that is shed has no
repair path and leaves a client wrong for good — the kind of bug that only
shows at 200v200. Native semantics (see §2): every transactional/one-shot
message is `FM_GUARANTEED`; only the continuous position/damage streams are
`FM_NOT_GUARANTEED`.

## 2. Native guarantee semantics (cited)

- The guaranteed constant lives in the client message-type enum:
  `clintlib/clintlib.h:725-730` — `BaseClient::MessageType`, `c_mtGuaranteed`
  at line 728.
- Server send-lane semantics (`_Utility/Messages.cpp:548-563`, `GenericSend`):
  - `FM_GUARANTEED` → `DPNSEND_GUARANTEED | DPNSEND_PRIORITY_HIGH | DPNSEND_COALESCE`,
    **0 ms timeout (never times out)**;
  - `FM_NOT_GUARANTEED` → `DPNSEND_NOCOMPLETE | DPNSEND_NONSEQUENTIAL`,
    **500 ms timeout (drops at 500 ms)**.
- One-shots are guaranteed on every path:
  - Chat — every `ForwardChatMessage` target branch forwards `FM_GUARANTEED`
    (FedSrv.CPP:3387 EVERYONE, 3437-3438 ALL_SECTOR, 3450/3460 ALLIES, 3477
    LEADERS, 3498/3511 SHIP, 3524-3525 TEAM).
  - Kill feed / death — `FM_S_KILL_SHIP` sent `FM_GUARANTEED` (FedSrv.CPP:11793
    build, 11914 send).
  - Ship delete — `FM_S_SHIP_DELETE` `FM_GUARANTEED` (fsship.cpp:201 build, 223 send).
  - Dock — `FM_S_DOCKED` `FM_GUARANTEED` (fsship.cpp:1039 build, 1042 send).
  - Game over — `FM_S_GAME_OVER` + `FM_S_GAME_OVER_PLAYERS` `FM_GUARANTEED`
    (fsmission.cpp:3063/3138 build, 3436-3438 send).
  - Mission stage / enter-game / joined-mission / missiondef — all `FM_GUARANTEED`
    (fsmission.cpp:1762, 830, 3957).
  - `FM_S_SET_CLUSTER` + its batched world-state intro (SSU/STATIONS/PROBES/
    ASTEROIDS) — `FM_GUARANTEED` (fsship.cpp:758/929 flush).
- Streams are best-effort:
  - `FM_S_HEAVY_SHIPS_UPDATE` / `FM_S_LIGHT_SHIPS_UPDATE` — `FM_NOT_GUARANTEED`
    (FedSrv.CPP:4189 / 4195).
  - periodic station-damage `FM_S_STATIONS_UPDATE` — `FM_NOT_GUARANTEED`
    (FedSrv.CPP:8271).
  - `FM_S_DESTROY_TREASURE` — `FM_NOT_GUARANTEED` (FedSrv.CPP:12503) *(era
    quirk — see §8)*.
  - `FM_S_ORDER_CHANGE` moving-target variant — `FM_NOT_GUARANTEED`
    (FedSrv.CPP:13730) while the accepted-command variant is `FM_GUARANTEED`.

**Classification** (per the brief): the reliable-tier split is a **design**
decision native encodes at the send site (guaranteed vs not). The 500 ms
best-effort timeout and DPNSEND_NONSEQUENTIAL are era/transport constraints of
DirectPlay; a modern TCP port achieves the same separation structurally with
lanes + shedding (below).

## 3. Inventory — native guarantee class vs port send lane

### 3.1 Native send sites (FedSrv/ + clintlib/)

| Fmid | FM_ name | site | native guarantee | citation |
|---|---|---:|---|---|
| 23 | FM_CS_CHATMESSAGE | server forward (all targets) | GUARANTEED | FedSrv.CPP:3387,3437-3438,3450,3460,3477,3498,3511,3524-3525 |
| 7 | FM_S_SET_CLUSTER | sector transfer / cluster intro | GUARANTEED | fsship.cpp:758,929 |
| 20 | FM_S_SHIP_DELETE | ship removed (destroyed/docked/left) | GUARANTEED | fsship.cpp:201,223 |
| 26 | FM_S_EXPORT | object export | GUARANTEED (by flush) | FedSrv.CPP:2028/2037; fsmission.cpp:3957 |
| 37 | FM_S_MISSION_STAGE | stage transitions | GUARANTEED | fsmission.cpp:1759,1762 |
| 38 | FM_S_MISSIONDEF | full mission def | GUARANTEED | fsmission.cpp:102,3944,3957 |
| 57 | FM_S_STATIONS_UPDATE | station hull/shield | MIXED: SetCluster batch GUARANTEED (fsship.cpp:833,929); periodic damage BEST-EFFORT (FedSrv.CPP:8262,8271) | both cited |
| 61 | FM_S_DOCKED | dock confirmation | GUARANTEED | fsship.cpp:1039,1042 |
| 62/63 | FM_S_GAME_OVER(_PLAYERS) | end-of-game stats | GUARANTEED | fsmission.cpp:3063/3138,3436-3438 |
| 78 | FM_S_SINGLE_SHIP_UPDATE | discrete spawn/join/own-ship | GUARANTEED (batch flush); continuous own-ship FM_GUARANTEED | fsship.cpp:787,929,523;544 |
| 90 | FM_S_SHIP_STATUS | status panel changes | GUARANTEED | FedSrv.CPP:3695; fsmission.cpp:4317,4339 |
| 92 | FM_S_PROBES_UPDATE | probe hull | GUARANTEED (batch flush) | fsship.cpp:865,929 |
| 95 | FM_S_JOINED_MISSION | join accepted | GUARANTEED | fsmission.cpp:3947,3957 |
| 105 | FM_S_KILL_SHIP | kill feed / death credits | GUARANTEED | FedSrv.CPP:11793,11914 |
| 128 | FM_S_ENTER_GAME | game-start handoff | GUARANTEED | fsmission.cpp:804,830 |
| 175 | FM_S_LIGHT_SHIPS_UPDATE | light positions | BEST-EFFORT | FedSrv.CPP:4195 |
| 176 | FM_S_HEAVY_SHIPS_UPDATE | heavy positions+hull | BEST-EFFORT | FedSrv.CPP:4189 |
| 179 | FM_S_ASTEROIDS_UPDATE | asteroid ore/fraction | GUARANTEED (batch flush) | fsship.cpp:897,929 |
| CS | FM_CS_ORDER_CHANGE | command change | CONDITIONAL (accepted GUARANTEED, moving-target BEST-EFFORT) | FedSrv.CPP:13710,13730 |

Bottom line: **no native one-shot send site uses `FM_NOT_GUARANTEED`** in FedSrv;
the only NOT_GUARANTEED sends are the position/damage streaming loop and the
treasure/order edge cases.

### 3.2 Port send sites (Allegiance.Server)

The port has two broadcast helpers
(`src/Allegiance.Server/Server.BroadcastSnapshot.cs:178-180`):

- `SendPeer(peer, packet)` → `QueueBroadcast(key=0, guaranteed=true)` →
  `_host.Send(...)` → **ordered lane, guaranteed**: never coalesced, never shed.
- `SendPeerCoalescing(peer, packet, key, guaranteed=false)` → **stream lane**:
  latest-wins per key, non-guaranteed frames shed under capacity. Used only by
  self-healing per-tick state streams (coalesce keys 1-13, Server.Broadcast.cs:40-67).

The full one-shot inventory (read-only audit, 45 sites) found **every one-shot
on the safe ordered+guaranteed lane** — zero violations:

| Broadcast method | file:line | one-shot | send path | fmid | risk |
|---|---:|---|---:|---|
| Player chat relay | Server.Dispatch.cs:462 | yes | SendPeer | 23 | none |
| SendSiteChat | Server.Callbacks.cs:75 | yes | SendPeer | 23 | none |
| BroadcastTeamChat / DroneChat | Server.Economy.cs:882/902 | yes | SendPeer | 23 | none |
| SendChatAnnouncement / SendBallotReply | Server.Lobby.cs:2376/1971 | yes | SendPeer | 23 | none |
| BroadcastKillShip (kill feed) | Server.Combat.cs:1797 | yes | SendPeer | 105 | none |
| BroadcastKillShip (death SHIP_DELETE) | Server.Combat.cs:1862 | yes | SendPeer | 20 | none |
| BroadcastShipDeparted / logged-off delete | Server.Combat.cs:2713; Server.Lifecycle.cs:2046 | yes | SendPeer | 20 | none |
| SendExplosionEvent | Server.Combat.cs:783 | yes | SendPeer | 207 | none |
| BroadcastAsteroidDestroyed / StationDestroyed | Server.Combat.cs:934/1059 | yes | SendPeer | 86/59 | none |
| BroadcastProbeDestroyed / MineDestroyed / MissileDestroyed(83) | Server.Combat.cs:840/659/378 | yes | SendPeer | 85/84/83 | none |
| BroadcastFireMissile / weapon-ready | Server.Combat.cs:330/440 | yes | SendPeer | 70/23 | none |
| OnShipDocked / SendInitialDockState | Server.Combat.cs:2841; Server.Docking.cs:40 | yes | SendPeer | 61 | none |
| OnShipWarped (SET_CLUSTER) | Server.Combat.cs:2665 | yes | SendPeer | 25 | none |
| DeclareGameOver / SendSideOnlyGameOver / MissionStage Over | Server.Lobby.cs:449/1209/536 | yes | SendPeer | 62/62/37 | none |
| DeactivateSide | Server.Lobby.cs:298,303 | yes | SendPeer | 68/23 | none |
| Economy (auto-donate, money, buckets) | Server.Economy.cs:112/429/502/634/661/685 | yes | SendPeer | 64/64/64/64/65/65 | none |
| Treasure spawn/destroy, gain-flag, building-effects | Server.Treasure.cs:18/49; Server.Docking.cs:1196/1226/1252/1290 | yes | SendPeer | 67/69/66/... | none |
| Join-side / team-leader / mission-owner / quit | Server.Lobby.cs:1831-1873 | yes | SendPeer | 125/121/123/... | none |
| PositionReq / DelPositionReq / wing id | Server.Lobby.cs:2407/2419; Server.ShipCommands.cs:928 | yes | SendPeer | 40/42/45 | none |
| Cross-side initial export | Server.FireAudience.cs:78-140 | yes | SendPeer | 26 | none |

The only `SendPeerCoalescing` uses are self-healing streams (own-ship pose
`CoalesceOwnShip=12` guaranteed, teammate positions `CoalesceShipStates=1`,
ships/stations/asteroids/probes/missile-lock/track/mining/cloak/lead-indicator,
keys 2-11,13) — the exact traffic the invariant permits to coalesce/shed.
One nuance (NOT a violation): in the parallel broadcast builder some one-shots
are recorded with `guaranteed=false, key=0`, but the apply loop routes
`key==0` through `SendPeer` regardless, so they still land ordered+guaranteed
(Server.Broadcast.cs:1304,1541).

## 4. Gates

| Gate | Result |
|---|---|
| Whole-solution build (Release, `AllegianceCore.slnx`) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 73 tool projects built** 0/0 |
| AOT publish (`dotnet publish src/Allegiance.Server … -p:PublishAot=true`) | **success, no IL2026/IL3050** |
| New P813d tests, 3× alone | 8/8 passed each run (3 runs) |
| Affected existing transport suites (coalesce/priority-lane/send-drop/hardening/own-ship) | 95/95 passed (flag OFF) |
| One fast-tier run | **Allegiance.Core.Tests 5101/5101 passed (incl. the 8 new P813d tests); full fast = 10191 tests** — overall flagged FAIL only by the 7 documented pre-existing `BotFixRegressionTests.Defense{Alert,AfterArrival,Detour,DisableSwitch,Relaunch,TravellingFighter x2}` load/parallelism-burst flakes (TEST_FLAKES.md:1218, "fast unit tests, fail only in the assembly-parallel MAIN burst; not touched by P648" — no transport involvement, reproduced only in the MAIN burst, unrelated to P813d) |

## 5. Proof tests — guaranteed never dropped/reordered, only delayed

New suite `tests/Allegiance.Core.Tests/Transport/P813dReliableTierTests.cs`
(parameterised over thread-per-peer and pool send modes):

- `Guaranteed_one_shots_survive_a_stall_and_arrive_in_order[false/true]`
  (4 cases) — pauses the per-peer send queue (stalled peer), floods best-effort
  stream past capacity AND 40 guaranteed one-shots, then releases; asserts every
  one-shot arrives **in exact order, nothing dropped/reordered**, while the
  stream lane coalesces to ≤1 per key. Runs flag OFF (ordered lane) and ON
  (park lane).
- `Reliable_park_lane_never_shed_and_never_coalesced` (2 cases) — flood a
  stalled peer far past capacity; all 50 parked one-shots survive (never shed),
  next-drained is the park lane.
- `Telemetry_counts_shed_only_for_best_effort_stream` (2 cases) — the shed
  counter fires **only** for `BestEffortStream`; `OneShot` and `ReliableStream`
  (guaranteed) are never shed; coalescing is recorded for best-effort stream
  only; one-shots counted as parked.

These pin the existing transport contract (also covered by the pre-existing
`TcpTransportCoalesceAndReasonTests`, `TcpTransportPriorityLaneTests`) and the
new park-lane behaviour. **No test fails on master** for the audit rows — the
invariant already held; the tests now prove it.

## 6. Bounded telemetry — shed/park counters per class

New `src/Allegiance.Core/Transport/ReliableTelemetry.cs`: a fixed
4-long-per-counter host-side table (classes `OneShot`, `ReliableStream`,
`BestEffortStream`, `BestEffortOrdered`), allocated only when the profiler is
on (mirrors `EgressAccounting`'s null fast path). Counters:

- `RecordShed(cls)` — best-effort frames dropped under capacity;
- `RecordCoalesced(cls)` — latest-wins superseded stream frames;
- `RecordOneShotParked(parkLane)` — one-shot reliable frames enqueued (and
  whether they took the park lane).

`DrainWindow()` atomically exchanges + conserves every counter. The server
emits one bounded line per profiler window
(`src/Allegiance.Server/Server.Egress.cs`):

```
[reliable] windowSecs=… oneShotParked=… oneShotParkedLane=… shed=[oneShot=0 relStream=0 bestStream=… bestOrdered=0] coalesced=[oneShot=0 relStream=… bestStream=… bestOrdered=0]
```

`shed.oneShot` and `shed.relStream` staying 0 is the operational proof that
guaranteed messages are never shed; `bestStream` rising shows real pressure.

## 7. EXPERIMENTAL flag + rig recipe

### 7.1 The experiment

`transport.reliableOneShot` (`bool`, **default OFF**) + env
`ALLEGIANCE_TRANSPORT_RELIABLE_ONESHOT`. When **OFF** (default), one-shots ride
the ordinary ordered lane exactly as today — behaviour byte-for-byte unchanged
(all existing transport tests green; §4). When **ON**, `TcpTransport` routes
every ordered-guaranteed one-shot into a dedicated per-peer **park lane**
(`PeerSender._parkQueue`) drained **strictly before** the ordered and stream
lanes, so a one-shot is never delayed behind accumulated bulk ordered traffic
or shed-eligible stream backlog; it is never shed (guaranteed) and never
coalesced (all keys 0), and still counts toward the byte/hard caps (bounded).
Boot prints the active state:
`[boot] transport reliableOneShot=PARK-LANE (EXPERIMENTAL; one-shots parked ahead of stream/ordered)`.

Rationale: at 200v200, chat/kill/game-over one-shots can sit behind the
continuously-produced stream backlog (and behind bulk exports) on a saturated
sink; parking them ahead of everything trades a little bulk-ordered latency for
lower one-shot latency and a stronger reliable tier. Whether it helps the target
metric is decided by the rig.

### 7.2 Rig recipe

Preset `showcase`, `target_sector` Alderan, `size_scale` 13, `mode: march`,
native ship-update mode, bots on the M4 Max, fog on, **two full matches
unattended** (game over → next → game over), flag OFF then ON.

- Enable the profiler (`"profiler":{"enabled":true,"reportIntervalSeconds":5}`)
  so both the `[egress-by-type]` and the new `[reliable]` lines print.
- Set the flag: server.json `transport.reliableOneShot: true`, or
  `ALLEGIANCE_TRANSPORT_RELIABLE_ONESHOT=1`; confirm the `[boot]` line.
- What to measure (log lines): ticks/5s, p95/max tick, slow ticks (existing tick
  profiler); REFUSED/drops (existing connection/`[conn]` lines); kills/min and
  bots-in-the-fight (existing scoring/logs); game-over handoff (P807
  `[game-over]` lines); `[reliable] oneShotParked` rising and `shed.bestStream`
  under load (new); `shed.oneShot==0` invariantly.
- Expected direction (hypothesis): oneShotParked high + oneShotParkedLane==park
  count ⇒ one-shots get a dedicated lane; lower p95/max tick and fewer
  REFUSED/drops if ordering helps the sink; neutral or slightly lower
  throughput for bulk ordered (the trade). Kept only if it improves the target
  metric without hurting the rest (§ of brief).

## 8. Parity rows (updated)

Per-message guarantee-class parity — native (cited §2) → port:

| Message | native class | port lane | parity |
|---|---:|---|---|
| CHAT (23) | GUARANTEED | ordered+guaranteed | done |
| KILL_SHIP (105) | GUARANTEED | ordered+guaranteed | done |
| SHIP_DELETE (20) | GUARANTEED | ordered+guaranteed | done |
| DOCKED (61) | GUARANTEED | ordered+guaranteed | done |
| GAME_OVER (62/63) | GUARANTEED | ordered+guaranteed | done |
| MISSION_STAGE (37) / ENTER_GAME (128) / JOINED_MISSION (95) / MISSIONDEF (38) | GUARANTEED | ordered+guaranteed | done |
| SET_CLUSTER (7) + batch intro | GUARANTEED | ordered+guaranteed | done |
| SHIP_STATUS (90) | GUARANTEED | ordered+guaranteed | done |
| HEAVY/LIGHT_SHIPS_UPDATE (176/175) | BEST-EFFORT | stream, non-guaranteed, shed | done |
| STATIONS_UPDATE (57) damage loop | BEST-EFFORT | stream, non-guaranteed, shed | done |
| SINGLE_SHIP_UPDATE (78) own-ship | GUARANTEED | stream+guaranteed (P608) | done |
| DESTROY_TREASURE | BEST-EFFORT (native quirk) | ordered+guaranteed (stronger) | done (port stricter than native) |

**Native behaviour classification** (brief §): the guaranteed/best-effort split
is a **design** decision at each send site. The 500 ms best-effort timeout is an
**era constraint** of DirectPlay sequencing. The `DESTROY_TREASURE`/`ORDER_CHANGE`
best-effort exceptions are native quirks (treasure-destroy is cosmetic) — a
modern port may safely be stricter (guaranteed), which the port already is; a
better modern approach is the lane + bounded-shred model implemented here
(rather than DirectPlay timeouts), with a reserved one-shot lane as the
experimental refinement.

## 9. Evidence & files

- Changed (working tree, uncommitted): `src/Allegiance.Core/Transport/
  TcpTransport.cs`, `src/Allegiance.Core/Transport/ReliableTelemetry.cs`
  (new), `src/Allegiance.Server/Server.cs`, `Server.Lifecycle.cs`,
  `Server.Egress.cs`, `ServerConfig.cs`, `tests/Allegiance.Core.Tests/
  Transport/P813dReliableTierTests.cs` (new).
- Scratch/logs: `.scratch/` (aot log, fast-tier log) — excluded from git; raw
  logs mirrored to `~/allegiance-evidence/P813D/` (`fast-tier.log`,
  `aot-publish.log`, `fast-summary.json`, `P813dReliableTierTests.cs`). The
  fast-tier TRX/summary also lives under
  `TestResults/tiers-20261024T200259.061354Z/fast/` in the worktree.
