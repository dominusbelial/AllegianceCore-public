# ALLEGIANCE_LOBBY_STAGE2 (P176) — `Allegiance.Server` opt-in heartbeat client

Branch `master`, on top of `f58f106` (P174, `Allegiance.Lobby` Stage 1). Baseline:
2816 tests in `Allegiance.Core.Tests` (+ 12 in `Allegiance.Lobby.Tests`). This task
adds 5 tests to the existing aggregate `Allegiance.Core.Tests` project → **2821**
(`Allegiance.Core.Tests`) / **2833** total across the solution.

Closes the "zero producers" gap left by Stage 1: a self-hosted `Allegiance.Server`
can now announce itself to an `Allegiance.Lobby` directory via an **opt-in** heartbeat
client. Everything below cites the real current code (verified fresh this session,
not assumed from the prompt).

## What changed

- **`src/Allegiance.Server/ServerConfig.cs`** — new `public sealed class LobbyConfig`
  (`Url`, `DisplayName`, `PublicAddress`, `HeartbeatIntervalSeconds` defaulting to 25)
  and a nullable `public LobbyConfig? Lobby { get; init; }` on `ServerConfig`, default
  `null` — the same nullable-opt-in convention as the existing `CapturePath`
  (`ServerConfig.cs`). No `ApplyCli` entry added (not boot-critical), matching the
  prompt's exclusion.
- **`src/Allegiance.Server/server.lobby-example.json`** (new) — a copy of the real
  `server.json`'s shape plus a `"lobby"` block with placeholder values. The live
  `server.json` was NOT touched (same precedent as `server.example.json` for P175).
- **`src/Allegiance.Server/LobbyHeartbeat.cs`** (new) — the client. One long-lived
  `HttpClient` (~5s timeout) it owns and disposes; self-driven on a
  `System.Threading.Timer`; re-entrancy guarded with a non-blocking
  `SemaphoreSlim(1,1)` `Wait(0)` (overlapping ticks skipped, never queued). Each tick
  POSTs the register/heartbeat payload, stores the returned `Id`/`Token` on success,
  and on a `409` **drops** the token so the next tick re-registers fresh (a rejected
  token means the server-side listing is gone — retrying it would be futile). Every
  failure path (network, timeout, non-2xx, JSON) is caught and logged with a
  `[lobby]` prefix (matching `Server.cs`'s `[...]` console convention), never thrown.
  `Dispose()`/`Stop()` stops the timer and fires one best-effort, short-timeout
  `DELETE /api/servers/{id}?token=…` (token as a **query** param, per the real
  `LobbyEndpoints.cs`) if it holds an id+token, swallowing any failure so it never
  blocks shutdown.
- **`src/Allegiance.Server/Server.cs`** —
  - New `_liveShipCount` field + `LivePlayerCount` property following the existing
    `_statsLock` snapshot pattern (`Server.cs:51`), populated in `SimPass()` in the
    same lock as `_loopIterations++` (`Server.cs:~504`). This is the ONLY thing the
    heartbeat reads from `Server` — it never touches the sim-thread-owned
    `Ships`/`_peerShips`, so there is no cross-thread enumeration hazard.
  - `StartLobbyHeartbeat(core)` called at the end of `Boot()` (`Server.cs`, after
    `_missions = missions;`), gated on `config.Lobby is not null`, constructing the
    client from `() => LivePlayerCount` and the once-resolved payload strings; logs
    `[lobby] heartbeat enabled -> <url>, every Ns`.
  - `Shutdown()` disposes the heartbeat field first (reverse-of-boot: it was
    constructed last in `Boot`), which fires the best-effort deregister DELETE.
- **`README.md`** — short note in the "Artwork/native-source paths" section on
  `server.lobby-example.json` and that the heartbeat is fully opt-in (no `"lobby"`
  block ⇒ no HTTP calls).
- **`tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj`** — added a
  `ProjectReference` to `src/Allegiance.Lobby/Allegiance.Lobby.csproj` and one NuGet
  package (`Microsoft.AspNetCore.Mvc.Testing` 10.0.11) — see Divergence #1.
- **`tests/Allegiance.Core.Tests/LobbyHeartbeatTests.cs`** (new) — boots the real,
  in-memory `Allegiance.Lobby` via `WebApplicationFactory` (see Divergence #2),
  points a real `LobbyHeartbeat` at it, and drives ticks through the internal
  `PumpAsync` seam (deterministic, no wall-clock flakiness):
  1. `First_heartbeat_registers_server_in_directory` — after one tick the server is in
     `GET /api/servers` with the right name / player count / mission / core.
  2. `Second_heartbeat_updates_same_listing_in_place` — a second tick with the stored
     token keeps **exactly one** entry, same `Id`, with the refreshed `PlayerCount`.
  3. `Dispose_deregisters_listing` — disposing the client removes the listing via the
     best-effort DELETE.
  4. `Lobby_null_constructs_no_heartbeat_client` — with `Lobby = null`, reflecting
     `Server._heartbeat` after a real `Boot` confirms no client is constructed
     (⇒ zero HTTP calls ever) — the fully-opt-out default, tested explicitly.
  Plus `LivePlayerCountTests.LivePlayerCount_tracks_joined_peers`: two real bots
  connect over the wire transport and `LivePlayerCount` follows 0 → 1 → 2, proving it
  reads the same value `_peerShips.Count` would give without touching `Ships`.

## Divergences from the prompt's assumptions (verified, not guessed)

1. **"Zero new NuGet dependency" is not achievable for the tests.** `WebApplicationFactory<T>`
   lives in the `Microsoft.AspNetCore.Mvc.Testing` NuGet package — it is NOT part of the
   in-box ASP.NET Core shared framework, and a `ProjectReference` to the Web-SDK Lobby
   project flows only the framework *reference*, not that test helper. So `Allegiance.Core.Tests`
   gained exactly one package reference: `Microsoft.AspNetCore.Mvc.Testing` 10.0.11 — the
   identical package/version `tests/Allegiance.Lobby.Tests` already pins (matching the
   installed 10.0.x AspNetCore runtime). No *other* dependency was added.
2. **`WebApplicationFactory<Program>` is ambiguous in this test project.** Both
   `Allegiance.Server` and `Allegiance.Lobby` emit a global `Program` (top-level
   statements in each), so `global::Program` is CS0433-ambiguous when both are
   referenced. Resolved by hosting against `WebApplicationFactory<Allegiance.Lobby.ServerRegistry>` —
   any public type in the target assembly identifies the app to `WebApplicationFactory`
   equivalently; no change to `Allegiance.Lobby` (which stays out-of-scope).
3. **`Allegiance.Server` does not reference the Lobby assembly, and shouldn't.** The
   prompt's framing (only the *test* project pulls in the ASP.NET Core framework
   reference transitively) implies the client must not depend on the Web-SDK Lobby
   assembly. So `LobbyHeartbeat` speaks the exact wire shape via an anonymous POST
   payload (camelCased by `System.Net.Http.Json`'s Web defaults; the Lobby binds
   case-insensitively, exactly as `tests/Allegiance.Lobby.Tests` proves) and a local
   `public sealed record LobbyRegisterResponse(Id, Token)` mirror. `Allegiance.Server`
   remains a plain (non-AspNetCore-runtime) app.
4. **`Version` payload** — resolved as the static core's version word (`core.Version`,
   an `int`, stringified), which the registry's `Version` field ("a version tag")
   accepts; consistent with what `[boot]` already logs.
5. **Heartbeat interval sanity vs. real defaults.** Confirmed the actual `LobbySettings.cs`
   defaults: `MaxAgeSeconds = 90`, `SweepIntervalSeconds = 30`. The client's 25s default
   thus tolerates ~3 missed beats (90/25) before a live server flaps off the list — within
   the recommended 20–30s band, verified against the real file rather than the prompt's
   paraphrase.
6. **Multi-mission simplification reported explicitly (not silent):** `ServerConfig.Games`
   is a list; the Lobby's `RegisterRequest` has one singular `MissionName`/`CoreFile`.
   Reported fields = the **first** configured game's `Name` (empty if `Games` is empty)
   and the server-wide `StaticCore` as `CoreFile`; `PlayerCount` aggregates across all
   missions via the new `LivePlayerCount`. `MaxPlayers` is sent as `0` (no
   player-cap concept exists in `ServerConfig`; no field invented) — both stated plainly
   in code comments and here.

## Verification

**Build:** `/home/domingo/.dotnet/dotnet build` across the whole solution — **0
warnings, 0 errors** (the Lobby `ProjectReference` + AspNetCore framework reference
flowing into `Allegiance.Core.Tests` produced no new warnings under
`TreatWarningsAsErrors=true`).

**New tests in isolation:** `LobbyHeartbeatTests` (4) green; `LivePlayerCountTests`
(1) green; combined 5-test filter green on two further reliability runs. Reliably clean.

**Discriminating proof (must-fail-if-removed):** with the `_liveShipCount` tick wiring
commented out, the build fails with CS0649 (field never assigned) under
TreatWarningsAsErrors; forcing `_liveShipCount = 0` instead makes
`LivePlayerCount_tracks_joined_peers` fail with `Expected: 1, Actual: 0` — both
demonstrating the test genuinely exercises the new wiring. Wiring restored afterward.

**Three consecutive full-solution runs:**

| Run | Core.Tests | Lobby.Tests | Failures |
|-----|-----------|-------------|----------|
| 1 | 2821 pass | 12 pass | **none (fully green)** |
| 2 | 2820 pass / 1 fail | 12 pass | `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience` — documented flake #4; **green in isolation** |
| 3 | 2820 pass / 1 fail | 12 pass | `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire` — documented flake `LiveCombatWireTests` (known intermittently flaky even in isolation); **whole class green in isolation** |

Every failure across the three runs maps exactly to a documented pre-existing flake
(live-network/concurrent-VM-driven), zero novel failure modes, and each flaked test is
green in isolation. Per the landing criteria this combination is accepted rather than
chasing an indefinitely unbroken full-suite streak.

**Manual smoke check** (local Lobby on `ASPNETCORE_URLS=http://127.0.0.1:5180`; server
run with a `server.lobby-example.json`-shaped config at heartbeat 3s; Lobby expiry
shrunk via `Lobby__MaxAgeSeconds=12` / `Lobby__SweepIntervalSeconds=4` env for a fast
observation):
- Server appeared in `GET /api/servers` within one heartbeat interval, with
  `name=P176 Smoke Server`, `port=6133`, `missionName`, `coreFile=PCore001.igc`,
  `version=990051329`, `maxPlayers=0`; log showed `[lobby] heartbeat enabled ->
  http://127.0.0.1:5180, every 3s` and repeated `[lobby] heartbeat ok -> <same id>`
  (idempotent in-place refresh, no duplicates).
- **Clean `SIGTERM`:** `[shutdown] clean: 448 ticks` logged and the listing went to
  `[]` immediately — the `Shutdown()`-driven deregister DELETE fired. ✓
- **`SIGKILL` (no graceful shutdown):** no `[shutdown]` line; the listing stayed
  present right after the kill, then vanished via the expiry sweep (observed empty
  after the shrunk 12s MaxAge window) — not by a deregister. ✓
  Both teardown behaviours are cleanly distinguishable, proving the heartbeat
  register/refresh/deregister path end to end.

Working tree clean; commit message follows the `P176:` convention.
