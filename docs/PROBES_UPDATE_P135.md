# P135 — PROBES_UPDATE(92) — per-side visible-probe sync

Commit: P135 — Wire/ProbesUpdate.cs codec + Server.BroadcastProbesUpdate: the
periodic per-side broadcast of every probe a side currently sees (its real
`Probe.SeenBySide`, already scored by the P132 sweep — no extension needed)
with its div-240 health fraction. The direct sibling of P133's STATIONS_UPDATE
and P134's ASTEROIDS_UPDATE, and the **third and last** of the three periodic
per-side syncs P131/P132 were built to unblock.

Branch: master (top was 5d236af = P134 before this change).

---

## 1. What this task builds

A new `PROBES_UPDATE`(92) codec and a per-tick, per-side broadcast that batch
every probe a side currently sees into one message and sends it to that side's
connections only — reusing the already-exposed P100 probe health
(`Probe.Fraction`) and P132's live visibility sweep. It is the exact
per-side batch-and-broadcast shape P133/P134 established, applied to probes —
and the simplest of the three, because a `ProbeState` is just an id + a health
fraction (`Messages.h:635-642`): no orientation, no ore, no shield. Every live
datum the message needs already existed in this port; nothing here is newly
derived, and (the headline finding) **the sweep needed no extension at all**.

Deliverables:

1. **Wire/ProbesUpdate.cs** — the `FM_VAR_ITEM(rgProbeStates)` codec: an entry
   per probe (`ProbesUpdateEntry`: `probeID` short + `bpFraction` byte = **3 B
   stride** under the struct's explicit `pack(1)` — the "Force the probe update
   structure to be packed to 3 bytes" comment at `Messages.h:635`) and the
   message (`ProbesUpdate`, fmid 92) with no fixed count field (count implicit
   in the blob length over the 3-byte element stride). `FromProbes` bakes the
   div-240 `CompactQuant.Percent` conversion and the field-semantics resolution
   in (§2a).
2. **Server.BroadcastProbesUpdate()** — called every `SimPass` (every tick)
   immediately after `BroadcastAsteroidsUpdate`, mirroring its exact shape:
   groups seated peers by `SideId`, gathers every probe in the mission that side
   currently sees (reading the live `Probe.SeenBySide` flags the sweep
   maintains — never re-deriving visibility), batches into one `ProbesUpdate`,
   and sends to that side's connections only, gated on `if (nProbes != 0)`
   exactly as the native snippet.
3. **Bot + 3 live loopback tests + 1 codec test** — per-side reception with
   correct id/fraction, cross-side absence (per-side batching), a real P100
   health change reflected in the next update, and a deterministic codec
   round-trip.

## 2. Findings the task asked to confirm (each read directly)

### 2a. `bpFraction` for a probe = the HEALTH fraction (same as P134's asteroids)

The message's own construction settles it: `pps->bpFraction = pprobe->GetFraction()`
(`fsship.cpp:877`), and `CprobeIGC::GetFraction()` simply returns `m_fraction`
(`probeigc.h:148-151`). `m_fraction` is the probe's **health pool**, written by
`CprobeIGC::ReceiveDamage` (`probeigc.h:92-119`; `m_fraction -= amount / maxHP`,
clamped to 0..1) — the direct-hit destructibility pool. A probe has no ore
concept at all, so this is unambiguous: `bpFraction` = health fraction, the same
conclusion P134 reached for asteroids and the same div-240 byte this arc
transmits for a body's fraction everywhere. Port analogue:
`Probe.Fraction` (`Probe.cs:160-173`, the P100-written pool, cited against
`probeigc.h:141` in the port — line numbers drifted slightly from the native
tree on disk, `probeigc.h:148-151`, substance identical). `FromProbes` sends
`CompactQuant.Percent(clamp(Probe.Fraction))`; an undamaged probe ships 240.

### 2b. P132's sweep ALREADY scores probes — no P134-style extension needed (the key contrast)

P134 found asteroids needed an explicit sweep extension (they are not `Model`s
and carry **no `SideId`**, so the own-side rule doesn't apply and they had to be
added as target-only participants). **Probes are different:** they are real
`Model`s AND carry a `SideId`. P132's `Sector.ResolveCrossSideVisibility`
already has a `case Probe p:` that adds the probe with `Add(p, p.SideId,
p.Position, p.ProbeType.Radius, p.ProbeType.ScannerRange, p.ProbeType.Signature)`
(`Sector.cs:2133-2136`) — so a probe joins both the target list and its own
side's scanner list. Consequences, both automatic and correct:

- **Own-side rule applies:** a probe is unconditionally visible to its own side
  (`Sector.cs:2177-2178`, `if (t.SideId is { } own) → visible(own, true)`), so a
  same-side probe needs **no scan** — always included for its own side
  (verification item 4).
- **Cross-side via scanner range:** a foreign side sees a probe only via one of
  that side's scanners in range with line of sight (`Sector.cs:2180-2203`) —
  the native `ImodelIGC::SeenBySide` model.

So the port needed **no change** to `Sector.cs` for this task — only the
`BroadcastProbesUpdate` reader over the already-populated flags. This is the
finding that sets P135 apart from P134.

### 2c. Probes-as-scanners: no double-count / self-visibility edge worth a test

Every probe is an unconditional scanner for its own side (P131's own finding,
`clusterIGC.h:219`; here the `Probe` case feeds `scannersBySide[ownSide]`).
Checking for trouble in the sweep: for a target probe, the cross-side loop
skips the target's own side (`if (sideId == t.SideId) continue;`, `Sector.cs:2182`)
— a probe is **never** scored by its own side's scanners, so its self-scanner
role cannot create a self-visibility artifact. And for the broadcast, the
per-side batch just filters `SeenBySide(sideId)`; it is indifferent to *how many*
scanners contributed, so a probe being simultaneously a scanner cannot double-
count itself or any same-side peer in the message. The one genuine behavioral
effect — a probe on side A can reveal an **enemy** probe to side A via scanner
range + LOS — is exactly the native semantic and is what item 2 (cross-side
absence) is built on. No extra test was warranted; the own-side-always and
cross-side-never cases (§3) cover the observable surface.

### 2d. Per-side send target — confirmed (same as STATIONS/ASTEROIDS_UPDATE)

The native constructs the batch per `pside` (`fsship.cpp:851-881`) and sends
`g.fm.SendMessages(GetConnection(), ...)` to that side's player connection
(`fsship.cpp:929`) — the message is per-side only, never whole-mission. The
port mirrors the exact selection `BroadcastStationsUpdate`/
`BroadcastAsteroidsUpdate` use: the seated peers whose `SideId` matches the
side whose visible-set was gathered. Probes are indexed by id and the message
carries no clusterID, so one combined per-side list across all sectors is
correct (the same rationale P133/P134 recorded).

## 3. Verification (no VM; all green)

Loopback `Bot`/in-process live tests in `tests/Allegiance.Core.Tests/ProbesUpdateLiveTests.cs`
(+ a deterministic codec test):

1. **Visible probe received with correct fields; absent from an unseeing side.**
   Side A deploys the probe (own side) → A's `PROBES_UPDATE` carries it with
   `probeID` and `bpFraction = 240`; the same-side teammate's connection also
   receives it (per-side fan-out). Side B (100 km away, past the probe's
   detection radius) receives **no** `PROBES_UPDATE` at all — the per-side
   batching proof.
2. **Same-side, no scan needed.** The probe is the deployer's own side's, so it
   is visible by the same-side rule alone (`Sector.cs:2177-2178`) — asserted
   `SeenBySide(own)==true` with no scanner required, i.e. always included for
   its own side.
3. **Health change reflected in the next update.** A real P100 direct-hit
   (`Probe.ReceiveDamage`, the same entry point combat collisions dispatch to,
   `Probe.cs:256-289`) drops the probe's `Fraction`; the next `PROBES_UPDATE`
   carries the div-240 encoding of the new value (strictly below 240).
4. Full suite: **2673 tests, 3 consecutive clean runs** (the 2670 P134 baseline
   + the 3 new tests). No `LiveChatSectorWireTests`/`LiveCombatWireTests` flakes
   fired in any of the three runs.

## 4. Does this close the three-update cluster?

Yes. P131/P132 unblocked three periodic per-side visibility syncs; P133 shipped
STATIONS_UPDATE(57), P134 shipped ASTEROIDS_UPDATE(179), and P135 ships
PROBES_UPDATE(92) — the last of the three. All three now flow every tick, per
side, gated on real `SeenBySide` from the single shared sweep.

With that, the natural **next** target on this same foundation is **Phase 5 —
multi-ship cross-side visibility**: the sweep and the per-side broadcast
machinery already produce per-side per-object visibility; the unexplored piece
is giving a *client* a coherent picture across multiple ships of one team
(shared detection/foefie awareness in the client view, versus today's per-ship
separation), which the P131/P132 per-side state and P133-P135 per-side messages
are precisely the substrate for.
