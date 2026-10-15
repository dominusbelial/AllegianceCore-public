# P489 — client bots steer with native's dodge law (shared with the server NPC autopilot), so they cross asteroid-field hubs

## Why this task exists

P484 (`769ca118`, `docs/reports/P484_ALEPH_PARKING_MULTIHOP.md` §5) proved the
long-standing "bots park at an aleph" stall (P429, P474) has **one cause**:
the MCP/vizfight client bots fly an **obstacle-blind straight line** to the
warp. In Alderan's dense mining-hub field they hit rocks, and the server
faithfully applies native impact damage, `2·|Δv|²/128` (`Sector.cs`
`ResolveShipAsteroidCollision` ~2203-2206, native `shipIGC.cpp:948-958`),
plus an elastic bounce. The bot never reaches the aleph's transit radius.
With the asteroids stripped, the same flight crosses cleanly. Warp export,
transit and routing were all proven correct.

**Server-side drones cross the same field fine** (P484's
`MultiHopDroneLiveTests`), because the server NPC autopilot dodges. That's
`Ship.TryComputeDodgeGoal` (`src/Allegiance.Core/Sim/Ship.cs:4735+`, with the
imminence gate at ~4865, used at ~4518), the P168/P170 port of native's
avoidance in `Igc/common.cpp:1920-2003`: avoid every pickable model, skip
the current navigation goal, use radius `GetRadius()+1`, and narrow to
ships only in fine-manoeuvre phases. The client bots don't use it: they use
`BotManager.CombatFlyTo` (`tools/allegiance-mcp/BotManager.cs:1205`), and for
the warp approach `CombatFlyTo(bot, warpPos, 200f, 450f, 0)` at `:1389`.

As a result, P485 (`701cf14b`) could not complete the live proofs of **P450**
(capture) or **P455** (elimination). The troop transport keeps being bounced
by the field.

## Scope

1. **Extract native's dodge law into a pure shared module**, following the
   precedent of P448a's `ShipKinematics` extraction: same math, testable
   without a live server. Its inputs are the mover's position, velocity and
   radius, the nearby obstacles (position, radius, velocity), and the goal
   to ignore. Its output is the dodge goal or none. `Ship.TryComputeDodgeGoal`
   must call it with **byte-identical server behaviour**: the existing
   P168/P170 dodge tests and `MultiHopDroneLiveTests` must stay green
   unchanged.
2. **Use it in the client bots.** In allegiance-mcp's flight helpers
   (`CombatFlyTo` and the warp approach), feed the module from the bot's own
   `ClientWorld`: asteroids, stations and known ships in its cluster.
   Do the same in vizfight's ported copy if it shares the code path. If it
   doesn't, name it as a follow-up.
3. **Live proof:** the P484 differential, inverted. Run the real 2-hop flight
   through **real** Alderan with asteroids present (P484's
   `ClientWarpApproachLiveTests` setup). Before the fix, it parks or ejects;
   after it, it transits both hops. Record minimum distance to the warp and
   the hull damage taken.
4. **Check the player autopilot.** Check whether the MonoGame client's own
   autopilot (`G`, P345/P356 `_autoPilotEngaged`) is also obstacle-blind.
   Native players' autopilot uses the same goto/avoid law. Report the
   finding. If the shared module makes it a small change, name it as
   follow-up **P489b**; don't change player flight in this task.

## Explicitly out of scope

- Changing the server NPC behaviour (byte-identical).
- Rerunning the parity presets (that's **P490**, next).
- Native `GotoPlan`'s phased waypoints. The port deliberately uses flat
  single-goal steering (see the comment at `Ship.cs:~4715-4733`).

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report the actual numbers you measured. Don't claim a pass rate you
  didn't observe.** P482's "0/20" claim didn't reproduce: it measured 1/15
  when re-run.
- **Commit before you finish.** The chain's guard stops on a missing commit,
  and P482's session ended without one.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`, then `git log -1` to confirm the commit
   exists.
2. The report `docs/reports/P489_CLIENT_BOT_DODGE.md`.
