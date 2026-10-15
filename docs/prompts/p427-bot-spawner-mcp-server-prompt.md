# P427 — a spawn-and-forget MCP server for driving real bots against a live server

## Why this task exists

This whole session's ad hoc bot-testing (P418-P420's vizfight `attach`
choreography, tonight's 15v15 test) has meant writing/editing C# in
`tools/vizfight/Program.cs`, rebuilding, and redeploying every time a
different scenario was needed (a different hull, a different loadout, a
different side/position). Domingo: *"if is something that will allow us
to spawn any kind of hull/payload on the fly instead of more scripts I
think it could be interesting"* — an MCP server that exposes real bot
control as callable tools, so a scenario is one tool call instead of a
code change + rebuild + redeploy cycle.

Design decision already made (Domingo): **spawn-and-forget**. A `spawn`
call starts a real bot and the caller is done — no returned handle to
track, no required follow-up despawn/command call. The bot simply
persists and does whatever its behavior says for as long as the MCP
server process is running against that server.

## Confirmed real building blocks already in this codebase

Everything needed to actually connect a bot to a real server and give it
a loadout already exists and is proven working (P418-P420, this whole
session's live 15v15 test):
- `Allegiance.Bot`'s `BotClass` (`Connect`, `SendLogonRequest`,
  `SendPositionReq`, `SendBuyLoadout`, `ShipAssignment`) — the exact same
  class `tools/vizfight`'s `AttachBot`/`AttachEnemyBot` already wrap.
- `Allegiance.ClientCore`'s `ConnectionSession`/`ClientWorld` — real wire
  connect/logon/seat/world-state tracking, no MonoGame dependency.
- Real hull/part resolution already solved in `tools/vizfight/Program.cs`:
  `RepresentativeCore()` (loads the real static core), a hull-name lookup
  by substring match (`hullNames`, `HullNameMap()`), and
  `FullInventoryParts(core, weaponPartId)` for a real playable loadout.
  Reuse this exact pattern — don't reinvent hull/part resolution.
- Real side/spawn/launch flow, proven in `AttachMain`: `SendPositionReq`
  (seat on a side), wait for `ShipAssignment`, `SendBuyLoadout(hullId,
  fLaunch: true, parts: ...)` (buy + launch in one real wire call).
- Existing bot behaviors worth exposing as presets rather than inventing
  new ones: the weave/loiter flight pattern (`AttachBot`'s
  `WeavePhase`/`WeaveFreq`/`WeaveAmp` + `AttachEnemyFlyTo`), and idle
  (seated but no movement commands — the simplest real state).

## Scope

1. A new standalone tool, `tools/allegiance-mcp` (a new console project,
   same `net10.0` target as the rest of the solution, referencing
   `Allegiance.Core`/`Allegiance.Bot`/`Allegiance.ClientCore` — mirror
   `tools/vizfight/vizfight.csproj`'s reference shape), implementing the
   MCP stdio protocol (investigate whether an official/community C# MCP
   server SDK package already exists and is reasonable to add — prefer
   that over hand-rolling the protocol; if none fits cleanly, a minimal
   hand-rolled stdio JSON-RPC server covering just the tool-call surface
   below is acceptable, document the choice either way).
2. Server startup: connects to one real, already-running
   `Allegiance.Server` given at startup (host + port args, e.g.
   `allegiance-mcp <host> <port>` — mirror `vizfight attach`'s own
   argument shape), the same way `AttachMain` does, before serving any
   MCP tool calls.
3. Tool: **`spawn_bot`** — the primary, spawn-and-forget entry point.
   Params: `hull` (name or substring, resolved the same way vizfight
   already does), `side` (0/1, or omitted to auto-pick same as
   `PickEnemySide`/first available), `parts` (list of part names,
   optional — default to `FullInventoryParts`'s real playable loadout
   when omitted), `behavior` (`"idle"` or `"weave"` to start; `"weave"`
   reuses the existing weave-flight pattern). Connects a real `BotClass`,
   seats it on the requested side, buys the loadout, launches it, and
   returns immediately with a short real confirmation (ship id, hull,
   side actually assigned) — the connection and any behavior loop then
   run in the background for the life of the MCP server process; the
   tool call does NOT block on the bot's ongoing behavior.
4. Tool: **`list_hulls`** and **`list_parts`** — real, read-only catalog
   lookups (from the same `RepresentativeCore()`/static core this
   already decodes) so a caller can discover valid names instead of
   guessing. Keep these simple (name + id, maybe a one-line category for
   parts) — not a full data dump.
5. Tool: **`list_active_bots`** — read-only: every bot this MCP server
   process has spawned and is still connected, with its real live
   ship id/hull/side/position (from its own `ClientWorld`). This is the
   ONLY visibility into spawned bots this task provides — no command/
   despawn tool (see out-of-scope).
6. Real, minimal error surfacing: an unresolvable hull/part name, an
   invalid side, or a real server-side refusal (e.g. `BuyLoadoutAck`
   refusing the hull) must return a real error message identifying what
   was wrong — never a silent no-op.

## Explicitly out of scope

- Any `despawn`/`command`/`recall` tool — spawn-and-forget means no
  lifecycle management beyond `list_active_bots`'s read-only visibility.
  If a spawned bot needs to go away, killing the MCP server process is
  the only mechanism for now; note this plainly as a known limitation
  rather than silently building more than asked.
- Any behavior beyond `idle`/`weave` (no combat AI, no mining, no
  cross-side rendezvous) — those are real, larger features (see
  P418-P420) and not what this task is for. A future task can add more
  `behavior` presets onto this same tool once the MCP plumbing exists.
- Any change to `tools/vizfight` itself — this is a new, separate tool
  that happens to reuse the same underlying libraries, not a
  modification of vizfight's own attach mode.
- Wiring this MCP server into any specific Claude Code / IDE config file
  — that's Domingo's own local setup step once the server exists and
  works from the command line; document how to invoke it (README section
  or top-of-Program.cs comment) but don't touch any `.mcp.json`/settings.

## Verification — real proof, not just build/test

- A test proving hull/part name resolution works (reuses whatever tests
  already exist against `RepresentativeCore()`'s real corpus — add new
  ones only for genuinely new logic).
- **Live proof, required**: start a real local dev server (`docker
  compose up -d --build` per the existing `docker-compose.yml`, or the
  non-Docker equivalent this repo already documents), start
  `allegiance-mcp <host> <port>`, and drive it via its own stdio protocol
  (a small test harness script or the MCP SDK's own client helper — not
  a manual human test) to: call `list_hulls`/`list_parts` and confirm
  real entries come back; call `spawn_bot` with a real hull + `weave`
  behavior and confirm via `list_active_bots` that a real ship exists,
  is seated, and its position changes over a few seconds (proving the
  weave loop is really running); call `spawn_bot` with a garbage hull
  name and confirm a real, specific error comes back instead of a silent
  failure.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The new `tools/allegiance-mcp` project + its tools, committed on
   `master` (not pushed — Domingo pushes).
2. Report doc `P427_BOT_SPAWNER_MCP_SERVER.md`: which MCP SDK/approach
   was used (or why hand-rolled), the exact tool surface implemented,
   and the live verification performed. Include the exact command to
   start the server and a copy-pasteable example of what invoking it
   from a Claude Code `.mcp.json`-style config would look like (as
   documentation only — don't add the config file yourself, see
   out-of-scope).
