# P540b — autopilot to a target in ANOTHER sector flies to its coordinates in the CURRENT sector ("middle of nowhere"): route through warps first (reuse P541's router)

## Why this task exists

**Domingo (2026-10-04, live on 221 at `c129fb1f`):** "pod autopilot did work
but it took me to the middle of nowhere".

**Client log (his session):**
```
[eject] own ship 8109 became lifepod hull 444 at Vector3 { X = -27.4, Y = 8272.0, Z = -3234.2 }
[monogame] eject: autopilot home autopilot ON → Garrison (Station#4328)
[eject] autopilot target chosen: Garrison (Station#4328) in sector Ganymede (1050)
```
The pod was in **Alderan (1052)**, where the fight was; Garrison is two warps
away in Ganymede. **P540's target pick was correct.** The flight wasn't.

**Root cause (traced by Claude):** `ResolveTargetPosition`
(`src/Allegiance.ClientCore.Game/Game1.Targeting.cs`, the `TargetKind.Station`
case) returns the station's position **without checking its sector**, so the
P524 autopilot (`TryGetAutopilotControls`, `Game1.FlightInput.cs:~1495`)
steers toward Ganymede's coordinates **inside Alderan**: empty space. The
same applies to any autopilot target in another sector (a probe, a ship
seen on the map).

**Native:** a goto command to a model in another cluster is flown by the
autopilot **through warps** (the cross-cluster path to the target's
cluster), then to the target. Cite the native path-finding the client
autopilot uses (search `clintlib`/`WinTrek`/`Igc` for the goto/autopilot
cluster-path logic, e.g. `FindPath`/warp routing used when
`GetCommandTarget(c_cmdCurrent)`'s cluster ≠ the ship's cluster).

**Reuse:** P541 (`36696197`) added `src/Allegiance.ClientCore.Game/Flight/StarmapGotoRouter.cs`,
client-side warp-path routing for the star-map goto, with a live proof that
it flies through two warps (`P541GotoFlyLiveTests`). Use it. Don't write a
second router.

## Scope

1. **Cross-sector autopilot targets:** when the autopilot target's sector ≠
   the own ship's sector, steer via `StarmapGotoRouter` (next warp toward the
   target's sector). On arrival in the target sector, switch to flying at the
   target itself (the existing P524 law). That covers the P540 eject case
   (station or rescue probe) and any other cross-sector target.
2. **Target position resolution must be sector-aware:** `ResolveTargetPosition`
   (and anything else that feeds the autopilot) must not return another
   sector's coordinates as if local. Return the routed waypoint instead, or
   expose the target sector so callers route.
3. **The pod docks on arrival:** the server's rescue/land path already lands
   a pod at a rescue-capable station (P301/P540 live proof). Verify it
   triggers at the end of a multi-warp trip.
4. Keep: manual-input disengage, the engage sound, and HUD/autopilot state.

## Explicitly out of scope

- Target choice (P540 already picks correctly).
- Server-side NPC routing.
- Star-map click behaviour (P541).

## Verification

- **Failing-first unit tests:** with the own ship in sector A and a station
  target in sector B, the autopilot steering goal is the next warp toward B
  (not the station's coordinates in A), and once in B it's the station.
  Same-sector targets are unchanged.
- **Live proof** (loopback/local server, real client session): eject in a
  sector **two warps** from the nearest rescue station. The pod warps
  through both and docks via the rescue path, with zero SHIP_RESETs and
  zero `[ctrlak]` rejects. Quote the client and server lines (target chosen
  → warp transits → `[rescue]`/`[lifepod] … docked`).
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and live proof are done, BEFORE the
  3× suite**, then amend with the suite numbers. **Include every new file
  you created** (check `git status --untracked-files=all`). A previous
  commit missed new files and broke clean checkouts. Confirm with
  `git log -1` and a clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P540b_AUTOPILOT_CROSS_SECTOR.md`: root cause with
   file:line, native citation, the fix, tests and live proof.
