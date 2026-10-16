# Player bundle maintenance

Use Python 3.11+, git and .NET SDK 10.0.400. On Linux install DejaVu Sans Mono
(`fonts-dejavu-core`) and FreeType for MGCB's two fonts. DesktopGL Content is
platform-independent, so CI cross-publishes both RIDs on Ubuntu and executes
the Windows apphost separately on Windows. No MonoGame graphics/audio objects
are constructed by the self-check.

```
python3 packaging/package.py --rid linux-x64
python3 packaging/package.py --rid win-x64
python3 packaging/smoke.py dist/Allegiance-linux-x64.tar.gz
# On Windows:
python packaging/smoke.py dist/Allegiance-win-x64.zip
```

The packer fetches exact `sources.json` commits, verifies them and copies only
tracked files. For offline builds use `--artwork-path /path/to/Artwork
--sky-path /path/to/AllegianceCore-sky --nuget-source /path/to/.nuget/packages`.
Overrides must have the pinned HEAD and no tracked modifications; untracked
files are ignored. No copy silently skips a missing file. Generated archives,
checksums and byte-count metadata live in ignored `dist/`. Archives at or above
2 GiB fail the build rather than producing an unpublishable release asset.
The whole tracked sky pack, including credits/licenses, is shipped separately
from artwork. The `bundle-files.sha256` inventory covers artwork, sky and MGCB
Content; `sources.json` records the external provenance.

## Manifest coverage

`artwork-manifest.txt` is a conservative dependency closure, not a claim of a
mathematically smallest set. `generate-manifest.py` combines:

- The real P498 recording (`docs/reports/P498_ARTWORK_MANIFEST.json`), covering
  boot, HUD, shop, flight and combat.
- Source references throughout the game and map generator, including lobby,
  menus, HUD and native effects.
- The PCore001 catalogue's model/texture/icon strings and every map's ASCII
  asset references, plus generated globe/background families. Alternate static
  cores are server data; the client currently always reads PCore001.
- Every original Ogg (rare voice lines are cheap compared with missing audio).
- Recursive MDL/MML references. For large compiled MDLs only the first 64 KiB
  containing the import table is scanned, avoiding image pixels. This is a
  conservative reference scan, not a new native MDL/IGC decoder.

`artwork-roots.txt` preserves the initial selection separately from its closure.
The client `--asset-census` loads ShopCatalog, walks the inspector catalogue
including drones and all stations, touches string references in every core
table (projectiles/expendables/parts/civs), then opens every root and dependency
through P498 RecordingAssetSource. The resulting recording can be retained as
CI evidence. This deterministic pass requires no server, GL device or sound.
A full-source census tests against the complete pinned checkout; new catalogue
references fail even when a renderer would otherwise swallow a loading error.

```
python3 packaging/generate-manifest.py --artwork /path/to/Artwork
# Review both text-file diffs, then:
python3 packaging/generate-manifest.py --artwork /path/to/Artwork --check
dotnet run --project src/Allegiance.ClientCore.MonoGame -- \
  --asset-census --manifest packaging/artwork-manifest.txt \
  --roots packaging/artwork-roots.txt --asset-manifest /tmp/census.json
```

Packaged launches automatically guard artwork OpenRead calls against the
manifest. Sky overrides have their own layer. Unlisted artwork opens
are remembered even if a loader catches the exception; the client exits 2.
Missing optional probes retain the normal fallback. `--check-assets` runs the
same census and verifies the SHA256 inventory, including all sky/Content files.
CI extracts outside the repository and clears ALLEGIANCE_* environment overrides
so the check cannot accidentally borrow a developer's configured art directory.
It is an asset-resolution smoke, not proof that a GPU rendered or a server
connection succeeded. No new live-session coverage is claimed by this task.

Optional content omitted from player bundles: the private Meshy pack. Native
art is the supported fallback. No sky layers are intentionally omitted.

## Release checklist for Claude

1. Review the working-tree diff/report and run the full socket/live suite.
   Verify the private-to-public publish filter includes `packaging/` and
   `.github/workflows/release.yml`, as well as the P498 input recording.
2. Build both archives; on Linux run the extracted client against a local
   server using the live smoke below. Check native and HD sky, the shop, every
   inspector hull/station, effects, lobby/menus, F1/F6 and audio. Look for
   unlisted-artwork failures or unavailable sky messages. Confirm user config
   survives replacing the install folder.
3. On the public repo run `workflow_dispatch`. It must upload two archives,
   individual checksums, sizes/provenance and census artifacts without creating
   a GitHub Release. Inspect the native Windows smoke job.
4. Only after review, Claude creates/pushes an annotated `v*` tag over SSH.
   This Codex lane never creates a tag, push or release. The workflow's publish
   job accepts only public-repo tag push events, uses job-scoped contents:write
   and GITHUB_TOKEN, and waits for tests and both platform smokes.
5. Verify the GitHub Release contains both archives, `SHA256SUMS.txt`, byte-count
   metadata and player notes. Download both archives fresh, check SHA256SUMS and
   start on clean Linux/Windows machines without .NET, git or art checkouts.

Live smoke (outside the Codex socket-restricted sandbox):

```
packaging/smoke-live.sh /absolute/path/Allegiance-linux-x64 127.0.0.1 6073
```

The server must already be running; this launches capture for 30 seconds with
HD sky, manifest enforcement and a recording. Xvfb can provide a virtual display
on a machine with working Mesa/OpenGL. Keep the log/frames, verify connection
and visible layers, and inspect the manifest. The script rejects unavailable
assets; optional private Meshy-pack absence is the documented exception.
