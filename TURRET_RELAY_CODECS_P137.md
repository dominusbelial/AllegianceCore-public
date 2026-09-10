# TURRET_RELAY_CODECS_P137 — typed turret wire codecs for LIGHT/HEAVY_SHIPS_UPDATE

Status: landed. Foundation-only — no broadcast/sim-loop wiring (that is P139 per
P136's breakdown). Types the two turret sub-blobs that P136 carried as opaque
`byte[]` passthrough.

## What changed

- Added `ServerActiveTurretUpdate` — the 16-byte S→C turret-relay record the
  heavy message's `rgActiveTurretUpdates` blob is made of.
- `HeavyShipsUpdate.ActiveTurretBlob` (`byte[]`) → `ActiveTurretUpdates`
  (`ServerActiveTurretUpdate[]`).
- `LightShipsUpdate.InactiveTurretBlob` (`byte[]`) → `InactiveTurretShipIds`
  (`short[]` = `ShipID[]`).
- Three new predicate round-trip tests + the two P136 tests upgraded to the
  typed shapes (`LightHeavyShipsUpdateCodecTests.cs`).

Files touched (all under `AllegianceCore/`):

| File | Change |
|---|---|
| `src/Allegiance.Core/Wire/HeavyShipsUpdate.cs` | new `ServerActiveTurretUpdate` type; message now uses typed `ActiveTurretUpdates[]`; `FromShips` gained an optional turret list |
| `src/Allegiance.Core/Wire/LightShipsUpdate.cs` | `InactiveTurretShipIds` (short[]) replaces the opaque blob; `FromShips` gained an optional ShipID list |
| `tests/Allegiance.Core.Tests/LightHeavyShipsUpdateCodecTests.cs` | typed round-trips + exact-layout tests |

## Real field layouts (confirmed from source, not trusted from P136's paraphrase)

### `ServerActiveTurretUpdate` — 16 bytes — `Igc/igc.h:2478-2485`

```cpp
class ServerActiveTurretUpdate                                      //16 bytes
{
    public:
        ShipID              shipID;                     //2
        CompactTime         time;                       //2
        CompactControlData  controls;                   //4
        CompactOrientation  orientation;                //8
};
```
The block is pack(1) (`#pragma pack(pop)` at igc.h:2486), so it is exactly
2+2+4+8 = 16 bytes, no padding. Offsets: 0 shipID, 2 CompactTime (short delta
from the message's `timeReference`), 4..7 controls, 8..15 orientation.

**Distinct from P109.** The port's P109 relay-input codec,
`ClientActiveTurretUpdate` (`Igc/igc.h:2470-2476`), is a *different* 16-byte
shape — a full 4-byte absolute `Time`, no shipID:

```cpp
class ClientActiveTurretUpdate                                      //12 bytes (comment; actual 4+4+8=16)
{
    public:
        Time                time;                       //4
        CompactControlData  controls;                   //4
        CompactOrientation  orientation;                //8
};
```
`ServerActiveTurretUpdate` is the **server→observer relay** of *other* ships'
turret states, hence the leading `ShipID` and the compacted 2-byte time. It is
not P109's message reused — it shares only field-shape similarity, exactly as
P136's MD §4/§6 found.

**Producer** (`FedSrv/FedSrv.CPP:4049-4071`, in `SendShipUpdates`): the server
walks the ships with a recent turret update, skips the recipient itself
(`pfsShip != pfsPlayer`), copies `controls`/`orientation` from the turret
ship's stored P109 `ClientActiveTurretUpdate`
(`GetPlayer()->GetActiveTurretUpdate()`), sets `time` as a delta from the
message's `timeReference` (`g.timeNow`), and stamps `shipID`. The blob's size
is `CB_ATU_VARDATA(n) = n * sizeof(ServerActiveTurretUpdate)`
(FedSrv.CPP:3982).

### Light message's inactive-turret list — `ShipID[]`, no per-turret state

P136's description ("just a list of ship IDs with inactive turrets") is
**confirmed**. The native light message packs only ShipIDs:

- Size macro `CB_ITU_VARDATA(elems) = elems * sizeof(ShipID)`
  (FedSrv.CPP:3984) — note it is `sizeof(ShipID)`, *not*
  `sizeof(SomeStruct)`, confirming a plain id list.
- Fill site `*(pInactive++) = rgInactiveTurrets[i]->GetShipID();`
  (FedSrv.CPP:4017-4019) — each entry is exactly one `ShipID`.

So the typed `short[]` (ShipID is a 2-byte signed short) is the faithful shape.
No per-turret state travels in the light message.

## Port conventions reused

- `ServerActiveTurretUpdate` follows the exact canonical-bytes pattern of
  `ClientActiveTurretUpdate` / `HeavyShipUpdateEntry`: a sealed class storing
  the 16 wire bytes, with `ShipId`/`TimeOffset`/`Time(ref)`/`Controls`/
  `Orientation` accessors all re-derived from the bytes, so
  `DecodeBody(EncodeBody(x))` is a fixed point.
- `TimeOffset` is `timeReference − time` (mirrors `CompactTime::Set`,
  igc.h:2184-2190, via `CompactQuant.EncodeTimeOffset`, Quant.cs:116-117);
  `Time(ref)` recovers the absolute via `CompactQuant.DecodeTimeOffset`
  (Quant.cs:121-122) — the same delta machinery `HeavyShipUpdateEntry` uses.
- Packing/unpacking of both lists sits inside the existing ib/cb
  `VarItem` regions; element counts are implicit in `cb` over the fixed
  element width (16 for the active record, 2 for ShipID), and the decoders
  validate divisibility.

## Verification

- Pure unit tests, no live Bot/server, no VM (matches P136's own predicate
  style).
- Focused: `LightHeavyShipsUpdateCodecTests` — 5/5 pass, including an exact
  16-byte layout check against igc.h:2478-2485 (shipID @0, CompactTime @2,
  controls @4, orientation @8) and the delta recovering the source absolute
  through the message's `timeReference`.
- Full suite: **3 consecutive clean runs**, 2678 passed / 0 failed / 0 skipped
  each (baseline 2675 + 3 new tests). No
  `LiveChatSectorWireTests`/`LiveCombatWireTests`/`LiveInSpaceEjectTests`
  flakes fired on any of the three runs.
- Out of scope honored: P138/P139/P140/P141 untouched; no change to P103–P136
  code beyond typing these two blob fields.

## Next task (per P136's own breakdown, §7.2)

**P138 — visibility source for ships.** Extend the P132 cross-side detection
sweep so foreign ships become per-side visibility targets (native sends *all*
ships in a cluster, FedSrv.CPP:3680-3690; decide strict-side vs cluster-wide).
Establishes `Ship.SeenBySide` — the input the P139 broadcast wiring will sweep.
Follows P137 because the relay codecs it populates are now typed and ready.
