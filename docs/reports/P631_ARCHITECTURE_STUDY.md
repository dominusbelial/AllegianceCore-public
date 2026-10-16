# P631 — Server Architecture Study (summary, measurements, phase tasks)

> Companion design doc: `docs/architecture/SERVER_PIPELINE.md` (full design,
> cost model, native-fidelity classification, Rust readiness).
> Status: study + design + phased plan; **no production code landed**. Committed
> on `master`, not pushed. No 221 access; all numbers below are from prior
> measured runs on disk (timelines + reports) and the source, reproduced as
> commands in §Reproduction of the design doc.

---

## 1. TL;DR

The 200v200 baseline is bound by **two different cliffs**, and the plan targets
both:

- **Distributed (Cliff A, broadcast-bound):** simulation is ~3–4% of the tick
  (P600); the cost is per-peer personalised encode — `step:broadcastShipStates`
  hit 17.35 ms at 391 players in `port` mode and per-player egress 239 kB/s
  (P604); `crossSideExportSweep` hit 461 ms in combat.
- **Crowded single sector (Cliff B, sim-bound — the real 200v200 limit,
  clash38):** ~387 bots in one sector ⇒ `step:update` ~35 ms mean / p95 56–63 ms
  (dominated by `sector:1052`), tick ~50–65 ms, 74–86 ticks/5 s for 16 minutes.
  Here the lever is **intra-sector parallel simulation** (which P600 correctly
  deprioritised *only* because no sector was saturated at that sample).

The target shape: **sim/publish/send split on an immutable per-tick snapshot,
encode-once fan-out per visibility group, interest tiers (T0/T1/T2) with client
dead-reckoning, acked delta baselines, reliable/unreliable lane separation,
per-sector parallel sim with warp hand-off** — see the design doc. Flags with A/B
and rollback per phase.

---

## 2. Measurements (what today's runs actually showed)

All figures from the timeline files in `~/allegiance-metrics-ab-2026-10-11/`
(with `marks.txt` legend) and `docs/reports/{P604,P619,P600}`.

### 2.1 Ship-state model and egress at ~391 players (P604:16-23)

| metric | ~121 | ~225 | ~391 |
|---|---|---|---|
| `step:broadcastShipStates` ms — port | 1.48 | 5.24 | 17.35 |
| `step:broadcastShipStates` ms — native | 0.68 | 1.56 | 3.26 |
| tick-wall mean ms — port | 3.78 | 9.69 | 25.11 |
| tick-wall mean ms — native | 2.98 | 6.66 | 13.98 |
| egress per player kB/s — port | 76.9 | 149.9 | 239.3 |
| egress per player kB/s — native | 11.1 | 12.8 | 16.6 |

`port` = today's per-tick teammate `posBatch` stream; `native` = P604's
deviation-ranked HEAVY/LIGHT budget (~1.6 kB/s ship-state budget), which cut
`broadcastShipStates` −81% and egress −93%, but the non-ship per-side syncs
(stations/asteroids/probes/missiles/exports/status/clusterinfo) are identical in
both modes.

### 2.2 GC / allocation / stalls (P619)

| run (392 players) | ticks/5 s | tick p95 | tick max/window | gen2/window |
|---|---|---|---|---|
| clash12 native, workstation GC | ~41 | 74 ms | 2.2–2.4 s | 1–2 |
| clash13 port, workstation GC | 16–34 | 1.2–1.8 s | 1.2–1.8 s | 54–67 |
| clash14 port, server GC | (recovers) | 65–80 ms | ~2 s once/100 ticks | — |

Server GC fixes the general p95 but **one ~2 s tick per 100-tick window
survives both modes + server GC**. `[slowtick]` attribution names it: a
**synchronous inbound-receive burst in `step:drain`** (captured 432 ms;
`unaccounted≈0, pause+0` → not GC, not broadcast). Likely hundreds of bot ACKs
aligned to the 100/40-tick batching arriving together and drained serially.
This is a receive-path, not send-path, problem → addressed by Ph.5 (and eased by
tiers/deltas reducing inbound decode on the bot side, P636d).

### 2.3 Crowded single sector — the real 200v200 limit (clash38, 2026-10-14)

- Below 20 Hz for 16 min (11:30–11:46): 74–86 ticks/5 s, tick mean 50–65 ms
  while ~387 bots fought in one sector.
- Profiler window 16:30:22Z: `step:update` mean 35 ms / p95 56–63 ms, almost
  entirely `sector:1052` (Alderan); `sim.combat` 8–9 ms; `broadcastShipsUpdate`
  8–9 ms.
- Consequence: `sim.combat` at 8–9 ms is a small fraction of `step:update`; the
  rest of the sector update (movement/nav/collision/other per-object passes) is
  where the time goes → parallelising *within* the sector (spatial partition +
  deterministic merge) plus cutting per-ship cost is the lever, not just
  parallelising `sim.combat`.

### 2.4 Top costs after P619c/P621 (answered, scope-1 deliverable)

Named top costs on the 200v200 shape, in order of measured impact at the target
scale:

1. **`step:update` in one saturated sector** — 35 ms mean / p95 56–63 ms
   (clash38); the crowd concentrates all of combat+collision+nav in one sector.
   (P621 convex-hull collisions and P621b broadphase already cut the per-pair
   cost; the residual is the O(ships×neighbours) loop.)
2. **Per-peer personalised encode** — `step:broadcastShipStates` 17.35 ms port /
   serial parts of the sweeps; `crossSideExportSweep` up to 461 ms in combat on
   the earlier (pre-P619c) runs.
3. **Inbound `step:drain` burst** — the ~2 s aligned stall (P619), separate from
   GC.
4. **Managed allocation/GC** under port mode (clash13: 54–67 gen2/window,
   workstation GC) — eliminated by server GC + alloc cuts (P619) and further
   shrinkable by encode-once fan-out (shared buffers) and (eventually) a native
   sim core.
5. **Bot-side decode/publish cost** (P632–P636d) — co-determines perceived
   lethality at scale; tiers + deltas reduce it (fewer entities/decodes/client).

### 2.5 Bot-side / snapshot-lethality findings folded in (P632–P636d)

- Port-mode ~390 bots in one process only decided every ~30 s (decode + GC +
  per-bot world-lock contention, ~9 cores, ~800 threads on the M4 Max) — they
  never reached the fight (P632/P636).
- P636's lock-free decision snapshot fixed decision rate but **cost combat at
  scale**: P636c OFF ≡ ON (both ~3.3 cores, ~140 ms pass p95) proved the cost
  was the **receive-side publish** (~390×25 Hz×400 ships ≈ 4M decode-equiv/s),
  not the read; P636d turned publishing off (off CPU 2.4 vs 3.3 cores, pass p95
  105 vs 140 ms) and made `off` a strict no-op — **defaults off**. ON's snapshot
  also added aim staleness (snapAge p95 167–179 ms); P636c's dead-reckon-to-now
  addressed the staleness axis, P636d the publish cost axis.
- Design implication: a scalable per-client view (interest tiers + deltas) cuts
  each client's decode cost, so both the real client and the bot host benefit;
  and "sharing one simulation" (P447/P448, `Allegiance.Core`) is what lets a
  client dead-reckon between sparse tier updates without a divergent local sim.

---

## 3. The design at a glance

Full detail: `docs/architecture/SERVER_PIPELINE.md`. Headlines:

- **(a) Sim/publish/send split:** sim thread builds an immutable per-tick
  snapshot and stops encoding/sending; publish workers encode off the sim
  thread; network threads only send.
- **(b) Encode-once fan-out:** per (sector × visibility-group × tier) shared
  encoded buffer; clients hold buffer refs; O(G) encodes + O(N) refs instead of
  O(N×V) encodes (mirrors xorgy `ArenaEncoder`).
- **(c) Interest tiers:** T0 target/near full-rate, T1 mid reduced-rate reduced
  fields, T2 far summaries — keeps native's deviation *intent* (target always
  shown; spend on the diverged) but drops the fixed 2+2 heavy/light *mechanism*
  (era constraint).
- **(d) Delta compression** against per-client acked baselines; repair delta for
  laggers; bounded revision ring.
- **(e) Transport:** keep TCP default for LAN phases; target UDP lanes via
  GameNetworkingSockets (BSD-3; C# wrappers listed) with strict
  reliable/unreliable lane separation (their documented failing case); QUIC =
  secondary; encryption/identity config-gated, mandatory before public exposure.
- **(f) Per-sector parallel sim + warp hand-off** for the crowded-sector cliff;
  deterministic merge preserved.
- **(g) Client:** tiers only safe because the client dead-recks (P448a/b); keep
  ONE shared simulation.
- **(h) Native-faithful vs implementation:** gameplay/visibility semantics kept;
  era budgets replaced; per-peer encode + one-serial-tick replaced.
- **(i) Rust:** start with a shared sim-core crate + C ABI under the C# core,
  A/B for byte-equal output; orchestration/networking rewrite later.

---

## 4. Phased plan (ready-to-scope tasks)

Each phase = a delegatable task (12–28 min, split as needed) behind a flag whose
default is the pre-phase behaviour (byte-for-byte, like P603/P604), with an A/B
on 221 and a rollback flag. All phases measure the 200v200 baseline locally
(`showcase`, `size_scale 13`, Alderan, staging on) before contact and in
combat, and must pass the live bot gate (leave home → reach Alderan → kills > 0
at 30v30) for anything touching broadcast/transport/wire.

| Ph | Flag | Change | Acceptance metric (target) | Rollback |
|---|---|---|---|---|
| 0 | — | Pin baseline (this study) | reproducible A-side | — |
| 1 | `broadcast.immutableSnapshot` | sim/publish split: snapshot + encode off sim thread, byte-identical | `step:broadcastShipStates`/`crossSideExportSweep` leave sim tick; sim tick → sim-only floor | flag off |
| 2 | `broadcast.encodeOnce` | encode-once fan-out for ship-state | per-peer encode O(refs); broadcast step collapse; egress byte-identical first | flag off |
| 3 | `broadcast.interestTiers` | T0/T1/T2 + client dead-reckon between tiers | egress ≤ ~17 kB/s at 390; kills/5 s ≈ baseline (T0 keeps targets); real-client smooth (backbuffer proof) | flag off |
| 4 | `broadcast.deltas` | acked delta baselines + repair deltas | steady-state ship bytes << full-state; no repair storms | flag off |
| 5 | `receive.decouple` | fix the ~2 s aligned inbound `step:drain` burst (P619) | `[slowtick]` drain stalls gone; p95 stable under server GC | flag off |
| 6 | `sim.parallelSectorCells` | intra-sector spatial partition + deterministic merge | 387-bot one-sector `step:update` 35 ms → ≤15 ms; determinism A/B passes | flag off |
| 7 | `sim.perSectorThreads` | per-sector threads + warp hand-off | 1,024 multi-sector tick ≥ 40 Hz | flag off |
| 8 | `transport.lanes` | UDP lanes via GNS (LAN-safe default off) | 1,024 enc conns; unreliable lane never delayed by reliable bursts; server p99 near sim floor | default TPP TCP |

**Recommended order:** Ph.1 → 2 → 5 → 3 → 4 → 6 → 7 → 8. Rationale: 1–2 and 5
attack Cliff A + the measured stall with near-zero behavioural risk and
compound; 3–4 add the egress win native already showed is possible; 6–7 attack
Cliff B (the true 200v200 single-sector limit); 8 and the Rust core (design §4.i)
are the furthest horizon. The Rust sim-core crate is explicitly **not** a first
step — land the architecture in C#, then port the deterministic sim core under
it behind a byte-equality A/B.

---

## 5. Verification status

- Design + report committed on `master` (not pushed), docs-only — no build/test
  delta, so the `scripts/run-tests.sh` 2× suite gate does not apply to a
  code-free docs change. Measured numbers above are quoted from the cited
  reports/timelines (each verifiable by the reproduction commands in the design
  doc §7); no numbers were invented.
- Native citations used: `FedSrv.CPP:3614-3621,3742-4196,3844-3846,3870,3923,
  3937-3950,4075-4125,4141-4146,4127-4133,4189` (budget + deviation),
  `fsship.h:65-66` (deviation accumulator).
- Not done here (explicitly out of scope): production code, Rust code, touching
  221, live 390-bot run (requires the rig workflow; the A/B phases will run it).
