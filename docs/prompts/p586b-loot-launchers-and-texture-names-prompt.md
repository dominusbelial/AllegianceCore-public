# P586b — loot still shows gold gems live: launcher parts (missiles/mines/chaff/probes) aren't resolved, and treasure texture names miss the native `bmp` suffix

## Why this task exists

Domingo, live on 221 @ `5e9eabff` (P586 `6fb4ad3c` + P588), 15v15: "I still see
gems." Client log:
```
[loot] treasure obj=5717 code=Part payload=80: no resolvable native visual — falling back to the gold gem
[monogame] native treasure texture 'part.mdl' unavailable (not present) — rendering lit-solid
[loot] treasure obj=5802 code=Part payload=81: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6054 code=Part payload=80: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6055 code=Part payload=153: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6191 code=Part payload=153: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6271 code=Part payload=80: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6273 code=Part payload=153: no resolvable native visual — falling back to the gold gem
[monogame] native treasure texture 'fuel_icon.mdl' unavailable (not present) — rendering lit-solid
[loot] treasure obj=6754 code=Part payload=81: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6765 code=Part payload=13: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=7365 code=Part payload=150: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=7366 code=Part payload=13: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=7378 code=Part payload=150: no resolvable native visual — falling back to the gold gem
[monogame] native treasure texture 'ammo_icon.mdl' unavailable (not present) — rendering lit-solid
[loot] treasure obj=10340 code=Part payload=17: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=10341 code=Part payload=80: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=10659 code=Part payload=80: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=10683 code=Part payload=13: no resolvable native visual — falling back to the gold gem
```
1. **Unresolved part ids 80, 81, 153, 13, 150 → gem.**
   `Rendering/TreasureVisualResolver.cs:~76-95` only searches
   `catalog.Parts` (the 117 plain parts). Part **13 is the mine dispenser**
   (MCP log: "fired mine dispenser (part 13)"); these are **launcher part
   types** (missile/mine/chaff/probe), which live in `StaticCore.Launchers`
   (`Model/StaticCore.cs:45`) / `ShopCatalog.Launchers`
   (`ShopCatalog.cs:~35, 104-125`). Native resolves `m_buyable =
   GetPartType(treasureID)` for ANY part type, launchers included
   (`treasureIGC.cpp:~80-126`): use the launcher part's own `ModelName`/
   `IconName` (or its expendable's model if that's what native shows; check
   `DataLauncherTypeIGC` and native `GetModelName` for launcher parts).
   Also verify **why P586's resolver didn't use the full static core** (it
   should resolve from `StaticCore.PartTypes`/`Launchers` directly, not a shop
   view that filters buyables).
2. **Texture names without the `bmp` suffix:** "native treasure texture
   'part.mdl' / 'fuel_icon.mdl' unavailable". The artwork files are
   `fuel_iconbmp.mdl`, `ammo_iconbmp.mdl`, `fuelbmp.mdl`, `ammo3bmp.mdl`, …
   Native textures resolve as `<name>bmp` (the P566/P567 MDL paths already do
   this); fix the stem mapping, and check what model 'part' is (there's no
   `part.mdl` in `~/Development/Artwork`: find what native draws for those
   parts, e.g. the generic part model name in the core/igc).
3. **P586's live proof missed this** (its test loot was cash/power-ups): the
   proof must cover **every treasure kind the 15v15 showcase drops**.

## Scope

Resolve every treasure from the static core (parts incl. launchers +
developments), correct the model/texture stems, keep the gem only as a true
last resort (logged). Add a startup self-check: resolve every part type and
launcher in the loaded core and log any that would fall back (expect 0).

## Proof

- The self-check: 0 unresolved part/launcher types in the 221 core.
- Live 15v15 on the 221 config (MCP showcase) in the real window: screenshots of
  dropped missile packs, mine dispensers, chaff, probes, fuel and ammo packs,
  guns, shields (each its native model, textured), **zero** "[loot] … gold
  gem" lines in the client log for the whole run.
- Tests for launcher resolution and the stem mapping.
- Report `docs/reports/P586b_LOOT_LAUNCHERS_AND_TEXTURES.md`.

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
