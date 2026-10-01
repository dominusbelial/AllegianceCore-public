# P469 — quick-chat orders to drones (native CommandToDrone)

Stage: AllegianceCore port task P469 (landed on `master`).
Prior top commit: `34559959 P457: quick-chat orders carry their command + target; server queues on relay`.
Depends on: **P457** (quick-chats carry `CommandID` + a target), which landed first.

## Summary

In the original, a player orders a drone by selecting it and sending a
quick-chat command as an **Individual** chat; `ForwardChatMessage`'s
`CHAT_INDIVIDUAL` branch detects a **non-player** recipient and hands it to
`CommandToDrone` (`FedSrv.CPP:3557-3573` → `3175-3325`). The drone takes the
order at once and replies **"Affirmative."**.

P457 made command chats carry `CommandID` + `OidTarget`/`OtTarget` and queue a
P424 queued order on **same-side real players**. It explicitly left NPC
recipients untouched (`QueueOrdersFromRelayedChat` skips `IsNpc`, "NPC
recipients -> P469"). This task supplies that half: an Individual-family chat
naming a **same-side NPC** with a legal `CommandID` makes the drone obey
immediately and reply "Affirmative."

Relationship: P424/P457 are the *accept-or-reject* flow for real players (free
will). P469 is the *immediate obedience* flow for drones, mirroring native's
two paths in `ForwardChatMessage`'s `CHAT_INDIVIDUAL` case (player →
forward/queue; non-player → `CommandToDrone`).

## What the brief's positives said — verified (and two corrections)

- **`CommandToDrone` requires a same-side sender and arms a 120 s runaway
  cooldown.** Verified `FedSrv.CPP:3190-3193`: gate `commandID != c_cidNone &&
  sender.GetSide() == drone.GetSide()`, then
  `SetRunawayCheckCooldown(120.0f)`. The runaway cooldown is a **named gap**
  (below): this port has no runaway-return pass, so the 120 s cooldown has no
  consumer.
- **It resolves the target model, or builds a buoy from `pdb`.** Verified
  `FedSrv.CPP:3197-3260`. The buoy-from-`pdb` branch is **out of scope** — the
  P403 `ChatBuoy` path already owns buoy orders — so the drone-chat buoy branch
  is not reproduced (documented, not silent).
- **LegalCommand gate.** Verified `FedSrv.CPP:3252` calls
  `pshipTo->LegalCommand(pcd->commandID, *ppmodelTarget)` — the **two-argument**
  `LegalCommand(cid, pmodel)` at `shipIGC.h:1604-1721`. **Correction to the
  brief:** the brief presented the *one-arg* per-pilot-type **table**
  (`shipIGC.h:1560-1602`, Carrier/Miner/Layer/Builder/Wingman) as the gate, but
  the two-arg method is what `CommandToDrone` actually calls. They agree
  functionally — each two-arg case restricts by pilot type exactly as the table
  does (Attack is Wingman-only, Mine is Miner-only, Build is
  Layer/Builder-only, DoNothing is always legal) — so a Carrier told to attack
  and a Wingman told to mine are illegal under both. This port implements the
  two-arg version (the authoritative gate) and documents both below.
- **On success the order is echoed to the command wing.** Verified
  `FedSrv.CPP:3561-3571` (`ForwardChatMessageToWing` to the command wing +
  Charlie/Delta/Echo wings, `pcd->chatTarget = CHAT_INDIVIDUAL_ECHO` first).
  This is a **named gap** (below): this port has no command-wing routing worth
  replicating for this path.
- **`Default with target → Goto`.** The brief cites the *BaseClient* mapping
  (`clintlib.cpp:2200-2215`), but `CommandToDrone` calls the **server-side**
  `IshipIGC::GetDefaultOrder(pmodel)` (`shipIGC.h:2071-2160`) which is what this
  port implements (base case `c_cidGoto`; Miner/Builder on a capability-matching
  asteroid → Mine/Build; Layer on buoy/warp → Build; Wingman on a friendly
  target → Defend).

## Command → arm mapping

The chat's `CommandID` maps onto an **existing** port sim arm (scope: "Only map
to arms that already exist"). Native sets the drone's `c_cmdAccepted` slot to
whatever the command is; this port has per-purpose sim slots instead, so each
legal command lands on the slot whose behaviour already exists.

| CommandID | Native | Value | Port arm | Behaviour |
|---|---|---|---|---|
| `c_cidGoto` | Goto | 5 | `Ship.SetGoto` (**P403**) | waypoint at the target's position/sector (the P403 goto objective) |
| `c_cidAttack` | Attack | 1 | `Ship.AttackTarget` (**P162**) | wingman engages the named enemy |
| `c_cidDefend` | Defend | 3 | `Ship.DefendTarget` (**P166**) | wingman protects the named charge |
| `c_cidMine` | Mine | 8 | `Ship.MiningAsteroidTarget` (**P156**) | miner heads to the named rock |
| `c_cidBuild` | Build | 9 | **P425** `StartBuildIfLegal` | direct-NPC-build starts timed construction |
| `c_cidDoNothing` | DoNothing | -3 | `Ship.StayDocked = true` (**P148**) | stay/hold |
| `c_cidDefault` | Default | 0 | → `GetDefaultOrder` → Goto/Mine/Build/Defend | resolved from the target before legality |

## Legality versus native

The port implements native `LegalCommand(cid, pmodel)` (`shipIGC.h:1604-1721`),
the gate `CommandToDrone` actually runs, with target-null/self/foreign-mission →
DoNothing-only, the `bFriendly` test (`shipIGC.h:1621`, same-side-or-allied via
the P203 `AlliedSides`), and per-case pilot-type + object-type + capability
checks.

The one-arg per-pilot-type **table** (`shipIGC.h:1559-1602`) and the port's
behaviour:

| Pilot type | Table allows | Port behaviour (via two-arg cases) |
|---|---|---|
| Carrier | Default/Goto/Join/DoNothing | same (Attack/Mine/Build rejected) |
| Miner | + Mine | same |
| Layer | + Build (buoy/warp) | same |
| Builder | + Build (asteroid) | same |
| Wingman | Default/Goto/Attack/Pickup/DoNothing/Repair | same |

## Unsupported commands

These are legal in native (allowed for some pilot type) but this port has **no
matching sim arm** — the drone is *not* commanded (logged, no "Affirmative."):

- **`c_cidJoin` (7)** — join the target's wing (`FedSrv.CPP:3259-3274`; the
  port's wing membership is nominal). No arm.
- **`c_cidPickup` (4)** — rescue/lifepod pickup. The rescue *mechanic* exists
  but no "a commander orders a specific pickup" slot does. No arm.
- **`c_cidRepair` (6)** — repair a friendly ship/station. No arm.
- **`c_cidCapture` (2)** — capture a station; native is **player-only**
  (`m_pilotType >= c_ptPlayer`, `shipIGC.h:1651`), so no drone could ever carry
  it — declared for the table, never legal for an NPC.

Native would still set the accepted slot and reply "Affirmative." for these (it
always does on any legal command); the port does not, because setting a slot it
cannot act on would be a lie. This is an honest scope boundary (report-only per
the brief), not a silent omission.

## Implementation

Added to `Server.ShipCommands.cs` (after the P457 `ResolveChatOrderTarget`) and
wired into the relay in `Server.Dispatch.cs`:

1. **`TryCommandToDrone(from, sender, chat)`** — entry from the relay: filters to
   the Individual-family targets (`Individual`/`IndividualNoFilter`/`Group`/
   `GroupNoEcho`; `IndividualEcho` is server-generated and skipped), applies the
   native range gate (`FedSrv.CPP:3364-3368`), resolves the named recipient
   (`CFSShip::GetShipFromID`, `FedSrv.CPP:3541`), requires an **NPC**, applies
   the same-side + non-`c_cidNone` gate (`FedSrv.CPP:3190-3191`), then calls
   `CommandToDrone`. A real-player recipient is untouched (the P457 path owns
   those).
2. **`CommandToDrone(from, sender, drone, chat)`** — resolves the target model
   via P457's `ResolveChatOrderTarget`; resolves `c_cidDefault` via
   `GetDefaultOrder`; runs `LegalCommand`; `SetStayDocked(false)` (Xynth #48,
   `FedSrv.CPP:3254`); maps onto an arm via `ApplyDroneCommand`; on success
   replies `"Affirmative."` to the commander (`SendDroneAffirmative`,
   `FedSrv.CPP:3256-3257`).
3. **`LegalCommand`** — the two-arg native gate (above).
4. **`GetDefaultOrder`** — the server-side `IshipIGC::GetDefaultOrder`.
5. **`ApplyDroneCommand`** — the arm mapping table; returns `true` only when an
   arm was actually set. `Build` reuses the **P425** `StartBuildIfLegal` (now
   returns `bool` — a non-breaking refactor of its existing `void`; both P422
   and P425 callers ignore the return).
6. **`SendDroneAffirmative`** — the "Affirmative." Individual chat from the
   drone, using the same private-chat construction `RefuseBuild` already uses.

Wire constants: added `OrderChange.CidDefault(0)/CidCapture(2)/CidPickup(4)/
CidRepair(6)/CidJoin(7)/CidMine(8)` (native `igc.h:901-909`) — the existing set
had only `CidDoNothing/-3, CidNone/-1, CidAttack/1, CidDefend/3, CidGoto/5,
CidBuild/9, CidMax/10`.

## Named gaps (deliberately not implemented)

- **120 s runaway cooldown** (`FedSrv.CPP:3193`): this port has no
  runaway-return pass — a commanded drone never autonomously abandons a player
  command to return to base — so the cooldown has no consumer.
- **Command-wing echo** (`FedSrv.CPP:3563-3571`): echoing the accepted order to
  the command wing + Charlie/Delta/Echo wings is not replicated (no command-wing
  routing worth reusing here).
- **Buoy-from-`pdb` construction** (`FedSrv.CPP:3199-3208`, 3224-3233): the P403
  `ChatBuoy` path already owns buoy orders, so the drone-chat buoy branch is not
  reproduced.
- **Miner remembered queued buoy** (`FedSrv.CPP:3295-3321`): native stores the
  commanded location in `c_cmdQueued` for a miner to return to; out of scope
  (no such read-back exists in the port).

## Verification

- **Loopback (primary):** a Bot on side 0 sends an Individual `cidAttack` chat
  at a **same-side Wingman NPC** (spawned with `SpawnNpcShip(..., droneTypeId:
  <Wingman>)`), targeting an enemy ship. Asserts the Wingman's
  `AttackTarget == enemy` **and** that the sender's client receives the drone's
  `"Affirmative."`. Wingman + enemy in the same sector so the set `AttackTarget`
  is not dropped by the Wingman's same-sector-only pursuit validation.
- **Illegal:** `cidMine` to the same Wingman (a Wingman cannot mine) → no slot
  change, no `"Affirmative."`.
- **Enemy-side sender:** a sender on the *other* side's Individual `cidAttack`
  at the drone → ignored (same-side gate), no `"Affirmative."`.

**Pre-fix failure (recorded):** with the `TryCommandToDrone` wiring in the relay
commented out, the primary test fails —

```
[xUnit] LiveQuickChatToDroneCommandTests.Individual_Attack_OrdersSameSideWingman_AndAffirms [FAIL]
  Assert.True() Failure: the commanding player's client must receive the drone's "Affirmative."
```

(The drone never replies — the order path is dead. The "Affirmative." reply can
only be produced by `CommandToDrone`, so it is the must-fail-if-removed
discriminator; an idle Wingman could otherwise autonomously pick the nearest
in-sector enemy via P162, so `AttackTarget` alone is not a clean discriminator.)
Restoring the wiring makes all three tests pass.

New tests: `LiveQuickChatToDroneCommandTests` (3 live wire proofs).

## Regression

- Related suites (P457 `LiveQuickChatOrderWireTests`, `NpcWingmanAttackLiveTests`,
  `NpcWingmanDefendLiveTests`, `DirectNpcBuildOrderLiveTests`, plus the new
  class): **13/13 pass**.
- Full suite: 3590 total (see the failure ledger — only the documented flakes;
  none reference P469 symbols).
- Whole-solution build: **0 warnings / 0 errors**.
