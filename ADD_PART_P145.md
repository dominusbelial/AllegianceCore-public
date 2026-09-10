# ADD_PART (P145) — treasure-ack mount placement (the pickup round-trip)

Branch `master`, on top of `e9dd08c` (P144, DROP_PART). Baseline: 2716 tests;
this task adds 6 (`AddPartTests` + a corrected `TreasureWireTests` name) →
2722 total.

This closes the last Tier-3 "Phase 1 parts-system remainder" item
(`ADD_PART`=164) except `LOADOUT_CHANGE`(96), which is its own separately-scoped
task. `ADD_PART` is only ever sent from the native `FM_C_TREASURE_ACK` handler —
the client's response to being offered a part-type treasure pickup, choosing
which mount to put it in (or declining). Every primitive it needs already
existed in this port (P123's `AcquireTreasure` codec + cash-pickup pass, P144's
`Ship.CreateDropTreasure` + `PartData` + `TreasureSpawn` send), so this task
mostly wires them together.

## What changed

- **`src/Allegiance.Core/Wire/AddPart.cs`** (new) — the `FM_S_ADD_PART(164)`
  codec: `shipID` short + a plain 5-byte `PartData` (`partID`/`amount`/`mountID`,
  `igc.h:1674-1679`) + 1 align pad = fixed 12 B. It is an S message only ever
  sent by the server (unlike the CS-relay `SwapPart`/`DropPart`, there is no
  shipID-fill-in game). `partID` is the part *type* id, `amount` the *granted*
  quantity, `mountID` the destination slot.
- **`src/Allegiance.Core/Wire/TreasureAck.cs`** (corrected) — this is the task's
  mandated pre-existing-inaccuracy fix. P123 built the codec believing the
  native header's inline comment (`Mount mountID; //== c_mountNA`,
  `Messages.h:878`) — that the field is "always `c_mountNA`". The actual handler
  (`FedSrv.CPP:6094-6152`) branches on `pfmTA->mountID != c_mountNA`
  (`FedSrv.CPP:6099`): a real client-supplied mount when accepting, the
  sentinel only when declining. Ground truth is the handler, not the comment.
  So `MountId` is now a real variable field decoded/encoded like any other
  (the `SwapPart`/`DropPart` plain settable-field pattern), and `MountNA` is
  kept only as the named decline-sentinel constant. Documented in the type doc;
  stated here explicitly because it is a real (if minor) fix, not a silent
  workaround.
- **`src/Allegiance.Core/Sim/Ship.cs`**
  - `PendingTreasureOffer` (:339) — the per-player pending offer
    `(PartTypeId, Amount)?`, the port of native's `CFSPlayer` treasure-buffer
    (`SetTreasureData`/`GetTreasureObjectID`/`GetTreasureAmount`,
    `FedSrv.CPP:13608`), hung directly on `Ship` per this port's transient-state
    precedent (`SideId`/`StationId`/`TurretId`/`ParentShip`); `null` = native's
    `NA` sentinel.
  - `SetPartAmount` (:888) — rewrite an existing `MountedPart`'s `Amount`
    (list-entry replacement, the `RepointMount` idiom), the port of
    `ppart->SetAmount(ppart->GetAmount() + amount)` (`FedSrv.CPP:6101-6103`).
  - `CreateDropTreasure(DataPartTypeIGC type, short amount, now, time0)`
    (:1028) — a second overload of the P144 method that builds a part treasure
    from a part *type* + quantity instead of a `MountedPart`; the original
    `CreateDropTreasure(MountedPart, …)` now delegates to it (:1000). Native's
    DROP_PART and TREASURE_ACK-decline call the *same* `CreateTreasure` overload
    with the same `dv=25 / lifespan=120` args, so both use identical geometry —
    no reimplementation, just a shared builder.
- **`src/Allegiance.Core/Model/Sector.cs`** — `ResolveTreasurePickups` (:763)
  gate extended from `cash only` to `Cash or Part` (:790); the doc comment's
  "cash only this slice" block rewritten to the new reality.
- **`src/Allegiance.Server/Server.cs`**
  - `CanMount` helper (:4899) factored out of `MountCombatWeapon`'s inline
    PartMask-subset test (`hullTypeIGC.h:199-213`, `part.PartMask & ~hp == 0`),
    now shared with the ack handler — not duplicated.
  - `PerformTreasurePickup` (:4046) gains the `Part` branch: buffer the offer +
    send `AcquireTreasure` to the picking player's *own* connection only (native
    `SendMessages(pfsPlayer->GetConnection(), …)`, `FedSrv.CPP:13639-13641`) —
    the first real send of the P123 codec — gated on "no offer already pending";
    and **always** still calls `BroadcastTreasureDestroyed` (native
    `KillTreasureEvent` fires regardless of code — see finding below).
  - dispatch branch for `TREASURE_ACK` (:2309) + `HandleTreasureAck` (:3133):
    the port of `FedSrv.CPP:6094-6152` (see "Mount decision" below).
- **`src/Allegiance.Bot/Bot.cs`** — `SendTreasureAck(byte mountId)` helper,
  plus inbound accumulators + decode cases for `AcquireTreasure`(162) and
  `AddPart`(164), so the live test drives and observes the full round-trip.
- **`tests/Allegiance.Core.Tests/AddPartTests.cs`** (new) — 2 wire-layout pins
  + 4 live loopback proofs; **`Wire/TreasureWireTests.cs`** — the P123
  `TreasureAck_RoundTrips_MountNA` test corrected to the real variable-field
  semantics.

## Finding 1 — the treasure is destroyed on touch, before any ack

Confirmed against `Igc/treasureIGC.h:77-102` (`HandleCollision`): on *any*
successful `HitTreasureEvent` return, `KillTreasureEvent` fires — the treasure
is destroyed (+ `DESTROY_TREASURE` broadcast) the instant it's touched, part and
cash alike, *before* the player has chosen a mount. If the player later
declines, a **new** treasure is created at the then-current position; it is not
the same object persisting.

Port: `PerformTreasurePickup`'s `Part` branch ends with
`BroadcastTreasureDestroyed(treasure)` unconditionally (`Server.cs:4079`), even
in the already-pending case where no offer is sent. Verified live: after touch,
`DESTROY_TREASURE` reaches every peer and the treasure is gone from the sector
*before* any ack (AddPartTests merge + pending scenarios).

## Finding 2 — the pending-offer buffer and the already-pending edge case

Native `HitTreasureEvent` only buffers + offers when the player's buffer is
empty: `if ((tc != c_tcPart) || (GetTreasureObjectID() == NA))`
(`FedSrv.CPP:13604-13607`) — otherwise the whole offer block is skipped, but the
function still returns true, so `KillTreasureEvent` still destroys the touched
treasure. It is a real, slightly punishing native edge case, ported faithfully
(not softened): a second part-treasure touched while one offer is pending is
destroyed but produces no second `ACQUIRE_TREASURE`, and the buffered offer is
unchanged.

Port: `PerformTreasurePickup` checks `ship.PendingTreasureOffer is null` before
buffering + sending, and always destroys (`Server.cs:4046-4079`). Verified live
(AddPartTests pending scenario): two part-treasures touched back-to-back →
`DESTROY_TREASURE` ×2, exactly one `ACQUIRE_TREASURE`, offer unchanged.

## Finding 3 — merge-into-existing vs mount-fresh

Native ack accept (`FedSrv.CPP:6101-6106`): resolve the mounted part at
`(GetEquipmentType(ppt), mountID)`; if it's the same part type,
`SetAmount(old + amount)` (merge); else `CreateAndAddPart(ppt, mountID, amount)`
(fresh). The `ADD_PART` broadcast carries the *granted* (buffered) amount, not a
merged total (`FedSrv.CPP:6110-6116`), sector-wide
(`GetGroupSectorFlying(pcluster)`, `FedSrv.CPP:6118-6119`).

Port: `HandleTreasureAck` reuses the exact
`_parts.FirstOrDefault(p => p.EquipmentType == et && p.MountId == mount)`
lookup the P143/P144 methods share; merge → `SetPartAmount`; fresh →
`Ship.MountPart(…, ExpandedPartData.FullyMounted)`; then `SendSectorWide` of
`AddPart{ shipID, PartData(partTypeId, grantedAmount, mount) }`. CanMount is
checked (bad mount = native `TrapHackBoot(!CanMount)`, `FedSrv.CPP:6100`) — the
port analogue logs and returns *without* clearing the offer (native's boot
abandons before the clear). Verified live: merge case (pre-mounted W@0 amount 10
+ picked W×5 → server-side 15, `ADD_PART` amount 5) and fresh case (empty mount →
new part at that mount, `ADD_PART` amount 7).

## Finding 4 — decline re-creates the treasure at the current position

Native ack decline (`FedSrv.CPP:6125-6129`): `CreateTreasure(g.timeNow, ship,
amount, ppt, GetPosition(), 25.0f, 120.0f)` — a *new* treasure (the original was
already destroyed on touch) at the ship's *current* position, same
`dv=25 / lifespan=120` args as DROP_PART.

Port: the decline branch calls
`ship.CreateDropTreasure(partType, offer.Amount, _simNow, (uint)TickIndex)`
(`Ship.cs:1028`) and `SendSectorWide(TreasureSpawn)` — the P144 builder reused,
not reimplemented (geometry: `P0 = pos + (0.5·hull.Length+10)·randDir`, `V0 =
randDir·25 + vel`, so the new treasure lands "roughly at the current position").
Either way the offer is cleared (`FedSrv.CPP:6148`). Verified live: after moving
the ship away from the touch point and declining, a new `TreasureSpawn` appears
whose `P0` is near the *moved* position and far from the original touch point,
with a new object id (`Code=Part`, `TreasureId=partId`, `Amount=buffered`).

## Gates

The ack handler gates on mission started + a non-null `PendingTreasureOffer`
(the task's enumerated gates, mirroring native's `GetTreasureObjectID() != NA`
/ stage checks). The native's opening
`TrapHackBoot(GetParentShip() == NULL)` (`FedSrv.CPP:6099`) is **deliberately
not ported**: it would boot every ordinary unboarded pilot sending an ack, which
is exactly the primary working scenario — it reads as a native quirk
inconsistent with the feature. Stated in `HandleTreasureAck`'s remarks, not
silently dropped.

## Out of scope (untouched)

`LOADOUT_CHANGE`(96), the cash-pickup path beyond the shared gate extension,
building a new part-type lookup (reused `Mission.Core.PartTypes`), and
non-player treasure pickup (already gated out by the existing
`HasCapability(HabmRescue)` caller's `pilotType >= c_ptPlayer`).

## Verification

- 6 new tests: `AddPartTests` (codec pin 12 B / fmid 164; `TreasureAck` mount
  field real; live merge, fresh, decline, pending-edge) + the corrected
  `TreasureWireTests.TreasureAck_RoundTrips_MountId`.
- Full suite **2722 total, all green** on **3 consecutive clean runs** (no
  flake reruns needed this round). Every command used
  `/home/domingo/.dotnet/dotnet`; no VM, no Obsidian, no `sudo`.
