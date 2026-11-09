# P969 — Release bundles must report their own version

## Problem

Fresh installs of the published v0.1.5 bundles reported client version `0.0.0`.
`Allegiance.Update.UpdateState.ResolveInstalledVersion`
(`src/Allegiance.Update/UpdateApplier.cs:39`) reads
`<appRoot>/autoupdate/installed.json` and falls back to `"0.0.0"` when the file is
missing. Only the updater's apply step writes that file
(`UpdateApplier.cs:92-95`). The release pipeline built bundles with no
`autoupdate/installed.json`, so:

* `LogonRequest.ClientVersion` (`src/Allegiance.ClientCore.Game/Game1.Session.cs:2052`)
  was `"0.0.0"`.
* A server with `minimumClientVersion: "0.1.5"` refused the logon
  (`src/Allegiance.Server/Server.Dispatch.cs:2703-2720`,
  `UpdateVersionGate.IsTooOld`).
* The lobby server list hid the server from that client
  (`src/Allegiance.ClientCore.Game/Game1.Session.cs:432-442`).
* The updater treated a fresh install as version `0.0.0`, so a same-version feed
  would stage a needless zero-file update instead of reporting "up to date".

## Reproduction

A Linux x64 portable bundle was built the same way the release workflow does:

```bash
python3 packaging/package.py --rid linux-x64 --format portable --version 0.1.6
```

Extracted to `/tmp/p969-verify2` and checked with a tiny harness:

```bash
dotnet run --project .scratch/p969/ResolveVersion -- /tmp/p969-verify2
# With the fix present: 0.1.6
```

Removing `autoupdate/installed.json` reproduced the old behaviour:

```bash
mv /tmp/p969-verify2/autoupdate/installed.json /tmp/p969-verify2/autoupdate/installed.json.bak
dotnet run --project .scratch/p969/ResolveVersion -- /tmp/p969-verify2
# 0.0.0
```

A local signed feed was built from the bundle with `tools/feedbuild` (no
`--stamp` needed; the bundle already carries the full manifest), and a second
harness called `UpdateClient.CheckAndStageAsync` against it:

| Install state | Feed version | Updater decision |
| --- | --- | --- |
| With `installed.json` v0.1.6 | 0.1.6 | `UpToDate`, 0 changed files |
| Without `installed.json` (old 0.1.5 shape) | 0.1.6 | `Pending`, 0 changed files (needless) |
| With `installed.json` v0.1.6 | 0.1.7 | `Pending`, 1 changed file (delta) |
| Without `installed.json` (old shape) | 0.1.7 | `Pending`, 1 changed file (delta) |

After `UpdateApplier.ApplyPendingIfAny`, the upgraded tree reported `0.1.7`.

## Design choice

`packaging/package.py` now writes a full `autoupdate/installed.json` into every
bundle using the same schema as `UpdateManifestBuilder`
(`src/Allegiance.Update/UpdateManifestBuilder.cs`). The manifest contains every
application file's relative path, size, SHA-256 and Unix mode, excluding only the
updater state directory and the HD-pack subtrees (`packs/`, `hd/`). It is written
after the packaging-only `bundle-files.sha256` inventory so the manifest is a
complete picture of the shipped tree.

Why this approach:

* **Correct version on first run** — `ResolveInstalledVersion` reads the packaged
  version immediately.
* **Correct "up to date"** — `UpdateClient.CheckAndStageAsync` compares the feed
  version to the installed manifest version; equality now returns `UpToDate`
  instead of a needless `Pending`.
* **Correct delta** — `UpdateClient.HashLocalTree` hashes the real files on disk
  and skips anything whose SHA-256 already matches the feed manifest. A fresh
  install of version X against feed X therefore downloads nothing.
* **Correct obsolete removal on first upgrade** — the installed manifest lists
  every file the bundle shipped, so files dropped by the next release are removed
  when that update is applied.
* **No assembly-version stamping** — the assemblies carry
  `1.0.0+<sha>` `InformationalVersion`, which is not comparable to release
  versions. Stamping them would require changing `Version.TryParse` semantics and
  would not help the updater. Keeping the version in `installed.json` matches the
  updater's existing contract.

## Changes

### packaging/package.py

* Added `build_installed_manifest(bundle, version)` and
  `write_installed_manifest(bundle, version)`
  (`packaging/package.py:_EXCLUDED_MANIFEST_PREFIXES` onward).
* The build path calls `write_installed_manifest(bundle, args.version)` after all
  files (including the packaging-only `bundle-files.sha256` inventory) are copied
  and before artifacts are emitted.
* Modes are recorded as 3-digit octal (`755` for executables on Linux, `644` on
  Windows), matching `UpdateManifestBuilder.ModeToOctal`.

### packaging/test_packaging.py

* `test_bundle_writes_installed_version_manifest` — a fake bundle gets a valid
  manifest with the packaged version, correct hashes and excluded state files.
* `test_installed_manifest_excludes_state_and_hd_packs` — confirms the same
  exclusion rules as `UpdateManifestBuilder.Build`.
* `test_dev_bundle_reports_zero_version` — a `--version 0.0.0` dev build still
  writes a manifest and reports `0.0.0`.

### src/Allegiance.Update / tests

* Added `UpdateStateTests` in
  `tests/Allegiance.Update.Tests/UpdatePrimitivesTests.cs`:
  * `ResolveInstalledVersion_reads_written_manifest`
  * `ResolveInstalledVersion_falls_back_to_zero_when_missing`
  * `ResolveInstalledVersion_falls_back_to_zero_when_manifest_is_corrupt`
  * `ReadInstalledManifest_excludes_updater_state_directory`

### Smoke scripts

* `packaging/smoke.py` now reads the adjacent `<archive>.json` metadata and
  asserts `autoupdate/installed.json` reports the same version; `--version` can
  override the expected value.
* `packaging/smoke-appimage.sh` checks `autoupdate/installed.json` against
  `EXPECTED_VERSION`.
* `packaging/smoke-windows.ps1` checks the installed `autoupdate/installed.json`
  version matches the Inno Setup registry `DisplayVersion`.

### Release workflow

* `.github/workflows/release.yml` passes `EXPECTED_VERSION` into the AppImage
  smoke container.
* Removed `--stamp` from the `feedbuild` invocation: the unpacked bundle already
  carries the full `autoupdate/installed.json` written by `package.py`, so
  stamping an empty manifest over it is unnecessary and would make the unpacked
  intermediate inconsistent with the published bundle.

### Glossary

* `GLOSSARY.md` §6b now references bundle version reporting and the report.

## File:line reference

| File | Lines | What |
| --- | --- | --- |
| `packaging/package.py` | 20-26, 73-119, 249-276, 362 | manifest helpers and write call |
| `packaging/test_packaging.py` | 1-19, 350-413 | new packaging tests |
| `packaging/smoke.py` | 9, 70-78, 89-96 | version assertion in smoke |
| `packaging/smoke-appimage.sh` | 14-20 | `EXPECTED_VERSION` check |
| `packaging/smoke-windows.ps1` | 103-108 | installed.json vs registry check |
| `.github/workflows/release.yml` | 151-164 | pass expected version to AppImage smoke; remove feedbuild `--stamp` |
| `tests/Allegiance.Update.Tests/UpdatePrimitivesTests.cs` | 142-220 | `UpdateStateTests` |
| `src/Allegiance.Update/UpdateApplier.cs` | 10-46 | version resolution (unchanged, relied upon) |
| `src/Allegiance.Update/UpdateManifestBuilder.cs` | 47-79 | schema the manifest matches |
| `src/Allegiance.ClientCore.Game/Game1.Session.cs` | 2052-2053 | `InstalledClientVersion` |
| `src/Allegiance.Server/Server.Dispatch.cs` | 2703-2720 | minimum-client gate |

## Verification

* `dotnet build AllegianceCore.slnx -m:1 -p:AutoRestoreMGCBTool=false` — 0 warnings, 0 errors.
* `tools/build-tools.sh` — all 123 tool projects built.
* `python3 -m unittest discover -s packaging -p 'test_*.py'` — 23/23 pass.
* `dotnet test tests/Allegiance.Update.Tests` — 32/32 pass, 3× alone stable.
* `tools/run-tests.sh --tier fast` — one known non-task timing offender:
  `Allegiance.Core.Tests.P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`
  exceeded the calibrated 10 s Fast limit while functionally passing. It passed
  3× in isolation (9 s each) and is already ledgered in `docs/TEST_FLAKES.md`.
* Built bundle `dist/Allegiance-linux-x64.tar.gz` (v0.1.6, 644 MB) and ran
  `packaging/smoke.py dist/Allegiance-linux-x64.tar.gz` — passed, including the
  new version assertion.
* Manual harness verified `ResolveInstalledVersion`, `UpdateClient.CheckAndStageAsync`
  and `UpdateApplier.ApplyPendingIfAny` behaviours in the table above.

## Exact release check

Before re-enabling a server `minimumClientVersion`, the next release should:

1. Build the bundle with the intended version, e.g. `python3 packaging/package.py --rid linux-x64 --format portable --version 0.1.7`.
2. Extract it and run `python3 -c "import json; print(json.load(open('Allegiance-linux-x64/autoupdate/installed.json'))['version'])"` — must equal the release version.
3. Run `python3 packaging/smoke.py dist/Allegiance-<rid>.<ext>` — the embedded version assertion must pass.
4. Only then set `minimumClientVersion` to that release version.

## Native intent

Native Allegiance used a lobby-side `FileList.txt` CRC computed by
`lobby/AutoDownload.cpp:72-76` and sent via `AUTO_UPDATE_INFO(253)`. A client
whose `crcFileList` did not match was told to update
(`lobby/client.cpp:272-281`), and a mismatched `verLobby` got a `LOGON_NACK`
(`lobby/client.cpp:290-309`). The modern port replaces the CRC with per-file
SHA-256 manifests and compares release versions, but the intent is the same: a
fresh install must identify itself so the server/lobby can gate it. Writing
`installed.json` at bundle time gives the fresh install that identity.

## Modern approach

* SHA-256 manifests signed with ECDSA P-256 (`UpdateManifestBuilder`,
  `UpdateSignature`).
* Apply-on-restart staging (`UpdateApplier`) with re-verification of every byte.
* Per-file delta downloads (`UpdateClient.HashLocalTree`) so first-run
  same-version checks cost no network traffic.
* Bundle-time manifest generation in Python so the release pipeline needs no
  extra C# tool invocation and the schema stays identical to the feed manifest.
