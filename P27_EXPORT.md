# P27 — FM_S_EXPORT(26): wire codec, live send, and the honest result

Branch `master`, on top of `511278b` (P26). Date 2026-08-30.

## What was added

1. **Wire codec** `src/Allegiance.Core/Wire/ExportShip.cs` — mirrors
   `SetCluster.cs` + `NewShipInstance.cs`'s VarItem pattern, naming chosen as
   `ExportShip` (consistent with the noun-style message classes `NewShipInstance`,
   `SetCluster`, `ChatMessage`; `ShipExport`/`ObjectExport` were considered and
   rejected for being ambiguous with the per-tick update classes). Envelope:
   `cbmsg(2) | fmid=26(2) | VarItem(ib,cb=4) | objecttype(int32)=0 | DataShipIGC(52)`
   = **64 bytes** (`Messages.h:240-243`; `ObjectType` is a `[v1_enum]` →
   forced 4-byte int32 per `AGCIDL.idl:303`, not a short). Both `Encode()` and
   `Decode()`; 6 unit tests in `tests/Allegiance.Core.Tests/Wire/
   ExportShipWireTests.cs`.

   **DataShipIGC encoding copied verbatim from `NewShipInstance.cs`, not
   extracted into a shared helper.** Reasoning: this codebase's convention is
   self-contained message-per-file codecs (SetCluster, ChatMessage,
   NewShipInstance all embed their DataShip logic). Refactoring P26's
   proven, byte-verified code into a shared helper would have increased the
   blast radius of the change and touched already-tested code for no
   functional gain, since the two messages differ in the outer envelope
   (EXPORT inserts the `objecttype` field). A copy keeps the diff additive
   and P26's encoding literally intact.

2. **Sent live** `src/Allegiance.Server/Server.cs` — **replacing** the
   inert `NEW_SHIP_INSTANCE(31)` send (same side-null guard P26 added), in
   the same spot: the `if (validated)` block, before `SET_CLUSTER` (now at
   `Server.cs` in the `if (live.Ship.SideId is { } sideId)` block). The peer
   gets `ExportShip` (own ship only, not broadcast).

   **Judgment call — replace, not keep-both:** FM_S_EXPORT(26) is the
   client's *real* object-creation path (verified: `NativeClient.cpp:161-167`
   routes fmid 26 to `BaseClient::HandleMsg` → `appmsg.cpp:80-105` →
   `m_pCoreIGC->CreateObject`), whereas FM_CS_NEW_SHIP_INSTANCE(31) has *no*
   case anywhere on the client dispatcher (`appmsg.cpp` full switch) and is
   therefore dead-on-arrival (harmless — the switch `default:` at
   `appmsg.cpp:3583-3590` returns `S_FALSE` without crashing — but inert).
   Continuing to ship a message we know the client never dispatches is
   misleading; EXPORT fully supersedes it. The NSI codec+tests are kept as a
   faithful-but-inert port of a real protocol fmid (31), zero cost.

   `nParts = 0` still. Real per-hardpoint `PartData[]` export remains out of
   scope, unchanged from P26.

## Wire capture (byte-exact proof)

`fedmsgdump` on the server `--capture` (/tmp/p27_capture.bin), verbatim:
```
[TX server→client] 68 bytes
CRC verified over 64 message bytes (computed 0x3D609FC5, packet 0x3D609FC5)
[msg 0 @ 0] fmid=26 cbmsg=64 (EXPORT)
    id=26 (EXPORT) UNPORTED — 64 bytes, not decoded
    40 00 1A 00 0C 00 34 00 00 00 00 00 00 00 00 00 34 00 01 00 A0 1F
    74 31 34 42 6F 74 40 42 4F 54 00 ... 0A 00 00 00 FF FF 00 00 00 00 00 00 00 00
```
Decoded field-by-field (LE): cbmsg=0x0040=64, fmid=0x001A=26, ib=0x000C=12,
cb=0x0034=52, objecttype=int32@8 = 0 (OT_ship), blob@12: dwPrivate=0,
partsOffset=52, hullID=1, shipID=0x1FA0=8096, name 't14Bot@BOT' (char[25]),
nParts=0, sideID=0 (Alpha), pilotType=10, abmOrders=0, baseObjectID=-1,
nDeaths/Ejections/Kills=0. **Exactly** the declared 64-byte layout, sent
after `LOGONACK(2)` and before `SET_CLUSTER(7)`.

Server log (verbatim, key line):
```
[create] peer 1 't14Bot@BOT' -> EXPORT hull 1 ship 8096 side 0
```

## Live test (server on Linux host 192.168.0.140, T14 WOPR build on VM 192.168.0.240)

Used the **existing** T14/P26 WOPR build
(`C:\Users\domingo\Allegiance\src\Wopr\Wopr\bin\x86\FZDebug-NoYellowComm\Wopr.exe`,
08/30 17:42:49) — binary still present, so no rebuild. Ran
`Wopr.exe -d 192.168.0.140 6073`. Config verified: `ServerAddress=192.168.0.140`,
`ServerPort=6073`, `UseTcpTransport=true`, registry auth guid `T14BOTGUID`.

WOPR stdout (verbatim):
```
[t14] TeamDirectorDirectTest.Run(192.168.0.140,6073)
[t14] Configuration.ServerAddress='192.168.0.140' ServerPort=6073 directConnect=True
[t14] Creating player 't14Bot@BOT' (side 0, pilot).
Creating player: t14Bot@BOT
ScoutExploreMap t14Bot@BOT: AttachMessages
ScoutExploreMap t14Bot@BOT: Starting strategy: ScoutExploreMap
[t14] Ship assigned; observing position for ~30 seconds.
[t14] position read skipped: Object reference not set to an instance of an object.   (×14)
[t14] Observation done. max 1s-position-delta = 0.0 units.  -> no significant movement
[t14] disconnecting.
Strategy destroyed for t14Bot@BOT, ScoutExploreMap
ClientConnection::OnLogonAckEvent fValidated=TRUE bRetry=FALSE reason=(null)
```

Team-director log tail (`t14Bot@BOT_teamdirector.txt`, verbatim):
```
8/30/2026 6:39:49 PM: Starting message processing loop.
8/30/2026 6:39:49 PM: Direct connect: waiting for ship assignment.
8/30/2026 6:39:49 PM: Ship assigned, attaching ScoutExploreMap strategy.
8/30/2026 6:39:49 PM: ScoutExploreMap attach/start threw: Object reference not set to an instance of an object.
```
(The `position read skipped: ...` lines and the `attach/start threw:` line
are **still present**.)

## Honest result: NRE not cleared — precise diagnosis (why EXPORT alone cannot fix T14)

The server half is now proven byte-exact and delivered. But **the NRE
persists identically to T14**, and this time the reason is precise and is
*not* a server encoding/framing defect:

- The inbound message **does** reach the real object-creation path:
  `NativeClient.cpp:161-167` routes fmid 26 to `BaseClient::HandleMsg`, whose
  `case FM_S_EXPORT` (`appmsg.cpp:80-105`) calls
  `m_pCoreIGC->CreateObject(objecttype, exportData, cb)`. For `OT_ship(0)`
  that news a `CshipIGC` and `AddShip`s it to the core's ship registry
  (`missionigc.cpp:3299-3384`, `shipIGC.cpp:165-203`) — so
  `m_pCoreIGC->GetShip(8096)` would return non-null after processing.
- **But the WOPR strategy's ship is not that object.** `ClientConnection.
  GetShip()` → `NativeClient::GetShip()` → `BaseClient::GetShip()`, which is
  `inline IshipIGC* GetShip() const { return m_ship; }` (`clintlib.h:994`).
  And `m_ship` is assigned in exactly **two** places in the whole tree:
  `BaseClient::CreateDummyShip` (`clintlib.cpp:3859-3882`) — which builds a
  no-hull (`hullID=NA`), `shipID=NA` stand-in via `CreateObject(OT_ship, &ds)`
  at `clintlib.cpp:3879` and binds it to `m_ship` — and `m_ship=NULL` in
  `Terminate` (`clintlib.cpp:1281`). `CreateDummyShip` runs at
  `BaseClient::Initialize` (`clintlib.cpp:1203`) and `OnQuitMission`
  (`clintlib.cpp:3832`). **The receiving path (`appmsg.cpp`) never reassigns
  `m_ship` from a received EXPORT.** So the `CshipIGC(8096)` our server's
  EXPORT creates is a *separate* registered object; the client's player-ship
  identity stays bound to the locally-created dummy (hullID `NA`, no hull
  type, no cluster/position).
- Consequence: `ship.GetBaseHullType()` on the strategy's ship returns
  `NULL` (dummy has no hull), so `IsCurrentShipAScout()`/`Start()`
  (`ScoutExploreMap.cs:781-787`) and the observation read at `igcWrapper.h:850`
  still throw NRE. No autopilot/control is ever issued, so no position deltas
  (`max 1s-position-delta = 0.0`), exactly as T14/P26.

**What would clear it (out of scope for this task):** the client must *adopt*
the exported ship as its player identity (`m_ship`), which in this codebase
is governed by `BaseClient::CreateDummyShip`/ship-binding on the receiving
side — a native-client change, and outside this task's additive-only, C#
server-side boundary. Neither fmid 31 nor fmid 26 can force that adoption
from the wire alone in this tree.

This is the same honest-negative-result standard as T14/P26/T8: the server
side is exact and delivered, the remaining blocker is precisely diagnosed
and left unforced.

## Files changed

- `src/Allegiance.Core/Wire/ExportShip.cs` — new, FM_S_EXPORT(26) codec
- `tests/Allegiance.Core.Tests/Wire/ExportShipWireTests.cs` — new, 6 tests
- `src/Allegiance.Server/Server.cs` — replace NEW_SHIP_INSTANCE send with EXPORT
- `README.md` — document EXPORT(26) as the sent create message; NSI kept as inert port
- `P27_EXPORT.md` — this writeup

Build clean (0 warnings, 0 errors). **1539** tests pass (was 1533, +6).

No changes to `~/Development/Allegiance` (C++) were made.
