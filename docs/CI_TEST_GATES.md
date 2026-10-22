# CI test gates (and how to reproduce them locally)

Both GitHub workflows run on a **clean ubuntu runner**: no artwork corpus, no
native C++ source, no Meshy/HD/sky packs, no display and **no GPU**. Every test
that needs one of those must SKIP there, never fail.

| Workflow | Step | Command |
|---|---|---|
| `ci.yml` (push/PR to master) | Test Fast tier (non-corpus gate) | `tools/run-tests.sh --tier fast --no-build` with `TEST_FILTER="Requires!=Artwork&Requires!=NativeSource&Requires!=Gpu&Requires!=MeshyPack&Lane!=LiveTiming"` |
| `release.yml` (player release) | Existing CI test gate | `dotnet test AllegianceCore.slnx --no-build --filter "Requires!=Artwork&Requires!=NativeSource&Lane!=LiveTiming"` — **all tiers** (Fast, Live, Perf), and it does *not* filter `Requires=Gpu` |

The release gate is the stricter one: Live/Perf tests that `ci.yml` never runs
are run there. Run 39702545411 (CI-1) failed on 30 such tests.

## Reproduce the release gate locally

```bash
tools/run-ci-gate.sh --clean-clone          # build + gate, clean-runner conditions
tools/run-ci-gate.sh --clean-clone --no-build
tools/run-ci-gate.sh --no-build             # same gate, with local.env (artwork, GPU)
```

`--clean-clone` makes this workstation look like the runner even with a
`local.env` present:

* every `ALLEGIANCE_*_PATH` points at a nonexistent directory (the environment
  variable wins over `local.env`, see `src/Allegiance.Core/ExternalPaths.cs`);
* `DISPLAY` / `WAYLAND_DISPLAY` are unset;
* `__EGL_VENDOR_LIBRARY_FILENAMES` points at a nonexistent file, so libglvnd
  finds no EGL driver and MonoGame's offscreen SDL context throws
  `NoSuitableGraphicsDeviceException` — exactly what the GPU-less runner does.

Expected result: `Failed: 0` in every test project (skips are fine).

## Conventions for tests that need external resources

* **Artwork corpus** (P597): `[Trait("Requires", "Artwork")]` + `[SkippableFact]`
  / `[SkippableTheory]`, and `Require.Artwork();` as the first line. Class-level
  static corpus loads must be lazy (`Lazy<StaticCore>`), otherwise the type
  initializer throws before `Require.Artwork()` can skip.
* **Real GL device** (CI-1): the proof tool runs in a child process with
  `SDL_VIDEODRIVER=offscreen`; mark the test `[Trait("Requires", "Gpu")]` +
  `[SkippableFact]` and call `Require.GraphicsDevice(output)` on the child's
  combined output **before** asserting. It skips only when MonoGame could not
  create a device at all; any other failure still fails. Set
  `ALLEGIANCE_REQUIRE_GPU=1` on a GPU machine to turn that skip into a failure.
* `Require.*()` throws `SkipException`; a plain `[Fact]`/`[Theory]` reports that
  as a **failure** — always use the `Skippable*` attributes.

## Regression guard

`tests/Allegiance.Core.Tests/CleanCloneSkipGuardTests.cs` (Fast tier) parses
every test file with Roslyn and fails when a test (directly or through helpers,
fields, properties or constructors of its own class)

1. calls `Require.*()` / `Skip.If*()` from a plain `[Fact]`/`[Theory]`;
2. reads the corpus through `TestArtRoot` without being skippable and reaching
   `Require.Artwork()`;
3. creates a real GL device (`SDL_VIDEODRIVER` child process, `new GraphicsDevice`)
   without being skippable and reaching `Require.GraphicsDevice(output)`.

Rules 2–3 ignore tests the gates filter out by trait. The scan cannot see
corpus use hidden inside product code (e.g. `ClientMissionCoreCache` behind a
battle replay), so `tools/run-ci-gate.sh --clean-clone` remains the
authoritative check before a release tag.
