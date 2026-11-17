# P1041 — Jet HUD skin story 7/9: amber variant (P972 slice 3E)

Worktree `hermes/p1041` of `AllegianceCore`, base commit `775c4710` (the P1040c merge). No commit, no push, no stash,
no remote host, no Docker (not installed here). Scratch in `.scratch/p1041/` (deleted at the end); raw captures outside
the repo in `~/allegiance-evidence/P1041/`; the report's assets are the PNGs under
`docs/reports/JET_AMBER_P1041_assets/` (1.71 MB, ≤ 2 MB). Scratch `.scratch/p1041/` and `.scratch/p940/`
were deleted at the end; the six lesson-window logs are preserved in `~/allegiance-evidence/P1041/windows/`.

**Status: DONE for items 1–5 and the gates.** Two deviations are stated plainly: the Options "decoded-pixel"
comparison is a decode-level pixel *measurement* (the translucent panel bleeds 4 % of a different live scene) plus an
exact row-text comparison (§7), and the training gate's 3× repeats exclude the 21-min-bounded `P912TrainingWindowTests`
after the full script's run 1 showed 4 pass / 2 ledgered prompt-sequence failures (§9.2).

## Item status

| # | Item | Status |
|---|---|---|
| 0 | Report first, kept current | **DONE** (this file, written before the first source edit and updated as work landed) |
| 1 | `jet.amber.skin.json`, full amber palette, contrast table | **DONE** (§2, §3) |
| 2 | No green leftovers: colour inventory, stated fixed colour, off-palette test both palettes | **DONE** (§4, §5; 0 off-palette colours in 141 scenes, both palettes) |
| 3 | Persisted `JetPalette`, Options selector, atomic switch + toast, training restore | **DONE** (§6, §7) |
| 4 | Identical geometry green vs amber, `--jet` captures both, 0 diffs, audits 0 | **DONE** (§5: 0 geometry diffs in 141 scenes; every audit class 0 in amber) |
| 5 | Tag text per palette (`JET / AMBER - WIP` / `JET / GREEN - WIP`) | **DONE** (§3.3) |
| A | Baseline-first Classic/Modern zero-delta (1,848) | **DONE** (§8) |
| B | Audit counts per palette (all classes 0) | **DONE** (§5) |
| C | Gate: build 0/0 + tools, new tests 3× alone, one fast tier, 0 B/frame both palettes | **DONE** (§9) |
| D | Evidence captures + 2× crops + every image opened; Options screen capture | **DONE** (§6) |

## 1. What changed

Every Jet value still comes from the existing `HudModel`/native-derived data: the variant swaps **colour only**
(P972 §4.1 "the amber alternative swaps the entire palette"), and both variants share the renderer, layout ids and
every measured box (P972 §9 3E "identical measured boxes").

| File | Lines | Change |
|---|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/Skins/jet.amber.skin.json` | **new** | The amber variant: `"Id": "jet"`, `"Variant": "amber"`, full five-role palette (P972 §4.1 proposal). |
| `.../Rendering/Skins/HudSkinDefinition.cs` | 15-29 | Optional `Variant` token on the definition (null = an ordinary skin). |
| `.../Rendering/Skins/HudSkinLoader.cs` | 44-53 | A variant must be a normalized token AND carry a full palette; anything else rejects the definition in full. |
| `.../Rendering/Skins/HudSkinCatalog.cs` | 9-12, 22-29, 50-75 | Variants are loaded into a separate map (`TryGetVariant`) and are **never** in `Installed`, so the F-key cycle cannot select a fourth skin. |
| `.../Rendering/Skins/JetPaletteIds.cs` | **new** | `green`/`amber` ids, normalization (unknown/blank → green, one diagnostic), `Next`, `IsKnown`. |
| `.../Rendering/Skins/JetPaletteSwitcher.cs` | **new** | The two-palette registry (preloaded green + amber), queued request, boundary apply; `JetPaletteRenderer` (the stable `IHudSkinRenderer` in the skin switcher's Jet slot); `HudSkinNotice.Format` (`SKIN: Jet / amber`); `JetPaletteRow.Visible/Label`. |
| `.../Rendering/HudSettings.cs` | 36-58, 176-179 | Persisted `JetPalette` string (separate from `Skin`), request delegate + `ActivateJetPalette`, normalization in `Normalize` (unknown → green with one diagnostic). |
| `.../Rendering/Jet/JetHudRenderer.cs` | 35-40, 158-161, 296, 318-325, 1872-1875 | Reads the variant from the definition; `Palette`, `TagLabel`; the WIP tag draws the active palette's own name. |
| `.../Rendering/Jet/JetHudRenderer.Console.cs` | 811-812 | The controls hint's tag obstacle is measured from the active palette's tag. |
| `src/Allegiance.ClientCore.Game/Game1.GpuBudget.cs` | 31-33, 51-75 | Both variants preloaded at `LoadContent`; the Jet slot is the palette adapter. |
| " | 99-121 | `CommitHudSkinChange` applies the skin AND palette at the boundary, saves once, and shows `SKIN: Jet / <palette>`; `ActiveJetPalette`, `JetPaletteRowVisible`. |
| " | 140-155 | Panel row count/`GraphicsRowFor` (the variant row sits directly under the skin row); semantics of every later row preserved. |
| " | 248-280, 340-364 | Left/Right on the variant row queues the palette; the HUD-size slider follows the shifted row; rows hidden when the gate is off. |
| " | 385-392, 430-436 | Row list: `Jet palette: amber|green` inserted at index 7 only while Jet is active with `hud.jetSkin` on; proof-only row log. |
| `src/Allegiance.ClientCore.Game/Game1.MouseOptions.cs` | 165, 182, 194 | The P948 proof's mouse rows use the semantic→panel row mapping. |
| `src/Allegiance.ClientCore.Game/Game1.TrainingIntegration.cs` | 10-13, 29, 62-66 | The pre-lesson snapshot carries `JetPalette`; the restore queues it before the boundary commit. |
| `src/Allegiance.ClientCore.Game/Game1.P1041Proof.cs` | **new** | `ALLEGIANCE_P1041_PROOF=1`: opens the panel on the variant row, captures green → amber → green from `EndDraw` (after the panel is drawn). Presentation only. |
| `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` | 402-404 | Calls the proof hook beside the P948 one. |
| `tools/hud2-proof/Program.cs` | 194-200, 250-290 | Loads the amber variant beside the green one (preloaded). |
| " | 570-690 | Each scene is drawn by BOTH palettes: amber PNG, amber recorder, per-palette off-palette counts, green-vs-amber geometry signature, tag runs. |
| " | 946-976, 992-1050, 2560-2650, 2810-2840 | New counts (`HUD2_JET_PALETTES`, `HUD2_JET_GEOMETRY`, `HUD2_JET_AMBER_AUDIT`), the colour-allowed predicate and the geometry signature. |
| `tests/.../Skins/JetAmberPaletteTests.cs` | **new** | 24 tests (§9). |
| `tools/p1041-options-proof/` | **new** | Real-backbuffer Options-screen capture (local server + real client, offscreen SDL), reusing the P1041 hook. |

`git status` also lists the untracked `P1041_BRIEF.md`; **it is deleted before finishing** (the brief must not be in the
change set). `docs/parity/*.csv` is untouched (no native behavioural rule changed — see §10).

## 2. The amber palette (P972 §4.1, slice 3E)

`jet.skin.json` (green) is byte-unchanged. The amber variant, `jet.amber.skin.json`:

| Role | Green (`jet.skin.json`) | Amber (`jet.amber.skin.json`) |
|---|---|---|
| Primary | `#79FFC0` | `#FFD280` (P972's proposed primary, unchanged) |
| Dim | `#3DA87B` | `#B98A45` (proposed) |
| Hot | `#C9FFE3` | `#FFF0C2` (proposed) |
| Plate | `#06110C` (green-neutral) | `#120F08` (amber-neutral near-black) |
| Keyline | `#080B09` (near-black green-neutral) | `#0B0908` (near-black amber-neutral) |

The plate/keyline are the only values not literally in P972's proposal — it asks for "near-black green/amber-neutral"
plates (P972 §4.1) and gives no hex, so the amber pair keeps the green pair's luminance (plate L 0.0047 → 0.0049,
keyline 0.0031 → 0.0028) with the hue rotated into the amber family. The three ink roles are exactly the proposal —
no adjustment was needed (the contrast table below shows every role keeps the green variant's legibility class).

Variant identity: `"Id": "jet"` (it shares Jet's renderer/layout id) plus `"Variant": "amber"`. The catalog keeps it out
of `Installed`, so `HudSkinCatalogTests` still sees exactly 3 skins, and `HudSkinSwitcher.Cycle` can never reach it.

## 3. Contrast (WCAG relative luminance, sRGB; measured not eyeballed)

Backdrops are the port's own proof backdrops (`hud2-proof`): dark `rgb(40,53,76)`, bright `rgb(225,215,205)`.

| Pair | Green | Amber |
|---|---|---|
| Primary vs Plate | 15.44 : 1 | 13.47 : 1 |
| Dim vs Plate | 6.48 : 1 | 6.18 : 1 |
| Hot vs Plate | 17.29 : 1 | 16.85 : 1 |
| Primary vs Keyline | 15.89 : 1 | 13.99 : 1 |
| Dim vs Keyline | 6.67 : 1 | 6.42 : 1 |
| Hot vs Keyline | 17.79 : 1 | 17.50 : 1 |
| Primary vs dark | 9.91 : 1 | 8.68 : 1 |
| Dim vs dark | 4.16 : 1 | 3.98 : 1 |
| Hot vs dark | 11.09 : 1 | 10.86 : 1 |
| Primary vs bright | 1.14 : 1 | **1.00 : 1** |
| Dim vs bright | 2.09 : 1 | 2.18 : 1 |
| Hot vs bright | 1.28 : 1 | 1.25 : 1 |
| Plate vs dark / bright | 1.56 / 13.54 | 1.55 / 13.49 |

Reading of the table (the measured reason the proposal is kept as written): every *text* glyph carries the near-black
keyline first (P972 §4.1), and numerics sit on a Plate, so the operative pairs are ink-vs-keyline (≥ 6.42 amber) and
ink-vs-plate (≥ 6.18 amber) — both variants are in the same legibility class. The direct ink-vs-bright-backdrop ratio is
low in **both** variants (green primary 1.14, amber primary 1.00); that is the pre-existing P1036 property of the skin
(a bright backdrop is crossed by the keyline, not by bare ink), not a regression: the only un-keylined ink is the
segmented bar fills, and those use Dim (2.18) / Hot (1.25) / a Dim shade, with the amber variant marginally better than
green on the bright backdrop for Dim (2.18 vs 2.09). The one place the amber variant is measurably weaker is primary
ink over the bright backdrop (1.00 vs 1.14) — but primary ink is always keyline-backed or on a plate in the renderer, so
there is no legible-reading regression; no value was adjusted because none is needed and P972 says adjust only with a
measured contrast reason.

### 3.3 Tag text (item 5)

`JetHudRenderer.TagText` stays `JET / GREEN - WIP`; `TagTextAmber` is `JET / AMBER - WIP` (both 17 characters), and
`TagLabel` picks by variant. The tag draws through `_painter.Dim`, i.e. the variant's own dim ink. Green's tag text is
unchanged, which is why the green pixel gate is exactly 0 (§8).

## 4. Colour inventory (item 2) — every colour the Jet renderer submits

Method: the proof's recording surface now keeps **every submitted colour** (`Line`/`Fill`/`Text`) and the palette audit
classifies each one. `HUD2_JET_PALETTES` over all 141 scenes: **green off-palette 0, amber off-palette 0** (no scene in
the detail list means none offending). `JetAmberPaletteTests.Every_jet_scene_uses_only_the_active_palette_in_both_variants`
asserts the same on its own 13-scene headless set, both palettes.

| Colour source (where the renderer gets it) | Role in the inventory |
|---|---|
| `JetVectorPainter.Primary` — gun cross, FPM, ladder, tapes, designator, CMD frames, radar contacts | palette role |
| `JetVectorPainter.Dim` — captions, dim ladder rungs, out-of-range lead, secondary rows, tag | palette role |
| `JetVectorPainter.Hot` — alerts, lock-full, threat, selected recipient/verb, flash | palette role |
| `JetVectorPainter.Plate` — every readout/pane/cell backing, `Plate * alpha` with alpha ∈ {0.62, 0.72, 0.80, 0.82, 0.85, 0.97} | palette role + its alpha backing steps |
| `JetVectorPainter.Keyline` — every text's eight-offset under-stroke, every line/polyline/arc/bracket under-stroke, plate frames | palette role |
| `Role * f` steps: radar ring 0.55, leader 0.8, label 0.75, unlit bar 0.35/0.3, tip row 0.75, wrapped tail 0.9, radar range fade 0.25…1 (`JetHudRenderer.Radar.cs:614-615`) | brightness steps of a palette role (documented in `PaletteAllows`) |
| `Color.Lerp(Dim, Hot, t)` — the shared ballistic-lead ramp mapped to the palette (`JetHudRenderer.cs:1671-1674`) | the Dim→Hot segment |
| `console.SideColor` — the shared console's **native side-identity colour** (`WinTrek/consoledata.cpp:99-107` `pside->GetColor()`; `Artwork/dialog.mdl:26` `mySideColor`) used for ordinary tips and its row-brightness steps (`JetHudRenderer.Console.cs:473,490,499`) | **intentional fixed colour** (P972 §4.1 shape/text redundancy: side identity is native data, not skin colour) |
| Host overlay text (`DrawOverlayText`, the `SKIN:` toast) | outside the Jet scene composition; the host supplies it (white, `Game1.Rendering.cs:3605`), reported for completeness |

No literal `Color.X`, no hex literal and no un-keylined foreign colour remains in `Rendering/Jet/`: the only
`new Color(...)` in the folder are the side-colour conversion (`JetHudRenderer.Console.cs:473`) and the palette parse
(`JetHudRenderer.cs:1893-1894`). The CMD panel, radar, plates, scanlines/glow all submit palette roles (scanlines/glow
are the keyline under-strokes and the plate backing — never a second hue).

## 5. Identical geometry + audit counts per palette (item 4, item B)

`tools/hud2-proof --jet` now draws **each of the 141 scenes twice** — green, then amber — through two recording
surfaces, and compares a colour-free geometry signature (text runs and their measured boxes, every stroke and width,
every fill/plate rectangle, icon glyphs, lock cues and cue readouts, radar contact glyphs, band rings, leaders, radar
plates). The tag run is excluded because item 5 makes its label — and only its label — differ.

```
HUD2_JET_PALETTES green off-palette 0, amber off-palette 0 over 141 scenes
HUD2_JET_GEOMETRY green-vs-amber diffs 0 over 141 scenes (the WIP tag's own text/box excluded: item 5's label)
HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0,
  cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0, text-overlap-same 0 over 141 scenes
HUD2_JET_RADAR_STROKES text-over-glyph 0, stroke-through-text 0, backing plates 1165 over 141 scenes
HUD2_JET_ELEMENTS element-missing 0 over 141 scenes
HUD2_JET_CONSOLE hint-blocked 0, cmd-cell leaks 0 over 141 scenes
HUD2_JET_CALLOUT callout-crossed 0 over 141 scenes
HUD2_JET_AMBER_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0,
  cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0, text-overlap-same 0, text-over-glyph 0,
  stroke-through-text 0, radar plates 1165, callout-crossed 0 over 141 scenes
```

| Audit class | Green (141 scenes) | Amber (141 scenes) |
|---|---|---|
| stroke-vs-text | 0 | 0 |
| text-outside-plate | 0 | 0 |
| text-off-screen | 0 | 0 |
| icon-too-small | 0 | 0 |
| text-in-plate | 0 | 0 |
| cue-vs-readout | 0 | 0 |
| legible-behind-pane | 0 | 0 |
| TextOverlapCross | 0 | 0 |
| TextOverlapSame | 0 | 0 |
| radar TextOverGlyph | 0 | 0 |
| radar StrokeThroughText | 0 | 0 |
| element-missing | 0 | 0 |
| hint-blocked | 0 | 0 |
| cmd-cell leaks | 0 | 0 |
| callout-crossed | 0 | 0 |
| **green-vs-amber geometry diffs** | - | **0** |
| **off-palette colours** | **0** | **0** |

## 6. Evidence — every image opened and described

Raw sets (outside the repo): `~/allegiance-evidence/P1041/jet-final/` (282 PNGs = 141 green + 141 amber),
`~/allegiance-evidence/P1041/jet-before/` (141 green), the Options sets `options-jet-amber/`, `options-classic-off/`,
`options-modern-off/`, `...-before/`; logs beside them. Repo assets (23 files, 1.75 MB):
`docs/reports/JET_AMBER_P1041_assets/`.

What I actually saw (I opened every asset below; the vision model's small-font readings were checked against the
measured boxes and, for the panel, against the logged row list — where it disagreed with a measurement I say so):

| Asset | What it shows |
|---|---|
| `jet-level-1920x1080-dark-amber.png` / `-bright-amber` | Flight frame: amber pitch ladder with numerals, amber `SPD / MPS` and `THR` tapes with boxed numerics, amber flight-path marker circle + wings, amber stores block and bars, all keyline-backed; the amber plate blocks the background where a readout sits. |
| `jet-level-800x480-dark-amber.png` / `-bright-amber` | The same at 800×480: fewer ladder rungs, short edge tapes, the amber ink still dominant over the plate (no near-black readout). |
| `jet-level-1920x1080-dark.png` / `jet-level-800x480-bright.png` | The GREEN comparison frames (before the change, byte-identical to the after run): same boxes, green ink. |
| `jet-locked-1920x1080-dark-amber.png` / `jet-locked-800x480-bright-amber` | Amber target data: the vision read `LOCK 62%` and the enemy-fighter pane; the thin corner designator and cue arc are hard for the model to see at 1080p, so their presence is taken from the recorded geometry (`jet-capture.json` `lockFit`, the cue/readout boxes, stroke list) rather than from the model's description — the readout sits on an amber-neutral plate and the lead marker is amber. |
| `jet-radar-dense-1920x1080-dark-amber.png`, `jet-radar-dense-800x480-dark-amber.png` | Amber Jet radar scope with the dense contact set: square tactical scope, contact glyphs and label blocks with leader strokes; nothing strikes a reading. |
| `jet-radar-dense-3840x2160-dark-amber.png` | The same dense scene at 4K: the same vector paths at density, no inflated centre ladder. |
| `jet-caution-1920x1080-bright-amber.png` | Master-caution stack + critical tip + vote banner over the bright backdrop, amber ink with the near-black keyline carrying legibility. |
| `jet-tips-800x480-bright-amber.png` | Tip stack at 800×480 on the bright backdrop; the tips are in the **native side colour** (cyan family), the rest amber — the fixed-colour rule in §4. |
| `jet-cmd-1920x1080-dark-amber.png`, `jet-cmd-800x480-bright-amber.png` | The Jet CMD console: amber frames/cells, `WING`/verb/recipient cells, bottom compact rows. |
| `jet-pod-800x480-dark-amber.png` | Lifepod frame: amber `LIFEPOD OXY` block and pod tapes. |
| `jet-training-1920x1080-dark-amber.png` | Training callout over the amber ladder (the ladder yields to the callout, 0 callout-crossed). |
| `crop-radar-800-2x.png` | 2× nearest-neighbour crop of the 800×480 dense radar (scope box measured at 533.3,174 144×144 in `jet-capture.json`): square scope outline, contact marks, amber label plates and leader strokes; no stroke crosses a label's ink. |
| `crop-console-800-2x.png` | 2× nearest-neighbour crop of the 800×480 console/CMD band (panel measured at 265,348 274×92). Read at the crop: `WPN GROUP [1:Plasma] BST ON CMD`, `CARGO 1/5 Ammo pack x3`, the CMD cells `ME SECTOR TEAM ALL WING DELTA` with the verbs `ATK CAP DEF PIK GO REP JOIN MINE BLD`, and the tag `JET / AMBER - WIP` — the item-5 label and the compact forms, all amber, inside their bands. |
| `options-jet-amber.png` | The real in-game panel (800×480, offscreen SDL backbuffer) with the skin row reading **`HUD style: Jet`** and the next row **`> Jet palette: amber`** highlighted yellow (verified at 2×: rows `RCAS sharpness: 50 %` … `> Jet palette: amber`). |
| `options-jet-green.png` | The same panel after Left: **`Jet palette: green`**, and the HUD ink behind the panel is green (the applied palette). The toast itself is behind the panel's opaque backing in this frame; the toast text comes from the boundary log (§7). |
| `options-classic-off.png`, `options-modern-off.png` | The panel with `hud.jetSkin` off: 24 rows, no variant row (see §7). |

**No green pixel is left in any amber asset**: a pixel classifier (exact match ±4 to the three green ink roles) reports
**0 green-palette pixels** in all 16 amber Jet/options frames and the two derived 2× crops, and **0 amber-palette
pixels** in the two green comparison frames; the Classic/Modern off panels contain neither palette's ink (the panel is
grey/white chrome). The
only non-amber ink in the amber set is the native side colour (cyan family, 1,437 px in `jet-tips-800x480-bright-amber`,
all shades of `rgb(18,242,214)`), which §4 states as the intentional fixed colour — the tool's palette audit and the
pixel classifier agree.

Where vision was unreliable: on 800×480 small fonts it mis-read row prefixes and once invented a game title; the panel
readings above were confirmed against the logged row list and the measured boxes, and the radar's square/round state
against `jet-capture.json` (rings `round=false` for `jet-radar-dense`), not against the model's description.

## 7. Options screen, persistence and the atomic switch (item 3)

- **Persisted variant:** `HudSettings.JetPalette` (`"JetPalette": "green"|"amber"` in `hud.json`), separate from
  `Skin`. Missing/blank/unknown → `green` with one diagnostic (`JetPaletteIds.LogFallback`). `HudSkinMigrationTests`'
  unknown-skin rule is untouched: unknown `Skin` → Modern, and the stored palette is **kept** (tested).
- **Selector:** the Ctrl+F6 Graphics/HUD panel gains `Jet palette: amber|green` directly under `HUD style:` (row 7),
  **only while Jet is the active skin AND `hud.jetSkin` is on**. With the gate off the row is **HIDDEN** (not a disabled
  row) and the stored value is kept; on Classic/Modern the panel is the same 24 rows as before. Left/Right on the row
  toggles the variant, exactly the keyboard behaviour of the neighbouring skin row (no row has a mouse hit rectangle in
  this panel; the HUD-size slider is the only mouse element and it follows its shifted row — `HudSizeSliderRect`).
- **Atomic switch:** the row (or any `HudSettings.JetPalette = …`) queues a request; `CommitHudSkinChange` (the same
  update/draw boundary as the skin switch, `Game1.FlightInput.cs:260`) calls `JetPaletteSwitcher.ApplyPending`, which
  swaps **one preloaded reference**, then saves and shows the toast `SKIN: Jet / amber` / `SKIN: Jet / green`
  (`HudSkinNotice.Format`; proven in the run log: `after Right (applied): palette 'green', toast 'SKIN: Jet / green'`,
  `after Left (applied): palette 'amber', toast 'SKIN: Jet / amber'`). Both variants were built at `LoadContent`
  (`Game1.GpuBudget.cs:51-75`), so a switch parses, loads and allocates nothing.
- **No reset:** a palette switch changes no `HudLevel`, radar LOD/mode, chat, target, selected part, pane state, training
  state or sound loop — the palette registry only publishes a renderer reference; the settings fields it touches are
  `JetPalette` alone. The F-key cycle walks `HudSkinSwitcher`'s installed skins and never calls the palette registry
  (tested: amber stays amber across a full Jet → Classic → Modern → Jet cycle).
- **Training restore:** `TrainingPreferences` now carries `JetPalette` (`Game1.TrainingIntegration.cs:12,29`) and
  `ReleaseTraining` queues it before the boundary commit (`:62-66`). Two unit tests: a lesson started under amber
  restores amber even after switching the variant *and* the skin inside the lesson; a lesson started green restores
  green. The persisted `hud.json` value is asserted too.
- **Classic/Modern options unchanged with the gate off:** the panel's row list is logged (proof env only) and compared
  before vs after the change — **identical byte-for-byte text for both styles** (`[hud.panel] …` 24 rows,
  `~/allegiance-evidence/P1041/panel-classic-{before,after}.txt`, `panel-modern-…`). A decoded-pixel comparison of the
  captures was also made: the panel backing is `new Color(5,15,22,245)`, i.e. **96 % opaque**, so a live scene shows
  through it at 4 %; the two processes had different star/ship frames, and 18,579 (Classic) / 15,821 (Modern) of the
  panel-region pixels differ at byte level. That is the panel's alpha bleed, not a row/geometry change: the row text is
  identical (above), the slider rect's formula reduces to the old literal
  `78+7·46+30 = 430` (i.e. `(360,430,260,8)`) when the row is absent (`GraphicsRowFor(7) == 7`), and the panel
  height/count are the old values (24 rows, `GraphicsRowCount` = 24). **This is the one deviation from the letter of the brief** (a pixel-identical compare of a
  translucent live panel across two processes), stated here plainly.

## 8. Zero-delta baseline and the green Jet gate

- **Baseline first:** captured from the unmodified binary at `775c4710` before the first edit
  (`hud2-proof --capture ~/allegiance-evidence/P1041/baseline`, 6.6 min, manifest commit `775c4710e7ff+dirty` — the
  dirty flag counts only the untracked brief/report). After the change the same tool compared against it:
  **`HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt
  golden(s)`** (924 Classic + 924 Modern; the 84 are the pre-existing low-resource badge inside its recorded animation
  mask).
- **Green Jet unchanged:** `--jet` was captured before the edit (`jet-before`, old binary) and after (`jet-final`).
  All **141 green PNGs are byte-identical** (SHA-256) — 0 pixel diffs, so item 5 changed nothing in green (its tag text
  was already `JET / GREEN - WIP`). The amber set is new (`-amber.png`).
- **0 B/frame in both palettes:** the existing Jet allocation tests cover green; the new
  `Amber_draw_allocates_zero_bytes_per_frame` draws the amber renderer through a silent surface for 20,000 frames after
  a 2,000-frame warm-up and asserts **0 bytes** (3/3 alone). Both renderers are preloaded, so no switch allocates.

## 9. Gates and tests

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Tools build | `tools/build-tools.sh` | **all 142 tool projects built** (exit 0) |
| New tests | `dotnet test --filter FullyQualifiedName~JetAmberPaletteTests` | **24 passed / 0 failed / 0 skipped ×3 alone** |
| Skin suite | `--filter FullyQualifiedName~Skins` (422 cases) | **421 passed / 1 failed** in the parallel sweep: `JetRadarTests.The_radar_draws_every_contact_kind_and_overlay_and_allocates_nothing` (an allocation-count test) — **pass ×3 alone** (75-76 ms). Ledgered in `docs/TEST_FLAKES.md` (allocation-measurement-under-load family); no radar path changed. |
| Fast tier | `tools/run-tests.sh --tier fast` | **15,161 cases / 548.7 s**, infrastructure PASS; 3 ledgered load/allocation cases fail once and pass 3/3 alone; 5 timing-only exceedances (§9.1) |
| Training gate | `tools/run-training-tests.sh --no-build --repeat 3` (artwork) + the 3× training filter with the 21-min-bounded `P912TrainingWindowTests` excluded | **ClientCore 126/126 ×3; MonoGame 256 passed / 1 pre-existing skip ×3**; the full-script run 1's six real-window children: 4 PASS, 2 ledgered prompt-sequence failures (§9.2) |
| Jet audit / palette / geometry | `hud2-proof --jet` | §5 (all 0) |

The proof tool produces 282 PNGs in one run (green + amber per scene) and exits 0 only when every green/amber
audit class, the off-palette counts and the geometry diffs are 0.

`docs/TEST_FLAKES.md` gains a P1041 gate-observations section for the fast-tier and skin-suite observations above
(the allocation/timing families are already led; the section states which cases pass 3/3 alone).

### 9.1 Fast tier

`tools/run-tests.sh --tier fast` on the final tree: **15,161 cases in 548.7 s**, infrastructure checks PASS, 5
timing-only exceedances (all passed, 14.9–15.8 s: the ledgered loadout/gunner/nanite/asteroid/civ scan families).
Three cases failed under the sweep and each **passes 3/3 alone** on the changed tree:

| Case | Sweep failure | Alone | Diagnosis |
|---|---|---|---|
| `Skins.JetConsoleTests.The_console_composition_allocates_zero_bytes_per_frame` | 32 B over 20,000 frames | **pass ×3** (0.75 s) | Allocation-measurement-under-load family (already ledgered); the composition is unchanged by this task except that the palette's tag string is selected without allocating (the amber allocation test is 0 B alone). |
| `Allegiance.ClientCore.Tests.MissileTrackingLiveTests.FiredMissile_AppearsMovesAndDisappears_InClientWorld_OverTheWire` | live missile over the wire | **pass ×3** | Ledgered live-missile family; no wire/sim path touched. |
| `Allegiance.Core.Tests.P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5)` | `5.0000000000000009` against the inclusive 5 s bound | **pass ×3** (one row of one run failed the same one-ULP value — the ledgered floating upper-bound family) | Ledgered one-ULP timer family; timer/bomb code untouched. |

The earlier sweep (on the pre-rename binary) additionally failed the three source-inspection panel tests
(`P948MouseOptionsTests`, `P836HudLevelTests`, `Hud19CentreHudTests`) because they assert the literal
`GraphicsRowCount = 24`; the constant keeps that name (the live count is the `PanelRowCount` property), so all three
pass now. Raw log: `~/allegiance-evidence/P1041/fast-tier-final.log`; per-project logs and the sweep's `summary.json`
copied to `~/allegiance-evidence/P1041/fast-tier/`.

### 9.2 Training gate

The change touches a training-affecting file outside `Rendering/Jet/` (`Game1.TrainingIntegration.cs`: the palette in
the pre-lesson snapshot and its restore), so the training gate was run with artwork:

- **Full script as written, repeat 1:** `tools/run-training-tests.sh --no-build --repeat 3` runs the whole training
  filter including the 20 **`P912TrainingWindowTests`** real-window lesson proofs, each spawning a full lesson window
  bounded at 21 min. Run 1: `Allegiance.ClientCore.Tests` **126/126 PASS (9 s)**; `Allegiance.ClientCore.MonoGame.Tests`
  ran all six `m2` window children — **4 PASS** (`Classic`/`Modern` × `muted`, `missing-audio`; each window reports
  `P912_WINDOW_PASS … lessons=1,…,9`) and **2 FAIL** (`reminders` for both styles) with
  `Unhandled exception: M2 9.2: native prompts missing or out of order`
  (`tools/p912-training-proof/Program.cs:206`). That signature is the already-ledgered full-gate-only real-window
  prompt-sequence family (`docs/TEST_FLAKES.md`, the P945 rows: same assertion, same reason — instructor prompt
  snapshots under a loaded full gate). The change cannot cause it: the palette is captured at `SaveTrainingPreferences`
  and restored at `ReleaseTraining` (lesson EXIT), no `TrainingController`/goal/prompt/overlay-id line moves, and the
  window reached goal 9.1 with screenshots written before the assertion.
- **Because each window is 21-min-bounded and three full repeats would exceed the task's time cap** (run 1 alone used
  ~40 min of windows), the 3× gate was run with `P912TrainingWindowTests` excluded, exactly as the P1040c training gate
  did for the same reason: **`ClientCore 126/126` and `MonoGame` (every training class except the 20 window proofs)
  ×3**, sequential runsettings, XDG isolated, artwork from `local.env` — results in
  `~/allegiance-evidence/P1041/training-nop912/summary.txt` (filled below). The window proofs are Live/Artwork tests
  the lane rules reserve for Claude's mini runner per merged batch; the six observed children and their logs are kept
  in `.scratch/p940/windows/` → copied to `~/allegiance-evidence/P1041/training/1/Allegiance.ClientCore.MonoGame.Tests/`
  before the scratch is deleted.

**Training gate result (3 repeats, `P912TrainingWindowTests` excluded):**

```
run 1  Allegiance.ClientCore.Tests          126 passed / 0 failed / 0 skipped  (10 s)
run 1  Allegiance.ClientCore.MonoGame.Tests 256 passed / 0 failed / 1 skipped  (11 m 09 s)
run 2  Allegiance.ClientCore.Tests          126 passed / 0 failed / 0 skipped  (10 s)
run 2  Allegiance.ClientCore.MonoGame.Tests 256 passed / 0 failed / 1 skipped  (11 m 08 s)
run 3  Allegiance.ClientCore.Tests          126 passed / 0 failed / 0 skipped  (10 s)
run 3  Allegiance.ClientCore.MonoGame.Tests 256 passed / 0 failed / 1 skipped  (11 m 08 s)
```

(the single skip is the pre-existing `P940BasicFlightInputTests.Installed_training_voice_plays_through_the_production_sound_loader`, unchanged). Logs/TRX:
`~/allegiance-evidence/P1041/training-nop912/`. The palette snapshot/restore and the Jet overlay tests are inside those
257 cases; the six real-window children of run 1 of the full script are in `~/allegiance-evidence/P1041/windows/`
(4 `P912_WINDOW_PASS`, 2 ledgered prompt-sequence failures).

Training-affecting files outside `Rendering/Jet/` that changed: **`Game1.TrainingIntegration.cs`** (the palette in the
pre-lesson snapshot/restore) — hence the training gate is required and is run. The other Game1 changes are the panel
rows, the commit/toast and the proof hook; `Rendering/Jet/` changes are palette/tag plumbing only.

## 10. Native intent and Modern approach

**Native intent.** Native Allegiance draws this skin's content from the same places Jet does, and the variant changes
none of them: a side's ink is `pside->GetColor()` (`WinTrek/consoledata.cpp:99-107`, used as `mySideColor` at
`Artwork/dialog.mdl:26,4722` and for the station/target rows at `:4584,4593,4993`), the console layouts are the mdl's
own rectangles (`dialog.mdl`, `WinTrek/console.cpp`), and the radar is the native allowed-contact set
(`WinTrek/radarimage.cpp`). Native has no colour-variant preference at all — P972 §4.1 defines the monochrome/skin rule
as presentation, so the amber variant is a port-level palette swap of the same native readings, with side identity
(the one native colour that carries meaning) deliberately fixed so the colour-blind/side-identity rule still holds.

**Modern approach.** Both styles run the same shared model: Classic and Modern are pixel- and semantically identical to
the baseline (1,848 combinations, 0 diffs), the palette row exists only in the port's Ctrl+F6 graphics/HUD panel and
only while the gated Jet skin is active, and the variant is persisted beside the skin (not as a skin id) exactly as
P972 §2.4 requires. Jet remains behind `hud.jetSkin` (default off); no default was flipped and Jet is not selectable
without the flag.
