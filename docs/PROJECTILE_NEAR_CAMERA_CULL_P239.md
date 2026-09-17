# P239 — Fix projectile bolts flooding the screen in cockpit view

## Relationship
Pure **MonoGame-client rendering fix** (Allegiance.ClientCore.MonoGame). Zero
change to `Allegiance.Core`/`Allegiance.Server` sim logic and zero change to the
wire. Landed directly on `master` (Domingo pushes). Closes a visual bug exposed
by the combination of P231 (projectile rendering) and P229 (default-to-cockpit).

## Root cause — confirmed by direct inspection + live repro
Weapon fire has **no muzzle offset** — an accepted, documented scope cut:
`Sim/Weapon.cs:230` spawns every projectile at the ship's own centre
(`myPosition = _ship.Position;  // + weapon muzzle offset (none: no hull mesh)`),
mirroring native's own no-hull-mesh fallback (`weaponIGC.cpp:203`).

The cockpit camera eye, however, sits **14 units forward** of that same centre
(`Rendering/CockpitCamera.cs` `_forwardOffset = 14f`, P211). Each projectile
renders as a **solid box 32–480 units long** centred on the spawn
(`Game1.cs` `MinBoltLength`/`MaxBoltLength`/`BoltLengthPerUnitSeconds`) with
**culling disabled** (`Rendering/ProjectileRenderer.cs` `CullMode.None`, so both
inner and outer faces draw). For the first moments after a shot spawns at the
ship centre, that box's enormous longitudinal extent therefore **contains the
cockpit eye** — the camera is briefly *inside* a solid amber box. Holding fire
(Space) keeps a steady stream spawning right on the eye ⇒ a persistent solid
"yellow" screen.

**Mechanism definitively classified as CAMERA-INSIDE-GEOMETRY** (not merely
near-clip slicing): for a mid-size bolt (e.g. 480 long, half 240) the eye at 14
units is deep inside the box in every axis (14 ≪ 240, and lateral offset ~0 <
box half-width 4). The near-clip variant (a bolt grazing the eye) is a
secondary flavour; both are handled by the same single fix.

## Fix chosen — near-camera cull of the bolt's oriented box (why)
In `Game1`'s projectile gather loop (the `foreach (var pj in
world.Projectiles)` block), skip drawing any bolt whose solid oriented box is
inside (or within a gap of) the **active camera eye**. This is the direct
first-person-shooter analogue of culling geometry that clips through the
camera. Implemented as a pure, unit-tested helper:

`Rendering/ProjectileCameraCull.cs :: ShouldCull(eye, pos, dir, length, width,
clearance)` — transforms the eye into the bolt's local frame
(`u = (eye−pos)·dir` longitudinal, `perp² = |eye−pos|² − u²` perpendicular) and
culls when `|u| ≤ length/2 + clearance && perp² ≤ (width/2 + clearance)²`.

Applied to **both camera modes** through the single active `cameraEye`
(chase-cam's eye is ~360 units behind the ship — `Game1.cs:359` `distanceBehind
350, heightAbove 90`, near plane 10 — so own bolts are outside the 240 half-box
and the cull is essentially never triggered there; consistent with chase never
having hit this in practice).

### Why clearance = 60 (the important tuning)
An initial clearance of 3 (a token "near-plane" margin) **still flooded one of
14 frames live** (frame-12: 67.8% amber, 0% black — the exact bug signature).
Cause: when a bolt first becomes drawable (eye just clears the box by 3 units),
its 8-unit-wide cross-section still subtends ~100° of the 75° FOV and fills the
view. 60 units ahead reduces that to ~7.6° — a clean small streak — while
delaying own-forward shots' first visible frame by only ~50 ms of flight (600
u/s). Distant bolts are untouched (their centres lie far outside any near gap).

## Missiles: verified NOT to need the same fix
Missile launch also has no muzzle offset (`Sim/Ship.cs:2339` `FireMissile`
"muzzle at origin"), so the same latent question arose. Measurement settles it:

- The rendered missile mesh is **tiny**: `mis05.mdl` (the P234 demo missile,
  type 34) parses to extents **1.73 × 1.73 × 11.35** units — maximum reach from
  centre ~5.7 units (measured by parsing the real .mdl).
- The cockpit eye is **14 units** forward of the missile's spawn (ship centre),
  so the eye is *always outside* the missile mesh, even at the instant of
  launch. The largest missile dimension (11.35) never reaches 14.
- Missiles are also drawn with back-face culling (`MissileRenderer.cs`
  `CullClockwiseFace`, not `CullNone`) and move at their real velocity,
  clearing the vicinity immediately.

Conclusion: missiles are **genuinely unaffected in practice** — no fix applied.
Only the synthetic 32–480-unit projectile bolt (whose huge box is centred on the
spawn and lingers over the eye) needed the cull.

## Out of scope (honoured)
- No change to `Weapon.cs` or `Ship.FireMissile` spawn positions (sim-level,
  documented simplification).
- No per-type projectile textures/colours (separate gap).
- Native reference `~/Development/Allegiance` untouched (read-only).
- P231/P234 core tracking/rendering logic untouched beyond the near-camera cull.

## Live screenshot verification (required — offscreen RenderTarget2D, cockpit view)
Reproduced the exact bug first, then confirmed the fix, both with the real
stack: temp server (teams:1, port 26908) + real MonoGame client direct-connect,
TEMP fight-hull spawn override + TEMP auto-fire (hold Fire ~22s past connect) +
offscreen RT captures (P207/P218 technique). Evidence saved to
`.verify_p239_bug/` and `.verify_p239_cockpit/`.

**BEFORE (bug, cull disabled):** 347 captured frames while holding fire.
`frame_00` is the clean pre-fire cockpit (black space + stars + HUD). Frames
`01–346` are **byte-identical** solid-amber floods: quantitative interior-band
analysis = **67.8% amber, 0% black** (the whole 3D view hidden behind the
opaque bolt box; HUD overlaid). Persistent full-screen yellow — the bug.

**AFTER (fix, clearance 60):** 14 captured frames while holding fire, all
distinct. Quantitative analysis: **max 0.6% amber, ~94% black space** across all
14 — never floods. Objective per-frame log (diagnostic removed before commit):
`camera=Cockpit`, `drawn=1–4` bolts/frame; frames with `drawn>0` carry 1000–2900
bright-amber pixels (the visible distant streak bolts), frames with `drawn=0`
carry 0. Bolts render as small receding streaks in the distance; the view stays
clear. All temp diagnostic edits reverted (`git diff` Server.cs EMPTY; Game1.cs
diff is only the intended constant + cull check).

## Build / test
- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors**.
- New unit tests: `tests/Allegiance.ClientCore.MonoGame.Tests/
  ProjectileCameraCullTests.cs` (11 tests, pure-logic, no graphics device):
  the real cockpit-spawn cull (eye 14 ahead, bolt at ship centre), the near-gap
  boundary both axially and laterally, rotation invariance, default-dir
  fallback. Pass in isolation.
- MonoGame.Tests: 216 → **226** (+10; +11 tests − 1 net? — 11 test methods added).
- Core.Tests / ClientCore.Tests baselines confirmed against the pre-task counts
  (see final summary for the live run numbers and the flake allowance).

## Citations
- `Sim/Weapon.cs:230` — projectile spawn at ship centre, "no muzzle offset".
- `Sim/Ship.cs:2339` — missile spawn at origin, no muzzle offset.
- `Rendering/CockpitCamera.cs` — `_forwardOffset = 14f`, eye 14 ahead of centre.
- `Rendering/ProjectileRenderer.cs` — CullMode.None (both faces drawn).
- `Game1.cs` bolt sizing constants (`MinBoltLength` 32 / `MaxBoltLength` 480 /
  `BoltLengthPerUnitSeconds` 0.12 / `ProjectileWidth` 8).
- `Sim/Ship.cs:2339` FireMissile "muzzle at origin"; `mis05.mdl` measured
  extents (1.73×1.73×11.35) — missile model too small to reach the 14-unit eye.
- Native: `weaponIGC.cpp:203` no-hull-mesh fallback (read-only reference).
