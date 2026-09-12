# P128 — SHIP_DELETE SDR_LOGGEDOFF — retire the zombie ship a disconnected player leaves behind

Repo: `AllegianceCore` branch `master`, on top of `f695d3f` (P127).

Scope: wire the `SDR_LOGGEDOFF`(4) reason into the existing `SHIP_DELETE(20)`
slice and have the P127 `ReleasePeer` disconnect seam retire the departing
player's ship — broadcast `SHIP_DELETE(20, SDR_LOGGEDOFF)` and remove the ship
from its sector, so it no longer flies forever under `Mission.Update`. One new
reason code reusing P103's `SHIP_DELETE` machinery and P127's `ReleasePeer`
seam — no new mechanism.

---

## Ordinal confirmed against the raw header (not the prompt's arithmetic)

Read the native enum directly:

`/home/domingo/Development/Allegiance/src/Inc/Messages.h:188-195`
```cpp
enum ShipDeleteReason
{
  SDR_OUTOFRANGE, SDR_KILLED, SDR_LEFTSECTOR, SDR_DOCKED, // client will cache ships when deleted for these reasons
  SDR_LOGGEDOFF,  // cliend is gone, so waste their ship and everything about it
  SDR_TERMINATE,  // client is getting a new ship so blow the old one away.
  SDR_SUPERCEDED, // client will never get this. This happens when a client logs back in while their
                  //    previous connection still existed. The new connection supercedes the old one.
};
DEFINE_FEDMSG(S, SHIP_DELETE, 20) // when the ship is really not in the sector anymore
  ShipID           shipID;
  ShipDeleteReason sdr;
```
So `OUTOFRANGE=0, KILLED=1, LEFTSECTOR=2, DOCKED=3, LOGGEDOFF=4, TERMINATE=5,
SUPERCEDED=6`. **`SDR_LOGGEDOFF = 4` confirmed** — matches the prompt's ordinal.
(The native `Messages.h` is not vendored inside `AllegianceCore`; it lives in
the sibling native checkout `/home/domingo/Development/Allegiance`.)

---

## Findings (each confirmed against source)

### Where a ship actually leaves its sector today (reused removal mechanism)

The port's single "a model no longer occupies this sector" primitive is
`Sector.Remove(Model)` → `_models.Remove` (`src/Allegiance.Core/Model/Sector.cs:217-218`).
The kill/death path drives it reactively through the **death contract**: when a
ship's hull fraction reaches 0, `Ship.Dead` is true (`Ship.cs:118`), and
`Sector.Update` drops it from `_models` (`Sector.cs:1082`, `Sector.cs:1499` —
`if (ship.Dead) _models.Remove(ship)`). The same `Sector.Remove` is used for a
clean sector move (`Server.cs:3532`, ripcord teleport) and warp crossing
(`ServerHost.cs:667`).

**P128 reuses `Sector.Remove(ship)` for the logged-off case** — the exact same
primitive the death path uses — rather than building a second removal.

### Hook point: `ReleasePeer` (P127's shared seam)

`Server.ReleasePeer(ConnectionId)` (`Server.cs:940`) is the single cleanup path
funneled into by **both** the graceful `LOGOFF` handler (`Server.cs:1951`,
`ReleasePeer(from)`) and the abrupt transport drop (`PeerLeftEvt` →
`HandleTransportEvent` → `ReleasePeer`, `Server.cs:919`). P128 widens it to also
retire the departing ship, so both paths get the same retirement with no second
mechanism. Idempotency preserved: it now uses `_peerShips.Remove(id, out live)`,
so the double-release case (LOGOFF, then the transport's own `OnPeerLeft`) is a
no-op on the second call.

`RetireShipOnDisconnect(LiveShip)` (added at `Server.cs`) is the port's analogue
of native `CFSShip::~CFSShip` (`/home/domingo/Development/Allegiance/src/FedSrv/fsship.cpp:58-68`):
```cpp
CFSShip::~CFSShip()
{
  AnnounceExit(NULL, SDR_LOGGEDOFF);
  ...
  m_pShip->Terminate();
}
```
Three steps, each mirroring native with a reused port mechanism:
1. Detach boarded children (finding #3 below).
2. Broadcast `SHIP_DELETE(20, SDR_LOGGEDOFF)` — `ShipDelete.ForLoggedOffShip(ship.Id)`.
3. `sector.Remove(ship)` + `ship.Sector = null` (mirrors `Terminate`'s `GetCluster() → NULL`).

### Finding #3 — boarded children: detached (ejected), not destroyed

Native `CshipIGC::Terminate` (`/home/domingo/Development/Allegiance/src/Igc/shipIGC.cpp:205-262`)
handles children with the comment "**Blow away the children**"
(`shipIGC.cpp:243-250`): it calls `SetParentShip(NULL)` on each child. Critically,
`SetParentShip(NULL)` is **not** destruction — the NULL-parent branch
(`shipIGC.cpp:3671-3700`) ejects the child as an independent ship: *"Put us in
an eject pod … (ain't ejection fun) … we can now see things."* So native's
LOGGEDOFF blows the boarding relationship away but the child's own ship survives
as an independent vessel; it is not destroyed.

This port's existing "detach a boarded child" helper is `PerformLeave(boarder,
carrier)` (`Server.cs:2676-2736`) — the helper P110/P108 already use to boot
children on the teleport bNewHull path (`Server.cs:3981-3982`:
`foreach (var kid in ship.ChildShips.ToArray()) PerformLeave(kid, ship);`).
`PerformLeave` gates on the carrier being docked (`Server.cs:2678-2682`), which
always passes here because **boarding itself gates on the carrier being docked**
(`Server.cs:2660-2662`: the board gate is `carrier.StationId != null`). So P128
reuses `PerformLeave` verbatim for each child — the existing cascade, not new
logic. It clears `ParentShip`/`TurretId`, removes the child from the carrier's
child list, restores the child to its side's Lifepod hull, and broadcasts
`LEAVE_SHIP(94)` to the whole side. The child's own ship stays placed and
commandable by its (still-connected) owner.

The live test `Carrier_logoff_detaches_boarded_children_and_retires_the_carrier`
proves it: after the carrier's LOGOFF the child's `ParentShip` is null, it is out
of the carrier's `ChildShips`, the carrier's ship is removed from the sector, and
the child's own ship is still in the sector.

### Finding #4 — a docked player disconnecting: `SDR_LOGGEDOFF` still applies

Being docked does **not** change LOGGEDOFF behavior. Native's `CFSShip::~CFSShip`
(`fsship.cpp:58-68`) fires on destruction unconditionally — there is no dock
check in the destructor, and `AnnounceExit`'s parent-ship early-return guard
only exempts `SDR_DOCKED`/`SDR_LEFTSECTOR` (`fsship.cpp:198`), not `SDR_LOGGEDOFF`.
In this port a docked ship is still placed in a sector's `_models` (it just has
`StationId != null`), so `ship.Sector` is non-null and the same retirement path
applies.

The live test `Docked_player_logoff_still_retires_the_ship` proves it: a ship
latched docked (`StationId != null`) that sends `LOGOFF` is removed from the
sector, matching native.

---

## What changed

1. `src/Allegiance.Core/Wire/ShipDelete.cs`
   - `public const int SdrLoggedOff = 4;` (ordinal confirmed from the raw
     header above) + `public static ShipDelete ForLoggedOffShip(short shipId)`,
     mirroring `ForKilledShip` exactly.
   - Class doc updated: this slice now emits `SDR_KILLED`(1) and
     `SDR_LOGGEDOFF`(4).

2. `src/Allegiance.Server/Server.cs`
   - `ReleasePeer` now removes the peer via `_peerShips.Remove(id, out live)`
     (preserving idempotency) and calls `RetireShipOnDisconnect(live)`.
   - New `RetireShipOnDisconnect(LiveShip)`: detach children via `PerformLeave`,
     broadcast `ShipDelete.ForLoggedOffShip`, `sector.Remove(ship)`,
     `ship.Sector = null`.
   - Updated the `PeerLeftEvt` (`Server.cs:913-921`), `ReleasePeer` (`Server.cs:932`),
     and `LOGOFF` handler (`Server.cs:2017-2032`) doc comments to the new
     retirement behaviour.

3. Docs
   - `LOGOFF_P127.md` — P128 supersession banner on the "ship stays in sector"
     claim it recorded.
   - `BOT.md` — updated the "ship stays after disconnect" bullet to the P128
     retirement behaviour.

## Tests

Suite: **2633 passed, 0 failed — 3 consecutive full-suite runs** (baseline 2627
at the P127 checkpoint; +6 net new cases). No `LiveChatSectorWireTests` /
`LiveCombatWireTests` flake fired on any of the three runs.

New / rewritten in `tests/Allegiance.Core.Tests/LiveLogoffTests.cs` (10 tests):
- `ShipDelete_loggedoff_round_trips` + `ShipDelete_loggedoff_emits_exact_declared_bytes`
  — codec: `ForLoggedOffShip` round-trips and emits the exact 12 bytes with
  `sdr = 4` (same layout as SDR_KILLED's exact-bytes test, different enum value).
- `Logoff_echoes_releases_and_retires_the_ship` — single graceful LOGOFF: echo
  received, peer released, **ship removed from its sector** (supersedes P127's
  "stays in sector" assertion).
- `An_observing_peer_witnesses_loggedoff_ship_delete` — a still-connected
  same-sector observer receives `SHIP_DELETE(20, SDR_LOGGEDOFF)` naming the
  leaver's ship id, which is gone from the sector; observer's ship untouched.
- `Abrupt_drop_retires_the_ship_and_broadcasts_loggedoff` — the same via an
  abrupt socket drop (transport `OnPeerLeft` → `ReleasePeer`): observer sees
  `SDR_LOGGEDOFF`, ship removed.
- `Logoff_cleanup_matches_an_abrupt_disconnect_footprint` — graceful and abrupt
  drop retire both ships identically (single `ReleasePeer`, no second mechanism).
- `Docked_player_logoff_still_retires_the_ship` — finding #4 live-proof.
- `Carrier_logoff_detaches_boarded_children_and_retires_the_carrier` — finding
  #3 live-proof.

Modified: `tests/Allegiance.Core.Tests/BotFlightTests.cs`
(`Bot_disconnect_leaves_the_server_ticking`) — the fresh-bot "different position"
proxy was a side-effect of the old abandoned-ship-stays-and-flies behaviour;
rewritten to assert the P128 retirement directly (ship removed from the sector)
and that a fresh bot is served a distinct ship object.

### Live-loopback evidence (captured during the filtered run)

```
[logoff] ship 8096 — SHIP_DELETE(20) SDR_LOGGEDOFF -> 0 peer(s)
LOGOFF-LIVE: peer LOGGER ship 8096 got 1 echo(es), released from Ships, ship-object in-sector=False after 71 ticks
[logoff] ship 8096 — SHIP_DELETE(20) SDR_LOGGEDOFF -> 0 peer(s)
LOGOFF-DOCKED-LIVE: docked ship 8096 retired from sector after logoff (5 ticks)
[logoff] ship 8096 — SHIP_DELETE(20) SDR_LOGGEDOFF -> 1 peer(s)
LOGOFF-BOARD-LIVE: carrier 8096 logged off — child 8097 detached (ParentShip null, out of child list), carrier retired, child in-sector=True after 37 ticks
[logoff] ship 8097 — SHIP_DELETE(20) SDR_LOGGEDOFF -> 1 peer(s)
LOGOFF-OBS-LIVE: observer 8096 saw SHIP_DELETE(20) SDR_LOGGEDOFF shipID=8097 after 38 ticks; leaver ship in-sector=False
[logoff] ship 8097 — SHIP_DELETE(20) SDR_LOGGEDOFF -> 1 peer(s)
LOGOFF-ABRUPT-LIVE: observer saw SHIP_DELETE(20) SDR_LOGGEDOFF shipID=8097 for abrupt drop after 1 ticks
LOGOFF-LIVE: graceful 8096 and abrupt 8097 both released and retired after 2 ticks
```

---

## Still open in this small cluster

- **Audience is sector-scoped, native is whole-mission.** Native
  `AnnounceExit` for non-DOCKED/LEFTSECTOR reasons addresses the whole mission
  group (`GetGroupMission()`, `fsship.cpp:213-219`); P128 broadcasts
  `SDR_LOGGEDOFF` to the same-sector peers, matching this port's established
  SHIP_DELETE convention (P103's KILL_SHIP sector audience — same documented
  narrowing, `SUICIDE_SHIP_DELETE_P103.md`). Consistent, not an oversight.
- **A combat-killed carrier's children are still not cascaded.** The port's
  death path (death contract + `BroadcastKillShip`) does not detach a dying
  carrier's boarded children — a pre-existing cluster-E limitation. P128 detaches
  them for the disconnect path only; unifying death-path child handling is a
  separate follow-up, out of this task's "reuse, don't duplicate" scope.
- **`SDR_TERMINATE` / `SDR_SUPERCEDED` / `SDR_OUTOFRANGE` / `SDR_LEFTSECTOR`**
  remain unconstructed (unchanged from P103's out-of-scope list).
