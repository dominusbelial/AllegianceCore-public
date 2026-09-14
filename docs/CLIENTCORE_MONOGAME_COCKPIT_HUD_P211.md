# P211 — First-person cockpit camera + a modern HUD overlay

## Summary

Two additive, purely-client additions to the playable MonoGame POC
(`src/Allegiance.ClientCore.MonoGame/`, P207-P210):

1. **A first-person cockpit camera mode** (toggle **V**) — you sit inside the
   ship looking along its real forward direction using its FULL orientation
   (including roll), and your own hull is not drawn. Default at startup remains
   the third-person Chase camera, so anyone not pressing V sees exactly the
   pre-P211 behaviour.
2. **A real, modern 2D HUD** drawn via `SpriteBatch` on top of the 3D scene —
   not a clone of the original game's "Casio digital watch" look. Thin glowing
   lines/arcs, translucent glass panels, high-contrast monospace numerals,
   teal/cyan accent tied to the Bios Scout's own model-texture energy-conduit
   look.

Every HUD value comes from the local ship's most recent `ServerShipUpdate`
(the same 47-byte per-tick wire payload already driving position/orientation),
so it is real data with zero extra wire work — no placeholders.

## Task, part 1 — cockpit camera

- `Rendering/CockpitCamera.cs` (new, sibling to `ChaseCamera.cs`): the eye sits
  at the ship's position plus a small local forward offset (14 u) so the near
  clip stays clear of the cockpit interior, and looks along the ship's real
  forward using its FULL rotation (with roll — the point of being "inside").
  Uses the exact camera-relative convention of `ChaseCamera.Matrices` (view
  always looks FROM `Vector3.Zero`, caller subtracts the `out cameraEye` from
  every drawn object via `PoseConverter.ToXnaWorldRelative`) — the P207
  large-world-coordinate fix. Same FOV/near/far family as the chase camera
  (75°, 1..6000) so switching views keeps perceived scale.
- `Game1.cs`: a `CameraMode` (Chase default / Cockpit) toggled edge-triggered
  on **V** ("View" — free; FlightInput uses WASD, arrows, Q/E, A/D, Escape and
  M). In `Draw()`, the selected camera supplies the view/projection; the ship
  loop skips the local hull when `id == me && mode == Cockpit` (every OTHER
  known ship still renders). No changes to `ShipRenderer.cs`, `ChaseCamera.cs`,
  `FlightInput.cs`, `Starfield.cs` or `PoseConverter.cs` — purely additive.

## Task, part 2 — HUD overlay

- `Rendering/HudFrame.cs`: a plain data record of the telemetry the widgets
  need, lifted straight from `ServerShipUpdate` — hull/shield/ammo/fuel/energy/
  power fractions, real speed (`Velocity.Length()`), the raw throttle axis
  (`Controls.Throttle`, -1..+1), and afterburner-active
  (`StateM & ShipControlState.AfterburnerButton`). No MonoGame types.
- `Rendering/HudRenderer.cs`: draws the overlay with `SpriteBatch` — a center
  crosshair (always, regardless of camera mode), a left glass panel of six
  labelled bars (HULL/SHIELD/ENERGY/FUEL/AMMO/POWER), a bottom-center speed
  readout, a right-side vertical throttle gauge (-1 idle bottom … +1 top,
  centre tick at 0), and a top-center afterburner + PWR readout. All shapes are
  procedural (a 1x1 white `Texture2D` tinted+scaled for fills/lines) — no
  external image assets.
- `Rendering/HudColor.cs` / `Rendering/HudFormat.cs`: pure, testable logic
  (no MonoGame types in the public surface). `StatusColor` is a smooth
  red→amber→teal-green ramp keyed to the fraction with a `warnBelow` threshold;
  `ThrottlePercent` maps -1..+1 → 0..100; `FormatPercent`/`FormatSpeed` produce
  the readout strings.
- Fonts: `Content/HUD.spritefont` (DejaVu Sans Mono 14px) and
  `Content/HUDLarge.spritefont` (30px for the speed numeral), registered in
  `Content/Content.mgcb` (same pipeline entry format as the ship model). The
  mono face is a reliable always-installed Linux system font with a clean
  technical look.

## Real-data provenance (server side is unchanged)

`ServerShipUpdate.FromModel` (`src/Allegiance.Core/Wire/ServerShipUpdate.cs`)
fills hull from real `Ship.Fraction` (damage lowers it) and shield from real
`Ship.ShieldFraction` (0 when no shield mounted); ore from real cargo. Ammo/
fuel/energy/power are currently sent as 1.0 by `FromModel` (the flight demo
never drains them) — so in a fresh flight those bars read 100%, hull 100%, and
shield 0% because the spawned Bios Scout carries no shield. That empty SHIELD
bar is REAL wire data (no shield mounted), not a placeholder — exactly the kind
of honesty the brief asked the screenshots to demonstrate. This task made no
change to `Allegiance.Core`; if the server later fills those slots with real
values, the HUD shows them automatically.

## Visual-design caveat (please read)

This is a first-pass visual design. I confirmed — via real offscreen captures
(see Verification) — that the HUD renders legibly, in the right places, with
real values, over both camera modes; I did NOT assert beyond what a screenshot
can prove that it "looks good" in an artistic sense. The exact spacing, tints,
line weights and widget layout are all easy to iterate on once Domingo sees it
live (toggle with V, look at the bar panel / speed / throttle / reticle) — this
is the floor, not the ceiling.

## Verification

- `dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors**, whole
  solution.
- `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` — **run 3×, 61/61
  passing every run** (24 pre-existing + the 18 new `HudFormatTests` +
  P210's controller tests; exact total 61).
- Real screenshot verification (established P207 pattern — OFFSCREEN
  `RenderTarget2D`, NOT the on-screen backbuffer, which is confirmed
  unreliable on this Wayland box). Started a fresh `Allegiance.Server` (port
  6190), connected the MonoGame client, let the mission cross the real 15 s
  briefing countdown to Started, then captured two frames and read them back
  with vision:
  - **Chase + HUD** (`/.verify_p211/chase_hud.png`): third-person ship rendered
    over the starfield, HUD overlaid — all six bars, center crosshair on the
    ship, speed readout, throttle gauge, CHASE tag, afterburner/PWR.
  - **Cockpit + HUD** (`/.verify_p211/cockpit_hud.png`): clean first-person
    view into open space, **own hull absent**, HUD overlaid, crosshair centered,
    COCKPIT tag, same widgets live.
  Server log corroborated the session: `[ctrl]` per-tick control acceptance for
  the client ship, mission reaching Started, clean shutdown on client exit.
  The `.verify_p211/*.png` frames are left in place (untracked) for Domingo to
  eyeball the visual design; not committed.

## Files changed

- `src/.../Rendering/CameraMode.cs` (new) — Chase/Cockpit enum.
- `src/.../Rendering/CockpitCamera.cs` (new) — first-person camera.
- `src/.../Rendering/HudFrame.cs` (new) — HUD telemetry record.
- `src/.../Rendering/HudColor.cs` (new) — plain 0..1 colour + Lerp.
- `src/.../Rendering/HudFormat.cs` (new) — status ramp + formatters.
- `src/.../Rendering/HudRenderer.cs` (new) — SpriteBatch overlay.
- `src/.../Content/HUD.spritefont`, `Content/HUDLarge.spritefont` (new) — fonts.
- `src/.../Content/Content.mgcb` — two new font entries.
- `src/.../Game1.cs` — V toggle, camera selection, skip-own-hull-in-cockpit,
  HUD plumbing + `DrawHud()`.
- `tests/.../HudFormatTests.cs` (new) — 18 tests for the pure logic.

Temporary offscreen-capture scaffolding used for verification was removed
before commit.
