# P448b — render remote ships through the P448a dead reckoner

## Why this task exists

P448a (read its report: `docs/reports/P448A_KINEMATICS_DEAD_RECKONER.md`)
built `ShipKinematics` and a pure `ShipDeadReckoner` that replay the real
flight model from each server update to "now". That's the port of native
`CalculateShip`/`WarpShip`
(`~/Development/Allegiance/src/Igc/shipIGC.cpp:3244-3297`).

The MonoGame client still draws every remote ship from its raw last-known
`ServerShipUpdate`:
- `src/Allegiance.ClientCore.MonoGame/Flight/PredictedPose.cs`.
- The pose rebuild loop in `Game1.FlightInput.cs` starting around line
  1005 ("Recompute every ship's render pose from its raw last-known
  ServerShipUpdate").

At the default 20 upd/s, enemies visibly step, and every remote ship is drawn
about one packet in the past, which hurts aiming and leading.

**History to respect:** a previous `RemoteShipPredictor` (a blend/lerp
smoother) was removed on 2026-09-14 because it made ships flash, teleport
and ghost with up to 21 ships in flight. That was confirmed live. See the
P448a report for why physics replay differs. This task must prove it
doesn't bring those artifacts back.

## Scope

1. **Per remote ship, keep a `ShipDeadReckoner`.** Re-seed it on each new
   pose update for that ship: `SingleShipUpdate`, the heavy/light ships
   updates, and SET_CLUSTER. Find every `ShipEntity.Latest` write in
   `ClientWorld` (`:841`, `:918`, `:985`) and stamp the local arrival time.
   Keep the reckoner state out of `ClientWorld`'s wire-truth fields, or
   clearly separate it.
2. **Render remote ships at `PoseAt(now)`** instead of the raw pose. This
   covers the hull draw plus anything attached to the rendered pose: engine
   trails, nozzle glow, damage emitters, name/health brackets, radar
   position and target-selection projection. Find each consumer of `_poses`
   and route it through the same pose so nothing detaches from the hull.
3. **Leave the local player's own ship on its current raw pose.** That's
   P447. Leave ships with `IsNotVisible`/`IsGone`/dead state (P370) alone,
   and don't reckon a ship with no pose.
4. **Correction on re-seed**: a new update may disagree with the
   extrapolated pose. First measure the typical jump in the live test (log
   max/mean correction distance). Only if it's visibly large, add a short
   visual-only error decay, for example over 100 ms, with the error offset
   decaying and not a lerp between stale poses. Document which you did and
   why.
5. **Kill switch**: a `liveSettings` flag (P378's config section, find it)
   to turn reckoning off and return to raw poses, defaulting to on. This
   keeps an A/B comparison cheap for Domingo.

## Explicitly out of scope

- Own-ship prediction (P447a/b).
- Changing the server update rate or wire format.
- vizfight rendering. It's a separate visualizer, so leave it on raw poses.
- Other queued work: P438–P440, P445, P446, P449.

## Verification

- **A test that fails on unfixed code first**: a client-level test.
  - Feed a scripted sequence of real `ServerShipUpdate`s (from a server-side
    `Ship` flying a constant turn, sampled at 20 Hz) into `ClientWorld`.
  - Query the render pose at mid-interval times.
  - Assert it's within a small tolerance of the true ship pose at that
    instant.
  - Before the change, the render pose equals the last packet (the stale
    baseline). Record both numbers.
- **Live proof against the real server**, loopback or the LAN docker
  server:
  - Spawn a few MCP or vizfight bots flying combat patterns, plus a
    MonoGame client.
  - Capture evidence that remote ships move smoothly: log per-frame
    render-pose deltas for one remote ship, showing a steady per-frame step
    instead of 20 Hz jumps, and log correction distances on re-seed.
  - Check explicitly for the old failure modes: no flashing, no
    teleporting, no ghosting with 10+ ships. Say plainly what you observed.
  - If you can't do a live run in this session, say so. Don't claim it.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. Code and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P448B_REMOTE_SHIP_DEAD_RECKONING.md`. Include
   before/after numbers, the live evidence and correction-distance
   statistics, which pose consumers were rerouted, and the kill-switch name.
