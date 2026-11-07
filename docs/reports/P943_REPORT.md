# P943 — Training slideshows show the native slide images

The training slideshow screens now draw the real native artwork instead of a text stand-in, so the Basic Flight
postlude P940 captured as a black slide with only `Basic Flight  1/1  any key: next` shows the hangar bitmap. The port
reads each mission's `tm_*.mdl` from the artwork install, evaluates the slide image expression at the native 800×600
y-up layout, draws it scaled and centred in the window, and keeps the native narration as the fallback when a bitmap
is absent. The empty post-mission captions are filled from the MDL narration comments as well.

Changes are left in the working tree. No commits, pushes, `git stash`, remote-host operations or files outside the
worktree were made; all scratch data is in `.scratch/p943/` (git-ignored).

## Inventory — every corpus slideshow and every slide

The table lists each namespace native routes to (`WinTrek/WinTrek.cpp:2473-2493` pre, `:2495-2517` post), its slide
count, each slide's image expression as the parser evaluates it, the bitmap stems it needs and whether each exists in
the artwork corpus (`$ALLEGIANCE_ARTWORK_PATH`). Every `use` line of every slideshow declares the same four
namespaces — `SlideshowData` (created by native for the exported names, `WinTrek/Slideshow.cpp:41-42`), `sounddef`
(the `…SoundId` symbols), `font` (`largeFont`) and `gamepanes` (`white`) — and all four resolve. Mission 7 has a blank
native entry in both tables (no slideshow); mission 9 is blank and mission 10 reuses the mission-6 post slides.

| Namespace | # | Sound stem | Image expression | Imports | Present |
| --- | --- | --- | --- | --- | --- |
| `tm_1_introduction` | 15 | | | tm_slide_backgroundbmp, hangarbmp, tm_slide_circle_overlaybmp, tm_helpbmp, tm_loadout_backgroundbmp, tm_teleportbmp | yes/yes/yes/yes/yes/yes |
| | 1 | `tm_slide_1_01` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 94 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 2 | `tm_slide_1_02` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 536 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 3 | `tm_slide_1_03` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 212 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 4 | `tm_slide_1_04a` | ImportImage("hangarbmp", false) | | |
| | 5 | `tm_slide_1_04b` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(205, 375)), ImportImage("hangarbmp", false)]) | | |
| | 6 | `tm_slide_1_05` | GroupImage([TranslateImage(ImportImage("tm_helpbmp", false), Point(105, 125)), ImportImage("hangarbmp", false)]) | | |
| | 7 | `tm_slide_1_06` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(360, 285)), TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(536, 230)), ImportImage("hangarbmp", false)]) | | |
| | 8 | `tm_slide_1_07` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(360, 285)), ImportImage("hangarbmp", false)]) | | |
| | 9 | `tm_slide_1_08` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(536, 230)), ImportImage("hangarbmp", false)]) | | |
| | 10 | `tm_slide_1_09` | ImportImage("tm_loadout_backgroundbmp", false) | | |
| | 11 | `tm_slide_1_10` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(542, 357)), TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(54, 370)), TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(84, 188)), ImportImage("hangarbmp", false)]) | | |
| | 12 | `tm_slide_1_11` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(207, 231)), ImportImage("hangarbmp", false)]) | | |
| | 13 | `tm_slide_1_12` | GroupImage([TranslateImage(ImportImage("tm_teleportbmp", false), Point(105, 320)), ImportImage("hangarbmp", false)]) | | |
| | 14 | `tm_slide_1_13` | GroupImage([TranslateImage(ImportImage("tm_teleportbmp", false), Point(105, 320)), ImportImage("hangarbmp", false)]) | | |
| | 15 | `tm_slide_1_14` | ImportImage("hangarbmp", false) | | |
| `tm_2_basic_flight` | 1 | | | tm_slide_circle_overlaybmp, hangarbmp | yes/yes |
| | 1 | `tm_slide_2_01` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(360, 285)), ImportImage("hangarbmp", false)]) | | |
| `tm_2_basic_flight_post` | 1 | | | hangarbmp | yes |
| | 1 | `tm_slide_2_post_01` | ImportImage("hangarbmp", false) | | |
| `tm_3_basic_weaponry` | 13 | | | hangarbmp, tm_slide_circle_overlaybmp, tm_loadout_backgroundbmp | yes/yes/yes |
| | 1 | `tm_slide_3_01` | ImportImage("hangarbmp", false) | | |
| | 2 | `tm_slide_3_02` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(536, 230)), ImportImage("hangarbmp", false)]) | | |
| | 3 | `tm_slide_3_03` | ImportImage("tm_loadout_backgroundbmp", false) | | |
| | 4 | `tm_slide_3_04` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(469, 444)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 5 | `tm_slide_3_05` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(16, 457)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 6 | `tm_slide_3_06` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(644, 448)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 7 | `tm_slide_3_07` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(648, 369)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 8 | `tm_slide_3_08` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(14, 294)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 9 | `tm_slide_3_09` | ImportImage("tm_loadout_backgroundbmp", false) | | |
| | 10 | `tm_slide_3_10` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(644, 226)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 11 | `tm_slide_3_11` | ImportImage("tm_loadout_backgroundbmp", false) | | |
| | 12 | `tm_slide_3_12` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(205, 137)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| | 13 | `tm_slide_3_13` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(458, 373)), ImportImage("tm_loadout_backgroundbmp", false)]) | | |
| `tm_3_basic_weaponry_post` | 1 | | | hangarbmp | yes |
| | 1 | `tm_slide_3_post_01` | ImportImage("hangarbmp", false) | | |
| `tm_4_enemy_engagement` | 2 | | | tm_slide_backgroundbmp | yes |
| | 1 | `tm_slide_4_01` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 267 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 2 | `tm_slide_4_02` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 162 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| `tm_4_enemy_engagement_post` | 1 | | | hangarbmp | yes |
| | 1 | `tm_slide_4_post_01` | ImportImage("hangarbmp", false) | | |
| `tm_5_command_view` | 26 | | | tm_slide_circle_overlaybmp, hangarbmp, tm_slide_5_01bmp, tm_slide_5_02bmp, tm_slide_5_03bmp, tm_slide_5_04bmp, tm_slide_5_06bmp, tm_slide_5_07bmp, tm_slide_5_08bmp, tm_slide_5_10bmp, tm_slide_5_12bmp, tm_slide_5_13bmp, tm_slide_5_14bmp, tm_slide_5_15bmp, tm_slide_5_16bmp, tm_slide_5_18bmp, tm_slide_5_19bmp, tm_slide_5_20bmp, tm_slide_5_23bmp, tm_slide_5_24bmp, tm_slide_5_25bmp | yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes/yes |
| | 1 | `tm_slide_5_01` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(84, 188)), ImportImage("hangarbmp", false)]) | | |
| | 2 | `tm_slide_5_02` | ImportImage("tm_slide_5_01bmp", false) | | |
| | 3 | `tm_slide_5_03` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(47, 42)), ImportImage("tm_slide_5_01bmp", false)]) | | |
| | 4 | `tm_slide_5_04` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(115, 350)), ImportImage("tm_slide_5_01bmp", false)]) | | |
| | 5 | `tm_slide_5_05` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(542, 357)), ImportImage("hangarbmp", false)]) | | |
| | 6 | `tm_slide_5_06` | ImportImage("tm_slide_5_02bmp", false) | | |
| | 7 | `tm_slide_5_07` | ImportImage("tm_slide_5_03bmp", false) | | |
| | 8 | `tm_slide_5_08` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(247, 78)), ImportImage("tm_slide_5_04bmp", false)]) | | |
| | 9 | `tm_slide_5_09` | ImportImage("tm_slide_5_06bmp", false) | | |
| | 10 | `droneReport4Duty` | ImportImage("tm_slide_5_07bmp", false) | | |
| | 11 | `tm_slide_5_11` | ImportImage("tm_slide_5_08bmp", false) | | |
| | 12 | `tm_slide_5_12` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(247, 78)), ImportImage("tm_slide_5_10bmp", false)]) | | |
| | 13 | `tm_slide_5_13` | ImportImage("tm_slide_5_10bmp", false) | | |
| | 14 | `tm_slide_5_14` | ImportImage("tm_slide_5_12bmp", false) | | |
| | 15 | `tm_slide_5_15` | ImportImage("tm_slide_5_13bmp", false) | | |
| | 16 | `tm_slide_5_16` | ImportImage("tm_slide_5_14bmp", false) | | |
| | 17 | `tm_slide_5_17` | ImportImage("tm_slide_5_15bmp", false) | | |
| | 18 | `tm_slide_5_18` | ImportImage("tm_slide_5_16bmp", false) | | |
| | 19 | `tm_slide_5_19` | ImportImage("tm_slide_5_18bmp", false) | | |
| | 20 | `tm_slide_5_20` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(654, 29)), ImportImage("tm_slide_5_19bmp", false)]) | | |
| | 21 | `tm_slide_5_21` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(243, 190)), ImportImage("tm_slide_5_20bmp", false)]) | | |
| | 22 | `tm_slide_5_22` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(643, 23)), ImportImage("tm_slide_5_20bmp", false)]) | | |
| | 23 | `tm_slide_5_23` | ImportImage("tm_slide_5_23bmp", false) | | |
| | 24 | `tm_slide_5_24` | ImportImage("tm_slide_5_24bmp", false) | | |
| | 25 | `tm_slide_5_25` | ImportImage("tm_slide_5_25bmp", false) | | |
| | 26 | `tm_slide_5_26` | ImportImage("hangarbmp", false) | | |
| `tm_5_command_view_post` | 4 | | | tm_slide_circle_overlaybmp, hangarbmp, tm_gamestatebmp | yes/yes/yes |
| | 1 | `tm_slide_5_post_01` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(54, 370)), ImportImage("hangarbmp", false)]) | | |
| | 2 | `tm_slide_5_post_02` | GroupImage([TranslateImage(ImportImage("tm_gamestatebmp", false), Point(105, 125)), ImportImage("hangarbmp", false)]) | | |
| | 3 | `tm_slide_5_post_03` | GroupImage([TranslateImage(ImportImage("tm_slide_circle_overlaybmp", true), Point(205, 375)), ImportImage("hangarbmp", false)]) | | |
| | 4 | `tm_slide_5_post_04` | ImportImage("hangarbmp", false) | | |
| `tm_6_practice_arena` | 1 | | | tm_slide_backgroundbmp | yes |
| | 1 | `tm_slide_6_01` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 112 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| `tm_6_practice_arena_post` | 1 | | | hangarbmp | yes |
| | 1 | `tm_slide_6_post_01` | ImportImage("hangarbmp", false) | | |
| `tm_8_nanite` | 5 | | | tm_slide_backgroundbmp | yes |
| | 1 | `tm_slide_8_01` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 233 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 2 | `tm_slide_8_02` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 390 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 3 | `tm_slide_8_03` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 325 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 4 | `tm_slide_8_04` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 210 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 5 | `tm_slide_8_05` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 407 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| `tm_8_nanite_post` | 4 | | | tm_slide_backgroundbmp | yes |
| | 1 | `tm_slide_8_post_01` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 131 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 2 | `tm_slide_8_post_02` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 185 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 3 | `tm_slide_8_post_03` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 125 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |
| | 4 | `tm_slide_8_post_04` | GroupImage([TranslateImage(StringImage(JustifyLeft, 500, largeFont, white, text 216 chars), Point(150, 450)), ImportImage("tm_slide_backgroundbmp", false)]) | | |

Bitmap inventory: `hangarbmp`, `tm_loadout_backgroundbmp`, `tm_slide_backgroundbmp`, `tm_slide_5_01bmp` …
`tm_slide_5_25bmp` are binary 800×600 RGB565 `.mdl` surfaces; `tm_slide_circle_overlaybmp` is 140×140 (imported with
`colorKey = true`), `tm_helpbmp` 518×393, `tm_teleportbmp` 340×217, `tm_gamestatebmp` 214×279. All are installed;
nothing in any slideshow is missing. The 75 slides are: introduction 15, basic flight 1 + 1 post, basic weaponry 13 +
1 post, enemy engagement 2 + 1 post, command view 26 + 4 post, practice arena 1 + 1 post, nanite 5 + 4 post.

## Operators implemented (native citations)

| Expression | Native rule | Port |
| --- | --- | --- |
| `ImportImage("stem", colorKey)` | Load the bitmap as a surface; `bColorKey` goes to `LoadImage`'s colour-key hint (`engine/modeler.cpp:691-706`, `:2463-2492`, hint `:2491-2492`). `ImportImage`'s pixel size is kept (`engine/modeler.cpp:723-735`). | `SlideshowScript` AST `SlideshowImage.Import`; `SlideshowArt.ProbeSize`/`Get` decode the binary `.mdl` (`MdlTextureDecoder`) or the `ImportImageFromFile` script; `colorKey` keys the near-black surround to alpha 0 and premultiplies, as the other 2-D loaders do. |
| `GroupImage([…])` | The list is painted in REVERSE: `AddImage` pushes each child to the front (`engine/modeler.cpp:1544-1562`, `engine/image.cpp:918-921`) and `Render` walks front-to-back (`engine/image.cpp:954-976`), so the FIRST child ends up on top. | `SlideshowLayout.Walk` walks the list from last to first. Test: `Group_image_paints_its_first_child_last_so_it_ends_on_top`. |
| `TranslateImage(image, Point(x, y))` | The point is the child's origin in native y-up screen space (`engine/image.cpp:828-845`). | Accumulated offset during the walk; `ScreenTransform.ImageRect` flips y for the port's top-left pixels. Test: `Translate_image_offsets_its_child_to_the_native_point`. |
| `StringImage(JustifyLeft, width, font, color, text)` | The block hangs BELOW its point (`Rect(0, -ysize, width, 0)`, `engine/image.cpp:337-355`) and wraps at its width (`engine/image.cpp:357-400`). | `SlideshowImage.Text` → `TextOp` at the translate point; the renderer wraps with the HUD font at the native 500 px width and scales with the screen. Test: `String_image_hangs_below_its_point_and_wraps_at_its_width`. |
| `Point(x, y)` | A native point value used by the translate and the text position (`engine/modeler.cpp:2269`). | `PointValue`, resolvable through a name (`slide_text_position`). |
| Identifier aliases | MDL assignment statements (`slide_text_1 = "…"`, `hangar_background = ImportImage(…)`). | The parser keeps a symbol table and resolves aliases (with a depth guard). |

The screen transform is native's: the whole 800×600 screen is scaled by `min(window/screen)` and centred
(`WinTrek/WinTrek.cpp:2180-2217` builds the screen image, `UpdateBackdropCentering` `:3656-3693` computes the scale and
the half-the-slack translate; `Screen::GetSize` is 800×600, `WinTrek/screen.h:14`). The slideshow's own image tree is
`WrapImage`/`GroupImage` (`WinTrek/Slideshow.cpp:33-39`, `:153-156`).

## Never a blank slide

- The slide's caption is the native narration: the `StringImage` text when the art carries it (introduction 1-3, enemy
  engagement, practice arena, nanite), otherwise the `//` comment block above the slide (`tm_1_introduction.mdl:58-60`).
- When the screen's layout resolves no drawable operation (a missing bitmap or namespace), the caption is drawn in the
  pre-P943 centred style; when that is empty too, the slideshow title labels the slide, so the screen is never empty.
- A missing bitmap is logged once per stem (`SlideshowArt.LogMissingOnce`); the artwork test asserts the corpus has
  none.
- Empty post-mission captions filled from the MDL comments: mission 2 (`tm_2_basic_flight_post.mdl:6-9`), mission 3
  (`tm_3_basic_weaponry_post.mdl:6-10`), mission 4 (`tm_4_enemy_engagement_post.mdl:6-12`). Mission 5 and 8 already
  carried text; mission 6's post slide has no narration comment in its MDL (only `hangarbmp`), so its caption stays
  empty and its image is the content — documented in `TrainingSlideData`'s summary.
- Non-ASCII characters in the corpus text (Windows-1252 apostrophes) become `?` so the ASCII-only HUD font can draw
  them; repairing the corpus text is out of scope, as the brief says.

## Before / after

Before — `docs/reports/P940_assets/m2-complete.png`, opened and inspected: a solid black slide with only the footer
`Basic Flight  1/1  any key: next` (P940's real-window capture of the postlude).

After — `docs/reports/P943_assets/p943-tm_2_basic_flight_post-01.png`, opened and inspected: the hangar interior
artwork (control consoles, the "HANGAR" door) as the letterboxed 4:3 screen with the same footer underneath. The
same run captured all 75 slides through the shipping `Game1` intro screen; four representative PNGs are committed as
evidence (each ≤ 300 KB, media pending Domingo's review):

| Capture | What was verified by opening it |
| --- | --- |
| `P943_assets/p943-tm_2_basic_flight_post-01.png` | Basic Flight postlude: hangar art + footer (the P940 black case). |
| `P943_assets/p943-tm_1_introduction-05.png` | Introduction 5/15: hangar art with the red circle overlay drawn on top at its native `Point(205, 375)` — exercises `GroupImage` + `TranslateImage` + a colour-keyed import. |
| `P943_assets/p943-tm_4_enemy_engagement-01.png` | Enemy Engagement 1/2: the slide background with the white `StringImage` narration starting "The most important aspect…" and the footer. |
| `P943_assets/p943-tm_5_command_view-02.png` | Command View 2/26: a full-screen imported command-view screenshot (sector map, mini-map, command bar) — a plain `ImportImage` slide. |

Beyond the four, every captured slide was reviewed: a 75-tile contact sheet of the run was opened and inspected, and a
pixel check found no near-black slide (all 75 have >5% lit pixels; the sheet's last row carries three slides plus
black padding cells). The footers read the expected `title  n/count  any key: next`, and the text slides keep their
narration above the footer (the Nanite credits slide fits without clipping).

## Captures and the proof tool

`tools/p943-slideshow-proof` opens the shipping `Game1` intro, walks `SlideshowNamespaces.All()`, sets the real
slideshow model for each namespace and photographs every slide through the game's own autoshot (real backbuffer,
`SaveAsPng`) at the game's 800×480 default window (scale 0.8, the 4:3 screen letterboxed). **Claude's review
correction:** the first captures asked for 800×600, but SDL's offscreen driver keeps its 800×480 surface, so their top
120 rows were never rendered (transparent) and each slide was clipped. The renderer was right (pixel-identical to the
decoded `hangarbmp` at offset 0); the committed assets are recaptures at 800×480, all 75 with no transparent pixel.
Invocation:

```
SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null ALLEGIANCE_NOVSYNC=1 \
  dotnet tools/p943-slideshow-proof/bin/Debug/net10.0/p943-slideshow-proof.dll \
  src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content .scratch/p943/captures
```

It printed `P943_SLIDESHOW_PASS shots=75`; the 75 PNGs (15 MB) stay under `.scratch/p943/captures/` and are not
committed (only the four small representatives above are in the report assets).

Retained evidence under `.scratch/p943/`: `captures/` (+ `contact-sheet.png`), `fail-before.log` / `fail-after.log`,
`tests-3x.log`, `flake-3x.log`, `flake-core-3x.log`, `fast-tier.log`, `sweep-summary/` (the Fast sweep's per-project
logs + `summary.json`), `inventory.md`, `scorecard.log`, `slnrebuild.log`, `build-tools.log`.

## Tests

New:

- `tests/Allegiance.ClientCore.Tests/P943SlideshowScriptTests.cs` — one parser/evaluator test per operator used
  (`ImportImage`, `GroupImage` draw order, `TranslateImage`, `StringImage`), the narration-comment extraction, the
  missing-bitmap path, the namespace table, and the post-mission captions.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P943SlideshowArtTests.cs` (artwork-gated, `Trait("Requires","Artwork")`)
  — every corpus slideshow parses to the caption table's slide count and sound stems, every slide resolves every
  import, and every slide has drawable art or a caption; plus the Basic Flight postlude, the composed introduction
  slide, a text slide, and the native bitmap sizes.

Fail-before (recorded in `.scratch/p943/fail-before.log`): with the pre-P943 `TrainingSlideData` restored,
`Every_post_mission_slide_carries_its_native_narration` fails with `every native post-mission slide must carry its
narration` (the three post captions were empty); with the change restored it passes. The artwork test's
`Assert.Equal(table.Length, slideshow.Count)` / stem equality also fail on the old table only if the counts drift — the
caption assertion is the fail-before for the black postlude.

Existing slideshow timing/dismiss tests stay green: `P785TrainingCompletionTests.Slideshow_*`,
`P785TrainingHandlerTests.Slideshow_keys_advance_and_the_post_slideshow_dismisses_to_the_training_screen`,
`P910TrainingIntegrationTests` slideshow cases. The only existing-test edit is `TrainingSlides.Introduction` →
`TrainingSlides.Introduction()` (the property became a method so it can take the asset source; same data, same
assertions).

Artwork skip count: **0 skipped** in both new test classes (the corpus is configured through `local.env`). A clean
clone without `ALLEGIANCE_ARTWORK_PATH` skips the five artwork tests with the standard `Require.Artwork()` reason and
still runs the eight parser/caption tests (which use inline MDL text).

## Files

- `src/Allegiance.ClientCore/Training/SlideshowScript.cs` — the MDL reader, the `SlideshowImage` expression tree and
  the narration comment extraction.
- `src/Allegiance.ClientCore/Training/SlideshowLayout.cs` — evaluation into native screen-space operations and the
  800×600 → window transform.
- `src/Allegiance.ClientCore/Training/SlideshowNamespaces.cs` — the native mission → namespace table.
- `src/Allegiance.ClientCore/Training/SlideshowModel.cs` — `Slide.Art` plus the corrected summary (the art is in the
  corpus).
- `src/Allegiance.ClientCore.Game/TrainingSlides.cs` — parse the mission MDL through the client asset source, merge
  narration/captions, fall back to `TrainingSlideData` when the art is absent.
- `src/Allegiance.ClientCore.Game/TrainingSlideData.cs` — filled post-mission captions and corrected summary.
- `src/Allegiance.ClientCore.Game/Rendering/SlideshowArt.cs` — bitmap decode (binary `.mdl` / `ImportImageFromFile`),
  colour-key, size probing without a GPU, missing-art logging.
- `src/Allegiance.ClientCore.Game/Game1.Slideshow.cs` — `DrawSlideArt` / caption fallback; `Game1.IntroScreen.cs` now
  calls them, and `Game1.cs` disposes the bitmap cache.
- `tools/p943-slideshow-proof/` — the capture tool.
- `docs/parity/training-matrix.csv` — TRN-0200 now cites the real slide composition (it previously said "visual slide
  composition may differ" and pointed at `Game1.FlightInput.cs`); scorecard unchanged at training 836/901 (92.79%),
  213/235 (90.64%) because the row was already credited.
- `GLOSSARY.md` — new "Training slideshow artwork (P943)" entry; `tools/check-glossary-links.py` passes.
- `docs/reports/P785_TRAINING_COMPLETION.md` is a historical task record; its "visual stand-in for the full-screen
  slide images" wording describes the state at P785 and is left as written. The current statements (GLOSSARY, parity
  TRN-0200, the source comments) now say the artwork is installed.

## Gate

| Check | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx -t:Rebuild` (43 projects) | **0 warnings, 0 errors** (11.8 s) |
| `tools/build-tools.sh` | **all 118 tool projects built**, each 0 warnings/errors (includes the new `tools/p943-slideshow-proof`) |
| New/affected tests, 3× alone, fresh process each | `P943SlideshowScriptTests` 8/8 ×3, `P943SlideshowArtTests` 5/5 ×3 (0 skips), `P785TrainingCompletionTests` 14/14 ×3, `P785TrainingHandlerTests` 5/5 ×3, `P910TrainingIntegrationTests` 35/35 ×3 — 201 passes, 0 skips |
| One `tools/run-tests.sh --tier fast` | 13,269 passed / 5 failed / 3 Meshy skips (587.8 s); all five failures are ledgered, unrelated flakes whose isolation matches the ledger — see [TEST_FLAKES.md](../TEST_FLAKES.md#p943-validation--one-fast-sweep-2026-11-07-utc) |
| Artwork skip count | **0** in both new classes (corpus configured); a clean clone skips the 5 artwork tests |
| `tools/check-glossary-links.py` | OK, 604 distinct paths |
| `scripts/parity-scorecard.py` | all matrices valid; training **836/901 (92.79%)**, **213/235 (90.64%)** — unchanged (TRN-0200 was already credited) |

No `src/Allegiance.Core` or `src/Allegiance.Server` code changed, so the rig AOT publish was not part of this light
gate; no Live-tier test was added or touched. `docs/TEST_FLAKES.md` gained the P943 sweep entry.

## Not resolved / out of scope

- **Slide audio and timing** are untouched (already native: `SlideshowModel` sound stems and the 1 s post-sound timer).
  `droneReport4Duty` (command-view slide 10) still has no clip in the corpus, as before.
- **Text font**: the port draws `StringImage` text with the 14 px HUD monospace font at the native 500 px wrap width
  and scales it with the screen; native uses `largeFont` from `font.mdl`. Visual parity only — the layout, position and
  wrap width match.
- **Mission 6 post** has no native narration text to fill.
- **Mission 7** has no slideshow (blank native namespace); mission 9 blank / mission 10 reuses the mission-6 post
  slides, as native does.
- **Text colour** is white (all template slides author `white`); other colours would need the colour table imported.
- The corpus text's `?` characters are left as-is, per the brief.

## Native intent

**Design:** the slideshow is a narrated, key-advanced briefing whose content is a full-screen composite image — a
background frame (hangar, loadout, command view, or the text panel) with small overlays marking the UI element the
voice describes. The 800×600 screen is scaled as one unit into the window, so the overlays must land on the same
picture elements at every resolution. The port now reproduces the composition rather than paraphrasing it.

**Era constraint:** the artwork corpus carries the narration twice — as wrapped `//` comments above each slide and, for
the text slides, as a `StringImage` — and it carries no alpha channel in the binary bitmaps, so overlays are drawn with
a black colour key. The port's ASCII-only HUD font forces the existing `?` substitution for the corpus's Windows-1252
apostrophes.

**Bug (fixed):** the port shipped a caption-only stand-in premised on the mistaken belief that the slide art was not
installed, and gave the post-mission slides empty captions. That produced the black postlude P940 photographed. No
native behaviour was changed for this task; only the port's rendering and its fallback data were corrected.
