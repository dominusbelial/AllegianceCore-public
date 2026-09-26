# P339 — Fix render-state leaks in the 3D draw pass (B1 + B2)

Port: C# `AllegianceCore`. Source of findings: read-only kimi-k3 review of the 3D
pipeline — `~/Development/allegiance-tools/reference/pi-3d-pipeline-review-kimik3.log`
section 4, findings **B1** and **B2**. Native reference
(`~/Development/Allegiance`) untouched (read-only). Other findings (B3–B5, U1–U3,
textures, retune sweep) are out of scope — separate tasks.

## B1 — the frame was NOT self-contained: depth was genuinely disabled at 3D-pass start

Finding: no explicit depth/blend/rasterizer state reset at the top of the Flying 3D
pass; the previous frame's last draw is a SpriteBatch HUD/overlay pass, and
SpriteBatch.Begin sets `DepthStencilState.None` / `BlendState.AlphaBlend` without
restoring on End(). The review flagged this as uncertain-but-suspicious because ships
demonstrably render correctly live, and instructed a live probe first.

### The B1 live probe (what it actually showed, pre-fix)

I added a temporary diagnostic that logged `GraphicsDevice.BlendState` /
`DepthStencilState` / `RasterizerState` at the exact top of the Flying 3D pass, then
flew a REAL MonoGame client (`vizfight proof` single-team scene + `--capture chase`,
real server 42595, real ConnectionSession). Every logged frame read:

```
[B1PROBE] 3Dpass-start frame=N blend=AlphaBlend depth=None raster=CullCounterClockwise
```

**The static read was correct.** Every frame starts the 3D pass with
`DepthStencilState.None` — depth testing AND writes disabled — for the opaque
starfield/ship/asteroid/station geometry (none of Starfield, ShipRenderer, or
AsteroidRenderer sets depth state). Why ships still "looked correct" live, resolving
the contradiction: opaque a=255 geometry still paints solid under `AlphaBlend` (the
blend equation reduces to src), masking the blend half; and the tested scenes are
sparse enough that painter's-order-with-no-depth rarely collides — so the depth
disability stayed latent. The bug was real but masked, exactly the review's second
hypothesis.

### The B1 fix

Explicit three-state reset at the top of the Flying 3D pass (Game1.cs), so the frame
is self-contained regardless of what the previous frame left behind:

```csharp
GraphicsDevice.BlendState = BlendState.Opaque;
GraphicsDevice.DepthStencilState = DepthStencilState.Default;
GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
```

Now depth is truly enabled for the whole opaque pass and the additive passes that
follow (Warps/Explosions with DepthRead) can rely on real occluders.

## B2 — the three additive particle renderers wrote depth and leaked state

Finding (confirmed): `MissileTrailRenderer.Draw`, `DamageDebrisRenderer.Draw`, and
`WarpTransitParticleRenderer.Draw` all used `DepthStencilState.Default` (depth writes
ON) for additive glow billboards and restored only `BlendState`, leaking the mutated
`DepthStencilState` and `RasterizerState.CullNone` into whatever drew next. The
codebase's own convention (ExplosionRenderer / ChaffRenderer / MineRenderer) uses
`DepthRead` and restores all three states; WarpRenderer documents "restore all three
states we touched".

Because ExplosionRenderer draws AFTER trails/sparks/debris in Game1's draw order, the
leaked depth writes from the earlier passes hard-clip the later, softer transparent
explosion rims (fragments behind a nearer particle's written depth fail the DepthRead
test and vanish).

### The B2 fix (copies ExplosionRenderer's exact pattern)

In all three renderers: `DepthStencilState.Default` → `DepthStencilState.DepthRead`,
and the post-draw restore becomes the full three-state restore:

```csharp
gd.BlendState = BlendState.Opaque;
gd.DepthStencilState = DepthStencilState.Default;
gd.RasterizerState = RasterizerState.CullCounterClockwise;
```

## Verification

### Build
Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors**.

### B1 live proof
Real MonoGame client probe (above) — pre-fix frame-start state was `blend=AlphaBlend
depth=None raster=CullCounterClockwise` on every frame; the B1 reset is what makes the
pass self-contained.

### B2 live proof — hard-edged clipping removed
A throwaway harness (`/tmp/p339verify`) draws the REAL production renderers in the
EXACT Game1 Flying 3D-pass order (B1 reset → missile trails → warp sparks → damage
debris → explosion) onto a Depth24 offscreen target. The scene stages a large
BigShip blast with trail/spark/debris billboards nearer to the camera overlapping the
blast silhouette. Run once against the fixed tree and once against the 3 renderers
temporarily reverted (`git stash push`), so the ONLY difference is the renderer state
handling.

- **BUGGY (pre-fix):** the explosion is chopped up — solid black rectangular blocks
  are carved out of the fireball exactly where the nearer particle billboards wrote
  depth (558 explosion pixels; vision: "solid black rectangular blocks … straight,
  hard edges"). Post-pass device state leaked `Default`+`CullNone`.
- **FIXED:** the explosion is a clean, round, smooth additive fireball with the
  particles glowing over it (1192 explosion pixels; vision: "no straight-edged
  rectangular cuts or notches"); every particle pass restores depth/rasterizer to
  Default/CCCW after drawing.

Evidence: `.verify_p339/composite_BUGGY.png` vs `.verify_p339/composite_FIXED.png`.

Post-fix the real MonoGame client was also flown again (`--capture chase`, real
server) — coherent scene, intact ship, correct warp additive blending, no
missing/black/transparent geometry (`.verify_p339/postfix_realclient_chase.png`).

Constraint honestly documented (same as P338): PCore001.igc has no
`MissileLauncher` part, so no real missile trail can be fired in the real-client
capture; the trail component of the B2 live proof is therefore exercised through the
real MissileTrailRenderer in the layered-transparency harness (same real code path),
not a live-fired missile.

## Test suite

Monogame.ClientCore.Tests / Allegiance.Core.Tests / Allegiance.ClientCore.Tests /
Allegiance.Lobby.Tests run 2x; only the documented timing/network flakes are expected
(LiveTurretGunner persistent machine flake, the intermittent live-net flakes) — no
regressions. Changes are MonoGame-client render-state only; no Core/Server logic
touched, so Core counts are unchanged.

## Scope boundaries honored

- No native source touched.
- No other review finding (textures, scale gaps, retune sweep, B3–B5/U1–U3) addressed.

## Deliverables

Committed on `master` (working tree clean of tracked changes) — not pushed; Domingo
pushes.
