# P492 — chaff dispensers fit no hull: verify and fix the part-mask decode

## Why this task exists

P435 (`docs/reports/P435_*`) found that all **6** real chaff dispensers in
PCore001 "fit NO hull": no hull's `PmEquipment[ChaffLauncher]` covers their
mask. Chaff mounts only on the same-hull free-mount path and gets dropped on
any hull swap. P465c restated the suspicion and didn't investigate it:
"`PmEquipment[0]` / `LauncherPartMask` decode remains the suspected culprit".
Native players clearly used chaff, so this is almost certainly a **decode
or index bug**, not real data. Native is at `~/Development/Allegiance/src/`.

- Native's hull field is `PartMask pmEquipment[ET_MAX]` (`Igc/igc.h:1809`).
- The port decodes it into `DataHullTypeIGC.PmEquipment`
  (`src/Allegiance.Core/Data/DataHullTypeIGC.cs:53`, `:365`).
- The port's mount gate is `CanMountPart` (`:232-260`).
- On the launcher side, the chaff dispenser's `PartMask` comes from the
  24-byte `DataLauncherTypeIGC` record.

## Scope

1. **Establish the truth from the real bytes.** For every real hull and the
   6 chaff dispensers, dump the raw `pmEquipment[]` array (all `ET_MAX`
   slots) and each dispenser's mask. Check, with `igc.h` citations:
   - the `EquipmentType` enum order (which index is `ET_ChaffLauncher`);
   - the struct offset and size of `pmEquipment`;
   - the field the launcher's mask is decoded from.
   Find the mismatch.
2. Fix the decode or index at its source. Then show which hulls really
   accept chaff, and that the same fix doesn't change mounting for other
   equipment types: compare before/after mount results for every hull and
   part.
3. Show a chaff dispenser mounting on a real hull through a hull-swap
   `BUY_LOADOUT`, and firing (P435's `HandleFireExpendable` → `DeployChaff`).

## Explicitly out of scope

- Chaff decoy behaviour, which already matches native per the weapons pass.
- Shop UI changes beyond what the fixed data naturally produces.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P492_CHAFF_MOUNT_MASK.md`.
