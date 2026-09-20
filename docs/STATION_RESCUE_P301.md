# STATION_RESCUE_P301 — c_sabmCapture / c_sabmRescue(+RescueAny) / c_sabmStart

Investigate the three real native `StationAbilityBitMask` (`c_sabm*`) bits missing
from the port, wire the one with a real gameplay consumer, and document the rest
honestly. Companion to the round-4 family-completeness audit
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-missing-arcs-round2.md` §StationAbilityBitMask).

## Bit values (native `Igc/igc.h:744-759`) — corrected vs the wiki's prose list

The wiki's Round-4 section lists the members textually but not by value; the
authoritative values are:

```
c_sabmUnload = 0x01   c_sabmStart = 0x02   c_sabmRestart = 0x04
c_sabmRipcord = 0x08  c_sabmCapture = 0x10 c_sabmLand = 0x20
c_sabmRepair = 0x40   c_sabmRemoteLeadIndicator = 0x80  c_sabmReload = 0x100
c_sabmFlag = 0x200    c_sabmPedestal = 0x400  c_sabmTeleportUnload = 0x800 (dead)
c_sabmCapLand = 0x1000  c_sabmRescue = 0x2000  c_sabmRescueAny = 0x4000
```

Before this task the port decoded 9 of 14 real bits (Unload/Restart/Ripcord/Land/
Flag/Pedestal/Repair/Reload/RemoteLeadIndicator). P301 adds the 5 remaining real
bits — **Start(0x02), Capture(0x10), CapLand(0x1000), Rescue(0x2000), RescueAny(0x4000)**
— all read via `DataStationTypeIGC.HasCapability` (the same one-line convention
the P300 hull family used). `TeleportUnload` stays excluded (native's own
"removed 7/23/09" dead code, `common.cpp:314`).

## Findings per bit

### c_sabmCapture = 0x10 — NO real gate; correctly NOT wired (do-not-"fix")

Native has **no gameplay consumer** of this bit. An exhaustive grep of the whole
tree finds only: the definition (`igc.h:748`), the string decode
(`missionigc.cpp:272`), the debug dump (`missionigc.cpp:4333`), and the Wopr
constants mirror. The real capture entry point `CFSShip::CaptureStation`
(`FedSrv/fsship.cpp:577-631`) re-sides the station and broadcasts
`STATION_CAPTURE` **without ever consulting the bit**; it is gated purely by a
`c_habmBoard` hull docking an enemy station (the port's P126 capture,
`Sector.cs` ~2436, mirrors this faithfully). Real PCore001 data confirms the bit
is not a discriminator: almost every capturable base carries it (e.g. Garrison
#15 sabm=0x0377 has Capture=0x10), but the mechanic never reads it.

Conclusion: native treats every station as capturable regardless of the bit, so
**adding a gate here would be unfaithful**. Decoded for family completeness with
a doc note so a future reader does not "fix" it the wrong way. No live-test
requirement (out-of-scope outcome), only this citation.

### c_sabmRescue = 0x2000 — REAL consumer → wired (the task's central fix)

Native's OT_station collision branch (`shipIGC.cpp:862-867`):

```cpp
else if (m_myHullType.HasCapability(c_habmLifepod) &&
         pst->HasCapability(c_sabmRescue) &&
         ((GetSide() == pModel->GetSide()) || IsideIGC::AlliedSides(...)))
{
    if (GetMyMission()->GetIgcSite()->RescueShipEvent(this, NULL))
        break;
}
```

i.e. a drifting `c_habmLifepod` that reaches a **same-side/allied** station whose
type carries `c_sabmRescue` is rescued. `FedSrvSiteBase::RescueShipEvent`
(`FedSrv.CPP:13033-13074`): with `pshipRescuer == NULL` (a station, not a ship)
the whole `PLAYER_RESCUED`/score/Steam block is skipped — the pod is simply
docked at a friendly station. The port's P115 ship-ship rescue
(`Sector.ResolveRescueEvents`) and P279 oxygen timer never modelled this
**station** half — so a pod drifting to a rescue-capable friendly base had no
way to be saved in the port. This is the real gap P301 closes.

The other native consumers of the bit corroborate it: the lifepod waypoint/target
classifiers require `c_sabmRescue | c_sabmLand` to treat a base as a rescue point
(`shipIGC.cpp:4028-4032`, `shipIGC.h:2126-2132`), and the client eject-target
search uses `c_sabmRescue | c_sabmRescueAny | c_sabmLand` (`clintlib/appmsg.cpp:233`).

**Real core data makes the gate meaningful.** In PCore001 the standard
Garrison/Supremacy/Expansion/Tactical/Outpost/Shipyard types do **not** carry
`c_sabmRescue`; only Refineries (0x2801), SpecMines (0x2000/0x2008), some
Teleports (0x2008) and TeleRefineries (0x2809) do. So in a real game most bases
do NOT auto-rescue pods — the bit selects specific rescue-capable installations
(and is how Iron Coalition's "all stations rescue pods" faction trait would be
expressed). The port's own pre-existing dock pass lets any same-side ship
(including a pod) dock at any friendly station regardless of hull, which is a
separate, pre-existing breadth-of-landing divergence (`c_sabmCapLand`-class
work, explicitly out of scope) — it does not obviate this rescue bit.

**Wiring** (all new, builds clean, 6 new tests):
- `DataStationTypeIGC.SabmRescue = 0x2000` (decoded, read via `HasCapability`).
- `Sector.ResolveStationRescueEvents()` — a per-tick pass (native
  `shipIGC.cpp:862-867`): a flying, placed, alive, in-mission `c_habmLifepod`
  ship within `0.5·Length + station.Radius` of a same-side, non-destroyed station
  whose type `HasCapability(SabmRescue)` is queued. Uses the port's established
  point-in-sphere proximity convention (like the dock/mining/rescue passes); the
  allied leg collapses to same-side (no alliance concept, same simplification as
  every `AlliedSides` gate in this arc). **Only `c_sabmRescue` is consulted** —
  `c_sabmRescueAny` is not used for stations in native.
- `Sector.DrainStationRescues()` — the accumulate/drain seam (mirrors
  `DrainRescues`), drained on the sim thread.
- `Server.PerformStationRescue(rescuee, station)` — the NULL-rescuer half of
  `RescueShipEvent`: **no** `PLAYER_RESCUED` wire, **no** score; docks the pod at
  the rescue station (moving its sector + latches `Ship.StationId` + points the
  owning peer's `LiveShip.Sector`), which is exactly the latch that **cancels the
  P279 oxygen timer** (`Ship.OnUpdate`'s `StationId is null` conjunct,
  `Ship.cs:5538`). P279 itself needs no change — its dock-cancels-oxygen gate is
  what this new rescue path feeds into.

### c_sabmRescueAny = 0x4000 — decoded, not wired (native reserves it for stations)

`igc.h:758`: *"not used (but reserved for pods)"*. The only STATION consumer of
either rescue bit, the shipIGC.cpp:862 collision branch, tests `c_sabmRescue`
only. `c_sabmRescueAny`'s real uses are the **client-side** eject-target
classifier (`clintlib/appmsg.cpp:233`, `WinTrek/WinTrek.cpp:10225,10253`) and the
**expendable** twin `c_eabmRescueAny` (`igc.h:782`, probe rescue — a separate,
out-of-scope mechanic; the port's `Probe.cs:213` already notes it is not
modelled). Decoded for completeness; P301's station rescue gates on
`SabmRescue` only, faithful to native.

### c_sabmStart = 0x02 — decoded, NOT connected to the spawn-hull gap (future work)

Native's **only** gameplay consumer of `c_sabmStart` is `PopulateCluster`
(`common.cpp:2981-2998`): in a **flags game** it places a flag-pedestal pair at
every `c_sabmStart` station. It has **nothing** to do with per-player spawn
hull/location selection. The task's hypothesis — that `c_sabmStart` connects to
the port's open "every player spawns via one hardcoded default hull" gap — is
**disproven** by the source: that gap is a separate concern with no `c_sabmStart`
consumer. The other native appearances are the string decode
(`missionigc.cpp:266`) and debug dump (`missionigc.cpp:4327`).

The port has no flags-game pedestal-population infrastructure (the only
`SabmPedestal` use is the indestructibility gate in `Station.ReceiveDamage`), so
wiring `c_sabmStart` would require building the whole flags-game start
population — named as separate future work, not half-built here. Decoded for the
family's completeness (P300 precedent).

### c_sabmCapLand = 0x1000 — decoded, not wired (named as future work)

Read by native's OT_station dock gate (`shipIGC.cpp:725-727`,
`fighter ? c_sabmLand : c_sabmCapLand`) and the lifepod waypoint/target
classifiers (`shipIGC.cpp:4028-4032`, `shipIGC.h:2126-2128`). The port's dock
pass does not yet distinguish fighter vs capital/pod landing eligibility — that
is the separately-scoped capital-ship/boarding/landing-eligibility work, not this
task. Decoded for completeness.

## Out of scope (confirmed, not built)
- Larger "spawn hull doesn't reflect chosen civ" gap — unrelated to `c_sabmStart`.
- `c_sabmTeleportUnload` — native dead code ("removed 7/23/09").
- Probe rescue (`c_eabmRescueAny`) — expendable family, separate mechanic.
- Capital-ship landing eligibility (`c_sabmCapLand` narrowing of the dock pass).

## Live proof
- **Model tests** (`StationRescueModelTests`, +4): real core/map mission with
  synthetic stations (real garrison #15 type, Radius 423.5, capability mask
  overridden). Pod parked 300u (inside the 425.5 rescue threshold, outside the
  200 DockRadius) so the outcome is attributable to the rescue bit alone.
  - Positive: `c_sabmRescue` station detects the lifepod (DrainStationRescues),
    re-fired each tick (no latch, native-atomic).
  - Negative: real garrison caps (0x0377, no rescue) → not detected; opposite-side
    `c_sabmRescue` station → not detected (same-side gate); out-of-range → not
    detected (proximity gate).
- **Live server tests** (`LiveStationRescueTests`, +2): real in-process server +
  real loopback Bot + real player lifepod + injected `c_sabmRescue` station in
  the no-station fly sector.
  - Positive: pod reaches the `c_sabmRescue` station → DOCKED there
    (`StationId` = the oxygen-cancel latch) with **no** `PLAYER_RESCUED` wire
    (NULL-rescuer fidelity).
  - Negative: same geometry at a non-rescue garrison → not docked, no wire.
- **Must-fail-if-removed**: disabling the `ResolveStationRescueEvents()` call
  turns the two positive tests red (detection empty / never docked); the
  negatives are trivially-red-still-green (they assert absence).

## Build / test status
- Whole-solution build: 0 warnings, 0 errors.
- New: 6 tests (`StationRescueModelTests` ×4, `LiveStationRescueTests` ×2), all
  pass in isolation; must-fail verified.
- Regressions: P279 `LiveLifepodOxygenTests` + P115 `LiveRescueTests` (7 tests)
  still green. Full-suite run N of the task's ≥2 runs logged in the commit
  message/stats; only the documented flakes may appear.

## Citations
- Native bits: `Igc/igc.h:744-759`; decode dump `missionigc.cpp:266-292`;
  debug dump `missionigc.cpp:4325-4356`.
- `c_sabmRescue` collision branch: `shipIGC.cpp:862-867`; `RescueShipEvent`
  `FedSrv.CPP:13033-13074`; lifepod waypoint `shipIGC.cpp:4028-4032`;
  target classify `shipIGC.h:2126-2132`; client eject target
  `clintlib/appmsg.cpp:233`.
- `c_sabmCapture`: `CFSShip::CaptureStation` `FedSrv/fsship.cpp:577-631`;
  `CaptureStationEvent` `FedSrv.CPP:13080`; port capture `Sector.cs` ~2436.
- `c_sabmStart`: `PopulateCluster` `common.cpp:2981-2998`.
- `c_sabmCapLand`: `shipIGC.cpp:725-727`, `4028-4032`, `shipIGC.h:2126-2128`.
- Real-core data: PCore001 station-type `sabmCapabilities` and civ lifepod hulls
  dumped via the port's decoder (Garrison #15 sabm=0x0377 Radius=423.5;
  Refinery 0x2801; SpecMine 0x2008/0x2000; lifepod dreghpod habm=0x00A4
  includes `HabmLifepod`=0x04, Length=4).
