# MULTISHIP_VISIBILITY_INVESTIGATION_P136.md

**Task:** P136 — Phase 5 multi-ship visibility: investigation + smallest real foundation slice.
**Branch:** `master` (top was `830e6a1` P135 at start). **Commit:** see git log (this task's head).
**Scope met:** investigation complete + codec foundation slice landed (wire-only, wired into nothing).

---

## 0. Headline conclusions

1. **The three structs are NOT "uninferable" — they are fully declared in the
   native reference.** `ServerLightShipUpdate`, `ServerHeavyShipUpdate` and
   `CompactShipFractions` have complete class declarations at
   `/home/domingo/Development/Allegiance/src/Igc/igc.h:2352-2442`. The
   scoping brief's claim that "no struct declaration anywhere in this
   checkout" is true of the **C# port checkout** (`AllegianceCore/src/`,
   where they were never ported) but NOT of the native source, which is
   authoritative here and used by the whole P13x arc. No usage-inference was
   needed; I reconstructed the layouts from the declarations and
   cross-checked them against all three cited usage families (server
   builder, IGC client decode, reference-client decode) for confirmation.
   This makes the task's "genuinely uninferable → investigation-only" branch
   moot — real code was the correct deliverable.

2. **Delta compression is load-bearing for the wire shape and was kept,
   not simplified.** `ServerHeavyShipUpdate` carries `CompactTime` (2 B) +
   `CompactPosition` (6 B) deltas relative to the message's
   `timeReference`/`positionReference`, not absolute time/vector. Because
   this port's established bar is byte-exact native fidelity (see §4) and
   real-client interop is in scope, the foundation slice implements the
   **faithful delta form**. The simplification the brief permitted is
   applied at the **builder/broadcast level**, not the wire: no deviation
   sort, no per-interval budget.

3. **The deviation-sorted bandwidth budget is a real, separable
   algorithm — investigated, not built.** Native `SendShipUpdates`
   (FedSrv.CPP:3662-4228) sorts eligible ships by prediction deviation and
   throttles heavy/light/turret updates to hardcoded counts (2/2/2,
   FedSrv.CPP:3844-3846). This is orthogonal to the wire format; the
   foundation slice substitutes "send every supplied ship" (cited
   simplification) and defers the budget to its own task (§7).

4. **Turret bundles are genuinely separate from P109.** The
   `rgActiveTurretUpdates`/`rgInactiveTurretUpdates` blobs relay *other*
   ships' turret states to observers as a server→client broadcast
   (`ServerActiveTurretUpdate`, igc.h:2478-2485, which has a `shipID` +
   `CompactTime` that P109's `ClientActiveTurretUpdate` lacks), so they do
   **not** reuse P109's codec. The slice carries both turret blobs as
   **opaque passthrough** (byte-exact round-trip, empty by default) and
   defers typing them to a dedicated turret-relay task.

5. **What was actually built** (foundation slice, mirroring P114/P131):
   the wire codecs for `LIGHT_SHIPS_UPDATE`(175) and `HEAVY_SHIPS_UPDATE`(176)
   with faithful field layouts, plus the two missing quant primitives
   (`CompactTime`, `CompactPosition`), and two isolated predicate round-trip
   tests. Nothing is wired into the sim loop or any broadcast.

---

## 1. Real field layouts (declared, cross-checked)

All three structs live in the `#pragma pack(push,1)` block of `igc.h`
(mirrored by this port as pack(1), no padding). Field order below is the
declaration order; byte offsets follow.

### 1.1 `CompactShipFractions` — igc.h:2352-2421 — **6 bytes**

```
offset 0  BytePercentage  m_bpHullFraction    //1
offset 1  BytePercentage  m_bpShieldFraction  //1
offset 2  BytePercentage  m_bpAmmo            //1
offset 3  BytePercentage  m_bpFuel            //1
offset 4  BytePercentage  m_bpEnergy          //1
offset 5  BytePercentage  m_bpOre             //1   (Xynth #156, 7/2010)
```
All six are div-240 `BytePercentage` slots (Utility.h:1307-1312), exactly
the convention this port already encodes in `ServerShipUpdate` (bytes 40-45).
Note the class comment still says "6 bytes" (igc.h:2352) but the heavy
record's per-field comment says fractions `//5` (igc.h:2440) — a stale
remnant from before `m_bpOre` was added; the true current size is 6.
Client consumer: `CshipIGC::ProcessFractions` (shipIGC.cpp:3056-3065), which
sets hull/shield/fuel/ammo/energy/ore from the six slots.

### 1.2 `ServerLightShipUpdate` — igc.h:2423-2429 — **8 bytes**

```
offset 0  short   shipID         //2
offset 2  ushort  stateM         //2   CompactState, igc.h:2258-2273
offset 4  [4]     controls       //4   CompactControlData, igc.h:2333-2350
```
The "cheap" per-ship payload: id + state bits + commanded controls only.
Client decode: `CshipIGC::ProcessShipUpdate(const ServerLightShipUpdate&)`
(shipIGC.cpp:3097-3102) reads `stateM` then `controls`; reference client
(appmsg.cpp:712-731) strides `sizeof(ServerLightShipUpdate)` per element and
asserts the id is not the receiver's own.

### 1.3 `ServerHeavyShipUpdate : ServerLightShipUpdate` — igc.h:2431-2442 — **39 bytes**

```
offset 0  short    shipID         //2   (inherited)
offset 2  ushort   stateM         //2   (inherited)
offset 4  [4]      controls       //4   (inherited)
offset 8  short    time           //2   CompactTime — delta from message's timeReference
offset 10 [6]      position       //6   CompactPosition — 3 deltas from positionReference
offset 16 [8]      orientation    //8   CompactOrientation, igc.h:2137
offset 24 [5]      velocity       //5   CompactVelocity, igc.h:2275
offset 29 [3]      turnRates      //3   CompactTurnRates, igc.h:2314
offset 32 [6]      fractions      //6   CompactShipFractions
offset 38 [1]      power          //1   BytePercentage
= 39 bytes (2+2+4+2+6+8+5+3+6+1)
```
The declared class comment says "38 bytes" (igc.h:2431) — again stale by the
1 extra byte of `m_bpOre`. True current width is 39. Distinguish from the
related-but-different `ServerSingleShipUpdate` (igc.h:2444-2455), which uses
**absolute** `Time`(4)+`Vector`(12) and is what this port's existing
`ServerShipUpdate` (47 B) implements. The difference is exactly the delta
compression (§3). Client decode:
`CshipIGC::ProcessShipUpdate(Time, Vector, const ServerHeavyShipUpdate&)`
(shipIGC.cpp:3104-3113+) exports `time` from `timeReference`
(:3111) and reads orientation/velocity/turnRates/power/fractions; reference
client (appmsg.cpp:736-800) handles the same.

### 1.4 Confirmation: the drone structs are distinct and dead
`CFSDrone::GetHeavyShipUpdate`/`GetLightShipUpdate` (fsship.cpp:1437-1449)
operate on `HeavyShipUpdate`/`LightShipUpdate` — **different** structs with no
`Server` prefix — and the entire pair is **commented out** (dead code). They
are unrelated to the `Server*` wire structs and were left untouched, as
scoped.

### 1.5 Cross-check summary
| Family | Source | Layout matches |
|---|---|---|
| server builder | FedSrv.CPP:3623-3655 (`AddUpdate`) | heavy fills shipID/time/position/controls/orientation/stateM/turnRates/velocity/power/fractions ✓ |
| IGC client | shipIGC.cpp:3056-3113 | light reads stateM+controls; heavy reads delta time, full state ✓ |
| reference client | appmsg.cpp:712-800 | strides match 8 B / 39 B; target fields read ✓ |

---

## 2. Message wire layouts (the two messages)

### 2.1 `LIGHT_SHIPS_UPDATE` (175) — Messages.h:890-893
Two var items, no scalar region:
```
offset  0  ushort cbmsg
offset  2  ushort fmid = 175
offset  4  ushort ib (rgInactiveTurretUpdates)
offset  6  ushort cb (rgInactiveTurretUpdates)   // packed ShipID shorts
offset  8  ushort ib (rgLightShipUpdates)
offset 10  ushort cb (rgLightShipUpdates)        // n × ServerLightShipUpdate(8)
offset 12  blob — inactive-turret ShipIDs, then light ship updates
= 12 + blob bytes
```
Builder ordering confirmed in FedSrv.CPP:4009-4024 (inactive turrets first,
then light ship updates; ib computed as running sums).

### 2.2 `HEAVY_SHIPS_UPDATE` (176) — Messages.h:895-905
Two var items + a 24-byte scalar region:
```
offset  0  ushort cbmsg
offset  2  ushort fmid = 176
offset  4  ushort ib (rgActiveTurretUpdates)
offset  6  ushort cb (rgActiveTurretUpdates)     // n × ServerActiveTurretUpdate(16)
offset  8  ushort ib (rgHeavyShipUpdates)
offset 10  ushort cb (rgHeavyShipUpdates)        // n × ServerHeavyShipUpdate(39)
offset 12  uint   timeReference                  // Time, 4
offset 16  Vector positionReference              // 12
offset 28  byte   bpTargetHull
offset 29  byte   bpTargetShield
offset 30  [6]    fractions                       // CompactShipFractions
offset 36  blob — active-turret updates, then heavy ship updates
= 36 + blob bytes
```
Scalar semantics (all per-recipient, FedSrv.CPP): `timeReference = g.timeNow`
(:3996); `positionReference` = the recipient's own source-ship position
(:4128-4133); `fractions` = the recipient's own source-ship fractions
(:4044-4047); `bpTargetHull`/`bpTargetShield` = the recipient's current
command-target hull/shield, `bpTargetHull == 255` meaning "no target"
(:4079-4125, read at appmsg.cpp:742).

---

## 3. Delta-vs-absolute: decision (kept faithful)

Native `ServerHeavyShipUpdate` compresses time and position as deltas from
per-recipient references:
- `CompactTime` (igc.h:2178-2196): `short` = `timeBase.clock() - timeNow.clock()`.
  Signed ⇒ ±32767 clock units (±~5.5 min at the 100 Hz clock). Invertible exactly.
- `CompactPosition` (igc.h:2198-2256): three `short`s, each under the
  nonlinear mapping `Displacement(delta)=delta²/±120²`, `Delta(disp)=±√|disp|·120`
  (igc.h:2203-2240). Inverts to ~0.5-unit accuracy out to 1000 units from the
  reference; zero delta inverts exactly.

**Decision: implement the faithful delta form, not a simplified absolute one.**
Rationale:
- This port's established bar is byte-exact native wire fidelity: P130/P133/P134
  preserved `pack(1)` layouts to the byte (see the 3-byte `ProbeState`,
  Messages.h:635; the 13-byte `AsteroidState`, Messages.h:917), `BytePercentage`
  is div-240 (Utility.h:1312), `ServerShipUpdate` is the exact 47-byte native
  shape. A heavy record that used absolute time/vector would be a *different,
  shorter wire format* than the real protocol.
- Real-client interoperability is in scope (P83 WINTREK_REAL_CLIENT); the real
  client's decoder (shipIGC.cpp:3104, appmsg.cpp:798) reads these as deltas.
- The layout is *declared*, not inferred, so there is no inference risk to pay
  for the simplification.

The two new primitives were added to `CompactQuant`
(src/.../Wire/Compact/Quant.cs): `EncodeTimeOffset`/`DecodeTimeOffset` and
`EncodePositionDelta`/`DecodePositionDelta`, each with the native formula in
the XML doc. The foundation slice proves them via round-trip (see §5).

**Where simplification IS applied:** the per-recipient customization. The
slice's `HeavyShipsUpdate.FromShips` uses one reference (the server time + a
caller-supplied position) for all entries and sets the target fields to
255/255 (no target) and fractions to full — these are broadcast-stage
policy (what to put in the scalar region per recipient), not wire-format
decisions, so carrying them unspecialized is honest.

---

## 4. Bandwidth-budget / deviation sort: decision (not built)

Native `SendShipUpdates` (FedSrv.CPP:3662-4228):
- Collects ships with a fresh update (:3751-3817), then picks heavy/light
  counts under an (historically bandwidth-derived, now hardcoded) budget of
  **2 heavy / 2 light / 2 turret** per interval (FedSrv.CPP:3844-3846;
  the original per-player `GetBandwidth()` patch is commented out at
  :3839-3843).
- When it can't send everything, it `qsort`s by prediction deviation —
  `CmpShipDeviation` (FedSrv.CPP:3614-3621), descending — and promotes the
  high-deviation ships to heavy, demoting others to light or nothing, with a
  random tie-breaker jitter (:3923-3951) so no ship starves.
- Heavy and light messages are assembled and the light one is even
  memcpy'd onto the end of the heavy one and sent as a single buffer
  (:4185-4189).

**Decision: do not build it in this task.** It is a traffic-shaping heuristic
that is (a) orthogonal to the wire format, (b) load-bearing only for
bandwidth-limited sectors (and partly disabled upstream), and (c) exactly the
kind of complexity this port's smaller-scale missions can drop. The
foundation slice's builders (`FromShips`) simply include every supplied ship —
a plain, honest substitution that produces valid messages. Restoring the
budget/sort is a listed follow-up task (§7) for anyone who needs it.

---

## 5. What was actually built (foundation slice)

Mirroring P114's and P131's foundation-only scope — **codecs only, wired into
nothing**.

| File | Contents |
|---|---|
| `src/Allegiance.Core/Wire/Compact/Quant.cs` | `+ CompactTime` (`EncodeTimeOffset`/`DecodeTimeOffset`) and `CompactPosition` (`EncodePositionDelta`/`DecodePositionDelta`) helpers, faithful to igc.h:2178/2198. |
| `src/Allegiance.Core/Wire/LightShipsUpdate.cs` | `LightShipUpdateEntry` (8 B, igc.h:2423) + `LightShipsUpdate` (175) codec with two var items and opaque inactive-turret blob; `FromShips`. |
| `src/Allegiance.Core/Wire/HeavyShipsUpdate.cs` | `HeavyShipUpdateEntry` (39 B, igc.h:2431) + `HeavyShipsUpdate` (176) codec with two var items + scalar region and opaque active-turret blob; `FromShips`. |
| `tests/Allegiance.Core.Tests/LightHeavyShipsUpdateCodecTests.cs` | 2 isolated predicate round-trip tests (exact wire layout + field recovery, incl. delta time/position). |

Design notes:
- `HeavyShipUpdateEntry` follows `ServerShipUpdate`'s proven "store the
  canonical bytes; every accessor re-derives" pattern, so encode→decode is a
  fixed point. Its `Time`/`Position` accessors take the enclosing message's
  references, matching native `ProcessShipUpdate`.
- Round-trip is exact for `CompactTime` (integer algebra) and for
  `CompactPosition` at zero delta; non-zero deltas round-trip within the
  native ±~0.5u accuracy, asserted in the test.
- Turret blobs are `byte[]` passthrough: byte-for-byte round-trip, populated
  empty by `FromShips`. Typed by the turret-relay task (§7).
- FedMessageDumper.cs already mapped 175/176 to their names, so no registry
  change was needed.

Verification: full suite green 3× consecutively at **2675** tests
(2673 prior + 2 new). See commit message for the run log.

---

## 6. Findings of note (evidence)

- **The scoping premise about missing declarations was based on the port, not
  the native source.** Worth recording so future tasks check the native
  reference (`~/Development/Allegiance/src/`) before assuming a struct needs
  usage-inference.
- **Struct size comments are stale in native.** Both `ServerHeavyShipUpdate`
  ("38", igc.h:2431) and its `fractions` field ("5", igc.h:2440) predate the
  `m_bpOre` addition (Xynth #156); the true current widths are 39 and 6. Same
  for `CompactShipFractions`' own comment (correct at 6).
- **`ServerActiveTurretUpdate` (16 B, igc.h:2478-2485)** = shipID +
  CompactTime + controls + orientation. It is the S→C observer-relay shape and
  is genuinely different from P109's 16-byte `ClientActiveTurretUpdate`
  (igc.h:2470-2476: full `Time`, no shipID). The port has only the latter;
  the relay is not P109's codec reused.
- **Heavy+light are concatenated into one UDP frame** natively (:4185-4189).
  This is a transport coalescing choice, not part of either codec; the
  slice encodes each message independently and leaves coalescing to the
  future broadcast task.

---

## 7. Concrete multi-task breakdown for the remaining Phase-5 work

Each is sized to this arc's one-task-one-feature pattern. None existed in the
port before P136.

1. **P137 — wire codecs for the turret relay.** Add `ServerActiveTurretUpdate`
   (16 B) and type the heavy message's `ActiveTurretBlob`; type the light
   message's `InactiveTurretBlob` as `ShipID[]`. Predicate round-trip tests.
   (Small; the layout is declared, igc.h:2478-2485.)
2. **P138 — visibility source for ships.** Extend the P132 cross-side sweep so
   foreign ships become per-side visibility targets (native sends *all* ships
   in a cluster, FedSrv.CPP:3680-3690; decide whether to honor strict side
   visibility or cluster-wide for this port). Establishes
   `Ship.SeenBySide`.
3. **P139 — broadcast wiring.** Add a periodic per-cluster/per-side
   `BroadcastShipsUpdate` that assembles `LIGHT_SHIPS_UPDATE` /
   `HEAVY_SHIPS_UPDATE` from the swept ships and sends them to the relevant
   connection group(s) — the same shape as P133-135's
   `BroadcastStationsUpdate` etc. Decide light-vs-heavy assignment policy
   (simplest: one heavy per ship; optionally reintroduce the budget).
4. **P140 — receive/decode on the peer.** Decode 175/176 in the receiving
   transport and apply updates to `Ship` instances (hull/shield/power,
   orientation, position via the delta references) — the counterpart to the
   native client `ProcessShipUpdate`.
5. **(optional) P141 — deviation budget.** Restore the deviation-sorted
   bandwidth throttle (FedSrv.CPP:3614, 3844-3951) if sector populations ever
   exceed what a naive send-all can fit; otherwise document it as intentionally
   dropped.

---

## 8. Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`
(see shell history / commit log for the id used).
