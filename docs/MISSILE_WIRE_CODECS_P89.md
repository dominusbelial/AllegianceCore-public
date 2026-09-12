# MISSILE_WIRE_CODECS_P89

Phase 2, slice 3 — **wire layer only**. Adds the three wire codecs a later
slice needs to broadcast missiles, with **no sim integration**: no `Missile`
sim object, no `Ship.FireMissile` trigger, no broadcast wiring into
`Server.cs`/`ServerHost.cs`/`IIgcSite`. This task produces encode/decode/
round-trip codecs only; running inside a real tick is out of scope by design.

## What was added

| File | Purpose |
|---|---|
| `src/Allegiance.Core/Wire/MissileLaunchData.cs` | Per-missile var-array entry, `readonly record struct` with `Write`/`Read` + `Size` (mirrors `PartData.cs`). |
| `src/Allegiance.Core/Wire/FireMissile.cs` | `FM_CS_FIRE_MISSILE` (70) codec, var-item-first message with fixed tail + `MissileLaunchData[]` array tail (follows `BuyLoadoutAck.cs`). |
| `src/Allegiance.Core/Wire/MissileDestroyed.cs` | `FM_S_MISSILE_DESTROYED` (83) simple fixed-size codec (follows `KillShip.cs`). |
| `tests/Allegiance.Core.Tests/Wire/MissileWireCodecsWireTests.cs` | 14 pure round-trip + byte-level unit tests (no sockets/server). |

Net +14 tests → baseline went 2310 → **2324**.

## How the `MissileLaunchData` trailing-padding question was resolved (and why)

Logical content is 38 bytes: three `Vector`s (3 × 12 = 36) + one
`MissileID` `short` (2). Two facts settle the width:

1. **It is only ever carried as array elements.** `MissileLaunchData` is
   solely the element type of `FIRE_MISSILE`'s var-item `missileLaunchData[]`.
   Every consumer derives the element count by dividing the blob `cb` by
   `sizeof(MissileLaunchData)` — server `FedSrv.CPP:5447`, client
   `appmsg.cpp:1611`. So the per-element stride is what matters, and an array
   element must be padded to the struct's alignment, not just to its own
   logical length.
2. **MSVC default alignment rounds a struct's size up to its largest
   member's alignment.** The widest/alignment-driving member is the
   `Vector` (4-byte floats). So `sizeof(MissileLaunchData)` = 38 rounded up
   to a multiple of 4 = **40 bytes**, with 2 trailing pad bytes after
   `missileID`.

This is the same trailing-pad-to-struct-alignment model already documented
for `DataStationIGC` in `DataStationIGC.cs:13-20` ("the only wasted byte is
the trailing pad at 87 rounding the struct to 88") and for
`DataMissileTypeIGC` in `DataMissileTypeIGC.cs:14-23` (each explicit pad at a
narrow/wide transition). It is additionally consistent with how the sibling
`FIRE_MISSILE` fixed region itself rounds up to the 4-byte struct alignment
(`sizeof(FMD_CS_FIRE_MISSILE)` = 32, which `BEGIN_PFM_CREATE` passes as
`cbfm` — `messagecore.h:724` — and blobs start at that offset —
`Messages.cpp:404`).

Where it is settled in code: `src/Allegiance.Core/Wire/MissileLaunchData.cs`
(the type doc's trailing-padding note; `Size = 40`).

## Other layout facts pinned along the way (file:line)

- `struct MissileLaunchData` — `src/Inc/Messages.h:539-544`.
- `DEFINE_FEDMSG(CS, FIRE_MISSILE, 70)` — `src/Inc/Messages.h:547-557`.
- `DEFINE_FEDMSG(S, MISSILE_DESTROYED, 83)` — `src/Inc/Messages.h:597-601`.
- All ID fields are `short` (`ObjectID` = `short`): `src/Igc/igc.h:477`;
  `ShipID` `:480`; `SectorID` `:487`; `MissileID` `:493`; `ExpendableTypeID`
  `:497`.
- `ObjectType` is the `[v1_enum]` 4-byte `AGCObjectType` int32 —
  `src/Inc/AGCIDL.idl:303`, `AGC_Ship = 0` `:308` — same width
  `ExportShip.cs:115` writes.
- Var items MUST precede fixed fields (`messagecore.h:779-781`) and blobs
  are appended at `pbFM + cbfm` (`_Utility/Messages.cpp:404`).
- `Time` → `uint`, this project's established wire convention
  (`ProjectileInstance.cs:17`, `LogonRequest.cs:46`, `ShipUpdate.cs:31`).

### Computed sizes

- `MissileLaunchData.Size` = **40** (36 fields + 2 trailing pad).
- `FireMissile.FixedSize` = **32** (4 hdr + 4 var ib/cb + 4 lock + 4
  timeFired + 6 three shorts + 2 pad + 4 targetType + 2 targetID + 1 bDud +
  1 trailing pad). Whole message = `32 + 40·n`.
- `MissileDestroyed.FixedSize` = **20** (4 hdr + 12 Vector + 2 clusterID + 2
  missileID; no alignment gaps).

## Verification

Baseline re-confirmed before adding tests: `dotnet test` → 2310 passed
(pre-P89). With the new 14 tests the suite is 2324. Final gate was **3
consecutive clean full-suite runs** (see commit below).

## Out of scope (deferred, untouched here)

- Minimal **non-homing `Missile` sim object** + `Ship.FireMissile`-style
  trigger + same-side broadcast wiring (mirroring P88's `Projectile`/
  `Weapon.FireWeapon`/broadcast idiom) — the **next task in this arc**.
- Homing/tracking physics, target-lock behavior, chaff/spoofing.
- `FIRE_EXPENDABLE`(82/87), `MISSILE_SPOOFED`/`END_SPOOFING`(110/111),
  `CREATE_CHAFF`(109).
- `MINE_DESTROYED`(84), `PROBE_DESTROYED`(85), `ASTEROID_DESTROYED`(86),
  `PROBES_UPDATE`(92).
