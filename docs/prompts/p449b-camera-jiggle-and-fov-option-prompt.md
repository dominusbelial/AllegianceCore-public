# P449b — native camera jiggle (afterburner + impacts) and a flight-FOV setting

## Why this task exists

The gameplay-parity pass found that the MonoGame camera has no physical
feedback at all. Native shakes the view under afterburner and on impacts:

- **Jiggle**, `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:374-399`:
  - An accumulator `m_jiggle` decays with a 0.25 s half-life
    (`m_jiggle *= pow(0.25, dt)`).
  - Each frame, if the own ship has an afterburner or `m_jiggle > 1`, then
    `power = m_jiggle + afterburner->GetPower()`.
  - The camera orientation gets a random yaw and pitch of up to
    `0.5° · power` in each axis.
  - `SetJiggle(j)` replaces the accumulator (`WinTrek.cpp:504-508`).
- **Impact triggers**: `PlayVisualEffect(effectJiggle, ship, …)`, handled at
  `WinTrek/trekigc.cpp:4994-5001` for the own ship only. It's called on
  collisions with intensity `2·damage·c_impactJiggleCoefficient`, where the
  coefficient is `1/20` (`Igc/shipIGC.cpp:624, 836, 1006, 1191-1195`).
- **FOV**: native's default is **50°** (`WinTrek/WinTrek.h:26`,
  `s_fDefaultFOV = RadiansFromDegrees(50.0f)`). The port uses **75°** in
  flight (`src/Allegiance.ClientCore.MonoGame/Game1.Lifecycle.cs:118`,
  `_debugMode ? 50f : 75f`). The `ChaseCamera.cs:46-61` doc calls this
  deliberate ("wider FOV measurably increases perceived depth and speed").
  **Domingo has decided: match native, default 50°.** This task changes the
  flight default to 50° and makes it configurable. Update the
  `ChaseCamera.cs` doc comment to match.
- **The port**: no jiggle or shake anywhere in the camera code
  (`Rendering/ChaseCamera.cs`, `Rendering/CockpitCamera.cs`).
  - The own ship's afterburner power *is* on the wire:
    `ServerShipUpdate.Power`, byte 46
    (`src/Allegiance.Core/Wire/ServerShipUpdate.cs:89`). It already drives
    P358's nozzle-glow ramp.
  - There's **no client-side collision event**. Collisions are resolved on
    the server, and no wire message tells the client "you collided, damage
    X".

## Scope

1. **A pure jiggle model**, a small testable class: an accumulator with
   native's 0.25 half-life decay, `power = jiggle + abPower`, and a random
   yaw/pitch offset of up to 0.5°·power. Use an injectable RNG for tests.
2. **Apply it to the camera orientation** in chase and cockpit modes (native
   applies it to the camera, `Apply(dt, bJiggle, …)`). It's visual only:
   aiming reticles and the lead indicator must not shake with the view
   unless native's did. Check how native projects the HUD against the
   jiggled camera and match it.
3. **Afterburner source**: the own ship's afterburner power from the pose
   source the camera uses (`ServerShipUpdate.Power`, or the P447b predictor
   if it has landed). Apply only when an afterburner is mounted, as native
   does.
4. **Impact source**: investigate whether a real per-collision signal can
   reach the client.
   - If the server already sends something distinguishable (a collision
     sound cue, a damage message with a collision source), use it with
     native's intensity formula.
   - If not, **don't invent one from hull-fraction drops**: weapon hits
     don't jiggle in native. Document the gap and scope the smallest wire
     change as a named follow-up task in the report.
5. **FOV setting**: a `liveSettings` value (P378 section), for example
   `flightFovDegrees`, **default 50 (native `s_fDefaultFOV`)** in both
   normal and debug mode, which is already 50. Replace the
   `_debugMode ? 50f : 75f` expression (`Game1.Lifecycle.cs:118`) with the
   setting. Check any camera-framing or target-projection code or test that
   assumes 75° and update it.

## Explicitly out of scope

- Space dust (P449a). The aleph proximity FOV squeeze
  (`WinTrek.cpp:405-420`), which is a possible later task.
- Force feedback (`PlayFFEffect`).
- Other queued work: P438–P440, P445–P448.

## Verification

- Pure tests (these fail on unfixed code because the model doesn't exist):
  - Decay follows the 0.25 half-life.
  - The offset magnitude is bounded by 0.5°·power.
  - No offset with no afterburner and a jiggle ≤ 1.
  - `SetJiggle` replaces the accumulator.
- A client-level check that afterburner power > 0 produces a non-zero camera
  offset and power 0 produces none.
- A live check if possible: fly with afterburner and describe the shake.
- A config test that the FOV value is read and applied, and that the default is 50°.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. Code and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P449B_CAMERA_JIGGLE_FOV.md`. Include the HUD
   and jiggle interaction decision, the impact-signal finding (and the
   follow-up scope if no signal exists), and the FOV setting name with the
   native 50° note.
