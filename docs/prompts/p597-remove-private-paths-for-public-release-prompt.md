# P597 — public-release prep: remove hardcoded `/home/domingo` paths, untrack private server configs, make tests run (or skip honestly) on a clean clone

## Why this task exists

Domingo plans to make this repo public. A read-only readiness audit
(2026-10-10) found **926 lines in 464 tracked files** containing
`/home/domingo`. Most are in historical `docs/reports` and `docs/prompts`
(out of scope here). The ones that matter break or mislead a fresh clone:

- **Product code silently falls back to the owner's machine:**
  - `src/Allegiance.ClientCore.Game/Assets/AssetSources.cs:19`
  - `src/Allegiance.Server/ServerConfig.cs:313`
  - `src/Allegiance.ClientCore.Explorer/Program.cs:48`

  Each is `ALLEGIANCE_ARTWORK_PATH ?? "/home/domingo/Development/Artwork"`.
  The same path appears in doc comments in
  `src/Allegiance.ClientCore.Game/Audio/CountdownSound.cs:26`,
  `SoundManager.cs:24` and `Flight/MouseFlightController.cs:25`.
- **Tests:** about 118 test files repeat that fallback: 117 for Artwork, 3 for
  `/home/domingo/Development/Allegiance` (the native source). For example
  `tests/Allegiance.ClientCore.MonoGame.Tests/AsteroidSpinTests.cs:22` and
  `AssetSourceTests.cs:128`. On a machine without the corpus they throw
  `DirectoryNotFoundException`. That's why `.github/workflows/ci.yml` runs
  `dotnet test` with `continue-on-error` (its header comment explains it).
  CI is therefore effectively build-only.
- **Tracked private server configs:**
  - `src/Allegiance.Server/server.json`
  - `server.p557.json`, `server.p558.json`, `server.p562.json` (task-specific,
    some with `adminCheats` enabled and an `artPath` on the owner's machine)

  Examples already exist: `server.example.json`, `.linux.json`, `.windows.json`.
- **Scripts and tools:** `scripts/build-android-head.sh`, `live-proof.sh`,
  `p539-before-baseline.sh`, several `repro_p591*.sh`; `tools/space-objects`
  (6 files), `tools/vizfight` (3), `tools/allegiance-mcp` (2),
  `tools/weapon-status-proof`. Find the full list with
  `git grep -lI /home/domingo -- src tests scripts tools docker`.
- **Nothing sets the env vars on the owner's machine either.** `~/.profile`
  and `scripts/run-tests.sh` don't export `ALLEGIANCE_ARTWORK_PATH` or
  `ALLEGIANCE_NATIVE_SRC_PATH`. Every run today works only because of the
  fallback. So the fix must keep the owner's workflow (and delegates' test
  runs) working without the fallback.

## Scope

1. **One resolver for external paths** (artwork corpus, native source
   checkout), used by product code, tools and tests.
   - **Order:**
     1. The env var (`ALLEGIANCE_ARTWORK_PATH`, `ALLEGIANCE_NATIVE_SRC_PATH`).
     2. A **gitignored local config** at the repo root, e.g. `local.env` or
        `allegiance.local.json` (your choice; document it), with a committed
        `*.example` template.
     3. Otherwise **not found**. Product code then fails with a clear,
        actionable message: what's missing, the env var, the FreeAllegiance
        Artwork link (https://github.com/FreeAllegiance/Artwork), and how to
        set the path. No guessing of user home paths.
   - Remove every `/home/domingo` from `src/` (code and comments; comments
     can say `$ALLEGIANCE_ARTWORK_PATH`).
2. **Tests:**
   - Replace the ~118 copies of the fallback with the shared resolver.
   - Tests that need the corpus or native source must **skip with a clear
     reason** when it's missing (xUnit 2.9.3 here: use a skippable-fact
     approach or equivalent, not silent passes). They carry a trait, e.g.
     `[Trait("Requires", "Artwork")]` / `"NativeSource"`, so CI can filter them.
   - **CI honesty:** `ci.yml` runs the tests that don't need external data
     **as a gating step** (no `continue-on-error`). Update its header comment.
     If some tests are flaky on CI, use the flake ledger
     (`docs/TEST_FLAKES.md`) with a documented filter, not a blanket ignore.
3. **Owner's machine keeps working:** create the gitignored local config on
   this workstation pointing at `/home/domingo/Development/Artwork` and
   `/home/domingo/Development/Allegiance`. Make `scripts/run-tests.sh` (and
   any script that runs the client or server) load it. Show that the full
   suite still finds the corpus, with the same pass/skip counts as before,
   **not** mass-skipping.
4. **Server configs:** untrack `src/Allegiance.Server/server.json` and the
   `server.p5xx.json` files (`git rm --cached`; keep local copies if you
   need them), add them to `.gitignore`, and make the server's config loading
   documented (falls back to an example or errors clearly). Check
   `docker/` and the deploy path (`docker/compose.server.json`, the 221
   compose setup) still work. **Do not touch the 221 server or
   `~/allegiance-deploy`**; just make sure the committed docker files don't
   depend on the untracked ones.
5. **Scripts and tools:** replace `/home/domingo/...` with `$HOME`-free
   repo-relative paths, the resolver/env vars, or required arguments.
6. **`.gitignore`:** add `.verify*/`, `graphify-out/`, `roster_dump.json`,
   the local config, and the untracked server configs. Untrack the stray
   tracked `.verify_p558/` and the `.log`/`.out` files under
   `docs/reports/P575_assets` and `P578_assets`.
7. **A guard test:** fails if any tracked file under `src/`, `tests/`,
   `scripts/`, `tools/`, `docker/` or `.github/` contains `/home/domingo` (or
   another absolute home path pattern), so this doesn't creep back.

## Out of scope

- `docs/reports/**` and `docs/prompts/**` (historical record; decided
  separately).
- The compiled shaders `*.ogl.mgfxo` that embed a build path (rebuilding
  needs the Wine toolchain); note it in the report.
- README rewrite, CONTRIBUTING, CODE_OF_CONDUCT, SECURITY (separate task).
- Admin-cheats hardening; the git history rewrite.

## Verification

- **Clean clone, no artwork:** `git clone` this repo to a temp dir, with
  `ALLEGIANCE_ARTWORK_PATH` and `ALLEGIANCE_NATIVE_SRC_PATH` unset and no
  local config. Whole-solution build 0 warnings/0 errors. `dotnet test` with
  the CI filter passes, and the artwork tests report **skipped (with
  reason)**, not failed. Report the counts.
- **This workstation with the local config:** full suite via
  `scripts/run-tests.sh`, 2 consecutive runs on the final code. Pass/skip
  counts are comparable to P596/P581b's runs (cite them); artwork tests
  **run**, not skip.
- The guard test (item 7) passes, and fails if a `/home/domingo` string is
  added to a file under `src/`.
- The client and server still start against the local config (a short live
  run on a local server in your own temporary worktree).

## Deliverables

Commit on `master` (not pushed); report
`docs/reports/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md` covering: the resolver,
the local-config format, the counts before/after (`git grep -c /home/domingo`
per top-level dir), the clean-clone results, CI changes, and what's left in
docs/.

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

## ⛔ Shared-environment rules (Domingo, 2026-10-10)

- **Never modify, build in, check out or run from `~/allegiance-play2` or
  `~/allegiance-deploy`.** Those are Domingo's live play client and the 221
  deploy source. For live proofs, create your **own temporary worktree**
  (`git worktree add /tmp/<task>-proof HEAD`), use it, and remove it when
  done.
- **Never connect to, deploy to, or restart the 221 server (`192.168.0.221`).**
  Use a local server on this workstation (as the P572 tooling does).
- Stop every local server, client and bot process you started (by exact PID)
  before ending your session.

## 🔎 Reverse engineering the native binaries (when a task needs compiled-code evidence)

The native C++ source (`~/Development/Allegiance/src`) stays the primary
reference. When you need what the **compiler actually produced** (resolved
constants, inlined macros, vtable slots, struct offsets), symbol-aware
decompiles of our FZDebug builds are available. Use the CLI, not MCP:

```bash
G=~/Development/allegiance-tools/ghidra/ghidra-pdb.sh
B=~/Development/allegiance-binaries
$G $B/server/AllSrv.exe list 'CFSMission::'                     # names + addresses
$G $B/server/AllSrv.exe decomp 'CFSMission::GetCountSides$' 1   # decompiled C
# also: $B/client/Allegiance.exe, $B/interop/AllegianceInterop.dll
```
Projects are pre-analysed (calls take seconds); never re-import or delete
`$B/ghidra-projects`. Cite the function name + address in your report.
