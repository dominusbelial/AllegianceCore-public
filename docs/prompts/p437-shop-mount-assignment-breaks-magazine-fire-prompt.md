# P437 — shop's sequential mount numbering breaks missile lock/fire for real players

## Why this task exists

Domingo, live-testing missiles tonight, hit a real, confirmed, previously
undiscovered bug: after buying a launcher (or several) from P434's shop
UI, lock never builds (no HUD sprite) and firing never works — even
though the server confirms the launcher was genuinely granted. Root-caused
this session, end to end, with exact evidence:

- His ship's real granted loadout (server log):
  ```
  [loadout] 13 ship 8110: granting real launcher part 157 ('SRM Anti-Base 1', Magazine@6 ...)
  [loadout] 13 ship 8110: granting real launcher part 155 ('LRM Hunter 1', Magazine@7 ...)
  [loadout] 13 ship 8110: granting real launcher part 153 ('MRM Seeker 1', Magazine@8 ...)
  [loadout] 13 ship 8110: granting real launcher part 150 ('SRM Dumbfire 1', Magazine@9 ...)
  [loadout] 13 ship 8110: granting real launcher part 103 ('MRM Quickfire 1', Magazine@10 ...)
  ```
  Five real magazines genuinely mounted — at `MountId` 6-10, never 0.
- Both real gates that matter are hardcoded to `MountId == 0` for
  `EquipmentType.Magazine`:
  - `Ship.cs:3262` (`UpdateMountedMissileLock`): `_parts.FirstOrDefault(p
    => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0)` — if
    nothing is at mount 0, this returns immediately and
    `UpdateMissileLock` is **never even called**. This is the entire
    reason no lock sprite ever appears — it's not a rendering bug, the
    lock value never leaves `None`.
  - `Server.ShipCommands.cs:867` (`HandleFireMissile`): the identical
    `MountId == 0` lookup, refusing fire with "no mounted missile
    launcher (ET_Magazine@0)" when nothing is there.
- The actual cause of the wrong mount number:
  `src/Allegiance.ClientCore.MonoGame/Rendering/ShopModel.cs`'s
  `TogglePart`/`RenumberMounts` (`ShopModel.cs:316-338`) assigns
  `MountId` as a single **global sequential counter across every chosen
  part regardless of equipment type**
  (`(byte)_chosenParts.Count` on add, `(byte)i` on renumber-after-remove).
  Buy a weapon + afterburner + cloak + shield first (mounts 0-3), THEN a
  magazine, and the magazine lands at mount 4 — never 0. This is why
  bots never hit this bug: their fixed, small loadout builders
  (`FullInventoryParts`/`Catalog.PartsForWeapon`) happen to order things
  so the magazine lands at 0 by construction, not because the mounting
  scheme is actually correct. Native's real hardpoint numbering is
  per-equipment-type (a ship's first magazine is mount 0 REGARDLESS of
  how many weapons/other equipment it also carries) — this port's shop
  numbering is the thing that's wrong.

## Scope

1. **Fix the real bug**: `ShopModel`'s mount-numbering must be
   per-equipment-type, not global. The first `Magazine`-type part a
   player chooses must land at `MountId 0` for that type regardless of
   how many non-magazine parts were chosen before or after it — mirror
   whatever the CORRECT real native/hardpoint numbering scheme actually
   is (investigate `Ship.MountPart`/`ResolvePart`'s own real contract
   for what a "mount" number is actually supposed to mean per type,
   don't just patch around the symptom).
2. **Investigate the real multi-magazine question** before deciding how
   to handle it: does ANY real hull in `PCore001.igc` have more than one
   genuine magazine hardpoint (i.e., is "buy 5 different missile
   launchers and have them all usable at once" ever a real native
   scenario), or does every hull have exactly one real magazine slot? If
   it's genuinely one-slot-per-hull (the likely native reality — a
   single "missile bay"), the shop should behave like a real
   single-slot equipment category: choosing a new launcher REPLACES any
   previously chosen one (the same way a hull change or a single-weapon-
   hardpoint swap already works elsewhere in this codebase — reuse
   that pattern if it exists), rather than silently accumulating five
   unusable magazines. If some hulls genuinely have multiple real
   magazine hardpoints, the fix must correctly number multiple magazines
   0,1,2... within the Magazine type and `HandleFireMissile`/
   `UpdateMountedMissileLock` must pick a sensible one (document which,
   and why) rather than only ever considering mount 0.
3. Confirm (and fix if also broken) whether this exact same
   global-vs-per-type mount numbering bug affects any OTHER
   equipment type through the shop (Weapon/Afterburner/Cloak/Shield) —
   investigate honestly; don't assume it's Magazine-only without
   checking.

## Explicitly out of scope

- `Ship.UpdateMissileLock`'s own lock-building math — untouched, already
  correct once it's actually invoked.
- `HandleFireMissile`'s own fire gate logic (ammo/cooldown/legality) —
  untouched, already correct; only its MOUNT LOOKUP is affected by
  whatever this task decides for multi-magazine handling.
- P433/P434/P435/P436's own work — this is a real bug this session's
  own shop UI work (P434) introduced, being fixed, not a re-scope of
  those tasks.
- `tools/allegiance-mcp`/`tools/vizfight` — their loadout-building
  already avoids this bug by construction (small fixed part lists);
  confirm this remains true, but no changes needed there unless your
  investigation finds a real, separate problem.

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce Domingo's exact real scenario —
  buy a weapon + afterburner + cloak + shield FIRST, then a real
  missile launcher, from the actual shop UI — and confirm via real
  server log lines that the launcher is granted at `MountId 0` this
  time, that lock genuinely builds (real `MissileLockUpdate` reaching
  `IsFullLock`), and that a real `FIRE_MISSILE(70)` is accepted and
  fires (mirroring P433's own live-fire proof standard). Also test
  buying a SECOND different launcher afterward and confirm whatever
  real single-slot-replace-or-multi-slot behavior was decided in step 2
  actually happens as intended.
- A test proving the corrected mount-numbering logic in isolation
  (pure, no graphics device — mirroring `ShopModel`'s existing test
  style).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The mount-numbering fix (+ the single-vs-multi-magazine handling
   decision), committed on `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P437_SHOP_MOUNT_NUMBERING_FIX.md`: the
   exact real cause confirmed, the fix made, the real hull hardpoint
   investigation finding (single vs multi magazine slots), and the live
   fire verification performed.
