# P575 — autopilot and bots must DOCK like native: port `Waypoint::GetGotoPosition`'s station case + `DoApproach` (line up on a landing bay, face its entry direction, roll, enter), as ONE shared routine used by the player autopilot, eject/pod-home, bots and NPC GotoPlan, instead of flying straight at the station centre

## Why this task exists

**Domingo:** "some of the ships are getting stuck on the station outer ring"
and asked "does our autopilot self-dock?". Claude traced it (2026-10-07):

**Port today: straight at the centre.**
- Client autopilot: `src/Allegiance.ClientCore.Game/Flight/AutopilotSteering.cs`
  (`AutopilotArrivalRadius = 60f`, `:44`) steers at the target position and
  disengages "arrived" within 60 u of a stationary target
  (`Game1.FlightInput.cs:~1939-1984`). For a station, that's its centre.
- Bot pods: `tools/allegiance-mcp/BotManager.cs:~2084-2127` ("In-sector: fly at
  the station centre to reach the dock footprint").
- NPC GotoPlan: `src/Allegiance.Core/Sim/Ship.cs:~4690-4710` is "a deliberate
  radical simplification of native GotoPlan::SetControls" (turn toward the
  target's position).
- **Docking itself is native and correct**: the server's in-bay test
  (`src/Allegiance.Core/Sim/Station.cs:~292-300`, native `stationIGC.cpp:390-420`,
  `InGarage`) uses the real bay geometry (`Collision/HullLandingBay.cs`,
  `StationLandingBayCache.cs`; P550 Land/CapLand gates). So a ship docks only
  when the straight line happens to enter a bay. Otherwise it grinds on the
  hull/ring. Eject-home did dock once live (client log: `eject: autopilot home →
  Garrison` then `docked: hangar`), but that depends on geometry.

**Native: a real bay approach.** `Igc/common.cpp`:
- `Waypoint::GetGotoPosition` (`:1324+`), `case OT_station` (`:~1505-1545`,
  "Trying to dock …"): `nLand = fighter ? pst->GetLandSlots() :
  pst->GetCapLandSlots()`; if 0, "power glide in" (`c_gpmFast` at the centre,
  skip collision with the target); else gather each slot's
  `GetLandPosition(i,0)` / `GetLandDirection(i,0)` × station orientation, and
  `DoApproach(...)`; if `c_gpmEnter`, add `c_gpmRoll`.
- `Waypoint::DoApproach` (`:1157-1320`): choose the bay, stage outside it,
  face the entry direction, then enter (`c_gpmEnter | c_gpmNoDodge`, `:1308`).
  Mask constants at `:1140-1150`.
- The same structure for carriers (`OT_ship`, `:~1440-1490`, `phtCarrier` land
  slots), for completeness.
- How the mask turns into controls: `GotoPlan::SetControls`
  (`common.cpp:~1830-2000+`). Port what's needed for the approach and entry
  (facing, roll, speed near the bay), not the whole autopilot rewrite.

## Scope

1. A shared, pure, testable **station-approach planner** in
   `src/Allegiance.Core` (e.g. `Sim/DockApproach.cs`) porting
   `GetGotoPosition`'s station/carrier cases + `DoApproach` 1:1 (bay choice,
   staging point, facing vector, enter/roll/no-dodge/fast flags), taking the
   bay data the port already loads (`HullLandingBay`/`StationLandingBayCache`)
   and the hull's fighter/capital capability (LandSlots vs CapLandSlots,
   matching P550's gates).
2. **Client autopilot:** when the target is a friendly dockable station (or the
   eject/pod home target, P540/P571), follow the planner: approach the bay,
   enter, keep autopilot on until the server docks the ship (no "arrived"
   disengage at 60 u of the centre). Native player autopilot docking UX: check
   `WinTrek`/`clintlib` for any message/sound on auto-dock and match it.
3. **Bots** (`BotManager` pod-home, relaunched bots, any "go dock" path) and
   **NPC GotoPlan** (`Ship.cs` plan steering for drones returning to base) use
   the same planner.
4. Stations with no bays for that hull class: native "power glide in".

**Player autopilot cases to cover explicitly (Domingo: "if I target my own
bases and use autopilot it should use the same logic to dock, correct?" Yes):**
- Target your own (or an allied, dockable) station in the **same sector** and
  engage autopilot → the bay approach → docked, unattended.
- Target it from **another sector** (star-map goto, P541): route through the
  alephs, then hand over to the bay approach on arrival in the station's sector.
- **Manual input cancels** as today ("autopilot OFF (manual input)");
  re-engaging recomputes the best bay from the current position.
- Not dockable for this hull (no bay of the right class, enemy station): native
  behaviour (power glide / plain goto), no false "dock" attempt.
Live proof must include the player cases (same-sector and cross-sector), not
only bots.

## Out of scope

The full native `GotoPlan::SetControls` physics rewrite, dodge (P-later), and
changing the server's dock test (already native).

## Proof

- Failing-first unit tests: bay selection and the staging point vs native
  maths for a station type from the 221 core (fighter vs capital slots), the
  enter/roll flags, the no-bay power-glide case.
- **Live, real window (P572 `live-proof.sh` / `--auto-*`):** the player
  autopilot docks unattended at **every station type present on 221's map**,
  starting from 3 directions (including behind the station, where the
  straight line hits the ring). Frame strips showing line-up → entry → hangar
  screen; zero ring collisions (server collision log lines).
- **Bots:** a showcase run with pods sent home: count docked vs stuck, before
  and after (server `[dock]`/collision lines, MCP log).
- Report `docs/reports/P575_NATIVE_DOCK_APPROACH.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
