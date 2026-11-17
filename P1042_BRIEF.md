# P1042 — Jet HUD skin story 8/9 (part 1): screen-chrome boundary + Jet intro and hangar/loadout chrome (P972 slices 1F + 3F, first half)

Report name: `docs/reports/JET_SCREENS_P1042.md`.

## Context
Jet stories P1035–P1041 are merged (`docs/reports/JET_*`; amber = `JET_AMBER_P1041.md`). The flight HUD is done in
green and amber. Screens are not: **the screen-chrome boundary `IScreenSkinRenderer` (P972 slice 1F) does not exist**
— the skin system covers the flight HUD only (`Rendering/Skins/`). Read P972 (`docs/reports/UI_SKINS_P972.md`)
**line ~114 (`IScreenSkinRenderer` file row), ~172 (its contract: semantic screen model + the SAME resolved rectangles
used by hit testing; it can never buy/launch/sort/join/navigate/change focus), ~212 (atomic switch: screen chrome
changes with the skin), ~492 (screen families; native slideshow pictures remain content), slices 1F (~579), 2E (~590)
and 3F (~602)**, then `Game1.IntroScreen.cs`, `Rendering/HangarScreen*.cs`, `Rendering/Hangar*.cs`,
`Rendering/DockedScreenRenderer.cs`, `Rendering/DockedTouchZones.cs`, and the reports `INTRO_KEYS_P946.md`,
`P731_INTRO_SCREEN.md`, `HANGAR_ART_P945.md`, `P519_NATIVE_LOADOUT_SCREEN.md`. **Write the report FIRST** (skeleton +
a status line per item) and keep it current.

This story is part 1 of the screens work: the boundary plus intro and hangar/loadout. The remaining screen families
(browser/connect, options, career/training chooser, create game, team/ready, briefing, starmap, scoreboard/game over,
help/credits, key bindings, quick chat, in-flight panes) are **P1042b** — do not start them; list them in the report
with the file each lives in, so P1042b can be briefed.

## Items
1. **Boundary, zero visual delta (slice 1F, intro + hangar/loadout only).** Add `Rendering/Skins/IScreenSkinRenderer.cs`
   and a Classic/Modern implementation that is a pure adapter over today's drawing (the `HudSkinRenderer` pattern).
   Route the intro screen and the hangar/docked/loadout screens through it. The renderer receives a read-only screen
   model plus the resolved rectangles that hit testing already uses — **one geometry source** (P972 §6.1): prove it
   with tests that every drawn button/row/zone rectangle equals its hit rectangle, for every intro action and every
   hangar/loadout action. No change to any value, command, focus order, hover, ACK, animation, sound or native art.
2. **Classic/Modern screens unchanged.** Capture screen goldens of intro and hangar/loadout (every state the existing
   proof tools can reach: hover/focus/selected/disabled where applicable, at 800×480 and 1920×1080) **before editing**,
   and compare after: decoded-pixel equality, 0 diffs. Say which tool you used or added.
3. **Jet intro chrome** (flag `hud.jetSkin` on, skin Jet), both palettes: thin-vector frames, plates and keylined text in
   the Jet palette around the same actions; native slideshow pictures/art untouched; same 13 intro commands (cite
   `INTRO_KEYS_P946.md`), same keys, same focus/hover, same rectangles.
4. **Jet hangar/loadout chrome**, both palettes: same Buy/Default/ACK/launch/hover/badge/animation and the same
   rectangles; ship preview, hangar art and launch animation are content (unchanged). Native values/text only.
5. **Switching:** the screen chrome changes with the skin at the same update/draw boundary as the HUD skin and the palette
   (no half-switched frame; nothing preloaded lazily in `Draw`); switching on a screen keeps focus/selection/scroll.
   With `hud.jetSkin` off nothing in this story is visible.
6. **800×480 and 4K:** every Jet screen element fits at 800×480 (stated compact rules) and is sharp at 4K.

## Audit and evidence
Extend or add a proof tool so Jet screens get the layout audit classes that apply (stroke-vs-text, text-outside-plate,
text-off-screen, TextOverlapCross/Same, plus a new **`hit-vs-draw`** class: any drawn control rectangle ≠ its hit
rectangle) — all 0. Capture intro and hangar/loadout in Jet green and amber at 1920×1080 and 800×480, dark-relevant
states (focused/hover/disabled), plus one 4K, into `docs/reports/JET_SCREENS_P1042_assets/` (PNG ≤ 2 MB total; 2×
nearest-neighbour crops of one 800×480 intro and the loadout list). **Open and look at every image yourself**; where
vision is unreliable at small fonts, say so, rely on the crops and measured boxes, never describe something the image
does not show.

## Gate
Baseline-first: the 1,848-combination HUD zero-delta (Classic/Modern, 0 pixel / 0 semantic) AND the item-2 screen
goldens (0 diffs) — both captured before editing. Jet flight HUD green/amber unchanged (byte-identical `--jet` captures
vs before). 0 B/frame on the Jet screens (allocation test). New tests 3× alone. Build 0/0 + `tools/build-tools.sh`.
One `tools/run-tests.sh --tier fast` (non-task failures 3× alone + `docs/TEST_FLAKES.md`). **Training gate:** if any
training-affecting file changes (the intro's training/single-player chooser entry counts), run
`tools/run-training-tests.sh --no-build --repeat 3` with artwork; the 21-min real-window proofs may be excluded from
repeats 2–3 only, and then run them once and report each child's result. Otherwise say so and list the files changed.

## Not in scope
P1042b screen families (above), ship gate/real-window proof (P1043). No server, Core, wire, audio or input change; no
change to any screen's commands, values, focus order or native art. Do not make Jet selectable without the flag.

---
## Common rules (every Jet story)
- Behaviour parity: Jet is presentation only; every value comes from existing models/native-derived data. Never invent
  data. Visual departures stay behind `hud.jetSkin` (default off).
- Read `AGENTS.md`, `GLOSSARY.md`, and P972 §4 + §9 before starting. Native 2-D conventions apply (y-up, StringImage
  hangs below its point, GroupImage paints in reverse). Every change covers Classic AND Modern (prove unchanged).
- Lane environment: MS-A2 Debian 13 container, own clone; your worktree is the current directory (own branch). SDK
  `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only;
  `local.env`). Docker is not installed. **Do not leave the brief file in the change set** (check `git status`).
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1042/`, do not
  commit or push, do not contact the rig, mini, Mac, VPS or any public host. Public-repo hygiene. Every process you
  start must end before you finish. Build-hang workaround: `--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false`.
- **Time:** 6 hours max; report first and kept current. If time runs short, finish items 1–2 (the boundary with zero
  delta) and item 3 properly, and report exactly where the rest stopped.
- **Report:** what changed (file:line), the P1042b screen list, audit counts, tests (Passed/Failed/Skipped), zero-delta
  counts (HUD + screens), anything unfinished. Footer: "Native intent" and "Modern approach".
