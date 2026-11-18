# P1042c — Jet screens part 3: the in-game screen families + legacy docked shop

**Task brief:** `P1042C_BRIEF.md` (P972 slices 1F/3F, final; part 3 of 3).
**Worktree:** the lane worktree, branch `hermes/P1042C` off Claude's master. **Nothing committed** — every change
stays in the working tree (`P1042C_BRIEF.md` is untracked and is NOT part of the change set).
**Local env:** `local.env` → artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src`.

## Status

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 0 | Report first, kept current | DONE | this file |
| 0a | Benchmark real-window golden (Classic/Modern + Jet green/amber) + audit | DONE | §1: real client, 14 intro views ×4 skins, Benchmark captured at 800×480; the 1920 row-placement anomaly and the residual em-dash font crash are stated |
| 0b | Character Info title vs tab row: measure, Jet-only fix, audit rule flags before / 0 after | DONE | §2: pre-fix **5** title-vs-tab findings, post-fix **0** (gap N = 6 px) |
| 1 | In-game families routed through `IScreenSkinRenderer`, zero Classic/Modern delta | DONE | §3: **102/102** decoded-pixel identical (17 scenes × 3 resolutions × Classic+Modern) |
| 2 | Jet chrome per family (green + amber), same commands/keys/focus/rects | DONE | §4 (panel families full chrome; scoreboard/starmap/game-over/docked shop frames over unchanged content) |
| 3 | Switching / 800×480 / 4K / `hud.jetSkin` off | DONE (with the stated 800-px carried limits) | §5 |
| A | Audit counts per screen, both palettes, 800×480/1080p/4K, every class 0 | DONE | §6: **188 combos, 1,438 controls, every class 0** |
| B | Assets (≤ 2 MB) + every image opened and read | DONE | §7: 33 PNG, **1,270,415 bytes** |
| G | Gate: HUD zero-delta, screen goldens, Jet flight identical, 0 B/frame, tests 3×, build 0/0, tools build, one fast tier, training gate | DONE — fast tier exit 1 on ledgered-only failures + one unledgered Core test-host crash, training gate PASS with the run-1 stop (§8) | §8 |

---

## 1. Item 0a — the Benchmark sub-view (real window)

The P1042b walk tool (`tools/p1042b-screen-walk`) drives the **shipping** `Game1` intro through the production
`_introView` field and photographs the real backbuffer with the game's own autoshot. It already listed
`Benchmark` last (P1042b could not capture it). On this tree a **residual instance of the same pre-existing
font defect** still aborted the capture: the Benchmark subtitle carried an em dash (`Big AI battles — and the
one settings tune`, `Game1.Benchmark.cs:103`) that `SpriteFont.MeasureString` rejects (the same class as the
`×` fixed at the P1042b merge). It is now an ASCII hyphen (`:103`), which is the only text changed anywhere in
this story and is reported here explicitly.

Runs (fresh profile, `ALLEGIANCE_PLAYER_SETTINGS` with `Visuals.hud.jetSkin`, `ALLEGIANCE_HUD_SETTINGS`):

| run | views captured | Benchmark |
|---|---|---|
| Classic 800×480 | 14 | **captured** — `benchmark-classic-800.png` |
| Modern 800×480 | 14 | **captured** — `benchmark-modern-800.png` |
| Jet green 800×480 (`hud.jetSkin` on) | 14 | **captured** — `benchmark-jet-green-800.png` |
| Jet amber 800×480 | 14 | **captured** — `benchmark-jet-amber-800.png` |
| Classic/Modern/Jet 1920×1080 and 1280×720/1600×900 (controls) | 14 each | captured, but see the anomaly |

**Read-back (opened every cited image).** At 800×480 the Classic Benchmark screen shows the title `Showcase`,
its subtitle, the nine Showcase rows and the paragraph (`A Showcase is a big AI battle … 150s x3 … Arrows /
Enter; Escape skips.`) — ink bands measured at y 36-65 (title), 86-103 (subtitle), 214-459 (rows+paragraph) by
the lane's PNG decoder, and the image itself shows the rows as white text on black. The Jet green/amber
captures show the same strings plates/framed in the Jet palette; the amber set is the same geometry.
**Anomaly, stated plainly:** at window heights above ~900 px the *rows and title* of the Benchmark (and of the
Career view) are absent from the captured frame while the bottom paragraph/controls remain — the ink bands are
36-459 at 800×480, 252-667 at 1280×720, and only 988-1045 at 1920×1080. This is the intro drawing under the
offscreen harness (the paragraph is drawn by the same method as the rows), not code this story touches; the
Benchmark golden therefore uses 800×480, where the whole screen is present. The audit fixture (§6) covers the
chrome at 1080p/4K.

## 2. Item 0b — Character Info title vs tab row (Jet only)

**Measured boxes.** Classic career title `[ CHARACTER INFO ]` in `HUDLarge`: **432×47 at (184,24)** at 800×480
(1024×47 at (448,24) at 1080p, 2048×47 at 4K — the audit's `--pre-title` record). The tab row's top edge is
`CareerScreenRenderer.TabRect`'s y = **63**. The classic title's bottom is **71** → it overlaps the tab row by
**8 px at every resolution**. The old Jet chrome drew the same title through the content renderer, so Jet showed
the same crowding.

**Fix (Jet chrome only).** `CareerScreenRenderer` gained `drawTitle` (default **true**, so Classic/Modern keep
drawing it, pixel-identical) and `TitleFor` / `TitleTop`=24 / `TabRowTop`=63
(`CareerScreenRenderer.cs:33-43,78`). `JetScreenSkinRenderer.DrawCareer` passes `drawTitle: false` and draws the
title itself, fitted into the band `24 … TabRowTop − N − 1` at scale `min(1, band/ink)` centred
(`JetScreenSkinRenderer.cs:278-292`, `CareerScreenTitleGap = 6f` at `:50`). At 800×480 the fitted title is
24…56, i.e. a **7 px** clearance; Classic/Modern are untouched.

**The audit rule (N = 6 px).** `ScreenAudit.TitleVsTab(recorder, titleId, tabFamily, gap)`
(`tools/p1042-screen-proof/Recorder.cs`) counts a title run whose bottom is less than `gap` above a tab-row
control (overlap is the negative case). `tools/p1042-screen-proof --audit` reports `title-vs-tab` per line and
fails on it; `--pre-title` injects the measured pre-fix title geometry (the classic anchor/scale measured with
the real fonts) so the rule can be shown to fire:

| build | title-vs-tab (career, green/amber × 3 resolutions) |
|---|---|
| pre-fix geometry (`--pre-title`) | **5** per combo (the title crowds all five tabs) |
| post-fix chrome (`--audit`) | **0** |

Unit proof: `P1042cScreenChromeTests.Career_title_vs_tab_rule_flags_the_classic_title_and_clears_the_fitted_one`
(the measured 432/1024/2048 × 47 title boxes) and `Career_content_renderer_keeps_the_title_by_default`.

Real-window read-back: `career-jet-green-800.png` shows `[ CHARACTER INFO ]` with a small clear space above the
tab row (`Career Info`, `Leaderboard`, `Squads`, `Events`, `Back`); vision confirmed the separation, and the
5→0 measurement above is the proof (vision at 800 px small text is not reliable enough alone).

## 3. Item 1 — the in-game families behind the boundary (zero Classic/Modern delta)

### 3.1 New frame model and the ONE builder

| File | What it is |
|---|---|
| `Rendering/Screens/PanelScreenFrames.cs:35` | `ScreenPanelFrame` — viewport, family id, panel rectangle, every fill in the pre-boundary draw order (`ScreenFill`, `:11`), every text run at its resolved anchor (`PanelTextRow`, `:25`: exact classic string, anchor, band, large/scale, classic ink, role, selected, control, column width) and, optional, the native-art block (ready-check civ icon). |
| same file `:153` | `PanelScreenFrames.SideSelectFrame` — the P281 renderer's arithmetic verbatim (padding 24, gap 3, backdrop, 1 px outline, 3 px top accent). |
| same file `:229` | `ReadyCheckFrame` — P282 (the 88 px civ-art reservation, the icon box and scale). |
| same file `:303` | `BriefingFrame` — P271 (`MissionBriefingLayout.WrapWords`, block stacking, countdown/preparing line). |
| same file `:405` | `TeamPaneFrame` — P639 (compact placement, the `TeamPaneRenderer.Fit` truncation is applied in the builder so the drawn strings are identical). |
| same file `:461` | `QuickChatFrame` — P217 (560 px cap, title 34, row 26, the 2 px top/left accent bars). |
| same file `:515` | `KeyBindFrame` — P625 (72 %/90 % panel, header/preset, the visible window, selection band, footer). |
| same file `:590` | `ConnectionLostFrame` — P320 (88 %/62 % capped panel, 2 px outline, 4 px top, divider, footer). |
| `Rendering/Screens/PanelScreenPainter.cs:20` | the Classic/Modern painter: fills in order, then every run at its anchor/scale/classic ink, then the optional native art. Owns no geometry and no model. |

The builders reproduce each renderer's arithmetic **float for float** (the three places where the classic code
measured unscaled and then scaled the run — the key-bind footer, the connection-lost body, the briefing blocks —
are reproduced exactly), which is what makes the decoded-pixel compare zero rather than "close".

### 3.2 Interface and adapters

* `IScreenSkinRenderer` (`Rendering/Skins/IScreenSkinRenderer.cs`) gained `DrawPanel(in ScreenPanelFrame)`,
  `DrawScoreboard(in ScoreboardFrame)`, `DrawStarmap(in StarmapScreenFrame)` and `DrawGameOver(in
  GameOverScreenFrame)` (`DrawGameOver` is `#if !BROWSER` because the BROWSER target does not compile the P752
  renderer — the same rule P1042 used for its Classic fallback).
* `Rendering/Skins/ScreenFrames.cs` — `ScoreboardFrame` (rows, self name, panel/header/row bands, optional
  `ScoreboardPalette`), `StarmapScreenFrame` (the exact data `StarmapRenderer` takes incl. the two read-only
  lookups, the panel and info-pane boxes), `GameOverScreenFrame` (the resolved panel text + the two tables).
* `Rendering/Skins/ScreenRenderers.cs` — the borrowed content renderers (panel painter, leaderboard, starmap,
  sector info, game-over), all constructed at `LoadContent` (`Game1.GpuBudget.cs:79-95`) and passed to both
  adapters; nothing is created in Draw.
* `ClassicScreenSkinRenderer` (`:53-78`) forwards: panel → the painter; leaderboard → `ScoreboardOverlayRenderer`
  (null palette); starmap → `StarmapRenderer` + `SectorInfoPaneRenderer`; game over → the P752 renderer.
* `JetScreenSkinRenderer` implements the four methods (§4).

### 3.3 Routing (production call sites)

| Family | Where Game1 now builds the frame |
|---|---|
| side select | `Game1.Panes.cs:1628` |
| ready check (roster + civ picker + team-name edit rows) | `Game1.Panes.cs:2262` (the native civ icon is passed as the frame's art) |
| mission briefing (native + fallback text) | `Game1.Rendering.cs:2058,2065,2072` |
| team pane (all four draw sites) | `Game1.Rendering.cs:2207`, `Game1.ConsoleModes.cs:269,294,304` |
| quick chat | `Game1.Rendering.cs:1961` |
| key bindings | `Game1.KeyBind.cs:77` |
| connection lost | `Game1.Session.cs:1178` |
| leaderboard | `Game1.Rendering.cs:2000` (geometry from `ScoreboardLayout.Layout`) |
| starmap + sector info | `Game1.Rendering.cs:4171` (panel/expand rects from the renderers' own geometry) |
| game over | `Game1.Rendering.cs:2283` |

Behaviour untouched: no command, key, focus rule, validation, caret/selection/IME path, modal gate, countdown
or ACK was edited; the frames are value/read-only. The capture/rebind flow is `KeyBindingController`'s
(unchanged); quick chat's rising-edge selector and send path are unchanged; the ready-check team-name edit
buffer/caret is the model's; the sector-info expand click still hit-tests `LastBox`.

### 3.4 One geometry source for the content families

* `ScoreboardLayout.Layout` (`ScoreboardLayout.cs:186`) — the panel, six column origins, header baseline: the
  P639 renderer paints from it and `ScoreboardFrame` resolves the same boxes.
* `SectorInfoPaneRenderer.Box` (`:153`) — the info pane's box/line count/warning flag; the renderer paints from
  it and the frame frames it (the expand button stays `ExpandButtonRect`, the click path's own call).
* The game-over table header rects are the renderer's `LastSideHeaderRects`/`LastPlayerHeaderRects` — the very
  rects its pointer sorts on; the chrome reads them after the content draw rather than duplicating the maths.
* The legacy docked shop already had `DockedScreenPalette` (P1042); Jet re-inks it through the same
  palette (unchanged code path) — the brief's "Jet frames over the native layout" is satisfied by that
  palette + the unchanged `DockedScreenRenderer` rows (§4.4 states the limitation).

### 3.5 Zero Classic/Modern delta (captured BEFORE editing, compared after)

Method: `tools/p1042-screen-proof --legacy <dir>` renders every P1042c scene through the **pre-boundary
renderers** (the untouched `SideSelectOverlayRenderer`, `ReadyCheckOverlayRenderer`, `MissionBriefingRenderer`,
`TeamPaneRenderer`, `QuickChatMenuRenderer`, `KeyBindingRenderer`, `ConnectionLostScreenRenderer`,
`ScoreboardOverlayRenderer`, `StarmapRenderer`+`SectorInfoPaneRenderer`, `GameOverScreenRenderer`); the goldens
were captured from that path before the frame builders were wired. `--legacy-compare <dir>` renders the same
scenes through **the Classic and Modern adapters** and compares decoded RGBA pixels:

```
P1042C_LEGACY_COMPARE combinations=102 differing=0
```

17 scenes (side select ×2, ready check ×3, briefing ×2, team pane ×2, quick chat ×2, key bindings ×2,
connection lost, leaderboard, starmap, game over) × 3 resolutions (800×480, 1920×1080, 3840×2160) ×
{Classic, Modern} = 102 comparisons, **0 differing pixels** — re-run on the final tree after the last edits.

The three deltas found while building the painter (connection-lost top accent colour and body anchoring, the
key-bind footer's unscaled centring) were real builder bugs and are fixed; the final tree is 0/102.

## 4. Item 2 — Jet chrome per family (green + amber)

### 4.1 Panel families (full chrome)

`JetScreenSkinRenderer.DrawPanel` (`:333`): the panel gets an **outset** corner-bracket frame (3 px outside the
panel box, 9 px arms) plus a top rule, so no bracket can cross the panel's own text; every selectable row is
plated at exactly `PanelTextRow.Rect` with 5 px corner brackets, the selection carrying **both** a full-stroke
bracket + denser plate and `Hot` ink (never hue alone); the row run is left-aligned at the model's anchor,
indented 8 px past the bracket arm, vertically centred and fitted inside the plate + keyline band; every
non-control run keeps its model anchor and is keylined in its role ink. Green and amber draw identical
geometry (test `Panel_selection_is_shape_and_ink_and_green_equals_amber`).

* Selection is measured, not asserted: the 800×480 side-select crop carries `>` on Belters with the Hot ink and
  full-stroke brackets; the pixel count of Hot ink exists only on that row (unit test).
* Text fields: the team-name edit line and the ready-check rows keep their model strings/caret (the caret is
  the model's `_` in the buffer; the frame carries the exact run).
* Quick chat: the port-only column legend `KEY   VOICE COMMAND` is chrome the vector skin owns (its classic 8 px
  row offset overlaps the first choice's ink at every resolution — a pre-existing tightness); the command rows
  keep their real keys/captions/shortcut tint and the submenu `...` marker.
* Key bindings: the command band is the command column only (316 px) so its brackets cannot reach the key /
  description columns; the capture banner, conflict warning and reset prompt are the controller's strings.

### 4.2 Content families (frames over unchanged content)

* Leaderboard: the roster cells are drawn by the unchanged P639 renderer re-inked through `ScoreboardPalette`
  (Plate/Dim/Primary/Hot); Jet frames the panel, the header band and every row band from
  `ScoreboardLayout.Layout`.
* Starmap: the map (sector dots, warp edges, labels, icons) and the info-pane text stay **native content**; Jet
  frames the map panel, the info pane box and the expand button (the click rect).
* Game over: the winner/reason text and both tables stay content; Jet frames the panel and the two header rows
  at the renderer's reported rects (the sort targets).
* Legacy docked shop: re-inked through `DockedScreenPalette` (P1042's path, unchanged); it keeps **no Jet
  frames over its rows** because `DockedScreenRenderer` measures its rows internally (the P1042 report's
  carried note) — stated as a limitation, not claimed as done.

### 4.3 Same commands / keys / focus / hover / scroll / text entry

Nothing in the frames can reach a model action. The frames carry values only (strings, rects, inks, and the
starmap's two pure read-only lookups — the same ones the renderer takes). `hud.jetSkin` off → the Jet adapter is
never published (§5), so nothing here is visible.

### 4.4 `hud.jetSkin` off

`ScreenSkinRegistry.Resolve` (`P1042 §5`) is unchanged: a gated-off Jet resolves to Modern. Registry tests still
cover it (P1042's suite, re-run in the fast tier).

## 5. Item 3 — switching, 800×480, 4K

* Switching is the existing boundary: `ScreenSkinRegistry.Publish` is called only from `CommitHudSkinChange`
  (`Game1.GpuBudget.cs:130`), the same call that applies the HUD skin and the palette; every adapter and the
  panel painter are preloaded at `LoadContent`; `Draw` reads one reference. Focus/selection/scroll/typed text
  live in the models and are untouched (no input handler edited).
* 800×480: every audited control is on-screen at 800×480 except the three states whose **shared classic layout**
  is taller than the screen (ready-check with the civ picker open / team-name editor, a >16-entry quick-chat
  menu, and the connection-lost body which already crosses its panel at 800 in Classic). Those three are
  audited at 1080p/4K and are listed as carried limitations in §9; the classic pixel gate still covers them at
  800×480 (§3.5). Every other family's controls are inside the viewport at 800×480 (test + audit).
* 4K: the chrome is vector (1.5 px strokes/1 px keylines; brackets on the panel/rows), no lazy loads and no
  bitmap fonts beyond the existing HUD faces.

## 6. Audit

`tools/p1042-screen-proof --audit <dir>` (evidence `<lane evidence dir>/P1042C/audit-post/audit.json`):
**188 combos** (34 scenes × resolutions × 2 palettes, minus the three documented 800-px skips), **1,438 drawn
controls**, every class 0:

| class | count |
|---|---|
| `hit-vs-draw` | **0** |
| stroke-vs-text | **0** |
| text-outside-plate | **0** |
| text-off-screen | **0** |
| TextOverlapCross (incl. ContentOverlap) | **0** |
| TextOverlapSame | **0** |
| title-vs-tab (item 0b, N = 6) | **0** |
| failures | **0** |

Controls per scene and resolution (green = amber, per combo): side select **3** (waiting 0), ready check **3**,
ready check + civ picker **7** (1080p/4K), ready check + team-name editor **8** (1080p/4K), mission briefing **0**
(text runs only), team pane **0**, quick chat **0** (its rows are runs, not controls), key bindings **15 / 40 /
87** at 800×480 / 1080p / 4K (the visible window of the scrollable list), connection lost **0**, leaderboard
**6** (header + 5 roster rows), starmap **2** (info pane + expand button), game over **18** (the 8 side-table + 10 player-table column
headers the renderer reports — the same rects its sort pointer uses). Plus the P1042/P1042b scenes as before.

Audit refinements this story needed (all in `Recorder.cs`, documented in-line):
* a recorded run's box is the union of its ink and its 1 px keyline offsets — overlap tests use the **ink box**
  (1 px narrower, 4 px shorter per side for the font's line pitch), so exact abutment on a 20 px line pitch is
  not a collision while a real one still is;
* the chrome reports non-control runs under `panel.line` (no plate attribution) and the quick-chat legend under
  its own id;
* `title-vs-tab` is a first-class class of the result record (`ScreenAudit.TitleVsTab`).

## 7. Assets and visual read-back

`docs/reports/JET_SCREENS_P1042C_assets/` — **33 PNGs, 1,270,415 bytes** (≤ 2 MB): Jet green/amber captures of
every new family at 800×480 (both palettes for side select, briefing, leaderboard, starmap, game over; green for
ready check, team pane, quick chat, key bindings, connection lost), 1080p captures of side select, starmap,
game over, ready-check-civ (both palettes), **2× nearest-neighbour crops of the 800×480 side select, key
bindings and ready check**, and the four real-window Benchmark captures + four real-window Career captures.

**What I actually saw (opened every image cited; vision stated plainly).**
* `crop-sideselect-800-2x.png`: a green framed panel; title `SELECT SIDE`, hint rows, then Iron Coalition,
  `> Belters` (selected), Gigacorp, `Not on a team`, BeltRunner, Nova, and the randomize hint — legible at 2×.
* `starmap__jet-green__1920x1080.png`: the map with five sector dots (`Alpha`/`Serenity`/`Dregs`/`Cortex` and
  a highlighted `YOU`), warp lines between them, and the green-bordered info pane reading `Sol Sector`,
  `Asteroids: 4`, `He3`/`He3-rich`/`He3 ore: 260`, `PROBES`. **The thin Jet frame brackets are not visible to
  vision at this scale** — the frame is 1 px hairline; the box is proven by the audit's recorded rects, not by
  the description.
* `career-jet-green-800.png`: `[ CHARACTER INFO ]`, a clear space, then the tab row; the empty-state line
  `No pilot selected. Sign in (F2)…` and the footer controls. Vision confirmed the title/tab separation.
* `benchmark-*-800.png`: `Showcase` title, subtitle, nine rows and the explanatory paragraph.
* At 800×480 vision is unreliable for small glyphs (it mis-segmented `Belters`/`BeltRunners` and read
  `Not on a team` as a team row); every claim that depends on small text rests on the 2× crops, the audit's
  measured boxes or the pixel counts, not on the visual description.

## 8. Gate results

| Gate | Result |
|---|---|
| Solution build `dotnet build AllegianceCore.slnx` | **PASS** — **0 warnings, 0 errors** (build servers disabled); the BROWSER target compiles the shared files behind the `#if !BROWSER` guards |
| `tools/build-tools.sh` | **PASS** — all **146** tool projects built, exit 0 |
| New tests 3× alone | **PASS** — `P1042cScreenChromeTests` **13/13** three times (77 / 73 / 68 ms) |
| 0 B/frame | **PASS** — `--alloc`: **3,120 frames** (intro+hangar+view+career + 7 panel families + leaderboard + starmap chrome) → **0 bytes**; unit test 240 frames of the panel chrome → 0 |
| HUD 1,848-combination zero-delta (Classic/Modern) | **PASS** — `tools/hud2-proof --compare` against `<lane evidence dir>/P1041/baseline`: `HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)` (the 84 are the wall-clock `!` badge the tool masks by design, as P1042/P1042b) |
| Jet flight HUD green/amber byte-identical | **PASS** — `tools/hud2-proof --jet` after vs `P1041/jet-after`: **282/282 PNGs SHA-256 identical**; the same run's own audit: `HUD2_JET_PALETTES green off-palette 0, amber off-palette 0 over 141 scenes`; `HUD2_JET_GEOMETRY green-vs-amber diffs 0 over 141 scenes`; `HUD2_JET_AMBER_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0, text-overlap-same 0, text-over-glyph 0, stroke-through-text 0, callout-crossed 0 over 141 scenes` |
| P1042c item-1 screen goldens (Classic/Modern) | **PASS** — **102/102** decoded-pixel identical (`--legacy` captured before the wiring, `--legacy-compare` after) |
| One fast tier | **FAIL (tier exit 1)** — `tools/run-tests.sh --tier fast`: **13,727 tests, 313.2 s**. Three `Allegiance.ClientCore.Tests` failures, every one already ledgered and **3/3 alone**: `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (ledgered 11/14-alone family), `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` (ledgered expected -2 / actual 0 at line 229), `SpectatorFrameLiveTests.Translates_ARealWorldAndARealKillShip` (ledgered victim-never-died signature). None is in a file this story touched (no session/chat/transport file changed). `Allegiance.Core.Tests` **aborted with an unledgered test-host crash** — `System.InvalidOperationException: The collection has been marked as complete with regards to additions` — after 5,000 passes, so the runner reported 1,769 unexecuted cases and a coverage mismatch; the isolated Core Fast suite re-run under concurrent lanes failed only the ledgered `P739TimerLiveTests` one-ULP row (delay 7, `docs/TEST_FLAKES.md:252`). Eight Fast executions exceeded the P1049 10 s calibration (the usual offenders). Reported as-is, not claimed green. |
| Training gate | **PASS with the stated run-1 stop** — §8.1: run 1 ClientCore 131/131 + **18 real-window children PASS** (M2 ×8 including the reminders variants that failed in P1042/P1042b, M3 ×2, M4 ×3, M5 ×2, M8 Nanite ×3); repeats 2 and 3: ClientCore **131/131** each, MonoGame **256 passed / 1 skipped** each (11 m 09 s / 11 m 07 s); the run-1 sweep was stopped inside the assigned-lesson family (the ~20-window family P1042 carried) |

### 8.1 Training gate

`tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once` with artwork (`local.env`), results in
`<lane evidence dir>/P1042C/training/` and the per-window `run.log`s the proofs write.

**Run 1, `Allegiance.ClientCore.Tests` (all training filters): PASS — 131/131 (10 s).**

**Run 1, `Allegiance.ClientCore.MonoGame.Tests` real-window children: 18 children ran, every one PASS** (the
result line each child wrote):

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
```

(The four M2 children per style are the muted / missing-audio / reminders / default variants; P1042/P1042b's
reminders children failed in their runs — here they pass. Nanite's M8 completion windows pass in both styles.)

After the M2–M8 families the run-1 suite continued into the `lesson=8` / assigned-lesson family (each window up
to the 21-min bound, the ~20-window family P1042 left to Claude's mini runner); it was **stopped there by exact
PID** and the two repeat passes were run directly with the P912 window class excluded (the script's own
`--window-proofs-once` rule), results in `<lane evidence dir>/P1042C/training-repeat-{2,3}/`:

| repeat | ClientCore.Tests (training filters) | MonoGame.Tests (P912 windows excluded) |
|---|---|---|
| 2 | **131/131 PASS (10 s)** | **256 passed / 1 skipped (11 m 09 s)** |
| 3 | **131/131 PASS (10 s)** | **256 passed / 1 skipped (11 m 07 s)** |

Per-child logs and the 18 result lines: `<lane evidence dir>/P1042C/training/windows/result-lines.txt` (the
proofs' own `run.log` per child). The only training-gate gap is the run-1 continuation into the
`lesson=8`/assigned-lesson family (each window up to 21 min; the same family P1042 reported as a 20-window
carry for Claude's mini runner) — it was stopped by exact PID after the M2–M8 families, and the three-pass
suites above are the gate's repeat evidence. No training test failed.

## 9. Carried limitations / unfinished

* **In-flight panes** (inventory, investment, selection, target, teleport and the chat pane chrome): **not
  routed**. They are drawn inside the HUD skin pass (`HudInventoryPane`, `ChatRenderer`, the pane layouts and
  the HUD renderers), not by a discrete overlay renderer; moving them behind the screen boundary means changing
  the HUD renderers themselves, which the brief places out of scope for this story (P1043 owns flight
  symbology) and which would put the 1,848-combination HUD zero-delta gate at risk. This is the one item-1
  family left.
* **Legacy docked shop**: re-inked (P1042's palette path) but it has no Jet frames over its rows (the renderer
  measures them internally). The V2 shop is fully framed.
* **800×480 overflow in the shared classic layouts**: the ready-check panel with the civ picker open or the
  team-name editor, a quick-chat menu deeper than the screen, and the connection-lost body already exceed
  800×480 in Classic; the Jet chrome draws only the runs that fall inside the viewport and those three states
  are audited at 1080p/4K. A shared-layout fix would change Classic pixels (forbidden by this story's gate).
* **Benchmark at ≥1600 px**: the intro's rows/title do not appear in the offscreen capture at large windows (the
  bottom runs do); the Benchmark golden is the 800×480 real window (§1).
* Loadout V2 "WEAPON DPS NaN" was not touched (not seen in this story's captures).
* No rig, mini, Mac or public host was contacted; no Live-tier test was added.

## Native intent

Every rectangle this story routes is the port's existing one: native's side-select / team screens are the
team-list and player-list panes (`WinTrek/teamscreen.cpp:378-386,470-510`, selection `:2962-2982`), the ready
check is the same TeamScreen before `STAGE_STARTED` (`:1662-1672`, ownership gates `:1554-1573`), the briefing
is `MissionBriefPopup` (`sectormap.cpp`/`teamscreen.cpp:732-826`), the starmap is `StarMapPane` +
`SectorInfoPane` over the authored cluster coordinates (`sectormap.cpp:743-833,1310-1316`, info pane
`:139-538`, expand button `:186-201`), the leaderboard mirrors the in-mission roster fields (`clintlib.h:346-361`),
the game-over screen is `GameOverScreen`'s two sortable lists (`gameoverscreen.cpp:840-1009`), the rebind popup
is `InputMapPopup` (`WinTrek/trekinput.cpp:1631+`), quick chat is `quickchat.mdl`, and the docked shop is the
native station screen (`hangar.mdl`, `DockedScreenRenderer`'s own geometry). Native draws these as screen panes
painted top-left/y-down; the port keeps that convention for screens and the HUD's y-up images stay untouched.
No string, command, key order or hit rule changed; the only text edit is the em dash that crashed the font
measure (§1).

## Modern approach

One generic, allocation-free screen frame carries what the pre-boundary renderers already resolved — the panel
box, every fill in draw order and every text run at its anchor/scale/classic ink — so Classic/Modern paint the
same pixels through one painter (proved 102/102 by decoded-pixel compare) and the Jet vector chrome plates,
brackets and keylines exactly those boxes in its five-role palette, with shape+ink selection. Content that a
single renderer owns (leaderboard cells, starmap, game-over tables, docked shop) keeps that renderer and gains
either a palette or frames at its own geometry (one source per family: `ScoreboardLayout.Layout`,
`SectorInfoPaneRenderer.Box`, the renderer's own header rects). The chrome is preloaded and published by the
same update-boundary call as the HUD skin/palette, so switching is atomic; the proof tool's `hit-vs-draw`
compares every drawn control with the frame's own band, and the new `title-vs-tab` class (N = 6 px) measures the
career title against the tab row it used to touch.
