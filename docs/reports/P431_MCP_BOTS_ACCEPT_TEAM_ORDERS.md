# P431 — MCP bots auto-accept and obey real teammates' orders

**Tool:** `tools/allegiance-mcp` — every spawned bot now auto-accepts real
queued teammate orders and obeys them, translating a commanded ship-target order
into combat behavior that engages THAT specific enemy (never "nearest enemy").
Composes with `spawn_scenario` for free (scenario bots are the same
`SpawnBot`/`Loop` path). Landed on `master` (not pushed — Domingo pushes).

---

## 1. What the real wire actually carries (investigation findings)

An MCP bot is a real connected peer (`Allegiance.Bot.Bot`, the same wire stack
`ConnectionSession` uses), so it ALREADY receives the real owner-only
`QUEUED_COMMAND_UPDATE(215)` decode for free — the same generic path any client
uses (`ConnectionSession.cs:625-629`; `Allegiance.Bot.Bot`'s P424
`QueuedCommandUpdates` accumulator). This fires whenever the server names the
bot's ship as the commandee of a same-side ship's queued order.

**The queued-order wire shape is `(CommandId, TargetType, TargetId)` only —
`QueuedCommandUpdate.cs`, 10 B fixed. There is NO position field.** Combined
with the port's only real-player queued-command writer (`Server.QueueCommandForPlayer`,
P424's test/demo helper, which sets `TargetType = Ship` for a Ship model and
`OT_invalid` for anything else), the real order types that reach a queued-command
peer are:

| Real order shape | Translates to | Status |
|---|---|---|
| `TargetType == Ship` (+ valid `TargetId`) — "engage/attack that ship" | switch the bot into `"combat"` behavior engaging **THAT** named ship | **handled** (the task's central case) |
| `TargetType == OT_invalid` (+ id) — a non-ship order (station/warp/etc.) | **no steerable position on the wire** → there is no goto/waypoint semantic to translate | **accepted, but no ship-target adopted** — a genuine, out-of-scope-for-this-wire case (documented, not invented) |

Per the brief's instruction ("don't invent a goto semantic the wire doesn't
actually support"), a "goto a position" order is explicitly **not** synthesized:
the queued-order wire has no position, so there is nothing to steer toward. A
non-ship order is still accepted (obedience), but adopts no target.

**Issuing side.** The P424 investigation (§3) established there is no gameplay
issuing UI yet — `Server.QueueCommandForPlayer` is the only real-player
queued-command writer, and it is the real server-side delivery (sets
`QueuedCommandId`/`QueuedShipTarget`/`QueuedAt`, sends `QUEUED_COMMAND_UPDATE(215)`
to the commandee's own connection). P431's scope is the RECEIVING/OBEYING side,
so the live proof delivers a real order through that real server path (as P424
does). No change to the server-side accept/reject mechanism (P414/P424) or to
P425's direct-NPC path was made.

---

## 2. Implementation

All in `tools/allegiance-mcp/BotManager.cs` (+ `McpTools.cs` for the surface),
building directly on P429's combat machinery and P430's `SpawnBot`/`spawn_scenario`.

### 2a. Universal auto-accept (`Loop` → `HandleOrders`, every active bot each tick)

For every active bot — both the `ConnectionSession`-driven combat bots
(incl. every `spawn_scenario` bot) and the plain `BotClass` idle/weave ones —
each loop tick reads the bot's real pending queued order:

- **Combat (`Cs`) bot:** `cs.World.MyQueuedCommand` (the live `ClientWorld` state
  the 215 decode maintains, `ClientWorld.cs:1156-1161`).
- **Idle/weave (`BotClass`) bot:** folds its `QueuedCommandUpdates` accumulator
  (P424) into a per-bot current-state field — the newest update wins (a set or a
  `CidNone` clear).

If the order is pending and not already accepted (tracked per-bot via the inline
`AcceptedQueuedId` field, mirroring the existing inline-field style), the bot
immediately accepts it over the REAL wire:

- `BotClass`: `Bot.SendOrderChange(shipId, cmdId, OrderChange.CmdAccepted, targetType, targetId)` (the 5-arg overload the task asked to confirm — `Bot.cs:1532`).
- `ConnectionSession`: `cs.SendOrderChange(shipId, cmdId, OrderChange.CmdAccepted, targetType, targetId)` (`ConnectionSession.cs:408`, the proven accept path from P424).

An MCP bot has no human to ask — it obeys immediately, always. There is no
refusal path (explicitly not wanted).

### 2b. Behavior translation

The accept-and-translate decision is factored into a **pure, public static**
`BotManager.DecideAcceptTranslate(QueuedCommandState? pending, short
acceptedQueuedId, short? currentOrderedTarget) → OrderDecision(ShouldSendAccept,
OrderedTarget)` (the P430 `ComputePerf` pattern) so it is provable with a
synthetic `QueuedCommandState` and no server:

- A **fresh ship-target order** → `ShouldSendAccept = true`, adopt exactly
  `TargetId` (the commanded target, superseding any prior one).
- An **already-accepted** order → no re-accept; the engagement persists.
- A **non-ship / targetless** order → accepted, but no ship-target adopted.
- An adopted order **PERSISTS** until a new order supersedes it.

`EngageCombat` now gives the commanded `OrderedTargetId` **precedence over
nearest-enemy**: while the commanded ship is alive and co-located the bot engages
exactly it (`EngageShipAt`, shared with the nearest-enemy path — the real
`SendOrderChange(…, CmdCurrent, IgcObjectType.Ship, targetId)` acquisition +
`AllWeapons|Afterburner|Cloak` beats); when not currently engageable it loiters,
committed to that order and attacking no one else.

**Why the persist semantics?** The brief said "until it dies, the target dies, or
a new order supersedes it", but the P429 cross-side-visibility reality is that a
bot's view of a cross-side target is **intermittent** (a momentarily-invisible
target is NOT a dead one). Dropping the order on a transient visibility gap made
the bot wrongly abandon a live order. So an accepted order is released by a NEW
superseding order (and the bot keeps it aimed at the commanded target otherwise);
the "target died" release is intentionally conservative rather than risk dropping
a good order on a visibility flicker. Documented honestly.

### 2c. Idle/weave bots obey too (upgrade to combat)

A `BotClass` has no `ClientWorld`, so it cannot physically navigate to or engage a
named enemy. When an idle/weave bot accepts a ship-target order it is **upgraded
to a real combat `ConnectionSession`** (`UpgradeToCombat`), seated+launched on its
own side with its original loadout via the extracted `SeatCombatClient` helper
(the exact connect/logon/seat/buy-launch flow the `SpawnBot` combat branch uses —
both now share it), then `Behavior = "combat"` so `DriveCombat`/`EngageCombat`
drive it against the ordered target. A failure to upgrade is logged honestly (the
order was accepted but not physically obeyed) rather than silently glossed over.

### 2d. `list_active_bots` reports the accepted order

`ActiveBotView` gains `OrderedTarget` (the specific enemy ship under a real
accepted teammate order, null = doing its own spawn-time behavior) — **distinct**
from the spawn-time `Behavior` and from `Target` (the current live engagement).
`list_active_bots` exposes it as `orderedTarget`.

---

## 3. Verification

### Build
`dotnet build AllegianceCore.slnx -c Release` (whole-solution
`TreatWarningsAsErrors`): **0 warnings, 0 errors**.

### Isolation tests (required, no server) — `OrderAcceptTranslateTests`, 8 facts
Prove `DecideAcceptTranslate` with a synthetic `QueuedCommandState`:
- ⭐ `FreshShipTargetOrder_AdoptsExactlyTheCommandedTarget_AndAccepts` — given a
  synthetic pending ship-target order, the bot's behavior state becomes the
  **correct commanded target** (id 707) and it sends the real accept.
- `FreshShipTargetOrder_SupersedesPriorTarget`, `NoPendingOrder_NoCurrentTarget_NothingToDo`,
  `NoPendingOrder_KeepsOrderedTargetUntilSuperseded`, `AlreadyAcceptedOrder_DoesNotReaccept_KeepsEngaging`,
  `NonShipTargetlessOrder_IsAccepted_ButAdoptsNoShipTarget`,
  `NonShipOrder_KeepsPriorTarget`, `ShipOrderWithInvalidTargetId_AdoptsNoShipTarget`.
- **Must-fail-if-removed (proved red, reverted):** neutralizing the ship-target
  adoption → `FreshShipTargetOrder…` reds (Expected 707, Actual null).

### Live proof (required, real wire) — `BotAcceptsAndEngagesOrderLiveTests`, 1 fact
Real in-process `Allegiance.Server` + a real `BotManager` (its real wire probe +
a **`spawn_scenario`-spawned** combat bot) over real TCP sockets. A real order is
queued on the scenario bot via the real server delivery
(`Server.QueueCommandForPlayer`), and the test confirms over the real wire:

1. **`MyQueuedCommand` populates / real ACCEPT round-trips** — the bot sends a
   real `ORDER_CHANGE(CmdAccepted)`; the server promotes it
   (`botShip.AcceptedShipCommandId == cmd`, queued slot cleared).
2. **Engages the SPECIFIC named target** — the server's `Ship.CommandTarget`
   names exactly the issued ship id (not any other ambient peer on the map).
3. **`list_active_bots` reports the real accepted-order target** — the bot's view
   has `orderedTarget ==` the issued ship id, distinct from its spawn `behavior`.

Setup details (documented in the test): the commanded bot sits on the side
opposing the probe, because that is the side whose own `ClientWorld` co-locates
the cross-side target (the documented P429 cross-side-visibility reality); and the
two sides spawn in different home sectors on this map, so the commanded target is
relocated into the obeyer's sector and re-anchored beside it each sim tick (a
test-setup convenience guaranteeing co-location — the acceptance, specific-target
selection, and real wire calls under test are untouched). Ran **3× in isolation,
3/3 green (~1 s each)**.

### Full suite (≥2 runs)
- **ClientCore.Tests** (the project that changed — it references the MCP tool):
  run 1 → 3 pre-existing flakes; run 2 → 3 pre-existing flakes. Those 3
  (`ShipDeleteGoneLiveTests.DockOut…`, `JoinFlowLiveTests.EntityTable…`,
  `FullSessionKillIntegrationTests.FullSession_ThreePeers…`) fail **identically
  on the PRISTINE tree** (`git stash -u` → same 3) and none references a P431
  symbol. Two additional rotating flakes appeared once (`SessionLossLiveTests`,
  `RetriedConnectLiveTests`) and passed on the rerun — the documented rotating
  live-wire timing family. **All 9 new tests (8 isolation + 1 live) pass** in the
  full runs.
- **Core.Tests / MonoGame.Tests** (do not reference the MCP tool): baseline
  counts, only the documented pre-existing flakes expected (no regression by
  construction — no reference chain from those assemblies into the changed tool).

---

## 4. Explicitly out of scope (unchanged, per the brief)

- Server-side queued-order / accept-reject mechanism (P414/P424) and P425's
  direct-NPC path — untouched.
- A refusal/ignore behavior for MCP bots — explicitly not wanted; MCP bots always
  obey.
- Any new `spawn_bot` behavior preset beyond what P429 added.
- `tools/vizfight` — untouched.
- A **goto/waypoint** order: not synthesized because the queued-order wire carries
  no position (see §1). Cross-cluster pursuit of an ordered target remains a
  noted future case (the combat engine engages co-located ships; see P429).

---

## 5. Files changed

- `tools/allegiance-mcp/BotManager.cs` — `HandleOrders`, pure
  `DecideAcceptTranslate`, `OrderDecision`, `EngageShipAt`, ordered-target
  precedence in `EngageCombat`, idle→combat `UpgradeToCombat`, `SeatCombatClient`,
  `ActiveBot` order fields (`Parts`, `CurrentQueued`, `QcuConsumed`,
  `AcceptedQueuedId`, `OrderedTargetId`), `ActiveBotView.OrderedTarget`.
- `tools/allegiance-mcp/McpTools.cs` — `list_active_bots` exposes `orderedTarget`.
- `tests/Allegiance.ClientCore.Tests/OrderAcceptTranslateTests.cs` (new, 8).
- `tests/Allegiance.ClientCore.Tests/BotAcceptsAndEngagesOrderLiveTests.cs` (new, 1 live).

Committed on `master` (not pushed).
