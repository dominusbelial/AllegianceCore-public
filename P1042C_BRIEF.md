# P1042c — Jet screens part 3: the in-game screen families + legacy docked shop (P972 slices 1F/3F, final)

Report name: `docs/reports/JET_SCREENS_P1042C.md`.

## Context
P1042 and P1042b are merged. Read fully: `docs/reports/JET_SCREENS_P1042.md` (§1 the boundary, §7 audit) and
`docs/reports/JET_SCREENS_P1042B.md` (§1 Buy/Def root cause + `TextOverlapCross`, §2.1 the `IntroViewFrame` contract,
§2.4 Benchmark, "Unfinished / carried"). Also P972 (`docs/reports/UI_SKINS_P972.md`) §4, §6.1, §9 and the
screen-families section, `docs/reports/CI_FAST_TIMING_P1049.md` (P1049b section: Jet allocation windows),
`AGENTS.md`, `GLOSSARY.md`. Boundary code: `Rendering/Skins/IScreenSkinRenderer.cs`, `ClassicScreenSkinRenderer.cs`,
`ScreenFrames.cs`, `ScreenHitGeometry.cs`, `ScreenSkinRegistry.cs`, `Rendering/Jet/JetScreenChrome.cs`,
`JetScreenSkinRenderer.cs`, `tools/p1042-screen-proof`. **Write the report FIRST** (skeleton + a status line per
item) and keep it current.

This is part 3 of 3 — after it every non-flight screen goes through the boundary.

## Items
0. **Carried from P1042b.**
   - **Benchmark real-window golden.** The pre-existing SpriteFont crash (`Game1.Benchmark.cs:99`, "×" not in the
     font) was fixed on master ("x"). Capture the Benchmark sub-view's Classic/Modern goldens and Jet green/amber
     captures that P1042b could not, and audit them.
   - **Character Info title vs tab row.** The career/Character Info title crowds (touches/overlaps) the tab row in
     Jet. Measure the boxes, fix the spacing in the Jet chrome only (Classic/Modern unchanged), and add an audit rule
     that flags a title box within N px of / overlapping a tab-row box (state N); prove it flags before, 0 after.
1. **Route the in-game families through `IScreenSkinRenderer`, zero Classic/Modern delta.** Same pattern as
   P1042/P1042b (pure adapter; read-only screen model + the SAME resolved rectangles hit testing uses; draw == hit
   tests for every control):
   - team / side select + civ picker + team name edit + ready check (`SideSelectOverlayRenderer.cs`,
     `SideSelectLayout.cs`, `TeamCivPickerLayout.cs`, `TeamNameEditLayout.cs`, `ReadyCheckOverlayRenderer.cs`,
     `ReadyCheckLayout.cs`, `TeamPaneRenderer.cs`);
   - mission briefing (`MissionBriefingRenderer.cs`, `MissionBriefingLayout.cs`);
   - starmap + sector info (`StarmapRenderer.cs`, `StarmapLayout.cs`, `StarmapHitTest.cs`, `SectorInfoPaneRenderer.cs`);
     **the map content (sectors, alephs, icons) stays native content** — only frames/plates/text chrome change;
   - scoreboard + game over (`ScoreboardOverlayRenderer.cs`, `GameOverScreenRenderer.cs`, `GameOverTablesLayout.cs`);
   - key bindings (`KeyBindingRenderer.cs`, `Game1.KeyBind.cs`) — capture/rebind flow unchanged;
   - quick chat menu (`QuickChatMenuRenderer.cs`);
   - in-flight panes that are screen panels, not flight symbology (`Game1.Panes.cs`: inventory, investment,
     selection, target, teleport panes; chat pane chrome) — **flight HUD symbology is out of scope** (P1043);
   - connection lost (`ConnectionLostScreenRenderer.cs`);
   - legacy native-layout docked shop (`DockedScreenRenderer.cs`, `ShopLayout.cs`, `ShopModel.cs`): Jet frames
     over the native layout; native shop art/icons stay content.
   Capture Classic AND Modern goldens of every reachable state of these screens **before editing** (extend
   `tools/p1042-screen-proof`; drive real states through the embedded host — no faked models), compare after:
   decoded-pixel equality, 0 diffs (explain any wall-clock animation delta with a same-binary control).
2. **Jet chrome for each family**, green and amber: thin-vector frames, plates and keylined text in the five-role
   palette over the same rectangles; same commands, keys, focus order, hover, selection, scroll, text entry and
   validation; native values/text only; shape/text redundancy, never hue-only selection (team colours keep a
   shape/text cue too). Chat/team-name text fields keep caret/selection/IME behaviour exactly.
3. **Switching and sizes:** chrome switches with the skin at the existing boundary (no lazy loads in `Draw`;
   switching on a screen keeps focus/selection/scroll/typed text). Every element fits at 800×480 and is sharp at 4K.
   With `hud.jetSkin` off nothing in this story is visible.

## Audit and evidence
`tools/p1042-screen-proof` audit for every new Jet screen, both palettes, 800×480 / 1920×1080 / one 4K:
stroke-vs-text, text-outside-plate, text-off-screen, TextOverlapCross/Same, title-vs-tab (item 0), `hit-vs-draw` —
all 0, counts per screen in the report. Captures into `docs/reports/JET_SCREENS_P1042C_assets/` (PNG ≤ 2 MB total;
include 2× nearest-neighbour crops for every 800×480 screen with small text). **Open and look at every image
yourself**; where vision is unreliable at small fonts, say so and rely on crops and measured boxes — never describe
something the image does not show. Use real game text, not fixture text, wherever the screen can be reached live
(P1042b's Help capture used fixture text — say explicitly where a fixture is unavoidable and why).

## Gate
Baseline-first: the 1,848-combination HUD zero-delta (Classic/Modern) AND the P1042 + P1042b + item-1 screen
goldens (0 diffs), captured before editing. Jet flight HUD green/amber byte-identical vs before. 0 B/frame on every
new Jet screen (allocation test using the P1049b `JetAllocationWindow` sizing, `== 0`, in `[Collection("Training")]`
like the existing Jet tests). New tests 3× alone. Build 0/0 + `tools/build-tools.sh`. One `tools/run-tests.sh --tier
fast` (non-task failures 3× alone + `docs/TEST_FLAKES.md`). **Training gate (in-game panes and briefing are used by
training, so required):** `tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once` with artwork; run
the real-window proofs once and report each child's result line.

## Not in scope
P1043 Jet ship/flight gate and flight symbology. No server, Core, wire, audio or input-map change; no change to any
screen's commands, values, focus order, validation or native art. Do not make Jet selectable without the flag. Do not
fix the Loadout V2 "WEAPON DPS NaN" text — note it if seen.

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
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1042c/`, do not
  commit or push, do not contact the rig, mini, Mac, VPS or any public host. Public-repo hygiene: no secrets, no
  personal paths/IPs/hostnames in committed files (evidence paths as `<lane evidence dir>`). Every process you start
  must end before you finish — stop by exact PID. Build-hang workaround: `--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false`.
- **Time:** 7 hours max; report first and kept current. Priority if time runs short: item 0, then item 1 (boundary +
  zero delta) for all families, then item 2 family by family; report exactly where it stopped. A status report is not
  a stopping point. **The training gate and the real-window captures are required items, not optional** — do not end
  without running them; if one fails, report the exact child result line.
- **Report:** what changed (file:line), audit counts per screen, tests (Passed/Failed/Skipped), zero-delta counts
  (HUD + screens), allocation results, training-gate child results, images with what each shows, anything unfinished.
  Footer: "Native intent" and "Modern approach".
