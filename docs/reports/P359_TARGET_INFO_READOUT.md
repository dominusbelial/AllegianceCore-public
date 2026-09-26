# P359 — Target info readout: pilot name + health bar + bearing vector on the selection bracket

## What was built

A pure client-rendering addition to the MonoGame client. When the player's
`T`/`G` autopilot target (P212/P345) is a ship, the HUD now shows, beneath the
P345 selection bracket, that ship's pilot name and a small live hull/shield
health bar. Independently, a new bearing arrow in the bottom-right corner points
toward the selected target's real relative bearing from the camera for EVERY
targetable kind (ship/station/asteroid/warp), and stays visible even when the
target is off-screen. No new wire data, no server change.

### Files changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/TargetSelectionView.cs`
  The P345 bracket record struct now also carries `CharacterName`, `HullFraction`
  and `ShieldFraction` (all null for a non-ship target). Plain data; nulls are the
  "no readout" signal.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs`
  - `ComputeTargetSelectionBracket` now, under the existing `_session.Gate` lock,
    when the resolved target is a `TargetKind.Ship`, looks up its live `ShipEntity`
    in `world.Ships` and snapshots `CharacterName` + `HullFraction`/`ShieldFraction`
    from the same per-ship `ServerShipUpdate` the player's own gauges read. This
    works for any ship the server exported to this client — friendly or enemy —
    which is the same trust boundary every other HUD element already relies on.
    Station/asteroid/warp targets leave the fields null → no readout (native's own
    "if it is a ship" gate).
  - New `ComputeTargetBearing(view, proj, cameraEye)` computes the target's
    screen-space unit direction by treating the offset `(target − cameraEye)` as a
    pure direction (`w=0`) through `view*proj` — the resulting homogeneous point is
    the correct perspective azimuth/elevation, and the sign of `clip.W` folds the
    behind-the-camera case into the mirrored direction with no extra branch. It is
    deliberately NOT gated on the target being in the view frustum, so the arrow
    works exactly when the target is off-screen. A new field
    `_targetBearingScreenDir` carries it to the HUD each frame.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs`
  - `Draw` gained a `Vector2? targetBearing` param; draws the bearing arrow when set.
  - `DrawTargetSelectionBracket` now draws the readout beneath the bracket when the
    record carries a name: the pilot name in the existing HUD text colour, then a
    compact hull `DrawBar` (and a thinner shield bar beneath it). Reuses the exact
    `DrawBar(float fraction, float x, float y, float w, float h)` fraction-bar
    primitive the player's own gauges use — no new drawing routine.
  - New `DrawTargetBearingArrow`: a fixed-position cyan chevron (bottom-right, clear
    of the throttle/speed readouts) with a pivot dot and a `TGT` caption, fanned
    along the supplied bearing using the same rotated-thin-rect line primitive as the
    bracket. No new font, no new asset.

The only new files are the live-proof harness (below). Targeting logic itself
(`_autoTarget`, `T`/`G`) is untouched; out-of-scope distinctions (separate weapons
target, station/asteroid/warp readouts) are intentionally not addressed.

## Live verification

Harness: `tools/targetinfo-proof` — an offscreen MonoGame `Game` (renders to a
`RenderTarget2D`, headless on `:0`) that boots a REAL in-process server
(`XXV_simple.igc`), connects TWO real `ConnectionSession` clients (`Me_Fighter` →
Alpha, `Target_Enemy` → Bravo), and once the rendering client genuinely SEES the
cross-side target ship in its live world tables (real `CharacterName`, `Latest`
hull/shield fractions, position — all over the real wire, confirmed by the
`[xexport] side 0 <- peer 1 ship 8097` log), projects the target exactly like
`Game1.ComputeTargetSelectionBracket`/`ComputeTargetBearing` and renders the REAL
`HudRenderer.Draw` offscreen. Frames saved to `.verify_p359/`.

Observed, from the harness console + the rendered frames (verified visually):

1. **Ship target — real pilot name + full health bar.**
   `[proof] ME sees target: name='Target_Enemy' hull=1.000 shield=0.000
   pos=(1440,730,920)` → `p359_ship_healthy.png`: the cyan square selection bracket
   is drawn, with `Target_Enemy` in HUD text directly beneath it and a full-width
   hull bar (plus an empty shield-track bar) below the name. (A fighter has no
   shield in `PCore001`, hence `shield=0.000` — the shield bar renders as an empty
   track, no bogus fill.)

2. **Live damage drops the bar in real time.**
   The target ship is damaged through the server's real `Ship.ReceiveDamage` path
   (the same primitive real combat and the collision pass use), gated on the target
   having fully launched (a just-seated peer gets `RepairAndRefuel` at launch which
   resets the fraction, so damage must wait for undock — observed and fixed in the
   harness).
   `[proof] server hull 1.000 -> 0.467 after 1 damage shots` →
   `p359_ship_damaged.png` shows the hull bar now filled only ~half, matching the
   observed `0.467`. Because `HullFraction`/`ShieldFraction` are broadcast to every
   peer who can see the ship, the rendering client observed the drop with no extra
   synchronization work — exactly the free multiplayer behaviour the brief describes.

3. **Non-ship target degrades cleanly.**
   A station (`Garrison`) target renders the bracket with NO pilot name and NO
   health bars (clean degrade — no crash, no bogus empty bar), but DOES still show
   the bearing arrow: `p359_station_no_readout.png`.

4. **Bearing arrow points the right way when the target is off-screen.**
   A camera turned 45° away from the same real target (so it is outside the 30°
   half-FOV) gives `[proof] offscreen check: target-in-frustum=False, bearing=
   (-1.00,-0.00) (expect pointing LEFT => x<0)` — the bracket is null (out of view)
   but `p359_bearing_offscreen.png` shows the chevron pointing LEFT, i.e. toward the
   target's real bearing. Both the numerical check and the frame agree. (When the
   target is dead-centred in front of the camera the bearing is geometrically
   degenerate and falls back to a neutral down-pointer — the bracket/readout already
   show that target, so the arrow is only ever decisive off-axis, which is its point.)

## Test-suite status

- Whole solution build: 0 warnings, 0 errors.
- `dotnet test AllegianceCore.slnx` run multiple times: Lobby (12) and
  MonoGame.Tests (658) always pass; ClientCore.Tests 167–168 pass; Core.Tests
  3275–3276 of 3279 pass. Every failure is a documented live-wire timing flake with
  a rotating subset each run (LiveTurretGunnerBindingTests, Multi_mission_disconnect,
  ProjectileBroadcastTests, LiveRipcordTests, LiveMoneyTests, AsteroidsUpdateLive,
  LiveChatSectorWireTests, …). These are server-side sim/wire tests; this change
  touches only the MonoGame client rendering (not referenced by Core.Tests). A
  PRISTINE A/B (stashing this change and rerunning the flaky set on the untouched
  tree) reproduced the same failures — conclusively environmental, not a regression.
