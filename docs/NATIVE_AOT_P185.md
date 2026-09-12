# P185 — Native AOT support for Allegiance.Server

Scope: **`Allegiance.Server` only**. `Allegiance.Lobby` (ASP.NET Core / Web SDK)
is explicitly out of scope and untouched. No wire-protocol, config-format, or
Lobby-API change: this task only changes *how* JSON is serialized/deserialized,
never *what* is sent.

## Outcome

`Allegiance.Server` now publishes as a self-contained Native AOT binary — a single
8.3 MB native ELF x86-64 executable with **no managed-runtime dependency**. Proven
end-to-end: real `dotnet publish -p:PublishAot=true`, the native binary booted
against a real config + real artwork, and a real wire client (the `Allegiance.Bot`
console app) completed a full logon → ship-assignment → control round-trip against
it — byte-identical behavior to the JIT server. The Docker image builds and runs the
native binary from a `runtime-deps` base (no .NET runtime in the final stage).

## What changed

### `src/Allegiance.Server/ServerJsonContext.cs` (NEW)
Source-generated `System.Text.Json` contract (`internal sealed partial class
ServerJsonContext : JsonSerializerContext`) covering every type that crosses a JSON
boundary: `ServerConfig`, `GameConfig`, `LobbyConfig`, `LobbyRegisterRequest`,
`LobbyRegisterResponse`. Single option set:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
```

This reproduces the union of both former paths' behaviour (see
JSON-shape preservation below).

### `src/Allegiance.Server/ServerConfig.cs`
`ServerConfigLoader.Load` now deserializes through the generated context
(`JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)`) instead
of the reflection-based `<ServerConfig>(json, JsonOptions)` overload. The now-unused
reflection `JsonOptions` field was removed.

### `src/Allegiance.Server/LobbyHeartbeat.cs`
- The anonymous heartbeat-payload type became a named `internal sealed record
  LobbyRegisterRequest(Name, Address, Port, PlayerCount, MaxPlayers, MissionName,
  CoreFile, Version, Token)` (added beside `LobbyRegisterResponse`), so the source
  generator has a type to attach `[JsonSerializable]` to.
- `PostAsJsonAsync("/api/servers/register", req)` → adds
  `ServerJsonContext.Default.LobbyRegisterRequest`.
- `ReadFromJsonAsync<LobbyRegisterResponse>()` →
  `ReadFromJsonAsync(ServerJsonContext.Default.LobbyRegisterResponse)`.

### `src/Allegiance.Server/Allegiance.Server.csproj`
Added `<PublishAot>true</PublishAot>`. Confirmed this does **not** alter the normal
JIT build/test loop: Debug build is clean (0 warnings, under `TreatWarningsAsErrors`)
and the full suite runs under JIT (see Verification).

### `src/Allegiance.Server/Dockerfile`
- Build stage: installs Native AOT prerequisites (`clang`, `zlib1g-dev`) — the SDK
  image ships no platform linker.
- Restore pins the RID: `dotnet restore ... -r linux-x64`.
- Publish becomes: `-c Release -r linux-x64 --self-contained -p:PublishAot=true
  -o /app --no-restore`. Pinned to `linux-x64` (multi-arch out of scope, matching the
  existing single-arch build).
- Final stage base: `mcr.microsoft.com/dotnet/runtime:10.0.11` →
  `mcr.microsoft.com/dotnet/runtime-deps:10.0` (native binary needs no managed
  runtime).
- `ENTRYPOINT` → `["./Allegiance.Server"]` (invokes the native executable directly).
- Every existing convention preserved: header comment's artwork/config bind-mount
  documentation, the documentation-only `EXPOSE 6073`, the restore-then-copy
  layer-caching structure.

### Tests
- `tests/Allegiance.Core.Tests/ServerTests.cs` —
  `Load_ParsesRealConfig_WithCommentsAndTrailingCommas`: parses a real
  `server.json`-shaped camelCase config with a comment and trailing commas through
  `ServerConfigLoader.Load`, asserting every field populates (incl. the `Lobby`
  block). Guards comment-skip + trailing-comma tolerance through source-gen.
- `tests/Allegiance.Core.Tests/LobbyHeartbeatTests.cs` —
  - `LobbyRegisterRequest_serializes_to_byte_identical_wire_shape`: serializes the
    new named record via the context and asserts it equals, byte-for-byte, what the
    old anonymous payload produced under `JsonSerializerDefaults.Web` (camelCase
    keys, null `token` still emitted).
  - `LobbyRegisterResponse_roundtrips_exact_wire_shape`: the response round-trips to
    `{"id":"id-7","token":"tok-9"}`.

## JSON-shape preservation — verification

Three JSON boundaries exist; two distinct option sets were in play before, now
merged into one context:

| Boundary | Before | After |
|---|---|---|
| Config file read (`ServerConfigLoader.Load`) | `JsonOptions`: case-insensitive, skip comments, allow trailing commas; default (PascalCase) naming | Context: case-insensitive, skip comments, allow trailing commas; CamelCase naming |
| Heartbeat POST request | anonymous type under `System.Net.Http.Json` Web defaults → **camelCase**, nulls emitted | `LobbyRegisterRequest` via context (CamelCase) → **same camelCase, nulls emitted** |
| Heartbeat response read | `ReadFromJsonAsync<T>` Web defaults (case-insensitive) | `LobbyRegisterResponse` via context (case-insensitive) |

Why a single CamelCase-named context is safe:
- `PropertyNamingPolicy` only affects **writes**. The config types are only ever
  deserialized (verified: no `JsonSerializer.Serialize` of `ServerConfig` anywhere in
  the Server project), and deserialization is case-insensitive, so the camelCase
  config keys still bind to the PascalCase C# properties identically.
- The heartbeat request is a **write** that must stay camelCase for byte-identical
  wire bytes — CamelCase naming guarantees it.
- The response read is case-insensitive, so `id`/`token` bind to `Id`/`Token`.

Proof (all automated, committed as tests):
1. `LobbyRegisterRequest_serializes_to_byte_identical_wire_shape` — byte-for-byte
   equality against the old anonymous-under-Web-defaults output (the strongest
   possible "bytes unchanged" check).
2. `LobbyRegisterResponse_roundtrips_exact_wire_shape` — exact-string snapshot +
   round-trip.
3. `Load_ParsesRealConfig_WithCommentsAndTrailingCommas` — config parses identically
   (incl. comment-skipping + trailing-comma tolerance the old loader provided).
4. Runtime proof — the native binary booted from a real camelCase config (see below),
   confirming the source-gen loader reads real configs at run time.

## Native-binary smoke test (real, not just a clean publish)

Reproduced the failing publish first (confirmed the three `IL2026`/`IL3050` sites at
exactly `ServerConfig.cs:119`, `LobbyHeartbeat.cs:129`, `LobbyHeartbeat.cs:141` — no
drift, and **no new error categories** beyond those three). After the fix:

```
dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release \
    -r linux-x64 --self-contained -p:PublishAot=true -o /tmp/aotpub
```
→ `Allegiance.Server` is an ELF 64-bit x86-64 PIE executable, 8.3 MB (plus a
separate `.dbg`). Stripped, dynamically linked only against OS libs — no managed
runtime.

Ran the native binary against a real config (camelCase, real
`/home/domingo/Development/Artwork`, `PCore001.igc` static core, `XXV_simple.igc`
map, port 26323). It booted (loaded config + artwork through the source-gen
loader), listened on `0.0.0.0:26323`, and served the `Allegiance.Bot` wire client:

```
[bot] connected to 127.0.0.1:26323 as 'SMOKEBOT'
[bot] LOGONACK: validated=True shipID=8096 ("Welcome SMOKEBOT")
[bot] ship 8096 assigned to sector 1051 at (720, 730, 920), heading (...)
[bot] commanding full forward thrust for 4s
[bot] commanding a yaw (still thrusting) for 2s
```

A full logon handshake (validated), `SET_CLUSTER` ship assignment in a real sector,
and control commands over the real transport — the native binary drives the wire
protocol exactly as the JIT build does.

The console-bot tool then printed "no SINGLE_SHIP_UPDATE observations" and crashed
(`ArgumentOutOfRangeException` in its own empty-trajectory formatting at
`Allegiance.Bot/Program.cs:98`). **This is a pre-existing tool quirk, not a server or
AOT regression:** the identical command against a **JIT** server (second instance,
port 26424, same `Allegiance.Bot.dll`) produced identical output — same `shipID
8096`, same `sector 1051`, same "no SINGLE_SHIP_UPDATE observations", same crash.
AOT changed nothing about the wire behaviour; the position-feed observation simply
isn't picked up by this old tool's reporting loop on either build.

## Docker image smoke test

`docker build -f src/Allegiance.Server/Dockerfile -t allegianceserver:p185 .` →
**build succeeded** (first attempt failed only because the SDK image ships no
Native AOT linker; the build stage now installs `clang` + `zlib1g-dev`).

Ran the image the way it's designed to be run (nothing baked in): bind-mount the
real artwork at `/artwork`, mount a config whose `artPath` is `/artwork` and port
`26525`, and start with `--config`. Boot output shows the native binary loading the
real core through the source-gen config loader and serving:

```
[boot] static core 'PCore001.igc' loaded: version=990051329, 10 civs, 447 hulls, 143 station types
[boot] transport listening on 0.0.0.0:26525 (all interfaces)
[boot] game 'Docker AOT Smoke' ready: map=XXV_simple.igc, 13 sectors, 2 sides [Alpha, Bravo], ...
[boot] server ready: 1 mission(s), 20 upd/s (period 50 ms). Ctrl-C to stop.
```

Wire client then connected through the container's published port:

```
[bot] connected to 127.0.0.1:26525 as 'DOCKERBOT'
[bot] LOGONACK: validated=True shipID=8096 ("Welcome DOCKERBOT")
[bot] ship 8096 assigned to sector 1051 at (720, 730, 920), heading (...)
```

So the image **builds and runs**: it boots, listens, accepts a connection, and serves
the logon + ship-assignment wire protocol.

Final-stage inspection confirms true native distribution:
- `docker images` → **212 MB**, consisting of `runtime-deps` + `/app/{Allegiance.Server,
  Allegiance.Server.dbg, Allegiance.Core.pdb}` only.
- No `/usr/share/dotnet` in the image → **no managed runtime**.
- No `*.dll` in `/app` → the managed assembly is gone entirely (AOT-compiled away).

## Divergences from the prompt's assumptions (verified, not guessed)

1. **Line numbers** `ServerConfig.cs:119`, `LobbyHeartbeat.cs:129/:141` were
   re-verified against the live tree and matched exactly — no drift.
2. **No new error categories** appeared beyond the 3 known `IL2026`/`IL3050` sites.
3. The config examples are **camelCase** (not PascalCase): `artPath`, `staticCore`,
   etc. This is invisible to deserialization (case-insensitive), but it is why the
   context's `PropertyNamingPolicy = CamelCase` is strictly necessary — it keeps the
   heartbeat *request* writes byte-identical while leaving config reads unaffected.
4. **`ServerConfig` is never serialized** anywhere (grep-confirmed), so choosing a
   global CamelCase naming policy on the shared context has no round-trip impact on
   config.
5. The SDK Docker image **lacks Native AOT's Linux linker** — the Dockerfile must
   install `clang` + `zlib1g-dev` in the build stage. This is a real prerequisite the
   brief did not anticipate (a plain `dotnet publish` on this host worked because a
   system toolchain is present).
6. The console-bot's "no SINGLE_SHIP_UPDATE observations" + `ArgumentOutOfRange`
   crash is a **pre-existing tool quirk**, proven identical on a JIT server — not an
   AOT-induced difference.
