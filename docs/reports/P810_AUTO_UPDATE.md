# P810 — Client and server auto-update (native lobby AutoUpdate, modernised)

Design doc: [docs/P810_AUTO_UPDATE_DESIGN.md](../P810_AUTO_UPDATE_DESIGN.md) —
decision: a small **in-house signed-manifest updater** over Velopack (reasons in
§2 of the design doc). Idea source `wivuu/stellarallegiance` ADR 0004/0005 —
ideas only, no code copied.

## What native did, and what the port does

### Parity rows

Rows live in `docs/parity/parity-matrix.csv`, area `autoupdate` (added by this
task; `scripts/parity-scorecard.py` validates them). Summary:

| Row | Native rule (cite) | Port before | Port after | Classification |
|---|---|---|---|---|
| Lobby advertises the official file set | `AUTO_UPDATE_INFO(253)` = FTP site/dir/account/password + the `FileList.txt` CRC (`lobby/messageslc.h:45-52`) | Lobby had no update advertisement at all | `GET /api/update/info` = `{currentVersion, minimumVersion, feedUrl}`; unconfigured (default) answers all-null and clients skip the check — matching a native lobby with AutoUpdate unconfigured | design (modernised): no FTP credentials ride the wire; HTTPS feed URL instead |
| Client reports its file set at LOGON | `crcFileList = FileCRC("FileList.txt")` (`clintlib/clintlib.cpp:1418,1428`); the lobby compares and sends `AUTO_UPDATE_INFO` to old clients (`lobby/client.cpp:272-281`, its CRC computed at `lobby/AutoUpdate.cpp:72-76`) | nothing | **No game-wire change** (parity rule: only native-carried data). The check is HTTPS + client-side: the intro poll fetches `/api/update/info`; a server's `minClientVersion` (heartbeat → `ServerListingDto`) hides listings the installed client cannot join | design (modernised); fail-closed like native's unknown CRC at `client.cpp:272` |
| Client downloads only changed files | `BeginUpdate` fetches the official list, verifies local files **per file**, downloads the diff (`clintlib/AutoDownload.cpp:225-282`, local analysis `:993`) | no updater | `UpdateClient.CheckAndStageAsync`: SHA-256 the local tree, download only differing/missing files into `autoupdate/staging/`, verify every byte against the signed manifest; request-count test proves the delta (unchanged file fetched 0× on v1→v2) | design ported; era constraint replaced (CRC-32 + FTP → SHA-256 + HTTPS + ECDSA P-256 signature; a tampered manifest or package is refused before staging) |
| Client applies on restart | `appmsg.cpp:3398-3430` (create AutoDownload, disconnect lobby, `BeginUpdate`); Reloader prep `AutoDownload.cpp:225-240` | players re-downloaded manually | `UpdateApplier.ApplyPendingIfAny` runs at the very top of `Program.Main` (before SDL/GL/audio), re-verifying each staged SHA-256, restoring Unix exec bits, removing obsolete files, writing `installed.json`, failing safe to the old build. The lobby is consulted *before* connecting, so there is nothing to disconnect | design (modernised) |
| Server update machinery | standalone `AllSrvUpdate` tool, CRC from argv (`src/AutoUpdate/AutoUpdate.cpp:325-356`) | servers redeployed by hand | `ServerConfig.Updates` (opt-in, **default off**): at game over — the single shared `DeclareGameOver` quiet point, after every GAME_OVER packet is on the wire and the mission rotated — a fire-and-forget feed check stages a newer build; applied before the world loads on the next start. Gate: `ServerUpdateGate` (pure, tested) | design (modernised); Docker's better path stays image pulls (documented) |

**How the data reaches the client:** everything rides existing transports —
the lobby's HTTP surface (`/api/update/info`, the `AUTO_UPDATE_INFO` role) and
plain HTTPS GETs to the feed; no new field on any native wire message. The
server's `minClientVersion` rides the existing lobby-heartbeat register payload
(like P806's `GameParams`), not the game connection.

### HD texture pack (Meshy rule)

`packs/` and `hd/` are excluded from every manifest
(`UpdateFeed.ExcludedPrefixes`, enforced in the builder, the path guard and the
client's local hashing) and `UpdateManifestBuilderTests` /
`The_hd_texture_pack_is_never_in_the_manifest` pin it. The updater can never
pull the HD pack implicitly; it stays a separate, explicit download.

## Layout of the change

- **`src/Allegiance.Update/`** (new library — justified: shared verbatim by the
  AOT server and the MonoGame client, zero NuGet deps, so neither drags the
  other's framework): `UpdateManifest`/`UpdateFeedIndex` (feed contract),
  `UpdateSignature` (ECDSA P-256, BCL), `UpdateManifestBuilder` (+ path guard,
  exclusions, Unix-mode mapping), `UpdateClient` (check + delta + stage),
  `UpdateApplier`/`UpdateState` (apply on restart, installed.json, pending
  marker), `LobbyUpdateInfoClient`/`UpdateVersionGate`. Source-generated JSON
  only (`UpdateJsonContext`) — the AOT rule.
- **`src/Allegiance.Server`**: `UpdatesConfig` (+ `Validate`), heartbeat
  `minClientVersion`, `Server.Update.cs` (`ServerUpdateGate`,
  `ScheduleServerUpdateCheckAtGameOver`), the `DeclareGameOver` hook, pending
  apply in `ServerMain.RunAsync`, `ServerJsonContext` entry.
- **`src/Allegiance.Lobby`**: `LobbySettings` version fields,
  `/api/update/info`, `MinClientVersion` on `RegisterRequest`/`ServerListing`/
  `ServerListingDto` (absent stays null — pre-P810 payloads are byte-identical).
- **`src/Allegiance.ClientCore.MonoGame`**: `ClientUpdateBootstrap` (thin env/arg
  plumbing; every rule lives in the library) hooked in `Program.Main` after the
  asset checks. Key sources: `ALLEGIANCE_UPDATE_PUBLIC_KEY` (PEM),
  `ALLEGIANCE_UPDATE_PUBLIC_KEY_FILE`, or `autoupdate/feed-public.pem` beside
  the app; feed override `ALLEGIANCE_UPDATE_FEED` / `--update-feed <url>`.
- **`src/Allegiance.ClientCore.Game`**: `ServerListing.MinClientVersion` +
  `RequiresNewerClient` (shared intro + connect browse paths).
- **`tools/feedbuild/`** (release/proof tool, built by `tools/build-tools.sh`):
  manifest + signature + `latest.json`, `--stamp` (write
  `autoupdate/installed.json` into the bundle), `--asset-prefix` (GitHub
  Release's flat asset namespace: entry URLs become
  `feed-<rid>-dir__file`), `--verify` (end-to-end self-check through the real
  `UpdateClient`).
- **`.github/workflows/release.yml`**: new `feed` job (per RID: unpack the
  verified portable bundle, stamp the version, `dotnet run --project tools/feedbuild`
  with the key from the `RELEASE_UPDATE_SIGNING_KEY_PEM` secret, upload
  `feed-<rid>`); `publish` attaches the feeds unmerged so the GitHub Release is
  a flat-asset feed root per RID. `workflow_dispatch` = the dry-run: it runs
  everything except `publish`; without the secret it emits an UNSIGNED feed
  (marked `UNSIGNED`, refused by every verifier) — the dry-run cannot publish.
- **Key handling** (see `packaging/README.md`): one PKCS#8 ECDSA P-256 private
  PEM, stored only as the repo secret `RELEASE_UPDATE_SIGNING_KEY_PEM`; the
  public half is derived (or shipped as `feed-public.pem`); rotation = new key
  pair + new secret (clients pin the public key they trust). No secret ever
  touches the tree; `scripts/publish-guard.sh` scans.
- **Docker**: no new service/volume/port/env var (the self-update is opt-in via
  `server.json`'s `updates` section and is deliberately left OFF for image
  deployments — `docker/README.md` documents this). `docker compose config`
  validates; `docker compose build` + `up` smoke-tested locally (see gate).

## Verification (light gate)

- **Feed slice** — local HTTP feed v1 → v2, real `UpdateClient` against a real
  HTTP listener (`UpdateClientLoopbackTests`, 7 tests): detects, downloads only
  the delta (request-count proof: `launcher.sh` 0 requests on v2), verifies the
  signature, **refuses a tampered package** (size/SHA-256) and a tampered
  manifest and a wrong key, applies on a simulated restart (exec bit restored,
  obsolete file removed, pending cleared), and a poisoned staging always fails
  its re-verification. 28/28 tests, run **3× alone**: PASS ×3.
  Raw TRX: `~/allegiance-evidence/P810/` (loopback feed logs in
  `docs/reports/P810_assets/`).
- **feedbuild dry-run** — openssl-generated P-256 key, a synthetic bundle tree,
  `python3 -m http.server` as the feed host, `--stamp --verify`: the tool built
  `latest.json` + `manifest-2.0.0.json` + 3 file assets and its self-check
  (signature + SHA-256 through the real client) staged 2.0.0. Workflow YAML
  validated (`yaml.safe_load`; jobs `test, package, windows-smoke, feed,
  appimage-smoke, macos-smoke, publish`; `feed` needs `package`, `publish`
  needs `feed`). CI's GitHub-hosted dry-run is left to Claude's dispatch.
- **Lobby** — `UpdateInfoTests` 5/5 ×3 alone (unconfigured nulls, configured
  advertisement, empty-string-absent, `minClientVersion` on the listing).
- **Server gate** — `ServerUpdateGateTests` 5/5 ×3 alone (absent/disabled/
  partial configs never check; config validation).
- **Client rule** — `ServerListingMinClientVersionTests` 5/5 ×3 alone.
- **Whole-solution build** — `dotnet build AllegianceCore.slnx`: 0 warnings,
  0 errors.
- **tools** — `tools/build-tools.sh`: all 76 tool projects built (incl. the new
  `tools/feedbuild`).
- **AOT publish** — `dotnet publish src/Allegiance.Server -c Release -r
  linux-x64 --self-contained -p:PublishAot=true`: clean, no IL2026/IL3050
  (source-generated JSON only).
- **Fast tier** — `tools/run-tests.sh --tier fast`: PASS, 10355 tests (the
  runner's project list now includes `Allegiance.Update.Tests` — 28 tests ran
  in-tier); results under `~/allegiance-evidence/P810/fast-tier-summary.json`.
  One earlier in-tier run hit the already-ledgered load flake
  `P784ComposeTests.Fanout_issues_one_order_message_per_recipient_on_the_wire`
  (docs/TEST_FLAKES.md, P801 merge) — it passed 3/3 alone; the final run is a
  clean PASS.
- **Docker** — `docker compose config` OK; `docker compose build` (both images,
  Dockerfiles extended for the new project reference) and `up` bring the stack
  up; the lobby answered `/api/update/info` with the unconfigured all-null body
  and `/healthz` 200; server boot log clean; `down` removed the stack. The
  rig's untracked `~/allegiance-test/docker-compose.override.yml` untouched.

## Failures on master

None — this feature area did not exist on master; no existing test covers or
breaks it (the full solution build + fast tier were green before and after).

## Unfinished / notes for review

1. **Fresh-install version stamping**: bundles from `package.py` do not carry
   `autoupdate/installed.json`, so a never-updated install reports `0.0.0`
   (fail-closed: any advertised current/minimum triggers a check — the same
   direction as native's unknown CRC). A follow-up can stamp the version into
   the portable bundles at package time; the feed job already stamps its own
   unpacked trees.
2. **GitHub Release feed hosting**: release assets are a flat namespace, so the
   workflow uses `--asset-prefix feed-<rid>-` and `UpdateFileEntry.AssetName`;
   a plain static tree (nginx/GH Pages) needs no prefix. Both documented in
   `packaging/README.md`.
3. **In-flight upgrade UX**: the check happens at boot only (plus the lobby's
   advertisement); no in-game "update available" prompt yet — the native
   client also only updated at the lobby boundary (`appmsg.cpp:3398`).
4. **Android**: no Android client exists; nothing to update (out of scope per
   the brief, recorded here as the explicit gap).
5. The `RELEASE_UPDATE_SIGNING_KEY_PEM` secret must be created by Domingo
   before the first real signed tag (one `openssl` command, documented in
   `packaging/README.md` §Release signing).
