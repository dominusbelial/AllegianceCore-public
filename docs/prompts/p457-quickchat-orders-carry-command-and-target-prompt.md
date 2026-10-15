# P457 — quick-chat orders carry their command and target; teammates get a queued order

## Why this task exists

From the 2026-09-29 gameplay-parity pass: **"Attack my target", "Defend
base", "Follow me" and the rest are voice lines only. They order nobody.**
In the original, the quick-chat menu is the main way players and
commanders give orders.

- **Native, sender side:** each quick-chat command carries a command id, a
  target-type mask and an ability mask. They are declared in the shipped
  data, for example
  `attackTargetCommand = QuickChatCommand(attackTargetSonicChat, cidAttack, ttMyTarget, 0)`,
  `defendBaseCommand = QuickChatCommand(defendBaseSonicChat, cidDefend, ttFriendly | ttStation | ttNearest, 0)`
  and `followMeCommand = QuickChatCommand(..., cidGoto, ttMe, 0)`
  (`src/Allegiance.ClientCore.MonoGame/Content/quickchat.mdl`, the
  `QuickChatCommand(` block around lines 704-723; the `tt*` constants are
  near the top of the same file).
- `SendQuickChat(idSonicChat, ttMask, cid, aabm)`
  (`~/Development/Allegiance/src/WinTrek/console.cpp:1534-1600`) resolves
  the target:
  - `cidMyAccepted` → own accepted command.
  - `ttMyAccepted`/`ttMyQueued`/`ttMyTarget` → own command targets.
  - `ttWorstEnemy` → top of own damage track.
  - `ttMe` → own ship.
  - Otherwise: the current target if it is findable under the mask, else
    `FindTarget(ship, ttMask, …, aabm)` for the nearest match.
  - No target → `cid = c_cidNone`.

  It then sends the chat with `cid` plus the target's object type and id.
- **Native, server side:** `ForwardChatMessage` (`FedSrv.CPP:3355-3600`)
  forwards a command chat to same-side player recipients
  (`:3545-3553`). A command chat on **Everyone** is not forwarded
  (`:3385-3391`). Drones get `CommandToDrone` on Individual chats only
  (`:3555-3570`); that half is **P469**.
- **Native, receiver side:** the receiving client turns a command chat into
  its own queued order. `WinTrekClient::ReceiveChat` (`WinTrek/trekigc.cpp:4467+`)
  calls `SetQueuedCommand(pshipSender, cid, pmodelTarget)` (`trekigc.cpp:4860-4890`)
  and plays `newCommandMsgSound` (`trekigc.cpp:~4537-4540`).
- **Port:**
  - The quick-chat parser keeps only the sonic-chat index and caption.
    `CommandDecl` captures just the name
    (`MonoGame/QuickChat/QuickChatMdlParser.cs:52`), `QuickChatSendAction`
    carries no command (`QuickChat/QuickChatMenu.cs:60-73`), and
    `SendQuickChatLeaf` sends voice only (`Game1.Session.cs:564-568`).
  - `ConnectionSession.SendChat` already has `commandId`/`oidTarget`/`otTarget`
    parameters (`ClientCore/Session/ConnectionSession.cs:189-212`), and the
    wire `ChatMessage` carries them (`Core/Wire/ChatMessage.cs:69-72`).
  - The server chat relay (`Server.Dispatch.cs:224+`) ignores `CommandID`.
  - The P424 queued-order machinery (Home/End/Delete accept/reject, the
    15 s auto-reject, `QUEUED_COMMAND_UPDATE(210)`) is real, but its only
    writer is the documented **test/demo-only**
    `Server.QueueCommandForPlayer` (`Server.ShipCommands.cs:1380-1440`),
    whose own doc says "will be superseded by the real issuing flow."
    This task is that issuing flow.
  - The port's queued-order state is **server-authoritative** (P424), so
    the server queues on relay instead of the receiving client queueing.
    Keep that divergence and document it.

## Scope

1. **Parse** `cid`, `ttMask` and the ability mask from each
   `QuickChatCommand(...)` into the quick-chat data model. Resolve the
   `cid*`, `tt*` and `habm*` symbols from the mdl's own constant
   declarations; don't hand-copy numbers.
2. **Resolve the target client-side**, following native `SendQuickChat`:
   - `cidMyAccepted`, `ttMyAccepted`/`ttMyQueued`: from the client's known
     own accepted/queued command (P424 state), where the port has it.
   - `ttMyTarget`: the T-selected target.
   - `ttMe`: own ship.
   - Nearest-family masks (`ttFriendly`/`ttEnemy`/`ttNeutral` ×
     `ttShip`/`ttStation`/`ttWarp`/`ttTreasure`, `ttNearest`, ability mask):
     the current target if it matches, else the nearest matching object in
     the player's own sector. `ttAnyCluster` may widen that; document it.
   - `ttWorstEnemy`: the client has no damage track, so resolve to none
     and document it.
   - No target → `cid = none` (voice only, exactly as today).
3. **Send** the `CHATMESSAGE(23)` with `CommandID` and `OidTarget`/`OtTarget`
   set. The `voiceOver` index and caption stay unchanged.
4. **Server:** when a relayed `CHATMESSAGE` has a legal
   `CommandID != c_cidNone` (native's range check, `FedSrv.CPP:3364-3368`)
   and its target is **not** Everyone:
   - For each **same-side real-player** recipient the relay actually
     delivers to (Team/Wing/FriendlySector/Individual/Leaders), excluding
     the sender, queue the order with the resolved target model through
     the P424 path.
   - Promote `QueueCommandForPlayer` from test-only to the real writer:
     update its doc comment, keep its guards.
   - Ignore a target that doesn't resolve server-side (log it; the order
     becomes targetless or is dropped per native's `pmodel == NULL` rule,
     and you should document which).
   - NPC recipients are untouched here (P469).
5. **Receiver feedback:** play native's new-command sound when a queued
   order arrives, if the port has the asset mapped; otherwise log it as a
   gap. The existing P424 HUD shows the queued order.

## Explicitly out of scope

- **Commanding drones by quick-chat** (`CommandToDrone`, "Affirmative"
  replies, echo to command wing) → **P469**, and the new drone command
  arms (Attack/Defend/Goto/Pickup/Mine/Repair).
- Typed-chat default target and Tab cycling → P456.
- Commander sector-map ordering (picking a sector or rock on the map).
- Any change to the P424 accept/reject keys or the auto-reject timer.
- Alliances.

## Verification

- A loopback wire test with 3 Bots: A and B on side 0, C on side 1.
  - A sends the "attack my target" quick-chat (`cidAttack`, `ttMyTarget`)
    with C as target, to Team.
  - Assert B receives `QUEUED_COMMAND_UPDATE(210)` with `cidAttack` and
    target C, and C receives nothing.
  - **Run against unfixed code first and record the failure** (no 210)
    in the report.
- Everyone-target command chat → nobody gets a queued order.
- Pure tests: the mdl parse yields the right cid/ttMask for
  `attackTargetCommand`, `defendBaseCommand` and `followMeCommand`; target
  resolution per mask, including "no match → cid none".
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings, 0 errors.
- Full suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only documented live-wire flakes are acceptable; name any you see.

## Deliverables

1. The fix and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` first.
2. Report doc `docs/reports/P457_QUICKCHAT_ORDERS_CARRY_COMMAND.md`. Cover
   the pre-fix failure, the per-mask resolution table (implemented versus
   deferred), the server-side queueing divergence and why, and what P469
   still needs.
