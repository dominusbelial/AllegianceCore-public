# P241 — Rebuild the cockpit HUD using real original-game gauge art

## Summary

P211 shipped a deliberately *modern* HUD (its own doc comment said "not a clone
of the original game's 'Casio digital watch' look"). Domingo asked for the
**authentic** original look: "old style game gauges with bar levels, reminds me
of a Casio watch." This stage rebuilds the six core flight gauges from **real
original-game art** — the root-level `*Gaugebmp.mdl` segmented-arc gauges — and
repositions them into native's symmetric training-overlay layout. Purely client
(`src/Allegiance.ClientCore.MonoGame/`); zero `Allegiance.Core`/wire change.

The gauges still read the exact same real telemetry they always did
(`HudFrame`, lifted from the local ship's `ServerShipUpdate` each frame — hull /
shield / energy / fuel / ammo / throttle). The job was to **present that same
data** through real textures, not to source new data.

## Asset selection (looked, not guessed)

Per the brief, the candidates in `~/Development/Artwork/CortUI/` and the
root-level `*Gaugebmp.mdl` files were **actually inspected** (pixel analysis +
vision on decoded images; see `.verify_p241/candidates.png`):

- **`CortUI/lesshud/`** — the brief's "reasonable first guess". Inspection
  showed these are sparse **black masks**: mostly-empty canvases with a thin
  greyscale arc (e.g. the hull gauge is 15 603 / 16 384 pixels near-black with
  only ~800 lit pixels). They read as thin outlines, **not** the retro segmented
  fill gauge Domingo described.
- **`CortUI/jelloshot/`** — has a proper two-layer `_gauge` + `_gauge_back`
  scheme, but the aesthetic is glossy "jelly", not the segmented retro look.
- **Root-level `*Gaugebmp.mdl`** — **chosen**. Decoding them (RGB565) reveals
  exactly the retro segmented-arc meters Domingo remembered: discrete bar
  segments following a curve, lit-silver against a dark surround — a Casio-watch
  segmented display. This is the authentic original-game gauge art.

So the root `*Gaugebmp.mdl` files were selected: `HullGaugebmp`,
`ShieldGaugebmp`, `EnergyGaugebmp`, `FuelGaugebmp`, `AmmoGaugebmp`,
`ThrustGaugebmp` (each 128×128).

## `.mdl` TEXTURE decoder (new)

The port already had the MESH half of `.mdl` (`Allegiance.Core/Data/MdlFile.cs`
handles `MeshGeo`/`FrameData`); the TEXTURE half (`BinarySurfaceInfo` +
RGB565 pixel rows) was **not** ported. `Rendering/MdlTextureDecoder.cs` is a
faithful C# port of the proven `decode_texture.py` recipe
(`~/Development/allegiance-tools/reference/`, read-only reference; and the
`allegiance-mdl-model-format` wiki page):

- Walks the header (namespaces/imports/exports), skips the four-word
  `definition-index / ObjectImport / import-index / ObjectBinary` prologue,
  reads the `BinarySurfaceInfo` (size/pitch/bitcount + RGBA masks), then the
  RGB565 pixel rows — **flipped vertically** to match the reference ("row 0 =
  top").
- Pure and MonoGame-free (returns a plain `MdlTexture(Width, Height, Rgba)`
  record), so it is unit-testable headless. Verified against a hand-built
  synthetic fixture *and* against the real `HullGaugebmp.mdl` (128×128, has lit
  content).

The gauges are loaded at runtime straight from the artwork tree
(`GaugeTextureSource.ResolveArtworkDir()`, the exact `ALLEGIANCE_ARTWORK_PATH`
convention `MissileModelSource` already uses for real `.mdl` assets) — no files
copied into Content, no content-pipeline work. The near-black surround (RGB565
has no alpha) is alpha-keyed to transparent (`ToPremultipliedRgba`, Rec.601
luminance ≤ 40) so the gauges render over the colourful 3-D scene without a
black box; survivors are premultiplied for `BlendState.AlphaBlend`.

## Real gauge rendering — the native clipped reveal

Native renders each gauge as a **single pre-rendered bitmap revealed by a
sub-rect** proportional to the value (`GaugeImage::Render`,
`engine/image.cpp:489-540`): for `JustifyBottom` it draws only the bottom
`value · size` of the bitmap. The segmented arc therefore **lights up
progressively** as the quantity rises.

`HudRenderer` reproduces exactly that: it draws the real texture with a source
rectangle of only the bottom `fraction` of the texture, aligned to the full
gauge's bottom edge. Before committing to this, the reveal was simulated on all
six decoded gauges to confirm each lights monotonically
(bottom-up bright-pixel counts grow strictly with value:
hull `44→350→658→721`, shield identical, energy `1→10→16→31`, fuel `1→8→19→26`,
ammo `1→10→16→31`, thrust `1→8→19→26`) — so the fill genuinely tracks
telemetry, not a static image.

Throttle is mapped `-1..+1 → 0..1` via `(throttle+1)/2` (idle/empty →
full-forward/full), consistent with the existing `HudFormat.ThrottlePercent`.
Each gauge gets a short label + a live percentage beneath (HULL/SHLD/ENGY/FUEL/
AMMO/THR — all pure ASCII, respecting the P214 spritefont char-range pitfall).

## Layout — native symmetric arrangement

Repositioned from the P211 single-column left-side stack to native's own
training-tutorial overlay coordinates (`WinTrek/indicator.cpp:514-566`, the
screen-space offsets from centre used to annotate each gauge):

| Gauge  | Native offset (px from centre) | Placement |
|--------|--------------------------------|-----------|
| Throttle | (−126.5, +93.5)  | upper-left  |
| Fuel     | (+126.5, +93.5)  | upper-right |
| Energy   | (+126.5, −112.5) | lower-right |
| Ammo     | (−126.5, −112.5) | lower-left  |
| Hull     | (−65.5,  +35.5)  | centre-left  |
| Shield   | (+65.5,  +35.5)  | centre-right |

The offsets are scaled by a viewport-relative spread (`h/680`, clamped) so the
six gauges clear each other at any window size; the **arrangement** is native,
the spacing is this port's. Everything already on the HUD today stays in place
and is re-fitted around the gauges: the centre reticle + FIRING tag (P221), the
missile-lock target box (P230), bottom-centre speed, top-centre afterburner/
PWR and weapons-mode readouts (P225), and the camera-mode tag. A "power" readout
is retained as text (reactor PWR is not one of the six gauges).

## Scope boundary

- **In scope:** the six core flight gauges the client already showed (hull,
  shield, energy, fuel, ammo, throttle→thrust) using real original-game art.
- **Out of scope (unchanged):** target-ship gauges, turret HUD, kill feed,
  radar/map (the `M` map view is untouched), and every other `CortUI`/`*bmp.mdl`
  asset this client does not show today. No `.mdl` UI-panel-script
  interpretation (native's compiled-layout engine is not ported — the real
  gauge textures are reused directly with this port's own layout code).
- `~/Development/Allegiance` and `~/Development/allegiance-tools/reference/`
  are untouched (read-only references).
- **Graceful degradation:** if artwork is unavailable or a gauge fails to
  decode, `GaugeTextures.Load` skips it with a logged warning and `HudRenderer`
  falls back to the P211 procedural panel/bars — the client still starts and
  flies.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings /
  0 errors**.
- Tests (baselines: MonoGame 236 at P240, Core 3065, ClientCore 83):
  - `Allegiance.ClientCore.MonoGame.Tests`: **241 passing** (+5 new
    `MdlTextureDecoderTests`).
  - `Allegiance.ClientCore.Tests`: **83 passing** (unchanged).
  - `Allegiance.Core.Tests`: see run log in `.verify_p241/` (only the
    pre-existing `LiveTurretGunnerBindingTests` timing flake is expected).
- **Live screenshot proof, offscreen `RenderTarget2D`** (the established
  P207/P234/P235 technique). Two layers of proof in `.verify_p241/`:
  1. **Deterministic multi-level sweep** (a standalone MiniGame harness that
     builds the REAL `HudRenderer` + REAL `GaugeTextures` and renders the real
     production `Draw` path): `01_full.png` (all 100% — every gauge fully lit),
     `02_moderate.png`, `03_damaged.png` (hull 28%, shield 0% — arcs shorten),
     `04_critical.png` (hull 12%, nearly empty), and a controlled **hull sweep**
     `05/06/07_hull_sweep_15/50/85.png` where only the HULL gauge changes —
     its segments light monotonically 15→50→85 while every other gauge is held
     constant. This proves the textured gauges **reflect telemetry**, not a
     static image.
  2. **Genuine live-stack capture** (`live_capture.png`): booted a real
     `Allegiance.Server` (port 6903, temp `teams:1` config) and connected the
     real MonoGame client; the capture taken in real flight shows the real Bios
     Scout's genuine state — HULL/FUEL/AMMO/ENGY 100% (full arcs), **SHLD 0%
     empty** (the spawned scout carries no shield — real data), speed 0 U/S
     idle, THR idle. End-to-end: real server → real `ServerShipUpdate` → real
     `HudFrame` → real `HudRenderer` → real textures.
- All temporary scaffolding (the live-capture diagnostic in `Game1.cs`, the
  `server.json` temp config, the `/tmp` harness) was removed before commit;
  `git diff` on `Game1.cs` shows only the intended P241 wiring.

## Files changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/MdlTextureDecoder.cs` (new) —
  the `.mdl` TEXTURE decoder (`BinarySurfaceInfo`/RGB565) + premultiplied
  transparent-surround conversion.
- `src/Allegiance.ClientCore.MonoGame/Rendering/GaugeTextures.cs` (new) —
  `GaugeKind`, artwork-path resolution + `.mdl`-stem mapping, and the six
  `Texture2D` loader.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` — textured
  gauges in the native symmetric layout with the bottom-up clipped reveal;
  procedural panel retained as the no-artwork fallback.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — decode + load the gauges in
  `LoadContent`, pass to `HudRenderer`.
- `tests/Allegiance.ClientCore.MonoGame.Tests/MdlTextureDecoderTests.cs` (new)
  — 5 tests (RGB565+flip, bad-magic, alpha-key, kind mapping, real-artifact).

Evidence (`candidates.png`, the seven sweep frames, `live_capture.png`) is left
in the untracked `.verify_p241/` directory for Domingo to eyeball; not
committed. The `/tmp/p241hud` harness is disposable.
