# ORDER_CHANGE(93) — P153: the generic non-NPC path (a real player locks a target)

Branch: `master` (top: 54d6e29 P152, this task adds the P153 commit on top).
Scope: the final, previously no-op `else` in `Server.HandleOrderChange` plus a
new cross-side ship resolver. No Windows VM involved.

## What this task does

The real client's `SetTarget` UI action (`WinTrek.cpp:8820-8852`) — the
everyday "lock onto this target" used constantly during play, e.g. selecting an
enemy ship — sends `ORDER_CHANGE(93)` with `command = c_cmdCurrent` and the
selected target's `objectType`/`objectID`. The native generic fallback for a
real player's own ship is:

```cpp
// FedSrv.CPP:7335-7339
if (pside->GetObjectID() >= 0 && !DoNotExecute)
{
    pmodel = pMission->GetModel(pfmOC->objectType, pfmOC->objectID);
    pship->SetCommand(pfmOC->command, pmodel, pfmOC->commandID);
}
```

`DoNotExecute` is only set `true` in the NPC (`pilotType < c_ptPlayer`) branch
(`FedSrv.CPP:7284-7285`), so for a real player it stays `false` and this is the
branch that runs. The port had this as a logged no-op (the final `else` in
`HandleOrderChange`, previously `Server.cs:3436-3441`) — meaning a real
player's own `ORDER_CHANGE` did nothing, despite being the message's most
common real-world use. `Ship.CommandTarget` (built in P139 to feed the
`bpTargetHull`/`bpTargetShield` scalars in `HeavyShipsUpdate`) had no real
production write path — grep confirmed it was only ever set by test code
(`ShipsUpdateLiveTests.cs:213`, `TurretGunnerFiringTests.cs:219`), never by a
wire handler. P153 wires the real one.

## Changes

1. **New cross-side resolver `Server.FindShipAnySide(short id, Mission? mission)`
   (`Server.cs:4992-5008`).** Searches `_peerShips` first, then every sector's
   model list for NPC/drone ships (the P147 `SpawnNpcShip` population, which has
   no `_peerShips` entry) — the exact two-source scan `FindSameSideShip` gained
   in P148 (`Server.cs:4956-4971`), minus the side filter. Returns `null` for an
   unknown id, which the caller treats as native `GetModel` returning `NULL`
   (`FedSrv.CPP:7336-7338`).

2. **Extended final `else` of `HandleOrderChange` (`Server.cs:3436-3497`).**
   When the commanded ship is a real player ship (`!target.IsNpc`) and
   `req.Command == OrderChange.CmdCurrent`:
   - `req.ObjectID == NA` → clear: `target.CommandTarget = null` (the SetTarget
     `pmodel==NULL` case sends `objectType=OT_invalid` + `objectID=NA`,
     `WinTrek.cpp:8834-8836`);
   - `req.ObjectType == IgcObjectType.Ship` → resolve via `FindShipAnySide` and
     set `target.CommandTarget` (a null resolution = native `GetModel` NULL =
     clear);
   - any other `objectType` (station/etc.) → logged no-op, `CommandTarget`
     unchanged (scope boundary, below).

3. **Bot helper** `SendOrderChange(short, short, short, IgcObjectType, short)`
   (`Bot.cs:807-821`) exposing `objectType`/`objectID` so tests can drive the
   fallback's target (the existing 2-/3-arg overloads leave them at their struct
   defaults, which the NPC branches never read).

## Cross-side resolver scope

- `FindShipAnySide` spans `_peerShips` **and** every sector's model list, with
  **no side restriction**. Target-locking an *enemy* ship is the whole point of
  `SetTarget`; native resolves generically via `pMission->GetModel(objectType,
  `objectID)` which spans the whole mission with no side filter
  (`FedSrv.CPP:7336`). Do **not** reuse `FindSameSideShip` (same-side only,
  `Server.cs:4956`) or `TryResolveViewTarget` (`Server.cs:5033`, built for
  `VIEW_CLUSTER`: returns only scalar position/sector/side, never an object
  reference, and its ship lookup is `_peerShips`-only — it would miss an NPC
  target, the same gap P150 closed for `FindSameSideShip`).
- The **commanded** ship resolution (`target`) is a different resolution than
  the **targeted** object. `target = FindSameSideShip(req.ShipID, ship.SideId,
  mission) ?? ship` (`Server.cs:3339`, P148/P150 dispatcher code, unchanged by
  this task) stays same-side. P153 only adds the *targeted*-object half.

## Ship-only `CommandTarget` limitation (and why)

`Ship.CommandTarget` is `Model?`-typed (`Ship.cs:558`) and ship-only in
practice. This port's runtime `Station` (`Sim/Station.cs`) is deliberately
**not** a `Model` — its own doc comment states a station does not join the
sector's `_models` loop (a static body) — so `CommandTarget` can hold a targeted
**ship** (any side, NPC or real peer) but **cannot hold a station reference**
without a type change this task intentionally does not make. Consequently
`c_cmdCurrent` is scoped to ship targets only; a station-typed (or any other
non-ship) `objectType` is a **logged no-op** (no crash, no silent wrong write,
`Server.cs:3480-3488`). This is a stated scope boundary, not a bug.

## Deferred: `c_cmdAccepted` for a non-NPC target

`SetAccepted` (`WinTrek.cpp:8875-8852`) is a more advanced UI action that
interacts with the client's own autopilot state, which this port does not model,
and the port's existing `AcceptedTarget` slot is `DataStationIGC?`-typed (P150,
built specifically for the NPC return-to-base case), not generic. Wiring a real
player's `c_cmdAccepted` would need a design decision about that typing. Left a
logged no-op (`Server.cs:3488-3497`), same as before — only `c_cmdCurrent` is in
scope this task.

## Explicitly out of scope

- `c_cmdAccepted` for a non-NPC (real player) target — deferred, needs its own
  typing decision (see above).
- Station (or any non-ship) targets for `c_cmdCurrent` — this port's `Station`
  isn't a `Model`; out of scope for `CommandTarget`'s current shape.
- Any change to the NPC-gated branches (P148/P150/P152) — this task only touches
  the final, previously-no-op `else`.
- Any change to P103–P152 code beyond the new resolver and that one branch's
  extension. (`ApplyCommandSlot`, `FindSameSideShip`, `SpawnNpcShip`,
  `TryResolveViewTarget` all unchanged.)

## Verification

- Live loopback tests added in `OrderChangeCurrentLiveTests.cs` (in-process
  server, two real peers on different sides, no VM):
  1. `Real_player_locks_enemy_peer_across_sides_via_c_cmdCurrent` — A locks B
     (other side); `A.CommandTarget` becomes B's real `Ship` reference.
  2. `Real_player_clears_command_target_with_na_sentinel` — after locking,
     `objectID=NA` reverts `A.CommandTarget` to null.
  3. `Real_player_locks_ownerless_npc_ship_via_c_cmdCurrent` — A locks a P147
     `SpawnNpcShip` (no `_peerShips` entry) via the sector-scan half.
  4. `Station_typed_object_is_documented_no_op_and_does_not_change_target` —
     station `objectType` is a logged no-op; `CommandTarget` unchanged.
- Full suite: **2750 tests passing** (P152 baseline 2746 + 4 new). Three
  consecutive clean full runs (RUN10/11/12) before commit; intervening runs that
  red-flagged fired only documented pre-existing timing flakes
  (`LiveCombatWireTests`, `LiveSuicideShipDeleteTests`), each rerun clean —
  none related to this diff.
