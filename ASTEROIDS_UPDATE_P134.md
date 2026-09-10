# P134 — ASTEROIDS_UPDATE(179) — per-side visible-asteroid sync

Commit: P134 — Wire/AsteroidsUpdate.cs codec + Server.BroadcastAsteroidsUpdate: the
periodic per-side broadcast of every asteroid a side currently sees (its real
`Asteroid.SeenBySide`, now scored by the P132 sweep extended to asteroids) with
its raw ore amount and div-240 health fraction. The direct sibling of P133's
STATIONS_UPDATE and the second consumer P131/P132 were built to unblock.

Branch: master (top was 58ff415 = P133 before this change).

---

## 1. What this task builds

A new `ASTEROIDS_UPDATE`(179) codec and a per-tick, per-side broadcast that
batch every asteroid a side currently sees into one message and sends it to
that side's connections only — reusing the already-exposed P102 asteroid fields
(`Asteroid.OreRemaining`/`Fraction`) and P132's live visibility sweep. It is the
exact per-side batch-and-broadcast shape P133 established for stations, applied
to asteroids. Every live datum the message needs already existed in this port;
nothing here is newly derived except making the rock a genuine visibility target.

Deliverables:

1. **Wire/AsteroidsUpdate.cs** — the `FM_VAR_ITEM(rgAsteroidStates)` codec: an
   entry per asteroid (`AsteroidStateEntry`: `asteroidID` short + 8-byte
   `CompactOrientation co` + `ore` short + `bpFraction` byte = **13 B stride**
   under the struct's `pack(1)`) and the message (`AsteroidsUpdate`, fmid 179)
   with no fixed count field (count implicit in the blob length over the 13-byte
   element stride). `FromAsteroids` bakes the div-240 `CompactQuant.Percent`
   conversion and the two field-semantics resolutions in (§2a, §2b).
2. **Server.BroadcastAsteroidsUpdate()** — called every `SimPass` (every tick)
   immediately after `BroadcastStationsUpdate`, mirroring its exact shape:
   groups seated peers by `SideId`, gathers every asteroid in the mission that
   side currently sees (reading the live `Asteroid.SeenBySide` flags the sweep
   maintains — never re-deriving visibility), batches into one `AsteroidsUpdate`,
   and sends to that side's connections only, gated on `if (nAsteroids != 0)`
   exactly as the native snippet.
3. **P132 sweep extended to asteroids** — native gives a rock `c_mtSeenBySide`
   (`asteroidigc.cpp:36`) and scores it in the detection sweep like any model;
   the port's P132 sweep previously scored only ships/probes/stations. P134 makes
   `Asteroid` an `ISideVisible` (a `SideVisibilityState` + `SeenBySide`/
   `GetCurrentEye`) and adds `_mineable` rocks to the sweep's target list
   (`Sector.cs:2142-2156`). An asteroid has no `SideId`, so the own-side rule
   never applies — it is seen by a side only via one of that side's scanners in
   range with line of sight, exactly like a cross-side ship.
4. **Bot + 3 live loopback tests + 1 codec test** — per-side reception, cross-side
   absence, and ore-depletion reflection, plus a deterministic codec round-trip.

## 2. Findings the task asked to confirm (each read directly)

### 2a. `bpFraction` = the HEALTH fraction, NOT the ore fraction (the key finding)

The task flagged that `AsteroidState` carries *both* a raw `ore` short *and* a
`bpFraction`, and warned not to assume they are redundant. Reading the message's
own construction settles it — they are genuinely distinct:

- `pas->ore = short(pasteroid->GetOre())` (`fsship.cpp:909`), and
  `GetOre()` returns the rock's **current ore pool** `m_asteroidDef.ore`
  (`asteroidigc.h:187-190`) — the same pool mining drains (`MineOre`,
  `asteroidigc.h:221-233`). → port's `Asteroid.OreRemaining`.
- `pas->bpFraction = pasteroid->GetFraction()` (`fsship.cpp:911`), and
  `GetFraction()` returns `m_fraction` — the rock's **direct-hit damage/health
  pool** (`asteroidigc.h:172-174`, written by ReceiveDamage at `:143`/`:152`),
  independent of the ore pool. The ore fraction is a **separate** accessor,
  `GetOreFraction()` (`igc.h:4162`), transmitted only by `MineAsteroidEvent` /
  `ASTEROID_MINED`(198) — never by this message.

So `ore` is the raw remaining ore and `bpFraction` is the rock's current damage
fraction. The live test proves the non-redundancy end-to-end: as the miner drains
the rock, the `ore` short declines (610→605) while `bpFraction` stays 240/240
(mining does not damage the rock). `FromAsteroids` maps `OreRemaining`→`ore`
(clamped to short range, the native `short(...)` truncation) and
`Asteroid.Fraction`→`bpFraction` (via `CompactQuant.Percent`, div-240).

### 2b. `co` = identity (confirmed: this port's Asteroid has no orientation concept)

Native sends `pas->co.Set(pasteroid->GetOrientation())` (`fsship.cpp:910`), and
`CompactOrientation` (igc.h:2137-2176) is the 8-byte quantized quaternion
(`unsigned short qa` + `short qx/qy/qz`).

This port's runtime `Asteroid` deliberately carries **no** orientation/transform
(P102 kept it minimal — it is not a `Model`, has no live transform, no
`GetOrientation`). The static record's `Data.Rotation` is a native **axis-angle**
`Rotation` (Utility.h:515, axis+angle — not a quaternion), and reconstructing the
runtime orientation quaternion from it would require re-building the model's full
transform — "inventing a rotation system for one field," exactly what this arc
declines to do. P134 therefore sends the **identity** CompactOrientation (the
all-zero form, which native `Export` treats as "reset to identity,"
`igc.h:2155,2164-2165`), the same identity-`co` convention the ship-update
payload already ships (`ClientShipUpdate.cs:87`). Documented plainly; the test
asserts `co == CompactOrientation.Identity`.

### 2c. Packing — `#pragma pack(push, 1)`, stride 13 (not 14)

`AsteroidState` sits inside `#pragma pack(push, 1)` (`Messages.h:917`) with a
matching `pack(pop)` at `:925`. Every member is 1-byte aligned, so the layout is
the raw field-sum with no tail rounding:

    offset 0:  short asteroidID
    offset 2:  ushort co.qa
    offset 4:  short  co.qx
    offset 6:  short  co.qy
    offset 8:  short  co.qz
    offset 10: short  ore
    offset 12: byte   bpFraction
    = 13 bytes, no alignment gaps

Under default MSVC alignment the max member alignment would be 2 (the shorts),
rounding the total to 14; the `pack(1)` is what collapses it to 13, and the
client reads `n * sizeof(AsteroidState)` = 13 per element (`appmsg.cpp:607-612`).
This is the same non-default-packing discipline P130/P133 flagged for this
message family. The codec test pins the 13-byte stride (`cb = 3 × 13 = 39`,
total 47 B).

### 2d. Per-side send target (P133's pattern), not the whole-mission events

Native builds `ASTEROIDS_UPDATE` in the same per-cluster-per-side block as
`STATIONS_UPDATE`/`PROBES_UPDATE` (`fsship.cpp:883-915`, the sibling of
`:817-849`), filtered by `pasteroid->SeenBySide(pside)` and addressed to pside's
own connection group. P134 mirrors `BroadcastStationsUpdate` exactly: per-side
batching to that side's seated peers only, gated on `nAsteroids != 0`. This
contrasts the whole-mission audiences of the event-family broadcasts
`ASTEROID_MINED`(198)/`ASTEROID_DESTROYED`(86) — a genuinely different message
family (periodic per-side sync vs. event-driven whole-mission announcement), kept
separate per the task's explicit boundary.

The message carries no clusterID (native def `Messages.h:926-928` has only the
`rgAsteroidStates` var item) and the client indexes asteroids by id, so the port
aggregates the per-side list across all sectors into one message — correct, the
identical rationale P133 recorded for `STATIONS_UPDATE`.

### 2e. Cadence — periodic per tick (P133's convention)

`ASTEROIDS_UPDATE` is periodic per native, not a one-time join-sync. Following
the P132 §3 / P133 precedent, this port runs the broadcast every `SimPass`
(every tick) after `Mission.Update`, so each message reflects that tick's fresh
sweep and the latest ore/health — an ore change lands in the **next** tick's
message (verification point 3). Empty sides are suppressed (`visible.Length == 0
→ continue`), matching the native `if (nAsteroids != 0)` guard.

## 3. Verification

No VM, no native client — real loopback `Bot`/in-process-server live tests in
`tests/Allegiance.Core.Tests/AsteroidsUpdateLiveTests.cs`, built on P133/P120's
3-bot / 2-side harness with ONE synthetic He3 rock (id 6721) at the origin (the
demo map has zero asteroids, so it is the only rock in the universe), a
scanner-capable hull, side A's miner parked on the rock, side B's only scanner
30 km away (never in detection range), and a side-A teammate nearby:

1. **Visible asteroid received; unseeing side excluded.** Side A's
   `ASTEROIDS_UPDATE` carries the rock with the correct `asteroidID`, `ore`
   within the live pool bounds, `bpFraction` 240 (undamaged rock), and identity
   `co`. Side B (30 km away, sees nothing) receives **no** `ASTEROIDS_UPDATE` at
   all — the per-side batching proof, no whole-mission leak.
   (`Visible_asteroid_received_correct_fields_absent_from_unseeing_side`)
2. **Absence semantics** are subsumed into (1): B's side never sees the rock, so
   it never appears in any B update — the same-asteroid-not-visible-to-another-
   side requirement.
3. **Ore change reflected; health fraction stable.** Real P102/P55 mining
   (side-A miner parked on the rock drains ~5/tick; regen 0.025 is negligible)
   depresses the `ore` short in the **next** `ASTEROIDS_UPDATE` while
   `bpFraction` stays 240 — the live non-redundancy proof of §2a.
   (`Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full`)
4. **Codec round-trip.** Multiple entries survive Encode/Decode with the exact
   wire layout (fmid 179, ib=8, cb=n×13, cbmsg, implicit count, 13-byte stride).
   (`AsteroidsUpdate_codec_round_trips_multiple_entries`)

Actual observed output:

    P134-VISIBLE: rock 6721 (ore 610/620, bp 240/240, co identity) -> A's ASTEROIDS_UPDATE(179); B gets none [PER SIDE]
    P134-ORE: rock 6721 ore 610->605 (of 620) as the miner drains it; bpFraction stays 240/240 [ore != bpFraction CONFIRMED]
    P134-CODEC: ASTEROIDS_UPDATE(179) 3 entries, wire 47 B, stride 13, round-tripped

Baseline re-confirmed with `/home/domingo/.dotnet/dotnet test`: the suite grew
by **+3** (2 live + 1 codec; the sweep-extension added no new test in
CrossSideVisibilitySweepTests because the existing tests already pass with the
asteroid-target change — a pure superset) to **2670**, and three consecutive full
runs cleared clean (2670/2670, no `LiveChatSectorWireTests`/`LiveCombatWireTests`
timing flakes fired).

## 4. Scope boundaries honoured

- `PROBES_UPDATE`(92) — separate, now also unblocked by the same P131/P132
  foundation; the last of the three periodic syncs, named in §5 as the concrete
  next task. Untouched here.
- `ASTEROID_MINED`(198)/`ASTEROID_DESTROYED`(86) — already landed (P102/P120),
  a different (whole-mission event) message family; no changes.
- P103–P133 code reused; the only change beyond reuse is the minimal P132-sweep
  extension that makes asteroids genuine visibility targets (required for the
  task's per-side premise; native gives rocks `c_mtSeenBySide`). Everything else
  reads already-exposed helpers (`Asteroid.OreRemaining`/`Fraction`,
  `BroadcastStationsUpdate`'s pattern as a direct template).

## 5. Concrete, now-unblocked next task

- **PROBES_UPDATE (92)** — the last of the three periodic per-side syncs the
  P131/P132 foundation unblocked (with STATIONS_UPDATE/P133 and
  ASTEROIDS_UPDATE/P134). Broadcast a side's probe visibility list, driven by
  each probe's live `SeenBySide` (`Messages.h:863-881` for the native batch,
  `ProbeState`: `probeID` short + `bpFraction` byte). Straightforward: a
  `Wire/ProbesUpdate.cs` codec (one ib/cb var pair, count implicit in blob/stride,
  `Probe.Fraction` div-240) + a per-tick per-side send slot cloned from
  `BroadcastAsteroidsUpdate`'s exact shape.

## 6. Session stats

Logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260910_035259_655adb`
(session still open at log time): msgs=170, tool_calls=97, api_calls=73,
tokens_in=7.8M, tokens_out=60.5k.
