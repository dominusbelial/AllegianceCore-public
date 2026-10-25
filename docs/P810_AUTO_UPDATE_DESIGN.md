# P810 — Client and server auto-update: design doc + vertical slice

Task: port the native lobby **AutoUpdate** role to the modern release pipeline
(signed releases, delta downloads). Idea source (ideas only, no code copied):
`wivuu/stellarallegiance` ADR 0004 (Velopack launcher) / ADR 0005 (servers
self-update).

## 1. What native did (the rules being ported)

| Rule | Native cite |
|---|---|
| The lobby advertises the official file set: FTP site/dir/account/password + the CRC of `FileList.txt` in `AUTO_UPDATE_INFO(253)` | `lobby/messageslc.h:45-52` |
| The client reports its local file-list CRC at LOGON (`crcFileList = FileCRC("FileList.txt")`) | `clintlib/clintlib.cpp:1418,1428` |
| The lobby compares that CRC against its official one and sends `AUTO_UPDATE_INFO` only to old clients | `lobby/client.cpp:272-281`; the lobby's CRC is computed from `FileList.txt` at `lobby/AutoUpdate.cpp:72-76` |
| On receiving it the client creates the AutoDownload, **disconnects from the lobby**, and runs `BeginUpdate` | `clintlib/appmsg.cpp:3398-3430` |
| `BeginUpdate` fetches the official file list over HTTP, verifies local files **per file**, and downloads only the changed/missing ones | `clintlib/AutoDownload.cpp:225-282` (local-list shortcut :249-263, HTTP fetch of `FileList.txt` :265-280), local analysis `:993` |
| The server side had the same machinery as a standalone `AllSrvUpdate` tool (CRC of a file list on argv, then update) | `src/AutoUpdate/AutoUpdate.cpp:325-356` |

Native classified:

- **Design** — "the lobby tells the client which files are official; the client
  fetches only what differs, then restarts into the new build". Ported.
- **Era constraint** — CRC-32 file lists over anonymous FTP, plaintext
  account/password on the wire (`messageslc.h:45-49`). *Modern better approach*:
  SHA-256 content hashes over HTTPS with an ECDSA P-256 signature over the
  manifest, so a tampered feed cannot install code.
- **Bug (by today's standards)** — downloading executable files with no
  authenticity check at all; and `SetFTPSite` credentials rode every client's
  logon response. The signed-manifest design removes both.

## 2. Choice: Velopack vs a small in-house updater

Considered Velopack (the idea source's launcher choice). Rejected for this repo:

1. **It wants to own the install layout** (`Update.exe` / `.AppImage` hooks /
   its `releases.{channel}.json` feed). The port already has a tested packaging
   pipeline (`packaging/package.py`: portable tar.gz/zip, AppImage, Inno Setup
   installer, all smoke-tested in `release.yml`). Velopack would replace, not
   extend, that pipeline.
2. **Delta downloads**: Velopack's deltas are its own differential package
   format. Native's delta was "download only the files whose CRC differs"
   (`AutoDownload.cpp:993+` local analysis). The in-house manifest reproduces
   exactly that behaviour with SHA-256, which is the parity goal.
3. **macOS**: Velopack's macOS story is the weakest of its platforms; the port
   ships `osx-arm64` portable bundles today. A file-manifest updater works
   identically on Linux/Windows/macOS because it operates on the unpacked
   bundle directory, whatever the container format.
4. **Dependency hygiene**: the in-house updater needs zero NuGet packages
   (BCL ECDSA + `HttpClient`), which matters for the AOT-published server.

**Decision: in-house updater** — a signed per-file manifest, applied on
restart. Android: the port has no Android client; nothing to update — noted as
out of scope (P810 scope lists it "separately"; it stays unaddressed until an
Android port exists).

## 3. Design

### Feed contract (native `FileList.txt` + `AUTO_UPDATE_INFO` modernised)

A release feed is any static HTTPS root (GitHub Release assets, an nginx dir,
a local HTTP dir in tests) holding:

```
latest.json                  # {"version":"2.0.0","manifest":"manifest-2.0.0.json","signature":"<base64 ECDSA sig of the manifest bytes>"}
manifest-2.0.0.json          # UpdateManifest: version + every file (path, size, sha256, unixMode)
<relative file paths>        # the feed mirrors the unpacked bundle's application tree
```

- The **signature** is ECDSA P-256 (FIPS-curve, BCL-native) over the exact
  manifest bytes. The public key is configured client/server-side; the private
  key exists only in the release workflow's secrets (`packaging/README.md`).
  A tampered manifest or package is refused before anything is staged.
- **Delta**: the client hashes its local files and downloads only entries whose
  SHA-256 differs or that are missing — the direct modernisation of native's
  per-file CRC compare (`AutoDownload.cpp:993+`). Removed files are deleted on
  apply.
- `unixMode` per entry preserves executable bits through the HTTP hop
  (launchers, `allegiance.sh`); the applier chmods on Unix.
- **The HD texture pack never rides the feed.** `packs/` is in the manifest
  builder's exclusion list and `UpdateApplier` refuses entries under it — the
  Meshy rule (P601): the HD pack stays a separate, explicit, optional download.
  (Covered by `UpdateManifestBuilderTests.ExcludesHdTexturePackPaths`.)

### Client (`src/Allegiance.ClientCore.MonoGame/Program.cs`)

1. **Apply-on-restart first**: before SDL/GL/audio load, `UpdateApplier`
   applies any staged pending update (fail-safe: any error logs and the old
   build boots). This is the native Reloader step
   (`AutoDownload.cpp:225-240` sets the working dir for the reloader).
2. **Update check** (the `AUTO_UPDATE_INFO` trigger): after the apply, before
   the window opens, the client fetches the lobby's `GET /api/update/info`
   (Section 4). If it carries a `feedUrl` **and** (`currentVersion` or
   `minimumVersion` is newer than the installed one), the client checks the
   feed and stages the update, reporting progress to the console log
   (`logs/client.log`, P749). Native disconnected the lobby during a download
   (`appmsg.cpp:3419`); the port checks *before* connecting, so there is
   nothing to disconnect — classified **design (modernised)**.
3. A feed can also be forced with `ALLEGIANCE_UPDATE_FEED` (+ optional
   `ALLEGIANCE_UPDATE_PUBLIC_KEY`) for testing/offline lobbies.
4. The installed version is `autoupdate/installed.json`, written by the
   applier; without it the client reports the assembly's informational version
   (`0.0.0` in dev builds).

### Lobby (`src/Allegiance.Lobby`)

- `LobbySettings.CurrentClientVersion` / `MinimumClientVersion` /
  `UpdateFeedUrl`; `GET /api/update/info` returns
  `{currentVersion, minimumVersion, feedUrl}` — the HTTP analogue of
  `AUTO_UPDATE_INFO(253)` (`messageslc.h:45-52`). Empty/absent values mean
  "not advertised"; the client then does nothing (parity with a native lobby
  with AutoUpdate unconfigured).
- Servers can require a minimum client version: the heartbeat's
  `minClientVersion` (optional) is stored on the listing and exposed in
  `ServerListingDto`. The client's server-list poll hides listings that demand
  a newer client than installed (pure rule in
  `Allegiance.ClientCore.Game/ServerListing.cs`, both lobby flows). Native
  enforced this at the lobby itself via the LOGON `crcFileList`
  (`clintlib.cpp:1418` → `client.cpp:272-281`); the port advertises instead —
  classified **design (modernised: no new data on the game-connection wire;
  the join protocol stays byte-compatible)**.

### Server (`src/Allegiance.Server`)

- `ServerConfig.Updates` (`UpdatesConfig`): `Enabled` (**default off**),
  `FeedUrl`, `PublicKeyPem`. Opt-in, exactly like the brief asks.
- At game over — the native-like quiet point, the single shared
  `DeclareGameOver` path (P178) after the finished mission is rotated and every
  GAME_OVER packet is already on the wire — the server schedules a
  fire-and-forget feed check + stage on the thread pool (never the sim thread,
  never mid-match). A staged update is applied on the next process start
  (Docker `restart: unless-stopped` or systemd brings it back on the new
  build). The gate logic (`ServerUpdateGate`) is pure and unit-tested.
- **Docker users keep pulling images**: `docker/compose.server.json` does not
  set the `updates` section and the default is off; `docker/README.md` says to
  leave it off in images (the updater would fight the image's read-only-ish
  expectations). *Modern better approach* for containers is image pulls — the
  updater is for bare-metal/binary deployments.

### Release workflow (`.github/workflows/release.yml`)

New `feed` job after `package`: per RID, unpack the portable bundle, run
`tools/feedbuild` (manifest + ECDSA signature + `latest.json`) with the private
key from the `RELEASE_UPDATE_SIGNING_KEY_PEM` secret, upload `feed-<rid>`
artifacts; the `publish` job attaches them to the GitHub release next to the
bundles, so the release itself is the feed root. Key handling is documented in
`packaging/README.md`; the key never touches the repo (publish-guard scans).
`workflow_dispatch` runs the whole pipeline with no publish job — the dry-run.

## 4. Verification slice

Local HTTP feed v1 → v2: client detects the newer version, downloads only the
changed files (delta proof: an unchanged file is requested 0 times), verifies
the signature, **refuses a tampered package** (both tampered manifest and
tampered file bytes), applies on a simulated restart; the server gate stages
only at game over and only when enabled; the lobby advertises and hides
too-old servers.
