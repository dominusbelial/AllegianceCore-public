# P1042b — Jet screens part 2: Buy/Def doubled-text fix + the pre-game screen families (P972 slices 1F/3F, cont.)

Report name: `docs/reports/JET_SCREENS_P1042B.md`.

## Context
P1042 is merged (`docs/reports/JET_SCREENS_P1042.md` — read it fully, especially §1 the boundary, §4 hangar, §7 audit,
the "P1042b — the remaining screen families" table and "Unfinished / carried to P1042b"). It added
`Rendering/Skins/IScreenSkinRenderer.cs`, `ClassicScreenSkinRenderer.cs`, `ScreenFrames.cs`, `ScreenHitGeometry.cs`,
`ScreenSkinRegistry.cs`, `Rendering/Jet/JetScreenChrome.cs`, `JetScreenSkinRenderer.cs`,
`Rendering/Screens/IntroScreenPainter.cs` and `tools/p1042-screen-proof`. Only the intro **main menu** and the
hangar/Loadout V2 go through the boundary today. Also read P972 (`docs/reports/UI_SKINS_P972.md`) §4, §6.1, §9 and the
screen-families section, `AGENTS.md`, `GLOSSARY.md`. **Write the report FIRST** (skeleton + a status line per item) and
keep it current.

This is part 2 of 3. Part 3 (**P1042c**, do not start) is the in-game families: team/side select + ready check,
briefing, starmap/sector info, scoreboard/game over, key bindings, quick chat, in-flight panes, connection lost, and
Jet frames for the legacy native-layout docked shop.

## Items
0. **Defect from P1042 review: doubled Buy/Def text.** With Jet on, the hangar's `Buy`/`Def` cells show Jet's keylined
   text drawn over the native label (two overlapping words), at 1920×1080 and 800×480 (`JetScreenSkinRenderer.cs:182`
   `DrawHangar`, `PortVerbRect`). Find where the native label comes from (baked into the `hangar.mdl` button art, or
   drawn by `HangarScreenRenderer`) and fix it so exactly ONE label is visible: if the label is native art/content,
   Jet draws frame/plate only and no text there; if it is port-drawn text, Jet replaces it through the boundary.
   Add an audit rule that catches this class (Jet text box overlapping a native/content text or label region in the
   same control → `TextOverlapCross`), prove it flags the pre-fix build and is 0 after. Crop evidence at 2× of the
   cells, before and after, both palettes.
1. **Route the pre-game families through `IScreenSkinRenderer`, zero Classic/Modern delta.** Same pattern as P1042
   (pure adapter; read-only screen model + the SAME resolved rectangles hit testing uses; draw == hit tests for every
   control):
   - intro sub-views: Find Server / server browser / Direct Connect (`Game1.ConnectScreen.cs`,
     `src/Allegiance.ClientCore.Game.Lobby/`, `Rendering/ConnectScreenLayout.cs`), host, join link, benchmark;
   - options / privacy (`Game1.OptionsMenu.cs`, `OptionsMenuModel.cs`) and the settings sub-view;
   - career / squads / events (`Game1.Career.cs`, `Rendering/CareerScreenRenderer.cs`);
   - single-player + training chooser, slideshow, suggest (`Game1.IntroScreen.cs` `IntroView.SinglePlayer`,
     `Training`, `TrainingSlideshow`, `TrainingSuggest`; `TrainingScreenModel`, `SinglePlayerChooserModel`) —
     **slideshow pictures stay native content**;
   - create game wizard (`Game1.CreateGame.cs`);
   - help / credits (`Rendering/HelpOverlayRenderer.cs`, `Rendering/CreditsOverlayRenderer.cs`).
   Capture Classic AND Modern goldens of every reachable state of these screens **before editing** (extend
   `tools/p1042-screen-proof`), compare after: decoded-pixel equality, 0 diffs (explain any wall-clock animation delta
   with a same-binary control, as P1042 §2.3 did).
2. **Jet chrome for each family**, green and amber: thin-vector frames, plates and keylined text in the five-role
   palette over the same rectangles; same commands, keys, focus order, hover, selection, scroll, text entry and
   validation; native values/text only; shape/text redundancy, never hue-only selection. Text fields (server address,
   game name, pilot name) keep their caret/selection/IME behaviour exactly.
3. **Switching and sizes:** chrome switches with the skin at the existing boundary (no lazy loads in `Draw`; switching
   on a screen keeps focus/selection/scroll/typed text). Every element fits at 800×480 and is sharp at 4K. With
   `hud.jetSkin` off nothing in this story is visible.

## Audit and evidence
`tools/p1042-screen-proof` audit for every new Jet screen, both palettes, 800×480 / 1920×1080 / one 4K: stroke-vs-text,
text-outside-plate, text-off-screen, TextOverlapCross/Same, `hit-vs-draw` — all 0, counts per screen in the report.
Captures into `docs/reports/JET_SCREENS_P1042B_assets/` (PNG ≤ 2 MB total; include 2× nearest-neighbour crops for every
800×480 screen with small text). **Open and look at every image yourself**; where vision is unreliable at small fonts,
say so and rely on crops and measured boxes — never describe something the image does not show.

## Gate
Baseline-first: the 1,848-combination HUD zero-delta (Classic/Modern) AND the P1042 + item-1 screen goldens (0 diffs),
captured before editing. Jet flight HUD green/amber byte-identical vs before. 0 B/frame on every Jet screen (allocation
test, window sized like P1049: one warm-up cycle + two measured cycles of the largest cache you find, `== 0`). New tests
3× alone. Build 0/0 + `tools/build-tools.sh`. One `tools/run-tests.sh --tier fast` (non-task failures 3× alone +
`docs/TEST_FLAKES.md`). **Training gate (the training chooser is in scope, so this is required):**
`tools/run-training-tests.sh --no-build --repeat 3` with artwork; the ~21-min real-window proofs may be excluded from
repeats 2–3 only — run them once and report each child's result line.

## Not in scope
P1042c families (above), P1043 ship gate. No server, Core, wire, audio or input-map change; no change to any screen's
commands, values, focus order, validation or native art. Do not make Jet selectable without the flag. Do not fix the
Loadout V2 "WEAPON DPS NaN" text (separate task) — just note it if seen.

---
## Common rules (every Jet story)
- Behaviour parity: Jet is presentation only; every value comes from existing models/native-derived data. Never invent
  data. Visual departures stay behind `hud.jetSkin` (default off).
- Native 2-D conventions apply (y-up for HUD images, StringImage hangs below its point, GroupImage paints in reverse;
  screen panes are top-left/y-down, as P1042 noted). Every change covers Classic AND Modern (prove unchanged).
- Lane environment: MS-A2 Debian 13 container, own clone; your worktree is the current directory (own branch). SDK
  `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only;
  `local.env`). Real windows run under the lane's existing Xvfb/offscreen setup used by P1042. Docker is not installed.
  **Do not leave the brief file in the change set** (check `git status`).
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1042b/`, do not
  commit or push, do not contact the rig, mini, Mac, VPS or any public host. Public-repo hygiene: no secrets, no
  personal paths/IPs/hostnames in committed files (evidence paths as `<lane evidence dir>`). Every process you start
  must end before you finish — stop by exact PID. Build-hang workaround: `--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false`.
- **Time:** 6 hours max; report first and kept current. Priority if time runs short: item 0, then item 1 (boundary +
  zero delta) for all families, then item 2 family by family; report exactly where it stopped. A status report is not
  a stopping point.
- **Report:** what changed (file:line), item-0 root cause and fix, audit counts per screen, tests
  (Passed/Failed/Skipped), zero-delta counts (HUD + screens), training-gate child results, anything unfinished.
  Footer: "Native intent" and "Modern approach".
