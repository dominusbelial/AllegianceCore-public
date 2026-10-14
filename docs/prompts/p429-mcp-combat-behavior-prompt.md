# P429 — a real "combat" behavior for allegiance-mcp's spawn_bot

## Why this task exists

Domingo, after watching tonight's vizfight-driven 15v15 (real cross-side
rendezvous + real gunfire): *"the sequence I saw was very nice, if we
could get to similar scenarios via mcp"* — `allegiance-mcp`'s (P427)
`spawn_bot` only supports `idle`/`weave` today (explicitly deferred at
the time: *"a future task can add more behavior presets onto this same
tool once the MCP plumbing exists"*). This is that task: port the real,
already-proven combat choreography from `tools/vizfight` into a new
`spawn_bot` behavior, so the same kind of scenario is reachable through
MCP tool calls instead of only through vizfight's own hardcoded
15v15 attach flow.

## Confirmed real building blocks already proven working (P418-P420)

Everything needed already exists and is live-verified in
`tools/vizfight/Program.cs` — port/reuse this logic into
`tools/allegiance-mcp/BotManager.cs`'s behavior loop, don't
re-derive it:

- **Cross-cluster rendezvous routing**: `AttachEnemyFlyTo`
  (`Program.cs:2811-2850ish`) + `ClusterGraph.FindShortestPath` (a real
  BFS over the live warp topology from `CLUSTERINFO`) — a bot on one
  side's home cluster can navigate real warps to reach a named
  destination cluster, narrating each hop.
- **Nearest-enemy engagement**: `EngageEnemy` (`Program.cs:2863-2901`)
  — once co-located with the target cluster, finds the nearest ship of
  a different side in its own live `ClientWorld`, points itself at it
  via a real wire call (`AcquireCrossSideTarget`,
  `Program.cs:2679-2680`: `cs.SendOrderChange(myShipId,
  OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship,
  enemyId)`), and flies toward it firing real weapons.
- **Full-inventory combat state**: `ShipControlState.AllWeapons |
  AfterburnerButton | CloakActive` passed to `CommandControls`
  (periodic boost/cloak beats, not held constantly — see the
  `LastBoostAt`/`LastCloakAt` timing pattern in `EngageEnemy`), the
  exact `[ctrl]` combination confirmed live over the wire in P420's
  report.
- Real cross-side hits are NOT suppressed by the friendly-fire gate
  (confirmed live, P420) — this is why the choreography produces real
  kills, not just visual gunfire.

## Confirmed current allegiance-mcp shape to extend

`tools/allegiance-mcp/BotManager.cs`'s behavior loop (`Loop()`, around
line 240-266) currently has exactly two branches: `"weave"` (a
sinusoidal yaw/throttle wander) and the implicit `idle` fallback (no
commands sent at all). `ActiveBot` (the per-bot record, around line
339-358) already carries `WeavePhase`/`WeaveFreq`/`WeaveAmp` for the
weave behavior — a `"combat"` branch needs its own small amount of
per-bot state (a `TargetId`, `LastTargetAt`, `LastBoostAt`,
`LastCloakAt`, and a real-warp `Path`/rendezvous-cluster field —
mirror `AttachEnemyBot`'s exact field shape from vizfight, don't
invent new names/shapes for the same concepts).

## Scope

1. Add `"combat"` as a third valid `behavior` value for `spawn_bot`.
   Extend its JSON schema/description in `McpTools.cs` accordingly.
2. Add one new **optional** `spawn_bot` argument: `target_sector`
   (a real sector NAME or id — resolve names the same tolerant way
   hull/part names already resolve, reusing whatever the existing
   `Catalog.cs` lookup pattern already does for other real-corpus
   lookups). Semantics: a `"combat"` bot without `target_sector`
   engages any different-side ship already visible in its OWN current
   cluster (no travel); WITH `target_sector`, it first navigates real
   warps there (via the same `ClusterGraph.FindShortestPath` routing)
   before engaging. This is what lets a caller reproduce "both sides
   converge on one place and fight" by passing the SAME `target_sector`
   to spawn calls on both sides — the composable primitive Domingo's
   Alderan idea and tonight's Ganymede rendezvous both reduce to.
3. Port `EngageEnemy`'s real find-nearest-different-side-ship-and-fire
   logic into the new `"combat"` branch of `BotManager.Loop()`,
   reusing the exact wire calls (`SendOrderChange` for cross-side
   target acquisition, `CommandControls` with the real `AllWeapons|
   Afterburner|Cloak` combination) — a faithful port, not a
   reinterpretation.
4. `list_active_bots` should report a combat bot's current target
   (ship id, if any) alongside its existing fields, so a caller can see
   engagement happening without needing a second tool.

## Explicitly out of scope

- Any new behavior beyond `combat` (no mining, no station-building,
  no wingman/formation AI) — this task is specifically the P420
  gunfight choreography, ported, not a general AI system.
- Missiles — still genuinely unusable by ANY bot until a separate task
  extends loadout resolution to pull a real `Magazine`-class launcher
  from `core.Launchers` (a distinct, already-identified gap, out of
  scope here).
- Any change to `tools/vizfight` itself.
- A `despawn`/`recall` tool — spawn-and-forget stays the standing
  design; killing the MCP server process remains the only lifecycle
  control (P427's existing, unchanged limitation).

## Verification — real proof, not just build/test

- **Live proof, required**: against a real running dev server, spawn
  one `"combat"` bot on side 0 and one on side 1 with the SAME
  `target_sector`, and confirm via `list_active_bots` (and/or a
  screenshot of vizfight's own web view, if still running alongside,
  though vizfight itself is untouched by this task) that: both
  navigate real warps toward the shared sector, become co-located, and
  a real engagement begins (target field populated, positions closing,
  and ideally a real `KILL_SHIP` event if the fight runs long enough).
- A test proving the `target_sector` name resolves the same tolerant
  way hull/part names already do (exact-over-substring, real error on
  no match).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The `"combat"` behavior + `target_sector` argument, committed on
   `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P429_MCP_COMBAT_BEHAVIOR.md` (reports live
   there now, not the repo root): the exact port made, the
   `target_sector` resolution rules, and the live rendezvous+combat
   verification performed.
