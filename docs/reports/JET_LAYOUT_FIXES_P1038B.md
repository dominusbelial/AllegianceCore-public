# P1038b — Jet HUD skin: layout/readability fixes for stores, caution, F4 pane and lock

Follow-up to P1038 (`docs/reports/JET_STORES_CAUTION_P1038.md`), in the `hermes/p1038b` worktree of
`AllegianceCore` at Claude's master (`5d33543d`). No commit, no push, no stash, no remote host, no Docker (not
installed here). Raw captures live outside the repo in `~/allegiance-evidence/P1038B/` (the baseline sweep, the
before/after Jet captures and the comparison logs); the report's evidence is the **40 PNGs + `jet-capture.json`** (41 files)
under `docs/reports/JET_LAYOUT_FIXES_P1038B_assets/` (1,945,334 bytes = 1.85 MB, inside the 2 MB task cap).

Jet stays presentation-only: every value is the shared `HudModel`/`HudGauge`/`HudOwnStatus`/`HudFrame.WeaponSystems`/
`HudContext.InventoryPane`/`HudContext.Lock` data, nothing is invented, and no shared-model, server, Core, wire,
audio or input path is touched. The story fixes the six measured readability defects in Jet-only code and extends the
proof's layout audit so each class is caught automatically.

**Status**: 1 done (caption band under the title, STATS inside the plate) · 2 done (bars beside/under with a gap) ·
3 done (measured row pitch) · 4 done (icons at cap height) · 5 done (padded, bounded caution panel) · 6 done (padded
stats, readout-plate opacity) · 7 done (native-size designator enclosed by a segmented ring) · audit extension done
(four new classes, 0 in every fixed scene; one documented lock-cue exception).

## 1. Zero-delta baseline first, then the comparison

The **unmodified** tree was built and captured before any edit:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1038B/baseline
HUD2_MANIFEST 1848 combinations, commit 5d33543d3149+dirty, driver llvmpipe
HUD2_CAPTURE_PASS 1848 combinations in 6.1 min
```

After the change the same tree was re-rendered against that baseline:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 82 animation-only diff(s), 0 corrupt golden(s)
```

1848 = 22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins (924 Classic + 924 Modern), decoded-RGBA
goldens plus a semantic element/visibility snapshot per combination. The only differences are inside the fixture's
recorded animation mask (the wall-clock low-resource `!` badge, `ModernHudRenderer.cs:166`, 82 of 1848 entries);
every entry reports `outside: 0`. **Classic and Modern stay pixel- and semantically identical: 1,848/1,848
combinations, 0 pixel diffs, 0 semantic diffs.** Everything here is Jet-only code plus the proof tool.

## 2. The six defects, before → after

Each pair below was opened and read with the image tool at 2–3× zoom; the measurements come from the recording
surface the proof renders (`jet-capture.json`, schema 5). Full frames and every crop are in
`docs/reports/JET_LAYOUT_FIXES_P1038B_assets/`.

### 2.1 The F4 column caption sat on the last row

P1038 drew `SLOT KEY PART            QTY  STATE    STATS` in the pane's **bottom cap**, i.e. on the last inventory
line: in `jet-f4-1920x1080-dark.png` it printed across the `Fuel Pod` row.

* Before (`f4-header-before-1920x1080-dark.png`): the caption row is the last line of the table, directly over
  `Fuel Pod`; it is one literal string whose `SLOT` sits left of `KEY` although their columns are at 26 and 8 native
  px, and its tail (`STATS`) reaches the plate's right edge.
* After (`f4-header-after-1920x1080-dark.png`, `f4-header-after-800x480-dark.png`): a **caption band of its own**
  (`JetInventoryPresentation.HeaderRow = 14` native px) directly under the hull title (`Fig Adv`), six measured
  captions each over the column it names (`KEY` 8, `SLOT` 26, `PART` 38, `QTY` 190, `STATE` 224, `STATS` 282 native
  px), each clamped inside the plate's padding, and the leading three laid out with a measured gap so they cannot
  overlap at 800×480. The pane grows by that band; no inventory row is stolen. Native list order is top-down
  (`inventoryimage.cpp:299-346` inserts weapons → observers → turrets → magazine … → cargo into one column), and the
  native title (`dialog.mdl:1803-1807`) stays in the top cap, so the caption under it matches the native reading
  order (a StringImage hangs below its point in the y-up native layout).

### 2.2 Bars drawn through their own words

The store `HULL`/`SHLD` bars were stroked at `rowTop + 13·scale` — inside the 22 px text line box — and the F4
`MOUNT`/`ARM` bars at `rowTop + 0.62·rowH`, which crossed the bottom of the state word. Both read as strikethrough.

* Before (`stores-bars-before-1920x1080-dark.png`, `f4-bars-before-1920x1080-dark.png`): the HULL/SHLD bars cut
  through the lower half of their own percentage, and the MOUNT/ARM bar cuts the bottom of the word.
* After (`stores-bars-after-1920x1080-dark.png`, `f4-bars-after-1920x1080-dark.png`): the store bar is **beside**
  the reading, `BarGap = 6·scale` after the value's ink and vertically centred on the text line
  (`JetStoresPanel.BarY`); the F4 progress bar is **under** the state word, `ProgressBarGap = 2·rowScale` below the
  measured text height and clamped inside the row. The audit's `stroke-vs-text` count for both scenes is 0.

### 2.3 Store rows too tight

The row pitch was a fixed `18·scale`, while `SpriteFont.MeasureString` returns the font's **line spacing** — 22 px at
scale 1 for the 8 pt HUD font. At 1080p consecutive line boxes therefore overlapped by 4 px (and the bars added
ink inside the same band); on the bright backdrop the rows merged.

* Before (`stores-rows-before-1920x1080-bright.png`): HULL/SHLD/ENG/FUEL/GUN/MSL/KB/SIG/annunciators read as one
  dense block; the bright backdrop bleeds between the lines.
* After (`stores-rows-after-1920x1080-bright.png`): the pitch is the **measured** line height plus `RowGap = 3·scale`
  (`RowHeight(22,1) = 25 px`, ≥ 2·scale gap at every density), the block stays bottom-anchored, and the rows read
  apart on both backdrops. At 800×480 the block keeps its three compact rows (P972 §4.5) at the same measured pitch,
  and drops the `STORES` header so the taller rows stay clear of the speed readout plate above them.

### 2.4 Annunciator icons illegible

The `AUTO`/`VLOCK`/`FLAG`/`EYE`/`CLK`/`ART` glyphs and the caution tick were drawn at `6·scale` (6 px at 1080p,
4 px at 800×480) — the P1038 review's "2–4 px marks".

* Before (`flags-icons-before-1920x1080-dark.png`): the glyphs before the words are specks; only the words identify
  the state.
* After (`flags-icons-after-1920x1080-dark.png`): each glyph is drawn at the **cap height of its word**
  (`IconSize(CapHeight(lineHeight))`, 11 px at 1080p and 7.3 px at 800×480), keeps its keyline, and the word stays
  beside it — state is never carried by the icon or a hue alone. The caution's leading mark is enlarged the same way
  (see 2.5); the compact block keeps the word-only form (P972 §4.5) so nothing shrinks at 800×480.

### 2.5 Caution panel overflow

The panel's plate ended exactly where the longest line's ink ended (no right padding) and the rows were
right-aligned to `w − 24·scale`; `Reloading ammo…` therefore spilled past the plate, and the leading ticks were
2–4 px.

* Before (`caution-before-1920x1080-dark.png`): rows touch the plate's right edge (and the longer `CRITICAL TIP`
  line visibly crosses it); the leading `!`/open-square marks are hairlines.
* After (`caution-after-1920x1080-dark.png`, `caution-after-1920x1080-bright.png`): the panel width is the measured
  widest row plus `pad = 6·scale` on **all four sides**, the plate's right edge keeps the same edge gutter as the
  other Jet plates (`JetCautionModel.RightInset × scale`), every row is left-aligned at `panel.X + pad` with the
  icon + `IconGap` before its words, and the icon is a cap-height mark. A row too wide for the bounded panel is
  drawn at the measured fit scale (the value is never truncated). `text-outside-plate` = 0 in both caution scenes.

### 2.6 F4 stats past the edge and the pane's see-through background

The stat stack was gated only 4 native px inside the plate, the caption could reach the plate edge, the title's run
box began above the plate, and the pane's plate used 0.78 opacity (weaker than every other Jet plate), so contacts
and the tape readouts behind it showed through and cluttered the table.

* Before (`f4-stats-before-1920x1080-bright.png`): `CLK END 12 s` ends at the pane's right edge and the bright
  backdrop shows through the table.
* After (`f4-stats-after-1920x1080-bright.png`): every cell keeps `Pad = 6` native px inside the plate and every stat
  run must end a further `4·rowScale` before the padding (a run that cannot fit is not drawn at all — its label and
  value stay in the shared model — which is the stated abbreviation rule); the plate uses the **readout plate's 0.82
  opacity** (`ReadoutPlateOpacity`, the same value `JetHudRenderer.DrawReadout` uses), the captions are inside the
  plate, and the hull title is clamped to the plate top. `text-off-screen` = 0 and `text-outside-plate` = 0 in all
  F4 scenes.

### 2.7 The lock no longer shrank the target box

P1038 made the designator fit inside a lock **diamond** by contracting the box (`126 → 66.36 px` half at radius 120,
lock 62 %), so the box jumped when a lock started and stopped framing the target. P972 §4.3 allows a
"segmented diamond/arc": the cue now **encloses** the native-size box instead.

* Before (`lock-before-1920x1080-dark.png`, `lock-partial-before-800x480-dark.png`): the square designator is
  contracted and its corners sit just inside a 16-segment diamond; the box no longer frames the target.
* After (`lock-after-1920x1080-dark.png`, `lock-partial-after-800x480-dark.png`,
  `lockfull-after-1920x1080-bright.png`): the designator keeps its native half-span (radius × 1.05, P1037 clamps), and
  the lock cue is a **16-segment ring of radius `√2·LockCueHalf + LockClearance`** — at radius 120 that is
  **205.7 px** at lock 62 % and **184.2 px** at full lock, against a corner distance of **178.2 px**, so the ring
  clears the corners by 27.5 px while building and closes exactly 6 px outside them at full lock. The native
  `radiusBracket + 40·(1−lock)` shrink still drives every size (`radarimage.cpp:325-332`); partial = open/dashed
  segments marching on the shared `HudLock.NowSeconds`, full = closed/hot, exactly as before. The readout keeps the
  P1038 placement rule (`cx ± max(half, ring) + 8·scale`) plus `BelowIfCrossed`, gains a "below the cue" placement
  when neither side fits (800×480), and is clamped inside the viewport, so no readout leaves the screen.

## 3. Audit extension (so this does not come back)

`tools/hud2-proof --jet` now records **strokes** and **icon glyphs** in its recording surface and audits four more
classes per scene (JSON schema 5; counts + 4 samples each in `jet-capture.json`):

| Check | Rule |
|---|---|
| `StrokeHits` (stroke-vs-text) | a line/bar segment drawn **after** a text run (i.e. on top of it) whose ink crosses the run's capital-ink band (the middle of the SpriteFont line box: 18 %→64 % of its height, inset 1 px). Background furniture drawn before the text (the P1036 ladder rungs, a plate's tick) is not a strikethrough, and a pane drawn over a run hides it. |
| `TextOutsidePlate` | a run overlapping the plate drawn before it (its own plate) but not fully inside it. |
| `TextOffScreen` | a run box beyond the viewport minus a 4·scale edge gutter (content of a pane that is sliding out is exempt: the native 0.5 s F4 slide is intentionally off screen while closed). |
| `IconTooSmall` | an icon glyph shorter than the cap height of the word it introduces; the renderer reports each mark through the optional `IJetGlyphAudit` channel (`JetVectorPainter.Glyph`), so the check costs one type test per icon in production and draws nothing. |

Totals over the 74 Jet scenes, final tree: **stroke-vs-text 10, text-outside-plate 0, text-off-screen 0,
icon-too-small 0**. The ten stroke hits are the one intentional exception:

* **Intentional exception — lock cue over HUD furniture.** In the four lock scenes at 800×480 (`jet-locked`,
  `jet-lockfull`, `jet-locklarge`, each on dark and bright) a ring chord passes over the `HDG 045` readout, and in
  `jet-locked` 1920 the chord passes over the order-arrow word `ACC`. The cue is a shape anchored to the shared
  target position: enclosing a native-size box at 800×480 necessarily sweeps the furniture around the target, exactly
  as native draws its lock surface at `radiusBracket` over the same HUD (`radarimage.cpp:325-332`); moving the cue
  would break parity with the target position. Counted per scene in `jet-capture.json`, not fixed and not hidden.

Per-scene counts for the six fixed widgets (all four classes):

| Scene | StrokeHits | TextOutsidePlate | TextOffScreen | IconTooSmall |
|---|---|---|---|---|
| `jet-stores` 1920/800 dark + bright | 0 | 0 | 0 | 0 |
| `jet-stores-low`, `jet-stores-critical` 1920/800 | 0 | 0 | 0 | 0 |
| `jet-pod`, `jet-flags`, `jet-ghost` 1920/800 | 0 | 0 | 0 | 0 |
| `jet-caution` 1920/800 dark + bright | 0 | 0 | 0 | 0 |
| `jet-f4` 1920 dark + bright, 800 dark | 0 | 0 | 0 | 0 |
| `jet-f4-empty` 1920/800 dark | 0 | 0 | 0 | 0 |
| `jet-locked` / `jet-lockfull` / `jet-locklarge` 800 dark + bright | 1–2 | 0 | 0 | 0 (exception above) |
| `jet-locksmall` 1920/800 | 0 | 0 | 0 | 0 |

`CaptionOverlaps` and `BoxOverlaps` (P1036/P1038 checks) stay **0 in all 74 scenes**.

## 4. What changed

| File:line | Change |
|---|---|
| `src/.../Rendering/Jet/JetStoresPanel.cs:42,47,87-105,187-200` | `RowGap = 3`, `BarGap = 6`, `RowHeight(textHeight, scale) = max(18·scale, textHeight + RowGap·scale)`, `RowTop/HeaderTop` take the measured pitch, `BarY` centres a bar on a text line, `IconGap`, `IconSize`, `CapInkFraction`/`CapHeight` (capitals ≈ half the measured line spacing). |
| `.../Jet/JetHudRenderer.StoresCaution.cs:73-90` | `DrawStores` measures the line height, pitches rows by it, drops the compact header (800×480), draws `HULL`/`SHLD` bars beside the value with `BarGap`. |
| `.../Jet/JetHudRenderer.StoresCaution.cs:213,347-390` | Annunciator glyphs at cap height, each reported to `IJetGlyphAudit`; word kept beside the mark; compact row stays word-only. |
| `.../Jet/JetHudRenderer.StoresCaution.cs:240-252` | `SegmentedBar` takes a vertical CENTRE (so a bar can sit beside or under a line without touching it). |
| `.../Jet/JetHudRenderer.StoresCaution.cs:409-474,565-586` | Caution panel: measured widest row, `pad` on all sides, left-aligned rows at `panel.X + pad`, right edge at `RightInset × scale`, fit-scaled when a tip would exceed the bounded width, cap-height `!`/open-square icons reported to the audit. |
| `.../Jet/JetHudRenderer.StoresCaution.cs:604-673` | F4 pane: caption band under the title with six measured, non-overlapping captions; plate at `ReadoutPlateOpacity = 0.82`; title clamped to the plate; rows start after the caption band; padding on every cell. |
| `.../Jet/JetHudRenderer.StoresCaution.cs:675-780` | F4 row: measured text centring, progress bars under the state word (`ProgressBarGap`), stat gate at `Pad` + `4·rowScale` (a run that cannot fit is dropped, never clipped). |
| `.../Jet/JetHudRenderer.cs:759-830` | Designator keeps its native size (the P1038 contraction is gone); the readout clears the ring, gains a below-the-cue placement and is clamped inside the viewport. |
| `.../Jet/JetHudRenderer.cs:916-927` | `LockHalfAt` → `LockCueRadiusAt` (native half → ring radius). |
| `.../Jet/JetHudRenderer.cs:983-1017` | `DrawLock` draws the segmented ring (`LockRingSegment`) and places `LOCK nn%`/`LOCK` above it, or below it when the top would leave the viewport. |
| `.../Jet/JetTargetGeometry.cs:96-127,163-174` | `LockClearance`, `Sqrt2`, `LockCueHalf` (native `LockBracketStyle.HalfSizeForFraction` + the P1037 floor), `LockRingRadius`, `LockRingSegment`, `RingPoint`; `LockDiamondHalf`, `DesignatorHalfInsideLock`, `InverseSqrt2`, `LockSegment`, `DiamondPoint` removed. |
| `.../Jet/JetInventoryPresentation.cs:32,36,57-80,105-124` | `HeaderRow = 14`, `Pad = 6`, `ProgressBarHeight/Gap`, `HeaderColumns` (six captions + anchors), `Height`/`RowTop` include the caption band. |
| `.../Jet/JetVectorPainter.cs:34-45,276-282` | New optional `IJetGlyphAudit` channel + `JetVectorPainter.Glyph` (production and test surfaces are unaffected). |
| `tools/hud2-proof/Program.cs` | `--jet` audit records strokes, plates with draw order, glyph boxes; adds `StrokeHits`, `TextOutsidePlate`, `TextOffScreen`, `Icons`, `IconTooSmall` + samples, `capHeight` per scene, schema 5, and a `HUD2_JET_AUDIT` summary line. `LockFit` now reports `lockHalf`, `ringRadius`, corner distance and gap. |
| `tests/.../Skins/JetStoresCautionTests.cs` | Lock pins follow the ring (`LockCueHalf`/`LockRingRadius`), the `HeaderTop`/`RowTop` calls take the measured pitch, and 14 new geometry pins cover the six defects (§5). |
| `GLOSSARY.md:1079` | New entry: the P1038b layout rules (measured pitch, bars beside/under, icons at cap height, padded plates, the lock ring, the four new audit classes). |

### Rules stated

* **Bars**: beside the reading (stores, `BarGap`), under the state word (F4, `ProgressBarGap`); never across a glyph.
* **Stat runs**: every drawn run ends at least `Pad + 4·rowScale` inside the pane; a run that cannot fit is not drawn
  (its label and value remain in the shared model) instead of being clipped at the plate edge.
* **Long caution line**: if the widest row would exceed the bounded panel width, the whole panel is drawn at the
  measured fit scale — the value is never truncated.
* **Compact (800×480)**: three store rows, no block header, word-only annunciators, two caution lines, the F4 pane
  keeps its anchor, caption band and rows.

## 5. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetStoresCautionTests.cs` — **73 tests** (59 P1038 + 14 new
geometry pins, no pixel goldens):

* the F4 captions sit under the title, above the first row and inside the plate, and never overlap each other
  (1920×1080 and 800×480);
* the F4 `MOUNT`/`ARM` bar starts at the state column **below** the word's text box and stays inside its row
  (both resolutions);
* the store `HULL`/`SHLD` bar starts `BarGap` after its reading and intersects neither glyph box;
* `RowHeight` = measured line height + gap (≥ 2·scale at 0.667/1/1.5/2), and the drawn rows are exactly one pitch
  apart at 1080p, three compact rows one pitch apart at 800×480;
* annunciator and caution icons are reported and are at least the cap height (compact keeps the words only);
* every caution row keeps `pad` inside the plate, whose right edge is `RightInset × scale`;
* the cloak row's stat runs end inside the pane's padding;
* the F4 plate is filled with the readout plate's `0.82` opacity;
* at 800×480 with a high lock no text run leaves the viewport (`LOCK` included);
* the lock pins: the ring encloses the native designator at lock 0/0.25/0.62/1.0, the native shrink drives the ring
  (−40·√2 from open to full), the edge clip is kept, and a locked 1080p readout clears the ring radius;
* the P1038 behaviour tests (stores gates, caution priority, damage-flash timing, no timer/sound, F4 rows, compact,
  4K, 0 B/frame, draw smoke) are unchanged and green.

```
JetStoresCautionTests run 1: Passed 73, Failed 0, Skipped 0
JetStoresCautionTests run 2: Passed 73, Failed 0, Skipped 0
JetStoresCautionTests run 3: Passed 73, Failed 0, Skipped 0
Skins namespace: 317/317 (was 303; +14)
```

Nothing here exceeds 10 s, so no `[Trait("Tier","Live")]` is needed.

## 6. Screenshots

`tools/hud2-proof --jet` re-captured all 74 scenes at 1920×1080 and 800×480 (dark and bright where the state is
colour-sensitive) into `~/allegiance-evidence/P1038B/jet-after6/`; the before set is
`~/allegiance-evidence/P1038B/jet-before/`. The assets folder holds the 41 files (40 PNGs + the capture JSON): the per-defect before/after
crops, eleven whole-frame after shots and `jet-capture.json` (schema 5). Every image was opened with the image tool;
the crops above are what is visible, and the audit's numbers corroborate them:

| Image | What is actually visible |
|---|---|
| `jet-stores-1920x1080-dark.png`, `jet-stores-800x480-bright.png` | Normal stores: `STORES` header (full form only), `HULL 82%`, `SHLD 70%`, `ENG 100%`, `FUEL 90%`, `GUN 80% 160`, `MSL 2 READY`, `KB 15 SIG 42`, `AUTO VLOCK FLAG`; the segmented bars sit right of the HULL/SHLD percentages; the rows are visibly separated; the compact frame keeps three rows and no header. |
| `stores-rows-before/after-1920x1080-bright.png` | Before: nine rows merge into one block over the bright backdrop. After: the same values on a measured pitch, each row readable, the bars beside the reading. |
| `jet-caution-1920x1080-dark.png`, `-bright`, `800x480` | The five-line stack `MSL TRACK 72%`, `DAMAGE`, `ENG 3%`, `HULL 5%`, `BOUNDARY` with a cap-height `!` or open square; every row inside the panel with padding, the plate ending at the same edge gutter as the readout plates; 800×480 shows two lines. |
| `caution-before/after-1920x1080-dark.png` | Before: the rows are flush to the plate's right edge and the longer tip crosses it; after: a left-aligned padded column with the icons legible. |
| `jet-f4-1920x1080-dark.png`, `-bright`, `jet-f4-800x480-dark.png` | The pane: title `Fig Adv`, then the caption band `KEY SLOT PART QTY STATE STATS`, then the rows (`1. Gatling Gun`, `2. Plasma Cannon`, a turret, `Observers: 2`, `MOUNT`, `ARM`, `SHD STR 500 RECH 3 s`, `AB SPD 220mps AB END 8`, `CLK STR 82% CLK END 12 s`, `QTY 40`, the `S`/cargo rows); progress bars sit under `MOUNT`/`ARM`; no word is struck through; the table reads cleanly over the bright backdrop. |
| `f4-header-before/after-1920x1080-dark.png`, `f4-header-*-800x480-dark.png` | Before: `SLOT KEY PART … STATS` on the last row over `Fuel Pod`. After: the six captions in their own band under the title, each over its column, `STATS` inside the plate. |
| `f4-bars-before/after-1920x1080-dark.png` | Before: the `MOUNT`/`ARM` bar cuts the bottom of the word; after: it sits cleanly under it. |
| `f4-stats-before/after-1920x1080-bright.png` | Before: `CLK END 12 s` at the pane edge and the backdrop showing through; after: the stats end inside the padding and the denser plate keeps the table readable. |
| `flags-icons-before/after-1920x1080-dark.png` | Before: specks before `AUTO VLOCK FLAG`; after: cap-height chevron/stand/corner/pennant marks beside the words. |
| `jet-locked-1920x1080-dark.png`, `lock-full-after-800x480-dark.png` | Partial lock at 1080p: a dashed ring around a full-size square designator with `LOCK 62%` above it and `HOSTILE`/name/range readouts outside the ring. The 800×480 full-lock crop shows the closed hot ring and `LOCK` placed clear of the viewport edge (below the ring: the above position would be off screen). The box no longer shrinks when the lock starts. |
| `lock-before/after-1920x1080-dark.png`, `lock-partial-before/after-800x480-dark/bright.png`, `lock-full-*-800x480-dark/bright.png`, `lockfull-before/after-1920x1080-bright.png` | Before: the contracted box inside a diamond, `LOCK 62%` hugging it; after: the native-size box framed by the ring at both resolutions, with the readout clear of it and inside the screen. |
| `jet-locksmall-1920x1080-dark.png`, `jet-locklarge-1920x1080-dark.png` | The two fixture radii: 22 px (designator 23.1, ring 65.4, gap 32.7) and 145 px (designator at the 150 px ceiling, ring 221.3, gap 9.2) — the ring encloses the box in both. |

## 7. Coverage, parity and glossary

`JetHudRenderer.Coverage` is unchanged at **64 ids** (no element added or removed; the lock remains
`Missile lock flower`). No `docs/parity/*.csv` row changes status (Jet is presentation-only; a look cannot promote a
`done-visual` row): `scripts/parity-scorecard.py docs/parity/parity-matrix.csv` validates and `hud` stays 111/111
rows, 330/330 weighted, TOTAL 99.61 %.

`GLOSSARY.md:1079` gains the P1038b entry; `tools/check-glossary-links.py` passes.

## 8. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (28.7 s) |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built**, each 0 warnings / 0 errors |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetStoresCautionTests` (3 processes) | **73/73 each run** |
| Existing skin tests | `dotnet test --filter FullyQualifiedName~Skins` | **317/317** (32 s) |
| Zero-delta (baseline first, then compare) | `--capture ~/allegiance-evidence/P1038B/baseline` → `--compare` back to it | **`1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 82 animation-only diff(s), 0 corrupt golden(s)`** |
| Jet evidence capture | `hud2-proof --jet ~/allegiance-evidence/P1038B/jet-after6` | `HUD2_JET_CAPTURE_PASS 74 scenes`; `HUD2_JET_AUDIT stroke-vs-text 10, text-outside-plate 0, text-off-screen 0, icon-too-small 0` |
| Glossary links | `python3 tools/check-glossary-links.py` | `GLOSSARY.md link check OK (918 distinct paths checked)` |
| Parity CSV | `python3 scripts/parity-scorecard.py docs/parity/parity-matrix.csv` | `hud` 111/111 rows, 330/330 weighted (unchanged), TOTAL 99.61 % |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **13,558 tests, 328.5 s, FAIL**: Browser 12, Update 55, Lobby 209, MonoGame 5,723 passed / 4 not executed; `Core.Tests` and `ClientCore.Tests` each 1 failed. Both failures are ledgered load families and pass **3/3 alone** (`WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` 3/3 at ≈0.39 s; `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` 3/3 at ≈1 s), recorded in `docs/TEST_FLAKES.md` (P1038b section) with the four calibrated-limit offenders (P796 12.8 s, SC2Nanite 18.1 s, P814 14.9 s, SC2Nanite 12.4 s — timing-only, already ledgered). The reported `missing 1468 cases` follows from the two failed suites, not from a discovery change. |
| AOT publish | not run — no `src/Allegiance.Server` or `src/Allegiance.Core` change | n/a |
| Training gate | not run — no training-affecting code | n/a |

## 9. Anything unfinished / caveats

* **The lock-cue-over-furniture exception** (§3) is the only non-zero new-audit count; it is inherent to enclosing a
  native-size box with a cue anchored on the shared target position at 800×480.
* **Numeric-only runs are not stroke-checked**: the recording surface's stroke-vs-text rule applies to runs carrying
  a letter (the ladder numerals and tape graduations are marks every overlay crosses, and all six defects are word
  rows). Stated so the check's blind spot is explicit.
* **The stores header is absent at 800×480** (three readout rows are the compact form, P972 §4.5); the words
  `HULL/SHLD/ENG` identify the block there.
* **Text-vs-text** stays informational (the P1036 rule): the ladder numerals and the designator readouts can share a
  row of sky; captions and plate overlaps are 0 in all 74 scenes.
* **The lock ring can be clipped by a viewport edge** exactly like the native bracket (`radiusBracket` clipped to
  `distanceToEdge + c_flBorderSide`), because it is √2 larger than the designator.
* **New media** (40 PNGs + `jet-capture.json`, 1,945,334 bytes = 1.85 MB) needs Domingo's review before any push.
* The brief file `P1038B_BRIEF.md` is removed from the worktree root so it cannot enter the change set.

## Native intent

Native owns every value and every timing these widgets present: the six centre-HUD gauges (`dialog.mdl:2403-2506`),
the own-status icons (eye `dialog.mdl:2526-2534`, cloak `consoledata.cpp:563-575`, cloak strength `:480-489`,
autopilot `console.cpp:793`, vector lock `consoledata.cpp:465-475`, flag/artifact `console.cpp:847-863`, kill bonus
`:824-828`, signature `:865-866`), the gun/launcher ready semantics (`consoledata.cpp:1157-1202`,
`inventoryimage.cpp:299-346`), the warnings the caution panel aggregates (damage flash `trekigc.cpp:4270-4283`,
incoming missile `MISSILE_TRACK_WARNING(208)`, `ShipWarningMask`, critical tips `console.cpp:913-931`, death
`shipIGC.h:2465-2469`, pod oxygen, ripcord `dialog.mdl:5139-5154`, lives/kills `console.cpp:812-821`, vote
`dialog.mdl:2114-2120`), and the F4 inventory itself (`dialog.mdl:5385-5393` + the `inv*.mdl` columns and the native
title `dialog.mdl:1803-1807`). The lock cue keeps native's per-state size — `radiusBracket = 1.05 × radiusObject`
clipped to `distanceToEdge + c_flBorderSide` (`radarimage.cpp:302-319`) and `radiusBracket + 40·(1−lock)` for a
partial lock (`radarimage.cpp:325-332`) — and draws it as a segmented arc, which P972 §4.3 permits; native's
`DrawExpandedBlip` surfaces are drawn over the same flight symbology, so a cue chord over a ladder numeral is native
behaviour, not a Jet invention. The layout fixes change no value, no gate, no timer and no sound: they only place
the same readings where they can be read.

## Modern approach

The fixes live in the three pure presentation files plus the renderer partial, so they are headlessly testable: the
row pitch is a measured function (`JetStoresPanel.RowHeight`), the bars have named gaps (`BarGap`,
`ProgressBarGap`) and take a centre line, the icons have one size rule (`IconSize(CapHeight(lineHeight))`) reported
through an optional audit interface that costs nothing in production, the caution panel and the F4 pane compute
their width/padding from measured text, and the lock is one pure geometry family (`LockCueHalf`,
`LockRingRadius`, `LockRingSegment`) that keeps the native shrink and the shared clock. The proof measures the
result instead of trusting it: the recording surface now records strokes, plate draw order and icon boxes, and
reports the four new classes per scene, so "no bar strikes a word, no text leaves its plate or the screen, no icon
is a speck" is a fact in `jet-capture.json`. Classic and Modern remain pixel- and semantically identical, proven by
the 1,848-combination decoded-RGBA comparison against a baseline captured before the first edit.
