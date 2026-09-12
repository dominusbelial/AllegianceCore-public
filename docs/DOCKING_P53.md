# P53 — Real docking: arrive → dock, request → undock

## Status: IMPLEMENTED + PROVEN via loopback; pending Claude Code's live-pass confirmation

This task closes the gap P52's own pass uncovered: `AllegianceCore` already
exports real station objects to clients (`Wire/ExportStation.cs`,
`Sector.Stations` is a fully real, position-aware `IReadOnlyList<DataStationIGC>`,
`Model/Sector.cs:44,56,66`) yet `Server.cs` hardcoded "no docking" into every
ship-status broadcast (`StationId = -1` / `ParentId = -1`, `Server.cs:414-415`).
This change wires real dock/undock state transitions and their wire
visibility. Pure server-side: sim + wire port, `AllegianceCore`-only (same
shape as P51's PAYDAY arc), no Windows VM, no native-repo edits.

---

## What is proven (verified via loopback, not fabricated)

Implemented, built clean, and proven with a loopback `Bot` test over real TCP
(`tests/Allegiance.Core.Tests/DockDockingTests.cs`):

1. **Arrival-triggered dock** — a ship within the simplified dock radius of a
   *friendly* station (same `SideId`) latches `Ship.StationId` server-side and
   the peer receives a real `FM_S_SHIP_STATUS(90)` carrying the real
   `StationID` and `State = c_ssDocked (2)` on the next broadcast. The whole
   wire side rides the existing `ShipStatus`/`SHIP_STATUS` broadcast — **no new
   message, no new fmid**. (Test: `FriendlyDock_sets_StationId_and_StateDocked_then_Undock_clears`.)
2. **Request → undock** — an inbound `FM_C_DOCKED`(48) with `stationID == NA`
   (-1), the only surviving half of that message, clears `Ship.StationId`; the
   next `SHIP_STATUS` carries `StationID = -1` and `State = c_ssFlying (3)`.
   Test also proves the docked state is a **latch** — moving the ship out of
   radius *without* undocking keeps it docked, so the clearing is attributable
   to the undock message, not the move.
3. **Friendly-only gate** — a ship parked *inside* a **hostile** station's
   radius (verified geometrically inside) never docks; the friendly gate, not
   geometry, is what blocks it. (Test: `HostileStation_in_radius_does_not_trigger_dock`.)
4. **No regression** — a ship nowhere near any station (default spawn; the
   first sector of `XXV_simple.igc` has zero stations) keeps broadcasting
   normal flying `SHIP_STATUS(90)`s with `StationID` NA, and never enters a
   docked state. (Test: `Ship_nowhere_near_a_station_is_unaffected`.)
5. **Codec correctness** — `FM_C_DOCKED`(48) round-trips and emits the exact 6
   declared bytes (cbmsg=6, fmid=48, `StationID` short; `Messages.h:399-405`).

Full suite: **2245 passed, 0 failed, 3 consecutive runs** (baseline was 2240
— this task only adds tests, removes none).

## Files changed

| File | Change |
|---|---|
| `src/Allegiance.Core/Sim/Ship.cs` | Added `short? StationId` — the docked-state field. |
| `src/Allegiance.Core/Model/Sector.cs` | Added `DockRadius` const + `ResolveDockCollisions` dock pass, invoked after the warp pass in `Update`. |
| `src/Allegiance.Core/Model/DockStation.cs` *(new)* | Minimal `IStationIgc` carrier wrapping a `DataStationIGC`, mirroring `ResolvedWarp : IWarpIgc`. |
| `src/Allegiance.Server/ServerHost.cs` | Overrode `IIgcSite.DockWithStationEvent` — the real dock state transition + `[dock]` log. |
| `src/Allegiance.Core/Wire/Docked.cs` *(new)* | `FM_C_DOCKED`(48) codec. |
| `src/Allegiance.Bot/Bot.cs` | Added `SendDocked()` harness helper (the client-side of the undock announce). |
| `src/Allegiance.Server/Server.cs` | Added `FM_C_DOCKED` case (undock) + derived docked state in `MakeOwnShipStatus`. |
| `tests/Allegiance.Core.Tests/DockDockingTests.cs` *(new)* | The 5 loopback/codec proofs above. |

---

## How it works, and the design decisions (each with `file:line` I read)

### Docked state field — nullable `short?`, not a `StateM` bit (`Ship.cs`)
`Ship.StationId` (`short?`, `Sim/Ship.cs`) is the docked state: `null` while
flying free, a value while docked. **Chosen deliberately over a `StateM` bit** —
this is the one judgment call I'm flagging against a literal reading of the
task. The task scope said "reuse `ShipStatus.StateDocked = 2` as the bit
value", but `StateM` is the C++ `ShipControlStateIGC` **button** bitmask
(`igc.h:803-835`; `Sim/ShipControlState.cs`), sent verbatim as the control
word on the wire (`ServerShipUpdate.FromModel`, `Wire/ServerShipUpdate.cs:106`)
and consumed by the movement/weapon slices. Its value `2` already *means*
`SelectedWeaponTwo` (`ShipControlState.cs:34`, inside `SelectedWeaponMask ==
3`), so folding a "docked" flag into `StateM` at bit `2` would collide with
weapon selection and leak a control flag onto the wire as if it were a button.
The wire `ShipState` enum that actually carries `c_ssDocked = 2`
(`ShipStatus.cs:40,62`) is a separate 6-bit field with **no `StateM` home** —
so the honest place for "docked" is a dedicated property, and the emitted
`ShipStatusMessage.State = StateDocked (2)` *reuses* the task's mandated value
("reuse, don't invent") via derivation rather than via a colliding `StateM`
bit. This follows `Ship.SideId`'s nullable-short convention (`Ship.cs:139`)
for "an id that may or may not be set". Detailed rationale is in the property's
XML doc (`Ship.cs`).

### Dock detection — proximity on the sim tick (`Sector.cs`)
`Sector.ResolveDockCollisions` (`Model/Sector.cs`), called from `Update` after
the warp pass, mirrors `ResolveWarpCollisions`: a per-tick point-in-sphere
test of each non-docked, non-dead, side-carrying ship against the sector's
stations. `DataStationIGC` carries only a `Position` (`float[3]`) — **no
size/radius** (`Data/DataStationIGC.cs:21-32`) — so the native
`InGarage`-based landing-slot test (`shipIGC.cpp:843,853`) cannot be ported
faithfully; `Sector.DockRadius = 200.0f` is an explicit **simplification** of
the real slot-based approach, stated as such in its XML doc. Geometry offered
by the data is only position; radius is a chosen constant.

**Distance-only, not velocity-near-zero.** The reference decides arrival by
*geometry* on the collision tick (`InGarage(ship, predictedPos)` is a position
test, `shipIGC.cpp:853`) — it does not require the ship to have slowed. So a
low-velocity clause would diverge from native semantics for no benefit, and it
would make the loopback proof need a throttle choreography instead of a
deterministic position check. Documented in the method remarks.

### The `[dock]` seam, mirroring P51's PaydayEvent (`ServerHost.cs`)
`ServerHost.DockWithStationEvent` (`ServerHost.cs`) is the real IIgcSite
override that performs the state transition: sets `Ship.StationId` and logs
`[dock] ...`, ported from the skeleton of
`FedSrvSiteBase::DockWithStationEvent` (`FedSrv.CPP:13087-13110`). It is wired
the same shape P51 wired `PaydayEvent` (`ServerHost.cs:107-108`), except the
`Server` needs **no** hook — unlike `ShipKilled`/`ShipWarped`/`Payday` (which
broadcast side-channel events), `MakeOwnShipStatus` derives the status from the
live `Ship` each broadcast, so setting `Ship.StationId` is *sufficient* for the
wire. The marker seam is bridged via the new `DockStation` carrier
(`Model/DockStation.cs`) exactly as `ResolvedWarp : IWarpIgc` carries warp
data into `HitWarpEvent` (`Model/ResolvedWarp.cs:23`).

### Friendly-only gate (`Sector.cs`, matching scope)
Native `DockWithStationEvent` interleaves hostile capture (`FedSrv.CPP:13100-13105`)
in the same body; this port does **not** — only same-side docking
(`station.SideId == ship.SideId`). `CaptureStation` and the new-hull/lifepod/
teleport re-dock path are explicitly out of scope and absent. The native
`OkToWarp()` eligibility gate (`FedSrv.CPP:13093` = `m_warpState == warpReady`,
`fsship.h:186-188`) is mirrored by the existing `WarpCooldownSeconds` window on
`Ship.LastWarpTime` — a ship that just warped cannot instantly dock onto a
station it transited onto. Fresh ships (`LastWarpTime == -∞`) are ready
immediately.

### Undock (`Server.cs`, `FM_C_DOCKED` fmid 48)
New dispatch case in `ProcessDatagram`, following the existing per-fmid
pattern (cf. the `ChatMessage` case, `Server.cs:1040-1076`). Message format
read from `Messages.h:399-405`: `StationID stationID`, header(4)+short(2) =
6 bytes; its own comment says the `stationID != NA` dock-request half is
obsolete — **only `stationID == NA` is handled**, mirroring the native undock
gate (`GetStation() != NULL`, `FedSrv.CPP:7040-7041`, then
`SetStation(NULL)` for NA, `FedSrv.CPP:7050-7052`). Clears `Ship.StationId`,
logs `[undock]`. A non-NA value is reported and dropped (never fabricated into
a re-dock).

### Wire side needed zero new codec work — confirmed
`ShipStatusMessage` already had `StateDocked = 2`, and `StationId`/`ParentId`
fully implemented and wire-correct (`Wire/ShipStatus.cs:62,71-72,92-93,128-129`);
the only change was populating them with real data in `MakeOwnShipStatus`
(`Server.cs:408-420` → derived from `Ship.StationId`). `Wire/ServerShipUpdate.cs`
untouched.

---

## What a follow-up live pass would still need to confirm

Everything above is **server-side sim + wire proof via a managed loopback
bot**. It does **not** involve a real `Wopr.exe` client in the native
`Allegiance` tree. As with P51/P52, this repo's standing P4X→live-pass pattern
applies: the native client's actual reaction to a live dock/undock cycle must
be observed on the Windows VM (Claude Code's job). Concretely:

1. **A `Wopr.exe` `ScoutExploreMap` pilot flown manually near a friendly
   station** should receive the docked `SHIP_STATUS(90)` (`StationID` set,
   `State=c_ssDocked`) and — after the server's `[undock]` responds to an
   `FM_C_DOCKED(48)` — a flying `SHIP_STATUS` again. The native client's
   visibility/`ShipState` handling of `c_ssDocked` in `appmsg.cpp`/`igc.h` is
   where the last-mile compatibility would surface.
2. **A `CommanderResearchAndExpand` commander given a chance to actually reach
   `UndockMinersAndStartMining`'s docked-state gate** —
   `CommanderResearchAndExpand.cs:1555-1558`: `miners.Where(p =>
   (ShipState)p.GetPlayerInfo().LastSeenState() == ShipState.c_ssDocked)`. For
   that branch to ever engage, a `.Miner`-named ship on the client's own side
   must be seen as docked (`LastSeenState() == c_ssDocked`) — i.e. the client
   must be observing exactly the docked `ShipStatus` this server now emits.
   That is the real end-to-end payoff this change enables (mining once a miner
   is truly docked), and the natural thing for the live pass to exercise.
3. Sanity: a hostile/capture situation still shows no dock (this server never
   ports `CaptureStation`), and multi-slot capital-ship bays are still
   single-slot (unchanged from scope).

Deliberately **not** in this change: capture-the-flag / hostile-station-capture,
capital-ship multi-slot landing-bay selection, cargo / repair / rearm /
buying-selling, and the obsolete client-initiated dock-request half of
`FM_C_DOCKED`. All separate arcs, all untouched.

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py
20260902_115503_ab26e4`:

    20260902_115503_ab26e4  start=11:55:12  dur=STILL RUNNING / no clean end
    msgs=146 tool_calls=83 api_calls=63  tokens_in=7,237,908 tokens_out=38,108

(captured near the end of the run; "STILL RUNNING" is expected — this session
had not closed when the log was taken.)
