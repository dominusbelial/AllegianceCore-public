# P644 — portable player bundles

Implemented in the working tree. No commits, pushes, tags or releases were
created, and no workflow was dispatched. Claude reviews this work, runs the
live checks and triggers the first public release.

## Changes

- `ExternalPaths`: environment → local.env → executable-relative `artwork/`
  or `sky/`, only when present. NativeSource has no bundled default. The
  explicit resolution context makes precedence and unrelated working-directory
  behavior testable without modifying process-global environment variables.
- Per-user `AllegianceCore/player.json` remembers Server/Port/Pilot/Lobby;
  launchers create an editable default, and explicit arguments override it.
  `keymap.json` now defaults to the same per-user configuration directory.
  The existing ALLEGIANCE_KEYMAP override and explicit test directory remain.
- The desktop artwork source automatically enforces the reviewed manifest for
  all consumers, including default-source helpers/startup audits. Unlisted
  OpenRead attempts persist as violations even if a loader swallows the error;
  the game exits nonzero. Optional Exists probes retain their fallback behavior.
  Sky remains a separate higher-priority source.
- The manifest generator unions P498's recorded boot/shop/flight/combat session
  with source references, PCore001's data, all maps' asset references, runtime
  globe/background families, all audio, and recursive MDL/MML dependencies.
  Only tracked Artwork files participate: this workstation's untracked sky
  copies cannot leak into the artwork subset. There are 2,082 roots and 2,773
  selected files: 976,788,863 bytes (931.5 MiB) of original Artwork.
- The deterministic display/socket-free census loads ShopCatalog, enumerates
  all 447 inspector hulls (including drones) and 143 station types, touches
  string references across every core table, and reads every selected file
  through RecordingAssetSource. `--check-assets` also reads/verifies every
  packaged artwork, sky and MGCB Content file against SHA256 inventory.
- `packaging/package.py` publishes self-contained Release linux-x64/win-x64,
  verifies external pins, copies the selected artwork and whole tracked sky
  pack, includes required licenses/credits and player instructions, and emits
  an archive, checksum and size/provenance JSON. Archives >=2 GiB fail. Local
  checkout/NuGet overrides support offline use. Linux includes a launcher,
  original SVG icon, desktop template and per-user shortcut installer; Windows
  includes Allegiance.exe, play.bat and its PowerShell helper.
- The public-only tag release job waits for the existing CI test filter and
  extracted Linux/Windows smokes. DesktopGL content and both RIDs publish on
  Ubuntu; Windows executes the actual Windows apphost. Manual dispatch builds
  artifacts only. The release job alone has contents:write and uses GITHUB_TOKEN.
  Release notes come from a checked-in template.
- README download instructions, GUIDE packaging notes and the full Claude
  release checklist are included. AppImage, Inno Setup, signing, Android and
  auto-update were optional/out of scope and were not added.

## External pins and native references

- Artwork: `4a60aa636405de66bca6bf48bff99c25a16a2ca3`
- Sky: `5272ded6066429e4ac7fdbb21cec192c09da92a2`

No simulation/gameplay rule was ported. The conservative dependency scanner
recognizes an IGC map's leading byte count to distinguish it from alternate
static cores: `~/Development/Allegiance/src/Igc/missionigc.cpp:82` writes the
count, `~/Development/Allegiance/src/Igc/missionigc.cpp:89` writes its body, and `~/Development/Allegiance/src/Igc/missionigc.cpp:2364` writes the
static-core version prefix. Existing C# MDL/catalogue/inspector behavior is
reused; there is no new native decoder. The MDL dependency scan is conservative
ASCII matching, limited to the first 64 KiB for large compiled units, rather
than a claim to interpret their object graphs.

## Verification

- Restored the fresh worktree from the local NuGet cache using
  `dotnet restore AllegianceCore.slnx --source /home/domingo/.nuget/packages
  -p:NuGetAudit=false -m:1`.
- Required `dotnet build AllegianceCore.slnx --no-restore -m:1` passed with
  **0 warnings / 0 errors**.
- **9 path tests and 93 selected asset/catalogue tests passed**, using xUnit's
  in-process discovery/execution API to avoid VSTest's TCP test-host listener.
  This includes all new tests plus existing asset-source/layering, inspector,
  hull/station/model/texture and sound-table coverage. The temporary runner
  resides under ignored `.verify/p644-runner`; it is not a product change.
- **3 packaging unit tests passed**: unsafe/duplicate/empty manifest paths,
  fail-closed copying of missing/untracked files, and pin mismatch rejection.
- Full-checkout census passed (447 hulls, 143 stations, 2,773 artwork files).
  Removing `dn_faphshtwr.mdl` from a temporary manifest made the same census
  fail with exit 2 naming that file. This also caught/fixed the generator's
  initial token-boundary assumption about packed IGC numeric bytes adjoining
  ASCII model names.
- Manifest regeneration/check, Python syntax/import, shell syntax and
  `git diff --check` passed. The release YAML parsed successfully; tag/manual
  triggers, platform matrix, smoke dependencies, public repository condition
  and job-scoped write permission were reviewed. act/actionlint and a GitHub
  workflow dispatch were not available/run here.

## Linux archive

Built with:

```
python3 packaging/package.py --rid linux-x64 \
  --artwork-path /home/domingo/Development/Artwork \
  --sky-path /home/domingo/Development/AllegianceCore-sky \
  --nuget-source /home/domingo/.nuget/packages
python3 packaging/smoke.py dist/Allegiance-linux-x64.tar.gz
```

`dist/Allegiance-linux-x64.tar.gz`: **535,140,245 bytes
(510.35 MiB)**; extracted files total **1,220,284,018 bytes
(1.136 GiB)**. This is 24.9% of the 2 GiB per-file ceiling.

SHA256: `69d687005e82f687489a765540b1f31b89f6c4b85d63f99d1124d84908ef4120`.

The final archive was extracted into a temporary directory outside the repo;
`--check-assets` passed with ALLEGIANCE_* overrides removed. A second extraction
also passed via `allegiance.sh --check-assets` from a path containing spaces,
with DOTNET_ROOT pointing at a nonexistent directory and multilevel lookup
disabled. The launcher created per-user player.json, and install-desktop.sh
created an absolute-path desktop shortcut under a temporary XDG_DATA_HOME.
Changing the extracted sky/giant-objects.json made the check fail with exit 2
and a checksum mismatch. No user configuration was written into the bundle.

Layout (all within `Allegiance-linux-x64/`):

```
Allegiance                    # renamed self-contained apphost
Allegiance.ClientCore.*        # managed assemblies/deps/runtimeconfig
*.so, System.*, ...            # bundled .NET and native runtime dependencies
Content/                      # MGCB fonts/audio, shaders, VFX + licenses
artwork/                      # manifest subset + Artwork LICENSE
sky/                          # all tracked sky files, CREDITS.md, licenses/
assets/ships/                 # public catalogue and overrides
allegiance.sh, install-desktop.sh, allegiance.desktop, allegiance.svg
artwork-manifest.txt, artwork-roots.txt, bundle-files.sha256, sources.json
LICENSE, NOTICE, CREDITS.md, README-PLAYERS.txt
```

## Remaining verification for Claude/CI

The Windows self-contained build was attempted with the local offline source
and failed NU1101: the cache lacks Microsoft.NETCore.App.Runtime.win-x64,
Microsoft.NETCore.App.Host.win-x64 and Microsoft.AspNetCore.App.Runtime.win-x64.
No Windows archive or Windows size is claimed. CI restores these packages and
runs the extracted executable on windows-2022; this remains unverified locally.

The sandbox cannot open sockets, so no live server/client session or GL/audio
render was attempted. The new live-smoke script is ready for Claude against an
already-running local server (HD sky, automatic side/ready/launch, capture and
manifest recording). It rejects unavailable-asset messages. The included sky
pack passed the asset-resolution smoke; visible sky layers, audio playback,
actual connection, clean-machine Windows operation and the first public CI run
still need Claude's verification. The only intentionally omitted optional art
is the private Meshy pack; native art is the supported fallback.

Coverage combines the existing P498 recording and deterministic conservative
reads; it does not claim fresh live recordings of every screen/map. The selected
artwork set is deliberately conservative, not proven mathematically minimal.
It still yields a single archive far below GitHub's per-file limit.
