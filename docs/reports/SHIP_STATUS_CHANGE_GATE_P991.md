# P991 — SHIP_STATUS(90): native change gate instead of the periodic refresh (behind a default-off flag)

Base: Claude's local master at dispatch (`60390d68`, includes P983 broadcast telemetry and P990's per-`fmid`
attribution and per-`fmid`/per-side reasoning). Origin: P990 §2.1/§2.3/§6 candidate 0
(`docs/reports/BROADCAST_SLICE_D_P990.md`) — on the 400-bot rig the ordered one-shot lane is 47.7 % of egress and
`SHIP_STATUS(90)` is 58 % of that lane, most of it the port's **periodic refreshes**, which native does not have.

Status: complete in the working tree, **not committed**. New flag `broadcast.shipStatusChangeGate`, **default
off**; **off is byte-identical to the base commit** (pinned below, verified against a build of the base tree). On,
every `SHIP_STATUS` send becomes edge-triggered on the packed status content and a joining/reconnecting peer gets
native's per-ship status dump.

---

## 0. Answers in one paragraph

- **The native rule.** Native sends a ship's status to a side **only when it changed**: `SendShipUpdates` runs
  every other frame, walks every side × every ship, compares the side's `ShipStatus` with the copy it last sent
  (`if (*pss != *pssOld)`) and on a difference sends to the side group and copies the new value into the old one
  (`FedSrv/FedSrv.CPP:3665-3719`). The complete picture reaches a player once, at join, from
  `CFSMission::SendMissionInfo` ("send the data on all known ships", one `SHIP_STATUS` per ship of the mission
  whose side id is `>= 0`, carrying the joining side's copy — `FedSrv/fsmission.cpp:4303-4323`).
- **What the port did instead.** The same edges plus three periodic **refreshes** the flag now replaces: own ship
  every 40 ticks, same-side teammates every 100 ticks (per-recipient phase, P604b), cross-side seen enemies every
  100 ticks per pair (P493/P604b). On the 144-peer loopback fixture those refreshes are 260.7 `SHIP_STATUS`
  frames/tick with stable visibility, 408.4 with enemies crossing the scanner net; at the rig's 411 peers P990
  counts ≈ 1 700 frames/tick from them alone (O(P²)).
- **What the flag does.** One content value per **(side, subject)** — native's `m_rgShipStatus`/`pssOld` shape —
  compared once per subject per tick in the already-existing serial detection sweep (`SweepShipDetection`), before
  either builder runs. The serial and the parallel per-peer builders then only READ the resulting "changed this
  tick" set, so they stay byte-for-byte equal. Every existing edge (sector change, HUD-17 `Detected` flip,
  spotted/hidden, dock/launch, hull change, boarding, respawn/new id, side change) is a field of that content and
  therefore fires through the comparison.
- **Measured (largest local loopback fixture, rig baseline flags, P990 method, §5).** Stable visibility:
  `SHIP_STATUS` **260.7 → 51.8 frames/tick (−80.1 %)**, 7 298 → 1 452 B/tick, and the 51.8 remaining frames are
  exactly the one-time first delivery of every (recipient, subject) pair in the 400-tick run (144×71 same-side +
  144×72 cross-side + own ≈ 20 736 = 51.84×400) — **zero repeats**. Ordered lane −45.1 %, total egress −3.8 %,
  tick p95 7.97 → 7.99 ms (noise) and `step:broadcastShipStates` 0.864 → 0.794 ms. With visibility churn (enemies
  leaving/entering the scanner net every ~50 ticks — a worst case where every edge is real information)
  `SHIP_STATUS` still drops 408.4 → 226.8 frames/tick (−44.5 %), ordered lane −29.7 %, egress −3.2 %.
  The gate's own cost is **+0.011 ms/tick** at 144 ships (0.15 % of the tick), zero allocation, memory O(ships ×
  sides) = 288 entries at 144 ships.
- **Verdict for the rig.** Do it: it removes the O(P²) refresh rows of P990 §2.3 with no loss of information, and
  it is the prerequisite that makes the remaining slice-D candidates (warnings/mining/track/EXPORT) measurable
  against a quieter ordered lane.

---

## 1. The native rule, with citations (re-grepped on this tree's reference copy)

| Native rule | Where | What the port does |
|---|---|---|
| Ship updates go out on **odd frames only** (`nFrame % 2 == 0 → return`) | `FedSrv/FedSrv.CPP:3665-3666` | The port's cadence is per tick; P991 does not change cadence, it changes *which* statuses are built. |
| Per **side**, per **ship** in the mission, skip lobby ships (`GetSide()->GetObjectID() >= 0`) | `FedSrv/FedSrv.CPP:3676-3689` | Per (mission, side) grouping already exists in `BroadcastShipStates`; the gate uses the same population. |
| **Send only when the side's copy changed**: `ShipStatus* pss = pfsShip->GetShipStatus(sid); ShipStatus* pssOld = pfsShip->GetOldShipStatus(sid); if (*pss != *pssOld) { … pfmShipStatus->status = *pssOld = *pss; }` | `FedSrv/FedSrv.CPP:3690-3699` | `ShipStatusContent` equality per (side, subject), committed once per subject per tick (`Server.ShipStatusGate.cs`). |
| The send is addressed to the **side group**, `FM_GUARANTEED, FM_FLUSH` | `FedSrv/FedSrv.CPP:3719` | A changed copy is sent to every recipient of that side (`StatusGateCrossSide` → whole audience; own-side paths already walk per recipient). |
| One `ShipStatus` **per (ship, side)**, updated by events — `ShipStatusSpotted` (state/sector/hull/parent + the own side's `Detected` bit), `ShipStatusHidden` (unknown bit + `Detected` recompute), `ShipStatusDocked` (station/sector/state), `ShipStatusHullChange`, `ShipStatusStart/Launched/Exit/Restart` | `FedSrv/fsship.cpp:237-291`, `:293-340`, `:349-384`, `:405-435`, `:437-470`, `:471-521` | These are exactly the fields of `ShipStatusContent`, which is why every one of them fires through the comparison (see §3). `StateTime` is the one native field the port pins at 0 (`ShipStatus.cs:86,107`; "sim clock unused"), so it never forces a re-send. |
| A joining player gets the full picture in `CFSMission::SendMissionInfo`: "send the data on all known ships", every ship of the mission with a real side, the joining side's copy, *"We know something about the ship, even if it is obsolete info"* | `FedSrv/fsmission.cpp:4303-4323` (function at `:4108`; called at `:736` for a player, `:2031` for a whole side) | `Server.SendStatusDumpToJoiner` — see §2.2. |
| `SendMissionInfo` also runs for a whole side (`pfsPlayer == NULL` → the side group) | `FedSrv/fsmission.cpp:4106-4114`, `:2031` | Out of scope here (P991 dumps per joiner); noted for a later side-broadcast task. |

The port's own client-side status consumers (`ClientWorld.Apply(ShipStatusMessage)`,
`src/Allegiance.ClientCore/State/ClientWorld.cs:3198-3222`) are **idempotent** on a repeated identical status: they
upsert hull/sector/station/parent/state/unknown/detected and (re-)retire a sector pose. So a repeat carries no
information — which is the whole argument for removing it.

---

## 2. Every port send site, and the flag's decision

All four sites are in `src/Allegiance.Server/Server.Broadcast.cs` (serial) and its P603 parallel twin in the same
file; the cross-side site is a separate serial sweep that both builds share. The content builders live in
`src/Allegiance.Server/Server.ShipStatusGate.cs` and are the **single source** for the packed bytes: the
message path (`MakeOwnShipStatus`/`MakeNpcShipStatus`/`MakeCrossSideShipStatus`) and the pooled fast path
(`PackStatus`/`PackCrossSideStatus`, `Server.BroadcastBuffers.cs`) now serialise the same `ShipStatusContent`.

| # | Site | Flag OFF (today, untouched) | Flag ON |
|---|---|---|---|
| 1 | **Own ship** `Server.Broadcast.cs:866-869` (serial), `:1522-1525` (parallel) | `sectorChanged \|\| refreshDue(40 ticks) \|\| _shipDetectedChanged` | `_ownStatusChanged.Contains(ship.Id)` — the sector and the `Detected` bit are content fields |
| 2 | **Same-side teammate** `:1042-1045` (serial), `:1590-1593` (parallel) | `lastTeammateTick < 0 \|\| recipientRefreshDue(100, phase) \|\| _shipDetectedChanged` | `lastTeammateTick < 0 \|\| _ownStatusChanged.Contains(teammateId)` (the per-pair latch keeps the "first send for this pair" and the export-before-status ordering) |
| 3 | **Same-side NPC (P366)** `:1211-1214` (serial), `:1689-1692` (parallel) | `lastNpcTick < 0 \|\| recipientRefreshDue \|\| _shipDetectedChanged` | `lastNpcTick < 0 \|\| _ownStatusChanged.Contains(npc.Id)` |
| 4 | **Cross-side seen enemy (P493)** `SweepCrossSideShipStatuses` `:2528-2532` → `Server.ShipStatusGate.cs:270-318` | edge send + per-`(conn,ship)` phase refresh every 100 ticks | edge send (the `Unknown` bit flips); a changed side copy sent **once to the whole side**; a peer behind the side's committed copy (late joiner/reconnect/side switch) repaired by the same bounded phase rotation; a **hidden** ship sends nothing |
| 5 | **Join / reconnect dump** `Server.Lobby.cs:5520` → `Server.ShipStatusGate.cs:399` | (does not exist; the own-ship status only) | native `SendMissionInfo`'s "all known ships": own side (own-side copy) + every cross-side ship the joiner's side sees (spotted copy); own ship skipped and deliberately left unrecorded so the first tick re-sends it |

**Where the decision is made and why that matters.** The gate's per-subject decision is taken in
`SweepShipDetection` (`Server.Broadcast.cs:2384-2432`, serial, once per tick, the sweep that already recomputes
every own-side ship's `Detected` bit): `TrackOwnStatusContent`/`TrackNpcStatusContent`
(`Server.ShipStatusGate.cs:208-227`) build the content, compare it with `_lastStatusSentBySide[(side, id)]` and,
if it differs, commit and add the id to `_ownStatusChanged`. Both builders only **read** that set, so P603's
serial ≡ parallel property holds by construction (pinned by a new test).

**Cross-side detail (`StatusGateCrossSide`).** The port's cross-side content is per seeing side, not per recipient
(`PackCrossSideStatus(ship, sector, spotted)` depends only on the subject and the side's visibility), so the gate
keys it by `(seeingSide, subject)` exactly like native. On a still-visible ship whose copy changed (sector, hull,
dock state) the status goes to `conns` — the whole side (`GatherSideConnections`), native's side-group send. Peers
whose latch is older than the committed copy (`_crossSideShipStatusTick < SentTick`) are exactly the ones that
never received the current copy; they are found through the existing `CrossSideSweepCache.Recipients` phase
rotation, so a 400-peer sweep never scans every pair every tick — the same bound as the flag-off catch-up.

### 2.1 The flag

`BroadcastConfig.ShipStatusChangeGate` (`src/Allegiance.Server/ServerConfig.cs`), JSON
`broadcast.shipStatusChangeGate`, env `ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE` (env wins, strict grammar
`0/1/false/true`, anything else throws at boot — same shape as `broadcast.pendingStreamIndex`/`immutableSnapshot`).
Resolved and printed at boot in `Server.Lifecycle.cs:182-187` via `ResolveShipStatusChangeGate`:

```
[boot] broadcast.shipStatusChangeGate = false — periodic SHIP_STATUS(90) refreshes (own/teammate/cross-side) unchanged (today's wire)
[boot] broadcast.shipStatusChangeGate = true — SHIP_STATUS(90) is sent only when the side's copy changes (native FedSrv.CPP:3665-3719); the three periodic refreshes are off; a joining/reconnecting peer gets a full status dump
```

Also wired into `docker-compose.yml` + `docker/README.md` (the other experimental `broadcast.*`/`sim.*` switches
are listed there), and `docker compose config` validates.

### 2.2 The join/reconnect dump (native `SendMissionInfo` shape)

`SendStatusDumpToJoiner` (`Server.ShipStatusGate.cs:399-455`), called from `CompleteJoinAndLaunch`
(`Server.Lobby.cs:5509-5520`) right after the own-ship `SHIP_STATUS` the join burst already sends:

- **Which ships**: every ship of the joined mission with `SideId >= 0` (native's
  `pship->GetSide()->GetObjectID() >= 0` filter) that the joiner's side either owns (own-side copy: `LiveShip`
  sector for a peer ship, the ship's own sector for an NPC) or **currently sees** (`SeenBySide(joinSide)`, the
  cross-side spotted copy).
- **Unseen enemies are skipped on purpose**: native's per-side copy for a side that never saw a ship holds only
  defaults (`ShipStatusStart` writes the ship's own side's copy, `FedSrv/fsship.cpp:437-447`), so there is nothing
  to tell — and sending this port's last-known hull/sector would hand the side information it never had.
- **Own ship**: already sent by the join burst immediately above, so it is not in the dump and, deliberately,
  **not recorded** in the gate — the first broadcast tick re-sends it (one 24-byte frame per join). That is the
  port's repair for a consumer that subscribes to its transport after the burst (the native managed strategies
  attach post-assignment, `StrategyBase.cs:90` — the reason `StatusRefreshTicks` existed); after it the
  incremental path has a delivered, current copy to compare against.
- **State**: every dumped entry is recorded as delivered **to the joiner only** — the per-(recipient, subject)
  latch of the owning path — so nothing repeats to it until the content changes. The side's committed copy is
  **not** written (Claude's review fix, see §10). Packets are chunked at `FedPacket.MaxMessagesPerPacket`.
- **Rotation re-seats** (`announceToMission: false`, not a resume) skip the dump: the migration latch sweep has
  already emptied the pair latches, so the ordinary first-send path re-delivers the picture without an
  O(peers × ships) burst inside the intermission.

### 2.3 The state, and where it is freed

| Structure | Key | Size bound | Freed |
|---|---|---|---|
| `_lastStatusSentBySide` | `(SideId, ShipId)` → `(ShipStatusContent, SentTick)` | ships × sides (288 at 144 ships/2 sides; ~1 200 at 400 ships/3 sides) | peer departure (`Server.Lifecycle.cs:2313`), parked-seat release (`Server.ReconnectGrace.cs:432`), migration prep + latch sweep (`Server.Lobby.cs:1085-1089`, `:1277-1284`), rotation (`Server.WorldRetirement.cs:64` + `TrimRetiredLatchCapacity`), recycled ship id (`Server.Broadcast.cs:2035`), side change (`Server.ShipStatusGate.cs:334`) |
| `_ownStatusChanged` | subject ship id | ships, cleared every tick | in place (`HashSet.Clear`) |

Not per (recipient, subject): the port's own-side and cross-side copies are per **subject**, exactly like native's
per-side `m_rgShipStatus`, so the table is O(ships × sides) rather than O(recipients × subjects). The per-pair
"has this recipient been told" answer is the latch each path already keeps (`_lastTeammateStatusTick`,
`_lastSameSideNpcStatusTick`, `_crossSideShipStatusTick`), which those paths already maintain and already free.
The comparison allocates nothing (a struct compare against a dictionary entry) — pinned by a zero-allocation test.

---

## 3. Step 2 — the dependency table (why the refreshes existed, and what covers each case now)

Method: `git log -S` over the three constants and their send sites, then read every consumer of `SHIP_STATUS` in
`src/Allegiance.ClientCore*`, `src/Allegiance.Bot`, `tools/allegiance-mcp`, the join/reconnect paths and the MCP
bot surface.

`git log -S` trail: `StatusRefreshTicks` ← P39/P58 (`Server.cs:993-1000` doc: the native managed strategy attaches
its `MessageReceiver` after ship assignment, so the join-burst status can be consumed before the handler exists);
`TeammateStatusRefreshTicks` ← P58 (`Server.cs:1002-1010`: a commander must learn a docked miner teammate);
`CrossSideStatusRefreshTicks` ← P493 (`Server.Broadcast.cs:2424-2443`: "late-joiner / lost-packet heal"); the
per-recipient/per-pair phase staggering ← P604b.

| Case | Why the refresh was believed necessary | Covered with the gate ON | Citation |
|---|---|---|---|
| **Missed edge packet** (the P493 comment's "lost-packet heal") | the cross-side catch-up re-sent a seen enemy's status every 100 ticks | **Not a thing on this transport.** TCP is a reliable, ordered byte stream; every `SHIP_STATUS` goes out on the ordered one-shot lane (`SendPeer` → `guaranteed: true`, `Server.Lifecycle.cs:1026-1036`; `TcpTransport.Send` enqueues into a per-peer FIFO drained in order, `src/Allegiance.Core/Transport/TcpTransport.cs:80-110,560-598`). There is no packet loss and no reordering inside a peer's stream | `TcpTransport.cs:80-110`, `:560-598`; `Server.Lifecycle.cs:1026-1036` |
| **Late joiner** (a peer that connects after the world exists) | teammate + cross-side refresh eventually told it | Join burst: the native dump (§2.2) sends every ship its side knows; every per-pair latch is empty for a new `ConnectionId` (ids are minted monotonically, `TcpTransport.cs:844`), so the ordinary first-send path would also deliver same-side statuses on the next tick | `Server.ShipStatusGate.cs:399`; `Server.Lobby.cs:5514-5520` |
| **Reconnect (P813c grace)** | the reseated peer needs the whole picture again | the resume replays the join burst (`CompleteJoinAndLaunch(..., resumed: true)`) → same dump; the parked seat's latches were freed on disconnect (`Server.Lifecycle.cs` latch block) | `Server.ReconnectGrace.cs:330-345,432` |
| **A recipient that changes side** | refresh re-taught it the new side's picture | `OnShipSideChanged` drops the subject's per-side copies **and** the switching peer's per-pair latches, so the first-send path re-delivers its new side's picture | `Server.ShipStatusGate.cs:334-358`; hook `Server.Lobby.cs:3950-3952` |
| **A subject that changes side** | — | same call: its per-side copies are dropped, so the new side's own-side copy and every seeing side's cross-side copy are re-sent by the ordinary edges | same |
| **A subject that respawns / changes hull** | refresh eventually showed the new hull | a respawn is a **new ship id** (`AllocateShipId`, `Server.Docking.cs:2171`) → new key → first send; a hull change is the `HullId` field → content change. Recycled ids drop their stale copies in `AnnounceFreshShip` | `Server.Broadcast.cs:2035`; test `(d)` in §7 |
| **A subject that leaves and re-enters visibility** | cross-side refresh re-sent a still-visible enemy | the hidden→spotted edge flips the `Unknown` bit (content change); hidden, unchanged ships send nothing | test §7 cross-side |
| **Sector change of the recipient** (warp) | own refresh every 40 ticks | `SectorId` is a content field; also the join-burst copy is unrecorded so the first tick re-sends | test `(a)` §7 |
| **Own-ship strategy attach race** (P39: the managed strategy subscribes after assignment) | own refresh every 2 s | first tick after the join re-sends the own status (join-burst copy deliberately unrecorded, §2.2); after that, an own status only changes on a real event. **Residual, native-identical:** a consumer that attaches *later* than that sees no repeat, exactly as with native's change gate — native has no own-status refresh either | `Server.ShipStatusGate.cs:406-415` |
| **HUD-17 `Detected` flip** | the port's per-tick delta re-sent on change | `Detected` is bit 7 of the wire state byte → content field | test `(f)` §7 |
| **Dock / launch / board / observer** | refresh eventually showed it | state byte + station/parent/hull fields → content | test `(b)`, `(c)`, `(e)` §7 |
| **A dead ship** | — | the port sends **no** status for a dead ship (it leaves the mission's live set; the client learns from `SHIP_DELETE`, `FedSrv/fsship.cpp:194-224`), so there is no status edge to lose. Native's `ShipStatusExit` dead status is not part of the port's status stream (unchanged by this task) | — |
| **Bot / MCP consumers** (`src/Allegiance.Bot/Bot.cs`, `tools/allegiance-mcp/BotManager.*.cs`, `src/Allegiance.ClientCore*/**`) | they read `ClientWorld`/bot state that `Apply(ShipStatusMessage)` maintains | none of them counts or waits for status *frames*; they read the fields, which are idempotent. Grep: no consumer of `Fmid == 90` outside `ClientWorld`/`ConnectionSession`/bot export bookkeeping | `ClientWorld.cs:3198-3222`; `ConnectionSession.cs:1617-1620` |
| **Two matches back to back** (memory) | — | the per-side table is dropped for the retired world at rotation and capacity-trimmed; pinned by a live join/leave test (§7) | `Server.WorldRetirement.cs:64,120` |

---

## 4. What changed (file by file)

| File | Change |
|---|---|
| `src/Allegiance.Server/Server.ShipStatusGate.cs` **(new)** | `ShipStatusContent` (the packed status as a value), the flag + resolver, content builders (`OwnStatusContent`/`NpcStatusContent`/`CrossSideStatusContent`), `StatusMessage`, the per-tick subject decision (`TrackOwnStatusContent`/`TrackNpcStatusContent`), the cross-side gate (`StatusGateCrossSide`), the join dump (`SendStatusDumpToJoiner`), the side-change edge (`OnShipSideChanged`), and the cleanup helpers (`DropShipStatusContent`/`DropRetiredStatusContent`) + `StatusGateCounts()` test seam |
| `src/Allegiance.Server/Server.Broadcast.cs` | the four decision sites (serial `:866`, `:1042`, `:1211`; parallel `:1522`, `:1590`, `:1689`); `SweepShipDetection` fills the per-tick set (`:2384-2432`); `SweepCrossSideShipStatuses` delegates to the gate (`:2519-2532`); `MakeOwnShipStatus`/`MakeNpcShipStatus`/`MakeCrossSideShipStatus` build from the content (`:1734`, `:1759`, `:2594`); `AnnounceFreshShip` drops recycled-id copies (`:2035`) |
| `src/Allegiance.Server/Server.BroadcastBuffers.cs` | pooled `PackStatus`/`PackCrossSideStatus` serialise the same content (`WriteStatusContent`) |
| `src/Allegiance.Server/Server.Lifecycle.cs` | boot resolution + print (`:182-187`); departure cleanup (`:2313`) |
| `src/Allegiance.Server/Server.Lobby.cs` | side-change hook (`:3950-3952`); join dump call (`:5509-5520`); migration prep drop (`:1085-1089`) + latch-sweep step 13 (`:1277-1284`) |
| `src/Allegiance.Server/Server.ReconnectGrace.cs` | parked-ship release drops the subject's copies (`:432`) |
| `src/Allegiance.Server/Server.WorldRetirement.cs` | rotation drops the retired world's copies (`:64`) + capacity trim (`:149`) |
| `src/Allegiance.Server/Server.cs` | `ConnectionScopedCacheCounts` carries the gate table size (leak tests) |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs` | `TestSweepShipDetection()`; `TestResetBroadcastState` clears the gate |
| `src/Allegiance.Server/ServerConfig.cs` | `BroadcastConfig.ShipStatusChangeGate` (default false) with the full rationale |
| `docker-compose.yml`, `docker/README.md` | the new env override, documented like the other experimental switches |
| `GLOSSARY.md` | the P991 entry (term, files, env, report) |
| `tests/Allegiance.Core.Tests/P991FlagOffWirePinTests.cs` **(new)** | the flag-off wire pin (below) |
| `tests/Allegiance.Core.Tests/P991ShipStatusChangeGateTests.cs` **(new)** | the flag-on behaviour tests (below) |
| `docs/reports/SHIP_STATUS_CHANGE_GATE_P991.md` | this report |

No parity CSV row: the flag changes *when* an existing message is sent, not a ported rule; the native rules this
report cites are referenced from the gate and its tests (`FedSrv.CPP:3690-3699`, `fsmission.cpp:4303-4323`,
`fsship.cpp:237-291`), and P990 already recorded the refresh as port-only redundancy.

---

## 5. Measured before/after (Do item 4)

Fixture and method: exactly P990 §4/§5 — the largest local loopback fixture, **144 seated peers**, one sector, the
rig baseline (`immutableSnapshot`+`encodeOnce`, `broadcast.parallelBuild=true` with 8 workers,
`transport.mode=pool`, `broadcast.shipUpdateModel=native`, `sim.nativeGotoPlan=true`), the P983/P990 broadcast
telemetry ON, the profiler ON, 400 ticks of the full broadcast suffix with the diagnostic's window report taken at
the end. This time the fixture is **two-sided** (72/72 in one sector) so both O(P²) refresh rows of P990 §2.3 —
same-side teammate and cross-side seen enemy — are exercised, and it is measured under two visibility regimes:
*stable* (each side sees the other continuously) and *churn* (every ship leaves/enters the enemy's scanner net
every ~50 ticks, i.e. a constant stream of real spotted/hidden edges). Two runs per arm; the numbers are
bit-identical across repeats (deterministic fixture), so no ranges are needed. Host: 24 vCPU shared, load
average ≈ 9 during the runs.

| Arm | `SHIP_STATUS` frames/tick | `SHIP_STATUS` B/tick | ordered lane (window) | total egress (window) | `SHIP_STATUS` share of egress | tick p50 / p95 / p99 | `step:broadcastShipStates` mean/p95 | `step:broadcastShipsUpdate` mean/p95 | `step:crossSideShipStatusSweep` mean/p95 |
|---|---|---|---|---|---|---|---|---|---|
| **stable OFF** | 260.7 | 7 298 | 5 188 112 | 61 528 478 | 4.7 % | 7.28 / 7.97 / 8.89 ms | 0.864 / 1.003 ms | 6.224 / 6.380 ms | 0.079 / 0.125 ms |
| **stable ON** | **51.8** (−80.1 %) | **1 452** (−80.1 %) | **2 849 496** (−45.1 %) | **59 189 862** (−3.8 %) | **1.0 %** | 7.26 / 7.99 / 9.51 ms | **0.794 / 1.009** ms | 6.252 / 6.384 ms | 0.069 / 0.094 ms |
| **churn OFF** | 408.4 | 11 436 | 6 843 360 | 63 183 726 | 7.2 % | 7.32 / 10.10 / 19.79 ms | 1.008 / 1.990 ms | 6.610 / 6.657 ms | 0.107 / 0.166 ms |
| **churn ON** | **226.8** (−44.5 %) | **6 350** (−44.5 %) | **4 809 048** (−29.7 %) | **61 149 414** (−3.2 %) | **4.2 %** | 7.24 / 8.20 / 10.62 ms | **0.882 / 1.315** ms | 6.184 / 6.363 ms | 0.088 / 0.094 ms |

Reading the numbers:

- **Stable visibility — the rig's steady state.** The ON arm's *entire* `SHIP_STATUS` traffic over 400 ticks is
  20 736 messages = the one-time first delivery of every pair (144 recipients × 71 same-side mates + 144 × 72 seen
  enemies + 144 own) — **zero repeats**, i.e. the refresh share is 100 % of the OFF−ON difference.
- **Churn visibility — the worst case for the gate.** ON still sends 226.8 frames/tick because every
  spotted/hidden edge is real information delivered to the whole seeing side (native does the same); OFF is higher
  because it *also* refreshes. This row is the honest floor of the gate on a world where enemies constantly cross
  the scanner net.
- **The gate's own cost**: `SweepShipDetection` (the only place the per-subject compare runs) measured warm over
  500 iterations on the same 144-ship world: **0.0110 ms/tick OFF → 0.0220 ms/tick ON**, i.e. **+0.011 ms/tick**
  (~76 ns per ship) = **0.15 %** of the 7.3 ms tick. No allocation (pinned). Memory: 288 entries (144 ships × 2
  sides) = the `ships × sides` bound.
- **Wire volume**: the whole `other` lane drops from 10.5 %/10.8 % of egress to 7.9 %/4.8 % of the window; the
  remaining rows of P990 §2.1 (`EXPORT(26)` 23.0 → 32.7 %, `SHIP_WARNING_UPDATE(218)` 10.1 → 14.4 % **of the
  smaller lane**) confirm the next candidates' order of magnitude on a quieter lane.
- **Per-tick frames and bytes are what the rig A/B should read**, plus the P990 §5 list (total egress B/s and
  per-peer B/s, tick p50/p95/p99, `step:broadcastShipsUpdate`, update-age p50/p95/p99 and per-peer max,
  `neverPosed` / `[bb-age-why]` reasons — `starved` must not rise — correction distance / `[ctrlak]`, bot combat
  outcome, `droppedStreamFrames`/`coalescedSnapshots`) **plus** `[bb-other] fmid=90` frames/bytes per window and
  the `[bb-other]` share of the ordered lane.

### 5.1 What the flag does *not* touch

Pose updates (`HEAVY/LIGHT_SHIPS_UPDATE`), cadence, interest tiers, the byte budget, `CLUSTERINFO`,
`SHIP_WARNING_UPDATE`, mining/missile-track, the teammate `EXPORT` stream, and client rendering — all out of scope
and all byte-identical in both arms (the `heavy`/`light`/`keyed` category totals in the measurement are identical
to the byte across the arms).

---

## 6. Memory bound and cleanup (Do item 3)

- One entry per **(side, subject)** — native's `m_rgShipStatus` shape — plus the tick it was committed
  (`SentTick`) so the cross-side repair can tell "this peer has the current copy" from "this peer is behind".
  Count is `captured ships × sides`: **288 at 144 ships / 2 sides**; at the rig's shape (400 ships, 3 sides)
  ≈ 1 200 entries ≈ 48 KB.
- Dropped when **either side leaves**: peer departure frees the departing ship's copies (and all of its per-pair
  latches, as before); a parked seat's ship frees them on release; the migration's latch sweep frees the retired
  world's ships one collection per tick; the rotation frees every ship of the retired world and trims the backing
  arrays; a recycled id is dropped before its new holder's first status; a side change drops the subject's copies.
- No growth across matches: `RetirePerWorldShipState` applies the same complement rule as the other per-world
  latches, and the live join/leave test asserts the table returns to empty after the last peer leaves.
- The steady-state comparison allocates nothing (zero-allocation pin) and is O(ships), not O(pairs): the per-pair
  cost of the builders is unchanged (a `HashSet.Contains`, exactly what `_shipDetectedChanged.Contains` cost
  before).

---

## 7. Tests

New (both files in `tests/Allegiance.Core.Tests/`, `[Collection("LiveNetwork")]`, artwork-gated):

| # | Test | Tier |
|---|---|---|
| 1 | `P991FlagOffWirePinTests.Flag_off_wire_is_byte_identical_to_the_pinned_base_commit_transcript` — 24 peers, 2 sides, an NPC, a probe/mine, 40 replayed ticks of the whole broadcast suffix; SHA-256 over every captured frame (tick, recipient, coalesce key, guaranteed, length, bytes) + frame and byte totals | Fast |
| 2 | `P991FlagOffWirePinTests.Flag_off_serial_and_parallel_builds_emit_the_same_bytes` — P603's byte identity re-pinned around the shared content encoders | Fast |
| 3 | `Flag_on_sends_a_stationary_teammate_npc_and_enemy_status_exactly_once` — 12 ticks, every (recipient, subject) pair exactly once on tick 0, per-recipient population counted | Fast |
| 4 | `Flag_off_still_refreshes_the_same_stationary_world` — the two arms really differ on the wire | Fast |
| 5 | `Flag_on_sends_exactly_one_status_per_content_edge_with_the_right_content` — sector change, dock, launch, hull change, boarding, HUD-17 `Detected` on/off; asserts exactly one frame per edge and the content | Fast |
| 6 | `Flag_on_cross_side_copy_changes_send_once_and_never_leak_a_hidden_ship` — spotted edge, warp while visible (one per side-0 peer), hidden edge, warp while hidden (**nothing**), re-spotted | Fast |
| 7 | `Flag_on_late_joiner_learns_its_own_side_first_tick_and_seen_enemies_within_one_interval` | Fast |
| 8 | `Flag_on_gate_state_stays_constant_and_the_comparison_allocates_nothing` — 200 sweeps allocate 0 B, table constant, wire silent for 20 ticks | Fast |
| 9 | `Flag_on_serial_and_parallel_builds_are_byte_identical` — P603's pin with the gate on (motion + dock + launch) | Fast |
| 10 | `Flag_on_client_view_matches_the_flag_off_view_after_the_same_scenario` — 130 ticks, every captured message replayed into a real `ClientWorld`; the two views must agree on every ship's `HullId/SectorId/StationId/State/Unknown/Detected` | Fast |
| 11 | `Flag_on_gate_state_is_freed_when_peers_leave` — 8 real loopback clients (`Allegiance.Bot.Bot`), 3 disconnect, all disconnect; the table shrinks and returns to empty | Fast (loopback) |
| 12 | `Flag_grammar_is_the_same_as_the_other_broadcast_switches` (7 cases) + `Invalid_environment_is_rejected` | Fast |

The flag-off pin (#1) was generated on **the base commit's build** of the same file (the file uses only seams that
exist there) and then on this tree: both produce frame count `7459`, byte total `459922` and digest
`AB03 2045 3760 ABDC C600 5B79 9F06 AC88 9320 35F8 2DCC 7575 FAD6 0CF2 23EB AE25`. That is the "pin it"
requirement: the flag-off wire is not merely argued to be unchanged, it is the base commit's bytes.

Test counts, gate and the showcase runs: **§9** — new tests **19/19 × 3 runs**, 0 skipped; fast suite
13,947 tests with four non-task failures, each isolated and accounted for.

---

## 8. Evidence and scratch

- Scratch (deleted): `.scratch/p991/` — the flag-off digest runs against a reverted tree and the measurement
  harness (a temporary xunit file that booted the 144-peer fixture twice per visibility regime and parsed the
  `[bb-*]` rows + `TickProfiler.Snapshot()`).
- No report assets: every number in §5 is inline and reproducible with the command in §9.
- Raw gate output: `~/allegiance-evidence/P991/p991-gate/` (outside the repo).

---

## 9. Gate

Filled by the runs recorded here:

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings, 0 errors** |
| `tools/build-tools.sh` (same workaround via a `DOTNET` wrapper) | **all 130 tool projects built**, 0 warnings |
| AOT publish (`src/Allegiance.Server`, `linux-x64`, `PublishAot=true`) | **succeeded**, no IL2026/IL3050 warnings, 17,505,936-byte native binary |
| new tests, 3× alone | **19/19 passed, 0 failed, 0 skipped** in each of three runs (`--filter FullyQualifiedName~P991`) |
| existing status/broadcast/teammate/parallel cohorts (Core.Tests) | **206/206 passed** (`ShipStatus|Broadcast|Teammate|P603|P604`) |
| existing rotation/migration/handoff cohorts (ClientCore.Tests) | **10/10 passed** (`P884|P807|P493|P775`) |
| one full fast suite (`tools/run-tests.sh --tier fast`, `--no-build`) | **13,947 tests, 625.5 s — 4 failures, none touching this task** (see the triage table below) |
| showcase never-parks, 5× flag OFF + 5× flag ON (+ one recheck per arm) | **5 pass / 1 fail per arm**; the single failure landed on the *same run in both arms* (arrival timeout at the 480 s window, the ledgered P700 arrival flake; parking violations 0 in every run) |
| 2-side seeded showcase with the flag ON (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`) | **passed** in 4 m 33 s (kills, pods, relaunch, mining, research all reached) |
| `tools/check-glossary-links.py` | **OK** (745 distinct paths) |
| `docker compose config -q` | **valid** |
| `grep -c $'\r'` on the touched docs | 0 (LF only) |

The worktree carries the gitignored `local.env` (`ALLEGIANCE_ARTWORK_PATH`, `ALLEGIANCE_NATIVE_SRC_PATH`), so
**Skipped = 0** in every P991 run, in the 206-test status/broadcast cohort and in the 10-test rotation cohort (the
only `NotExecuted` cases anywhere are the three pre-existing MonoGame xunit skips, identical on every tree).

Showcase runs (wall seconds, alternating arms so host drift hits both):

| Run | flag OFF | flag ON |
|---|---|---|
| 1 | 326 s pass | 199 s pass |
| 2 | 486 s **fail** (arrival at the 480 s window; 0 park violations) | 485 s **fail** (same signature) |
| 3 | 339 s pass | 214 s pass |
| 4 | 144 s pass | 387 s pass |
| 5 | 183 s pass | 251 s pass |
| recheck | 294 s pass | 437 s pass |

Fast-tier triage (all four, isolated re-runs; details in `docs/TEST_FLAKES.md` "P991" section):

| Case | Sweep | 3× alone |
|---|---|---|
| `ProbeCombatLiveTests.Lethal_hit_kills_probe…` (17.1 s) | slow-Fast + missing destroy receipt | 3/3 pass |
| `ArmedProbeLiveTests.Armed_probe_spawns_hits…` (37.4 s) | slow-Fast + missing ammo/damage receipt | 3/3 pass |
| `LiveSideSelectTests.Flag_on_auto_seats…` | the ledgered auto-seat clock boundary | 1 pass / 2 fail alone — already ledgered (clean-base 2/3) |
| `P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim` | aim receipt, 0.3 s | 3/3 pass |
| slow-Fast: `P750NativeAsteroidTypesTests` 11.2 s, `P777WarpExitWobbleTests` 10.1 s | assertions passed | ledgered near-limit families |

Raw gate output: `~/allegiance-evidence/P991/p991-gate/` (the fast-suite TRX/logs + `summary.json` and the
showcase run log). The `build-tools.sh` and AOT-publish runs were checked from their console output (0 warnings,
no IL2026/IL3050, 17 505 936-byte binary) and are reproducible with the two commands named in the table.

---

## 10. Claude's review

**Defect fixed — the join dump rewrote the sides' committed copies.** `SendStatusDumpToJoiner` stored every dumped
status in `_lastStatusSentBySide[(shipSide, id)]`. For a seen enemy that key is the ENEMY's own-side copy, so the
cross-side content (`Detected=false`) overwrote the own-side copy (`Detected=true` while seen); the next sweep saw a
"change" and re-sent that enemy's status to its whole side. Every join therefore cost O(seen enemies × enemy side)
redundant frames; at the rig's 400-bot start that is the O(P²)-per-join burst this flag exists to remove. Writing the
right key would not have been correct either: re-stamping `SentTick` makes every other peer of the seeing side look
"behind" to the cross-side repair, and an own-side copy newer than the last sweep would hide a change from the
teammates. Fix: the dump records only the joiner's per-pair latches; the per-tick sweep alone maintains the committed
copies. The live tests never saw it because the late-joiner unit test seeds the joiner without running the dump; a
new seam (`TestSendStatusDumpToJoiner`) and test
`Flag_on_join_dump_reaches_only_the_joiner_and_leaves_every_side_copy_alone` reproduce it (before the fix: 9 re-sent
frames to the 3 enemy peers; after: 0).

**Checked and correct.** Flag-off decision terms are the old ones verbatim, serial and parallel; the sweep and both
builders run in the same step sequence in both broadcast orders, so a committed change always reaches its builder
(a builder step that throws loses that tick's change, exactly as native updates `pssOld` at send time); the
bots/MCP read `ClientWorld`, whose `Apply(ShipStatusMessage)` upserts, so a status that arrives before the
teammate EXPORT is not lost; native citations re-grepped. Residual, documented: the "own ship re-sent on the first
tick" repair applies to a fresh ship; a resumed peer gets its own status from the join burst only.

## Native intent

Native's broadcast never re-sends knowledge a side already has. A ship's status is a per-(ship, side) record —
`CFSShip::m_rgShipStatus` (`FedSrv/fsship.cpp:237-335`) — maintained by discrete events (spotted, hidden, docked,
launched, hull change, restart, exit), and `SendShipUpdates` walks every side and every ship **every other frame**
and emits a `SHIP_STATUS` only when that record differs from the copy the side was last sent
(`FedSrv/FedSrv.CPP:3665-3719`), addressed to the whole side. The complete picture reaches a player exactly once,
at join, from `CFSMission::SendMissionInfo`'s "send the data on all known ships"
(`FedSrv/fsmission.cpp:4303-4323`). There is no periodic status refresh anywhere in native. P991 makes the port
follow that rule behind a default-off flag, keeps every event edge through the comparison (they are fields of the
status record), adds the join-time dump native has, and leaves the flag-off wire byte-identical to the base commit.

## Modern approach

The change is a **single content value per (side, subject)** and **one comparison per subject per tick**, not a
per-pair scan: the port's own-side and cross-side statuses are per subject exactly like native's per-side copies,
so the gate can live in the serial detection sweep that already recomputes every ship's `Detected` bit, and both
the serial and the parallel per-peer builders only read its result — which is what keeps P603's byte identity
intact. The content struct (`ShipStatusContent`) is now the single definition of the packed status, shared by the
message encoders, the pooled fast path and the gate, so "what is sent" and "what was last sent" cannot drift. The
three refreshes are deleted rather than tuned (native has none), the join/reconnect path gains native's full dump,
and the remaining per-recipient lag (late joiner, reconnect, side switch, recycled id, rotation) is handled by the
latches the server already had plus explicit invalidation edges. Measured on the P990 fixture: `SHIP_STATUS` 260.7 →
51.8 frames/tick with stable visibility (the residual is exactly one first delivery per pair), 3.8 % of total
egress, −0.07 ms in the ship-state step, +0.011 ms/tick for the gate itself, and a table bounded by
`ships × sides`.
