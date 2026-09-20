# P299 — Nanite-gun 2× repair credit for bomber/HTT targets (`c_habmThreatToStation`)

Direct, small extension of **P277** (the nanite field-repair weapon). Native
doubles the nanite-gun *repair credit* when repairing a hull whose base type
carries the 16th `HullAbilityBitMask` bit `c_habmThreatToStation` — *"double
points for nanning a bomber/htt"* (`Igc/shipIGC.cpp:1262-1263`).

## Relationship to native (cited, not assumed)

Native `CshipIGC::ReceiveDamage`'s repair branch (`shipIGC.cpp:1244-1267`)
restores the target's hull fraction, then — **only** when the launcher is a ship
repairing a *friendly* (same-side or allied) target (`:1257-1258`) — credits the
launcher a repair stat (`:1260-1266`):

```cpp
float repairFraction = -amount * dtmArmor / maxHP;
if (GetBaseHullType() && (GetBaseHullType()->GetCapabilities() & c_habmThreatToStation))
    repairFraction *= 2.0f;          //double points for nanning a bomber/htt
pIship->AddRepair(repairFraction);
```

`AddRepair` (`shipIGC.h:1882-1884`) accumulates `m_repair` — *"Xynth: amount of
nanning performed by ship as a fraction of hull repaired"*. So:

- The doubled value is the launcher's **repair credit** (a "fraction of a hull
  repaired" stat), **not** the hull-restore amount. A bomber is healed at the
  same rate as a fighter; only the launcher's credited points double.
- Credit is awarded **only for a friendly target** (the side test exists solely
  to decide the launcher's repair-stat). A cross-side nan round still heals the
  enemy's hull (P277's faithful wrinkle) but earns no credit.
- On over-repair, native shrinks `amount` to "the amount that had effect for
  stat" (`:1250-1251`), so the credited stat matches the real repair and
  spraying a topped-up hull farms nothing.

## What "repair credit" means in THIS port's P277 (scope-point 3 answered)

P277's doc said plainly: *"no repair source exists yet, so it is dead code in
practice"* and the port had **no repair-stat infrastructure at all** — P277 only
fed the direct hull-restore amount. So P299 does not double an existing number:
it adds the missing analogue of native's `m_repair` so the bonus has a real
target. That analogue is `Ship.Repair` / `Ship.AddRepair(float)` (the "fraction
of a hull repaired" accumulator), not the hull-restore delta. Nothing was
fabricated; this is native's exact value and unit (`shipIGC.h:1882-1884`).

## Changes

- `src/Allegiance.Core/Data/DataHullTypeIGC.cs` — new `HabmThreatToStation =
  0x200` constant (`igc.h:735`), read via the existing `HasCapability(short)`.
- `src/Allegiance.Core/Sim/Ship.cs`
  - New `_repair` field + `Repair` property + `AddRepair(float)` (port of native
    `m_repair` / `AddRepair`).
  - `ReceiveDamage`'s `amount < 0` branch now awards the launcher credit after
    restoring hull: friendly gate collapses to **same-side-only** (`SideId ==
    launcher.SideId`) because this port has no alliance/`AlliedSides` concept
    (the established P107/P112/P115/P203 precedent), and doubles the credit when
    the target's `HullType` carries `HabmThreatToStation`. The clipped/effective
    restored fraction is derived directly (mirrors native's `amount`-shrinking),
    so over-repairing a full hull credits ~0.

The hull-restore math is untouched — P277's base repair mechanic is unchanged
(out of scope).

## Which real hulls carry the bit (scope-point 1 answered)

Measured against the **whole real corpus** (`PayloadDecodeTests.Hulls()` +
`Corpus.Files`): **9,139** hull records across the shipped cores carry `0x200`,
led by the bomber/HTT/capital classes native names — every `bom*`, the HTT/transport
set (`ta_htt`, `tftransport`, `dregtt`, `utl*`), capitals/cruisers (`cap*`,
`crus*`, flagships), plus a scattering of assault fighters (e.g. `fig04`,
`fig07`, `fig20`) and attack-stealth (`dn_fadrshstl`) records. The same model
name recurs with different capability sets in one core (e.g. `dn_fadrshbmb` at
`0x0892` without the bit and `0x0A92` with it), so the bit — not the name — is
the faithful discriminator, and all tests select by bit.

## Tests

`Allegiance.Core.Tests/NaniteThreatBonusTests.cs` (3, unit):
- `Same_side_threat_target_earns_double_repair_credit` — a real bomber hull
  (`0x200` set) and a real non-threat hull each repaired by the same 0.1 fraction
  of their hull earn launcher credit `2×0.1` and `1×0.1`; hull-restore stays 0.1
  for both (the bonus is launcher-side points, not a stronger heal).
- `Cross_side_repair_heals_hull_but_awards_no_credit` — an enemy's hull is still
  healed but the launcher earns no credit (faithful side gate).
- `Repairing_an_already_full_hull_adds_no_credit` — over-repair credits ~0
  (native's clipped-stat behavior).

`Allegiance.ClientCore.Tests/NaniteThreatBonusLiveTests.cs` (1, live over real
loopback TCP): boots two real in-process servers on `XC02.igc`, each with a real
nan-gunned shooter and a friendly target damaged to the same 0.5 fraction — once
a real Dreg **bomber** (`dn_fadrshbmb`, bit set), once a real **fighter**
(`dn_fadrshfig`, bit clear). Both repaired to full by the real ER Nanite; the
shooter's `Repair` is read from the live server ship:

```
LIVE-NAN-CREDIT[dn_fadrshbmb thr=True]:  shooter.Repair = 1.0000 after 155 ticks (start 0.5)
LIVE-NAN-CREDIT[dn_fadrshfig  thr=False]: shooter.Repair = 0.5000 after  61 ticks (start 0.5)
LIVE-NAN-CREDIT-RATIO: bomber 1.000 / fighter 0.500 = 2.000 (expect ≈2)
```

Each target restored 0.5 of its own hull; the bomber's credit is **exactly
2×** the fighter's — the genuine before/after the task requires, in real server
state driven by real fired rounds.

**Must-fail verified (both):** removing the `credit *= 2.0f` branch turns the
unit `Same_side_threat_target_earns_double_repair_credit` assertion (2Δ→Δ) and
the live ratio assertion (2.0→1.0) red; both revert cleanly.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full suite: see run results recorded at task completion; only the documented
  pre-existing flakes (`LiveTurretGunnerBindingTests` timing, etc.) are expected.

## Out of scope (unchanged)

- `c_habmCaptureThreat` / `c_habmMiner` (the other two missing hull-ability bits
  from the same pass) — separate task.
- Any change to P277's base repair mechanic beyond this bonus multiplier.
- Native reference source (`~/Development/Allegiance`) — read-only, never modified.
