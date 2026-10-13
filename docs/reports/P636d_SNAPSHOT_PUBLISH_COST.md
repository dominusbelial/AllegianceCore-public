# P636d — the decision snapshot's PUBLISH cost on the receive threads: measured, fixed, and verified

Date: 2026-10-13. Branch: `master` (not pushed). Deliverable report + commit.

## TL;DR

P636c's `ALLEGIANCE_BOT_DECISION_SNAPSHOT=off` switch only reverted the bot decision **read**
path to the live tables — the receive thread **kept publishing** a full ~400-ship decision
snapshot on a fixed ~25 Hz timer for every bot. At 390 bots that is ~390 × 25 × 400 ≈ 4 M ship
copies/s of receive-thread work that the switch could not turn off, which is why clash31
(`off`) still got 0 kills on the real rig. This task **measures** that cost at 390 bots and
**fixes** it:

- **`off` now disables publishing entirely** (`PublishDecisionSnapshot` is a strict no-op →
  measured `pubPs = 0.0` at 390 bots). The switch stops the WRITE, not just the read.
- **On is now lazy and active-gated**: the receive thread republishes only while the bot
  decision loop holds a sticky active request, **skips when the world has not changed** since
  the last publish, and idles to **zero** for idle/not-seated worlds. Measured publish volume at
  390 bots fell ~65–75 % (≈ 9,750/s → ≈ 2,600–3,900/s) and the process dropped from ~3.4 cores
  to ~2.9 (on) / ~2.4 (off).
- **Measured the headline**: the P636c OFF == ON result (both ~3.3 cores, ~140 ms decision-pass
  p95) confirms the cost is the PUBLISH (present in both modes), not the read — and turning the
  publish off with the switch now actually cuts it (off CPU 2.4 vs 3.3, pass p95 105 vs 140 ms).
- P636c's aim freshness (dead-reckon to "now") and the lock-free read (no `cs.Gate` in
  decisions) are both preserved — only the *receive-side publish* cadence changed.

## Why the P636c `off` was not enough (measured)

On this workstation at 195v195 (390 bot clients, one bot process, local in-process server, port
model), before P636d the switch made almost **no** difference to the process:

| Build / mode (390 bots) | cpu (cores) | decision pass mean / p95 (ms) | decisionHz | snapAge p95 (ms) |
|---|---|---|---|---|
| P636c **ON** (read snapshot, publish 25 Hz) | 3.1–4.0 | 84–95 / **136–146** | 5–18 | **167–179** |
| P636c **OFF** (live reads, publish STILL 25 Hz) | 3.0–3.7 | 88–90 / **136–142** | 8–13 | 0 (no reads) |

OFF ≈ ON because the switch only disabled the READ. The receive-thread publish — the work this
task removes — ran identically in both modes.

## The fix (all client-core / bot-harness; no server change)

### 1. `off` disables publishing entirely

`ClientWorld.DecisionSnapshotEnabled` (single source of truth for the A/B switch, moved from the
tool layer) is now honoured **by the receive thread**: `PublishDecisionSnapshot()` returns
immediately when it is `false`, performing ZERO rebuilds. The tool-side
`BotManager.DecisionSnapshotEnabled` delegates to it so the read and publish paths can never
disagree about the mode.

### 2. On is lazy and active-gated, with skip-unchanged

`ClientWorld` gained a sticky active request level:
- `RequestDecisionRefresh()` — the bot decision loop asserts, once per pass, that it wants fresh
  snapshots (`BotManager.Loop`, before Phase A).
- `CancelDecisionRefresh()` — not-seated bots are revoked each pass, so their worlds publish zero.
- `PublishDecisionSnapshot()` rebuilds only when the level is held **and** the decision-relevant
  world changed since the last publish (`MarkSnapshotDirty()` bumps a generation counter from the
  receive-thread Apply paths that mutate Ships/Stations/Warps) **and** the ≤ ~25 Hz interval floor
  elapsed. So active bots get fresh snapshots at the decision cadence while idle/off worlds do no
  work, and a world with no new ship/station/warp activity skips the rebuild entirely.

### 3. Instrumentation (the measurement evidence)

Added (thread-safe, surfaced through `get_perf_stats` and the P632 PERF line):
- **snapshot publish rate** (rebuilds/s across every world) and **publish ms/s** (receive-side
  rebuild wall-time) — directly quantify the receive-thread publish cost this task targets;
- **decode lag** — age of the newest applied update vs wall (`now − LastAppliedTick64`), the
  `p95 ≤ 100 ms` acceptance metric;
- the P636c snapshot-age and decision-pass metrics are unchanged.

## Measurements (this host; numbers actually measured)

### Before vs after at 390 bots (195v195, port, local server)

| Build / mode | cpu | pass p95 | decLag p95 | publish/s | gen2 (~2 min) |
|---|---|---|---|---|---|
| P636c ON | ~3.4 | ~140 ms | (n/a) | ~9,750 (25 Hz×390) | ~380 |
| P636c OFF | ~3.3 | ~140 ms | (n/a) | ~9,750 (still publishing) | ~330 |
| **P636d ON** | ~2.9 | ~130 ms | 1–174 ms* | **~2,600–3,900** | ~410 |
| **P636d OFF** | **~2.4** | **~105 ms** | (no snapshot) | **0.0** | **~150** |

\* At 390 on this host the server never brings both sides to contact (arrival collapse, the
documented out-of-scope per-peer world-sync limit), so decode lag intermittently spikes when the
server stops streaming to a peer; in the contact-capable runs below it is uniformly ≤ 28 ms.

### Contact-capable scales (where this host does reach combat)

| Run (P636d) | reached | kills | decision pass p95 | decLag p95 | snapAge p95 | cpu |
|---|---|---|---|---|---|---|
| 30v30 **OFF** | 60/60 | **42** | 0.8 ms | 1 ms | — | 1.7 |
| 100v100 ON | 144–155/200 | 12–17 | 29–55 ms | 1–28 ms | 51–80 ms | ~3.6 |
| 150v150 ON | 65/300 | 0* | 82 ms | 10 ms | 90 ms | 3.4 |

\* No contact at 150v150 on this host (arrival collapse) — the P636c 150v150 OFF reference (3
kills / 100 reached) was likewise on the edge of contact, so lethality is not comparable at that
scale here. The real-rig 200v200 contact is the decisive lethality test (as the task states).

### Headline acceptance vs measured

| Acceptance | Status |
|---|---|
| `off` = zero publishes | ✅ `pubPs = 0.0` at 390 and 30v30 (measured) |
| bot CPU at 390 ≤ P636c (proxy baseline; da43841f ran pre-instrumentation) | ✅ off 2.4 < 3.3; on ~2.9 < ~3.4 |
| decode lag p95 ≤ 100 ms | ✅ in all contact runs (1–28 ms); at 390 limited only by the out-of-scope server arrival stall |
| lethality 100v100/150v150 ≥ snapshot-off | ⚠️ 150v150 never contacts on this host; 100v100 arrivals vary 144–179/200 (server-side), making the comparison noise-dominated — the real rig is decisive |
| decision pass p95 < 50 ms | ✅ 30v30 (0.8 ms); ~ met 100v100 (~50 ms); not met at 390/150v150 on this host (arrival-collapse-bound) |

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| client advances remote ships between updates (dead-reckon to "now") | `CshipIGC::CalculateShip` (ShipDeadReckoner, P448b) | design | preserved unchanged; the aim still dead-reckons the engaged target + own pose to the decision's "now" (P636c), lock-free |
| decision cadence ~20 Hz / 50 ms | era pacing | era constraint → intent kept | deadline scheduler (P636b) unchanged; the publish floor bounds per-world publish to ≤ ~25 Hz |
| the decision snapshot itself | n/a (harness-side decoupling, not a native client feature) | modern addition | P636/P636b/P636c/P636d: a lock-free immutable snapshot so decisions skip the receive gate; this task only makes its PUBLISH lazy + switchable |

## Modern alternative considered

Native broadcasts every remote ship to every peer on a fixed modem-era budget; the port built a
lock-free decision snapshot so 390 bots' decisions don't fight the receive thread's gate. The
naive publish rebuilt that snapshot on a fixed 25 Hz timer per bot regardless of whether anyone
was deciding — "work nobody consumes" at scale. The modern pattern (interest/relevancy — send /
derive only what a consumer actually needs, and only while it is listening) is exactly what P636d
applies: publish only while a bot is actively deciding, skip when nothing changed, and let the
switch stop it outright. A further "publish only the own-sector slice + engaged target" (the
task's optional `and/or`) is the natural follow-up and is proposed, not implemented, to keep this
change behaviour-neutral and low-risk; the dead-reckoned aim already tolerates the bounded
snapshot age it would introduce.

## Verification

- **Failing-first tests** — `tests/Allegiance.ClientCore.Tests/P636dSnapshotPublishCostTests.cs`
  (6 new): `NoPublish_WithoutARequest`, `Request_WithUnchangedWorld_SkipsRebuild`,
  `Request_WithChangedWorld_Rebuilds`, `OffMode_PerformsZeroPublishes`,
  `ActiveLevel_PublishesAtCadence_WhileHeld_AndCancelStopsIt`,
  `FreshSnapshot_KeepsPublishStamp_AndDeadReckon`. Each is red on HEAD (the new API surface does
  not exist / the fixed-timer publish rebuilt unconditionally) and green here. Existing snapshot
  tests updated to the request-driven contract (`DecisionSnapshotTests`,
  `DecisionSnapshotAllocationTests`, `P636bCadenceSnapshotTests`). Targeted run: **30/30 pass**
  (snapshot + P636b + P636c + P636d + perf aggregation).
- **Whole-solution build:** 0 warnings, 0 errors.
- **Full suite — `scripts/run-tests.sh`, 2 consecutive runs on the final code** (cite
  `docs/TEST_FLAKES.md`; report each as it happened):
  - **Run 1:** Lobby 12/12; MonoGame 1922/1925 (3 env skip); ClientCore **677/678** (1 flake:
    `ClientWarpApproachSlowHullLiveTests.Legacy_200_450…` — warp-timing, TFL family seen in
    P633/P636c; verified green in isolation); Core 4008/4009 (1 flake:
    `LiveTreasurePickupTests.Rescue_ship_picking_cash…` TFL:157; verified green in isolation
    twice).
  - **Run 2:** Lobby 12/12; MonoGame 1922/1925; ClientCore **678/678 (clean, incl. all P636d
    tests)**; Core 4005/4009 (4 *different* live-wire flakes — `ConsoleLogNonBlocking…`,
    `WarpBombTests.Live_whole…`, `LoadoutChangeLiveTests.Mount_and_remove…`,
    `LiveRipcordTests.Re_target…` — all verified green in isolation; the run-to-run flake mix is
    the documented live-socket family, none touch the P636d snapshot paths).
  - All 6 unique failures verified **green in isolation** on the final code.
- **Mandatory combat check:** 30v30 `off`, 150 s window → **42 kills** (25A/17B), 60/60 reached,
  `pubPs = 0.0` (kills > 0 in both ship-update paths exercised by the switch; the off path is the
  P635 live-read model).
- **200v200 footer (port, this host):** arrival collapse (50–70/390 reached, 0 kills) — the
  pre-existing out-of-scope server world-sync limit (P632/P633/P636b), unchanged by this
  client-side fix; the real-rig 200v200 contact is the decisive lethality check Claude performs.

## Deliverables

- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `DecisionSnapshotEnabled` (switch honoured
  by the writer), `RequestDecisionRefresh`/`CancelDecisionRefresh` (active-gated lazy publish),
  `MarkSnapshotDirty` (skip-unchanged generation), `MarkApplied`/`LastAppliedTick64` (decode lag),
  `SnapshotPublishCount`/`SnapshotPublishMs` (publish-cost instrumentation).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `MarkApplied()` on the receive path.
- `tools/allegiance-mcp/BotManager.cs` — decision loop asserts/revokes refresh per active bot;
  publish + decode-lag samplers; `DecisionSnapshotEnabled` delegated to ClientCore.
- `tools/allegiance-mcp/McpTools.cs` — `snapshotPublishPerSec`, `snapshotPublishMsPerSec`,
  `decodeLagMs` in `get_perf_stats`.
- `tests/Allegiance.ClientCore.Tests/P636dSnapshotPublishCostTests.cs` (new, 6) + updated
  snapshot tests; `P632PortModeZeroKillsLiveTests.cs` PERF line extended.
- `docs/reports/P636d_SNAPSHOT_PUBLISH_COST.md` (this report).

Committed on `master` (not pushed). See `git log -1`.
