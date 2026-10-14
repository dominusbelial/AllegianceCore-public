# P572 — make REAL-WINDOW visual proof scriptable: client auto-seat / auto-ready / auto-launch / auto-goto flags + timed multi-screenshots + a one-command `live-proof` script (local server + optional MCP bots + windowed client)

## Why this task exists

The visual-proof rule now requires **real client window** screenshots (not
`--capture`/offscreen), because offscreen-only proofs hid real-window
regressions: P547 (all geometry invisible), P562 (aleph funnel black), and P566
(native ships inside-out + blank hangar previews, found by Domingo in minutes).
**P566 could not produce real-window screenshots:** "the real-window
`ALLEGIANCE_AUTOSHOT` path needs the client to side-seat interactively, which is
capture-mode-only here and not automatable headlessly; a dogfight additionally
requires spawning combat bots" (`docs/reports/P566_NATIVE_SHIP_MESHES.md` §Performance).

**Existing pieces:** `ALLEGIANCE_AUTOSHOT=<delayMs>` (P559; `Game1.cs:~1018`,
`Game1.FlightInput.cs:~1310-1320`: one real-backbuffer shot via the F11
`CaptureBackbuffer` path); `--capture` auto side-select (P306,
`Game1.Panes.cs` `UpdateSideSelect` capture branch); `--window WxH`; the MCP bots
(`tools/allegiance-mcp`, `spawn_scenario`, arena via `adminCheats`); the stable
MCP stdio driver `~/.local/share/allegiance-mcp-driver/mcp_driver.py` (Claude's,
optional to reuse); the many `tools/*-proof` harnesses.

## Scope

1. **Client automation flags (normal windowed mode, NOT capture mode):**
   - `--auto-side <id>`: pick the side on the side-select screen automatically;
   - `--auto-ready`: send ready (Y) when appropriate;
   - `--auto-launch [hull]`: in the hangar, select the hull (or keep the
     current one), apply its default loadout (P571 if landed, else the current
     parts), and launch;
   - `--auto-goto <sector>`: after launch, engage the P541 star-map goto to a
     sector (route through warps), and `--auto-target <kind>` (nearest enemy /
     friendly / station / aleph) for framing;
   - `--camera chase|cockpit|...` initial camera mode; `--hide-hud` (if P565's
     toggle exists, else a minimal one);
   - `--autoshots <t1,t2,...>`: real-backbuffer screenshots at those seconds
     after launch (extending ALLEGIANCE_AUTOSHOT), to `--autoshot-dir <dir>`,
     filenames with time + sector + camera;
   - `--auto-dock-after <s>` (to shoot the hangar preview: dock, step through
     N hulls in the shop, screenshot each preview), and `--exit-after <s>`.
   All off by default; client-only; nothing on the wire beyond what a player
   sends.
2. **`scripts/live-proof.sh`** (one command): boots a local
   `Allegiance.Server` (separate process, a config with the 221 map, testUnlock
   and optional `adminCheats` for arena), optionally starts the MCP and spawns a
   scenario (e.g. `--bots "spawn_scenario {size:5,target_sector:Alderan}"`),
   launches the windowed client with the automation flags, waits, collects the
   screenshots, shuts everything down cleanly by exact PIDs, and prints the PNG
   paths. Works on this workstation (X/Wayland display available).
3. **Docs:** `docs/LIVE_PROOF.md`, with recipes for: own ship in chase view, a
   dogfight with bots, an aleph at 3 distances, the home station, a hangar preview
   sweep across hulls, soft on/off, `--ships native|meshy`.
4. Update `docs/prompts/README` or the shared prompt footer guidance to point
   at the recipe.

## Proof

- Unit tests for flag parsing and the auto-state machine (side → ready →
  launch → goto → shots → exit), pure parts.
- **Run the script for real** and commit the outputs in the report assets:
  (a) own ship in chase view (native ships), (b) a 5v5 dogfight frame with bots,
  (c) the Rigel aleph at 1 km, (d) a hangar preview sweep of 6 hulls. All real
  backbuffer PNGs at 2560×1440 that open in a normal viewer (check them).
- Report `docs/reports/P572_LIVE_PROOF_HOOK.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
