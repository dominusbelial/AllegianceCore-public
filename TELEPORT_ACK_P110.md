# P110 — TELEPORT_ACK (extend existing docking)

Repo: `AllegianceCore` branch `master`, on top of `bf2ba30` (P109). Scope: the
**non-`NA`** half of the existing `FM_C_DOCKED(48)` handler — the
teleport/relocation dock request the native serves from *inside* the same
handler (`FedSrv.CPP:7031-7135`). This port already implemented `FM_C_DOCKED`
only as an undock request; this task extends it to answer a real
`FM_S_TELEPORT_ACK(102)`. No Windows VM needed.

---

## What was built

### `src/Allegiance.Core/Wire/TeleportAck.cs` — `FM_S_TELEPORT_ACK(102)`

`Messages.h:681-684`:
```cpp
DEFINE_FEDMSG(S, TELEPORT_ACK, 102)
    StationID   stationID;
    bool        bNewHull;
END_FEDMSG
```
Layout (7 bytes, no padding — the `short` is 2-aligned after the 4-byte
header, then the C++ `bool` byte):
```
offset 0: ushort cbmsg = 7
offset 2: ushort fmid  = 102
offset 4: short  stationID
offset 6: byte   bNewHull   (0/1)
= 7 B fixed, cbmsg = 7
```
`Encode()` emits `{07-00-66-00 <id lo> <id hi> <bNewHull>}`; `Decode()`
validates the header and recovers both fields. Matches the sibling
`KILL_SHIP(105)` odd-tailed-bool precedent (`KillShip.cs:13-15`).

### Extended `FM_C_DOCKED` handler (`Server.cs`)

The pre-existing undock-only branch (grep anchor was the old comment "handle
an inbound `FM_C_DOCKED` as an UNDOCK request") now routes a non-`NA`
`stationID` to a new `PerformTeleport(from, live, stationId)` method
(`Server.cs`). Gates preserved unchanged: the ship must be peer-owned and
docked somewhere (`GetStation()!=NULL`, `FedSrv.CPP:7040-7041`).

### `PerformTeleport` (`Server.cs`)

Port of `FedSrv.CPP:7054-7131`:
- **Validate** (`FedSrv.CPP:7063-7066`): the named station exists, is on the
  player's side or an ally's (collapses to same-side — no alliance concept,
  matching how `FindFriendlyStation` defines "friendly"), and is
  `c_sabmRestart`-capable. `c_sabmRestart = 0x04` (`igc.h:746`); the bit test
  is the new `DataStationTypeIGC.HasCapability` helper
  (`DataStationTypeIGC.cs`, field `SabmCapabilities` already decoded).
- **Reject** (`FedSrv.CPP:7067-7073`): ack `stationID = NA`, `bNewHull =
  false`, and `break` — the ship is **left where it is**, not moved.
- **Acknowledge** (`FedSrv.CPP:7075-7077`): echo the real `stationID`,
  `bNewHull = !station.CanBuy(current hull)`.
- **`bNewHull` path** (`FedSrv.CPP:7079-7120`): boot boarded children via the
  existing `PerformLeave` (P108), refund the ship's value via the existing
  `ShipValue` (P108) into the side treasury, strip parts via the existing
  `Ship.ClearLoadout`, and swap to the side's Lifepod hull via the existing
  `Ship.ChangeHull` (P105/P107/P108) — **all existing helpers, none
  reimplemented**.
- **Relocate** (`FedSrv.CPP:7124-7131`): move any boarded children and the
  ship itself to the target station's cluster and dock it there, and re-point
  the owning peer's `LiveShip.Sector` snapshot (the P105 pattern,
  `Server.cs:3469-3470`) so `SHIP_STATUS` frames stay coherent.

### `bNewHull` decision: `StationSellsHull` (`Server.cs`)

Native `station.CanBuy(b) = b->GetRequiredTechs() <= (station.GetLocalTechs()
| side.GetTechs())` (`stationIGC.h:383-391`; station local techs =
`ttbmLocal`, `stationtypeigc.h:159-161`). This port has **no** tech-tree
simulation, so the side's runtime-developed term is approximated by its
civilization's **base** tech mask (`DataCivilizationIGC.TtbmBaseTechs`, the
faction's starting knowledge) — the closest static proxy this port carries.
Therefore:
`bNewHull = !(hull.TtbmRequired ⊆ (stationType.TtbmLocal | civ.TtbmBaseTechs))`
(all 50-byte/400-bit tech masks). Measured on PCore001.igc this yields a sane
dichotomy: a garrison sells its faction's starter hulls (Scout / Lt
Interceptor / Fighter / Miner per civ → `bNewHull=false`) and rejects every
advanced hull (`bNewHull=true`). Stated plainly, not a silent drop.

---

## Did P105 land? Were its helpers reused or duplicated?

**Yes, P105 landed** (`9a20cea`, already in the base). Its helpers were
**reused directly, not duplicated** — the `bNewHull` path calls `ChangeHull`
(the P105/P107/P108 hull swap), the side's Lifepod resolution (`side.
Civilization.Lifepod` → `Core.HullTypes[...]`), `ShipValue` + the
deposit-into-side-treasury convention (P108's board refund, `Server.cs`),
`Ship.ClearLoadout` (part strip), and `PerformLeave` (boarded-child boot, P108).
Nothing was reimplemented a second time. (The extra `ClearLoadout`/refund/
hull-swap in `BoardShip` predates P110 and already shared the same helpers.)

## Boarded children — cluster E

**Cluster E (P108/P109) had landed**, so the boarded-children sub-case is
**implemented**: `PerformTeleport` moves `Ship.ChildShips` to the target
station (`FedSrv.CPP:7124-7130`), and on the `bNewHull` path first boots them
via `PerformLeave` (`FedSrv.CPP:7083-7087`). Not skipped.

## Real trigger for a non-`NA` `FM_C_DOCKED` — the natural next follow-up

Still **unbuilt** (as scoped): nothing yet causes a client to send a non-`NA`
`FM_C_DOCKED` — this port has no rescue/warp-arrival relocation trigger. The
natural next follow-up once one exists is a **rescue flow** (a downed player
being towed/teleported home → the server/commander sends the relocated ship a
reason to request a specific friendly station) or a **warp/respawn landing**
that hands a displaced ship an explicit `stationID`. Until then this task has
only made the server correctly **answer** such a request (the "port the
responder, defer the trigger" discipline). `SHIP_RESET`(183) /
`RELAUNCH_SHIP`(192) / the four `RIPCORD_*`(184-187) messages remain separate,
unscoped.

## Verification

**No VM.** A real loopback `Bot`/in-process-server live test extends the
existing docking recipe (`LiveTeleportDockTests.cs`, 8 tests):

| Scenario | Wire assert |
|---|---|
| `TeleportAck_message_round_trips` / `_emits_exact_declared_bytes` | codec round-trips; exact 7-byte layout `07-00-66-00-42-41-00` / `…-01`. |
| Friendly restart station + **buyable** hull | `stationID` echoed (1051), `bNewHull=false`, hull unchanged, redocked. |
| Friendly restart station + **non-buyable** hull | `bNewHull=true`, hull→Lifepod(33), docked at 1051, `Parts` emptied, side treasury += hull+part value. |
| Nonexistent station (9999) | `stationID=NA`, ship stays put. |
| Enemy station (other side's garrison 1052) | `stationID=NA`, ship stays put. |
| Friendly **non-restart-capable** station (synthetic type-28 'Teleport', sabm=0x0008) | `stationID=NA`, ship stays put. |
| **Regression**: undock `stationID==NA` while docked | undocks exactly as before — `StationId` cleared, flying status on wire, **no** TELEPORT_ACK. |

Captured live logs confirm the real server path ran (not trivial passes):
```
[teleport] ship 8096 -> station 1051 'Garrison' bNewHull=True — TELEPORT_ACK(102)
[teleport] ship 8096 refunded hardware value 1 to side 0 treasury (...)
[teleport] ship 8096 hull -> Lifepod 33 (old hull not sellable at station 1051)
[teleport] ship 8096 docked at station 1051 'Garrison' in sector 'Ceto'
[teleport] 1 ship 8096: request for station 9001 rejected — not c_sabmRestart-capable -> TELEPORT_ACK stationID=NA
[teleport] 1 ship 8096: request for station 1052 rejected — not on this side or an ally's -> TELEPORT_ACK stationID=NA
[undock] ship 8096 undocked from station 1051 — next SHIP_STATUS(90) carries StationID=NA + State=c_ssFlying
```

Full suite: **8 new tests; the entire existing docking/loadout suite passes
unmodified.** Final counts after the 3-consecutive-clean barrier are in the
commit message below (net +8 on the 2459 P109 baseline).

## Files changed
- `src/Allegiance.Core/Wire/TeleportAck.cs` (new codec)
- `src/Allegiance.Core/Data/DataStationTypeIGC.cs` (+ `SabmRestart` const,
  `HasCapability`)
- `src/Allegiance.Server/Server.cs` (handler routing + `PerformTeleport` +
  `StationSellsHull`)
- `src/Allegiance.Bot/Bot.cs` (TeleportAck receive capture)
- `tests/Allegiance.Core.Tests/LiveTeleportDockTests.cs` (new)
