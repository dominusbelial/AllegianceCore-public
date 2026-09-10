# P133 — STATIONS_UPDATE(57) — per-side visible-station hull/shield sync

Commit: P133 — Wire/StationsUpdate.cs codec + Server.BroadcastStationsUpdate: the
periodic per-side broadcast of every station a side currently sees
(`Station.SeenBySide`, now real via P132) with its live P125 hull/shield
fractions. The first of the four consumers P131/P132 were built to unblock.

Branch: master (top was 207b18f = P132 before this change).

---

## 1. What this task builds

A new `STATIONS_UPDATE`(57) codec and a per-tick, per-side broadcast that batch
every station a side currently sees into one message and sends it to that side's
connections only — reusing the already-exposed P125 station fractions
(`Station.ArmorFraction`/`ShieldFraction`) and P132's live visibility sweep
(`Station.SeenBySide`). It is the exact `ASTEROID_MINED`(198)/P120 batch-and-
broadcast shape the task described, applied to a different object type.

Deliverables:

1. **Wire/StationsUpdate.cs** — the `FM_VAR_ITEM(rgStationStates)` codec: an
   entry per station (`StationsUpdateEntry`: `stationID` short +
   `bpHullFraction` byte + `bpShieldFraction` byte = 4 B stride) and the message
   (`StationsUpdate`, fmid 57) with no fixed count field (the count is implicit
   in the blob length over the 4-byte element stride). `FromStations` bakes the
   div-240 `CompactQuant.Percent` float→byte conversion in, the exact
   `BytePercentage` convention the port already uses for a body's fraction on
   the wire (`AsteroidMined.bpOreFraction`, `ServerShipUpdate.HullFraction`/
   `ShieldFraction`).
2. **Server.BroadcastStationsUpdate()** — called every `SimPass` (every tick)
   after `Mission.Update` (so the just-computed P132 sweep and the latest P125
   fractions are what get encoded). Groups seated peers by `SideId`, gathers
   every station in the mission that side currently sees (reading the live
   `Station.SeenBySide` flags the sweep maintains — never re-deriving
   visibility independently), batches into one `StationsUpdate`, and sends to
   that side's connections only, gated on `if (nStations != 0)` exactly as the
   native snippet.
3. **Bot + 3 live loopback tests** — per-side reception, cross-side absence,
   and fraction-change reflection, plus a deterministic codec round-trip.

## 2. Findings the task asked to confirm (each read directly)

### 2a. Fraction encoding — reused, not invented

`bpHullFraction`/`bpShieldFraction` are div-240 `BytePercentage`s via
`CompactQuant.Percent` (`Wire/Compact/Quant.cs:20-26`, divisor 240,
`Utility.h:1312`, clamps 0..240). This is the exact convention this port already
uses for a body's fraction on the wire — `AsteroidMined.BpOreFraction`
(`AsteroidMined.cs:59`) and the ship-update `HullFraction`/`ShieldFraction`
slots (`ServerShipUpdate.cs:122-123`). No new float→byte conversion is
invented; `StationsUpdate.FromStations` maps `Station.ArmorFraction` →
`bpHullFraction` and `Station.ShieldFraction` → `bpShieldFraction`
(`Station.cs:176,182`).

> NOTE on a red herring: `Station`'s class remark (Station.cs:77-83) that the
> native `Export` writes `BpHull`/`BpShield` as a lossy `(byte)m_hullFraction`
> cast concerns the static `DataStationIGC` **export record**, NOT the live
> `STATIONS_UPDATE` message — those bytes never seed the fractions (which start
> at 1.0f). The task's own direction ("confirm the BytePercentage encoding
> convention this port already uses elsewhere … and reuse it") governs the
> wire message, and div-240 is that convention.

### 2b. Per-side, per-cluster batching → this port batches per side across all sectors

Native builds `STATIONS_UPDATE` per cluster per side in a loop that also builds
`PROBES_UPDATE`/`ASTEROIDS_UPDATE` (`fsship.cpp:820-845`+). P126's own finding
doc confirms the finer shape: STATIONS_UPDATE is "built and sent **per ship**
inside the ship-status/update block and filtered by `pstation->SeenBySide(pside)`"
(STATION_CAPTURE_P126.md:165-169, citing `fsship.cpp:825,841`). Because
visibility is per-side (not per-ship), every ship on a side sees the same
station list, so batching **per side** — one message to that side's whole
connection group — carries identical content to native's per-ship sends and is
more efficient. The task sanctions this directly: the send target is "`pside`'s
own connection group," not whole-mission. This contrasts the whole-mission
audiences of `ASTEROID_MINED`(198)/`STATION_DESTROYED`(59)/`STATION_CAPTURE`(58).

The message carries no clusterID (native def `Messages.h:418-426` has only the
`rgStationStates` var item) and the client indexes stations by id, so this port
aggregates the per-side list across all sectors into one message — correct, and
the only sensible shape given the lack of a clusterID field.

### 2c. Gating — live sweep results, not re-derived

`BroadcastStationsUpdate` reads each `Station.SeenBySide(sideId)` flag — the
exact boolean P132's per-tick `Sector.ResolveCrossSideVisibility`
(`Sector.cs:2101-2189`) wrote into the station's `SideVisibility` state. The
server never recomputes distance/LOS itself; it consumes the sweep's output, the
same "query, don't re-derive" principle everywhere else in this arc.

### 2d. Trigger / cadence — PERIODIC per tick (the finding, stated plainly)

`STATIONS_UPDATE` is **periodic per native, not a one-time join-sync**. Native
emits it inside the periodic per-ship ship-status/update block
(`fsship.cpp:825,841`, per P126) — the same throttled cadence that drives
`ShipStatus`/the ship update block, distinct from the one-time join-sync bursts
that send `CREATE_BUCKETS`/`SET_MONEY`/`SET_BRIEFING_TEXT`.

This port implements it as a **per-tick** pass in `SimPass` (immediately after
`BroadcastShipStates`, the port's own per-tick analog of the native ship-status
block), following the convention P132 §3 already established and documented: the
port runs every existing `sector.Update`/broadcast pass **every tick** and
deliberately does **not** invent a new update-frequency mechanism. Because it
runs after `Mission.Update`, each tick's message reflects that tick's freshly
computed sweep and fractions — so a fraction change lands in the **next** tick's
message (verification point 3). The native every-N throttle is a cadence
optimization, not a semantic gate; the port's per-tick run is the conscious,
documented fidelity difference (same trade P132 made for the visibility sweep).

An empty side is suppressed (`visible.Length == 0 → continue`), matching the
native `if (nStations != 0)` guard in the quoted snippet.

## 3. Verification

No VM, no native client — real loopback `Bot`/in-process-server live tests in
`tests/Allegiance.Core.Tests/StationsUpdateLiveTests.cs`, built on the
3-bot / 2-side harness (A,B,A) with TWO far-apart synthetic stations (side-A base
at z = −20000, side-B base at z = +20000; all ships clustered ~20 km away; station
types `with`-modified with `ScannerRange = 0` so nothing cross-scans):

1. **Visible station received; unseeing side excluded.** Side A's `STATIONS_UPDATE`
   carries SA (id + full div-240 hull/shield = 240/240) and NOT SB; side B's carries
   SB and NOT SA — the per-side batching proof, no whole-mission leak.
   (`Visible_station_received_correct_fields_absent_from_unseeing_side`)
2. **Absence semantics** are subsumed into (1): a side not seeing a station gets an
   update with it absent (here, each side's update is exactly its own base — SB is
   absent from A's update, SA absent from B's).
3. **Fraction change reflected.** A real P125 `Station.ReceiveDamage` shield hit
   (cross-side launcher so the friendly-fire gate passes; absorbed =
   P·dtmShield/maxShield, survivable) lowers `ShieldFraction`; side A's **next**
   `STATIONS_UPDATE` carries the new div-240 shield byte (hull stays 240).
   (`Damaged_station_fraction_reflected_in_next_update`)
4. **Codec round-trip.** Multiple entries survive Encode/Decode with the exact wire
   layout (fmid 57, ib=8, cb=n×4, cbmsg, implicit count).
   (`StationsUpdate_codec_round_trips_multiple_entries`)

Baseline re-confirmed with `/home/domingo/.dotnet/dotnet test` (expected 2664
going in per the P132 checkpoint; the suite grew by +3 to 2667). Pre-existing
`LiveChatSectorWireTests`/`LiveCombatWireTests` timing flakes are unrelated to
this task — rerun if either fires (they are the documented ~1-in-8 pre-existing
flakes), and any run must clear 3 consecutive full-suite runs before commit.

## 4. Scope boundaries honoured

- `PROBES_UPDATE`(92)/`ASTEROIDS_UPDATE`(179) — separate tasks, now also
  unblocked by the same P131/P132 foundation (named in §5 as the concrete next
  steps). Untouched here.
- `STATION_CAPTURE`(58)/`STATION_DESTROYED`(59) — already landed (P126/P125), no
  changes. The whole-mission broadcast pattern they use was consciously NOT
  copied (this message is per-side).
- P103–P132 code reused, not modified: P125's `ArmorFraction`/`ShieldFraction`,
  P131/P132's `SeenBySide`/sweep.

## 5. Concrete, now-unblocked next tasks

- **PROBES_UPDATE (92)** — broadcast a side's probe visibility list, driven by
  each probe's live `SeenBySide`. Same per-side batched-shape as this task; a
  `Wire/ProbesUpdate.cs` codec + per-tick per-side send slot straight into
  `BroadcastStationsUpdate`'s pattern.
- **ASTEROIDS_UPDATE (179)** — broadcast per-side asteroid visibility/ore, driven
  by each asteroid's live `SeenBySide` (`Messages.h:926-928`), the same
  `FM_VAR_ITEM` batched form (see ASTEROID_MINED_P120.md:90-95).
- **Phase 5 — multi-ship cross-side visibility** — the client-facing fog-of-war
  exports that consume these states wholesale (CROSS_SIDE_VISIBILITY_SWEEP_P132.md §6).

## 6. Session stats

Logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260910_032535_92abc6`
(session still open at log time): msgs=124, tool_calls=70, api_calls=54,
tokens_in=4.7M, tokens_out=42.4k.
