# MINER_STRATEGY_P57 — build a real, obedient `Miner` strategy that honors the commander's dispatch orders and mines/sells for real

Date: 2026-09-02. Source-reading diagnosis + managed-C# implementation only —
no VM, no build/test harness reachable from this environment (same constraint
as P50/P52). Every technical claim below is sourced from precise reading of the
code, tagged CONFIRMED / PLAUSIBLE / UNCERTAIN. The change is **unverified,
diagnosis-quality** and left uncommitted for Claude Code's own live-VM pass to
judge, matching P50/P52 precedent.

## 0. Repos verified up front

- `Allegiance` (native C++/C# reference, repo root `/home/domingo/Development/Allegiance`):
  branch `master`, top commit `7cd20c43` (HEAD = `P52: attach CommanderResearchAndExpand on direct-connect when isCommander is set`). `git status` before edits showed pre-existing expected clutter: 12 untracked `TRANSPORT_P*.md`/`BOOT_PATH.md` reports and a **modified-but-uncommitted** `src/Wopr/Wopr/Strategies/ScoutExploreMap.cs` (P50's own live-tuned warp-bounce fix, deliberately left uncommitted, not ours, not reverted).
- `AllegianceCore` (report target root `/home/domingo/Development/AllegianceCore`): contains P50/P52-style reports; this report added here, no product code touched in `AllegianceCore`.

## 1. Dispatch mechanism — confirmed from source (this is the receiving side)

The commander dispatches a docked `.Miner`-named ship via a chat order:

- Trigger: `CommanderResearchAndExpand.UndockMinersAndStartMining`
  (`src/Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:1555-1622`), ships
  found by `GetName().StartsWith(".Miner")` (:1557) that are docked (:1558).
- Order sent via `ClientConnection.SendChat(GetShip(), CHAT_INDIVIDUAL,
  ship.GetObjectID(), 0, "", (sbyte)command, buoy.GetObjectType(),
  buoy.GetObjectID(), buoy, true)` (:1618), where `buoy =
  CreateBuoy((sbyte)BuoyType.c_buoyCluster, 0,0,0, targetCluster.GetObjectID(),
  true)` (:1615) and `command = ship.GetDefaultOrder(buoy)` (:1616).
- `SendChat` signature CONFIRMED from `ClientConnection.h:235-267`: `(pshipSender,
  ctRecipient, oidRecipient, soundID, pszText, cid, otTarget, oidTarget,
  pmodelTarget, bObjectModel)`. So the receiver's `message.cd.oidRecipient` =
  the miner ship's `ObjectID`, `message.cd.commandID` = the `GetDefaultOrder`
  command, `message.otTarget`/`message.oidTarget` = the cluster buoy's
  type/id. That mapping is exactly what the task stated.
- Receiving struct: `FMD_CS_CHATMESSAGE` (`AllMessages.h:1497-1557`) exposes
  `Message`, `cd` (a `ChatDataWrapper`), `oidTarget`, `otTarget` — all
  CONFIRMED.
- Relay (`AllegianceCore`'s `Server.SelectChatTargets`) — explicitly out of
  scope, not touched.

## 2. The two genuinely tricky claims — read against source, honestly

### Claim A: Which `ChatDataWrapper` field means "this order is for me"?

`ChatDataWrapper` (`AllMessages.h:1430-1484`) exposes `sidSender`, `chatTarget`,
`oidRecipient`, `voiceOver`, `commandID`, `bObjectModel`.

- CONFIRMED: `oidRecipient` is the recipient-ship identifier. It is populated
  from the wire chat's recipient object id, which is what the commander sets
  to `ship.GetObjectID()` in the `SendChat` call above. Therefore "meant for
  me" = `message.cd.oidRecipient == ClientConnection.GetShip().GetObjectID()`.
  This matches the existing same-field guard in `StrategyBase.cs:209`
  (`client.SideLeaderShipID(SideIndex) != client.GetShip()?.GetObjectID()` —
  the codebase consistently treats `ship.GetObjectID()` as "self").
- `sidSender` = the commander's ship id (used in the Miner log for context, not
  for matching). `chatTarget` = `ChatTarget.CHAT_INDIVIDUAL`.
- Confidence: CONFIRMED. `oidRecipient` is the right self-match field.
  Rationale reinforced by symmetry with the sender side's own call.

### Claim B: The accessor chain to resolve the destination model from the wire ids

- CONFIRMED: `ClientConnection.GetCore()` returns `ImissionIGCWrapper^`
  (`ClientConnection.h:177`).
- CONFIRMED: `ImissionIGCWrapper` (the "core"/mission object) exposes
  `ImodelIGCWrapper ^ GetModel(ObjectType type, ObjectID id)`
  (`igcWrapper.h:672`).
- Therefore the chain is `ClientConnection.GetCore().GetModel(message.otTarget,
  message.oidTarget)` → the cluster buoy the commander created. This is used in
  the Miner chat handler. This mirrors how the codebase already resolves typed
  objects from the core (`GetShip`, `GetStation`, `GetCluster` on the same
  object); `GetModel` is the generic type+id variant. The commander's buoy is
  visible to the miner's client because it was created with
  `visible=true` (`:1615`) and relayed as the chat's object model.
- Confidence: PLAUSIBLE→CONFIRMED for the accessor chain existing and being
  the correct one. One honest UNKNOWN: whether `GetModel(OT_buoy, buoyID)`
  returns the buoy model reliably from the receiving client's perspective
  (visibility/caching is a runtime concern, not answerable from source) —
  this is exactly what the VM pass must check. If it returns null, the Miner
  already logs the unresolved target rather than throwing.

## 3. New files / edits (scope — nothing else touched)

### `src/Wopr/Wopr/Strategies/Miner.cs` (NEW)
`Miner : StrategyBase`, `StrategyID.Miner`, `TimeSpan.MaxValue`. Behavior:

- `Start()`: logs readiness, does nothing self-directed (an obedient miner
  waits for orders — no self-dispatch; that is the commander's job).
- `AttachMessages`: hooks `FMD_CS_CHATMESSAGE` (orders) and
  `FMD_S_SHIP_STATUS` (periodic fullness check), mirroring
  `ScoutExploreMap.AttachMessages`.
- `MessageReceiver_FMD_CS_CHATMESSAGE`: guards `message.cd.oidRecipient ==
  ship.GetObjectID()`; resolves `target = GetCore().GetModel(otTarget,
  oidTarget)`; issues `ship.SetCommand((sbyte)CommandType.c_cmdCurrent, target,
  (sbyte)message.cd.commandID)` + `ship.SetAutopilot(true)`; logs. Uses the
  exact `SetCommand((sbyte)CommandType..., ..., (sbyte)CommandID...)` idiom
  ScoutExploreMap already uses.
- `MessageReceiver_FMD_S_SHIP_STATUS`: guards `message.shipID ==
  ship.GetObjectID()` (same guard as `ScoutExploreMap.cs:107`); if
  `ship.GetOre() > ReturnHomeWhenOreAbove` (flat `80f` threshold, labeled a
  simplification) and not docked, calls `HeadHome`; `HeadHome` picks the
  nearest friendly station via
  `GetSide().GetStations().Where(side==mine).OrderBy(GetDistanceSquared(ship,p))`
  (the friendly-station idiom from `CommanderResearchAndExpand.cs:1577`) and
  issues a `c_cidGoto`. Docking + auto-sell there is already live-proven
  (P53/P54/P56); this only points the ship at a station.

### `src/Wopr/Wopr/Constants/StrategyID.cs`
Added one enum member `Miner`. CONFIRMED matches the three-member precedent.

### `src/Wopr/Wopr/TeamDirector.cs` (direct-connect ship-assignment block, P52's own fix)
Replaced the single ternary with a name-based branch:
```
bool isMiner = createPlayerParams.playerName.StartsWith(".Miner");
StrategyID botStrategy = isMiner ? StrategyID.Miner
                        : createPlayerParams.isCommander ? StrategyID.CommanderResearchAndExpand
                        : StrategyID.ScoutExploreMap;
```
- `createPlayerParams.playerName` is the property read for the bot name
  (CONFIRMED from `TeamDirector.cs:404-406` usage and the `playerName` we
  attach with).
- Precedence choice, stated explicitly in code and here: **`.Miner`-name check
  wins over `isCommander`.** A ship named `.Miner*` is always a miner, never a
  commander — even if both conditions were somehow set — because the commander
  treats a `.Miner`-named ship as an ore carrier to be dispatched, so attaching
  the self-directing `CommanderResearchAndExpand` would be wrong. The existing
  `isCommander → CommanderResearchAndExpand` behavior is preserved for any
  non-miner ship.

## 4. Important correction to the task's premise (flagged, not papered over)

The task asserted the ship wrapper exposes `GetOreFraction()` at
`igcWrapper.h:1804` and said "Use this for 'am I full, time to head home'
logic — no new wire plumbing needed."

**Reading the source, that is not the ship accessor.** `GetOreFraction()`
(`igcWrapper.h:1804`, impl `igcWrapper.cpp:3836-3838`) lives on the
**asteroid** wrapper `IasteroidIGCWrapper` (declared `:1785-1808`) — it is the
asteroid's depletion fraction, not the ship's cargo fraction.

The ship's cargo is tracked as an absolute ore quantity: `IshipIGCWrapper`
exposes `float GetOre()` (`igcWrapper.h:1055`, impl `igcWrapper.cpp:1956-1959`
→ native `m_instance->GetOre()`), and the native engine accumulates it as it
mines (`shipIGC.cpp:2215-2241`) against a capacity computed as
`c_fCapacityHe3 * miningCapacityAttr`, where `c_fCapacityHe3 = 90.0f`
(`missionigc.cpp:2692,2747`). So a true "fraction full" needs dividing
`ship.GetOre()` by the side's live capacity — which is not exposed on the ship
wrapper directly.

Therefore the Miner uses a **flat `ReturnHomeWhenOreAbove = 80f`** (just under
the nominal 90-unit hold) and labels it a simplification, using
`ship.GetOre()` (the correct ship accessor) instead of `GetOreFraction()`.
This is the honest adaptation: the ship accessor the task pointed at does not
exist; the correct one is `GetOre()`. The flat threshold is explicitly tunable.
Additionally, the engine itself already auto-sends a miner home when full
(`shipIGC.cpp:2219-2231` sends the miner to an unload station via
`FindTarget(...c_sabmUnload)`), so this check is a proactive complement, not
the sole safety net — `ship.GetOre() > 80f` merely gives the bot a head start.

## 5. Confidence per claim

- `oidRecipient` = "for me" → **CONFIRMED** (source + sender-side symmetry).
- Accessor chain `GetCore().GetModel(otTarget, oidTarget)` → **CONFIRMED** to
  exist and be the correct typed lookup; whether it returns the buoy from the
  miner's client at runtime is **UNCERTAIN** (visibility/caching not provable
  from source; null is logged gracefully).
- Mining-with-one-carry-all timing: the autonomous return-to-base precedes any
  miner-side combat/rework because mining coordination (who gets which
  asteroid) and the "return when full" are engine-native (`shipIGC.cpp`); our
  check only adds a client-side early departure. **PLAUSIBLE** that no further
  client logic is needed for opportunistic mining once the miner is near the
  ordered cluster.
- Everything else (enum member, precedence branch, wire fields) → **CONFIRMED**.

## 6. Out-of-scope respected

Not touched: `ScoutExploreMap.cs` (its P50 warp-bounce edit is preserved as the
pre-existing uncommitted change; our diff does not include it), any
`AllegianceCore` file, `CommanderResearchAndExpand.cs` scoring logic, docking,
selling, crowding/queueing/combat. `git diff` for our work = `StrategyID.cs`
(one enum value) + `TeamDirector.cs` (name branch) + new `Miner.cs`; the
`ScoutExploreMap.cs` modification shown in `git status` is P50's and pre-existed.

## 7. What Claude Code's live-VM pass should check (the 6 accept criteria)

1. **Attachment:** a `.Miner`-named ship gets `StrategyID.Miner` (vs a non-miner
   still getting Scout/Commander) on direct-connect — verify the `TeamDirector`
   log line "Ship assigned, attaching Miner strategy."
2. **Receipt+parse:** the miner receives the commander's `FMD_CS_CHATMESSAGE`,
   passes the `oidRecipient==self` guard, logs "Received dispatch order from
   ...", and resolves `target` non-null (Check §4 unknown: `GetCore().GetModel`
   returns the cluster buoy).
3. **Flies there:** the ship's command changes and it actually moves toward the
   ordered cluster (position deltas / real `[ctrl]` frames), warp-crossing as
   needed (reuse the P50 warp-nudge learnings if a warp parks it).
4. **Mines opportunistically once near:** engine-native mining engages because
   the ship is in the cluster near a He3 rock (server-side mechanics already
   proven; only proximity needed).
5. **Detects full and heads home:** `ship.GetOre() > 80` trips the SHIP_STATUS
   check → logs "Hold is full ... heading home" → flies to the nearest friendly
   station (validate the 80 threshold against the live side's capacity;
   raising/lowering is a one-line change if the real capacity differs).
6. **Docking triggers auto-sell:** reaching the station docks and the
   already-proven sell fires (P53/P54/P56), clearing ore.

Also confirm no regression: non-miner bots still route to Scout/Commander.

## 8. Verification limits (honest)

- No VM/build harness here: `Miner.cs` never compiled/run. UNVERIFIED,
  diagnosis-quality code — exactly the P50/P52 posture.
- No claim of build success or runtime success is made.

## 9. Git hygiene

All three edits are **left uncommitted**. In `Allegiance`:
`src/Wopr/Wopr/Strategies/Miner.cs` (new), `src/Wopr/Wopr/Constants/StrategyID.cs`,
`src/Wopr/Wopr/TeamDirector.cs`. P50's pre-existing `ScoutExploreMap.cs`
modification and the report files remain untouched. In `AllegianceCore`, only
this report is added.

## 10. Session stats

```
20260902_181956_eba4b1  start=18:20:04  dur=STILL RUNNING (at write time)  msgs=95 tool_calls=60 api_calls=35  tokens_in=2,161,184 tokens_out=16,392
```
(`python3 ~/Development/allegiance-tools/session_stats.py 20260902_181956_eba4b1`)
