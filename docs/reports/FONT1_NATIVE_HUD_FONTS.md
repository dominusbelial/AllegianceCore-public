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
| medBoldVerdana | 13 | DejaVu Sans Bold | 9 | 12.00 | 16 (FONT-1b; Size 8 measured −62) |
| promptFont | 12 | DejaVu Sans Bold | 8 | 10.67 | 12 (B8, promptFont only after FONT-1b) |
| largeBoldFont | 14 | DejaVu Sans Bold | 9 | 12.00 | 39 |
| lgBoldVerdana | 16 | DejaVu Sans Bold | 10 | 13.33 | 2 |

Seven unique (face, Size) bakes, each at 1× and 2×: **R6, R8, R9, B7, B8, B9, B10**
(`NativeR6`…`NativeB10` and `@2x` variants). The task's scoping table recorded Regular
8/10/12 and Bold 9/11/12/13; the raw width-fit now lands within one Size of those for every
cell. **FONT-1b corrected one entry:** medBoldVerdana was baked at Size 8 (−62 px measured
width error); Size 9 measures +16 and its native cell is 13 px vs promptFont's 12 px, so the
two must not share a bake — it now uses the existing B9 bake (the stem count stays 7).
largeBoldFont on B9 is not a deviation (its native cell is 14 px). Each em above is the
achieved pixel size (Size·96/72) whose advance-sum is shown.

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
| Own status (SIG, cloak strength, missing-art labels) | clamp(H/680,1.15,2.1) | medVerdana (Size 8) | 1.588×em → ~16.9 | 2.1×em → ~22.4 |
| Own-status kill-bonus number (on the medal) | medal group 0.72, floored at 1 | medBoldVerdana (Size 9) | ~12.0 (measured: digit ink 8 px tall) | ~12.0 |
| Lives/kills badges | TextScale (native 1:1) | medBoldVerdana (Size 9, em 12) | ~12.0 | ~12.0 |
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

**17 rows are `done`** (font was their only gap; FONT-1 flipped 17, FONT-1b returned
Kill bonus to `partial`, and **FONT-1c returned it to `done`** after the native y-up group
offsets put the black integer on the medal disc — see the review-fixes section): Target
speed, Turret carrier identity, Accepted order text, Queued order text, Vote banner,
Ripcord countdown, Equipment quantity and empty warning, Equipment weapon range, Equipment
shot damage, Equipment burst rate, Equipment shield strength, Equipment shield recharge
seconds, Equipment afterburner top speed, Equipment afterburner endurance, Inventory own
ship name, Equipment empty slot affordances, Kill bonus.

**Kept `partial`** (other gaps remain; font clause replaced): Signature, Target
name, Radar name/range labels, Radar bars, Sector location, Chat history, Tip text lines,
Critical tips, Money credits, Flight inventory pane, Part identity and key, Cargo slots,
Command view summary. **HUD-14b gauge rows 382-387 stayed `partial`** — the font is not their sole gap
(the label tint still uses the port accent, not the live side colour).

Every edited line replaces the font clause with `DejaVu Sans substitutes Win32 Verdana (not
redistributable); native <font>` and keeps exactly 6 columns, LF endings.

## Tests (MonoGame.Tests)

`Font1NativeFontTests` (9 tests, final FONT-1b state): (a) registry maps every classic-HUD
native identity to a (face, px, bold) pair and both density assets are built (`Content.mgcb` +
file existence); seven unique bakes; the density selector (1× ≤ 1.25, else 2× with residual).
(a2) FONT-1b per-id table: every identity's 1×/`@2x` `.spritefont` face, Size, Style and exact
double-density Size match the `font.mdl`-derived expected table. (b) source guard: no
classic-HUD text site in `HudRenderer.cs`/`ChatRenderer.cs` passes `_small`/`_big` to a
draw/measure call (exact-line whitelist: only the `HudFont` fallback line). (c) every
`.spritefont` `<FontName>` ends in `.ttf` whose file exists relative to it; nothing in
`.spritefont`/`.mgcb`/csproj names Verdana. (d) FONT-1b behaviour test: Modern's `HudFont`
override returns its own small/big for every id at its own `TextScale` (no GraphicsDevice).
(e) the DejaVu licence sits next to the bundled `.ttf`s.

Final measured gate (FONT-1b tree): `Font1NativeFontTests` **9/9 × 3 runs alone**; one full fast
tier (`tools/run-tests.sh --tier fast`): **PASS, 9704 tests, 270.7 s**. The first fast run hit one
unrelated `TargetPaneModelTests.Unseen_or_unlive_target_reads_zero_hull_and_shield` concurrent-
dictionary exception under parallel load; it passed **3/3 alone** and the second full run passed
(`docs/TEST_FLAKES.md` load flake).

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

FONT-1b re-shot every flipped row that this review asked to see plus the two layout fixes; the
new assets and the per-shot vision readback are in the **Review fixes (FONT-1b)** section below.

**Vision readback (hermes vision on the real PNGs):** the classic HUD text is a
proportional sans-serif — close-up of the chat/console glyphs shows a round open `e` and
clearly variable letter widths (`i`/`l` narrower than `m`/`w`), i.e. DejaVu Sans, not the
old monospace. Text sizes/weights are consistent; nothing is blurred or clipped. Two minor
non-font overlaps are noted as layout, not font defects: the target name and a radar label
for the same object sit close together (both reference the garrison), and the minimap
touches the inventory pane's edge at 2560×1440. The Modern shot's gauges are the thin
smooth arcs — the Modern skin is byte-for-byte unchanged in look.

## Review fixes (FONT-1b)

Base: master `cc5b55e9` (FONT-1 `0fab95a5`); branch `pi/font1b`. The reviewer's re-measurements
were confirmed: MonoGame `<Size>` is points and fractional sizes floor, so the bake set is
8 / 9.33 / 10.67 / 12 / 13.33 px, and the native width errors are the report's table (smVerdana
−19, med/smallFont +18, largeFont −23, smallBold −2, medBold −62, prompt −12, largeBold −39,
lgBold −2). Native per-site choices in `Artwork/dialog.mdl` were spot-checked as correct.
Verdana is still never bundled or referenced.

### 1. medBoldVerdana Size 8 → 9

`src/Allegiance.ClientCore.Game/Rendering/NativeFonts.cs` now maps
`[NativeFont.MedBoldVerdana] = 9` (em 12.00). The measured ASCII 32-126 width error is **−62 at
Size 8** and **+16 at Size 9**, and medBoldVerdana's native cell is 13 px vs promptFont's 12 px,
so they must not share a bake. The Bold-9 bake already exists (`NativeB9`), so the stem count
stays 7 (R6, R8, R9, B7, B8, B9, B10). The report's font table and per-pane table now use
Size 9 / em 12.00 / ~19.1 px at 1080p; parity rows 389, 456 and 457 say `(~12px)`. "largeBoldFont
→ B9" is **not** a deviation (its native cell is 14 px) and was dropped from the deviation list.
The per-id test below pins Size 9 from the `font.mdl` table.

### 2. Command-view summary is native 1:1

`HudRenderer.DrawCommandShipSummary` and `DrawCommandGauge` no longer use the own-status
`Spread` for the summary: the text uses `HudFont(id, TextScale)`, and the gauge bar's 66 px
inset / 52×4 art size use the same `TextScale`, matching the native
`cortCommandModeInfo` group at `SideLeft(-200,120)->(36,120)` (`Artwork/dialog.mdl:4997-5027`,
`:5412`; data `WinTrek/console.cpp:778-800`). The pane's `(36, h/2−120)` anchor is in native
pixels and was not spread-scaled. At 1080p the rows are 13 px apart with 12 px em text, and the
screenshot shows no overlap (the old ~20 px/26 px glyph boxes on 13 px rows are gone).

### 3. Lives/kills badge text and the kill-bonus number

- `DrawConsoleBadge` now calls `HudFont(NativeFont.MedBoldVerdana, TextScale)`. The badge art is
  drawn at `1f` and its box/offsets at `TextScale` (`dialog.mdl:5070-5128`), so the text at
  `TextScale` is the scale "this pane already uses for its positions"; the old `Spread` (~1.59×
  at 1080p) made the glyphs spill above the 34 px badge.
- Kill bonus (`DrawOwnStatus`): the native `ExperienceImage` is ONE group — the black integer and
  the `killcounterbmp` bitmap are drawn at the same group scale (`Artwork/dialog.mdl:2556-2566`,
  `:2617`; `WinTrek/console.cpp:830`). The port draws the bitmap at the native 0.72 art base, so
  the integer is now `artScale * killFs` where `killFs` is the registry's residual (1× bake for a
  0.72 pane), and its 80-wide box is placed relative to the bitmap at the native inner offset
  `(0−(−23), 5−(−65)) = (23,70)` pane px × 0.72
  (`OwnStatusLayout.KillCounterNumberFromBitmap`, cited to `dialog.mdl:2561-2566`). This also
  fixes the old spread-scaled box, which put the number ~19 px BELOW the 92 px-tall drawn bitmap
  instead of inside it. `Hud6bOwnStatusTests` pins the inner offset.
  **FONT-1c correction:** `(23,70)` was still wrong because the bitmap's `Point(-23,-65)` is its
  y-UP LOWER-left, and 70 was an additive restatement that ignored the 128 px bitmap height.
  FONT-1c derives `(23, H−70)·g` from the bitmap top (H=128 → 58) at the same group scale g;
  see review fix 2 below. The target pane's own group is `TargetPaneLayout`, untouched.

  Evidence: `FONT1B_assets/font1b-389-killbonus-1920x1080.jpg` shows the red star/wings
  `killcounterbmp` at the ExperienceImage anchor with the live 25% server kill bonus
  (`AddKillBonus` ×4 on the real server ship). The decoded bitmap is a 128×128 canvas whose
  surround is fully transparent (saved as `FONT1B_assets/killcounterbmp-decoded.png`), so the
  native black `(0,0,0)` integer is not legible against space. **Row 389 therefore stays
  `partial`** with a note that the integer's on-screen check is owed; the remaining gap is the
  native 3-D surface/plate behind the number (HUD-6 art decode), not the font size or weight.

### 4. On-screen check for every flipped row (matrix honesty)

New proof harness `tools/font1b-proof` (outside the solution, like `hud18-proof`; **retired in
the FONT-1c review round** — `tools/font1c-proof` now also shoots the command summary): the real
`Game1` window, a real local server, classic skin, 1920×1080 backbuffer. States the local server
cannot reach in a two-minute proof are injected through the SAME wire messages the production
client dispatches (`ClientWorld.Apply`: `ExportShip` + `ShipStatusMessage` for the turret seat,
`OrderChange`/`QueuedCommandUpdate`, `Ballot`, `RipcordActivate`, `PlayerInfo`); ordinary and
critical tips go through `ClientWorld.PostPlainText`. The server ticker is paused while an
injected state is on screen so its per-tick own-ship `SHIP_STATUS` cannot race it. Full-resolution
PNGs are in `~/allegiance-evidence/FONT1B/`; the committed JPEGs/crops are in
`docs/reports/FONT1B_assets/` (1.1 MB total).

| Row(s) | Asset | What is actually visible (opened and read) |
|---|---|---|
| 389 Kill bonus | `font1b-389-killbonus-1920x1080.jpg`, `crop-389-killbonus.png` | red star-and-wings kill-counter bitmap at the centre-HUD anchor, `SIG: 150%` below it, live 25% kill-bonus feed; the black integer itself is not discernible (see fix 3) |
| 448 Turret carrier identity | `font1b-448-turret-carrier-1920x1080.jpg` | top-right `Current location:` / `Monmar`, then `Turret of:` in yellow with `Proof Carrier` in red below it |
| 450 Accepted order, 451 Queued order | `font1b-450-451-457-462-tips-1920x1080.jpg`, `crop-450-451-orders.png` | top-left `Current orders:` (yellow promptFont), red `goto Garrison` with the red queued glyph, and the accepted `attack Garrison` line with the yellow check glyph |
| 457 Vote banner | same shot | white `Attack the enemy garrison in Omega?` centred under the chat pane (and the grey `[VOTE] …` system chat line above it) |
| 462 Ripcord countdown | same shot | `Ripcord in: 9` at the native centre-left anchor |
| Tip lines (455/456) | same shot | bottom-centre teal `NEW COMMAND RECEIVED - [Home] to accept` and gold `Tip: press T to select the object in the reticle`; red critical `WARNING: you are taking hull damage` under the chat pane |
| 476, 479-485 Inventory stat rows | `font1b-476-479-485-inventory-1920x1080.jpg`, `crop-476-485-inventory.png` | F4 pane: centred `Adv Scout` header (`smallBoldFont`); `1. EW Pulse Laser  625m / 5 X 10` (range/damage/burst), `7. Lrg Shield 3  4687 / 0 sec` (strength/recharge), `9. Hyper-Jump  540mps / 2` (afterburner top speed/endurance), then the cargo rows. The mount is real (server `ClearLoadout`/`MountPart` → `LOADOUT_CHANGE`). Quantities (`X 10`, `2`) render in `smallFont`; no empty-slot row exists in this loadout, so the red-A warning is not exercised here |
| fix 2 Command summary | `font1b-fix2-command-summary-1920x1080.jpg`, `crop-fix2-command-summary.png` | command view: teal `Ship: Adv Scout`, then Shields/Hull/Energy/Ammo/Fuel/Thrust rows with 52×4 bars and `100%`, `Speed: 0 mps`, `SIG: 150%`, `Kill bonus: 25%`, `2025 cr AP off RADAR Default` — 13 px row pitch, no glyph overlap |
| fix 3 Lives/kills badges | `font1b-fix3-lives-kills-1920x1080.jpg`, `crop-fix3-badges.png` | top-left `3 lives` beside the lives art and `7 kills` beside the skull art. FONT-1c re-opened this shot and measured the glyphs ABOVE the 22/25 px art: FONT-1b had treated the native y-up Point(18/15,20) as the text BOTTOM, so this row's "inside their badges" claim was wrong. FONT-1c fix 3 corrects the text top and re-shoots it (see below). |
| 405, 489, 490, location label | FONT-1 assets | already shown on screen in FONT-1 (target speed, own ship name, empty-slot affordances, `Current location:`) and not re-shot |

Only row 389 could not be fully shown; it is back to `partial` as required. All other rows keep
their previous status.

### 5. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Font1NativeFontTests.cs` now has 9 tests:

- **(a2) per-id spritefont table** — a `font.mdl`-derived expected table (native symbol, cell
  height `m_size.y`, Size, bold) for all 9 identities. For each id it asserts the registry Size/
  bold/cell, the 1× `.spritefont`'s `<FontName>` is the bundled `DejaVuSans.ttf` or
  `DejaVuSans-Bold.ttf`, `<Style>` is `Regular`/`Bold` accordingly, `<Size>` equals the registry
  point size, and the `@2x` file is the same face/style at exactly double the Size.
- **(d) Modern behaviour test** replaces the old source-string assertion: an uninitialised
  `ModernHudRenderer` gets sentinel `SpriteFont` objects and `_scale` through reflection (no
  GraphicsDevice), then the real protected `HudFont` override is invoked for every id — every id
  must return Modern's own small font, only `LgBoldVerdana` its big font, and the scale is
  Modern's `TextScale` (1.734f), never the pane scale passed in.
- **(b) mono-font guard** is now an exact-line whitelist: only the four field/constructor lines
  and the one `HudRenderer.HudFont` fallback line may mention `_small`/`_big` in
  `HudRenderer.cs`/`ChatRenderer.cs`; arbitrary lines containing `_big : _small` are rejected.

`Hud6bOwnStatusTests.Kill_counter_uses_the_native_experience_group_inner_offsets` was updated to
pin the new bitmap-relative number box.

Measured on the final tree, each filter **3/3 runs alone PASS**: `Font1NativeFontTests` 9/9,
`Hud6bOwnStatusTests` 12/12, Hud12b 17/17, `HudConsoleTextTests` 25/25, Chat 140/140,
TargetPane 28/28, `HudInventoryPaneTests` 17/17. One full fast tier
(`tools/run-tests.sh --tier fast`): **PASS — 9704 tests, 270.7 s**
(`~/allegiance-evidence/FONT1B/tiers-20261021T121116.389071Z/summary.json`;
all run logs are in `~/allegiance-evidence/FONT1B/`). A first fast run failed one unrelated concurrent-dictionary
test that then passed 3/3 alone (load flake, `docs/TEST_FLAKES.md`).

### 6. Packaging

`fonts-dejavu-core` was removed from both install lines in `.github/workflows/release.yml`
(test + package jobs) and from `packaging/Dockerfile.smoke-ubuntu`. `libfreetype6` stays: it is
the font rasteriser the content pipeline/MonoGame uses at build/runtime, not a system font. A
repo-wide grep finds no other release/packaging install of DejaVu; the only other reference is
`tools/space-objects/contact_sheet.py`, a dev-only screenshot helper that reads a system DejaVu
path and is not part of the release or the game.

### 7. Report accuracy and gate

The flip list above is corrected (16 `done`, Kill bonus back to `partial`); every gate number in
this section was measured on the final tree. Whole-solution build: **0 warnings, 0 errors**. No
`src/Allegiance.Server` or `src/Allegiance.Core` production file changed (only the server project
is referenced by the proof harness), so no AOT publish and no showcase run were owed. HUD-19
gauge code, HUD-21 target preview, HUD-18 target rows and the classic overlays drawn directly by
`Game1` with `_hudSmall` were not touched.

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

## Review fixes (FONT-1c)

Base: branch `pi/font1c` (master + FONT-1b cherry-picked as `4e73f18f`). Model:
DeepSeek V4.1 Flash. The review's items 1 (medBold 9), 2 (command summary at
`TextScale`), 5 (tests) and 6 (packaging) were confirmed good. The remaining
problems were almost all **native y-up** mistakes, fixed below. Every native
cite is from `~/Development/Allegiance/src/` / `~/Development/Artwork/dialog.mdl`.

### 1. Own-status cluster anchors were y-flipped (fixed first)

`OwnStatusLayout.Placement` is the native `DefaultCenterHudOn` pane point
(`dialog.mdl:2604-2623`) converted with the HUD-14b gauge rule: the 640x480 pane
has a y-UP origin at its BOTTOM-left (`engine/context.cpp:552`), its top-left is
viewport-centre minus (320,240) (`dialog.mdl:5397`), so
**X = consoleX − 320, Y = 240 − consoleY**. The first round wrote
`consoleY − 240`, mirroring the cluster about the viewport centre.

| Element | `dialog.mdl` | Native point | Centre-relative offset (screen y-down) |
|---|---|---|---|
| Kill bonus (`ExperienceImage`) | 2617 | (281,102) | (−39, **+138**) |
| Signature (`SignatureImage`) | 2621 | (281,133) | (−39, **+107**) |
| Detection eye (`RadarDetectPane`) | 2618 | (390,55) | (70, **+185**) |
| Cloak active (`CloakOn`) | 2609 | (120,80) | (−200, **+160**) |
| Autopilot (`cortAutopilotOnImage`) | 2608 | (177,342) | (−143, **−102**) |
| Vector lock (`cortVectorLockOn`) | 2607 | (442,337) | (122, **−97**) |
| Carried flag (`CaptureFlag`) | 2619 | (427,350) | (107, **−110**) |
| Carried artifact (`Artifact`) | 2620 | (159,350) | (−161, **−110**) |

Every one of those elements really is a member of `DefaultCenterHudOn`
(`dialog.mdl:2606-2622`). The cloaked-strength/drained readout is a PORT
extension (no native element, dialog.mdl:570-621 is GlassHUD-only); it stays on
the same x. *(Superseded in the review round below: the cloak anchor is the
art's LOWER-left, so +140 put the text on the art; it is now **+180**, below it.)*

`Hud6bOwnStatusTests` now has a `NativeStatusAnchors` table that carries each
native point **and its `dialog.mdl` line**, derives `(x−320, 240−y)` and checks
1920x1080/2560x1440; a second test pins the four reviewed offsets are not
y-flipped. The HUD-11 `ReserveAnchor` re-check found no own-status element was
ever reserved (the reservation path only covers the console anchors, gauges,
chat, target pane, inventory and minimap), and after the move the cluster clears
all of them: SIG/medal are below the gauge ring, the eye sits right of centre,
cloak left of centre, and none intersects the top-centre chat or top-right
location blocks (see the screenshots below).

### 2. Kill-bonus number now lands on the medal

Native `ExperienceImage` (dialog.mdl:2559-2566) puts `killcounterbmp`'s
LOWER-left at `Point(-23,-65)` and the integer StringImage's box TOP at
`Point(0,5)` (both y-up, same group). With the 128 px decoded bitmap and ONE
group scale **g = 0.72** (the art's draw scale):

- bitmap screen top-left from the group anchor = `(−23, 65−H)·g` = `(−23, −63)·0.72`;
- integer box top-left from the bitmap's top-left = `(23, H−70)·g` = `(23, 58)·0.72`;
- absolute integer top from the group anchor = `(0, −5)·g` (5 native px above it).

`OwnStatusLayout.KillCounterBitmapOffset(g, H)` and
`KillCounterNumberFromBitmap(g, H)` now express exactly those formulas;
`DrawOwnStatus` passes the loaded `tex.Height` (128) and the 0.72 art scale to
both. This replaces the FONT-1b offset that used `Spread` (1.588) for the bitmap
and a restated additive `70` for the number. `Hud6bOwnStatusTests` pins the
native-derived `H−70` value (58 at H=128) and the absolute `−5·g`, so the old
`70` constant is gone.

**Row 389 is back to `done`.** The re-shot real client shows the black `25`
inside the red/orange medal disc (asset `FONT1C_assets/crop-killbonus-medal.png`:
digits at x≈915-937, y≈753-766; medal x≈907-947, y≈752-769), with `SIG: 150%`
above it — the FONT-1b claim that the integer was illegible is obsolete. The
target pane's `TargetPaneLayout.ExperienceNumberTopLeft` (HUD-20) was NOT
touched, and the FONT-1b 4191 citation was removed from this own-status section.

### 3. Lives/kills badge text sits inside the art

Native `LivesOnImage` / `KillsOnImage` translate `StringImage(JustifyCenter, 80,
medBoldVerdana, …)` by `Point(18,20)` / `Point(15,20)` (dialog.mdl:5070-5080,
5107-5120) in y-up from the badge art's lower edge. The y-up origin is the text
**TOP**, so `HudConsoleFormat.BadgeTextTop(artTop, artHeight, scale) =
artTop + artHeight − 20·scale`; `DrawConsoleBadge` uses it for both the glyphs
and the backing `FillRect`, keeping the native 18/15 box offsets and the
`JustifyCenter` centring inside the 80-wide box. The decoded art is only 89x22
(lives) and 88x25 (kills), so the FONT-1b "bottom = artTop+height−20" put the
glyphs above the art (review measured y 87-96 vs art 97-118).

Re-shot and measured: lives art y 97-118, its text ink y 101-110
(`crop-badges.png`); kills art y ~180-194, text ink y 184-193. Both strings are
inside their badges. `HudConsoleTextTests` pins the helper (and the 150-wide
ripcord box); the report's old "both strings sit inside their badges" row is
corrected above to record why FONT-1b's shot did not.

### 4. Order rows use the native 20/40 px offsets

`HudConsoleFormat.OrderQueuedTop(labelTop, scale) = labelTop + 20·scale` and
`OrderAcceptedTop = labelTop + 40·scale`, from the native `Point(0,-20)` /
`Point(0,-40)` (dialog.mdl:1641-1652) — the same values `OrderIconTopLeft`
already used for the arrow/check glyphs. Before, the text used a measured line
height (~13/25 px at 1080p) while the glyphs used 20/40, so the glyph floated
~7 px off its line. Both Classic and Modern call the new signatures, and the
`hud2-proof` pixel assertions were updated to 57/77 (was 59/81).

Shown on screen (`crop-orders.png`): label `Current orders:` ink y 38-46, red
`goto Garrison` row + red `>` glyph ink y 56-68, accepted `attack Garrison` +
yellow check ink y 78-86; the glyph top stays exactly 3 px above its own text
top at scale 1, as native draws it. `Hud15CommandIconTests` and
`Hud11LayoutPerfTests` pin the offsets and the text/glyph alignment.

### 5. Ripcord countdown is the native centred softText string

Native is `StringImage(JustifyCenter, 150, lgBoldVerdana, softText, …)`
(dialog.mdl:5145-5151; `softText = Color(0.586,0.578,0.680)`, dialog.mdl:3517)
in a `SideCenter(-75,15)` pane (dialog.mdl:5429). The port drew it left-aligned
at `cx−75` in `TextMain` with a "No centering" note. Now:
`HudConsoleFormat.RipcordWidth = 150`, both renderers centre the measured glyphs
inside the 150-wide box at `cx−75`, the top stays 15 px above centre, and the
colour is softText. `ModernHudRenderer` also uses the softText colour now.

The HUD-11 reservation was changed to reserve the whole 150-wide box (previous
code reserved only a left-aligned run at `cx−75`). Without that, centring moved
the Modern ripcord glyphs into a projected radar label at 1.5× scale; the
`ModernHudTests` render check caught the overlap, and reserving the box fixes it
(Modern 5/5 ×3 after the change).

Screen check: *(corrected in the review round below — the earlier "x≈884-1038,
labels at y≈579, no overlap" measurement was wrong.)*

### 6. Empty-ammo warning exercised

`tools/font1c-proof` now injects a real empty magazine through the production
wire path (`SingleShipUpdate` with `AmmoFraction = 0`, the same field
`PartWrapper::IsOutOfAmmo` reads, consoledata.cpp:1132-1145) and opens the F4
pane. The re-shot `crop-inv-weapon.png` shows the native red `A` at x≈1698-1708,
y≈568-579, immediately after `EW Pulse Laser` and before the `625m / 5 X 10`
stats. Row 476 stays `done`; the only cosmetic note is that the port's row
name-fit does not reserve the `AmmoX` column, so the `A` overlaps the last glyph
of the name (the `r` of `Laser`).

### 7. Modern command-summary gauge base restored

`DrawCommandGauge` now computes
`float paneScale = UsesModernGauges ? OwnStatusLayout.Spread(_viewportHeight) : TextScale;`
so Modern (which inherits `DrawCommandShipSummary`) gets back its pre-FONT-1b
spread-based 66 px inset / 52x4 bar size, while classic keeps FONT-1b's native
1:1 `TextScale`. Pinned by a new `Hud12bSourceGuardTests` check.

### 8. Row 448 turret/carrier clash noted

The native text position is correct, so it was not moved. The port-only roll
readout (`+000deg`, no native counterpart) sits directly below/over the red
`Proof Carrier` name at the top right (`crop-turret-clash.png`: `Turret of:`
y≈75, `Proof Carrier` y≈86-97, roll text y≈94 with its bar at y≈106). The
clash is recorded in parity row 448 as a port-extension layout issue.

### 9. Stale text removed

- `Game1.Lifecycle.cs` font-loader comment now says medBoldVerdana no longer
  shares promptFont's B8 bake after FONT-1b moved it to Size 9.
- This report's per-pane table now has a separate **Lives/kills badges** row at
  `TextScale` (~12 px), and the own-status row keeps the spread.
- Parity row 407 (`Target kill bonus`) now says `medBoldVerdana (~12px; FONT-1b
  moved the bake to Size 9)`, not ~10.7 px.
- `HUD20_HUD_ART_GAPS.md` got dated 2026-10-21 notes at the medal-anchor note
  (~:211) and the "FONT-1 sizes are unchanged" line (~:391): FONT-1b moved
  medBold to Size 9 (em 12.0, ≈14 px cell, still inside the medal's 89..121
  span) rather than rewriting the original measurement.
- The Hud6b assertion that pinned `70 * 0.72` now derives `(H−70)·g` from the
  native point and the decoded bitmap height.

### Screenshots (real client, classic skin, 1920x1080)

Harness: `tools/font1c-proof` (the font1b-proof pattern: real `Game1` over TCP
against a real local server, production messages for every injected state; the
server ticker is paused while an injected state is on screen). Full-resolution
PNGs and the run log are in `~/allegiance-evidence/FONT1C/`; the committed JPEGs
and crops are in `docs/reports/FONT1C_assets/` (766,160 bytes total after the review-round re-shoot, under the 2 MB
budget). Every shot below was opened and read back.

| What | Asset | Opened/measured |
|---|---|---|
| Own-status cluster (kill bonus medal + number, SIG) | `font1c-ownstatus-killbonus-1920x1080.jpg`, `crop-killbonus-medal.png` | **re-shot in the review round:** medal disc x≈953-970, y≈752-768 (centre ≈961) directly under `SIG: 150%` ink x≈920-1003 (centre ≈961); black `25` ink x≈954-967, y≈759-766 (12 px em). Reticle/viewport centre is (960,540): SIG and medal sit BELOW it. |
| Eye + cloak | `font1c-ownstatus-eye-cloak-1920x1080.jpg`, `crop-eye.png`, `crop-eye-cloak.png` | **re-shot in the review round:** the 92 px (128×0.72) canvases now rise ABOVE their anchors — detection eye glyph x≈1108-1125, y≈783-792 inside the canvas y≈742-834 (anchor 1071,834 = its lower-left); `concloakbmp` glyph + `CLOAK ENGAGED` x≈662-689, y≈745-772 inside the canvas y≈702-794 (anchor 642,794); `CLOAK 65%` ink x≈656-752, y≈830-841, below the cloak art (text anchor +180 → y≈826). |
| Lives/kills badges | `font1c-badges-1920x1080.jpg`, `crop-badges.png`, `crop-badge-lives.png`, `crop-badge-kills.png` | **re-shot in the review round:** livesbmp art x from 40, y 75-96 (lower edge on the native y=97), `3 lives` ink x≈79-118, y≈79-88; hudKillsbmp art y 152-176, `7 kills` ink x≈78-113, y≈159-168. Text inside the art band, art bottoms on the native anchors. |
| Order rows + glyphs | `font1c-450-451-457-462-tips-1920x1080.jpg`, `crop-orders.png` | label ink y 38-46; red `goto Garrison` + red `>` ink y 56-68; accepted `attack Garrison` + yellow check ink y 78-86; glyph and text tops differ by exactly 3 px at scale 1. |
| Vote banner (row 457) | same shot | white `Attack the enemy garrison in Omega?` centred under the chat pane at y≈175, with the grey `[VOTE] …` chat line above it; unchanged by FONT-1c and still matching the native 85+12·NumChatLines top. |
| Ripcord countdown | same shot, `crop-ripcord.png` | **re-shot in the review round:** `Ripcord in: 10` softText ink x≈911-1010, y≈528-540, centred on x≈960.5; ARMOR ink x≈877-899 / SHIELD x≈1019-1045 at y≈540-545 on the same band, 12 / 9 px clear horizontally; the `R` and the final `0` overlap the inner ARMOR/SHIELD gauge arcs (see the review-round notes). |
| Command-view summary | `font1c-command-summary-1920x1080.jpg` | added in the review round (the shot moved here from the retired `font1b-proof`): `Ship: Adv Scout`, Shields…Thrust rows with bars and percentages, `Speed: 0 mps`, `SIG: 150%`, `Kill bonus: 25%`, `0 cr AP off RADAR Default`; centred `Ripcord in: 6` under the garrison. |
| Empty-ammo quantity state | `font1c-476-inventory-empty-ammo-1920x1080.jpg`, `crop-inv-weapon.png` | F4 pane `Adv Scout`; `1. EW Pulse Laser` followed by the red `A` at x≈1698-1708, y≈568-579 and the `625m / 5 X 10` stats; `7. Lrg Shield 3 4687 / 0 sec`, `9. Hyper-Jump 540mps / 2`, cargo rows. |
| Turret/carrier + roll clash | `font1c-448-turret-carrier-1920x1080.jpg`, `crop-turret-clash.png` | `Turret of:` y≈75, red `Proof Carrier` y≈86-97, port-only `+000deg` y≈94 with its slider bar y≈106 crossing the name's lower line. |
| Decoded killcounterbmp | `killcounter-art.png` | the 128x128 art the black integer is drawn on (medal disc rows ~52-72). |

### Tests and gate (measured on the final tree)

- Whole-solution build `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
- 3× alone, every filter the task names: `Font1*` 9/9 ×3, `Hud6b*` 13/13 ×3,
  `Hud11*` 16/16 ×3, `Hud17*` 3/3 ×3, `Hud20*` 11/11 ×3, `HudConsoleText*`
  25/25 ×3, `Hud12b*` 18/18 ×3, `TargetPane*` 28/28 ×3, `ModernHud*` 5/5 ×3.
  The ModernHud filter first failed 3/3 because centring the ripcord exposed a
  real radar-label overlap; reserving the 150-wide box fixed it (the failure was
  NOT waved away as a flake).
- ONE fast tier: the first run (`~/allegiance-evidence/FONT1C/fast-tier-1/`) failed
  two unrelated Live tests (`CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
  and `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`);
  both passed **3/3 alone** and are recorded as load flakes per
  `docs/TEST_FLAKES.md`. The re-run
  (`~/allegiance-evidence/FONT1C/fast-tier-2/`): **PASS — 9786 tests, 275.7 s**, with one
  auto-retried quarantine flake (`RetriedConnectLiveTests`).
- No `src/Allegiance.Server` or `src/Allegiance.Core` production file changed
  (only `docs/parity`, `docs/reports`, `tools/hud2-proof`, the new proof tool,
  `src/Allegiance.ClientCore.Game` and MonoGame tests), so **no AOT publish** and
  no showcase run were owed.
- `python3 scripts/parity-scorecard.py`: **665 rows validated**; hud area
  42.59% (46/108).

### Parity rows touched

- **389 Kill bonus → `done`** (number shown on the medal; test pins the y-up
  group offsets).
- **450 Accepted / 451 Queued order text** stay `done`, now with the native
  20/40 px text rows and the shared glyph alignment measured on screen.
- **448 Turret carrier identity** stays `done` (native text correct) with the
  port-only roll-readout clash recorded.
- **462 Ripcord countdown** stays `done` (centred 150-wide softText box shown;
  review round: the note now records the inner-arc overlap instead of "no overlap").
- **476 Equipment quantity and empty warning** stays `done` (empty-ammo red `A`
  now exercised on screen).
- **459 Lives remaining** stays `partial`: the badge is now drawn correctly
  inside its art, but the port still does not simulate the native lives-limit
  elimination rule (production missions send the unlimited sentinel).
- **407 Target kill bonus** note corrected to ~12px; no other row status changed
  (no row was marked `n/a`).

### Owed / not done

- The port-only roll readout clash in row 448 is recorded, not moved (the brief
  allowed either; moving native text would have been wrong).
- The Modern command-summary gauge restore has a source guard but no new
  screenshot; it restores the pre-FONT-1b Modern look, and the classic summary
  was already shown by FONT-1b.
- No Live/perf-tier run was made beyond the fast tier; per the lane rules those
  run on Claude's mini test runner per merged batch.

## Review fixes (FONT-1c review round, Claude)

All three native 2-D conventions were re-checked against the source before each
change: TranslateImage points are y-up and a bitmap's bounds are
`Rect(Point(0,0), size)` so the point is its **lower-left** (`engine/image.cpp:184-190`,
`engine/paneimage.cpp:91-97`); a StringImage's bounds are `Rect(0,-h,w,0)` so the
point is the text's **upper-left** (`engine/image.cpp:353, 384-393`); GroupImage
paints its list in reverse (`engine/image.cpp:918-921`).

1. **Status icons drew from the wrong corner.** `DrawStatusIcon` put the bitmap's
   top-left on the y-up anchor; it now draws at
   `OwnStatusLayout.StatusIconTopLeft` = `(anchor.X, anchor.Y − H·scale)`, so the
   art's LOWER-left sits on the anchor (eye/radonbmp, cloak, autopilot, vector
   lock, flag, artifact). The port-only cloak-strength text moved from +140 (which
   was now on the art) to **+180**, 20 native px below the art's bottom edge —
   the same 20-unit spacing Modern's `CLOAK` label/strength pair used before
   FONT-1c, so Modern (whose status text hangs from the anchor) does not collide
   either. The `OwnStatusLayout` class/`ScreenPos` comments that said "TOP-LEFT
   corner" were rewritten. Measured: cloak canvas y≈702-794, glyph y≈745-772,
   `CLOAK 65%` ink y≈830-841; eye canvas y≈742-834, glyph y≈783-792.
2. **Kill-bonus group centred and font floor respected.** Native number and SIG are
   both 80-wide JustifyCenter boxes at x=281 (`dialog.mdl:2559-2564, 2595-2602,
   2617, 2621`). The group origin is now `OwnStatusLayout.KillBonusGroupOrigin` =
   anchor.X + 40·(spread − g), so the number box centre is the SIG box centre
   (medal ≈961 vs SIG ≈961, was ≈927 vs ≈961). The number draws at the registry's
   floored scale `killFs` (= 1, em 12 px) instead of `0.72·killFs` (~8.6 px). The
   `dialog.mdl:2556-2566` offsets `(-23, 65−H)·g` / `(23, H−70)·g` are unchanged.
3. Per-pane table: the kill-bonus number now has its own row (~12 px at every
   resolution); SIG/cloak text stay at the spread (medVerdana ~16.9 px at 1080p).
4. **Ripcord measurement corrected.** `Ripcord in: 10` ink x≈911-1010, y≈528-540,
   centred on x≈960.5; ARMOR ink x≈877-899 and SHIELD x≈1019-1045 at y≈540-545 are
   on the same band, 12 / 9 px clear horizontally. The `R` and the last glyph
   overlap the inner ARMOR/SHIELD gauge arcs (arc pixels at x≈908-922 and
   ≈999-1013 on rows 529-541). **Native does not:** native draws the gauge pane
   1:1, and mapping the port's 0.72-scaled arcs back to pane space puts them at
   x≈248-267 / ≈374-394, outside the ≈270-370 run of the 150-wide centred
   lgBoldVerdana string (DejaVu B10 matches its width, error 2 px) whose top is
   15 px above centre (`dialog.mdl:2445-2453, 2516-2524, 2610-2611, 5145-5151,
   5429`). The overlap is a port pane-scale difference (gauges 0.72, ripcord 1:1),
   recorded in parity row 462, not fixed here.
5. **Modern changes, stated explicitly.** Modern's own-status texts call
   `OwnStatusLayout.ScreenPos` (`ModernHudRenderer.cs:176`), so the FONT-1c y-flip
   moved them too (SIG/kill bonus/eye/cloak now below the reticle, autopilot/vector
   lock/flag/artifact above). The Modern ripcord changed from `Accent` to
   softText and is now centred in the 150-wide box (`HudConsoleFormat.RipcordTextLeft`,
   shared with classic).
6. Row 476 / §6: the red `A` **overlaps the last glyph** of the name.
7. **Real ripcord x assertions.** `Hud15CommandIconTests.Ripcord_text_is_centred_on_the_viewport_centre_line`
   measures `Ripcord in: 9` with a real `SpriteFont` (no device) and asserts the
   centred left edge (`w/2 − 71.5·s`) and centre at 1920/1 and 2560/1.5; the
   tautological constant re-add was dropped. `hud2-proof` now checks the ripcord
   box is centred on x=960 (left = 960 − width/2) and not at the old 885 edge
   (`RequireCentredBox`); `HUD2_RENDER_PASS 288`.
8. `tools/font1b-proof` deleted (it was never in `AllegianceCore.slnx`); its only
   unique shot, the command-view summary, moved into `tools/font1c-proof`
   (`font1c-command-summary-1920x1080.jpg`).
9. Parity row 448 names `DrawRoll` (the method), not `DrawRollIndicator`.
10. **Lives/kills badges were 22/25 px low and 5 px right.** Native
    `(LivesImage|KillsImage, SideTopLeft, Point(45,-97|-177), Point(40,-97|-177))`
    (`dialog.mdl:5441-5445`): `GetSidePoint(ATTopLeft) = (xmin, ymax)`
    (`trekctrls.cpp:1016-1028`) plus the animated point, and a shown overlay goes to
    its END point (`Show(true)` → `MoveEnd()`, `console.cpp:3089-3096`) = x 40. The
    untranslated `ImportImage` member's lower-left is that point, so the art spans
    y `[97−H, 97]` / `[177−H, 177]`. `DrawConsoleBadge` now takes the anchor and
    uses `HudConsoleFormat.BadgeLeft` (40) and `BadgeArtTop(anchor, H)`; the
    item-3 text relation `BadgeTextTop(artTop, H) = anchor − 20` is unchanged.
    Measured: lives art y 75-96, `3 lives` ink y≈79-88; kills art y 152-176,
    `7 kills` ink y≈159-168. **Native-faithful side effect:** the lives badge band
    (77-97) now shares rows with the accepted-order line (`LastOrderImage`
    `SideTopLeft(50,-37)`, `Point(7,-40)`, `dialog.mdl:1646-1648, 5450` → text top
    77) when both show — visible in `crop-orders.png`; native has the same overlap
    (both are `ofInFlight`/`ofAlways` at those points), so it was not moved.

### Tests added (and which fail on the pre-fix renderer)

- `Hud6bOwnStatusTests.Status_icon_draw_rect_bottom_edge_sits_on_the_native_anchor`
  (6 icons × 3 viewports: draw-rect bottom = anchor) and
  `DrawStatusIcon_draws_the_bitmap_from_its_lower_left_anchor` (source guard —
  **fails on the pre-fix `HudRenderer.cs`**).
- `Hud6bOwnStatusTests.Cloak_strength_text_hangs_below_the_cloak_art` (fails on the
  pre-fix +140 placement).
- `Hud6bOwnStatusTests.Kill_bonus_group_shares_the_signature_box_centre_and_the_font_floor`
  (**fails on the pre-fix `HudRenderer.cs`**).
- `HudConsoleTextTests.Badge_art_lower_left_sits_on_the_native_side_top_left_end_point`
  (**fails on the pre-fix `HudRenderer.cs`**); the older badge-text assertion now
  uses the corrected art tops.
- `Hud15CommandIconTests.Ripcord_text_is_centred_on_the_viewport_centre_line` (2 cases).

### Review-round gate

- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
- Targeted filter (Font1|Hud6b|HudConsoleText|Hud15|Hud11|Hud12b|ModernHud|Hud17|Hud20|TargetPane|OwnStatus):
  **149/149 ×3**.
- `tools/hud2-proof --test`: `HUD2_RENDER_PASS 288` (incl. the new centred-ripcord check).
- ONE fast tier: 9832 tests, 277.9 s; one failure,
  `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` — a
  listed load flake in `docs/TEST_FLAKES.md` (socket-cleanup count race), **3/3 pass alone**.
  No other failure.
- `python3 scripts/parity-scorecard.py`: **665 rows validated**; hud 42.59% (46/108), no status change.
- Re-shot with `tools/font1c-proof`; full-resolution PNGs of this round were kept in the
  fixup scratch directory (`~/allegiance-scratch/fix-font1c-scratch/capture/`), the
  committed JPEG/crops replace the stale FONT-1c ones (766,160 bytes total).
- Parity rows touched: 389 Kill bonus (centring + font floor, new measurements), 448 (`DrawRoll`),
  459 Lives remaining and 460 Mission kills (badge art lower-left on the native (40,−97/−177)
  end point; native-faithful order-row overlap noted), 462 Ripcord countdown (inner-arc
  overlap instead of "no overlap"), 476 (A overlaps the name's last glyph). No status changed.
- Not fixed, noted: `HudElements.cs` still carries the pre-FONT-1c (y-flipped) own-status
  `HudPlacement` metadata (e.g. Kill bonus `-138`); the renderers use `OwnStatusLayout`, so
  nothing on screen depends on it.
