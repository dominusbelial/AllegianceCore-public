# P577b — HD sky corrections (Domingo's review of P577's picks): keep the ORIGINAL native planets in the HD tier, and replace the two weak HD backgrounds (globe1 = M51 galaxy, globe3 = SN 1987A)

## Why this task exists

P577 (HD sky tier; read its report `docs/reports/P577_*.md` and the manifest
`docs/reports/P577_assets/sky-hd.json`) mapped each native environment and
planet to telescope imagery. Domingo reviewed the list (2026-10-08):

1. **Planets: keep the originals.** P577 replaced the native alien planets
   (`nebplntNN`) with real Solar System bodies (Earth "Blue Marble", Jupiter,
   Mars, Saturn, Venus, the Moon, Titan, Voyager disks), which are too
   recognisable for Allegiance sectors. In the HD tier, **use the native
   planet textures** (the P576 path: `nebplntNNbmp.mdl` / `nebplntNN.png`).
   If cheap and clean, a quality upscale of the native planet art (no new
   content) is fine; otherwise native as-is. Remove the NASA planet assets and
   their manifest/credits entries.
2. **Swap the two weak backgrounds:**
   - **globe1** (native: bright yellow/green/cyan/orange, multi-lobed) is mapped to the
     **Whirlpool Galaxy M51** (heic0506a): a face-on galaxy, wrong kind of
     object for a wrap-around backdrop.
   - **globe3** (native: red/orange/pink emission blob; used by Alderan, Cirrus,
     Terra, Juniper on the 221 map) is mapped to **Supernova 1987A**
     (heic1704a): a tiny object, mostly black, a weak fit.
   Pick replacements that are **nebulae filling the frame** (emission or
   reflection: rich, wide-field, no single bright galaxy/point object),
   palette- and layout-matched to the native environment (compare against
   P576's `contact_environments.png`), licensed CC BY 4.0 (ESA/Hubble,
   ESA/Webb) or NASA public domain.
3. **Verify every pick's ID.** For all 10 environments (not just the two),
   open each `source_url`, confirm the release ID shows the stated subject,
   and record the exact image title and credit line from the page. Fix any
   mismatch.

## Proof

- Native | old HD | new HD side-by-side for globe1 and globe3; the real
  window in Alderan (globe3) and one globe1 sector with `--sky hd`; native
  planets visible in HD mode.
- Updated `sky-hd.json`, credits screen and `licenses/`; the manifest
  validation test passes.
- Report `docs/reports/P577b_HD_SKY_CORRECTIONS.md`.

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
