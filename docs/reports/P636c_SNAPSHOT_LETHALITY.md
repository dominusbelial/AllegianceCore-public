# P636c — Lock-free snapshot aim-staleness: cause, fix, and honest scale attribution

Date: 2026-10-13. Branch: `master` (commit not pushed). Deliverable report + commit.

## TL;DR

P636/P636b decoupled bot decisions from the receive-thread gate with a lock-free
decision snapshot — a real win that is fully kept here. But the snapshot FREEZES
the pose inputs of the aim path at the ~25 Hz publish cadence. The lead law
(`solveForLead`, `src/Igc/common.cpp:121`) and the fire gate
(`c_fMaxOffAngle = 0.10f`, `src/Igc/shipIGC.cpp:2476`, gate at `:2600`) consume
the engaged target's and the bot's own position/velocity/orientation. A frozen
pose up to the publish age old shifts the intercept point beyond hull radius —
a fast scout at ~850 u/s moves ~34 u in a 40 ms publish age, larger than a fighter
hull radius — so the bot's nose never settles on the moving lead, the fire gate
stays shut, and the bot fires far less.

**What I found (with numbers):** the snapshot's age at decision time is ~22–44 ms
mean / ~42–100 ms p95 at 100–150v150, growing to ~56 ms mean / 155 ms p95 at
200v200. That age is a REAL defect in the aim inputs, quantified and fixed
(failing-first). But the headline 150v150/200v200 0-kill is **not** primarily the
snapshot: on this workstation the two sides **never co-locate** at those scales
for ANY mode — including `off` (P635 live reads): 150v150 OFF = 3 kills / 100
reached, ON-fixed = 0 / 93. That arrival collapse is the out-of-scope server-side
per-peer world-sync capacity issue P632/P633/P636b already attributed it to. The
controlled staleness experiment isolates the snapshot's own mechanism: a 750 ms-
stale snapshot with RAW aim collapses lethality to 0 kills / 4.1 % hit rate even
**with** contact (83/100 reached).

**The fix (keeps the no-lock benefit):** only the two aim-critical poses — the
engaged target and the bot's own ship — are dead-reckoned forward to the
decision's "now" (`pos + vel × age`, uniform motion) from the snapshot, entirely
lock-free. Every O(N) scan and navigation path still reads the raw snapshot with
no `cs.Gate` acquisition.

## A/B table (P632 harness, local in-process server, port unless noted)

| Build / mode | Model | Scale | window | reached | kills | shots | hitRate | hull/shield | decision Hz | snapAge mean/p95 |
|---|---|---|---|---|---|---|---|---|---|---|
| `da43841f` P635 (hist, earlier) | port | 150v150 | 130 s | 194/300 | 29 | 20353 | 77.5% | – | n/a | n/a |
| P636b hist (raw snapshot) | port | 150v150 | 130 s | 129/300 | 11 | 10679 | 79.8% | – | ~17–24 | n/a |
| **ON-fixed (this work)** | port | 150v150 | 115 s | 93/300 | 0 | 4015 | 68.2% | 2321/416 | ~20 | 41/100 |
| **OFF (P635 live, switch)** | port | 150v150 | 130 s | 100/300 | 3 | 5390 | 66.9% | 2585/1021 | ~25 | 0/0 |
| **ON-fixed** | port | 100v100 | 110 s | 179/200 | 25 | 18864 | 76.1% | 9702/4662 | ~20 | 22/42 |
| **ON raw (fix off)** | port | 100v100 | 110 s | 181/200 | 21 | 17686 | 75.5% | 9171/4176 | ~17 | 21/45 |
| **OFF (P635 live)** | port | 100v100 | 110 s | 179/200 | 23 | 18697 | 77.2% | 10064/4365 | ~20 | 0/0 |
| **ON-fixed** | native | 100v100 | 110 s | 178/200 | 110 | 48081 | 68.2% | 20884/11885 | ~20 | 21/39 |
| **ON-fixed** | native | 30v30 | 120 s | 52/60 | 37 | 18525 | 55.3% | 6583/3658 | ~20 | – |
| **ON-fixed** | port | 200v200 | 100 s | 66/400 | 0 | 957 | 34.2% | 327/0 | 7.5 | 56/155 |

Controlled staleness experiment (isolates the mechanism; `ALLEGIANCE_BOT_SNAPSHOT_PUBLISH_MS=750`, 50v50, port):

| mode | reached | kills | shots | hitRate |
|---|---|---|---|---|
| 750 ms-stale snapshot, RAW aim (fix off) | 83/100 | 0 | 169 | 4.1% |
| 750 ms-stale snapshot, dead-reckoned (fix on) | 85/100 | 0 | 229 | 0% (nav also stale at 750 ms) |

Reading: a genuinely stale snapshot destroys lethality even with contact (RAW 4 %
hit). Dead-reckoning recovers it over the realistic short ages (~20–40 ms; the
100v100 RAW 21 → FIXED 25) but cannot repair a 750 ms freeze, where the velocity
itself is stale and navigation on the stale snapshot also breaks — 750 ms is beyond
the uniform-motion envelope and beyond the real operating point. The moderate
staleness (200 ms) at an easy 50v50 contact still gives 68 kills even raw, so the
effect is intensity/geometry-dependent: it bites hardest in dense, fast fights,
which is exactly the rig's 200v200 contact scenario.

## Cause, with numbers

1. **Snapshot age (the defect).** Every aim input in `EngageShipAt` (`TryCsPose`
   own pose, snapshot target pos/vel) is frozen at the ~25 Hz publish. Measured
   age at decision time: 100v100 ~22 ms mean/42 ms p95; 150v150 ~41/100 ms;
   200v200 ~56/155 ms. A 850 u/s scout drifts ~34 u in 40 ms — beyond a fighter
   hull radius — so the frozen pose points the lead at empty space and the fire
   gate (`angleToLead ≤ 0.10 rad`) stays shut. Unit-proven:
   `P636cSnapshotLethalityTests.Raw_frozen_pose_misses_beyond_hull_radius_at_publish_age`.
2. **Orientation/velocity basis: NO bug.** `FillShipSnapshot` stores rows as
   (right, up, backward) from `GetRight/GetUp/-GetForward`; `MaterializeOrientation`
   → `SetBasis` reproduces the identical `Orientation`. Verified equivalent to the
   live `Latest.Orientation`; no handedness/sign defect (this matches P636b).
3. **Visible-ships compaction: NO dropped targets.** Phase A, `NearestOtherEnemy`
   and the reach query iterate `VisibleShips`, which is exactly the `IsVisible`
   subset the P635 scans filtered on — behaviour-identical (P636b equivalence
   test; reconfirmed by the switch A/B where OFF and ON produce the same target
   counts).
4. **Dominant scale term is arrival collapse, not the snapshot.** 150v150 OFF
   (P635 live) = 3 kills / 100 reached, ON = 0/93 on this host — the two sides
   never co-locate. This is the documented out-of-scope server-side per-peer
   world-sync limit; the snapshot's staleness is a real but secondary contributor
   that matters most precisely where contact already fails (or, on the real rig,
   where contact succeeds at 200v200 and staleness becomes the limiter).
5. **Publish cost / decode starvation: NOT indicated.** Decision cadence held
   ~20 Hz at ≤150v150 and ~7.5 Hz at 200v200 (host-overload-bound); the snapshot
   publish is allocation-light (pooled, P636 measured 272 B/ship/round).

## The fix (keeps the no-lock benefit)

- `DecisionSnapshot.PublishedTick64` — monotonic publish stamp.
- `DecisionSnapshot.DeadReckonPosition(in DecisionShip, double ageMs)` — pure
  uniform-motion extrapolation (`pos + vel × age`), unit-testable.
- `BotManager.TryCsFreshPose` — the aim path now reads the snapshot (no gate) and
  dead-reckons the engaged target and own ship to the decision's "now"; the O(N)
  scans and navigation still read the raw snapshot. The P636 lock-free benefit is
  fully preserved: no `cs.Gate` is acquired for any of this.
- Failing-first: `Snapshot_deadreckon_matches_live_pose_where_raw_freeze_misses`
  and `DeadReckon_is_lockfree_pure_over_the_snapshot` go RED when dead-reckoning
  is disabled (verified), GREEN with it.

## The A/B switch (`ALLEGIANCE_BOT_DECISION_SNAPSHOT`)

`on` (default) / `off`. `off` reverts the decision read path to the pre-P636 P635
live-table reads under `cs.Gate` (`TryCsPose`, `CsSector`, Phase A scan,
`EngageShipAt`, `NearestOtherEnemy`, `SameSideShipsInLine`, warp maps) — verified
with `snapAge=0` in off mode. Sub-switches for isolation:
`ALLEGIANCE_BOT_SNAPSHOT_DEADRECKON` (default on) and
`ALLEGIANCE_BOT_SNAPSHOT_PUBLISH_MS` (default 40) drive the controlled A/B.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| aim uses current shooter/target pose + velocity | `solveForLead`, common.cpp:121 | design | kept; the aim law is unchanged — only the freshness of the pose fed to it was degraded by the snapshot freeze |
| fire gate `angleToLead ≤ 0.10 rad` | shipIGC.cpp:2476, 2600 | design | kept; unchanged |
| client advances remote ships between updates (dead-reckon to now) | `CshipIGC::CalculateShip` (ShipDeadReckoner.cs P448b, ±1.5 s window shipIGC.cpp:3113) | design | the fix dead-reckons the engaged target/own pose to "now" — the first-order (uniform-motion) term of native's CalculateShip, accurate over the measured short publish ages |
| decision cadence ~20 Hz / 50 ms | era pacing | era constraint → intent kept | deadline scheduler (P636b), unchanged |

## Modern alternative considered

Native (and the early port) computed the aim straight off the live pose every
tick. The P636 snapshot trades a lock for a frozen-but-cheap read; the naive
result is aim staleness. Modern engines solve this with snapshot interpolation /
dead-reckoned prediction to presentation time (the standard network-game
technique) rather than consuming the raw frozen sample. P636c adopts exactly that
for the two aim-critical poses (cheap, lock-free), and keeps the frozen snapshot
for the O(N) scans where a few ms of pose age is irrelevant to target selection.
A full per-bot spatial index + higher-rate pose lane for the engaged target is the
async-receive follow-up (unchanged from P636/P636b).

## Verification

- **Failing-first tests:** 3 new (`P636cSnapshotLethalityTests`) — verified RED
  with dead-reckoning disabled, GREEN with it. Plus the 7 P636 + 7 P636b snapshot
  tests still pass (17 total targeted).
- **Whole-solution build:** 0 warnings, 0 errors (Release).
- **Full suite — `scripts/run-tests.sh`, 2 consecutive runs on the final code**
  (cite `docs/TEST_FLAKES.md`; every failure is a ledgered server-side live-wire
  load-flake, none referencing the decision snapshot):
  - Run 1: Lobby 12/12; MonoGame 1875/1878 (3 env skip); ClientCore 671/672
    (1 flake: `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_…`,
    TFL:404); Core 4006/4009 (3 flakes: `LiveTreasurePickupTests.Rescue_ship…`
    TFL:157, `LiveTeamLobbyTests.Unready_broadcasts…` TFL:405,
    `AsteroidMinedLiveTests.No_broadcast_fires_once…` TFL:268/272 family).
  - Run 2: Lobby 12/12; MonoGame 1875/1878; ClientCore **672/672 (clean)**; Core
    4003/4009 (6 flakes: `AutoReloadOnEmptyTests.RELOAD129` TFL:52,
    `StationUnderAttackAlertLiveTests.Sub_30…` TFL:418 family,
    `P590PodRescueTests.Pod_pickup_order_streams…` TFL:432,
    `LiveRipcordTests.Ship_anchor_insufficient_energy…`,
    `GameOverPlayersWireTests.Conquest_win…` + `ConquestTerritoryLiveTests…`
    TFL:29/30/267 family — the recurring load-variant P636b also saw).
  - All 10 unique failures **verified green in isolation** on the final code
    (1 ClientCore + 9 Core).
- **200v200 footer (port, this host):** 66/400 reached, 0 kills — arrival
  collapse, matching the P636b 200v200 collapse (P635 4 kills / 106 reached there
  too); reconfirms the out-of-scope world-sync bottleneck.
- **Mandatory combat check:** 30v30 native 37 kills / 52 reached (kills > 0);
  100v100 native 110 kills — no regression in either ship-update model.

## Deliverables

- `src/Allegiance.ClientCore/State/DecisionSnapshot.cs` — `PublishedTick64`,
  `DeadReckonPosition`.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — publish stamps `PublishedTick64`;
  publish interval overridable via `ALLEGIANCE_BOT_SNAPSHOT_PUBLISH_MS`.
- `tools/allegiance-mcp/BotManager.cs` — `ALLEGIANCE_BOT_DECISION_SNAPSHOT` switch
  (off = P635 live reads), `TryCsFreshPose` (dead-reckoned aim), snapshot-age
  instrumentation surfaced in `get_perf_stats`.
- `tests/Allegiance.ClientCore.Tests/P636cSnapshotLethalityTests.cs` (new, 3).
- `tests/Allegiance.ClientCore.Tests/P632PortModeZeroKillsLiveTests.cs` — `snapAge`
  in the PERF line.
- `docs/reports/P636c_SNAPSHOT_LETHALITY.md` (this report).

Committed on `master` (not pushed). See `git log -1`.
