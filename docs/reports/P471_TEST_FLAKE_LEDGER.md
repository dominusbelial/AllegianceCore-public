# P471 — test-suite flake ledger + the systemic cause

## Summary

`AllegianceCore`'s suite looked "noisy with timing flakes," but measuring it
produced a different, sharper conclusion: **most of the most-repeated
"flakes" are not flakes at all — they are deterministic, in-isolation
failures**, several of them genuine regressions introduced since P439's
report classified them as "green alone." P471 fixes the two named
pristine-tree failures, plus five more deterministic failures in the same
conquest/game-over cluster it uncovered, and lands one systemic change
(serialize the test assemblies) that measurably removes the remaining
cross-assembly *load-induced* class.

Single source of truth: `docs/TEST_FLAKES.md`.

## Methodology

- Baseline: three full-suite runs of the pristine tree
  (`/home/domingo/.dotnet/dotnet test`, solution root). Each ~4m22s wall.
- Isolation probes: every failing test was re-run alone with `--filter` to
  separate "fails because the suite is loaded" from "fails no matter what."
  A test that reproduces alone is a **real bug or stale test**, not a flake.
- Verifier: xunit parallelism config inventory (no `xunit.runner.json` /
  `maxParallelThreads`; an existing `[CollectionDefinition("LiveNetwork",
  DisableParallelization = true)]` already serializes live classes within an
  assembly) and a wall-time check that proved the four test assemblies execute
  their tests in parallel.

### Key measured facts

1. Three pristine runs: 13 / 9 / 12 failures.
2. Wall time (262 s) is far below the sum of the per-assembly durations
   (~357 s) → the four test assemblies run their tests **in parallel**, so
   live-socket test classes in different assemblies tick their loopback
   `Server`s at the same time.
3. The conquest / game-over cluster (`ConquestTerritoryLiveTests`,
   `GameOverPlayersWireTests`) failed 3/3 even in **complete isolation** —
   P439's "green alone, fails under full-suite socket load" label was wrong
   for them.
4. The genuinely load-induced set is small and is cross-assembly-sensitive:
   e.g. `LiveMoneyTests.Board_refund_...` passed when Core.Tests ran alone but
   failed in the parallel full suite.

## The two pristine-tree failures (step 2)

### `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`
Reproduces **in isolation** → a real (test) defect, not a flake. After P439's
rotation, the original mission `A` retains `Stage=Over` but the GAME_OVER is
sent to peer `a0` over the real loopback socket; the test pumped only until
`missionA.Stage == MissionStage.Over` and asserted `a0.GameOvers` before the
async receive landed. The sibling single-mission test already pumps on
`GameOvers.Count > 0 && stage == Over`. **Fix (test):** widen the pump to
require `a0.GameOvers.Count > 0` (budget raised to 5000 ticks). This removed
the deterministic failure; the residual (see after) is an intermittent
winner-mismatch from multi-mission peer-seating order that P472 should harden.

### `LiveWinConditionTests.Board_refused_once_mission_over`
Does **not** reproduce in isolation (passes 1/1 alone; failed 1/3 in the
parallel suite). It is a genuine *load-induced* flake: the control leg's
1200 ms board latch expires under cross-assembly socket load. Not a real bug
on the current tree; retested and categorized as load-flake, addressed by the
systemic change below. (P439's "mission no longer permanently Over" concern
does not bite here — the refusal leg sets `Stage=Over` directly and behaves
correctly in isolation.)

## Five more deterministic failures uncovered (also fixed)

The same P450/P439 interactions broke five conquest-family tests
deterministically; they are real, previously-mislabeled as flakes:

- **Shield-down capture gate (P450) vs shield-less flag bases.** P450's
  capture gate requires the base's shields below `c_fcidDownedShield`
  (`Sector.cs:3468`; native `shipIGC.cpp:849-851`). The conquest harnesses
  built their synthetic flag stations with `MaxShieldHitPoints = 0f`, and the
  port keeps a shield-less station's `ShieldFraction` pinned at 1.0 — so the
  gate rejects capture **forever**. Production is native-correct; the tests
  were stale. **Fix (tests):** give the flag bases a real shield
  (`ShieldRegeneration = 0f` so it stays down) and drop it through the real
  damage path (`ReceiveDamage`) before the capturer arrives — the same
  knock-out `StationCaptureLiveTests` already uses.
- **Rotation-during-update (P439) invalidates the `_missions` enumerator.**
  A conquest win fires inside `m.Update` (native fires `CheckForVictoryByFlags`
  inside the mission sweep), and `DeclareGameOver → RotateMissionAfterGameOver`
  sets `_missions[idx] = fresh` — a C# `List<T>` indexer set bumps the
  enumerator's version, so `foreach (var m in _missions)` in `SimPass`
  (`Server.Broadcast.cs:41`) throws "Collection was modified" on the next
  `MoveNext`. P439's comment claiming index-swap is enumerator-safe is wrong
  for `List<T>`. **Fix (production):** iterate a snapshot
  (`_missions.ToArray()`) in the update stage. Cost is one tiny array per
  tick; native allows a mission to end mid-sweep.

Verified: all five conquest-family tests now pass in isolation and in the
combined run (18/18 for the three touched classes).

## Still-open deterministic real bugs (handed to P472)

These reproduce in isolation and are therefore real bugs, not flakes; P471
documents them in the ledger rather than fixing them (diagnosis/broadcast
semantics and client-session logic are out of P471's scope):

- `ClientCore.Tests.ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` (3/3)
- `ClientCore.Tests.JoinFlowLiveTests.EntityTable_TracksARealTickingServer` (3/3; dup entity)
- `Core.Tests.LiveTurretGunnerBindingTests.Board_binds_gunner_...` (3/3; SINGLE_SHIP_UPDATE fires on orientation-only change)

## Systemic change (step 3) and its measured effect

**Change:** serialize the four test assemblies. Added
`scripts/run-tests.sh`, which builds once (parallel) then runs each test
assembly sequentially with `--no-build`. This is the only lever that removes
the *cross-assembly* socket contention (the assemblies demonstrably ran in
parallel before; `maxParallelThreads` is per-assembly and cannot prevent four
assemblies' live networks from overlapping). Unit tests inside an assembly
keep full parallelism (no thread cap), so pure unit-test throughput is
preserved.

- Before: parallel `dotnet test`, **~4m22s** wall.
- After: sequential `scripts/run-tests.sh`, **~5m30s** wall (sum of the four
  assemblies' sequential test durations ≈ 5m + a ~30s parallel build).

Measured effect: `Board_refused_once_mission_over` (the other named
pristine-tree test, a cross-assembly latch-timing flake) went from failing 1/3
to passing **3/3**, and the conquest/game-over cluster is green in all three
after-runs. `LiveMoneyTests.Board_refund_…` remained 2/3 — its fixed-sleep
receipt is intrinsic to the Core.Tests live sequence, not cross-assembly, so it
is a P472 condition-wait target. Bottom line: sequential assemblies remove the
*cross-assembly* load-induced class (the mechanistic cause proven by
wall<sum), at +~26% wall time, while preserving full unit-test parallelism
inside each assembly. Residual 1/3–2/3 fluctuations are the fixed-sleep timing
class P472 rewrites.

## After: three full-suite runs (post-change)

Three sequential runs through `scripts/run-tests.sh` (wall ~5m30s each):

- **run 1 (7 failures):** ShipDeleteGone.DockOut; ShipDeleteGone.CrossSide_Warper;
  JoinFlow.EntityTable; AutoReloadOnEmpty.Auto_reload_relays; LiveTurret;
  LiveMoney.Board_refund; CargoBay.Reload_malformed.
- **run 2 (7):** ShipDeleteGone.DockOut; JoinFlow.EntityTable;
  Transport.InboundFloodSafety.Oversized; Multi_mission;
  LiveTurret; LiveMoney.Board_refund; CargoBay.Reload_fuel_pack.
- **run 3 (4):** ShipDeleteGone.DockOut; JoinFlow.EntityTable; Multi_mission; LiveTurret.

Every remaining failure is listed in `docs/TEST_FLAKES.md` with its
failures/runs, isolation status, cause and status. The three runs' totals
fell from 13/9/12 (pristine) to 7/7/4 (post-fix), and the deterministic
conquest/game-over cluster (5 tests) is gone entirely.

## P472 handoff

P472 should:
1. Fix the three remaining deterministic real bugs (all reproduce in
   isolation): `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
   `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
   `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`.
2. Harden `Multi_mission_disconnect_scopes_per_mission` against
   peer-seating order (choose the disconnect target by its actual
   mission/side, not by index), and harden the `CargoBayReloadTests` members.
3. Convert the surviving live tests' fixed `Thread.Sleep`/`Task.Delay` waits
   into condition waits (the `load-flake` rows in `docs/TEST_FLAKES.md`).
