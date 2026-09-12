# P93 — Mine wire codecs (Phase 2, slice 6, wire layer only)

**Branch:** `master` · **Parent:** `9d3a99f` (P91) · **Scope:** wire layer only —
no sim integration. Produces the `EXPORT` (26)/`DESTROYED` (84) mine pair,
mirroring P89's precedent exactly (codecs → proof → later sim object).

---

## Why this is wire-only, and why no dedicated instance message

Mines are the next Phase 2 object family after missiles (P89-P91). Unlike
missiles, mines need no dedicated "instance" fmid the way
`PROJECTILE_INSTANCE`(12)/`FIRE_MISSILE`(70) did: the reference server creates
a mine through the **same generic** `CreateObject(..., OT_mine, &obj,
sizeof(obj))` path every other exported object uses (`FedSrv.CPP:10072`), and
a native mine exports via `CmineIGC::Export` writing `sizeof(DataMineExport)`
(`src/Igc/mineigc.cpp:158-181`). So this task is a new `ExportMine` codec
sharing fmid 26 — not a new fmid, not a var-item-array message like
`FireMissile`'s. The EXPORT var-item is the envelope's own mechanism, not
evidence the blob varies in length: `DataMineExport` is fixed at 32 bytes.

`AGC_Mine` = `OT_mine` = 3 (`src/Igc/igc.h:122`).

---

## Deriving `DataMineExport` byte offsets (field-by-field)

Source: `DataMineExport : public DataMineBase` (`src/Igc/igc.h:2549-2572`;
same declaration verbatim in a doc block at `FedSrv.CPP:10011-10034`).
ID typedefs are `ObjectID = short` (`igc.h:477,478-500`); `Time` → `uint`;
`Vector` → three floats; `BytePercentage` → one byte; both `bool`s →
standalone 1-byte fields (none declared `bool :1`).

| field      | type               | wire | offset | bytes |
|------------|--------------------|------|--------|-------|
| `p0`       | `Vector`           | 3×f32| 0-11   | 12    |
| `time0`    | `Time`             | u32  | 12-15  | 4     |
| `mineID`   | `MineID` (`short`) | i16  | 16-17  | 2     |
| `exportF`  | `bool`             | b    | 18     | 1     |
| *(pad)*    |                    |      | **19** | 1     |
| `clusterID`| `SectorID` (`short`)| i16 | 20-21  | 2     |
| `minetypeID`| `ExpendableTypeID`| i16  | 22-23  | 2     |
| `launcherID`| `ShipID`          | i16  | 24-25  | 2     |
| `sideID`   | `SideID`           | i16  | 26-27  | 2     |
| `fraction` | `BytePercentage`   | b    | 28     | 1     |
| `createNow`| `bool`             | b    | 29     | 1     |
| *(pad×2)*  |                    |      | 30-31  | 2     |

**Logical content: 30 bytes.** **Padding reasoning:**

1. **Narrow-field pad at 19.** `DataMineBase` runs `p0`(12) + `time0`(4) +
   `mineID`(2) + `exportF`(1) = 19 bytes, ending odd at offset 18. The first
   derived field `clusterID` is a 2-aligned `short` — an odd offset 19 can't
   hold it — so one pad byte lands at 19, pushing `clusterID` to 20. This is
   the same narrow-run rule `DataStationIGC.cs` models for this codebase.
   (Equivalently: the `DataMineBase` subobject is 19 content bytes rounded by
   its own 4-alignment to `sizeof` 20, so derived members start at 20 either
   way.)
2. **Trailing pad to 32.** `fraction`(28) + `createNow`(29) make 30 content
   bytes; the struct's largest member alignment is 4 (`Vector`/`Time`), so
   `sizeof(DataMineExport)` rounds 30 up to **32** with 2 trailing pad bytes.

Both conclusions are confirmed empirically by a throwaway
`g++` `offsetof`/`sizeof` probe of the exact struct:
`p0=0 time0=12 mineID=16 exportF=18 clusterID=20 minetypeID=22 launcherID=24
sideID=26 fraction=28 createNow=29` and `sizeof(DataMineExport)=32`,
`alignof=4`. (Matches MSVC default alignment — all members ≤ 4-byte align.)

**Per-field type conventions reused (not reinvented):**
- IDs → `short` (established throughout the port; P89 cited `igc.h:477-497`).
- `Time` → `uint` (`ProjectileInstance.cs:16-18`).
- `Vector` → shared 12-byte three-float LE helpers (`MissileLaunchData.cs:80-90`).
- `BytePercentage` → `CompactQuant.Percent/DePercent` (div-240,
  `Utility.h:1312`), the same convention `ServerShipUpdate.cs:73-87` uses for
  hull/shield/ammo fractions.

**Resulting wire sizes:** `DataMineExport.Size = 32`; `ExportMine.FixedSize =
4 hdr + 4 varitem + 4 objecttype + 32 blob = 44`; `MineDestroyed.FixedSize =
4 hdr + 2 clusterID + 2 mineID = 8`.

---

## Out of scope (this slice deliberately does NOT do these)

- `Ship`/`Sim` integration: no `Mine` sim object, no `DeployMine`, no broadcast
  wiring into `Server.cs`/`ServerHost.cs`/`IIgcSite`.
- Proximity detonation, blast/damage, mine destructibility (the `fraction`
  field implies a mine can be damaged/destroyed by weapon fire — real sim
  behavior for a later task mirroring P91's shape with a proximity trigger).
- `FIRE_EXPENDABLE` (82/87) — the shared client-fire/broadcast-something-was-
  fired notification covering mines/chaff/expendables (`Messages.h:593-595,
  619-622`); separate shared-across-expendable-types message, not this pair.
- `PROBE_DESTROYED`(85), `ASTEROID_DESTROYED`(86), `PROBES_UPDATE`(92),
  `CREATE_CHAFF`(109) — separate object families/items.

---

## Follow-up (next in this arc, mirroring P90)

**A minimal stationary/slow-drift `Mine` sim object + deploy trigger +
same-side broadcast** — the sim-layer companion to this wire proof:
`Ship.DeployMine` creates a `Mine` at the ship's position and broadcasts
`ExportMine` (same-side list population, like stations), and a destruction
path raises `MineDestroyed`. Proximity-detonation and the `fraction` damage
mechanics land in the slice after that (mirroring P91's collision/damage
shape but triggered by proximity instead of a swept collision).

## Verification

- No VM. Pure round-trip + byte-level unit tests
  (`tests/Allegiance.Core.Tests/Wire/MineWireCodecsWireTests.cs`, same style
  as `ExportStationWireTests.cs` / `MissileWireCodecsWireTests.cs`):
  `DataMineExport` round-trips every field + byte-exact 32-byte emission +
  wrong-size rejection; `ExportMine` round-trips + 44-byte exact emission +
  wrong-fmid/objecttype/blob-size rejection; `MineDestroyed` round-trips its
  two fields + 8-byte size/exact emission + rejection.
- Build + full suite **green × 3 consecutive**: `Passed: 2344, Failed: 0`
  each of 3 runs (baseline entering was 2343 passing / 2344 with the new
  suite; all 10 new tests pass).
- Session stats logged via
  `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.
