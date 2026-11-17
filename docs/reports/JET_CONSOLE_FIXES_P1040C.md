# P1040c — Jet HUD skin: console residuals (CMD wing word struck by grid strokes, command-view bottom line, training callout over the ladder)

Worktree `hermes/p1040c` of `AllegianceCore`, base commit `ec7b58b5` (the P1040b merge). No commit, no push, no
stash, no remote host, no Docker (not installed here). Scratch in `.scratch/p1040c/` (deleted at the end); raw
captures outside the repo in `~/allegiance-evidence/P1040c/`; the report's assets are the PNGs + a trimmed
`jet-capture.json` under `docs/reports/JET_CONSOLE_FIXES_P1040C_assets/` (≤ 2 MB).

**Status: DONE** — every item has its verdict, measured before/after and gate result (§7).

## Item status

| # | Item | Status |
|---|---|---|
| 0 | Report first, kept current | **DONE** (this file) |
| 1 | `DELTA` struck by the verb-bar grid: wing cell backing plate in the native paint order, hit rectangles unchanged, audit class counts it | **DONE** (§2.1) |
| 2 | Command-view bottom line keeps the selected weapon + boost state; stated compact form; `CARGO` checked | **DONE** (§2.2) |
| 3 | Training callout over the ladder: Jet's ladder yields; new `callout-crossed` audit class | **DONE** (§2.3) |
| 4 | `LIFEPOD OXY` field gap at 800×480 | **DONE** (§2.4) |
| 5 | Report describes what each image shows (measured boxes + pixel measures) | **DONE** (§6) |
| A | Baseline-first Classic/Modern zero-delta (1,848, 0 pixel / 0 semantic) | **DONE** (§1) |
| B | Audit: new classes count the P1040b frames as failures, 0 after; every existing class 0 | **DONE** (§4) |
| C | Gate: 0 B/frame, new tests 3× alone, build 0/0 + `tools/build-tools.sh`, one fast tier | **DONE** (§7; the fast tier's four failures are ledgered load flakes, 3/3 alone) |
| D | Re-capture CMD, command-view and every training scene at 1920×1080 / 800×480 dark+bright; 2× crops; every image opened | **DONE** (§6) |

## 1. Zero-delta baseline (Classic and Modern unchanged)

Baseline captured on the **unmodified tree** at `ec7b58b5` (13:02–13:08 UTC, before the first Jet edit at
13:21 UTC): `hud2-proof --capture ~/allegiance-evidence/P1040c/baseline` → **1,848 combinations** (924 Classic +
924 Modern, decoded-RGBA goldens + semantic snapshots; the manifest's `dirty` flag is `git status --porcelain`
non-empty, which counted the untracked brief/report files present then — no tracked file had been modified).
The capture's `HUD2_FINDING` overlap lines for Classic scenes are the pre-existing informational candidates the
harness prints, not failures. After the change the same binary was compared against it:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)
```

The only differences are the fixture's wall-clock low-resource `!` badge inside its recorded animation mask (the
P980/P1038-documented family, 83 of 1,848 entries — the same count P1040b recorded; every entry reports
identical outside the mask). Every change in this task is Jet-only code plus the proof tool and tests, so Classic
and Modern are pixel- and semantically identical.

## 2. Defects — before/after

All boxes are px from the captures (`~/allegiance-evidence/P1040c/jet-before/jet-capture.json` = the P1040b
geometry with the new audit classes; `.../jet-final3/jet-capture.json` = after). **Before** = the P1040b merge
(`ec7b58b5`, captured by reverting the four Jet renderer files to `HEAD` and rebuilding the proof tool),
**after** = this change.

### 2.1 `DELTA` struck by the verb-bar grid — fixed

**The geometry (native, unchanged).** `WingCombo` is a 54×42 cell at panel + (216,8) and `VerbsBB` is a
268×54 bar at panel + (2,38) (`dialog.mdl:38-42,73-87`; `Hud10Layouts.cs:184-199`), so the combo's lower 12 px
sit inside the verb bar's band and the last verb column (MINE|BLD divider at panel-local x 244) runs through it.
At 800×480 the cell is screen **481…535 × 356…398**, the bar's top edge is at y **386** and the divider at x
**509**.

**What the before image shows (measured).** `DELTA`'s run is **483.8…532.2 × 377…396**, inside the cell, but
three cell-frame strokes cross its ink band: the two verb-bar top-edge segments `479,386→509,386` and
`509,386→535,386`, and the MINE|BLD divider `509,386→509,440`. In the 800×480 frame the row **y = 385** carries
green ink in **30** columns of the cell (the line); after the fix only **13** remain (the letters' own strokes),
and the cell's interior paints at plate **(5,17,12)** instead of the panel's **(15,27,29)** — i.e. the cell now
has its own solid backing. The P1040b `cmd-cell-leak` check stayed 0 (the word was already inside its cell) and
`stroke-vs-text` read 0 (see §4 for why).

**Fix — the stated option: an opaque backing plate under the wing cell, in the native paint order.** Native
paints `CommandBackground`, `RecipientsBar`, `VerbsBar` and **then** `WingCombo` (`Game1.Commanding.cs:355-382`),
so the combo's art covers the verb bar in the overlap. Jet now does the same: the recipient and verb frames are
drawn first, then `FillPlate(WingCell, 0.97)` and the wing cell's own frame, then all cell words
(`JetHudRenderer.Console.cs:916-1005`). 0.97 is the near-opaque step the layout audit accepts as a plate (tint
alpha ≥ 240/255, the same threshold as the F4 pane's backing), so the strokes are genuinely interrupted rather
than merely redrawn under the ink. The alternative (not drawing the verb-bar strokes inside the wing cell) was
rejected: it leaves a gap in the bar's frame at the overlap and does not reproduce the native paint order.

**Hit rectangles stay native.** The drawn cell is still exactly `CommandBarLayout.WingLeft/Top/Width/Height`
(the test asserts it), and `JetCommandConsole.HitTest` keeps the production precedence **wing → verb →
recipient** (`Game1.Commanding.cs:399-437`; the console's event targets are registered in that order,
`console.cpp:748-763`, with the later-painted combo on top). In the overlap (panel-local x 244…270, y 38…50) a
click resolves to **WING**, not the verb — a new test clicks a point inside both rectangles and compares Jet's
control with the production order and the action word (`wing command`).

### 2.2 Command-view bottom line cut — fixed

**Before** (`jet-cmd-800x480-dark`): the CMD panel bounds the bottom band (panel left 265 − 8·scale ≈ 259.7), so
the flight-parts row had 203.7 px from x 56 — less than the full line's 271.3 px — and read
**`WEAPONS: GROUP  BOOST: O...`** (198 px of ellipsis-fit ink): the selected weapon (`[1:Plasma]`) and the boost
state were lost. The same row in the non-command 800×480 scenes was cut to
`WEAPONS: GROUP  BOOST:...` whenever furniture (the ladder numerals) trimmed its band.

**After — stated compact forms** (`JetBottomLines`, `JetHudRenderer.Console.cs:414-447`): the row takes its whole
shared line if it fits; otherwise it takes its compact form, built **from the same shared line** (nothing is
invented, and a line that is not the shared shape is returned unchanged):

| Row | Form | At 800×480 |
|---|---|---|
| Flight parts | `WPN <GROUP\|SINGLE n> [<mount>:<weapon>] BST <ON\|OFF>` — group, selected weapon and boost state; the later mounts and the `PART:` selection give way | **`WPN GROUP [1:Plasma] BST ON`** — ink 56…254 × 322…336.7 (198 px), inside the 203.7 px band |
| Flight parts, when even that cannot fit (the ladder trims the band to 183 px in `jet-pitched`) | the same readings without the row's own `WPN` label | **`GROUP [1:Plasma] BST ON`** — ink 121.3…290 × 322…336.7 (168.7 px) |
| Cargo | the hold OCCUPANCY head + the first detail segment, its trailing `xN` amount dropped; the selection prompt gives way (occupancy only if needed) | **`CARGO 1/5  Ammo pack x3`** — ink 56…224.7 × 348…363 (168.7 px), whole |

At 1920×1080 both rows are byte-identical to P1040b (`WEAPONS: GROUP  BOOST: ON  [1:Plasma]`, 407 px;
`CARGO 1/5  Ammo pack x3`, 253 px). No word is cut: the observed drawn strings contain `GROUP`, `[1:Plasma]`
and `BST ON` whole, and no `...`.

### 2.3 Training callout over the pitch ladder at 1920×1080 — fixed

**Before** (`jet-training-1920x1080-dark`): the shared callout's measured block is **855…1240 × 686…734**. The
ladder's `-20` rung numerals at **877…910 × 689…711** and **1010…1043 × 689…711** (`ladder:-20`, samples in
`jet-capture.json`) sit inside it — the numerals are struck by `PUSH V TO TOGGLE MOUSE CONTROL`. The audit read 0
because the pair callout-vs-ladder was exempt from both sides ("fixed instrument furniture"), and the rungs are
drawn before the callout's run. The `CUR` word named in the brief is the order-arrow label (`CurrentLabel`,
`JetHudRenderer.cs:103`, a world-anchored marker exempt from moving): measured, its box sits just above the
callout's ink band and is **not** crossed; the counted crossings are the two `-20` rung numerals.

**Fix — the stated rule: while a training callout is up, Jet's ladder yields.** The callout anchor is native
(`HudTrainingCallout`, indicator.cpp:634-641) and does not move; the ladder is Jet's own furniture, so any rung,
rung numeral or the `PITCH` caption whose ink would cross the callout's measured block is **not drawn that
frame** (`JetHudRenderer.cs:600-700`, `TryTrainingCallout` at `JetHudRenderer.Console.cs:1060-1120`). The block is
inflated by each mark's full ink width (line + keyline allowance), the same measure the audit counts a crossing
with, so nothing that grazes the block survives. The chosen option is *withdraw the Jet furniture*, not *put a
plate under the callout*: a plate would also blank whatever non-ladder content (chat text) lies under the block at
the smaller window, while withdrawing only Jet's own marks leaves every other reading intact. Every training
scene at every resolution is checked (§5): at 800×480 the callout lands below the ladder and nothing is
withdrawn.

**After:** at 1920×1080 the callout's two lines are intact with no ladder numeral or rung across them, and the
ladder keeps every rung/numeral that does not meet the block (the test proves the withdrawn set is exactly the
crossing set). 800×480 training frames are byte-identical to P1040b.

### 2.4 `LIFEPOD OXY` at 800×480 — fixed

**Before** (`jet-podoxy-800x480-dark`): the compact pod row drew `LIFEPOD` and `OXY` with the single 5 px unit
word gap, which at the 0.667 density scale is 3.3 px; measured ink runs in the row: `LIFEPOD` 122…172, gap
**173…175 (3 px)**, `OXY` 176…198 — they touch.

**After — computed:**

| Row | Before | After |
|---|---|---|
| Compact pod row | `LIFEPOD` 121.3…172.6, `OXY` at 176 (3 px ink gap) | `LIFEPOD` 121.3…172.6, **`OXY` at 179.3** — ink gap **6 px**; `95 s` at 244…273.3, `RETURN` / `Shift+Z / G...` on the next row |

**Rule (stated).** The identity word and the `OXY` field are two FIELDS sharing one row in the compact form, so
they are separated by `JetConsoleLayout.PodFieldGap` = two word gaps (10 units) — at 800×480 that is 6.7 px,
enough for a clear pixel column between the ink. Only the lifepod row's field gap changes; every other row keeps
the single word gap (no other frame moves).

## 3. Changes (file:line)

Presentation only; no server, Core, wire, simulation, audio, input-binding, chat-routing, fan-out or
training-lesson change.

| File | Line | Change |
|---|---|---|
| `Rendering/Jet/JetHudRenderer.Console.cs` | `59-66` | `WingCellPlateOpacity` (0.97) with the native paint-order rule |
| | `100-103` | `TrainingCalloutBox` (the measured callout block, for the proof) |
| | `389-465` | `DrawBottomLines`/`BottomText`/`Compact`/`CommandBandLeft`: bounded-band compact form (full → compact → narrow → ellipsis), cached by (line, kind, label) |
| | `916-1005` | `DrawCommandBar`: cell frames in native paint order, wing cell plate + frame, then all words |
| | `1115-1195` | `TryTrainingCallout` + `TrainingCalloutGeometry`: the callout geometry resolved without drawing (one source for draw and the ladder's yield test) |
| `Rendering/Jet/JetHudRenderer.cs` | `366` | `_trainingBox = default` per frame |
| | `383-387` | the callout block resolved BEFORE the ladder |
| | `584-712` | `DrawPitchLadder`: rungs, numerals and the `PITCH` caption that meet the callout block are withdrawn; `CalloutAvoid` |
| `Rendering/Jet/JetConsoleLayout.cs` | `71-82` | `PodFieldGap` / `PodFieldGapAt` (the compact pod row's field gap) |
| `Rendering/Jet/JetFlightGeometry.cs` | `258-300` | `SegmentHitsBox` (Liang–Barsky slab clip, the same test the audit uses) |
| `Rendering/Jet/JetBottomLines.cs` | new | the stated compact forms for the two bottom rows |
| `tools/hud2-proof/Program.cs` | `521-532` | the roll lesson on the bright backdrop (every training scene dark+bright) |
| | `723-736` | capture: `callout`, `podRows`, `flightPartsTexts` |
| | `2650-2665` | `stroke-vs-text`: the CMD element's own frame strokes are counted (the own-element exemption no longer hides a grid stroke) |
| | `2688-2726` | new `callout-crossed` class (Jet furniture strokes/runs across a `training` run's ink band, plate-hidden strokes exempt) |
| | `2963-2990` | `IsJetFurniture`, `IsGridElement`; `cmd` added to `IsFixedInstrumentStroke` |
| | `3291-3297` | the audit record carries `CalloutCrossed` |
| | `795-828` | aggregate print + non-zero exit for `callout-crossed` |
| tests `Skins/JetConsoleTests.cs` | `669-905` | five new tests; the recorder now stamps element + order and records fills |
| `docs/TEST_FLAKES.md` | top | P1040c gate observations (the four Fast-tier cases, 3/3 alone) |

## 4. Audit extension

* **`stroke-vs-text` counts the CMD grid (why P1040b missed it).** Two halves: (a) the CMD panel announces ONE
  element (`cmd`) for its cell frames and its words, and the ordered rule exempted a stroke that belongs to the
  run's own element (`if (stroke.Element == text.Element) continue`); (b) `cmd` was not a *fixed instrument*
  stroke, so a stroke drawn before the run was skipped anyway (the frames are pass 1, the words pass 2). The fix
  states that `cmd`'s strokes are a GRID, never a run's own mark (`IsGridElement`) and adds `cmd` to
  `IsFixedInstrumentStroke`. **The class counts the P1040b frames as failures: 21 hits over 7 CMD scenes**
  (`jet-cmd` 1920 dark/bright, 800 dark/bright, 3840, `jet-commandview` 1920, 800 — 3 crossings of `DELTA`
  each: the two verb-bar top-edge segments and the MINE|BLD divider). **0 after** (§7). The near-opaque wing-cell
  plate is what exempts the crossing strokes after the fix (the same plate rule the F4 pane uses).
* **`callout-crossed` (new class).** For every `training` run's ink band it counts (a) any Jet furniture stroke
  (`ladder`/`hdg`/`spd`/`thr`/`radar`) that crosses it, unless a near-opaque plate drawn after the stroke and
  before the run hides it, and (b) any Jet furniture run whose box intersects it. The callout's own position stays
  exempt (its anchor is native), but Jet's furniture is not: **the class counts the P1040b frames as failures: 4
  hits over 2 training scenes** (the `-20` numerals, left and right, at 1920×1080 dark and bright). **0 after**
  in all 141 scenes.
* **Every existing class stays 0** in all 141 scenes: `stroke-vs-text 0`, `text-outside-plate 0`,
  `text-off-screen 0`, `icon-too-small 0`, `text-in-plate 0`, `cue-vs-readout 0`, `legible-behind-pane 0`,
  `text-overlap-cross 0`, `text-overlap-same 0`, radar `text-over-glyph 0`, `stroke-through-text 0`,
  `element-missing 0`, `hint-blocked 0`, cmd-cell leaks 0 (`HUD2_JET_ELEMENTS`/`HUD2_JET_CONSOLE` lines).

## 5. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetConsoleTests.cs` — **44 tests** (39 P1040/P1040b + 5 new).

| New test | What it pins |
|---|---|
| `The_wing_combo_plate_is_drawn_between_the_verb_bar_strokes_and_its_words` | the native overlap is real (cmd strokes cross the wing cell's interior); one ≥240/255 plate covers the cell, drawn after every crossing stroke and before `DELTA`; the cell is still the native `CommandBarLayout` rectangle |
| `A_click_in_the_wing_combo_overlap_resolves_to_the_wing_in_the_native_order` | a point inside BOTH the wing combo and the last verb column resolves to `Wing` in the Jet geometry and in the production order (wing → verb → recipient) |
| `The_command_view_bottom_rows_keep_the_selected_weapon_and_the_boost_state` | at 800×480 in the command view the flight-parts row carries `GROUP`, `[1:Plasma]` and the boost state whole (no ellipsis) and stays left of the CMD panel; the cargo row keeps its occupancy and item |
| `A_training_callout_withdraws_only_the_ladder_furniture_it_would_cross` | for all 23 overlay ids × 1920×1080 and 800×480: no ladder stroke or ladder run crosses the callout block, and the withdrawn set is exactly the set that crosses it (no other rung disappears) |
| `The_lifepod_compact_row_separates_LIFEPOD_and_OXY_by_the_word_gap` | the pod block's own rows at 800×480 keep the field gap (`PodFieldGapAt`) between `LIFEPOD` and `OXY`, and still fit the column |

The P1040 "0 B/frame" allocation test still passes (the compact cache, the callout geometry and the ladder's
yield test are all allocation-free on a steady frame).

## 6. Screenshots (every cited image opened and read)

The assets (`docs/reports/JET_CONSOLE_FIXES_P1040C_assets/`, 25 files, 1,401,226 bytes) are lossless RGB
re-encodes of `~/allegiance-evidence/P1040c/jet-final3/` (the frames were encoded from the pixel-identical
`jet-final2` capture; every decoded pixel was verified identical) plus 2× nearest-neighbour crops
(`ffmpeg … scale=…:flags=neighbor`), the before-fix crops from `.../jet-before/`, and a trimmed `jet-capture.json`
holding the 16 cited scenes.
Every full frame and every crop listed below was opened and read; where the read-back misread glyphs the claim was
checked against the measured boxes / pixel measures in `jet-capture.json` (the lane rule).

**Which frames moved (sha256 decode of the PNGs, before vs after).** 127 of the 141 scenes are **byte-identical**
to P1040b; exactly **14** changed, and each change is confined to the defect's own box:

| Scene | Pixels changed | Bounding box | Why |
|---|---|---|---|
| `jet-cmd-1920x1080-dark` / `-bright` | 1,482 / 1,536 | 1041,957…1094,997 | the wing cell's plate (the native 54×42 cell) |
| `jet-cmd-3840x2160-dark` | 1,482 | 2001,2037…2054,2077 | same cell at 4K |
| `jet-commandview-1920x1080-dark` | 1,482 | 1041,957…1094,997 | same cell |
| `jet-cmd-800x480-dark` / `-bright` | 3,234 / 3,302 | 64,324…534,397 | the cell's plate + the compact flight-parts row |
| `jet-commandview-800x480-dark` | 3,234 | 64,324…534,397 | same |
| `jet-training-1920x1080-dark` / `-bright` | 559 / 559 | 879,692…1042,707 | the two `-20` numerals withdrawn from the callout block |
| `jet-podoxy-800x480-dark` / `-bright`, `jet-pod-800x480-dark` | 301 | 176,214…235,224 | the pod row's field gap (`OXY` +3 px right) |
| `jet-lifepod-800x480-dark` | 2,102 | 129,214…324,336 | pod row gap + the compact flight-parts row |
| `jet-pitched-800x480-dark` | 1,718 | 120,324…302,336 | the compact flight-parts row (band trimmed to 183 px by the ladder) |

| Image | What is actually visible (checked against `jet-capture.json`) |
|---|---|
| `jet-cmd-800x480-dark.png` / `-bright.png` | The command view: `CMD` caption, the 274×92 panel at 265…539 × 348…440 with `ME SECTOR TEAM ALL`, the wing combo cell 481…535 × 356…398 now a **darker solid cell** holding `WING` (489…527 × 358…377) and `DELTA` (483.8…532.2 × 377…396) with **no line across the letters** (row y=385: 13 green columns vs 30 before); `ATK CAP DEF PIK GO REP JOIN MINE BLD`; the bottom-left rows read `WPN GROUP [1:Plasma] BST ON` and `CARGO 1/5  Ammo pack x3`; the sector/mission rows above the panel. |
| `jet-cmd-1920x1080-dark.png` / `-bright.png` | The same panel at 825…1099 × 948…1040, the cell 1041…1095 × 956…998 (`WING` 1049…1087 × 958…977, `DELTA` 1043.8…1092.2 × 977…996) on its plate; the three command summary lines above; the bottom rows are the full `WEAPONS: GROUP  BOOST: ON  [1:Plasma]` / `CARGO 1/5  Ammo pack x3` (407 / 253 px). |
| `jet-commandview-1920x1080-dark.png`, `jet-commandview-800x480-dark.png` | The same panel/cell (command view with the first slide step); the 800×480 frame carries the compact `WPN GROUP [1:Plasma] BST ON` row under the panel's left edge. |
| `jet-training-1920x1080-dark.png` / `-bright.png` | The shared callout `PUSH Q AND E TO ROLL LEFT AND RIGHT` / `PUSH V TO TOGGLE MOUSE CONTROL` at its native anchor (block 855…1240 × 686…734); **no ladder numeral or rung inside that block** (the `-20` numerals that sat at 877…910 × 689…711 and 1010…1043 × 689…711 are not drawn), while the ladder keeps every other rung and numeral; the controls hint `Shift+F1 opens the controls` at the bottom centre (811.5…1108.5 × 976…998). |
| `jet-training-800x480-dark.png` / `-bright.png` | The same callout (its block sits below the ladder at this size, so nothing is withdrawn here — byte-identical to P1040b); the `+20/+10/00/-10` numerals and `PITCH 00` intact; the hint in its clear slot below the tip band. |
| `jet-rollcue-1920x1080-dark.png` / `-bright.png`, `jet-rollcue-800x480-dark.png` / `-bright.png` | The roll lesson (banked −28°): the ladder rolled with its numerals, the roll pointer above the top rung, **no training callout** (overlay 0) — the yield rule is inert, frames byte-identical to P1040b (the two bright variants are new evidence captures of the same frame). |
| `jet-podoxy-800x480-dark.png` / `-bright.png` | `SECTOR Alystra`, then `LIFEPOD` (121.3…172.6) with a **6 px ink gap** to `OXY` (179.3…202), `95 s` right-aligned at 244…273.3, `RETURN Shift+Z / G...` below; the stores block, ladder and the full bottom rows unchanged. |
| `cmd-wing-cell-before-800-2x.png` / `cmd-wing-cell-800-2x.png` | 2× of the 800×480 cell: before, the verb bar's top edge and the MINE|BLD divider run through `DELTA`; after, the cell is a solid plate and the letters are clean. |
| `cmd-wing-cell-1920-2x.png` | 2× of the 1080p cell after the fix: `WING` over `DELTA` inside the bordered darker cell. |
| `cmd-bottom-band-before-800-2x.png` / `cmd-bottom-band-800-2x.png` | 2× of the 800×480 command-view bottom band: before `WEAPONS: GROUP  BOOST: O...`; after `WPN GROUP [1:Plasma] BST ON` and `CARGO 1/5  Ammo pack x3`. |
| `callout-ladder-before-1920-2x.png` / `callout-ladder-1920-2x.png` | 2× of the 1080p callout area: before, the `-20` numerals sit in the callout's line; after, the words are alone in the block with no numeral beside them. |
| `pod-row-800-2x.png` | 2× of the 800×480 pod rows: `LIFEPOD OXY 95 s` with the field gap, `RETURN Shift+Z / G...`. |

**Vision caveat (as the lane rule requires).** The read-back misreads small glyphs (it read `BOOST: O...` as
`BOOST: 0...`, took `WING DELTA`'s plate for "a black background", and could not resolve the 6 px pod gap); every
position claim above is therefore taken from the measured boxes in `jet-capture.json` and the pixel measures in
this section, not from the read-back. Two read-backs (the before wing-cell crop and the before callout crop) did
**not** see the strike the review reported; the pixel measures do (row y=385: 30 green columns before / 13 after;
the numeral rects at 877…910 and 1010…1043 × 689…711 lose their numeral ink), so the report states the strike
from the measurement, not from the read-back. The `jet-rollcue-800x480-bright` read-back returned an unrelated
description of a different game's HUD; that frame's claims are the geometry's (overlay 0 → no callout; the same
ladder code path and model as the `jet-rollcue-800x480-dark` frame, which reads correctly and is byte-identical
to P1040b), and its own pixels differ from the dark variant only in the backdrop and the ink composited over it.

## 7. Gate log

| Gate | Command | Result |
|---|---|---|
| Baseline (before any edit) | `hud2-proof --capture ~/allegiance-evidence/P1040c/baseline` | **1,848 combinations** (924 Classic + 924 Modern) from the unmodified tree |
| Zero-delta | `hud2-proof --compare ~/allegiance-evidence/P1040c/baseline` | **`1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)`** |
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (23.9 s) |
| Tools build | `tools/build-tools.sh` | **build-tools: all 141 tool projects built** |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetConsoleTests` ×3 | **44/44** on each of three runs (486 / 495 / 515 ms) — includes the P1040 0 B/frame allocation test |
| Skin suite 3× alone | `dotnet test --filter FullyQualifiedName~Skins` ×3 | **399/399** on each of three runs (27 / 28 / 42 s) |
| Jet evidence capture | `hud2-proof --jet ~/allegiance-evidence/P1040c/jet-final3` | **141 scenes**; `HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0, text-overlap-same 0`; radar `text-over-glyph 0, stroke-through-text 0`; **`element-missing 0`, `hint-blocked 0`, cmd-cell leaks 0, `HUD2_JET_CALLOUT callout-crossed 0`** |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **`tiers-20261117T135903.252301Z`: 15,137 tests in 550.4 s, 4 failed / 7 not executed**, 9/9 infrastructure checks, 7 executions over the calibrated 10 s limit (all passed, timing only). The four failures are unrelated to the change (`git diff -- src/Allegiance.Core src/Allegiance.Server` is empty) and every one passes **3/3 alone**: `P985RemoteTimelineTests.Pose_queries_allocate_no_managed_bytes` (6,088 B allocation measurement), `DeadLatchRespawnLiveTests…`, `NpcWingmanRangeMaintenanceLiveTests…`, `AsteroidMinedLiveTests…` — the last three already ledgered; the first is the allocation-under-load family. Ledgered in `docs/TEST_FLAKES.md` ("P1040c gate observations"). |
| Training gate | the training filter 3× on both client projects, `P912TrainingWindowTests` excluded | **ClientCore 126/126 ×3; MonoGame 256 passed / 1 skipped (257 cases) ×3** (10 m 54 s each, sequential runsettings). The 20 Live/Artwork lesson-*window* proofs in `P912TrainingWindowTests` are excluded: each spawns a full lesson window bounded at 21 min, so three repeats would exceed the task's time cap — and the change touches no lesson logic (`git diff` is limited to `Rendering/Jet/`, `tools/hud2-proof/`, the skin tests and docs; no `TrainingController`/goal/overlay-id line moves). The callout's own HUD tests (JetConsoleTests, all overlays) and the Skins suite pass 3×. |
| Glossary links | `python3 tools/check-glossary-links.py` | **GLOSSARY.md link check OK (941 distinct paths checked)** |
| Parity scorecard | `python3 scripts/parity-scorecard.py` | `parity-matrix.csv: validated 721 rows`, exit 0, no matrix row changed (this task ports no rule — the native anchors are cited, not re-derived) |
| AOT publish | not run — no `src/Allegiance.Server` / `src/Allegiance.Core` change | n/a |
| `docker compose config` | not run — no service/port/store/env change | n/a |

## 8. Anything unfinished / caveats

* **New media** (25 files, 1.4 MB under `docs/reports/JET_CONSOLE_FIXES_P1040C_assets/`) needs Domingo's review
  before any push, per the lane rules. The 141 full frames and the 2× crops (raw) stay outside the repo in
  `~/allegiance-evidence/P1040c/jet-final3/` and `.../jet-before/`.
* **`RETURN Shift+Z / G...` at 800×480** still prints the fitted form (the 152 px column cannot hold the whole
  native instruction). That is the P1040b-documented state, unchanged here; the binding's keys are the leading
  tokens and are never cut.
* **The compact flight-parts form gives way on the LATER fields**: with a bounded band the form keeps the weapon
  group, the first weapon mount and the boost state, and drops the remaining mounts and the `PART:` selection;
  the `WPN` label is dropped only when even that form cannot fit (`jet-pitched`, band 183 px). Nothing is cut
  mid-word; the ellipsis remains the last resort.
* **The compact form also applies outside the command view** when furniture trims the band: `jet-pitched-800x480`
  and `jet-lifepod-800x480` now draw `GROUP [1:Plasma] BST ON` / `WPN GROUP [1:Plasma] BST ON` instead of
  `WEAPONS: GROUP  BOOST: O...` (the band is bounded by the ladder numerals / the CMD column). That is the stated
  rule ("whenever the band is bounded"), and both readings are whole instead of cut.
* **The ladder yields a whole rung** whose ink meets the callout block — including a rung the audit's ink-band
  rule would not have counted (at 1920×1080 that is the `-20` rung whose dashes sit at the top edge of the
  callout's glyph band). Deliberate: the withdrawal is measured against the block, the count against the ink band,
  so the audit can never fail on a mark that is still drawn.
* **Overlay 12** (`INVENTORY DISPLAY`) has a native absolute anchor that puts its callout off-screen at 800×480;
  the sweep still asserts the rule there (vacuously), and the capture set has no scene for it (the evidence
  scenes carry overlay 17 and the roll lesson).
* **The wing cell is darker than the panel** (plate 0.97 vs the panel's 0.72) at every resolution where the CMD
  panel is drawn — that is the native paint order (the combo's own art over the verb bar), and it is the change
  that makes the strike impossible.
* **The Jet capture now has 141 scenes** (139 in P1040b + the roll lesson's two bright variants) — the Classic/
  Modern baseline is unaffected (it uses its own 1,848-combination matrix).
* **83 animation-only diffs** in the zero-delta compare are the fixture's wall-clock `!` badge inside its
  recorded mask (the P980/P1038-documented family), exactly as P1040b recorded.
* **The training gate is the reduced sweep** (§7): the training filter on both client projects 3× with
  `P912TrainingWindowTests` excluded. Those 20 cases are Live/Artwork lesson-window proofs that each spawn a
  full lesson window with a 21-minute bound (measured ~5–9 min on this host), so three repeats would eat the
  task's time cap; the change does not touch a lesson, goal, overlay id or the training control flow, and the
  callout's own HUD tests (every overlay at both resolutions) plus the Skins suite pass 3×.
* **No rig/remote work**: HUD-only presentation, no server or config change; nothing to apply outside this tree.
* The brief file `P1040C_BRIEF.md` was **removed from the worktree root** at the end (read first, then deleted), so
  it cannot enter the change set; `git status` lists only the changed source/test/tool files, the report, its
  assets and `docs/TEST_FLAKES.md`.

## Native intent

Native owns every reading: the command bar's `WingCombo` is a 54×42 combo over the verb bar (`dialog.mdl:38-42,
73-87`) painted last (`Game1.Commanding.cs:355-382`), and the picker tests the combo before the verb column
(`console.cpp:748-763`, `Game1.Commanding.cs:399-437`); the bottom-left lines are the native fixed offsets
(`Game1.InFlightUtility.cs:169`, `Game1.FlightParts.cs:324`) whose readings — weapon group, selected weapon,
boost state, hold occupancy — are the pilot's; the training callout table is native (`indicator.cpp:461-701`,
roll case :634-641) and its anchors may not move, so the SKIN yields, not the callout; the eject-pod block is the
native `EjectPodImage` identity with its `GetEndurance` gauge (`dialog.mdl:1922-1996`, `consoledata.cpp:534-545`).

## Modern approach

The skin composes over the same shared readings and states every rule it applies: the CMD panel paints its cells
in the native order and gives the wing combo its own near-opaque plate; the bottom rows take a stated compact form
built from the shared line when the measured band cannot hold it; the ladder withholds exactly the furniture that
would meet a shared callout, resolved from one measured block; and the layout audit now counts both classes (CMD
grid strokes are a grid, not a run's own mark; Jet furniture across a training callout) so a regression is a
number, not a review note. Nothing invents a value: every string and every box comes from the existing model.
