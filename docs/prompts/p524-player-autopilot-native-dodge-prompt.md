# P524 — player autopilot (G) steers around obstacles with native's dodge law (P489b)

## Why this task exists

**Domingo's live playtest (2026-10-03):** using autopilot (`G`) to reach the
nearest friendly base, "auto pilot found it finally". The route was slow and
indirect.

**A known, named gap:** P489 (`29715303`,
`docs/reports/P489_CLIENT_BOT_DODGE.md` §5, lines ~39-43) found the MonoGame
client's `G` autopilot is **obstacle-blind**. `AutopilotSteering.SteerToward`
(`src/Allegiance.ClientCore.Game/Flight/AutopilotSteering.cs:117`) flies a
straight line with no dodge term. P489 named this follow-up **P489b**,
"a small change once the shared module + ClientWorld feed are wired in".

**What exists already:**
- **The native dodge law, extracted:** `ShipDodgeLaw`
  (`src/Allegiance.Core/Sim/ShipDodgeLaw.cs`: `TryComputeDodgeGoal` :67,
  `IsImminent` :125, `SolveForImpact` :154). It's the P168/P170 port of
  native `Igc/common.cpp:1920-2003`, used **byte-identically** by the server
  NPC autopilot (`Ship.TryComputeDodgeGoal`) and by the client bots.
- **The bot feed** from a `ClientWorld`: `BotManager.TryDodgeGoal`
  (`tools/allegiance-mcp/BotManager.cs:~1273`), wired at `:1246`, which
  passes the asteroids, stations and known ships in the mover's cluster,
  skipping the current goal (native `pmodelSkip`).
- **Native players' autopilot uses the same goto/avoid law.** Native is at
  `~/Development/Allegiance/src/`. Verify where native's client autopilot
  calls the avoidance (`common.cpp` GotoPlan / dodge) and cite it.

## Scope

1. **Feed `ShipDodgeLaw` into the player autopilot.** In the `G` autopilot
   path (`AutopilotSteering.SteerToward` and its caller in
   `Game1.FlightInput.cs` ~1780-1830), compute a dodge goal from the
   client's `ClientWorld` (same-cluster asteroids, stations and visible
   ships; skip the current target, as native does) and steer to it when a
   collision is imminent, exactly as the bots do. **Share the feed code**
   with the bot path (move it into `Allegiance.ClientCore` if needed) rather
   than writing a third copy.
2. **Preserve the existing autopilot behaviour otherwise:** arrival radius,
   braking, manual-input disengage (P345/P356), and docking with a station
   target (a station you're flying *to* is the skipped goal, so the
   autopilot doesn't dodge its own destination).
3. **Native-model compatibility:** the autopilot output feeds P508's local
   sim, as manual input does. No server change.

## Explicitly out of scope

- Server NPC autopilot (unchanged, byte-identical).
- Bot steering (P489).
- Native GotoPlan's phased waypoints. The port deliberately uses flat
  single-goal steering (`Ship.cs` ~4715-4733).

## Verification

- **Failing first:** a pure test where the autopilot heads toward a target
  with an asteroid on the straight line steers away when the collision is
  imminent, and still arrives. The current code steers straight into it.
- **A live check:** a real client with `G` toward a friendly station across
  a dense field (for example Alderan's mining hub), recording minimum rock
  clearance and hull damage taken, versus straight-line before.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P524_PLAYER_AUTOPILOT_DODGE.md`.
