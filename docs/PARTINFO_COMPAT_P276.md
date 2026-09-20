# P276 — Part-info + connector-compatibility panel (follow-up to P268)

Feature: when the local ship is docked, highlighting a shop item in the P268
hangar/loadout screen now shows a right-side **detail panel** with the item's
real numeric stats **and** — for a part — whether it actually fits the
player's current hull ("FITS / DOES NOT FIT"). P268's shop previously showed
only name + price, so a player could not tell what a part did or whether it
would mount.

This is display-only: P268's buy/launch flow and `ShopModel`'s purchase logic
are untouched.

## Native content cited

- The docked loadout UI declares the panes in `WinTrek/loadout.h`:
  `PartInfoPane` (`loadout.h:29`), `ConnectorPane` (`loadout.h:22`), and
  `PurchasesPane` (`loadout.h:44`).
- `PartInfoPaneImpl` (`WinTrek/loadout.cpp:1357-1504`) shows the buyable's
  **name**, then a **Cost** / **Time-to-build** pair, then its **description**
  (`DrawDescription`, `loadout.cpp:1443-1488`; the "Cost:  $N" / "Time: N Sec."
  lines at `:1455-1456`). This panel reproduces those three lines verbatim for
  every highlighted hull/part.
- `PartInfoPaneImpl::DisplayTeamPurchase` (`loadout.cpp:1380-1405`) toggles the
  inventory connector pane vs the purchases connector pane based on
  `bPartInInventory` — the native split between "do I already own it" and
  "am I about to buy it". See *PurchasesPane equivalence* below.
- **Compatibility is the engine's real mount test**, ported as
  `DataHullTypeIGC.CanMountPart(in DataPartTypeIGC)` from
  `IhullTypeIGC::CanMount` (`src/Igc/hullTypeIGC.h:199-213`) combined with
  `GetPartMask(EquipmentType, Mount)` (`src/Igc/hullTypeIGC.h:163-176`):
  - weapons are **per-hardpoint**: the hull carries up to `maxWeapons`
    `HardpointData` records, each with a `partMask`, and a weapon fits if any
    one hardpoint's mask covers it (`(part.partMask & ~hp.partMask) == 0`);
  - every other `EquipmentType` is a single mount (mount 0) whose mask is the
    hull's `pmEquipment[et]` table (`PartMask pmEquipment[ET_MAX]`,
    `src/Igc/igc.h:1809`; `ET_MAX = 8`, `igc.h:530-539`).

## Exact decoded fields used (real core data, no placeholders)

All figures come straight from the decoded `PCore001.igc` records.

- **Common (hull & part)**: `Price`, `TimeToBuild`, `Name`, `Description`.
- **Hulls** (`DataHullTypeIGC`): `Mass`, `Signature`, `Speed`, `MaxTurnRates`
  (yaw/pitch/roll), `Thrust`, `HitPoints`, `MaxEnergy`, `RechargeRate`,
  `ScannerRange`, `MaxFuel`.
- **Parts** (`DataPartTypeIGC`, discriminated by `EquipmentType`/`Subtype`):
  - Weapon: `Mass`, `Signature`, `EnergyPerShot`, `DtimeReady`, `DtimeBurst`,
    `Dispersion`, `CAmmoPerShot`;
  - Shield: `Mass`, `Signature`, `MaxStrength`, `RateRegen`;
  - Cloak: `Mass`, `Signature`, `EnergyConsumption`, `MaxCloaking`, `OnRate`,
    `OffRate`;
  - Afterburner: `Mass`, `Signature`, `FuelConsumption`, `MaxThrust`;
  - Pack: `Mass`, `Signature`, `Amount`.
- **Compatibility**: `DataHullTypeIGC.Hardpoints[].PartMask` +
  `DataHullTypeIGC.PmEquipment[(int)EquipmentType]` vs
  `DataPartTypeIGC.PartMask`.

> Finding (not a placeholder, documented): per-shot **damage** and **range**
> are not on the weapon record — they live on the projectile type it points at
> (`DataProjectileTypeIGC.Power`/`Speed`/`Lifespan` via `Weapon.ProjectileTypeId`),
> which the shop catalogue does not carry. Wiring that in would expand
> `ShopCatalog`'s scope, so the weapon panel shows the weapon record's own real
> fields (energy/shot, fire rate, burst delay, dispersion, ammo/shot) instead,
> and damage/range is a named follow-up.

## PurchasesPane equivalence (task item 4 — confirmed, not rebuilt)

P268's `ShopLayout` **PENDING LOADOUT** section (the chosen hull + toggled
parts the player is about to buy) is a genuine equivalent of native's
`PurchasesPane` (`loadout.h:44`) — the pending-purchase list. It is not
rebuilt here. Native's finer-grained **inventory-vs-purchases connector toggle**
(`DisplayTeamPurchase`, `bPartInInventory`) is deliberately collapsed: this
port's panel shows one per-part fit verdict against the current hull, which the
brief allows ("a simple fits / doesn't fit indicator is sufficient").

## Out of scope (unchanged)

- P268 buy/launch flow and `ShopModel` purchase logic — untouched.
- A full graphical connector diagram (native `ConnectorPane` visual layout) —
  ASCII "FITS / DOES NOT FIT" per part is used instead.
- Native reference source (`~/Development/Allegiance`) — read-only, only cited.

## Implementation

- `src/Allegiance.Core/Data/DataHullTypeIGC.cs`: `CanMountPart(in DataPartTypeIGC)`
  — pure port of native `CanMount`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/PartInfoLayout.cs` (new, pure &
  headless-testable, ASCII-safe): builds the detail rows for the highlighted
  row (hull stats OR part stats + compatibility verdict).
- `src/Allegiance.ClientCore.MonoGame/Rendering/DockedScreenRenderer.cs`: new
  `DrawDetailPanel` draws the P276 panel to the right of the shop, sharing the
  renderer's SpriteBatch session; `ShopLayout.Kind` gained `HighlightFit` /
  `HighlightNoFit` (green / red verdict lines).
- `src/Allegiance.ClientCore.MonoGame/Game1.cs`: docked draw passes
  `PartInfoLayout.Build(shopModel)` as the detail rows.

## Tests (new: `tests/.../PartInfoLayoutTests.cs`, 8)

Real-catalogue integration plus a bitwise synthetic test of `CanMountPart`
(native semantics: weapon fits iff a hardpoint mask covers every part bit;
non-weapon fits iff `pmEquipment[et]` covers it). Verdict discrimination
proven: the fixture hull (Adv Scout, real data) has both a compatible real
weapon (PE Gat Gun 1, partMask 0x0001 → FITS) and an incompatible one
(PE Mini-Gun 1, partMask 0x0008 → DOES NOT FIT). Must-fail verified: breaking
`CanMountPart` to always-true removes the no-fit case and the test goes red.

## Verification

- Whole-solution build: **0 warnings, 0 errors**.
- Full test suite run twice; every interruption was a pre-existing documented
  flake (`LiveTurretGunnerBindingTests` persistent machine-state flake;
  `LiveWinConditionTests.Multi_mission_...`, `LiveInSpaceEjectTests.Already_a_...`
  and `LoadoutChangeLiveTests.Mount_and_remove_...` all pass in isolation —
  environmental, not regressions). MonoGame.Tests 428 green (incl. the 8 new).
- **Live visual proof** (offscreen render of the real `DockedScreenRenderer`,
  real PCore001 catalogue; `/tmp/p276verify`): three PNGs —
  - `p276_hull.png` — highlighted hull (Adv Miner) shows real stats
    (mass 100, max speed 120, thrust 1000, hit points 1500, scanner 2666, ...),
    no compatibility section;
  - `p276_fits.png` — highlighted **PE Gat Gun 1** on the Adv Scout shows real
    weapon stats (energy/shot 4.59, fire ready 0.25s, ...) and a green
    **FITS current hull (Adv Scout)**;
  - `p276_nofit.png` — highlighted **PE Mini-Gun 1** on the same Adv Scout shows
    real stats (energy/shot 4.56, ...) and a red
    **DOES NOT FIT current hull (Adv Scout)**.
  Both fit and no-fit cases verified visually.
