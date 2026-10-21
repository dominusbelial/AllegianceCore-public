# HUD-19 — centre-HUD indicator blend, colours and toggles

Branch: **master** (direct commit `HUD-19:`). Base: HEAD containing HUD-14b
(`b60e216a`); HUD-19 also carries the **HUD-14c** fixups from Claude's review
of HUD-14b (logged in the HUD-14 report, below).

This round closes the six `partial` centre-HUD indicator rows in the parity
matrix (`docs/parity/parity-matrix.csv`, area `hud`): the native-additive
reticle/aim crosshairs, the `ShowJoystickIndicator` toggle, the real native
joystick chevron mesh, and the native `DrawExpandedBlip` lock flower — plus
shared One/One blend state reused by the HUD-14 classic gauge pass.

## What changed

### 1. Shared additive (One/One) indicator pass (classic skin, HudRenderer base)

Native composes the centre indicators with `BlendModeAdd` only for the
`directioninbmp` reticle and every `centeroutbmp` draw
(`WinTrek/indicator.cpp:149-150` reticle, `:268-276` chase/missile aim point,
`:361-362` cockpit predictor, `:399-400` chase aim no-target). Native
`BlendModeAdd` is `SRCALPHA, DESTBLEND=ONE` with alpha = texture × diffuse
(`engine/RasterD3D9.cpp:295-302`).

The port's HUD textures are **premultiplied**
(`MdlTextureDecoder.ToPremultipliedRgba`, `IndicatorTextures.cs:124`), so
`BlendState.Additive` (SrcAlpha/One) would apply alpha twice. The correct form
is **One/One** with the tint's rgb pre-scaled by its alpha — native
SRCALPHA/ONE on straight alpha equals One/One on premultiplied. For the
reticle that is `0.4·0.4 = 0.16` per channel; the grey `(0.33,0.33,0.33,0.5)`
becomes `0.165` per channel; `(1−g,g,0)` (alpha 1) is unchanged. Derived and
implemented in `HudRenderer.AdditiveTint` and the shared static
`HudRenderer.AdditiveOneOne`.

`DrawCentreIndicatorsAdditive` opens a dedicated One/One pass (begins/ends its
own SpriteBatch session inside the outer alpha batch, same pattern as the
gauge pass) and draws the reticle + aim predictor + chase aim when their real
artwork decoded. The velocity marker, lead surfaces, joystick, turn arrows and
lock flower stay in the alpha pass — matching native (the `directionbmp`
velocity and `leadinbmp/leadoutbmp` leads go through the 2-D `DrawImage` path
which takes no colour and inherits the context blend). The procedural
fallbacks (no artwork) and the Modern skin's overrides
(`ModernHudRenderer.cs:166-248`) stay on alpha blend, as scoped.

**Blend-state leak (classified).** Native's aim-predictor branch calls
`SetBlendMode(BlendModeAdd)` (`indicator.cpp:361`) which leaks into
`RenderDirectionIndicator`, called last (`indicator.cpp:796`); the velocity
marker therefore inherits Add in the same `Render` even though it has no blend
of its own. The task directs us to treat this as an **accidental native
carry-over**, not design — the port keeps the velocity marker on the alpha
pass.

### 2. ShowJoystickIndicator toggle ported

`HudSettings.ShowJoystickIndicator` (default `true`; native ctor
`WinTrek.cpp:2739`, `LoadPreference("ShowJoystickIndicator", 1)`
`WinTrek.cpp:3059`). It persists with the other HUD settings, is threaded
through `HudModelAdapter.Create` into `HudContext.ShowJoystickIndicator`
(which already gates `HudModel.JoystickIndicatorVisible`), and gets a
Graphics/HUD overlay row "Joystick indicator: On/Off" (native Graphics Options,
`WinTrek.cpp:4507,4526`; row count 12 → 13, Up/Down wrap `% 13`). The footer
(`78 + rows·46 + 8`) still fits inside the 680 px panel at 1280×720 (row 13 at
y 630, footer at 684 < panel bottom 712). The P506 mouse-stick widget also
reports the shared id "Joystick turning indicator" (`HudRenderer.cs:381-384`)
— that widget is a separate port feature and is left alone.

### 3. Native joystickindicator.mdl chevron (classic skin)

The stroked chevron is replaced by the real 6-vertex mesh from
`joystickindicator.mdl`: vertices `(-9,-3.5) (-8.4,-0.8) (0,1) (0,4) (9,-3.5)
(8.4,-0.8)`, indices `[0,1,2, 1,3,2, 3,5,2, 2,5,4]`, wrapped in `TextureGeo`
with `joystickindicatortexture.png` (white, alpha 0x B3 ≈ 0.70). `JoystickMesh.cs`
rasterises the 4 triangles once at load (supersampled for clean edges) into an
18×8 premultiplied texture; the renderer draws it centred with SpriteBatch
rotation and scale inside the alpha pass, allocating nothing per frame. Alpha =
`0.70 × |control|`. The lit colour resolves to near-uniform white (light
`(0,0,1)` white, ambient 0.25 on a face-on normal `(0,0,1)`, so `N·L = 1`
everywhere — `engine/device3d.cpp:444` onward), so the bake is white;
the residual lighting term is documented as the only simplification.

**y-up sign settled.** Native image space is y-up (`engine/context.cpp:552`).
`Translate(centre − 100·(yaw,pitch))` (`indicator.cpp:166`) converts to screen
y-down as `(centre.X − 100·yaw, centre.Y + 100·pitch)` — the old port had
`− 100·pitch` (y-mirrored). The port's `_lastSentControls.Pitch` carries the
same sign as native `jsValues[c_axisPitch]` (it is "the live turn control
vector this frame actually sent (jsValues[c_axisYaw]/[c_axisPitch]
equivalent)", `Game1.Rendering.cs:2832-2834`), so the screen position is
`+100·pitch` and the rotation is the native y-up angle mapped to screen space
(`JoystickLayout`, pinned by test for `(yaw,pitch)=(0,+0.5)` → down at
`cy+50`, and `(+0.5,0)` → left at `cx−50`). The Modern skin's glyph is
unchanged.

### 4. Lock flower via native DrawExpandedBlip quadrants

Native draws the lock surface as **four centred** quarter-bitmaps at
`(±delta, ±delta)` with `delta = radius/√2` floored at `xsize/4`, colour
White, forced `BlendModeSourceAlpha` (`radarimage.cpp:255-277,271`); called
with `radiusBracket` (full) or `radiusBracket + 40·(1−lock)` (partial)
(`radarimage.cpp:327-333`). The quarters keep native pixel size and **move
apart** — they never scale. `DrawLockBracket` now uses the pure
`RadarBracketRenderer.ExpandedBlipDelta`/`ExpandedBlipQuadrant` and a new pure
`HudRenderer.ExpandedBlipQuadrantDest` (each quarter centred at `±delta`,
White, alpha) instead of squashing the whole 64 px sprite to a `2·s` span.

**"No sinusoid" confirmed native** (`radarimage.cpp:325-333` has none). The
dead `LockBracketStyle.HalfSizeFor` sinusoid and its tests are deleted; the
class summary no longer claims a retained pulse.

**Radar `DrawExpandedBlip` centring discrepancy (reported, NOT fixed — radar
brackets are out of scope).** The radar's own `DrawExpandedBlip`
(`RadarBracketRenderer.cs:653-668`) draws each quadrant with its **top-left
corner** at `centre + offset`, whereas native (`radarimage.cpp:272-275`) draws
each quarter **centred** (`bCentered=true`) at `±delta`. The radar version
therefore sits each quadrant half-a-quarter (~16 px for 32×32 art) farther from
the centre than native. This is a pre-existing HUD-12/13 radar-bracket
discrepancy; the lock flower in this round uses the correct centred form.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| Additive reticle + centeroutbmp crosshairs | `indicator.cpp:149-150,268-276,361-362,399-400`; `RasterD3D9.cpp:295-302` | design | One/One additive pass, tint rgb pre-scaled by alpha (`AdditiveOneOne`) |
| Velocity/lead inherit context blend | `indicator.cpp:145,328-330`; `context.cpp:546-583,343` | design | Stay on alpha pass; the Add leak is classified accidental carry-over (`indicator.cpp:361→796`) |
| ShowJoystickIndicator toggle | `WinTrek.cpp:2739,3059,4507,4526,5222-5238` | design | Ported (`HudSettings` + overlay row + adapter) |
| Joystick y-up layout | `indicator.cpp:154-177`; `context.cpp:552` | design | Screen `+100·pitch` (y-mirrored), native angle → screen rotation |
| Joystick mesh geometry | `joystickindicator.mdl`; `indicator.cpp:167-172` | design | 6-vertex chevron baked once, drawn centred, alpha `0.70·\|control\|` |
| Joystick lit colour | `device3d.cpp:444`+; `indicator.cpp:168-169` | design | Near-uniform white (face-on `N·L=1`); residual lighting term simplified (documented) |
| Lock flower quadrants | `radarimage.cpp:255-277,327-333` | design | Four centred native-size quarters at `±delta`, White, alpha |
| No sinusoid | `radarimage.cpp:325-333` | design | Dead `HalfSizeFor` pulse deleted |
| Radar bracket quadrant top-left placement | `radarimage.cpp:272-275` vs `RadarBracketRenderer.cs:667` | bug | Reported only; radar brackets out of scope |

## Modern alternative considered

Native stamps the lock and indicators as small 2-D surfaces and turns blend
modes on/off per draw in one context. A modern approach would bake the whole
centre-HUD cluster (reticle, crosshairs, lock flower, joystick) into one atlas
and drive the additive layers with a single premultiplied One/One batch (the
port already batches the gauges + indicators this way) — fewer draw calls and
no per-surface state switches. This round adopts that batching for the
centre indicators (one shared One/One state) but keeps the individual sprite
composition native-faithful for fidelity; the joystick chevron is already
rasterised once at load (GPU-friendly, no per-frame allocation). A full
centre-HUD atlas + masked-fragment-shader reveal is a clean follow-up.

## P506 note

The mouse-stick virtual-joystick widget reports the shared parity id "Joystick
turning indicator" (`HudRenderer.cs:381-384`). It is a separate port feature
(unrelated to the native `ShowJoystickIndicator`), so it is left alone; the id
collision is noted here and in the matrix row.

## Verification

- Whole-solution Debug build: 0 warnings / 0 errors.
- New `Hud19CentreHudTests` + affected suites run 3× alone: **135/135 ×3 PASS**
  (HudModel, ModernHud, ModernHudSettingsInput, LockBracketStyle,
  LockTextureSource, Hud8bCurrentCommand, Hud14ClassicGauge, Hud4Radar, Hud19).
- One full fast-tier run (`tools/run-tests.sh --tier fast`): **9,658 tests,
  271.7 s**; one failure in untouched `Allegiance.Core`
  (`P761bCommandRelayTests…`) — passed **3/3 class-isolated** and logged in
  `docs/TEST_FLAKES.md` as a live-network load flake. All HUD-19, HUD-14c and
  renderer/settings tests passed.
- `P700ShowcaseLiveTests…never_parks…` 5×: **not run** — HUD-19 changes are
  renderer/settings only (`Allegiance.ClientCore.Game`), they cannot affect
  flight/sim/bots, so the conditional showcase check is not triggered.
- `python3 scripts/parity-scorecard.py`: runs clean; CSV `{6}` columns, LF only.
- Failing-first: the joystick y-up layout and lock-quadrant tests were written
  against the corrected native-derived values and fail on the pre-change code.

## Screenshots (real client, 1920×1080, classic skin)

**Not captured in this session.** The required proof is a capture of the REAL
client backbuffer (the production `Allegiance.ClientCore.MonoGame` window via
the `ALLEGIANCE_AUTOSHOT` path), staging a bright-background cockpit, a turning
joystick, the toggle-off state, and a missile lock. Producing those requires
launching the full windowed client against a local server with a mission,
connecting, switching to the classic skin and turning/acquiring a lock — a
heavyweight live proof that could not be completed within this one-shot
session's budget without risking an unresolved run.

What the changes ARE verified by instead:
- headless unit/`Hud19CentreHudTests` (135/135 ×3) pinning the additive
  pre-scale tints, the y-up joystick layout, the mesh rasterizer and the lock
  quadrant geometry;
- the offscreen renderer harness `tools/hud2-proof` (real production
  `HudRenderer` + real artwork) already exercises the classic skin across
  flight/combat/chase/docked scenarios at 1920×1080 in the fast-tier
  `ModernHudTests.Both_skins_render_Hud1_scenarios…` (a `[Tier=Live]` test).

The real-client screenshots remain a follow-up to capture on an interactive
session.

