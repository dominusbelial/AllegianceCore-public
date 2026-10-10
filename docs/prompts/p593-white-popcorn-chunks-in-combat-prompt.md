# P593 — "white popcorn" in combat: streams of large, UNTEXTURED white faceted 3D chunks appear when ships shoot; identify the object and make it native (texture + size + count)

## Why this task exists

Domingo (2026-10-09, 15v15+ on 221 @ `b0f02182`, video
`~/Videos/2026-10-09 17-40-17.mp4` around **5:50–6:40**; crop
`docs/prompts/p593-white-popcorn-chunks-crop.png`): "some white popcorn
looking things that come out while we shoot onto the enemy … maybe they are
just oversized?" The crop shows **dozens of white faceted 3D chunks** in **two
long straight lines**, plain lit white (no texture), each large on screen.

Claude ruled out: ambient dust (`DebrisGeo`: tiny decals), damage-debris
particles (`DamageDebrisParticles`: billboards, size 1.5–2.5), chaff
(`ChaffModelSource`: a camera-facing decal of `fxminebmp`/`cc_cmtexbmp`),
loot (P586b models: the white canisters with radiation symbols are pickups,
those are fine), explosions (approved).
**Prime suspect:** `Rendering/ProjectileModelRenderer.cs` (P544), which draws the
3D-model projectile rounds (EW Lsr Can 1/2, EW Mini-LC 1/2, **PE MassDriver**)
"at its authored size … no uniform rescale", streams of solid slugs. If the
texture isn't applied (lit-solid fallback, cf. P586b's `bmp`-suffix bug) and/or
the size is wrong, you get exactly this.

## Scope

1. **Identify** the object for certain: reproduce a 15v15 on the 221 config
   (local server + MCP) in the real window; log, for every 3D-model draw in
   view, the renderer + type id + model + texture (with a one-shot debug
   overlay or log), and match the white chunks. Report what they are.
2. **Fix to native:** texture (resolve `<name>bmp` as native/P586b; no lit-solid
   white), **size** (native: projectile model `SetRadius(radius)`
   `projectileIGC.cpp:50-56`; check the model's own extent vs radius; P544
   claims no rescale, so verify against native rendering of that projectile),
   brightness/lighting, **count/lifetime** (the stream density vs native fire
   rate). If they're not projectiles, apply the same to whatever they are.
3. Check every other 3D-model projectile/missile/probe for the same
   texture/size problem (a contact sheet of each in the real window at
   100/400 u, next to the native expectation).

## Proof

Before/after real-window crops of the same weapon firing (the white chunks
gone/correct), the per-type contact sheet, a log of resolved textures (0
lit-solid fallbacks), tests for the texture-stem and size rules.
Report `docs/reports/P593_WHITE_POPCORN_CHUNKS.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 2 consecutive runs **on the final code** (reduced from 3 per Domingo, 2026-10-09: the flakes are known/ledgered). Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 2× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 2× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
