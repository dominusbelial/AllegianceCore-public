# AllegianceCore

C# port of the Allegiance simulation data layer. Server-side only — no
rendering, no networking, no COM.

Scope decision: **headless server + bots first, no render client.**

## Build & test

```bash
export DOTNET_ROOT="$HOME/.dotnet"     # NOT /snap/bin/dotnet, which is a broken stub
export PATH="$DOTNET_ROOT:$PATH"
dotnet build
dotnet test
```

SDK 10.0.400. `dotnet --version` must print `10.0.400`; if you see
"Looks like you don't yet have a .NET SDK", your `PATH` is wrong.

```bash
dotnet run --project tools/igcdump -- /home/domingo/Development/Artwork/PCore001.igc
dotnet run --project tools/cvhdump -- /home/domingo/Development/Artwork/fig01.cvh
dotnet run --project tools/missiondump -- /home/domingo/Development/Artwork/PCore001.igc \
                                         /home/domingo/Development/Artwork/m2xwheel.igc
```

## Status

**Headless server: done.** The boot path (BOOT_PATH.md §"Minimum viable boot") is
implemented end-to-end in `src/Allegiance.Server/`: a program that boots a real
core, instantiates configured missions, ticks them at a fixed cadence, and
accepts TCP logon clients. See **`Headless server`** below.

**Container frame: done and verified. Payload decoding: every shipped object
type is done — all fifteen static-core types (fixed- and variable-length) plus
the four map-file objects. Type universe: `StaticCore` + `MissionMap` + a
non-throwing `CrossRefValidator` connect the decoders into a loadable,
ID-memoized model (checklist item 4 of the boot path).**

All eleven fixed-size static payload structs, the four variable-length static
types (part subtypes, treasure sets), and the four fixed-size map-file objects
are decoded and corpus-verified. The standalone `.cvh` collision-hull format is
now decoded too — see **`.cvh collision hulls`** below.

| | | |
|---|---|---|
| corpus | **210 unique** `.igc` files (217 on disk; 7 are byte-identical cross-tree duplicates) |
| acceptance | **210/210 consumed exactly** — no trailing bytes, no overruns |
| wrappers | 99 static cores (`version`+`datasize`), 111 maps (`datasize`) |
| type universe | 99 `StaticCore` + 111 `MissionMap` load; 1370 tests passing |
| tests | **1370** passing (containers + payloads + `StaticCore`/`MissionMap`/`CrossRefValidator` + `.cvh`) |

### The acceptance property

There is no Windows here to diff against the original writer, so the oracle is
**exact byte consumption**: for every file the record stream must land precisely
on end-of-body. A wrong field width or offset anywhere desynchronises everything
after it, so this single property catches nearly every layout error without a
reference implementation.

It was first established by `../allegiance-tools/scan_igc_corpus.py`; this
library reproduces it.

## Implemented

- `IgcObjectType` — the 24 `ObjectType` values (`src/Igc/igc.h:114-160`)
- `IgcRecord` — the 6-byte header: `int16 type`, `int32 size` (payload excludes header)
- `IgcReader` — wrapper detection + record-stream walk
- `IgcFile` — records, wrapper form, version, exactness
- **`Wire/`** — the FEDMSG wire framing + logon handshake: `cbmsg`/`fmid`
  header, variable items, `FedPacket` (+CRC), and `LogonRequest`/`LogonAck`.
  See **`FEDMSG.md`** and **"The FEDMSG wire"** below.

**Payload decoders** (`src/Allegiance.Core/Data/`), each a `readonly record
struct` with a `Decode(ReadOnlySpan<byte>)` that throws on a wrong record size:

| type | struct | size | corpus count |
|---|---|---|---|
| 22 `OT_projectileType` | `DataProjectileTypeIGC` | 88 | 9369 |
| 23 `OT_missileType` | `DataMissileTypeIGC` | 524 | 5370 |
| 24 `OT_mineType` | `DataMineTypeIGC` | 480 | 782 |
| 25 `OT_probeType` | `DataProbeTypeIGC` | 492 | 4760 |
| 26 `OT_chaffType` | `DataChaffTypeIGC` | 468 | 619 |
| 27 `OT_civilization` | `DataCivilizationIGC` | 268 | 952 |
| 29 `OT_hullType` | `DataHullTypeIGC` | 540 + 30·maxWeapons | 38927 |
| 31 `OT_stationType` | `DataStationTypeIGC` | 524 | 12659 |
| 32 `OT_development` | `DataDevelopmentIGC` | 468 | 28500 |
| 33 `OT_droneType` | `DataDroneTypeIGC` | 384 | 6503 |
| 34 `OT_constants` | `DataConstantsIGC` | 1760 | 99 (one per core) |
| 1 `OT_station` | `DataStationIGC` | 88 | 289 |
| 13 `OT_cluster` | `DataClusterIGC` | 92 | 1506 |
| 7 `OT_warp` | `DataWarpIGC` | 108 | 5212 |
| 5 `OT_asteroid` | `DataAsteroidIGC` | 144 | 973 |
| 30 `OT_partType` | `DataPartTypeIGC` + `DataLauncherTypeIGC` | 24/400/412/416/424 | 22439 |
| 28 `OT_treasureSet` | `DataTreasureSetIGC` | 32 + 4·n | 99 |

The five `OT_expendableType`-derived types (missile/mine/probe/chaff, plus the
shared base read into each) embed a `LauncherDef`; their full flattened
layouts were verified per-record. `DataConstantsIGC` is a flat 40 + 20×20
matrix of floats (`c_fcidMax`/`c_dmgidMax`/`c_defidMax`, all 20 or 40).

`OT_partType` dispatches by record size to `ClauncherTypeIGC` (24 B) or
`CpartTypeIGC` (`src/Igc/missionigc.cpp:3344-3355`); the base part then carries a
subtype tail chosen by `equipmentType` (`DataPartTypeIGC` base is 396 bytes,
but no bare 396-byte record exists — every record carries a subtype tail).
Seven shapes occur: 24 (launcher), 400 (`DataPackTypeIGC`), 412
(`DataShieldTypeIGC`), 416 (`DataCloakTypeIGC` *and* `DataAfterburnerTypeIGC`
share the size, distinguished by `equipmentType`), 424 (`DataWeaponTypeIGC`).
The decoder models this as a base record + a discriminated `Subtype` so callers
can switch on equipment type; `DataLauncherTypeIGC` is a separate 24-byte
struct.

`OT_treasureSet` is read with the wiki's ⭐ recommended reader: entry count is
derived from the record size (`n = (size − 32) / 4`) and the header count is
ignored. Each entry is 4 bytes: `treasureID` (ushort) + `treasureCode` (byte) +
`chance` (byte).

Detection walks each candidate wrapper and takes whichever lands exactly on
end-of-body, rather than guessing from the filename. A wrong wrapper
desynchronises the very first header, so this is unambiguous in practice.

### Corpus-measured corrections to the wiki table

The wiki's `allegiance-igc-file-format` page carries a ⭐ corrections section,
but further field-level discrepancies surfaced while decoding (none in the
wiki's own corrections list; all are reflected in the decoders):

1. **`DataProjectileTypeIGC.width` is at offset 80, not 78.** After
   `bDirectional` at 76 the float needs 4-byte alignment, so there are three pad
   bytes 77-79. Verified: `width`@80 is a clean float in all 9369 records and
   `ambientSound` is `-1` in every one.
2. **`DataCivilizationIGC` has a pad byte at 159** pushing `gasBaseAttributes`
   to 160-259; the trailing `lifepod`/`civilizationID`/`initialStationTypeID`
   sit at 260/262/264 (the wiki's draft said gas@159 and IDs at 259/261/263).
   Verified: every `lifepod` and `initialStationTypeID` decodes to a real
   hull/station-type ID present in the same core.

3. **The expendable types and the station type carry the same
   narrow-to-wide padding pattern.** For missile/mine/probe/chaff
   (all `DataExpendableTypeIGC`-derived, `igc.h:1947`), the fully flattened
   layouts have pads at 50-51 (after `DataObjectIGC`), 426-427 (inside the
   embedded `LauncherDef` after its `DataBuyableIGC` base), and a single pad
   before the derived tail — 463 for the tail-start float. Concretely:
   `loadTime`@52, `launcherDef.signature`@428, `hitPoints`@440, and
   `expendabletypeID`@446 are common to all four; `iconName` ends at 463.
   The tails then diverge: missile `acceleration`@464 (…`ambientSound`@520,
   two trailing pads to 524), mine `radius`@464 (…`damageType`@476, three
   trailing pads to 480), chaff `chaffStrength`@464, probe `scannerRange`@464
   (…`dtRipcord`@488). The station type (`igc.h:2649`) pads after its base to
   `signature`@364 and has a single pad before `constructionDroneTypeID`@458
   (after the byte `classID`@456). Every float in every one of these records
   is a clean finite value across the corpus; these offsets match
   `tools/verify_layout.py`, which the last batch left in the tree.
4. **`DataConstantsIGC` has no padding** — it is entirely floats
   (40 `floatConstants`, then `20×20 damageConstants`, `igc.h:21`), so the
   1760-byte struct is trivially 4-aligned throughout.
5. **The shipped `DataAsteroidIGC` tail differs from current `igc.h`.** The
   files put `clusterID` at **116**, `name` at **118** and `fraction` at
   **143**, with **three uninterpreted bytes at 113-115** between `iconName`
   (ending at 112) and `clusterID`. Today's `igc.h:1891` struct (and the wiki
   table) would place clusterID at 114 (one pad at 113) and name at 115 — two
   bytes earlier — so the shipped maps were written by an older revision whose
   struct carried three extra bytes here. The cross-reference check is
   decisive: `clusterID`@116 resolves to a real sector in the same file in
   **973/973** asteroid records. The 113-115 bytes are overwhelmingly
   uninitialized (MSVC debug fill `0xCC`, or zero) and are deliberately **not**
   decoded.
6. **Part types are dispatched by `equipmentType`@376, a 2-byte `short`**
   (not the 4-byte enum one might assume — `igc.h:530` types `EquipmentType`
   as `short`), followed by `partMask`@378. In the 412-byte `DataShieldTypeIGC`
   tail, `defenseType` is a single byte at **404** (`DefenseTypeID` is
   `unsigned char`, `igc.h:557`) with an intra-struct pad at 405 before the
   short `activateSound`@406 — so it reads as one clean byte, never a
   two-byte sound pair. The cross-reference checks are decisive: every weapon
   `projectileTypeID` and every launcher `expendabletypeID` resolves to a real
   projectile/expendable in the same core (**0 mismatches in 22,439 records**),
   and every non-NA part `successorPartID` names a real part.
7. **Treasure-set entries are four bytes, and the header count is wrong.**
   The shipped files carry the *older* two-byte-id form of `TreasureData`
   (`treasureID` ushort + `treasureCode` byte + `chance` byte = 4 B), not
   today's `igc.h:2100` `ObjectID`-based 6→8-byte form. The header's two-byte
   count at offset 27 reads **`256 × entries`** in **every one of the 99
   records**, generation A and B alike — it is not `n`, and it is not random
   garbage either. Despite the regularity, it is deliberately **not** depended
   on: the decoder derives `n = (size − 32) / 4` from the record size, exactly
   as the wiki's ⭐ recommended reader prescribes. Validation is decisive:
   1693/1693 part-coded `treasureID`s resolve to real parts in the same core,
   96/96 powerup codes are valid PowerupCode bitmaps, and the 96 cash
   `treasureID`s are the dollar amounts (500…10000). Because the corpus shows
   no real generation split in the on-disk bytes, the reader does not branch
   on generation; the count-field anomaly remains recorded here and unsolved
   (an honest documented workaround beats a confident wrong theory).

## Not implemented

The runtime map-object types `OT_ship` (0), `OT_treasure` (8), `OT_mine` (3),
`OT_probe` (4), `OT_buoy` (9) and `OT_bucket` (14) are **intentionally not
implemented** — they carry **zero records** across the whole 210-file corpus
(measured 2026-08-28), so a decoder for them would be an unfalsifiable guess
that compiles, passes, and silently rots. They are runtime/savegame objects
that never appear in a shipped `.igc` map. (This corrects an earlier draft of
this section that listed ship and treasure as pending work.)

Layouts are specified in the wiki page `allegiance-igc-file-format`.
⚠️ That page carries a **corpus-measured corrections** section; where it
contradicts an earlier section, the corrections win. Where the page and
`src/Igc/igc.h` disagree, `igc.h` wins (it is the actual writer).

## Facts worth keeping

- **Plaintext on disk.** The `munge` decrypt hook is wired only for the Zone
  Club download path; `ALLSRV_STANDALONE` passes NULL
  (`src/FedSrv/fsmission.cpp:164-166`). No decryptor needed.
- **Hull stride is 30**, not 29: `540 + 30 * maxWeapons`. Sizes observed are
  540/570/600/…/780, every delta exactly 30.
- **The version word is a Unix timestamp** — `PCore001.igc` carries
  `990051329` = 2001-05-17, matching
  `iStaticCoreVersion = static_cast<...>(time(0))` (`missionigc.cpp:2363`).
- **Artwork lives in two trees.** A search scoped to the code repo alone finds
  almost none of the corpus.

## Detailed documentation

The per-subsystem detail lived here until it reached 1,096 lines. It now lives in
`docs/`, and the design traces stay at the repo root:

| file | covers |
|---|---|
| `docs/formats.md` | `.cvh` collision-hull format |
| `docs/model.md` | `StaticCore`/`MissionMap`, mission assembly, the `IIgcSite` seam |
| `docs/simulation.md` | tick cadence, flight model, ships in sectors, collision |
| `docs/wire.md` | FEDMSG framing, `FM_VAR_ITEM`, the non-standard CRC |
| `BOOT_PATH.md` `MISSION_INIT.md` `COLLISION.md` `FEDMSG.md` `TRANSPORT_SEAM.md` | the C++ traces these were built from |

---

# Headless server

`src/Allegiance.Server/` is the program this port was aimed at: a headless
Allegiance server that boots, loads real game data, ticks a real mission at a
fixed cadence, and accepts TCP clients through the logon handshake. It assembles
components that already existed (`StaticCore`, `MissionMap`, `Mission`,
`TcpTransport`, the `Wire` codec) into a runnable `Main()`.

## How to run

```bash
export DOTNET_ROOT="$HOME/.dotnet"     # NOT /snap/bin/dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build                           # builds the whole solution incl. the server

# from the repo root:
dotnet run --project src/Allegiance.Server -- --config src/Allegiance.Server/server.json --port 6200
```

Press Ctrl-C (SIGINT) or `kill -TERM <pid>` to stop; the server tears down in
reverse of boot and prints `[shutdown] clean: N ticks, M overruns`.

The default `server.json` boots `PCore001.igc` against `XXV_simple.igc` into one
two-team mission ("Test Mission": Dreghklar vs Ga'Taraan) on port 6073 at 20 Hz.
Any of the corpus cores/maps can be substituted via the config file.

## Config format

The shape is decided (see the task): a JSON file at `--config` (default
`server.json`), with scalar fields overridable on the command line
(`--port`, `--artpath`, `--staticcore`, `--ups`/`--updatespersec`). Games are
file-defined — there is no CLI form for the nested `games` array.

```json
{
  "artPath": "/home/domingo/Development/Artwork",
  "staticCore": "PCore001.igc",
  "port": 6073,
  "updatesPerSecond": 20,
  "games": [
    { "name": "Test Mission", "map": "XXV_simple.igc", "teams": 2,
      "civIds": [1, 3], "sideNames": ["Alpha", "Bravo"] }
  ]
}
```

| field | meaning |
|---|---|
| `artPath` | root of the artwork tree (holds the core and map files) |
| `staticCore` | core file name, relative to `artPath` — loaded at boot (checklist item 4) |
| `port` | TCP port the logon transport listens on; 0 selects an ephemeral port |
| `updatesPerSecond` | tick cadence in Hz; fixed period = 1000/ups ms |
| `games[].name` | human label, logs only |
| `games[].map` | map file name, relative to `artPath` |
| `games[].teams` | number of sides; sides are built for ids 0..teams-1 |
| `games[].civIds` | per-team `rgCivID[]` — index the core's civilizations. **Not derivable from core+map**; these are mission parameters (`MISSION_INIT.md` §2). |
| `games[].sideNames` | per-team display names (`UpdateSides`' caller-supplied array, `missionigc.cpp:3762`) |

Sides come from the config exactly as the reference engine derives them from
`MissionParams::nTeams`/`rgCivID[]` plus host-supplied names — that is why they
are in the file and not inferred. A `civIds[t]` that is absent from the core is a
boot-time `InvalidOperationException` naming the offending id.

## Boot path (the 10-item checklist)

Reproduce: config + artwork path (1-2); the gateway site + transport (3, 6);
load the static core into the type universe (4); instantiate each configured
game as a started `Mission` with its own resolved sides and sectors (8); the
fixed-cadence loop (9); signal handling + teardown in reverse (10).

Stub: `cores.txt`/`ranks.txt`/`maps.txt` (5) — the download/rank lists are out of
scope; boot logs `(stub)` rather than silently ignoring them.

Drop: public lobby registration (7), COM/ATL/AGC, NT-service, admin COM, Steam,
the single-instance mutex, SQL, lobby advertising.

The "gateway mission" of checklist item 3 is the `ServerHost` seam itself: in the
reference engine the gateway hosts the connection/lobby surface and is the first
bound `IIgcSite` at boot. With the lobby dropped, its only surviving duty — being
the live `IIgcSite` the missions bind to and the owner of the transport — is
`ServerHost` (`src/Allegiance.Server/ServerHost.cs`); every game mission binds to
that same live host, so the tick reaches a site that is actually observing.

## The tick loop

Fixed period `1000/updatesPerSecond` ms (50 ms at 20 Hz). Each pass:

1. **Pump transport** — transport-thread callbacks (`ITransportSite` events)
   are queued into a `ConcurrentQueue` and drained on the simulation thread, so
   the "single owner" is the tick loop (the same funnel as FedMessaging's
   `m_csMsgList`).
2. **Advance sim time** by exactly one period, then `mission.Update(now)` for
   every started mission. Sides update once per `Update`; each sector once per
   chunk (`Mission.TimeIncrement` = 250 ms chunks — a 50 ms tick is one chunk).
3. **Sleep the remainder** of the period to hold cadence, measured with a
   `Stopwatch` (monotonic — never wall time).

**Two clocks, deliberately apart.** The *simulation* clock advances by exactly
`period` per tick — deterministic and strictly monotonic, so it can never trip
`Mission.Update`'s backwards-time or `deltaT >= 600` guards. The *cadence* clock
is a `Stopwatch` measuring the real cost of a pass to decide how long to sleep
and whether to log.

**Tick-overrun policy.** The real pass cost is compared against the period; the
difference is the sleep budget. If the cost already exceeded the period, the
server **logs `[overrun]`** with the excess, does **not** skip a tick and does
**not** accumulate debt — it simply has nothing left to sleep and advances the
sim clock by the full period anyway. Under sustained overrun the sim clock
therefore advances faster than real wall time; that drift is made visible by the
overrun log rather than silently hidden. We never clamp, skip, or spiral: the sim
clock is independent of wall time by construction.

## Signals, teardown, threads

`PosixSignalRegistration` intercepts SIGINT (Ctrl-C) and SIGTERM on Linux and
cancels the loop (`PosixSignalContext.Cancel = true` — forgetting this lets the
signal's default disposition kill the process mid-loop, so it is set explicitly).
Teardown is the reverse of boot: the loop stops, `TcpTransport.Shutdown()` joins
its accept + per-peer read threads, and the site drops. No orphan threads
remain — `Server` is `IDisposable` so a test that aborts mid-flight still tears
the transport down.

## What the server does and does not do yet

Does: boot a real core + real map from the corpus; assemble missions with real
sides/sectors; tick them at cadence; accept TCP peers through the transport greet
and complete the logon handshake (`FM_C_LOGONREQ` → `FM_S_LOGONACK`, validating
the client version and answering with a server time + ship id); log every
join/handshake/leave; survive an abrupt client drop; stop cleanly on signal.

Does **not** do yet (deliberately, so the milestone stays honest): drive gameplay
objects (no ships/missiles/stations in motion — sides and sectors update, which
is the empty-mission tick the C++ also runs); build stations, combat, economy,
chat, or lobbies; load `cores.txt`/`ranks.txt`/`maps.txt`; advertise to a public
lobby; write to a database. Out-of-scope port machinery (COM, NT service, Steam,
SQL) is not present. Any inbound packet that is not a well-formed logon request
is reported and ignored — it cannot crash the loop.
