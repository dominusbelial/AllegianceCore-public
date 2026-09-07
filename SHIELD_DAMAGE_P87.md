# P87 — Real shield damage absorption (Phase 2, slice 1)

**Date:** 2026-09-07
**Repo:** `/home/domingo/Development/AllegianceCore`, branch `master` (tip `05257b6`, the P86 checkpoint, before this work).
**Scope:** retire the deferred ("no shields") half of `Ship.ReceiveDamage` —
the roadmap's Phase-2 primary goal "combat depth". Damage absorption only.

---

## 1. The exact multiplication order (traced from both source ends)

The crux of this task is *whose* armor constant is applied *where*, because a
naive port that double-applies or never-applies `dtmArmor` still compiles and
passes a loose test. Reading both ends together resolves it:

**Caller — `CshipIGC::ReceiveDamage` (`src/Igc/shipIGC.cpp:1212-1300`):**
- `shipIGC.cpp:1233` — `dtmArmor = GetDamageConstant(type, hullDefenseType)` (the **hull's** constant).
- `shipIGC.cpp:1285` — when a shield is mounted it calls
  `ApplyDamage(timeCollision, type, amount, deltaP)` passing the **raw,
  un-multiplied** `amount`; `shipIGC.cpp:1290` — no shield ⇒ `leakage = amount`.
- `shipIGC.cpp:1297` — *after* the call, the **caller** does `leakage *= dtmArmor`.

**Shield — `CshieldIGC::ApplyDamage` (`src/Igc/shieldIGC.cpp:140-195`):**
- `shieldIGC.cpp:145-146` — `dtm = GetDamageConstant(type, shieldDefenseType)`
  (the **shield's** own constant, its `DataShieldTypeIGC::defenseType`), then `amount *= dtm`.
- `shieldIGC.cpp:156` — `absorbed = amount / maxStrength` (now in armor-multiplied units).
- `shieldIGC.cpp:158` — `down = GetFloatConstant(c_fcidDownedShield)`.
- `shieldIGC.cpp:160-164` — if `m_fraction <= down`: `leakage = amount / dtm`
  (**divides the shield's own armor back out** ⇒ returns the original raw `amount`).
- `shieldIGC.cpp:167` — `usefulShieldFraction = m_fraction - down`.
- `shieldIGC.cpp:173-177` — if `absorbed <= usefulShieldFraction`: `leakage = 0` (full absorb).
- `shieldIGC.cpp:178-187` — else `leakage = (absorbed - usefulShieldFraction) * maxStrength / dtm`
  (**again divides back out** the shield's `dtm`).
- `shieldIGC.cpp:189` — `SetFraction(m_fraction - absorbed)`.

**Net effect:** the shield's own `dtm` is applied *inside* `ApplyDamage` and
*cancelled back out* before it returns, so the caller's `leakage *= dtmArmor`
(re-applied at `shipIGC.cpp:1297`, already present in this port's `Ship.cs`
long before P87) applies the **hull's** constant exactly once. The two constants
are deliberately different (`dtm` uses the shield's `defenseType`; `dtmArmor`
uses the hull's `defenseType`), and the port preserves that separation:
`Ship.ApplyShieldDamage(type, amount, maxStrength, defenseType)` does
`amount *= DamageConstant(type, defenseType)` and then `/ dtm` on the way out,
while the caller's existing `leakage *= DamageConstant(type)` (the hull's) stays
outside. This is why the "shield down" case is byte-for-byte identical to
"no shield": both return raw `amount`, then the caller's `dtmArmor` applies once.

### Where the order is enforced in code

- `src/Allegiance.Core/Sim/Ship.cs` — `ReceiveDamage` (now mounts a shield-aware
  branch), `DamageConstant(byte,byte)`, `ApplyShieldDamage`, `ShieldFraction`.
- `src/Allegiance.Core/Wire/ServerShipUpdate.cs:123` — wire byte 41.

---

## 2. Explicit simplifications (all stated, none hidden)

1. **No shield regeneration.** `CshieldIGC::GetRegeneration`
   (`shieldIGC.h:106-111`, `rateRegen` per tick) is a separate, real mechanic and
   is out of this slice. A shield that has absorbed stays down until... nothing
   in this slice — the *next* natural follow-up. Not half-implemented here.
2. **Side-level global attributes omitted** (`c_gaMaxShieldShip`,
   `shieldIGC.h:112-117`; the `c_gaShieldRegenerationShip` term,
   `shieldIGC.h:106-111`). The side `GlobalAttributeSet` system is not ported
   anywhere in this codebase, so both multipliers are treated as 1.0 — the same
   honest-simplification style this repo already uses for the payday/income
   constants.
3. **Hardcoded `c_downedShield = 0.1f`.** The C++ defines it
   (`missionigc.cpp:2690`) and wires it into the `DataConstantsIGC` slot
   `c_fcidDownedShield` (`missionigc.cpp:2745`) as a mission-init override. This
   port hardcodes the literal `0.1f` with that citation, following the same
   pattern the port already uses for `Mission.SecondsBetweenPaydays` /
   `IncomePerPayday` (no `DataConstantsIGC.FloatConstants` array is wired up for
   a single value).
4. **No hit-shape bookkeeping.** `SetFraction`'s side effect
   (`shieldIGC.h:129-143`, `SetShape(c_htsEllipse/convexHullMax)`) is native
   rendering/collision-shape bookkeeping with no analogue in this port; skipped.
5. **Multiple shields:** the native `m_mountedOthers[ET_Shield]` is a single
   slot; this port mirrors that — `ReceiveDamage` looks up the first
   `EquipmentType.Shield` part in `Ship.Parts` (matching how `Weapons` is
   consulted for the existing combat code), so the usual one-shield loadout is
   the faithfully mirrored case.

---

## 3. The three-case damage-math distinction, with the real asserted numbers

The real-hit tests run against the representative static core the test suite
loads (`Corpus.Files`, first `VersionAndDataSize` wrapper). Reported numbers are
what the tests actually assert (asserted via `Assert.Equal(…, 3)` where noted);
they are computed from the decoded core, not hardcoded. Headline:
**the actual test core sits on an identity armor diagonal — every obtainable,
survivable shield combo has `dtmShield = dtmHull = 1`.** So the *real-hit*
tests verify the three cases and their relative behaviour with real numbers, and
the non-unit-armor multiplication order is pinned separately by a **direct seam
test** (§3.4) that calls the private `ApplyShieldDamage` with a real shield
whose armor cell is genuinely ≠ 1.

- **Case 1 — full absorb** (`Small_hit_is_fully_absorbed_and_leaves_the_hull_untouched`):
  hull 13 (225 HP), a real mounted shield, one real weapon hit (raw 100, `dtmS=1`,
  `dtmH=1`). `absorbed = 100·1/str = 0.2 ≤ useful(0.9)`, so `leakage = 0`:
  shield `1.0 → 0.8` (asserted `expectedNewShield = 1 − 0.2 = 0.8`),
  hull `Fraction` stays exactly `1.0`. This is the "shield fully absorbs, hull
  takes nothing" branch.
- **Case 2 — partial absorb / overload** (`Big_hit_drains_the_shield_and_leaks_the_excess_to_the_hull`):
  same hull 13 (225 HP), a different real shield, one real weapon hit (raw 200,
  `dtmS=1`, `dtmH=1`). `absorbed = 200/str = 1.111 > useful(0.9)`, so the
  abnormal branch leaks `(1.111−0.9)·str/1 = 38`; the caller's `·dtmArmor(1)`
  makes 38 hull damage. Asserted: shield `→ 0` (clamped), hull `225 → 187`
  (`MaxHP − ExcessHullDamage`, `Assert.Equal(…, 1)`). This is the "both shield
  drained AND hull takes the armor-multiplied remainder" branch.
- **Case 3 — shield down** (`Down_shield_leaks_identically_to_unshielded`):
  the shield is knocked to `0` (≤ the 0.1 threshold), then a further identical
  real hit lands. Asserted: the shielded ship's hull-fraction delta
  (`0.8889`) equals the no-shield ship's delta (`0.8889`) *exactly*, and both
  equal `raw·dtmHull/maxHP = 200·1/225 = 0.8889`. This is the "down shield leaks
  exactly as if unshielded" branch — the structural equivalence the task's
  warning targets.

- **§3.4 — the seam test pins the multiplication order with a non-unit cell.**
  `ApplyShieldDamage_seam_keeps_armor_order_with_non_unit_cell` (in
  `CombatShieldTests.cs`) reflects the private `Ship.ApplyShieldDamage` and feeds
  it a **real** shield part whose armor cell for the chosen damage type is ≠ 1
  (the test locates one in the loaded core; the value observed is `dtm ≈ 0.75`).
  With `raw = 813.75`:
  - **down branch** — asserts `leakage == raw` (813.75), i.e. the shield's
    `amount *= dtm` is divided back out by `dtm` (proven: drop that `/dtm` and
    leakage becomes `813.75·0.75 = 610.3125`, and the assert fails);
  - **partial branch** — with `fraction = 0.9` (`useful = 0.8`) and a raw sized
    so `absorbed = 1.081`, asserts `leakage == (1.081−0.8)·maxStrength/dtm`
    (proven: forget `amount *= dtm` and the value diverges to `1085`, and the
    assert fails).
  These are the two *new* multiplication sites in this task; both demonstrably
  discriminate a wrong order with real numbers (both were verified to fail when
  the code was temporarily mutated).

- **Case 4 — no shield mounted** is the unchanged pre-P87 path. The entire
  existing `CombatDamageTests.cs` suite passes unmodified (no weakened
  assertions), and `CombatShieldTests` includes a mount/clear test asserting a
  fresh ship reads `ShieldFraction == 0`, mounts → `1.0`, `ClearLoadout` → `0`.
- **Wire:** `ServerShipUpdate.FromModel` on a shielded ship round-trips a
  non-zero `ShieldFraction` through byte 41 (encode → decode → property reads
  back ~1.0 clamped); a no-shield ship encodes `0.0`.

---

## 4. Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — `_shieldFraction` field + `ShieldFraction`
  property; `MountPart` arms a `Shield` part (`CshieldIGC::Arm`,
  `shieldIGC.h:99-103`); `ClearLoadout` resets to 0; `ReceiveDamage` shield
  branch; `DamageConstant(byte,byte)` overload; `ApplyShieldDamage` helper;
  updated the old "No shields" doc remark.
- `src/Allegiance.Core/Wire/ServerShipUpdate.cs` — `u._b[41]` now writes
  `Math.Clamp(ship.ShieldFraction, 0, 1)` (was hardcoded `0.0`).
- `tests/Allegiance.Core.Tests/CombatShieldTests.cs` — new (6 tests).

## 5. Verification

- No VM, no native-repo change, no new wire message, no undocumented struct —
  none of this slice needed a Windows-VM probe (the native `src/Igc/*` sources
  were read directly for reference only).
- Full suite: **3 consecutive clean runs**, 2307 tests (2301 baseline + 6 new),
  0 failures each. Looped with
  `/home/domingo/.dotnet/dotnet test` (the broken PATH snap `dotnet` is never used).
