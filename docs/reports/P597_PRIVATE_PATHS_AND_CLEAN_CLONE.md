# P597 — public-release prep: private paths and clean-clone readiness

Date: 2026-10-10 · Branch: `master` (commit pending suite numbers) · Not pushed.

## Goal

Make the repo publishable: remove hardcoded `/home/domingo` paths from product
code, tools, scripts and tests; stop tracking private server configs; make the
tests skip (with a reason) rather than fail on a machine with no artwork corpus;
and give CI an honest non-corpus gate.

## One resolver for external paths

New file `src/Allegiance.Core/ExternalPaths.cs` is the single resolver for the
two external corpora — the artwork corpus (`.igc`/`.cvh`/`.mdl`…) and the native
C++ source checkout. Resolution order:

1. The environment variable (`ALLEGIANCE_ARTWORK_PATH` / `ALLEGIANCE_NATIVE_SRC_PATH`).
2. The gitignored `local.env` at the repo root (keys are the same names).
3. Otherwise **not found** — no guessing of user home paths.

Product code calls `ExternalPaths.Require(kind)`, which throws a clear,
actionable `ExternalResourceUnavailableException` when the resource is missing
(the message names the missing resource, the env var, the repo-root `local.env`,
the FreeAllegiance Artwork URL — https://github.com/FreeAllegiance/Artwork — and
the doc). `ExternalPaths.IsAvailable(kind)` lets tests skip gracefully.

Used by product code, tools and tests:

- `src/Allegiance.ClientCore.Game/Assets/AssetSources.cs` — `ResolveRoot()`
  now calls `ExternalPaths.Require(Artwork)`.
- `src/Allegiance.Server/ServerConfig.cs` — `ArtPath` initializes from
  `ExternalPaths.Resolve(Artwork)`; `Server.Lifecycle.ValidatePaths` surfaces the
  actionable message.
- `src/Allegiance.ClientCore.Explorer/Program.cs` — `ArtRoot` uses `Require`.
- Doc-comment paths replaced with `$ALLEGIANCE_ARTWORK_PATH` /
  `$ALLEGIANCE_NATIVE_SRC_PATH` (`CountdownSound.cs`, `SoundManager.cs`,
  `MouseFlightController.cs`).

## Local config format

The repo-root `local.env` (gitignored; template `local.env.example` committed)
holds per-machine overrides using the env-var names:

```
ALLEGIANCE_ARTWORK_PATH=/home/domingo/Development/Artwork
ALLEGIANCE_NATIVE_SRC_PATH=/home/domingo/Development/Allegiance
```

`scripts/run-tests.sh` sources `local.env` (if present) so the owner's full
suite keeps finding the corpus without any hardcoded fallback. `local.env.example`
documents the format and the resolution order.

## Tests: skip, don't fail

Every corpus-backed test carries a `[Trait("Requires","Artwork")]` (or
`"NativeSource"`) trait and is converted to `[SkippableFact]` /
`[SkippableTheory]` (Xunit.SkippableFact 1.5.85) with `Require.Artwork();` /
`Require.NativeSource();` as its first statement — via the shared helper
`tests/_shared/TestRequire.cs` (namespace `Allegiance.TestInfra`). When the
corpus is absent (clean clone) the test is reported **Skipped with a reason**,
not Failed. On the owner machine (corpus present) the guard is a no-op and the
test runs and passes.

Details:

- Pure-logic tests in corpus files were left as plain `[Fact]` (per-method
  tagging), so they still run on CI. Conservative bias: when a method's
  corpus-dependence was unclear it was tagged, so the clean-clone guarantee
  ("no corpus test fails") holds; a small number of pure methods in mixed
  files may therefore also be filtered from the CI gate (documented tradeoff,
  no owner-machine impact).
- `Corpus.cs` / `CvhCorpus.cs` no longer throw when the corpus is absent
  (`Discover` returns `[]`; new `Available` flag). `Corpus.Theory()` and the new
  `CvhCorpus.Paths()` yield a single sentinel row when unavailable so
  `[SkippableTheory]` methods skip visibly instead of failing with a
  parameter-mismatch from an empty dataset.
- The four corpus theory classes (`ContainerTests`, `DataCluster/Station/Warp
  IGCRoundTripTests`) were converted with the sentinel + `Require.Artwork()`.

## Server configs untracked

`git rm --cached` (kept on disk, added to `.gitignore`):
`src/Allegiance.Server/server.json`, `server.p557.json`, `server.p558.json`,
`server.p562.json`. Committed templates remain (`server.example.json`,
`server.example.linux.json`, `server.example.windows.json`). The config loader
now errors clearly when a config file is missing, suggesting the matching
`.example` file. The committed docker files (`docker/compose.server.json`,
`docker-compose.yml`) use the container path `/artwork` and never depended on the
untracked configs; `docker/compose.override.debugserver.yml` now binds artwork
via `$ALLEGIANCE_ARTWORK_PATH` (falls back to the placeholder) instead of a
hardcoded host path.

## Scripts and tools

All `/home/domingo` references replaced with `$HOME`-free forms:

- `scripts/run-tests.sh`: portable `DOTNET` (`${DOTNET:-$(command -v dotnet)}`),
  sources `local.env`.
- Repro/proof scripts: `REPO` computed from the script location, `ART`
  from `$ALLEGIANCE_ARTWORK_PATH` (falling back to the repo's `Artwork` sibling),
  `DOTNET` portable; heredoc `artPath` and `ALLEGIANCE_ARTWORK_PATH` use `$ART`.
- Tools (`*.cs`): the artwork/native fallbacks now call
  `ExternalPaths.Resolve(...)`; Content/`.verify` paths are repo-relative;
  `glow-proof`/`strobe-proof` `const` roots became `static readonly`.
- `tools/space-objects/*.py` and `tools/verify_*.py`: artwork/native roots come
  from the env vars; `docs/reports/...` outputs are repo-relative.
- `tools/vizfight/run_*.sh`: `REPO`/`DOTNET` portable.

> Note: a handful of proof tools (`minimap-proof`, `weapon-status-proof`, and
> a few `*-proof`) reference MonoGame rendering APIs that evolved in later
> P-series and were already failing to compile **before** this task (they are
> not in `AllegianceCore.slnx` and are not built by the solution/CI). P597 only
> cleaned their paths; fixing the stale API calls is a separate task. The guard
> test below passes on them because they are not built by the solution.

## `.gitignore`

Added: `local.env`, `src/Allegiance.Server/server.json`, `server.p5*.json`,
`docker-compose.override.yml`, `.verify*/`, `graphify-out/`, `roster_dump.json`.
Untracked the stray `.verify_p558/` and the `.log`/`.out` files under
`docs/reports/P575_assets` and `P578_assets`.

## Guard test (item 7)

`tests/Allegiance.Core.Tests/PrivatePathGuardTests.cs` — a plain `[Fact]`
(`git ls-files` on `src tests scripts tools docker .github`, skips binaries)
fails if any tracked file under those dirs contains `/home/domingo`. Verified
to fail when a `/home/domingo` string is added to a tracked `src/` file, and to
pass on the cleaned tree. Extend the pattern list there if another absolute
home path is ever observed.

## CI (item 3)

`.github/workflows/ci.yml` rewritten: `dotnet build` remains the build gate, and
`dotnet test` now runs with
`--filter "Requires!=Artwork&Requires!=NativeSource"` **without**
`continue-on-error` — the non-corpus tests are a real gate. The header comment
no longer justifies a masked failure. Flaky tests are handled via the existing
flake ledger `docs/TEST_FLAKES.md` (a documented filter), not a blanket ignore.

## Counts (before / after)

`git grep -c /home/domingo` per top-level dir, text files only (5 `.mgfxo`
binaries excluded and still embed a build path — rebuilding needs the Wine
toolchain, out of scope):

| dir      | before (lines) | after |
|----------|---------------|-------|
| src      | 24            | 0     |
| tests    | 121           | 0     |
| scripts  | 67            | 0     |
| tools    | 60            | 0     |
| docker   | 2             | 0     |
| .github  | 1             | 0     |
| **subtotal** | **275**   | **0** |
| docs     | 651 (hist.)   | 274 files still (out of scope) |

Task-wide total was 926 lines / 464 tracked files; the in-scope source-facing
directories are now clean. `docs/` retains its historical `/home/domingo`
references (decided separately) and is excluded from the guard.

## Clean clone (verification)

A fresh `git clone` of this branch to a temp dir (no env vars, no `local.env`):
- Whole-solution `dotnet build`: 0 warnings / 0 errors.
- `dotnet test AllegianceCore.slnx --no-build --filter "Requires!=Artwork&Requires!=NativeSource"`
  passes; running without the filter, every corpus-backed test reports
  **Skipped** (with reason), none Failed.
- This matches the in-repo corpus-absent simulation
  (`ALLEGIANCE_ARTWORK_PATH`/`ALLEGIANCE_NATIVE_SRC_PATH` pointed at a missing
  dir): ClientCore 0/429/150, MonoGame 0/1447/266, Core 0/700/1209
  (failed/passed/skipped).

## Owner machine (verification)

`scripts/run-tests.sh` on this workstation (loads `local.env`, corpus present)
— artwork tests **run**, not skip. Two consecutive runs on the final code:

| run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|-----------|------|
| 1 | 12/12 | 1713/1713 | 578/579 | 3869/3872 |
| 2 | 12/12 | 1713/1713 | 579/579 | 3866/3872 |

The only failures are the established live-wire-receipt **load-flakes**
(`GameOverPlayersWireTests`, `LiveChatSectorWireTests`, `LiveRelaunchTests`,
`SideEliminationLiveTests`, `LiveStationRescueTests`, and — newly observed and
ledgered this run — `P590PodRescueTests`), all documented in
`docs/TEST_FLAKES.md` (P597 observation section); none fail in isolation. No
test that the P597 path/skip change touches fails.

## What remains in docs/

274 tracked files under `docs/` still contain `/home/domingo` (historical
`docs/reports` / `docs/prompts` — a separate decision). 5 compiled `.mgfxo`
shaders embed a build path (Wine toolchain rebuild, out of scope). README /
CONTRIBUTING / CODE_OF_CONDUCT / SECURITY rewrites are separate tasks.
