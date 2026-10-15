# P563 — minefield visual parity: mine sprites are drawn ~10× native size and too crowded; match native's decal scale, and thin the field as its strength drops

## Why this task exists

**Domingo, live on 221 (2026-10-06):** "i had the chance to see the minefields
too, it does the job but each disk looks a bit big and too close to each other?
they need a pass based on the original client".

**Native** (`~/Development/Allegiance/src/effect/efgeo.cpp:15-87`, `MineFieldGeoImpl`;
hooked up in `WinTrek/trekigc.cpp:1931-1962` `LoadMine`/`SetMineStrength`):
- count `1 + int(strength·radius²/100)` decals at `Vector::RandomPosition(radius)`
  (uniform in the field sphere);
- **each decal drawn with `DrawDecal(..., scale = 1, angle 0, BlendModeSourceAlpha)`**;
  in `engine/context.cpp:2282-2320` the decal's size is
  `decal.m_scale = mat.GetScale() * scale` (the mine object's local-to-eye scale
  × 1). Read the decal rendering path to pin the exact world half-size, and
  find what scale the mine's ThingSite/thing has (if any);
- brightness `bound(100 / eyeDistance, 0, 1)`, grey (no side tint);
- texture = the mine type's `textureName + "bmp"`;
- **`SetStrength(strength)` shrinks the visible count** as the field is
  depleted (`efgeo.cpp:~40-46`), driven by `SetMineStrength`
  (`trekigc.cpp:1959-1961`; trace the native caller in `Igc` mine code and the
  wire message that carries strength to clients).

**Port** (`src/Allegiance.ClientCore.Game/Rendering/MineRenderer.cs`,
`MineFieldGeometry.cs`, P291): the count and brightness are native, but
**`DecalHalfExtent = 10f`** ("display tuning … a small glowing mote against a
field extent of 75–250 units") makes every sprite **20 world units wide**,
about 10× native, so a radius-200 field (~400 sprites) looks like crowded
discs. Check also whether `strength` is held at 1.0 forever (no depletion
thinning).

## Scope

1. **Native sprite size:** derive the world half-extent from native's decal
   math (scale 1 × the mine thing's scale) and use it. Remove the 10 u display
   tuning. If the native size turns out barely visible at combat range,
   **don't enlarge it silently**: report with real-client screenshots and
   propose a documented option (e.g. a small HUD marker, as native's own
   indicators would), then let Domingo choose.
2. **Distribution:** confirm `RandomPosition(radius)` semantics (uniform in the
   volume vs on a shell) and match it.
3. **Depletion thinning:** the field's visible count follows native
   `SetStrength` as mines are consumed (port the strength the server already
   tracks, P452 attrition, to the client if it isn't sent; cite the native
   message).
4. Same for every mine type (dogfight `Mine Pack` mines, radius 75; layer-drone
   fields, up to radius 250 / 600 s).

## Explicitly out of scope

- Mine damage/attrition rules (P452), mine visibility rules (P453), minefield
  laying (P560).

## Proof

- Failing-first tests: decal half-extent equals the native-derived value;
  count vs strength (depletion); position distribution.
- **Contact sheet:** a radius-75 and a radius-200 field at 100/300/800 u, old
  vs new, with an Adv Fighter for scale.
- **Real client screenshot** (window) of a live field, plus a field before and
  after being partly depleted.
- Report `docs/reports/P563_MINEFIELD_VISUALS.md`.

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
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

- **Visual proof rule:** screenshots must come from the **real client window** (backbuffer,
  e.g. `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture`/offscreen renders alone don't
  count; that gap hid the P547 (invisible geometry) and P562 (near-invisible aleph) regressions.

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
