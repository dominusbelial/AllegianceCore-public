# P875 — native GotoPlan as the default: the NPC-travel suite passes (or every failure classified)

Goal (from the brief): flip `SimConfig.NativeGotoPlan` to **true** with the whole
NPC-travel suite green, classifying each of the 16 failures Claude reverted for
in a0c21876 as either *stale test* (asserting the port's legacy steering) or
*real GotoPlan gap*. Native behaviour is the spec (`~/Development/Allegiance/src/`,
re-read for every cite below). Changes stay uncommitted in the working tree of
`pi/p875` (base 9a8115db).

## The flip (commit d0d0acca's edits, re-applied)

- `src/Allegiance.Server/SimConfig.cs` — `NativeGotoPlan = true`, summary cites
  the 2026-10-28 rig A/B (b52 vs b50) and the legacy-port-steering opt-out.
- `src/Allegiance.Server/Server.Lifecycle.cs` — boot log line reads
  `(native NPC travel; false = legacy port steering)`.
- `tests/Allegiance.Core.Tests/P817NativeGotoPlanTests.cs` —
  `Flag_defaults_on_and_environment_overrides_config` asserts the default is ON;
  all 43 cases pass 3×.
- `docs/parity/parity-matrix.csv` — "Drones: miners / Native optimal travel
  controls" row `partial → implemented` (evidence cites both P817 and this
  report). `python3 scripts/parity-scorecard.py` validates.
- `docs/EXPERIMENTS.md` — the `gotoplan` experiment row removed (it is no
  longer an experiment; `sim.nativeGotoPlan=false` remains the rollback switch).

## Why the 16 tests failed with GotoPlan on: one root cause, three fixture sins

Instrumented runs (temporary env-gated diagnostics, since removed) showed every
failure is the **native visibility gate**: the P817 adapter resolves a target's
presence with `NativeCanSee` and routes only along **seen** aleph edges — the
faithful port of native `pship->CanSee(pmodel)` (`Igc/shipIGC.h:341-364,380-389`)
gating `GetIgcSite()->GetCluster(pship, pmodel)` (`Igc/igc.h:4909-4913`) and
`FindPath`'s per-edge `if (pship->CanSee(pwarp))` (`Igc/common.cpp:899-927`),
with native's no-known-route outcome being "keep the original target, dodge in
place" (`common.cpp:1920-1926`). The legacy port steering ignored visibility
entirely, so fixtures that (a) ordered drones into sectors no side had ever
scanned, or (b) aimed command targets at objects native cannot represent, passed
under legacy and hung under native. Production never hits this: the lobby seeds
starting visibility at the Started transition (`Server.Lobby.cs:2132`,
`Mission.SeedStartingVisibility`, the port of the bShowMap reveal,
`missionigc.cpp:4056-4071` + `mapmakerigc.cpp:470-479,1002-1017`), stations,
probes and scouts keep the live sweep fed (`FedSrv.CPP:10746,10878-11010`), and
predictable statics latch once seen (`FedSrv.CPP:11124-11126,11133-11137`).

## Per-test table

| Test | Failing assertion (reproduced) | Classification | Native cite | Fix |
|---|---|---|---|---|
| `P591cDroneRoutingTests.Side1_layer_from_Organia…guard_off` | `guard=OFF laid=False sector=1051 crossings=0 steps=60000` — the layer never departs | stale fixture (unscouted route) | `common.cpp:899-927` FindPath gates each edge on `pship->CanSee`; `:1920-1926` unknown route ⇒ dodge in place; `warpIGC.cpp:35` warps are `c_mtSeenBySide` | `ShowMap = true` on the test game config + explicit `Mission.SeedStartingVisibility()` (the direct `Stage = Started` write bypasses the lobby's Started-transition seed, `Server.Lobby.cs:2132`); all assertions unchanged (lay, crossings ≤ route length, 0 guard blocks) |
| `P591cDroneRoutingTests.Side0_layer_from_Rigel…guard_off` | same symptom, Rigel 1059 | stale fixture (same root cause) | same | same |
| `P591cDroneRoutingTests.After_crossing_into_intermediate_sector…` | drone never leaves Organia (expected Gaia 1062) | stale fixture (same root cause) | same | same; the plan-target invariant assertions are untouched |
| `P591cDroneRoutingTests.Re_entry_guard_stays_armed_but_block_counter_stays_zero…` | `laid=False`, no movement | stale fixture (same root cause) | same | same |
| `P591cDroneRoutingTests.Drone_reaches_and_lays_with_the_guard_left_at_its_default_off` | same | stale fixture (same root cause) | same | same |
| `P591bLayerPingPongTests.Side1_layer_from_Organia…without_ping_pong` | `layer must lay and be consumed` — never lays | stale fixture (same root cause) | same | `ShowMap = true` + `SeedStartingVisibility()` in `DriveSide` |
| `P591bLayerPingPongTests.Side0_layer_from_Rigel…without_ping_pong` | same | stale fixture (same root cause) | same | same |
| `NpcDodgeLiveTests` (5 tests) | `finalStation=700.9` etc. — the NPC never moves | stale fixture: the goal was a **data-only `DataStationIGC`** never placed in the sector — a command target native cannot represent (`m_commandTargets[c_cmdPlan]` is a live model, `shipIGC.h:440-460`); `StationTarget` finds no runtime ⇒ `Present=false` ⇒ native waits (`igc.h:4909-4913`, `common.cpp:1873-1874`) | same cites | goals rewritten to native **layer build orders** (`SetLayerBuild`, the c_ptLayer `c_cidBuild` point delivery — `shipIGC.cpp:4055-4109` creates the c_buoyWaypoint, `:2810-2828` lays + consumes the drone on completion), spawned via `SpawnNpcShip` with a real layer drone type + mine expendable. The obstacle-station test's obstacle became enemy-owned so the idle default order (take-cover, `shipIGC.cpp:1943`) cannot adopt it. **No bar was moved**: dodge clearance, near-direct-line, arrival and settle assertions are byte-identical |
| `NpcAutopilotLiveTests.Flying_npc_with_plan_target_moves_measurably_closer_to_station` | `d1 < d0 - 200` failed — NPC frozen at spawn | stale fixture (data-only station target, as above) | same | rewritten as a layer build order (`Flying_npc_with_waypoint_goto_moves_measurably_closer_to_target`); bars unchanged |
| `NpcAutopilotLiveTests.Flying_npc_reaches_and_settles_near_target…` | `must settle near the target: d=898` | stale fixture (same) | same | same rewrite; settle/runaway bars unchanged |
| `NpcCrossClusterMinerDecisionLiveTests.Idle_npc_in_empty_sector_finds_and_mines_cross_sector_asteroid…` | `NPC never crossed into Haumea/1057` — rock target `pres=False` | stale fixture: the decision cycle (P652 domain) stays visibility-blind, but native travel needs the route and target seen | `igc.h:4909-4913`; `common.cpp:899-927,1873-1874`; `asteroidigc.cpp:36` rocks are `c_mtSeenBySide` | `ShowMap = true` + `SeedStartingVisibility()` for the route; the placed rock marked seen by all sides **after it exists** (ShipSpawner runs at boot) — the exact "scouted field" seeding P528b's fixture documents |
| `InitialMinerSpawnLiveTests.Debug_mode_auto_starts_at_boot…` | `debug-mode miners never warped to Alderan` | stale fixture (unscouted route/field on the m2xspiderw boot) | same | `ShowMap = true` on the config + `SeedStartingVisibility()` + the authored field marked seen (P528b pattern) |
| `P650ServerRulesTests.Local_mining_battle_reports_income_and_leaves_drained_rocks` | `Assert.NotNull(drilling)` — miners stuck targeting rocks whose route was unknown | stale fixture (same; income flowed from home-sector rocks while cross-sector travel stalled) | same | `ShowMap = true` (the same P817-era fixture stance P528b already carries) + `SeedStartingVisibility()` + field seen |

Real GotoPlan gaps found: **none**. Every failure above is the fixture asserting
something native cannot do (fly an unknown route, chase an unrepresentable
target); with native-representable fixtures the port's GotoPlan meets or exceeds
every original bar (P591b/c layers now lay in seconds with route-length
crossings and 0 guard blocks; the dodge/autopilot NPCs arrive at 34–36 u from a
20 u-radius goal).

Also checked per the brief: `P650SimRulesTests` (all pass), and
`MiningStateWireTests` — **4/4 under both** `ALLEGIANCE_SIM_NATIVE_GOTOPLAN=1`
and `=0`.

## Item 4 — the Core `Mission.NativeGotoPlanEnabled` default stays `false`

`Mission.NativeGotoPlanEnabled` remains default-off. Cite: the only production
writer is the server — `Server.Lifecycle.cs:1360` always assigns it explicitly
from `SimConfig.ResolveNativeGotoPlan()` (now ON), and the single-player
skirmish host boots a real `Srv` with a `ServerConfig` (`SkirmishHost.cs:68`),
so every production mission runs native. The Core default is P817's opt-in seam
for direct-constructing tests; flipping it would silently re-arm hundreds of
synthetic `Mission` fixtures built against the legacy steering that
`sim.nativeGotoPlan=false` must keep working, without changing production
behaviour. The both-arms suites (`MinerDecisionCycleLiveTests`,
`P652EconomyDroneAiTests`, `P528bMinerSellRegressionLiveTests`,
`BuildingConstructionLiveTests`, `NpcBuilderDefaultOrderLiveTests`,
`P769BuilderCollisionBuildTests`) still run both arms — re-verified green on
this tree (6/6, 128/128, 2/2, 10/10, 6/6, 8/8).

## Verification (light gate, as specified)

| Check | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | 0 warnings / 0 errors |
| `tools/build-tools.sh` | all 92 tool projects built |
| AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained`) | succeeded, 0 warnings / no IL2026/IL3050 |
| Every touched/listed test 3× alone | `P591cDroneRoutingTests` 6/6 ×3; `P591bLayerPingPongTests` 3/3 ×3; `NpcDodgeLiveTests` 6/6 (one uncaptured class flake, then 14 consecutive 6/6; ledgered); `NpcAutopilotLiveTests` 3/3 ×3; `NpcCrossClusterMinerDecisionLiveTests` 2/2 ×3; `InitialMinerSpawnLiveTests` 4/4 ×3; `P650ServerRulesTests` 3/3 ×3; `P817NativeGotoPlanTests` 43/43 ×3 |
| Both-arms economy suites | all six classes green (see Item 4) |
| `tools/run-tests.sh --tier fast` | run 3×. Sweep 1 (`tiers-20261029T034058`): 2 failures — one pre-existing master failure (below) + one load-staged flake that passes 3/3 alone. Sweep 2 ran while another lane's testhost was executing on this workstation (8 contention-attributed failures, **all pass 3/3 alone**). Sweep 3: the pre-existing failure + one legacy-arm-only wingman failure + one contention flake. Details in `docs/TEST_FLAKES.md` (P875 section) |
| Live tier, NPC/drone classes | the listed classes **are** Live; each ran 3× alone (above) |
| Showcase never-parks 5× | **5/5 passed**: reached 24/24/26/24/24 of 30; longest stopped-with-target 0.906/0.401/0.000/0.000/0.000 s (bar 5 s) |
| `python3 scripts/parity-scorecard.py` | validates; `tools/check-glossary-links.py` OK (279 paths) |

**Pre-existing master failure (proven not lane-caused):**
`NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
fails deterministically in **both** arms and was reproduced on a pristine `HEAD`
worktree (`.scratch/baseline`, `git worktree add` at 9a8115db, local.env copied,
1/1 failed). Its Phase-1 charge-centred acquisition evaluates but does not
acquire. P875 touches no wingman/defend source. Related:
`NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy…` **fails only with
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN=0`** (legacy arm, 37 s) and passes 2/2 with the
flag on — the flip improves it; recorded in `docs/TEST_FLAKES.md`.

## Native-intent footer

The port's GotoPlan is *stricter* than the legacy steering, exactly where native
is: a drone flies only what its side has seen (`shipIGC.h:341-389` CanSee;
`common.cpp:894-1011` FindPath), waits dodging-in-place when no route is known
(`common.cpp:1920-1926`), terminates an invisible/dead target's plan
(`common.cpp:1873-1874,2319-2321` via `igc.h:4909-4913` GetCluster), treats
command targets as live models (`shipIGC.h:440-460`; the layer's c_cidBuild
target is a c_buoyWaypoint buoy created at `shipIGC.cpp:4055-4109` and consumed
by LayExpendable at `:2810-2828`), and always sees its own side and
non-side-visible models like waypoint buoys (`FedSrv.CPP:11133-11137`,
`:10884-10892`). The fixtures now stage exactly the knowledge a native side
would have (bShowMap reveal, scouted field) — no assertion was weakened
anywhere; several got *stronger* in practice because GotoPlan's routes are
faster and cleaner than the legacy brake-and-drift paths they replaced.

**Acceptance test:** Claude's 2026-10-28 rig A/B (200v200, b52 vs b50: tick
median 16.2 vs 16.3 ms, overrun windows 4 vs 6, 19 vs 8 stations destroyed, one
conquest win) remains the acceptance evidence for the default flip; this task's
rig-free verification establishes suite correctness, not rig performance.

## Unfinished / notes

- The two pre-existing `NpcWingman*` master failures are documented, not fixed —
  they reproduce on pristine `HEAD`, are outside this task's scope, and one is
  fixed *by* the flip. Whoever picks them up: the defend test's Phase-1
  acquisition (`UpdateWingmanDefend`/`EvaluateWingmanDefend`) runs twice within
  budget without acquiring; instrument from there.
- `NpcDodgeLiveTests` produced one uncaptured 1-of-6 class failure in 15 runs
  (ledgered, unreproduced).
- Fast-sweep failures under workstation contention (another lane's testhost ran
  concurrently during sweep 2) are contention-attributed with solo 3/3 evidence
  in `docs/TEST_FLAKES.md`.
