# TRANSPORT_T2 — wire `FedMessaging` onto `ITransport*` (finished)

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `ec64d5aa T1: ITransport seam + DirectPlayTransport wrapper (additive)`

This is the **first successful completion of T2's scope**. A prior attempt
wired most of it, got killed mid-run (~46 min) before it reached the verify
step, and left its work uncommitted in the tree. That uncommitted tree was
inspected by hand against a real Windows build and judged ~85% done. This
session finished it with exactly 4 diagnosed fixes (below) and then proved it
green with a real Windows build. Nothing was redesigned.

---

## 1. What was wired (carried forward from the original T2 scope)

`FedMessaging` (src/Inc/messagecore.h + src/_Utility/Messages.cpp) now holds
an `ITransport*` and drives lifecycle/send through it. `DirectPlayTransport`
owns every DirectPlay COM object (`m_pDirectPlayClient` / `m_pDirectPlayServer`
are no longer FedMessaging members). All file:line refer to Messages.cpp unless
noted.

Constructor / destructor (203-233):
- `m_transport(new DirectPlayTransport(&m_transportSite))` — constructed with
  the `FMTransportSite` adapter (219-220).
- `~FedMessaging` calls `Shutdown()`, then `delete m_transport` (229-231).

Send — `GenericSend` (497-554): the `m_pDirectPlayClient->Send` /
`m_pDirectPlayServer->SendTo` call (formerly 547-555) is replaced by
`m_transport->Send( precip->GetID(), pv, cb, fGuaranteed )` (538). CRC/framing
stays message-layer; the transport derives its own reliability flags.

Host — `HostSession` (1095-1131): the `DPN_APPLICATION_DESC`/local-address/port/
`Host()` dance (formerly 1110-1164) is replaced by
`m_transport->Host( guidApplication, dwPort )` (1114), then the Everyone group
and `ResetOutBuffer` (1123, 1125). State reflected from `IsConnected()` (1120).

Teardown — `Shutdown` (1140-1179): the `Close()`/`Release()` blocks and enum
cancel are replaced by `m_transport->Shutdown()` (1149-1150).

Join — `JoinSession` (1192-1227): enum + `Connect()` moved into
`m_transport->Join( szServer, dwPort, szCharName, outSelf, outServer )` (1208),
ids taken from out-params (1221-1222), state from `IsConnected()` (1223).

JoinSessionInstance x2 (1242-1310 and 1284): both reduce to
`m_transport->Join(...)`; the DirectPlay-specific connect (formerly
1262-1298) and the pre-discovered `addr`/`device` are transport-managed.

Other forwarding wrappers:
- `EnumSessions` (1339-1347) -> `DirectPlayTransport::EnumSessions` (via the
  concrete cast, see Fix A/B).
- `GetIPAddress` (1330) / `GetListeningPort` (1336) -> now-on-ITransport
  methods (resolves T1 ambiguity #7).

Removed (superseded by the transport): `MsgHandler`, `EnumHostsInternal`,
`EnumHostsCallback`, `InitDPlayClient`, `InitDPlayServer`, `ConnectToDPAddress`
(dead), and the `m_pDirectPlayClient`/`m_pDirectPlayServer` members.

---

## 2. The 4 fixes this session applied (see file:line, read at edit time)

### Fix A — `SetApplicationGuid` through the right pointer type (2 sites)
`ITransport*` has no `SetApplicationGuid` (it is DirectPlay-only, T1 chose not
to put it on the interface), so the two direct calls did not compile. Mirroring
`FedMessaging::GetDPlayServer()`, both now `static_cast<DirectPlayTransport*>`
before calling:
- `Messages.cpp:1202` in `JoinSession`
- `Messages.cpp:1258` in `JoinSessionInstance`

(these are the pre-Join calls that resolve T1 ambiguity #2).

### Fix B — delete dead `EnumHostsInternal` left behind
Between the new `FedMessaging::EnumSessions` (ends at the `}` on `Messages.cpp:
1347`) and `FedMessaging::CreateConnection` (now `Messages.cpp:1349`) sat an
orphaned block: 3 dangling statements with no enclosing function
(`m_fSessionCallback = true;` / `return EnumHostsInternal(...);` / `}`)
immediately followed by the ENTIRE old body of `FedMessaging::EnumHostsInternal`
(DirectPlay enumeration logic T1 already moved into
`DirectPlayTransport::EnumHostsInternal`). The old copy referenced removed
members (`m_pDirectPlayClient`, `InitDPlayClient`) — the source of the
C4430/C2059/C2143/C2447/C2039 cascade. Deleted `Messages.cpp:1349-1512`
inclusive (1786 -> 1622 lines). Verified the seam: EnumSessions `}` -> blank ->
CreateConnection, nothing else changed.

### Fix C — restore default arg on `EnumHostsInternal`
`DirectPlayTransport::EnumSessions` (`.cpp:529`) calls
`EnumHostsInternal( guidApplication, szServer )` with 2 args; the declaration
had lost the original `DWORD dwPort = 6073` default when the signature moved.
Restored in the header only (`DirectPlayTransport.h:100-101`), not the `.cpp`
definition (normal C++ style).

### Fix D — 5 out-of-scope methods still referenced removed members
T2's out-of-scope list said both "do not touch `SetSessionDetails`,
`GetCountConnections`, `GetConnectionSendQueue`, `GetConnectionInfo`,
`GetSendQueue`" AND "remove the raw `m_pDirectPlayClient`/`m_pDirectPlayServer`
members" — conflicting instructions, because those 5 methods still reference
the removed members. This is the one place we touched those methods, and only
mechanically (pointer-expression swap, no other change):

- `SetSessionDetails` (`Messages.cpp:1370,1371,1381,1387`) ->
  `GetDPlayServer()`
- `GetCountConnections` (`Messages.cpp:1396,1399,1409`) ->
  `GetDPlayServer()`
- `GetConnectionSendQueue` (`Messages.cpp:1607,1608`) -> `GetDPlayServer()`
- `GetConnectionInfo` (`Messages.cpp:1618,1619`) -> `GetDPlayServer()`
- `GetSendQueue` (`Messages.cpp:1589,1590`) -> `GetDPlayClient()`

`GetDPlayClient()` is a new thin forward added mirroring `GetDPlayServer()`:
`DirectPlayTransport.h:90` adds `GetDirectPlayClient()` next to
`GetDirectPlayServer()`, and `messagecore.h:522-526` adds `GetDPlayClient()`.
Same null-safe ternary as `GetDPlayServer()`; no extra null checks were added —
existing guards kept verbatim.

---

## 3. Build evidence (real Windows build, verbatim)

Host 192.168.0.240, MSBuild selected by vswhere: VS 18 Community, MSBuild
18.9.1 (product "18\Community"), `C:\Program Files (x86)\Microsoft Visual
Studio\Installer\vswhere.exe` -> MSBuild.exe. Recipe per task, with one host
accommodation (see note below).

Second run (targets correctly combined via an argument array so `;` was not
split by PowerShell):

```
MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
CWD=C:\Users\domingo\Allegiance\VS2022
=== Build Utility;ClintLib ===

  Microsoft (R) Library Manager Version 14.51.36256.0
  Copyright (C) Microsoft Corporation.  All rights reserved.
  
  Guids.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\Guids\Guids.lib
  ZLibrary.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ZLib\Zlib.lib
  Utility.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib
  pch.cpp
  appmsg.cpp
  AutoDownload.cpp
  badwords.cpp
  clintlib.cpp
  Generating Code...
  ClintLib.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ClintLib\ClintLib.lib
LASTEXITCODE=0
RESULT_OK
```

**Definition of green met**: `Utility` and `ClintLib` both build, **0 errors**.
`Messages.cpp` and `DirectPlayTransport.cpp` compiled (present in the
`Generating Code...` run and the `Utility.lib` object list, including
`Messages.obj` and `DirectPlayTransport.obj`). The only warning in the whole
build is the pre-existing `ztime.cpp(104,1): warning C4172` from ZLibrary —
unrelated to this change.

First run (before I fixed the `/t` target quoting) produced the same Utility
result — zero compiler errors from my files:

```
  pch.cpp
  allegdb.cpp
  CollisionEntry.cpp
  CollisionQueue.cpp
  ConfigurationStore.cpp
  CRC.cpp
  Endpoint.cpp
  FileLoader.cpp
  HitTest.cpp
  KDnode.cpp
  KDroot.cpp
  Messages.cpp
  DirectPlayTransport.cpp
  Utility.cpp
  Generating Code...
  Utility.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib
  ...
    1 Warning(s)
    0 Error(s)
```

**Host accommodation (not a code change):** the four fixes build clean ONLY
under a resolvable toolset. This host registers only `v145` (14.51.36231) and
`v180` MSVC toolsets — it has **no v143 toolset installed** (vswhere resolved
to VS 18). Every `VS2022/*.vcxproj` hardcodes `<PlatformToolset>v143</PlatformToolset>`,
and those elements win over the command-line `/p:PlatformToolset=v145` for the
dependency projects (Guids.vcxproj, ZLibrary.vcxproj -> MSB8020). To actually
exercise the code, the extracted host copy was retargeted v143->v145 by the
build script before compiling; that retarget touches only the extracted copy
on 192.168.0.240, never the git tree. This is an environment setup difference,
not a defect introduced by these fixes. If you want a from-scratch re-proof,
either install the v143 toolset or pass a consistently-retargeted copy.

---

## 4. Ambiguity carry-forward for T3 (the receive path)

Resolved this round:
- **#2 (ITransport::Join carries no app GUID)** — RESOLVED. FedMessaging calls
  `static_cast<DirectPlayTransport*>(m_transport)->SetApplicationGuid(...)`
  immediately before `Join` at `Messages.cpp:1202` (`JoinSession`) and
  `Messages.cpp:1258` (`JoinSessionInstance`, falling back to `FEDSRV_GUID`).
- **#7 (GetIPAddress/GetListeningPort not on ITransport)** — RESOLVED.
  Both promoted onto `ITransport` (`ITransport.h:163,165`); FedMessaging
  forwards at `Messages.cpp:1330` and `Messages.cpp:1336`.

Still open, for the receive-path task (T3):
- **#1 (no `OnSessionFound` on `ITransportSite`)** — enumeration discovery is
  still not surfaced to the site; DirectPlayTransport records what it finds
  internally. Open for T3.
- **#4 (`SEND_COMPLETE` -> NAK is not forwarded)** — `ITransportSite` has no
  NAK hook and the transport doesn't own `m_handleMap`. `OnSysMessage`'s
  SEND_COMPLETE block (Messages.cpp:741-782) still exists but is unreachable
  from the live path until T3 wires it. Open for T3.
- **#5 (receive buffer lifetime)** — the transport calls
  `site->OnReceive(from, data, cb)` synchronously and returns the DirectPlay
  buffer immediately after; the site must copy during the call. The original
  async buffering + ReturnBuffer ordering must be restored/revisited when the
  receive queue comes up. Open for T3.

Also intentionally deferred to T3 (task rules): the `FMTransportSite` five
placeholder methods (`Messages.cpp:249-276`) — the receive-path adapter — were
NOT touched.

---

## 5. Landmines to know about (so T3 is not surprised)

1. **Circular include bug (original T2 attempt).** Putting
   `#include "messagecore.h"` inside ITransport.h produced a circular-include
   failure: one header's `#ifndef` guard hid the other's content where it was
   needed, collapsing into ~9 apparently-unrelated errors
   (`'ITransportSite': base class undefined`, C3668/C2143/C4430/C2238,
   `'m_transport': undeclared identifier`). One root cause, not nine bugs.
   Documented in ITransport.h:30-41. This session's tree is immune (ITransport.h
   self-contains its types, no include of messagecore.h) — but if you reintroduce
   a cross-include in T3, remember the symptom.

2. **Scope conflict found this session (fix D).** The original T2 scope list
   contained two conflicting instructions: keep the 5 introspection methods
   out of scope, yet remove `m_pDirectPlayClient`/`m_pDirectPlayServer`
   members — which those 5 methods reference. Left unresolved, it is a silent
   latent break (only surfaced by the build). Resolved here by the mechanical
   `GetDPlayServer()`/`GetDPlayClient()` swap described in section 2, fix D.
   When scoping the next task, do not assume "leave method X alone" and
   "remove member Y it uses" can both hold.

3. `m_fSessionCallback` / `g_guidApplication` and the old enum UX are now inert;
   `EnumHostsCallback` no longer fires a site callback (T1 ambiguity #1). Do not
   expect `OnSessionFound` from any transport method today.

---

## 6. Status

- Branch `port-transport`, HEAD `ec64d5aa` + this commit (post-green).
- `AllegianceCore/` untouched; `FMTransportSite` placeholders untouched.
- Committed only after the real Windows build confirmed green (section 3).
