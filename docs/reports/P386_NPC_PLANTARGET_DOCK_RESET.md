# P386 — NPC return-to-sell PlanTarget never cleared on dock (miner decision-cycle stall)

Date: 2026-09-25 · Branch: `master` · Fix site: `src/Allegiance.Server/ServerHost.cs`

## Symptom (observed live, this session)

A P382-spawned initial miner (ship 8096 on the real server) completed its
**first** economic cycle flawlessly — spawn docked → P381 auto-undocked after
10 s → warped to Alderan → mined a rock to full (150/150) → warped home →
docked → sold 150 ore for 750 credits. Every cycle *since* was broken: the ship
oscillated `undock → (no travel) → dock` every ~10 s forever. Raw server log
showed zero `[warp]`/`[mine]`/`[mine-sell]` lines after the first cycle — only
repeated `[npc-autolaunch]`/`[dock]` pairs.

## Root cause

`Ship.cs:5999` gates the **entire** NPC default-order re-decision on
`if (PlanTarget is null)`:

```
if (PlanTarget is null)
{
    if (AcceptedTarget is { } accepted) { ... promote into PlanTarget ... }
    else if (IsNpc) { ... PilotTypeMiner → DecideMiningOrSelling() ... }
}
```

`DecideMiningOrSelling()` (`Ship.cs:5115`) sets `PlanTarget = station` for the
sell leg once the bag is full (`Ship.cs:5137`). **Nothing in the whole codebase
ever clears `PlanTarget` back to `null` on arrival.** Grepping the solution for
`PlanTarget = null` / `= <null>` outside `Ship.cs` gives **zero hits** in
`Sector.cs` / any `Server*.cs`. So once a miner completes one sell trip,
`PlanTarget` permanently holds "go to the station I'm now sitting at":

- Later ticks see `PlanTarget is not null`, so the **entire block above is
  skipped** — the miner cycle never re-enters.
- The movement/arrival code driving the ship toward `PlanTarget` sees it has
  already arrived (position unchanged since the last dock) and re-docks it.
- P381's new auto-launch pass re-undocks it → infinite `undock→redock`, never
  mining again.

P381 is what exposed this — nothing previously relaunched a docked NPC more
than once, so the staleness never mattered.

## Fix

`ServerHost.cs` — `DockWithStationEvent`, the **one** "a ship has just arrived
and docked" transition for every docking ship (player or NPC). It already does
the analogous arrival reset for ore (`sh.Ore = 0f` on the sell). Immediately
after the "just arrived" `sh.StationId = ...` (line 663, **not** the defensive
already-docked early return above it):

```csharp
sh.PlanTarget = null;
sh.PlanCommandId = Allegiance.Core.Wire.OrderChange.CidNone;   // "unset slot" (Ship.cs:971)
sh.AcceptedTarget = null;
sh.AcceptedCommandId = Allegiance.Core.Wire.OrderChange.CidNone;
```

Plus an NPC-only log line `[npc] ship {id} arrived — PlanTarget/AcceptedTarget
cleared, ready to re-decide next tick`.

- **No `IsNpc` guard on the field-clear** (only on the log line): `PlanTarget`
  is exclusively NPC-produced (its own doc comment, `Ship.cs:961-963`), so the
  clear is a safe no-op for real player docks.
- **`AcceptedTarget`/`AcceptedCommandId` cleared for the same reason (brief item
  4, investigated and confirmed real):** `AcceptedTarget` is the *other*
  return-to-base slot, promoted into `PlanTarget` at `Ship.cs:6001-6004` every
  time the plan slot is empty. If it survived docking, the very next tick would
  re-promote it and recreate the identical infinite loop even with the
  PlanTarget reset alone. It is produced only by the return-to-base
  `ORDER_CHANGE` branch (`ApplyCommandSlot`, `Server.ShipCommands.cs:1272`), so
  it is NPC-only too — clearing it on dock is safe and correct ("a ship that
  arrived has consumed its return-to-base order").
- **`MiningAsteroidTarget` needs no reset here:** the sell leg's mutual
  exclusion (`Ship.cs:5139`) already nulls it before the sell trip.

## Verification — real proof, not just build/test

### Must-fail (proven red on the pristine tree)

The new regression test `Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell`
fails **at cycle 1's dock** on the pristine tree with
`Assert.Null(npc.PlanTarget)` → "Value of type 'Nullable<DataStationIGC>' has a
value" (the stale sell-station PlanTarget persists). With the fix it is green.

### Multi-cycle live proof (fixed tree)

The same test boots a real in-process server + bot and drives the FULL
autonomous loop **three times** through the real `ResolveDockCollisions →
ServerHost.DockWithStationEvent → P381 auto-undock` path, with real
`[mine]`/`[mine-sell]`/`[dock]`/`[npc-autolaunch]` log lines on every cycle:

```
[mine] ship 8097 began mining asteroid 7711 ...
[mine] ship 8097 is full (150/150 ore) — mining paused
[npc] ship 8097 arrived at station 8011 — PlanTarget/AcceptedTarget cleared, ready to re-decide next tick
[mine-sell] ship 8097 sold 150.0 ore at 5/u = 750.0 credits ...
[dock] ship 8097 docked at station 8011 'CYCLE' ...
P386-CYCLE 1/3: sold 150.0 ore ...
... (cycle 2) ...
P386-CYCLE 2/3: sold 150.0 ore ...
... (cycle 3) ...
P386-CYCLE 3/3: sold 150.0 ore ...
P386-LIVE: miner repeated the full autonomous mine→sell cycle 3 times
```

The `Accepted_return_to_base_slot_cleared_on_dock` test seeds a stale
return-to-base `AcceptedTarget` + `PlanTarget`, docks the ship via the real
path, and asserts **both** slots (and their command ids) are cleared — red on
pristine, green with the fix.

### Diagnostic chain that located the rock-removal artifact (test-only)

Instrumenting the multi-cycle test showed the ship's sector `Mineable` list
became empty after cycle 1: the synthetic rock used `HitPoints: 0`, and the
miner, on departing the rock with a full bag (`MiningAsteroidTarget` cleared →
no longer P254 "actively mining"), grazed the very rock and **destroyed** it
(`Asteroid.ReceiveDamage` → `_fraction = 0` → `RemoveAsteroid`). The single-cycle
P156 tests never noticed because they stop at the first dock. The multi-cycle
test now gives its rock real `HitPoints` (`100000f`) — a real map rock has real
HP — so this is a test-setup artifact, not the live bug.

## Test-suite fallout and corrections (documented, reviewed)

Clearing `PlanTarget` on every dock is the semantically correct universal fix
("a ship that arrived and docked has consumed its plan"), but it legitimately
changes post-dock NPC behavior: a docked NPC now correctly resumes its decision
cycle (re-mines after selling, re-decides after arriving) instead of remaining
parked with a stale `PlanTarget`. Five pre-existing Npc live tests encoded the
old stale-plan regime and were updated to assert the corrected behavior (each
still guards its real purpose — none was weakened to vacuity):

1. `NpcAutopilotLiveTests.Damaged_npc_take_cover_now_produces_observable_movement`
   — dereferenced `npc.PlanTarget.Value` after the ship docked at its hiding
   station (plan legitimately cleared → NRE). Now measures the **minimum**
   approach to the hiding station (and whether it docked there).
2. `NpcDodgeLiveTests.Traveling_npc_steers_around_a_station_obstacle_and_reaches_its_own_station_goal`
   — measured distance to goal station A at a late tick *after* the ship docked
   there and correctly flew off to re-mine. Now measures the **minimum** approach
   to A (it must reach it) while still asserting it dodged the obstacle.
3. `NpcCrossClusterLiveTests.Cross_cluster_npc_paths_through_warp_and_continues_to_station_in_new_sector`
   — same: late-tick distance after dock+re-depart. Now measures the **minimum**
   approach to the station in the target sector.
4. `NpcAsteroidContentionLiveTests.Two_same_side_idle_miners_lower_ore_keeps_rock_higher_ore_redirected_to_unload`
   — asserted the displaced loser "never mines" / stays parked at the unload.
   After P386 the loser correctly re-contends *after selling* (native-faithful:
   a miner sells then mines again). Now asserts the arbitration still routes the
   higher-ore loser to SELL (its ore drops via the unload sale) and the lower-ore
   winner mines.
5. `NpcCrossSideContentionLiveTests.Enemy_contender_evicts_everyone_no_redirect_and_none_mines`
   — Stage 1 (first-eviction no-redirect, the P167 core) is preserved verbatim.
   Stage 2's long-window "same-side winner never redirected" no longer holds
   because under P386 a displaced miner re-enters the cycle, allowing P159 to
   sell-redirect the same-side winner in a no-enemy window (the redirect targets
   the unload; it does not mine the contested rock, so the lockout's core —
   nobody mines the contested rock — still holds). Kept: nobody mines the
   contested rock; the enemy is never redirected.

The other full-suite failures observed across runs (LiveGainFlag, Conquest-
Territory, GameOverPlayers, AsteroidMined, ShipsUpdate, LiveTurretGunner) all
pass in isolation on the fixed tree — they are the documented machine-state /
live-wire timing flakes, not regressions from this change.

## Build / tests

- Whole-solution build: 0 warnings, 0 errors.
- Full `Allegiance.Core.Tests` suite run at least twice (see the two run logs);
  only the documented live-wire flakes rotate.

## Out of scope (unchanged)

- `ResolveMining` crowding-simplification — untouched.
- P381 auto-launch gating — untouched (this bug is entirely on the
  decision-cycle side).
- P368–P385 — untouched.

## Deliverables

- Fix committed on `master` (not pushed — Domingo pushes).
- This report: `P386_NPC_PLANTARGET_DOCK_RESET.md`.
