# HUD-14 — classic centre-console gauges on the native dialog.mdl layer model

Base: master at dispatch (HUD-12 `193e8ea3`). Branch: **master** (`64759ca8`), committed then amended with the suite + docs (see the final commit). The six own-ship gauge rows in `docs/parity/parity-matrix.csv` (Hull integrity, Shield charge, Energy reserve, Fuel reserve, Ammunition reserve, Thrust setting) were `partial` with the note "**whole-bitmap clipping/size/spacing differs from native dialog.mdl rect layers; port % numeral is an extension**". All six are flipped to `done`; the `%` numeral is now a HUD option behind `HudSettings.GaugePercentage` (default **off** = native).

## What changed

Native builds each classic centre gauge as a `GroupImage` of layered images (`~/Development/Artwork/dialog.mdl:2173-2516`) assembled into the `DefaultCenterHudOn` console group (`dialog.mdl:2604-2623`): a frame (hull/shield), a faint back, a fill revealed through a `GaugeImageRect(justify, ImportImage(...), Rect(...), true, value)` that draws only that **Rect** of the bitmap in proportion to the value, a red variant switched by `Flash`, a spine (thrust/fuel/ammo/energy), and a `StringImage` label, each placed by `TranslateImage` and finally by the console `TranslateImage`.

The port's classic renderer previously clipped the **whole** 128 px bitmap, drew a hand-tuned spread and size, used `GaugeTextureSource.MdlStemFor` (the **faint back** stem) as the sole revealed texture for the arc gauges, and always drew a `%` numeral. HUD-14 replaces that with a data-table-driven native layer model.

### New data table — `ClassicGaugeSpec` / `ClassicGaugeTable` (`src/.../Rendering/ClassicGaugeSpec.cs`)

One record per gauge — not hand-tuned constants — with every field citing its `dialog.mdl` line:

| Gauge | back | fill | red | spine | frame | source rect | reveal | console offset | label | value source | mdl |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Thrust** | thrustgaugebmp | thrustgauge2bmp | – | thrustspinebmp | – | (15,10,95,108) | Top | (−159,6) | THRUST | playerThrottle | 2173 |
| **Fuel** | fuelgaugebmp | fuelgauge2bmp | – | fuelspinebmp | – | (17,10,96,108) | Top | (41,6) | FUEL | GetFuel(Me) | 2226 |
| **Ammo** | ammogaugebmp | ammogauge2bmp | – | ammospinebmp | – | (29,18,69,92) | Bottom | (−173,−135) | AMMO | GetAmmo(Me) | 2278 |
| **Energy** | energygaugebmp | energygauge2bmp | – | energyspinebmp | – | (30,18,69,92) | Bottom | (52,−135) | ENERGY | GetPercentEnergy(Me) | 2331 |
| **Hull** | hullgauge2bmp | hullgaugebmp | hullgaugeredbmp | – | centerhudleftbmp | (40,23,48,82) | Bottom | (−105,−60) | ARMOR | GetPercentHitPoints(Me) | 2404 |
| **Shield** | shieldgauge2bmp | shieldgaugebmp | shieldgaugeredbmp | – | centerhudrightbmp | (40,23,48,82) | Bottom | (−20,−60) | SHIELD | GetPercentShields(Me) | 2474 |

- **Source rects** are the native `GaugeImageRect` rects transcribed exactly (`dialog.mdl:2179,2233,2286,2339,2407,2481`) — e.g. hull `Rect(40,23,88,105)` becomes the 48×82 band. The reveal restricts to this band; whole-bitmap clipping is gone.
- **Console offsets** are the `DefaultCenterHudOn` `TranslateImage` points (`dialog.mdl:2610-2615`) minus `(320,240)` — the 640×480 pane's top-left is at centre−(320,240) (`dialog.mdl:5397`) — giving centre-relative positions, scaled by the classic cluster spread exactly like the rest of the classic console (`OwnStatusLayout.Spread`).
- **Layer child points** (back/fill/spine/frame/label `TranslateImage`) are transcribed per gauge, e.g. hull fill at `(27,27)` (`dialog.mdl:2427`) while its frame is at `(0,0)` and its back at `(−13,4)` (`dialog.mdl:2433`).
- **Label alignment** matches the native `StringImage` justification: THRUST/AMMO/SHIELD right-aligned into the 100-wide box, FUEL/ENERGY/ARMOR left-aligned.

### Reveal rule — `ClassicGaugeTable.RevealSource`

Mirrors native `GaugeImageRect` (b3D path, `engine/image.cpp:468-531`): only `yoffset = (int)(rect.Height · value)` rows of the band are drawn, from the justified edge — **Top** anchors to the band top and grows down (THRUST/FUEL, `dialog.mdl:2173,2226` JustifyTop), **Bottom** anchors to the band bottom and grows up (AMMO/ENERGY/HULL/SHIELD, `dialog.mdl:2278,2331,2404,2474` JustifyBottom). This is the same direction HUD-6 already validated; only the source **band** (and the layer composition) changed. The `(int)` cast reproduces native truncation.

### Texture loading — `GaugeTextures`

Now loads every native layer per `ClassicGaugeTable` (back, bright `*2` fill, red variant, spine, centerhud frame). The critical fix: the arc gauges reveal the bright **`*gauge2bmp` fill** (e.g. `thrustgauge2bmp`) instead of the faint back (`thrustgaugebmp`), and the back is drawn as a separate full-value layer. Missing layers degrade gracefully.

### The `%` numeral — `HudSettings.GaugePercentage` (default off)

Native has no percentage on these gauges. The port's `%` readout is now an **extension behind `HudSettings.GaugePercentage`** (default `false` = native), threaded through `HudRenderer` and `Game1.Lifecycle`. With the flag on, the classic renderer draws the percentage centred beneath the gauge; off (default), none — matching native. The label itself is drawn at its native position (not below the gauge) in both cases.

## Value sources → HUD model mapping

| Native binding | Port HUD model field |
| --- | --- |
| `GetPercentHitPoints(Me, OnEveryFrame)` (`dialog.mdl:2404`) | `HudFrame.HullFraction` |
| `GetPercentShields(Me, OnEveryFrame)` (`:2474`) | `HudFrame.ShieldFraction` |
| `GetPercentEnergy(Me, OnEveryFrame)` (`:2331`) | `HudFrame.EnergyFraction` |
| `GetFuel(Me, OnEveryFrame)` (`:2226`) | `HudFrame.FuelFraction` (pod OXY override stays a port extension) |
| `GetAmmo(Me, OnEveryFrame)` (`:2278`) | `HudFrame.AmmoFraction` |
| `playerThrottle` (`:2173`; the dialog notes at `:503` that FUEL and THROTTLE are switched in naming) | `(HudFrame.Throttle + 1) / 2` |

The `Flash` damage binding (`dialog.mdl:2418-2440` hull, `:2498-2510` shield) maps to `HudFrame.Flashing`, which the model applies to Hull and Shield only — and the renderer selects the red variant exactly when `ClassicGaugeTable.UseRedFill(DamageFlash, spec)` (flash on **and** a red stem exists).

## Verification

- **Whole-solution Release build**: 0 warnings / 0 errors.
- **Failing-first**: the reveal-rect tests were written against hand-derived native numbers, failed, and exposed a transcription bug (the XNA `Rectangle(X,Y,W,H)` values were the native `Rect` endpoints), then passed once the native band widths/heights were correct — the tests now pin the corrected values.
- **New `Hud14ClassicGaugeTests`** (27 tests): reveal source rect at 0 / 0.5 / 1 with native integer truncation; reveal direction and `DefaultCenterHudOn` console offsets; the bright-fill-vs-back split (the core loader fix); the red/flash switch condition; the `%` flag off by default; and the allocation-free per-frame gauge path (`Spec`/`RevealSource`/`UseRedFill` allocate nothing over 200 k iterations).
- **Affected suites**: `Hud14ClassicGaugeTests` 27/27, `HudModelTests`, `ModernHudTests`, `MdlTextureDecoderTests`, `Hud11LayoutPerfTests`, `Hud6bOwnStatusTests`, `Hud12DataAndArtTests`, `LifepodHudTests` — 133 pass, 3 skip (pre-existing conditional skips).
- **`P674ParityScorecardTests`**: 8/8 pass; `parity-matrix.csv` validates (665 rows) and the scorecard reports `hud` **17/108** done.
- **Fast tier** (`tools/run-tests.sh --tier fast`): **9486 tests, 275.0 s, PASS**.
- No `src/Allegiance.Core` / `src/Allegiance.Server` production source changed, so the conditional server AOT publish is not triggered.

## Real-client screenshots (the REAL backbuffer)

Two real windowed `Allegiance.ClientCore.MonoGame` clients against a real local server (`m2scatter.igc`, PCore001, Alpha/Bravo) flew and captured the classic console via the production autoshot path. To stage the six gauges at distinct deterministic levels, a **temporary, env-gated screenshot aid** (`ALLEGIANCE_HUD_DEMO="30,70,50,20,90,60,flash"`) fed the sim input values (hull 30% + flash, shield 70%, fuel 50%, ammo 20%, energy 90%, thrust 60%) into an otherwise-real client frame; it was reverted before commit and does not ship. The rendering — layers, reveal, colours, flash, labels, no-% default — is the real client's.

| Resolution | Asset |
| --- | --- |
| 1920×1080 | [`classic-gauges-1920x1080.webp`](HUD14_assets/classic-gauges-1920x1080.webp) |
| 1280×720 | [`classic-gauges-1280x720.webp`](HUD14_assets/classic-gauges-1280x720.webp) |
| Native plan vs real | [`compare-native-layout-vs-real.webp`](HUD14_assets/compare-native-layout-vs-real.webp) |

Raw PNG originals remain under `~/allegiance-evidence/HUD14/raw/` (outside the repository); webp assets total 64 KB (under the 2 MB cap).

**Describing the 1920×1080 shot (from looking at it):** six arc gauges cluster around the view centre. HULL (labelled ARMOR) is lower-centre-left, filled bottom-up to ~30% in solid red (the damage flash); SHIELD lower-centre-right, bottom-up ~70%, also red mid-flash (native flashes hull and shield together on the single `Flash` timer). THRUST lower-left and FUEL lower-right reveal top-down to ~60% and ~50% in the native bright segmented-art tint; AMMO upper-left bottom-up ~20%; ENERGY upper-right bottom-up ~90%. All six labels (ARMOR/SHIELD/FUEL/THRUST/AMMO/ENERGY) are legible at their native positions; **no % numerals appear** (flag off). No overlap, garble or missing gauge.

**Describing the 1280×720 shot (from looking at it):** the same six gauges remain clean and correctly placed at the lower resolution — AMMO/ENERGY upper pair, ARMOR/SHIELD centre pair with frames, THRUST/FUEL lower pair; fill directions and the low/high levels read correctly; no overlap, garble or missing gauge; no % numerals.

**Native-layout comparison (side-by-side):** the left blueprint (native `DefaultCenterHudOn` plan, 640×480 pane outlined) and the right real-client crop occupy the same relative positions — THRUST/FUEL lower-centre, AMMO/ENERGY upper, and the frame-bearing ARMOR/SHIELD centred between them — and the same fill levels, confirming the console offsets in the data table drive the real render.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| Rect-band reveal + justification | `engine/image.cpp:468-531`; `dialog.mdl` per gauge | design | Ports the exact band + justified-edge rule (`RevealSource`). |
| Layered GroupImage composition (frame/back/fill/red/spine/label) | `dialog.mdl:2173-2516` | design | Ports every layer and its child `TranslateImage` point. |
| Red variant switches on `Flash` for hull **and** shield | `dialog.mdl:2418-2440,2498-2510` | design | Ports `UseRedFill(Flash, spec)`; both go red together like native. |
| Faint back = the `*gaugebmp` art; bright fill = `*gauge2bmp` | `dialog.mdl:2179-2189` etc. | design | The core fix: reveals the bright fill, draws the back separately. |
| No % numeral | (absent in dialog.mdl) | design | Port extension behind `HudSettings.GaugePercentage`, default off. |
| Console offsets from `DefaultCenterHudOn` | `dialog.mdl:2604-2623,5397` | design | Ports the offsets; scaled by the classic cluster spread (era-scaled, see below). |
| `smVerdana` labels | `dialog.mdl` | era constraint | Keeps the port's HUD font substitute (fonts are out of HUD-14 scope). |

**Modern alternative considered.** Native stamps the whole console as a handful of 640×480 surfaces and clips the reveal in software. A modern approach would bake the six gauges into a single atlas and drive the fills with a masked fragment shader (one draw call instead of ~30), and hoist the invariant per-gauge source rects (which this change already does — `RevealSource` is allocation-free and the spec table is precomputed). This change keeps native's layered-Draw composition for fidelity; an atlas+shader rewrite is a clean follow-up with identical output and lower draw-call cost, and is noted for a future rendering pass.

## Matrix

Six rows flipped `partial → done` with notes citing the native rects, layer stems, reveal, console offsets and the new `%` flag. `Speed` (a separate text-readout row) is untouched and stays `partial`; the label font substitution is likewise unchanged (noted, not solved).

## Remaining gaps / out of scope

- Fonts: native `smVerdana` stays substituted (out of HUD-14 scope).
- Speed row (`dialog.mdl:2546`): still bottom-centre typography rather than the native centre-pane `lgBoldVerdana` "n mps" string — a separate row, untouched here.
- Target-pane gauges (`DrawTargetGauge`) use their own dedicated art and were not changed; the shared HUD font path is unchanged, so their output is identical.
- Radar brackets (HUD-13) and command/mounting icons (HUD-15) are other agents' work — untouched.
