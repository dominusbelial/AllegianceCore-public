# BOT — the "and bots" slice: connect, get a ship, fly it

Work log and protocol decisions for the second half of the scope decision.
Reference C++ lives in the sibling checkout `/home/domingo/Development/Allegiance/`
(read-only here). Line numbers below are against that tree as of 2026-08-29.
This task is **pure C#** — nothing under `Allegiance/` or `Artwork/` is touched.

## Goal restated

A bot process that connects, is assigned a ship, commands a manoeuvre, and
observes the ship move. The server stays **authoritative**: inbound control
input only sets the ship's controls; **only `Mission.Update` moves it**.

## Step 1 — the real messages (nothing invented)

We need the minimum real subset for: *client is assigned a ship → client sends
control input → client learns where its ship is*. Of the 136 `DEFINE_FEDMSG`
entries, the following real messages cover exactly that flow:

| id | macro | dir | role in the flight demo |
|----|-------|-----|--------------------------|
| 7  | `FM_S_SET_CLUSTER` | S→C | **assign the ship**: server tells the launched client its `SectorID` + full `ServerSingleShipUpdate` (position/velocity/orientation/controls/fractions/power) + `cookie` — `Messages.h:83-87`. This is the message the reference sends when a client launches/undocks, so it is the honest "you are in sector X, here is your ship" message. |
| 24 | `FM_C_SHIP_UPDATE` | C→S | **control input**: `Time timeUpdate` + `Cookie cookie` + `ClientShipUpdate` (position/velocity/orientation echo, `CompactState`, `CompactControlData`, turn rates, power) — `Messages.h:227-231`. Sent repeatedly by the client at `c_dsecUpdateClient = 0.15s`. |
| 78 | `FM_S_SINGLE_SHIP_UPDATE` | S→C | **observe position**: `ObjectType otTarget` (short), `ObjectID oidTarget`, `ServerSingleShipUpdate`, `bool bIsRipcording` — `Messages.h:583-588`. The server's per-ship state broadcast; its payload carries a **raw full-precision `Vector position`**, ideal for the trajectory observation. |

Rejected as too heavy for now (recorded gap, not faked):
- `FM_C_BUY_LOADOUT(4)` / `FM_S_BUY_LOADOUT_ACK(5)` — the real launch path,
  but the `loadout` is a `ShipLoadout` = `HullID` + array of `ExpandedPartData`,
  which drags in the whole parts/equipment machinery. Our server assigns a ship
  directly on logon (the config already supplies a hull from the static core),
  so no parts are bought. `SET_CLUSTER` is what the server uses to hand the
  client its ship anyway.
- `FM_C_NEW_SHIP_INSTANCE(31)` / `FM_S_EXPORT(26)` — carry `DataShipIGC` +
  parts export for *creating* a ship object on the client; not needed because
  `SET_CLUSTER` already delivers the full live `ServerSingleShipUpdate` and the
  bot does not maintain a client-side object graph.

So the **three real messages ported** are `SET_CLUSTER(7)`, `SHIP_UPDATE(24)`,
`SINGLE_SHIP_UPDATE(78)` plus the two compact payload structs they embed:
`ServerSingleShipUpdate` and `ClientShipUpdate`. Those are the actual
game-data layout on the wire — fully ported (no subset hack needed).

### The embedded compact structs (the real byte layout)

All live under `#pragma pack(push,1)` (`igc.h:2120-2486`) so they are
padding-free — sequential bytes in the exact field order below. The size
comments in the header are stale (they predate the `bpOre` fraction field and
several later additions) — **the field declarations win**, not the comments.

`ServerSingleShipUpdate` (`igc.h:2444`) inherits `ServerLightShipUpdate`
(=`shipID` short + `CompactState stateM` ushort + `CompactControlData controls`
4 bytes) then adds:

```
offset  size  field                                    type/source
0       2     shipID                                   ShipID = short (igc.h:480)
2       2     stateM                                   CompactState ushort (igc.h:2258)
4       4     controls                                 CompactControlData = 4 signed chars yaw,pitch,roll,throttle (igc.h:2333)
8       4     time                                     Time = uint32 (ztime.h:22)
12      12    position                                 Vector = 3 floats (vector.h)
24      8     orientation                             CompactOrientation = ushort qa + short qx,qy,qz (igc.h:2137)
32      5     velocity                                 CompactVelocity = ushort speed/flags + 3 BytePercentage (igc.h:2275)
37      3     turnRates                               CompactTurnRates = 3 signed chars yaw,pitch,roll (igc.h:2314)
40      6     fractions                                CompactShipFractions = 6 BytePercentage hull/shield/ammo/fuel/energy/ore (igc.h:2352)
46      1     power                                    BytePercentage (Utility.h:1307)
= 47 bytes (comment says 46; pre-ore)
```

`ClientShipUpdate` (`igc.h:2457`) is standalone — no inheritance:

```
offset  size  field        type
0       4     time         uint32
4       12    position     Vector (3 floats)
16      8     orientation  CompactOrientation
24      2     stateM       CompactState (ushort)
26      4     controls     CompactControlData
30      5     velocity     CompactVelocity
35      3     turnRates    CompactTurnRates
38      1     power        BytePercentage
= 39 bytes (comment says 33; stale)
```

### Alignment of the enclosing message structs (MSVC default packing)

The `FMD_*` structs are **not** packed; members align to their size (up to the
8-byte MSVC pack). Where a compact payload (alignment 4 — contains a float)
follows a small field, padding is inserted. Using `sizeof` semantics:

- `FMD_S_SET_CLUSTER`: `cbmsg(2)+fmid(2)+sectorID(2)` → pad 2 → `ServerSingleShipUpdate(47)` → `cookie(1)` → 56, 4-aligned → **FixedSize 56**.
- `FMD_C_SHIP_UPDATE`: `cbmsg(2)+fmid(2)+timeUpdate(4)` → `cookie(1)` → pad 3 → `ClientShipUpdate(39)` → 51, rounded to 4 → **FixedSize 52**.
- `FMD_S_SINGLE_SHIP_UPDATE`: `cbmsg(2)+fmid(2)+otTarget(2)+oidTarget(2)` → (8, already 4-aligned) `ServerSingleShipUpdate(47)` → `bIsRipcording(1)` → 56, 4-aligned → **FixedSize 56**.

None of the three messages has a variable item, so `cbmsg == FixedSize` and no
blobs are appended. `ObjectType` is a **`short`** (`igc.h:115`), so
`otTarget` is 2 bytes, not 4.

### Quantization conventions (must match the C++ exactly)

- `BytePercentage` (`Utility.h:1307`): Divisor = **240** (not 255). Store:
  `clamp(value*240 + 0.5)` to 0..240 (the `operator=` clamps, the ctor asserts).
  Retrieve: `byte / 240.0f`. Value semantics in `CompactShipFractions.power`.
- `CompactControlData` (`igc.h:2333`): per axis `char = floor(v*127 + 0.5)`;
  retrieve `char/127.0f`. Signed char written two's-complement.
- `CompactTurnRates` (`igc.h:2314`): per axis `char = floor(rate*127/(2π)+0.5)`;
  retrieve `char*(2π)/127`.
- `CompactVelocity` (`igc.h:2275`): `speed<1` → ushort 0 (components unused);
  else ushort = `(speed*8) | sign bits` (0x2000=x-,0x4000=y-,0x8000=z-) with
  `speed<1000` asserted; 3 component `BytePercentage` = `|v_i|/speed`.
  Retrieve: `speed = 0.125*(ushort&0x1fff)`; `v_i = comp * (±speed)`.
- `CompactOrientation` (`igc.h:2137`): `qa=ushort(a*65535+0.5)`,
  `qx/y/z=short(q*32767)` (truncation), a≥0 guaranteed by sign-normalisation of
  the quaternion; `Orientation↔Quaternion` port in `CompackOrientation.cs`.

## Implementation (Steps 2-5) — done

Files under `src/Allegiance.Core/Wire/`:
- `ServerShipUpdate.cs` — wire payload for `ServerSingleShipUpdate` (47 B).
- `ClientShipUpdate.cs` — wire payload for `ClientShipUpdate` (39 B).
- `Compact/Quant.cs` — quantization helpers (percent, control, turn rate, velocity, quaternion-orientation).
- `SetCluster.cs` (7), `ShipUpdate.cs` (24), `SingleShipUpdate.cs` (78).

Server (`src/Allegiance.Server/Server.cs`): on a validated logon it allocates a
`Ship` (id fountain from 8096; `ShipSpawner` seam is internal, default = first
core hull staggered by peer count, oriented straight ahead in the first sector),
`Mission.PlaceShip`s it, answers the handshake with the ship id, then sends
`SET_CLUSTER` (sector + full `ServerSingleShipUpdate`). Inbound `FM_C_SHIP_UPDATE`
only sets the peer's ship `Controls`/`StateM`. `BroadcastShipStates`, run after
`Mission.Update` in `SimPass`, sends the peer a `SINGLE_SHIP_UPDATE` when its
ship moved more than 1e-3 world units since the last broadcast. Both the control
write and the broadcast run on the **simulation thread** — the network reader
never touches the ship, so "only `Mission.Update` moves it" holds by
construction. `TickIndex` fills the payload's uint `Time` slot (so observations
are keyable to tick index, not seconds).

Bot (`src/Allegiance.Bot/`): `Bot.cs` (reusable client: connect → logon → wait
`SET_CLUSTER` → `CommandControls` → collect `SINGLE_SHIP_UPDATE` observations)
and `Program.cs` (console driver: full-forward thrust then a yaw, print the
trajectory).

## Verification — done (`tests/Allegiance.Core.Tests/BotFlightTests.cs`)

In-process server + real bots over loopback, all green:

1. **Trajectory match (⭐)** — the server's ship positions equal an
   **independent** `Ship.ExecuteMove` rollout computed by the test from the same
   hull/spawn/command/cadence, rolled forward per-tick. The reference re-reads
   the server's `dt` from `SimNow` and mirrors the controls the server applied
   each tick (constant after onset), so it is float-identical by construction;
   asserted within 1e-3. A passing match proves simulation, not echo — the bot's
   position echoes are all zero.
2. **Wire fidelity** — the bot's observed `SINGLE_SHIP_UPDATE` positions match
   the same independent rollout at their tick stamps.
3. **Zero thrust ⇒ no movement** — and no broadcast traffic (never dirty).
4. **Two bots** get different ships (distinct ids/spawns) and fly independently
   (A thrusts, B idles).
5. **Disconnect** — a bot leaving releases its ship from the broadcast set; the
   server keeps ticking and a fresh bot is still served.

Existing 1480 tests stay green → **1484 total**.

## Known gaps / notes (honest, not faked)

- **Ship clock on placement.** A `Ship`'s `Model._lastUpdate` starts at 0
  regardless of when it is placed, so a bot that logs on *long* after boot (say
  at sim-time 120 s) gets one oversized first `dt` on its very first sector tick.
  With neutral controls that step keeps it at rest (cruise targets speed 0), so
  the flight demo is unaffected; the real fix is seeding the model clock to the
  placement sim-time. Left as recorded, not silently corrected.
- **Observation time is tick index**, not seconds (uint `Time` slot). Distinct
  from `LogonAck.TimeServer` which is seconds-derived. Fine for the demo; a real
  sync implementation would reconcile them.
- The ship stays in its sector (and keeps flying) after its peer disconnects —
  it is just no longer broadcast. Desired for the "leave the server ticking"
  check; a production server would reap it.
- Everything the demo deliberately does not port (parts/loadout, ship-export
  object creation, cluster hopping, combat, time sync) is listed in the README's
  "What a real client would still need".
