# ORDER_CHANGE(93) — P148 (Stage 2: "stay docked" slice)

Task: command a docked NPC ship to stay docked — the first point a real connected
player issues a real `ORDER_CHANGE` command to a real (inert) NPC ship and a real
server-side state change results. Branch `master` at `92c2003` (P147) going in.

---

## 1. `command` vs `commandID` — confirmed distinct

Both fields appear in `DEFINE_FEDMSG(CS, ORDER_CHANGE, 93)` (`Inc/Messages.h:648-654`):

```cpp
DEFINE_FEDMSG(CS, ORDER_CHANGE, 93)
  ShipID        shipID;
  ObjectID      objectID;
  ObjectType    objectType;
  CommandID     commandID;
  Command       command;
END_FEDMSG
```

They are different native scalar *types* with different value spaces:

- `command` is a `Command` — the **slot selector** naming which of the native
  4-slot command array the action lands in:
  `c_cmdQueued=0 / c_cmdAccepted=1 / c_cmdCurrent=2 / c_cmdPlan=3`
  (`Igc/igc.h:848-852`; mirrored in `Wopr/Wopr/Constants/CommandType.cs:9-15`).
  It indexes `SetCommand(slot, target, id)` — e.g. the return-to-base branch
  writes `c_cmdQueued` (`FedSrv.CPP:7322`) and the generic fallback writes
  `pfmOC->command` as the array index (`FedSrv.CPP:7338`).
- `commandID` is a `CommandID` — the **actual action**, `c_cidDoNothing=-3` …
  `c_cidBuild=9` (`Igc/igc.h:897-912`; mirrored in
  `Wopr/Wopr/Constants/CommandID.cs:9-26`). The stay-docked gate reads exactly
  `pfmOC->commandID == c_cidDoNothing` (`FedSrv.CPP:7290`).

The codec exposes them as separate properties `CommandId` and `Command`
(`src/Allegiance.Core/Wire/OrderChange.cs`), each documented with its distinct
semantics and native citation, so they cannot be conflated.

## 2. Wire widths — REAL FINDING (both 1 byte, NOT the short default)

The task brief offered `short` as a "reasonable default" but instructed chasing
the typedef chains (the technique that resolved `WingID`/`ObjectID` in P146).
Chasing them yields the **real answer, both are single bytes**:

- `typedef char  Command;`   (`Igc/igc.h:842`)
- `typedef char  CommandID;` (`Igc/igc.h:897`)

MSVC `char` is signed, so `c_cidDoNothing = -3` (`igc.h:898`) fits in one byte.
By contrast:
- `ShipID`/`ObjectID` = `typedef short ObjectID` (`Igc/igc.h:477,480`) — 2 bytes.
- `ObjectType` = `typedef short ObjectType` (`Igc/igc.h:115`) — 2 bytes, reused
  via the already-`short`-backed `IgcObjectType` enum.

Layout (C++ struct align 2), total **12 bytes**:

```
offset  0: ushort cbmsg
offset  2: ushort fmid = 93
offset  4: short  shipID      (ShipID)
offset  6: short  objectID    (ObjectID)
offset  8: short  objectType  (ObjectType)
offset 10: sbyte  commandID   (CommandID, 1 byte)
offset 11: sbyte  command     (Command,  1 byte)
= 12 B fixed, no var items → cbmsg = 12 (offset 12 needs no pad)
```

Implemented in `src/Allegiance.Core/Wire/OrderChange.cs` (`FixedSize = 12`).
`CommandId`/`Command` are exposed as `short` properties but encoded/decoded as
single sign-extended bytes (`(sbyte)` on write, `(short)(sbyte)ReadByte()` on
read). This is a stated real finding, superseding the short default.

## 3. `IsNpc` design choice — explicit marker (recommended path taken)

Stage 2 needed some way to tell "target is an NPC/drone" from "target is a real
player's own ship" — native's gate is `GetPilotType() < c_ptPlayer`
(`FedSrv.CPP:7285`), true only for drones, never a real player's ship. This port
had no `PilotType` marker at all (P147 deliberately did not add one).

I chose the **explicit marker** the brief recommended: `public bool IsNpc
{ get; init; } = false` on `Ship` (`src/Allegiance.Core/Sim/Ship.cs`, after
`WingId`), set `true` only by `Server.SpawnNpcShip`
(`src/Allegiance.Server/Server.cs:2644-2655`, via the object initializer
`new Ship(...) { IsNpc = true }`).

Why explicit rather than the structurally-equivalent alternative (a ship having
no owning `_peerShips` entry):
1. **Faithful** to native's own explicit per-ship `pilotType` member — it is a
   real ship attribute in the source, not an inferred absence.
2. **Robust to incidental wiring**: the "no peer entry" test would break if a
   drone ever gained a peer surrogate (e.g. a spectating GM seat), or if the
   peer registry were ever reorganized; `IsNpc` names the fact directly.
3. **Unambiguous at the single call site** — `init`-only so it cannot be flipped
   after the spawn site, and `false` for every real seated peer by construction.

Justified (brief's mandate to state the choice and reasoning); the alternative
was consciously declined, not forgotten.

## 4. `FindSameSideShip` extension — mission-wide, side-scoped

Native's target resolution is `pside->GetShip(pfmOC->shipID)` (`FedSrv.CPP:7275-7278`)
— **side-scoped, not sector-scoped** — and must find drones, which live in the
sector model lists but have no `_peerShips` entry (P147). The original
`FindSameSideShip` (`Server.cs:4644`) searched only `_peerShips`, so it could not
resolve an NPC.

Extended to also enumerate the mission's full ship set — every sector's model
list filtered to `Ship` instances — with the same id + side match
(`Server.cs:4644-4673`). A placed NPC lives in a sector's model list
(`Mission.PlaceShip` → `sector.Add`, `Mission.cs:347-358`), so this finds it
while staying side-scoped and id-keyed, faithful to native. The `_peerShips`
scan is retained; the mission scan is the net-new coverage. The signature gained
a `Mission?` parameter; the sole existing caller (the board-ship handler,
`Server.cs:2800`) passes its mission.

## 5. `HandleOrderChange` — the stay-docked slice

Added at `src/Allegiance.Server/Server.cs` (after `HandleSetWingId`), reached via
a new dispatch branch (`else if (fmid == OrderChange.Fmid)`, before the final
unsupported-fmid `else`). Ports exactly the quoted native slice
(`FedSrv.CPP:7264-7299`):

- **Gate: `STAGE_STARTED` only.** `!pfsMission || GetStage() != STAGE_STARTED`
  (`FedSrv.CPP:7266`) — no other checks precede the decode.
- **Target resolution:** `pship = pfsPlayer->GetIGCShip()`; `if
  (pside->GetShip(shipID)) pship = pside->GetShip(...)` (`FedSrv.CPP:7271-7278`)
  — resolved on the commander's own side; an unresolvable `shipID` leaves
  `pship` as the sender's own ship (not an error). Mirrored via the extended
  `FindSameSideShip(...) ?? ship`.
- **Mutation:** when target `IsNpc` AND `StationId is not null` (docked) AND
  `commandID == c_cidDoNothing` → `target.StayDocked = true`
  (`pship->SetStayDocked(true)`, `FedSrv.CPP:7292`).
- `Ship.StayDocked` is a new `bool` (default `false`), the port of native
  `m_stayDocked` (`Ship.cs`).

`Ship.StationId` is a plain settable `short?` already used for direct docking
elsewhere (e.g. `Server.cs:3529,4134-4135`), so the test docks the NPC by setting
it directly — no new docking mechanism.

## 6. Explicit exclusions (out of scope, logged-and-no-oped)

Every branch outside the stay-docked gate is reached by `HandleOrderChange` only
to **log and no-op** — it never guesses at behavior:

- **Launch** (`FedSrv.CPP:7296-7302`) — needs `PickDefaultOrder`, a real
  per-pilot-type decision tree (miner case alone is 60+ lines). Out of scope.
- **Return to base** (undocked drone, `FedSrv.CPP:7304-7332`) — needs
  `FindTarget` (nearest-friendly-station-or-shipyard search). Out of scope.
- **Generic `SetCommand` fallback** for non-NPC/non-docked targets
  (`FedSrv.CPP:7335-7339`) — needs the full native 4-slot command array this port
  does not have (`Ship.Command` state / `SetCommand`). Out of scope.
- **Chat acknowledgment** (`FedSrv.CPP:7293-7294`) — a stretch goal, **not
  implemented**. Native sends a `SendChat(... CHAT_INDIVIDUAL ...)` "Affirmative."
  from the commanded ship to the commander. This port's chat relay is
  client-initiated (`Server.cs:2528+`) and has no reusable "server constructs a
  chat as if from ship X" helper; building one is out of the deliberate-narrow
  scope. The state mutation is the proof point and stands alone; the ack is
  documented as deferred, not silently dropped.

## 7. Verification

- No VM needed. A real loopback `Bot`/in-process-server live test
  (`tests/Allegiance.Core.Tests/OrderChangeStayDockedLiveTests.cs`) boots a real
  connected bot on a side, spawns an NPC via `Server.SpawnNpcShip`, docks it at a
  real station id (set directly), issues `ORDER_CHANGE` targeting the NPC with
  `commandID = c_cidDoNothing`, and asserts `StayDocked == true` server-side. A
  second test issues `ORDER_CHANGE` against the peer's own (non-NPC) ship and
  asserts it is a documented no-op (`StayDocked` untouched, no crash) — proving
  the `IsNpc` gate. Both pass.
- Full existing suite passes unmodified — see 3-consecutive-run result below.
- Committed only after build + test clean on 3 consecutive runs.

Baseline: P147 checkpoint was 2727 tests going in; this task adds 2 →
(see full-suite totals recorded in the run logs).
