# P1040 — Jet HUD skin story 6/9: console, chat/command, lifecycle and training callouts (P972 slice 3D)

Report name: `docs/reports/JET_CONSOLE_P1040.md`.

## Context
Jet stories P1035–P1039c are merged (`docs/reports/JET_*`). Read them, `Rendering/Jet/`, and P972
(`docs/reports/UI_SKINS_P972.md`) **§4 (Jet), §5.1 roll tutorial row (line 357), §5.3 training callouts row
(line 397), §5.5 (26 rows, lines 431–460), §5.6 last two rows (turret gunner identity, command view ship summary),
and slice 3D (line 600)** first. **Write the report FIRST** (skeleton, a status line per item) and update it after
each item.

Today Jet does not draw its own console: `JetHudRenderer.DrawConsole` (`Rendering/Jet/JetHudRenderer.cs:442`)
forwards chat, cargo and flight parts to the Classic passes, `DrawCommand` (`:438`) forwards the command summary,
and pod/death go through `_resources.PodDeath`. P1038 already covers some §5.5 rows in the master-caution panel
(see `CoverageIds`, `JetHudRenderer.cs:50-75`).

## Items
1. **Walk all 26 §5.5 rows** in the report, plus the §5.6 turret-gunner and command-view rows, the §5.3 training
   callout row and the §5.1 roll tutorial row: for each, Jet equivalent implemented (file:line) / already covered
   by an earlier Jet story (cite) / shared and unchanged (say why) / deferred with a reason. Nothing silently
   dropped.
2. **Jet console/chat** (command-line console): chat history with channel/sender, bold leader, the same
   history/scroll/channels and configured height; **all five compose states** with recipient and caret; typed
   input/focus unchanged. Same 0.5 s slide progress/sound/mask edges applied to the vector pane.
3. **`CMD` console**: recipient/verb/wing controls with the **same hit rectangles and fan-out** as today. Draw and
   hit test must use one geometry source (P972 §6.1 rule) — prove it with a test that clicks every control
   through the Jet geometry and gets the same action as Classic/Modern.
4. **Tips, orders, vote, location**: three ordinary tip slots and the critical slots with the shared 30 s / 7.5 s
   lifetimes (check what P1038's caution panel already does — no duplicate display of the same tip);
   `ACC`/`QUE` order text lines; `VOTE` banner with the same text/offset and Y/N input; `SECTOR` readout;
   `TURRET / carrier` identity; `MISSION T−/T+`; no flight money counter (investment pane only).
5. **Lifecycle**: `LIFEPOD` panel + return-to-base binding; `OXY` segmented endurance + numeric (same fraction);
   `PILOT DOWN / GHOST` banner under the same `IsGhost` gate; command-view ship summary with identical fields and
   nothing invented; turret gunner rows.
6. **Training**: thin-vector training HUD callouts and hint in Jet chrome with the **same geometry/anchor/text and
   control flow** as the shared training callouts; the roll tutorial cue as a ladder roll pointer. Training
   preference restore already snapshots the skin id (`Game1.TrainingIntegration.cs:10-65`): add a unit test that
   a lesson started under Jet restores Jet (and Modern when `hud.jetSkin` was switched off meanwhile — state the
   rule). Do not change training control flow.
7. **800×480**: every new element fits with the existing Jet furniture; stated compact forms. Long chat lines,
   long pilot names and long recipient lists must fit or truncate with `…` by a stated rule.

## Audit and evidence
All existing Jet audit classes (stroke-vs-text, text-outside-plate, text-off-screen, icon-too-small, text-in-plate,
cue-vs-readout, legible-behind-pane, TextOverlapCross, TextOverlapSame, TextOverGlyph, radar StrokeThroughText) stay
0 in every Jet scene, including the new ones. Add `hud2-proof --jet` scenes for: chat history (long), each of the
five compose states, CMD console with a long recipient list, tips + critical tips, vote, orders, lifepod with oxygen,
ghost/death, turret/carrier, command view, training callout + hint, roll cue — at 1920×1080 and 800×480, dark and
bright (+ one 4K). PNGs into `docs/reports/JET_CONSOLE_P1040_assets/` (≤ 2 MB total; include 2× nearest-neighbour
crops of the console). **Open and look at every image yourself**; where your vision is unreliable at small fonts,
say so, rely on the 2× crops and the measured boxes, and never describe a position the measured rectangles
contradict.

## Gate
Baseline-first Classic/Modern zero-delta (1,848 combinations, 0 pixel / 0 semantic diffs) — capture the baseline
before editing. 0 B/frame (chat/console text formatting must not allocate per frame — preallocate/cache). New tests
3× alone. Build 0/0 + `tools/build-tools.sh`. One `tools/run-tests.sh --tier fast` (non-task failures 3× alone +
`docs/TEST_FLAKES.md`). **Training gate:** if any training-affecting file outside `Rendering/Jet/` changes, run
`tools/run-training-tests.sh --no-build --repeat 3` with artwork; if none changes, say so and list the files changed.
Parity CSV rows unchanged in status (presentation only).

## Not in scope
Amber variant (P1041), screens/menus (P1042), ship gate (P1043). No server, Core, wire, audio or input change; no
change to chat routing, command fan-out, training lesson logic, or which tips/orders exist.

---
## Common rules (every Jet story)
- Behaviour parity: Jet is presentation only; every value comes from the existing `HudModel`/native-derived data.
  Never invent data.
- Read `AGENTS.md`, `GLOSSARY.md`, and P972 §4 + §9 before starting. Native 2-D conventions apply (y-up, StringImage
  hangs below its point, GroupImage paints in reverse). HUD work covers Classic AND Modern (prove unchanged).
- Lane environment: MS-A2 Debian 13 container, own clone; your worktree is the current directory (own branch). SDK
  `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only;
  `local.env`). Docker is not installed. Do not leave the brief file in the change set.
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1040/`, do not
  commit or push, do not contact the rig, mini, Mac, VPS or any public host. Public-repo hygiene. Every process you
  start must end before you finish. Build-hang workaround: `--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false`.
- **Time:** 6 hours max; report first and kept current. If time runs short, finish items 1–3 properly and report
  exactly where the rest stopped.
- **Report:** what changed (file:line), the row walk, audit counts, tests (Passed/Failed/Skipped), zero-delta counts,
  anything unfinished. Footer: "Native intent" and "Modern approach".
