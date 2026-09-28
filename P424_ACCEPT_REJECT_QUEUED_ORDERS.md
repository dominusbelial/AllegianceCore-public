# P424 — Accept / Reject / Clear a received queued order

Real keys for a player's pending (received-but-unaccepted) order, the native
mechanism confirmed real from footage ("you could receive orders and either one
player would accept or ignore them"). The server-side queued/accepted slots and
wire handlers already existed in this port with zero client-facing way to use
them — this stage wires the real Accept/Reject/Clear keys, the real 15 s
auto-reject timer, and an honest minimal test path.

## The keys added (MonoGame client)

Added in `Game1.UpdateFlying` (`src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs`,
P424 block), edge-triggered and composing-gated like every other flight key.
They mirror the native key actions at `WinTrek.cpp:10389-10406`:

| Key | Native TK_ | Action |
|-----|-----------|--------|
| **Home** | `TK_AcceptCommand` / `TK_ExecuteCommand` → `AcceptQueuedCommand(bExecute)` | **ACCEPT**: promote the pending queued order to the accepted slot. |
| **End**   | `TK_RejectCommand` → `RejectQueuedCommand(true, true)` | **REJECT**: clear the pending queued order (decline it). |
| **Delete**| `TK_ClearCommand` → `SetAccepted(NULL, c_cidNone)` | **CLEAR**: cancel whatever order is already accepted (distinct from rejecting a still-queued one). |

Keys chosen because they are unclaimed in flight (grep-verified; `,/./` are the
starmap prev/next keys and `,`/`.` also cycle the shop builder target, so they
were avoided). Registered in `Rendering/KeyBindingTable.cs` under a new
"QUEUED ORDERS (RECEIVED COMMAND)" group; the H help overlay now lists them.

Each key sends a **real `ORDER_CHANGE(93)`** wire message through the existing
`ConnectionSession.SendOrderChange`, reusing the server's real handlers:
- **ACCEPT** → `ORDER_CHANGE {shipID=self, command=CmdQueued, commandID=<pending id>,
  OT_invalid, NA}`. The client echoes the command id it learned from its pending
  order (native `AcceptQueuedCommand`, `WinTrek.cpp:10626-10712`).
- **REJECT** → `ORDER_CHANGE {shipID=self, command=CmdQueued, commandID=CidNone,
  OT_invalid, NA}` (native `RejectQueuedCommand`, `WinTrek.cpp:10583-10623`).
- **CLEAR** → `ORDER_CHANGE {shipID=self, command=CmdAccepted, commandID=CidNone,
  OT_invalid, NA}` — the pre-existing P154 clear-accepted path
  (`Server.ShipCommands.cs`, `AcceptedShipTarget=null; AcceptedShipCommandId=CidNone`,
  `WinTrek.cpp:8834-8836`).

The client learns its pending order via a new **owner-only** wire message,
`QUEUED_COMMAND_UPDATE(215)` (see below), stored in `ClientWorld.MyQueuedCommand`.

## The auto-reject timer (real location: server-side)

Native's 15 s auto-reject is a **client** update-loop check
`if (m_timeRejectQueuedCommand < time) RejectQueuedCommand(false)` at
`WinTrek.cpp:7686-7687`, with `c_dtRejectQueuedCommand = 15.0f` (`:84`) armed in
`SetQueuedCommand` (`:10721`). But this port's real-player queued slot lives
**server-side** (`Ship.QueuedCommandId`), and this MonoGame client is a pure
renderer with no sim — so the timer is the per-tick server pass
`Server.AutoRejectExpiredQueuedCommands`, hooked into `SimPass` as
`Step("queuedCommandAutoReject", ...)` (`src/Allegiance.Server/Server.Broadcast.cs`),
matching where this port already houses per-tick timed behaviours (e.g. ripcord
countdowns). It clears any real-player pending queued order older than
`Ship.QueuedCommandAutoRejectSeconds = 15.0` of sim time — native's genuine
"ignore it" outcome. Anchor: `Ship.QueuedAt` (sim time the order was queued),
cleared on accept/reject so the timer is naturally disarmed (native arms it on
`SetQueuedCommand` and disarms to +20 days in `AcceptQueuedCommand` /
`RejectQueuedCommand`).

## Point 3 — does anything already set another real player's `QueuedCommandId`? (investigation)

**No.** Traced the whole port:
- The only writers of `Ship.QueuedCommandId` are the **NPC return-to-base**
  branch of `HandleOrderChange` (P150, which *clears* it to `c_cidNone`) and
  `ApplyCommandSlot` for NPC station targets — never a real player's pending
  order, and no reader promotes/accepts it.
- The real-player generic fallback (P153/P154) handles only
  `command == CmdAccepted` / `CmdCurrent`; `command == CmdQueued` fell through to
  the logged out-of-scope no-op.
- Native's "give an order to a player" delivery is the **mouse-driven
  Command-console / squadron command UI** (`trekigc.cpp:4875-4887` →
  `CTrekClientWindow::SetQueuedCommand`, `WinTrek.cpp:10713-10727`) — a much
  larger, architecturally different feature already established this session as
  legitimately out of scope.

Per the brief, rather than leave the new keys unverifiable I added a **minimal
test/demo-only path**: `Server.QueueCommandForPlayer(Ship, commandId, Model?)`,
clearly documented as test-only (no gameplay code calls it; it will be superseded
by the real issuing UI). It is the only real-player queued-command writer: it
sets `QueuedCommandId` (+ `QueuedShipTarget`, + `QueuedAt`) on a real player ship
and notifies that player's own client via `QUEUED_COMMAND_UPDATE(215)`. It is
refused for NPCs / non-peers.

To make the keys meaningful the port also delivers the pending order to the
owner's client: a new **owner-only** message `QUEUED_COMMAND_UPDATE(215)`
(`src/Allegiance.Core/Wire/QueuedCommandUpdate.cs`, fixed 10 B: commandID +
objectType + objectID; above native's real FMID max 200 and the port's prior
extensions 201-214, so 215 is free). The MonoGame client decodes it
(`ConnectionSession`) → `ClientWorld.MyQueuedCommand`, the same owner-only
"stream what native's client derives locally" convention P230/P234/P423
established for missile-lock/mining/ore.

## Point 4 — money-request dual-use decision: **scoped out as a separate follow-up**

Native's `AcceptQueuedCommand` / `RejectQueuedCommand` also handle a *pending
money-donation request* from another player when `GetLastMoneyRequest() != 0`
(`WinTrek.cpp:10583-10650`) — accept donates (clamped to what you have), reject
sends the "Negative." reply. Traced against this port:

- The port's money plumbing is real and reused elsewhere (P197 `PlayerWallet`,
  P199 `REQUEST_MONEY(182)` server-forward, P203 `PLAYER_DONATE(67)`,
  `SetMoney(115)`/`MoneyChange(60)`).
- But the pending-money-request state is **never delivered to the recipient's
  MonoGame client**: P199 forwards `REQUEST_MONEY` to the chosen recipient over
  the wire, yet the client has **no `case RequestMoney.Fmid`** anywhere (grepped
  `Allegiance.ClientCore` — zero references). So the recipient client has no
  awareness of a pending money request, no way to distinguish "respond to a money
  request" from "respond to a queued order" on the shared keys, and accepting
  would need to *execute* a real donation (wallet debit + `PLAYER_DONATE`) —
  a genuinely separate state + delivery + action path, not a clean single-plumbing
  reuse. 

Decision: **explicitly out of scope as a separate follow-up**, consistent with
the brief's option B. Documented here, not silently dropped.

## Verification — real proof

**Tests (added):**
- `tests/Allegiance.Core.Tests/AcceptRejectQueuedOrderLiveTests.cs` (7, live-loopback
  against the **real** in-process server over a **real** TCP socket, driving the
  **real** `ORDER_CHANGE(93)` / `QUEUED_COMMAND_UPDATE(215)` wire — the exact
  payloads the MonoGame keys send):
  1. `QueuedCommandUpdate_roundtrips_full_shape` — codec.
  2. `Accept_promotes_queued_to_accepted` — queue via the test helper → owner
     client notified over the wire → ACCEPT promotes action + target into the
     accepted slot and clears the queued slot.
  3. `Reject_clears_the_queued_slot` — ACCEPT untouched.
  4. `Auto_reject_clears_after_15_seconds_of_inaction` — still pending at ~9.5 s,
     cleared past 15 s.
  5. `Accept_with_no_pending_order_is_noop`.
  6. `Reject_with_no_pending_order_is_noop`.
  7. `Accept_echoing_a_different_command_id_is_refused`.
- `tests/Allegiance.ClientCore.Tests/ClientWorldQueuedCommandTests.cs` (3):
  `ClientWorld.Apply(QueuedCommandUpdate)` sets `MyQueuedCommand`; a `CidNone`
  notification clears it; `Apply(IFedMessage)` dispatch reaches the per-type
  overload (the P264 lesson).
- Updated `HelpOverlayTests.Table_HasAllLogicalGroups` (group count 8 → 9 + the
  new group's entries).

**Must-fail-if-removed (proven red, then reverted):**
- Neutralising the auto-reject threshold → `Auto_reject` test reds (Expected -1,
  Actual 5 — still pending past 15 s).
- Neutralising the accept promotion → `Accept_promotes` test reds (Expected 5,
  Actual -1 — accepted not populated).

**Whole-solution Release build:** 0 warnings, 0 errors.

**Full suite, run 3×:** Core.Tests 3518 (only the documented rotating live-wire
timing flakes recur — every one confirmed passing in isolation with zero
references to P424 symbols; the two that fail even in isolation are the two
documented persistent flakes, `LiveTurretGunnerBindingTests` P215 and the
`LiveWinCondition` family P200/P245, both confirmed failing on the **pristine**
tree via `git stash`). ClientCore.Tests 240 (two machine-state live flakes
`ShipDeleteGoneLiveTests` / `JoinFlowLiveTests` confirmed failing on the pristine
tree, unrelated to P424). MonoGame.Tests 826/826.

**Live proof:** the live-loopback tests are the real proof of point 3's flow, all
against the real server + real wire: queue → owner notified via
`QUEUED_COMMAND_UPDATE(215)` → ACCEPT promotes (server log
`[orderchange] ... ACCEPTED own queued order 5 -> accepted slot (target #8099),
queued cleared`), REJECT clears, and true inaction auto-rejects after >15 s
(`[queuedcmd] ship 8098: queued order commandID=5 AUTO-REJECTED after 15s
inaction`). **Honest scope boundary on "the ship acts on it":** this port's
real-player accepted slot (`AcceptedShipCommandId`/`AcceptedShipTarget`) is pure
storage — nothing yet *consumes* it into ship behaviour (it was pure storage
since P154; native drives autopilot from it, which is a separate executor
follow-up). Acceptance demonstrably populates the real accepted state; acting on
it is a documented follow-up, not silently assumed. The MonoGame key handlers are
thin wire-send wrappers whose exact payloads the tests exercise end-to-end.

## Files changed
- `src/Allegiance.Core/Wire/QueuedCommandUpdate.cs` (new, FMID 215)
- `src/Allegiance.Core/Sim/Ship.cs` — `QueuedShipTarget`, `QueuedAt`,
  `QueuedCommandAutoRejectSeconds`
- `src/Allegiance.Server/Server.ShipCommands.cs` — real-player queued-slot
  accept/reject in `HandleOrderChange`; `QueueCommandForPlayer` (test/demo-only);
  `ClearQueuedCommand`; `AutoRejectExpiredQueuedCommands`
- `src/Allegiance.Server/Server.Broadcast.cs` — `Step("queuedCommandAutoReject", …)`
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — receive `case 215`
- `src/Allegiance.ClientCore/State/ClientLiveState.cs` — `QueuedCommandState`
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `MyQueuedCommand` + `Apply`
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — Home/End/Delete keys
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — key latch fields
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs` — new group
- `src/Allegiance.Bot/Bot.cs` — `QueuedCommandUpdates` accumulator
- tests (above)

Committed on `master` (not pushed).
