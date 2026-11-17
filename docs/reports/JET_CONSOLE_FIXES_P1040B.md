# P1040b — Jet HUD skin: 800×480 console fixes (lifepod drawn, hint slot, bottom-line scale/width, CMD wing cell)

Worktree `hermes/p1040b` of `AllegianceCore`, base commit `2363a118` (the P1040 merge). No commit, no push, no
stash, no remote host, no Docker (not installed here). Scratch in `.scratch/p1040b/` (deleted at the end); raw
captures outside the repo in `~/allegiance-evidence/P1040b/`; the report's assets are the PNGs + a trimmed
`jet-capture.json` under `docs/reports/JET_CONSOLE_FIXES_P1040B_assets/` (**1.4 MB**, inside the 2 MB cap).

**Status: DONE** — every item has its verdict, measured before/after and gate result. Written before the
implementation and updated after every item (the brief's order).

## Item status

| # | Item | Status |
|---|---|---|
| 0 | Report first, kept current | **DONE** (this file) |
| 1 | Lifepod block always drawn when `PodVisible` at 800×480; `POD OXY` leaves the caution stack only when the block was drawn; vote "never zero"; test | **DONE** (§2.1) |
| 2 | Controls hint in a stated clear slot; audit gap found and fixed; the rest of that class counted | **DONE** (§2.2, §4) |
| 3 | Bottom lines at the Jet scaled size with a band width that keeps the reading; tips wide/wrapped (≥ 24 chars) | **DONE** (§2.3) |
| 4 | CMD wing cell: every cell word inside its cell (two-axis fit), hit rectangles unchanged | **DONE** (§2.4) |
| 5 | Report describes what each image shows (measured boxes) | **DONE** (§6) |
| A | Baseline-first Classic/Modern zero-delta (1,848, 0 pixel / 0 semantic) | **DONE** (§1) |
| B | Audit extension: `ElementMissing` 0; hint/tips/bottom lines inside stroke-vs-text and text-overlap; every existing class 0 | **DONE** (§4) |
| C | Gate: 0 B/frame, new tests 3× alone, build 0/0 + `tools/build-tools.sh`, one fast tier | **DONE** (§7 — the one fast-tier failure is the ledgered pickup load flake, 3/3 alone) |
| D | Re-capture the console scenes at 1920×1080 and 800×480 dark+bright; 2× crops; every image opened | **DONE** (§6) |

## 1. Zero-delta baseline (Classic and Modern unchanged)

Baseline captured on the **unmodified tree** (commit `2363a118`, before any edit):
`hud2-proof --capture ~/allegiance-evidence/P1040b/baseline` → **1,848 combinations in 5.7 min** (924 Classic +
924 Modern, decoded-RGBA goldens + semantic snapshots). After the change the same binary was compared against it:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)
```

22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins = **1,848**. The only differences are the
fixture's wall-clock low-resource `!` badge inside its recorded animation mask (the P980/P1038-documented family,
83 of 1,848 entries; every entry reports `outside: 0`). Every change in this task is Jet-only code plus the
proof tool and tests, so Classic and Modern are pixel- and semantically identical.

## 2. Defects — before/after at 800×480 and 1920×1080

All boxes are px from the capture (`~/allegiance-evidence/P1040b/jet-final/jet-capture.json`; the trimmed copy in
the assets carries the same numbers). **Before** = the P1040 merge (`2363a118`), **after** = this change.

### 2.1 Lifepod panel missing at 800×480 (regression) — fixed

**Before** (`jet-podoxy-800x480-dark`): the console column was `121.3…273.3 × 228.7…245.3` — exactly **one row** —
and it held `SECTOR Alystra`. `LifepodBox` was `0×0`: no `LIFEPOD`, no `OXY nn s`, no segmented bar, no `RETURN`.
The caution stack had already dropped `POD OXY` (it was dropped on `model.PodVisible`, i.e. the *gate*, not on the
drawn result), so the oxygen endurance and the return binding were shown **nowhere** at that size (P1040 §6's
claim that the image showed the panel was wrong). At 1920×1080 the block was `182…770 × 465…540` (three rows).

**After** (`jet-podoxy-800x480-dark`):

| Box | Before | After |
|---|---|---|
| Chat pane | 121.3…352.7 × 101.3…223.3 (7 rows) | 121.3…352.7 × 101.3…**190.0** (5 rows — the log yields the block's rows) |
| Console column | 121.3…273.3 × 228.7…245.3 (1 row) | 121.3…273.3 × **195.3…245.3** (3 rows) |
| Lifepod block | `0×0` (not drawn) | **121.3…273.3 × 212.0…245.3** (2 rows, `LifepodBox` h 33.3) |
| Drawn column rows | `SECTOR Alystra` | `SECTOR Alystra`, `LIFEPOD OXY ▮ 95 s`, `RETURN Shift+Z / G...` |

* The 1080p frame is **unchanged**: chat pane 182…537 × 224…407, column 182…770 × 415…540, lifepod
  182…770 × 465…540, rows `SECTOR`, `MISSION T+ 02:05`, `LIFEPOD`, `OXY 95 s`, `RETURN Shift+Z / G AUTOPILOT TO BASE`
  (the pod keeps its native position directly under the rows because the band is deep enough).
* **Rule (stated).** The column reserves the `LIFEPOD` block *first* (`JetConsoleLayout.PodBlockHeight`), plus the
  column's own first ordinary row (sector/viewing), before the chat log takes its height
  (`JetConsoleLayout.ChatLinesWithReserve`); the ordinary rows are then limited by
  `rowsBudget = floor((band − block)/pitch)`, so the block is always drawn and the lowest-priority row
  (mission, then ACC, then QUE, then turret, then sector) is the element that gives way. The chat log is a
  scrolling native window with the OLDER marker, so a shorter window loses no reading.
* **Compact form (stated, two rows at h < 700):** row 1 = `LIFEPOD` + `OXY` + the segmented endurance bar + the
  numeric endurance right-aligned at the column edge; row 2 = `RETURN <binding>`. Standard frames keep the native
  three rows (`LIFEPOD` / `OXY ▮ nn s` / `RETURN …`).
* **One display per reading.** The caution panel now drops `POD OXY` only when `_lifepodDrawable` is true — the
  block the column reserved and drew this frame (`JetHudRenderer.cs:421` sets it before `DrawCaution`;
  `JetHudRenderer.StoresCaution.cs:468` reads it). If the band cannot hold the block the panel is not drawn and the
  caution line stays, so the endurance can never be zero-displayed (test 3).
* **Vote banner, same rule.** The ballot is drawn at both resolutions whenever the model carries it
  (`_painter.Drawn("vote")` at `JetHudRenderer.Console.cs:834`); the `ElementMissing` audit proves it (test 4).

### 2.2 Controls hint over the pitch ladder — fixed, and the audit gap closed

**Before** (`jet-training-800x480-dark`): `DrawControlHint` kept the shared renderer's *unscaled* anchor
`ChatPaneLayout.BottomFromTop + 6` → y = 132, centred (x ≈ 301…499 at 198 px wide). At 800×480 that row crosses
the ladder's `+20` rung (numerals `356.7…378.7 × 126…140.7`) and the chat pane (`121.3…352.7 × 101.3…223.3`): the
box intersects both. The layout audit still printed **0**.

**Why it read 0 (two halves of the same gap, both fixed in `tools/hud2-proof/Program.cs`):**

1. `IsInstrumentFurniture` listed `hint` and `training` as instrument furniture, and `TextOverlapCross` exempts
   furniture-vs-furniture pairs — so a hint over a ladder numeral was never counted. `hint` is removed; `training`
   stays, because its anchors are the shared native table (`indicator.cpp:461-701`) a skin may not move.
2. `stroke-vs-text` ignored every stroke drawn **before** the text (`if (stroke.Seq < text.Seq) continue;`). The
   ladder's rungs and the tape rails are painted first, so a label laid across them was invisible to that rule too.
   `IsFixedInstrumentStroke` + an ordered exception (`OpaquePlateBetween`) close it: a fixed instrument's stroke
   drawn before a run counts unless the run is that instrument's own mark, shared tutorial geometry, a
   world-anchored marker, or hidden behind a ≥ 240/255 backing plate.

**What else the same gap could have hidden:** every Jet-owned label over the ladder's rungs/tape rails — the cargo
and flight-parts rows, the tips, the vote banner, the console rows, the CMD caption, and the hint. Measured: the
extended stroke rule flags the F4 pane's own rows over the ladder rungs (25 candidate crossings across the three
`jet-f4` scenes) — the pane's 0.97 backing hides the rung, so those are correctly excluded by the plate rule;
after that exclusion the class is **0 in all 139 scenes**.

**After:** the hint takes the first CLEAR slot of `JetConsoleLayout.HintSlot`, tested against every obstacle the
frame registered (furniture, chat pane, console column, vote, tip stack, bottom rows, compose/CMD panels, tag):

| Frame | Before | After |
|---|---|---|
| `jet-training-800x480-dark` | x 301…499, y 132…146.7 (over the `+20` rung + chat pane) | **slot 0** — 301…499 × **374.7…389.3**, below the tip band, under the `PITCH 00` caption (touching its box edge only), `hintClear = true` |
| `jet-training-1920x1080-dark` | x 811.5…1108.5, y 132…152 | **slot 0** — 811.5…1108.5 × **976…998**, in the bottom band under the cargo row, above the tag, `hintClear = true` |

**Slot rule (stated).** The hint is a one-line tutorial band: 0 = below the ordinary tip stack (the bottom band's
free row), 1 = above the chat pane's marker row, 2 = the bottom-right band under the radar scope. Edges that merely
touch are not overlaps (`JetConsoleLayout.ClearanceTolerance` = 0.5 px). A hint that fits no slot is drawn at the
least-covered one and `hint-blocked` counts it (0 in 139 scenes).

### 2.3 Bottom lines oversized and truncated to nothing — fixed

**Before** (`jet-level-800x480-dark`, `jet-tips-800x480-dark`): the two shared bottom lines were drawn at the raw
**1.0** scale (not the Jet HUD's density scale) inside the console column's 152 px width. The result was
`WEAPONS: G...`, `CARGO 1/5 ...` and tips cut to `Welcome to the se...` (all larger than the rest of the Jet HUD).
At 1920×1080 the same lines were normal size (scale 1.0 there).

**After:** both rows are drawn in the Jet text role (`large: false`) at the Jet HUD's own density scale
(`ModernHudScale.Core`, 0.667 at 800×480, 1.0 at 1080p) and fitted to the **bottom band** — the console edge to the
viewport gutter, trimmed only by fixed furniture the row's own ink band crosses (`JetConsoleLayout.BandRight`) and,
in the command view, by the CMD panel's left edge:

| Row (800×480) | Before | After |
|---|---|---|
| Flight parts | `WEAPONS: G...` (scale 1.0, 152 px slot) | **`WEAPONS: GROUP  BOOST: ON  [1:Plasma]`** — ink 121.3…392.7 × 322…336.7 (271 px), at scale 0.667 |
| Cargo | `CARGO 1/5 ...` | **`CARGO 1/5  Ammo pack x3`** — ink 121.3…290 × 348…362.7 |
| Tips | `Welcome to the se...` (152 px) | **all three whole** — `Welcome to the sector` / `Alystra: 3 miners on duty` / `Payday: $500 deposited into your account balance`, block 121.3…473.3 × 372…424.7 (352 px), 3 rows |

At 1920×1080 the rows are 253 / 407 px of ink in the same positions as P1040 (`948`, `922`) and the tip block is
182…770 × 972…1044 — identical text, identical place.

**Wrap rule (stated).** A tip gets the full bottom band, not the console column. A tip that still does not fit is
split at the last word boundary that fits and continued on the next row at the same pitch (`JetTipWrapCache`,
cached by text/width/scale, 0 B on a steady frame); only a single unbreakable word is clipped, and then with the
ASCII ellipsis. No tip is clipped below 24 characters (test asserts the drawn head is a true prefix and the stack
is two rows tall).

### 2.4 CMD wing cell overflow — fixed

**Before** (`jet-cmd-800x480-dark`): the wing combo cell is the native `WingCombo` rectangle — panel + (216,8),
54×42 → screen **481…535 × 356…398**. The `WING` caption was fitted to the cell's *width* only and the name drawn
at `cellY + capHeight + 3`, i.e. from y 386 to ~405 — **7 px past the cell's bottom edge** (and into the verb bar's
top edge), which is the `DEL`/`TA` broken across the border in the review capture.

**After** (`CellBlock`, `JetHudRenderer.Console.cs:976`): both lines take ONE fit scale from the cell's inner box
(width **and** height), the block is centred vertically and each line centred horizontally; a word the 0.5
readability floor still cannot fit is abbreviated with the ASCII ellipsis. Measured: cell `481…535 × 356…398`,
`WING` at 489…527 × 358…377, `DELTA` at 483.8…532.2 × 377…396 — both inside. At 1080p: cell 1041…1095 × 956…998,
`WING` 1049…1087 × 958…977, `DELTA` 1043.8…1092.2 × 977…996. The cells are still the native
`CommandBarLayout` rectangles (`JetCommandConsole`), so draw and hit test cannot disagree; the proof counts
**cmd-cell leaks = 0** in all 7 CMD scenes (1920/800/3840, dark/bright, command view).

The other cells (`ME SECTOR TEAM ALL`, `ATK CAP DEF PIK GO REP JOIN MINE BLD`) now also fit in both axes
(`CellWord`), with the ellipsis as the last resort.

## 3. Changes (file:line)

Presentation only; no server, Core, wire, simulation, audio, input-binding, chat-routing, fan-out or
training-lesson change.

**`src/Allegiance.ClientCore.Game/Rendering/Jet/JetConsoleLayout.cs`**

| Line | Change |
|---|---|
| `:55-66` | `PodRowsStandard` (3) / `PodRowsCompact` (2) and `PodRows(h)` — the lifepod block's rows per height |
| `:137-171` | `PodBlockHeight` (the block's reserved rows) and `ChatLinesWithReserve` (the chat pane's drawn count once the block is reserved) |
| `:179-212` | `BandGap`, `BandRight` (the bottom band's right edge from the registered furniture) |
| `:216-268` | `HintSlot` (the three stated hint slots), `IsClear`/`Cover`/`ClearanceTolerance` (a slot is clear when no obstacle overlaps it by more than half a pixel) |

**`src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.Console.cs`**

| Line | Change |
|---|---|
| `:70-115` | New measured boxes read by the proof/tests: `LifepodDrawable`, `HintSlotIndex`, `HintBox`, `HintClear`, `TipBlock`, `CargoBox`, `FlightPartsBox`; the reused obstacle list |
| `:125-175` | `ChatPaneRect` reserves the lifepod block (+ the sector row + a compose panel) before the log takes its height |
| `:186-205` | `DrawConsoleJet` re-resolves the pane and the block reservation (a console-only call agrees with the HUD pass) |
| `:334-402` | `DrawBottomLines`/`BottomText`: Jet scale, measured bottom band, command-view bound; the boxes are recorded |
| `:403-500` | `DrawTips(model, scale, draw)`: full-band width, two-row wrap (`JetTipWrapCache`), the measured tip block |
| `:520-600` | `DrawConsoleColumn`: row budget above the reserved block, the block drawn directly under the rows (native order preserved at 1080p) |
| `:602-620` | `LifepodReserved` (does the block fit the band?) and `HasSectorRow` |
| `:622-682` | `DrawLifepodBlock`: compact two-row form / standard three-row form, the endurance right-aligned, the bar shrinking into the room, `Drawn("lifepod")` |
| `:715-790` | `DrawControlHint`: fitted to the slot, obstacle set, first clear slot, `HintClear`/`HintSlotIndex` |
| `:820-840` | `DrawVote`: reports `Drawn("vote")` |
| `:915-990` | `CellWord` (two-axis fit + ellipsis), `CellBlock` (the wing cell's two-line block), `FitScale(text, w, h)` |

**`src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.cs:410-422`** — the console boxes the caution
compaction reads (`_chatPane`, `_lifepodDrawable`) are resolved BEFORE `DrawCaution`.

**`src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.StoresCaution.cs:456-470,528-545`** — `POD OXY`
leaves the stack only when `_lifepodDrawable`; the panel and its Ghost/Missile rows report
`Drawn("caution"/"death"/"missile")`.

**`src/Allegiance.ClientCore.Game/Rendering/Jet/JetVectorPainter.cs:57-77,368-380`** — `IJetElementAudit.Drawn`
(the drawn-result channel) and its painter pass-through.

**`tools/hud2-proof/Program.cs`**

| Line | Change |
|---|---|
| `:470-610` | Capture: `hint`, `hintSlot`, `hintClear`, `tips`, `tipRows`, `cargo`, `flightParts`, `cargoTexts`, `lifepodDrawable`, `commandCells` (+ the wing cell's own runs), `elements { eligible, drawn, missing }`; the run now exits non-zero on any `element-missing`, `hint-blocked` or cell leak |
| `:690-800` | Audit totals `HUD2_JET_ELEMENTS` / `HUD2_JET_CONSOLE` |
| `:815-850` | `EligibleElements`: the scene model's gates (lifepod, death, master caution, missile, vote) restricted to the panel's own selected set |
| `:855-905` | `CommandCells` / `CellLeaks`: each cell word against its own native cell |
| `:2390-2405` | The recording surface's `Drawn` set + `Reset` |
| `:2625-2650` | stroke-vs-text: fixed-instrument strokes drawn earlier now count (the hint-over-the-rung class), with the opaque-plate and own-mark/world-marker exemptions |
| `:2895-2925` | `IsInstrumentFurniture` no longer lists `hint`; `IsFixedInstrumentStroke` and `OpaquePlateBetween` added |
| `:2992-3035` | `TextsInside`, `RunsInside`, `RunsWithText` (evidence helpers) |

**`tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetConsoleTests.cs`** — seven new tests and the reworked hint
test (§5); **`JetRadarTests.cs:558,601`** — the two recorders implement the new `Drawn` member (no-op).

## 4. Audit extension

* **`ElementMissing`** (`HUD2_JET_ELEMENTS element-missing 0`): for every scene the proof computes the set the
  model makes eligible — `lifepod` (`PodVisible`), `death` (`Pilot down / ghost` in the panel's selected set),
  `caution` (master caution with at least one selected row), `missile` (`MSL TRACK` selected) and `vote`
  (`VoteText`) — and compares it with the elements the renderer reported as **committed**
  (`IJetElementAudit.Drawn`). Eligibility respects the panel's own gate (`FlightVisible`) and its stated line limit
  (2 compact / 5 full), so the class is "what must be on screen" vs "what was drawn", never a claim about a line
  the panel legitimately does not show. **0 in 139 scenes** (P1040 would have failed `jet-podoxy-800x480-*:lifepod`
  — the regression this task fixes).
* **The hint is inside the text audits.** `hint` is no longer furniture for `TextOverlapCross`, and the
  fixed-instrument stroke rule now sees a run over a rung; a slot that fits nothing is counted as `hint-blocked`
  (**0**).
* **Tips and bottom lines** are ordinary text runs (elements `tips` / `cargo`), so they are inside
  `TextOverlapCross`, `TextOverlapSame`, stroke-vs-text, text-outside-plate and text-off-screen; their filled
  bands are measured, not assumed.
* **CMD cells**: every cell word is checked against its own native rectangle; **cmd-cell leaks 0** in all 7 CMD
  scenes.
* **Every existing class stays 0** in all 139 scenes: `stroke-vs-text 0`, `text-outside-plate 0`,
  `text-off-screen 0`, `icon-too-small 0`, `text-in-plate 0`, `cue-vs-readout 0`, `legible-behind-pane 0`,
  `text-overlap-cross 0`, `text-overlap-same 0`, radar `text-over-glyph 0`, `stroke-through-text 0`.

## 5. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetConsoleTests.cs` — **39 tests** (32 P1040 + 7 new; the
P1040 hint test rewritten), all headless through the recording `IJetVectorSurface`.

| New / changed test | What it pins |
|---|---|
| `The_lifepod_block_is_drawn_at_800x480_with_a_long_log_and_every_column_row` | at 800×480 with a 9-line log (marker row) and every column row (sector, turret, queued, accepted, mission) the block is drawn (`LifepodDrawable`, `LifepodBox.Height > 0`), `LIFEPOD`/`OXY`/`95 s`/`RETURN` are drawn inside the block above the stores top, the log yields rows, and the caution does not repeat `POD OXY` |
| `The_compact_lifepod_form_keeps_two_rows_and_the_standard_form_three` | 2 rows × pitch at 800×480, 3 rows at 1080p; the compact row carries identity + OXY + endurance |
| `A_lifepod_that_cannot_be_drawn_leaves_POD_OXY_in_the_caution_stack` | at 640×240 the block cannot fit → `LifepodDrawable` false, no `LIFEPOD`, and `POD OXY` stays in the caution stack (one display per reading, never zero) |
| `The_vote_banner_is_drawn_whenever_the_model_carries_a_ballot` | `VoteBox` non-empty at 800×480 and 1080p with a full caution stack; the stack's `VOTE` line is not drawn |
| `The_controls_hint_keeps_a_slot_clear_of_the_ladder_the_chat_pane_and_every_element` (rewritten) | at 800×480 and 1080p: `HintClear`, a valid slot index, no overlap with the chat pane or any furniture box, on screen, and the drawn run is the reported box |
| `The_bottom_lines_use_the_jet_scale_and_keep_their_reading` | the full `WEAPONS:` / `CARGO` strings are drawn (no ellipsis), the run height is `12·scale` (the Jet scale, not 1.0), inside the gutter |
| `A_tip_too_long_for_the_band_wraps_onto_a_second_row` | the whole tip is never drawn as one cut line; the drawn head is a true prefix of it; the stack is two rows tall |
| `Every_drawn_command_cell_word_stays_inside_its_cell` | every recipient/verb word, the `WING` caption and `DELTA` are inside their native cell rectangles in both axes |

Results: **39/39 ×3** alone (§7). The P1040 0 B/frame allocation test still passes (the new caches keep a steady
frame allocation-free; the obstacle list is reused and `CollectionsMarshal.AsSpan` does not allocate).

## 6. Screenshots (every cited image opened and read)

The assets (`docs/reports/JET_CONSOLE_FIXES_P1040B_assets/`, **1.4 MB**, 21 files = 16 PNGs + 4 crops + the JSON)
are lossless RGB re-encodes (`ffmpeg -pix_fmt rgb24 -pred mixed`, verified byte-identical decoded pixels) of
`~/allegiance-evidence/P1040b/jet-final/`. Each was opened and read; the small 800×480 frames were also read from
the 2× nearest-neighbour crops and checked against the measured boxes.

| Image | What is actually visible |
|---|---|
| `jet-podoxy-800x480-dark.png` / `-bright.png` | `HDG 045`; the chat pane with two bracketed lines on the left; then the column `SECTOR Alystra`, `LIFEPOD OXY ▮▮▮▮ 95 s`, `RETURN Shift+Z / G...`; the stores block (`HULL 82% SHLD 70%`, `OXY 63%`, `GUN 0%`, `MSL 2`, `AUTO VLOCK FLAG`); the ladder `+20/+10/00/PITCH 00`; `WEAPONS: GROUP BOOST: ON [1:Plasma]` and `CARGO 1/5 Ammo pack x3` at the bottom left, full. **The lifepod block is present** (it was absent in P1040's image). |
| `jet-podoxy-1920x1080-dark.png` / `-bright.png` | the same column with `SECTOR Alystra`, `MISSION T+ 02:05`, then `LIFEPOD`, `OXY` with the eight-segment bar and `95 s`, `RETURN Shift+Z / G AUTOPILOT TO BASE`; stores block, ladder, radar scope, full bottom lines. |
| `jet-training-800x480-dark.png` / `-bright.png` | the thin-vector roll callout (`PUSH Q AND E TO ROLL LEFT AND RIGHT`, `PUSH V TO TOGGLE MOUSE CONTROL`) and the centred `Shift+F1 opens the controls` hint in the bottom band **below** the tip band and `PITCH 00` — clear of the ladder numerals, the chat pane and the stores block. |
| `jet-training-1920x1080-dark.png` / `-bright.png` | the same callout at full density; the hint at the bottom centre (976…998) under the `CARGO` row, above the `JET / GREEN - WIP` tag. |
| `jet-tips-800x480-dark.png` / `-bright.png` | `MASTER CAUTION` top right with two critical tips; the vote banner above the ladder's top rung; `WEAPONS:`/`CARGO` and the three ordinary tips at the bottom left, all complete (`Welcome to the sector`, `Alystra: 3 miners on duty`, `Payday: $500 deposited into your account balance`). |
| `jet-tips-1920x1080-dark.png` / `-bright.png` | the same stack: two caution lines, the ballot, and three full tips (972…1044) over the two bottom lines. |
| `jet-cmd-800x480-dark.png` / `-bright.png` | the command view: `CMD` caption, the native 274×92 panel at 265…539 × 348…440 with `ME SECTOR TEAM ALL`, `WING` over `DELTA` inside the 54×42 combo cell, `ATK CAP DEF PIK GO REP JOIN MINE BLD`; the summary in the top centre; the sector/mission rows above. |
| `jet-cmd-1920x1080-dark.png` / `-bright.png` | the same panel (825…1099 × 948…1040) — `WING`/`DELTA` stacked inside the combo cell — and the three summary lines above (`COMMAND SHIP: Fig Adv`, `Hull 82% Shield 70% Speed 260 mps`, `RECIPIENTS: …`). |
| `console-column-2x.png` | 2× crop of the 800×480 column: `SECTOR Alystra` / `LIFEPOD OXY ........ 95 s` (the segmented bar reads as a dotted rule) / `RETURN Shift+Z / G...` — the endurance and the binding are inside the column, above the stores block. |
| `hint-slot-2x.png` | 2× crop around the 800×480 hint: `PITCH 00` above, `Shift+F1 opens the controls` on its own band below it, nothing crossing the ladder. |
| `cmd-wing-cell-2x.png` | 2× crop of the wing combo: `WING` over `DELTA`, both inside the cell's border. |
| `bottom-lines-2x.png` | 2× crop of the 800×480 bottom band: `WEAPONS: GROUP BOOST: ON [1:Plasma]`, `CARGO 1/5 Ammo pack x3`, `PITCH 00`, `Welcome to the sector`, `Alystra: 3 miners on duty` — no ellipsis anywhere. |

**Vision caveat (stated as the brief requires).** At 800×480 the read-back can misread individual glyphs (it read
the segmented bar as `------`, `HULL` as `WULL`, and once took the ladder numerals for tip lines); every position
claim above was therefore checked against the measured boxes in `jet-capture.json` and the 2× crops, and nothing is
described that the measured rectangles contradict. `RETURN Shift+Z / G...` is the *real* fitted form at 800×480
(the column is 152 px wide; the binding's keys are the leading tokens and are never cut).

## 7. Gate log

| Gate | Command | Result |
|---|---|---|
| Baseline (before any edit) | `hud2-proof --capture ~/allegiance-evidence/P1040b/baseline` | **1,848 combinations** (924 Classic + 924 Modern) in 5.7 min from the unmodified tree |
| Zero-delta | `hud2-proof --compare ~/allegiance-evidence/P1040b/baseline` | **`1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)`** |
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Tools build | `tools/build-tools.sh` | **build-tools: all 141 tool projects built** |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetConsoleTests` ×3 | **39/39** on each of three runs (501 / 452 / 464 ms) |
| Skin suite 3× alone | `dotnet test --filter FullyQualifiedName~Skins` ×3 | **394/394** on each of three runs (28 / 27 / 27 s) |
| Jet evidence capture | `hud2-proof --jet ~/allegiance-evidence/P1040b/jet-final` | **139 scenes**; audit `stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0, text-overlap-same 0`; radar `text-over-glyph 0, stroke-through-text 0`; **`element-missing 0`, `hint-blocked 0`, `cmd-cell leaks 0`** |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **`tiers-20261117T122839.242435Z`: 15,105 passed / 7 not executed / 1 failed, 558.8 s**, 9/9 infrastructure checks pass, 5 executions over the calibrated 11.529 s limit (all passed, timing only). The one failure is the **already-ledgered** `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` (pickup-acquire touch/physics receipt, `docs/TEST_FLAKES.md` "load-flake"; the runner's own quarantine retry also passed it), and it passes **3/3 alone** here (406 / 428 / 357 ms) — a load flake, no P1040b file involved. `Allegiance.ClientCore.MonoGame.Tests` 5,806 passed / 4 not executed, `Allegiance.ClientCore.Tests` 2,449 passed, Core 6,551 / 3 not executed, Lobby 209, Update 55, Browser 12 + 23. The P1039/P1040 background-transport crash did not recur. |
| Glossary links | `python3 tools/check-glossary-links.py` | **GLOSSARY.md link check OK (941 distinct paths)** |
| Parity scorecard | `python3 scripts/parity-scorecard.py` | clean, unchanged (no new matrix rows: this task ports no rule, only Jet drawing) |
| AOT publish | not run — no `src/Allegiance.Server` / `src/Allegiance.Core` change | n/a |
| `docker compose config` | not run — no service/port/store/env change | n/a |

**Training gate.** No training-affecting file outside `Rendering/Jet/` changed: the lesson logic
(`TrainingController`, `TrainingIntegration`, overlay ids) is untouched, and the changed non-Jet files are the
proof tool, the tests and the Jet painter's audit channel. `tools/run-training-tests.sh` is therefore not required
by the lane rule; the training-restore tests in `JetConsoleTests` still pass (394/394 ×3 Skins).

## 8. Anything unfinished / caveats

* **`RETURN` at 800×480** prints `Shift+Z / G...` — the 152 px column cannot hold the whole native instruction
  (`Shift+Z / G AUTOPILOT TO BASE`); the *keys* (the binding) are the leading tokens and are never cut. Stated, not
  an omission.
* **The chat log yields rows in a pod at 800×480** (7 → 5 rows in `jet-podoxy`): the block's reservation is the
  only way to keep a survival-critical panel on a 480 px frame; the log is a scrolling native window.
* **The 800×480 images all change** (the bottom lines/tips now use the Jet scale — that is defect 3); at 1920×1080
  the only changed frames are `jet-training` (the hint moved by design) and `jet-cmd`/`jet-commandview` (the wing
  cell now fits inside its cell). Every other 1080p scene is byte-identical to P1040.
* **`ElementMissing` eligibility** follows the panel's own selection (priority order + line limit), so it never
  demands a line the panel legitimately does not show; the state it guards (a critical element gated on but not
  drawn) is exactly the P1040 defect.
* **New media** (21 files, 1.4 MB) needs Domingo's review before any push, per the lane rules.
* The brief file `P1040B_BRIEF.md` was **removed from the worktree root** (read first, then deleted), so it cannot
  enter the change set; `git status` lists only the eight edited files, the report and its assets.

## Native intent

Native owns every reading the Jet console draws. The eject-pod panel is the `EjectPodImage` identity with its
`GetEndurance` gauge (`dialog.mdl:1922-1996,5436`, `consoledata.cpp:534-545`) — an adrift pod's oxygen endurance and
its return binding are survival-critical, so they outrank the sector/order rows. The chat list is the native
`ChatListPane` (355 px, `8 + itemHeight·NumChatLines`, hidden scrollbar, `CommPane SideTop(-160,-126)`,
`dialog.mdl:180`, `chatpane.cpp:374-380`) — a scrolling window, so losing rows to a critical panel loses no
reading. The P949 controls hint is a one-time tutorial band at a shared anchor (`ChatPaneLayout.BottomFromTop`),
and the tips are the three retained ordinary slots with their side colour (`dialog.mdl:5347-5349`,
`consoledata.cpp:99-107`); both are *labels*, not instruments, so they must never sit on the ladder. The cargo and
flight-parts rows are native fixed offsets (`Game1.InFlightUtility.cs:169`, `Game1.FlightParts.cs:324`) whose text
belongs to the HUD font, not to a larger face. The command bar is the native `CommandPane` (`dialog.mdl:73-87`)
whose `WingCombo` is a 54×42 cell over the verb bar; every cell word is drawn inside its own rectangle and the
picker tests the same rectangles in the order wing → verb → recipient (`console.cpp:1134-1192,1264-1278`).
Native 2-D conventions still apply: y-up anchors negated into the port's y-down space, `StringImage` hanging below
its point, group paint order.

## Modern approach

The fix is pure Jet composition over the same shared readings: one layout module owns the rules
(`JetConsoleLayout`: the lifepod block's reserved rows and the chat-line cap, the measured bottom band, the three
hint slots and their half-pixel clearance), the partial renderer only reads the model, and every "drawn" claim is
reported through an audit channel rather than asserted (`IJetElementAudit.Drawn`). The column is prioritised, not
nudged: the critical block is reserved first, the ordinary rows take the budget above it, and the lowest-priority
row is the one that gives way. Band labels are placed by a measured slot search against every obstacle the frame
registered, so an element can never silently land on the ladder again; and the layout audit now counts that class
(fixed-instrument strokes drawn before a label, and a label over instrument furniture) while excluding the pane's
own opaque backing and the native world-anchored markers. Every fitted string is cached per (source, width, scale)
and the tip wrap per (text, width, scale), so a steady frame still allocates 0 B; Classic/Modern stay pixel- and
semantically identical over the full 1,848-combination matrix, and the Jet layout audit is 0 in all 139 scenes
including the new `element-missing`, `hint-blocked` and CMD-cell leak counts.
