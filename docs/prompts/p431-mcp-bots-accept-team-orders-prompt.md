# P431 — MCP bots auto-accept and obey real teammates' orders

## Why this task exists

Domingo: *"could we have the bots accept commands by team members?"* —
a real player (via P425's PageDown/Insert commandee-order flow, or
another bot) should be able to select an `allegiance-mcp`-spawned bot
and issue it a real order, and the bot should obey — an MCP bot acting
as a real, obedient teammate instead of only running its own spawn-time
behavior forever.

This task assumes **P429** (real `"combat"` behavior) and **P430**
(`spawn_scenario`/perf stats) have already landed — build on both
directly; if either has not landed when this is picked up, stop and say
so.

## Confirmed real mechanic already working end-to-end (no server change needed)

An MCP bot is a real connected peer (`Allegiance.Bot.BotClass`, the same
wire stack `ConnectionSession` uses), so it ALREADY receives a real
`QueuedCommandUpdate(?)` decode for free, the exact same generic path
any client uses — this is not MonoGame-specific plumbing:

- `src/Allegiance.ClientCore/State/ClientWorld.cs:44,239-240` —
  `MyQueuedCommand` (a `QueuedCommandState?`), set from
  `Apply(QueuedCommandUpdate)` (`ClientWorld.cs:1156-1159`) whenever the
  server names this ship as the target of another same-side ship's
  queued order (P414/P424's real accept/reject mechanic — the path for
  commanding a real PLAYER-controlled ship, distinct from P425's
  direct-NPC path, and the one that applies here since an MCP bot is a
  real connected peer, not a server-side NPC).
- `src/Allegiance.ClientCore/State/ClientLiveState.cs:18` —
  `QueuedCommandState(short CommandId, IgcObjectType TargetType, short
  TargetId)` — the real pending order's shape.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs:625-629`
  decodes the real wire message into this state automatically on
  receipt — confirm `Allegiance.Bot.BotClass` exposes the equivalent
  (either by sharing this exact code path or its own parallel one; cite
  whichever is true, don't assume).
- **Accepting it** is a real wire call already proven this session:
  `ConnectionSession.SendOrderChange(shipId, commandId,
  OrderChange.CmdAccepted, targetType, targetId)`
  (`ConnectionSession.cs:401-419`, `OrderChange.cs:114`
  `CmdAccepted = 1`) — writes the real `Ship.AcceptedShipTarget`.
  Confirm `BotClass`'s own equivalent `SendOrderChange` overload (cited
  in `ConnectionSession.cs`'s own doc comment as already existing:
  *"the client mirror of `Bot.SendOrderChange(short, short, short,
  IgcObjectType, short)`"*) and use that, not a new send path.

## Scope

1. In `tools/allegiance-mcp/BotManager.cs`'s `Loop()`, for every active
   bot each tick: check its real `MyQueuedCommand`. If one is pending
   and hasn't already been accepted (track this per-bot, mirroring the
   existing inline-field style `ActiveBot` already uses for its other
   per-bot state — don't add a parallel tracking system), immediately
   accept it via the real `SendOrderChange(..., CmdAccepted, ...)` call
   — an MCP bot has no human to ask, it obeys immediately, always
   (that's the whole point of this task; don't add a refusal path
   unless a real legality gate already exists server-side that would
   refuse it anyway — in which case just let the refusal happen
   naturally over the wire, don't pre-guess it client-side).
2. Translate the accepted order into real behavior:
   - **A `CmdCurrent`-style ship-target order** (targeting an enemy
     ship): switch the bot into `"combat"` behavior (P429) engaging
     THAT SPECIFIC commanded target — not "nearest enemy," the actually
     named one — until it dies, the target dies, or a new order
     supersedes it.
   - **A goto/waypoint-style order** (if the real wire shape supports
     naming a non-ship target like a warp/sector — investigate what
     target types P414/P424's queued-order path actually carries and
     handle whatever is real; don't invent a goto semantic the wire
     doesn't actually support): steer the bot toward that real position.
   - Any other real `CommandId` your investigation finds actually
     reaches a queued-command peer: handle it if it's a small, obvious
     addition: otherwise note it as a real, out-of-scope case in the
     report rather than guessing.
3. `list_active_bots` should report whether a bot is currently under a
   real accepted teammate order (and its target, if a ship) alongside
   its existing fields, distinct from its own spawn-time `behavior`
   value, so a caller can see "this bot is doing its own thing" vs
   "this bot is obeying an order right now."

## Must compose with `spawn_scenario` (P430)

Order-acceptance must be a universal property of every active bot's
tick loop, not something wired only into the single-bot `spawn_bot`
path. Since `spawn_scenario` spawns its bots through the exact same
`SpawnBot` call (per P430's own scope), this should compose for free —
but VERIFY it live as part of this task's own proof, don't just assume
it: spawn a real `spawn_scenario` batch (any size), then issue a real
order to one of the scenario-spawned bots specifically (not a
separately, individually spawn_bot-ed one) and confirm it obeys exactly
the same way. If it does NOT compose for free for some real reason
found during implementation, fix that rather than shipping a
"individually-spawned bots obey, scenario bots don't" split.

## Explicitly out of scope

- Any change to the server-side queued-order/accept-reject mechanism
  itself (P414/P424) or to P425's direct-NPC path — both already work,
  this task only makes an MCP bot a real, always-accepting recipient on
  the existing player-to-player path.
- A REFUSAL/ignore behavior for MCP bots (a bot with free will that
  sometimes declines) — explicitly not wanted; MCP bots always obey.
- Any new `spawn_bot` behavior beyond what P429 already added — this
  task is about ACCEPTING orders that override/direct existing
  behavior, not a new spawn-time preset.
- Any change to `tools/vizfight`.

## Verification — real proof, not just build/test

- **Live proof, required**: from a real MonoGame client (a real human
  player, or a second scripted peer if that's more reliable to automate
  — your call, document which), select an MCP-spawned bot as commandee
  (P425's PageDown cycle) and a real enemy ship as target, press Insert,
  and confirm over the real wire: the bot's `MyQueuedCommand` populates,
  the bot sends a real `CmdAccepted` back, and the bot then visibly
  turns to engage the SPECIFIC named target (not just any nearby enemy)
  — confirm via `list_active_bots` showing the real accepted-order
  target matches what was actually issued.
- A test proving the accept-and-translate logic in isolation (given a
  synthetic `QueuedCommandState`, the bot's behavior state changes to
  the correct commanded target) without needing a live server for this
  part.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The order-acceptance + behavior-translation logic, committed on
   `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P431_MCP_BOTS_ACCEPT_TEAM_ORDERS.md`: which
   real order types were found to actually reach a queued-command peer
   and got handled, which (if any) were found but explicitly deferred,
   and the live verification performed.
