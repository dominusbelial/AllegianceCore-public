# P143 — SWAP_PART(34) — in-space mounted-part slot swap

Status: implemented, live-loopback-tested, 3× consecutive clean full-suite runs
(2713 tests). Branch `master`, HEAD `b1194ee` (P142) + this change.

Repo: `/home/domingo/Development/AllegianceCore`
Native reference: `/home/domingo/Development/Allegiance/src`

---

## What SWAP_PART is

Native `Inc/Messages.h:273-278`:

```cpp
DEFINE_FEDMSG(CS, SWAP_PART, 34)    //Swap two parts
  ShipID        shipID;
  EquipmentType etOld;
  Mount         mountOld;
  Mount         mountNew;
END_FEDMSG
```

Native handler `FedSrv/FedSrv.CPP:6459-6501` (read in full, quoted in the task):
a flying, unboarded ship moves the mounted part at `(etOld, mountOld)` to
`mountNew`; if a *different* part of the *same* `etOld` type already occupies
`mountNew`, the two exchange slots, otherwise it is a plain move; the original
message (with `shipID` filled in) is then re-forwarded sector-wide via
`GetGroupSectorFlying(pcluster)` (`FedSrv.CPP:6497-6498`). No `LOADOUT_CHANGE`
is queued here — the broadcast **is** the SWAP_PART message itself.

## Implementation (files touched)

| File | What |
|---|---|
| `src/Allegiance.Core/Wire/SwapPart.cs` | new codec (fmid 34) |
| `src/Allegiance.Core/Sim/Ship.cs` | `TrySwapPart` + `RepointMount` |
| `src/Allegiance.Core/Sim/Weapon.cs` | `SetMountId` (keep realized weapon's mount in sync) |
| `src/Allegiance.Server/Server.cs` | dispatch branch + `HandleSwapPart` |
| `src/Allegiance.Bot/Bot.cs` | inbound decode accumulator + `SendSwapPart` helper |
| `src/Allegiance.Core/Wire/FedMessageDumper.cs` | SWAP_PART formatter |
| `tests/Allegiance.Core.Tests/SwapPartTests.cs` | codec + live loopback test |

## Confirmation 1 — swap-vs-move logic

Native (`FedSrv.CPP:6485-6495`):

```cpp
IpartIGC* ppart = ...GetMountedPart(etOld, mountOld);
if (ppart)
{
    IpartIGC* ppartNew = ...GetMountedPart(etOld, mountNew);   // SAME etOld
    if (ppartNew)
    {
        ppart->SetMountID(c_mountNA);
        ppartNew->SetMountID(mountOld);
    }
    ppart->SetMountID(mountNew);
    ...ForwardMessage(...);
}
```

Faithfully ported in `Ship.TrySwapPart` (`Ship.cs`, added after `MountPart`):
- `src` = the part at `(etOld, mountOld)`; if none, return `false` — the
  native's silent no-op (`FedSrv.CPP:6485-6486`: no error, no broadcast, no
  ack). Matches `HandlePromote`'s `promoted is null` silent branch
  (`Server.cs:2798-2802`).
- `dst` = `GetMountedPart(etOld, mountNew)` — the **same** `etOld`, not a
  separate type (`FedSrv.CPP:6488`). Only a part of the same equipment type
  can trade the destination slot.
- If `dst` is a *different* part ⇒ both repointed (`src → mountNew`,
  `dst → mountOld`), the exchange. If not (slot empty, or `mountOld==mountNew`)
  ⇒ plain move (`src → mountNew`), only that part's `MountId` changes.
- The native's temporary `SetMountID(c_mountNA)` hop exists to dodge the
  mount-index collision in its mount-keyed store; this port's `_parts` is a
  plain `List` (`Ship.cs:197`) so there is no collision and both parts are
  repointed directly — net effect identical.
- Because `MountedPart`'s properties are `init`-only (`MountedPart.cs:28-40`),
  `RepointMount` replaces the list entry with a fresh `MountedPart`
  (preserving `PartId/Amount/FractionMounted/Type/FunctionalWeapon`) rather
  than mutating in place, and pushes the new slot into any realized
  `Weapon` via `Weapon.SetMountId` (`Weapon.cs`) so firing/turret-binding read
  the new slot (`Weapon.cs:357-366`). `_parts` is `private`
  (`Ship.cs:197`), so the swap lives as a `Ship` method, not from `Server.cs`.

Return value mirrors the native broadcast condition: `true` whenever a source
part existed (a swap, a plain move, or a same-slot no-op each warrant the
echo), `false` only when the source is empty.

## Confirmation 2 — sector-wide, not side-wide

`HandleSwapPart` (`Server.cs`, placed directly after `HandlePromote`) broadcasts
via `SendSectorWide(ship.Sector!.Id, ...)` — the port's `GetGroupSectorFlying`
analogue (`Server.cs:3215-3221`), which sends to **every** peer whose sector
matches, regardless of side. This is the same route `HandlePromote` (P140) uses
and deliberately *not* the same-side-only relay of HIGHLIGHT_CLUSTER (P142).
The native target is literally `GetGroupSectorFlying(pcluster)`
(`FedSrv.CPP:6498`), the whole sector.

Verified in the live test with 3 bots / 2 teams round-robin (giving a same-side
pair *and* a cross-side enemy in the same starter sector): after the subject
swaps mounts 0↔1, **both** the same-side observer *and* the cross-side enemy
received the SWAP_PART broadcast with matching `shipID/etOld/mountOld/mountNew`
(`SwapPartTests.cs`, phase 1). A side-wide relay would never reach the enemy.
The cross-side enemy being in the subject's sector is asserted explicitly before
the swap.

## Gates (matching HandlePromote's shape, plus the one extra native has)

`HandleSwapPart` reproduces `HandlePromote`'s two-stage structure
(`Server.cs:2777-2811`): `mission is null || mission.Stage != MissionStage.Started`
→ log-and-return (native `FedSrv.CPP:6461`), then lookup; on success the
broadcast. On top of that it adds the unboarded exclusion `ship.ParentShip is
not null` → log-and-return, the port of native's explicit
`TrapHackBoot(GetParentShip() == NULL)` (`FedSrv.CPP:6464`) that `HandlePromote`
itself doesn't need (a boarded gunner can't promote itself, but a boarded
gunner *can* send SWAP_PART). `Ship.ParentShip` is the real property
(`Ship.cs:299`). The no-source-match branch is a silent log-and-return, matching
`HandlePromote`'s own silent branch.

## Confirmation 3 — the three explicit exclusions

1. **DROP_PART(33) not built.** Creating a part-type treasure in space is
   out of scope: this port's `Sim/Treasure.cs` implements only *cash* pickup
   (`TreasureCode.Cash`); part-type treasure creation is the separately-scoped
   follow-up noted in `Treasure.cs`'s remarks and the P123 report. Nothing here
   spawns treasure.
2. **ADD_PART(164) not built.** ADD_PART is sent only from the native
   `FM_C_TREASURE_ACK` handler (`FedSrv.CPP:6113-6132`) — the client's
   mount-targeted "put this picked-up part here" ack. This port's treasure
   pickup is auto-pickup-by-proximity for cash only
   (`PerformTreasurePickup`), with no mount-targeted TREASURE_ACK flow. Not
   wired here.
3. **LOADOUT_CHANGE(96) not added.** Confirmed by reading
   `FedSrv.CPP:6459-6501` in full: the *only* forward from the SWAP_PART
   handler is `ForwardMessage(GetGroupSectorFlying(pcluster), pfmSwapPart,
   FM_GUARANTEED)` at `:6498` — no `QueueLoadoutChange`/`ExportShipLoadout`
   call. The broadcast **is** the SWAP_PART message itself. (LOADOUT_CHANGE is
   a full-loadout resync fired from *other* sites, `fsship.cpp:707-736`.)
   Emitting one here would diverge from native.

Additionally, cargo-bay (negative) mount support is absent from this port's
loadout model (pre-existing P61 simplification: `MountedPart.MountId` is a
non-negative `byte`, `MountedPart.cs:34`); `mountOld`/`mountNew` are treated as
non-negative bytes in the codec, following that established convention. This is
the port's first bare wire `EquipmentType` field — encoded as a plain LE Int16
cast from the `short` enum (`DataPartTypeIGC.cs:9`), matching native
`typedef short EquipmentType` (`Igc/igc.h:530`).

## Wire layout (SwapPart.cs)

```
offset 0: ushort cbmsg = 12
offset 2: ushort fmid  = 34
offset 4: short  shipID     (ShipID)
offset 6: short  etOld      (EquipmentType)
offset 8: char   mountOld   (Mount)
offset 9: char   mountNew   (Mount)
offset 10-11: 2 B align pad (C++ struct align 2 → sizeof 12)
= 12 B fixed, no var items
```

`EquipmentType` is `short` in both native (`igc.h:530`) and this port
(`DataPartTypeIGC.cs:9`); `Mount` is `char` (`igc.h:841`). Verified by the
codec round-trip test (including a hand-built native byte array).

## Verification

- `tests/Allegiance.Core.Tests/SwapPartTests.cs`:
  - `SwapPart_codec_roundtrips_fixed_12_byte_layout` — 12-B layout, fmid 34,
    lossless round-trip, and a hand-built native byte decode.
  - `SwapPart_swaps_two_occupied_slots_sectorwide_moves_and_silent_noop` —
    real 3-bot loopback: seats the subject with two **distinct** real weapons
    at mounts 0/1, then asserts (a) the two parts really exchange slots
    server-side (`Ship.Parts` keyed by `MountId`), (b) the same-side observer
    **and** cross-side enemy both receive the SWAP_PART with matching fields
    (sector-wide), (c) the requester's own echo arrives; then the plain-move
    case (target mount empty → only the moved part changes) and the silent
    no-op case (source mount empty → no broadcast to anyone, server unchanged).
- Full suite: 2713 tests, **3 consecutive clean runs** (final count). The only
  failures observed across the many runs were in the pre-existing documented
  flaky class `LiveCombatWireTests` (kill-over-wire timing) — reran to 3-clean;
  not a regression from this change.

## Out of scope (unchanged)

- P103–P142 code beyond the minimum needed for this handler + codec + bot
  decode (the only adjacent touch is `Weapon.SetMountId`, required so a swapped
  realized weapon keeps firing from its new slot).
- DROP_PART / ADD_PART / LOADOUT_CHANGE / cargo-bay mounts — see above.
