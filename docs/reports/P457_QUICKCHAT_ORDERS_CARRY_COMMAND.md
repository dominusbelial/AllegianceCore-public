# P457 — quick-chat orders carry their command and target; teammates get a queued order

Stage: AllegianceCore port task P457 (landed on `master`).
Prior top commit: `d9884ff5 P470: remove the player-flyable Builder (P285), keep native behaviour`.

## Summary

In the original, the quick-chat menu is the main way players and commanders give
orders. Before this task every quick-chat line was **voice-only**: the
`CommandDecl` captured only the sonic-chat index + caption, `QuickChatSendAction`
carried no command, and `SendQuickChatLeaf` sent voice only. This task makes a
quick-chat command carry its **command id and resolved target** on the wire
(`CHATMESSAGE(23)`'s already-existing `CommandID` + `OidTarget`/`OtTarget`), and
makes the **server queue a real P424 queued order** on each same-side real-player
recipient the chat reaches.

Relationship to prior work: P424 built the queued-order machinery
(accept/reject/clear keys, 15 s auto-reject, `QUEUED_COMMAND_UPDATE(215)`) but its
only writer was the documented **test/demo-only** `Server.QueueCommandForPlayer`.
P457 is that "real issuing flow"; the task brief's scope item 4 explicitly names
it: *"Promote `QueueCommandForPlayer` from test-only to the real writer."*

## What the brief's positives said — verified

- **`quickchat.mdl` declares the command block.** Confirmed
  `src/Allegiance.ClientCore.MonoGame/Content/quickchat.mdl:762-821`:
  `attackTargetCommand = QuickChatCommand(attackTargetSonicChat, cidAttack, ttMyTarget, 0)`,
  `defendBaseCommand = QuickChatCommand(defendBaseSonicChat, cidDefend, ttFriendly | ttStation | ttNearest, 0)`,
  `followMeCommand = QuickChatCommand(followMeSonicChat, cidGoto, ttMe, 0)`, and the
  `tt*`/`cid*`/`habm*` scalars at the top (`ttMyTarget = 0x80002`, `cidAttack = 1`,
  `cidDefend = 3`, `cidGoto = 5`, `ttMe = 0x80004`, …).
- **`SendChat` already carries command/target params.** Confirmed
  `ClientCore/Session/ConnectionSession.cs:189-212` and `Bot.cs:1066-1089`; the
  wire `ChatMessage` carries `CommandID`/`OidTarget`/`OtTarget`
  (`Core/Wire/ChatMessage.cs:69-72`).
- **The server chat relay ignored `CommandID`.** Confirmed
  `Server.Dispatch.cs:224-268`.
- **P424's writer was test/demo-only.** Confirmed
  `Server.ShipCommands.cs` (pre-P457 doc: *"will be superseded by the real issuing
  flow"*).
- **Native range check.** `FedSrv.CPP:3364-3368`: a chat is invalid iff
  `commandID < c_cidNone || commandID >= c_cidMax`; `c_cidNone=-1`, `c_cidMax=10`
  (`igc.h:899,912`). A command chat is a *legal* id `!= c_cidNone`.
- **Command chat on Everyone is not forwarded.** `FedSrv.CPP:3385-3391`: in the
  `CHAT_EVERYONE` case native only forwards when `commandID == c_cidNone`.

## Pre-fix failure (recorded)

Running the new wire proof against the **unfixed** code (queueing neutralised — the
`QueueCommandForPlayer` call in the relay commented out):

```
[xUnit] LiveQuickChatOrderWireTests.AttackMyTarget_Team_QueuesOnTeammate_NotSenderOrEnemy [FAIL]
  Assert.Contains() Failure: Filter not matched in collection
  Collection: []
```

The teammate received **no `QUEUED_COMMAND_UPDATE(215)`** — collection empty —
because the relay never queued anything. Restoring the wiring makes it pass.
(The same probe doubles as the "must-fail-if-removed" check; both wire tests are
green with the wiring in place.)

## Implementation

### 1. Parse `cid`/`ttMask`/ability from the mdl's own constants

`QuickChatMdlParser.cs` now:
- Parses the mdl's scalar constant table (`ttFriendly = 0x0001;`, `cidAttack = 1;`,
  `cidNone = -1;`, `habmFighter = 0x80;`) via a new `ConstDecl` regex + `ParseConstants`.
- `ParseCommands` keeps all four `QuickChatCommand(...)` args (sonic, cid, ttMask,
  ability) instead of just the first and third.
- Resolves each command's `CommandId` (single symbol), `TargetTypeMask` and
  `AbilityMask` (bitwise-OR expression) **from the file's own constants** —
  `ResolveSymbol`/`EvaluateExpression` — never hand-copied numbers.

`QuickChatSendAction` (`QuickChatMenu.cs`) gains `CommandId`, `TargetTypeMask`,
`AbilityMask`. The parser sets them at parse time.

### 2. Resolve the target client-side

New pure `QuickChatOrderResolver.cs` ports native `SendQuickChat`'s switch
(`console.cpp:1549-1584`), producing the wire `(cid, oidTarget, otTarget)`:

| Mask (special) | Native | Port |
|---|---|---|
| `ttMyTarget` | `GetCommandTarget(c_cmdCurrent)` | **Implemented** — the T-selected target (`_autoTarget`). |
| `ttMe` | own ship | **Implemented** — own ship id, type Ship. |
| `ttMyQueued` | own queued-command target | **Implemented** (approximation) — the P424 `ClientWorld.MyQueuedCommand` target; the port exposes only the queued state. |
| `ttMyAccepted` | own accepted-command target | **Deferred** — no accepted-command state client-side; degrades to none (documented gap). |
| `ttWorstEnemy` | top of own damage track | **Deferred** — the port has no damage track; degrades to none (documented gap). |
| `cidMyAccepted` | resolves to sender's accepted command id | **Deferred** — no accepted slot client-side; degrades to `c_cidNone` (documented gap). |

| Mask (family) | Native | Port |
|---|---|---|
| `ttFriendly`/`ttEnemy`/`ttNeutral` × `ttShip`/`ttStation`/`ttWarp`, `ttNearest`, ability mask | current target if it matches, else nearest in-sector | **Implemented** — same rule over the player's same-sector candidates (`GatherSectorCandidates`), with the ability gate reading `CandidateObject.RoleBits`. |
| `ttAnyCluster` | widens `FindTarget` past the current sector | **Deferred** — the client gathers same-sector candidates only; a mask with `ttAnyCluster` still searches the own sector (the four real commands carrying it target friendly stations, which the sector pool already covers in normal play). |
| `ttTreasure`/`ttAsteroid`/`ttMissile`/`ttBuoy` type bits | findable in `FindTarget` | **Partial** — ships/stations/warps/asteroids are classified; treasure/missile/buoy are not in the candidate pool (no shipped quick-chat command uses them). |

Rule: **no target → `cid = c_cidNone`** (voice-only), exactly as native
(`console.cpp:1586-1588`). Verified by pure tests.

`SendQuickChatLeaf` (`Game1.Session.cs:568`) now resolves the order and sends the
`CHATMESSAGE` with `CommandID`, `OidTarget`/`OtTarget` set; a voice-only line
sends `CommandID = c_cidNone`, unchanged behaviour for the receiving side.

### 3. The wire no-command sentinel

Normal/typed chats and voice-only quick-chat lines must carry `CommandID =
c_cidNone` (-1), matching native (`SendChatInternal`, `console.cpp:1607-1609`),
so the new server gate (a chat is an order iff `CommandID != c_cidNone`) never
queues an order from a plain chat. The port's shared `SendChat`/`SendChatBuoy`
defaults stay `0` (several byte-exact wire tests pin that value), so the two real
MonoGame send sites pass `commandId` explicitly instead: the **typed-chat** send
passes `OrderChange.CidNone`, and `SendQuickChatLeaf` passes the resolved order's
cid (real cid for an order, `CidNone` for a voice-only line). A legacy caller
still using the `0` default simply sends a `cidDefault` that the server attempts
then drops as an unresolvable target (logged, never queued) — harmless and
documented, since real quick-chat orders are always `cidAttack(1)`..`cidBuild(9)`.

### 4. Server queues on relay (authoritative)

`Server.Dispatch.cs` relay now calls `QueueOrdersFromRelayedChat` after delivering
a chat. It:
- treats a chat as an order iff `CommandID` is legal (`[CidNone, CidMax)`) and
  `!= CidNone`;
- never queues for a `ChatTarget.Everyone` command chat (native does not forward
  it);
- resolves the sender-chosen target server-side from `OtTarget`/`OidTarget`
  (ship via `FindShipAnySide`; station/asteroid/warp via the mission's
  `Sectors[].Stations/Mineable/Warps`);
- drops the order with a log when the target doesn't resolve server-side
  (**choice:** *dropped*, matching native's `pmodel == NULL` → non-command);
- for each same-side real-player recipient the relay delivered to (excluding the
  sender), calls the P424 path. NPC recipients are untouched (**P469**).

`QueueCommandForPlayer` was **promoted** to the real writer: its doc now says so,
its guards (NPC / non-peer / `CidNone`) are retained, and it gained optional
`wireOtTarget`/`wireOidTarget` so a **non-Model** target (station/asteroid/warp —
the port's `QueuedShipTarget` is a `Model?` and cannot hold them) still names the
right object in the client's `QUEUED_COMMAND_UPDATE`. A real Model target (a Ship)
resolves type/id from the model as before.

### 5. Receiver feedback

The existing P424 HUD shows the arriving queued order. Playing native's
`newCommandMsgSound` on arrival (trekigc.cpp:~4537-4540) is a **gap**: the port
does not map that cue in its sound table, so the brief's "otherwise log it as a
gap" branch is taken — noted for a follow-up, no fabricated asset.

## Server-authoritative divergence (and why)

Native queues on the **receiving client**: `ForwardChatMessage` forwards the raw
command chat to same-side players and each client calls
`SetQueuedCommand(pshipSender, cid, pmodelTarget)` locally
(`WinTrek/trekigc.cpp:4860-4890`). This port's queued-order state is
**server-authoritative** (P424) — `Ship.QueuedCommandId`/`QueuedShipTarget` live
server-side and the MonoGame renderer has no sim to queue into. So the server
queues on relay instead, then streams `QUEUED_COMMAND_UPDATE(215)` to the owning
player's client. That divergence is deliberate and documented in the code.

## Tests

- `tests/Allegiance.ClientCore.MonoGame.Tests/QuickChatOrderResolverTests.cs`
  (13 new) — per-mask target resolution, including "no match → cid none",
  nearest-family, ability-gate, `ttMe`, `ttMyQueued`, `ttWorstEnemy`→none.
- `QuickChatMdlParserTests.Parses_cid_and_ttMask_into_the_send_action` (1 new) —
  the mdl parse yields the right `cid`/`ttMask` for `attackTargetCommand`,
  `defendBaseCommand` and `followMeCommand` from the file's own constants.
- `tests/Allegiance.Core.Tests/LiveQuickChatOrderWireTests.cs` (2 new, live):
  - A sends "attack my target" (`cidAttack`, target C) to Team → B receives
    `QUEUED_COMMAND_UPDATE(215)` with `cidAttack` + target C; sender A and enemy C
    receive nothing.
  - Everyone-target command chat → nobody gets a queued order.
  - Pre-fix (queueing neutralised) the first test fails with no 215 (recorded above).

## Build / suite

- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings, 0 errors**.
- New tests pass in isolation (13 resolver + 1 parser in MonoGame.Tests; 2 live wire in Core.Tests).
- The chat/wire family passes in isolation post-fix: LiveChatWire + LiveChatBuoy +
  LiveChatSector + LiveQuickChatOrderWire + AcceptRejectQueuedOrder = 12/12.

### Full-suite note (environmental, not a P457 regression)

Three full-suite runs of `Allegiance.Core.Tests` (3587 tests) were executed. The
failure sets ROTATE run to run (10 → 8 → 13 → 7 → 6 different live tests), none of
them in P457's files, and the one chat test that flapped once (`LiveChatBuoyWireTests`)
was a genuine regression from an over-broad default change that has been fixed and
now passes. The only failures that recur in every post-fix run are:

- `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  — the documented persistent machine-state flake (fails in isolation and on the
  pristine tree; skill's six-flake list).
- `GameOverPlayersWireTests` (2) and `ConquestTerritoryLiveTests` (3) — a persistent
  environmental conquest-win → GAME_OVER broadcast-timing issue on this machine:
  **both reproduce on the PRISTINE tree** (verified by `git stash -u` + rebuild) and
  neither file references any P457 symbol, so they are pre-existing environmental
  failures, not this task's regression.

A fully-clean full suite could not be produced on this machine today because of that
pre-existing conquest-win environmental issue; every failure not in the persistent
set is a rotating live flake (CargoBayReload / AutoReloadOnEmpty / AsteroidMined /
SideElimination / ChatSector / ChatBuoy / WinCondition) that passes in isolation.
This is the same ephemeral-port / timing amplification the skill's landing notes
describe for back-to-back LiveNetwork runs.

## What P469 still needs

P469 ("quick-chat orders to drones") is the sibling half: native's
`CommandToDrone` on Individual chats plus the drone-command arms (Attack/Defend/
Goto/Pickup/Mine/Repair), the "Affirmative"/negative replies and the command-wing
echo. This task deliberately does not touch drones — NPC recipients are skipped in
`QueueOrdersFromRelayedChat` (a `continue`), exactly as the brief's out-of-scope
list requires.
