# P749 — Player crash safety: never crash on a missing asset, always write a crash log, check bundles for every mode

Date: 2026-10-20 · Branch: `master` · Commit: `9534abe4`

## Summary

Domingo's first macOS run of the player bundle (2026-10-19) crashed twice on
unhandled `InvalidDataException`s (missing training core in `TrainingHost.Start`;
an artwork case-mismatch in `ManifestAssetSource.OpenRead` via
`WarpRenderer.LoadTextureFile`) and wrote **no log** because Finder discards
stdout/stderr. This task hardens the player so:

1. every crash leaves a diagnostic file, and every run keeps a rolling log;
2. player modes never die on a missing/unreadable asset or core — optional art
   falls back, and a mode that cannot start returns to the menu with a message;
3. the packaged-bundle check exercises every mode's asset closure at build time.

Scope item 2's two *specific* crashes were already fixed by Claude on the branch
this task's base merged in (missing training core → bundle generator; case
mismatch → case-insensitive canonical lookup in `ManifestAssetSource`). What
remains and is delivered here is the systemic hardening around those.

## Changes

### 1. Crash log + rolling client.log (`src/Allegiance.ClientCore.Game/Diagnostics/CrashLog.cs`)

- `CrashLog.Install()` is called first in `Program.Main`, before anything can
  throw. It redirects `Console.Out`/`Console.Error` through a `TeeTextWriter`
  that mirrors every write into a ~220-line ring buffer **and** a rolling,
  size-capped `client.log`, and installs handlers for
  `AppDomain.CurrentDomain.UnhandledException`,
  `TaskScheduler.UnobservedTaskException`, plus a try/catch around `game.Run()`.
- On an unhandled exception it writes `logs/crash-<UTC time>.txt` under the
  client's existing per-user data dir (`PlayerConfig.DirectoryPath`, the same
  dir `player.json`/`hud.json` already use): macOS
  `~/Library/Application Support/AllegianceCore`, Windows
  `%LOCALAPPDATA%\AllegianceCore`, Linux `~/.local/share/AllegianceCore`.
- The report contains version, git commit (from `AssemblyInformationalVersion`),
  OS, GPU, current mode, the exception + stack, and the last ~200 console lines.
- `client.log` is capped (~2 MB) and rotates to `client.log.1`, so a normal run
  keeps a bounded console record even when launched from a launcher.
- GPU is captured from `GraphicsDevice.Adapter` in `LoadContent`; mode is
  tracked from the launch path and per-screen transitions
  (`training:<name>`, `connect:host:port`, `intro`, `training-error`, …).

### 2. Never crash on a missing asset/core in player modes

- **Training start** (`Game1.Training.cs`): `TrainingHost.Start` is now guarded
  through `Game1.TryStartTraining` (a small, testable seam). Any start fault
  (missing `PCore014.igc` / training map, unreadable asset, boot failure) sets
  `_trainingError` to
  `"Training files are missing from this install: <map>. Reinstall or report this log."`
  and returns to the chooser — the exception never escapes to kill the app, and
  no half-built host is left behind.
- **Skirmish start** was already guarded (the client's `UpdateSkirmish` wraps
  `SkirmishHost.StartAsync` in try/catch → `_skirmishError` shown in the menu);
  confirmed unchanged.
- **Optional warp texture** (`WarpRenderer.LoadTextureFile` — the second macOS
  crash site): a missing / unlisted-in-manifest / unreadable / corrupt texture
  now falls back to the native soft placeholder and logs once per stem, instead
  of propagating `InvalidDataException`. A small GPU-free helper
  (`WarpRenderer.TryReadOptional`) centralises the guarded read so it is
  unit-tested directly. Ships/asteroids/stations already had analogous fallbacks
  (native renderers) and are untouched.

### 3. Bundle check exercises every mode (`src/Allegiance.ClientCore.Game/Assets/ModeAssetClosure.cs`)

`--check-assets` now also walks, through the packaged `ManifestAssetSource`:
- the training static core `PCore014.igc` (full all-table string sweep of every
  model/texture/sound reference, mirroring the existing `PCore001` sweep), and
- every mode's map: `training_1`, `training_3`, `training_5`, `training_6`,
  `training_nan`, and the skirmish/benchmark `m2xspiderw` — parsing each with
  `MissionMap.Load` and touching each sector's planet/poster names and each
  warp's texture/icon names (including the `bmp`-infixed bitmap surfaces native
  uses), failing on any missing file.

Any missing core/map — or referenced asset absent from the manifest — is now a
**build-time failure**, caught before a player ever launches. In the running
bundle this is belt-and-suspenders with the pre-existing manifest-touch
(`PlayerBundleCheck`), which already fails when a listed file is absent; the new
presence gate adds a clean message for a file missing from both disk and list.

## Tests (`tests/Allegiance.ClientCore.MonoGame.Tests/P749CrashSafetyTests.cs`)

| Test | What it proves |
|---|---|
| `CrashHandlerWritesReportFileWithHeaderAndRingBuffer` | crash file lands under `<data>/logs/crash-…` with version/commit/OS/GPU/mode + exception + ring-buffer lines |
| `MissingOptionalAssetFallsBackInsteadOfThrowing` | `TryReadOptional` returns null (fallback) for unlisted / absent art — never throws |
| `ModeWithMissingCoreReturnsToMenuWithMessage` | `TryStartTraining` yields no host + the "Training files are missing from this install: …" message |
| `BundleCheckCatchesDeliberatelyRemovedTrainingMap` | `TouchModeAssetClosure` throws on a missing `training_1.igc` |
| `BundleCheckPassesWhenEveryModeFileIsPresent` | the full closure parses and touches assets on the real corpus without throwing |

Each of the 5 ran **3× alone** (all green), plus 2× within the fast tier (below).

## Verification

| Gate | Result |
|---|---|
| Whole-solution build (`AllegianceCore.slnx -c Release`) | **0 warnings, 0 errors** |
| AOT publish (`Server`/`Core` changed?) | **Not required** — no change to `src/Allegiance.Server` or `src/Allegiance.Core` |
| New/affected tests 3× alone | 5/5 passed × 3 |
| Fast tier once (final code) | **PASS** — 8,869 tests, 269 s, 0 failed |
| linux-x64 portable bundle build | Built (`Allegiance-linux-x64.tar.gz`, 634 MB) |
| `./Allegiance --check-assets` on the bundle | **exit 0** — "Asset census OK: 447 hulls, 143 stations, 2877 artwork files"; includes PCore014 + all 5 training maps + m2xspiderw |
| Bundle check with a training map removed | **exit 2** — catches a removed `training_1.igc` (build-time, not player-time) |

Scratch + evidence: `~/allegiance-scratch/P749/` (bundle, check outputs
`check-assets.out`, `ck2-pos.out`, `ck2-neg.out`, bundle build logs). No local
servers/clients/bots were started (the `--check-assets` runs are display/socket
free and exit); nothing to stop.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Optional texture that can't load → skip/fallback, don't crash | native effect loaders take a surface handle from `pmodeler->LoadImage`/`pcontext->GetTexture()` (`src/effect/alephgeo.cpp:53,210`) and renderers tolerate a null surface | design | guarded `WarpRenderer.LoadTextureFile` → native soft placeholder, logged once |
| Missing art must not abort a player session | native assumes a full install; Windows crash would surface a modal, macOS kills the process | bug/era (Windows-centrism) | fall back + log; never propagate |
| A mode that can't start returns the player to the chooser | native mission-init failure backs out to the mission select screen | design | `TryStartTraining` → menu + clear message |
| Diagnostic trail on crash | native had no portable crash log (Windows Dr. Watson only) | new (modern improvement) | `CrashLog` crash file + rolling `client.log` |

Most of this task is **defensive hardening** — new safeguards, not faithful
ports — so the classifications above are honest: the few native behaviours
(optional-art fallback, mission-init backout) are respected as design intent;
the crash-log and presence gates are modern additions with no native analogue.

## Modern alternative considered

Native ships no user-visible diagnostic; a Windows player saw a raw fatal-error
dialog, a macOS player got a silent SIGABRT. The modern, engine-standard choice —
structured crash logging to a per-user dir (as Unity/Godot/Unreal do) plus a
continuous rotating console log — is exactly what this task adds; it is strictly
more informative than native's modal and costs nothing at runtime. The mode
closure is likewise the modern "verify all referenced assets at build time"
(asset-baking) discipline that native lacked entirely.
