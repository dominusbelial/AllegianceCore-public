# P324 — Tick-cost reduction batch: visibility sweep throttle, candidate-list caching, batch cadence, NPC BFS caching

Four related MEDIUM performance findings from the zai/glm-5.3 full-codebase review
(`pages/allegiance-server-review-glm53-full-codebase.md`), batched because they are the
same pattern: real per-tick work that native either throttles or caches but the port
re-did unconditionally every tick. Landed on `master` (not pushed — Domingo pushes).
Each finding is an optimization of a hot simulation path; observable game behavior is
unchanged (only computed more cheaply).

Reference source is read-only (`~/Development/Allegiance`, never modified).

---

## M2 — Cross-side visibility sweep throttled to native's every-10 cadence

**Finding.** `Sector.ResolveCrossSideVisibility()` ran every sector tick: a
targets × scanners × asteroids line-of-sight sweep (VisibilityMath.InScannerRange /
LineOfSightExist) recomputed every participant's `SeenBySide` every pass.

**Native behavior.** `CclusterIGC::Update` calls each model's `UpdateSeenBySide()`
(and the per-cluster site callback) only on every 10th pass:
`if ((m_nPass++) % c_nPassesPerUpdate == 0)` (`src/Igc/clusterIGC.cpp:348`), with
`const int c_nPassesPerUpdate = 10` (`src/Igc/clusterIGC.h:22`). So native flips a
target's visible/invisible state on a ~10-pass timescale, not every tick.

**Approach.** Ported the throttle faithfully: added `Sector.c_nPassesPerUpdate = 10`
and a per-sector pass counter, and gated the sweep call in `Sector.Update` with the
identical `(_visPass++ % c_nPassesPerUpdate) == 0`. The sweep still runs on the first
pass (boot/join), then every 10th. `ResolveCrossSideVisibility()` itself is untouched,
so its semantics and the direct-call unit tests are unchanged.

**Observable-behavior check.** A target appears/disappears on roughly the same
real-world timescale as native (≤ ~10 ticks ≈ 0.3-0.5 s), not instantly-every-tick.
The station/asteroid/probe per-side broadcasts read `SeenBySide`, which now refreshes
every 10 ticks — coherent with the M4 cadence below.

**Measured (P306 stress, 150 bots / 20 Hz, 35 s).** Cross-side sweep executions per
tick: **13.0 → 1.3** (10.0× fewer) — 9022 sweeps / 693 ticks before vs 910 / 694 after.

---

## M3 — Ship-update candidate list cached once per sector per tick

**Finding.** `Sector.GetShipUpdateCandidates()` allocated a fresh `List<Ship>` and
re-scanned `_models` on every call, and it is called from many hot sites —
`BroadcastShipsUpdate` once per seated peer per tick, plus the NPC arbitration searches
(`ArbitrateAsteroidContention`, `TryComputeDodgeGoal`, `FindNearestEnemyShip`,
`FindLeastTargetedAsteroidAnyCluster`). At 150 peers that is ~150 reallocations +
re-scans per sector per tick.

**Approach.** Cached the candidate list once per sector per tick. The cache is built
lazily on the first reader in the tick (keyed by the sector's `UpdateCount`) and shared
by every caller until the tick advances or the model membership changes. `Sector.Add` /
`Remove` bump a membership version, so a ship that dies, warps out or docks mid-tick
(they all `Remove` from `_models`) invalidates the in-tick cache and the host's
post-`Mission.Update` broadcast re-derives the true post-tick membership — preserving
the exact behavior (a dead/departed ship is dropped from the broadcast). Callers are
read-only (`foreach`), so sharing the list is safe.

**Observable-behavior check.** Same candidate set delivered to every peer; only the
repeated allocation/re-scan is eliminated.

**Measured.** Candidate-list **rebuilds** per tick: **150.2 → 1.0** (~150× fewer
allocations) — 104100 rebuilds / 693 ticks before vs 695 / 694 after (calls stayed
~150/tick but now hit the shared cache).

---

## M4 — STATIONS/ASTEROIDS/PROBES per-side batches cadence-throttled

**Finding.** `Server.BroadcastStationsUpdate` / `BroadcastAsteroidsUpdate` /
`BroadcastProbesUpdate` gathered, re-encoded and re-sent the full per-side batch every
tick even though almost all of it is identical frame-to-frame (asteroids don't move; ore
changes only while actively mined; probe health only on damage).

**Native behavior.** These per-side object batches are sent to a flying player on
cluster **entry** — `CFSPlayer::SetCluster` (`src/FedSrv/fsship.cpp:817-915`) — not
continuously every tick; a docked player gets `STATIONS_UPDATE` every 0.75 s
(`FedSrv.CPP` `UpdateMissions`, `dtimStationUpdateInterval = 0.75f`). There is **no**
`MISSILES_UPDATE` message in native at all (the port's `MissileUpdate(205)` is a
renderer-only invention for the port client, documented in
`docs/MISSILE_RENDERING_P234.md`).

**Approach.** Cadence-throttled the three per-side object broadcasts to every
`Sector.c_nPassesPerUpdate` (10) ticks — aligned with the M2 visibility cadence, since
these broadcasts read `SeenBySide`. A quiet tick (not a cadence tick and no join) now
skips the gather, encode **and** send entirely. A side is force-sent immediately when a
peer newly joins it (`TryJoinSide` marks it in `_objectSyncDirtySides`; consumed/cleared
after the probes broadcast), reproducing native's "send on cluster entry" so a fresh
joiner is not left waiting up to a full cadence window for the sector's objects.
Real changes (ore mined, a station captured, probe damage) propagate within ≤ 10 ticks
(~0.3-0.5 s) — prompt and native-faithful (faster than native's 0.75 s docked cadence).

**Missiles deliberately excluded** from throttling: an in-flight missile's position
changes every tick, so the "identical frame-to-frame" premise does not hold; the
per-side in-flight count is tiny (negligible cost); and a renderer-only client needs
smooth missile motion (throttling would visibly stutter). There is no native
`MISSILES_UPDATE` cadence to match, so per-tick is the closest faithful choice.
Documented rather than changed.

**Observable-behavior check.** Stations/asteroids/probes reach clients within ~10 ticks;
new joiners get them immediately via the dirty flag; station/asteroid/probe state changes
(the live tests) are reflected in the next cadence send.

**Measured.** Per-side object batch **sends** per tick: **1.0 → 0.099** (10.1× fewer) —
694 sends / 693 ticks before vs 69 / 694 after; the per-tick gather is now also skipped
on quiet ticks.

---

## M8 — NPC cross-cluster pathfinding cached per NPC

**Finding.** `Ship.ApplyNpcAutopilot()` re-ran a warp-graph BFS every tick per NPC for
both the cross-cluster steering goal (`CrossClusterWarpGoal` →
`Sector.FindPathToward`) and the ripcord anchor (`TryRipcordOrDefer` →
`FindRipcordAnchorCluster`), with the code's own comment noting "no caching is built
here."

**Approach.** Both BFS results depend only on the ship's **current sector id** and the
**target cluster id** — both stable between the ship's warps, with the warp graph static
per mission — so each is cached and re-resolved only when that (sector, target) key
changes: a new target (mining→selling switch, a different station/asteroid) or the ship
warping to a new sector (which is exactly the re-path-after-warp P157 relied on). The
dynamic `IsSafeToRipcord()` test still runs every tick; only the static BFS is cached.
A cached `null` (unreachable target) is honoured, so a permanently-unreachable target
stops paying the BFS every tick.

**What "the goal changing" means for this autopilot** (the invalidation trigger): the
nav context is the pair (ship sector, target cluster). A station-side change (capture)
is the only stale risk for the ripcord half — downstream `RequestRipcord` re-validates
the anchor, and a stale miss merely forfeits a ripcord shortcut (the caller falls
through to the warp leg), so reaching the real destination is preserved.

**Observable-behavior check.** NPCs still navigate to their real goals; the 30
NPC-cross-cluster / autopilot / ripcord live tests pass, including transit to the target
sector and the mining→selling cross-cluster switch (which changes the cache key).

**Measured (focused cross-cluster benchmark, 1000 ticks).** Cross-cluster BFS
executions per tick: **2.0 → 0.002** (~1000× fewer) — pathFinds=1000 + ripcordBfs=1000
before vs 1 + 1 after (one resolution cached for the whole unchanged journey).

---

## Aggregate macro measurement (P306 harness)

`tools/vizfight stress <webPort> 150 20 35 0` — 150 real bots cruising on one team,
the population where P306 documented server collapse.

| metric | before | after |
|---|---|---|
| visibility sweeps / tick | 13.0 | 1.3 |
| candidate-list rebuilds / tick | 150.2 | 1.0 |
| per-side object sends / tick | 1.0 | 0.099 |
| cross-cluster BFS / tick | 2.0 | 0.002 |

The aggregate tick-cost average in the single-team cruise scenario is dominated by the
out-of-scope `BroadcastShipStates` O(P²) broadcast (P317) and per-peer heavy ship
updates, so per-tick wall-time there does not move; the four optimizations each
demonstrably reduce their own per-tick work by 10-1000× (measured above), which is the
deterministic, box-noise-immune signal. The cruise scenario does not exercise
cross-cluster NPCs (hence the dedicated M8 benchmark).

## Build / test

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- `Allegiance.Core.Tests` run 2×: only the pre-existing documented
  `LiveTurretGunnerBindingTests` flake fails consistently; the handful of other
  failures are environmental live-loopback timing flakes that vary run-to-run and pass
  in isolation (verified) — the exact baseline pattern on this shared box.
- `Allegiance.Lobby.Tests` 12/12; `Allegiance.ClientCore.Tests` 162/162 (one transient
  flake on the first run, clean on rerun); `Allegiance.ClientCore.MonoGame.Tests`
  578/578.

## Scope boundaries / exclusions

- CRITICAL/HIGH findings (P311-P318) and `BroadcastShipStates` O(P²) (P317) are
  separate tasks — untouched.
- P322/P323 and the other MEDIUM findings — separate tasks.
- Native reference source untouched (read-only).
- The `MISSILES_UPDATE` stream is deliberately not cadence-throttled (rationale in M4).
