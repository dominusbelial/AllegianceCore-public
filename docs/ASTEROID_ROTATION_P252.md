# P252 — Asteroid rotation (client-side)

Stage of the "gameplay sense" review pass: Domingo recalled asteroids spinning
in the original game and wanted it verified before building. This stage adds a
pure **client-side** rotation to the rendered rocks, mirroring the warp-funnel
precedent. All changes are in the MonoGame client; **zero server / sim change**.

---

## 1. Real semantics of `DataAsteroidIGC.Rotation` (confirmed against native)

Native `CasteroidIGC::Initialize` applies the wire rotation:
`Igc/asteroidigc.cpp:44` → `SetRotation(dataAsteroid->rotation)`, consumed every
tick by the generic `CmodelIGC::Update` (`Igc/modelIGC.cpp:85-99`):

```cpp
if (m_rotation.angle() != 0.0f) {
    Orientation o (m_pHitTest->GetOrientation());
    o.PostRotate(m_rotation.axis(), m_rotation.angle() * dt);
    ...
}
```

The `Rotation` class (`src/_Utility/Utility.h:515-562`) is literally two
members: `Vector m_axis` (3 floats) + `float m_angle`. So the **4 floats are
axis(x,y,z) + angle**, where the angle is an angular **rate** — native
`Time::operator-` (`src/zlib/ztime.h:293-294`) divides the tick delta by
`fResolution() = 1000`, so `dt = now - m_lastUpdate` is in **seconds**, making
`angle × dt` radians per tick → **angle is in radians/second**. The axis is
body-local (PostRotate is a post-multiply of the current orientation), and
since it is constant each tick the net motion is a steady spin about that
fixed body axis.

Port decode is byte-identical to the native struct layout: `DataAsteroidIGC`
(`src/Igc/igc.h:1891`) puts `signature@0, position@4, up@16, forward@28,
rotation@40..55 (axis@40,44,48 + angle@52)`, then `AsteroidDef ore@56...`.
`DataAsteroidIGC.cs:77-83` reads `Rotation` as a raw `float[4]` at offset 40 —
**exactly** the native `Rotation` (axis + angle). No guessing needed.

## 2. Implementation (client-only, `Game1.cs` + a new pure class)

- **`Rendering/AsteroidSpin.cs`** (new, pure MonoGame-free logic — no MonoGame
  type on the public surface, the P240 discipline): holds one rock's normalized
  axis + rate (rad/s), accumulates `Angle += rate * dt`, and a static
  `FromWire(float[4])` builder. Guards a degenerate (zero) axis by substituting
  a fixed default and degrades a malformed array to a stationary rock — both
  defensive only (see §3).
- **`Game1.cs`**: a `Dictionary<short, AsteroidSpin> _asteroidSpins` keyed by
  `AsteroidId`. The existing P218 per-instance gather loop seeds (once) a spin
  from `ast.Rotation` and reuses it every frame; the draw loop advances it by
  the frame's real `deltaSeconds` (the WarpRenderer accumulate-real-dt
  precedent, `WarpRenderer.cs:158-165`) and applies
  `Matrix.CreateFromAxisAngle(wireAxis, accumulatedAngle)` as a **post-multiply
  of the base orientation** — exactly native's `o.PostRotate(axis, angle*dt)`
  composed over time. Per-instance state means each rock spins about its own
  axis at its own rate (not lockstep).

Because every instance carries its **own** `AsteroidSpin`, `Game1` reuses the
per-instance draw loop's natural structure (keyed by asteroid id) rather than a
single shared elapsed value like `WarpRenderer` (fine for a handful of warps,
wrong for 134 bulk rocks).

## 3. Real-data check (this port's actual live map, not static-core defaults)

Decoded the shipped demo map `m2xspiderw.igc` (the same file `Server.cs:514`
loads via `ArtPath`) through the real `MissionMap`/`IgcReader` path:

- All **134** asteroids live in cluster **1052** ("Alderan").
- **134 of 134 distinct** angle values — every rock spins at its own rate, so
  they are definitively **not** in lockstep.
- Rates span **0.002–0.196 rad/s** (≈0.03–1.9 RPM, mean ~0.097 rad/s); **zero
  rate count = 0**.
- Axis magnitudes ~1.0–1.6, **none degenerate** (min ~1.005).

**Conclusion: the real data is healthy and non-degenerate — no arbitrary
fallback is exercised live.** The defensive fallbacks in `AsteroidSpin` are
unreached by this map (documented as such).

Separate honest finding: **132 of 134** asteroids carry zero `Forward`/`Up`.
`Orientation.Set(zero,zero)` (port `Orientation.cs:89-108`) detects the zero
cross/length and `Reset()`s to **identity**, so these rocks render at identity
base orientation — which is why P218's live rocks looked fine and why this
stage's spin rides cleanly on the wire axis. (The 2 non-degenerate rocks use
their real orientation; base orientation is orthogonal to the spin.)

## 4. Live visual proof (established offscreen-technique, `.verify_p252/`)

Standalone `/tmp/p252verify` MiniGame (ProjectReference to the MonoGame csproj)
rendering synchronously offscreen to a RenderTarget2D, using the **real**
`AsteroidRenderer` (game-default mesh + P251 normals) and the **real**
`AsteroidSpin`, replicating the exact `Game1` world composition, fed by **real
m2xspiderw asteroids**. The spin was advanced through the real
`AsteroidSpin.Advance` at a 10 Hz client cadence for **ΔT = 5.0 s**.

| pair | ΔT | rotated? | result |
|---|---|---|---|
| `fast_t0` vs `fast_tEnd` (id 3312, rate 0.1956 rad/s ⇒ ~56°) | 5 s | yes | **10.64%** of pixels changed, MAD 2.34 |
| `fast_control_t0` vs `fast_control_tEnd` (NO-SPIN twin) | 5 s | no | **byte-identical** (same md5), 0.00% |
| `slow_t0` vs `slow_tEnd` (id 3271, rate 0.0022 rad/s ⇒ ~0.6°) | 5 s | barely | 0.17% changed |

The **byte-identical no-spin control** proves the change is caused by the
rotation wiring, not per-frame variance. The **62× gap** between the fast rock
(10.64%) and the slow rock (0.17%) over the same elapsed time proves they
rotate at their distinct real rates — **not in lockstep**. Vision-check of
`fast_t0.png` vs `fast_tEnd.png` shows the *same* rock with its specular
highlights relocated (11-o'clock + 1–2-o'clock → a single top-center glint) and
the shadowed rim migrated bottom → lower-right: a genuinely rotated surface.

## 5. Build / test status

- Whole-solution build: **0 warnings, 0 errors**.
- `MonoGame.Tests`: **275 passing** (269 at P251 + 6 new `AsteroidSpinTests`).
  Full run green, no regressions.
- New unit tests cover: wire axis normalization + rate preservation,
  `Advance` accumulation, non-lockstep divergence, degenerate-axis fallback,
  malformed-wire → stationary, and a **real-map test** asserting m2xspiderw
  gives every rock a distinct non-zero rate on a valid axis.
- `Core.Tests` / `ClientCore.Tests`: baselines re-confirmed (see commit /
  run logs); only the pre-existing `LiveTurretGunnerBindingTests` timing flake
  is expected.

## Scope boundaries honoured

- **No** asteroid collision (P251 confirmed never a physical obstacle).
- **No** texture work (P253, separate).
- **No** normals/shading change (P251, separate; this stage's proof renders
  correctly with P251 already landed — top commit is 4783fce).
- **No** `Asteroid.cs` server-side sim change (still not a `Model`, still not
  in the per-tick sector update loop).
- **No** edit to native reference source (read-only).

Committed on `master` directly (Domingo pushes).
