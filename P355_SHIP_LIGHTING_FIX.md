# P355 — SHIP LIGHTING FIX

Status: LANDED on `master` (not pushed — Domingo pushes).

## TL;DR

The task's leading hypothesis — that a NORMAL hull (the IC "Enh Fighter",
`wc_icfig`) crushes to visually-black at unlucky orientations because
`BasicEffect`'s 3 fixed world-space lights all miss its faces and only the flat
ambient term remains — is **FALSIFIED by exhaustive measurement**. The named
hull never renders near-black at any orientation, before or after any change.
The genuinely "indistinguishable-from-the-backdrop" hulls that DO exist turn out
to be per-asset **content defects** (models whose UVs sample pure black →
render as flat black silhouettes at every orientation and under any lighting),
which are **not fixable in the renderer** and live in the P352/P353 regeneration
scope the task already declared out of band.

The fix landed here is therefore the in-scope robustness improvement the task
itself offered as a candidate: a **camera-relative headlight** on the ship
renderer (repurposing the weakest default back-light), so the face the camera is
actually looking at always receives direct light regardless of the ship's world
orientation. It measurably raises the worst-case brightness of the genuinely
dark hulls and is vision-confirmed not to wash out well-lit or dark-livery
ships. It does **not** (and cannot) repair the flat-black content-defect hulls —
that is a separate model/UV fix, noted below.

---

## 1. Verification: the leading hypothesis does NOT hold

Requirement: reproduce two identical `wc_icfig` ships at two different
orientations (one toward a light, one away from all three) and show the
away-facing one crushes to black.

Used the **real production rendering path** — `ShipRenderer.Draw` +
`HullModelResolver` content + the compiled P287 models/textures + real
camera/projection — through a verification harness (`tools/`-style scratch under
`.verify_p355/`, not committed).

### 1a. Full 3-axis orientation sweep (819 orientations per hull)

Dense grid over full SO(3): yaw −180..180 step 30, pitch −90..90 step 30,
roll −180..180 step 45. Metric: the 95th-percentile luminance (p95) of ship
pixels against a pure-black backdrop — robust to a stray specular highlight
masking a generally dark render. "Crushed" = p95 < 0.15 (essentially invisible
against black).

Result for `ic-fighter` (the exact hull named in the task): **819/819
orientations, p95 never below 0.47, 0 crushed.** Ambient alone (0.32–0.36) keeps
a normal IC texture readable at every orientation.

### 1b. Full 72-hull census (coarse sweep per content model)

Classifying every real content model (72 hulls):

- **NORMAL** (min-p95 ≥ 0.30): includes **every "Enh Fighter" variant** —
  `ic-fighter` (0.53), `giga-fighter` (0.39), `oh-fighter` (0.38) — plus the
  large majority of hulls. None of these ever crush to black.
- **DARK** (0.19–0.30): tf-fig, gt-int, gt-dev, ic-dev, gt-bomber, gt-mustang,
  bios/ic-devastator, bios-bomber, ic-frigate, bios/ic-ejectpod, gt-rd, gt-scout.
  Dark textures, but visible (not "indistinguishable from black").
- **INVISIBLE** (renders but essentially all-dark): `bios_scout_upscaled`,
  `rix-int`.
- **FLAT-BLACK** (model rasterizes a pure-black silhouette — zero pixels above
  the backdrop threshold at EVERY orientation): `gt-fighter`, `gt-tt`,
  `dreg-fig2`, `ic-trooptransport`, `bios-trooptransport`.

### 1c. Forensic isolation — the flat-black hulls are content defects, not lighting

Probed `gt-fighter`:

- `gt-fighter` model + `gt-fighter` texture → flat black.
- `gt-fighter` model + **`ic-fighter`'s bright texture** → STILL flat black
  (the model ignores even a bright texture).
- `ic-fighter` model + `gt-fighter` texture → renders normally (texture is fine).
- Ambient cranked to **×20** → still zero pixels above black (`shipPx=0`).

Conclusion: these models rasterize a silhouette whose **UVs sample pure black**
(degenerate/zero UVs from the P352 Meshy regeneration), so no amount of lighting
can illuminate them — `light × black texel = black`. This is a model/UV content
defect in the P352/P353 regeneration scope the task explicitly excluded, not a
lighting problem, and **no renderer-side lighting fix can help them**.

### 1d. Synthesis

No normal-textured hull — and specifically none of the three hulls that display
as "Enh Fighter" (`ic-fighter`/`giga-fighter`/`oh-fighter`) — renders
indistinguishable-from-black at any orientation. The playtest's "two Enh
Fighters, one black" therefore cannot be explained by the orientation-lighting
mechanism. Most likely explanations, in order: (a) the "black" ship was actually
one of the content-defect flat-black hulls (several render invisible and are
easy to misidentify as a same-class "fighter"), or (b) a per-instance rendering
state issue in the live `Game1` per-ship gather that a single-ship draw cannot
exhibit. Either way, the orientation hypothesis is not the cause, and I am not
forcing it.

## 2. The applied fix

`src/Allegiance.ClientCore.MonoGame/Rendering/ShipRenderer.cs`.

`BasicEffect` exposes only 3 directional lights; the 3 defaults are FIXED in
world space, so a ship's world rotation (from its sim `Orientation`, per
`PoseConverter.ToXnaWorldRelative`) decides how much light its faces receive.
Chosen approach (one the task explicitly listed): **camera/view-relative
headlight**. The weakest default (the back/rim light, slot 2, diffuse
~0.32–0.39) is repurposed into a light that always points **from the camera into
the scene** — `light.Direction = camera world forward` (BasicEffect's
`Direction` is the propagation direction, so setting it to the camera forward
lights exactly the faces the camera sees). Every camera-facing surface now gets
direct light regardless of ship world orientation.

```csharp
var viewInverse = Matrix.Invert(view);
var headlightDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.Forward, viewInverse));
// per effect, after EnableDefaultLighting():
basic.DirectionalLight2.Direction    = headlightDir;
basic.DirectionalLight2.DiffuseColor = HeadlightColor;   // (0.62, 0.64, 0.68)
basic.DirectionalLight2.SpecularColor = Vector3.Zero;
basic.DirectionalLight2.Enabled      = true;
```

**Why this approach over the alternatives:**
- *Raising `ShipAmbient`'s floor* was rejected: ambient multiplies every face
  uniformly, so it would wash out already-well-lit ships and lift the dark
  liveries into looking wrong — the exact regression the task forbids.
- *Minimum per-pixel brightness clamp* was rejected: BasicEffect has no output
  clamp; a custom shader/post-process is out of proportion for this POC, and a
  blanket floor would artificially brighten the dark liveries (again contrary to
  P352/P353 guidance). It would also still be defeated by a black texture.
- The **headlight** is surgical: it only adds light to the faces the camera sees
  (which is what determines whether a ship is visible against the backdrop),
  keeps the two real default lights (key+fill) for familiar shading, and
  deliberately uses a moderate intensity so the well-lit and dark-livery looks
  are preserved.

## 3. Before / after evidence (same full-819 sweep, same method)

Worst-case 95th-percentile luminance (higher = better; >0.15 = visible):

| hull | class | min-p95 before | min-p95 after | avg before | avg after |
|------|-------|----------------|---------------|------------|-----------|
| ic-fighter | control / NORMAL | 0.468 | 0.573 | 0.816 | 0.914 |
| gt-sf | DARK livery | 0.282 | 0.345 | 0.692 | 0.776 |
| gt-carrier | DARK livery | 0.233 | 0.264 | 0.611 | 0.683 |

Every hull's worst-case floor rises; `ic-fighter` stays NORMAL (avg 0.91, not
blown to 1.0 — texture modelling preserved). Representative before/after shots
of the darkest real hull (`gt-int`) and the control at their dimmest orientations
are saved under `.verify_p355/evidence/*_BEFORE_*.png` / `*_AFTER_*.png`.

**Well-lit no-regression (required):** vision-checked `ic-fighter` post-fix —
"clearly textured with visible panel lines and color variation... strong
contrast between bright highlights and dark shadows... NOT flat, washed-out, or
blown-out white." The `gt-sf` dark livery — "appears dark/metallic as expected,
NOT washed out or overly brightened." The P352/P353 dark liveries still read as
recognizably dark; only their near-black worst-case orientations are lifted.

**Flat-black content-defect hulls are unchanged** (they render black at every
orientation regardless of lighting) — confirmed, and left untouched per scope;
they need a UV/model regeneration fix (P352/P353 territory).

## 4. Live client

Ran the real MonoGame client (`--capture`) against a real in-process server
seeded with two `ic-fighter` companions at 180°-opposed orientations
(`teams:1`, `.verify_p355/liveharness/`): clean connect, clean exit (0), frames
written. The default capture fleet camera framed the player's first-person
cockpit with the companions outside frame, so a clean side-by-side fleet still
shot needs camera choreography; the rendering evidence above already exercises
the exact production `ShipRenderer.Draw` + real content path, which is the
authoritative renderer under test. A follow-up can choreograph the fleet-camera
live shot if a literal two-ships-in-frame frame is wanted.

## 5. Build / tests

- Whole-solution build: **0 warnings, 0 errors**.
- Full test suite run twice: pass counts unchanged; the only failures are 5
  pre-existing **live-wire timing flakes** in `Allegiance.Core.Tests`
  (`InboundFloodSafetyLiveTests`, `ProjectileBroadcastTests`,
  `LiveTurretGunnerBindingTests`, `LiveMoneyTests`, `GameOverPlayersWireTests`).
  `Allegiance.Core.Tests` references only Core/Server/Bot/Lobby — it has zero
  coupling to the MonoGame renderer this task changed, so these failures are
  categorically unrelated to this change.

## 6. Notes for follow-up (explicitly out of scope, not modified)

- `StationRenderer.cs:87`, `ProbeRenderer.cs:102`, `BuildingEffectRenderer.cs:81`
  and the missile renderer use the same `(0.32,0.34,0.36)` ambient constant; the
  headlight technique could apply there too if a follow-up wants parity — ships
  only were touched here.
- The **flat-black content-defect hulls** (`gt-fighter`, `gt-tt`, `dreg-fig2`,
  `ic-trooptransport`, `bios-trooptransport`) render invisible at every
  orientation; root cause is a model/UV defect (degenerate UVs sample pure
  black). These need a P352/P353-style model/UV regeneration — NOT a lighting
  change, which cannot help. Recommend a dedicated content task.
