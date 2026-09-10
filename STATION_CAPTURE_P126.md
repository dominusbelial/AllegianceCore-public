# P126 — STATION_CAPTURE(58): hostile docking captures an enemy station

Date: 2026-09-09. Branch: `master` (base `a2d91b0` P125 + this task). No VM,
no Obsidian/MCP.

## Headline

The deferred hostile-capture branch is now real. P110's own doc comment on the
dock pass read *"Friendly-only gate (FedSrv.CPP:13100-13105 — hostile-capture
branch explicitly NOT ported; friendly docking only)"*; P126 builds that branch.
A **different-side** ship whose hull has `c_habmBoard` that reaches a station's
dock radius **captures** it — re-sides the base to the capturer's side, applies
the post-capture damage fudge (`ArmorFraction` halved, `ShieldFraction` set to
the heroic-engineer regen value), broadcasts a real `FM_S_STATION_CAPTURE(58)`
to the **whole mission**, and then docks normally at its newly-captured base
(native docking continues unconditionally after the capture, `FedSrv.CPP:13107`).

## The real finding (confirmed, contradicts the earlier P125 note)

P125's "next task" note guessed capture was *"dock-while-shields-down"*. That is
**wrong**, confirmed by reading the native trigger itself:

- `FedSrvSiteBase::DockWithStationEvent` (`FedSrv.CPP:13087-13105`) gates capture
  on **only** three things: (a) `pside != psideOld` (different side), (b) the
  docking ship's hull `HasCapability(c_habmBoard)`, (c) `!AlliedSides(...) ||
  bAllowFriendlyFire`. There is **no shield or hull-fraction gate at all** — it is
  purely a docking-time check. The port's P125 `ShieldFraction` is therefore *not*
  part of the capture trigger (it is only consumed by the post-capture fudge).

## Every simplification, confirmed and cited

- **Alliance / `AlliedSides` term** — native (c) is `!AlliedSides(pside,
  psideOld) || bAllowFriendlyFire` (`FedSrv.CPP:13101-13102`). This port has no
  alliance concept and no `bAllowFriendlyFire` toggle (friendly fire always off),
  so (c) is **always true** — the exact precedent every `AlliedSides` /
  `bAllowAlliedRip` gate this arc simplified (P112 et al.). Stated in
  `Sector.ResolveDockCollisions`.
- **`AddBaseCapture`** (`fsship.cpp:617`) — confirmed **N/A**: the port's
  `Model.Side` (`Model/Side.cs`) has no base-capture counter, and there is no
  other consumer for one. Not invented (per the task's explicit instruction).
- **Score / Steam bookkeeping** (`CaptureBase`, `AwardBaseKillOrCapture`,
  `fsship.cpp:600-613`) — confirmed **N/A**: no score or Steam system exists
  anywhere in this port (same finding as P125's kill path).
- **The post-capture fudge is real mechanics, ported faithfully** (not a
  simplification): `SetFraction(GetFraction() * 0.5f)` halves the CURRENT hull
  fraction (`fsship.cpp:582`), and the heroic-engineer shield
  `regenRatio = ShieldRegeneration / MaxShieldHitPoints; boost = regenRatio * 7.0f`
  (`fsship.cpp:589-596`) uses the already-decoded `ShieldRegeneration` /
  `MaxShieldHitPoints` (`DataStationTypeIGC.cs:33,35`) into the P125 live
  `ArmorFraction` / `ShieldFraction` pools (`Sim/Station.cs`).

## Implementation

### `DataHullTypeIGC.cs`
Added `HabmBoard = 0x01` (`c_habmBoard`, `src/Igc/igc.h:726`) the same one-line
way P114 added its three `Habm*` bits — read via the existing `HasCapability`.

### `Sim/Station.cs`
- `SideId` became a mutable private-set property (initialized from
  `Data.SideId` in the constructor) so capture can re-side the runtime station
  (native `SetSide`, `fsship.cpp:631`); the friendly-fire gate
  (`ReceiveDamage`) now reads `SideId` instead of `Data.SideId` so a capture's
  new owner's shots are correctly treated as friendly.
- New `Capture(short newSideId)` — the sim-side **state** half of
  `CFSShip::CaptureStation` (`fsship.cpp:577-631`): halves `ArmorFraction`,
  sets `ShieldFraction` to the heroic-engineer value, re-sides `SideId`.

### `Model/Sector.cs` — `ResolveDockCollisions`
Restructured the inner station loop:
1. **Point-in-sphere arrival check first** (simplified `InGarage`,
   `|ship − station| ≤ DockRadius`, `Sector.cs DockRadius=200`) — a ship must
   actually *reach* the station's dock footprint before it can dock **or**
   capture (native derives arrival from `InGarage` on the collision tick,
   `shipIGC.cpp:843,853`).
2. Then the dock gate: if `station.SideId != side`, a ship with
   `HasCapability(HabmBoard)` **captures** — the site's
   `CaptureStationEvent` seam is raised (so the host broadcasts with `sidOld`
   still current, i.e. **before** re-siding, mirroring
   `FedSrvSiteBase::CaptureStationEvent` → `CFSShip::CaptureStation`,
   `FedSrv.CPP:13080-13084` / `fsship.cpp:620-631`), then the static record is
   re-sided (`_stations[si] = station with { SideId = side }`) and the runtime
   `Station.Capture(side)` runs. A hostile non-`HabmBoard` ship is rejected
   (no capture, no dock — the normal cross-side rejection). A static-only
   station (unresolvable type, P125) is not capturable (no live state).
3. Falls through to the ordinary friendly dock — the station is now the
   capturer's own side (native docking continues, `FedSrv.CPP:13107`).

### Wire: `Wire/StationCapture.cs` (new)
`FM_S_STATION_CAPTURE`(58) codec — `Messages.h:429-434`: `stationID` (short),
`sidOld` (SideID short), `sidNew` (SideID short), `shipIDCredit` (ShipID short).
Fixed 12 bytes (header 4 + 4×short), no var items. Round-trips + pinned 12-byte
layout test.

### Host seam → broadcast: `ServerHost.cs` + `Server.cs`
- `ServerHost.CaptureStationEvent(ship, station)` override (mirrors
  `KillStationEvent`'s bridge shape) raises a new `StationCaptured` hook.
- `Server` wires it (`_host.StationCaptured = BroadcastStationCapture`) and
  `BroadcastStationCapture(Ship, Station)` broadcasts `STATION_CAPTURE(58)`:
  `stationID` from the station, `sidOld = station.SideId` (the OLD owner —
  correct because the sector raises the seam pre-reside), `sidNew =
  ship.SideId`, `shipIDCredit = ship.Id` (native order `fsship.cpp:625-628`).

### `Bot.cs`
Decodes + accumulates `StationCapture` (collection + reset), mirroring
`StationDestroyeds` (P125).

## Broadcast target (explicit question) — confirmed, with citation

Native sends `STATION_CAPTURE` via
`g.fm.SendMessages(GetMission()->GetGroupRealSides(), FM_GUARANTEED, FM_FLUSH)`
(**`fsship.cpp:629`**) — the **WHOLE mission**, all real sides, the same
audience as `STATION_DESTROYED` (`FedSrv.CPP:12279`) and `ASTEROID_DESTROYED`
(P102). The port broadcasts to every connected peer (`_peerShips`), both sides.
Do not "fix" it to same-side.

## Docked children / boarded ships — confirmed, with citation

Native does **not** eject a captured station's existing docked ships: neither
`CFSShip::CaptureStation` (`fsship.cpp:577-641`) nor its callers call
`VacateStation` on capture (contrast the kill path, `FedSrv.CPP:12237-12279` →
`fsmission.cpp:4783`). Docked ships simply remain docked, now at a re-sided
base — `Ship.StationId` is untouched by capture, so **no state change** is
needed. Confirmed in `BroadcastStationCapture`'s doc comment.

## Verification

**Unit** (`StationCaptureTests.cs`, 3): `Capture` halves the CURRENT armor
fraction + sets the heroic shield (MaxShield 100 / regen 10 → 0.7) + re-sides;
capturing again halves again (0.5→0.25, proving multiplicative-not-reset); a
shield-less station (MaxShield 0) starts the shield at 0.

**Wire** (`Wire/StationCaptureWireCodecsWireTests.cs`, 4): constants (fmid 58,
fixed 12), round-trip of all four fields, `sidOld == sidNew` round-trip, exact
12-byte layout.

**Live loopback** (`StationCaptureLiveTests.cs`, 3, in-process server + real
bots):
1. **Hostile `HabmBoard` ship docks an enemy base** → base re-sides to the
   capturer's side (runtime `Station.SideId` AND the static record both flip),
   `ArmorFraction` exactly 1.0→0.5, `ShieldFraction` 0.7, the ship then docks at
   its newly-captured base, and `STATION_CAPTURE(58)` with the correct
   `stationID`/`sidOld`/`sidNew`/`shipIDCredit` reaches **every** peer (whole
   mission — cross-side capturer AND both same-side teammates).
2. **Hostile non-`HabmBoard` ship** parked at the enemy base → no capture
   (side/armor/shield unchanged), no dock, no broadcast.
3. **Same-side ship** docking its own base → plain friendly dock, no capture,
   no fudge, no broadcast (no regression).

Observed live evidence:
```
P126-LIVE-CAPTURE: ship 8097 (side 1) captured base 7901 (0->1); armor 1.0->0.5, shield 0.7; docked (StationId=7901); STATION_CAPTURE(58) -> 3 peer(s) [WHOLE MISSION]
P126-LIVE-NOBOARD: hostile non-boardable ship parked at enemy base -> no capture, no dock, no broadcast (side/armor/shield unchanged)
P126-LIVE-FRIENDLY: same-side ship 8096 docked its own base 7901 -> plain friendly dock, no capture (armor/shield untouched, no broadcast)
```

**Full suite**: baseline `2613` passing on entry (re-confirmed); after this task
`2623` passing, **0 failed** (10 added, all pre-existing tests green
**unmodified**). Three consecutive clean full-suite runs (one transient
`LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire`
timing flake fired mid-sequence — the documented ~1-in-8 pre-existing flake,
unrelated to P126 — and the rerun came back clean).

## `STATIONS_UPDATE`(57) finding (investigated, out of scope — confirmed deferred)

Confirmed independently (and consistent with P125's note): `STATIONS_UPDATE` is
a **variable-length** `FM_VAR_ITEM(rgStationStates)` batch (`Messages.h:425-427`)
of `StationState` records (per-station `stationID` + `bpHullFraction` +
`bpShieldFraction`), built and sent **per ship** inside the ship-status/update
block and filtered by `pstation->SeenBySide(pside)` (**`fsship.cpp:825,841`**) —
the per-side visibility system this port has already deferred for
`PROBES_UPDATE`/`ASTEROIDS_UPDATE` (there is no `SeenBySide` anywhere in the
port). So `STATIONS_UPDATE` remains genuinely gated on that same deferred system
and is correctly **not** built here. This task nevertheless completes the data
groundwork: the `bpHullFraction` / `bpShieldFraction` it would transmit are now
real live data — `Station.ArmorFraction` / `Station.ShieldFraction`, which
capture (`fsship.cpp:582,596`) and damage (P125) both mutate.

## What remains open

- **`STATIONS_UPDATE`(57)** — blocked on the deferred `SeenBySide` per-side
  visibility system (shared with `PROBES_UPDATE`/`ASTEROIDS_UPDATE`); data
  groundwork (live `ArmorFraction`/`ShieldFraction`) is now in place.
- **Phase 4 bucket-completion / `BUILDINGEFFECT_DESTROYED`** — still open, as
  carried forward from earlier phases; no dependency on this task.
