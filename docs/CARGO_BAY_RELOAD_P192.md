# CARGO_BAY_RELOAD_P192

## Summary

P192 ports native Allegiance's cargo-bay system into the C# port as a
**separate** `Ship.CargoHold` list plus the `FM_CS_RELOAD(129)` wire codec,
handler, and broadcast. Per Domingo's 2026-09-13 decision, `MountedPart.MountId`
is **not** widened; translation to/from native's negative-mount wire convention
happens only at the `SWAP_PART`/`RELOAD` codec boundary.

Lands **one wire message** over the P191 baseline (115 → **116/136**):
`RELOAD(129)`. `SWAP_PART(34)` was already ported (P143) and is **extended**,
not newly landed — the P59-era swap handler now reaches the cargo hold on
negative mounts, which is native's real mechanism for filling/emptying cargo.

Baseline: 2950 (P191) → **2957** tests (+7).

## Files changed

| File | Change |
|---|---|
| `src/Allegiance.Core/Wire/Reload.cs` | **NEW** — `Reload`(129) codec (`FM_VAR_ITEM(rgReloads)` of `{short amountTransfered; signed-char mount; pad}` + `shipID`) and `ReloadData` (4 B/entry). |
| `src/Allegiance.Core/Wire/SwapPart.cs` | `MountOld`/`MountNew` widened `byte` → `sbyte` so a negative wire mount can address cargo (sign-extended on decode, mirroring `PromoteRequest`). |
| `src/Allegiance.Core/Sim/Ship.cs` | `CargoHold` (capacity-5 array), `CargoSlotForMount`, cargo-aware `TrySwapPartWithCargo`, RELOAD entry application `TryApplyReloadEntry`, mount-fraction reset helpers, `ClearLoadout` empties cargo. |
| `src/Allegiance.Server/Server.cs` | RELOAD(129) dispatch branch + `HandleReload`; `HandleSwapPart` now dispatches via `TrySwapPartWithCargo`. |
| `src/Allegiance.Bot/Bot.cs` | `ReloadBroadcasts` accumulator + `SendReload`; `SendSwapPart` takes `sbyte`. |
| `tests/.../CargoBayReloadTests.cs` | **NEW** — 7 tests. |
| `tests/.../SwapPartTests.cs` | assertions `byte` → `sbyte`. |

## Cargo-slot capacity & negative-index mapping decision

Native stores a ship's cargo hold as `IpartIGC* m_mountedCargos[c_maxCargo]`
(`Igc/shipIGC.h:2567`), addressed by **negative** `Mount` values (`Mount` is a
signed `char`, `Igc/igc.h:841`). The port models it as
`private readonly MountedPart?[] _cargo = new MountedPart?[CargoCapacity]` on
`Ship`, a list separate from `_parts`.

- **Capacity = 5.** This session **located the real value**: `c_maxCargo = 5`
  at `Igc/igc.h:845` (immediately followed by `c_mountNA = -c_maxCargo - 1 =
  -6`, `Igc/igc.h:846`). The task prompt had asserted the value was "not found
  anywhere in this checkout"; a full-tree grep disproved that (`igc.h:845` is
  in the `Igc/` subtree). `Ship.CargoCapacity = 5` matches it exactly — no
  guess was needed. This also confirms native's own "fuel, ammo, missile,
  chaff, dispenser" 5-category comment (`FedSrv.CPP:6523`).
- **Index mapping:** wire mount `m ∈ [-5,-1]` → slot `CargoSlotForMount(m) =
  -m - 1` (`-1`→0 … `-5`→4). This is native's own `&m_mountedCargos[mountID +
  c_maxCargo]` (`Igc/shipIGC.cpp:1524`), verified against `CshipIGC::PartLocation`
  (`shipIGC.cpp:1516-1549`), which routes negative mounts to the cargo array
  (and ignores the `EquipmentType`, exactly as the RELOAD handler's
  `GetMountedPart(NA, mount)` does).
- A cargo-held part's `MountedPart.MountId` is a placeholder `0` — the slot is
  implicit in the array position, documenting that this port's unsigned
  `MountId` cannot hold native's negative cargo mount.

## Extended `SWAP_PART` dispatch (cargo prerequisite)

Native `SWAP_PART` (`FedSrv.CPP:6459-6501`) already supports negative
(cargo) mounts via `GetMountedPart`/`SetMountID`'s negative routing. This port's
P59-era `TrySwapPart` only scanned `_parts` with `byte` mounts, so it could not
fill/empty cargo — and `RELOAD` would have had nothing to operate on.

`Ship.TrySwapPartWithCargo(EquipmentType etOld, int mountOld, int mountNew)`
generalizes `TrySwapPart`'s same-equipment-type/slot-trade semantics across both
stores: `ResolvePart` routes a non-negative mount to `_parts` and a negative
mount to `_cargo`; a source part is detached from its current home and placed in
the destination (a negative destination stores a placeholder-`MountId`-0 copy in
`_cargo`; a non-negative destination re-equips via `MountPart`, realizing a
weapon subtype). All four sign combinations work, so an equipped part moved into
cargo round-trips back to an equipped slot. `Server.HandleSwapPart` now calls
this instead of `TrySwapPart`, so the existing sector-wide `SWAP_PART` relay
carries negative mounts naturally. No out-of-scope change to `_parts`/
`MountedPart.MountId`'s type.

## `RELOAD` codec (129)

```
DEFINE_FEDMSG(CS, RELOAD, 129)
    FM_VAR_ITEM(rgReloads);
    ShipID  shipID;
END_FEDMSG
```
Layout (natural alignment): `header(4) + ibReloads(2) + cbReloads(2) +
shipID(2)` = 10 fixed, then `nReloads × 4`-byte `ReloadData{short
amountTransfered; signed-char mount; pad}`. `cbReloads = 4·nReloads`;
`nReloads ∈ [1,5]` is the handler's gate (`FedSrv.CPP:6523`). The single codec
serves both directions (client sends entries with shipID ignored; the server
echoes the same bytes with shipID filled, like `SwapPart`).

## `RELOAD` handler

`Server.HandleReload` (port of `FedSrv.CPP:6504-6653`):

- **Gates:** mission `Started` + unboarded (`ParentShip == null`,
  `FedSrv.CPP:6506,6509`), a real sector **and** non-empty loadout
  (`GetParts()->n() != 0`, the NYI lifepod hack, `FedSrv.CPP:6516`).
- **Per-entry application** via `Ship.TryApplyReloadEntry(mount,
  amountTransfered)`, in wire order:
  - **Ammo pack** (`PackType == 0`, `c_packAmmo`, `Igc/igc.h:549`):
    `amountTransfered == -1` (NA) terminates the pack; otherwise a partial drain
    is validated (`< 0` or `>= amount` is malformed) and subtracted. The ship's
    `Ammo` pool gains the transferred amount and every mounted ammo-consuming
    weapon's mounted-fraction is reset to 0 (`FedSrv.CPP:6570-6582`).
  - **Fuel pack** (`PackType == 1`, `c_packFuel`, `Igc/igc.h:550`): native calls
    `SetFuel`, which this port has never implemented (documented no-op,
    `Ship.cs`); the amount is ignored but the equipped afterburner's
    mounted-fraction is still reset (`FedSrv.CPP:6583-6591`).
  - **Launcher** (chaff in this port; magazine/dispenser enumerated but have no
    decodable corpus record): `amountTransfered == -1` terminates the equipped
    same-type launcher (refused if it still holds ammo) and mounts the cargo
    launcher at slot 0; otherwise the transferred amount is validated and moved
    into the equipped launcher, whose mounted-fraction is reset
    (`FedSrv.CPP:6593-6640`). Native's `ResetTimeLoaded()` is **N/A** — this
    port tracks no per-launcher "time since last reload" field.
- **Success:** the whole message is re-broadcast sector-wide with `shipID`
  filled in (`SendSectorWide`, native `GetGroupSectorFlying(pcluster)`,
  `FedSrv.CPP:6652`).

## Malformed-entry response decision (divergence)

Native sets `bBoot = true; break;` on any malformed entry, then hard-disconnects
the connection with `TrapHackBoot(!bBoot)` (`FedSrv.CPP:6531-6648`). This port
**softens** that to a log-and-stop — the remaining entries in the batch are
refused and **no broadcast is sent**, but the connection stays up. This follows
this port's established TrapHackBoot convention for C→S messages
(log-and-refuse, never a server-initiated disconnect — cf. `HandleTreasureAck`/
`HandleFireExpendable`, P182/P184/P186).

Crucially, the **no-rollback-of-earlier-entries** behavior is replicated
faithfully: entries earlier in the same batch that already applied their
transfer keep their effect on a later malformed entry. This is a genuine native
quirk (the `break` leaves them applied), not a hack-detection edge case, so it
is preserved — the malformed-entry test proves it.

## Findings / corrections to the prompt's assumptions

1. **`c_maxCargo` WAS found** — `= 5` at `Igc/igc.h:845` (the prompt asserted it
   wasn't in the checkout). Used directly; no default-guess needed. Bonus: this
   also pinned `c_mountNA = -6`.
2. **The prompt's "no fuel pool" premise held** — confirmed `Ship` has only
   `Ammo`/`Energy` (`Ship.cs:101`), `DataHullTypeIGC.MaxFuel` is static-core
   data, and the existing `Ship.cs` remarks already document the fuel no-op. The
   fuel branch decodes/accepts and resets the afterburner fraction but never
   touches a fuel pool.
3. **The port has no `SetMountedFraction`-style reload-delay state machine** in
   the sim layer — `Weapon` explicitly does not model the `mountedFraction`
   ramp (`weaponIGC.h:30-49` is "not modelled"). The mounted-fraction this port
   does carry is the wire `MountedPart.FractionMounted` byte, so the "reload-
   delay reset" is implemented as zeroing that byte on the target parts (a real,
   observer-visible wire change), not a duplicated new state machine.
4. **Launcher reachability** — only `ChaffLauncher` is constructible (the
   construction-only mount case established in P182); `Magazine`/`Dispenser`
   have no decodable corpus record. `IsLauncherType` enumerates all three for
   native fidelity but only the chaff case is reachable.

## Tests (`tests/Allegiance.Core.Tests/CargoBayReloadTests.cs`, +7)

1. `Reload_codec_roundtrips_fixed_10_plus_4n_layout` — pins the 10 + 4·n layout,
   fmid 129, negative mounts, NA (-1) amount; byte-for-byte native decode.
2. `SwapPart_negative_mount_roundtrips_equipped_part_into_and_out_of_cargo` —
   ⭐ the real round-trip proof (equipped → cargo via negative `mountNew` →
   back via negative `mountOld`).
3. `Reload_transfers_ammo_updates_ship_ammo_resets_weapon_fraction_and_broadcasts` —
   ⭐ must-fail-if-removed: live `Ship.Ammo` bump, ammo-weapon mounted-fraction
   reset, sector relay observed.
4. `Reload_NA_terminates_the_cargo_pack` — NA removes the pack (and, per native,
   transfers the whole amount).
5. `Reload_swaps_cargo_launcher_ammo_into_equipped_partially_loaded_launcher` —
   ⭐ must-fail-if-removed: launcher `Amount` swap + fraction reset.
6. `Reload_malformed_entry_refuses_rest_without_rollback_or_broadcast` — ⭐ the
   no-rollback quirk: entry 1's transfer persists, entry 2 (over-transfer)
   refused, no broadcast.
7. `Reload_fuel_pack_is_accepted_with_no_observable_effect` — accepted/relayed,
   drained, but no ship-pool change (fuel no-op).

Each test respects the native NYI `GetParts()->n() != 0` gate by keeping at
least one equipped part (moving *everything* to cargo skips RELOAD, matching
`FedSrv.CPP:6516`).

**Must-fail-if-removed** verified live: neutering `TryApplyReloadEntry`
(`return false`) turned tests 3 & 5 red; rejecting negative mounts in
`TrySwapPartWithCargo` turned tests 2 & 3 red. Both reverted clean.

## Verification

- Build clean (0 warnings / 0 errors), full solution.
- 3+ consecutive full-suite runs, all green in aggregate with only documented
  flakes interrupting:
  - Run 1: 2956/2957, `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`
    (documented flake) — green in isolation.
  - Run 2: **2957/2957 clean.**
  - Run 3: 2956/2957, `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
    (documented flake) — green in isolation.
  - Run 4: **2957/2957 clean.**
- Count 2957 = 2950 (P191) + 7 new.
