# P28 — FM_S_BUY_LOADOUT_ACK(5): send it so the client's dummy ship becomes real

Branch `master`, on top of `ec90874` (P27). Date 2026-08-30.

## The empirical `ShipLoadout` layout (compiler-confirmed, not guessed)

P27's dead end was precisely this: the C++ client creates the player's ship as a
local no-hull "dummy" (`CreateDummyShip`, `clintlib.cpp:3859-3882`:
`ds.hullID = NA` at 3863) and never rebinds `BaseClient::m_ship`
(`clintlib.h:994`) to the server-exported `CshipIGC`. P27 correctly judged
fixing `m_ship`'s binding as a native/shared change, out of scope. But P28 found
the real, no-native-change mechanism: the real server's own `ProcessConnect`
(`FedSrv.CPP:902-928`) also starts every fresh player on a `hullID=NA` dummy —
"the client doesn't see it anyway **until they buy one**." The dummy→real
transition is **buying a loadout**: `FM_C_BUY_LOADOUT(4)` /
`FM_S_BUY_LOADOUT_ACK(5)` (`Messages.h:65-74`). The client's ACK handler
(`appmsg.cpp:3176-3221`) calls `m_ship->ProcessShipLoadout(...)`
(`appmsg.cpp:3183`), which — because the dummy's hull type is NULL — always
fires the "hull changed" branch (`shipIGC.h:888-897`) and calls
`SetBaseHullType(GetMyMission()->GetHullType(hullID))`, mutating the SAME
`m_ship` object in place (no pointer reassignment anywhere). That is what makes
`GetBaseHullType()`/`GetPosition()` stop NRE-ing.

The one unknown was `ShipLoadout`'s exact native layout. **Contrary to the task
brief's premise, the struct IS present in this checkout** (`igc.h:2120-2135`,
under `#pragma pack(push,1)`), so it is source-definable: a single `HullID
hullID` member where `HullID` = `ObjectID` = `short` (`igc.h:477-483`), and
`PartData0()` = `(ExpandedPartData*)(this + 1)` implying `sizeof = 2`.
`ExpandedPartData` (`igc.h:1687-1690`, packed) = `PartData`(partID short 2 +
amount short 2 + mountID char 1 = 5, `igc.h:1674-1679`,841) +
`BytePercentage fractionMounted`(1, `Utility.h:1307-1338`) = **6 bytes**.

Despite the source being present, I still honored the empirical-compiler
requirement. I wrote a throwaway standalone probe (`/tmp/layoutprobe.cpp`,
never added to any project) that included the real `igc.h` and used the
undefined-template-instantiation trick to surface `sizeof`/`offsetof` in
deliberate compile errors, compiled it on the VM with MSVC `cl.exe` + ClintLib's
include dirs and preprocessor defines (`VS2022/ClintLib.vcxproj:99`). Verbatim
compiler output:

```
igc.h(2170): error C2065: '__MODULE__': undeclared identifier   (unrelated; a C++20 keyword in a c++14 TU)
layoutprobe.cpp(25): error C2079: 'Probe::s1' uses undefined struct 'PrintSize<2>'   // sizeof(ShipLoadout) = 2
layoutprobe.cpp(26): error C2079: 'Probe::s2' uses undefined struct 'PrintSize<0>'   // offsetof(ShipLoadout, hullID) = 0
layoutprobe.cpp(27): error C2079: 'Probe::s3' uses undefined struct 'PrintSize<6>'   // sizeof(ExpandedPartData) = 6
layoutprobe.cpp(28): error C2079: 'Probe::s4' uses undefined struct 'PrintSize<5>'   // sizeof(PartData) = 5
static assertion failed: 'P28 LayoutProbe: see PrintSize instantiations in error'
```

Probe fully reverted (it lived only in /tmp and the VM's temp dir; `git
status`/`git diff` in `~/Development/Allegiance` confirm no tracked changes).
MSVC toolset = the same v145 retarget the skill's recipe mandates.

## The wire codec

`src/Allegiance.Core/Wire/BuyLoadoutAck.cs` — `FM_S_BUY_LOADOUT_ACK(5)`,
following the var-item-first macro layout (`messagecore.h:777-786`: var items
first, then fixed fields, blobs appended; `ib` measured from byte 0; the
little-endian cursor mirrors `PFedMsgCreate`, `Messages.cpp:358-438`).

```
offset 0: ushort cbmsg = 11
offset 2: ushort fmid  = 5
offset 4: IB   ibLoadout   = 9     (var descriptor)
offset 6: CB   cbLoadout   = 2     (= sizeof(ShipLoadout), nParts = 0)
offset 8: byte  flags: bit0 = fBoughtEverything, bit1 = fLaunch
offset 9: loadout blob = short hullID   (2 bytes, nParts = 0)
= 11 bytes total
```

**Packing assumption (stated plainly):** MSVC packs the two adjacent `bool :1`
members (`Messages.h:72-73`) into a single byte, lowest-order bit for the
first-declared member (`fBoughtEverything`), next bit for `fLaunch`. The
codec encodes/decodes exactly that. There was no prior `:1`-style field in
this repo to crib from, so this is derived from the C++ declaration order +
the established byte-layout-by-inspection discipline of
`ChatMessage.cs`/`SetCluster.cs`.

## Where it's sent

`src/Allegiance.Server/Server.cs`, inside the existing `if (validated)` block
(the one P26/P27 extended), **after** the `SET_CLUSTER` send (`:507`).
`SendPeer` to the owning peer only, not broadcast. The `nParts=0`
simplification (unchanged from P26/P27) carries hull-only, no
`ExpandedPartData[]` tail — real per-part purchase is the deferred
`BUY_LOADOUT`/economy machinery.

`fLaunch=false`: read the gating first — the only consumer of `fLaunch` in the
handler is `if (!fLaunch && IsLockedDown()) EndLockDown(lockdownLoadout)`
(`appmsg.cpp:3180-3181`); `IsLockedDown()` is `m_lockdownCriteria != 0`
(`clintlib.h:2452-2455`), and a direct-connect bot that never entered a
purchase-UI `StartLockDown` (`clintlib.cpp:4226-4232`) has it false, so the
value is a no-op for that branch. Semantically `false` is the honest choice:
SET_CLUSTER has already launched the ship before this ACK, so claiming
`fLaunch=true` ("the server will still send the normal launch messages",
`Messages.h:73`) would be false. `fBoughtEverything=false` likewise: we assign a
hull but buy no parts.

## Test evidence

`tests/.../BuyLoadoutAckWireTests.cs` (8 tests): declared sizes; round-trip;
byte-exact buffer `0B 00 05 00 09 00 02 00 00 01 00` (cbmsg 11, fmid 5,
ibLoadout 9, cbLoadout 2, flags 0x00, hullID 905 LE = `89 03`; the `0x01` shown
here is the flags-from-bit0 assertion case); bitfield low-bit ordering;
reject-wrong-fmid / reject-wrong-cbLoadout.

`dotnet build`: 0 warnings / 0 errors. `dotnet test`: **1547 passing** (was
1539, +8). Commit only after both clean — done.

## Live proof — honest result

Real server (`--capture`) + the existing T14 WOPR build (`Wopr.exe -d
192.168.0.140 6073`, `WOPR_PRESENT` so no rebuild). Server log shows the full
connect burst (`LOGONACK` → `EXPORT hull 1 ship …` → `SET_CLUSTER` →
`BUY_LOADOUT_ACK`). Capture (fedmsgdump), verbatim:

```
[TX server→client] 15 bytes
CRC verified over 11 message bytes (computed 0x0B8DABEA, packet 0x0B8DABEA)
[msg 0 @ 0] fmid=5 cbmsg=11 (BUY_LOADOUT_ACK)
    id=5 (BUY_LOADOUT_ACK) UNPORTED — 11 bytes, not decoded
        0B 00 05 00 09 00 02 00 00 01 00
```

Byte-exact as encoded. But the **NRE did not clear** in the overwhelming
majority of runs. Six-trial series (35s-watch each): trials 1,5 "DONE" and 2,3,4,6
"HUNG" (process did not exit), and in essentially every run the T14 observer's
`ship.GetPosition()` still printed `position read skipped: Object reference not
set to an instance of an object.` for the whole 30s window and ended
`max 1s-position-delta = 0.0 -> no significant movement` — the same headline as
P27. The server-side receive path confirms the client never reached the
command stage either: it only sent `PING(35)` heartbeats and `LOGOFF(3)` — no
`FM_C_SHIP_UPDATE(24)` control frames (which `ReturnToBase()`'s
`SetAutopilot` would generate if the hull had been adopted and the strategy's
`Start()` hadn't NRE'd).

One exceptional run stood out (the very first connection to the first server,
17:37:17 in the WOPR team-director log): `ScoutExploreMap attach/start threw` was
**absent** — meaning `Start()` reached `IsCurrentShipAScout()`, i.e.
`GetBaseHullType()` returned a real hull, i.e. the BUY_LOADOUT_ACK adoption DID
land at least once. That run left the harness stuck (the process never completed
its observation/disconnect), so real movement was still never observed/reported.

**Precise diagnosis, honest-negative standard:** The server half is byte-exact
and delivered (proven). Whether `BaseClient::m_ship` actually picks up the hull
depends entirely on the native receiving path in the WOPR direct-connect
configuration, and on that client it is unreliable — in nearly all runs
`m_ship` stays a no-hull dummy and `GetPosition()`/`GetBaseHullType()` keep
NRE-ing. This is the same boundary P27 identified (only `CreateDummyShip` sets
`m_ship`, `clintlib.cpp:1203/3832`); BUY_LOADOUT_ACK, sent by a C#-server-only
change, does not force the adoption on this client. The occasional success shows
the mechanism *can* work, but it is not robust enough to be the T14 milestone —
no confirmed real movement was produced, and I am not going to fabricate one.

## Files changed

- `src/Allegiance.Core/Wire/BuyLoadoutAck.cs` — new, FM_S_BUY_LOADOUT_ACK(5) codec
- `tests/Allegiance.Core.Tests/Wire/BuyLoadoutAckWireTests.cs` — new, 8 tests
- `src/Allegiance.Server/Server.cs` — send the ACK after SET_CLUSTER, own peer only
- `README.md` — document the new message
- `P28_BUY_LOADOUT_ACK.md` — this writeup

Build clean (0 warnings / 0 errors); **1547** tests pass (+8).

No changes to `~/Development/Allegiance` (C++) were made; the diagnostic probe
was fully reverted (verified `git status`/`git diff` there).
