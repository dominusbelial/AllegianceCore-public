# P1038c — Jet HUD skin: F4 title/caption collision, pane opacity, row-1 marker, compact lock cue

Follow-up to P1038b (`docs/reports/JET_LAYOUT_FIXES_P1038B.md`, merged at `6553f09d`), in the `hermes/p1038c`
worktree of `AllegianceCore` at Claude's master. No commit, no push, no stash, no remote host, no Docker (not
installed here). Temporary scratch lived in `.scratch/p1038c/` (removed at completion); raw captures and logs live
outside the repo in `~/allegiance-evidence/P1038C/` (baseline, the pre-fix Jet set, the final Jet set, the sweep
and compare logs); the report's evidence is
the **26 before/after crops + 3 whole frames + `jet-capture.json`** (30 files, 1,841,022 bytes = 1.76 MiB) under
`docs/reports/JET_LAYOUT_FIXES_P1038C_assets/`.

Jet stays presentation-only: every value is the shared `HudModel`/`HudGauge`/`HudOwnStatus`/
`HudFrame.WeaponSystems`/`HudContext.InventoryPane`/`HudContext.Lock` data, nothing is invented, and no
shared-model, server, Core, wire, audio or input path is touched. The story fixes the four defects Claude's
review found in its own P1038b screenshots, confirms the fifth (800×480 lock-readout fit) in Jet-only code, and
extends the proof's layout audit so each class is counted.

**Status**: 1 done (F4 title row) · 2 done (pane backing 0.97) · 3 done (one cap-height slot marker in its own
gutter; icon-word gap ¾ cap) · 4 done (four 30° corner arcs; native closing kept; designator native size) ·
5 confirmed (800×480 lock readout fully on screen; a bottom clamp added as a guard) · audit extension done
(`TextOverlapInPlate`, cue-vs-own-readout, legible-behind-pane counted; P1038b exception 10 → 0) · gate done.

## 1. Zero-delta baseline first, then the comparison

The **unmodified** tree (`6553f09d`) was built (`tools/hud2-proof`, Release) and captured before any edit:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1038C/baseline
HUD2_MANIFEST 1848 combinations, commit 6553f09d095b+dirty, driver llvmpipe (LLVM 19.1.7)
HUD2_CAPTURE_PASS 1848 combinations in 8.5 min
```

After all changes the same tree was re-rendered against that baseline:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)
```

1848 = 22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins (924 Classic + 924 Modern), decoded-RGBA
goldens plus a semantic element/visibility snapshot per combination. The only differences are inside the
fixture's recorded animation mask (the wall-clock low-resource `!` badge, `ModernHudRenderer.cs:166`, 83 of 1848
entries); every entry reports `outside: 0`. **Classic and Modern stay pixel- and semantically identical:
1,848/1,848 combinations, 0 pixel diffs, 0 semantic diffs.** Everything here is Jet-only code plus the proof tool.

The pre-fix Jet evidence set was captured from the same unmodified source before the first edit
(`--jet ~/allegiance-evidence/P1038C/jet-before`, 74 scenes), and the one scene the fixture did not carry at
800×480 on the bright backdrop (`jet-f4`) was rendered from a temporary `git archive HEAD` copy of the old tree
with only that one scene line added (build scratch, removed after the captures; `--jet` into a scratch folder, 75
scenes; its audit line reproduces the P1038b numbers exactly: `stroke-vs-text 10, text-outside-plate 0,
text-off-screen 0, icon-too-small 0`).

## 2. The defects, before → after

Each pair was opened with the image tool and read; the measured numbers below come from the recording surface
(`jet-capture.json`, schema 6) and from direct ink-band/pixel analysis of the PNGs (each ASCII/pixel check is a
look at the rendered pixels, not a guess). At the 6–8× pixel-font zooms the image model's prose was unreliable
(it described the same gap before and after), so every claim below is tied to a measured band or gap.

### 2.1 F4 hull title collides with the caption band → the title has its own row

P1038b centred `Fig Adv` in the native 14-unit top cap while the HUD font's line box is 22 native units
(`MeasureString` height = line spacing), so the title's descender reached into the `KEY SLOT PART QTY STATE
STATS` band.

* **Before** (`f4-title-before-1920x1080-dark.png`, `f4-title-before-800x480-dark.png`,
  `f4-title-before-800x480-bright.png`): in the title/caption columns the bright ink is **one continuous band** —
  at 1920 (x 1445–1520) rows **409–443**, at 800 (x 506–575) rows **79–100**: the `g` descender and the caption
  glyphs share rows, and the crop shows the caption words printed through the bottom of `Fig Adv`.
* **After** (`f4-title-after-*.png`): **two bands with blank rows between them** — 1920: title **393–420**,
  caption **428–443** (7 blank rows); 800: title **70–86**, caption **90–100** (3 blank rows). The title row is
  `JetInventoryPresentation.TitleRow = 24` native px (the 22-unit line box + the 2 px minimum gap), then the
  caption band (`HeaderRow = 14`), then the data rows. At 1080 the measured leftover is
  `(24−22) × paneScale = 3.18 px ≥ 2·scale (2 px)`; at 800 it is `1.8 px ≥ 2·scale (1.33 px)`. The pane is
  bottom-anchored, so it grows upward by `TitleRow − CapHeight = 10` native px (**15.9 px at 1080, 9 px at 800**)
  and the caption band and rows keep exactly their old screen y — no row is stolen (test-pinned). The title is
  drawn top-aligned in its row, never clamped into the band.

### 2.2 F4 pane bleed-through → 0.97 backing (others stay 0.82)

The pane used the readout plate's 0.82 opacity, so contact icons, the `80%` plate, `IDLE`, the ladder numerals
and `ACC` ghosted through the table (worst at 800×480).

* **Before** (`f4-bleed-before-1920x1080-bright.png`, `-800x480-dark`, `-800x480-bright`): the opened crop shows
  faint numerals/tick labels inside the dark table (the 1920 bright crop visibly carries ghosted tick numbers
  under the right-hand stat columns), and the measurement in the 7 px strip where the throttle tape's ticks/labels
  sit behind the pane finds **231–337 pixels** differing from the plate by > 10/255 (peak channel delta
  **19.5–179.6**), i.e. legible HUD ink.
* **After** (`f4-bleed-after-*`, and the whole frames): the same strips contain **0** pixels > 10/255 (peak
  **2.9–7.6**, the 3 % residual). `JetInventoryPresentation.PlateOpacity = 0.97` — the **lowest hundredth** that
  keeps every composited ghost below the audit's stated legibility threshold of **8 of 255 channel separation
  from the plate** on both the dark `(40,53,76)` and bright `(225,215,205)` proof backdrops (0.95 leaves 10.1 on
  the dark backdrop for a white icon, 0.97 leaves 6.5), while the world stays faintly visible (unlike an opaque
  1.0 fill). Every other Jet plate keeps `ReadoutPlateOpacity = 0.82` (test-pinned: the readout plate still fills
  `Plate × 0.82`).

### 2.3 Marker over the row-1 text → one cap-height mark in its own gutter; icon gaps

The glyph over the start of `Gatling Gun` is the **selected/active slot marker**: row 1 in the fixture is
`Selected = true` **and** `Active = true` (`Program.cs JetInventoryPane`), and P1038b drew its two states as two
separate 5·rowScale boxes side by side at native x 26.

* **Before** (`f4-marker-before-1920x1080-dark.png`, `-800x480-dark`): at 1920 the ink runs are
  **1141–1147** (7 px open box) and **1151–1156** (6 px filled box) with the name's ink starting at **1162** —
  **5 px** away, reading as two dots on the first letter; at 800 the two 3 px marks are **358–362** and
  **364–366** against the name at **370** (**3 px**). They sit on the top edge of the text line, not in the row's
  cap band.
* **After** (`f4-marker-after-*`): one mark in `JetInventoryPresentation.MarkerColumn = 20` (its own gutter),
  drawn at `IconSize(CapHeight(textHeight))` — a **12.6 px** box (ink run **1131–1144**) at 1920 and an **8 px**
  box (ink run **353–360**) at 800 — and the name starts at **1162 / 370**, i.e. a measured **17 px / 9 px** clear
  gap (≥ 8 native px by construction).
  Selected is an open square outline, active a filled square; when both are set the fill is inset inside the
  outline so the two states share the one cap-height cell. The mark is reported through `IJetGlyphAudit`, so the
  proof's icon-size rule covers it (no text run intersects it — test-pinned).
* **Annunciator gap** (`flags-gap-before-1920x1080-dark.png` → `flags-gap-after-…`): `JetStoresPanel.IconWordGap`
  is `max(IconGap·scale, ¾·capHeight)`. Before the EYE icon's ink ended at 106 and the `E` began at **113** (6 px);
  after it begins at **117** (10 px), and every following icon/word shifts by the same +5 px. The ¾ cap run gap
  leaves ≥ ¼ cap of clean sky after the icon's and the word's 1 px keylines (the brief's floor), and the caution
  panel uses the same rule for its `!`/open-square marks.

### 2.4 Lock cue too large, crossing its own readout → four 30° corner arcs

P1038b drew a 16-segment ring of radius `√2·LockCueHalf + clearance` (**205.7 px** at radius 120 / lock 62 %,
1080p), which swept across the pitch ladder, `ACC` and (at 800×480) ran into the `LOCK 62%` readout.

* **Before** (`lock-cue-before-1920x1080-dark.png`, `lock-partial-before-800x480-dark.png`, `-800x480-bright`):
  the crops show a segmented ring of long chords around the square bracket, chords crossing ladder rungs and the
  order word, and a chord entering the caption at 800×480.
* **After** (`lock-cue-after-1920x1080-dark.png`, `lock-partial-after-800x480-dark.png`, `-800x480-bright`, whole
  frame `jet-locked-800x480-dark.png`): **four 30° arcs, one centred on each designator diagonal**
  (45°/135°/225°/315°), leaving a **60° gap at every axis direction** (top/right/bottom/left) where the readouts,
  `ACC` and the heading plate live. Distance = `√2·designatorHalf + LockClearance + LockClosingSpan·(1−lock)`
  with `LockClosingSpan = 40` (native `radiusBracket + 40·(1−lock)`, radarimage.cpp:325-332); axis extent =
  `distance·cos 15°`. The **designator keeps its native size** (no shrink) and the cue's corner distance clears
  the box corner by `clearance + closing` exactly. Measured (`jet-capture.json lockFit`):

  | Scene | designator half | corner | cue distance | axis extent | gap outside corner | closing |
  |---|---|---|---|---|---|---|
  | `jet-locked` 1920 | 126.0 | 178.19 | 199.39 | 192.60 | 21.2 (6 + 15.2) | 15.2 |
  | `jet-lockfull` 1920 | 126.0 | 178.19 | 184.19 | 177.91 | 6.0 | 0 |
  | `jet-locked` 800 | 120.62 | 170.59 | 184.72 | 178.43 | 14.13 (4 + 10.13) | 10.13 |
  | `jet-lockfull` 800 | 120.62 | 170.59 | 174.59 | 168.64 | 4.0 | 0 |
  | `jet-locksmall` 1920 | 23.1 | 32.67 | 53.87 | 52.03 | 21.2 | 15.2 |
  | `jet-locklarge` 1920 | 150.0 | 212.13 | 218.13 | 210.70 | 6.0 | 0 |

  Partial = every other **7.5° dash** (4 per corner, 8 inked) marching on the shared `HudLock.NowSeconds` clock
  plus `LOCK nn%`; full = the whole 30° arc per corner (closed) in the hot colour plus `LOCK`; none = nothing.
  Each 30° arc is a **6-chord polyline** (`JetVectorPainter.CueArcSegments`; the chord sagitta is ≤ 0.24 px at the
  largest cue radius), so a partial cue submits 96 short segments and a full cue 48 — close to the old ring's 32
  — instead of the automatic 70-per-arc tessellation; the 100,000-frame allocation draw runs in 0.69 s alone. The
  cue is reported through the optional `IJetCueAudit` channel, so the proof's cue-vs-own-readout check is
  measured, not assumed (0 in every scene), and the generic stroke-vs-text count dropped **10 → 0**.

### 2.5 800×480 lock readout fully on screen — confirmed

`lock-readout-before-800x480-dark.png` and `lock-readout-after-800x480-dark.png`: every line (`HOSTILE …`,
`1250 m CLO +43`, `HULL 060 SHLD 030`) is fully inside the frame in both captures; the old and new audits both
report `text-off-screen 0`. The 800×480 worst case (the unit pin's centre at y = 192) has the readout block from
**382 to 430 px** against the **477 px** gutter limit. Because the cue's axis extent is now smaller, the block
also fits to the right of the cue at 1080p (extent + `8·scale`) instead of being pushed to the screen edge. A
**bottom clamp** (`y ≤ h − gutter − blockHeight`) was added so a below-the-cue placement pushed down by the
heading plate can never lose its last line in a future frame — the on-screen rule wins over drop-below-furniture
when both cannot hold (test-pinned at 1920×1080 and 800×480).

## 3. Audit extension

`tools/hud2-proof --jet` records every text run's ink colour, every plate's tint/alpha, every lock-cue arc and the
lock readout block through the optional `IJetCueAudit` channel (`IJetVectorSurface` implementations that do not
implement it pay one type test per lock frame and draw nothing). Schema 6 adds **three counted classes** per
scene (`jet-capture.json`, counts + 4 samples each):

| Check | Rule |
|---|---|
| `TextOverlapInPlate` | two runs **owned by the same plate** (the last plate drawn before the run that contains it) intersect. A pane's owned box is inset horizontally by `Pad × pane scale`, so the Jet WIP tag drawn at a pane edge is not pane content. Cross-plate text-vs-text stays informational. |
| `CueReadoutHits` | any recorded cue arc whose chorded ink (12 samples, Liang–Barsky with a 2·scale pad) crosses the `LOCK nn%`/`LOCK` readout block the renderer reported for that cue. |
| `LegibleBehindPane` | any text run/icon glyph drawn **before** a near-opaque pane (alpha ≥ 240, taller than four rows) whose box intersects the pane and whose composited ghost keeps **≥ 8 of 255** channel separation from the plate-over-backdrop composite. 8/255 (3.1 %) is the stated threshold; at 0.97 the worst case is 6.5, so the class is 0 by construction and any future opacity regression is caught. |

Totals over the **75 Jet scenes**, final tree:

```
HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0,
               text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0 over 75 scenes
```

The same 75-scene fixture on the P1038b code reports `stroke-vs-text 10, text-outside-plate 0,
text-off-screen 0, icon-too-small 0` (the ten are the P1038b ring-over-furniture exception: the ring chords over
`HDG 045` in the 800×480 lock scenes and over `ACC` at 1080p). **With the compact cue that exception drops to 0**
— the four arcs' 60° axis gaps keep them away from the heading plate, the tapes, `ACC` and the ladder readouts, and
no residual hit needs a reason. `CaptionOverlaps` and `BoxOverlaps` stay **0 in all 75 scenes**.

The three new classes are not merely satisfied by the new tree — they would have caught the old defects:
`TextOverlapInPlate` triggers on the P1038b geometry, where the title's run box (`top … top+34.9`) and the caption
runs (`top+22.2 … top+47.3`) overlap by **12.7 px at 1080** and **7.2 px at 800**; `LegibleBehindPane` triggers on
any 0.82 pane (the tape ghosts measured 231–337 px > 10/255); `CueReadoutHits` is the guard for the cue/readout
pairing the renderer now reports explicitly.

## 4. What changed

| File:line | Change |
|---|---|
| `src/.../Rendering/Jet/JetInventoryPresentation.cs:44` | `TitleRow = 24` native px (the measured 22-unit line box + 2 px); replaces the 14-unit top cap. |
| `.../JetInventoryPresentation.cs:59` | `PlateOpacity = 0.97` for the F4 pane backing (stated rationale: lowest hundredth under the 8/255 ghost threshold on both backdrops). |
| `.../JetInventoryPresentation.cs:73` | `MarkerColumn = 20` (own slot gutter; a cap-height mark ends at 28, name starts at 38). |
| `.../JetInventoryPresentation.cs:137-155` | `TitleTop`, `Height`, `HeaderTop`, `RowTop` in title-row → caption band → data-row order; the pane grows by `TitleRow − CapHeight`. |
| `src/.../Rendering/Jet/JetStoresPanel.cs:194-197` | `IconGapFraction = 0.75` (of cap height), `IconWordGap(capHeight, scale) = max(IconGap·scale, ¾·cap)`. |
| `src/.../Rendering/Jet/JetTargetGeometry.cs:109-151` | `LockClosingSpan = 40` (native term), `LockCueCornerDistance` (`√2·half + clearance + 40·scale·(1−lock)`), `LockCornerSweep = 30°`, `LockCueExtentFraction = cos 15°`, `LockCornerCount = 4`, `LockCornerDashes = 4`, `LockCueExtent`, `LockCornerAngle`; `LockCueHalf`, `LockRingRadius`, `LockRingSegment`, `RingPoint`, `LockSegmentsPerSide` removed. |
| `src/.../Rendering/Jet/JetHudRenderer.cs:762,795-838` | Designator readout clears the cue's axis extent (`LockCueExtentAt`); `blockHeight` measured from the rows actually drawn; bottom clamp `y ≤ h − gutter − blockHeight`. |
| `.../JetHudRenderer.cs:927-935` | `LockCueRadiusAt` → `LockCueExtentAt` (cue axis extent + one stroke). |
| `.../JetHudRenderer.cs:963-1031` | `DrawLock` draws the four corner arcs (30° full / every other 7.5° dash partial) at the corner distance, reports each arc and the readout block to `IJetCueAudit`, and places the caption above/below the cue's extent. |
| `src/.../Jet/JetHudRenderer.StoresCaution.cs:390,422,442` | Annunciator/caution icon→word gap through `IconWordGap`. |
| `.../JetHudRenderer.StoresCaution.cs:625-668` | F4 pane: `PlateOpacity` backing, title top-aligned in `TitleTop`, caption band at `HeaderTop`, rows at `RowTop`. |
| `.../JetHudRenderer.StoresCaution.cs:679-684,706-729` | One cap-height selected/active slot glyph in the marker gutter, reported to `IJetGlyphAudit` with pre-built ids (0 B/frame). |
| `src/.../Jet/JetVectorPainter.cs:48-54,214-222,300-316` | Optional `IJetCueAudit` channel + `CueArc`/`CueReadout`; `Arc` takes an optional chord count and the cue uses 6 per 30° arc (production and test surfaces unaffected). |
| `tools/hud2-proof/Program.cs:438` | New `jet-f4` 800×480 **bright** scene (75 scenes). |
| `tools/hud2-proof/Program.cs:1717-1741` | `LockFit` reports designator half, corner distance, cue distance, axis extent, gap, clearance and closing term. |
| `tools/hud2-proof/Program.cs:1825-2180` | Recording surface records ink colours, plate tint/alpha, cue arcs and readout blocks; audit adds `TextOverlapInPlate`, `CueReadoutHits`, `LegibleBehindPane` (+ samples); schema 6; summary line prints all seven classes. |
| `tests/.../Skins/JetStoresCautionTests.cs` | Lock pins follow the corner-arc family; 8 new tests (§5). |
| `tests/.../Skins/JetTargetLockTests.cs` | Partial/full lock tests count the cue arcs (8 dashes / 4 closed arcs) instead of the 16-segment ring; the recorder implements `IJetCueAudit`. |
| `GLOSSARY.md:1097` | New P1038c entry (title row, pane backing, marker gutter, icon gap, compact cue, the three new audit classes). |

### Rules stated

* **F4 title**: its own 24-unit row above the caption band; the pane grows upward by `TitleRow − CapHeight`; the
  title is never clamped into the band.
* **F4 backing**: 0.97 for the full-overlay pane; every other Jet plate stays 0.82; a ghost is legible at ≥ 8/255.
* **Slot marker**: one cap-height cell in the `SLOT` gutter (20 → 28 native px), ≥ 8 native px before the name;
  selected = open outline, active = filled, both = fill inset in the outline.
* **Icon gap**: ¾ cap (≥ the required ¼ cap of clear sky after keylines).
* **Lock cue**: four 30° corner arcs centred on the diagonals, `√2·designatorHalf + clearance + 40·(1−lock)`;
  partial = 7.5° dashes on the shared clock + `LOCK nn%`, full = closed hot arcs + `LOCK`; the designator keeps
  its native size; the readout clears `cos 15° · distance + stroke`.

## 5. Tests

`JetStoresCautionTests` — **81 tests** (73 P1038/P1038b + 8 new), `JetTargetLockTests` — **71**, all headless
through recording surfaces (no pixel goldens):

* the F4 title sits at `TitleTop` (the pane top), the band starts one `TitleRow` below it, the rows one
  `HeaderRow` after the band, the 24-unit row leaves ≥ 2·scale px for the real 22-unit font box, the drawn title
  box cannot reach the band, and the pane height grew by `TitleRow − CapHeight` (1920×1080 and 800×480);
* the F4 pane fill is `Plate × PlateOpacity` (0.97 ≥ 0.95) while a normal readout plate still fills `Plate × 0.82`;
* row 1's marker is one `inventory:slot:selected+active` cap-height glyph at `MarkerColumn`, ends ≥ 2·paneScale px
  before `Gatling Gun`, and intersects no text run;
* each annunciator icon keeps ≥ ¼ cap between its box and its word's run box;
* no two F4 runs inside the pane intersect (the unit analogue of `TextOverlapInPlate`);
* the lock cue never enters its own readout band and the readout is fully on screen at both resolutions;
* partial = 8 arcs of 7.5° at the corner distance, full = 4 arcs of 30°, both phase tests on the shared clock;
* the corner/gap/closing pins: gap = `clearance + 40·(1−lock)`, extent = `distance·cos 15°`, open→full closes
  40 px, the edge clip is kept;
* the P1038 behaviour tests, the 4K pins and the **0 B/frame** allocation gate are unchanged and green.

```
JetStoresCautionTests + JetTargetLockTests, run 1: Passed 152, Failed 0, Skipped 0
JetStoresCautionTests + JetTargetLockTests, run 2: Passed 152, Failed 0, Skipped 0
JetStoresCautionTests + JetTargetLockTests, run 3: Passed 152, Failed 0, Skipped 0
Skins namespace: 325/325 (was 317; +8)
```

Nothing here exceeds 10 s, so no `[Trait("Tier","Live")]` is needed.

## 6. Screenshots

`tools/hud2-proof --jet` re-captured **75 scenes** into `~/allegiance-evidence/P1038C/jet-final2/` (the full set:
every Jet scene at 1920×1080 and 800×480, dark and bright where the state is colour-sensitive; 76 files incl.
`jet-capture.json`). The asset folder keeps the 26 per-defect crops, 3 whole frames and the JSON (1.76 MiB). Each
asset was opened with the image tool; at the 6–8× pixel-font zooms the model's prose was inconsistent, so the
visible facts below are also pinned to ink bands / pixel counts from the PNG itself.

| Image | What is actually visible |
|---|---|
| `f4-title-before-1920x1080-dark.png` | One continuous bright ink band 409–443 where `Fig Adv` and `QTY STATE STATS` share rows; the caption words run through the title's descender. |
| `f4-title-after-1920x1080-dark.png` | `Fig Adv` at 393–420, blank rows, then `KEY SLOT PART QTY STATE STATS` at 428–443 — a 7-row empty band. |
| `f4-title-before-800x480-dark.png`, `-bright.png` | Same collision at 800×480 (one band 79–100 in the title/caption columns). |
| `f4-title-after-800x480-dark.png`, `-bright.png` | Title 70–86, caption 90–100 (3 blank rows), on both backdrops. |
| `f4-bleed-before-1920x1080-bright.png` | The dark table over bright space visibly carries faint tick numbers inside the right-hand columns (231–337 ghost pixels > 10/255 in the tape strip; peak 179.6). |
| `f4-bleed-after-1920x1080-bright.png` | Clean near-black table; 0 ghost pixels > 10/255 (peak 7.6). |
| `f4-bleed-before-800x480-dark.png`, `f4-bleed-after-800x480-dark.png`, `f4-bleed-before-800x480-bright.png`, `f4-bleed-after-800x480-bright.png` | Same pair at the compact window: ghosted ladder/tape marks before (peak 19.5 dark / 40.4 bright), none after (peak 2.9 / 7.6). |
| `f4-marker-before-1920x1080-dark.png` | Two tiny boxes (1141–1147 open, 1151–1156 filled) hugging the first letter of `Gatling Gun` at 1162. |
| `f4-marker-after-1920x1080-dark.png` | One 12.6 px open box with a filled centre (ink run 1131–1144), 17 px before the name (5 px before the fix). |
| `f4-marker-before-800x480-dark.png`, `f4-marker-after-800x480-dark.png` | 3 px dots at 358–366 against the name at 370 (3 px gap) before; one 8 px compound square at 353–360 with a 9 px gap after. |
| `flags-gap-before-1920x1080-dark.png` | `EYE CLK 70 AUTO VLOCK FLAG ART` with the icons 6 px from the first letter of each word. |
| `flags-gap-after-1920x1080-dark.png` | The same words with a 10 px icon-word gap (¾ cap); each glyph is a cap-height chevron/stand/corner/pennant/diamond. |
| `lock-cue-before-1920x1080-dark.png` | A 16-segment ring of long chords around the square bracket, sweeping across the ladder rungs and the order word. |
| `lock-cue-after-1920x1080-dark.png` | Four short dashes at the bracket's corners only (no ring), `LOCK 62%` above them, ladder and `ACC` clear of the cue. |
| `lock-partial-before-800x480-dark.png`, `-bright.png` | The large segmented ring at the compact window, a chord entering the `LOCK 62%` caption area. |
| `lock-partial-after-800x480-dark.png`, `-bright.png` | Four corner dashes around the native-size box, `LOCK 62%` below the cue, fully inside the 60° axis gap. |
| `lock-readout-before/after-800x480-dark.png` | `HOSTILE …` (long pilot name), `1250 m CLO +43`, `HULL 060 SHLD 030` all fully visible; no line clipped at the bottom (block ends at 430 of 476). |
| `jet-f4-1920x1080-dark.png` (whole frame) | The pane reads as a table: title row, caption band, 12 rows with `MOUNT`/`ARM` bars, stats inside the plate; the dark right-bottom quadrant behind it is uniform (no ladder/tape ghosts). |
| `jet-f4-800x480-bright.png` (whole frame) | The compact pane on bright space: title above the caption band, rows readable, backing clean; stores/caution/lock elsewhere unchanged. |
| `jet-locked-800x480-dark.png` (whole frame) | The square designator framed by four corner dashes, `LOCK 62%` clear of them, the three-line target readout on screen, stores and caution unchanged. |

## 7. Coverage, parity and glossary

`JetHudRenderer.Coverage` is unchanged at **64 ids** (no element added or removed; the lock remains
`Missile lock flower`). No `docs/parity/*.csv` row changes status (Jet is presentation-only; a look cannot promote
a `done-visual` row): `scripts/parity-scorecard.py docs/parity/parity-matrix.csv` validates and `hud` stays
111/111 rows, 330/330 weighted, TOTAL 99.61 %.

`GLOSSARY.md:1097` gains the P1038c entry; `tools/check-glossary-links.py` passes
(`GLOSSARY.md link check OK (919 distinct paths checked)`).

## 8. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (23.3 s, final tree) |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built**, each 0 warnings / 0 errors (re-run after the final `src` change) |
| New/affected tests 3× alone | `dotnet test --filter FullyQualifiedName~JetStoresCautionTests\|FullyQualifiedName~JetTargetLockTests` (3 processes) | **152/152 each run** (`Passed 152, Failed 0, Skipped 0`) |
| Affected allocation test 3× alone | `--filter ...JetTargetLockTests.Draw_with_a_locked_leading_ordered_target_allocates_nothing` | **3/3 pass, 0.67–0.70 s each** |
| Existing skin tests | `dotnet test --filter FullyQualifiedName~Skins` | **325/325** (31 s) |
| Zero-delta (baseline first, then compare) | `--capture ~/allegiance-evidence/P1038C/baseline` → `--compare` back to it | **`1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)`** (final tree; the `--jet` capture is not part of the Classic/Modern matrix) |
| Jet evidence capture | `hud2-proof --jet ~/allegiance-evidence/P1038C/jet-final2` | `HUD2_JET_CAPTURE_PASS 75 scenes`; `HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0` |
| Glossary links | `python3 tools/check-glossary-links.py` | `GLOSSARY.md link check OK (919 distinct paths checked)` |
| Parity CSV | `python3 scripts/parity-scorecard.py docs/parity/parity-matrix.csv` | `hud` 111/111 rows, 330/330 weighted (unchanged), TOTAL 99.61 % |
| Fast tier (one run, final tree) | `tools/run-tests.sh --tier fast` | **15,034 tests, 642.4 s, 3 failed / 7 not executed, 7 calibrated-limit offenders (all pre-existing, timing-only)**. The 3 failures are ledgered load families, each **3/3 alone**: `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` (7,096 B / 10k frames in the sweep; 15–16 ms alone), `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (≈1 s alone), `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` (0.33–0.34 s alone). Recorded in `docs/TEST_FLAKES.md` (P1038c section). |
| AOT publish | not run — no `src/Allegiance.Server` or `src/Allegiance.Core` change | n/a |
| Training gate | not run — no training-affecting code | n/a |

## 9. Anything unfinished / caveats

* **The 800×480 bright F4 scene is new** in the fixture (75 scenes now); the pre-fix image for that one
  combination was rendered from a temporary `git archive HEAD` copy of the unmodified tree with only the scene line
  added (build scratch, removed after the captures), so the before/after pair is real old code → new code, not a
  recolour.
* **Audit blind spot (unchanged)**: numeric-only runs are not stroke-checked (the ladder numerals and tape
  graduations are furniture every overlay crosses); cue-vs-readout is measured separately for the lock caption, and
  the target readout (`HOSTILE …`) is covered by the generic stroke rule because the cue is drawn after it.
* **The shared radar pass is not on the recorded Jet surface**: contact icons/labels behind the pane are not
  individually recorded by `LegibleBehindPane`. Its rule is colour-independent (the pane leaves ≤ 3 % of any
  pixel's ink-vs-backdrop difference), and the pixel strip check (0 ghosts at 0.97, peak 7.6) covers those pixels
  in the rendered frame; stated so the boundary is explicit.
* **The cue can still be clipped by the viewport edge** for a target near the edge, exactly like native's
  `radiusBracket` clip (radarimage.cpp:308-319); the arcs then exit the screen instead of shrinking.
* **Text-vs-text across plates** stays informational (P1036 rule, e.g. ladder numerals sharing sky with a
  readout); only same-plate overlap is counted, and it is 0.
* **New media** (30 files, 1,840,938 bytes) needs Domingo's review before any push.
* The brief file `P1038C_BRIEF.md` stays untracked and is excluded via `.git/info/exclude` so it cannot enter the
  change set.

## Native intent

Native owns every value and every timing these widgets present: the F4 inventory itself (`dialog.mdl:5385-5393`,
the `inv*.mdl` columns, the ship-name title `dialog.mdl:1803-1807`, the 0.5 s slide `dialog.mdl:5387`, the row
order `WinTrek/inventoryimage.cpp:299-346`), the per-state lock sizing `radiusBracket = 1.05 × radiusObject`
clipped to `distanceToEdge + c_flBorderSide` (radarimage.cpp:302-319) and
`radiusBracket + 40·(1−lock)` for a partial lock (radarimage.cpp:325-332), and the lock tone/flash timing that
stays with `MissileLockFeedback`. The layout fixes change no value, gate, timer or sound: they only place the same
readings where they can be read. Native's inventory pane is a solid line-background artwork, which is why the Jet
F4 pane's backing is near-opaque rather than a see-through readout plate; native's lock surface is drawn over the
same flight symbology, so the cue is anchored to the shared target position and never moved to avoid furniture.

## Modern approach

The fixes live in the pure presentation files plus the renderer partials, so they are headlessly testable: the
title row, the pane opacity, the marker gutter, the icon gap and the cue are named constants/functions
(`TitleRow`, `PlateOpacity`, `MarkerColumn`, `IconWordGap`, `LockCueCornerDistance`, `LockCueExtent`,
`LockCornerAngle`), and the proof measures the result instead of trusting it. The recording surface now records ink
colours, plate alpha, cue arcs and the lock readout, so "no two runs overlap inside a plate", "no cue arc crosses
its own readout" and "nothing legible survives behind the pane" are facts in `jet-capture.json` (schema 6), not
assertions. Classic and Modern remain pixel- and semantically identical, proven by the 1,848-combination
decoded-RGBA comparison against a baseline captured before the first edit.
