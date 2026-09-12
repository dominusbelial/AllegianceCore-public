# P26 — FM_CS_NEW_SHIP_INSTANCE(31): wire codec, live send, and the honest result

Branch `master`, on top of `c9d3e05` (P25). Date 2026-08-30.

## What was added

1. **Wire codec** `src/Allegiance.Core/Wire/NewShipInstance.cs` — mirrors
   `SetCluster.cs` + `LogonRequest.cs`'s VarItem pattern. Envelope:
   `cbmsg(2) | fmid=31(2) | VarItem(ib,cb=4) | DataShipIGC(52)` = **60 bytes**.
   `exportData` = `DataShipIGC` with `nParts=0` (52 bytes, no `PartData[]`),
   layout as the task table (offsets corrected). Both `Encode()` and
   `Decode()`.

2. **Sent live** `src/Allegiance.Server/Server.cs:440-492` — in the `if
   (validated)` block, before `SET_CLUSTER`, the server sends
   `NewShipInstance` to the peer **own ship only** (not broadcast), with a
   side-null guard (`[create] ... skipping NEW_SHIP_INSTANCE` instead of
   fabricating side 0). `nParts=0` simplification stated plainly in a code
   comment.

3. **Tests** `tests/Allegiance.Core.Tests/Wire/NewShipInstanceWireTests.cs`
   (6 tests): fixed sizes, round-trip, byte-exact emit vs. hand-composed
   buffer, name truncation/zero-pad, wrong-fmid and nParts!=0 rejection.

Build clean, **1533** tests pass (was 1527, +6). `dotnet build` clean
(0 warnings, 0 errors).

## Live test (server on Linux host, T14 WOPR build on VM 192.168.0.240)

Server log (verbatim, key line):
```
[create] peer 1 't14Bot@BOT' -> NEW_SHIP_INSTANCE hull 1 ship 8096 side 0
[wire] from 1: unsupported fmid 35 — ignored at headless milestone
```
Capture (`--capture`) decoded field-by-field against the table — **exact**:
```
cbmsg 60, fmid 31, ib 8, cb 52, dwPrivate 0, partsOffset 52, hullID 1,
shipID 8096, name 't14Bot@BOT' (char[25], NUL + zero-pad), nParts 0,
sideID 0 (Alpha), pilotType 10, pads 0, abmOrders 0, baseObjectID -1 (NA),
nDeaths/nEjections/nKills 0.
```

WOPR stdout (T14 build, `Wopr.exe -d 192.168.0.140 6073`), verbatim:
```
[t14] Ship assigned; observing position for ~30 seconds.
[t14] position read skipped: Object reference not set to an instance of an object.   (still repeated)
ScoutExploreMap t14Bot@BOT: AttachMessages
ScoutExploreMap t14Bot@BOT: Starting strategy: ScoutExploreMap
[t14] position read skipped: Object reference not set to an instance of an object.   (×)
[t14] Observation done. max 1s-position-delta = 0.0 units.  -> no significant movement
```

## The honest result: NRE not cleared — precise follow-up finding

`NEW_SHIP_INSTANCE(31)` is proven correctly encoded and sent, **but the NRE
persists identically to T14**. The reason is concrete and reproducible:

- `BaseClient::HandleMsg` — the real client's message dispatcher the WOPR
  drives via `NativeClient.cpp:166,171` — is `src/clintlib/appmsg.cpp`. Its
  switch has **no `case FM_CS_NEW_SHIP_INSTANCE`** at all
  (`appmsg.cpp:32-3594`). Receiving fmid 31 therefore does nothing to create
  or allocate a ship object on the client.
- The WOPR layer's own wrapper, `MessageReceiver.cs:282-289`, merely unwraps
  the raw bytes into an `FMD_CS_NEW_SHIP_INSTANCE` and fires the
  `FMD_CS_NEW_SHIP_INSTANCE` event — **which has no subscribers** (no `+=`
  anywhere in `src/Wopr/`).
- Neither the C++ server (`FedSrv`) nor `Igc` ever *send* fmid 31, and
  `WinTrek`'s message handling does not dispatch it either (the only `case
  31:` in `WinTrek.cpp:5156` is a joystick dead-zone value, not a message).

**The message the client actually uses for object creation is
`FM_S_EXPORT(26)`** (`Messages.h:240-243`): `BaseClient::HandleMsg`
(`appmsg.cpp:80-105`) calls
`m_pCoreIGC->CreateObject(lastUpdate, pfmExport->objecttype,
FM_VAR_REF(pfmExport, exportData), pfmExport->cbexportData)` — with
`objecttype` separately typed (`OT_ship` = 0) plus the same `DataShipIGC`
`exportData` blob. **That** is what would make `GetShip()` non-null.

So the correct follow-up is to port `FM_S_EXPORT(26)` (wire: `cbmsg |
fmid=26 | VarItem exportData | ObjectType objecttype`, see
`Messages.h:240-243`) and send it (objecttype=`OT_ship`=0) before
`SET_CLUSTER`, instead of / in addition to `NEW_SHIP_INSTANCE`. The
`DataShipIGC` codec written for P26 transfers directly — `FM_S_EXPORT` carries
the same 52-byte blob. Per the task rule, no further fix was forced here; this
is the well-bounded negative result, at the same bar as T14/T8.

Server stopped after the run. No changes to `~/Development/Allegiance` (C++)
were made.
