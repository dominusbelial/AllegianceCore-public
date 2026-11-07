# P945 — The native hangar screen (`hangar.mdl`) is drawn again

**Task:** draw the native hangar art on the docked hangar screen, replacing the port's
bottom row of text cells (`Game1.Hangar.cs`'s pre-P945 `DrawHangarControls`).
**Worktree:** `<worktree>` (branch off Claude's master `9e51530a`).
Nothing committed — all changes are in the working tree.
**Local env:** `local.env` present (`ALLEGIANCE_ARTWORK_PATH=<artwork>`,
`ALLEGIANCE_NATIVE_SRC_PATH=<native-src>`), so no corpus test skipped:
the runs below report **Skipped: 0** except where noted.

---

## 1. Native findings (every line re-grepped before citing)

### 1.1 The screen and its panes

`Artwork/hangar.mdl:87-100` builds the whole screen as one `ScreenPane` over
`ImportImage("hangarbmp", false)` (an 800×600 station-interior bitmap) with ten children at
fixed points:

| pane | point | native size | artwork |
|---|---|---|---|
| launch | (367,162) | 130×214 | `animlaunch` FrameImage (background = the 130×214 surface embedded in `animlaunch.mdl`) |
| command | (84,283) | 104×104 | `animradarbmp` (104×208 strip) |
| loadout | (526,247) | 160×160 | `animloadoutbmp` (160×320) |
| invest | (530,88) | 156×156 | `animinvestbmp` (156×312) |
| help | (208,95) | 116×116 | `btnquitbmp` (file `BtnQuitbmp.mdl`, 116×232) |
| teleport | (214,248) | 114×114 | `animsectorbmp` (114×228) |
| team | (43,102) | 156×156 | `animteambmp` (156×312) |
| exit | (80,515) | 83×24 | `btnexitgamebmp` (83×48) |
| hover text | (100,561) | 600×24 | `hoverInfoPane` |
| in-turret badge | (320,450) | 172×30 | `hangarinturretbmp` (`hangar.mdl:74-80`, `:100`) |

Sizes are the artwork's own: `ButtonFacePane` takes
`m_ysize = size.Y() / m_countFaces` with `m_countFaces = CountBits(m_dwFaces)`
(`engine/button.cpp:120-128`), and `FrameImageButtonPane` takes
`m_pimage->GetBounds()` (`engine/frameimage.cpp:372-374`). The eight button strips are all
two-face strips, so pane = strip width × (strip height / 2) — verified against the decoded
bitmaps in `P945HangarArtTests.Every_hangar_art_stem_resolves_with_its_native_size`.

**Coordinate convention (verified, not assumed).** `ScreenPaneFactory` places each child with
`SetOffset(WinPoint(point.X(), point.Y()))` (`effect/efpane.cpp:642-682`), and `Pane::PaintAll`
offsets the D3D9 viewport with `newViewport.Y = surfClip.YMin() + surfOffset.Y()`
(`engine/pane.cpp:454-481`) — i.e. **top-left, y-down**. The screenshots agree: the launch pane's
door sits at y≈162 from the top of `hangarbmp`, the hover strip at y=561 is the bottom strip.
(The y-up convention belongs to HUD *images*, not to screen panes.) The whole screen is scaled by
`min(w/800, h/600)` and centred (`UpdateBackdropCentering`, `WinTrek/WinTrek.cpp:3656-3681`) —
the same letterbox rule P943 uses for the slides.

### 1.2 Button faces — the strip layout

`ButtonPane(ImportImage(...), 5, false)` (`hangar.mdl:13-20`) →
`ButtonPaneFactory` (`WinTrek/trekmdl.cpp:517-546`) → `CreateButtonFacePane(surface, 5)`
→ `ImageButtonFacePane` (`engine/button.cpp:17-300`):

* `5 = ButtonFaceUp | ButtonFaceInside` (`engine/button.h:77-80`), `CountBits(5) = 2`
  (`engine/button.cpp:120`); `m_ysize = surface.Y() / 2` (`:128`), `ZAssert(countFaces * ysize ==
  surface.Y())` (`:139`) — the strip is **two equal rows**.
* `GetFaceIndex` (`engine/button.cpp:190-209`) walks the face bits from the bottom of the mask:
  `ButtonFaceUp` → **row 0**, `ButtonFaceInside` → **row 1**.
* `Paint` (`engine/button.cpp:238-296`) picks the face: pointer outside → `ButtonFaceUp`;
  pointer inside (not down) → `ChooseFace(ButtonFaceInside, ButtonFaceUp)` = **row 1**
  (`:281`); pointer down → `ChooseFace(ButtonFaceDown, ButtonFaceUp)` = `ButtonFaceUp`
  (mask bit 5 has no down face, `:245-256`). So **pressing shows the normal face** — a native
  quirk the port reproduces because it draws exactly the two faces the strip has.
* `BltFace`/`Paint` blit the whole strip width (`xmin = 0`, `xmax = surface.X()` when
  `xmax == 0`, `engine/button.cpp:126-132`) and the selected row (`:211-225`).

### 1.3 The launch animation

`launchButtonPane = FrameImageButtonPane("animlaunch", .7, false, false)` (`hangar.mdl:12`), i.e.
0.7 s, `loop = false`, `finish = false`. `FrameImageButtonPaneFactory::Apply`
(`engine/modeler.cpp:136-175`) resolves the `animlaunch` namespace's `frame`
(`ModifiableNumber`) and `image`, then `CreateFrameImageButtonPane(time, image, frame, 0.7, false,
false)`.

* `Evaluate` (`engine/frameimage.cpp:413-456`): `delta = (time - timeStart)/duration`; while the
  pointer is inside the value ramps **forward** from the value it entered with; when it leaves it
  ramps **backwards**; `loop = false`/`finish = false` means it is simply clamped to [0,1]
  (`MouseEnter`/`MouseLeave` latch `valueStart`/`timeStart`, `engine/frameimage.cpp:471-486`).
  Idle ⇒ 0 ⇒ **frame 0 = the closed door**; fully hovered ⇒ 1 ⇒ frame 15 = the open door.
* `FrameImage::GetFrame` = `bound((int)(value * nFrame), 0, nFrame - 1)`
  (`engine/frameimage.cpp:114-122`).
* The frames are **XOR RLE deltas over a background surface**
  (`ExtractRLEFrame`, `engine/frameimage.cpp:124-173`; `PlayRLE`, `:175-232`): a WORD per run,
  low 14 bits = count, top 2 bits = fill/BYTE/WORD/DWORD XOR. `FrameImageFactory::Read`
  (`engine/frameimage.cpp:297-311`) reads a DWORD `nFrame`, `nFrame` DWORD offsets and then
  `offsets[nFrame-1]` bytes of RLE (the writer at `:259-267` stores exactly that) — so
  **frame 0 is the background, delta `i` is `[offsets[i], offsets[i+1])`, and frames `1..nFrame-1`
  consume deltas `0..nFrame-2`**. `ExtractRLEFrame` replays `0..iFrame`, so its last pass reads
  `offsets[nFrame]` — one past the array, into the RLE payload's first DWORD — which is 32,771 for
  `animlaunch.mdl` while `offsets[nFrame-1]` is 332,321, i.e. `prle >= pend`: **a no-op**. The port
  reproduces the intent (last pass ends at the payload end ⇒ no-op) instead of reading out of
  bounds (see *Native intent*).
* `animlaunch.mdl` declares **nFrame = 16** and its decoded frames are byte-identical to
  `animlaunchbmp`'s 4×4 sheet of 130×214 cells in **row-major** order — the test asserts that, so
  the frame count and order are pinned by the artwork, not by a guess
  (`P945HangarArtTests.Launch_animation_has_16_native_frames_in_the_sheet_order`).

### 1.4 Hover text

`hoverInfoPane` is a `SwitchPane(CurrentHover, [...])` of `StringPane`s (`hangar.mdl:30-72`) in
`hugeFont`, colour `Color(0.75,0.75,0.75)` (`:26`), size `Point(600,24)` (`:27`), `JustifyCenter`.
`StringPane::Paint` (`engine/controls.cpp:533-575`) draws at the pane's top-left
(`origin = WinPoint(nXStart, 0) + psurface->GetOffset()`), centres by the measured text width
(`:560-564`), and **clips** to the pane width (`GetMaxTextLength`, `:537-547`). Parsing
`Artwork/font.mdl` gives `hugeFont`: cell height **23 px**, and every native string fits 600 px
(widest = the teleport line at 474 px), so no clipping ever happens with the real font. The hover
ids are `WinTrek/hangarscreen.cpp:36-48`; the strings are set by the `OnHover*` handlers
(`:243-300`).

### 1.5 The in-turret badge

`InTurretPane = SwitchPane(BoardState, [(ImagePane(ImportImage("hangarinturretbmp", false)), 2)])`
(`hangar.mdl:74-80`), placed at (320,450) (`:100`). `BoardState` is 0 = not parented, 1 = parented
observer (`GetTurretID() == NA`), 2 = turret gunner (`WinTrek/console.cpp:797-804`). So the badge
is drawn **only for state 2**.

### 1.6 Docking shows the hangar screen

`WinTrek/WinTrek.cpp:6565` (and `:9381`) `SetViewMode(IsGhost() ? vmCommand : vmHangar)`, and
`case vmHangar:` at `:6706-6718` builds `CreateHangarScreen(GetModeler(), "hangar")` and — note —
**`m_pradarImage->SetRadarLOD(RadarImage::c_rlNone)`** (`:6714`): native hides the radar in the
hangar; the `ofInHangar` overlay flag is raised at `:6836`.

---

## 2. What changed

### New files

| file | what it does |
|---|---|
| `src/Allegiance.Core/Data/MdlFrameImage.cs` | The `FrameImage` half of the binary `.mdl` format: object-graph walk to the `image` export, `nFrame` + offsets, and native's XOR `PlayRLE`, returning `MdlFrameAnimation` (surface size, frames in play order, initial `frame` value). Pure data, no MonoGame types, headless-testable. Reuses `MdlTextureDecoder`'s magic/rounding. |
| `src/Allegiance.ClientCore.Game/Rendering/HangarScreen.cs` | **The** geometry + string source for the native screen: the eight controls with their stems and points, the native pane sizes, face count/rows, the hover-text pane, the in-turret pane, the hover string table, `ShowsInTurretBadge`, the letterbox `ScreenTransform` and the port-only verb rectangles. Draw and hit-test both call this. |
| `src/Allegiance.ClientCore.Game/Rendering/HangarArt.cs` | Loads the artwork: case-insensitive stem resolution (exact name first, then a cached case-folded index of the artwork root — `hangar.mdl` asks for `btnquitbmp`, the file is `BtnQuitbmp.mdl`), `ProbeSize`/`ProbeLaunch` headless probes for the tests, the two-face strip slicing, the 16 decoded launch frames, missing-art logged **once** per stem. Blits opaque (native imports these with `colorKey = false`, `hangar.mdl:13-20`, and `SetColorKeyHint(false)` at `WinTrek/hangarscreen.cpp:54`), so the hangar interior's genuinely black pixels are not keyed away. |
| `src/Allegiance.ClientCore.Game/Rendering/HangarLaunchAnimation.cs` | `FrameImageButtonPane`'s 0.7 s ramp in/out with no loop/finish, and `Frame(nFrame)` = native's `bound((int)(value*nFrame),0,nFrame-1)`. Pure arithmetic. |
| `src/Allegiance.ClientCore.Game/Rendering/HangarScreenRenderer.cs` | Draws the screen: background, controls in native order (`Invest` hidden without developments, `hangarscreen.cpp:120`), launch frame, hover text (native strings, 0.75 grey, centred in the 600×24 pane at the native hugeFont cell height), in-turret badge; a labelled box at the native rectangle for a panes whose art is missing. Both HUD styles take the same path — `HudStyle` changes HUD typography/reticles, not this screen's art. |
| `src/Allegiance.ClientCore.Game/LiveProof/HangarProofScript.cs` | `--auto-hangar` script parser (`hover:launch@1,shot@1.5,click:loadout@3,back@4,...`), report-only for typos. |
| `src/Allegiance.ClientCore.Game/Game1.HangarProof.cs` | The script driver: parks the synthetic pointer on the **drawn** rectangle, clicks through the production `HangarControls.Hit` → `ActivateHangar`, sets the same `_hangarOnly` flag Escape sets, and queues the ordinary P572 autoshot. Inert without the flag. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P945HangarArtTests.cs` | 16 headless tests (§4). |

### Edited files

| file | change |
|---|---|
| `Rendering/HangarControls.cs` | Geometry now delegates to `HangarScreen` (native rects, port-only cells); `Feedback` returns the native hover strings (port wording only for Buy/Default). The enum and `HangarLaunchSequence` are unchanged. |
| `Game1.Hangar.cs` | `DrawHangarControls` draws the native screen when the art is installed, else the pre-P945 text row (same rectangles) — never blank; the launch animation is advanced in `UpdateHangarControls` from the same hover the hit test computes; `_nativeHangarScreen` property; renderer field. |
| `Game1.Rendering.cs` | The flight HUD, radar and starmap yield to the native hangar screen (native sets radar LOD none and raises `ofInHangar`); the loadout screen's board text stays on the loadout screen only, so the hangar shows the native badge instead of the port's `[IN TURRET]` text. |
| `Game1.Panes.cs` | Docking now enters the **hangar view** (`_hangarOnly = true`) like native `vmHangar`; pre-P945 the port opened the loadout view, which is why the native hangar was never the first docked screen. |
| `Game1.cs`, `Game1.Lifecycle.cs` | `_nativeFonts` kept as a field (loaded once, already used by the HUD) for the hangar hover text; the new renderer is disposed in `UnloadContent`. |
| `Game1.AutoProof.cs`, `LiveProof/AutoProofPlan.cs` | `--auto-hangar` flag + `ScheduleAutoshot(..., nameTag)` for readable proof filenames. |
| `packaging/artwork-{roots,manifest}.txt` | Regenerated (`generate-manifest.py`): +12 hangar art files the code now needs. Diff reviewed — only hangar art. |
| `docs/parity/parity-matrix.csv` | Six new `screens` rows for the hangar screen's visuals (art/layout, face strips, launch animation, hover text, in-turret badge, dock-enters-hangar), all `done-visual` with `VISUAL:` reasons; no other row's status changed. |
| `GLOSSARY.md` | New "Native hangar screen (P945)" entry (glossary link check passes). |
| `docs/TEST_FLAKES.md` | P945 validation section: the two self-inflicted regressions (disposal guard, packaging manifest) that the first sweep caught and that are fixed, plus the environment/load flakes measured 3×/5×. |

### Where Buy and Default went (and why)

Native's hangar has **no Buy and no Default button** — `HangarControls.Actions` carries 10 verbs,
native's `hangar.mdl` has 8 panes plus the hover strip and the badge, and native's hover id enum
(`WinTrek/hangarscreen.cpp:36-48`) has no such ids. They are port-only verbs (P759): buying is what
the loadout screen does in native.

They stay **reachable and clickable**, but out of the 800×600 artwork:

* preferred: the **letterbox margin** beside the scaled screen (`PortVerbRect`) — at 1920×1080 the
  screen is height-limited, leaving 240 px per side; the cells ("Buy", "Def") sit at the
  bottom-right of that margin and the screenshots show them there;
* when the viewport is exactly 4:3 (no margin at all) they fall back to the bottom-right corner
  **inside** the screen, past the hover pane's right edge (native's lowest/right-most control is
  the hover text at x 100..700, y 561..585) — the cells are 46×16 native px and cover only dark
  console pixels of the background.

They remain hit-tested through the same `HangarScreen.Rect` the renderer draws, and their hover
text keeps the port's wording (native has none). `P945HangarArtTests.Port_only_verbs_sit_outside_the_native_art_when_there_is_a_margin`
pins both placements.

---

## 3. Real-window proof (P572 `live-proof.sh`, 1920×1080, real backbuffer)

Both runs: local server, real window, `--auto-side 0 --auto-ready`, no auto-launch, the scripted
hangar driver on top:

```
--auto-hangar=hover:launch@1,shot@1.35,shot@1.9,hover:loadout@3,shot@3.5,idle@4.5,shot@4.8,\
click:loadout@6,shot@6.6,back@8,shot@8.5,click:launch@10
```

Screenshots: `.scratch/p945/proof/classic/` (HUD `{"Style":"Classic"}`) and
`.scratch/p945/proof/modern/` (HUD `{"Style":"Modern"}`), each with the client log
(`client.out`) and server log. Every screenshot below was opened and read during this task —
these descriptions are of what is actually visible.

**Classic** (`proof_hangar02…png`, t≈1.35 s after the hangar appeared — launch hover, mid-ramp):
the native 800×600 station interior, letterboxed with black bars left and right, filling the
middle of the 1920×1080 frame. The four monitor panels, the "HANGAR 1 S" door, the blinking console
lights, the round radar dish and the keyboard are all native bitmap art. The door is caught
**half open**: the aperture shows the bright vertical light strip and a sliver of hull, and the
animated right-hand door edge has not finished travelling. The `EXIT GAME` button is visible at
the bottom of the screen, and "Launch ship" is centred in grey on the bottom strip. Bottom-right,
in the right letterbox margin, two small cells read "Buy" and "Def". **No flight HUD**: no
radar, no weapons text, no gauges.

**Classic `proof_hangar03` (t≈1.9 s — the same hover 0.7 s later):** identical framing, but the
door is now **fully open** — the aperture is a black rectangle with stars and the right-hand door
plate has slid away; "Launch ship" still centred, Buy/Def still in the margin. That pair is the
0.7 s animation, proven on screen.

**Classic `proof_hangar05` (loadout hover):** the pointer sits on the lower-right monitor; that
monitor's art switches to the **inside face** — the whole loadout panel lights up green/white
against the dark red normal face in the other frames — and the hover text reads "Ship's loadout".
The launch door is back to **closed** ("GAR 1 S" visible): leaving the launch pane ramped it down.
This is the ButtonFaceUp/ButtonFaceInside pair from the strip.

**Classic `proof_hangar07` (idle, t≈4.8 s):** pointer parked at (1,1); no hover text at all
(native's hover-none string is empty), every button on its normal dark face, the door closed.

**Classic `proof_hangar09` (after `click:loadout`):** the port's loadout/shop screen — "STATION
DOCKED - LOADOUT", the hull list, the pending-loadout and money blocks, the hull stat panel and the
orbiting hull render. The click on the **native Loadout pane** opened it (the log shows
`hangar click Loadout -> 1331,589 (hit Loadout)`).

**Classic `proof_hangar11` (after `back`):** the hangar screen again with Loadout still hovered
(bright panel + "Ship's loadout"), i.e. the shop → hangar back path works and the native screen is
redrawn.

**Classic `proof_t012.0…png` (2 s after `click:launch`):** full flight HUD, cockpit view over
asteroids with the lock/lead reticle, `SIG: 60%`, `WEAPONS: GROUP BOOST: OFF`, radar mini-map —
the Launch pane's click really launched the ship (the log shows `hangar click Launch -> (hit
Launch)` then `undocked: leaving hangar`).

**Modern** (`proof_hangar02/03/05/07/09/11`): the hangar frames are pixel-for-pixel the same
native screen — same door frames, same lit loadout monitor, same grey hover text, Buy/Def in the
margin — because the hangar screen's art is not a per-style asset. That the Modern profile really
was active is visible in the post-launch shot (`proof_t012.0…png`): the Modern HUD (circular
purity-style gauges, "HUD Tactical", the modern target/radar groups) instead of Classic's.

Console evidence for the click paths (Classic run):

```
[autoproof] --auto-hangar: 12 scripted step(s) from the hangar appearing
[autoproof] hangar hover Launch -> 1018,484 at 1.2s
[autoproof] hangar hover Loadout -> 1331,589 at 3.6s
[autoproof] hangar click Loadout -> 1331,589 (hit Loadout) at 6.1s
[autoproof] hangar back -> hangar screen at 8.3s
[autoproof] hangar click Launch -> 1018,484 (hit Launch) at 10.5s
[monogame] undocked: leaving hangar
```

Not photographed: the **in-turret badge** in the real window — it needs a second client boarding a
turret (`--auto-board`), which is a separate P803-style scenario; the state switch is pinned
headlessly instead (`In_turret_badge_is_drawn_only_for_board_state_two`, and the badge draws at
`hangarinturretbmp`'s decoded 172×30 at the native (320,450)).

---

## 4. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P945HangarArtTests.cs` (16 tests, all
`Requires=Artwork`; **16 passed / 0 failed / 0 skipped** — 3× alone: 16/16, 16/16, 16/16):

1. `Every_hangar_art_stem_resolves_with_its_native_size` — background 800×600, badge 172×30,
   every strip resolves case-insensitively with `NativeSize == (stripWidth, stripHeight/2)`, the
   launch pane is the FrameImage's 130×214 with 16 frames.
2. `Launch_animation_has_16_native_frames_in_the_sheet_order` — decoded frames ==
   `animlaunchbmp` 4×4 sheet, byte-exact, row-major.
3. `Native_layout_maps_every_point_and_button_rectangle` (800×600, 1920×1080, 2560×1080) — scale,
   centre, every pane point/rect, hover pane, in-turret pane, and y-down ordering
   (launch above exit above hover text).
4. `Hit_test_is_the_draw_rectangle_for_every_action` (three viewports) — centre **and all four
   corners** of every one of the 10 verbs hit that verb, no overlaps, (0,0) misses, Invest hidden
   without developments.
5. `Port_only_verbs_sit_outside_the_native_art_when_there_is_a_margin` (16:9 + 4:3).
6. `Hover_ids_map_to_the_native_string_table` — the eight native strings verbatim, 0.75 grey,
   23 px cell.
7. `Native_hover_strings_fit_the_600px_hugefont_pane` — `hugeFont` parsed from `font.mdl`:
   height 23, every native string ≤ 600 px.
8. `In_turret_badge_is_drawn_only_for_board_state_two` — null/0/1 false, 2 true.
9. `Launch_animation_ramps_in_0_7s_and_back_out_like_native` — 0 → 0.5 (frame 8) at 0.35 s → 1
   (frame 15) at 0.7 s, clamped, ramps back to 0 (frame 0); frame mapping bounds.
10. `Button_strips_draw_the_normal_face_then_the_inside_face`.
11. `Missing_art_selection_falls_back_to_the_text_row_hangar` — an empty asset source reports every
    stem absent (no throw) and `IsInstalled == false`, so the text-row hangar is used.

Affected suites re-run: `P748InterfaceTests` + `P803` + `P519` + `P787` + `ScoreboardToggleGate` +
`P571AutopilotDock` + `P932` + `P911` + `P910` → **133 passed / 0 failed / 0 skipped**.

## 5. Gate

| item | result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **succeeded, 0 warnings, 0 errors** |
| `tools/build-tools.sh` | **118 tool projects built, 0 warnings, 0 errors** |
| New tests 3× alone | **16/16, 16/16, 16/16** |
| One full Fast tier (`tools/run-tests.sh --tier fast`) | 13,329 tests, 631 s, **FAIL** — every failure is a ledged/known entry or host load; see below. My own two regressions found by the first sweep were fixed before this run. |
| Showcase never-parks 5× (`P700ShowcaseLiveTests`, both cases) | **4/5 passes** vs the ledged 5/5 base rate; the single failure happened inside a 12 m 17 s run (the passing runs took 8 m 15 s–13 m 55 s) with the host at load average 11–14, and neither case exercises the docked screen. |
| Training gate (`tools/run-training-tests.sh --no-build --repeat 3`) | `Allegiance.ClientCore.Tests` training cohort **126/126 PASS** (11 s). The `MonoGame.Tests` training cohort (real-window lessons, ~9 min per case) did not complete inside its 20-minute budget on this loaded host: `P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(Classic, reminders)` failed its M2 **prompt-sequence snapshot** ("native prompts missing or out of order") after a 9 m 3 s run — that snapshot is `ClientTrainingSite.Messages`, i.e. instructor messages posted by the training *host*, which P945 does not touch (no host, mission or input code changed; the docked view flag and the HUD draw path are presentation-only). The ledged family is "full-gate-only real-window window failure, cause not isolated". The training tests that do exercise the docked screen (`P932`/`P911`/`P910`, 133 tests) all pass. |
| Non-task failures 3× alone | `StationCombatLiveTests.Cross_side_missile…` **pass/pass/pass**; `P902SoundTemplateTests…allocate_nothing` **pass/pass/pass**; `BallotVotingLiveTests.Resign_passing…` **pass/pass/pass**; `NpcWingmanDefendLiveTests…` known persistent baseline failure (fails on the unmodified base, 9 prior ledger entries); `MissileLockLiveTests.MissileLock_BuildsPartialThenFull…` known open failure (P933/P935/P938); `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` **fail/fail/fail** — environmental: the engine's own tailnet scan finds a live Allegiance server on this workstation's tailnet (`100.75.197.111:6073`, 14 players) even though the test injects a null status; no discovery code was touched. Recorded in `docs/TEST_FLAKES.md`. |

`P945HangarArtTests` reports **Skipped: 0** (the artwork corpus is present); the two `SkippableFact`
methods would skip on a clone without `ALLEGIANCE_ARTWORK_PATH`.

---

## Native intent

* **Design — the launch button is the hangar door.** Native does not draw a "LAUNCH" button: the
  launch pane *is* the 130×214 hangar-door aperture in `hangarbmp`, and hovering it plays the
  0.7 s door-opening cinematic (`animlaunch.mdl`'s 16 XOR-delta frames), leaving closes it again.
  That is why the pane is 130×214 at (367,162) rather than a labelled button. Ported as-is.
* **Design — two faces per button, no pressed face.** `ButtonPane(..., 5, ...)` requests exactly
  `ButtonFaceUp | ButtonFaceInside`, so the artwork has no pressed state; native deliberately
  falls back to the up face while the mouse is down (`engine/button.cpp:245-256`). Reproduced (the
  port draws the same two strip rows; it never invents a third).
* **Era constraint — Win32 bitmap fonts.** The hover text uses the engine's pre-rasterised
  `hugeFont` (23 px cell, `font.mdl`). Win32 Arial/Verdana bitmaps are not redistributable, so the
  port substitutes its bundled DejaVu Sans bake scaled to the native cell height (FONT-1's
  established substitution). The strings, colour, pane, centring and screen scale are native.
* **Era constraint — `ImportImage(..., false)` is opaque.** The hangar art has no colour key;
  native relies on black pixels staying black. The port therefore blits it with alpha 255 instead
  of the HUD-art near-black key (`MdlTextureDecoder.ToPremultipliedRgba`), which would punch
  holes in the interior.
* **Bug — the `FrameImage` last-pass offset is out of bounds.** `ExtractRLEFrame` replays deltas
  `0..iFrame` for a frame whose payload ranges only exist for `0..nFrame-2`, so its final pass
  reads `m_pdwOffsets[nFrame]` — the first four RLE bytes reinterpreted as an offset. For
  `animlaunch.mdl` that value (32,771) is below `offsets[nFrame-1]` (332,321), so the pass is a
  no-op and the art is fine; a file whose first RLE WORD happened to be a large number would
  decode differently. **Not copied**: the port's last pass ends at the payload end (also a no-op),
  so it reproduces what native actually draws without the out-of-bounds read.
* **Bug — the port opened the loadout screen on docking.** Pre-P945 `HandleDockedScreen` set
  `_hangarOnly = false` on dock, so the first docked screen was the shop and the native hangar
  only appeared via Escape. Native docks into `vmHangar` (`WinTrek/WinTrek.cpp:6565,9381`, screen at
  `:6706`). Fixed (one line) because otherwise the screen this task draws would never be the one
  the player sees; the loadout is one click away, on the native Loadout pane.

## Modern approach

The native hangar screen is the same artwork in **Classic and Modern** — `HudStyle` overrides HUD
typography, reticles and gauge art, and none of that is part of `hangar.mdl`; there is no
per-style hangar asset in the artwork tree. So the renderer has no style branch at all: both
styles get the letterboxed native screen, the native button faces and the native hover text, and
both were captured (§3). The only style-visible difference in the proof pairs is the HUD in the
post-launch frame, which is exactly where the styles differ.

Where the port deliberately differs from a literal native implementation, for a modern
maintainer:

* **One geometry source.** `HangarScreen` is the single table consulted by the renderer and the
  pointer hit test, so "drawn" and "clickable" cannot drift — the 800×600 coordinates scale by
  `min(w/800, h/600)` at any resolution, and the tests check three viewports plus the rectangle
  corners.
* **Portable art loading.** Stems resolve case-insensitively (Windows-native names vs a
  case-sensitive checkout), sizes are probed headlessly so the test suite can assert the layout
  without a GPU, and the `FrameImage` decoder lives in `Allegiance.Core` with no MonoGame types.
* **Graceful degradation.** Missing art logs once and falls back (text row for the whole screen, a
  labelled box for one pane) instead of native's NULL `LoadImage` blanks — the screen this task is
  about must never be empty.
* **New, minimal proof surface.** `--auto-hangar` boots no new scenario: it drives the production
  hover/click paths and the existing autoshot, so the hangar screen can be photographed
  reproducibly in CI-style runs.
