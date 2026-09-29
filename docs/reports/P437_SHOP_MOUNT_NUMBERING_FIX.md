# P437 — Shop sequential mount numbering breaks missile lock/fire for real players

Date: 2026-09-29
Branch: `master` (landed directly, per the AllegianceCore convention — Domingo pushes)

## 1. The exact real cause (confirmed)

Domingo live-tested missiles and found that after buying a launcher from P434's
shop UI, lock never built (no HUD sprite) and firing never worked, even though
the server log confirmed the launcher was genuinely granted:

```
[loadout] 13 ship 8110: granting real launcher part 157 ('SRM Anti-Base 1', Magazine@6 ...)
[loadout] 13 ship 8110: granting real launcher part 155 ('LRM Hunter 1', Magazine@7 ...)
[loadout] 13 ship 8110: granting real launcher part 153 ('MRM Seeker 1', Magazine@8 ...)
[loadout] 13 ship 8110: granting real launcher part 150 ('SRM Dumbfire 1', Magazine@9 ...)
[loadout] 13 ship 8110: granting real launcher part 103 ('MRM Quickfire 1', Magazine@10 ...)
```

Five real magazines mounted at `MountId` 6–10, **never 0**.

Two independent consumption gates are hardcoded to `MountId == 0` for
`EquipmentType.Magazine`:

- `Ship.UpdateMountedMissileLock` (`Ship.cs:3262`):
  `_parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0)`
  — with nothing at mount 0 it returns immediately and `UpdateMissileLock` is
  **never called**, so the lock value never leaves `None`. This is the entire
  reason no lock sprite ever appears — it is not a rendering bug.
- `Server.HandleFireMissile` (`Server.ShipCommands.cs:867`): the identical
  mount-0 lookup refuses fire with "no mounted missile launcher (ET_Magazine@0)".

The actual root cause is in `ShopModel`'s mount numbering:

- `ShopModel.TogglePart` assigned `MountId = (byte)_chosenParts.Count` — a
  single **global sequential counter across every chosen part regardless of
  equipment type**.
- `ShopModel.RenumberMounts` renumbered every part to `(byte)i` (global list
  position).

Buy a weapon (0) + afterburner (1) + cloak (2) + shield (3) first, then a
launcher, and the magazine lands at mount 4 — never 0. The server faithfully
honoured the requested mount id (`ApplyLoadout → Ship.MountPart(partType, …,
p.MountId, …)`), so the magazine sat at a slot neither the lock nor fire gate
ever reads.

**Why bots never hit it:** `tools/allegiance-mcp` `Catalog.PartsForWeapon` /
`ResolveParts` and `tools/vizfight` `FullInventoryParts` / `AppendCompatibleLauncher`
build small fixed part lists and hardcode every part at **mount 0** (per-type) —
the weapon at 0, afterburner/cloak/shield each at 0, and the launcher at 0 —
so the magazine is at 0 by construction, not because the mounting scheme was
right. Verified (see §5).

## 2. The real hardpoint/mount contract (investigated, not patched around)

The port's own mount contract is `(EquipmentType, MountId)` everywhere:

- `Ship.ResolvePart(et, mount)` scans `_parts` for `EquipmentType == et && MountId == mount`.
- `Ship.TrySwapPart`/`TrySwapPartWithCargo` trade same-`EquipmentType` slots.
- Every non-weapon consumer resolves its type at **mount 0**: the P269 cloak
  slot (`Ship.cs:6183`, `ET_Cloak && MountId == 0`), the P412 afterburner slot
  (`Ship.cs:6311`, `ET_Afterburner && MountId == 0`), dispenser/chaff fire
  (`HandleFireExpendable`, `ET == req.Et && MountId == 0`), missile lock/fire
  (above), and the P192 reload `FirstOrDefault(p => ET == launcherType && MountId == 0)`.

`DataHullTypeIGC.CanMountPart` documents and enforces the native reality:
**only `Weapon` is per-hardpoint** (the hull carries up to `MaxWeapons`
`Hardpoint` records, each with its own `partMask`, so weapons mount at 0..N);
**every other equipment type has a single mount, mount 0**, whose mask is the
hull's `PmEquipment[et]` table (`PartMask pmEquipment[ET_MAX]`,
`src/Igc/igc.h:1809`). There is no concept of a second magazine hardpoint on
any real hull — a ship has exactly one "missile bay" (one `ET_Magazine` slot).

**Decision (step 2 of the brief): single-slot per non-Weapon type.** Because no
real hull has more than one magazine hardpoint, "buy five different launchers
and have them all usable at once" is not a real native scenario. The shop must
therefore behave like a real single-slot equipment category: choosing a new
launcher **REPLACES** the previously chosen one, exactly as the port's own
tool loadout builders already do, rather than silently accumulating five
unusable magazines. `HandleFireMissile` / `UpdateMountedMissileLock` correctly
keep reading the single slot at mount 0 — no multi-magazine logic is warranted,
and none was added.

## 3. Scope item 3 — the same bug broke Cloak and Afterburner too (confirmed)

Investigating honestly rather than assuming Magazine-only, the global numbering
also broke every other non-Weapon type that is consumed at mount 0:

- **Cloak** — `Ship.cs:6183` reads `ET_Cloak && MountId == 0`; a cloak bought
  after a weapon landed at mount ≥1 and never engaged.
- **Afterburner** — `Ship.cs:6311` reads `ET_Afterburner && MountId == 0`;
  same non-zero mount problem.
- **Dispenser / ChaffLauncher** — `HandleFireExpendable` reads `ET == req.Et &&
  MountId == 0`; same problem (part of the launcher family).
- **Shield** — `Ship` shields are read by type alone
  (`FirstOrDefault(p => p.EquipmentType == EquipmentType.Shield)`, mount-agnostic),
  so a shield "worked" despite the wrong mount; it is now also correctly at
  mount 0 for consistency.

The per-type fix in §4 repairs **all** of these uniformly, because it forces
every non-Weapon type to its single mount 0.

## 4. The fix

`src/Allegiance.ClientCore.MonoGame/Rendering/ShopModel.cs`:

1. **`RenumberMounts` is now per-equipment-type.** Each chosen part's mount is
   derived from its real `EquipmentType` (resolved via the existing
   `LookupPart`, which covers both core parts and P434/P435 launcher offers):
   a non-`Weapon` part → **mount 0**; a `Weapon` part → its hardpoint index
   `0..N` within the Weapon type (native's per-hardpoint scheme).

2. **`TogglePart` enforces the single-slot contract.** When a chosen part is a
   non-`Weapon` type and a part of that same `EquipmentType` is already chosen,
   the new part **replaces** the old one (the native one-slot reality). Weapons
   stack (per-hardpoint). Removing an existing part and renumbering still work
   as before.

This is the same scheme the proven-working tool loadout builders already use
(every non-Weapon type at mount 0, weapons stacking), so the human shop now
produces exactly the loadout shape a P433/P435-equipped bot carries.

### Semantics of mount id after the fix

Multiple different non-Weapon types legitimately share "mount 0" — that is
correct, because mount numbers are namespaced by `EquipmentType` (a `Magazine@0`
and a `Cloak@0` are distinct parts, exactly as in the tool loadouts and as the
server resolves them). The point of the fix is that the *first* magazine the
player buys is always at the slot the lock/fire gates read, regardless of how
many weapons/other equipment were chosen before or after it.

## 5. Verification — real proof

### 5a. Whole-solution build

All seven `src` projects build clean with `/home/domingo/.dotnet/dotnet build -c Release`:

```
Allegiance.Core        : 0 Warning(s) 0 Error(s)
Allegiance.Server      : 0 Warning(s) 0 Error(s)
Allegiance.ClientCore  : 0 Warning(s) 0 Error(s)
Allegiance.ClientCore.MonoGame : 0 Warning(s) 0 Error(s)
Allegiance.Bot         : 0 Warning(s) 0 Error(s)
Allegiance.Lobby       : 0 Warning(s) 0 Error(s)
Allegiance.ClientCore.Explorer : 0 Warning(s) 0 Error(s)
```

(Tests inherit `TreatWarningsAsErrors` from `Directory.Build.props`, so the
test build is 0-warning too.)

### 5b. Unit test of the corrected numbering (pure, no graphics device)

`tests/…/ShopModelTests.cs` — added:

- `Launcher_bought_after_weapon_ab_cloak_shield_lands_at_mount_zero` — Domingo's
  exact sequence (weapon → afterburner → cloak → shield → missile launcher)
  leaves all five distinct parts chosen with the launcher at **mount 0** and
  every non-weapon type at its single mount 0.
- `Choosing_a_second_launcher_replaces_the_first_single_slot` — toggling a
  second different launcher collapses the Magazine set to exactly one (the new
  one) at mount 0.
- `Multiple_weapons_stack_across_distinct_weapon_mounts` — two guns sit at
  Weapon mounts 0 and 1 while the single launcher holds its own Magazine mount 0.

Updated the two P394 seeding tests that previously asserted the old global
sequential convention (`Shop_opens_with_current_parts_pre_seeded`,
`Seeded_parts_renumber_to_per_type_mount_ids`) to the corrected per-type
convention.

### 5c. Live proof (real in-process server + real ConnectionSession wire)

`tests/…/ShopLauncherMountNumberingLiveTests.cs` reproduces the full journey:
spawn the ship flying with an enemy in range + a command target, open the real
`ShopModel`, buy weapon + afterburner + cloak + shield FIRST then a real missile
launcher (a `MaxLock == 0` type so the lock snaps to full once the mount-0
launcher is actually found — exactly the path the bug severed), send the real
`FM_C_BUY_LOADOUT(4)`, and assert:

- the authoritative server loadout grants the launcher as **`ET_Magazine@0`**
  (mount 0, the fix — previously 4–10) and the other four parts are also mounted;
- the client's `BUY_LOADOUT_ACK` loadout carries it at mount 0 too;
- lock genuinely builds — the owner's real `MISSILE_LOCK_UPDATE` reaches the
  client and `ClientWorld.MissileLock.IsFullLock` becomes true;
- a real `FIRE_MISSILE(70)` is accepted and fires — a homing `Missile` appears
  at the command target and the launcher's ammo decrements;
- buying a SECOND different launcher on a real mounted loadout replaces the
  first (single-slot).

Captured live proof line:

```
P437-SHOP-MOUNT-LIVE: bought weapon+AB+cloak+shield then launcher 5 -> granted ET_Magazine@0
  (fix; was 4-10), lock->full (1.000), FIRE_MISSILE missile 5464 -> target 8651, ammo 2->1;
  second launcher replaced (10@0) single-slot
```

### 5d. Confirmed tools need no change

`tools/allegiance-mcp` (`Catalog.PartsForWeapon` / `ResolveParts` /
launcher append) and `tools/vizfight` (`FullInventoryParts` /
`AppendCompatibleLauncher`) already build per-type loadouts with every non-Weapon
part — including the launcher — hardcoded at mount 0. They never hit the bug and
remain byte-consistent with the corrected shop scheme. No changes made there.

## 6. Test suite (run at least 2×)

Each suite run at least twice. Only the **pre-existing live-wire timing flakes**
appeared — all in networking/live-integration tests in `Core.Tests` /
`ClientCore.Tests`, none referencing `ShopModel` (the changed assembly is
`ClientCore.MonoGame`, which those projects do not reference):

| Project | Run 1 | Run 2 |
|---|---|---|
| `Allegiance.ClientCore.MonoGame.Tests` | 868/868 pass | 868/868 pass |
| `Allegiance.Core.Tests` | 3517 pass, 5 flake | 3521 pass, 1 flake |
| `Allegiance.ClientCore.Tests` | 291 pass, 4 flake | see notes |
| `Allegiance.Lobby.Tests` | 12/12 pass | 12/12 pass |

The flake set varied between runs (e.g. `LiveTurretGunnerBindingTests`,
`BotFlightTests`, `StationsUpdateLiveTests`, `JoinFlowLiveTests`,
`ThreePeerSeededSessionTests`, `ShipDeleteGoneLiveTests`,
`FullSessionKillIntegrationTests`) and individual flaky tests passed on rerun —
the characteristic signature of the documented timing-sensitive live-wire tests,
not a deterministic regression. The changed source (`ShopModel.cs`) is confined
to the `Allegiance.ClientCore.MonoGame` assembly, which the failing suites do not
reference, so the flakes are by construction unrelated to this change.

## 7. Files changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopModel.cs` — per-equipment-type
  `RenumberMounts` + single-slot `TogglePart` + `EquipmentTypeOf` helper (the fix).
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopModelTests.cs` — new P437 unit
  tests; updated two P394 seeding tests to the corrected convention.
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopLauncherMountNumberingLiveTests.cs`
  — the live buy→grant@0→lock→fire→replace proof (new).

Committed on `master` (not pushed — Domingo pushes).
