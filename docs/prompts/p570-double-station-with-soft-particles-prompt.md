# P570 — URGENT: (1) with soft particles ON a second orange copy of the home station appears (gone with `--soft off`); (2) P566 native ships render inside-out in game (front faces culled)

## Why this task exists

**Domingo, live on 221 (client `99df9de6` = P566 on top of P559/P562/P568,
window 2560×1440, soft particles ON), screenshot
`/home/domingo/Pictures/Screenshots/Screenshot From 2026-10-06 21-33-50.png`:**
in Ganymede, the teal friendly Garrison placeholder (hub + ring) is drawn, and
a **second, darker orange hub + ring** appears below it at a different
position and orientation. **Relaunching with `--soft off` removes it** ("yes
is gone").

**Facts (Claude):**
- The server sends side 0 exactly **one** station (`[stations] side 0 sees 1
  station(s)`), and the colour-pass loop filters to `st.ClusterId ==
  mySector` (`Game1.Rendering.cs:~680-709`).
- **Orange = the colour pass's enemy/neutral tint**: `sTint = (MySideId ==
  sSide) ? friendly teal : warm` (`Game1.Rendering.cs:~705`), drawn by
  `_stationRenderer.Draw` (`:709`). So the copy is most likely a **second entry
  in `_drawStations`** with a wrong side and orientation, not the depth
  encoding.
- Only with soft ON ⇒ suspect **P559's frame restructure**: the depth-first
  "gather the opaque scene" step (`_drawStationDepth`, `_drawShipDepth`,
  `_drawNativeShipDepth` (P566), `_drawRookDepth`; gather function near
  `Game1.Rendering.cs:~1625+`; `DrawSceneDepth :~1600-1622`) may append to, or
  reuse, the colour-pass list, or run the station loop twice per frame (once for
  the gather, once for colour) without clearing, or draw with stale
  `world`/side.
- `BuildingEffectRenderer` also uses the station mesh, but there is no build
  activity on 221. Rule it out explicitly.
- Also check ships and asteroids for the same duplication (a second faint
  ship or rock copy).

## Scope

1. Find the exact cause (file:line) with a real-window repro (local server,
   own side's home station in view, soft ON vs OFF).
2. Fix it so each station, ship and asteroid is drawn exactly once in the colour
   pass and once in the depth pass, with correct matrices.
3. A regression test: with soft ON, the per-frame colour-pass draw list
   contains exactly one entry per visible station (and ships/asteroids).

## ➕ Second bug, confirmed live: native ships render inside-out in game (P566)

**Domingo (client `99df9de6`, P566 native ships):** "the current ship looks
transparent". Claude built a **local test client with
`NativeShipRenderer`'s rasterizer set to `CullMode.None`** (instead of
`CullCounterClockwiseFace`, `Rendering/NativeShipRenderer.cs:~63`), and **the ship
is no longer transparent** (confirmed live). So in the real game the native mesh's
front faces are being **culled**: the winding P566 "corrected" is reversed again
by the in-game transform (`PoseConverter.ToXna`/`ToXnaRotation` world matrix,
the native `RY(π)` flip, `NativeScale`), while P566's contact sheets
(`roster-capture nativedump`) use a different transform and looked right.

**Fix (in this task):** make native ships cull the correct faces in the **real
game transform** (fix the winding/flip so `CullCounterClockwise` keeps the
outside), or justify `CullNone` like the Meshy path. Check the matrix
determinant (a mirror flips winding). Apply to the hangar preview path too.
Also verify **nose direction and level flight** (the P566 contact sheet's side view
looked pitched ~45°). Proof: real-window screenshots of the own ship (chase view),
other ships and the hangar preview, solid from all sides, with culling ON.

**Also (Domingo, same session): "some of the ships don't show at all spinning on
the background"**, i.e. the hangar/loadout **preview shows nothing for some hulls**
with P566 native ships (the preview uses the native renderer, P566 scope 3). Check
every player hull in the hangar preview path (culling, scale/framing, missing
texture/model, LOD), list which ones were blank and why, and fix them. Proof: a
real-window screenshot sweep of the hangar preview across all player hulls of
both 221 civs (or a contact sheet rendered through the same preview code path).

## Proof

- Failing-first test (draw-list counts with soft ON).
- **Real-window screenshots** (`ALLEGIANCE_AUTOSHOT`, 2560×1440) of the home
  station with soft ON before and after (no orange copy), and with soft OFF.
- Report `docs/reports/P570_DOUBLE_STATION.md`.

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
