# P365 — Engine nozzle glow decal (color-shifts with thrust)

Native function ported: `ThingGeoImpl::RenderGlow` + its driver `UpdateGlow`,
`effect/thinggeo.cpp:1419-1443` and `:440-460` — the real flat glowing-nozzle
look, drawn as a static decal at every hardpoint frame whose name contains
`"thrust"`, tinted and scaled by the ship's live thrust. This is a genuinely
separate effect from P358's exhaust particle plume (`AddThrustParticles`,
`smoke`/`rocket`-named hardpoints): it is a single static glow-decal bloom at
the nozzle, not particle puffs. Distinct hardpoint set (`thrust`/`rthrust`/
`lthrust`, not `smoke`/`rocket`), same models.

## The real HSB colour math as ported (exact, not approximate)

Native `UpdateGlow` (thinggeo.cpp:440-460) computes, from a `value`:

    hue  = pow(value,5) / 6
    sat  = 1 - 0.5·pow(value,20)
    bri  = 1 - pow(1-value,3)
    scaleGlow = 1 - pow(1-value,2)
    colour = HSBColor(hue, sat, bri)     // alpha 1

where `value` = the ship's real throttle mapped to [0,1] by native
`SetThrust(0.5·(jsValues[3]+1))` (thinggeo.cpp:8008), forced to `1.0` whenever
the afterburner is engaged (`m_fThrustPower ≠ 0`, thinggeo.cpp:452-455). Native
clamps `value` into range defensively (thinggeo.cpp:444-447); the port clamps to
[0,1].

The HSB→RGB conversion is `Color::SetHSBA` (`src/zlib/color.cpp:25-33`) with its
helper `HueComponent` (`color.cpp:12-23`):

    SetHSBA: hue *= 6;  white = 1-sat;
             r = bri·(white + sat·HueComponent(hue))
             g = bri·(white + sat·HueComponent(hue-2))
             b = bri·(white + sat·HueComponent(hue+2))

    HueComponent(hue) = |(hue+3) mod 6 − 3|  →  1 on [0,1], linear 1→0 on [1,2], 0 after
    (mod = C fmod; C#'s % float remainder has the same sign-of-dividend semantics)

The port reproduces all of it verbatim in `Rendering/EngineNozzleGlow.cs`
(HueComponent, Hsba, Compute). Verified output at representative thrust values:

    value 0.00 -> (0.000,0.000,0.000)  scale 0.000   (bri 0, scale 0 -> native's
                                                      "do not render" gate,
                                                      RenderGlow:1421 m_scaleGlow>0)
    value 0.25 -> (0.578,0.001,0.000)  scale 0.438   dark red, small
    value 0.50 -> (0.875,0.027,0.000)  scale 0.750   red-orange, medium
    value 0.75 -> (0.984,0.235,0.002)  scale 0.938   orange-yellow, large
    value 1.00 -> (1.000,1.000,0.500)  scale 1.000   bright yellow-white, full size

So the glow is NOT a fixed static colour: brightness climbs monotonically while
hue swings red→yellow as thrust rises, exactly the real colour shift native shows.

## Hardpoint-position approach: (b) aft-centre approximation — consistent with P358

Option (a) — real per-hardpoint frame extraction for the `thrust`/`rthrust`/
`lthrust` frames — was investigated and, like P358's own conclusion for the same
models' `smoke`/`rocket` frames, judged not cleanly tractable here. This port
renders Meshy-regenerated content hulls (P287/P351-356), not the native `.mdl`
geometry; the native frames' local offsets live in a different coordinate frame
(native noses +Z vs this port's content noses −Z, plus arbitrary per-asset Meshy
scale). Mapping them across the 70+ content hulls would be fragile and would not
sit on the regenerated exhaust geometry.

Chose **(b) the hull's real aft-centre**: glow origin = `position +
backward·(radius·AftCenterFraction)`, `AftCenterFraction = 0.95`, with
`radius = 0.5 × HullType.Length` — the exact "faithful approximation when precise
native data isn't cleanly portable" precedent P358 (`ShipEngineTrailParticles`)
and `DamageDebrisParticles` (P297/P338) already use, and the same aft point as
the P358 exhaust emitter. It lands on the actually-rendered hull's aft, where the
native `thrust` frames sit (at the model aft, like the `smoke` frames at
≈1.05×radius), and reads unambiguously as the ship's engine nozzle.

Glow decal size is normalized to real world units: half-extent =
`scaleGlow·radius·GlowSizeFactor` (`GlowSizeFactor = 0.45`), so at full thrust the
bloom spans ≈0.9× the hull radius — a bright nozzle bloom on every hull class,
without engulfing the ship. (Native draws at `m_scaleGlow·m_scale` in native mesh
units; this is the same documented scale normalization P358 applies.)

The glow texture is the port's standard procedural soft radial sprite (white,
squared alpha falloff), the exact convention the five existing effect renderers
use. No dedicated glow texture exists in this port's content, so none was reused
(the brief's precondition — "check if one exists before creating a new asset" —
found none; creating a new binary asset would be the less consistent choice).

## Trigger / driver — the real already-on-the-wire thrust data (same as P358)

The glow is gated on native's `m_scaleGlow > 0` (i.e. `value > 0`), driven by the
same per-ship wire data P358 already proved the client holds for every visible
ship — no new wire data:

- `StateM.AfterburnerButton` (1024) — forced to `value = 1` while held.
- `Controls.Throttle` (axis [−1,1], `ServerShipUpdate.cs:46-50,114`) — mapped by
  native `SetThrust(0.5·(Throttle+1))` to `value` when not afterburning.

So an idling ship (throttle −1, no afterburner) shows no glow; a low throttle
shows a dim small red bloom; full throttle or afterburner shows a bright large
yellow-white bloom. Rendered for the local hull (non-cockpit, matching P358/P297)
AND every visible remote ship (native RenderGlow's per-ship scope).

## Implementation

- `Rendering/EngineNozzleGlow.cs` — pure, MonoGame-free port of the HSB colour
  math (HueComponent / Hsba / Compute).
- `Rendering/EngineNozzleGlowRenderer.cs` — camera-facing additive soft quad per
  glow (billboard right/up from the view matrix, additive soft blend, DepthRead,
  P339-B2 state restoration), tinted per-vertex by the HSB colour; procedural soft
  sprite. One `DrawUserIndexedPrimitives` call for all glows.
- `Game1.cs` — `_nozzleGlowRenderer`/`_nozzleGlowEmitters`/`_drawNozzleGlows`;
  `UpdateNozzleGlow` + `NozzleGlowValue` gathered under the Gate in the pose-gather
  pass (local ship gated to non-cockpit; every remote ship), drawn in the 3D pass
  right after the P358 exhaust trail (distinct effect alongside it).
- `tests/.../EngineNozzleGlowTests.cs` — 12 pure tests: exact HSB vectors at
  value 0/0.25/0.5/1, the no-render gate (scale 0 at value 0), monotonic
  brightness/size rise, value clamping, and the HSB colour-wheel sanity checks.

## Verification — real proof

Evidence: `.verify_p365/frames/` (PNGs + `p365_run.log`). Harness
(`tools/glow-proof/`, promoted from the scratch `.verify_p365/harness/`) reuses
P358's live-proof pattern: a real in-process
`Allegiance.Server` (XXV_simple) + two real `ConnectionSession` peers (ships
8096/8097, same side) on a realistically-sized fighter (Length 42.5 → radius
21.2), the local at X=−45 and remote at X=+45. Each ship's REAL server-side
`Controls.Throttle` + `StateM.AfterburnerButton` are driven (the exact data
`ServerShipUpdate.FromModel` quantizes onto the wire); the glow value → HSB colour
→ scale are computed from the real 47-byte wire payload, and the REAL
`EngineNozzleGlowRenderer` draws offscreen (960×640). Per-frame metrics: glow
count, per-half lit-pixel count (SIZE proxy), average R/G/B of lit pixels (HUE
proxy), PNG md5.

Results:

- Phase A (idle, throttle −1): `glows=0`, all frames byte-identical black
  baseline (md5 405F558AC020).
- Phase B1 (LOCAL low throttle, value 0.5): `glows=1`, left half lit 2414 px,
  avg RGB (92, 2) — strongly RED-dominant (G/R = 0.03). Vision: a small dim
  saturated-red bloom at the local ship's nozzle.
- Phase B2 (LOCAL afterburner, value 1): `glows=1`, left half lit 7429 px
  (> 2414 — LARGER), avg RGB (75, 75) — YELLOW (G/R = 1.00, vs B1's 0.03).
  Vision: a larger bright yellow-white bloom. → the real colour shift
  red→yellow and the real size growth between low and full thrust are both
  visibly and measurably confirmed.
- Phase C (REMOTE also afterburns): `glows=2`, right half lit 7425 px →
  the glow renders for a remote ship too, not just the local hull. Vision: two
  yellow-white blooms (left + right).
- Phase D (thrust stops on both): `glows=0`, lit 0, final md5 405F558AC020 =
  the EXACT Phase-A idle baseline → the glow disappears when thrust stops.

## Build / tests

- Whole-solution build: 0 warnings, 0 errors.
- `Allegiance.ClientCore.MonoGame.Tests`: 698 passed (12 new), twice (698/698).
- `dotnet test` full suite, 2 runs: all projects green except the pre-existing
  documented live-wire timing flakes in `Allegiance.Core.Tests` — run 1 had 12
  (LiveTurretGunnerBinding, LiveTreasurePickup, LiveRipcord, LiveRelaunch,
  CargoBayReload, + the confirmed-pre-existing deterministic
  `ProjectileBroadcastTests.Real_weapon_fire_broadcasts_projectile...`), run 2 a
  ROTATING different subset of 5 (LiveMoney, LiveTurretGunnerBinding,
  Multi_mission_disconnect, + that same ProjectileBroadcast). Rotating subset +
  varying count across runs = the documented env-flake signature; none touches
  this change, which adds zero code to `Allegiance.Core` (only the MonoGame
  client + its tests).
