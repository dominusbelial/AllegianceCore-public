# P381 — docked NPC drones auto-launch on their own

Port of native `CclusterIGC::Update`'s per-tick auto-launch loop
(`~/Development/Allegiance/src/Igc/clusterIGC.cpp:106-140`) and the
`IshipIGC::OkToLaunch` docked-minimum it reads (`shipIGC.h:1914-1915`). Before
this task, nothing in this port ever undocked a docked NPC on its own — a
miner that returned to dock and sell just "sat there". Now a docked drone
undocks itself after ~10 s and the existing per-pilot-type decision loop picks
it up the next tick, so it "just works" exactly like native.

## What was ported

### 1. `Ship.LastDockTime` (new field) — `src/Allegiance.Core/Sim/Ship.cs`
A `double`, same shape/default as the sibling `Ship.LastLaunchTime`
(`double.NegativeInfinity` — a ship that starts undocked reads as docked
"forever ago" and never spuriously trips the timer). Documented as the port of
native's per-ship "docked since" clock that `OkToLaunch`'s ~10 s minimum reads
(`GetMyLastUpdate()` is not updated while docked, so it pins the dock
transition).

### 2. `LastDockTime = <now>` stamped at every dock latch
The brief enumerated `Server.Docking.cs:171, 701-702, 767-768, 1820-1821`
labelled "board, rescue x2, teleport, normal dock". **Verification against the
live tree found that list incomplete/mis-labelled**: line 1820-1821 is the
*teleport* dock (PerformTeleport), not "normal dock", and the actual *normal*
dock — the return-to-sell path, the most important one for miners — latches
`StationId` in `ServerHost.DockWithStationEvent` (`ServerHost.cs:663`), reached
through the one `Sector.ResolveDockCollisions` funnel. The brief's list also
missed the lifepod-respawn dock (`Server.Combat.cs:1375`). Per the brief's own
principle ("stamp every site that sets StationId to a non-null value"), all of
them are now stamped:

| Site | Location | `now` source |
|---|---|---|
| Normal dock (return-to-sell / capture-fall-through) | `Sector.cs:3018`, immediately before the `DockWithStationEvent` seam (the single funnel; `ResolveDockCollisions` skips already-docked ships at `:2929`, so it only fires on a genuine new dock) | `now` (method param, == `_simNow`) |
| Board (inherits carrier's dock) | `Server.Docking.cs:171` | `_simNow` |
| Ship-ship rescue | `Server.Docking.cs:703-704` (kids + rescuee) | `_simNow` |
| Station rescue | `Server.Docking.cs:774-775` (kids + rescuee) | `_simNow` |
| Teleport | `Server.Docking.cs:1829-1830` (kids + ship) | `_simNow` |
| Lifepod respawn | `Server.Combat.cs:1376` | `_simNow` |

No new clock was added anywhere — each site uses the `now`/`_simNow` already in
scope, per the brief. `ServerHost.DockWithStationEvent` itself has no clock (its
interface signature carries no `now`, unlike `KillShipEvent`), so the normal-dock
stamp lives at the Sector funnel where `now` is available — the same tick value
the host latches `StationId` with.

### 3. The per-tick auto-launch pass — `Server.Docking.cs` (`RunNpcAutoLaunch`)
- Constant `NpcAutoLaunchCooldownSeconds = 10.0` (native `10.0f`).
- For every sector of every mission, every ship where `IsNpc && StationId is
  not null && ParentShip is null && !StayDocked && (now - LastDockTime) >=
  cooldown` is undocked via the **shared `UndockShipState`** the ORDER_CHANGE
  LAUNCH handler already uses (`Server.ShipCommands.cs:1100-1122`) — pure state
  mutation, **no new wire message** (observing peers learn the NPC's new flying
  state via the existing HeavyShipsUpdate/BroadcastShipsUpdate machinery, the
  P147/P152 reasoning). Logs `[npc-autolaunch] ... auto-undocked from station
  {id} after {elapsed}s docked`.
- **Added `ParentShip is null` gate** (beyond the brief): native iterates
  `pstation->GetShips()` — station-docked drones — and asserts they are never
  observers/parents. In this port a *boarded* passenger inherits the carrier's
  `StationId`; without the gate the pass could wrongly `UndockShipState` a
  boarded passenger (which `UndockShipState` does not detach). The gate keeps
  auto-launch strictly to station-docked drones, matching native.
- Hooks into `SimPass` in `Server.Broadcast.cs` as a new
  `Step("npcAutoLaunch", ...)` right after `Step("update", ...)`, so a drone
  that docked+sold in this tick is eligible from the next (matches native's
  loop running each cluster tick).

## Deliberately not ported

- **`OkToLaunch` feasibility pre-check** (`LegalCommand` /
  `PickDefaultOrder(…, bDocked=true)`, shipIGC.h:1919-1920): dead code for this
  port's scope, matching the established precedent at
  `Server.ShipCommands.cs:1089-1100` — the only ported `PickDefaultOrder`
  sub-block (the P151 take-cover check, shipIGC.h:2230) is gated on `!bDocked`,
  which `true` makes false. The real behavior is already covered for free: the
  instant the pass clears `StationId`, the ship's existing per-tick decision
  loop (`Ship.cs` ~6000-6110) picks it up the next tick.
- **Friend/enemy local-safety check** (shipIGC.h:1925-1969): **skipped** —
  it would require per-ship `SeenBySide` tracking, pilot-type/ally/distance
  classification, and a three-branch squared-distance threshold lattice
  (`c_d2AlwaysRun`/`c_d2SafeishDist`/`c_d2IgnoreDist`) this port does not
  track. It is a real native behavior (won't launch into a lopsided local
  fight) but is genuinely secondary to "miners undock at all", and the 
  feasibility check it guards is itself not ported. Skipped and documented
  plainly per the brief's instruction.

## Out of scope (untouched)
No new NPC spawning, no spawn-already-docked (that's P382), no real-player
docking/undocking change (`StayDocked`/`UndockShipState` reused exactly; the
pass is gated on `IsNpc &&`, which is non-negotiable). P368-P380 untouched.

## Build / test
- Whole solution builds 0 warnings, 0 errors (all 4 test projects + server/core).
- New live test `tests/Allegiance.Core.Tests/NpcAutoLaunchLiveTests.cs`
  (harness mirrors P156/P147: in-process server, one seated bot, XXV_simple).

### Live verification (real running server, deterministic `TickSim`)
`Docked_npc_miner_auto_undocks_after_cooldown_then_mines`:
1. **Player-unaffected control**: a real non-NPC player ship placed docked with
   `LastDockTime = NegativeInfinity` (would launch instantly were it not for the
   gate). Asserted still docked through every phase — proves the `IsNpc &&` gate.
2. **Phase A** — spawned docked NPC miner (`IsNpc=true`, `PilotTypeMiner`,
   `StationId` set, `LastDockTime` stamped at the current sim clock) does
   *nothing* for ~10 s (asserted still docked 1 s shy of the cooldown).
3. **Phase B** — auto-undocks on its own, zero commands:
   `[npc-autolaunch] ship 8097 side 1 auto-undocked from station 8881 after 10.0s docked`.
4. **Phase C** — the existing loop takes over: finds the asteroid
   (`MiningAsteroidTarget` set, `PlanTarget` null) and mines
   (`[mine] ship 8097 began mining`), proving the already-ported decision loop.
5. **Phase D** — full return-to-sell cycle via the **real** dock path: fills
   `150/150`, flies to DEPOT, docks (`[dock] ship 8097 docked at station 8881
   'DEPOT'`), `LastDockTime` was stamped by the real path (not
   `NegativeInfinity` → no instant relaunch), waits ~10 s, then auto-relaunches
   (`[npc-autolaunch] ... after 10.0s docked`) — the complete "miners just
   work" loop Domingo remembered from native play.

Ran 4× consecutively in isolation, all green (~1 s each).

## Test adjustments required by the new behavior (important)

P381 changes one thing that five existing test methods (across three files)
assumed incorrectly — none of these are flakes, they are deliberate
adaptations to the now-native-faithful behavior, and each is explained in-file:

1. **`NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at`
   (P170)** — originally docked its NPC and let it sit, asserting it "arrives"
   over a fixed 7000-tick window. P381 now auto-relaunches a docked NPC that
   still holds an accepted Goto (native relaunches Goto-drones too,
   `clusterIGC.cpp:132` `c_cidGoto`), so the ship legitimately docks → waits →
   undocks → re-approaches in a cycle, and the cumulative line-deviation over
   7000 ticks caught the re-approach turns. The test's actual purpose is the
   goal-*exclusion* steering proof (the NPC does not dodge its own docking
   station), which happens on the first approach. Updated to break at the
   **first dock**, isolating the goal-exclusion proof from the cycling.
2. **`OrderChangeStayDockedLiveTests` (P148) and `OrderChangeLaunchLiveTests`
   (P152), all four methods** — each manually latched a spawned NPC docked by
   setting `StationId` directly without a timestamp. With `LastDockTime`'s
   `NegativeInfinity` default, the auto-launch pass rightly treats that as
   "docked forever" and relaunches before the ORDER_CHANGE arrives — breaking
   the stay-docked latch. Production never hits this (every dock site stamps the
   clock), so these tests now stamp `LastDockTime = seat.Server.SimNow` when
   they manually dock — mirroring the real dock seam.

## Full-suite verification (run 3, post-fix: 3359 passed / 2 failed)

The only failures across the full-suite runs are **pre-existing live-wire
timing flakes**, confirmed unrelated to P381:
- `LiveTurretGunnerBindingTests` — the documented chronic env flake.
- `GameOverPlayersWireTests.Conquest_win...` — intermittent (flaked on a
  pristine master worktree too, 1/6), a game-over/across-mission wire test
  with no docking involvement.
- Rotating subsets across runs (CargoBayReloadTests, AddPartTests,
  AutoReloadOnEmptyTests, LiveRipcordTests, LiveRelaunchTests,
  Multi_mission_disconnect, LiveMoneyTests, ShipsUpdateLiveTests,
  LiveLifepodEnduranceDisplayTests, WarpBombTests) — different tests failed
  each run and all passed in isolation; the textbook env-flake signature (per
  the port workflow skill). The last two involve real player ships (IsNpc=false),
  which the pass's `IsNpc` gate exempts, so they cannot be P381 effects.

The *deterministic* failures (the five test methods above) are real behavior
consequences and were fixed as test adjustments, not dismissed.
