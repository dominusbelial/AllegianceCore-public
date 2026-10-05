# P531 — Stand-in / placeholder sweep across production code (P1–P200 parity, part 1)

Date: 2026-10-05. Read-only audit. Native = `~/Development/Allegiance/src` (C++),
port = `~/Development/AllegianceCore/src` (C#). Every non-MATCH row was verified by
reading source on both sides; `Igc/igc.h` is ISO-8859 and was searched with
`LC_ALL=C grep -a`.

Scope: all 43 "strong" markers in `docs/audits/p1-p200/standin-strong.txt` plus the
most impactful numeric-literal / rate / cadence / threshold / price / radius / timer /
damage markers from the 619-line `standin-all.txt` triage, concentrated in sim,
economy, combat and AI code. The three He3 mining stand-ins
(`Sector.MiningRate`, `Ship.MineCapacity`, `OreCreditPerUnit`) are **KNOWN — being
fixed by P528**; listed at the end, not re-analysed.

## Headline finding

The port has advanced far past the 2026-09-29 parity baseline. **Most** "stand-in"
comments are now *honest architectural proxies that reproduce native behaviour
equivalently* — proximity-sweep passes standing in for a physics collision system,
capability-bit predicates standing in for pilot-type checks, collated wire fields
standing in for C++ pointers. They are labelled correctly and do **not** carry a
test-convenience numeric. The genuinely divergent items where a native *value or
formula* exists and the port uses a fixed stand-in are few and cluster around:
(1) the **nearest-friendly-station search** (respawn/rescue target selection),
(2) the **flat DockRadius=200** dock trigger, and (3) the **same-side SeenBySide**
proxy (no fog-of-war). Several prior parity gaps were found **already FIXED**
(P443, P441, P446/P467, P450, P458, P461, P466, P308 — see Coverage).

---

## 1. Findings table

Verdict legend: **MATCH** = equivalent; **DIVERGES** = value/formula/behaviour
differs; **STAND-IN** = labelled/obvious placeholder where native data/formula
exists; **MISSING** = native behaviour absent; **UNPORTABLE** = can't apply (why).

### 1a. Economy / money

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| Payday cadence 60 s + income 600 (fixed literals) | missionigc.cpp:2691,2722,2746,2773-2774 (reference mission **overrides** the core slot) | Mission.cs:289-307 (`SecondsBetweenPaydays=60.0`, `IncomePerPayday=600.0`) | MATCH | LOW | none — faithful to native's own mission-set override |
| Starting-money seed (16000) | missionigc.cpp:2752,2697; fsmission.cpp:1992-2004 | Mission.cs:309-332,705-713 (P443 `StartingMoneyFor`, `ApplyStartingMoneyAndInitialPayday`) | MATCH | LOW | none — added by P443 |
| Payday station income Σ (was MISSING) | fsmission.cpp:2538-2546 | Mission.cs:637-654, `StationIncomeFor` (P443) | MATCH | LOW | none — fixed by P443 |
| Payday wallet credit via autodonate target | fsmission.cpp:2310-2324 | Server.Economy.cs:1115-1131 (P197; no MONEY_CHANGE at payday is native CFSPlayer::SetMoney behaviour) | MATCH | LOW | none |
| Buy cost/debit + FUNDS gate (was MISSING) | FedSrv.CPP:6182-6207,6197 | Server.ShipCommands.cs:2525-2541 (P441: cost = hull+Σparts−old, refuse if cost>wallet) | MATCH | LOW | none — fixed by P441 |
| Reload batch bound [1,5] | FedSrv.CPP:6523 `TrapHackBoot(nReloads>0&&nReloads<=5)` | Server.ShipCommands.cs:469-474 | MATCH | LOW | none |
| Station-type completion → constructor drone (was MISSING) | FedSrv.CPP:11343-11372 | Server.Economy.cs:690-734, `CompleteStationType` (P458) | MATCH | LOW | none — fixed by P458 |
| `BuilderStationType` as stand-in for `SetBaseData` | shipIGC.h:2050-2065 | Server.Economy.cs:702-704; Ship.cs:1556-1566 | MATCH-equiv | LOW | none — functional stand-in (station type a Builder builds) |
| Drone-type completion → real drone | FedSrv.CPP:11518-11550 | Server.Economy.cs:873-904, `CompleteDroneType` (P458) | MATCH | LOW | none — fixed by P458 |
| `assert(pilotType!=c_ptBuilder)` in drone bucket → log-and-ignore | FedSrv.CPP:11522 (debug assert) | Server.Economy.cs:881-888 | MATCH | LOW | none — soft-refusal of an assert, correct guard |

### 1b. Core sim (radii / timers / thresholds / physics)

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| **Flat dock radius 200** | shipIGC.cpp:843,853 `IstationIGC::InGarage(ship,pos)` slot geometry | Sector.cs:1480-1501 `DockRadius=200.0f` | **STAND-IN** | MED | derive dock trigger radius from the station type's real size (DataStationTypeIGC) instead of a flat 200 |
| Ship-update candidate ordering (no deviation sort) | FedSrv.CPP:3923 qsort-by-deviation | Sector.cs:445-454, `GetShipUpdateCandidates` | STAND-IN (deferred budget task) | LOW | reintroduce deviation-prioritised order in the bandwidth-budget task |
| Treasure pickup radius 5.0 | native treasure `SetRadius(5.0f)` | Game1.Rendering.cs:1104; TreasureRenderer.cs:31,40; TreasureMeshFactory.cs:56,75 | MATCH | LOW | none (native value reused for the mesh scale) |
| Wingman 200-tick re-eval cooldown removed | shipIGC.cpp:2406 `m_checkCooldown=200` (pure perf) | Ship.cs:6117-6122 | MATCH | LOW | none — perf-throttle removal, decision-identical |
| Lander-radius sphere simplification in InGarage | shipIGC.cpp:4407-4415 (slot-plane test) | Ship.cs:1232-1250 `landerRadius=0.5f*len` | STAND-IN | LOW | port the full slot-plane/point test rather than a bounding sphere |
| Weapon reach = Speed·Lifespan | weaponIGC.cpp:220 / projectile Speed,Lifespan | Ship.cs:5222-5232 `EstimateWeaponRange` | MATCH | LOW | none — honest derivation, not a guessed constant |
| Ripcord anchor → collapsed scalars | shipIGC.h:2590 `m_pmodelRipcord` | Ship.cs:1097-1107 | MATCH-equiv | LOW | none |
| Friendly-station search for respawn (nearest) | FedSrv.CPP:12034-12038 `FindTarget(...,c_ttFriendly\|c_ttStation\|c_ttNearest,...,c_sabmRestart)` + GetBase fallback | Server.Combat.cs:1828-1841, `FindFriendlyStation` (returns ANY same-side, first-found) | **DIVERGES** | MED | implement a spatial nearest-friendly-station search with the mission-base fallback |
| **Minefield attrition (dwell-damage per moving ship)** | mineigc.h:68-145, shipIGC.h:1998-2030 `AddMineDamage` (dt·speed²·rShip²/(2·rMine), multi-ship, moving-only) | Mine.cs:26-41,51-53 (endurance/destructibility P95/P96 ported; **still proximity single-hit**, no moving gate, no multi-ship); Sector.cs:2903-3020 | **KNOWN-OPEN** | MED-HIGH | port the native per-moving-ship dwell-damage integral in `Sector.ResolveMineDetonations` |
| Mine endurance decay formula | mineigc.h:120-128 | Mine.cs:240-247, `ReduceStrength` | MATCH | LOW | none |
| Post-capture armor halve / heroic shield | fsship.cpp:582,589-596 | Station.cs:445-455 | MATCH | LOW | none — native behaviour, not a simplification |
| Station GA armor/shield/regen/scanner | sideigc.h / stationIGC.cpp | Station.cs:160-192 `Effective*` (P446) | MATCH | LOW | none |
| Ship GA speed/thrust/turn/torque/energy (flight) | shipIGC.cpp:4528-4546 | Ship.cs:708-731,4306-4316 (`ExecuteMove` now GA-scaled) | MATCH (FIXED P446) | — | was parity-A #4, now closed |
| Ammo/damage GA in weapons+missiles | weaponIGC.cpp:220; missileigc.cpp:389,523 | Weapon.cs:259-263; Missile.cs:241,265 (P467) | MATCH (FIXED) | — | was parity-B #2, now closed |
| Energy recharge per tick (was MISSING) | shipIGC.cpp:295-303 | Ship.cs:6756-6774 (P461) | MATCH (FIXED) | — | was parity-B #1, now closed |
| Ship-vs-station physics (bounce+dmg) (was MISSING) | shipIGC.cpp:719-960 (elasticity 0.75, \|Δv\|² coeff) | Sector.cs:2505-2549 (P466) | MATCH (FIXED) | — | was parity-B/D gap, now closed |
| Capture shield-down gate (was MISSING) | shipIGC.cpp:849-857 (`c_fcidDownedShield`<0.1) | Sector.cs:3473-3489, `DownedShieldFraction=0.1f` (P450) | MATCH (FIXED) | — | was parity-D #1, now closed |
| Station/shield-side rescue pass (point-in-sphere) | shipIGC.cpp:1022-1058,862-867 | Sector.cs:849-874,946-967 (P115/P301) | MATCH-equiv | LOW | none — faithful capability gates, proximity sweep in lieu of physics |
| Treasure pickup pass (point-in-sphere) | treasureIGC.h:78-99 | Sector.cs:1146-1172 (P123) | MATCH-equiv | LOW | none |
| `c_buoyCluster=1` cluster buoy | native BuoyType.cs:11-12 | DataBuoyIGC.cs:46-49 | MATCH | LOW | none — 1 IS the native constant |
| Carrier lifepod rescue pass | shipIGC.cpp / treasureigc carrier branch | Sector.cs:1035 | MATCH-equiv | LOW | none |
| Collision-bounce `continue` when hostile shields up | shipIGC.cpp:851 | Sector.cs:3470-3472 | MATCH-equiv | LOW | none |

### 1c. Combat / ship-commands / docking / AI

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| **Friendly-station search (rescue)** | FedSrv.CPP:13065-13071 `FindTarget(...,c_ttNearest,...)` + GetBase | Server.Docking.cs:672-683 (reuses `FindFriendlyStation`, any same-side) | **DIVERGES** | MED | nearest-friendly-station search (shared with the respawn fix) |
| Same-side **SeenBySide** proxy (no fog-of-war) | ImodelIGC::SeenBySide(side) (scanner-based) | ShipCommands.cs:44-49; Docking.cs:1763-1770 `SeenByRequesterSide` (objectSide null \|\| == requesterSide) | **STAND-IN** | MED | gate target visibility by scanner/fog-of-war range once scanning exists |
| Eject pod born alive (SetFraction 1.0) | FedSrv.CPP:12759/12950,12954; shipIGC.cpp:1033-1036 (no dead gate) | Server.Combat.cs:1945-1960 (P308) | MATCH | LOW | none |
| "Builder-role" gate via c_habmBuilder capability | shipIGC.h:1699-1702 (pilot-type gate) | ShipCommands.cs:1958-1964,2030-2051,2066-2122 | MATCH-equiv | LOW | none — player builders proxied by hull capability, gates faithful |
| Same-sector rock guard (vs native enter-cluster) | shipIGC.h:1701; buildingeffectigc.h:256-258 | ShipCommands.cs:2048-2051,2117-2122 | MATCH-equiv | LOW | none — StartBuildingEffect precondition abstracted |
| FireMissile OT_* placeholder (no homing target) | — | Server.Combat.cs:291-296 `TargetType=0` | STAND-IN | LOW | populate homing target fields once homing missiles are ported |
| Mine export seen-by-enemy (same-side only) | fsmission.cpp:4238 (SeenBySide) | Server.Combat.cs:364-406 | **KNOWN-OPEN** | MED | add mines to the P132 visibility sweep + cross-side export |
| Base-under-attack team alert (no DamageStationEvent) | FedSrv.CPP:12183-12230 | IIgcSite.cs:301 (empty default) | **KNOWN-OPEN** | MED | raise/implement DamageStationEvent with 10 s throttle |

### 1d. Client / wire / mapgen / misc (mostly LOW)

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| Wire `cbmsg` written 0, fixed-size decode (×~120 sites) | Messages.h DEFINE_FEDMSG (fixed struct sizes) | AutoDonate.cs:22,60,66-68 (pattern); all `Wire/*` codecs | MATCH (aggregated) | LOW | none — systematic, drift-asserted; not a per-message divergence |
| Particle smoke-size `m_radius` stand-ins | particle/model radius | DamageDebrisParticles.cs:151,166; MissileTrailParticles.cs:114,119; WarpTransitParticles.cs:146 | UNPORTABLE | LOW | visual-only; use per-model radius when assets carry it |
| Procedural treasure/station/rock/music placeholders | native art/model/sound assets | TreasureMeshFactory.cs:30-51; StationMeshFactory.cs:36,75; RockTextureSource.cs:14,19; Music.cs; PlaceholderMusicFactory.cs | UNPORTABLE | LOW | asset-pipeline swap, no gameplay value faked |
| Starbase-origin shell placement (mapgen) | common.cpp:2882-2884 `RandomPosition` (avoids overlap) | MissionMapGenerator.cs:1340-1348 `RandomShellPosition` (600–2400 shell) | **STAND-IN** | LOW-MED | sample from a model-overlap-aware region once mapgen grows |
| `GAME_OVER_PLAYERS` roster | fsmission.cpp:3094-3141 | Server.Lobby.cs:646-653 (P455b) | MATCH-equiv | LOW | none |
| Ready gate (vs full FAllReady) | FAllReady (team-balance/rank) | Server.Lobby.cs:960-967 `IsAllOccupiedSidesReady` | **STAND-IN** | MED | add per-team max-players balance to readiness |
| RelaunchShip `Parts` tail vs ExportShipLoadout | FedSrv ExportShipLoadout | RelaunchShip.cs:91-95 | MATCH-equiv | LOW | none |
| LoadoutChange whole-mount-order loadout | native export | LoadoutChange.cs:80 | MATCH-equiv | LOW | none |
| Client player roster (autodonate dict) | GetSide()->GetShips() | ClientWorld.cs:~296-306 | MATCH-equiv | LOW | none |

---

## 2. Top fixes (ranked, sized as candidate P-tasks)

1. **Nearest-friendly-station search** — DIVERGES at Server.Combat.cs:1828
   (respawn) and Server.Docking.cs:673 (rescue) + Server.Lobby.cs:3050.
   Replace "any same-side first-found" with a spatial nearest search + mission-base
   fallback (native FedSrv.CPP:12034-12038,13065-13071). *Smallest vertical slice:
   one shared `FindNearestFriendlyStation` returning the spatially nearest
   `c_sabmRestart` station, wired into both callers; test multi-station placement.*
   Severity MED (respawn/rescue location feel).

2. **Derive the dock trigger radius from the station's real size** — STAND-IN,
   Sector.cs:1501 `DockRadius=200`. *Slice: expose `DataStationTypeIGC` radius;
   compute dock radius = station radius + landing margin; keep 200 as fallback.*
   Severity MED (docking trigger envelope).

3. **Same-side SeenBySide → scanner-gated visibility** — STAND-IN,
   ShipCommands.cs:49 / Docking.cs:1763. *Slice: replace the same-side predicate
   with scanner-range visibility (uses `EffectiveScannerRange`, already wired),
   for dock-target/ripcord-target legitimacy.* Severity MED. Needs the scanning
   system; larger.

4. **Minefield dwell-attrition** — KNOWN-OPEN, Mine.cs:37-41 / Sector.cs:2903-3020.
   Port native `AddMineDamage` (dt·speed²·rShip²/(2·rMine)) so fields attrition
   moving ships, not single-hit. Severity MED-HIGH (signature mechanic).

5. **Mine cross-side visibility export** — KNOWN-OPEN, Server.Combat.cs:364-406.
   Add mines to the P132 visibility sweep. Severity MED.

6. **FAllReady team-balance** — STAND-IN, Server.Lobby.cs:964. Slice: readiness
   accounts for per-team max players. Severity MED.

7. **Base-under-attack alert** — KNOWN-OPEN, IIgcSite.cs:301. Severity MED.

8. Minor (LOW): dock InGarage full slot test (Ship.cs:1238), landerRadius sphere;
   mapgen overlap-aware placement (MissionMapGenerator.cs:1341).

## 3. He3 KNOWN items (P528 — not analysed here)

`Sector.MiningRate`, `Ship.MineCapacity`, `OreCreditPerUnit` are the test-convenience
stand-ins surfaced by today's live test (rate ×100, capacity 150 vs core 90, price 5
vs core 80). They are being corrected by P528 toward the native formula
`ore × c_fcidValueHe3 × GA[c_gaMiningYield]` (shipIGC.cpp:1969-1990,1987-1988). Note
the port already refactored toward `EffectiveMiningRate/EffectiveMineCapacity/
EffectiveOreCreditPerUnit` GA-scaled accessors (Ship.cs:797-886, GlobalAttributeSet.cs:114-122);
P528 aligns the values. Listed KNOWN; intentionally excluded from the findings above.

## 4. Coverage

- **43/43 strong markers** classified; each traced to current source (line numbers
  drifted from the audit file — e.g. Ship.cs:5835/5910 bDocked gates now at 5812/5866/5941;
  Game1.Rendering:1026 pickup-5.0 now at 1104; ClientWorld:299 roster now ~296-306).
- **619-line triage**: pure-prose / history notes skipped per task; the ~120
  `cbmsg placeholder` wire lines collapsed to one systematic row; all numeric
  sim/economy/combat/AI markers with a native counterpart examined and classified
  (target ~40 impactful rows achieved in the table).
- Prior 2026-09-29 parity gaps re-checked and recorded as FIXED vs OPEN:
  FIXED — P443 (starting money, station income), P441 (funds gate/debit),
  P446/P467 (GA scaling to flight+weapons+missiles+stations+probes),
  P450 (capture shield gate), P458 (constructor/drone production),
  P461 (energy recharge), P466 (ship-station physics), P308 (eject pod alive).
  OPEN — minefield dwell-attrition (mineigc.h:68-145), mine cross-side export
  (fsmission.cpp:4238), armed/teleport probes (probeigc.cpp), base-under-attack
  alert (FedSrv.CPP:12183), player-flown-builder-vs-AI-constructor parity gap
  (ShipCommands.cs Builder-role, deliberate P285/P422 convenience).
- No production or test code was modified. `git log -1` / `git branch --show-current`
  confirm the commit below.
