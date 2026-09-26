# P365 — Engine nozzle glow decal (color-shifts with thrust, distinct from P358's exhaust particles)

## Why this task exists

Same `thinggeo.cpp` sweep that found P364. This is a genuinely
separate visual system from P358's engine exhaust particle trail —
both are triggered by the ship thrusting, but they're different
effects at different hardpoints:

- P358 (`AddThrustParticles`, `thinggeo.cpp:758+`) draws particle puffs
  at hardpoints named containing **"smoke"** or **"rocket"**.
- **This task** (`RenderGlow`, `thinggeo.cpp:1419-1443`) draws a real
  flat glow-decal sprite at every hardpoint whose name contains
  **"thrust"** (the `thrust`/`rthrust`/`lthrust` hardpoints — a
  DIFFERENT set of hardpoints than P358 uses):
  ```
  void RenderGlow(Context* pcontext)
  {
      if ((m_scaleGlow > 0) && m_pframes)
      {
          // for every hardpoint frame whose name contains "thrust":
          pcontext->DrawDecal(
              m_pimageGlow->GetSurface(),
              m_colorGlow,
              m_vecPosition + TransformOffset(data.m_vecPosition),
              Vector::GetZero(), Vector::GetZero(),
              m_scaleGlow * m_scale,
              0
          );
      }
  }
  ```
  This is the actual glowing-engine-nozzle look (a static decal image,
  tinted and scaled) — NOT particle puffs.

**The color and intensity are driven by real thrust magnitude**
(`UpdateGlow`, `thinggeo.cpp:440-460`):
```
float value = m_thrust;              // normalized 0..100, or forced to
if (m_fThrustPower != 0) value = 1.0f;  // 1.0 whenever actively thrusting

m_colorGlow = HSBColor(
    pow(value, 5) / 6.0f,             // hue shifts with thrust
    1 - 0.5f * (pow(value, 20)),      // saturation
    1 - pow(1 - value, 3)             // brightness
);
m_scaleGlow = 1 - pow(1 - value, 2);  // decal size also scales with thrust
```
This is a real color-shifting glow — dim/small at low thrust, bright/
large and shifted in hue at full thrust — not a fixed static color.

**Confirmed missing from this port**: no glow-decal/nozzle-glow
concept found anywhere in `Rendering/`.

## Scope

1. **Read the rest of `UpdateGlow`'s surrounding context** and
   `HSBColor`'s real HSB→RGB conversion (search native for its
   definition, likely a small utility class) to port the exact color
   math faithfully, not an approximation.
2. **Determine the real hardpoint positions** for the `thrust`/
   `rthrust`/`lthrust` hardpoints on this port's actual ship hulls —
   same category of question P358 already had to resolve for its own
   hardpoints (real per-hull extraction vs. an aft-point
   approximation). Check what P358 landed on (read its report,
   `P358_SHIP_ENGINE_TRAIL.md`, once it exists) and reuse the same
   approach for consistency if it's applicable, since these are
   different specific hardpoints on the same models.
3. **Build the glow decal rendering**: a simple textured quad/sprite
   (find or make a suitable glow texture — check if one already exists
   in this port's content, e.g. anything used for missile/explosion
   glows that could be reused, before creating a new asset) at each
   real thrust-nozzle hardpoint, tinted/scaled by the real HSB color
   math above, driven by the ship's actual real-time thrust state
   (the client already knows this — same data P358 uses).
4. Confirm it renders for any visible thrusting ship, not just the
   local player's own.

## Explicitly out of scope

- P358's exhaust particle plume — a separate, already-in-flight
  effect at DIFFERENT hardpoints (`smoke`/`rocket`-named, not
  `thrust`-named). Don't touch `ShipEngineTrailParticles.cs`/
  `ShipEngineTrailRenderer.cs`, this is an ADDITIONAL effect alongside
  it, not a replacement.
- P359-P364, P354-P357 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real client, thrust a real ship, confirm
  a real glow decal renders at the engine nozzle position(s), visibly
  changing color/size/intensity as thrust varies (compare idle vs. full
  thrust — the color shift should be real and visible, not a fixed
  static glow). Confirm it renders for a remote thrusting ship too.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The engine nozzle glow effect, committed on `master` (not pushed —
   Domingo pushes). Do not create a side branch.
2. Report doc `P365_ENGINE_NOZZLE_GLOW.md` in the repo root: the real
   HSB color math as ported, the hardpoint-position approach used, and
   the live verification performed (describe the actual color/size
   change observed between idle and full thrust).
