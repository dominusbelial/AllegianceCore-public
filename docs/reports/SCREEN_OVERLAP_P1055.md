# P1055 — content-vs-content overlap audit, Benchmark layout, starmap panes, ConsoleLog completed-queue crash

**Task brief:** `P1055_BRIEF.md` (untracked, not part of the change set).
**Worktree:** `/home/domingo/wt-P1055`, branch `hermes/P1055` off Claude's master `229b5796`. **Nothing committed** —
every change stays in the working tree.
**Local env:** `local.env` → artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src` (read-only).
Evidence: `docs/reports/SCREEN_OVERLAP_P1055_assets/` (28 PNG, 598,269 bytes); raw audit JSON/logs in
`<lane evidence dir>/P1055/` (`pre/` = the pre-fix audit).

## Status

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 0 | Report first, kept current | DONE | this file |
| 0a | `ContentOverlap` class + `content-off-screen`, every screen the P1042/P1042b/P1042c tools reach × 4 skins × 3 sizes, **before** fixing | DONE | §1 — **776 combos**, pre-fix 72 findings, post-fix 58, every remaining one triaged (fixed / native-faithful / carried) |
| 1 | Benchmark / Showcase sub-view at 800×480 | DONE | §2 — `BenchmarkMenuLayout` (one geometry source); 800: 0 overlaps, 0 off-screen, 9 rows + 6 wrapped lines on screen; **1080p byte-identical** (real-window 0 differing pixels) |
| 2 | Starmap sector-info pane over the alert line; probe icon/count spacing | DONE | §3 — the alert is the pane's first line (native placement); probe count at icon+4 px; 0 content findings in the starmap except the native-faithful pane-over-label at 800 |
| 3 | ConsoleLog crash on a completed queue | DONE | §4 — deterministic regressions fail before / pass after 3×; `TryEnqueue` retries once on the current generation, else drops-and-counts |
| G | Gate | DONE | §5 — HUD 1,848 zero-delta PASS, screen goldens 420/16 (only the two fixed screens) PASS, Jet 282/282 identical PASS, new tests 3× PASS, build 0/0 PASS, tools build PASS, fast tier exit 1 on ledgered-only flakes (Core ran to completion), training gate PASS with the stated run-1 stop and one ledgered repeat-3 load flake |

---

## 1. Item 0 — the content-vs-content audit

### 1.1 What was added (why the old classes could not see it)

`TextOverlapCross` (P1042b) compares a Jet **chrome** run with the **content** text it is drawn over; every other
class is chrome-vs-chrome or chrome-vs-plate. Two pieces of the game's own text overlapping each other were never a
class — which is exactly what Claude's review of the P1042c captures found (the Benchmark paragraph over its own size
rows, the sector-info pane over the alert line).

`tools/p1042-screen-proof/Recorder.cs` (proof tool only; no production change):

* `ScreenRecorder.ContentText(element, owner, text, position, scale, large)` — a content run measured with the real
  HUD fonts at the anchor the content renderer resolves; `owner` names the content panel the run belongs to.
* `ScreenRecorder.ContentPanel(element, box)` — a content panel/box (map panel, sector-info pane, leaderboard panel,
  career/game-over panel, a panel family's backdrop).
* `ScreenAudit.EvaluateContent(...)` — three classes:
  * **`content-overlap`**: two content text runs whose **ink** boxes intersect by more than the tolerance
    (**1 px penetration on each axis**, measured on the ink box so a 20 px line pitch that abuts exactly is not a
    collision). Reported as W×H px and px².
  * **`content-panel-overlap`**: a content run crossing a content panel it does not belong to. A run inside its own
    panel is not a collision, and a panel that fully contains the run's OWNER panel is an ancestor (the sector-info
    pane lives inside the map panel — its own lines are not "over the map").
  * **`content-off-screen`**: a content text box past the viewport.
* `tools/p1042-screen-proof/Content.cs` enumerates the runs from the **objects the draw path uses** — the
  `IntroMainFrame` layout anchors, `IntroViewFrame` rows/lines, the `ScreenPanelFrame` rows, `ScoreboardLayout.Layout`
  column origins, the starmap renderer's own title/site/pane arithmetic (`SectorInfoPaneRenderer.Box` +
  `SectorInfoPaneLayout.LineOrigins`), the career renderer's title/tab/status rectangles, the game-over panel header
  arithmetic — plus the hangar's own `IScreenRectAudit.Content` reports.
* `--audit` runs the content pass for every scene/size/palette and prints one `CONTENT` line per (scene, size, skin):
  content runs and boxes are **skin-independent** by construction (the four columns paint the same frame objects —
  the P1042c 102/102 decoded-pixel legacy compare is that proof), so the measured runs are attributed to Classic,
  Modern, Jet green and Jet amber. JSON: `content-audit.json`; findings: `content-findings.txt`.
* The proof tool now reaches the Benchmark sub-view directly: `view-benchmark` (fixture built from the production
  `BenchmarkMenuLayout`, `Program.cs`), and `--pre-benchmark` rebuilds the pre-fix constants so the class can be shown
  to fire. `--pre-starmap` likewise rebuilds the pre-fix starmap (alert under the header, pane without a warning line).
* Not enumerated (stated, not hidden): **loadout V2** — its text placement lives inside `LoadoutV2Renderer` and the
  display rows carry no rectangles; the `--audit` loop already excludes the loadout scene (P1013 owns its own layout
  audit). The three 800-px states the chrome audit already skips (quick-chat, ready-check picker/editor,
  connection-lost) are skipped for content at 800 for the same documented reason (P1042c §9: the shared classic layout
  is taller than 480) and are audited at 1080p/4K.

### 1.2 The pre-fix findings table (before any fix)

`tools/p1042-screen-proof --audit <dir> --pre-benchmark --pre-starmap` → **776 (scene, size, palette, skin) rows**
= 97 scene/size combinations (34 scenes × 3 sizes, minus the documented 800-px chrome skips) × 2 palettes × 4 skins.
Each combination records 0-230 content runs (intro 18, panels 4-23, scoreboard 37, keybind 122-123, starmap 14,
Benchmark 14, career 8, game-over 2; **hangar 0** — the Jet path suppresses the port-verb/hover runs because its own
chrome owns those labels, P1042b), and 0-2 content boxes. Pre-fix: **36 distinct findings per palette, 72 with both
palettes** (`<lane evidence dir>/P1055/pre/content-findings.txt`):

| screen | size | skins | the two texts / run-vs-box | overlap px | count |
|---|---|---|---|---|---|
| **view-benchmark** | 800×480 | all 4 | row 5 `Settings tune (fixed, ranked)` / description line 1 | **339×14 (4,746 px²)** | 1 |
| **view-benchmark** | 800×480 | all 4 | row 6 `Replay recorded battle` / description line 1 | **262×4 (1,048 px²)** | 1 |
| **view-benchmark** | 800×480 | all 4 | rows 8 `Calibrate this machine (bounded)`, 9 `Skip / Back` off-screen (y 476, 514 in a 480 px viewport) | — | 2 |
| **view-benchmark** | 800×480 | all 4 | description run off-screen (1,144×66 at x 96 in an 800 px viewport) | — | 1 |
| **starmap** | 800 / 1080 / 4K | all 4 | alert `Enemy miner spotted` over the sector-info pane box | **155×11** | 3 |
| keybind | 800×480 | all 4 | 7 list/footer runs past the right edge (x up to 1,163) | — | 7 |
| keybind | 3840×2160 | all 4 | key column run `TargetFriendlyConstructorNearest` × description run | 4×10 | 2 |
| quickchat | 1080 / 4K | all 4 | port-only legend `KEY   VOICE COMMAND` over `1` / `Affirmative` | 9×6 / 119×6 | 2 |
| career | 800×480 | all 4 | tab `Back` off-screen (x 827.5 in 800) | — | 1 |
| view-help | 800×480 | all 4 | subtitle off-screen (x −12.5 … 812) | — | 1 |

The chrome audit independently flagged the same Benchmark case at 800 (`--pre-benchmark`: 2 combos, 38 failures —
16 `stroke-vs-text` where the Jet row brackets crossed the paragraph's ink, 2 `text-off-screen`, 1 `overlap-cross`).

### 1.3 Post-fix state and triage

After the two fixes: the same 776 rows, **29 findings per palette (58 with both)** and no untriaged one.

| finding | decision | why / citation |
|---|---|---|
| view-benchmark overlaps + off-screen rows/description at 800 | **fixed (item 1)** | §2 |
| starmap alert under the header over the pane | **fixed (item 2)** | §3, native `WinTrek/sectormap.cpp:517-534` |
| starmap `Serenity` label over the pane box at 800 | **native-faithful, allow-listed** | native draws the info pane **over** the map at the group origin while the map is translated (9,20) (`dialog.mdl:1665-1681`, `GroupImage` reverse paint order), so a sector label under the pane is native behaviour; the map is content the port must not move |
| quickchat legend `KEY   VOICE COMMAND` over the first rows (1080/4K) | **native-faithful, allow-listed (port-only chrome)** | the legend row is port chrome with no native counterpart — native's quick chat is `quickchat.mdl` panes with no column legend (`QuickChatMenuRenderer`, P217); the 8 px row offset is pre-existing (P1042c §4.1) |
| keybind 7 runs past 800 + 2 column runs at 4K | **carried** | port-only key-binding screen (native's rebind UI is the mouse `InputMapPopup`, `WinTrek/trekinput.cpp:1631+`); the description column is measured at scale 1 while rows are fitted, so at 800/4K the column can cross. A layout change is outside this story and would move a screen the gate does not allow to change; recorded for the key-bind story |
| career tab `Back` off-screen at 800 | **carried** | five tabs at ±150 px need 750 px of centred width; pre-existing shared layout (P1042c §5's 800-px family). Fixing it changes Classic pixels of a screen this story does not fix |
| view-help subtitle off-screen at 800 | **carried** | port-only Help view; the subtitle is centred unscaled, pre-existing. Same shared-layout reason |

Findings per screen (post-fix, identical in all four skins and both palettes): view-benchmark **0**, starmap **1**
(the allow-listed label at 800 only), keybind 9, quickchat 4, career 1, view-help 1, intro/panels/scoreboard/gameover/
hangar **0**.

---

## 2. Item 1 — Benchmark / Showcase sub-view at 800×480

### 2.1 The defect (measured)

`Game1.DrawBenchmarkMenu` laid the view out at fixed constants — nine rows at `y = 210 + i·38`, band 34 (list ends at
548 px) and the three-paragraph description at `(0.12·w, h − 96)` with no wrapping. At 800×480 the description starts
at y 384, i.e. **inside** the list, and each paragraph is ~1,144 px wide. Measured on the real-window capture (pre-fix,
Classic): row ink at y 252-268, 290-307, 328-345, 366-382, **404-421, 442-459** — the last two are rows drawn *under*
the description — the two remaining rows fall below 480, and the dim description ink runs to **x 798** (the right edge).

### 2.2 The fix — one geometry source, fitted at the sizes that do not fit

New `src/Allegiance.ClientCore.Game/Rendering/BenchmarkMenuLayout.cs` (labels, paragraph, constants, `Compute`,
`Wrap`). `Game1.Benchmark.cs:85-114` now builds the frame from it.

* **Classic branch (unchanged pixels).** When the pre-P1055 constants fit (list above the description with a 10 px
  gap, the wrapped description inside the bottom margin) `Compute` returns them **verbatim**: rows 210/38/34, the
  description at `0.12·w`, `h − 96`, scale 1. 1920×1080 takes this branch — the real-window captures before/after are
  **byte-identical (0 differing pixels, Classic and Jet green)**, and the proof-tool compare has 0 diffs for
  `view-benchmark` at 1080p/4K.
* **Fitted branch (800×480).** The description is wrapped to the usable width (`w − 80`), bottom-anchored, and the
  list is compressed into the band between the subtitle (110 px) and the description (pitch ≥ 22 px, band = pitch −
  2). Rows, order, labels, selection and every input path are unchanged — **no scrolling was added** (that would need
  a new key binding, out of scope); "wrap to the available width" + "move the description below the list" is the
  combination that fits without new behaviour.
* Text content unchanged except the wrapping (`Benchmark_wrapping_preserves_the_description_text` joins the wrapped
  lines back to the original paragraphs).
* Every drawn row is its band (`BandedRow` at the band's top-left) and the hit rectangles come from the same frame —
  the P1042b/P1042c draw == hit rule is kept.

Measured after (real window, Classic 800): rows at y **114-327** (nine, `> Watch a battle…` … `Skip / Back`), the
description as **six wrapped lines at y 344-471, max x 750** — no overlap, nothing past 800. Jet green: the same text
bands plated by the chrome (109-328 / 344-471), ink 40-750.

### 2.3 Evidence

| file | what it shows (measured; vision stated where usable) |
|---|---|
| `benchmark-classic-800-before.png` / `-after.png` | before: 5 rows + paragraph over the 6th/7th row, ink to the right edge; after: nine rows, paragraph below them (vision on the 2× crop: 8 white rows + the yellow selected row, paragraph directly below, no overlap) |
| `benchmark-jet-800-before.png` / `-after.png` | the same with Jet plates/brackets; before has the paragraph crossing two plated rows |
| `benchmark-classic-1080-before.png` / `-after.png` | byte-identical pair (framing the 0-pixel claim) |
| `benchmark-jet-1080-before.png` / `-after.png` | byte-identical pair |
| `crop-benchmark-classic-800-before-2x.png` / `-after-2x.png`, `crop-benchmark-jet-green-800-before/after-2x.png` | 2× nearest-neighbour of the fixed 800 region (x 30-800, y 100-480) |

Vision: on the after 2× crop it read the rows (`Medium … Skip / Back`) and the paragraph **below** the list — the
layout claim. On the pre-fix 2× crop vision mis-segmented the rows and invented paragraph wording (the 800 px font is
below its reliability), so the before claim rests on the measured ink bands/photometric boxes above, not on vision.

---

## 3. Item 2 — starmap sector-info pane over the alert line

### 3.1 Measured defect (P1042c `starmap__jet-green__1920x1080.png`)

The port draws its header `STARMAP | YOU: Sol | TAB TOGGLE` at `panelY + 10`, then the selected sector's alert at
`panelY + 10 + titleHeight + 3` (y 255-276 line box, red ink measured 259-272), and the info pane anchored at
`panelY + 40` (box measured 466,262-621,409 Classic 1080) — the pane's translucent backing is painted *after* the map,
so its 0.90-alpha fill covers the lower half of the alert. The 4× crop of the P1042c capture shows exactly that
("otted C," — the left part of the red line under the pane's tint).

### 3.2 Native placement

Native draws the cluster warning **inside** `SectorInfoPane` at the pane's own origin, after the pane background
(`WinTrek/sectormap.cpp:517-534`, inside `SectorInfoPane::Render`, background first at `:398`), and it has no
`STARMAP | …` header at all (the port header is chrome). The pane itself is the group's first child painted over the
map, which is translated (9,20) (`dialog.mdl:1665-1681`). Native placement therefore wins: the alert belongs to the
pane. The port already has that slot — `SectorInfoPaneLayout.Data.WarningText`, drawn as the pane's first line at
`LineOrigins(box + (12,8))` — so:

* `Rendering/StarmapRenderer.cs:131-140` — the header-band alert draw is **removed** (and its flashing sector-name
  duplicate, which native does not draw: native paints one string).
* `Game1.Rendering.cs:4118-4132` — the pane's warning now falls back to the **selected** sector when there is no
  hover (`_starmapHoverSector ?? sid`): the keyboard client has no pointer, and native's MouseMove makes the hovered
  cluster the selection (`sectormap.cpp:1413-1461`), so the selection is the native mouse-over.
* `Rendering/SectorInfoPaneRenderer.cs:115-130,204-207` — the probe count is drawn the icon's reserved width + **4 px**
  right of the icon (native `ptNext + Point(iconWidth + 4, 0)`, `sectormap.cpp:338-344`); the port used +6 px. Measured
  before the change: diamond x 480-488, count ink from 498 → a **10 px** gap (no overlap existed at 1080); after the
  change the count ink starts at 496 (gap 8 px from the glyph's ink, 5.5 px from the diamond's edge) — native spacing,
  and the pane's box is 2 px narrower by the same measure.

### 3.3 Measured after

Classic 1080: no red ink at all; the pane's first line is the warning (pane-warning ink 480,274-684,291), and the pane
box grows to 466,262-698,431 (one line taller, 4 px wider than the old widest line). The 2× crop after: first line
`Enemy miner spotted` inside the box, then `Sol Sector`, `Asteroids: 4`, `He3: 1`, `He3-rich: 1`, `He3 ore: 260`,
`PROBES 3` — **no text crosses the border** (vision read all seven lines and confirmed containment). Classic/Modern
show the same geometry (the content renderers are shared); the Jet capture differs only in the chrome frame.

### 3.4 Evidence

`starmap-classic-1080-before/after.png`, `starmap-modern-1080-…`, `starmap-jet-green-1080-…`, the same at 800×480, and
`crop-starmap-classic-1080-before/after-2x.png`, `crop-starmap-classic-800-before/after-2x.png`.

---

## 4. Item 3 — ConsoleLog crash on a completed queue

### 4.1 Root cause (confirmed, not corrected)

`ConsoleLog.Write` (`src/Allegiance.Core/ConsoleLog.cs:162-163`) and `QueuedConsoleWriter.Emit` (`:340-341`) read
`_gen` and called `gen.Queue.TryAdd(line)`. `BlockingCollection.TryAdd` **throws**
`InvalidOperationException: The collection has been marked as complete with regards to additions` on a completed
collection — it does not return false. `ResetForTest` (`:299`) completes the OLD queue **before** it swaps `_gen`
(after a join that can wait seconds), and `Shutdown` (`:136`) completes the live queue at process exit and leaves it
installed; any writer hitting that window (the server's sim/broadcast/bot threads, or a test host's leftover
background writer) threw on a background thread, and the Core test host aborted ~5,000 tests into the P1042c fast tier.
Claude's reading was correct; the second call site (`Emit`, the redirected-console path) had the same defect.

### 4.2 The fix

* `TryEnqueue(line)` (`:170-195`) — enqueue on the generation current now; if that generation is COMPLETE, retry
  **once** on the generation current after the retry; if that one is complete too (shutdown, no drain left) the line is
  **dropped and counted** in `DroppedWrites`. A full (not completed) queue is a drop, as before.
* `TryAddTo(gen, line)` (`:198-211`) — the only place a line enters a queue; a completed collection is a drop, never an
  exception; it owns the `Enqueued`/`EmittedWrites` counters (so a retried line counts once on the generation it
  landed on).
* `Emit` uses the same helper; `ResetForTest` tolerates a queue `Shutdown` already completed.
* Behaviour kept: never blocks the caller (bounded queue, drop on full), `TotalWrites`/`EmittedWrites`/`DroppedWrites`
  and the P964 per-thread probe counters, the shared writer lock (P829), the one drain thread per generation (P964),
  P619c's non-blocking plain-console redirect. No change beyond the completed-queue case.

### 4.3 Deterministic regressions (fail before, pass after, 3× alone)

`tests/Allegiance.Core.Tests/Transport/ConsoleLogNonBlockingTests.cs`:

* `Write_after_Shutdown_completed_the_generation_is_dropped_and_counted` — ordered steps, no threads: `Shutdown()`
  completes the live generation, then one `Write` must drop-and-count. **Pre-fix**: the exact production exception
  (`InvalidOperationException`, `ConsoleLog.cs:163`). Post-fix: pass.
* `Write_hitting_a_generation_completed_by_ResetForTest_drops_instead_of_throwing` — a gated sink holds the drain
  inside `ResetForTest`'s join (the window is the join's own wait, seconds wide); the test captures the old generation
  object through reflection, waits (bounded, 5 ms poll, no busy spin) until that exact queue is
  `IsAddingCompleted`, asserts the swap has not happened, then writes once. **Pre-fix**: the same exception. Post-fix:
  pass, and the next write lands on the fresh generation.
* 3× alone: **3 passed / 0 failed** each run (2 new + the filter's sibling); the whole `ConsoleLog*` +
  `ConsoleVerbosity*` set: **13/13**.

### 4.4 Ledger

`docs/TEST_FLAKES.md` — new section "P1055 — ConsoleLog completed-generation crash": the P1042c abort (Core test
host, ~5,000 tests, `The collection has been marked as complete…`), root cause, fix, regression names.

---

## 5. Gate results

| Gate | Result |
|---|---|
| Solution build `dotnet build AllegianceCore.slnx` | **PASS — 0 warnings, 0 errors** (build servers disabled) |
| `tools/build-tools.sh` | **PASS — all 148 tool projects built**, exit 0 |
| HUD 1,848-combination zero-delta (Classic/Modern) | **PASS** — `tools/hud2-proof --compare <lane evidence dir>/P1041/baseline`: `HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)` (the 84 are the wall-clock `!` badge the tool masks by design, as P1042/P1042b/P1042c) |
| Jet flight HUD green/amber byte-identical | **PASS** — `tools/hud2-proof --jet` after vs `P1041/jet-after`: **282/282 PNGs SHA-256 identical** (only `jet-capture.json`/`run.json` metadata differ) |
| P1042 + P1042b + P1042c screen goldens (captured **before** editing) | **PASS with the two fixed screens listed** — `--out` on the pre-edit tree (420 combos: 35 scenes × 3 sizes × 4 skins, `--pre-benchmark --pre-starmap`), then `--compare` after: **420 combinations, 16 differing**, exactly `view-benchmark` at 800×480 and `starmap` at 800/1080/4K, each × 4 skins. Everything else (including `view-benchmark` at 1080p/4K) **0 pixels**. Changed regions (bounding boxes): Benchmark Classic 800 (40,114)-(799,471) 32,313 px, Jet green (39,109)-(779,479) 158,860 px — the compressed list + wrapped paragraph; starmap Classic 800 (106,121)-(390,293) 9,260 px, 1080 (466,259)-(750,431) 7,468 px, 4K (1426,799)-(1710,971) 7,468 px, Jet green 1080 (465,259)-(750,432) 7,657 px — the alert moved into the pane, the pane one line taller/wider, the probe label 2 px left |
| P1042c Classic/Modern legacy pixel gate | **PASS except the fixed screen** — `--legacy-compare <lane evidence dir>/P1042C/legacy`: **102 combinations, 6 differing** — exactly `starmap` (Classic+Modern × 3 sizes); the other 96 (all panel families, leaderboard, game-over) are 0 pixels. The starmap is this story's screen (§3) |
| New tests 3× alone | **PASS** — `P1055ScreenOverlapTests` **8/8** three times (21/24/22 ms); the two ConsoleLog regressions **3/3** three times plus the whole `ConsoleLog*`/`ConsoleVerbosity*` set **13/13** |
| One fast tier (`tools/run-tests.sh --tier fast`) | **FAIL (tier exit 1)** — **15,535 cases, 655.5 s**, calibration factor 1.265 (limit 12.649 s). `Allegiance.Core.Tests` **ran to completion: 6,761 passed / 2 failed / 3 skipped — no abort** (the P1042c completed-queue crash is gone). The two Core failures are ledgered load flakes, each **pass 3/3 alone**: `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` (relay/lookup receipt family, `TEST_FLAKES.md:429,2198`) and `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` (warp-receipt family, `:397,453,562,710,1031,1227`). Every other project passed (MonoGame 5,913 + 4 tier-excluded; ClientCore 2,456/2,456). Four Fast executions over the calibrated limit (SC2Nanite 18.6 s, P814 18.0 s, P796 13.5 s, P999 civ 5 17.9 s) — the usual load offenders, all passing |
| Training gate | **PASS (one ledgered load flake in repeat 3) with the run-1 stop stated** — `tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once` with artwork: run 1 ClientCore **131/131** + **20 real-window children (all PASS)**; the run-1 sweep then continued into the assigned-lesson family (`lesson=8`, up to 21 min per window) and was stopped there to fit the cap; repeats 2 and 3 were then run directly with the P912 window class excluded: §5.1 |


### 5.1 Training gate

`tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once` with artwork (`local.env`), results in
`<lane evidence dir>/P1055/training/` (per-run `output.log` + `training.trx`).

**Run 1, `Allegiance.ClientCore.Tests` (all training filters): PASS — 131/131 (12 s).**

**Run 1, `Allegiance.ClientCore.MonoGame.Tests` real-window children: 20 children ran, every one PASS** (the result
line each child wrote, `<lane evidence dir>/P1055/training/windows/result-lines.txt`):

```
4 × P912_WINDOW_PASS M2 Classic lessons=1,2,3,4,5,6,7,8,9
4 × P912_WINDOW_PASS M2 Modern  lessons=1,2,3,4,5,6,7,8,9
1 × P912_WINDOW_PASS M3 Classic lessons=1,2,3,4,5,6,7,8,9,10,11,12,13
1 × P912_WINDOW_PASS M3 Modern  lessons=1,2,3,4,5,6,7,8,9,10,11,12,13
1 × P912_WINDOW_PASS M4 Classic lessons=1,2,3,4,5,6,7,8,9,10
2 × P912_WINDOW_PASS M4 Modern  lessons=1,2,3,4,5,6,7,8,9,10
1 × P912_WINDOW_PASS M5 Classic lessons=1,2,3,4,5
1 × P912_WINDOW_PASS M5 Modern  lessons=1,2,3,4,5
1 × P912_WINDOW_PASS M8 Classic lessons=1,2,3
2 × P912_WINDOW_PASS M8 Modern  lessons=1,2,3
1 × P918_WINDOW_PASS Classic
1 × P918_WINDOW_PASS Modern
```

(The four M2 children per style are the default / muted / missing-audio / reminders variants; the M4 Modern pair is
default + `departure`; the M8 Modern pair is default + `loss`. The Nanite M8 completion windows pass in both styles.)

**Where the run-1 sweep stopped (stated).** After the M2/M3/M4/M5/M8 families the suite continued into the
assigned-lesson family (`m4 … lesson=8`, each window up to the 21-min bound — the family P1042 also left to Claude's
mini runner). It was **stopped by exact PID** (the `dotnet test` + `p912-training-proof window` processes) to stay
inside the 6-hour cap; no child had failed. Repeats 2 and 3 then ran directly with the P912 window class excluded (the
script's own `--window-proofs-once` rule), results in `<lane evidence dir>/P1055/training/{2,3}/`:

| repeat | ClientCore.Tests (training filters) | MonoGame.Tests (P912 windows excluded) |
|---|---|---|
| 2 | **131/131 PASS (12 s)** | **256 passed / 1 skipped (11 m 13 s)** |
| 3 | **131/131 PASS (10 s)** | **255 passed / 1 failed / 1 skipped (11 m 7 s)** — the failure is `P910TrainingWireTests.Self_chat_cannot_send_an_order_past_the_training_receive_guard(style: Classic)`, **pass 3/3 alone** (ledgered in `TEST_FLAKES.md`, P1055 training repeats) |

---

## 6. Evidence index

`docs/reports/SCREEN_OVERLAP_P1055_assets/` — **28 PNG, 598,269 bytes** (≤ 1.5 MB):

* Benchmark real-window captures (the walk tool drives the shipping `Game1` intro; 14 views per run, 4 runs per
  side): `benchmark-classic-800-before/after.png`, `benchmark-jet-800-…`, `benchmark-classic-1080-…`,
  `benchmark-jet-1080-…`.
* 2× nearest-neighbour crops of the fixed 800×480 Benchmark region: `crop-benchmark-classic-800-before/after-2x.png`,
  `crop-benchmark-jet-green-800-before/after-2x.png`.
* Starmap captures through the production renderers (`--out`, Classic/Modern/Jet green, 800×480 + 1920×1080):
  `starmap-{classic,modern,jet-green}-{800,1080}-{before,after}.png`, plus 2× crops of the pane/alert region
  (`crop-starmap-classic-{800,1080}-{before,after}-2x.png`).
* Raw audit JSON/logs, fast-tier TRX and training logs: `<lane evidence dir>/P1055/` (`pre/` = the pre-fix audit,
  `training/` = the gate).

**What the images show (measured; vision stated where it worked).**
* `crop-starmap-classic-1080-before-2x.png`: vision read the red line as "otted Cortex" and confirmed "the red text's
  lower half is covered by the bordered box" — exactly the measured 259-272 ink vs the pane's 262 top edge.
* `crop-starmap-classic-1080-after-2x.png`: vision read the box's first line `Enemy miner spotted` (red) then
  `Sol Sector`, `Asteroids: 4`, `He3: 1`, `He3-rich: 1`, `He3 ore: 260`, `PROBES 3`, and confirmed no text crosses the
  border; the measured warning ink 480,274-684,291 sits inside the pane box 466,262-698,431.
* `crop-benchmark-classic-800-after-2x.png`: vision counted the eight white rows plus the yellow selected row and put
  the paragraph directly below the list — the measured bands (rows 114-327, paragraph 344-471, ink to x 750) are the
  proof.
* The pre-fix Benchmark 2× crop: vision mis-segmented the rows and invented paragraph wording (800 px small text is
  below its reliability), so the pre-fix claim rests on the measured ink bands (rows 252-459 with the paragraph over
  rows 6-7, ink to x 798), not on the description.
* The 1080p Benchmark pairs are byte-identical (0 differing pixels), so there is nothing for vision to read: the two
  files have the same SHA-256.

---

## Native intent

Every rule this story applies is native's. The starmap's cluster warning lives **inside** the sector-info pane at the
pane's own origin, painted after the pane background (`WinTrek/sectormap.cpp:517-534`, `:398`), and the pane itself is
painted over the map (`dialog.mdl:1665-1681`, map translated (9,20)); the probe count follows the probe icon by the
icon's width + 4 px (`sectormap.cpp:338-344`); the keyboard client's selection is native's mouse-over cluster
(`sectormap.cpp:1413-1461`), so the selected sector's warning is the pane's warning. The Benchmark/Showcase sub-view
has **no native pane** — it is the port's own screen (`ination.cpp`/`introscreen.cpp`'s intro menus have no such
paragraph) — so the fix keeps the port's own text and geometry, wraps it to the width the port already owns and only
compresses the list at the size where the constants cannot fit; the 1080p geometry is the pre-existing port geometry
verbatim. The audit classes themselves measure the port's own drawing against the port's own frames, with native
citations only where the port departs (the allow-listed findings). The ConsoleLog fix touches no protocol: it keeps
P619c's non-blocking bounded sink, P829's shared writer lock and P964's generation/drain contract, and only stops a
completed collection from throwing.

## Modern approach

One geometry source per screen: `BenchmarkMenuLayout.Compute/Wrap` is the single arithmetic the draw path and the
proof use (classic constants when they fit, a fitted/wrapped layout when they cannot), and the starmap's warning is the
pane's already-modelled `WarningText` line rather than a second draw at the header, so there is exactly one owner for
the alert. The audit grew one content-side collector that reads the same frame objects the four skin adapters paint
(so Classic/Modern/Jet green/Jet amber are measured on identical geometry), an ink-box tolerance that ignores line
leading, and a nested-panel rule so a pane inside the map is not a false collision. The crash fix is a single
generation-aware enqueue used by both writers, with deterministic regressions that hold a reset's join open instead of
spinning. No allocation, no per-frame work and no new dependency was added.
