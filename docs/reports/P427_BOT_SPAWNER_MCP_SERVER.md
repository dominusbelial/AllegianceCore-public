# P427 — Spawn-and-forget MCP server for driving real bots against a live server

**Tool:** `tools/allegiance-mcp` — a stdio MCP server that turns real bot control
into callable tools against an already-running `Allegiance.Server`. A scenario
(hull / loadout / side / behavior) is now one `spawn_bot` call instead of a
code change + rebuild + redeploy cycle.

---

## 1. MCP approach used (and why)

**Used: the official Model Context Protocol C# server SDK**, NuGet package
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
**2.2.0** (Apache-2.0, by the Model Context Protocol project — the C# counterpart
of `@modelcontextprotocol/sdk`). It was selected because:

- It is the **official/canonical** C# SDK (not a community clone).
- It **targets `net10.0`** — the same framework as the rest of this solution.
- The repo already consumes NuGet packages routinely, so adding it is consistent
  with house practice (no new package-infrastructure burden).
- Licensing is permissive (Apache-2.0).

### How it's wired (worth knowing for the next person)

The SDK's documented factory API in this version is **`McpServer.Create(...)` +
`StdioServerTransport(...)` + `RunAsync()`** — there is **no `McpServerFactory`
type in 2.2.0** (I initially assumed `McpServerFactory.CreateAsync` from older
samples; it does not exist in this version, so do not reach for it). The clean
route is:

```
McpServer.Create(
    new StdioServerTransport(serverOptions, loggerFactory: null),
    serverOptions,
    loggerFactory: null,
    serviceProvider: null)
```

I registered the tool surface through the SDK's **low-level
`McpServerHandlers.ListToolsHandler` / `CallToolHandler`** delegates rather than
the `[McpServerTool]` attribute-introspection path, so the tool names,
descriptions and input JSON schemas are **hand-authored exactly** (important for
`spawn_bot`'s arguments) instead of inferred. All diagnostics go to **stderr**, so
the stdout protocol stream stays clean.

> Trade-off note: the SDK couples tool construction to the DI/hosting
> abstractions and its documented surface was thin in spots, but it worked
> cleanly under `TreatWarningsAsErrors` (whole-solution build is 0 warnings /
> 0 errors) and avoids hand-rolling the JSON-RPC protocol. That is the 
> **preferred choice per the task**. A minimal hand-rolled stdio JSON-RPC server
> was the fallback if the SDK fought us; it did not.

## 2. Tool surface

| Tool | Purpose |
|---|---|
| **`spawn_bot`** | Primary, spawn-and-forget. Args: `hull` (name/substring, required), `side` (0/1, optional — auto-picks the probe's side), `parts` (list of part names, optional — defaults to the real weapon/afterburner/cloak/shield loadout), `behavior` (`"idle"` default or `"weave"`). Connects a real `Allegiance.Bot.Bot`, seats it on the side, buys the loadout + launches in one real wire call, and returns a short real confirmation (`shipId`, `hull`, `side`). The connection + behavior loop then run in the background for the life of the MCP server process — the call does **not** block on ongoing behavior. |
| **`list_hulls`** | Read-only catalog of every hull in the real `PCore001.igc` (id + name). |
| **`list_parts`** | Read-only catalog of every part (id + name + equipment category). |
| **`list_active_bots`** | Read-only: every bot this process spawned and is still connected, with live `shipId`, `hull`, `side`, `sector` and `position` read from the probe's real `ClientWorld` (same authoritative source vizfight uses). |

Errors are real and specific: an unresolvable hull/part name, an invalid side, or
a genuine server-side failure returns an `isError` result identifying what was
wrong — never a silent no-op.

### Hull / part name resolution (real-corpus rules)

Because `PCore001.igc` contains **duplicate hull names** (e.g. ten "Adv Miner"
records across tech eras/civs, plus an "Adv Miner [SR]" variant), resolution
follows human-friendly rules (all proven against the real corpus by
`BotSpawnerResolutionTests`):

- **Exact-name hits win** over substring-only hits (so `"Adv Miner"` selects the
  Adv Miner family even though `"Adv Miner [SR]"` also contains it).
- Multiple records sharing **one name string** collapse to the **lowest-HullId**
  representative (same-named variants are one hull).
- Zero hits → `no hull matches '<q>' (known hulls: …)`; several **distinct**
  names → an ambiguity error listing them. Parts resolve identically.

## 3. Known limitations (by design)

- **No `despawn` / `command` / `recall` tool.** Spawn-and-forget means the only
  lifecycle control is killing the MCP server process (which disposes its bots).
  This is the explicitly agreed scope.
- **Behaviors are only `idle` and `weave`** — no combat/mining/rendezvous. A
  future task can add presets onto the same tool now that the MCP plumbing exists.
- **Live-position visibility is same-side.** A client only sees same-side ships
  anywhere plus its own cluster cross-side (the vizfight rule), so `spawn_bot`
  defaults a bot to the probe's side (visible), and `list_active_bots` reads its
  pose from the probe's real world. A bot spawned on the *opposite* side is
  reported with its assigned sector and a zeroed position (honest, documented
  scope) — its own `BotClass.Observations` do not populate for the bot's own ship
  on this server (confirmed by diagnostic), so there is no free cross-side source.
- **No changes to `tools/vizfight`** — this is a separate tool reusing the same
  underlying libraries.

## 4. Live verification (real proof)

Ran against a **real local dev server** (`Allegiance.Server.dll` on
`127.0.0.1:6073`, `PCore001.igc`, "Test Mission" Alpha/Bravo — the same server the
evening's 15v15 attached to), driven **through the MCP stdio protocol** by
`tools/allegiance-mcp/verify_stdio.py` (a scripted client, not a manual human
test). All checks passed:

```
initialize                                   PASS  (protocol negotiated)
tools/list -> [spawn_bot, list_hulls, list_parts, list_active_bots]  PASS
list_hulls -> 447 real hulls (first: Adv Miner id 1)                 PASS
list_parts -> 117 real parts (Weapon/Afterburner/Cloak/Shield/Pack)  PASS
spawn_bot("Adv Miner", weave) -> ok, shipId 8170, hull Adv Miner, side 1  PASS
list_active_bots #1 -> ship 8170 seated (sector 1050, pos [23760.03,730,899.6]) PASS
list_active_bots #2 (4s later) -> pos [23761.37,730,760.4]           PASS
        (position CHANGED -> the weave loop is really running)
spawn_bot("definitely-not-a-real-hull-xyzzy") -> isError, "no hull matches …"  PASS
spawn_bot(real hull + 2 real parts, idle) -> ok                       PASS
```

The **weave loop moving a real ship** (position delta over seconds) and a
**garbage hull returning a real specific error** are the two live requirements and
both demonstrably hold.

- Whole-solution build (`dotnet build AllegianceCore.slnx -c Release`):
  **0 warnings, 0 errors.**
- Full test suite run **twice**: the only failures are pre-existing **live-wire
  timing flakes** (join-flow / ship-gone / connection-order / win-condition /
  turret-binding / warp / cargo etc. real-network timing tests) whose **failing
  set varied run to run** (run 1: 5 Core + 3 ClientCore; run 2: 7 Core + 3
  ClientCore, mostly disjoint) — the signature of timing races under load (the
  box was simultaneously running a live 15v15 server + bots), not deterministic
  regressions. `Allegiance.Lobby.Tests` (12) and
  `Allegiance.ClientCore.MonoGame.Tests` (833) were fully green. The **new**
  `BotSpawnerResolutionTests` (8/8) pass deterministically and fast (no server).

### Resolution unit test
`tests/Allegiance.ClientCore.Tests/BotSpawnerResolutionTests.cs` (8 facts) proves
exact-name hull resolution, no-match errors, ambiguous handling, part resolution,
per-part categories, and the real four-category default loadout — against the real
`PCore001.igc` corpus.

## 5. Starting the server

Requires an already-running `Allegiance.Server` on the target host/port (any of:
`dotnet run --project src/Allegiance.Server`, `docker compose up -d --build`, or
the currently-running dev server). Then:

```
# from the repo root (Developer machine; artwork at $ALLEGIANCE_ARTWORK_PATH
# or the default /home/domingo/Development/Artwork)
dotnet run --project tools/allegiance-mcp -- 127.0.0.1 6073

# or run the built binary directly:
tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp 127.0.0.1 6073
```

On startup it connects one real probe client (logon + seat) to the server before
serving any tool call; startup fails loudly (non-zero exit) if the server is
unreachable. Diagnostics go to stderr; the MCP protocol is on stdin/stdout.

Self-check harness:
```
python3 tools/allegiance-mcp/verify_stdio.py 127.0.0.1 6073
```

## 6. Wiring it into a client (documentation only — not added by this task)

Example Claude Code / IDE `.mcp.json`-style entry (this is Domingo's own local
setup step; no config file was added here):

```json
{
  "mcpServers": {
    "allegiance": {
      "command": "dotnet",
      "args": [
        "run", "--project",
        "/home/domingo/Development/AllegianceCore/tools/allegiance-mcp",
        "--", "127.0.0.1", "6073"
      ]
    }
  }
}
```

Once wired, a caller can e.g.:

```
list_hulls                       # discover hull names
spawn_bot { hull: "Fighter", behavior: "weave" }   # one call, real bot
list_active_bots                 # see it live
```
