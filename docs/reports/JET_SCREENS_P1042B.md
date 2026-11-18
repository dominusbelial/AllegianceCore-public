# P1042b — Jet screens part 2: Buy/Def doubled-text fix + the pre-game screen families

**Task brief:** `P1042B_BRIEF.md` (P972 slices 1F/3F, continuation; part 2 of 3).
**Worktree:** Hermes lane worktree `wt-p1042b`, branch `hermes/p1042b` off Claude's master `ac673c39`.
Nothing committed — every change stays in the working tree (`P1042B_BRIEF.md` is untracked and is NOT part of the change set).
**Local env:** `local.env` → artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src` (read-only).

## Status

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 0 | Report first, kept current | DONE | this file |
| 1 | Doubled Buy/Def text: root cause + fix + `TextOverlapCross` rule (pre-fix flags, post-fix 0) + 2× crops | DONE | §1 |
| 2 | Route pre-game families through `IScreenSkinRenderer`, zero Classic/Modern delta | DONE (Benchmark caveat) | §2: 26 real-client goldens ×2 resolutions, **0 differing pixels** |
| 3 | Jet chrome per family (green + amber) over the same rectangles | DONE (slideshow hint only; Benchmark caveat) | §3 |
| 4 | Switching / 800×480 / 4K | DONE | §4 |
| A | Audit + evidence (0 for every class, counts per screen) | DONE | §5: 96 combos, every class 0 |
| G | Gate: zero-delta, Jet flight byte-identical, 0 B/frame, tests 3×, build 0/0, tools build, fast tier, training gate | DONE except the training-gate gap | §6 (HUD 1,848 zero-delta PASS, Jet 282/282 identical, fast tier one ledgered flake; training §6.2) |

---

## 1. Item 0 — the doubled Buy/Def text

### 1.1 Root cause — it is port-drawn text, not native art

The `Buy`/`Def` cells are the two **port-only verbs**: native's hangar has no Buy/Default pane
(`HangarScreen.cs:121-141`, "native has NO hover text and no button for Buy/Default"), so the port
draws the cells itself in `HangarScreenRenderer.DrawPortVerbs` (`HangarScreenRenderer.cs:154-172`):
a flat fill plus the label in the fallback HUD face at `rect.X + 2, rect.Y + 1`. Jet then drew its own
plate **and its own keylined label at the same `HangarScreen.PortVerbRect`** (`JetScreenSkinRenderer.cs:344-345`,
`DrawPortVerb` at `:364`), so two copies of the word were painted on top of each other — "BuBuy" / "DeDef"
in the 2× crop. The same defect existed on the **native hover string**: the shared renderer drew
`DrawHoverText` (`HangarScreenRenderer.cs:175-200`) and Jet drew the same string again at the same pane.
The P1042 audit could not see it because it recorded only Jet's primitives, never the content text the
shared renderer paints inside the same control.

### 1.2 The fix — the content renderer stops painting what the chrome paints

* `HangarScreenRenderer.Draw` gained `bool drawPortChrome = true` (`HangarScreenRenderer.cs:82-83`).
  When false it skips `DrawPortVerbs` **and** `DrawHoverText` (`:101-105`) — the two pieces the Jet chrome
  owns at the same rectangles; the native `hangar.mdl` artwork, faces, launch frames, badge and hover
  semantics are untouched.
* `JetScreenSkinRenderer.DrawHangar` passes `drawPortChrome: false` (`JetScreenSkinRenderer.cs:312-321`) and
  keeps its plate + keylined `Buy`/`Def` and hover runs, so **exactly one label** is visible.
* Classic/Modern keep the default (`true`) → their drawing is unchanged (proved by the hangar goldens in
  §2.3/§6 and by the `drawPortChrome` default assert in the new test).

### 1.3 The audit rule that catches the class (`TextOverlapCross`)

* `IScreenRectAudit` gained `Content(family, id, box, text)` (`IScreenSkinRenderer.cs:85-97`): a text run the
  shared **content** renderer painted inside a control the chrome also draws, reported with the same control id.
* `HangarScreenRenderer` reports the two port-verb labels (`HangarScreenRenderer.cs:163-166`) and the native
  hover string (`:195-198`) with their measured boxes when a proof surface is installed (production passes
  `null`; cost is one type test per run).
* `ScreenRecorder` stores them (`tools/p1042-screen-proof/Recorder.cs:34-36, 88-90`) and `ScreenAudit.Evaluate`
  counts chrome-text-over-content-text of the same control as a cross overlap (`Recorder.cs:227-246`):
  `TextOverlapCross` includes it and `ContentOverlap` reports the subset.

### 1.4 Pre-fix flags, post-fix 0

`tools/p1042-screen-proof --audit`, 7 hangar scenes × 3 resolutions × 2 palettes:

| build | `TextOverlapCross` | `ContentOverlap` | failures |
|---|---|---|---|
| pre-fix (`<lane evidence dir>/P1042B/audit-prefix.json`) | **44** | **44** (2 Buy/Def at 800×480 and 1920×1080 — the cells are 37/83 px wide; 0 at 4K where the 138 px cell keeps the two labels apart — plus 1 doubled hover line on every hover scene and resolution) | 44 |
| post-fix (`<lane evidence dir>/P1042B/audit-postfix.json`) | **0** | **0** | **0** |

Samples from the pre-fix run (green, 1080p, launch hover):
`overlap-content: hangar.Buy chrome 'Buy' over content 'Buy'`, `…hangar.Default chrome 'Def' over content 'Def'`,
`…hangar.hover chrome 'Launch ship' over content 'Launch ship'`.

### 1.5 Evidence (2× nearest-neighbour crops, both palettes)

| file | what it shows |
|---|---|
| `buydef-prefix-jet-green-1080-2x.png`, `buydef-prefix-jet-amber-1080-2x.png` | the two cells read **BuBuy** / **DeDef** — two labels on top of each other |
| `buydef-postfix-jet-green-1080-2x.png`, `buydef-postfix-jet-amber-1080-2x.png` | one **Buy** / **Def** per cell |
| `buydef-prefix-jet-green-800-2x.png`, `buydef-postfix-jet-green-800-2x.png` | the same pair at 800×480 |
| `hover-prefix-jet-green-1080-2x.png` | the hover line doubled (vision read "LaLaunch shipnip" — the two copies are visible even though the model mis-segments the glyphs) |
| `hover-postfix-jet-green-1080-2x.png` | one hover line |

Pixel proof of the fix's locality: pre→post Jet hangar diff at 1080p is **3,805 px, bounding box
(1685,1049)-(1851,1075)** = exactly the two port-verb cells (and on hover scenes the pane
(850,1018)-(1541,1053)); 800×480 idle: 670 px in (646,467)-(718,479). Classic/Modern hangar captures are
**bit-identical** pre→post (§6).

---

## 2. Item 1 — the pre-game families behind the boundary

### 2.1 The contract (new method, one frame type)

`IScreenSkinRenderer.DrawIntroView(in IntroViewFrame)` (`IScreenSkinRenderer.cs:52`) and
`DrawCareer(in CareerScreenFrame)` (`:57`). The frames (`Rendering/Skins/ScreenFrames.cs`):

* `IntroViewFrame` — view id, title/subtitle, `Rows` (`ScreenRow`: semantic `Label`, the exact classic run
  `Text`, its `Position`, its resolved `Rect`, scale, selection, role), `Lines` (`ScreenTextRun`), `Plates`
  (`ScreenPlate`), inks. Every run carries `ClassicInk` — the **exact** colour the pre-boundary code used — so
  Classic/Modern cannot drift; the role is only what a vector skin paints with.
* `CareerScreenFrame` — the P808c models, mode flags, cursor, description edit state, status, style.
* `IScreenContentRenderer` (`IScreenSkinRenderer.cs:66-69`) lets the vector skin borrow the P808c
  composition with its palette: Jet cannot reach a model action from it.

`ClassicScreenSkinRenderer` forwards both to `IntroScreenPainter.DrawView` / `.DrawCareer`
(`ClassicScreenSkinRenderer.cs:42-46`); `IntroScreenPainter.DrawView` (`IntroScreenPainter.cs:132`) paints the
rows/plates/lines in the pre-boundary order (fill plates → rows → lines → field underlines) and
`DrawCareer` (`:182-195`) calls the unchanged `CareerScreenRenderer.Draw`.

### 2.2 Routed families (all through the boundary)

| View | Frame builder |
|---|---|
| Find Server | `Game1.IntroScreen.cs:903` |
| Direct Connect | `Game1.IntroScreen.cs:961` |
| Single Player chooser | `Game1.IntroMenu.cs:126` |
| Skirmish setup | `Game1.IntroScreen.cs:986` |
| Training list | `Game1.IntroScreen.cs:1012` |
| Training slideshow (hint only; slide art stays content) | `Game1.IntroScreen.cs:1035` |
| Training suggestion | `Game1.IntroScreen.cs:1104` |
| Credits | `Game1.IntroScreen.cs:1133` |
| Help | `Game1.IntroScreen.cs:1167` |
| Options / privacy (`IntroView.Settings`) | `Game1.OptionsMenu.cs:349` |
| Create game wizard | `Game1.CreateGame.cs:139` |
| Host LAN / internet | `Game1.HostLan.cs:248` |
| Join link | `Game1.JoinLink.cs:196` |
| Benchmark menu | `Game1.Benchmark.cs:102` |
| Career / squads / events | `Game1.IntroScreen.cs:1040` (`DrawCareerScreen`) |
| Intro main menu | unchanged from P1042 |

Frame builders that are layout-driven keep the **IntroLayout rectangles** the drawing already used
(`CenteredListRow`, `Game1.IntroMenu.cs:72-83`); banded lists carry the selection plate's own rectangle as the
row band (`BandedRow`, `:85-89`); Career uses `CareerScreenRenderer.TabRect`/`PanelRect`
(`CareerScreenRenderer.cs:35,43`) — one rectangle source for the drawn tab and the chrome.

No command, key, focus rule, validation, caret/selection/IME path or native string was edited: the input
handlers (`UpdateIntro*`, `OnTextInput`, the join-password model) are untouched; the frames are value/read-only.

### 2.3 Classic/Modern goldens — real client, before vs after

Tool: `tools/p1042b-screen-walk` (new) runs the **shipping** `Game1` intro on a fresh profile, sets each
sub-view through the production `_introView` and photographs the real backbuffer with the game's own autoshot.
Two binaries: the pre-change tree (`git archive HEAD` into a scratch baseline) and the working tree,
both with the identical walk tool source, so the comparison is the drawing change only.

* 13 views × 2 HUD styles × 2 sizes = **52 captures**.
* **0 differing pixels in all 52** (decoded RGBA compare, not file bytes): FindServer, DirectConnect,
  SinglePlayer, SkirmishSetup, Training, TrainingSuggest, Credits, Help, Career, CreateGame, HostLan, JoinLink,
  Settings — Classic and Modern, 800×480 (offscreen SDL) and 1920×1080 (Xvfb).

Credits is wall-clock animated (`_introCreditsScroll += 0.35f` per frame); it compared identical because both
runs captured it at the same frame ordinal (the walk waits one draw after setting the view), so no
same-binary control was needed — if it had differed, the pre-change binary re-run would have been the control.

### 2.4 Benchmark — a pre-existing crash, honestly reported

The Benchmark sub-view **cannot be captured on either binary**: its native paragraph contains characters the
HUD font cannot measure (`×` in "150s ×3"), and `DrawIntroTitle` measures its subtitle
(`Game1.IntroMenu.cs:28` in the baseline tree). Both the pre-change and the changed client abort with
`ArgumentException: Text contains characters that cannot be resolved by this SpriteFont` at exactly that line
— it is a pre-existing defect, not introduced here. The Benchmark chrome is implemented and audited from a
fixture (§5) but has no real-window golden; that is the one item-1 gap.

---

## 3. Item 2 — Jet chrome per family

`JetScreenSkinRenderer.DrawIntroView` (`JetScreenSkinRenderer.cs:194`) and `.DrawCareer` (`:268`):

* title/subtitle keylined at the classic anchors (28 / 82), fitted to the 20 px gutter;
* every selectable row: a plate + corner brackets at **exactly** `ScreenRow.Rect`, label keylined and centred,
  fitted inside the frame band; the selected row is marked by **shape** (full `Stroke` vs `Hairline`, denser
  plate) **and** ink (`Hot` vs role ink) — never hue alone;
* focused text fields: a plate + keyline at the field's rectangle (no corner bracket — it would reach into the
  first glyph); every field still shows its classic caret/tipped label (the strings are the model's);
* other runs keep their classic anchors and strings, keylined in the palette; nothing is re-worded or re-ordered;
* Career: the unchanged P808c composition re-inked through `CareerScreenPalette` and the five tabs + table
  panel framed at `TabRect`/`PanelRect`.
* `hud.jetSkin` off → the Jet adapter is never published (registry rule unchanged, P1042 §5); nothing here is visible.

Green and amber draw identical geometry (test `View_selection_is_shape_and_ink_and_green_equals_amber`).
Selection measured, not asserted: in the 800×480 Find Server capture only the selected row carries Hot pixels
(124 px; 0 in the other rows); in the 1080p options capture only the selected row carries Hot ink.

**Production-path check (real client, not the fixture):** the shipping client was run with `hud.json`
`{"Skin":"jet"}` and the profile's `visuals.hud.jetSkin` on, at 800×480. Its own autoshots of Find Server and
Options contain exactly the Jet green inks — Primary `#79FFC0` (1,538 / 2,859 px), Dim `#3DA87B`
(4,899 / 2,316 px), Hot `#C9FFE3` (434 / 641 px), Keyline `#080B09` (7,711 / 12,902 px) — and **zero** Classic
title-teal pixels `#61E6D6`. The registry→painter path is therefore exercised by the real client for the new
families, not only by the proof tool.

---

## 4. Item 3 — switching, 800×480, 4K

* The frames are values; the adapters are preloaded at `LoadContent` and published by the same
  `CommitHudSkinChange` call as the HUD skin/palette (`Game1.GpuBudget.cs:79-93, 130`). No lazy load in `Draw`
  (Jet's new paths read one painter reference and one frame). Switching leaves focus/selection/scroll/typed
  text untouched because the frames carry no cursor or model reference (input handlers are not edited).
* 800×480: every row plate is the layout's own rectangle (P946 compressed column); Jet fits each label inside
  `JetScreenChrome.Inner` and never enlarges it above the classic scale. All controls audited on-screen (§5).
* 4K: 1.5 px strokes / 1 px keylines are vector; the 4K capture `jet-view-creategame__jet-green__3840x2160.png`
  shows hard-edged frames (1:1 crop checked).

---

## 5. Audit and evidence

`tools/p1042-screen-proof --audit` (`<lane evidence dir>/P1042B/audit-views.json`): 16 scenes × 3 resolutions × 2 palettes = **96 combos** (intro ×3, hangar ×4,
sub-view fixtures ×8, career ×1). Counts (all classes summed over the 96 combos):

| class | count |
|---|---|
| `hit-vs-draw` | **0** |
| `stroke-vs-text` | **0** |
| `text-outside-plate` | **0** |
| `text-off-screen` | **0** |
| `TextOverlapCross` (incl. `ContentOverlap`) | **0** |
| `TextOverlapSame` | **0** |
| failures | **0** |

Per-screen controls (green = amber): findserver 3, singleplayer 2, training 3, creategame 5, options 6, career 5;
directconnect/help/trainingsuggest have no selectable rows (lines only) → 0 controls.

`docs/reports/JET_SCREENS_P1042B_assets/` — **21 PNGs, 611,755 bytes** (≤ 2 MB): the 8 Buy/Def + hover crops,
7 Jet view captures, 2 career captures, a 4K view capture and four 2× crops of the 800×480 screens.

**Vision read-back, stated plainly:** I opened the images. At 800×480 the small font is unreliable — the model
misread `Def` as "Dot" in the post-fix 800 crop (the audit's measured boxes and the 1080p crop read "Buy"/"Def"
correctly). Every claim that depends on small text rests on the 2× crops, on the audit's measured boxes or on
pixel counts (hot-ink counts per row), not on the visual description. At 1080p the career composition (panel,
corner brackets, five tabs) matched the recorded rectangles.

---

## 6. Gate results

| Gate | Result |
|---|---|
| HUD 1,848-combination zero-delta (Classic/Modern) | **PASS** — 0 pixel, 0 semantic, 84 animation-only (the wall-clock `!` badge), 0 corrupt |
| Jet flight HUD green/amber byte-identical | **PASS** — 282/282 PNGs, SHA-256 identical |
| P1042 + item-1 screen goldens | **PASS** — 52/52 decoded-pixel identical (§2.3); P1042's own 96-image `--compare` set see §6.1 |
| 0 B/frame on every Jet screen | **PASS** — `--alloc`: 960 frames (intro+hangar+view+career chrome) → **0 bytes**; unit test 480 frames → 0 |
| New tests 3× alone | **PASS** — 11/11 ×3 (P1042bScreenChromeTests) |
| Solution build 0/0 | **PASS** — 0 warnings, 0 errors |
| `tools/build-tools.sh` | **PASS** — 146 tool projects |
| One fast tier | **FAIL (tier exit 1)** — 15,386 tests, 642.6 s: two already-ledgered failures (bot relaunch; P739 one-ULP delay 7) and 5 P1049 slow offenders; no failure in a file this story touched |
| Training gate | **PASS with one gap** — see §6.2 (real-window reminders child ledgered; run-3 MonoGame line unfinished) |

### 6.1 Gate detail

* **HUD 1,848-combination zero-delta** — baseline captured from the pre-change tree
  (`git archive HEAD` into a scratch baseline, commit `ac673c39+dirty`), then
  `tools/hud2-proof --compare` in the working tree: **`1848 combinations, 0 pixel diff(s), 0 semantic
  diff(s), 84 animation-only diff(s), 0 corrupt golden(s)`** (`HUD2_COMPARE_PASS`, `<lane evidence dir>/P1042B/hud-compare.log`). The 84 animation-only
  diffs are the wall-clock `!` badge the tool masks by design (P1042 §9 reported the same class).
* **Jet flight HUD green/amber byte-identical** — `tools/hud2-proof --jet` in the baseline tree then in the
  working tree: **282/282 PNGs byte-identical** (SHA-256 of the files), and every Jet flight audit class 0 over
  141 scenes (`HUD2_JET_CAPTURE_PASS`, `green-vs-amber diffs 0`; SHA-256 manifests `<lane evidence dir>/P1042B/jet-{before,after}-hashes.txt`).
* **P1042 screen set still deterministic** — `tools/p1042-screen-proof --compare` on the tool's own 204-image
  capture: **204 combinations, 0 differing**.
* **0 B/frame** — `--alloc`: **960 frames, 0 bytes** (intro + hangar + a 6-row sub-view + career chrome); the
  new unit test measures 480 frames of the same paths → 0. The career CONTENT painter is the pre-existing P808c
  renderer (it formats model strings when models are present); the chrome measured here is the Jet layer, the
  same scope P1042 used.
* **New tests** — `P1042bScreenChromeTests` **11/11 passed, three runs alone** (77/82/81 ms).
* **Build** — `dotnet build AllegianceCore.slnx` (build servers disabled): **0 warnings, 0 errors**.
* **Tools** — `tools/build-tools.sh`: **all 146 tool projects built**.
* **Fast tier** — `tools/run-tests.sh --tier fast`: **15,386 tests**, 642.6 s, **FAIL** with two failures
  (both already ledgered, neither in a file this story touched):
  * `Allegiance.ClientCore.Tests` —
    `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights(killPod: True, botCount: 3)`
    ("relaunched MCP bot must fight again", 16 s), ledgered at `docs/TEST_FLAKES.md:8909` with the same
    signature and timing; re-run **3× alone: 2/2 passed each (6/6/5 s)** → load flake.
  * `Allegiance.Core.Tests` —
    `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)`,
    the inclusive upper bound missed by one ULP: `7.0000000000000009` against `(6.9, 7)`, ledgered at
    `docs/TEST_FLAKES.md:252` with the identical value. Alone, the theory's three delay rows gave
    pass/fail/pass and then pass/pass/pass once the machine was quiet; a HEAD-archive tree in the same
    environment gave 3/3 in each of three runs, so the row is load-sensitive and its code path
    (`Allegiance.Core` timers) is untouched by this story.
  Five Fast executions also exceeded the calibrated 10 s limit (the P1049 offenders): `P814GunnerTests`
  17.3 s, `SC2NaniteSupportTests` 14.3 s, `P796VariedLoadoutTests` 16.1 s, the bot relaunch case 16.1 s,
  `P999CivStrategyTests` 23.4 s.
* **Training gate** — see §6.2.

### 6.2 Training gate

`tools/run-training-tests.sh --no-build --repeat 1` with artwork (`local.env`), bounded to 45 min as P1042 did:

| run / project | result |
|---|---|
| run 1 `Allegiance.ClientCore.Tests` (all training filters) | **126/126 PASS (12 s)** |
| run 1 `Allegiance.ClientCore.MonoGame.Tests` (training filter incl. P912 windows) | `Basic_flight_completes_with_muted_missing_or_repeated_narration(Classic, reminders)` **FAIL 8 m 52 s**; `(Modern, reminders)` **FAIL 8 m 52 s**; the muted / missing-audio children were still in their windows when the 45-min bound hit, so they produced no result line. Both failures are the already-ledgered `M2 9.2: native prompts missing or out of order` prompt-sequence child (`docs/TEST_FLAKES.md:9587`, same child and same 8 m 52 s in P1042/P945). |

Repeats 2–3 of the two client suites (same training filter, `sequential.runsettings`, artwork) with the
real-window classes named in the exclusion (`P912TrainingWindowTests`, `P918TrainingWindowTests`,
`P911TrainingArenaClientTests`):

| repeat | ClientCore.Tests | MonoGame.Tests |
|---|---|---|
| 2 | **126/126 PASS (16 s)** | **70 passed / 2 failed / 1 skipped** of 73, 32 m 19 s — the two failures are the P912 reminders children |
| 3 | **126/126 PASS (14 s)** | still running when the lane's time ran out (24 m 49 s, killed by exact PID) — no result line |

**Filter-precedence caveat, stated plainly:** the vstest `--filter` string `A|B|C|D&!x&!y&!z` binds the `&`
exclusions to the last `|` term only, so the P912 window children (2 × 8 m 52 s) still ran in repeat 2 — they
are exactly the two ledgered failures, and every other MonoGame training test (70) passed
(`<lane evidence dir>/P1042B/training-2-monogame.trx`). Repeat 3's MonoGame suite is therefore the training-gate
gap this lane leaves: its ClientCore half is 126/126 PASS; the muted / missing-audio window children of run 1
and the repeat-3 MonoGame line are for Claude's mini runner (they are real-window children, excluded from
repeats 2–3 by the brief's own allowance).

---

## Unfinished / carried

* Benchmark sub-view: implemented and audited, but no real-window golden because both the pre-change and the
  changed client abort on a pre-existing font-measure defect (§2.4). Not fixed here (not this story's scope).
* Training slideshow: only its hint goes through the boundary; the slide art remains native content by design.
* Loadout V2 "WEAPON DPS NaN" text was not touched (separate task); it was not seen in this story's captures.
* P1042c families (team/side select, briefing, starmap, scoreboard, key bindings, quick chat, in-flight panes,
  connection lost, legacy docked shop frames) are untouched.

## Native intent

The pre-game screens are native intro popups (`WinTrek/introscreen.cpp` — FindGames, CreditsPopup, the options
menu tree at `WinTrek.cpp:4429-4442`, the single-player/training entry at `introscreen.cpp:1221/:1558`) and the
career screens are the P808c port models of `leaderboardscreen.cpp` / `CharInfoScreen.cpp`. Native draws each
popup as panes over the backdrop; the port draws text at fixed anchors. This story keeps every string, anchor
and rectangle the port already had and only lets a skin paint around them; the hangar's Buy/Default are port
verbs with no native pane (`hangar.mdl` has none), which is exactly why their label was port-drawn text that
the shared renderer and Jet both painted.

## Modern approach

A second, generic screen chrome over the same frames as P1042: one `IntroViewFrame` display model per sub-view
(rows with their resolved bands, runs with their classic inks, plates), a classic painter that reproduces the
pre-boundary drawing byte-for-byte, and a Jet painter that frames/plates/keylines at the same rectangles in its
five-role palette with shape+ink selection. Career is a content composition re-inked through a palette and
framed at the renderer's own tab/panel rectangles. The audit now records content text as well as chrome text, so
a chrome label painted over a content label in the same control is a counted `TextOverlapCross` — the class that
catches the doubled Buy/Def defect and proves it fixed.
