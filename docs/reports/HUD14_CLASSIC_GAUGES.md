# HUD-14 / HUD-14b — classic centre-console gauges on the native dialog.mdl layer model

Base: master at dispatch (HUD-12 `193e8ea3`). Branch: **master**, committed directly (HUD-14 `4ace7b3b`, then HUD-14b `b60e216a`, amended with the suite + docs). HUD-14 built the six own-ship centre gauges as a data-table native layer model; **HUD-14b** (this round) corrects Claude's review findings: the console was upside down, the arc backs were offset, the lifepod OXY label was lost, the matrix over-claimed, `AllLoaded` allocated per frame, the console offsets and layer geometry scaled differently, and the blend/tints were non-native. **HUD-14c** (with HUD-19, below) fixes the remaining review findings: the additive blend state, the `hudColorshadows` misread, the label y-up anchor, and the weak tests/report claims.

The six own-ship gauge rows in `docs/parity/parity-matrix.csv` (Hull integrity, Shield charge, Energy reserve, Fuel reserve, Ammunition reserve, Thrust setting) are `partial` with the note **"native layer model; smVerdana substituted"** plus the item-7 tint gaps (the label tint uses the port's accent rather than the live side colour). The `%` numeral is a HUD option behind `HudSettings.GaugePercentage` (default **off** = native).

## What HUD-14 did (unchanged foundation)

Native builds each classic centre gauge as a `GroupImage` of layered images (`~/Development/Artwork/dialog.mdl:2173-2516`) assembled into the `DefaultCenterHudOn` console group (`dialog.mdl:2604-2623`): a frame (hull/shield), a faint back, a fill revealed through a `GaugeImageRect(justify, ImportImage(...), Rect(...), true, value)` that draws only that **Rect** of the bitmap in proportion to the value, a red variant switched by `Flash`, a spine (thrust/fuel/ammo/energy), and a `StringImage` label, each placed by `TranslateImage` and finally by the console `TranslateImage`. HUD-14 transcribed this into `ClassicGaugeSpec`/`ClassicGaugeTable` — one record per gauge (not hand-tuned constants), citing its native dialog.mdl line per gauge:

| Gauge | back | fill | red | spine | frame | source rect | reveal | console pt | label | value source | mdl |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Thrust** | thrustgaugebmp | thrustgauge2bmp | – | thrustspinebmp | – | (15,10,95,108) | Top | (161,246) | THRUST | playerThrottle | 2173 |
| **Fuel** | fuelgaugebmp | fuelgauge2bmp | – | fuelspinebmp | – | (17,10,96,108) | Top | (361,246) | FUEL | GetFuel(Me) | 2226 |
| **Ammo** | ammogaugebmp | ammogauge2bmp | – | ammospinebmp | – | (29,18,69,92) | Bottom | (147,105) | AMMO | GetAmmo(Me) | 2278 |
| **Energy** | energygaugebmp | energygauge2bmp | – | energyspinebmp | – | (30,18,69,92) | Bottom | (372,105) | ENERGY | GetPercentEnergy(Me) | 2331 |
| **Hull** | hullgauge2bmp | hullgaugebmp | hullgaugeredbmp | – | centerhudleftbmp | (40,23,48,82) | Bottom | (215,180) | ARMOR | GetPercentHitPoints(Me) | 2404 |
| **Shield** | shieldgauge2bmp | shieldgaugebmp | shieldgaugeredbmp | – | centerhudrightbmp | (40,23,48,82) | Bottom | (300,180) | SHIELD | GetPercentShields(Me) | 2474 |

- **Source rects** are the native `GaugeImageRect` rects (`dialog.mdl:2179,2233,2286,2339,2407,2481`); e.g. hull `Rect(40,23,88,105)` becomes the 48×82 band. Whole-bitmap clipping is gone.
- **Reveal** mirrors native `GaugeImageRect` (b3D, `engine/image.cpp:468-531`): only `yoffset = (int)(rect.Height · value)` rows of the band, from the justified edge — **Top** grows down (THRUST/FUEL), **Bottom** grows up (AMMO/ENERGY/HULL/SHIELD). Same direction HUD-6 validated.
- **Texture loading** (`GaugeTextures`) loads every layer per the spec (back, bright `*2` fill, red variant, spine, centerhud frame). The arc gauges reveal the bright `*gauge2bmp` fill, not the faint back. Missing layers degrade gracefully.
- **Value sources** map to `HudFrame.HullFraction/ShieldFraction/EnergyFraction/FuelFraction/AmmoFraction` and `(Throttle+1)/2`; the `Flash` binding maps to `HudFrame.Flashing` and the renderer selects the red variant when `ClassicGaugeTable.UseRedFill(flash, spec)`.

## HUD-14b fix round

Claude's review of HUD-14 found seven issues. Each is fixed below; the native source is cited for every ported rule.

### 1. Console was upside down (y-up vs y-down) — `ClassicGaugeLayout`
Native image/pane coordinates have y pointing **UP** from the bottom (`engine/context.cpp:552`, "DrawImage3D() uses lower left as (0, 0)"; `dialog.mdl:225/231/311/317`, "Photoshop measures Y from the top, Alleg from the bottom"). HUD-14 treated every `DefaultCenterHudOn` point and child point as y-**down**, so THRUST/FUEL (y-up 246 > 240) landed **below** centre, AMMO/ENERGY above, and the arcs splayed into an X instead of ringing the reticle.

The pane's top-left is at centre−(320,240) (`dialog.mdl:5397`). Converting each layer from native y-up console space to screen y-down, with **one** pane scale `s` applied to both the console offset and the layer geometry, gives (derived and commented in `ClassicGaugeLayout`):

```
screenY = centre.Y − ((consoleY − 240)·s + (childY + layerH)·s)
```

`classicGaugeLayout.Anchor` = `centre + (consoleX−320, 240−consoleY)·s`; a layer whose native y-up child point is `cy` with height `layerH` gets its **top** at `anchor.Y − (cy+layerH)·s` and left at `anchor.X + cx·s`. Headless tests pin THRUST/FUEL **above** centre and AMMO/ENERGY **below**, and each arc band in the quadrant its art faces.

### 2. Arc back layer offset — `DrawBack`
Native draws every arc back as a `GaugeImageRect(..., true, 1)` band sharing the fill's rect and TranslateImage point (`thrustGaugeBack` `dialog.mdl:2175-2185`, fuel `:2228`, ammo `:2280`, energy `:2333`). HUD-14's `DrawPlain` stamped the **whole 128px texture** at BackPoint, so the back sat `(15,10)·scale` away from the fill. Now `DrawBack` reveals the arc back through the same band path as the fill at fraction 1, so **back band origin == fill band origin** (pinned by test). Hull/shield backs are full `ImportImage3D` textures (`dialog.mdl:2432 hullGaugeBack`, `:2515 shieldGaugeBack`) and stay full-bleed, now tinted `hudColorshadows`.

### 3. Lifepod OXY label lost — `DrawGaugeLabel`
HUD-14's `DrawGaugeLabel` drew `spec.Label` (always "FUEL") and dropped the per-gauge override, so the pod's OXY no longer showed while the matrix and report claimed it did. `DrawGaugeLabel` now draws `gauge.Label` via `HudRenderer.ResolveGaugeLabel`, so the model's OXY override (`HudModel.cs:279`) is what shows. Verified with a **renderer-level** test (through `ResolveGaugeLabel`, not a source grep) and a real-client lifepod screenshot.

### 4. Matrix over-claim — `parity-matrix.csv`
HUD-14 flipped all six gauge rows to `done`, but they use a substitute font for native `smVerdana` (fonts out of scope), so `done` over-claimed. The six rows are now `partial` with the note **"native layer model; smVerdana substituted"** plus the item-7 gap (label tint uses the port accent, not the live side colour). Edited only those six lines as text; LF endings and exactly 6 columns preserved.

### 5. Per-frame allocation — `GaugeTextures.AllLoaded`
`AllLoaded` called `Enum.GetValues<GaugeKind>()` every frame (`GaugeTextures.cs`, called from `HudRenderer.cs:~315`). The kind array is now cached in a static field. The allocation test now covers `AllLoaded` and all **six** kinds (HUD-14's `(GaugeKind)(i & 5)` hit only four) plus the `DrawGauges` path pieces (`Spec`/`RevealSource`/`UseRedFill`/`Anchor`/`RevealDest`).

### 6. Scale drift — one pane scale
HUD-14 scaled console offsets by `OwnStatusLayout.Spread` (1.15–2.1) but layer geometry by 0.72, so the layers drifted apart. `ClassicGaugeLayout.PaneScale = 0.72` is now applied to **both** the console offsets and every layer's geometry. (HUD-14c correction: the `.5` at `dialog.mdl:5397` is the overlay slide-animation time, not a zoom, so 0.72 is a **port deviation**, not a native scale — see the HUD-14c section, item 3.)

### 7. Blend / colour gaps — additive pass + tints
Native composes the whole console additively (`hudBlendMode = BlendModeAdd`, `dialog.mdl:34`); HUD-14 used AlphaBlend, White and TextDim. The gauge layers now draw in a **separate additive SpriteBatch pass** (`BlendState.Additive`). Hull/shield backs are tinted `hudColorshadows` = `Color(45,36,59)·0.65` (`WinTrek/WinTrek.cpp:8688`), and the labels are tinted `hudColor` — native's `pside->GetColor()·g_hudBright` (`WinTrek/WinTrek.cpp:8681`). **Recorded gap:** the port substitutes its HUD accent for the live side colour (the own side colour is not threaded into the label pass), noted in the matrix. (HUD-14c correction: the labels in fact render in the port's **teal accent** (`HudLabelTint`), not the live side `hudColor` — see the HUD-14c section.)

## HUD-14c fix round (with HUD-19)

Claude's review of HUD-14b found five remaining code/report issues. Fixed here.

1. **Wrong additive BlendState for the gauges.** `HudRenderer.cs:~335` used
   `BlendState.Additive` (SrcAlpha/One) on the **premultiplied** gauge textures
   and SpriteFont glyphs, applying alpha a second time. Native `BlendModeAdd`
   is SRCALPHA/ONE on **straight-alpha** textures (`engine/RasterD3D9.cpp:295-302`),
   which equals **One/One on premultiplied**. A shared static
   `HudRenderer.AdditiveOneOne` (`Color/Alpha SourceBlend = One`,
   `Color/Alpha DestinationBlend = One`) is now used by the gauge pass and by
   HUD-19's reticle/crosshair pass. Test pins both passes reference that state.
2. **`hudColorshadows` misread.** Native `Color(45,36,59)` passes 0..1 FLOATS
   (`zlib/color.h:33`), so 45/36/59 are out of range, and `MakeD3DCOLOR` packs
   `MakeInt(255·c)` without clamping (`engine/context.h:327`). Simulating the
   truncation/bit-spill yields ARGB(157, 111, 55, 51) — a dim muddy
   (0.435,0.216,0.200) at alpha ~0.62. That is a **BUG**, so per the standing
   rule the port uses the **evident intent** — a dim shadow tint at the
   designer's **alpha 0.55** (the alpha HUD-14b dropped) — keeping the
   native-actual hue so the hull/shield backs stay visible; the near-black
   `/255` reading of 45,36,59 is never used. Divergence recorded in code + report.
3. **Scale deviation stated honestly.** The `.5` at `dialog.mdl:5397` is the
   overlay slide-animation time (every row has it with hide point `(−2000,…)`),
   not a zoom. `PaneScale = 0.72` (`ClassicGaugeSpec.cs:~191`) is therefore a
   **port deviation** (stated in the code comment): native has no per-pane
   scale factor; the fix is that ONE scale drives both offsets and geometry so
   the layers cannot drift.
4. **Label Y off by about one text height.** The native y-up `LabelPoint` is
   the text's LOWER-LEFT, so the screen top is `anchor.Y − LabelPoint.Y·s −
   textHeight` (`ClassicGaugeLayout.LabelTopY`); HUD-14/14b used the point as
   the top. Added a headless test.
5. **Weak tests.** The back-band test compared `RevealDest` with itself; it now
   routes through `HudRenderer.BackDestination` (the back-draw path) and
   asserts it equals the fill band computed independently. The OXY test now
   builds a real lifepod-state `HudModel` and asserts the renderer's Fuel label
   is "OXY".
6. **Report corrections.** Commit hash `b60e216a` (not `d1d71af5`); the
   "1920×1080" assets are 900×760 crops; the band/quadrant "failing-first"
   claim was false (only the offset test was failing-first); item 7's "gauges
   render in hudColor" corrected to the teal accent they actually use.

## Visual proof — REAL client backbuffer (1920×1080)

Taken from real windowed `Allegiance.ClientCore.MonoGame` clients against a real local server (`m2xspiderw.igc`, PCore001, Alpha/Bravo), auto-launched into cockpit flight, captured via the production autoshot path. To stage the six gauges at distinct deterministic levels, a **temporary, env-gated screenshot aid** (`ALLEGIANCE_HUD_DEMO`) fed the sim values into an otherwise-real client frame; it lived only in the proof worktree and does not ship.

| Skin / state | Asset |
| --- | --- |
| Classic, six arcs ringing the reticle (hull 30% + flash, shield 70%, fuel 50%, ammo 20%, energy 90%, thrust 60%) | [`classic-gauges-1920x1080.webp`](HUD14_assets/classic-gauges-1920x1080.webp) |
| Modern (staged levels, no regression) | [`hud14b-modern-1920x1080.webp`](HUD14_assets/hud14b-modern-1920x1080.webp) |
| Classic lifepod — the upper-right gauge reads **OXY** | [`hud14b-lifepod-1920x1080.webp`](HUD14_assets/hud14b-lifepod-1920x1080.webp) |

Raw PNG originals remain under `~/allegiance-scratch/HUD14b/` (outside the repo); the three webp assets total ~27 KB (well under the 2 MB cap). (HUD-14c correction: although captioned "1920×1080", the three published webp assets are **900×760 centre crops** of the real backbuffer, not full-frame 1920×1080 captures.)

**Looking at the classic 1920×1080 shot (via vision):** the six gauges ring the central reticle correctly. THRUST and FUEL sit **above** centre (left and right), each arc revealing ~mostly-full; AMMO and ENERGY sit **below** centre (left and right); ARMOR and SHIELD flank the reticle at the centre line with their bottom-up fills and frame art. All six labels are legible in the additive cyan tint; the arcs ring the reticle instead of splaying into an X. No overlap or cut-off gauges.

**Looking at the modern 1920×1080 shot:** the modern skin's six circular gauges render cleanly at the staged levels (AMMO 20%, THRUST 60%, ARMOR 30%, SHIELD 70%, FUEL 50%, ENERGY 90%), symmetric around the crosshair — HUD-14b did not regress the modern skin (Modern overrides the classic gauge path).

**Looking at the lifepod 1920×1080 shot:** the classic ring is intact and the upper-right gauge (the fuel position) is labelled **OXY**, not FUEL — the pod OXY override is drawn (see fix 3). The "YOU ARE IN AN EJECT POD" banner sits below the ring as native intends.

## Verification

- **Whole-solution Release build**: 0 warnings / 0 errors (twice — before and after the proof-worktree aid, which was uncommitted and removed).
- **Failing-first**: the HUD-14b console-offset test was written against the corrected native-derived values (`Y = 240−consoleY`) and fails against HUD-14's sign (`Hull`/`Shield` were pinned at `-60`, actual `+60`; THRUST/FUEL above centre and AMMO/ENERGY below fail with HUD-14's inverted offsets). (HUD-14c correction: the earlier claim that the "band/quadrant tests likewise fail on HUD-14's data" is **false** — `ClassicGaugeLayout`/the quadrant tests did not exist on HUD-14, and `back == fill` at value 1 could never have failed; only the console-offset test was genuinely failing-first.)
- **`Hud14ClassicGaugeTests`** now 36 tests: reveal source rect 0/0.5/1 + native truncation, reveal direction, corrected console offsets, arc quadrant/ring placement, back band == fill band (band size, not whole texture), single pane scale, red/flash switch, `%` flag off by default, `AllLoaded` + all-6-kinds allocation, and the renderer OXY label.
- **Affected suites run 3× alone** (`Hud14*`, Lifepod, HudModel, Hud11/12/13/15, Radar): 219/219 pass ×3.
- **`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`** 5× alone: pass (1m48–1m50s each).
- **Fast tier** (`tools/run-tests.sh --tier fast`, final code): 9587 tests, 271.6 s, **PASS** (re-run after the item-7 additive-scope fix).
- **`python3 scripts/parity-scorecard.py`**: runs clean; `parity-matrix.csv` validates (665 rows); CSV is LF with exactly 6 columns (`{6}`). Scorecard now honestly reports hud at 19/108 done (down from HUD-14's inflated count after the six rows → partial).
- No `src/Allegiance.Core` / `src/Allegiance.Server` production source changed, so the conditional server AOT publish is not triggered.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| y-up console + child coordinates | `engine/context.cpp:552`; `dialog.mdl:225/231/311/317` | design | Converts every layer via `ClassicGaugeLayout` (item 1). |
| Rect-band reveal + justification | `engine/image.cpp:468-531`; per-gauge dialog.mdl | design | Ports the exact band + justified-edge rule. |
| Arc back = GaugeImageRect value-1 band | `dialog.mdl:2175-2185` etc. | design | Back drawn through the same band path as the fill (item 2). |
| Pod label override | (port extension; native has no lifepod OXY gauge) | design | `DrawGaugeLabel` uses `gauge.Label` (item 3). |
| `smVerdana` label font | dialog.mdl StringImage | era constraint | Substitute font stays; `partial` in the matrix (item 4). |
| Whole-console additive blend | `dialog.mdl:34` | design | Separate additive pass (item 7). |
| `hudColorshadows` hull/shield backs | `WinTrek/WinTrek.cpp:8688`; `dialog.mdl:2432,2515` | design | Tints hull/shield backs (item 7). |
| `hudColor` labels = side colour | `WinTrek/WinTrek.cpp:8681` | design | Port accent substituted; gap recorded (item 7). |
| One pane zoom | `dialog.mdl:5397` `.5` | design | One `PaneScale` for offsets + geometry (item 6). |

**Modern alternative considered.** Native stamps the console as a handful of surfaces and clips the reveal in software. A modern approach would bake the gauges into one atlas and drive fills with a masked fragment shader (fewer draw calls); this round keeps native's layered composition for fidelity and notes an atlas+shader rewrite as a clean follow-up. The single additive pass for the six gauges (vs. one Additive batch for the whole HUD) is a deliberate, contained compromise to avoid re-blending the rest of the overlay.

## Matrix

Six rows flipped `done → partial` with "native layer model; smVerdana substituted" + the item-7 tint gap, edited only as text (LF, 6 columns preserved). `Speed` (a separate text-readout row) is untouched and stays `partial`.

## Remaining gaps / out of scope

- Fonts: native `smVerdana` stays substituted (out of scope; the user hasn't chosen a replacement).
- Label tint: the port's accent stands in for the live side `hudColor` (the own side colour isn't threaded into the label pass) — recorded in the matrix.
- Speed row (`dialog.mdl:2546`): still bottom-centre typography rather than the native centre-pane `lgBoldVerdana` "n mps" string — a separate row, untouched.
- Target-pane gauges (`DrawTargetGauge`) use their own dedicated art and were not changed.
- Radar brackets (HUD-13) and command/mounting icons (HUD-15) are other agents' work — untouched.
