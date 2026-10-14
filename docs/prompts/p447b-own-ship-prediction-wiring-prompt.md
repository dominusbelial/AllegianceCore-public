# P447b — wire the own-ship predictor into the MonoGame client, plus a live latency proof

## Why this task exists

P447a (read `docs/reports/P447A_OWN_SHIP_PREDICTOR.md`) built a pure
`OwnShipPredictor`. It steps the local ship with the real flight model from
the controls the client sends each frame, and corrects smoothly toward the
server's authoritative state, snapping beyond native's 20 m threshold
(`~/Development/Allegiance/src/Igc/shipIGC.cpp:3176`).

The MonoGame client still draws the local ship and positions the camera
from the raw last `ServerShipUpdate`
(`src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs:~1035-1050`). The
controls are sent from the same method (`:~990-1000`), so the input reaches
the screen one round trip plus up to 50 ms late, at 20 Hz. Native runs its
own ship locally every frame (client-authoritative, see the P447a "Why").

## Scope

1. **Own a single `OwnShipPredictor` in `Game1`.**
   - Each frame, after computing the command `(controls, stateM)` that's
     sent through `_session.CommandControls` (manual *or* autopilot), call
     `Step(frameDt, sameControls, sameStateM)`. It must be exactly what was
     sent: same values, same frame.
   - Feed it `OnServerUpdate` whenever the own ship's `Latest` changes.
     Detect it under the session gate using `ShipEntity.LastUpdateTick`.
2. **Render from the predicted pose**: own hull, chase/cockpit/external
   cameras, nozzle glow, engine trail, and anything else currently reading
   `_poses[me]` or the own `mineUp.Position/Orientation`.
   - Keep P255's mining-facing override working on top of the predicted
     pose.
   - The HUD speed readout should use predicted velocity.
   - Leave server-truth HUD values alone (hull, shield, fuel, ammo and so
     on).
3. **Hard resets** from P447a's list (dock, undock, launch, warp/sector
   change, ripcord, death, lifepod, teleport) must be wired to real client
   events. After each one, confirm the predictor re-seeds from the next
   server pose. Test warp by flying through an aleph.
4. **Kill switch**: a `liveSettings` flag (P378's config section) that
   disables prediction and returns to raw poses, defaulting to on. Also log
   a periodic `[predict]` line with mean/max correction distance and the
   snap count, so live sessions can be diagnosed.

## Explicitly out of scope

- Remote ships (P448b). Weapon and projectile origin from the predicted
  pose, which is a named follow-up.
- Any server-side or wire change.
- Other queued work: P438–P440, P445, P446, P449.

## Verification

- **A test that fails on unfixed code first**: a client-level test with a
  fake or loopback session. Change the sent controls and assert the local
  render pose responds within the same frame. Before this change it
  responds only after the next server update. Record both.
- **Live latency proof, required.** Use the real server, either local or
  the LAN docker server on 192.168.0.221. Add artificial latency: `tc
  netem` on the docker host or loopback (for example 100 ms RTT, plus a run
  with ±20 ms jitter). The earlier netem setup is documented in the wiki
  page `allegiance-monogame-remote-latency-test`; `sudo` may be needed, and
  if you can't get it, say so rather than faking it. Then:
  - Measure input→screen response for a yaw step with prediction on vs off
    (kill switch), from client logs of when the command was sent and when
    the render orientation first moved.
  - Report correction statistics: mean and max distance, and snaps per
    minute during ordinary flight, afterburner, strafing and a warp.
  - Fly into an asteroid and report what happens: expect a brief
    penetration, then correction.
  - State plainly what you observed and what you couldn't test.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. Code and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P447B_OWN_SHIP_PREDICTION_LIVE.md`. Include the
   rerouted pose consumers, the hard-reset wiring, the netem setup used, the
   on/off latency numbers, the correction statistics, the collision
   observation, and the kill-switch name.
