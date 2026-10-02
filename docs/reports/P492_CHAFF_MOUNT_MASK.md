# P492 — chaff dispensers fit no hull: verify and fix the part-mask decode

Date: 2026-10-02
Branch: master (committed, not pushed — Domingo pushes)
Prereqs: P435 (real mine/chaff/probe dispenser resolution/mounting/shop/firing) landed.

## Verdict up front

**There is no decode or index bug.** The port's decode of both sides of the
chaff-mount mask is byte-accurate against native, verified from the real bytes
of `PCore001.igc`. Chaff's "fits no hull on a hull-swap" outcome is a faithful
reproduction of native, caused by a genuine **PCore001 data inconsistency**:
every hull's chaff slot `PmEquipment[ET_ChaffLauncher]` is `0x0100` (light
hulls) / `0x0200` (capitals), while every real chaff launcher's embedded
`LauncherDef.partMask` is `0x0001` (Counter) / `0x0002` (Hvy Counter). Under
native's own mounting rule `(partMask & ~mountMask) == 0` no chaff launcher
ever fits — in the port **and** in native.

This is the finding P465c flagged as "`PmEquipment[0]` / `LauncherPartMask`
decode remains the suspected culprit" — but the suspicion is **disproven** by
the byte evidence below. Accordingly no production code changes were made
(fabricating a repaired mask would diverge from native fidelity); the deliverable
is this verification report plus regression tests that lock the faithful decode,
and a reproduction of the native-faithful same-hull chaff mount + fire path.

## The three things the brief asked to check (all correct)

1. **`EquipmentType` enum order / which index is `ET_ChaffLauncher`**
   Native (`src/Igc/igc.h:530-539`): `ET_ChaffLauncher = 0`,
   `ET_Weapon = 1`, `ET_Magazine = 2`, `ET_Dispenser = 3`, `ET_Shield = 4`,
   `ET_Cloak = 5`, `ET_Pack = 6`, `ET_Afterburner = 7`, `ET_MAX = 8`.
   The port's `EquipmentType` (`DataPartTypeIGC.cs:9-19`) is identical. So
   `PmEquipment[ET_ChaffLauncher] == PmEquipment[0]` — correctly the slot a
   chaff launcher (also `ET_ChaffLauncher == 0`) is checked against.

2. **Struct offset and size of `pmEquipment`**
   Native (`igc.h:1809`) declares `PartMask pmEquipment[ET_MAX]` — 8 × `short`
   = 16 bytes. Struct arithmetic places it at payload offset **509** (verified
   against the C++ default-alignment layout: `DataBuyableIGC` ends at 362,
   `mass@364 … textureName@496..509`, so `pmEquipment@509..525`). The port
   decodes exactly that (`DataHullTypeIGC.cs:308-310, 365`), and native's hull
   loader is a plain `memcpy` with **no** reordering of the array
   (`hullTypeIGC.cpp:32`), so file order == `GetPartMask`'s `pmEquipment[et]`
   order (`hullTypeIGC.h:163-176`).

3. **The field the launcher's mask is decoded from**
   The mountable launcher part's mask comes from the EXPENDABLE's embedded
   `LauncherDef.partMask` (`igc.h:1939-1945`; wired in
   `launchertypeigc.cpp:31-38`: `m_launcherDef = pexpendabletype->GetLauncherDef()`).
   For chaff that is `DataChaffTypeIGC.launcherDef.partMask` at payload offset
   **436**, little-endian — the same offset all four expendable kinds use
   (missile/mine/probe decode correctly at 436, cross-checked by their distinct
   mask values). The port reads it there (`DataChaffTypeIGC.cs:92`,
   `DataPartTypeIGC.FromChaffLauncher`).

## Truth from the real bytes (measured, PCore001.igc)

447 real hulls; 6 real chaff launchers.

| chaff launcher | LauncherPartMask |
|---|---|
| Counter 1 / 2 / 3 | 0x0001 |
| Hvy Counter 1 / 2 / 3 | 0x0002 |

Hull chaff-slot distribution `PmEquipment[0]`:

| value | hulls |
|---|---|
| 0x0000 | 40 |
| 0x0100 (light hulls: fighters/scouts/interceptors/bombers) | 334 |
| 0x0200 (capitals: corvette/cruiser/destroyer/carrier/devastator) | 73 |

Mount result: **(hull × chaff) pairs that fit = 0 of 2682**; hulls accepting at
least one chaff launcher = **0**. The parallelism is unmistakable and is what
the data author intended (Counter → 0x0100 light hulls, Hvy Counter → 0x0200
capitals), but the chaff launcher masks are authored in a *lower* bit-space
(`0x0001`/`0x0002`) than any hull's chaff slot, so `(0x0001 & ~0x0100) != 0`.

A full scan of every one of the 6 chaff records finds **no** `0x0100`/`0x0200`
two-byte field anywhere (the only `0x0100` hit is a misaligned overlap with
`launcherDef.expendableSize`), so there is no alternate mask field to read: the
value that would make chaff fit is simply not in the file.

## Native behaves identically (why this is faithful, not a port bug)

- Native hull loader copies `pmEquipment` verbatim (`hullTypeIGC.cpp:32`) → a
  fighter's chaff slot is `0x0100` in native too.
- Native `ClauncherTypeIGC::GetPartMask()` returns the expendable's
  `launcherDef.partMask` (`launchertypeigc.h`) → chaff is `0x0001` in native
  too.
- Native `IhullTypeIGC::CanMount` uses the same `(partMask & ~mountMask) == 0`
  (`hullTypeIGC.h:199-213`), same as the port's `CanMountPart`
  (`DataHullTypeIGC.cs:247-259`).

Therefore native also concludes chaff does not survive a hull-swap in PCore001.
Native players experience chaff through the **same-hull free-mount path** (the
port's P334 gate skips `CanMountPart` when the hull is unchanged), which is why
chaff is ubiquitous in play despite fitting no hull under the swap gate.

## Other equipment is healthy and unchanged (positive control)

Measured under the identical gate (447 hulls):

| expendable kind | launchers | hulls accepting ≥ 1 |
|---|---|---|
| missile | 56 | 188 |
| mine | 8 | 32 |
| probe | 53 | 447 |
| **chaff** | **6** | **0** |

Missiles/mines/probes fit the hulls they should, exactly as before. Because no
code changed, the before/after mount matrix is **byte-identical**: chaff `0/2682`
before and after; every other part's per-hull result unchanged (the full
matrix is reproducible from the test + the reproduction below).

## Reproduction / demonstration

- **Regression tests (new):** `tests/Allegiance.Core.Tests/ChaffMountMaskVerificationTests.cs`
  (4 tests, real PCore001) lock the faithful decode — chaff launcher masks are
  `0x0001`/`0x0002`; fighter/capital chaff slots are `0x0100`/`0x0200`; no hull
  covers any chaff mask; and a positive control asserting missile/mine mounting
  is intact. These pass on the current tree and will FAIL if a future change
  corrupts either side of the mask decode.
- **Live same-hull mount + fire (existing, re-run):**
  `DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live` (P435),
  Mine/Chaff/Probe **3/3** — a real wire BUY_LOADOUT on a real in-process server
  grants and mounts a real chaff dispenser (same-hull free-mount path) and a
  real `SendFireExpendable` reaches `HandleFireExpendable` → `DeployChaff`
  (P435's server log lines `… fired chaff 53 … dispenser 10 -> 9`). This is the
  path native players actually used.

## Verification

- Whole-solution `AllegianceCore.slnx -c Release` build: **0 warnings, 0 errors**
  (`/home/domingo/.dotnet/dotnet build`).
- Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (measured
  Passed! totals, Release):

  | project | run 1 | run 2 | run 3 |
  |---|---|---|---|
  | Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
  | Allegiance.ClientCore.MonoGame.Tests | 1079/1079 | 1079/1079 | 1079/1079 |
  | Allegiance.ClientCore.Tests | 401/401 | 400/401 | 400/401 |
  | Allegiance.Core.Tests | 3684/3687 | 3685/3687 | 3681/3687 |

  The only failures are the pre-existing, documented live-wire/load-flake
  family in `docs/TEST_FLAKES.md` (CargoBayReload is explicitly ledgered as a
  load-flake class, `TEST_FLAKES.md:53-54,122`; NpcShipFoundationLiveTests and
  LiveLifepodEnduranceDisplayTests recur in the same rotating live-wire timing
  family — Core counts rotated 3/2/6, ClientCore 0/1/1 across the three runs;
  a `--filter`-style rerun of Core.Tests confirmed the observed trio:
  `NpcShipFoundationLiveTests.Ownerless_npc_ship…`, `LiveLifepodEnduranceDisplayTests.Driver_sees…`,
  `CargoBayReloadTests.Reload_NA…`). **None of the failures reference any P492
  symbol.** The new `ChaffMountMaskVerificationTests` (deterministic, real
  PCore001, no live network) pass 4/4 in isolation and are unaffected by these
  timing flakes.

## Deliverables

1. Committed on `master`, not pushed (checked `git branch --show-current`).
   Subject `git log -1 --format=%s`: "P492: verify chaff part-mask decode — no bug, PCore001 data inconsistency" (hash in the live tree; confirm via `git log -1`).
2. This report: `docs/reports/P492_CHAFF_MOUNT_MASK.md`.

## Out of scope (unchanged, per brief)

- No chaff decoy-behaviour changes — already native-matching per the weapons pass.
- No shop-UI changes beyond what the fixed data naturally produces — there is no
  data fix to produce any, so none were made.
