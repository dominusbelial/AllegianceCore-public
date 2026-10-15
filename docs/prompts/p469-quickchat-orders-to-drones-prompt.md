# P469 — quick-chat orders to drones (native CommandToDrone)

**Depends on P457** landing first. P457 makes quick-chats carry
`CommandID` and a target; this task makes drones obey them.

## Why this task exists

Split out of P457 to fit the session window. In the original, a player
orders a drone by selecting it and sending a quick-chat command as an
Individual chat. The drone takes the order at once and answers
"Affirmative."

- **Native:** `ForwardChatMessage`, `CHAT_INDIVIDUAL` to a non-player
  (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3555-3570`), calls
  `CommandToDrone(pfsMission, pcd, otTarget, oidTarget, pdb, pfsSender,
  pshipTo, &pmodel)` (`FedSrv.CPP:3175-3325`).
  - It requires a same-side sender (`:3190-3191`) and sets a 120 s
    runaway cooldown (`:3193`).
  - It resolves the target model, or builds a buoy from `pdb`
    (`:3197-3260`).
  - It checks `pshipTo->LegalCommand(cid, pmodel)` against the per-pilot-
    type command table (`~/Development/Allegiance/src/Igc/shipIGC.h:1560-1600`):
    - Carrier: Default/Goto/Join/DoNothing
    - Miner: + Mine
    - Layer/Builder: + Build
    - Wingman: Default/Goto/Attack/Pickup/DoNothing/Repair
  - It sets the command and replies with a chat (`"Affirmative."` default,
    `pcReply`, `:3196`).
  - On success the order is echoed to the command wing (`:3561-3570`).
- **Port:**
  - Drone commands exist only as direct `ORDER_CHANGE(93)` arms in
    `Server.HandleOrderChange` (`Server.ShipCommands.cs:1054+`): Build via
    `HandleDirectNpcBuildOrder` (P425), docked stay/launch (P148/P152), and
    flying return-to-base (P150). Nothing reads a chat's `CommandID`.
  - The sim already has per-pilot-type behaviour to receive orders: miner
    cycle, layer, wingman attack/defend (P162/P166), builder, and goto
    (P403) (`src/Allegiance.Core/Sim/Ship.cs:~6600-6680`).

## Scope

1. In the chat relay, for an **Individual** (or Group→Individual) chat with
   a legal `CommandID` whose recipient is a same-side **NPC**, run a
   `CommandToDrone` port:
   - `LegalCommand(cid)` per pilot type, from the native table above.
   - Resolve the target model.
   - Map onto the existing Ship command slots/arms:
     - Goto → P403 goto
     - Attack → wingman `AttackTarget`
     - Defend → `DefendTarget` (P166)
     - Mine → miner target
     - Build → the P425 path
     - Default with target → Goto, as native `BaseClient` maps it
       (`clintlib.cpp:2200-2215`)
     - DoNothing → stay/hold
   - Illegal → no-op plus a log.
2. Reply to the sender with `"Affirmative."` (native reply text, Individual
   chat from the drone) on success.
3. Echo the command chat to the command wing as native does, if the
   port's wing routing makes this cheap. Otherwise leave it as a named
   gap.
4. Add Pickup/Repair only if a matching sim arm already exists; otherwise
   list them as unsupported in the report.

## Explicitly out of scope

- The client side (already done in P457), except for making
  quick-chat-to-selected-commandee send an **Individual** chat if P457
  didn't.
- New drone AI behaviours. Only map to arms that already exist.
- Spawning drones (P458/P459).
- Buoy/waypoint creation from `ChatBuoy` beyond what P403 already does.

## Verification

- Loopback: a Bot on side 0 sends an Individual `cidAttack` chat at a
  same-side wingman NPC (spawn one with `SpawnNpcShip(..., droneTypeId: <Wingman>)`),
  targeting an enemy ship. Assert that the NPC's `AttackTarget` is set,
  and that the sender receives "Affirmative." **Record the pre-fix failure.**
- An illegal command (`cidMine` to a wingman) → no change.
- An enemy-side sender → no change.
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings/errors.
  Suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs.

## Deliverables

1. Committed on **`master`**, not pushed.
2. `docs/reports/P469_QUICKCHAT_ORDERS_TO_DRONES.md`: the command → arm
   mapping table, legality table versus native, and unsupported commands.
