# R2 — Windows setup and Linux AppImage

2026-10-18, working branch `codex/r2`. Local artifact version: `0.2.0` (verification
only). No commits, pushes, tags, workflow dispatches or releases were made.

## Implementation

- `packaging/package.py --format portable|installer|all` keeps the portable default.
  Linux adds an AppImage; Windows adds an Inno Setup EXE; macOS retains its .app ZIP.
  `all` builds one payload for both formats. `--from-portable` verifies the archive
  checksum and inventory before building an installer on another platform.
- Windows uses the permanent AppId `{E5427756-E0DF-4CF0-80F2-668C53EC578A}` and the
  supplied numeric version for upgrades. Per-user installation needs no elevation
  and defaults to `%LOCALAPPDATA%\Programs\AllegianceCore`; optional all-users mode
  uses Program Files. Start-menu and optional desktop shortcuts launch the intro
  screen without arguments. The license page concatenates LICENSE, NOTICE,
  CREDITS.md and README-PLAYERS.txt. The uninstaller has no user-data deletion rules.
- AppImage wraps the Linux portable payload in `usr/lib/AllegianceCore`, with
  `AppRun`, `.desktop`, SVG icon and `.DirIcon` at the AppDir root. AppRun keeps
  arguments intact and uses the per-user config directory as its working directory,
  so relative screenshots also stay outside the image. Both FUSE-free runtime
  execution and explicit `--appimage-extract`/AppRun execution are covered.
- Versioned AppImage build-tool/runtime downloads are SHA256 pinned in
  `appimage-tools.json`. Packaging passes an explicit runtime file; no build-time
  implicit runtime download is needed. Every artifact gets a SHA256 sidecar and
  JSON size/provenance metadata, and fails packaging at or above 2 GiB.
- The existing manifest check initially failed on the dispatch base. Regeneration
  added the `trainingscreen.mdl` root referenced by the existing
  `TrainingScreenModels.cs:34` and 21 transitive dependencies. All formats now use
  the resulting same 2,795-file artwork manifest. Each installer retains its
  platform's portable inventory byte-for-byte.
  Artwork and sky pins remain unchanged. No gameplay/native rules changed.

## Artifacts and verification

<!-- ARTIFACTS -->

| Local artifact | Bytes | MiB |
| --- | ---: | ---: |
| `Allegiance-linux-x64.AppImage` | 537,184,760 | 512.30 |
| `Allegiance-linux-x64.tar.gz` | 593,327,570 | 565.84 |
| `Allegiance-osx-arm64.zip` | 590,929,832 | 563.55 |
| `Allegiance-win-x64-setup.exe` | 505,235,163 | 481.83 |
| `Allegiance-win-x64.zip` | 595,566,691 | 567.98 |

<!-- /ARTIFACTS -->

The actual Linux tar.gz and AppImage inventory files compare byte-for-byte.
The Linux, Windows and macOS archives also have identical artwork manifests and
identical hashes for all 3087 artwork/sky inventory entries.

Every artifact is below 2 GiB. Final artifact sizes, locations and SHA256s are recorded in
[verification.json](R2_assets/verification.json). Local archives/installers are
verification outputs, not published downloads. The macOS ZIP remains covered by
the existing macOS CI extraction, executable-mode and asset checks.

The final setup was compiled with **Inno Setup 6.7.3 under Wine 9.0** in a
container. A second Inno Setup 7.1.0 compile also succeeded. The direct host Wine
runtime cannot write its `/run/user` directory. An additional container install
attempt failed to resolve Wine's 64-bit Program Files known folder, before
copying application files; native silent install/upgrade/check/uninstall remains
the Windows CI gate. No native Windows smoke pass is claimed here. The macOS ZIP
was cross-built locally; running its arm64 launcher remains the macOS CI gate.

The whole-solution build passed with **0 warnings / 0 errors**. All **15 packaging
tests passed 3 consecutive runs**; each of the seven new methods also passed
**3/3 fresh individual invocations** (21/21). The tests cover real launcher execution with
spaces/no arguments, preserving existing preferences, identical manifests,
format routing, compiler arguments, combined license text, invalid versions,
portable/payload corruption, checksums/metadata and the 2 GiB boundary.
Python/shell syntax checks, workflow `actionlint`, and `git diff --check` passed.

The final AppImage passed `--check-assets` on this Ubuntu 26.04 workstation from
outside the repository, with environment overrides cleared and artwork/sky both
resolved inside its extracted image. The check enumerated **447 hulls, 143
stations and 2,795 artwork files**, including the full sky/content inventory.

Stock **Ubuntu 24.04** and **Fedora 43** smoke images install distro system
dependencies only, with no system .NET runtime or SDK. Each ran the final AppImage
as UID 1000 with `--network none`, a read-only container/root artifact mount and
writable temporary storage. Both passed:

1. The AppImage runtime's `--check-assets` via `APPIMAGE_EXTRACT_AND_RUN=1`.
2. Explicit extraction, `sha256sum --check bundle-files.sha256`, and extracted
   AppRun `--check-assets` after removing write permission from the AppDir.
3. The actual AppImage `--skirmish-smoke 60`: nine live bots, a running match and
   pilot travel greater than one metre. Exact wall/simulation times and distances
   are retained in the JSON evidence.

| Offline distro | Wall seconds | Bots alive | Pilot travel (m) |
| --- | ---: | --- | ---: |
| Ubuntu 24.04 | 60.04 | 9/9 | 2831.18 |
| Fedora 43 | 60.03 | 9/9 | 2830.75 |

Ubuntu additionally launched **AppRun with no arguments**, an unreachable
`http://192.0.2.1:5000` lobby, Xvfb/software GL and the existing intro screenshot
hook. It rendered a frame, wrote `intro_proof.png` in the per-user directory and
exited successfully within the 60-second limit. Internet access remained disabled;
loopback stayed available for the local skirmish server/bot traffic.

One unfiltered sweep of each required project completed. No second full sweep
was run.

| Project | Passed | Failed | Skipped | Duration |
| --- | ---: | ---: | ---: | --- |
| Core | 4,876 | 3 | 0 | 16 m 52 s |
| ClientCore | 1,380 | 0 | 1 | 26 m 29 s |
| MonoGame | 2,434 | 0 | 3 | 16 m 7 s |

Full-sweep totals: **8,690 pass / 3 fail / 4 skip** across **8,697 cases**.
The three MonoGame skips are the existing unavailable private Meshy-pack tests.
The ClientCore opt-in skip was covered separately below.

Each failed Core exact method passed **3/3** fresh individual invocations: the
P661 allocation counter, pooled ordered-lane observation and cross-side
HEAVY_SHIPS_UPDATE observation. They meet the brief's load-flake rule and are
recorded in `docs/TEST_FLAKES.md`; no test assertion or production C# code changed.
Exact names, failures, skip names and isolated outcomes are in the small ledger.
Core/Server code did not change, so the conditional AOT publish is not applicable;
packaging changes do not trigger the five-run bot-flight showcase gate.

The ClientCore default opt-in skip for
`P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms`
was covered separately with `ALLEGIANCE_LIVE_P660_SHOWCASE=1`: **1 pass / 0 fail /
0 skip**, 22 seconds. This exercises the real local 15v15 MCP scenario, human/bot
match rotation and finale rearming; no rig/221/200v200 run was invoked.

## CI gates

The release workflow builds Linux portable + AppImage, Windows portable + setup,
and the existing macOS ZIP. The Windows job uses `windows-latest` and Inno Setup
to build the EXE from the checksum-verified portable ZIP. The current
[Windows 2025 runner inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md)
lists Inno Setup 6.7.1; the workflow resolves `iscc` or its standard Inno Setup 6
installation path. Its smoke installs a
version-0.0.0 baseline, upgrades in place to the release version, checks the same
registration and a single uninstaller, hashes every installed portable file,
checks both shortcut targets/empty arguments, runs `--check-assets`, uninstalls,
and verifies that player.json and keymap.json survive. Both per-user and all-users
modes are gated. Native Windows execution is a CI check, not a claim about Wine.

The AppImage smoke matrix uses the same two stock-distro Dockerfiles and offline
commands exercised locally. Publishing waits for Windows, macOS and both Linux
smokes. Release notes enumerate all five downloads; the publish job verifies and
combines every artifact's checksum into SHA256SUMS.txt. Manual workflow dispatch
still builds artifacts without publishing. Numeric release tags must exceed
`v0.0.0` so the older-version upgrade check has a valid baseline.

## Player install instructions

**Windows:** verify `Allegiance-win-x64-setup.exe` against SHA256SUMS.txt, then run
it. For this unsigned build, SmartScreen may require **More info → Run anyway**.
Choose the default per-user mode, or all users with administrator approval. The
Start-menu entry and optional desktop shortcut open the intro screen. Run a newer
setup in the same mode to upgrade. Uninstall through Windows Installed apps;
`%APPDATA%\AllegianceCore\player.json` and keymap.json remain.

**Linux:** verify `Allegiance-linux-x64.AppImage`, run
`chmod +x Allegiance-linux-x64.AppImage`, then double-click or execute it. Optional
AppImageLauncher/Gear Lever can add a desktop-menu entry. If FUSE is unavailable,
run `./Allegiance-linux-x64.AppImage --appimage-extract`, then
`./squashfs-root/AppRun`, or use `APPIMAGE_EXTRACT_AND_RUN=1`. Replace the AppImage
to upgrade and delete it to uninstall. Config/keymap and relative screenshots
remain under `${XDG_CONFIG_HOME:-~/.config}/AllegianceCore`.

**Portable/macOS:** the existing Windows ZIP, Linux tar.gz and macOS .app ZIP
remain available. Extract the complete portable archive; on macOS drag the .app
to Applications and follow the included unsigned Gatekeeper instructions.
Single Player → Skirmish works without internet. Multiplayer needs an operator's
server/lobby. Detailed install steps and Linux system dependencies are included
in `packaging/README-PLAYERS.txt` and shipped in every payload.

## Evidence storage

Small repository evidence is limited to `docs/reports/R2_assets/verification.json`
(well below the 2 MB task cap). Raw logs/TRX/tool scratch are outside the repository
under `/tmp/allegiance-codex-r2-evidence/`; initial build/test processes wrote to
`/tmp/allegiance-evidence/R2/` and are copied into the unique evidence directory.
The requested `~/allegiance-evidence/R2/` destination is outside this sandbox's
writable roots; `/tmp` is the permitted external evidence destination.

Large generated archives/installers remain ignored under `dist/`: `/tmp` has a
shared quota and could not retain
all five downloads without starving concurrent test runs. External `artifacts/`
contains only their small JSON metadata/checksums. Raw logs/TRX are retained
outside the repo; no large evidence or archives are tracked. Build/runtime caches
also remain ignored under `dist/`.
