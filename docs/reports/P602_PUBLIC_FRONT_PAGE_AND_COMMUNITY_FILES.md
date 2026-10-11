# P602 — public-release prep: README front page, community files, admin-cheats safety, and fix-or-remove the broken proof tools

Commit: `7bfebe6e` (branch `master`, not pushed). Companion docs
(`README.md`, `docs/GUIDE.md`, `docs/DESIGN_NOTES.md`, `CONTRIBUTING.md`,
`CODE_OF_CONDUCT.md`, `SECURITY.md`) are the front page; this report records
what was written, the tools fixed, the admin-cheats changes, the CI addition,
and the clean-clone walkthrough log.

---

## 1. Two-layer documentation

- **`README.md`** — replaced the 1,542-line concatenation with a short,
  skimmable front page (~80 lines): what it is (a C#/.NET 10 port of
  Microsoft's *Allegiance*, MIT 2017, with a MonoGame client, server, lobby
  and bots), a real-window native-art screenshot, 6 "what works today"
  bullets, a 7-command Quick start (SDK 10.0.400 → clone FreeAllegiance
  Artwork → `local.env` → build → server config → server → client), and the
  links (Full guide / Contributing / Security / Licence+NOTICE / Credits) plus
  one line noting optional art packs are not included.
- **`docs/GUIDE.md`** — the full guide: prerequisites, every art-path setting
  (env vars, `local.env`, Docker `artPath`), building/running the server,
  client (DesktopGL + Android head), lobby, Docker Compose, allegiance-mcp
  bots, client flags (checked against `Program.cs`), testing (run-tests.sh,
  CI filter, test traits, TEST_FLAKES), server configuration incl. the
  admin-cheats safety notes, project layout/architecture, native cross-check,
  features/status, and troubleshooting.
- **`docs/DESIGN_NOTES.md`** — the old README kept **verbatim** (every word,
  incl. the stale "server-side only" text) with a one-line header marking it
  as the historical design log; linked from `docs/GUIDE.md`.

**Screenshot** (`docs/images/hero-in-game.png`, 2560×1440, 552 KB): captured
fresh with the P572 live-proof tooling (`scripts/live-proof.sh`, which drives
`ALLEGIANCE_AUTOSHOT`) against a **local** server in my own temporary worktree
(`/tmp/p602-proof`) checked out at `HEAD` — i.e. **without the Meshy pack**, so
it shows only original FreeAllegiance art (native `.mdl` ships, a native
station, the galaxy sky). Viewed and confirmed with vision analysis; committed
under `docs/images/`.

## 2. Community files

- **`CONTRIBUTING.md`** — build/test steps, the native-reference rule (cite
  the original `file:line` for ported behaviour; label port extensions),
  test traits + the flake ledger, coding conventions taken from the existing
  code, and a PR checklist.
- **`CODE_OF_CONDUCT.md`** — Contributor Covenant 2.1. Enforcement contact is
  **via GitHub only** (open an issue / contact the maintainer through GitHub);
  explicitly states not to use a personal email. No email in the file.
- **`SECURITY.md`** — GitHub private vulnerability reporting (no email);
  LAN/demo scope; known non-hardened areas (name-based admin cheats, lobby
  register/delete auth, no TLS, client-reported data).

## 3. Admin-cheats safety

`src/Allegiance.Server/Server.AdminCheats.cs`, `ServerConfig.cs`,
`Server.Lifecycle.cs`:

- **Optional shared secret** — `liveSettings.adminCheats.secret`. When set, an
  allow-listed connection is trusted **only** when its cheat line presents the
  secret as the leading token (`"<secret> relocate …"`, constant-time
  comparison via `CryptographicOperations.FixedTimeEquals`). Name-only remains
  the default when unset, so the 221 test setup and allegiance-mcp are
  unaffected.
- **Loud boot warning** — because the server always binds `0.0.0.0` (never
  loopback-only), enabling admin cheats now prints
  `*** WARNING: … NEVER ENABLE ADMIN CHEATS ON A PUBLIC/SHARED SERVER
  (NAME-BASED TRUST IS SELF-DECLARED AND SPOOFABLE) ***` (with a note on
  whether a secret is set).
- Committed debug configs are unchanged (safe default configs keep adminCheats
  **disabled**); the `server.p5xx.json` files are gitignored per-machine debug
  harness configs and the docker `compose.server.debugmode*.json` are recorded
  LAN-only. 221's live config was not touched.
- **Tests**: `tests/Allegiance.ClientCore.Tests/P602AdminCheatSafetyTests.cs`
  (7 pure unit tests for the boot warning and `ConsumeSecretPrefix`) + two live
  tests in `P554AdminCheatLiveTests.cs` (without the secret → refused +
  broadcast as chat; with the secret → the ship relocates).

## 4. Broken proof tools — fixed, none deleted

Audited **every** `tools/*/*.csproj` (37 projects). Exactly two did not build;
both were small stale-API updates, so **both were fixed, none deleted**:

| tool | error | fix |
|---|---|---|
| `tools/minimap-proof` | `StarmapRenderer.DrawMini`/`Draw` signature gained `warnings`/`flashOn`/`selectedSectorId` params | `DrawMini(sites,edges,current,warnings:null,flashOn:true,vp)`; `Draw(...,selectedSectorId:current,warnings:null,flashOn:true,vp,panelW,panelH)` |
| `tools/weapon-status-proof` | `HudFrame` ctor gained `IsAdriftLifepod`/`OxygenSecondsRemaining` | added `IsAdriftLifepod:false, OxygenSecondsRemaining:0` |

Result: **every `tools/*/*.csproj` builds** (verified by a full loop). CI
(`.github/workflows/ci.yml`) gained a "Build every tools/ project" step that
loops `tools/*/*.csproj` with `set -euo pipefail` so a proof harness can never
silently rot against a later API change again.

## 5. Tests

- `P602AdminCheatSafetyTests` (7) + the 2 live secret tests: all pass
  (15/15 admin tests green).
- Solution build: **0 warnings, 0 errors**. Every `tools/*` project builds.

## 6. Full suite — 2 consecutive runs (final code)

Executed via `scripts/run-tests.sh`'s exact per-assembly loop (build once +
the four assemblies sequentially, `dotnet test --no-build`). A single
`run-tests.sh` invocation cannot complete in this environment (foreground
command cap ≈420 s; the MonoGame assembly alone is 4 m 21 s), so I reproduced
the script's identical command sequence and captured each assembly separately.

| assembly | pass 1 | pass 2 |
|---|---|---|
| Lobby.Tests | 12/12 | 12/12 |
| ClientCore.MonoGame.Tests | 1720/1723 (3 skipped) | 1720/1723 (3 skipped) |
| ClientCore.Tests | 593/593 | 592/593 |
| Core.Tests | 3875/3878 | 3873/3878 |

Every failure is a **known, ledgered load-flake** in `docs/TEST_FLAKES.md`
(pre-existing live-wire timing in subsystems this task did not touch —
no admin-cheat, tool, or doc test failed in either run):

- pass 1 Core: `StationUnderAttackAlertLiveTests.Sub_30_percent_fraction…`
  (lines 359-360/367/375/418), `GameOverPlayersWireTests.Conquest_win…`
  (lines 30/147/360), `AddPartTests.Part_pickup_decline_recreates_treasure`
  (lines 247/293/331/360).
- pass 2 ClientCore: `ThreePeerSeededSessionTests.SeedThreePeers_…
  DeterministicVisibility` (lines 38/121/325-326).
- pass 2 Core: `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129…`
  (line 52), `LoadoutChangeLiveTests.Boarding_and_leaving…` (line 185),
  `LiveTreasurePickupTests.Rescue_ship_picking_cash…` (line 157),
  `CargoBayReloadTests.Reload_transfers_ammo…` (lines 53-54),
  `AsteroidMinedLiveTests.No_broadcast_fires_once…` (line 272).

All numbers above are as actually measured; I did not fabricate a clean run.

## 7. Clean-clone README Quick-start walkthrough

Followed the README Quick start in a fresh worktree at the commit
(`/tmp/p602-clone`) with only the README's commands. Full log below.

```
$ export DOTNET_ROOT="$HOME/.dotnet"
$ export PATH="$DOTNET_ROOT:$PATH"
$ dotnet --version
10.0.400
$ cp local.env.example local.env            # edit ALLEGIANCE_ARTWORK_PATH
$ dotnet build AllegianceCore.slnx
Build succeeded.  0 Warning(s)  0 Error(s)
$ cp src/Allegiance.Server/server.example.json src/Allegiance.Server/server.json
$ dotnet run --project src/Allegiance.Server -- --config src/Allegiance.Server/server.json
[boot] static core 'PCore001.igc' loaded: version=990051329, 10 civilizations, 447 hulls, 143 station types
[boot] transport listening on 0.0.0.0:6073 (all interfaces)
[boot] server ready: 1 mission(s), 20 upd/s (period 50 ms). Ctrl-C to stop.
[shutdown] clean: 223 ticks, 0 overruns
$ dotnet run --project src/Allegiance.ClientCore.MonoGame -- 127.0.0.1 6073 "My Pilot"
[monogame] direct connect: 127.0.0.1:6073 as 'My Pilot' (lobby skipped)
[monogame] shop catalogue: 447 hulls, 117 parts, 123 launchers (56 missile, 8 mine, 6 chaff, 53 probe)
[conn] peer 1 'My Pilot' joined
[logon] 1 'My Pilot' validated=True -> 'Welcome My Pilot' (server time 1235, ship 8098)
[sideselect] -> peer 1 SIDE_ROSTER(211): 2 side(s) [Alpha(0)=0, Bravo(1)=0]
```

The server boots, listens, ticks and shuts down cleanly; the client connects,
logs on, is seated (ship 8098) and shown the side roster — no errors on either
side. (The art clone step used an existing FreeAllegiance/Artwork checkout with
the same layout as `git clone https://github.com/FreeAllegiance/Artwork
../Artwork`.)

## 8. Email / link / hygiene verification

- `git grep -nI "@gmail\|@"` over `README.md`, `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `SECURITY.md`, `docs/GUIDE.md`: **clean**.
- Dead-link check on `README.md` and `docs/GUIDE.md` (relative markdown links
  resolved against the repo): **none**.
- No Meshy assets introduced; the hero shot is native art only.
