# P535 — Combat parity audit (P1–P200, part 5)

> **Dated correction — 2026-10-06 (P549 landed).** Two rows below were
> revisited by P549's failing-first fixes:
> - **"Collision damage … | MATCH" (line 44) was a false all-clear.** The
>   port had no `ReceiveDamage` friendly-fire gate, so friendly (same-side)
>   non-pod ramming dealt real hull damage — native suppresses it
>   (`shipIGC.cpp:1222-1228`). P549 ports the gate; the row is now genuinely
>   MATCH. See `docs/reports/P549_COMBAT_PARITY_GATES.md`.
> - **"Kill credit attribution … | DIVERGES / MISSING" (line 43) is now
>   FIXED.** P549 ports `DamageTrack` and credits the death to the top
>   total-damage dealer (`shipIGC.cpp:1319-1331`).
> - The **collision HP-pool** divergence (line 45, shield-OR-hull) applies only
>   to the static ship↔station / ship↔asteroid branches; ship-ship is the `+=`
>   terralthra fix and is unchanged. Remains a follow-up, not part of P549.

Read-only audit (part 5 of 8 of the P1–P200 parity pass). Scope: the commits
in `docs/audits/p1-p200/combat.txt` (41 commits) as they relate to **combat**:
damage formulas and the damage-type × defense matrix, shield recharge/absorb
math, hull/armor, collision damage, kill/eject/lifepod rules, missile /
mine / chaff / probe values (and where each comes from — the loaded core's
part/projectile data vs a literal), and explosion radius/damage falloff.

Method: every row below was verified by reading both native
(`~/Development/Allegiance/src`) and port (`~/Development/AllegianceCore/src`)
source, with file:line cited for both sides. Igc/igc.h is ISO-8859-encoded and
was grepped with `LC_ALL=C grep -a`. Items already reported by the 2026-09-29
parity pass (`~/Documents/Domingo-Llm-Wiki/raw/2026-09-29-parity-D-special-combat.md`
and `-B-weapons.md`) are marked FIXED/OPEN against **current** code.

Net picture: the combat core is in very good shape. The port's damage pipeline
(`Ship.ReceiveDamage` → damage matrix → shield `ApplyDamage` → hull → kill) is
a faithful, heavily cross-referenced port, and later P-tasks fixed most of the
known 2026-09-29 gaps (side research now scales combat stats via P467; the
minefield is now the real P452 attrition model; capture is shield-gated by
P450; the station under-attack alert is wired by P451). Four real divergences
remain, two with genuine gameplay weight:
**(1) kill credit goes to the last-hitter instead of the damage-track leader**
(which also skews the Kill-Bonus damage multiplier), and **(2) the collision
hit-point pool is hull+shield additive where native is shield-OR-hull**
(reproduces native's ramp behaviour); plus two cosmetic/low items (explosion
damage type drops the NoWarn|NoDebris flags; `c_fcidDownedShield` is a
hardcoded 0.1 literal instead of the core constant).

---

## Findings

| item | native file:line | port file:line | verdict | severity | proposed fix |
|---|---|---|---|---|---|
| Ship hull damage + **damage-type × defense matrix** (`dtmArmor`) | `Igc/shipIGC.cpp:1212-1350`; `GetDamageConstant` `Igc/igc.h:2751`; matrix `c_dmgidMax/defidMax=20` `igc.h:565,568` | `Sim/Ship.cs:3930-4030` `ReceiveDamage`; `DamageConstant` `Ship.cs:4130-4144`; `Data/DataConstantsIGC.cs` (matrix decode, row-major `[dmgid][defid]`, offset 160) | MATCH | – | none |
| Shield **absorption/leak** math (`dtm` cancel inside, hull `dtmArmor` outside) | `Igc/shieldIGC.cpp:140-195` `CshieldIGC::ApplyDamage` | `Sim/Ship.cs:4204-4244` `ApplyShieldDamage` | MATCH | – | none |
| Shield **regeneration** (`fraction += regen·dt/maxStrength`, unconditional) | `Igc/shieldIGC.cpp:88`; `GetRegeneration/MaxStrength` `shieldIGC.h:106-117` | `Sim/Ship.cs:4178-4187` `UpdateShield` (P389, P467b) | MATCH | – | none |
| **Downed-shield threshold** (`c_fcidDownedShield` = 0.1) | `Igc/missionigc.cpp:2690,2745`; read `shieldIGC.cpp:158,163` | `Sim/Ship.cs:4213` hardcoded `0.1f` (labelled) | MATCH (value) — **LOW note**: hardcoded literal, not mission-param-driven | LOW | read `FloatConstants[c_fcidDownedShield=5]` (port already does this for `LifepodEndurance[31]`) |
| Hull **repair path** (`amount<0`) + launcher nanite repair credit (×2 for ThreatToStation) | `Igc/shipIGC.cpp:1244-1267` | `Sim/Ship.cs:3952-3988` (P299) | MATCH | – | none |
| **Experience / Kill-Bonus** damage scaling (launcher multiplies amount once, pre-branch) | `Igc/shipIGC.h:1812-1817` (`1+0.5·x/(x+4)`); `shipIGC.cpp:1238-1242,144` | `Sim/Ship.cs:117,3949-3950` (P278) | MATCH | – | none |
| **Kill credit attribution** (damage-track buckets → top contributor) | `Igc/shipIGC.cpp:1320-1333` (`DamageTrack`→`pcredit`); `common.cpp:2592-2612` | `Sim/Ship.cs:4023` passes last `launcher` to `KillShipEvent`; no `DamageTrack` exists | **DIVERGES / MISSING** (documented deliberate omission, `Ship.cs:3911-3913` “no need to split credit”) | **MED** | port a per-target damage map; at `KillShipEvent` credit the bucket leader (feeds Kill Bonus → `ExperienceMultiplier` damage scaling) |
| **Collision damage** formula `hp·d/(hp+d)`, `cElasticity=0.75`, `c_impactDamageCoefficient=1/128`, lifepod exempt, `CollisionDamageType`=row 0 (`0xE0 & 0x1f`) | `Igc/shipIGC.cpp:930-993` (ship/station) & `:789-823` (ship-ship) | `Model/Sector.cs:2153-2252` (asteroid), `:2255+`/`:2462` (ship-ship), `:2681` (station); `CollisionDamageType=0` `Sector.cs:2155` | MATCH | – | none |
| **Collision HP pool** (shield-**or**-hull, native discards hull when shielded) | `Igc/shipIGC.cpp:792-796` and `:962-965`: `hp1 = shield ? shieldFraction·maxStrength : GetHitPoints()` | `Model/Sector.cs:2829-2840` `CollisionHpPool` = **hull + shield additive** (deliberate, cited at `Sector.cs:2218` P408) | **DIVERGES** (deliberate, documented) | **MED** | reproduce native shield-OR-hull: `hp = shielded ? shieldPool : hullHP` |
| **Explosion** radius/damage falloff `(1−d/R)²`, `d = dist − targetRadius`, within `R` | `Igc/clusterIGC.cpp:420-436` (ships) / `:448-458` (mines) | `Model/Sector.cs:3092-3131` `ResolveExplosion` | MATCH | – | none |
| Explosion damage **type flags** (native ORs `c_dmgidNoWarn|NoDebris`) | `Igc/clusterIGC.cpp:431` | `Model/Sector.cs:3103` passes raw `e.DamageType` | **DIVERGES** (cosmetic; same matrix row via `&0x1f`, so magnitude identical) | LOW | OR `0x20|0x40` before `ReceiveDamage` (suppresses warn/debris FX only) |
| Projectile **direct hit** damage + **blast-on-impact** (“area effect weapon”, unconditional) | `Igc/projectileIGC.cpp:177-231` (`:206-219` blast) | `Model/Sector.cs:1679-1751` + probe/station/asteroid branches | MATCH | – | none |
| Missile **single-target** damage on impact | `Igc/missileigc.cpp:452-479,512` | `Model/Sector.cs:1714-1720` | MATCH | – | none |
| Missile **blast on impact** (native: `KillMissileEvent`→`Explode(position)`) | `FedSrv/FedSrv.CPP:12526`; `missileigc.cpp:512` | `Model/Sector.cs:1731-1741` (shared ordnance blast) + expiry `Missile.cs:262-271` | MATCH (behavior) — **note**: port comment `Sector.cs:1711-1713` claims missiles are single-target, which is stale vs the shared blast block | LOW | correct the comment (behavior already matches native) |
| Missile **homing** (lead solve, turn-rate limited, unconditional thrust) (P179) | `Igc/missileigc.cpp:371-397,419`; `c_gaTurnRateMissile` `:389` | `Sim/Missile.cs:215-250` (P179, P467) | MATCH | – | none |
| Missile **lock buildup** (range, angle-off-bow, ECM in denominator) (P181) | `Igc/magazineigc.cpp:104-167` | `Sim/Ship.cs:3527-3586` `UpdateMissileLock` (incl. `HullType.Ecm`, documented) | MATCH | – | none |
| **Chaff spoof** probability (`cm=chaff·resist`; `f=cm−½resist²` or `½chaff²`; `rand(0,cm)≤f`; retarget) (P182) | `FedSrv/FedSrv.CPP:5680-5707` | `Sim/Ship.cs:3797-3827` `DeployChaff` | MATCH | – | none |
| **Minefield attrition** model (dwell integral, speed², rHim², endurance decay, worst-hit-per-tick, `newEndurance<1` kill) (P452) | `Igc/mineigc.h:68-145`; `AddMineDamage` `shipIGC.h:1998-2035`; applied `clusterIGC.cpp:334` | `Model/Sector.cs:2923-3017` `ResolveMineDetonations`; `Sim/Mine.cs:231-237` `TakeAttritionDamage` | MATCH (**D-pass #4 FIXED** — was single-hit consume) | – | none |
| Mine **blast destructibility** (`ReduceStrength`, `fraction<0.25`) (P96) | `Igc/mineigc.h:170-181` | `Sim/Mine.cs:210-215`; decay `:249-261` | MATCH | – | none |
| Mine **data values** (`MineRadius` offset 464 not base Radius, `Power`/`Endurance`/`DamageType`) | `Igc/igc.h:1992`; `mineigc.cpp:106` | `Data/DataMineTypeIGC.cs:97-105` (loaded-core driven) | MATCH | – | none |
| Missile **data values** (`Power`/`BlastPower`/`BlastRadius`/`DamageType`) | missile type data | `Data/DataMissileTypeIGC.cs:133`; `Sim/Missile.cs:147-155` (loaded-core driven) | MATCH | – | none |
| **Station combat damage** — shield-absorb-then-overflow-to-armor, `dmg=(ovf·maxShield·dtmArmor)/(maxArmor·dtmShield)`, shield bottoms out (P125) | `Igc/stationIGC.cpp:116-208` | `Sim/Station.cs:325-430` | MATCH | – | none |
| **Station regen** (armor-then-shield sequencing, GA-scaled) | `Igc/stationIGC.h:189-213`; `stationIGC.h:197,207` | `Sim/Station.cs` `Regenerate` (P125) | MATCH | – | none |
| **Station under-attack / critical team alert** (throttle 10s, cross/same-side) (P451) | `FedSrv/FedSrv.CPP:12183-12233` `DamageStationEvent` | `Sim/Station.cs:352-353` (raise); `Allegiance.Server/ServerHost.cs:534+` | MATCH (**D-pass #2 FIXED**) | – | none |
| **Station capture shield gate** (`ShieldFraction < c_fcidDownedShield`, not invulnerable) (P450) | `Igc/shipIGC.cpp:849-857` | `Model/Sector.cs:3492` (`rt.ShieldFraction >= DownedShieldFraction` reject), `DownedShieldFraction=0.1` `Sector.cs:1513` | MATCH (**D-pass #1 FIXED**) | – | none |
| **Probe destructibility** (direct-hit target, `amount×DamageConstant`, hull fraction, first-crossing destroy) (P100) | `Igc/probeigc.h:65-146` | `Sim/Probe.cs` `ReceiveDamage`; `Model/Sector.cs:1753-1813` | MATCH (with documented caveat: armed-probe same-side exemption `probeigc.h:87-88` deliberately not ported) | LOW | optional: port armed-probe fire-exemption |
| **Asteroid destructibility** (P102) | `Igc/asteroidigc.h:126-171` | `Model/Sector.cs:1917-1984` `Asteroid.ReceiveDamage`; `KillAsteroidEvent explodeF` `:1979-1984` | MATCH | – | none |
| **Warp bomb** — missile-vs-warp trigger, whole-mission `WARP_BOMB(188)`, 15 s delayed blast with `GetPower()` (P101) | `Igc/missileigc.cpp:459-467`; `FedSrv/FedSrv.CPP:12543-12561`; `Igc/warpIGC.h:54-90` (`:66` = `GetPower()`) | `Model/Sector.cs:3261-3350` `ResolveWarpBombTriggers`/`ResolveWarpBombDetonations`; `Wire/WarpBomb.cs` | MATCH | – | none |
| **Side-GA research applies to combat** (DamageGuns/DamageMissiles/SpeedAmmo/TurnRateMissile/MaxShieldShip/ShieldRegenShip/MaxArmorShip…) (P467) | `Igc/projectileIGC.cpp:132,186`; `missileigc.cpp:389,474,523`; `weaponIGC.cpp:220`; `shipIGC.cpp:4530+` | `Sim/Ship.cs:677-846`, `Sim/Missile.cs:289` `GaFactor`; `Sim/Weapon.cs:263` | MATCH (**B-weapons #2 FIXED**) | – | none |
| **Lifepod oxygen endurance** (`c_fLifepodEndurance=300`, `FloatConstants[31]`) + drift-to-die (P279/P107) | `Igc/missionigc.cpp:2724,2775`; `igc.h:96`; `shipIGC.cpp:1407-1408,361-363` | `Sim/Ship.cs:179-235,196` (reads loaded `FloatConstants[31]`) | MATCH | – | none |
| Kill path reuses combat `KillShipEvent` → FM_S_SHIP_DELETE / SUICIDE slice (P103/P107) | `FedSrv/FedSrv.CPP:11686-11762` `KillShipEvent` | `Allegiance.Server/Server.Combat.cs:1291+` | MATCH | – | none |
| **Wingman/NPC attack, firing, lead solve, range band** (P162/166/169/171), NPC take-cover (P151) | `FedSrv/FedSrv.CPP` order dispatch; lead solve `common.cpp:59-119` | `Sim/Ship.cs` wingman order + `SolveForImpact` (P168); firing shares the verified `Weapon.FireWeapon`→`ResolveCombatCollisions`→`ReceiveDamage` path | MATCH (behaviour audited light — no new damage formula; lead via shared `SolveForImpact`) | – | none |
| **Turret-gunner binding** (boarded gunner crews carrier weapon; server-side aim) (P109) | `FedSrv/FedSrv.CPP:2338`; `shipIGC.cpp:325-340` | `Weapon.SetGunner`; `Server` handlers `FM_C_ACTIVE/INACTIVE_TURRET_UPDATE(99/100)` | MATCH (server); client aim/fire out of this slice (see B-weapons #5) | – | client follow-up (separate pass) |
| Wire/visual layers (P43-49 spectator, P59 event feed, P88-99 projectile/missile/mine/probe codecs, P133/135 station/probe updates) | `Inc/Messages.h`; FedSrv/clintlib broadcast | `Allegiance.Core/Wire/*.cs` codecs + `Server.Combat.cs` | MATCH (transport-only; carry the already-verified damage/state values, no formula content) | – | none |

---

## Top fixes (ranked, sized as candidate P-tasks)

1. **Kill-credit via damage-track leader (MED, smallest slice).** Add a
   per-target damage map (`DamageTrack` analogue, native `common.cpp:2592`,
   consumed at `shipIGC.cpp:1320-1333`); on `KillShipEvent`
   (`Sim/Ship.cs:4023`) credit the top contributor instead of the last
   `launcher`. Feeds Kill Bonus → `ExperienceMultiplier`, so this also corrects
   the damage-scaling multiplier attribution. One accumulator + one pick at
   death + one test.

2. **Collision HP pool shield-OR-hull (MED, one-line + test).** Change
   `CollisionHpPool` (`Model/Sector.cs:2829-2840`) to native’s
   `shielded ? shieldFraction·maxStrength : hullHP` (`shipIGC.cpp:792-796`).
   Affects ramming damage to both bodies; currently hull+shield additive is
   systematically higher for shielded ships. Confirm with a before/after
   collision test.

3. **Explosion damage-type flags (LOW).** OR `c_dmgidNoWarn|c_dmgidNoDebris`
   (`0x20|0x40`) into the blast type before `ReceiveDamage`
   (`clusterIGC.cpp:431`); magnitude unchanged, aligns FX/warning behaviour.

4. **Read `c_fcidDownedShield` from the core (LOW).** Replace the hardcoded
   `0.1f` at `Ship.cs:4213` (and reuse for `Sector.cs:1513` capture gate) with
   `FloatConstants[5]`, mirroring the port’s own `LifepodEndurance` precedent.

5. *(Optional, LOW)* armed-probe same-side fire exemption (`probeigc.h:87-88`);
   and fix the stale missile single-target comment at `Sector.cs:1711-1713`.

---

## Coverage

All 41 commits in `docs/audits/p1-p200/combat.txt` were checked against
**current** source (as of `master`, ahead of P534):

**Deep (traced through current code + native counterpart, values/formulas
compared):**
- **P87** shield absorption, **P90/P91** missile sim + collision/damage,
  **P95** mine detonation, **P96** explosion propagation + mine destructibility,
  **P97** blast-on-impact/expiry, **P100** probe destructibility, **P101** warp
  bombing, **P102** asteroid destructibility, **P125** station combat damage,
  **P179** missile homing, **P181** missile lock, **P182** chaff/countermeasures,
  **P32** weapon arming/fire path (hardpoint + ammo/energy + real projectile
  data), **P278** experience-multiplier damage scaling, **P467** side-GA damage
  factors, **P450** capture shield gate, **P451** station alert, **P452** mine
  attrition, **P254/P354** collision damage.
- **P103** kill path, **P107** eject/lifepod, **P279** lifepod oxygen.

**Medium (structure + event routing verified, damage values via shared path):**
- **P162/P166/P169/P171** wingman AI, **P151** NPC take-cover — firing/lead reuse
  the verified `Weapon`→`ResolveCombatCollisions`→`ReceiveDamage` path and the
  shared `SolveForImpact`; no independent damage formula.
- **P109** turret-gunner binding — server-side crew + weapon assignment verified;
  client aim/fire explicitly out of this slice.

**Brief (wire/visual transport, no formula content — codecs carry the already-
verified values):**
- **P43/P44/P45/P46/P47/P49/P59** spectator/wirelayers, **P88/P89/P93** wire
  codecs, **P94/P98/P99** mine/probe sim+codecs, **P133/P135** station/probe
  update broadcasts.

Known gaps deliberately left OPEN (out of the damage-formula focus, recorded
against the 2026-09-29 D-pass): armed-probe/gun-tower autonomous fire,
teleport-probe ripcord, cross-side mine/probe scan visibility, in-flight turret
seat move, client-side turret aim. Minefield attrition and station-capture /
alert items from the D-pass are now **FIXED** (P452, P450, P451 respectively).

No production or test code was modified; this report is the only change.
