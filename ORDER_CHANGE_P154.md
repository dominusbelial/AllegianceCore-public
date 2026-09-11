# P154 — ORDER_CHANGE(93): the real-player `c_cmdAccepted` generic path

Branch: `master` · Top commit at start: `0910598` (P153) · Baseline: **2750 passing**

## What this task wires

P153 ported the generic (non-NPC) fallback of `HandleOrderChange` for
`command=c_cmdCurrent` (`SetTarget`, "lock onto this"). The real client's
**other** live use of that same generic path — `command=c_cmdAccepted` via
`SetAccepted` — was deliberately deferred pending a typing decision. This task
makes that decision and wires the server side.

Unlike `SetTarget` (which the client sends from one dedicated UI action),
`SetAccepted` is **not** dead or low-value: direct session investigation
confirmed it fires on **every menu-command execution**, called together with
`SetTarget` on the same target:

```cpp
SetTarget(pmodel, cid);
SetAccepted(pmodel, cid);      // WinTrek/WinTrek.cpp:10681-10682
```

and it has its own explicit "clear accepted command" keybind:

```cpp
case TK_ClearCommand:     // clear accepted command
{
    SetAccepted(NULL, c_cidNone);   // WinTrek/WinTrek.cpp:10395-10406
}
break;
```

Only the client's **autopilot** consequence of accepting an order is commented
out / dead in native (`WinTrek/WinTrek.cpp:8884-8895`) — the wire send itself
is real and live (`WinTrek/WinTrek.cpp:8876-8907`, the `SetAccepted` body always
fires `pfmOC->command = c_cmdAccepted; ...` when connected). This task therefore
models the server-side state storage, and **only** the storage — autopilot
behavior is out of scope (dead code in native, and this port has no autopilot
concept).

## The typing decision — a NEW, separate ship-typed slot (not a retype)

The existing `Ship.AcceptedTarget`/`Ship.AcceptedCommandId` (P150) were built
specifically for the **NPC return-to-base** use case and are typed
`DataStationIGC?` + `short` (stations are `DataStationIGC` record structs in
this port, not `Model`s — `src/Allegiance.Core/Sim/Ship.cs:560-578`).

The **real-player** `c_cmdAccepted` use is, in practice, **ship-typed**: the
live call site above (`WinTrek.cpp:10681-10682`) always passes the *same*
`pmodel` to `SetAccepted` as it does to `SetTarget` — a ship in the common case,
matching `Ship.CommandTarget`'s own `Model?` shape (`Ship.cs:558`).

This task does **not** repurpose or retype the NPC-specific slot. It adds a
**separate** new property pair for the real-player case:

```csharp
public Model? AcceptedShipTarget { get; set; }
public short  AcceptedShipCommandId { get; set; } = OrderChange.CidNone;
```

(`src/Allegiance.Core/Sim/Ship.cs:581-611`, the property declarations at
`Ship.cs:600`/`Ship.cs:608`, placed directly after the NPC-specific
`AcceptedCommandId`.)

**Why this is a new design choice, not an established pattern — stated
explicitly.** Native keeps one command-slot array
(`m_commandTargets[c_cmdAccepted]`, `shipIGC.h:2631`) and both producers write
index `c_cmdAccepted` (`Igc/igc.h:849`). This port deliberately maps that one
native array slot to **two differently-typed C# properties in different
contexts**. Prior tasks that split an array slot across properties predate a
codified pattern here, so this is presented as a **new** decision rather than
claimed precedent. The justification is semantic: the two `c_cmdAccepted`
producers —

1. the **NPC return-to-base** branch (`ApplyCommandSlot`, `Server.cs:3514`,
   fed by `FindNearestFriendlyStation`, which can only yield a
   `DataStationIGC`), and
2. a **real player's menu-order acceptance** of a ship target
   (`WinTrek.cpp:10681-10682`),

— are *semantically unrelated* despite sharing a native slot index. Forcing one
shared, awkwardly-typed slot would couple two unrelated behaviors behind a
`DataStationIGC?` type that cannot even express a ship target. Separate
properties let each context keep a faithful, strongly-typed home for its
producer.

## The reused resolver

No new resolver. P154 reuses **`Server.FindShipAnySide(short id, Mission? mission)`**
added in P153 (`src/Allegiance.Server/Server.cs:5015-5031`), with its exact
shape:

- `_peerShips` scan first (real peers), then every sector's model list (P147
  NPCs placed via `Mission.PlaceShip`, which have no `_peerShips` entry);
- **no side filter** — accepting/locking an enemy is the whole point of both
  client actions;
- returns `null` for an unknown id, which the caller treats as native
  `GetModel(...)` returning `NULL` (a clear/no-op, `FedSrv.CPP:7336-7338`);
- `IgcObjectType.Ship`-only resolution; any other objectType is a **logged
  no-op** (this port's `Station` is not a `Model`, so neither `Model?` slot can
  hold one — documented scope boundary, not a bug).

## The server-side change

One new arm added **alongside** P153's `CmdCurrent` arm inside the *same*
generic-fallback branch (`src/Allegiance.Server/Server.cs:3436-3497`), not a
duplicate of the surrounding gate. The branch now fires for
`command ∈ { c_cmdCurrent (P153), c_cmdAccepted (P154) }` against a non-NPC
`target` (a real player's own ship), both wired identically:

| condition                              | c_cmdAccepted action (P154)                       |
|----------------------------------------|---------------------------------------------------|
| `objectID == NA` (OT_invalid + NA)     | `AcceptedShipTarget = null; AcceptedShipCommandId = CidNone;` — the SetAccepted `pmodel==NULL` clear (`WinTrek.cpp:8834-8836`, driven by `TK_ClearCommand`, `WinTrek.cpp:10404`) |
| `objectType == IgcObjectType.Ship`     | `AcceptedShipTarget = FindShipAnySide(...); AcceptedShipCommandId = req.CommandId;` — native `GetModel` + `SetCommand(command, pmodel, commandID)` (`FedSrv.CPP:7335-7339`); unknown id → `null` (clear) |
| any other objectType                   | logged no-op; slot unchanged (scope boundary)     |

The `c_cmdCurrent` arm (P153) is unchanged apart from being folded into the
same shared branch; `CommandTarget` continues to be written exactly as before.
The four-P153-property independence is asserted by the live test `coexist` case
below.

## Live tests (no VM)

`tests/Allegiance.Core.Tests/OrderChangeAcceptedLiveTests.cs` — 4 tests on the
booted 2-bot / 2-side loopback harness (two real connected peers on different
sides), the same `SeatTwo()` scaffold P153 used:

1. ⭐ **Cross-side accept** — A (`c_cmdAccepted`, cmdID=c_cidGoto) names B (other
   side); server sets `A.AcceptedShipTarget` to B's real `Ship` reference and
   `A.AcceptedShipCommandId == c_cidGoto`. Proves cross-side resolution and the
   separate ship-typed slot (asserts `A.AcceptedTarget`/`AcceptedCommandId`
   remain null/CidNone).
2. **NA-sentinel clear** — after accepting B, `objectID=NA` → `AcceptedShipTarget`
   null, `AcceptedShipCommandId` back to `CidNone`.
3. **NPC target** — A accepts an ownerless P147 `SpawnNpcShip` (no `_peerShips`
   entry) → resolved via the sector model-list scan and set on
   `A.AcceptedShipTarget`.
4. **Coexistence** — c_cmdCurrent → `CommandTarget`, c_cmdAccepted →
   `AcceptedShipTarget` live simultaneously on A; neither bleeds into the
   NPC-specific `AcceptedTarget`/`AcceptedCommandId`; NA-clearing the accepted
   pair leaves `CommandTarget` locked on B (the P153 path undisturbed).

Result: **4/4 P154 passing**, P153's 4 still passing, full suite **2754 passing**.

## Explicit exclusions (all preserved / untouched)

- **Autopilot behavior** — dead code in native (`WinTrek.cpp:8884-8895`); this
  port has no autopilot concept. Only the state storage
  (`AcceptedShipTarget`/`AcceptedShipCommandId`) is modeled.
- **The existing NPC-specific `AcceptedTarget`/`AcceptedCommandId`** (P150,
  `DataStationIGC?`-typed) — not retyped, not repurposed, not touched.
- **Non-ship targets for `c_cmdAccepted`** (stations etc.) — same
  `Model?`-shape limitation as P153's `CommandTarget`.
- **`c_cmdQueued`/`c_cmdPlan` for real players** — not sent by any real client
  action (confirmed in P148/P150's own send-site investigation).
- **No changes to P103–P153 code** beyond the two new properties and the one
  new case folded into the shared generic-fallback branch.

## Verification

- `/home/domingo/.dotnet/dotnet test` (explicit dotnet binary — the PATH stub is
  a broken snap that demands sudo) — **2754 passing**, 3 consecutive clean runs
  (rerun-only on the five documented flakes, not counted as regressions).
- No Windows VM involved.
