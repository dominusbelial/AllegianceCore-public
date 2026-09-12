# ALLEGIANCE_LOBBY_STAGE1 (P174) — `Allegiance.Lobby`: a modern server-directory app

Branch `master`, on top of `c7f6648` (P173, LOADOUT_CHANGE). Baseline: 2816 tests
(`Allegiance.Core.Tests`); this task adds a whole new project + 12 tests → **2828 total**.

Brand-new ASP.NET Core Minimal API server-directory service, architecturally distinct
from the FEDMSG-porting arc (not `BALLOT`/`VOTE`/`TEAM_READY` lobby FEDMSGs — those are
N/A per `LOBBY_SOCIAL_NA_FINDINGS_P129.md`; not the legacy Zone.com/SQL matchmaker). A
game server registers itself, anyone queries who's live. In-memory only, self-service
ownership tokens, no persisted storage by design (plan:
`wiki/pages/allegiance-lobby-app-plan.md`).

## What changed

- **`AllegianceCore.slnx`** — added `src/Allegiance.Lobby/Allegiance.Lobby.csproj` under
  `/src/` and `tests/Allegiance.Lobby.Tests/Allegiance.Lobby.Tests.csproj` under `/tests/`.
  Without this the new project silently never builds/tests (there is no CI today); verified
  the root `dotnet build`/`dotnet test` pick both up.
- **`src/Allegiance.Lobby/Allegiance.Lobby.csproj`** — `Sdk="Microsoft.NET.Sdk.Web"`,
  `net10.0`. Deliberate, justified first use of the Web SDK in the tree (needs the ASP.NET
  Core runtime on the host, not just the base .NET runtime) — see the Divergence note.
- **`Program.cs`** — hand-written top-level statements (no `dotnet new web` scaffold; the
  stock template emits nullable/analyzer noise that `TreatWarningsAsErrors=true` would
  hard-fail). Registers `TimeProvider.System` as a singleton, `ServerRegistry` as a
  singleton built from that clock + `LobbySettings`, `AddRateLimiter()`,
  `AddHostedService<ExpirySweepService>()`; then `UseRateLimiter()`, `UseDefaultFiles()`,
  `UseStaticFiles()`, `MapLobbyEndpoints()`, `Run()`. Ends with `public partial class
  Program;` so `WebApplicationFactory<Program>` can host it.
- **`ServerListing.cs`** — `internal` `ServerListing` record (includes `Token`);
  `public` `ServerListingDto` (same fields minus `Token` — a genuinely separate type, not
  `[JsonIgnore]`, so a future field addition can't leak the token); `RegisterRequest`;
  `RegisterResponse`.
- **`ServerRegistry.cs`** — `ConcurrentDictionary<string, ServerListing>` keyed by Id;
  takes a `TimeProvider` + `maxEntries` (384) + `maxAge` (90s) in the constructor instead
  of sampling `DateTime.UtcNow` itself (mirrors `Mission.Update(now)` in `Allegiance.Core`).
  `Register` (new-vs-heartbeat by token presence; unmatched-token ⇒ reject, never silently
  create; cap rejects only *new* registrations, heartbeats to existing ids never evicted),
  `Deregister` (token-required, conditional remove), `List`, `Get` (filters expired →
  null), `PruneExpired(maxAge)`. One ingest-point hygiene guard (`Sanitize`) caps length
  and strips control chars from every caller-supplied string field.
- **`ExpirySweepService.cs`** — `BackgroundService` whose `PeriodicTimer(interval, time)`
  uses the injected `TimeProvider`; prunes entries older than `maxAge` (defaults 30s / 90s).
- **`RateLimiting.cs`** — in-box `Microsoft.AspNetCore.RateLimiting`, named
  IP-partitioned fixed-window policy on the register/heartbeat endpoint, with the
  reverse-proxy `ForwardedHeaders` warning written as a code comment at the partition point.
- **`LobbyEndpoints.cs`** — `POST /api/servers/register` (rate-limited; 409 when rejected),
  `DELETE /api/servers/{id}?token=…`, `GET /api/servers`, `GET /api/servers/{id}` (404 if
  missing/expired), `GET /healthz`.
- **`wwwroot/index.html` + `app.js`** — static auto-refreshing (15s) table. `app.js` builds
  every row solely via `document.createElement` + `.textContent`; never builds markup from
  server-supplied strings (XSS guard — the one place attacker-controlled text reaches other
  visitors' browsers). Served via `UseDefaultFiles()`/`UseStaticFiles()`.
- **`tests/Allegiance.Lobby.Tests/`** — `LobbyTestApp : WebApplicationFactory<Program>`
  harness plus `RegistrationTests`, `HeartbeatTests`, `ExpiryTests`, `DeregisterTests`,
  `BrowserPageTests` (12 tests total).

## Divergences from the prompt's assumptions (verified, not guessed)

- **`Microsoft.AspNetCore.Mvc.Testing` resolved to 10.0.11** — the highest 10.0.x
  matching the actually-installed `Microsoft.AspNetCore.App` runtime (10.0.10/10.0.11);
  10.0.12 exists on nuget but no 10.0.12 runtime is installed, so 10.0.11 was chosen to
  match the host.
- **The fake-clock package is `Microsoft.Extensions.TimeProvider.Testing`, not
  `Microsoft.Extensions.Time.Testing`.** The latter id does not exist on nuget; the former
  (10.8.0) provides `FakeTimeProvider`. Also: `FakeTimeProvider` does **not** implement
  `IDisposable` in this version — you cannot `using`-declare it (compile error CS1674).
- **`WebApplicationFactory` config-section binding does not reliably reach Program's
  `LobbySettings`.** Overriding `Lobby:*` via `ConfigureAppConfiguration` left the
  registry at the default 90s `MaxAge` (probed: `TimeProvider` override *did* win, config
  did *not*). So `LobbyTestApp` overrides the DI registrations directly
  (`RemoveAll<TimeProvider>/<ServerRegistry>/<IHostedService>` + re-add with the fake clock
  and configured window/cap) — deterministic and ordering-safe. Documented in the harness.
- **The hosted sweep timer is not driven by the fake clock in the HTTP expiry test.** A
  `PeriodicTimer` armed against a `FakeTimeProvider` re-arms its next tick asynchronously
  after each `WaitForNextTickAsync` continuation, so a single `Advance(N)` fires only the
  first due tick — making timer-driven expiry racy. The HTTP expiry test therefore keeps
  the hosted sweep inert (huge interval), advances the fake clock, asserts the detail
  endpoint 404s immediately (see next), then invokes
  `PruneExpired` through the app's own registry instance and asserts the list drops the
  entry. The registry-level `PruneExpired` logic is covered deterministically with the fake
  clock in the companion unit test.
- **`Get`/detail endpoint filters expired entries eagerly** — returns null once
  `LastSeenUtc` is older than `MaxAge`, so the detail endpoint 404s as soon as an entry
  goes stale, *before* any sweep. `List` is intentionally not lazily pruned (lock-free hot
  path); entries linger in the list at most one sweep interval. Matches the prompt's
  "404 if not found/**expired**".
- **`UseDefaultFiles()`** is required before `UseStaticFiles()` to serve `index.html` at
  `/` (bare `UseStaticFiles()` serves `/index.html` but 404s on `/`).

## Environmental note (not caused by this task)

During the verification window, a **separate concurrent process rewrote essentially every
file under `tests/Allegiance.Core.Tests/`** (an "ArtRoot → `TestArtRoot.Value`"
env-overridable artwork-root refactor, adding untracked `TestArtRoot.cs`). That external
change briefly introduced an unrelated compile error in `LiveRankTests.cs`
(`const RanksFile = ArtRoot + "/ranks.txt"` with `ArtRoot` becoming non-const); the same
process self-corrected it shortly after (now `static readonly`). This task made **no**
changes under `tests/Allegiance.Core.Tests/` and deliberately left the external work
untouched (repo-boundary discipline). It does not affect `Allegiance.Lobby`.

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (Core / Server / Bot / Lobby / both test
  projects / all tools; 0 W, 0 E).
- `Allegiance.Lobby.Tests` clean in isolation repeatedly (≥6 runs, incl. 2 back-to-back at
  the end; `--filter FullyQualifiedName~Allegiance.Lobby.Tests`).
- **Full suite** `dotnet test`: run 1 = clean 2816/2816 + 12/12; run 2 = 1 failure
  (`LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`) — a documented
  pre-existing flake, confirmed pre-existing by stashing this task's changes and re-running
  it 6× on the clean baseline (2 pass / 4 fail → genuine flake, not introduced here); run 3
  = clean; fresh run after the external Core.Tests refactor settled = **clean 2816/2816 +
  12/12**. Zero novel failure modes.
- **Manual smoke check** (`dotnet run --project src/Allegiance.Lobby`, `ASPNETCORE_URLS=
  http://localhost:5199`):
  - `GET /healthz` → 200.
  - `POST /api/servers/register` `{name:"Smoke Test",address:"1.2.3.4",port:6073,…}`
    → `{"id":"892adbc1…","token":"250b52c8…"}`; `GET /api/servers` returns it with the
    token absent from the public DTO.
  - Heartbeat with the returned token updated `playerCount` 2→11 on the same id/token,
    single listing (not a duplicate).
  - `DELETE /api/servers/{id}?token=wrong` → 404 (listing intact); with the correct token
    → 204 (listing removed).
  - `/` serves the browser page (contains "Live Servers"), `/app.js` serves.

## What I actually ran

Root build; Lobby tests in isolation (several times); 4 full-suite runs (3 clean, 1 with the
single documented pre-existing `LiveCombatWireTests` flake, plus a clean run after the
external refactor settled); flake-vs-baseline isolation probe (stash task changes, 6× run on
baseline); manual curl smoke check of all endpoints + the browser page.
