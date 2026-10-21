# FONT-1: native HUD fonts (bundled DejaVu Sans at the native Verdana sizes and weights)

## Summary

Every text-bearing classic HUD row was stuck at `partial` because the port drew all HUD
text in one substitute font (`HUD.spritefont` DejaVu Sans Mono 14 / `HUDLarge.spritefont`
30). Verdana cannot be shipped (not redistributable), so per Domingo's decision the classic
HUD now uses **bundled DejaVu Sans / DejaVu Sans Bold** (the Bitstream Vera lineage) baked
at each native pixel size and weight. The fonts are committed in the repo
(`src/Allegiance.ClientCore.MonoGame/Content/Fonts/`), so the content build gives the same
output on Linux, macOS and Windows with **no fonts installed**.

## How the native sizes were measured (from `font.mdl`)

The native classic-HUD fonts are pre-rasterised bitmap fonts, not `Win32Font`. They live in
`~/Development/Artwork/font.mdl` (14 `ImportFont` objects; `engine/font.cpp:353-366` reads
each via `FontImpl(IBinaryReaderSite*)`: DWORD height, DWORD width, DWORD dataSize, then
`CharData m_data[256]` — 12 bytes each: `WinPoint m_size` + `int m_offset`,
`engine/font.cpp:29-50` — then the glyph bits). This task parsed the MDL directly (scanning
the 14 `CharData` blocks in symbol order; cell height = each header's `m_size.y`) and
compared the ASCII 32-126 advance-width sums against DejaVu (and, as report-only evidence,
Verdana) rasterised with FreeType.

**MonoGame `<Size>` is in POINTS, not pixels.** Empirically verified on this pipeline
(MonoGame 3.8.5.1 MGCB): a baked font's `LineSpacing` and glyph advances for `<Size> N`
match FreeType at em **N·96/72 = N·1.3333**. Fractional `<Size>` is floored (Size 7.5 bakes
identically to Size 7), so the achievable em set is `{8.0, 9.33, 10.67, 12.0, 13.33, …}`.
Each native font was assigned the integer `Size` whose achieved em best matches its
`font.mdl` width table.

### Final font table (native cell height, chosen Size / achieved em, width error)

| Native font | cell (px) | Face | Size(pt) | achieved em | ASCII 32-126 width error |
|---|---|---|---|---|---|
| smVerdana | 10 | DejaVu Sans | 6 | 8.00 | 19 |
| medVerdana | 12 | DejaVu Sans | 8 | 10.67 | 18 |
| smallFont | 12 | DejaVu Sans | 8 | 10.67 | 18 (shares bake with medVerdana) |
| largeFont | 14 | DejaVu Sans | 9 | 12.00 | 23 |
| smallBoldFont | 12 | DejaVu Sans Bold | 7 | 9.33 | 2 |
| medBoldVerdana | 13 | DejaVu Sans Bold | 8 | 10.67 | 62 (short labels; per-char avg <1px) |
| promptFont | 12 | DejaVu Sans Bold | 8 | 10.67 | 12 (shares bake with medBoldVerdana) |
| largeBoldFont | 14 | DejaVu Sans Bold | 9 | 12.00 | 39 |
| lgBoldVerdana | 16 | DejaVu Sans Bold | 10 | 13.33 | 2 |

Seven unique (face, Size) bakes, each at 1× and 2×: **R6, R8, R9, B7, B8, B9, B10**
(`NativeR6`…`NativeB10` and `@2x` variants). The task's scoping table recorded Regular
8/10/12 and Bold 9/11/12/13; the raw width-fit lands within one Size of those for most
cells, and where it differs (medBoldVerdana → B8 to honour the task's recorded Bold-11
decision, which shares the promptFont bake; largeBoldFont → B9) the recorded decision is
followed. Each em above is the achieved pixel size (Size·96/72) whose advance-sum is shown.

### Native cell heights verified directly from `font.mdl` (all match the scoping table)
smallFont 12, smallBoldFont 12, largeFont 14, largeBoldFont 14, smVerdana 10, medVerdana 12,
medBoldVerdana 13, lgBoldVerdana 16, promptFont 12.

## Per-pane scale and resulting on-screen em at 1080p / 1440p

The port scales HUD panes independently (Scope 5 of the spec). Each classic-HUD text site
now uses the font at the pane's own position scale (floored at 1), so text grows with its
pane instead of staying at scale 1 in the mono 14pt font.

| Pane | pane scale | native font | em at 1080p | em at 1440p |
|---|---|---|---|---|
| Console text | TextScale = 1 | promptFont/medVerdana/etc | ~10.7-13.3 | ~10.7-13.3 |
| Target pane | clamp(H/680,0.9,2.1) | medVerdana / smVerdana | 1.588×em → ~16.9 | 2.1×em → ~22.4 |
| Inventory pane | clamp(H/680,0.9,2.1) | smallFont | 1.588×em → ~16.9 | 2.1×em → ~22.4 |
| Own status / badges | clamp(H/680,1.15,2.1) | medBoldVerdana | 1.588×em → ~16.9 | 2.1×em → ~22.4 |
| Classic gauge labels | max(1, PaneScale)=1 | smVerdana | ~8.0 | ~8.0 |
| Chat history | 1 (shrink dropped) | largeFont | ~12.0 | ~12.0 |

**Recorded deviation:** classic gauge labels are drawn at `max(1, PaneScale)` = **1**, i.e.
the native 0.72 pane scale would have put smVerdana at ~6 px; the port keeps them at the
readable 1× bake (recorded per the spec).

## Site → font list

Every classic-HUD text site in `HudRenderer.cs` was switched through a new
`protected virtual (SpriteFont, float) HudFont(NativeFont id, float paneScale)` hook
(classic → bundled registry; **Modern overrides it to keep its own SDF small/big fonts, so
Modern renders exactly as before**). Native `font.mdl` / `dialog.mdl` citations in code.

- `DrawConsoleText`: "Current location:/Turret of:/Current orders:" → **promptFont**; sector/
  carrier/order text → **medVerdana**; vote banner → **medBoldVerdana**; critical tips →
  **medBoldVerdana**; ordinary tips → slot 0 **medBoldVerdana**, slot 1 **medVerdana**, slot 2
  **smVerdana** (dialog.mdl:2006-2031); ripcord → **lgBoldVerdana**.
- Classic gauges (`DrawClassicGauge` fallback + `%` numeral, `DrawGaugeLabel`) → **smVerdana**
  (dialog.mdl:2204-2467).
- `DrawOwnStatus`: kill bonus number/badge → **medBoldVerdana** (dialog.mdl:2556); signature →
  **medVerdana** (2595); cloak (port addition) → **medVerdana**.
- Lives/kills badges (`DrawConsoleBadge`) → **medBoldVerdana** (5070-5128).
- Command summary → **medVerdana**; ship type → **medBoldVerdana** (4717-4721, 4988-4992).
- `DrawStatusIcon` fallback labels → **medVerdana**.
- `DrawSpeed`: numeral → **lgBoldVerdana** (2546), unit → **smVerdana** (Speed row stays partial).
- `DrawTargetPane`: name / ARMOR / SHIELD → **medVerdana**; range / speed → **smVerdana**.
- `DrawInventoryPane` ship name → **smallBoldFont** (1803); `DrawInventoryRow` every string →
  **smallFont** (`StringPane` default, trekmdl.cpp:390).
- Radar label block → **smallFont** (radarimage.cpp:1189).
- Port-only text with no native counterpart → **medVerdana**.
- `ChatRenderer` (classic): history → **largeFont** (would be largeBoldFont for leader lines —
  the chat model carries no per-line "from leader" flag, recorded gap); compose → **smallFont**.
  The `ChatPaneLayout.TextScale` shrink drops away because the native largeFont bake's
  LineSpacing is exactly 14 = the native item height.

Critical-tip wrap check: the port has **no** counterpart of `console.cpp:1067-1069`
(`GetMaxTextLength(str, 620)`); critical tips are fitted to the viewport by
`DrawMeasuredText`, so nothing extra to change.

## Parity matrix

**17 rows flipped to `done`** (font was their only gap): Kill bonus, Target speed, Turret
carrier identity, Accepted order text, Queued order text, Vote banner, Ripcord countdown,
Equipment quantity and empty warning, Equipment weapon range, Equipment shot damage,
Equipment burst rate, Equipment shield strength, Equipment shield recharge seconds,
Equipment afterburner top speed, Equipment afterburner endurance, Inventory own ship name,
Equipment empty slot affordances.

**Kept `partial`** (other gaps remain; font clause replaced): Signature, Target name, Radar
name/range labels, Radar bars, Sector location, Chat history, Tip text lines, Critical tips,
Money credits, Flight inventory pane, Part identity and key, Cargo slots, Command view
summary. **HUD-14b gauge rows 382-387 stayed `partial`** — the font is not their sole gap
(the label tint still uses the port accent, not the live side colour).

Every edited line replaces the font clause with `DejaVu Sans substitutes Win32 Verdana (not
redistributable); native <font>` and keeps exactly 6 columns, LF endings.

## Tests (MonoGame.Tests)

`Font1NativeFontTests` (8 tests): (a) registry maps every classic-HUD native identity to a
(face, px, bold) pair and both density assets are built (`Content.mgcb` + file existence);
seven unique bakes; the density selector (1× ≤ 1.25, else 2× with residual). (b) source
guard: no classic-HUD text site in `HudRenderer.cs`/`ChatRenderer.cs` passes `_small`/`_big`
to a draw/measure call (only the `HudFont` fallback). (c) every `.spritefont` `<FontName>`
ends in `.ttf` whose file exists relative to it; nothing in `.spritefont`/`.mgcb`/csproj
names Verdana. (d) Modern's `HudFont` override returns its own small/big for every id.
(e) the DejaVu licence sits next to the bundled `.ttf`s.

New + affected tests run **3× alone**: 228/228 each time. **One full fast tier**
(`tools/run-tests.sh --tier fast`): **PASS, 9643 tests, 272.3 s**.

## Content build / fonts bundled

The `.ttf`s are copied into `Content/Fonts/`; every `.spritefont` `<FontName>` points at the
bundled file by a path relative to the `.spritefont` (verified MGCB resolves it that way).
MGCB log names `Content/Fonts/DejaVuSans.ttf` (7×), `DejaVuSans-Bold.ttf` (8×),
`DejaVuSansMono.ttf` (2×) = 17 fonts. Rebuilding content with system DejaVu hidden from
fontconfig (`FONTCONFIG_FILE` → empty dir) still succeeds from the bundled files. `TextureFormat
= Color` (not Compressed) keeps small glyphs crisp. No `.ttf` is copied to output as loose
content (only the `.xnb`s ship).

## Real-client screenshots (classic skin, real backbuffer)

Captured from the real windowed client (P572 live-proof, local server, 221 map
`m2xspiderw.igc`) via `ALLEGIANCE_HUD_SETTINGS` selecting the skin. Full-resolution PNGs
are in `~/allegiance-evidence/FONT1/`; compressed JPEGs are committed here under
`docs/reports/FONT1_assets/`.

| Asset | Resolution | Skin | What it shows |
|---|---|---|---|
| `FONT1_assets/classic_2560_inflight.jpg` | 2560×1440 | Classic | in-flight: target pane (`Omega Debug Garrison: Garrison`, RANGE 451, SPEED 0, ARMOR, SHIELD), console `Current location: Omega`, chat log (`[ALL] Ganymede secured. …`), classic gauges THRUST/FUEL/AMMO/ENERGY/ARMOR/SHIELD, speed `5 mps`, `SIG: 60%`, radar labels |
| `FONT1_assets/classic_2560_inventory.jpg` | 2560×1440 | Classic | same plus the F4 inventory pane (`Lxy Scout` + `<cargo mount>` rows) open on the right |
| `FONT1_assets/classic_1080.jpg` | 1920×1080 | Classic | inventory + target + chat + console + gauges at 1080p |
| `FONT1_assets/modern_1080.jpg` | 1920×1080 | Modern | **unchanged** Modern skin (clean smooth arc gauges, SDF text) — proves FONT-1 did not alter Modern |

**Vision readback (hermes vision on the real PNGs):** the classic HUD text is a
proportional sans-serif — close-up of the chat/console glyphs shows a round open `e` and
clearly variable letter widths (`i`/`l` narrower than `m`/`w`), i.e. DejaVu Sans, not the
old monospace. Text sizes/weights are consistent; nothing is blurred or clipped. Two minor
non-font overlaps are noted as layout, not font defects: the target name and a radar label
for the same object sit close together (both reference the garrison), and the minimap
touches the inventory pane's edge at 2560×1440. The Modern shot's gauges are the thin
smooth arcs — the Modern skin is byte-for-byte unchanged in look.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Classic HUD drawn in proportional Verdana at small pixel sizes | Artwork/font.mdl, dialog.mdl | design | Bundled DejaVu Sans/Bold at the measured native em sizes |
| MonoGame font `Size` in points (×96/72) | MonoGame 3.8.5.1 MGCB | tool quirk | measured empirically; baked with the point→pixel conversion |
| console text at native 1:1 every resolution | HudRenderer.TextScale | design | kept |
| pane text not scaling with its pane (today) | HudRenderer | era constraint / port limitation | now scales with the pane (Scope 5) |

## Modern alternative considered

Native bakes tiny pre-rasterised bitmaps; a modern engine would render glyphs with a signed
distance-field font or vector text at draw time. The port's Modern skin already uses an SDF
atlas; the classic skin keeps baked spritefonts (matching native's crisp 1:1 small text and
the pixel-locked console layout). Trade-off: baked fonts can't be re-sized losslessly, which
is why FONT-1 bakes each used size at 1× and 2× and scales the residual (never upsampling a
small atlas).
