# P584 — 2D HUD art drawn upside down: `MdlTextureDecoder` flips every image vertically (right for 3D textures, wrong for flat HUD bitmaps); fix it for all 2D consumers and audit them

## Why this task exists

Domingo (2026-10-08, live on 221 @ `3db766aa`): "the eject banner looks
inverted somehow? like seen from a mirror". Claude decoded the native art both
ways (`docs/prompts/p584-deathbanner-orientation.png`,
`p584-ejectpod-orientation.png`; top = file row order, bottom = the decoder's
flip): `deathbannerbmp.mdl` (136×25, "YOU HAVE DIED / ESC "Q" TO QUIT") and
`conejectpoddisplaybmp.mdl` (172×44, "YOU ARE IN AN EJECT POD / "B","P" TO
AUTO PILOT TO BASE" + the O₂ gauge) are stored **top row first**.
`MdlTextureDecoder.Decode` (`Rendering/MdlTextureDecoder.cs:~91-92`) always
writes `dstRow = sizeY − 1 − srcRow` ("vertical flip (reference
convention)"), which suits the 3D texture paths but renders the P578
feedback art (`Rendering/FeedbackTextures.cs`) upside down.

A LOCAL, uncommitted patch in Domingo's play client (`~/allegiance-play2`)
un-flips rows inside `FeedbackTextures.TryDecode` only.

## Scope

1. Give the decoder an explicit orientation choice (e.g. `Decode(bytes,
   flipVertical: true)` keeping today's behaviour for 3D, or a separate
   `DecodeForSprite`), and use the **unflipped** (file-order) variant for every
   2D/HUD consumer. Check native: how `ImportImage(...)` HUD surfaces are
   blitted vs how textures are bound to geometry, to justify which consumers
   are 2D.
2. **Audit every `MdlTextureDecoder` consumer** (`LockTextures`,
   `NativeEffectTextures` decals/billboards, `ProjectileModelRenderer`,
   `ChaffModelSource`, `RockTextureSource`, `NativeStationRenderer`,
   `NativeShipRenderer`, `FeedbackTextures`, any HUD/pane images): for each,
   2D or 3D, current orientation correct or not, with evidence (a decoded
   PNG next to a real-window crop). Fix any other upside-down 2D art (lock
   icons, HUD gauges, cursor/target brackets, flare/decal sprites that have a
   readable up direction).
3. Remove the need for the local patch (it'll be dropped when the play client
   moves to your commit).

## Proof

- Real-window screenshots (P572 tooling) of the death banner (die as a
  ghost) and the eject-pod panel (eject: use MCP bots, or the P578 proof path)
  reading correctly, and of every other fixed 2D asset.
- Tests: decode a known asymmetric HUD asset and assert pixel (0,0) is the
  file's first row in the sprite variant, and still flipped in the 3D variant.
- Report `docs/reports/P584_HUD_2D_ART_ORIENTATION.md`.

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
