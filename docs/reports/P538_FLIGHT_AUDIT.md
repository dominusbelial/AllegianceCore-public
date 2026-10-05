# P538 — Flight, Physics & Warps parity audit (P1–P200, part 8)

Date: 2026-10-05 · Read-only audit · Input: `docs/audits/p1-p200/flight.txt` (25 commits)
Method: for each commit, `git show --stat` → locate produced code → read the
**current** port source (later tasks changed much of it) → verify against native
`~/Development/Allegiance/src` (`Igc/igc.h` read with `LC_ALL=C grep`, `FedSrv/`,
`Igc/`), cross-checked against the 2026-09-29 parity raw report A (flight) so no
known gap is re-reported as new.

Scope recall: thrust/turn/max-speed/afterburner math provenance (hull data vs
literal), drag & inertia, collision response, warp transit rules, ripcord timing,
autopilot — restricted to the 25 commits' produced code (warps/transit, undock,
missiles/projectiles, ripcord, NPC autopilot/range-maintenance, turret-gunner
movement). The core `ExecuteMove` integrator itself (drag/inertia/AB) was already
audited as faithful in the 2026-09-29 report A and is outside this commit slice.

---

## 1. Findings table

Verdicts: MATCH · DIVERGES (value/formula/behaviour differs) · STAND-IN (labelled
placeholder where native data/formula exists) · MISSING (native behaviour absent) ·
UNPORTABLE. Severity: HIGH / MED / LOW.

| # | item | native file:line | port file:line | verdict | severity | proposed fix |
|---|------|------------------|----------------|---------|----------|--------------|
| 1 | **Aleph mass-limit gate** — native blocks overweight ships from transiting (`pwarp->MassLimit()`), the limit parsed from the aleph NAME (`'*'`=fixed, `'+N:'`=mass N, default −1=unlimited); port transits every ship | `FedSrv/fsship.cpp:77`; `Igc/warpIGC.cpp:59-100` | `Server/ServerHost.cs:868-871,880-950` (gate omitted — comment admits `DataWarpIGC` "does not decode a mass limit") | MISSING | **HIGH** | parse the aleph-name `'+N:'`/`'*'` prefix convention on warp load into a `MassLimit`/`FixedPosition` on `ResolvedWarp`, and gate `HitWarpEvent` on `ship.Mass <= MassLimit` (player case, mirroring `fsship.cpp:77`); also gives capitals the real "can't fit the aleph" restriction |
| 2 | **Missile `readyTime` activation delay unused** — native coasts a missile straight at its initial speed for `readyTime`, only THEN enables homing+thrust (`now > m_timeActivate`); port decodes `ReadyTime` but never gates on it — missiles home and thrust from tick 0 | `Igc/missileigc.cpp:104,363-397` | `Data/DataMissileTypeIGC.cs:58,124` (decoded, unused); `Sim/Missile.cs:215-250` (no activation gate) | DIVERGES | **MED** | store `_timeActivate = creation + ReadyTime` on the missile and skip the homing+thrust block (coast at initial velocity) until `now >= _timeActivate`, restoring the guaranteed straight arm segment and native flight profile |
| 3 | **Warp-bomb trigger lacks the `c_eabmWarpBomb` capability gate** — native only warp-bombs when `eabm & c_eabmWarpBomb` (0x06); a NON-warp-bomb missile that reaches an aleph is a normal collision (warp not `c_mtDamagable` → normal missile termination+blast, no WARP_BOMB); port raises WARP_BOMB + 15 s bomb for EVERY missile touching an aleph | `Igc/missileigc.cpp:459-466`; `c_eabmWarpBomb`=`Igc/igc.h:772-774` | `Model/Sector.cs:3261-3302` `ResolveWarpBombTriggers` (no capability check at `:3272-3280`) | DIVERGES | **MED** | gate the pass on `(missile.MissileType.EabmCapabilities & 0x06) != 0`; route a non-warp-bomb missile that reaches an aleph to the normal ordnance-vs-warp termination/blast instead |
| 4 | **Missile lead solve is quadratic coast-at-current-speed, not native quartic-with-acceleration** — native solves the intercept with the missile's real acceleration (`solve(...)` quartic); port reuses `SolveForImpact` modelling the missile as coasting at current speed | `Igc/missileigc.cpp:260-352,387` | `Sim/Missile.cs:196-201,233-241` (documented approximation) | DIVERGES | **LOW** | port the native quartic `solve(dP,dV,accel,m_tImpact)` into the lead step; improves homing accuracy, esp. in the first seconds while the missile is still slow |
| 5 | **Aleph exit wobble/spin omitted** — native randomizes the exit orientation by a hit-off-centre `error` (mass-scaled) and adds a roll turn-rate kick; port exits with the destination warp's exact facing, zero wobble | `FedSrv/fsship.cpp:96-122` | `Server/ServerHost.cs:854-859,898-901` (documented "zero wobble") | DIVERGES | **LOW** | reproduce `error = (d/r²)+0.125` (mass-scaled for >300) and apply `Pitch/Yaw(random(−error,error))` + roll turn-rate before placing; cosmetic-feel |
| 6 | **Warp-bomb dual-end explosion not implemented** — native explodes at the hit warp AND the destination warp for `c_eabmWarpBombDual` (0x02) | `Igc/warpIGC.h:70-87` (`c_eabmWarpBombDual` = `igc.h:772`) | `Model/Sector.cs:3324-3327` (explicitly out of scope) | MISSING | LOW | when the missile type carries `EabmWarpBombDual`, schedule a second detonation at the destination warp's position/radius at the same `timeExplosion` |
| 7 | **Discrete point-in-sphere warp trigger vs native swept CCD** — native resolves the transit from a continuous collision-detection contact; port tests `\|ship−warp\|≤ radius+shipRadius` once per tick | `Igc/shipIGC.cpp:618-636` (via CCD `CollisionDetector`) | `Model/Sector.cs:3182-3211` `ResolveWarpCollisions` (point test `:3203-3204`) | DIVERGES | LOW | (optional) after the point test misses, do a swept segment test `(pos₀→pos₁)` against the warp sphere across the chunk; realistically unreachable at 20 Hz cruise speeds — low priority |
| 8 | **Ripcord target `debt` heuristic omitted** — native's `FindRipcordModel` picks among ripcord-target ships the one with lowest accumulated ripcord debt (paid by the target); port's anchor pick ignores debt | `Igc/shipIGC.h:1843-1858`; `Igc/shipIGC.cpp:3898-3915` | `Sim/Ship.cs:1125-1129` (`SetRipcordModel`, "has neither cloak nor debt accounting"); anchor pick `Server.cs` `FindRipcordModel` | DIVERGES | LOW | add a `RipcordDebt` scalar to the ship, adjust ±`RipcordCost` on anchor swap, prefer the lowest-debt target in `FindRipcordModel` |

Verified **MATCH** (values/formulas/conditions compared, not just presence):
- Warp transit trigger radius (`raw warp.Radius + shipRadius`, no visual-scale mult) — `Sector.cs:3203` matches native `GetRadius()` trigger (`shipIGC.cpp:629-636`); native's front/back + not-closing branches are commented-out dead code, so the "always transit on contact" live path is what's matched.
- Warp exit velocity `−(entry + 15)` — `ServerHost.cs:906-907` vs `fsship.cpp:126-127`, `c_fExitWarpSpeed=15.0` (`missionigc.cpp:2688`).
- Warp exit position `dest + up·rand(2,5) + right·rand(2,5) − (r+5)·backward` — `ServerHost.cs:924-934` vs `fsship.cpp:129-132`; the 2 s re-transit debounce (`WarpCooldownSeconds=2`, `Sector.cs:1398`) approximates native `m_warpState`.
- Warp-bomb 15 s delay — `Sector.cs:1419` vs `c_fWarpBombDelay=15.0` (`missionigc.cpp:2726`); blast amount = missile `Power` (not `BlastPower`) — `Sector.cs:3317-3321` vs `warpIGC.h:66`.
- Missile thrust: unconditional `Velocity += Forward·Accel·dt` — `Missile.cs:249-250` matches native `SetVelocity(myVel − backward·(accel·dt))` run unconditionally (`missileigc.cpp:393-397`); turn-rate GA scaling `c_gaTurnRateMissile` (`Missile.cs:241`) matches `:389`. (The "super-slow missile" bug found live 2026-09-16 was the port nesting thrust inside the target gate; now fixed and faithful.)
- Ripcord countdown seed = `RipcordSpeed` = `hull.RipcordSpeed / c_gaRipcordTime` — `Ship.cs:1141,1173-1174` vs `shipIGC.h:1536-1540`, `shipIGC.cpp:4664-4666` (default 10 s via `c_dtRipcord=10`, `missionigc.cpp:2700`).
- Ripcord per-tick revalidation (anchor legality: same-side, no-flag, cluster-present, target-capability) — `Sector.cs:782-806` `ResolveRipcordCountdowns` + `RipcordAnchorStillLegal` vs `ContinueRipcord` (`FedSrv.CPP:13379-13438`).
- Ripcord teleport placement — `Server.Docking.cs:512-633` vs `UseRipcord` (`FedSrv.CPP:13440-13505`): `r = targetRadius+ripRadius+25`, random direction, `ExitStationSpeed(50)` velocity, zeroed turn rates; ship-anchor energy gate (TARGET pays `RipcordCost`) and insufficient-energy abort both match; probe anchor pays no cost (native has no probe cost branch) — matches.
- `IsSafeToRipcord` drift formula — `Ship.cs:5541-5556` vs `shipIGC.cpp:4200-4229` (`k=thrust/(vMax·mass)`, `s=v0(1−e^{−k·tRipcord})/k`, `tMax=1+s/v0`); the scan candidate sets (same-side ships, asteroids, stations) match the dodge universe.
- Collision response — elasticity `0.75` (`Sector.cs:2153`) matches native `cElasticity=0.75` for asteroid (`:930`) and ship-ship (`:1093`); native's `1.5` is the commented-out warp-bounce branch (dead code). Impact damage `c_impactDamageCoefficient=1/128` (`Sector.cs:2154`) matches `shipIGC.cpp:623`.
- Wingman graduated match-speed throttle `2·targetDirSpeed/maxSpeed − 1` and ±20 threshold — `Ship.cs:5283-5300`, `4525` vs `shipIGC.cpp:2492-2512`.
- Missile homing lead `dV = target.Velocity·lock − myVel` — `Missile.cs:234` vs `missileigc.cpp:386`.

**Simplifications (STAND-IN, self-labelled, AI/NPC feel only):**
- **P155 autopilot `SteerToward`** (`Ship.cs:5175-5216`, consts `:4428,4438,4468` = ArrivalRadius 60 / BrakeStart 420 / TurnGain 2.0) is a simplified two-zone "turn-then-burn" controller vs native's velocity-managed `GotoPlan` (`common.cpp:1830-2000`); the tuning literals are port-invented, not sourced. STAND-IN, LOW (NPC cruise feel).
- **P171 wingman range maintenance** (`Ship.cs:5283-5300`) is a single-band reduction: only the in-heat ±20 band + graduated match; deliberately omits the approach/close/give-up bands, afterburner kicks and `criticalMovement` (`shipIGC.cpp:2492-2560`). STAND-IN, LOW.
- **Undock/launch placement** — radial `stationPos + dir·(DockRadius + 50)` with `dir·ExitStationSpeed(50)` (`Server.Docking.cs:1796-1804`) instead of native launch-slot geometry / rotating station-axis `forward` (`stationIGC.cpp:299-383`); the `50` exit speed matches `c_fcidExitStationSpeed` (`missionigc.cpp:2689`) but the directional geometry is simplified and tied to `DockRadius`. **Not re-reported as new** — this is the same undock/dock geometry gap as P537 #6/#7 (its proposed fix already includes decoding `LaunchSlots/LaunchPosition/LaunchDirection`); noted here as still OPEN for the flight side.

---

## 2. Top fixes (ranked, each a candidate P-task)

1. **Aleph mass-limit transit gate** (#1, HIGH). Smallest slice: parse the warp-name `'+'/'*'` prefix convention at load (`DataWarpIGC` or `ResolvedWarp` construction) → store `MassLimit` (default −1) + `FixedPosition`; add the `ship.Mass <= MassLimit` gate to `ServerHost.HitWarpEvent` (players only, per `fsship.cpp:77`). Restores the capital-can't-fit restriction and the "blocked aleph" tactic.
2. **Honor missile `readyTime` activation** (#2, MED). Store `_timeActivate = launch + ReadyTime` in the `Missile` ctor; wrap the homing+thrust block in `now >= _timeActivate` so the missile coasts its guaranteed straight arm segment first. Small, touches only `Missile.cs`.
3. **Gate warp-bombing on `c_eabmWarpBomb`** (#3, MED). In `ResolveWarpBombTriggers`, require `(missile.MissileType.EabmCapabilities & 0x06) != 0`; non-warp-bomb missiles hitting an aleph fall through to the normal ordnance terminal/blast path. Avoids spurious whole-mission `WARP_BOMB(188)` from any stray rocket.
4. **Port the native quartic missile-lead solve** (#4, LOW/MED). Replace the `SolveForImpact` coast assumption with native's iterative `solve(dP,dV,accel,t)` in `Missile.cs:233-241`; measurable early-flight homing accuracy gain.
5. **Add the aleph-exit wobble/spin** (#5, LOW). Reproduce `error=(d/r²)+0.125` (×300/mass over 300) and apply random pitch/yaw + roll turn-rate kick in `HitWarpEvent`; pure feel, cheap.
6. (Fold-ins) **dual-end warp-bomb explosion** (#6) and **ripcord debt target preference** (#8) — both LOW, small vertical slices gated on a capability bit / a scalar + comparator respectively.

---

## 3. Coverage

All 25 commits in `flight.txt` were processed via `git show --stat` and the produced
files located; current source read for every gameplay-affecting item.

Deep (both sides source-verified, values/formulas/conditions compared):
- P35 (DataWarpIGC export/Encode, ExportWarp) — #1 (name/prefix not parsed), #7.
- P41 (warp-transit sim) — transit trigger, exit vel/pos, debounce — MATCH (rows above).
- P74/P75 (undock: drop/restore launch velocity + debounce) — exit speed MATCH, geometry tied to P537 #6 (note).
- P80 (degenerate orientation substitution) — covered in P537; not flight-divergent.
- P91 (missile collision + damage), P100/P102 (probe/asteroid destructibility) — collision response constants (0.75, 1/128) verified MATCH; damage routing is weapons-domain (B report).
- P101 (warp bombing) — #3, #6; delay/blast-power MATCH.
- P109 (turret-gunner orientation prediction via `ExecuteTurretMove`) — reviewed; movement is the shared ship integrator already audited; no new flight-value divergence.
- P111/P112/P113 (ripcord request/countdown/teleport) — countdown, revalidation, teleport placement, energy gate — MATCH; #8 debt.
- P125 (station combat damage) — fully audited in P537 (stations slice); only flight relevance (4th collision target) confirmed MATCH there.
- P150 (ORDER_CHANGE return-to-base), P172 (NPC ripcord-as-travel) — `IsSafeToRipcord` + station-anchor search MATCH (#8).
- P155/P157 (autopilot foundation, cross-cluster FindPath) — `SteerToward` STAND-IN (documented); `FindPathToward` (`Sector.cs:561-587`) matches native `common.cpp:904-1002` breadth-first warp-path.
- P168/P170 (dodge stages 1-2 incl. stations) — dodge scan candidate set matches native `Dodge`; steering via `SteerToward` (STAND-IN above).
- P171 (wingman range maintenance) — single-band STAND-IN; the ported band's formula/threshold MATCH.

Light (presence confirmed, no flight value to compare): P37 (FM_S_MISSION_STAGE wire), P56 (sell ore on dock — economy domain), P59 (event feed UI).

Cross-reference to 2026-09-29 report A: none of this slice's items duplicate A's client-side items (local sim, persistent throttle, dead-reckoning, GA flight scaling, dust) — those are render/client concerns outside these 25 commits. The undock/dock launch-geometry gap is carried forward from P537 (still OPEN). All other known items are either MATCH or newly reported here.
