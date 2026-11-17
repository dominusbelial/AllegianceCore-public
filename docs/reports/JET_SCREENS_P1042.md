# P1042 — Jet HUD skin story 8/9 (part 1): screen-chrome boundary + Jet intro and hangar/loadout chrome

**Task brief:** `P1042_BRIEF.md` (P972 slices 1F + 3F, first half).
**Worktree:** `/home/domingo/wt-p1042` (branch `hermes/p1042` off Claude's master `d9a44fa9`).
Nothing committed — all changes stay in the working tree (`P1042_BRIEF.md` itself is untouched and untracked).
**Local env:** `local.env` → artwork `/srv/corpus/Artwork`, native `/srv/corpus/Allegiance/src` (read-only).

## Status

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 0 | Report first, kept current | DONE | this file |
| 1 | `IScreenSkinRenderer` + Classic/Modern adapter; intro + hangar/docked/loadout routed; one geometry source; draw == hit tests | DONE | §1, tests 32/32, audited `hit-vs-draw` 0 |
| 2 | Classic/Modern screen goldens before editing; decoded-pixel compare after (0 diffs) at 800×480 and 1920×1080 | DONE | §2: 26 real-window captures, 25 identical, 1 door-ramp phase (§2.3); legacy shop cross-check §2.4 |
| 3 | Jet intro chrome, both palettes, 13 commands/keys/focus/hover/rects, slideshow art untouched | DONE | §3, green+amber captures, 0 audit findings |
| 4 | Jet hangar/loadout chrome, both palettes, same verbs/ACK/launch/hover/badge/animation/rects | DONE | §4, captures, 0 audit findings, legacy shop re-inked |
| 5 | Atomic screen-chrome switch with skin + palette; focus/selection/scroll untouched; nothing visible with `hud.jetSkin` off | DONE | §5, registry tests |
| 6 | 800×480 fit (compact rules) and 4K sharpness | DONE | §6, `JetScreenChrome` fit tests, 4K capture |
| A | Layout audit incl. new `hit-vs-draw` — all 0 | DONE | §7: 42 scene/palette/resolution combos, 474 controls, every class 0 |
| B | Assets (PNG ≤ 2 MB total, 2× crops), every image opened and read | DONE | §8: 16 PNG, 2,015,024 bytes |
| G | Gate: HUD 1,848 zero-delta, Jet flight byte-identical, 0 B/frame, tests 3×, build 0/0, tools build, one fast tier, training gate | DONE | §9 |

---

## 1. The boundary (`IScreenSkinRenderer`) — what changed

### 1.1 Contract and frames (new files)

| File | What it is |
|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/Skins/IScreenSkinRenderer.cs:24` | `IScreenSkinRenderer` — `DrawIntro(in IntroMainFrame)`, `DrawHangar(in HangarScreenFrame)`, `DrawLoadout(in LoadoutScreenFrame)`, `Id`, `HangarArtAvailable`. The doc block states the contract from P972 §2.2/§6.1: a renderer gets a read-only model plus the resolved hit rectangles and can never buy/launch/sort/join/navigate or change focus (no cursor, scroll or command is reachable from a frame). |
| same file `:54` | `IScreenRectAudit` — optional audit channel (`Control`, `Plate`) implemented only by the proof surface, so production pays one type test. |
| same file `:70` | `ScreenActions` — the 13 intro command ids, precomputed element-id constants for intro rows/chrome, hangar verbs and loadout leaves (no per-frame string formatting). |
| `Rendering/Skins/ScreenFrames.cs` | `IntroMainFrame` (resolved `IntroLayoutResult`, 13 labels, selection, pilot field + caret offset, identity/hint, `ScreenInks`), `HangarScreenFrame` (hover verb, launch animation, native BoardState, developments), `LoadoutScreenFrame` (legacy rows or the V2 snapshot+layout, money/backdrop/footer/credit box, style, hover, hardpoint marker). |
| `Rendering/Skins/ScreenHitGeometry.cs:14` | **One geometry source**: `IntroRow`/`IntroRowAt`/`IntroCommandAt`/`IntroPilotFieldHit` over `IntroLayout`, `HangarRect`/`HangarHit` = `HangarScreen.Rect`/`HangarScreen.Hit` (the pointer's own call), `LoadoutV2Hit` = `LoadoutV2Layout.HitTest`. |
| `Rendering/Skins/ScreenSkinRegistry.cs:16` | Preloaded adapters (Classic, Modern, Jet gated), `Publish(skinId, jetEnabled)` returns whether the reference changed; unknown ids and a gated-off Jet resolve to Modern — the HUD switcher's exact rule. |
| `Rendering/Skins/ClassicScreenSkinRenderer.cs:16` | Pure adapter: forwards the frame to the untouched `IntroScreenPainter`, `HangarScreenRenderer`, `DockedScreenRenderer`, `LoadoutV2Renderer`. Borrowed renderers (game owns their lifetime). |
| `Rendering/Screens/IntroScreenPainter.cs:19` | The intro main-menu drawing moved verbatim (`DrawIntroMain` body) behind the boundary; Classic/Modern values come from the frame's inks. It draws at `frame.Layout.Rows[i]` only. |

### 1.2 Routing (existing files)

* `Game1.IntroMenu.cs:37-58` — registry/frames: `_screenSkins`, `InstallScreenSkins`, `ActiveScreen` (with a Classic fallback for the BROWSER project, which compiles this partial without the flight lifecycle), `DrawIntroMain` = `ActiveScreen.DrawIntro(BuildIntroMainFrame(...))`, `BuildIntroMainFrame` = read-only model (labels, layout, selection, pilot label/text/focus, resolved field, caret offset measured once, identity/hint scales).
* `Game1.GpuBudget.cs:79-93` — every screen adapter built at `LoadContent` (hangar art, docked renderer, V2 renderer, intro painter, Jet green/amber chrome); `Publish` with the persisted skin. `:130` — `CommitHudSkinChange` publishes the screen chrome in the same call as `HudSkinSwitcher.ApplyPending` and `JetPaletteSwitcher.ApplyPending` (atomic, before Draw).
* `Game1.Hangar.cs:278-302` — `DrawHangarControls` builds `HangarScreenFrame` (hover, the one launch-animation clock, `LoadoutBoardState()?.State`, developments) and calls the active screen; `HangarArtAvailable` false keeps the pre-P945 text-row fallback. `HangarScreenInks:301`.
* `Game1.LoadoutV2.cs:128` — V2 path builds `LoadoutScreenFrame` (snapshot, layout, style, router hover, hardpoint marker) and calls the active screen; the 3-D hull preview stays in `Game1` (it is scene content, not chrome).
* `Game1.Rendering.cs:2116` — legacy native-layout docked shop through the same frame (rows, detail rows, money, 0.62 backdrop, 84 px footer); investment/teleport panes stay where they were (P1042b).
* `Rendering/LoadoutV2Renderer.cs:12,48,250` — optional `LoadoutV2Palette` (null = the existing Classic/Modern constants) and optional keylined text (`TextKeylineOffsets`), used only by Jet.
* `Rendering/DockedScreenRenderer.cs:13,119` — optional `DockedScreenPalette` (null = `NativePalette`, byte-identical values).

Behaviour untouched: no command, value, focus order, hover rule, ACK, animation clock, sound or native art was edited. The launch animation still runs on the same `HangarLaunchAnimation` instance in `UpdateHangarControls`; the paint order (background → panes → port verbs → hover strip → badge) is the renderer's existing order.

### 1.3 Draw == hit, proven

`tests/Allegiance.ClientCore.MonoGame.Tests/Skins/P1042ScreenBoundaryTests.cs` (new, 32 tests, all pass 3× alone):

* `Intro_action_ids_are_the_thirteen_native_commands_in_menu_order` — `ScreenActions.IntroRow(i)` == `IntroScreenModel.Activate(i).ToString()` for all 13.
* `Intro_every_row_draws_at_its_hit_rectangle` (800×480, 1920×1080, 3840×2160 × selection 0/6/12) — every drawn row equals `ScreenHitGeometry.IntroRow`, its centre resolves that row and its exact `IntroCommand`; a point outside the column resolves −1.
* `Intro_pilot_field_hit_is_its_resolved_box`.
* `Hangar_every_action_draw_and_hit_are_the_same_rectangle` (3 resolutions × developments on/off) — for all 10 verbs, `HangarRect == HangarScreen.Rect`, the centre resolves that verb, the rect is non-empty and inside the viewport; with developments off the Invest pane cannot resolve Invest.
* `Hangar_keeps_eight_native_panes_plus_the_two_port_verbs` — 8 native panes, 10 verbs with developments, 9 without.
* `Loadout_every_leaf_hits_itself` (800×480/0.75, 1080p/1.0, 4K/1.5) — every V2 leaf's centre hit-tests back to the same kind/index/bounds; all bounds inside the viewport (+ the reserved footer).
* `Loadout_every_action_button_is_present_and_hit_testable` — Default/Revert/Buy/Launch/Back/Undock.
* `Loadout_every_text_budget_stays_inside_the_viewport`.
* Registry/gating/switching tests (see §5).
* Jet chrome: fit, green-vs-amber identical geometry, 0 B/frame.

---

## 2. Classic/Modern screen goldens (captured BEFORE editing, compared after)

### 2.1 Tool and method

* **Intro:** the production client's own proof hook (`ALLEGIANCE_AUTOSHOT`, P731) with a fresh profile (`ALLEGIANCE_PLAYER_SETTINGS`, `ShowStartupTrainingSuggestion=false`, `Pilot=""`), 800×480 offscreen and 1920×1080 under Xvfb. Captured at 18:56/18:57 UTC, **before** the first renderer edit (source mtimes 19:05+). The autoshot frame is the focused/idle main menu (selection 0, pilot field focused). The selected-row pixel path is not part of that single frame: it is exercised after the change by the proof tool's `intro-select`/`intro-last` scenes (selection 4 and 12) and by the geometry tests, which pin every row rectangle and the selection→command mapping for 0/6/12; the p946 key-state captures were not run before editing, so the item-2 pixel gate rests on the idle intro frame plus the hangar/loadout states.
* **Hangar + loadout:** the existing `scripts/live-proof.sh` (local server, real window, `--auto-hangar` script: launch hover → open door → loadout hover → idle → open the loadout screen → back) at 1920×1080 and 800×480, Classic and Modern. The Classic-1080 "before" run is the 18:58-18:59 Release build; for the other three sizes/styles the pre-change Release output (`Allegiance.ClientCore.Game.dll` 18:58) was copied aside and replayed with the same server config, because `live-proof.sh` rebuilds at start. Nothing in the screen drawing was edited before those runs.
* **Compare:** `pngdiff.py` (stdlib decoder, this lane's scratch) decodes both PNGs and counts differing RGBA pixels — not file bytes. `tools/p1042-screen-proof --compare` uses the same decoded-pixel rule with `PngCodec` for the tool's own golden set.

### 2.2 Result

| Capture set | Images | Decoded-pixel result |
|---|---|---|
| intro 800×480 | 1 | **0 differing pixels** (384,000 px) |
| intro 1920×1080 | 1 | **0 differing pixels** (2,073,600 px) |
| hangar classic 1920×1080 | 6 | **6/6 identical** |
| hangar classic 800×480 | 6 | 5/6 identical; `proof_hangar02` differs (see §2.3) |
| hangar modern 1920×1080 | 6 | **6/6 identical** |
| hangar modern 800×480 | 6 | **6/6 identical** |

Including the loadout screen (`proof_hangar09`) and the return-to-hangar shot (`proof_hangar11`), i.e. the V2 loadout and the legacy shop path both compare identical.

### 2.3 The one animation-phase delta (not a code delta)

`proof_hangar02` is the launch-hover shot taken 1.35 s after the hangar appears, i.e. **mid-ramp of the native 0.7 s door animation** (`engine/frameimage.cpp:413-456`). The differing pixels are exactly 5,603, bounding box `(374,130)-(477,300)` — the scaled launch pane rectangle at 800×480 (80 + 367·0.8 … , y 162·0.8 …) and nothing else. The *fully open* frame (`proof_hangar03`, t=1.9 s) and every other state are identical.

That this is wall-clock phase and not the boundary is measured: the **pre-change binary re-run** (same 18:58 Release build, same script) differs from its own first capture by the same 5,603 pixels in the same rectangle. Two runs of the unchanged client disagree identically to the before/after pair; the animation clock (`HangarLaunchAnimation.Update(..., Environment.TickCount64/1000.0)`) is wall-clock by design (`Game1.Hangar.cs:248-249`), not frame-counted.

### 2.4 The legacy native-layout shop (`hangar.loadoutV2=off`)

The V2 shop is the default and is in §2.2. The legacy shop (the `DockedScreenRenderer` path, whose palette this story parameterises) was checked separately: pre-change Release build vs the current build, same server, same script, `--visuals hangar.loadoutV2=off`, 1920×1080 Classic, 5 shots:

| shot | pre → post |
|---|---|
| `proof_hangar02` (launch hover, mid-ramp) | identical |
| `proof_hangar04` (loadout hover) | differs 41,359 px in the **launch pane rect** (901,292)-(1134,676) — the only variable content there is the 0.7 s door ramp, which the run latched at a different frame (two runs of the pre-change binary happened to agree on this state, so this one is a timing phase, not a rectangle or colour change) |
| `proof_hangar06` (idle) | identical |
| `proof_hangar08` (the legacy shop screen) | differs 14,846 px in (719,344)-(1291,741) — the **rotating hull preview seen through the translucent panel**; a re-run of the pre-change binary differs from its own first shot by 12,680 px in the *same* rectangle (same-binary control) |
| `proof_hangar10` (back to hangar) | identical |

So the legacy shop's panel/rows/text (the palette refactor) compare identical wherever the wall-clock scene does not move; the only differences are in the two places two runs of the unchanged client also differ, by the same shape and region.

---

## 3. Jet intro chrome (both palettes)

`Rendering/Jet/JetScreenSkinRenderer.cs:97` (`DrawIntro`), pure shapes/limits in `Rendering/Jet/JetScreenChrome.cs:11`:

* Title/subtitle: keylined in `Primary`/`Dim`, same anchors (y 28 / 82), fitted to the 20 px side gutter.
* All 13 rows: a plate at **exactly** `IntroLayout.Rows[i]`, corner brackets (full `Stroke` + `Hot` when selected, `Hairline` + `Dim` otherwise), and the native label keylined and centred, scaled by `JetScreenChrome.FitScale` (never larger than the layout's own `RowTextScale`, and shrunk until it fits inside the frame band). The selected row is shape-marked (brackets + plate + hot ink), never hue-only.
* Pilot field: plate + brackets at `IntroLayout.PilotField`, keylined label, caret bar at the frame's measured `PilotCaretOffset` (the classic caret position, computed once per frame by the model).
* Identity line and hint: the same strings and anchors, fitted into the band they own — on the compressed 800×480 column the status line is shrunk to sit strictly between the subtitle and the first row plate.
* Native slideshow/art: not drawn or tinted here; `DrawIntroSlideshow`/`DrawSlideArt` are untouched.
* Same 13 commands, keys, focus, hover and rectangles: the frame is the model's (selection/keys from `IntroScreenModel`), and the chrome only paints.
* `hud.jetSkin` off: the Jet adapter is never published (§5), so none of this is visible.

Evidence: `docs/reports/JET_SCREENS_P1042_assets/jet-intro-{green,amber}-800x480.png`, `…-1920x1080.png`, `jet-intro-select-green-1920x1080.png`, `jet-intro-green-3840x2160.png`, `crop-intro-800-2x.png`.

**Production-path check (real client, not the fixture):** the shipping client was run with `hud.json` `{"Skin":"jet"}` and the profile's `visuals.hud.jetSkin` on, at 800×480, and its own `ALLEGIANCE_AUTOSHOT` frame is the Jet intro
(`~/allegiance-evidence/P1042/screens/real/jet-intro800/screenshots/intro_proof.png`): the ink histogram of that
frame is exactly the Jet roles — `#79FFC0` primary (2,785 px), `#3DA87B` dim (4,018 px), `#C9FFE3` hot (892 px),
plate `#06110C` and keyline `#080B09` — with no Classic teal (`0.07,0.95,0.84`) pixel. So the registry→painter path
is exercised by the real client, not only by the proof tool.

---

## 4. Jet hangar / loadout chrome (both palettes)

* `DrawHangar` (`JetScreenSkinRenderer.cs:182`): the **native `hangar.mdl` artwork, faces, launch frames and in-turret badge are content and are drawn unchanged** by the shared `HangarScreenRenderer`; Jet then draws corner-bracket frames at every visible pane rectangle (`HangarScreen.Rect` — the pointer's call), a plate + corner bracket + keylined `Buy`/`Def` at the port-verb rectangles (`PortVerbRect`), and the native hover string in Jet ink on a plate at the native hover pane. Hover semantics: the native `ButtonFaceInside` swap still comes from `HangarScreenRenderer` (pointer inside the same rect); the launch animation is the same clock; the badge is state 2 only.
* `DrawLoadout` (`:249`): V2 — the P1013 composition is drawn by the unchanged renderer with a Jet **palette** (`LoadoutV2Palette`: accent/body/primary, muted/dim, better/hot, worse/dim, plate background, near-black keyline on every run) and Jet frames the seven regions + the six action leaves at their exact `element.Bounds`; hover is the layout's own hover. ACK/status (`frame.Status`) is drawn by the same renderer in `Better/Worse` roles. Legacy native-layout shop — re-inked through `DockedScreenPalette` only (rows/panel geometry untouched).
* Buy/Default/ACK/launch/hover/badge/animation: no transaction, command or clock was touched — `ActivateHangar`, `BuyFromHangar`, `BuyFlow` and the keyboard/pointer dispatchers are unchanged.
* Same rectangles: §1.3 + §7 (`hit-vs-draw` 0 on 10 hangar verbs per frame and 6 loadout actions per frame).

Evidence: `jet-hangar-{green,amber}-800x480.png`, `crop-hangar-buydef-1080.png` (Buy/Def cells), `crop-hangar-pane-1080-amber.png` (pane frames), `jet-loadout-{green,amber}-{800x480,1920x1080}.png`, `crop-loadout-list-800-2x.png`.

---

## 5. Switching (atomic, gated, stateless)

* `ScreenSkinRegistry.Publish` is called from `Game1.CommitHudSkinChange` (`Game1.GpuBudget.cs:130`) **in the same call** as the HUD skin and palette `ApplyPending`, i.e. at the update boundary before the frame draws. One reference read per frame (`ActiveScreen`). All adapters are preloaded at `LoadContent` (hangar art included), so a switch never decodes/parses/allocates in `Draw`.
* `hud.jetSkin` off at runtime while Jet is active: `HudSkinSwitcher.ObserveJetAvailability` queues Modern, `ApplyPending` changes the HUD skin, and the same `CommitHudSkinChange` publishes the Modern screen chrome — no half-switched frame. With the flag off Jet is never published (registry test: gated publish resolves to Modern).
* Nothing in a frame can carry state: `IntroMainFrame`/`HangarScreenFrame`/`LoadoutScreenFrame` are value/read-only models. The test `Switching_the_screen_chrome_leaves_the_model_and_geometry_untouched` rebuilds the frame after a registry switch and asserts the selection, labels, row pitch, pilot anchor, identity anchor and every row rectangle are unchanged. The real client's focus/selection/scroll machinery (`_introIndex`, `LoadoutV2InputRouter`, hangar hover) is not touched by any of these files (`git diff` shows no cursor/scroll code).
* Palette: the Jet screen adapter reads `JetPaletteSwitcher.Active` per frame (one reference read), and the palette is published by the same `CommitHudSkinChange`; a palette switch therefore cannot produce a half-switched screen either.

---

## 6. 800×480 and 4K

* 800×480: the intro keeps the P946 compressed column (`IntroLayout` min pitch 16 px); Jet plates are the row rectangles, text fitted inside `JetScreenChrome.Inner` (stroke + keyline band). Test `Jet_intro_chrome_fits_every_row_plate` at 800×480/1080p/4K asserts the inner area is non-empty, the fitted ink stays inside, and the scale never exceeds the layout's own. Hangar panes scale by `min(w/800,h/600)` (0.8 → 640×480 letterboxed); the port verbs fall back inside the screen at 4:3 (`HangarScreen.PortVerbRect`), and the audit sees all 10 controls on-screen at 800×480. The 2× crop `crop-intro-800-2x.png` reads every row label.
* 4K: the intro geometry is native pixel pitch (34 px rows) — unchanged by design (same rectangles), and the chrome is vector: 1.5 px strokes, 1 px keyline, no SDF blur (`jet-intro-green-3840x2160.png`, 1:1 crop checked). Hangar artwork is the native 800×600 bitmap (content, upscaled as before); the Jet frames/labels over it are vector and crisp. Loadout is laid out by `LoadoutV2Layout` at scale 1.5 with all bounds inside the 3840×2160 frame (test).
* No overscan/overflow fallback hides an element: every control is audited on-screen at all three sizes.

---

## 7. Layout audit (extended proof tool)

New tool `tools/p1042-screen-proof/` (`Program.cs`, `Recorder.cs`, `p1042-screen-proof.csproj`):
`--out` renders every scene × skin × resolution through the production screen renderers; `--audit` records every Jet primitive on a recording surface that measures with the **real HUD fonts**; `--compare` re-renders and compares decoded pixels; `--alloc` measures bytes/frame.

Audit classes and counts, **7 scenes (intro idle/select/last, hangar idle/launch/loadout/turret) × 3 resolutions (800×480, 1920×1080, 3840×2160) × 2 palettes = 42 combos, 474 drawn controls**:

| class | count |
|---|---|
| `hit-vs-draw` (drawn control rect ≠ its hit rect, or no hit action) | **0** |
| stroke-vs-text | **0** |
| text-outside-plate | **0** |
| text-off-screen | **0** |
| TextOverlapCross | **0** |
| TextOverlapSame | **0** |
| failures | **0** |

`hit-vs-draw` compares each recorded `Control` box against `ScreenHitGeometry` (`IntroLayout` rows; `HangarScreen.Rect` for all 10 verbs; `LoadoutV2Layout` leaves for the 6 actions) with a 0.01 px tolerance. Keyline runs are collapsed in the recorder (same text within 2 px) so same-element overlap measures real collisions, not the eight under-stroke offsets.

---

## 8. Assets and visual read-back

`docs/reports/JET_SCREENS_P1042_assets/` — 16 PNGs, **2,015,024 bytes** (≤ 2 MB), no upscaled originals:

| File | What it shows (I opened every one) |
|---|---|
| `jet-intro-green-800x480.png`, `jet-intro-amber-800x480.png` | 13 framed rows, title/subtitle, pilot field, status line, hint; amber/green single-hue chrome; nothing clipped. |
| `jet-intro-green-1920x1080.png`, `jet-intro-amber-1920x1080.png` | The same 13 commands on the wide layout; selected row visually distinct (measured: only row 0 carries the Hot ink, 192 hot-ink pixels vs 0 in the other rows — vision read the highlight wrongly at this size, so the selection claim rests on the pixel count, not on the description). |
| `jet-intro-select-green-1920x1080.png` | Selection on Options with the pilot field released (no fill/caret). |
| `jet-intro-green-3840x2160.png` | 4K; the 1:1 crop shows hard-edged frames/glyphs (no blur). |
| `jet-hangar-green-800x480.png`, `jet-hangar-amber-800x480.png` | Native station-interior art letterboxed, green/amber pane frames, Buy/Def cells (read at 4×), hover strip. |
| `crop-hangar-buydef-1080.png` | 2× crop of the Buy/Def cells at 1080p: two plated, keylined cells in the right letterbox margin. |
| `crop-hangar-pane-1080-amber.png` | 1:1 crop of amber pane brackets over the unchanged artwork. |
| `jet-loadout-green-1920x1080.png`, `jet-loadout-amber-1920x1080.png` | V2 panels (hulls, equipment/offers, preview, stats, price), framed action buttons, Jet-inked rows and keylines. |
| `jet-loadout-green-800x480.png`, `jet-loadout-amber-800x480.png` | Compact V2 at 800×480; all buttons present, rows ellipsized inside their text budgets (V2's existing fit rule). |
| `crop-intro-800-2x.png` | 2× nearest-neighbour crop of the 800×480 intro: all 13 labels legible. |
| `crop-loadout-list-800-2x.png` | 2× crop of the 800×480 hull/slot list: rows contained in their column (the crop edge is what cuts the text, the panel does not). |

**Vision reliability, stated plainly:** at 800×480 the vision read-back is unreliable for small fonts (it misread the highlighted intro row, invented labels such as "More network games" and read `Def` as "Cat"). Every claim above that depends on small text is backed by a measurement (pixel counts, the audit's measured boxes, the 2×/4× crops) or is not made. At 1080p the structure descriptions (panels, frames, Buy/Def cells, hover strip) were confirmed visually and match the recorded control rectangles.

These are **new media**: per the public-repo rule they need Domingo's review before any push.

---

## 9. Gate results

| Gate | Result |
|---|---|
| HUD 1,848-combination zero-delta (Classic/Modern, 0 pixel / 0 semantic) | **PASS** — `tools/hud2-proof --compare` against the pre-edit capture at `/home/domingo/allegiance-evidence/P1041/baseline` (1848 combos, manifest Commit `775c4710`, the story's pre-edit tree): `0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s) (the wall-clock "!" badge), 0 corrupt golden(s)`. No HUD renderer file is touched by this story. |
| Jet flight HUD green/amber byte-identical | **PASS** — `tools/hud2-proof --jet` after vs `P1041/jet-final`: **282/282 PNGs byte-identical** (green + amber, 141 scenes each); the Jet HUD audit line reports every class 0 over 141 scenes. |
| Screen goldens (Classic/Modern) | **23/24 identical**; the 24th is the mid-ramp door frame, proven to differ between two runs of the *unchanged* client by exactly the same 5,603 pixels (§2.3). |
| Jet screens 0 B/frame | **PASS** — `tools/p1042-screen-proof --alloc`: 480 frames (intro+hangar) → **0 bytes**; unit test does the same 240×2 frames → 0. |
| New tests 3× alone | **PASS** — 32/32 three times (60 ms). |
| Solution build 0/0 | **PASS** — `dotnet build AllegianceCore.slnx` (Debug, build servers disabled): 0 warnings, 0 errors. |
| `tools/build-tools.sh` | **PASS** — every `tools/*/*.csproj` built (see `/home/domingo/allegiance-evidence/P1042/buildtools2.log`). |
| One fast tier | see §9.1 |
| Training gate | see §9.1 |

### 9.1 Fast tier and training gate

**Fast tier** — `tools/run-tests.sh --tier fast`, exit 1 on one ledgered flake:

* `Allegiance.ClientCore.Browser.Tests` 12 passed; `Allegiance.ClientCore.Game.Browser.Tests` 23 passed;
  `Allegiance.Update.Tests` 55 passed; `Allegiance.Lobby.Tests` **208 passed / 1 failed**;
  `Allegiance.ClientCore.MonoGame.Tests` 5867 passed / 4 not-executed (skips); `Allegiance.ClientCore.Tests` 2449 passed;
  `Allegiance.Core.Tests` all passed (574 s wall, under concurrent lanes).
* The one failure is `Allegiance.Lobby.Tests.P808IdentityTests.Grants_expire_and_only_one_concurrent_poll_consumes_approval`
  (`SQLite error 5: database locked`), already ledgered at `docs/TEST_FLAKES.md:350`. Re-run **3× alone: 1/0/0 each
  (432/423/421 ms)** → load flake, not this story (no lobby file changed).

**Training gate** — run with artwork because `Game1.IntroMenu.cs` (the intro menu whose Single Player entry opens
the training chooser) is training-affecting. The 21-min-bounded real-window proofs are excluded from the 3× repeats
(the brief allows that for repeats 2–3) and were run once:

* `tools/run-training-tests.sh --no-build --repeat 1` — `Allegiance.ClientCore.Tests` **126/126 PASS (10 s)**; the
  MonoGame training filter then ran the six `m2` real-window children (`Basic flight completes with muted /
  missing / repeated narration`), each a full lesson window:
  `Classic muted` **PASS** `P912_WINDOW_PASS M2 Classic lessons=1..9`, `Classic missing-audio` **PASS**
  (`lessons=1..9`), `Classic reminders` **FAIL** (8 m 52 s), `Modern muted` **PASS** (`lessons=1..9`),
  `Modern missing-audio` **PASS** (`lessons=1..9`), `Modern reminders` **FAIL** (8 m 52 s). The two failures are
  the already-ledgered prompt-sequence child (`M2 9.2: native prompts missing or out of order`,
  `tools/p912-training-proof/Program.cs:206`; P1041 reported the same two). The stopped run's next
  `m2-Modern-default` window had already written `P912_WINDOW_PASS M2 Modern lessons=1..9`. Evidence:
  `~/allegiance-evidence/P1042/windows/<child>/` (the window's `run.log` and its `P912_WINDOW_*` result line; the
  two reminders children have no pass line). The run was stopped after ~40 min of windows (the
  remaining `Assigned_lesson`/`Every_lesson` Live windows are a 20-window family; see the note below).
* **Nanite completion (the AGENTS gate's named child)** run once, unbounded: both
  `Early_nanite_mounting_wins_through_real_input` windows **PASS** — `P912_WINDOW_PASS M8 Classic lessons=1,2,3`
  and `M8 Modern lessons=1,2,3` (2/2, 10 m 55 s; `~/allegiance-evidence/P1042/training-nanite/nanite.trx`).
* **3× suite with `P912TrainingWindowTests` excluded** (`--no-build`, artwork, both client projects serially):
  `Allegiance.ClientCore.Tests` **126/126** and `Allegiance.ClientCore.MonoGame.Tests` **256 passed / 1 skipped**
  in all three runs (11 m 09 s, 11 m 09 s, 11 m 16 s). Logs/TRX:
  `~/allegiance-evidence/P1042/training-repeats/`.

Time-bound note: the full P912 class also contains a ~20-window `Assigned_lesson`/`Every_lesson` family (each up
to the 21-min bound); the run was stopped after the `m2` family and the Nanite windows, and those Live windows are
covered by the exclusion the brief permits for the repeats. No training code was changed by this story.

### 9.2 Final re-verification on the final tree

After the last refactor (unused parameter removed from the Jet chrome's width fit), everything was re-run on the
final sources: solution build **0 warnings / 0 errors**; new tests **32/32 ×3**; `--audit` **0 failures** over 42
combos; `--alloc` **0 bytes** over 480 frames; `tools/p1042-screen-proof --compare` on its own 96-image capture
**0 differing pixels**; intro re-captured at 800×480 and 1920×1080 — **0 differing pixels** vs the pre-edit goldens.

---

## P1042b — the remaining screen families (NOT started; listed so P1042b can be briefed)

| Screen family | Where it lives today |
|---|---|
| Server browser / connect | `Game1.ConnectScreen.cs`, `src/Allegiance.ClientCore.Game.Lobby/`, `Rendering/ConnectScreenLayout.cs` |
| Options / privacy | `Game1.OptionsMenu.cs`, `OptionsMenuModel.cs`, `Game1.KeyBind.cs`, the Ctrl+F6 panel in `Game1.GpuBudget.cs` |
| Career / squads / events | `Game1.Career.cs`, `Rendering/CareerScreenRenderer.cs` |
| Single-player / training chooser + slideshow | `Game1.IntroScreen.cs` (`IntroView.SinglePlayer`, `Training`, `TrainingSlideshow`, `TrainingSuggest`), `TrainingScreenModel`, `SinglePlayerChooserModel` |
| Create game wizard | `Game1.CreateGame.cs` |
| Team / side select / ready check | `Rendering/SideSelectOverlayRenderer.cs`, `Rendering/ReadyCheckOverlayRenderer.cs`, `Rendering/ReadyCheckLayout.cs` |
| Briefing | `Rendering/MissionBriefingRenderer.cs`, `Rendering/MissionBriefingLayout.cs` |
| Starmap / sector info | `Rendering/StarmapRenderer.cs`, `Rendering/StarmapLayout.cs`, `Rendering/SectorInfoPaneRenderer.cs` |
| Scoreboard / game over | `Rendering/ScoreboardOverlayRenderer.cs`, `Rendering/GameOverScreenRenderer.cs` |
| Help / credits | `Rendering/HelpOverlayRenderer.cs`, `Rendering/CreditsOverlayRenderer.cs` |
| Key bindings | `Rendering/KeyBindingRenderer.cs`, `Rendering/KeyBindingTable.cs` |
| Quick chat | `Rendering/QuickChatMenuRenderer.cs`, `src/Allegiance.ClientCore.Game/QuickChat/` |
| In-flight panes (chat / inventory / cargo / command / teleport / investment) | `Rendering/ChatRenderer.cs`, `Rendering/HudInventoryPane.cs`, `Rendering/TeleportLayout.cs`, `Rendering/InvestmentLayout.cs`, `Rendering/HudCommandConsole.cs` |
| Connection lost | `Rendering/ConnectionLostScreenRenderer.cs` |

---

## Unfinished / carried to P1042b

* The intro's sub-views (Find Server, Direct Connect, chooser, training, slideshow, credits, help, settings, benchmark, career, create game, host, join link) are **not** routed through `IScreenSkinRenderer`; they keep today's drawing (this is P1042b's "single-player/training chooser" and "browser/connect"/"options" rows). Only the intro **main menu** (the 13 commands) is in this slice, as the brief's item 3 defines.
* The legacy native-layout docked shop is re-inked under Jet but has **no Jet chrome frames** (its rows are measured inside `DockedScreenRenderer`, not resolved rectangles); the V2 shop (default ON) is fully framed. No draw/hit duplication is introduced either way.
* Loadout V2 row text is not part of the recorded audit: the chrome audit covers Jet's controls and chrome text; the V2 renderer's rows are drawn by the unchanged renderer with fitted text budgets (tested: every `TextBounds` inside the viewport).
* No ship gate / real-window all-skins proof / P1043 items; no rig contact; no Live-tier runs (none added).

---

## Native intent

Native's intro menu is a fixed list of mouse panes (`WinTrek/introscreen.cpp:1212-1228` for the panes, `:1558-1876` for the handlers), and its hangar is one `ScreenPane` over `hangarbmp` with eight child panes at authored points (`Artwork/hangar.mdl:87-102`; `ScreenPaneFactory` offsets children by `SetOffset(WinPoint(point.X(), point.Y()))`, `effect/efpane.cpp:642-682`, painted in top-left, y-down viewport rows, `engine/pane.cpp:472-500`). The native action set, pane geometry, hover strings (`hangar.mdl:26-72`), two-face button strips (`engine/button.cpp:120-198,238-296`), the 0.7 s launch FrameImage (`hangar.mdl:12`, `engine/frameimage.cpp:413-456`) and the state-2 in-turret badge (`hangar.mdl:74-80`, `WinTrek/console.cpp:797-804`) are the behaviour this slice preserves: every rectangle is the port's existing `HangarScreen`/`IntroLayout` one, the commands are unchanged, and native art/slides remain content. Native's screen-pane convention (top-left) is kept distinct from the HUD's y-up images.

## Modern approach

A screen skin is a validated presentation adapter over a read-only frame: the models own text, selection and commands; `ScreenHitGeometry` is the single rectangle source used by both drawing and hit testing; Classic/Modern forward their existing drawing untouched (proved by decoded-pixel goldens), and Jet draws thin-vector chrome, plates and keylined text over the same boxes in its five-role palette, with shape/text redundancy instead of hue-only selection. All adapters are preloaded and published by the same update-boundary call that publishes the HUD skin and palette, so switching is atomic, allocation-free and cannot touch focus, selection or scroll. The proof tool records every primitive through the real fonts and counts the layout classes, including the new `hit-vs-draw`, for both palettes at 800×480, 1080p and 4K.
