# RESCUE_SHIP_EVENT_P115 — the real PLAYER_RESCUED(16) trigger

**Branch:** `master` (task started at `02cc29d`, P114; HEAD now `02cc29d` + this slice)
**Date:** 2026-09-09
**Closes the P104 deferral:** P104 shipped the `PLAYER_RESCUED`(16) wire codec
explicitly deferring its trigger "pending a real capability-bit decode"; P114
landed exactly that decode (`HabmRescue`/`HabmLifepod`); this task wires the
trigger. Self-contained, capability-bit-only — no landing-bay geometry, no
`RELAUNCH_SHIP`, no ship-vs-ship physics system (the point-in-sphere
proximity-sweep convention is this port's deliberate, established stand-in).

---

## 1. What the native trigger is

Quoted/cited in the brief: the friendly ship-vs-ship rescue branch of
`CshipIGC::HandleCollision` (`Igc/shipIGC.cpp:1022-1058`, the same block P114
read in full for the carrier-landing arc — only the rescue branch mattered
here) and `FedSrvSiteBase::RescueShipEvent` (`FedSrv/FedSrv.CPP:13033-13074`).
Symmetric: whichever colliding same-side ship is the lifepod, the other (if
`c_habmRescue`) rescues it. I reproduced that symmetry in the port's pass
verbatim.

---

## 2. What was built

### `src/Allegiance.Core/Model/Sector.cs`
- **`_pendingRescues`** field (`Sector.cs:106`): ship pairs detected this tick,
  same accumulate-per-tick / drain-after-`Mission.Update` split the ripcord
  pending lists (P112/P113) established — the Core side never sends wire.
- **`ResolveRescueEvents()`** (`Sector.cs:348-404`), a new per-tick pass called
  from `Sector.Update` (`Sector.cs:232`) alongside `ResolveDockCollisions` /
  `ResolveRipcordCountdowns` / etc. For every distinct **same-side** pair of
  flying (not dead / unplaced / docked) ships in the sector, if exactly one
  carries `DataHullTypeIGC.HabmLifepod` (`0x04`, `DataHullTypeIGC.cs:72`) and
  the other `HabmRescue` (`0x02`, `:98`), and the two are within proximity, the
  pair is queued.
  - **Symmetric roles** (`Sector.cs:373-386`): both `if` orientations
    (lifepod-first / rescue-first) resolve to `(rescuee, rescuer)` — direction
    free, matching `shipIGC.cpp:1027-1035`.
  - **Same-side gate** (`Sector.cs:371-372`): `a.SideId != b.SideId →
    continue`, the port of `bFriendly` (`shipIGC.cpp:1023-1026`); no alliance
    concept, so the allied leg collapses (the same simplification P112's
    `RipcordAnchorStillLegal` cites for its `bAllowAlliedRip` drop).
  - **Proximity** (`Sector.cs:389-396`): sum of the two ships' `0.5 ×
    HullType.Length` sphere radii (the port's established extent convention,
    P113/P114 precedent) — the ship-ship analogue of the dock/mining passes'
    `objectRadius + shipRadius` point-in-sphere test, and the `// Point-in-sphere`
    simplification of `ResolveDockCollisions` (`Sector.cs:1269`).
  - **Latch** (`Sector.cs:353-356`, 402-403): a docked pod is excluded (after
    a successful rescue the pod's `StationId` is set, so it cannot re-fire next
    tick — mirroring the dock pass's latch), and each ship joins at most one
    rescue per tick.
- **`DrainRescues()`** (`Sector.cs:302-307`): returns and clears the tick's
  pairs for the host.

### `src/Allegiance.Server/Server.cs`
- **SimPass drain** (`Server.cs:427-433`): after the ripcord teleport drain,
  before the state broadcast, `PerformRescue` runs on each drained pair so the
  docked rescuee's state is picked up by the same tick's SINGLE_SHIP_UPDATE /
  SHIP_STATUS.
- **`PerformRescue(Ship rescuee, Ship rescuer)`** (`Server.cs:2850`), the port
  of `RescueShipEvent`:
  1. **`PLAYER_RESCUED`(16) to both connections** (`Server.cs:2864-2871`) —
     constructs `PlayerRescued { ShipIdRescuer = rescuer.Id, ShipIdRescuee =
     rescuee.Id }` and sends to the peers owning either ship. Peer-resolution
     doubles as the native's `if (pshipRescuer)` "is a player/connection" gate:
     a non-peer ship's send is skipped (placement below still runs).
  2. **Score / Steam — N/A, skipped** (see §3).
  3. **`ResetWarpState()` — no equivalent needed** (see §3).
  4. **Station search** — reuses `FindFriendlyStation` (see §3).
  5. **Placement** — reuses `PerformTeleport`'s relocation core (see §3).

### `src/Allegiance.Bot/Bot.cs`
- `PlayerRescued` collector (`_playerRescueds`, `PlayerRescueds`,
  `ResetPlayerRescueds`, dispatch `case PlayerRescued.Fmid`, `Bot.cs:603-605`) —
  so the loopback bots can observe and assert the frame (same pattern as the
  ripcord collectors).

### `tests/Allegiance.Core.Tests/LiveRescueTests.cs` (new, 5 tests)
Real loopback `Bot` vs in-process `Server` on XXV_simple. Two ships injected
via `ShipSpawner` into the no-station fly sector 1052, hulls cloned from the
lowest-id non-`c_habmNoRipcord` hull with exactly the requested capability mask;
the real friendly station is the side-0 Garrison (id 1051) in sector 1053.
Positive cases spawn the pair out of range (so the join pump does not fire the
rescue), then drift one ship onto the other and pump.

---

## 3. The findings the brief asked me to confirm (each read, not guessed)

### 3a. Score / Steam — N/A, confirmed, skipped
`Wire/PlayerInfo.cs:168-207` encodes `rgPersistPlayerScores` as an empty var
item, and `PlayerInfo.cs:207` **throws** if a decode ever presents a non-empty
score list ("score history not supported"). There is no score system, no
`AddRescue()`/`GetPlayerScoreObject()`, and no Steam integration anywhere in the
port (grep over src finds neither). Native's `AddRescue`/`AwardPodPickup`/
`AwardGetRescued` (`FedSrv.CPP:13039-13051`) are therefore **intentionally
skipped**, the same precedent every score/Steam-adjacent gap this arc has already
hit (P104 named the same absence when deferring this very trigger). Stated in
`Server.cs:2807-2811`.

### 3b. `ResetWarpState()` — no real equivalent needed here
Native `ResetWarpState()` sets `m_warpState = warpReady` (`fsship.h:199-201`)
— re-arming the rescued ship so it can warp/dock again after a recent transit.
This port's warp-cooldown stand-in is `Ship.LastWarpTime` /
`Sector.WarpCooldownSeconds` (the collapsed `warpNoUpdate→warpWaiting→warpReady`
machine, `Ship.cs:123-135`, `fsship.cpp:1185-1191`). Reading **every other call
site** of `LastWarpTime` (the brief's instruction): it is only ever consulted as
a debounce by the two **proximity** passes — `ResolveWarpCollisions`
(`Sector.cs:1017`) and `ResolveDockCollisions` (`Sector.cs:1255`) — to stop a
just-transited ship re-crossing/re-docking. The rescue places the rescuee by a
**direct** dock (`ship.StationId = ...`, below) that never enters either pass, so
no warp-readiness gate stands between the rescue and its dock. Clearing
`LastWarpTime` would only prematurely drop a legitimate 2 s re-warp debounce with
zero benefit to the direct placement. **Conclusion: the existing cooldown already
serves `warpState`'s debounce purpose; no equivalent call is required.** Stated
in `Server.cs:2812-2822`.

### 3c. Station search — reused `FindFriendlyStation`, not reimplemented
P105's `FindFriendlyStation(Ship)` (`Server.cs:4177-4186`) is the port's
established stand-in for the native
`FindTarget(..., c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttNearest, ...,
c_sabmRestart)` nearest-friendly-station search (`FedSrv.CPP:12034-12038`, and
the same `c_ttFriendly | c_ttStation | c_ttNearest` shape at `Server.cs:4162`).
It already returns a **same-side** station (any friendly match, first-found — the
documented simplification of "nearest"). Because the native's fallback is
"if the found station's side != the rescuee's side, use the side base"
(`FedSrv.CPP:13067-13071`), and `FindFriendlyStation` can never return an
opposite-side station, that fallback is **inherently satisfied** — a same-side
station is never discarded. When no friendly station exists anywhere, the native
returns `false` with no dock (`FedSrv.CPP:13075`); mirrored (wire already sent,
no placement, `Server.cs:2881-2885`).

### 3d. Placement — reused `PerformTeleport`'s relocation core
`Server.PerformTeleport`'s relocation core (`Server.cs:3284-3299`, P110) is this
port's existing "put this ship at this station" mechanic: move to the station's
cluster, latch `Ship.StationId` (and any boarded children), and point the owning
peer's `LiveShip.Sector` snapshot at the station's cluster so the next SHIP_STATUS
reads the right sector. `PerformRescue` reproduces exactly that core
(`Server.cs:2887-2896`) — no new placement logic. Like the P110 teleport path, no
`DockWithStationEvent` seam / `FM_S_DOCKED`(61) is raised on the rescue (the
direct-dock latch is sufficient; the docked state is conveyed by the periodic
SHIP_STATUS(90)). Stated in `Server.cs:2826-2830`.

---

## 4. Verification — real loopback evidence for all four cases

Captured from the live test run (real `[rescue]` server logs + assertions):

| Case | Outcome |
|---|---|
| **1. Same-side lifepod drifts in range** | `[rescue] ship 8096 (lifepod) rescued by ship 8097 -> PLAYER_RESCUED(16) rescuer=8097/rescuee=8096; placed docked at station 1051 'Garrison' in sector 'Ceto'` — both bots received the same frame with `ShipIdRescuer=8097`, `ShipIdRescuee=8096`; pod `StationId != null`, station side 0, pod sector 1053. |
| **2. Opposite-side pair** | `RESCUE-NEG-SIDE: ... side 0 lifepod + side 1 rescuer on top of each other -> NO PLAYER_RESCUED, pod stays flying` — no frame on either bot, pod `StationId` null. **Plus the wrong-capability variant:** `RESCUE-NEG-CAPS: ... lifepod + no rescue bit ... -> NO PLAYER_RESCUED`. |
| **3. Out-of-range pair** | `RESCUE-NEG-RANGE: ... same-side capable but 1000u apart (> 12 threshold) -> NO PLAYER_RESCUED, pod stays flying` — test also asserts separation exceeds the sum-of-radii first. |
| **4. Direction doesn't matter** | Rescuer-drifts-to-pod (`RESCUE-LIVE`) and pod-drifts-to-rescuer (`RESCUE-SYM`, swapped spawn order) both fire with the **correct** capability-derived roles; both pods end up docked at Garrison. |

5/5 `LiveRescueTests` pass.

### Full suite
`/home/domingo/.dotnet/dotnet test` (explicit path; the PATH `dotnet` is the
broken snap stub). Six consecutive runs logged:
`2505/2505` clean on **five** of them (runs 2, 3, 4, 5, 6) — **three consecutive
clean runs (4, 5, 6) satisfy the commit gate**. The single failure was on run 1:
`LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire`
(`InvalidOperationException: no shooter ship`, `LiveCombatWireTests.cs:310`).
Investigated, not hand-waved: that test pumps until the TARGET's
`ShipAssignment` is set (`LiveCombatWireTests.cs:309`) then immediately reads the
SHOOTER's (`:310`), an inherent live race. I reproduced it **in isolation**
(2/12 isolated runs failed with none of my tests running), proving it is a
pre-existing live flake in that test — the P115 rescue pass is a no-op for the
combat fight-spawn (no lifepod/rescue-capable pairs), the SimPass rescue drain
is empty for the combat mission, and the Bot change is an additive dispatch case.
It is a second, separate pre-existing flake alongside the documented
`LiveChatSectorWireTests` ~1-in-8 timing flake (P108-P114); neither is a P115
regression. Baseline at P114 HEAD was **2500**; the delta is the **+5** rescue
tests (net new; no existing test needed superseding — the ripcord/P110-P114 code
is unchanged).

---

## 5. Scope compliance
- ✅ Real `PLAYER_RESCUED`(16) trigger wired (capability bits + proximity sweep).
- ✅ `Server.PerformRescue` (named per the task), modelled on `PerformRipcordTeleport`'s shape.
- ✅ Score/Steam skipped with the cited finding (§3a).
- ✅ `ResetWarpState` answered by reading all `LastWarpTime` call sites (§3b).
- ✅ Station search reused (`FindFriendlyStation`), placement reused (`PerformTeleport` core).
- ✅ Out of scope honored: no `RELAUNCH_SHIP`/`LandOnCarrierEvent`/bay geometry, no change to P110-P114 code, no rigid-body collision system (the proximity-sweep convention is deliberate).

## 6. Files changed
- `src/Allegiance.Core/Model/Sector.cs` — `_pendingRescues`, `ResolveRescueEvents`, `DrainRescues`, `Update` hook.
- `src/Allegiance.Server/Server.cs` — `PerformRescue`, `SimPass` rescue drain.
- `src/Allegiance.Bot/Bot.cs` — `PlayerRescued` collector (+ dispatch).
- `tests/Allegiance.Core.Tests/LiveRescueTests.cs` — new (5 live tests).
- `RESCUE_SHIP_EVENT_P115.md` — this report.

Session stats logged via `~/Development/allegiance-tools/session_stats.py`.
