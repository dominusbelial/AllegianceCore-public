# P797 — native map discovery: alephs, asteroids and stations reach a side only once it has seen them

Base: **master** `be4c3707` (re-checked with `git log -1` at start; every native line
number below was re-grepped before use, and the two the brief got slightly wrong are
called out as **corrected**). Worktree `allegiance-pi-p797`, branch `pi/p797`, model
DeepSeek. No commits, no pushes — everything is in the working tree. Reference tree:
`~/Development/Allegiance/src/`.

Delivered: alephs are now **per-side seen models** in the Core, the join export is
**per side** exactly as native `SendMissionInfo`, a **discovery sweep** sends a newly
seen static object to the seeing side mid-game (EXPORT + `OBJECT_SPOTTED`), the two
native map-reveal mission options are ported (default = native's off), and the client
needs no change because it already routes over the warps it has (`StarmapGotoRouter`
over `world.Warps`), which *is* the native rule (`common.cpp:916` `CanSee`).

---

## 1. The native rule (each brief item confirmed or corrected)

### 1.1 Join export is per side — confirmed

`CFSMission::SendMissionInfo(CFSPlayer*, IsideIGC*)` (`FedSrv/fsmission.cpp:4108`) is sent
**per side at join** (`:736` from `AddPlayerToSide`, `:2031` to a whole side):

| what | cite | gate |
|---|---|---|
| every cluster `ExportObj(pcluster, OT_cluster)` | `fsmission.cpp:4170-4174` | **none** (whole mission) |
| `CLUSTERINFO`(200) topology overview, **no positions** | `fsmission.cpp:4122-4166`, `Inc/Messages.h:1039` | **none** |
| alephs | `fsmission.cpp:4175-4186` (`if (…SeenBySide(pside))` at `:4181`) | SeenBySide |
| aleph bombs | `:4188-4199` (`:4189`) | SeenBySide |
| asteroids + their building effects | `:4201-4213` (`:4206`; `GetBuildingEffect()` → `:4209-4211`) | SeenBySide |
| stations | `:4215-4222` (`:4220`) | SeenBySide |
| treasures | `:4224-4231` (`:4229`) | SeenBySide |
| mines | `:4233-4240` (`:4238`) | SeenBySide |
| probes | `:4242-4249` (`:4247`) | SeenBySide |

### 1.2 Alephs are ordinary seen-by-side, predictable static models — confirmed

`CwarpIGC::Initialize` loads every aleph with
`c_mtStatic | c_mtHitable | c_mtSeenBySide | c_mtPredictable` (`Igc/warpIGC.cpp:35`), so
it takes part in the cluster's periodic sweep — `if ((m_nPass++) % c_nPassesPerUpdate == 0)
{ … UpdateSeenBySide() … }` (`Igc/clusterIGC.cpp:347-357`) → `ThingSiteImpl::UpdateSideVisibility`
(`FedSrv/FedSrv.CPP:10876-11129`).

### 1.3 Does a static model's "seen" ever revert? — **No.** Confirmed, and this was the
port's biggest divergence

In `UpdateSideVisibility` the lose-sight branch is
`else { sv.fVisible(false); … }`, but when the object had been seen before:

```c
if (!sv.fVisible() && seenBefore) {
    sv.CurrentEyed(false);
    sv.pLastSpotter(NULL);
    if (m_bPredictable) //keep static objects visible (but not currentEyed)
        sv.fVisible(true);
    else
        HideObject(pside, pmodel);
}
```
— `FedSrv/FedSrv.CPP:11117-11129`. `m_bPredictable` is set from the model's attributes
(`ThingSiteImpl::ThingSiteImpl`, `:10694-10702`), so **every static object latches**: warp
(`warpIGC.cpp:35`), asteroid (`asteroidigc.cpp:36`), station (`stationIGC.cpp:244`), mine
(`mineigc.cpp:104`), probe (`probeigc.cpp:115`), treasure (`treasureIGC.cpp:167-177`).
Only ships are non-predictable, so only ships' `SeenBySide` drops when the last scanner
leaves. A destroyed/rebuilt station is a **new object** (`CstationIGC` terminated and
re-created), so it starts with a fresh all-false array — "seen" reverts only by the object
ceasing to exist, never by a scanner leaving.

The port wrote `false` for every unseen target, so a side forgot a rock/station/aleph as
soon as its last scanner moved away. `SideVisibilityState.Predictable` (new) now
reproduces the native latch: the visible flag latches, the current-eye flag still decays.
That also fixes the earlier P453 mine assertions (two mine tests re-pointed at the native
rule — see §5).

### 1.4 Home-sector visibility at start — confirmed (this is what seeds a side's knowledge)

Two separate mechanisms, both defaulting to **off**:

* `bShowHomeSector` (`Igc/igc.h:1148`, default `false` at `igc.h:1313`) →
  `CmapMakerIGC::RevealHomeClusters` (`Igc/mapmakerigc.cpp:969-997`, called `:470-471`):
  "Make every model in the station's cluster visible to the side" —
  `pml->data()->SetSideVisibility(pside, true)`.
* `bShowMap` (`Igc/igc.h:1123`, default `false` at `igc.h:1244`) →
  `RevealMap` (`mapmakerigc.cpp:999-1018`, called `:473-477`, repeated at mission start by
  `Igc/missionigc.cpp:4056-4071`): every warp visible to every side.

With both off (native default), a side starts knowing exactly:

* its **own** stations/ships — the own-side rule
  `if (pside == pmodel->GetSide()) { sv.fVisible(true); … }` (`FedSrv.CPP:10907-10912`);
* whatever its **scanners** cover on the first sweep pass — stations carry
  `c_mtScanner` and a `ScannerRange` (`stationIGC.cpp:244`, `stationIGC.h:308-328`), so the
  home sector's nearby alephs/rocks are normally known, and the rest of the map is not.

Port: `Mission.SeedStartingVisibility()` applies the two optional reveals and then runs one
full `Sector.ResolveCrossSideVisibility()` pass, so the join export (and the first
`ASTEROIDS/STATIONS_UPDATE`) read real flags even before the first live sector tick. That is
the port's equivalent of native's first `Cluster::Update` pass.

### 1.5 How the client learns of a newly seen aleph/asteroid/station — confirmed

`UpdateSideVisibility` calls `ThingSiteImpl::ShowObject(pside, pmodel)` on the false→true
edge (`FedSrv.CPP:10925,10944-10950`). `ShowObject` (`:10750-10784`) sets the side's
`FM_GUARANTEED` default recipient and `ExportObj(pmodel, type, NULL)` to the **whole side**
("Tell everyone on the side about the newly visible static object", `:10766-10775`), then

* for an **aleph**: `ExportObj(pwarpOther, OT_warp, NULL)` for the partner and a forced
  `sv.fVisible(true)` on it — *"perform a hack here: if a aleph becomes visible, make its
  corresponding aleph visible"*, `FedSrv.CPP:10786-10800` — plus `WARP_BOMB` for both ends'
  bombs (`:10802-10817`);
* for an **asteroid**: `ExportObj(pbe, OT_buildingEffect, NULL)` when a build is running
  (`:10819-10825`);
* and then emits `OBJECT_SPOTTED`(6) — `otObject/oidObject` from the seen model,
  `otSpotter/oidSpotter` from the spotting scanner (`sv.pLastSpotter(s)`, `:10944`) — for a
  **station**, an **aleph**, or an asteroid whose
  `(capabilities & ~c_aabmBuildable) != 0` (`:11035-11062`; `c_aabmBuildable = 0x08`,
  `Igc/igc.h:765`). The spotting side's own ships also get scoring hooks
  (`SpotWarp`/`SpotSpecialAsteroid`, `:11063-11095`, out of scope here).

### 1.6 An unknown aleph and the autopilot — confirmed (the client already behaves this way)

Native's route finder is seeded **only with alephs the ship can see**:

```c
for (WarpLinkIGC* wLink = pwarps->first(); …) {
    IwarpIGC* pwarp = wLink->data();
    if (pship->CanSee(pwarp)) { … push the path … }
}
if (unexplored.n() == 0)
    return NULL;                    // "Never found a path"
```
— `Igc/common.cpp:894-960` (`CanSee` gate at `:916`, the NULL return `:955-956`);
`CanSee` = same side || `SeenBySide(side)` || `InScannerRange` (`Igc/shipIGC.h:380-388`).
`FindPath` returning NULL is what makes the autopilot refuse a route over an unknown aleph
(`Igc/common.cpp:1905-1907`).

The port's client stores only the warps it was exported (`ClientWorld.Warps`) and
`StarmapGotoRouter.TryNextWaypointFromWarps` builds its adjacency from exactly that table,
returning `false` when the next hop's warp is unknown (`StarmapGotoRouter.cs:53-120`;
callers treat `false` as "lost route", `Game1.FlightInput.cs:3415`, `Game1.Targeting.cs:769`).
So an unknown aleph is simply absent and routing over topology to a sector whose aleph is
unknown yields "no path" — the native behaviour, with **no client change needed**. The
sector map draws its edges from `CLUSTERINFO` (known topology) and its alephs from
`world.Warps` (known positions); radar likewise. Verified: no code path invents an aleph
from the graph.

### 1.7 A ship warping through an unseen aleph — confirmed

`CFSShip::ShipStatusWarped` only updates a side's ship status when
`GetIGCShip()->SeenBySide(pside) && pwarp->SeenBySide(pside)` (`FedSrv/fsship.cpp:481-495`),
i.e. a warp through an aleph the side has not seen moves no intel. The port already gates
per-side ship status on `SeenBySide` (P493 `SweepCrossSideShipStatuses`), so nothing to do.

### 1.8 Ore knowledge — confirmed (already ported; PAR-5 not closed)

`oreSeenBySide` (`Igc/asteroidigc.h:78-323`; written only for `GetCurrentEye(pside)` and not
while `m_inhibitUpdate`, `:70-82`) is summed per cluster by
`CclusterIGC::GetOre` — **`Igc/clusterIGC.h:266-275`** (the brief said `clusterIGC.cpp:271`;
**corrected**: the sum lives in the header) and the radar fill uses
`GetOreSeenBySide / (2·capacity)` (`WinTrek/radarimage.cpp:1051-1062`). The port's
`Asteroid.GetOreSeenBySide` / `UpdateOreKnowledge` already implement the native store
(P747), and `ASTEROIDS_UPDATE` sends native's `GetOre()` (`FedSrv/fsship.cpp:883-915`,
`GetOre` at `:909`) exactly as the port did. **PAR-5 is left as-is** and re-confirmed: the
missing piece is the *client-side* `oreSeen/(2·capacity)` fill, which needs a wire value
native does not carry (native's client maintains its own `oreSeenBySide` in its IGC; the
port's client has no IGC), so it does not fall out of this task.

---

## 2. What the port sent before / after, per side

Before P797 (`Server.Lobby.cs`), every peer got **all** warps/stations/asteroids of the whole
mission regardless of side, and no mines/probes/treasures at join:

```
[export] peer 1 'a' -> 44 warp(s), 2 station(s), 134 asteroid(s) (whole mission, every side)
```

After P797 the same burst is filtered per side and logs seen/total:

```
[export] peer 1 'p797_a' side 0 -> 2/44 warp(s), 1/2 station(s), 0/134 asteroid(s), 0 building effect(s), 0 probe(s) (…; seen/total)
[export] peer 2 'p797_b' side 1 -> 2/44 warp(s), 1/2 station(s), 0/134 asteroid(s), …
[export] peer 3 'p797_late' side 0 -> 4/44 warp(s), 1/2 station(s), 133/134 asteroid(s), …   (after side 0 flew into Alderan)
```

`CLUSTERINFO` is unchanged (13 clusters / 44 edges on `m2xspiderw.igc`), matching native.
The 2 alephs at join are the partner pair: seeing one reveals the other (§1.5).

`[discover]` lines (one per newly seen object per side — the rig probe) look like:

```
[discover] side 0 aleph 2150 'Cirrus' sector 1052 -> 1 peer(s)
[discover] side 0 asteroid 3202 '' sector 1052 -> 1 peer(s)
```

## 3. Implementation map

**Core**

* `Sim/SideVisibilityState.cs` — `Predictable` latch (native `FedSrv.CPP:11124-11126`),
  `LastSpotter` / `SetLastSpotter` / `ClearLastSpotter` (native `sv.pLastSpotter`, `:10944`,
  `:11119-11120`), `RevealToSide` (the forced `fVisible(true)` of the aleph-pair hack).
* `Sim/Asteroid.cs`, `Sim/Station.cs`, `Sim/Mine.cs`, `Sim/Probe.cs`, `Sim/Treasure.cs` —
  `SideVisibility.Predictable = true` (native attribute masks above).
* `Model/ResolvedWarp.cs` — `ResolvedWarp : ISideVisible` with `SideVisibility`
  (predictable), `SeenBySide`, `GetCurrentEye`, `Position`, `Radius`. Alephs are now
  visibility-tracked models without becoming `Model`s (no second sweep, no threading
  change: they piggyback `Sector.ResolveCrossSideVisibility`).
* `Model/Sector.cs` — the sweep adds **warps** and **treasures** as target-only
  participants (`sideId: null`), records/clears the last spotter, and after the scan
  performs native's aleph-pair reveal (one pass; the relation is a pair and the target
  latches).
* `Model/Mission.cs` — `ShowMap` / `ShowHomeSector` (native defaults **false**),
  `SeedStartingVisibility()` (optional `RevealHomeClusters`/`RevealMap`, then one full
  sweep; reveals are one-shot, the sweep is one-shot — later live sweeps keep it current).

**Server**

* `Server.Lobby.cs` — the join burst's object loop is per side (`live.Ship.SideId`), in
  native's order and with native's gates: warps, asteroids + their building effect,
  stations, probes, mines. Treasures are deliberately **not** in the burst: the client
  wipes its treasure table at `SET_CLUSTER` (`ClientWorld.Apply(SetCluster)`), so the
  port's existing post-`SET_CLUSTER` `SendTreasureSnapshot` is the only place a treasure
  survives — it is now per side (`SendTreasureSnapshot`, `Server.Treasure.cs`). The
  Started transition calls `mission.SeedStartingVisibility()`.
* `Server.Broadcast.cs` — `SweepCrossSideDiscoveryExports` (new step
  `crossSideDiscoverySweep`, right after the P453 mine sweep): per side, for every warp /
  asteroid / probe / treasure with `SeenBySide`, send each seated peer of that side the
  same EXPORT the join path builds (`ExportWarp`/`ExportAsteroid`+effect/`ExportProbe`/
  `TreasureSpawn`) plus `OBJECT_SPOTTED` where native emits it, latched per
  `(peer, kind, id)` in `_crossSideDiscoverySent` so it happens exactly once and a
  later-joining peer is caught up by its own join export.
* `Server.Research.cs` — `ExportDiscoveredStations` now also emits `OBJECT_SPOTTED` for a
  newly discovered station (native `:11035-11062`), using the side's recorded spotter.
* `Server.Docking.cs` — shared `BuildingEffectExport`; per-(peer, asteroid) latch
  `_buildingEffectExportSent` so the creation broadcast, the join export and the discovery
  export never create the same effect twice (native `CreateObject` is not an upsert);
  `SendTreasureSpawnSectorWide` latches treasure creation broadcasts.
* `ServerConfig.cs` / `Server.Lifecycle.cs` — `GameConfig.ShowMap` / `ShowHomeSector` →
  `Mission.ShowMap` / `ShowHomeSector` (both default false = native).
* Latch lifecycle: pruned on disconnect / reseat (`Server.Lifecycle.cs`,
  `Server.Lobby.cs`), cleared by the broadcast test seam.

**Client** — no production change needed (§1.6). `ClientWorld` already upserts
warp/asteroid/station/treasure entities by id, `_warps`-sized caches re-derive on a count
change (`RefreshViewWarpCache`), and the goto/autopilot router routes only over known
warps. This was verified by reading the paths, not assumed.

## 4. Determinism

The sweep is not parallelised (only collision *detection* is, P667), so the visibility sets
are schedule-independent by construction; the new test pins it: serial vs
`ParallelSectorSim` with 1 / 4 / 8 workers produces identical per-object per-side seen
strings over a whole mission (`P797MapDiscoveryTests.Serial_and_parallel_sector_sim_compute_identical_visibility_sets`).

## 5. Tests

New (`tests/Allegiance.Core.Tests/`):

* `P797MapDiscoveryTests.cs` (6, pure Core, real core + corpus map)
  * an aleph is seen only by the side whose scanner reaches it (and not by the other side);
  * **native latch**: seen stays seen after the scanner leaves, `GetCurrentEye` drops;
  * seeing one aleph reveals its partner to that side only;
  * `ShowMap` off leaves alephs unknown, on reveals all (to every side);
  * `ShowHomeSector` reveals the home cluster's models only when enabled;
  * serial vs parallel visibility determinism.
* `P797MapDiscoveryLiveTests.cs` (2, loopback TCP, `m2xspiderw.igc`)
  * a fresh side's join export is exactly its side's seen set (warps/asteroids/stations),
    and `CLUSTERINFO` still arrives whole (13 clusters / 44 edges);
  * flying a side-A ship next to an aleph side A never saw sends it (EXPORT +
    `OBJECT_SPOTTED`) to A's peer only, the sector's rocks follow, the other side receives
    neither, and a late-joining peer on side A gets both from its own join export
    (`4/44` warps, `133/134` rocks).

Edited because P797 changes the rule they encoded:

* `MineVisibilitySweepTests` (2) — the "scanner leaves ⇒ mine unseen" pair now asserts the
  native predictable latch (seen stays true, eye false); the occlusion test places the
  occluder *before* the first sweep so it still proves `LineOfSightExist` (a latched mine
  could no longer demonstrate it).
* `AsteroidExportTests` — the bot now receives its side's seen rocks, not all 134; the test
  seats the ship with a real scanner next to a rock field and asserts
  received == side-seen and < all.
* `P480RotationKeepsPlayersLiveTests` — stations arrive per side; asserts the new world's
  own stations arrived and every exported station was seen by that side.
* Bot/showcase harness configs opt into the **native** `showMap` option (the harness bots
  cannot scout until P798): `P660NextMatchLiveTests.Config`, `P733ResearchTests.Config`,
  and the per-file configs of P634, P610, P539, P539b, P539c, P539d, P539LogBaseline,
  P553, P554, P622, P632, P635, P655ProbeWorkers, P496CaptureProof, RunHullMatrix,
  BotAcceptsAndEngagesOrder, ClientReadyUpUnfreezes, plus `ClientWarpApproach*`,
  `SpectatorFrameLiveTests`, `P541GotoFlyLiveTests`.
* Fixture-level reveals (documented in-file, each replaced by P798 scouting):
  `ClientWarpApproachDodgeLiveTests` (Alderan's rock field),
  `TargetPickSectorScopeLiveTests` (the map-wide pool its per-sector assertions need),
  `WorldPopulationLiveTests` (the map's rocks), `P663TreasureLiveTests` (the watched flags),
  `P656cFinaleLiveTests` (one enemy station for the finale planner),
  `TreasureDropLiveTests` (a scanner hull so the dropped part is visible; the auto-seat
  lowered from 30 wall-seconds to 1 so the late-join export lands while the part lives).
* `P693TeamLimitsLiveTests` — the 30v30 showcase now spawns with `finale_after_minutes: 0`
  (the finale planner needs an **observed** enemy home station,
  `BotManager.Finale.cs:83`, which fog removes); the test's subject (capacity/team limits)
  is unaffected; P798 restores the finale.

**Which fail on master?** The two new files do not compile against master (they use
`ResolvedWarp.SeenBySide`, `SideVisibilityState.Predictable`, `Mission.SeedStartingVisibility`
— all introduced here), and their behavioural assertions are exactly master's divergences:
master exports all 44 alephs / 134 rocks / 2 stations to every side, never latches a static
object's visibility, has no `[export] … seen/total` split and no discovery sweep. Behaviourally
the edited `MineVisibilitySweepTests` (latch) and `AsteroidExportTests` (seen-set equality)
also fail against master's behaviour.

## 6. Gate numbers

* Solution build `AllegianceCore.slnx`: **0 warnings / 0 errors**.
* `tools/build-tools.sh`: **71/71 tool projects built**, 0 warnings/errors.
* AOT publish (`src/Allegiance.Server` `-c Release -r linux-x64 --self-contained
  -p:PublishAot=true`): **clean**, no IL2026/IL3050.
* New/affected tests **3× alone**: `P797MapDiscoveryTests` + `P797MapDiscoveryLiveTests`
  8/8 ×3; the affected set (`MineVisibilitySweepTests`, `AsteroidExportTests`,
  `DropPartTests`, `LiveBuildingEffectStartWireTests`) 18/18 ×3; the affected live set
  (`P663Treasure`, `TreasureDrop`, `WorldPopulation`, `SpectatorFrame`,
  `P693TeamLimits`, `P656cFinale`) 14/14 ×3.
* **Fast tier** (`tools/run-tests.sh --tier fast`): **PASS — 10 059 tests, 284.4 s**
  (Lobby 24, MonoGame 3268 (+3 not executed), ClientCore 1674, Core 5078, quarantine 12).
  Summary copied to `~/allegiance-evidence/P797/fast-summary-final.json`; raw logs at
  `~/allegiance-evidence/P797/*-main.log`.
* **Showcase never-parks 5×** (`P700ShowcaseLiveTests`): **6/6 pass** (5 gate runs +
  1 instrumented). Instrumented run: `reached=26/30, longest speed<1 with target=2.005s,
  shots=2919, hits=886, kills=8`, 91.7 s. With fog **and no `showMap`** the same test
  reached only **16/30 with 0 hits** (bots cannot find the next aleph → they loiter, which
  is the sanctioned fallback), which is why the showcase harness config now enables the
  native option.
* Two late fixes were needed after the first fast-tier run and are recorded here honestly:
  the effect double-create (fixed with `_buildingEffectExportSent`) and the treasure
  wipe-vs-latch interaction at `SET_CLUSTER` (fixed by keeping treasures out of the join
  burst and not reading the discovery latch in the snapshot).

## 7. Divergences, left-overs, and what P798 inherits

1. **Bots cannot scout yet.** With native defaults a side knows only home-sector content.
   `TransitWarp` already loiters when the next-hop aleph is unknown
   (`BotManager.cs:2570-2579`), which is the minimal fallback; no MCP behaviour was
   changed. Every bot/showcase harness config listed in §5 now sets `showMap: true`
   (native lobby option) so the live suites keep testing what they test. **P798** should
   replace those with the scout opening and then remove the flags.
2. **`showMap` / `showHomeSector` on the rig**: the deployed `server.json` on 221 still has
   neither field (both default false = native). For a bot showcase run before P798 lands,
   set `"showMap": true` inside the game object.
3. **Server-side ship autopilot** (`Sector.FindPathToward`) still routes over the *full*
   mission graph. Native's `FindPath` gates on `CanSee` (§1.6); the port's client-side
   router already behaves natively, but the server's own GotoPlan does not. Left unchanged
   deliberately (it is the authoritative sim and the blast radius is large — every miner,
   pod and NPC route); flagged for a follow-up.
4. **PAR-5 (ore fill)** — unchanged, see §1.8.
5. **Treasures**: delivered by the creation broadcast + the per-side post-`SET_CLUSTER`
   snapshot + the discovery sweep for other sectors; their `OBJECT_SPOTTED` is not emitted
   (native's gate excludes treasure too).
6. **Probes** now get a cross-side identity EXPORT (they had none before — `PROBES_UPDATE`
   carries health only); the client renders them from the new export.
7. **Alliances**: `bAllowAlliedViz` remains dropped (existing simplification), so an allied
   side is treated like any other side for the discovery gate.

## 8. Rig recipe (for Claude, on 221)

1. Baseline (master) and P797 build, same `server.json` (no `showMap`), same map
   (`m2xspiderw.igc`, 2 sides), bots as usual.
2. Capture the join lines and compare the per-peer exports:
   ```
   [export] peer N '<name>' side S -> w/44 warp(s), s/2 station(s), r/134 asteroid(s), e building effect(s), p probe(s) …
   ```
   *before*: every peer shows `44 / 2 / 134`; *after*: only the side's seen fraction
   (fresh side: `2/44` — the partner aleph pair — `1/2`, `0/134`). Note the per-side
   difference between side 0 and side 1 peers.
3. Watch discovery as the bots move (one line per object per side, unconditional):
   ```
   [discover] side S aleph <id> '<name>' sector <sector> -> <n> peer(s)
   [discover] side S asteroid <id> '<name>' sector <sector> -> <n> peer(s)
   ```
   A scout entering a new sector should produce aleph + asteroid lines for **its** side
   only; a late-joining peer on that side then shows the larger seen/total numbers in its
   `[export]` line.
4. Compare `CLUSTERINFO` (unchanged: cluster + warp counts), `STATIONS_UPDATE`/`ASTEROIDS_UPDATE`
   per-side counts, and the showcase never-parks/idle metrics with `showMap` on (bots
   cannot scout until P798).
