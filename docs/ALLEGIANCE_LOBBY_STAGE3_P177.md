# ALLEGIANCE_LOBBY_STAGE3 (P177) — deployment polish: Dockerfiles, docker-compose, CI, LOBBY_APP.md

Branch `master`, on top of `9794f0f` (P176, `Allegiance.Server` opt-in heartbeat
client — confirmed landed before starting). This is a deployment/tooling task: no
feature work in either app except the one log-message fix below. Baseline **2821**
(`Allegiance.Core.Tests`) / **12** (`Allegiance.Lobby.Tests`) = **2833** total, unchanged
by this task (zero test files touched).

Closes the "easy for everyone to run on the internet" goal: containerized deployments for
both apps, a one-command local trial, a first CI workflow (there was none before), and a
durable design-decisions doc. Everything below was verified fresh against the real landed
code and actually executed (not assumed from the prompt).

## What changed

- **`src/Allegiance.Server/Server.cs`** — the **one in-scope behavior fix**: the boot log
  claimed `[boot] transport listening on 127.0.0.1:{port}`, but the transport actually
  binds every interface (`TcpTransport.cs:77`, `new TcpListener(IPAddress.Any, port)`).
  Changed to `[boot] transport listening on 0.0.0.0:{port} (all interfaces)` — a truthful,
  one-line message fix, no behavior change. Confirmed in the container log during the
  compose trial. (Zero test impact: no test asserts on that string.)
- **`.dockerignore`** (new, repo root) — keeps `**/bin/`, `**/obj/`, `.git/`, IDE/os
  clutter out of the build context so a naive `COPY . .` can't bloat the image with
  gitignored-but-not-dockerignored build output.
- **`src/Allegiance.Server/Dockerfile`** (new) — build stage on
  `mcr.microsoft.com/dotnet/sdk:10.0.400` (pinned exact dev SDK); final stage on the plain
  **runtime** image `mcr.microsoft.com/dotnet/runtime:10.0.11` (it's a plain console app,
  `Sdk=Microsoft.NET.Sdk` — no ASP.NET Core needed). Header comment documents the Artwork
  bind-mount contract and that the image ships config-free; `EXPOSE 6073` (documentation
  only — the real port is config-driven); `ENTRYPOINT ["dotnet","Allegiance.Server.dll"]`.
- **`src/Allegiance.Lobby/Dockerfile`** (new) — same pinned SDK build stage; final stage on
  the **ASP.NET Core** runtime image `mcr.microsoft.com/dotnet/aspnet:10.0.11` (Web SDK,
  genuinely different base). `ENV ASPNETCORE_URLS=http://+:8080` (see Divergence #2);
  `EXPOSE 8080`; no volume/config requirements (fully in-memory; `LobbySettings` knobs have
  baked-in defaults).
- **`docker-compose.yml`** (new, repo root) — `lobby` + `server` services; `server`
  bind-mounts the artwork placeholder and a config file; documents that startup order
  doesn't matter (fail-soft heartbeat — confirmed against P176's actual `LobbyHeartbeat.cs`).
- **`docker/compose.server.json`** (new) — the server config for the compose trial:
  `artPath=/artwork`, `lobby.url=http://lobby:8080` (the lobby service's hostname+port on
  the compose network), `port=6073`. Bind-mounted read-only into the container.
- **`docker/artwork-placeholder/README.txt`** (new) — explains the placeholder (populate
  with a small subset, or repoint the volume at a real checkout).
- **`.github/workflows/ci.yml`** (new — `.github/workflows` did not exist) — triggers on
  push/PR to `master`; `setup-dotnet` pinned `10.0.400`; `dotnet build AllegianceCore.slnx`
  (hard gate) then `dotnet test AllegianceCore.slnx --no-build` (**continue-on-error**, see
  the finding below).
- **`LOBBY_APP.md`** (new, unnumbered feature-area doc, per the `BOT.md`/`FEDMSG.md`
  convention) — the durable why-behind-the-design reference: in-memory-vs-SQLite, the
  token ownership model, the `_statsLock`/`LivePlayerCount` thread-safety fix, the
  `ForwardedHeaders` reverse-proxy gotcha (restated from the `RateLimiting.cs` comment),
  the ArtPath volume-mount gotcha, the CI/corpus limitation, and the compose run
  instructions.

## The CI / corpus-testing finding (stated clearly)

On a GitHub-hosted runner there is **no artwork and no native C++ tree**, and
`ALLEGIANCE_ARTWORK_PATH` / `ALLEGIANCE_NATIVE_SRC_PATH` are unset, so their fallback
(`/home/domingo/Development/Artwork`, `/home/domingo/Development/Allegiance`) does not
exist. `Corpus.cs`/`CvhCorpus.cs` therefore throw `DirectoryNotFoundException` when
accessed, failing every corpus-driven test (of which there are dozens across containers,
wire decode, combat, live tests, etc.). This is **not** solved by disabling the tests or
checking the corpus into git (it's large and deliberately unversioned here).

**Choice made: `dotnet build` gates the job unconditionally; `dotnet test` is
`continue-on-error`** with a prominent explanation in the workflow header. Reasoning:

- There is **no trait system** in the suite (grep for `[Trait` returns nothing), and
  inventing one to distinguish corpus-dependent tests is exactly the scope creep the task
  warns against — so the `--filter` route has no clean, non-invented basis.
- `continue-on-error` still **runs** the whole suite and surfaces every result in the log
  (genuine non-corpus regressions remain visible to a reviewer), while a corpus-only
  failure doesn't red the whole PR for reasons unrelated to the contributor's change.
- Net: every contributor gets guaranteed build feedback; test feedback is present but
  best-effort given the missing external corpus.

The admitted weakness: a real non-corpus regression could hide behind the green job because
the step never fails. A proper fix — versioning a small representative corpus subset, or a
CI step that fetches the real corpus — is **explicitly out of scope** here and recorded as
a follow-up (below).

**Follow-ups (not attempted):** (a) check a small representative corpus subset into the
repo so the suite can at least run (would change the 210-file corpus baselines); (b) a CI
job that downloads the real Artwork/NativeSource trees from wherever Domingo keeps them.

## Divergences from the prompt's assumptions (verified, not guessed)

1. **A dedicated compose config file, not a reuse of `server.lobby-example.json`.** That
   example's `artPath=/path/to/Artwork` and `lobby.url=http://127.0.0.1:5147` are both wrong
   for the container (the mount point is `/artwork`, and the lobby is reachable on the
   compose network as `lobby:8080`). So I added `docker/compose.server.json` + a small
   `docker/` dir — a structural addition the prompt's scope implied (point 4 demands a
   bind-mounted config "built from server.lobby-example.json, pointing its lobby.url at the
   lobby service's compose-network hostname") but didn't spell out as a file.
2. **`ASPNETCORE_URLS=http://+:8080` is required in the Lobby Dockerfile.** `Program.cs`
   sets no URL (`app.Run()` with no `UseUrls`/`ASPNETCORE_URLS`), so the framework default
   (`http://localhost:5000`) binds **loopback-only** — useless inside a container. The env
   var in the Dockerfile is the standard .NET-container fix and is deployment-level, not an
   app-code change (stays in scope). Documented in the Dockerfile.
3. **Runtime image pins = `10.0.11`**, matching the actually-installed runtime patch
   (`dotnet --list-runtimes` → Microsoft.NETCore.App / AspNetCore.App 10.0.11) that the SDK
   10.0.400 band ships, and confirmed to exist on Docker Hub (manifest-inspect OK for
   `sdk:10.0.400`, `runtime:10.0.11`, `aspnet:10.0.11`). Chosen over a floating `10.0` for
   reproducibility.
4. **CI test form:** `dotnet test AllegianceCore.slnx --no-build` — validated locally
   (it's the exact form the 3-run verification script used). Build via
   `dotnet build AllegianceCore.slnx`, likewise validated.
5. **`docker compose` (v2, not `docker-compose`)** is what's installed (`Docker Compose
   version v5.5.1`), and the compose stack was actually brought up and exercised (see
   Verification).

## Verification (what actually ran)

Environment had everything: `/home/domingo/.dotnet/dotnet` 10.0.400, Docker 29.8.0,
Docker Compose v5.5.1, and the real Artwork + native Allegiance trees.

- **Build:** `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` → clean, **0 warnings
  0 errors** (all 6 src/tools projects + both test projects).
- **Tests:** 3 consecutive full-suite runs (`dotnet test AllegianceCore.slnx --no-build`):
  - Run 1: **2819/2821** + 12/12 — 2 failures, both in the documented pre-existing flake
    list: `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
    (#4) and `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire` (#2,
    the same flake P174 independently observed). No novel failure modes.
  - Run 2: **2821/2821** + 12/12 clean. Run 3: **2821/2821** + 12/12 clean.
  - Zero test files touched; the log fix has zero test impact.
- **Docker builds** (both from the repo root, `docker build -f …`): Server image OK
  (301 MB) and Lobby image OK (340 MB). **Inspected image contents**: Server `/app`
  contains **no** config file (only the published DLLs + generated `.deps.json`/
  `.runtimeconfig.json`) — the "ship config-free" requirement holds; Lobby `/app` includes
  `wwwroot/` (static assets) as expected.
- **`docker compose up --build`** — brought up both containers against a small real
  artwork subset (copied `PCore001.igc` + `XXV_simple.igc` into the placeholder for the
  trial, then removed them). Verified end-to-end:
  - Server booted off the mounted artwork at `/artwork`: static core loaded, and the log
    shows the fixed line `[boot] transport listening on 0.0.0.0:6073 (all interfaces)`.
  - `[lobby] heartbeat enabled -> http://lobby:8080, every 25s`, then
    `[lobby] heartbeat ok -> <id>, 0 player(s)` — the server announced itself.
  - Lobby on `http://[::]:8080`, `GET /healthz` → 200; `GET http://localhost:8080/api/servers`
    returned exactly one listing (`My Allegiance Server (compose)`, port 6073,
    `coreFile=PCore001.igc`, `version=990051329`), refreshing in place on successive beats
    (same id, no duplicates).
  - `docker compose down` tore down cleanly; the artwork placeholder was restored to
    empty (only `README.txt`), leaving the committed tree clean.
- **CI workflow commands** run locally in the same order the file specifies
  (`dotnet build AllegianceCore.slnx`, then `dotnet test AllegianceCore.slnx --no-build`) —
  both confirmed correct. `docker-compose.yml` validated via `docker compose config --quiet`;
  both YAML files parse clean.

## Out of scope (honestly untouched)

No behavior change to either app beyond the log fix; the CI/corpus gap is **reported, not
fixed** (follow-ups listed above); no image pushed to any registry; no HTTPS/TLS
implemented (documented recommendation: terminating reverse proxy). Working tree clean;
commit message follows the `P177:` convention.
