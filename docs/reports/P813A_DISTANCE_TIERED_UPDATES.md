# P813a — distance-tiered ship-update cadence on the server (EXPERIMENTAL)

Base: public master at dispatch `23002d99` (working tree of the PUBLIC AllegianceCore
clone). Branch `hermes/p813a`. Status: flag **default OFF**; changes left in the
working tree (no commit, no push). Report: `docs/reports/P813A_DISTANCE_TIERED_UPDATES.md`.

This task delivers a **third ship-update policy, `tiered`**, on top of the two existing
policies (`port` and `native`, `broadcast.shipUpdateModel`). The distance-tiered cadence
itself was built by **P677** (`broadcast.interestTiers`, default OFF — per-viewer
T0-full / T1-5Hz / T2-2Hz cadence, deviation-ranked inside each tier, HEAVY-only poses).
P813a's delta is two things P677 did **not** have:

1. **Big-change promotion** — a far (T1/T2) ship that has drifted past
   `broadcast.promotionEpsilon` since its last served pose is **promoted to a faster
   tier** (served at full rate) on that tick, so a manoeuvring distant ship is caught up
   **within one tick** and the client's dead reckoner never extrapolates further than the
   epsilon. Static far ships stay on their low cadence (the byte savings are preserved).
2. **Bounded per-tier telemetry** — `[update-age]` now reports promotions (count + /s) and
   served HEAVY **messages and record-bytes by tier**; per-tier max staleness was already
   reported (T0/T1/T2 p50/p95/max).

Everything is behind the flag; with `broadcast.interestTiers=false` the stream is
byte-identical to master (proven by the P677 saved-hash compatibility test, green in the
fast-tier run). New wire fields/messages are **out of scope** (brief §Out of scope) — the
tiered policy reuses the existing native HEAVY/LIGHT/SINGLE messages; promotion only
changes *which* far ships ride a given tick's HEAVY.

---

## How the data reaches the client (existing native field vs native message)

No new wire field and no new wire message was added. All remote-ship pose still travels
through the **existing native message `FM_S_HEAVY_SHIPS_UPDATE(176)`** and its
`ServerHeavyShipUpdate` record (the same fields native writes: relative position +
kinematics, `~/Development/Allegiance/src/Igc/igc.h:2423-2442`); LIGHT(175) remains
unused (P219 folds light into heavy; no LIGHT wiring), own-ship still rides
`FM_S_SINGLE_SHIP_UPDATE(78)`. The tiered policy only decides **which far ships are in
the current tick's HEAVY budget** and **how often** — the set of ships a peer may know
about (native visibility / `SeenBySide`) is unchanged, and one-shot events (fire, death,
dock) are never delayed (they ride their own immediate event wires, unchanged).

## Per-row native rule → port before/after → tests

| Native rule (cite) | Port before (master) | Port after (tiered ON) | Data reaches client via | Tests |
|---|---|---|---|---|
| Larger deviation has priority (`FedSrv/FedSrv.CPP:3614-3621`, sort `:3923`) | Budget path: sort candidates by `Deviation` desc, serve fixed heavy quota | Tiered path (P677): sort by tier, then `Deviation` desc inside each tier; P813a: promoted far ship still sorts by deviation within its tier | existing HEAVY(176) record | `InterestTierTests.Deviation_is_descending_within_tiers...` |
| Served deviation resets; skipped accumulate (`FedSrv.CPP:3937-3948`) | heavy-served reset to 0, skipped +1 | tier-served reset to 0, skipped +1; P813a: promotion resets on the promoted serve | HEAVY(176) | `P813aDistanceTierTests.*` |
| Fixed 2 heavy + 2 light quota, 1600 B/s budget (`FedSrv.CPP:3836,3844-3845`; bandwidth-adaptive sizing `:3841-3842` is dead code) | Density-scaled heavy quota (P632) | tiered policy (P677): a configurable pose **rate** per tier replaces the era's fixed quota (rate quotas, not a byte cap) | HEAVY(176) | `InterestTierTests.Target_and_near_enemies_are_full_rate_and_T2_meets_floor` |
| Command target occupies the first heavy slot (`FedSrv.CPP:4141-4146`) | target first heavy slot | unchanged (P677) | HEAVY(176) | `InterestTierTests.Target_and_near_enemies...` |
| Native client accepts a heavy update within ±1.5 s (`Igc/shipIGC.cpp:3113-3115`, `ProcessShipUpdate`) | port dead-reckoner `ExtrapolationCap=1.5 s` (`ShipDeadReckoner.cs:68`) | far ships served at lower cadence; P813a promotion caps positional drift to `promotionEpsilon` so extrapolation error stays bounded; P677 measured per-tier error (see below) | client `ShipDeadReckoner` (client-side, native behaviour) | `ShipDeadReckonerTests.Interest_tier_TCP_poses_bound_extrapolation_error` (6/6 pass) |
| Native visibility / cluster membership (`Sector.GetShipUpdateCandidates` P138; `FedSrv.CPP:4150-4165`) | peer sees cluster ships | unchanged — only cadence changes, never membership | HEAVY(176) | `P813aDistanceTierTests.*`, `InterestTierTests.*` |
| One-shot events (fire/death/dock) | immediate event wires | **never delayed** (unchanged, independent of tier cadence) | existing event messages (fire relay, death, docked) | covered by full suite |

**Tests (and which fail on master):** the flag is **default OFF**, so **no existing
master test fails with the flag off** — the flag-off stream is byte-identical to master
(P677 saved-hash compatibility test `BroadcastSnapshotLoopbackTests` in the compat set
passes, verified in the fast-tier run). The three **new** P813a tests
(`tests/Allegiance.Core.Tests/P813aDistanceTierTests.cs`) exercise the ON behaviour and
require the new code; on master they do not exist (the promotion code is absent), so they
are the only additions. They pass 3× alone.

## Design and classification

- **`broadcast.promotionEpsilon`** (new, default `200` world units; `0` disables): a far
  (T1/T2) ship whose pose has displaced ≥ this since its **last served pose** is
  promoted (full-rate send) that tick. Rationale: this is the "big change (turn/accel)"
  trigger; it **reuses the existing deviation ranking** (the promoted record sorts by
  deviation, and served ships reset their staleness accumulator). It also enforces a
  positional-max-staleness cap per far ship, which is what keeps the client's dead
  reckoner smooth at the low cadence.
- The existing tunables (radii `engagementRange` 1500 / `midDistanceRange` 6000,
  cadences `tier1Hz` 5 / `tier2Hz` 2, `minUpdateHz` floor) are documented in
  `ServerConfig.BroadcastConfig` and `docs/reports/P677_INTEREST_TIERS.md`.
- **Classification:** native's fixed 2+2 quota and 1600 B/s budget are **era
  constraints** (the bandwidth-adaptive sizing is dead code), not gameplay rules; the
  tiered policy spends a configurable pose rate per contact and keeps target priority +
  deviation order, so it is an experiment on top of the native message, not a parity
  claim. P813a's promotion is a **managed addition** (native has no such thing) and is
  flagged off by default.
- **Promotion implementation paths:** both the primary snapshot tier path
  (`BroadcastTierPose` → `ViewerPoseCadence.SelectTierCandidates`, used whenever the flag
  auto-enables immutable-snapshot + encode-once, i.e. production) and the legacy tier
  branch in `BroadcastShipsUpdatePeerLegacy` got the same promotion logic via a shared
  `ViewerPoseCadence.Promotable` / `MarkPosePosition`. `Promotable` returns false when
  `epsilonSq <= 0` so `promotionEpsilon=0` cleanly restores P677 behaviour in both paths.

## Bounded telemetry (new)

With tiers ON, `[update-age]` (rolling window, `Server.UpdateCadence.cs
TakeUpdateAgeReport`) now appends, beside the per-tier staleness bands
`T0/T1/T2 [n,p50,p95,max]`:

```
proms=[n=<count>,s=<per-sec>] msgs/Bps T0[m=..,b=..] T1[m=..,b=..] T2[m=..,b=..]
```

- **promotions/s** = big-change promotions in the window (a far ship force-served early).
- **msgs/Bps by tier** = served HEAVY record count and record-payload bytes by tier
  (`messages × HeavyShipUpdateEntry.Width`; the small per-message fixed header is not
  attributed to any tier). Counters are per-viewer, merged into the window on report and
  on disconnect-retire, and cleared each window — bounded.
- **max staleness per tier** = the existing T0/T1/T2 `max` (already present pre-P813a).

The promotion + per-tier message counters are only populated when the tier flag is on;
the flag-off report is unchanged (asserted by `Flag_off_emits_no_tier_telemetry...`).

## Verification (light gate)

Whole-solution build **0 warnings / 0 errors**; `tools/build-tools.sh` **73/73** tool
projects; **AOT publish**
(`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64
--self-contained -p:PublishAot=true`) produced the native binary with **0 IL2026/IL3050
warnings** (no reflection serialization; source-generated JSON). New tests **3× alone**
each (P813a 3/3 ×3, plus the affected InterestTier+UpdateCadence+P813a group **38/38
×3**), then **one fast-tier run**: **10,182 tests PASS**
(`TestResults/tiers-…/summary.json`; 3 optional Meshy-pack checks NotExecuted, as usual).

Loopback items from the brief's verification (all green):

| Brief item | Evidence |
|---|---|
| near ships every tick | `InterestTierTests.Target_and_near_enemies_are_full_rate_and_T2_meets_floor` |
| far ships at tier cadence | same test + `P813aDistanceTierTests.Far_ship_big_change...` (static far ship ≤8/40 serves at 10-tick cadence) |
| manoeuvring far ship promoted within one tick | `P813aDistanceTierTests.Far_ship_big_change_is_promoted_within_one_tick...` (moving far ship served every tick once it starts moving; static stays at cadence) |
| one-shot events immediate | unchanged immediate event wires; tier selection only shapes the HEAVY pose stream |
| flag OFF = byte-identical to today | P677 saved-hash compatibility test (green in fast tier) + `P813aDistanceTierTests.Flag_off_emits_no_tier_telemetry...` (no promotion/telemetry path with the flag off) |
| ShipDeadReckoner smooth at low cadence | `ShipDeadReckonerTests.Interest_tier_TCP_poses_bound_extrapolation_error` (6/6). P677 measured per-tier max position error: T2 (2 Hz) turn 1.632 m / reversal 5.043 m; T1 (5 Hz) turn 0.598 m / reversal 1.148 m — well under visual significance; P813a promotion additionally caps drift to `promotionEpsilon` (default 200 u), so far-ship extrapolation cannot grow unboundedly between serves |

## Local 50v50 benchmark (OFF vs ON)

Real loopback TCP, 100 seated peers (50v50) in one crowded sector, no weapons/AI,
positions advance each tick, recorded projection, P668_PEERS=100
(`Crowded_TCP_interest_tiers_reduce_bytes_and_report_ages`). Two modes: **OFF** =
snapshot+encode-once on, tiers off; **ON** = tiers on (incl. promotion, default ε=200).
Raw log: `~/allegiance-evidence/P813A/crowded50v50-p668-peers100.log`.

| Metric | OFF | ON | Δ |
|---|---|---|---|
| tick-wall mean / p95 / max | 8.81 / 10.74 / 13.72 ms | 6.89 / 7.56 / 7.79 ms | −21.8% mean |
| `broadcastShipsUpdate` mean / p95 / max | 6.23 / 8.19 / 10.26 ms | 5.32 / 5.86 / 6.01 ms | −14.6% mean |
| `broadcastShipStates` mean / p95 / max | 1.93 / 2.19 / 2.48 ms | 0.95 / 1.14 / 1.22 ms | −50.8% mean |
| egress enqueued B/s (send-pool) | 7,160,846 | 3,419,098 | −52.3% |
| per-peer egress B/s | 71,608 | 34,191 | −52.3% |

The crowded fixture's ships move only +1 u/tick, so promotion (ε=200) rarely fires and the
savings are the pure tier-cadence win; promotion engages only for real manoeuvres, which
is the desired behaviour. These are **local microbenchmarks**, not the rig's 200v200
decision.

## Rig recipe (for the 200v200 baseline, run by Claude on 221)

- **Flag:** `ALLEGIANCE_BROADCAST_TIERS=1` (or `broadcast.interestTiers=true`) for ON;
  unset for OFF. Optional: `broadcast.promotionEpsilon` (default 200), `tier1Hz`/`tier2Hz`,
  `engagementRange`/`midDistanceRange`.
- **Scenario:** `spawn_scenario` preset `showcase`, `target_sector` Alderan,
  `size_scale` 13, `mode: march`, native ship-update mode, bots on the M4 Max, fog on;
  two full matches unattended (game over → next match → game over), OFF vs ON.
- **What to measure (and expected direction with tiers ON):** ticks/5 s (should stay
  ≥100 / real time — do not let it drop), p95/max tick and **slow ticks** (↓ or flat),
  `broadcastShipStates` ms (↓), egress bytes/`enqueued-per-peer-bps` (↓),
  REFUSED/drops (flat or ↓), kills/min and bots-in-the-fight (flat), game-over handoff
  (flat), match-outcome spread (flat).
- **Which log lines show it:**
  - `[tickprof] window … tick-wall=…` and the `step:broadcastShipStates` /
    `step:broadcastShipsUpdate` profiler rows → tick cost.
  - `[egress] window: … enqueuedBytes=… enqueued-per-peer-bps=…` → bytes.
  - `[update-age] window=… T0/T1/T2[…max=…] proms=[n=…,s=…] msgs/Bps T0[…] T1[…] T2[…]`
    → per-tier staleness + promotions/s + per-tier messages/bytes (P813a bounded
    telemetry). Rising `T2…max` means a tier cadence too slow; rising `proms…s` means
    promotion working too hard (raise `promotionEpsilon`).

## Files changed (working tree, uncommitted)

- `src/Allegiance.Server/ServerConfig.cs` — `BroadcastConfig.PromotionEpsilon` (default 200).
- `src/Allegiance.Server/Server.InterestTiers.cs` — `_promotionEpsilonSq` + boot
  validation; `FormatTierTelemetry`; wire promotion + telemetry into `BroadcastTierPose`/
  `WriteTierRecord`.
- `src/Allegiance.Server/Server.UpdateCadence.cs` — `ViewerPoseCadence`: per-stamp
  last-served position, `Promotable` (epsilon-gated), `MarkPosePosition`, `Promotions` +
  `MessagesByTier` counters; `SelectTierCandidates` promotion; `TakeUpdateAgeReport`
  promotion + per-tier msgs/Bps emission; retire/init/shutdown aggregation.
- `src/Allegiance.Server/Server.Broadcast.cs` — legacy tier branch promotion + telemetry.
- `tests/Allegiance.Core.Tests/P813aDistanceTierTests.cs` — 3 new tests.

Unfinished / out of scope: the rig 200v200 baseline (Claude, per the brief); the
`tiered` flag is kept **OFF** by default until that decision; client-side smoothing is
P813b (out of scope); new wire fields/messages are out of scope.
