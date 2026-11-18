# P1055 — Screen text-overlap fixes + content-overlap audit + ConsoleLog completed-queue crash

Report name: `docs/reports/SCREEN_OVERLAP_P1055.md`.

## Context
P1042, P1042b and P1042c are merged. Read: `docs/reports/JET_SCREENS_P1042C.md` (§6 audit, §7 assets, §9 carried
limitations), `docs/reports/JET_SCREENS_P1042B.md` §1.3 (`TextOverlapCross`), `docs/TEST_FLAKES.md` (top sections),
`AGENTS.md`, `GLOSSARY.md`. Tools: `tools/p1042-screen-proof`, `tools/p1042b-screen-walk`. Claude's review of the
P1042c captures found overlaps the audit did not count, because `TextOverlapCross` only compares Jet chrome text with
content text — two pieces of the game's own text overlapping each other are never checked. **Write the report
FIRST** (skeleton + a status line per item) and keep it current. **Do not end your turn until items 0–3 are done or
the 6-hour cap is reached.** A status report is not a stopping point.

## Items
0. **Content-vs-content overlap audit (do this first).** Extend `tools/p1042-screen-proof --audit` (and the P1042b walk
   where it audits) with a `ContentOverlap` class: any two content text runs (or a content text run and a content
   panel/box) whose measured boxes intersect by more than a stated tolerance, plus `content-off-screen` (text box past
   the viewport). Run it on every screen the P1042/P1042b/P1042c tools reach, Classic, Modern, Jet green and amber,
   800×480 / 1920×1080 / one 4K, **before** fixing anything; record the full findings table (screen, size, skin,
   the two texts, overlap px). It must flag the two known cases below. Triage each finding: fix in this story,
   native-faithful (the native screen overlaps the same way — cite the native source; keep it and allow-list it with
   the citation), or carried (state why).
1. **Benchmark / Showcase sub-view at 800×480** (`Game1.Benchmark.cs` ~:95-110). In every skin the three-line
   description draws over the "Grand (200v200)" / "Settings tune" / "Replay recorded battle" rows and runs past the
   right edge. This is a port screen (no native pane). Fix the layout so nothing overlaps and nothing leaves the
   screen at 800×480, and it still looks the same at 1920×1080 where it fits (state any pixel change at 1080p and
   why). Options: wrap to the available width, move the description below the list with the list scrolled, or shorten
   — pick one and justify; text content unchanged except wrapping. Keep the P1042b/P1042c boundary rules (draw ==
   hit tests). Captures before/after, Classic + Jet green, 800×480 and 1920×1080.
2. **Starmap sector-info pane over the alert line.** At 1920×1080 (Jet green capture
   `docs/reports/JET_SCREENS_P1042C_assets/starmap__jet-green__1920x1080.png`) the sector info pane (`Sol Sector …
   PROBES`) covers the red "Spotted Cortex" event line under the STARMAP header, and the probe icon overlaps the
   "PROBES 3" count. Check Classic/Modern for the same. Find the native placement (`WinTrek` starmap/sector-info
   panes) and fix the port so the pane and the alert line do not overlap (native placement wins if it differs from
   the port's); fix the icon/count spacing. If native overlaps the same way, keep it, cite it, allow-list it.
3. **ConsoleLog crash on a completed queue (Core test host abort).** P1042c's fast tier aborted
   `Allegiance.Core.Tests` with `InvalidOperationException: The collection has been marked as complete with regards to
   additions` after ~5,000 tests. Claude's reading of the cause: `ConsoleLog.Write` (`src/Allegiance.Core/ConsoleLog.cs`
   ~:160-163) and `QueuedConsoleWriter.Emit` (~:338-341) read `_gen` and then call `gen.Queue.TryAdd(line)`; if
   `ResetForTest` (~:299, test path) or `Shutdown` (~:136, production) has already called `CompleteAdding` on that
   generation, `BlockingCollection.TryAdd` **throws** instead of returning false, and on a background thread that kills
   the process. Confirm or correct this with a deterministic regression test (a writer racing `ResetForTest`/
   `Shutdown`; no busy loops — use a barrier/ordered steps), then fix it so a line hitting a completed generation is
   retried once on the current generation or dropped-and-counted, never thrown, keeping P619c/P829/P964 behaviour (never
   block the caller, counters, shared writer lock). Prove the regression test fails before the fix and passes after,
   3× alone. Ledger the original abort in `docs/TEST_FLAKES.md` with the root cause.

## Evidence
Captures into `docs/reports/SCREEN_OVERLAP_P1055_assets/` (PNG ≤ 1.5 MB total; 2× nearest-neighbour crops of every
fixed 800×480 region). **Open and look at each image yourself** and describe only what it shows; where vision is
unreliable at small fonts, rely on crops and measured boxes.

## Gate
Baseline-first: the 1,848-combination HUD zero-delta and the P1042 + P1042b + P1042c screen goldens captured before
editing; after: 0 diffs except the screens this story fixes, each listed with the changed region and why. Jet flight
HUD green/amber byte-identical. Audit: every class 0 or allow-listed with a native citation, counts per screen in the
report. New tests 3× alone. Build 0/0 + `tools/build-tools.sh`. One `tools/run-tests.sh --tier fast` (non-task
failures 3× alone + `docs/TEST_FLAKES.md`; the Core project must run to completion). Training gate:
`tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once` with artwork — run every real-window child of
repeat 1 to completion, do not stop it early; report each child's result line. Footer: Native intent / Modern
approach.

## Not in scope
In-flight panes and legacy docked shop Jet frames (P1043 / later), flight HUD symbology, Loadout V2 "WEAPON DPS NaN".
No server, wire, audio, input-map or game-rule change; no change to screen commands, values, focus order or
validation. No ConsoleLog behaviour change beyond the completed-queue case.

---
## Common rules
- Behaviour parity: native behaviour wins; visual departures stay behind `hud.jetSkin` (default off). Every change
  covers Classic AND Modern (prove unchanged where not fixed). Native 2-D conventions: y-up for HUD images,
  StringImage hangs below its point, GroupImage paints in reverse; screen panes are top-left/y-down.
- Lane environment: MS-A2 Debian 13 container, own clone; your worktree is the current directory (own branch). SDK
  `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only;
  `local.env`). Real windows under the lane's existing Xvfb/offscreen setup. Docker is not installed.
  **Do not leave the brief file in the change set** (check `git status`; do not commit it).
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1055/`, do not
  commit or push, do not contact the rig, mini, Mac, VPS or any public host. Public-repo hygiene: no secrets, no
  personal paths/IPs/hostnames in committed files (evidence paths as `<lane evidence dir>`). Every process you start
  must end before you finish — stop by exact PID. Build-hang workaround: `--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false`.
- **Time:** 6 hours max; report first and kept current. Priority if time runs short: item 3, item 0, item 1, item 2;
  report exactly where each stopped. **The training gate and real-window captures are required, not optional.**
- **Report:** what changed (file:line), audit findings table before/after with triage, crash root cause + test,
  tests (Passed/Failed/Skipped), zero-delta counts, training-gate child results, images with what each shows,
  anything unfinished.
