# P105 — player-death auto-respawn + ENTER_LIFEPOD

Repo: `AllegianceCore` branch `master`, on top of `68dbb02` (P104). Scope:
the native death-respawn branch inside `FedSrvSiteBase::KillShipEvent`
(`FedSrv.CPP:12023-12086`) — turning a **player's true death** (`bKilled ==
true`) into a docked Lifepod at a friendly station plus a private
`FM_S_ENTER_LIFEPOD(166)` to that player only — hooked at the **exact same
seam P103 wired** (`ShipKilled` → `BroadcastKillShip`), not a parallel
mechanism. No Windows VM needed.

---

## What was built

### `src/Allegiance.Core/Wire/EnterLifepod.cs` — `FM_S_ENTER_LIFEPOD(166)`

#### A correction to the brief, confirmed against native sources
The task brief asserted *"ENTER_LIFEPOD carries no fields at all
(Messages.h:886-888) — a bare notification"*. **That is wrong.** The native
struct **does** declare a field:
`Messages.h:886-888` is
```cpp
DEFINE_FEDMSG(S, ENTER_LIFEPOD, 166)
  ShipID        shipID;
END_FEDMSG
```
(`ShipID` = `short`, via `typedef short ObjectID; typedef ObjectID ShipID`,
`igc.h:477,480`). The client casts the struct (`appmsg.cpp:1950
CASTPFM(pfmEnter, S, ENTER_LIFEPOD, pfm)`), and the managed wrapper reads and
writes it (`AllMessages.h:6470-6479`, `pfm->shipID = shipID`). The native
*sender* leaves it unset (the `//pfmEnterLifepod->shipID = shipID;` comment,
`FedSrv.CPP:12061`), but the field is genuinely on the wire. Per the task's
instruction to confirm each claim myself and adapt to what is actually there,
the codec carries the `shipID` short (populated with the dying ship's id — the
field the native neglects to fill, carrying the correct value is strictly more
useful than garbage). Flagged here rather than silently following the brief.

Layout (no padding — the short is 2-aligned right after the 4-byte header):
```
offset  0: ushort cbmsg
offset  2: ushort fmid = 166
offset  4: short  shipID   (ShipID = ObjectID = short)
= 6 B fixed, no var items → cbmsg = 6
```
`Encode()` emits `{06-00-A6-00 <shipID lo> <shipID hi>}`; `Decode()` validates
the header and recovers the short. `ForPlayer(shipId)` factory.

### The friendly-station helper (`Server.cs`, `FindFriendlyStation`)

The deliberate simplification, stated plainly (not a silent drop). Native:
```cpp
IstationIGC* pstationDest = FindTarget(pship, c_ttFriendly | c_ttStation |
    c_ttNearest | c_ttAnyCluster, NULL, NULL, NULL, NULL, c_sabmRestart);
if (!pstationDest) pstationDest = m_pfsMission->GetBase(pside);
```
— the **spatially-nearest** friendly station in any cluster, falling back to
the mission base. This port has **neither** a spatial nearest-search nor a
"mission base" concept (confirmed by grep — neither exists in
`Sector.cs`/`Mission.cs`), so `FindFriendlyStation(ship)` returns **any**
station whose `DataStationIGC.SideId` equals `ship.SideId`, first-found in the
mission's deterministic sector-then-station order. "Best" station is out of
scope; any one friendly match is fine, per the task. It returns `null` when no
friendly station exists anywhere, mirroring native's no-`pstationDest` path.

### The respawn hook (`Server.cs`, `BroadcastKillShip` tail)

Added immediately after P103's `SHIP_DELETE(20)` broadcast, same trigger, same
call site — no new seam, no change to `SHIP_DELETE`'s own broadcast. Guards,
each faithful to native:
- **(1) `IsPlayer()` gate** — this port's player/bot distinction *is* peer
  ownership: a ship with an owning peer in `_peerShips` is a player; one
  without is a bot and is **not** respawned (`FedSrv.CPP:12026`, native
  else-branch `FedSrv.CPP:12080-12085`: drone goes bye-bye).
- **(2) `if (pstationDest)` guard** — no friendly station anywhere ⇒ no
  respawn, exactly `FedSrv.CPP:12055`; the ship simply stays gone, whatever
  `SHIP_DELETE` already said.
- **(3)** no `SideId` ⇒ `FindFriendlyStation` returns `null` ⇒ covered by (2).

When all pass, mirrors native's relocate-and-dock (`FedSrv.CPP:12045-12074`):
`SetCluster(NULL)` → out of the old sector and into the station's cluster
(`FedSrv.CPP:12045` then `12067`), `SetBaseHullType(pht)` → `Ship.ChangeHull`
(the port of `CshipIGC::SetBaseHullType`, `shipIGC.cpp:1352`; does **not**
reset the hull fraction — a lifepod is still the dead ship, and native's
function never touches `m_fraction`), `SetStation`/`ShipStatusDocked` →
`Ship.StationId`, and `FM_S_ENTER_LIFEPOD(166)` to the owning peer's
connection only (`g.fm.SendMessages(pfsPlayer->GetConnection(), FM_GUARANTEED,
FM_FLUSH)`, `FedSrv.CPP:12059-12062` — never broadcast). Also re-points the
owner's `LiveShip.Sector` snapshot at the station's cluster so the per-tick
`SHIP_STATUS(90)` frames stay coherent (BroadcastShipStates /
`MakeOwnShipStatus` derive Sector from that snapshot).

Hull lookup reuses what the port already exposes: `side.Civilization.Lifepod`
(`DataCivilizationIGC.Lifepod`, short) → `mission.Core.HullTypes[...]`. Bailed
gracefully (logged, no respawn) if the side or the lifepod hull id is missing.

## Verification (all green, 3 consecutive full-suite runs)

Each run: `/home/domingo/.dotnet/dotnet test tests/Allegiance.Core.Tests` →
**2420 passed, 0 failed** (baseline incl. P104 ≈ 2415; net **+5** new test
cases, matching the two codec tests + three live scenarios). New tests in
`tests/Allegiance.Core.Tests/LivePlayerDeathRespawnTests.cs` (all in the
`LiveNetwork` sequential collection):

| Test | Asserts |
|---|---|
| `EnterLifepod_message_round_trips` | ENTER_LIFEPOD(166) codec round-trips the shipID short. |
| `EnterLifepod_message_emits_exact_declared_bytes` | Hand-composed 6-byte buffer (cbmsg=6, fmid=166, shipID short) matches the encoder exactly. |
| `Player_death_relocates_to_docked_lifepod_and_only_owner_gets_enter_lifepod` (live) | Player suicides in a station-less sector; dies → KILL_SHIP(105)+SHIP_DELETE(20,SDR_KILLED) to **both** peers (no regression), relocates to the station's cluster, hull → side's Lifepod, docked at the friendly station, and **only the owning peer** receives a real ENTER_LIFEPOD(166). |
| `Player_death_with_no_friendly_station_does_not_respawn` (live) | Player on a side with **no** station anywhere: no ENTER_LIFEPOD, no respawn (hull unchanged, not docked) — SHIP_DELETE/KILL_SHIP only. |
| `Bot_ship_death_never_triggers_respawn` (live) | Bot (no owning peer) with a valid side **and** a friendly station present: SHIP_DELETE/KILL_SHIP only, no ENTER_LIFEPOD, not docked — the `IsPlayer()` gate, not the station gate, is the discriminator. |

### Live-loopback evidence (captured during the detailed run)

```
[combat] ship 8096 killed by (none) at sim-t 1.9 — KILL_SHIP(105) -> 2 peer(s)
[combat] ship 8096 — SHIP_DELETE(20) SDR_KILLED -> 2 peer(s)
[lifepod] player ship 8096 -> docked Lifepod (hull 33) at station 1051 'Garrison' in sector 'Ceto' ; ENTER_LIFEPOD(166) -> own peer only
[respawn] player 8096 died after 73 ticks; hull 1 -> Lifepod 33; docked at station 1051 in sector 1053 (old sector 1051)
[respawn] ENTER_LIFEPOD(166) shipID=8096 = 06-00-A6-00-A0-1F
[respawn] SHIP_DELETE(20) shipID=8096 sdr=1(SDR_KILLED) = 0C-00-14-00-A0-1F-00-00-01-00-00-00; ENTER_LIFEPOD to observer=no
[combat] ship 8096 killed by (none) at sim-t 1.9 — KILL_SHIP(105) -> 2 peer(s)
[combat] ship 8096 — SHIP_DELETE(20) SDR_KILLED -> 2 peer(s)
[lifepod] player ship 8096 side 551 has no friendly station anywhere — no respawn (native FedSrv.CPP:12055 guard)
[respawn] player 8096 (side 551, no station) died after 74 ticks — no ENTER_LIFEPOD, no respawn: SHIP_DELETE only.
[combat] ship 9701 killed by (none) at sim-t 0.15 — KILL_SHIP(105) -> 1 peer(s)
[combat] ship 9701 — SHIP_DELETE(20) SDR_KILLED -> 1 peer(s)
[lifepod] ship 9701 has no owning peer (bot/non-player) — no respawn, stays dead
[respawn] bot 9701 died (side 0, friendly station present); peer 8096 saw SHIP_DELETE/KILL_SHIP only; 30 quiet ticks — no respawn.
```

The ENTER_LIFEPOD bytes `06-00-A6-00-A0-1F` = cbmsg 6, fmid 166 (0xA6), shipID
8096 (0x1FA0 LE) — the header+shipID layout above, exactly.

### Regression
The full existing suites — including P103's own new tests
(`LiveSuicideShipDeleteTests`) and P104's — pass unmodified. Nothing changed
in the `KILL_SHIP(105)`/`SHIP_DELETE(20)` broadcast itself; P105 only adds the
respawn alongside it in `BroadcastKillShip`.

## Out of scope / siblings
- The in-space **`EJECT`(77)** mechanic (drifting lifepod hull, sector
  broadcast, `bKilled == false` eject path) — a **separate, real branch of the
  same function** (`FedSrv.CPP:12000-12022` region, the true-death branch's
  predecessor), scoped as its own task **P107**. This task deliberately does
  not touch it: the two are mutually exclusive at runtime (eject happens when
  `bKilled == false`; this respawn when `bKilled == true`). `MissionParams.
  bEjectPods` only decides which branch runs — irrelevant to this one.
- A true nearest-station spatial search (simplified above) — explicit, not a
  silent drop.
- Multiple friendly stations / "best" station selection — any one friendly
  match is fine per the stated simplification.
- Any change to `SHIP_DELETE`'s own broadcast (P103 untouched).

## Native sources consulted
- `src/FedSrv/FedSrv.CPP:12023-12086` — `KillShipEvent` death-respawn branch (quoted in full above from the original).
- `src/Inc/Messages.h:886-888` — `DEFINE_FEDMSG(S, ENTER_LIFEPOD, 166)` + `ShipID shipID;`
- `src/Inc/Messages.h:188-199` — `enum ShipDeleteReason` + `DEFINE_FEDMSG(S, SHIP_DELETE, 20)` (P103)
- `src/Inc/igc.h:477,480` — `typedef short ObjectID; typedef ObjectID ShipID`
- `src/Igc/shipIGC.cpp:1352-1427` — `CshipIGC::SetBaseHullType` (subset ported into `Ship.ChangeHull`)
- `src/clintlib/appmsg.cpp:1945-1955` — client `FM_S_ENTER_LIFEPOD` handler (casts the struct)
- `src/Wopr/AllegianceInterop/AllMessages.h:6460-6479` — managed `FMD_S_ENTER_LIFEPOD` wrapper (reads/writes shipID)
- `src/Inc/Messages.h:590-591` — `DEFINE_FEDMSG(C, SUICIDE, 80)` (P103)
