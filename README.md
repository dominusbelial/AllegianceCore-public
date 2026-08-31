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
dotnet run --project tools/fedmsgdump -- /tmp/handshake.bin          # decode a server --capture
dotnet run --project tools/corpusvalidate                          # regression baseline over the corpus
```

## Status

**Combat: done (both halves) and wired live.** A `Ship` fires a mounted `Weapon`
whose shot becomes a real, ticking, self-expiring `Projectile` sector model
(first half), and a projectile that **hits** routes to the target's
`ReceiveDamage` (`shipIGC.cpp:1212`), drops its hull fraction, and — when the
hull reaches zero — kills it and emits `FM_S_KILL_SHIP(105)` exactly once
(second half). **Friendly fire is implemented**: ships carry a `SideId`, and
the collision pass excludes any same-side target (see **`Combat`** below). The
server now arms every fixed hardpoint on every spawned ship with a real weapon
(`MountCombatWeapon`; turret/gunner slots are skipped) and wires the mission site's `KillShipEvent` to a real
`FM_S_KILL_SHIP(105)` wire broadcast (`COMBAT_LIVE_WIRING.md`), proven by a
two-party live test (`LiveCombatWireTests`). Shields are deferred (our ships
mount none; the C++ no-shield branch is the only reachable path). See
**`Combat`** below.

**Bots: done.** A bot process connects, is assigned a ship, commands it and
observes it fly. The three real messages for *assign → command → observe*
(`FM_S_SET_CLUSTER` 7, `FM_C_SHIP_UPDATE` 24, `FM_S_SINGLE_SHIP_UPDATE` 78) and
their two compact payloads are ported into `Wire/` (round-trip tested); the
server gives every validated logon a ship on logon, applies inbound control
input to it, and broadcasts its state after each authoritative tick. The
centerpiece verification compares the server's observed trajectory to an
**independent** `Ship.ExecuteMove` rollout and matches within 1e-3. See
**`Bots`** below, `BOT.md` for the protocol findings, and the new
`BotFlightTests` for the proofs.

**Chat: done (two targets) and wired live — the rest explicitly deferred.**
`FM_CS_CHATMESSAGE(23)` is ported into `Wire/` (round-trip + byte-exact tested)
and the server relays inbound chats via `SendPeer` for exactly two targets:
`Everyone` (every connected peer) and `Team` (peers on the sender's `SideId`,
reusing the P21 side field). Every other `ChatTarget` (Leaders, Admin, Ship,
Allies, Individual, Wing, AllSector, FriendlySector, …) is logged plainly and
**dropped — never broadcast as a fallback, never thrown** (README's report-and-
ignore rule). A `Bot` can `SendChat(...)` and observe received chats
(`Bot.Chats`/`ResetChatReceived`). Proven three ways: the wire codec tests, a
live-socket test (`LiveChatWireTests`, `[Collection("LiveNetwork")]`), and the
`tools/chatdemo` demo — three real bots where Everyone reaches all 3, Team
reaches the 2 teammates, and Wing reaches none. See `P25_CHAT.md` for the wire
layout, file:lines, and the verbatim proof. The deferred targets are listed
under **`What a real client would still need`**.

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
| type universe | 99 `StaticCore` + 111 `MissionMap` load; 1772 tests passing |
| tests | **1772** passing (containers + payloads + `StaticCore`/`MissionMap`/`CrossRefValidator` + `.cvh` + wire decode + corpus baseline) |

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
  header, variable items, `FedPacket` (+CRC), and `LogonRequest`/`LogonAck`,
  plus the ported game messages `SetCluster`(7), `ShipUpdate`(24),
  `ChatMessage`(23), `SingleShipUpdate`(78), `KillShip`(105) and the
  the object-creation messages `NewShipInstance`(31) and **`ExportShip`(26)** —
  both carry a `DataShipIGC` with `nParts=0` (the wire port and the real
  create message respectively; the server sends `ExportShip`(26), which is
  what the C++ client's dispatcher actually routes to `CreateObject` — see
  **`P27_EXPORT.md`** and **`P26_NEW_SHIP_INSTANCE.md`** for the inert
  `NewShipInstance`(31) port), and now **`BuyLoadoutAck`(5)** — the server
  sends `FM_S_BUY_LOADOUT_ACK(5)` right after `SET_CLUSTER(7)` so the client
  adopts a real hull onto its own ship pointer. `ShipLoadout`(sizeof=2,
  hullID offset 0) and `ExpandedPartData`(6 B) were compiler-confirmed on the
  Windows VM against the checked-in `igc.h` (see **`P28_BUY_LOADOUT_ACK.md`**);
  the ACK carries a hull-only loadout (`nParts=0`), `fBoughtEverything=false`,
  `fLaunch=false` — the honest values for assigning a hull with no parts and no
  further launch messages. This is the dummy→real-hull transition the real
  server performs when a player buys a loadout (`FedSrv.CPP` `ProcessConnect`).
  Also new: **`ExportCluster`(26)** — the `OT_cluster`(13) half of the same
  `FM_S_EXPORT` message, conveying each `DataClusterIGC` (92 B) to a joining
  client before `SET_CLUSTER` so the client's `GetCluster(sectorID)`
  (`appmsg.cpp:1003`) resolves a real `CclusterIGC` and `flyingF()` becomes
  true (see **`P30_EXPORT_CLUSTER.md`**). And now **`PlayerInfo`(10)** — the
  server's per-player roster message and the missing carrier of a real side
  to the client's ship (see **`P31A_PLAYERINFO_WIRE.md`**).
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
| `P25_CHAT.md` | `FM_CS_CHATMESSAGE(23)` wire layout as ported, file:lines, live proof |
| `BOOT_PATH.md` `MISSION_INIT.md` `COLLISION.md` `FEDMSG.md` `TRANSPORT_SEAM.md` | the C++ traces these were built from |

---

---

# Combat — weapon firing & projectiles (first half)

The collision detector already reports contacts; combat is being ported in two
halves. **This half: a ship can fire a mounted weapon and the shot becomes a
real, ticking, self-expiring sector model.** It lives in `src/Allegiance.Core/Sim/`
as `Weapon` (the minimal port of `CweaponIGC`, `src/Igc/weaponIGC.cpp` +
`weaponIGC.h`) and `Projectile` (`CprojectileIGC`, `projectileIGC.cpp`).
Damage, shields, death and the collision→damage hook are the **second half**,
documented in the next section.

## The port

- **`Weapon`** — armed/active state, the cooldown gate
  (`m_nextFire < now`, `weaponIGC.cpp:149`) advanced by `dtimeBurst` per shot,
  the energy (`energy >= energyPerShot`) and ammo
  (`ammo > 0 || cAmmoPerShot == 0`) checks, the optional random dispersion, and
  projectile creation. `FireWeapon(now)` is a faithful line-by-line port;
  `Update(now)` does the select→activate→fire choreography.
- **`Ship`** gains `HullType`, `Energy` (seeded to `Hull.MaxEnergy`), `Ammo`
  (seeded to `Hull.MaxAmmo`), the `Sector` back-ref (set by `Mission.PlaceShip`)
  and a `Weapons` list. `Ship.OnUpdate` drives each mounted weapon's `Update`,
  mirroring how `CshipIGC::Update` calls its parts — so **only `Mission.Update`
  creates and moves projectiles**; firing inside the ship's tick sets intent and
  the authoritative chunk loop does the rest. Nothing on the network thread.
- **`Projectile`** — a real `Model` in the sector, created with
  position/velocity/forward derived from the firing ship at the muzzle
  (`weaponIGC.cpp:203,267`), moving in a straight line at constant velocity each
  chunk (`projectileIGC.cpp` base update), and **removing itself from the sector**
  once its clock reaches `birth + decoded Lifespan` (`projectileIGC.cpp:120-144`
  minus the blast, which is the next task).
- **`Sector.Update`** now iterates a snapshot of the model list. The prior slice
  could assume "no model self-terminates"; a projectile now spawns during a ship
  tick and self-terminates on expiry, both inside the model loop — the C++
  captures the successor link for exactly this (`clusterIGC.cpp:243`).
- **`Mission`** owns the runtime object-id counter (the `CreateObject` analogue)
  and routes the projectile's creation to the firing ship's sector.

## Reading the fire (weaponIGC.cpp:142-348)

Speed, lifespan, dispersion, energy-per-shot and ammo-per-shot all come from the
decoded `DataPartTypeIGC.Weapon` / `DataProjectileTypeIGC` in the static core —
**nothing is hardcoded** (the task rule). A stationary ship at identity
orientation (forward `(0,0,-1)`) fires a projectile whose velocity is exactly
`Speed·(0,0,-1)`.

Deliberate simplifications, each documented on the classes:
no gunner (turret weapons need a child gunner ship), no side global attributes
(the `c_gaSpeedAmmo` / `c_gaLifespanEnergy` multipliers are omitted — a no-side
core has value 1.0), no hull-mesh muzzle offset (no rendered model ⇒ muzzle is
the ship's centre), no target-leading/blast (needs a command target), and no
energy recharge (the ship drains but does not regen — economy is a later task, so
a weapon fires until its pool is exhausted unless the test re-feeds it). A weapon
is constructed **armed** (the part-mounting state machine is out of scope).

## Verification (`CombatWeaponFiringTests`, 5 tests)

- a projectile fired from a **stationary** ship travels along the heading at the
  decoded `Speed`;
- the projectile is **in the sector's model list** and ticks with it (its
  displacement between two samples equals `Velocity·elapsed`);
- ⭐ **it expires** — the sector's model count returns to its pre-fire value
  after the decoded `Lifespan` (no invisible accumulation);
- **cooldown is respected** — a second shot lands only after a burst interval
  elapses (and energy returns);
- a **projectile weapon consumes ammo** and stops when the magazine is empty.

Existing **1504 tests stay green** (now **1509**).

---

# Combat — damage & death (second half)

The first half made a ship fire a real, ticking, self-expiring projectile.
This half closes the loop: **a projectile that hits actually hurts, and enough
hits kill the ship and emit `FM_S_KILL_SHIP(105)` exactly once.**

## The port

- **Collision → damage.** `Sector.Update` already ran the `CollisionDetector`
  and reported contacts in time order (COLLISION.md) but applied nothing. The
  same contacts now route each projectile↔ship pair to the target ship's
  `ReceiveDamage`. No second detection path was added — the existing detector is
  reused; only the handler it feeds changed from "ignore" to "apply damage."
- **`Ship.ReceiveDamage`** (`shipIGC.cpp:1212`) — the faithful hull-damage
  port. When a projectile's `HandleCollision` (`projectileIGC.cpp:185-190`)
  contacts a ship, the damage `amount` and `type` come from the decoded
  `DataProjectileTypeIGC` (`Power`, `DamageType`) and `DataPartTypeIGC.Weapon`
  in the static core — **nothing hardcoded**. The armor factor `dtmArmor` is
  `DamageConstants[type][hull.DefenseType]` from the mission's `DataConstantsIGC`
  (the C++ `GetDamageConstant`, `shipIGC.cpp:1233`). Hull integrity is the
  `m_fraction` field (1.0 = full, 0.0 = dead); damage subtracts
  `leakage·dtmArmor / maxHP`.
- **Shields — deferred, not ported.** The C++ branches on
  `m_mountedOthers[ET_Shield]` (`shipIGC.cpp:1282`). If a shield is mounted it
  calls `CshieldIGC::ApplyDamage` (`shieldIGC.cpp:140`), a real absorption model
  that returns only the overflow as `leakage`. If no shield is mounted the else
  branch (`shipIGC.cpp:1287-1291`) sets `leakage = amount` — **all damage goes
  straight to the hull.** Our ships mount no shields (shield parts are out of
  scope for this task), so the no-shield branch is the *only* reachable path;
  `CshieldIGC::ApplyDamage` is not ported. Shields are a separate future task,
  not an approximation.
- **Death + `FM_S_KILL_SHIP(105)`.** When `m_fraction` reaches ≤ 0 the ship is
  dead. The `oldFraction > 0` guard (`shipIGC.cpp:1317`) fires the
  `KillShipEvent` exactly once — on the living→dead transition — never again on
  subsequent ticks. The dead ship is removed from the sector's model list, so
  later ticks neither update it nor re-trigger the event. The wire form is
  `Wire/KillShip.cs` (14 bytes: 4-byte FEDMSG base + shipID, typeCredit,
  idCredit, sideidKiller, bKillCredit, bDeathCredit), matching
  `Messages.h:696-703`.
- **Friendly-fire gate implemented.** `Ship` now carries an optional
  `SideId`, and the collision pass excludes a projectile from damaging any
  target whose side equals the launcher's (`Sector.ResolveCombatCollisions`),
  mirroring `ReceiveDamage` returning `c_drNoDamage` for a friendly launcher
  (`shipIGC.cpp:1222-1228`). Friendly fire is **always off** — there is no
  toggle in this codebase. The self-launcher exclusion (a ship cannot damage
  itself with its own projectile) remains, handled separately at the same
  pass. A ship with no side fights every other ship exactly as before.
  `SpawnDefaultShip` round-robins peers across `mission.Sides`; an empty side
  list logs the omission rather than fabricating one.

## Verification

`CombatDamageTests` (4 tests):
- **point-blank hit drops the target's hull by the weapon's decoded damage**;
  the shooter's hull is untouched (two-bots contract);
- **a projectile fired away never damages anything.**
- **a same-side target takes zero damage** (friendly-fire gate): the friendly
  round crosses the target without landing or terminating and expires — the
  ally's hull is untouched;
- **a cross-side target takes the weapon's full decoded damage**, proving the
  gate does not suppress legitimate enemy combat.

`CombatDeathTests` (4 tests):
- **enough hits kill the ship; `KILL_SHIP` is emitted exactly once** (never
  repeated on subsequent ticks), the dead ship leaves the sector, and the
  shooter's hull stays full (two-bots);
- **post-death `ReceiveDamage` returns `HullDamage` (not `Killed`)** and emits
  no second event — pins the `oldFraction>0` guard directly;
- **`KILL_SHIP` round-trips** and emits the exact 14 declared bytes (hand-
  composed from `Messages.h`, not from the encoder);
- **a dead ship stops being updated / leaves the sector** (the C++ removes the
  model from the sector's thing list on death, so the sector's model count
  returns to its pre-fire value after the kill).

Existing **1509 tests stay green** (now **1519**: +2 live-wire combat tests in
`LiveCombatWireTests.cs` and +2 friendly-fire tests in
`CombatDamageTests.cs`).

---

# Diagnostics: `fedmsgdump` and `corpusvalidate`

The `tools/` folder used to have only **static** dumpers (`igcdump`, `cvhdump`,
`missiondump`, `loopback`) — nothing for the **live** system, so when a bot
misbehaved the only evidence was a stack trace. These two tools change that:
one decodes the raw wire bytes a running server exchanges, the other pins the
state of the whole shipped corpus as a regression baseline.

## `fedmsgdump` — decode FEDMSG bytes to readable messages

`src/Allegiance.Core/Wire/FedMessageDumper` is the tolerant **decode path**
(it reuses the shipped codec — `FedMsgCrc`, `FedMessageHeader`, and each
ported message's `Decode`; it never reimplements decoding). `tools/fedmsgdump`
is a thin console over it.

```bash
dotnet run --project tools/fedmsgdump -- CAPTURE_FILE [CAPTURE_FILE ...]
dotnet run --project tools/fedmsgdump -- -      # hex on stdin
```

Input is the server's `--capture` output: a `CaptureStream` frame per packet —
`dir(1) + len(u32 LE) + packet`, where a packet is one-or-more messages plus
the trailing 4-byte CRC (the packet bytes are exactly what `FedPacket.Pack`
produced, so capture changes nothing on the wire). Output is one readable line
per message — packet boundary + direction, message id and name, the decoded
fixed fields, and each `FM_VAR_ITEM` with its `ib`/`cb` and (where printable)
string value — plus a **CRC verified / FAILED** verdict per packet.

It is built to be useful *precisely* when the data is bad, and degrades
instead of dying:

- **Unknown ids print, never throw.** Only the five messages of the
  assign→command→observe slice are ported; everything else prints
  `id=N (NAME) UNPORTED` with its raw length + hex and the walk continues.
- **Truncated / corrupt captures** stop the walk at the offending frame with
  *how far it got and why* (offset + reason), and a non-zero exit. Corrupt
  packets that still frame cleanly decode best-effort past a `CRC FAILED`.
  A dumper that throws on the first bad byte would be useless exactly when
  needed.

Exit code is `0` only when every frame parsed and every packet's CRC verified.
Verified end-to-end against a **real** capture: `--capture` + one bot logon
produces both directions — `LOGONREQ(1)` → `LOGONACK(2)` → `SET_CLUSTER(7)`
→ inbound `SHIP_UPDATE(24)` → the `SINGLE_SHIP_UPDATE(78)` feed — all CRC
verified. Unit tests (`FedMessageDumperTests`, `CaptureStreamTests`) cover the
decode path and the frame parser.

### `--capture` on the server

`src/Allegiance.Server` gains a strictly optional `--capture <file>` knob that
writes every received packet (in the transport-event drain) and every sent
packet (through the central `SendPeer` path used by the ACK, `SET_CLUSTER` and
`SINGLE_SHIP_UPDATE` emits) in `CaptureStream` frames. **Off by default** and,
when off, the only overhead is a null check — no behaviour or timing change.
When on, it flushes on shutdown.

```bash
dotnet run --project src/Allegiance.Server -- \
    --config src/Allegiance.Server/server.json --port 6073 --capture /tmp/handshake.bin
# then run a client/bot against the same port, Ctrl-C the server, and:
dotnet run --project tools/fedmsgdump -- /tmp/handshake.bin
```

## `corpusvalidate` — every core × map, checked

Walks the whole corpus (the same 210 unique `.igc` files across the two
artwork trees), and for **every** static core loads it and runs
`CrossRefValidator`; for **every** map loads it, assembles the mission against
a representative core (the corpus-wide loose pairing, as in
`MissionAssemblyTests`), and runs the map→core + map-internal cross-refs.

```bash
dotnet run --project tools/corpusvalidate                 # default roots (Artwork + Allegiance)
dotnet run --project tools/corpusvalidate -- --root <dir> # restrict roots
dotnet run --project tools/corpusvalidate -- --emit-baseline
```

It prints a per-file table (per core: type buckets loaded + issue kinds; per
map: sectors, warps resolved vs dangling, stations, asteroids) and an aggregate
summary, then compares the aggregates to an **embedded, locked baseline**. The
point is a *regression baseline, not a purity test* — known data quirks are
counted and recorded, not "fixed". Exit `0` only when the aggregates still
match; `--emit-baseline` prints the acknowledgement block to paste into
`Program.cs` when a change is deliberate. A file that no longer loads or a map
that no longer assembles is also non-zero.

### Corpus baseline (established 2026-08-29)

These are the counts `corpusvalidate` pins and refuses to drift from without an
explicit acknowledge + commit. They match the table above and
`CrossRefSurveyTests` / `CorpusValidateTests`:

| measure | count |
|---|---|
| unique `.igc` files | **210** |
| static cores / maps | 99 / 111 |
| sectors (clusters) | 1506 |
| warps | 5212 (dangling **0**) |
| stations | 289 |
| asteroids | 973 |
| cross-ref issues | `hull-successor=31`, `part-successor=3` (both genuine data quirks) |

Types loaded per core are also pinned per bucket; summed across the 99 cores
they reproduce the table's per-record totals (hull 38927, development 28500,
station-type 12659, launcher 11511 + part 10928 = 22439, projectile 9369,
drone 6503, missile 5370, probe 4760, civilization 952, mine 782, chaff 619,
treasure-set 99).

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
the client version and answering with a server time + ship id); on a validated
logon create and place a `Ship` (hull from the static core), **arm every fixed
hardpoint with a real weapon from the core** (`MountCombatWeapon` — the same
proven weapon on each fixed slot, turret/gunner hardpoints skipped), hand it to the client with
`FM_S_EXPORT(26)` (the object-creation `DataShipIGC` + a separately-typed
`objecttype=OT_ship(0)` that the real C++ client's dispatcher
`appmsg.cpp:80-105` routes to `CreateObject` — sent immediately before the
cluster, and with a deliberate `nParts=0` simplification documented below;
this replaces the inert `FM_CS_NEW_SHIP_INSTANCE(31)` send, whose codec and
tests remain as a faithful-but-inert port — see **`P27_EXPORT.md`**)
followed by `FM_S_SET_CLUSTER(7)` (sector + full `ServerSingleShipUpdate`);
accept inbound
`FM_C_SHIP_UPDATE(24)` and apply the peer's commanded controls *and* weapon-
selection state to its ship (the authoritative tick — `Mission.Update` — alone
moves the ship and fires mounted weapons); broadcast the ship's state as
`FM_S_SINGLE_SHIP_UPDATE(78)` after each tick that moves it; **wire the mission
site's `KillShipEvent` to a real `FM_S_KILL_SHIP(105)` broadcast** to the peers
in the victim's sector when a ship's hull reaches zero (the combat slice: a
mounted, selected weapon fires projectiles that collide, drop hull, and kill —
see `COMBAT_LIVE_WIRING.md`); log every join/handshake/launch/control/kill/leave;
survive an abrupt client drop; stop cleanly on signal.

Does **not** do yet (deliberately, so the milestone stays honest): build stations,
economy, the unimplemented chat targets (everything but `Everyone`/`Team`),
or lobbies; the launch path a *human* pilot needs
(`FM_C_BUY_LOADOUT` — parts/equipment — and `FM_S_EXPORT` are not ported; the
`FM_CS_NEW_SHIP_INSTANCE` object-creation half *is* now sent — the server
allocates the client's ship object via its `DataShipIGC`, then places the
already-created ship directly via `SET_CLUSTER`, which is what the engine uses
to place a launched client anyway — but it carries no mounted-part state
(`nParts = 0`, no `PartData[]` tail, a documented simplification below)); multiplayer rendezvous or team play; load
`cores.txt`/`ranks.txt`/`maps.txt`; advertise to a public lobby; write to a
database. Arming does not yet honour hardpoint/part `PartMask` compatibility
(`DataHullTypeIGC.Hardpoint.PartMask` vs `DataPartTypeIGC.PartMask`, nor the
hull's coarse `PmEquipment[ET_MAX]` gate) — every fixed hardpoint receives the
same proven weapon, a deliberate simplification in this slice's style (real
mask-based fitting belongs with the parts/equipment task, alongside
`FM_C_BUY_LOADOUT`, not here). The stock `server.json` default spawn hull ("Adv Miner", id 1) has
zero energy/ammo and no hardpoints, so a stock live server's mounted weapon
cannot actually fire — a combat-capable default hull (or a `ShipSpawner`) is
needed to demonstrate a live kill (see `COMBAT_LIVE_WIRING.md`, finding 1).
Out-of-scope port machinery (COM, NT service, Steam, SQL) is not
present. Any inbound packet that is not a well-formed logon request or ship
update is reported and ignored — it cannot crash the loop.

---

# Bots

The "and bots" half of the scope decision: a process that connects, is given a
ship, commands it, and observes it move. It uses **only real messages and real
field layouts** — the protocol findings are in `BOT.md`; nothing is invented.

## How to run

```bash
export DOTNET_ROOT="$HOME/.dotnet"     # NOT /snap/bin/dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build

# terminal 1 — the server (any port; 6073 is the config default)
dotnet run --project src/Allegiance.Server -- \
    --config src/Allegiance.Server/server.json --port 6073

# terminal 2 — a bot against it
dotnet run --project src/Allegiance.Bot -- 6073 DemoBot        # forward 4s, yaw 2s
#   optionally:  <port> <name> <forwardSeconds> <yawSeconds>
```

The bot prints its logon ACK, its assigned ship + sector, then the two trajectory
phases (full-forward thrust; then full-forward + yaw) as a per-tick
`pos / vel / heading` table from the `SINGLE_SHIP_UPDATE` feed, and the final
position. The observed positions are the raw full-precision floats the server
put on the wire.

## Combat demo — N independent 1v1 lanes of real bots, one real kill each (`tools/livecombat`)

There is also a **combat** demo (`tools/livecombat`). It boots a fightable
in-process server (via the server's own `ShipSpawner` seam — the stock
`server.json` default hull "Adv Miner" cannot fire), then drives **N pairs =
2N real `Bot` instances exactly as the console app drives one**. Combat fire
is straight-line by design (a ship's weapon fires along its own heading; there
is no aim-assist), so instead of a free-for-all melee the demo scales up to N
independent 1v1 **lanes**: each pair sits in its own lane (staggered on X, same
Z-facing arrangement as P22) so a shooter's straight shot only ever reaches its
own paired target. Each lane's shooter commands `AllWeapons` and holds
nose-to-nose, its target idles, and the server ticks real chunks until a real
`KILL_SHIP(105)` is broadcast to all peers. It prints the per-lane kill each
target bot observes through `Bot.Kills` (the P22 surface). Run it on any port
(or omit for an ephemeral one):

```bash
export DOTNET_ROOT="$HOME/.dotnet"     # NOT /snap/bin/dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build
dotnet run --project tools/livecombat -- 0     # or a fixed port instead of 0
```

It needs the artwork tree at the default `ArtRoot` path. `Pairs` (currently 4 →
8 real connections) and the lane geometry live at the top of
`tools/livecombat/Program.cs`. See `P22_TWO_BOT_COMBAT.md` for the two-bot
baseline and `P24_N_BOT_COMBAT.md` for the N-pair scaling design decisions and
verbatim captured output.

## Which messages are ported (Wire/)

All round-trip tested (and byte-layout pinned) in `GameMessageRoundTripTests`:

| id | message | payload | direction |
|----|---------|---------|-----------|
| 7  | `FM_S_SET_CLUSTER` | `ServerSingleShipUpdate` (47 B) + `sectorID` + `cookie` | S→C — "here is your ship" |
| 24 | `FM_C_SHIP_UPDATE` | `ClientShipUpdate` (39 B: controls + state) | C→S — commanded input |
| 78 | `FM_S_SINGLE_SHIP_UPDATE` | `ServerSingleShipUpdate` + `otTarget`/`oidTarget` + `bIsRipcording` | S→C — authoritative position |
| 38 | `FM_S_MISSIONDEF` | `MissionParams` (252 B) + description/cookie/side/stage/server-name arrays (= 864 B) | S→C — the mission definition that precedes a join |
| 95 | `FM_S_JOINED_MISSION` | `shipID` + `dtime` + `cookie` + `dwCookie` (= 20 B) | S→C — "you are in the game"; triggers the client's static-core load |
| 10 | `FM_S_PLAYERINFO` | `var-item(rgPersistPlayerScores)` + `CharacterName[25]` + `iSide`/`shipID`/`pilotType` etc. (= 68 B) | S→C — per-player roster; the one message that carries a real side to the client's ship |
| 26 | `FM_S_EXPORT` (`OT_cluster` 13) | `objecttype` int32 + `DataClusterIGC` (92 B) (= 104 B) | S→C — "here is sector X" |

Every peer is sent `MISSIONDEF` then `JOINED_MISSION` at logon, **before** the cluster `EXPORT`s, the ship `EXPORT`/`SET_CLUSTER`/`BUY_LOADOUT_ACK`. `MISSIONDEF` allocates the client's mission-info object (`appmsg.cpp:2416`) which `JOINED_MISSION` requires (`assert(m_pMissionInfo != NULL)`, `appmsg.cpp:2154`); `JOINED_MISSION` calls `ResetStaticData` (`appmsg.cpp:2162-2163`), which `FlushGameState`s the current mission and imports the local static core — so any object-creation/launch message sent before it would be destroyed by that flush, hence the ordering. Right after the join, the server **exports every cluster** (`OT_cluster`) to the peer, so that when `SET_CLUSTER` later arrives its handler's `GetCluster(sectorID)` (`appmsg.cpp:1003`) resolves a real `CclusterIGC` and assigns the ship to it — making `flyingF()` (`clintlib.h:1198`) true and `BaseClient::SendUpdate` start emitting `FM_C_SHIP_UPDATE`. This mirrors the reference server's `SendMissionInfo` cluster loop (`FedSrv/fsmission.cpp:4170-4173`). Two fields matter: `misparms.szIGCStaticFile` is the **extensionless** base name ("PCore001", because the client's `LoadIGCStaticCore` appends ".igc" — see `P29_MISSIONDEF_JOINED_MISSION.md`), and `misparms.verIGCcore` must equal the version word at offset 0 of that core; everything else in the large `MissionParams` is zeroed (simulation policy this headless server doesn't implement). See **`P29_MISSIONDEF_JOINED_MISSION.md`** and **`P30_EXPORT_CLUSTER.md`**.

Right after the cluster/ship `EXPORT`s, and still **before** `SET_CLUSTER`, the server sends **`FM_S_PLAYERINFO(10)`** (carrying the peer's real `iSide`, `shipID`, `pilotType`=10, `CharacterName`, everything else zeroed) preceded by a **second `FM_S_MISSIONDEF`** populated with `misparms.nTeams` and `rgCivID[]`. The second MISSIONDEF drives the client's `UpdateSides` (the `m_pMissionInfo != NULL` else-branch, `appmsg.cpp:2419-2425`) to create the real sides *and* `SIDE_TEAMLOBBY` via `CmissionIGC::AddSide` — the precondition the `PLAYERINFO` handler's `AddPlayerToMission`/`AddPlayerToSide` need, else the message cannot be processed. `PLAYERINFO` is the missing side-carrier: its handler is the only dispatched path that reaches `AddPlayerToSide` (`clintlib.cpp:4103`, the `pship->SetSide` at `:4124`), which is what assigns the side the `SET_CLUSTER` handler faults for. See **`P31A_PLAYERINFO_WIRE.md`**.

Supporting compact codecs (`BytePercentage` div-240, `CompactControlData`,
`CompactTurnRates`, `CompactVelocity`, quaternion `CompactOrientation`) live in
`Wire/Compact/`. The bot is `src/Allegiance.Bot/Bot.cs` (reusable client class)
+ `Program.cs` (console driver).

## Who moves the ship

The server is authoritative. Inbound `FM_C_SHIP_UPDATE` only sets the peer
ship's `Controls`/`StateM`; **only `Mission.Update` moves it** (the per-chunk
`Ship.ExecuteMove` + position integration — the flight model already in the
repo). Control input is applied on the simulation thread during the tick's
event drain, and `SINGLE_SHIP_UPDATE` is emitted after the tick, also on the sim
thread — never from the network reader.

## Verification

`tests/Allegiance.Core.Tests/BotFlightTests.cs` starts a server **in-process**,
runs real bots over the loopback TCP transport, and asserts:

- **trajectory match** — the server's ship positions equal an **independent**
  `Ship.ExecuteMove` rollout (seeded from the same hull/spawn/command/cadence,
  rolled forward by the test, never fed the server's output) within 1e-3 — the
  server is simulating, not echoing the client's zero-valued position guesses;
- the bot observed those exact positions on the wire (`SINGLE_SHIP_UPDATE`);
- **zero thrust ⇒ no movement**, and no update traffic (never dirty);
- **two bots get different ships** (distinct ids/spawns) and fly independently
  (A thrusts, B idles);
- a **bot disconnect leaves the server ticking**, releasing the ship from the
  broadcast set while the tick keeps moving it, and a fresh bot is still served.

## What a real client would still need

This demo speaks the *assign → command → observe* slice faithfully, but a
complete Allegiance client does a lot more. The real messages a human pilot's
launch path would exercise but which are **not yet ported** (and why they are
safe to defer — recorded in `BOT.md`, not faked):

- **`FM_C_BUY_LOADOUT(4)` / `FM_S_BUY_LOADOUT_ACK(5)`** — the parts/equipment
  purchase launch; it drags in the whole `ShipLoadout`/`ExpandedPartData`
  machinery. The server assigns a ship directly on logon via `SET_CLUSTER`
  (which is also how the engine places a launched client), so no parts are
  bought. A real client wanting to buy a loadout first would need these plus the
  full parts export.
- **`FM_S_EXPORT(26)`** — the *parts* export a render client uses to rebuild a
  mounted loadout after the ship object exists. **Not ported** (separate work).
- **`FM_CS_NEW_SHIP_INSTANCE(31)` is now ported and sent.** On a validated logon
  the server sends it to the peer just before `SET_CLUSTER` — the object
  creation message carrying a `DataShipIGC` (52 bytes) that makes the C++
  client's `Import`/`CshipIGC::Initialize` allocate the ship object its
  `GetShip()`/`GetBaseHullType()`/`GetPosition()` read from, closing the exact
  null-backed-wrapper `NullReferenceException` that T14 diagnosed. It is **own
  ship only** — deliberately not broadcast to observers (mutual multi-bot
  visibility is future work). **Stated simplification, `nParts = 0`:** the
  `DataShipIGC` is written with the object-creation header only and no
  `PartData[]` tail — it allocates the ship object but carries no mounted-part
  state; real per-hardpoint part export would need `Ship`'s mounted parts
  serialized in the exact `PartData` (partID/amount/mountID, 5 bytes each)
  format, which belongs with the parts/equipment task (alongside
  `FM_C_BUY_LOADOUT` and `FM_S_EXPORT`). Encoder/decode-round-trip and
  byte-exact tests are in `tests/Allegiance.Core.Tests/Wire/
  NewShipInstanceWireTests.cs`.
- Time synchronization / client prediction reconciliation (`timeUpdate`/echoed
  position are carried on the wire already but the server deliberately treats
  only `Controls`/`StateM` as authority).
- Cluster hopping (player-initiated warp), mines/probes,
  the lobby/advertising, and the fuller message families (`FM_S_STATIC_MAP_INFO`,
  `FM_S_SET_CLUSTER`'s sibling cluster messages, etc.). (`FM_S_PLAYERINFO` is
  no longer here — it is ported and sent as **`P31A_PLAYERINFO_WIRE.md`**
  describes.)
- **The unimplemented `ChatTarget`s.** Chat itself is wired for `Everyone` and
  `Team` only. The remaining real `AGCChatTarget` modes
  (`src/Inc/AGCIDL.idl:452-470`) — `Leaders`, `Admin`, `Ship`, `Allies`,
  `Individual`, `Individual_NoFilter`, `Wing`, `AllSector`, `FriendlySector`,
  plus the client-only `Group`/`GroupNoEcho` (translated into multiple sends on
  the real client) — are deliberately deferred: several need concepts this
  codebase doesn't yet model (commander/leader roles, wings, private 1:1
  delivery to a specific recipient). The server logs each one
  ("chat target N not implemented in this slice") and drops it; see
  `P25_CHAT.md`.

All **1772** tests pass: the bot flight suite, the tolerant-wire decode path
(`FedMessageDumperTests`/`CaptureStreamTests`), and the corpus regression lock
(`CorpusValidateTests`).
