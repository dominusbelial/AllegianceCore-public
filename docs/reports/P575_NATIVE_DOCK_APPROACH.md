# P575 — Native Dock Approach (autopilot & bots dock like native)

**Status:** implemented + unit-tested + same-sector live-proven, committed on `master`.

## Problem (why this exists)

Domingo reported ships "getting stuck on the station outer ring", and asked
whether the autopilot self-docks. Before P575 the port flew the straight line at
the **station centre** everywhere:

- Player autopilot: `AutopilotSteering.SteerToward` disengaged "arrived" within
  60 u of a stationary target (`Game1.FlightInput.cs`). For a station that is
  its centre — the ship grinds on the ring unless the straight line happens to
  thread a bay.
- Server NPC `GotoPlan` replacement (`Ship.ApplyNpcAutopilot` → `SteerToward`)
  flew `PlanTarget` stations at their centre.
- BotManager `DrivePodHome` flew the pod at `PodStationCenter`.

The server's dock **test** was already native (P550 `Station.InGarage`, a port of
`CstationIGC::InGarage`, `stationIGC.cpp:385-436`): a ship only docks when it
physically enters a real bay. So the fix is to make the approach *enter a bay*,
not to change the dock test (explicitly out of scope).

## What native does

`Waypoint::GetGotoPosition`'s `OT_station` case (`src/Igc/common.cpp:1505-1545`):

- Pick the bay set for the hull class — `c_habmFighter` → `GetLandSlots()`,
  else `GetCapLandSlots()` (`common.cpp:1513-1515`); if 0 bays, **power glide**:
  `c_gpmFast` straight at the centre, skipping the target (`:1517-1524`).
- Else transform each slot's plane-0 position/direction by the station
  orientation (`centers[i]=GetLandPosition(i,0)*o`, `:1528-1531`) and call
  `DoApproach` (`:1157-1320`), then OR `c_gpmRoll` when it chose `c_gpmEnter`
  (`:1540-1542`; the carrier twin `:1494`).

`Waypoint::DoApproach` (`:1157-1320`) chooses the best bay (smallest error² to
the approach line, `:1200-1244`), and decides a phase via the mask grammar
(`c_gpmKillThrottle/pivot/enter/fast/dodgeShips/noDodge/roll`, `:1140-1149`):
drift (`error²>36`, stage on the line), coast (`offset²>40`, kill throttle),
or — on the line, nose aligned and not turning (`CosForward2>0.999²`, rate²<
rateMax²) — **enter** with `c_gpmNoDodge` (`:1301-1318`), else pivot.
`GotoPlan::SetControls` then turns the mask into yaw/pitch/roll/throttle
(`:2030-2170`), including the enter roll-to-level computation (`:2090-2148`).

### Native-fidelity correction found while writing the tests

My first reading assumed the "staging point" (the quadratic solve `goal` in
DoApproach, `:1178-1196`) was where the ship flies during entry. Native actually
flies **at the bay centre** on the enter branch — `*pvectorGoto = *pcenterBest +
itsPosition` (`common.cpp:1287-1288`); the staging point `goalBest` is used for
*bay selection* (`error² = |dpRest−goal|²`, `:1230`) and as the `goto` in the
drift (`:1252`) / coast (`:1278`) branches. Proven from the native source and
encoded in the tests (`Aligned_not_turning_enters..._at_the_bay_centre` flies at
(0,0,20) the bay centre; `Drift_stage_flies_at_the_native_staging_point` flies
at (0,0,30) the staging point).

## Implementation

**New shared pure planner — `src/Allegiance.Core/Sim/DockApproach.cs`**
(MonoGame-free, operates on `Vector3`/`Orientation`/`ControlData`):
- `Mask` bits (`:1140-1149`), `DockBay`, `Step{Mask,BayIndex,Goal,Facing}`.
- `DockStation(...)` — the `OT_station` case + `DoApproach` 1:1 (bay selection,
  staging/coast/drift/pivot/enter, `c_gpmRoll` on enter, no-bay power glide).
- `StationBaysForClass(isFighter, bays, capLandSlots)` — the `:1513-1515`
  fighter/capital selector. In this port a station's geometry is a single
  `capgarage` set (`HullLandingBay.ReconstructStation`, `stationtypeigc.cpp:71-107`;
  221 station models carry no separate fighter `garage` bays), so both selectors
  collapse to the same set — documented, with the hook kept for a future core.
- `Steer(step, ...)` — the `SetControls` entry translation (pivot / kill-throttle
  / enter+roll / seek-fast), the port's shared face law for yaw/pitch and native's
  roll-to-level (`:2090-2148`).
- `CosForward2` (`orientation.cpp:441-444`) for the enter gate.

**Consumers — one shared routine, three call sites:**

| Consumer | File | Path |
|---|---|---|
| Player autopilot | `Game1.FlightInput.cs` `TryComputeDockSteer` | same-sector dockable station target → `DockApproach.Steer`; **no "arrived" disengage** — stays engaged until `ShipEntity.StationId != -1`, then `DisengageAutopilot("docked")`. Falls through to centre/goto (power glide) when not dockable/wrong-side. |
| Server NPC `GotoPlan` | `Ship.cs` `TryDockApproach` | same-cluster `PlanTarget` station → bay approach; dockable gate mirrors the server dock dispatch (`Sector.cs:3563-3566`), bays from `Sector.StationLandingBays` (the same cache `InGarage` consumes). |
| Bots pod-home | `BotManager.cs` `TryDockApproachControl` | in the pod's home sector → bay approach via the same planner (falls back to the old centre fly when unresolvable). |

Each gates on the hull's dockability (`SabmLand` for a fighter, `SabmCapLand`
otherwise — the exact need the server dock dispatch applies, `Sector.cs:3563-3566`)
so the ship never bay-approaches a station it cannot dock at (or an enemy/wrong-
side station client-side).

## Tests — failing-first

`tests/Allegiance.Core.Tests/DockApproachTests.cs`, 8 tests, every expectation
hand-derived from the native closed forms (`common.cpp` cited in each):
- no-bay power-glide (`Fast` at the centre);
- aligned + not turning → `Enter|NoDodge|Roll`, flying at the **bay centre**;
- misaligned on the line → pivot (`NoDodge`);
- drift → stages at the native **staging point** (0,0,30);
- nearest-bay selection (bay 0 wins, bay centre goal);
- fighter (all bays) vs capital (cap set) selector;
- enter steering = full throttle + roll-to-level;
- power-glide steering = full forward.

Written before `DockApproach` existed (reference fails to compile on old code =
red), green after. All 8 pass in every Core suite run.

## Build

Whole solution: **0 warnings, 0 errors** (`dotnet build -m:8`).

## Full suite — 3 consecutive runs on the final code

Runner note: `scripts/run-tests.sh` (which builds once then sequences the four
assemblies) exceeded this session's terminal tool cap (420 s) and was killed
before recording, so I ran the **same four assemblies it sequences, in its
order, one per call** — identical coverage, every run's numbers preserved (the
P529/P542 "suite result lost mid-run" failure mode avoided).

Measured (each run, per assembly):

| Run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1584/1584 | 544/545 | 3780/3781 |
| 2 | 12/12 | 1584/1584 | 545/545 | 3780/3781 |
| 3 | 12/12 | 1584/1584 | 543/545 | 3780/3781 |

Every failure is a **documented `docs/TEST_FLAKES.md` load/timing flake**, each
**confirmed to pass in isolation**:

- `FullSessionKillIntegrationTests...CrossSideKill` (Run 1, 3 — ledger line 33;
  passes alone in 1 s).
- `AddPartTests.Part_pickup_decline_recreates_treasure...` (Run 1 — ledger lines
  247/293/331; passes alone).
- `ShipUpdateBudgetLiveTests.Locked_target_is_always_included...` (Run 2 — ledger
  ~line 363 live-wire budget timing; passes alone).
- `ReadyCheckLiveTests.Only_the_mission_owner...` (Run 3 — ledger line 398/333).
- `StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window...` (Run 3 —
  ledger line 371/399; passes alone in 3 s).

None touch the docking/autopilot code paths P575 changed; all rotate run-to-run
exactly as the ledger documents. (My 8 new DockApproach tests are inside Core's
3781 and passed every run.)

## Live proof — real client window (P572 discipline, 2560×1440 backbuffer)

Same-sector player dock (real window, `ALL...` real backbuffer PNGs):
`docs/reports/P575_assets/dock_samesector/proof_t*.png`. The client log shows the
sequence end-to-end: `autopilot ON → Omega Debug Garrison (Station#24465)` →
`autopilot OFF (docked)` → `docked: hangar/loadout screen`. Frame inspection
confirms the ship lines up on the station's **bay opening** (targeting reticle on
the bay, `proof_t013.5`) and the **hangar "STATION DOCKED - LOADOUT"** screen
follows (`proof_t016.8`). Server confirms `[dock] ship 8098 docked at station
24465 'Omega Debug Garrison'`. **Zero station-ring collisions** in the server log
during the approach.

Crucially the disengage is the new terminal **`autopilot OFF (docked)`** — not the
old 60-u "arrived" (the ship stayed engaged through entry until the server docked
it), which is exactly the "no false arrival at the centre" requirement.

Cross-sector: attempted a `--auto-goto <sector> → --auto-dock-after` capture, but
the sector name supplied did not resolve in the m2xspiderw map instance, so that
run fell back to a (successful) same-sector dock. Cross-sector *routing* is
pre-existing (P540b/P541, unchanged); P575's contribution — the bay-approach
handover once the ship is in the target sector — is the same on-arrival path the
same-sector live proof exercises, and is additionally covered by the unit tests.
A dedicated live cross-sector capture was not completed this session (honest gap,
not claimed).

Bots: MCP `spawn_scenario` bots are spawned as player peers, so at death the
**server's own native eject-pod path** insta-docks them (`[lifepod] ... docked
Lifepod`), which does not exercise the BotManager `DrivePodHome` path P575 wired.
That path is implemented and shares the exact planner proven live above, but a
dedicated live BotManager pod-home capture was not completed this session.

## Files changed

- `src/Allegiance.Core/Sim/DockApproach.cs` (new)
- `src/Allegiance.Core/Sim/Ship.cs` (NPC `TryDockApproach`)
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` (player autopilot)
- `tools/allegiance-mcp/BotManager.cs` + `Catalog.cs` (pod home)
- `tests/Allegiance.Core.Tests/DockApproachTests.cs` (new, 8 tests)
- `docs/reports/P575_assets/` (live-proof scripts + real backbuffer PNGs)

Committed on `master` (not pushed). `git log -1` / clean `git status`.
