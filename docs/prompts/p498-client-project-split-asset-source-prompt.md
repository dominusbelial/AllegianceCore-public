# P498 — Android prep, step 1: split the MonoGame client into a shared game library + DesktopGL head, and add an asset-source abstraction

## Why this task exists

Domingo plans a **free Android client with cross-play** against the same
server. The decision (wiki `log.md` 2026-10-02): a **MonoGame Android head
sharing the existing code**. `Allegiance.Core` and `Allegiance.ClientCore`
are already plain `net10.0` libraries and are reused unchanged. The
MonoGame client (`src/Allegiance.ClientCore.MonoGame/Allegiance.ClientCore.MonoGame.csproj`,
an `Exe` on `MonoGame.Framework.DesktopGL` 3.8.5.1, about 35k lines, with
**no custom `.fx` effects** in `Content/Content.mgcb`) mixes portable game
code with desktop-only assumptions:

- **File-system asset loading.** It reads the native `Artwork` tree
  (**2.2 GB**) from disk at runtime, via `ALLEGIANCE_ARTWORK_PATH` and
  direct file IO. There are about 22 sites, concentrated in
  `Rendering/ExplosionAssets.cs` (5), `RockTextureSource.cs` (3),
  `LockTextures.cs` (3), `GaugeTextures.cs` (3), `AmbientDustRenderer.cs` (3),
  `ShopCatalog.cs`, `MissileModelSource.cs`, `MineModelSource.cs`,
  `ChaffModelSource.cs` and `Game1.Lifecycle.cs`. The other assets are the
  374 MB `Content/` folder, built by MGCB.
- **The desktop entry point and window**, and keyboard/mouse/joystick input
  (`Game1.FlightInput.cs`).

Step 1 is purely structural. **The desktop client must behave identically
afterwards.**

## Scope

1. **Project split.**
   - `Allegiance.ClientCore.Game` (name can be adjusted) is a class library
     holding `Game1` and all renderers, layouts and input translation.
     Reference MonoGame in a way that's platform-neutral: the
     `MonoGame.Framework.*` package comes from the head, or the library
     builds against the portable reference. Follow MonoGame 3.8's documented
     shared-library pattern and cite it in the report.
   - `Allegiance.ClientCore.MonoGame` becomes the **DesktopGL head**: the
     entry point, CLI flags (`--debugMode`, `--noPrediction`,
     `--noDeadReckon` and the rest) and the MGCB content build. Keep its
     assembly and executable name so existing launch commands, docs and
     scripts still work.
   - Move tests as needed, with namespaces kept or updated consistently.
2. **Asset-source abstraction.** Add one interface, for example
   `IAssetSource` with `Exists`, `OpenRead` and `Enumerate`, that every one
   of the file-IO sites above uses instead of direct `File.*` or
   environment lookups.
   - The desktop implementation keeps today's exact behaviour: the
     `ALLEGIANCE_ARTWORK_PATH` default and the fallbacks.
   - Platform heads supply their own implementation; Android will use APK
     assets or a downloaded pack in step 2.
3. **Asset manifest.** Add a tool or test mode that records **which Artwork
   files the client actually opens** during a representative session:
   boot, shop, flight, combat, the investment screen, docking. Report the
   file count and total size; it should be far below 2.2 GB. Commit the
   manifest, since it's the input for Android packaging in step 2.

## Explicitly out of scope

- Any Android project, toolchain or touch input (**P499**, next).
- Gameplay changes.
- Changing the server or the wire protocol.

## Verification

- Every fix has a test that fails on unfixed code first, where applicable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P498_CLIENT_SPLIT_ASSET_SOURCE.md`.
