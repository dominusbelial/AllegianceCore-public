# SHIP_STAT_SHEET_P1011 — Loadout V2: pure ship stat sheet + per-stat deltas

**Task:** P1011 (epic Loadout V2, Hermes story 2). **Base:** Claude's master at dispatch
(`874fd66a`, lane worktree `wt-p1011`). **Model only** — no drawing, no HUD change, no flag.

## Finding

Native's loadout screen has exactly **two** places that compute ship numbers, both in
`WinTrek/load.cpp`:

1. **Hull "performance info"** — `LoadoutImpl::UpdatePerformanceInfo`
   (`WinTrek/load.cpp:986-1054`). It fills eight rows for the hull being *edited* against the
   current ship's factory spec (`m_phullTypeBase`, `m_fSpecMass`, `m_fSpecSignature`):
   `MAX SPEED`, `ACCELERATION`, `TURN RATE`, `HIT POINTS`, `ENERGY`, `SCAN RANGE`, `SIGNATURE`
   and the value in the eighth row, `DEFENSE CLASS`. The labels are painted on the pane's
   background bitmap (`Artwork/hullinfobmp.mdl`); the class names are the `DefenseClassString`
   table in `Artwork/hullinfo.mdl`.
2. **Part info pane** — `LoadoutImpl::UpdatePartInfo` (`:1250-1305`) plus the per-type bodies:
   weapon `:1065-1094`, magazine `:1097-1120`, dispenser/probe+mine `:1122-1172`, chaff
   `:1175-1184`, shield `:1187-1202`, cloak `:1205-1224`, pack `:1226-1228` (empty),
   afterburner `:1230-1248`. Each pane's labels come from its background bitmap
   (`Artwork/partInfo*bmp.mdl`); the weapon/magazine `DAMAGE CLASS` strings are the
   `weaponDamageClassString` / `magazineDamageClassString` tables in `Artwork/partinfo.mdl`.

Decoded label sets (read straight off the shipped bitmaps):

| Pane | Native labels (order) |
|---|---|
| hull | `MAX SPEED`, `ACCELERATION`, `TURN RATE`, `HIT POINTS`, `ENERGY`, `SCAN RANGE`, `SIGNATURE`, `DEFENSE CLASS` |
| weapon | `DAMAGE/SEC`, `SHOTS/SEC`, `SIGNATURE`, `RANGE`, `SPEED`, `DAMAGE CLASS` |
| magazine | `DAMAGE`, `SIGNATURE`, `RANGE`, `LOCK TIME`, `MAX LOCK`, `DAMAGE CLASS` |
| probe | `HIT POINTS`, `RANGE`, `LIFESPAN`, `VISIBILITY` |
| mine | `DAMAGE`, `RADIUS`, `LIFESPAN`, `VISIBILITY` |
| chaff | `STRENGTH` |
| shield | `STRENGTH`, `RECHARGE`, `SIGNATURE`, `DEFENSE TYPE` |
| cloak | `CLOAKING`, `DURATION` |
| afterburner | `TOP SPEED`, `DURATION`, `SIGNATURE` |
| pack | (native `UpdatePackPartInfo` is empty) |

Two native details matter for a correct port:

* `RECHARGE` is **not** a rate: `UpdateShieldPartInfo` prints
  `maxStrength / (rateRegen × GA[c_gaShieldRegenerationShip])`, i.e. **seconds to refill**
  (`WinTrek/load.cpp:1193-1194`). Lower is better.
* `SIGNATURE` is divided by the side's `GA[c_gaSignature]` before the pane prints it ×100
  (`:1014-1015` for the hull, `:1271-1275` for a part). Lower is better.

## Root cause (where the gap was)

`src/Allegiance.ClientCore.Game/Rendering/ShopModel.cs` models the shop's *identity* rules
(slots `:278-308`, offers `:354-380`, price/`GetValue` `:166-200`, Default/Revert `:775-863`)
but never computes a ship's characteristics: the file has no mass, thrust, energy, shield or
weapon-output arithmetic at all. Nothing in `src/` combined a hull record with its mounted
parts and the side's GA into stats, so a V2 stats bar had no native-faithful source and would
have had to invent numbers. The native formulas it needs live only in the client UI
(`WinTrek/load.cpp:986-1245`) and have to be ported as data, separately from the drawing.

## Fix

Two new pure files under `src/Allegiance.ClientCore.Game/Rendering/` (no `GraphicsDevice`, no
P1010 dependency — they take `DataHullTypeIGC` + `DataPartTypeIGC` exactly like `ShopModel`):

### `ShipStatSheet.cs`

* `ShipStatSheet.Build(hull, parts, attributes, lookup)` — a flat, ordered row list
  (`Stats`) plus convenience values (`Mass`, `SignaturePercent`, `MaxSpeed`, …) and
  `Guns` (`GunOutput` per weapon mount).
* Rows carry `StatKey` (`Hull` / `Ship` / `Slot` scope + equipment type + mount + field),
  a native-verbatim `Label`, `Value` or class `Text`, a unit, a `StatDirection`
  (`HigherIsBetter` / `LowerIsBetter` / `Neutral`), and a `StatGroup` that separates
  **native rows** from **estimates** (`IsEstimate`).
* GA factors are applied exactly as the sim does — the sheet reads the named
  `GlobalAttributeSet` constants through `GlobalAttributeSet.Get`, mirroring the port's
  `Ship.Effective*` one-liners (`Ship.cs:1198-1260`), which are the port of native's
  `MyHullType` (`Igc/shipIGC.cpp:4529-4580`):
  speed × `GA[MaxSpeed]`, thrust × `GA[Thrust]`, turn rate × `GA[TurnRate]`,
  scanner range × `GA[ScanRange]`, energy × `GA[MaxEnergy]`, hit points × `GA[MaxArmorShip]`;
  recharge rate, max fuel, max ammo and capacities carry **no** factor. No Core change, so
  the sim stays the single source of its own rules and no AOT publish is triggered.
* Extras (Modern, `IsEstimate`): per-axis turn rates, energy recharge, fuel, ammo capacity,
  cargo (magazine/dispenser/chaff = native `IhullTypeIGC::GetCapacity`,
  `Igc/hullTypeIGC.h:178-196`), ship mass (hull + Σ part mass, `Igc/shipIGC.cpp:1468`),
  and `DAMAGE PER SHOT`. Every value that native derives keeps native's formula:
  acceleration `thrust/mass` (`:990`), mean turn rate `(yaw+pitch+roll)·180/(3π)` (`:991-1006`),
  weapon `(power+blastPower)·(1/dtimeBurst)·GA[DamageGuns]` (`:1072-1073`) with the ammo/energy
  speed+range GA split (`:1075-1081`), missile
  `(power+blast)·GA[DamageMissiles]` and `initialSpeed·lifespan+½·accel·lifespan²`
  (`:1103-1109`), probe scan range `lifespan·speed/1.2` (or `scannerRange·GA[ScanRange]`,
  `:1138-1143`), chaff effectiveness `s≤1 ? s/2 : 1−1/(2s)` (`:1180-1184`),
  shield refill time (`:1193-1194`), cloak duration (`:1215-1222`), afterburner
  `hullSpeed·(1+maxThrust/raw hull thrust)` and `maxFuel/(maxThrust·fuelConsumption)`
  (`:1238,1241`).
* Mirrors native's slot order (weapons by hardpoint, then magazine, dispenser, chaff, shield,
  cloak, afterburner, pack — the order `load.cpp:1976-2078` walks) and keeps first-wins for
  the single-socket types.
* `WithPart(type, mount, part?)` / `WithHull(hull)` return rebuilt sheets — the hover-delta
  and hull-switch inputs.

Class/type tables are native's art strings, transcribed in one place:
`HullDefenseClass` (`hullinfo.mdl`), `ShieldDefenseType` (`partinfo.mdl:365-370`),
`WeaponDamageClass` (`partinfo.mdl:315-333`), `MagazineDamageClass` (`partinfo.mdl:410-425`).

### `ShipStatDelta.cs`

`StatDelta.Between(oldSheet, newSheet)` pairs rows by `StatKey` (new-sheet order, old-only rows
appended), and reports per row: old/new absolute values (or class texts), `Difference`,
`Sign` (−1/0/+1), the stat's `StatDirection` and the resolved `StatOutcome`
(`Same` / `Better` / `Worse` / `Changed` / `Added` / `Removed`), plus `StatDelta.Count`.
Because slot keys use equipment type + mount rather than part id, replacing the part in a slot
produces a genuine OLD→NEW row for the same key — exactly what the epic's per-offer hover delta
needs. Directions encode the story's rule: signature, mass, shield refill time, lock time and
visibility are lower-is-better; speed, thrust-derived acceleration, hit points, energy, scan
range, damage/sec and ranges are higher-is-better; class names are `Neutral` (reported as
`Changed`).

## Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P1011ShipStatSheetTests.cs` (15 tests,
`[Trait("Requires","Artwork")]`+`[SkippableFact]`, real `PCore001.igc`, skips cleanly without
the corpus):

* Native label sets, verbatim, per pane (hull + every part type).
* Hand-computed fixtures, **3 hulls × 2 loadouts**: Adv Scout 285, Adv Fighter 288, Bomber 124;
  bare and native default loadouts. Examples pinned by the tests: Scout bare acceleration
  1200/40 = 30, turn 50 °/s; Scout default mass 40+5.25 = 45.25, signature (0.5+3.0)=350 %,
  Gat Gun 3.2×10 = 32 DPS / 600 m, MRM Seeker 25·6+½·60·36 = 1230, probe range 500,
  chaff 1−1/3 = 66.67 %; Fighter default mass 41.25, 3 guns, Hvy Booster top speed
  120·(1+1350/750) = 336, mine 700/75/5/15 %, SRM range 900; Bomber default mass 61.25,
  1175 %, 4 guns (2 fixed lasers: 50 DPS / 625 m; 2 turret AutoCans: 117.95 DPS / 1500 m),
  Med Shield 300 / 60 s, Anti-Base missile 400 / 1000 m / class "stations",
  hull class "Medium".
* GA applied vs identity: all six hull factors, shield strength/regen, gun DPS/speed,
  missile damage and probe scan range/visibility move by the exact GA products; mass and hull
  recharge do not.
* Part-swap delta (Gat Gun → EW Pulse Laser): DPS 32→50 (+18, better), range 600→625,
  speed 600→1250, `DAMAGE CLASS` "light"→"very light" (`Changed`), unchanged mass/signature.
* Lower-is-better delta (Sm Shield → Med Shield): strength 150→300 better, refill 75 s→60 s
  better, part signature 50→75 % worse, ship signature 350→375 % worse, mass worse.
* Hull-switch delta (Scout→Fighter with the same parts): speed worse, turn rate better,
  hit points better, scan range −2200 worse, acceleration −5.243 worse; slot rows `Same`.
* Extra guns on the new hull surface as `Added` rows; keys are unique per sheet; a missing
  core lookup keeps the labels and nulls the ordnance numbers instead of throwing.

**Result (3× alone, run 1/2/3): Passed 15, Failed 0, Skipped 0** each run.

## Parity

New ledger `docs/parity/p1011-loadout-stats.csv` (15 rows, sim-matrix column shape, validated
by `scripts/parity-scorecard.py`): each loadout info-pane field set and each **newly ported
display formula** (weapon DPS/shots, ammo/energy range rules, missile range, probe scan range,
chaff effectiveness, shield refill time, cloak duration, afterburner top speed/duration,
mass/signature accumulation) cites `WinTrek/load.cpp` and the `Igc/igc.h` `Data*TypeIGC`
field it reads. Rows are `implemented` with `reachability=test-only` until the P1014 renderer
consumes the model; the GA rules themselves are the already-ported P446/P467b/P476 sim rules,
so `sim-rules-matrix.csv` gains no row.

## Gate

- `dotnet build AllegianceCore.slnx` (safe flags): **0 warnings, 0 errors** (31.6 s).
- `tools/build-tools.sh`: **all 138 tool projects built** (exit 0).
- `tools/run-tests.sh --tier fast`: **14,280 cases in 670.4 s — 14,266 passed /
  7 failed / 7 not executed** (calibrated Fast limit 13.233 s; host load average
  11.7–18.0 from other lanes' sweeps). All 15 new P1011 cases passed in the sweep.
  The seven failures are non-task families already in `docs/TEST_FLAKES.md`;
  each was re-run **three times alone**: WarpTransit, P739 timer (delay 7),
  MissileBroadcast, GameOverPlayers roster, ArmedProbe, DeadLatch all
  **pass/pass/pass** (0.38–1 s); `MissileLockLiveTests` is **0/3 alone**, the
  persistent-alone family the ledger already records with a failing clean-base
  control (not load, not this task). Timing-only offenders under the sweep
  (P814/P796/SC2Nanite/P999Civ/P1000Civ theory cases) pass alone under the
  limit. Ledger entry appended to `docs/TEST_FLAKES.md`; the sweep is retained
  as FAIL, as the ledger convention requires.
- No `src/Allegiance.Core` or `src/Allegiance.Server` change, so the AOT
  publish gate does not apply.
- GLOSSARY: new "Loadout screens / shop model" and "Ship stat sheet + stat deltas
  (P1011)" entries; `tools/check-glossary-links.py` clean (818 paths).
- Parity CSV validated by `scripts/parity-scorecard.py` (15 rows, 17 columns,
  LF only).
- Sweep evidence (summary + the two failing-project logs): `~/allegiance-evidence/P1011/`.

## Hand-offs

* **P1014 (renderer):** consume `ShipStatSheet.Stats`/`Guns` and `StatDelta.Between`; the rows
  already carry native labels, units, the estimate flag and the better/worse verdict, so the
  renderer needs no formula of its own. Colour the delta by `StatOutcome`, not by the sign
  alone (signature/mass/refill time invert).
* **P1010 (view-model):** build `MountedStatPart` from `ExpandedPartData` (`PartId` resolved
  through the shop catalogue/launchers, `MountId`) — the sheet deliberately takes part types,
  not wire records.
* **P1012 (carry-over):** `WithHull` rebuilds mechanically; which parts survive a hull change
  is still native `CanMount`/`ReplaceLoadout` (`clintlib.h:1806-1848`) and belongs to P1012.
* **P1018:** the flag (`visuals.hangar.loadoutV2`) is untouched here; nothing is wired into
  `Game1.Hangar.cs`, so the flag-off screen is byte-identical.

## Native intent

Native's loadout screen tells the pilot what the hull and each part will do before they buy:
the hull pane prints the eight performance rows (against the current ship's factory spec) and
each part pane prints that equipment type's own numbers, every value computed from the core's
`Data*TypeIGC` records with the side's `GlobalAttributeSet` applied through `MyHullType`
exactly as the sim does. The row set, labels and formulas are native's; nothing is invented.

## Modern approach

The same numbers live in a pure, unit-testable `ShipStatSheet`/`StatDelta` pair so the V2
screen (behind `visuals.hangar.loadoutV2`) can show a stats bar and inline per-offer deltas
without duplicating any buy/mount rule. Native's mandatory rows keep native labels; the extra
values the epic asked for (per-axis turn rates, energy recharge, fuel, ammo/cargo, ship mass,
damage per shot) are explicitly flagged as estimates, and the delta layer adds the
higher/lower-is-better judgement native does not have.
