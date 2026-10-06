# P537 — Stations, Docking & Building parity audit (P1–P200, part 7)

Date: 2026-10-05 · Read-only audit · Input: `docs/audits/p1-p200/stations.txt` (30 commits)
Method: for each commit, `git show --stat` → locate the produced code → read the
**current** port source (later tasks changed much of it) → verify against native
`~/Development/Allegiance/src` (`Igc/igc.h` read with `LC_ALL=C grep`), cross-checked
against the 2026-09-29 parity raw reports so no known gap is re-reported as new.

Scope recall: station hull/shield/repair values & rates, docking range & rules,
constructor/builder flow (cost, build time, placement, asteroid requirements),
station capabilities, station destruction/capture.

---

## 1. Findings table

Verdicts: MATCH · DIVERGES (value/formula/behaviour differs) · STAND-IN (labelled
placeholder where native data/formula exists) · MISSING (native behaviour absent) ·
UNPORTABLE. Severity: HIGH / MED / LOW.

| # | item | native file:line | port file:line | verdict | severity | proposed fix |
|---|------|------------------|----------------|---------|----------|--------------|
| 1 | Station capacity decode (Price, MaxArmor/MaxShield, Armor/ShieldRegen, ScannerRange, Radius, defense idx, AabmBuild, Successor, Capabilities) | `Igc/igc.h:2649-2689` (DataStationTypeIGC); `igc.h:2634-2647` (DataStationIGC) | `Data/DataStationTypeIGC.cs` (Decode @195-260); `Data/DataStationIGC.cs` | MATCH | – | none (offsets verified vs native struct) |
| 2 | GA-scaling of station armour/shield/regen/scanner (c_gaMaxArmorStation, c_gaMaxShieldStation, c_gaArmorRegenerationStation, c_gaShieldRegenerationStation, c_gaScanRange) | `Igc/stationIGC.cpp:585-624` (MyStationType::Get*) | `Sim/Station.cs` `EffectiveMaxArmor/Shield/Regen/ScannerRange` (:90-160) | MATCH | – | none |
| 3 | Station damage intake — shield-absorbs-then-overflows-to-armour ratio `(absorbed·maxShield·dtmArmor)/(maxArmor·dtmShield)`; shieldless⇒no-damage | `Igc/stationIGC.cpp:116-208` (line 186 overflow ratio) | `Sim/Station.cs` `ReceiveDamage` (:295-450) | MATCH | – | none |
| 4 | Station regen cadence — armour first, shield only when armour full, GA-scaled num & denom | `Igc/stationIGC.h:195-208` | `Sim/Station.cs` `Regenerate` (:465-495) | MATCH | – | none |
| 5 | **Starting shield fraction seeded from map data** — native ctor `m_shieldFraction(0.0f)` then `Initialize` overwrites `m_hullFraction=bpHull; SetShieldFraction(bpShield)` | `Igc/stationIGC.h:146-147`; `Igc/stationIGC.cpp:78-79` | `Sim/Station.cs:258,264` force `1.0f`; `BpHull/BpShield` ignored | DIVERGES | LOW | seed fractions from the loaded `DataStationIGC.BpHull/BpShield`; port's docblock conflates `Export`'s lossy byte cast (`stationIGC.cpp:110-111`) with `Initialize`'s seeding |
| 6 | **Dock arrival test** — native is a 3-D land-slot hit-test `InGarage` (fighter `GetLandSlots`, capital `GetCapLandSlots`, per-plane position/direction) | `Igc/stationIGC.cpp:385-430`; `stationIGC.h:479`; `shipIGC.cpp:843,853` | `Model/Sector.cs:1501` `DockRadius=200.0f` spherical stand-in; dock gate `Sector.cs:3450` | STAND-IN | MED | decode land/launch slot geometry (LandSlots/CapLandSlots/LandPlanes/LandPosition/LandDirection) and implement InGarage; keep DockRadius only as fallback |
| 7 | **Fighter vs capital landing eligibility** (`c_sabmCapLand`) — non-fighter hulls dock only at CapLand stations; port has no fighter/capital distinction, all hulls dock identically | `Igc/shipIGC.cpp:725-727` (fighter?`c_sabmLand`:`c_sabmCapLand`); bit decoded `DataStationTypeIGC.cs` `SabmCapLand` | `Model/Sector.cs` dock gate is capability-agnostic (doc at `Sector.cs:2740`) | DIVERGES | MED | add a fighter-vs-capital gate using `HasCapability(SabmCapLand)` before the dock path |
| 8 | **Capture shield gate** — hostile `c_habmBoard` docks/captures only when `GetShieldFraction() < c_fcidDownedShield` (0.1); invulnerable-station gate | `Igc/shipIGC.cpp:849-858`; threshold `c_downedShield` missionigc.cpp:2745 | `Model/Sector.cs:1513` `DownedShieldFraction=0.1f`; capture branch gated on `rt.ShieldFraction >= DownedShieldFraction → continue` (`Sector.cs:3478`, `IsDockOrBoardCandidate` :2768) | **FIXED** (was OPEN, 2026-09-29 D-special-combat impact 5) | – | none — P450 closed it |
| 9 | Station destruction — `KillStationEvent`, `c_drKilled` on first ≤0, whole-mission STATION_DESTROYED + vacate | `Igc/stationIGC.cpp:189-194` | `Sim/Station.cs` Killed; combat pass `Model/Sector.cs:1847-1880`; wire `Wire/StationDestroyed.cs` | MATCH | – | none |
| 10 | Post-capture fudge — armour halved, shield set to hero-engineer `regenRatio·7.0`, re-side | `FedSrv/fsship.cpp:582-596` (SetFraction·0.5; boost; SetSide 631) | `Sim/Station.cs` `Capture` (:452-490) | MATCH | – | none |
| 11 | **Build completion timing** — native terminal transform fires at `now >= m_timeShrink` = start+**30 s** (cosmetic winds to +40 s) | `Igc/buildingeffectigc.h:19-22` (10+10+10+10), `:50-54` (m_timeComplete), `:314` (AsteroidUpdate at m_timeShrink) | `Sim/BuildingEffect.cs:61` `BuildDuration=40.0`; completion at `now >= CompletionTime` `Model/Sector.cs:3978` | DIVERGES | MED | fire the transform at start+30 (m_timeShrink), keep a 40 s residual only for the cosmetic object |
| 12 | **Build cost** — native charges the station Price when the side funds its station-type research bucket (constructor then spawned); port build path never deducts Price, never consumes a fund | `FedSrv/FedSrv.CPP:11343-11370` (CreateStockDrone on bucket completion) | build path `Ship.DecideBuilderDefaultOrder`→`Sector.StartBuildingEffect`(:363)→`CompleteBuild`(:4050) has no cost/debit | DIVERGES (STAND-IN) | MED | charge `stationType.Price` to the side when the build starts; tie to the open C-economy "station-type buckets" slice (S6) — not actionable standalone until buckets exist |
| 13 | Build asteroid-type gate — rock must have `AabmCapabilities` ⊇ station type's `AabmBuild` | `Igc/shipIGC.h:1695-1707` | `Model/Sector.cs:386` refuse if `!target.Data.HasCapability(stationType.AabmBuild)` | MATCH | – | none |
| 14 | Build placement + successor — station placed at asteroid position, side = builder's, new id; successor resolved on build | `FedSrv/FedSrv.CPP:11386-11402` (payload), `:11380` (GetSuccessorStationType) | `Model/Sector.cs` `BuildStationRecord` (:4087-4110) + P488 successor (:4041-4060) | MATCH | – | none |
| 15 | Builder default order — idle Builder finds least-targeted neutral asteroid and commits to a build; bucket-spawned constructor does NOT auto-build (waits for a Build order) | `Igc/shipIGC.h:2388-2415` (c_ptBuilder); `FedSrv.CPP:11358-11366` (rewrite to DoNothing) | `Ship.DecideBuilderDefaultOrder` (:5900-5985) + `WaitsForBuildOrder` gate | STAND-IN | LOW | travel phase collapsed (builder teleports to the rock); acceptable — targeting + no-auto-build honored |
| 16 | **DamageStationEvent raise position** — native raises only inside the shield branch, after the friendly-fire early-return; port raises for ANY non-repair, non-pedestal hit (incl. same-side & shieldless) | `Igc/stationIGC.cpp:168-169` (inside `dtmShield!=0 && maxShield!=0`) | `Sim/Station.cs:353` before the shield/friendly gates | DIVERGES | LOW | move the raise into the shield branch, after the same-side gate |
| 17 | Undock launch debounce (`LaunchCooldownSeconds=8 s`) | no native equivalent (native undock is `CFSShip` state, no global 8 s cooldown) | `Model/Sector.cs:1478` | STAND-IN | LOW | port-invented to stop instant re-dock; keep but document as a departure |
| 18 | Docked repair/reload — `c_sabmRepair` heals hull, `c_sabmReload` restores ammo/fuel on launch | `Igc/stationIGC.cpp:457-468` (RepairAndRefuel) | `Sim/Ship.cs:4087-4097` (P261) reads SabmRepair/SabmReload | MATCH | – | none |
| 19 | Station capability consumers wired (SabmUnload, SabmRestart, SabmRipcord, SabmLand, SabmFlag, SabmPedestal, SabmRescue, SabmRemoteLeadIndicator) | bits `Igc/igc.h:744-759`; consumers shipIGC.cpp / FedSrv | `DataStationTypeIGC.cs` const bits + HasCapability; wired in P110/P111/P149/P151/P156/P191/P301/P266 | MATCH (decoded+wired) | – | none (SabmCapture deliberately unwired — no native consumer; SabmCapLand see #7) |
| 20 | Ship-vs-station ramming (bounce + damage) and station as 4th ResolveCombatCollisions target | `Igc/shipIGC.cpp:725-830`; cElasticity 0.75 (:935) | `Model/Sector.cs:667` `ResolveShipStationCollisions`; `:2153` `CollisionElasticity=0.75`; station target `:1847` | **FIXED** (was OPEN, 2026-09-29 B-weapons impact 3) | – | none |
| 21 | Station wire formats (OT_station, OT_stationType decode/encode, station export raw-passthrough) | `Igc/igc.h:2634,2649`; `stationIGC.cpp:89-113` (Export) | `Data/DataStationIGC.cs`; `Data/DataStationTypeIGC.cs`; `Wire/ExportStation.cs` | MATCH | – | none |
| 22 | Payday station income Σ owned station types' `Income` | `FedSrv/fsmission.cpp:2538-2546` | `Model/Mission.cs:735` `StationIncomeFor(side)` | **FIXED** (was OPEN, 2026-09-29 C-economy impact 3) | – | none |

Not reported as new (still OPEN in their own domains, out of this slice's remit but
noted so they aren't lost): station-type / drone research buckets + constructor-spawn
(C-economy S6), BuildingTechs from owned stations' EffectTechs (C-economy S4/S5), NPC
drone hull = lowest HullId (C-economy), player-flown Builder convenience vs native
constructor drones (C-economy). Item 12 (build cost) depends on the first of those.

---

## 2. Top fixes (ranked, sized as candidate P-tasks)

1. **Capital-vs-fighter landing + real InGarage land-slot geometry** (#6/#7, HIGH).
   Smallest vertical slice: decode `LandSlots`/`CapLandSlots`/`LandPlanes`/`LandPosition`/
   `LandDirection`/`LaunchSlots` from the station type's per-type slot arrays; add a
   fighter(`SabmLand`)/capital(`SabmCapLand`) eligibility gate to the dock pass, then
   replace `DockRadius` with a per-plane InGarage projection. (The slot data lives in
   the loaded station-type asset, separate from OT_stationType — sizing depends on
   how that blob is currently decoded.)
2. **Correct build completion to the 30 s shrink boundary** (#11, MED). Tiny: change
   `BuildingEffect.CompletionTime` to fire at `start + 30` (m_timeShrink) while keeping
   the cosmetic 40 s; adjust the P163/165 live tests' expected times.
3. **Charge the station Price when a build commits** (#12, MED). Hook
   `StartBuildingEffect` (or the P425 build command) to debit `stationType.Price` from
   the side's funds and fail the build when unaffordable — gives the mechanic a real
   cost today; fold cleanly into the economy S6 bucket work later.
4. **Seed station starting fractions from BpHull/BpShield** (#5, LOW). In the Station
   ctor (or the load seam), set `ArmorFraction/ShieldFraction` from the wire record's
   bytes instead of forcing 1.0/1.0.
5. **Move DamageStationEvent raise into the shield branch** (#16, LOW). Relocate the
   seam to after the same-side/friendly gates so shieldless and same-side hits don't
   trigger damage alerts native wouldn't.

---

## 3. Coverage

All 30 commits in `stations.txt` were processed via `git show --stat` and the produced
files located; current source read for every gameplay-affecting item.

Deep (source-verified both sides, values/formulas compared):
- P35 (DataStationIGC export, station decode/encode) — #1,#21
- P53/P54/P56/P69/P71/P74/P75 (docking trigger, dock/undock, launch clear, SET_CLUSTER,
  undock debounce) — #6,#17
- P80 (degenerate orientation substitution) — checked: defensive guard in ExportStation,
  LOW, native asserts orthonormal — folded into #21 note
- P110 (teleport dock, restart-capable gate) — #19
- P125 (station combat damage, STATION_DESTROYED, vacate) — #3,#4,#9,#16,#20
- P126 (STATION_CAPTURE) — #8,#10
- P133 (STATIONS_UPDATE per-side hull/shield sync) — checked, wire sends live fractions; no divergence
- P148–P152 (ORDER_CHANGE: stay-docked, FIND_TARGET nearest friendly station, return to
  base, take cover, launch) — verified against P149 SabmLand/SabmRipcord/unload searches; #19
- P163/P164/P165 (BuildingEffect mechanic, BUILDINGEFFECT_DESTROYED, Builder AI) — #11,#12,#13,#14,#15
- P170 (stations in NPC dodge scan), P172 (NPC ripcord-as-travel station anchor) —
  verified station participates in collision scan and ripcord anchoring; #19
- P191 (conquest/territory, station-based win, SabmFlag) — verified SabmFlag consumption; #19

Light (presence confirmed, no values to compare):
- P59 (event feed UI), P78 (PlayerInfo baseObjectID sentinel crash fix), P106 (FM_C/S_
  VIEW_CLUSTER), P177 (deployment/Docker/CI), P181 (missile lock — tangential to stations).

Cross-referenced against the 2026-09-29 parity raw reports: three previously-open
station items are now FIXED (#8 capture shield gate, #20 ramming, #22 payday station
income); #12 build cost is tied to the still-open station-bucket economy slice; the
station-type/drone-bucket and BuildingTechs gaps remain open in the economy domain.

---

## Dated note — 2026-10-06 (P550)

**P550 closed #6 (dock arrival test / InGarage) and #7 (fighter vs capital landing).**
- **#7 — fighter-vs-capital land gate (shipIGC.cpp:725-727):** the dock pass
  (`Sector.ResolveDockCollisions`) now enforces the native station-ability gate
  — a `c_habmFighter` hull needs `SabmLand`; any non-fighter hull needs
  `SabmCapLand` — before the friendly/capture branches. (`SabmCapLand` is now
  consumed, no longer unused-outside-decode.) Reported by the live updates to
  `StationCaptureLiveTests`/`ConquestTerritoryLiveTests` (their synthetic bases
  carried `SabmCapabilities=0`/flag-only and were rightly refused).
- **#6 — real landing geometry, not the 200u sphere:** the dock pass now runs
  the native geometric predicate `Station.InGarage` (stationIGC.cpp:385-436,
  REUSING the carrier `InGarage` half-space test the port already had —
  `HullLandingBayCache` / `Ship.InGarage`), fed by each station TYPE's own
  `capgarageN{A..}` frames reconstructed from its `.cvh`
  (`HullLandingBay.ReconstructStation`, stationtypeigc.cpp:77-101) via the new
  `StationLandingBayCache`. When a station type's geometry is resolvable, the
  sphere alone is no longer sufficient — the ship must sit InGarage. When the
  model is ABSENT from the available corpus (a port data gap native never has —
  e.g. the standard `ss24a` garrison has no `.cvh` in the shipped artwork), the
  dock falls back to the documented `DockRadius` proximity stand-in; the
  capability gate applies in both paths. This keeps real-map docking working
  while enforcing strict native geometry wherever the data exists.
  Decked ships in a bay are now truly InGarage, not merely within 200u.
