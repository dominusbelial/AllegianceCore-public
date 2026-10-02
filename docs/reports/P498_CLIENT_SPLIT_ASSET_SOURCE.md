# P498 — Client split (shared game library + DesktopGL head) and asset-source abstraction

Date: 2026-10-02
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`)
Purpose: Step 1 of the free-Android-client plan. Split the 35k-line MonoGame
client into a **platform-neutral shared game library** + a **DesktopGL head**,
and route every Artwork file-open through an `IAssetSource` so platform heads
(Android, P499) supply their own backing store. **Step 1 is purely structural —
the desktop client behaves identically afterwards** (verified below).

---

## 1. What changed (project split)

Before: `src/Allegiance.ClientCore.MonoGame/` was one `Exe` on
`MonoGame.Framework.DesktopGL` 3.8.5.1 mixing all game code (~34,900 lines over
~150 files) with the desktop entry point and the MGCB content build.

After:

| Project | Role | Contents |
|---|---|---|
| `src/Allegiance.ClientCore.Game/` (NEW class library, assembly `Allegiance.ClientCore.Game`) | Shared game library | `Game1` + all partials, every renderer/layout, `Audio/`, `Flight/`, `QuickChat/`, `Capture/`, `CommandOrderResolver`, `ServerListing`, `Properties/AssemblyInfo` — i.e. everything except the desktop entry point |
| `src/Allegiance.ClientCore.MonoGame/` (unchanged path/assembly/exe name `Allegiance.ClientCore.MonoGame`) | DesktopGL head | `Program.cs` (entry point + all CLI flags), `Content/` (the MGCB content build + quickchat/sounddef) |

Mechanics:
- All game files were `git mv`'d into the new library preserving the
  `Allegiance.ClientCore.MonoGame` namespace, so the relocated code, tests and
  tool references compile unchanged. The library's **assembly** name is
  `Allegiance.ClientCore.Game`; the namespace is kept (documented divergence,
  renamed wholesale when the Android head lands in P499).
- The head keeps its `OutputType=Exe`, assembly name and exe name so every
  existing launch command, doc and script still works.
- `AllegianceCore.slnx` gains the new library project; the test project now
  references the library directly and adds its own `MonoGame.Framework.DesktopGL`
  (see below). Committed `tools/*` continue to reference the head and receive the
  library + MonoGame transitively, so they build unchanged.

### MonoGame shared-library pattern (cited)

The split follows MonoGame 3.8's documented shared-library pattern: a class
library holds the game and each platform head supplies the real framework
package. Two supporting references:

- MonoGame community guide, "Going cross-platform with MonoGame"
  (darkgenesis.zenithmoon.com/going-cross-platform-with-monogame.html) — the
  `dotnet new mglib` class-library + head structure.
- MonoGame forum, "Shared project for cross-platform development"
  (reddit.com/r/monogame/comments/u8cntx/) — `PrivateAssets="All"` is the
  mechanism that lets a library *build against the MonoGame API without
  shipping the framework itself*; each head brings its own
  `MonoGame.Framework.*`.

Applied here: the shared `Allegiance.ClientCore.Game` references
`MonoGame.Framework.DesktopGL` with `PrivateAssets="All"` (compiles against the
API, does not propagate the package). The DesktopGL head declares its own
`MonoGame.Framework.DesktopGL` + `MonoGame.Content.Builder.Task`; the test
project declares `MonoGame.Framework.DesktopGL` for the 8 tests that use MonoGame
math types. A future Android head (P499) would reference the same library and
supply `MonoGame.Framework.Android`.

---

## 2. Asset-source abstraction

Added under `src/Allegiance.ClientCore.Game/Assets/`:

- `IAssetSource` — `Exists(relativePath)`, `OpenRead(relativePath)`,
  `Enumerate(directoryRelativePath)`, all POSIX `/`-separated relative paths.
- `DirectoryAssetSource` — the DesktopGL implementation over the on-disk Artwork
  tree, preserving today's exact behaviour (`ALLEGIANCE_ARTWORK_PATH` default +
  `/home/domingo/Development/Artwork` fallback), with path-escape defence.
- `AssetSources` — the single place the artwork root env var is read
  (`ResolveRoot()`) and the default desktop source (`Artwork`).
- `RecordingAssetSource` — decorator that records every file actually opened
  (bytes read) for the asset manifest.

Every one of the ~22 artwork file-IO sites now reads through `IAssetSource`
instead of `System.IO.File.*` / `Environment.GetEnvironmentVariable`. The sites
(refactored to an `IAssetSource` overload; the previous `string artworkDir`
forms remain as thin `DirectoryAssetSource` adapters so committed tools keep
building):

- `Rendering/ExplosionAssets.cs` — exp20/22/23/24/25bmp.mdl, fx18bmp.mdl
- `Rendering/RockTextureSource.cs` — bgrnd05/51/52/53/55bmp.mdl
- `Rendering/LockTextures.cs` — plockbmp.mdl, lockedbmp.mdl
- `Rendering/GaugeTextures.cs` — the six `*Gaugebmp.mdl`
- `Rendering/AmbientDustRenderer.cs` — debris1bmp.mdl
- `Rendering/MissileModelSource.cs` — mis05.mdl demo + `NativeMdlRadius` reads
- `Rendering/ProbeModelSource.cs` — utl02.mdl
- `Rendering/MineModelSource.cs` — fxminebmp.mdl
- `Rendering/ChaffModelSource.cs` — fxminebmp/cc_cmtexbmp.mdl
- `Rendering/ShopCatalog.cs` — PCore001.igc (now `IgcReader.Parse(byte[])`)
- `Game1.FlightInput.cs` — `<ModelName>.cvh` collision hulls (combat)

`Game1` holds `_assets` (from `LaunchOptions.AssetSource`, defaulting to the
desktop source) and threads it through every loader in `Game1.Lifecycle.cs` and
the flight-input cvh read. Heads inject their own `IAssetSource` via
`LaunchOptions.AssetSource`; Android (P499) will supply an APK-assets / pack
implementation.

### Tests

Added `tests/Allegiance.ClientCore.MonoGame.Tests/AssetSourceTests.cs` (5 tests):
`DirectoryAssetSource` round-trip + path-escape rejection, `RecordingAssetSource`
byte recording + manifest totals, and a regression proving `ShopCatalog` routes
through `IAssetSource` with the **same result as the legacy string-path form**
(the "desktop behaves identically" guarantee). All existing real-asset tests
(gauge/lock/rock/explosion frame counts) still pass against the routed pipeline.

---

## 3. Asset manifest (representative session)

Tool/mode: the client's `--asset-manifest <path>` flag wraps the artwork source
in a `RecordingAssetSource` and writes a JSON manifest on exit. A representative
session was recorded two ways:

**(a) Live capture** — the real client ran in headless capture mode
(`--capture`, the P306 offscreen-render path) against a live `vizfight`-hosted
server (2 bots, 10 UPS), connecting, joining side 0, readying, flying and
rendering — covering **boot, shop, flight, docking-screen wiring and live
rendering**. It opened **27 Artwork files, 76,301,206 bytes (~72.7 MiB)**.

**(b) Combat cvh** — `MuzzleOffsetsFor` reads `<ModelName>.cvh` per hull only
on weapon fire; no weapon fired in the passive capture. Recorded deterministically
through the same `RecordingAssetSource` over the shop catalog's 179 distinct hull
models: **179 `.cvh` files, all present, 876,816 bytes (~0.86 MiB)**.

**Combined committed manifest** (`docs/reports/P498_ARTWORK_MANIFEST.json`):
206 files, **77,178,022 bytes (~73.6 MiB)** — versus the full Artwork tree of
6,526 files / 2,287,973,952 bytes (2.288 GB). That is **3.37%** of the tree,
far below the 2.2 GB figure. This is the Android-packaging input for P499.

The big contributors are the five 2048×2048 rock surfaces (`bgrnd*bmp.mdl`,
~42 MB) and the `exp22bmp.mdl` film strip (~15 MB) — legitimate, irreducible
runtime assets.

---

## 4. Verification (measured)

- **Whole-solution build** (`/home/domingo/.dotnet/dotnet build`): 0 warnings,
  0 errors.
- **Full suite** via `scripts/run-tests.sh` (P471 sequential runner): 3
  consecutive runs (5228 tests total). The P498-changed area
  (`Allegiance.ClientCore.MonoGame.Tests`, 1110 tests) passed **1110/1110 in
  all three runs**. The only failures across the three runs were pre-existing
  live-wire timing flakes in the **untouched** `Allegiance.Core` (3699 tests)
  and `Allegiance.ClientCore` (407 tests) assemblies — `git diff --name-only`
  confirms P498 changes touch none of those assemblies. Per-run MonoGame counts:
  1110✓ / 1110✓ / 1110✓. All flakes are logged in `docs/TEST_FLAKES.md` under
  "P498 session observations" (new entries added, previously-known ones cited);
  three of them recurred from the documented P482/P487/P495 classes.
- **Live-capture behaviour check**: the post-split client boots and loads every
  artwork asset through the new source with identical log output to the
  pre-split client, connects to the real server, flies and renders frames
  (PNGs produced), then exits cleanly — confirming "behaves identically".

---

## 5. Notes / divergences

- `weapon-status-proof` (a committed but NON-solution proof tool) has a
  **pre-existing, unrelated** compile error (`HudFrame` constructor argument
  mismatch) that predates P498 — it is not touched by this change and is not
  built by the solution. Flagged here for a separate ticket; left untouched to
  respect repo-boundary scope.
- The shared library's namespace is intentionally still
  `Allegiance.ClientCore.MonoGame`; the Android head (P499) can rename when it
  lands.
- The quickchat/sounddef Content `.mdl` files are loaded separately from
  `AppContext.BaseDirectory/Content` (the MGCB Content build), not the Artwork
  tree, so they are not part of this Artwork manifest.

## 6. Deliverable status

- Committed on `master` (not pushed): `git log -1` / `git branch --show-current`
  confirmed at commit time.
- Report: this file. Manifest: `docs/reports/P498_ARTWORK_MANIFEST.json`.
