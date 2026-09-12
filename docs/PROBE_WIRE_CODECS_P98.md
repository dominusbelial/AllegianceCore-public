# PROBE_WIRE_CODECS_P98 — wire layer only

Date: 2026-09-07. Branch: `master` at `57c3af4` (P97) + this slice.

P98 adds the probe half of Phase 2's object-family arc (mines P93-P96, then
probes P98 → later slices). **Wire-only**: `DataProbeExport` (the fixed
`OT_probe` blob inside the generic `FM_S_EXPORT` envelope), the `ExportProbe`
wrapper, and the `FM_S_PROBE_DESTROYED` codec. No sim object, no deploy
method, no broadcast wiring — that is the explicit follow-up below.

## Source of truth

The struct is **declared** in the checkout (contrary to the brief's premise)
at `src/Igc/igc.h:2598-2623`:

```
struct DataProbeBase {
    Vector              p0;
    Time                time0;
    ProbeID             probeID;
    bool                exportF;
};                                            // igc.h:2598-2604
struct DataProbeExport : DataProbeBase {
    ExpendableTypeID    probetypeID;
    SideID              sideID;
    SectorID            clusterID;
    ShipID              shipID;
    ObjectType          otTarget;             // NA when no target
    ObjectID            oidTarget;            // NA when no target
    bool                createNow;
};                                            // igc.h:2614-2623
```

Corroborated by `CprobeIGC::Export` (`src/Igc/probeigc.cpp:535-568`) which
assigns every field and returns `sizeof(DataProbeExport)`
(`probeigc.cpp:567`), and by `Initialize` (`probeigc.cpp:60`) asserting
`dataSize == sizeof(DataProbeExport)` on the export path.

**Two corrections to the brief's stated struct, both confirmed by the sources:**

1. **Field order.** `igc.h:2614-2623` declares `probetypeID, sideID,
   clusterID, shipID` (in that order), not the brief's
   `clusterID, probetypeID, sideID, shipID`. Declaration order — not the
   assignment order in `Export` — decides byte offsets, so the codec follows
   `igc.h`.
2. **`otTarget` is a 2-byte short, not a 4-byte enum.** The brief inferred a
   4-byte `ObjectType` from `ExportShip`'s message-level `objecttype` field,
   but that 4-byte `[v1_enum]` is a property of the EXPORT *envelope*, not of
   this struct's member. The member's type is the native `ObjectType`, which
   `igc.h:115` declares `typedef short ObjectType`. Consequently there is
   **no 4-byte enum alignment gap** in `DataProbeExport` — it is a clean run
   of 2-byte IDs, and the size is 36 bytes, not the 40+ the brief's assumption
   would imply.

## Derived byte layout — `DataProbeExport` = 36 B

Confirmed empirically with a g++ `offsetof`/`sizeof` probe reproducing the
typedefs (see `/tmp/probe_layout.c`).

| off | len | type  | field        | note |
|----:|----:|-------|--------------|------|
| 0   | 12  | Vector| p0           | three 4-byte LE floats |
| 12  | 4   | uint  | time0        | Time → uint (project convention) |
| 16  | 2   | short | probeID      | ProbeID = ObjectID (igc.h:496,477) |
| 18  | 1   | bool  | exportF      | standalone 1-byte, not a bitfield |
| 19  | 1   | PAD   | —            | probeID(16-17)+exportF(18) → off 19 odd; probetypeID is 2-aligned short |
| 20  | 2   | short | probetypeID  | ExpendableTypeID (igc.h:497) |
| 22  | 2   | short | sideID       | SideID (igc.h:491) |
| 24  | 2   | short | clusterID    | SectorID (igc.h:487) |
| 26  | 2   | short | shipID       | ShipID (igc.h:480) |
| 28  | 2   | short | otTarget     | ObjectType = native short (igc.h:115) — **no enum pad** |
| 30  | 2   | short | oidTarget    | ObjectID (igc.h:477) |
| 32  | 1   | bool  | createNow    | standalone 1-byte, not a bitfield |
| 33  | 3   | PAD   | —            | largest-member align = 4 (Vector/Time) → sizeof rounds 33 up to 36 |
|    |     |       | **= 36 B**    | `sizeof(DataProbeExport)` |

Padding arithmetic, field by field:

- 0-15: `p0` (12 B) + `time0` (4 B), all naturally 4-aligned, no padding.
- 16-18: `probeID` (2 B) + `exportF` (1 B) → next member `probetypeID` is a
  2-aligned `short`; offset 19 would be unaligned, so **one pad byte at 19**
  (same narrow-field-run rule as `DataMineExport`).
- 20-31: six `short`s back-to-back (probetypeID, sideID, clusterID, shipID,
  otTarget, oidTarget) — offsets 20..31, zero padding. This is the step where
  the brief expected a 4-byte enum to force `otTarget` to 32 and the others to
  shift; it does not happen because `ObjectType` is a 2-byte short.
- 32: `createNow` (1 B) → logical content ends at 33.
- Struct alignment = max member alignment = 4 (from `Vector`/`Time`).
  `sizeof` rounds 33 up to **36** → **3 trailing pad bytes**.

## Message shapes

### ExportProbe — `FM_S_EXPORT` (26), `OT_probe` = 4

`src/Inc/AGCIDL.idl:312` (`AGC_Probe = 4`, comment "IGC's OT_probe") matches
`src/Igc/igc.h:123` (`OT_probe = 4`) — **not** the brief's guessed `2`
(that would collide with `AGC_Missile`). Message:

```
  0   ushort cbmsg = 48
  2   ushort fmid = 26
  4   VarItem (ib, cb) exportData ; cb = 36
  8   int32  objecttype = OT_probe 4  ([v1_enum] AGCObjectType int32)
  12  DataProbeExport (36 B fixed)
  = 48 B
```

### ProbeDestroyed — `FM_S_PROBE_DESTROYED` (85)

`src/Inc/Messages.h:608-611` quoted in `ProbeDestroyed.cs`. Fixed 8 B:
header(4) + `clusterID`(2) + `probeID`(2), no var item, same shape as
`MineDestroyed`. `cbmsg = 8`.

## Verification

- 15 new tests in `tests/.../Wire/ProbeWireCodecsWireTests.cs` (round-trips
  incl. both has-target and NA-target `otTarget`/`oidTarget` branches,
  byte-exact buffers, offset pinning, decode-side fmid/objecttype/blob-size
  rejection, fixed sizes).
- Full suite: **3 consecutive clean runs, 2373 tests passing** (up from the
  ~2358 P97 checkpoint baseline).
- No VM involved.

## Follow-up (next task in this arc)

A minimal static `Probe` sim object + a `Ship.DeployProbe` trigger + the
same-side broadcast wiring, mirroring P94's mine slice. **Flagged plainly:**
because a probe carries `c_mtDamagable` (`src/Igc/probeigc.cpp:115`) it is a
direct-fire destructible target, so its removal cannot ride a mine-style
proximity/explosion path — `ResolveCombatCollisions` must be extended with a
real `target type` case for `OT_probe` that applies weapon damage to the
probe's own hull, and (later) the armed-sentry behaviour can then act on
`otTarget`/`oidTarget`. Out of scope here and untouched: `PROBES_UPDATE`(92),
`FIRE_EXPENDABLE`(82/87), `CREATE_CHAFF`(109), `ASTEROID_DESTROYED`(86), and
all `Ship`/`Sim` integration.
