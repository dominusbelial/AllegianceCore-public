# TRANSPORT_T4 — real TCP `ITransport` (`TcpTransport`), client-side only

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `319ef020 T3: wire FMTransportSite receive path onto m_msgList`
Scope: **additive** — `src/_Utility/TcpTransport.{h,cpp}` (new),
`VS2022/Utility.vcxproj` (two new entries + PCH override for the .cpp).
`AllegianceCore/` read-only (the C# reference), `FedMessaging`/`messagecore.h`/
`Messages.cpp`/`DirectPlayTransport.*` untouched. Nothing constructs a
`TcpTransport` yet — wiring a transport selector into `FedMessaging` is the
next stage. Committed only after the real Windows build (§3).

---

## 1. What was built

A faithful C++ port of the proven C# reference
`AllegianceCore/src/Allegiance.Core/Transport/TcpTransport.cs`. The wire
format and threading model are copied exactly; the C++ interface's mechanical
differences (HRESULT returns, `DPID`/`CB` instead of `ConnectionId`, `Join`'s
`outSelf`/`outServer` instead of a `LocalId` property) are adapted per
`src/_Utility/ITransport.h`. Two new files:

- `src/_Utility/TcpTransport.h` — header.
- `src/_Utility/TcpTransport.cpp` — implementation, PCH-exempt.

### Function -> function mapping to the C# reference

| C++ (this port) | C# reference (TcpTransport.cs) | Notes |
|---|---|---|
| `TcpTransport::ctor` (TcpTransport.cpp:64) | `TcpTransport(ITransportSite site)` (cs:67) | inits `m_csWrite`; asserts site non-null |
| `~TcpTransport` (:77) | (implicit) | calls `Shutdown()` + `DeleteCriticalSection` |
| `Host` (:91) | `Host(ushort)` (cs:73-83) | **STUB `E_NOTIMPL`** — Host/accept-loop deferred |
| `Join` (:97) | `Join(string,ushort,string)` (cs:85-98) | see §2 sequence |
| `Shutdown` (:209) | `Shutdown()` (cs:119-138) | 500ms join, suppress OnSessionLost |
| `IsConnected` (:243) | `IsConnected` property (cs:69) | `m_fConnected` |
| `GetLocalPlayerId` (:248) | `LocalId` property (cs:70) | always 0 on client path |
| `Send` (:258) | `Send(ConnectionId,span,bool)` (cs:100-117) | Data frame; fGuaranteed/target ignored |
| `GetSendQueueInfo` (:268) | n/a | stub `0/0` S_OK, mirrors DPT cpp:583 |
| `GetIPAddress` (:277) | n/a | **`E_NOTIMPL`** (nothing calls it) |
| `GetListeningPort` (:284) | `BoundPort` (cs:71) | **`E_NOTIMPL`** (nothing calls it) |
| `ReadThreadStart` (:294) | `Spawn` lambda cs:97/255 | `_beginthreadex` trampoline |
| `ReadLoop` (:300) | `ReadDataLoop` (cs:198-214) | frames delivered as "from server" id 1 |
| `NotifySessionLostOnce` (:336) | (no direct counterpart) | guarded single OnSessionLost |
| `WriteFrame` (:347) | `WriteFrame` (cs:218-227) | header+body under one write lock |
| `ReadFrame` (:394) | `ReadFrame` (cs:229-240) | type + LE length, cap check |
| `ReadExactly` (:433) | `ReadExactly` (cs:242-251) | loop recv until full |

---

## 2. Wire format, exactly as implemented

```
frame = type(1 byte) + length(4 bytes, little-endian) + body
  Greet = 0x01  one-shot, body = peer name as UTF-8 bytes
  Data  = 0x02  body = one FedPacket's codec output bytes, byte-for-byte
MaximumFrame = 1 << 24  (~16 MiB)  -- fail, never truncate
```

Client connect sequence (matches cs:85-98):
1. `WSASocket` → `getaddrinfo(address,port)` → `connect`.
2. **Write the Greet frame with `playerName` BEFORE spawning any read
   thread** (cs:96). (C# uses `Encoding.UTF8.GetBytes(playerName)` — this port
   writes the raw `char*` bytes, which are the caller's responsibility to have
   already encoded as UTF-8; same net bytes for an ASCII name, which is the
   practical case here.)
3. Set `outSelf=0`, `outServer=1`, `m_localPlayerId=0`, then spawn ONE
   background read thread (`_beginthreadex`, cs:97).
4. Read loop reads `Data` frames and calls `m_pSite->OnReceive(1, body, cb)`
   — every frame treated as coming from the fabricated **server peer id 1**.

Constants reused from the established client convention
(`DirectPlayTransport.cpp:479-480`, per `Messages.cpp:1603/1606`):
**client self = DPID 0, client server = DPID 1.**

Read loop / error policy: a frame type other than `Data` on the client loop, a
peer close (`recv()==0`), or a recv error stops the loop and fires
`OnSessionLost()` **once**, guarded by an `Interlocked` "already notified"
flag. `Shutdown()` sets that same flag first (intentional teardown = not a lost
session), so a racing recv error cannot double-fire. Partial `recv()`/`send()`
are looped until the full 5-byte header and full body are transferred (TCP has
no message boundaries) — `ReadExactly`/the write loops in §1.

`MaximumFrame` cap enforced in both `WriteFrame` (cb > cap → `E_FAIL`) and
`ReadFrame` (lenRaw > cap → `E_FAIL`); a crafted oversize length cannot force a
huge allocation.

---

## 3. PCH/winsock fix as applied (the landmine)

Diagnosis (unchanged from the brief): `src/_Utility/pch.h:28` is
`#include <windows.h>` with **no `WIN32_LEAN_AND_MEAN`**, which pulls the
legacy `<winsock.h>`. Every `.cpp` in `Utility.vcxproj` is forced to
`#include "pch.h"` first (MSVC PCH rule). A real socket needs `<winsock2.h>`;
including it after `<windows.h>`-via-winsock.h triggers the
winsock.h-vs-winsock2.h redefinition cascade.

Fix (decided in the brief, applied here):

1. `VS2022/Utility.vcxproj` — the `<ClCompile Include="..\src\_Utility\TcpTransport.cpp">`
   entry carries `<PrecompiledHeader>NotUsing</PrecompiledHeader>` for the
   three project configs, the same per-file override mechanism pch.cpp uses
   with `Create`. This TU is exempt from the project PCH.
2. `TcpTransport.cpp` — `<winsock2.h>` and `<ws2tcpip.h>` are the literal
   first two lines, **before** `#include "TcpTransport.h"` (which pulls
   `ITransport.h` → `<windows.h>` → the legacy winsock path). winsock2-before-
   windows is the canonical safe order: winsock2.h sets `_WINSOCKAPI_`, so
   windows.h's attempt to include winsock.h is skipped. `<ws2tcpip.h>` provides
   `getaddrinfo`/`freeaddrinfo`.
3. `#pragma comment(lib, "Ws2_32.lib")` in `TcpTransport.cpp` — self-contained;
   no `.vcxproj` linker edit (this project builds a static `.lib`, the import
   resolves only when a final `.exe` links `Utility.lib` in — out of scope, the
   pragma makes it correct once that happens).
4. The `.cpp` also `#define __MODULE__ "Utility"` (matching `pch.h:10`) and
   `#include "zassert.h"` because it is PCH-exempt — so the codebase's
   `assert` (=`ZAssert`, `zassert.h:92`) and `debugf` compile. Without
   `__MODULE__`, `assert()` fails to compile (`C2065` on the `__MODULE__`
   argument to `ZAssertImpl`).

Header `TcpTransport.h` includes `ITransport.h` as normal (headers don't
participate in the PCH mechanism the way the .cpp's translation unit does —
the danger was specifically winsock2-vs-windows ordering **within the .cpp**).
No redefinition error occurred on the real build (the `.cpp` compiled as its
own unit, visible in the log as a standalone `TcpTransport.cpp` line after
`Generating Code...`).

The build setup script also retargeted the extracted host copy `v143 -> v145`
(61 platform-toolset occurrences) because the host registers only v144/v145/v180
toolsets, no v143 — identical host accommodation T2/T3 documented. The git tree
is untouched by retargeting.

---

## 4. Verbatim build evidence (real Windows build)

Host `192.168.0.240` (SSH key auth, no ICMP — SSH only). `build_t4.ps1` scp'd
over and run with `powershell.exe -ExecutionPolicy Bypass`. Repo tar synced
(`--exclude=.git --exclude=Debug --exclude=Release`, 8,687,546 bytes); prior
`C:\Users\domingo\Allegiance` removed before extracting. MSBuild 18.9.1 via
vswhere. Two project-file builds (`Utility.vcxproj` then `ClintLib.vcxproj`,
config `FZDebug-NoYellowComm` — the sln's "Debug" mapping — `/p:PlatformToolset=v145`).

Relevant tail of the run:

```
=== Build Utility;ClintLib ===
UTIL_EXIT=0 CLINT_EXIT=0 LASTEXITCODE=0
=== Errors in log ===
ERROR_LINES=0
=== TcpTransport.obj present ===
C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\TcpTransport.obj
RESULT_OK
```

Full build output for the two projects (from `build_t4.log`, the tail):

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
  TcpTransport.cpp
  Utility.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib

  pch.cpp
  appmsg.cpp
  AutoDownload.cpp
  badwords.cpp
  clintlib.cpp
  Generating Code...
  ClintLib.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ClintLib\ClintLib.lib
```

**Zero errors.** `Select-String` for `error\s+C\d+|error\s+MSB\d+` across the
log: 0 matches. Only diagnostic in the whole run is the pre-existing warning
`src\zlib\ztime.cpp(104,1): warning C4172: returning address of local
variable or temporary : timeString [ZLibrary.vcxproj]` — present in T1/T2/T3,
unrelated to this change. My change contributes zero warnings. `TcpTransport.cpp`
compiled (standalone line in the `Generating Code...` region — the PCH-exempt
unit), and both `.lib` targets were emitted, proving no regression downstream.

Why two project-file builds instead of the literal `Allegiance.sln /t:Utility;ClintLib`
recipe: that invocation hit the same MSBuild quoting trap T2 recorded. With a
combined `;` target on the `.sln`, the metaproj target parser folded the
following `/p:` switches into the target name → `MSB4057: The target
"ClintLib /p:Configuration=Debug ..." does not exist` (first run, before the
fix). Passing `Configuration=Debug` to a bare `.vcxproj` also fails
(`MSB8013: doesn't contain Debug|Win32`), because the project configs are
named `FZDebug*`, not `Debug`. The recipe's `.sln` "Debug" map resolves to
project config `FZDebug-NoYellowComm` (the output dir proves it). So the build
here invokes each of the two project files directly with that config — exactly
the two project builds the `.sln` target runs, just unambiguous about quoting.
Same guarantee, no invented-format evidence.

**Definition of green met**: Utility and ClintLib both build with zero new
errors; `TcpTransport.obj` is present in the output. There is still no live
network test possible from this build-only recipe — a real two-party test (this
`TcpTransport` talking to the live C# server) is out of scope for this task and
this repo's build pipeline; verification is the clean build plus the careful
reading in §2 against the proven C# reference.

---

## 5. Deferred (explicit, for the next stage)

- **`Host` / accept loop** — returns `E_NOTIMPL` (TcpTransport.cpp:91). The C#
  server owns accepting TCP connections; the C++ side never listens yet.
- **Wiring a way to choose the transport** — `FedMessaging`'s constructor still
  hardcodes `new DirectPlayTransport(...)`; nothing constructs a `TcpTransport`.
  The future selector (config/env switch, or a factory) that lets WOPR pick
  `TcpTransport` over `DirectPlayTransport` is the next task. As part of that,
  the `Join` app-GUID gap disappears for TCP (it doesn't need
  `SetApplicationGuid`), which is what makes this swap clean for the client.
- **Live two-party network test** — this `TcpTransport` actually dialing the
  real C# headless server. Needs the C# server + a host outside this repo's
  build pipeline; proposed as the follow-on integration test.
- **`GetSendQueueInfo`** stays the introspection stub (`0/0`, S_OK) matching
  DirectPlayTransport's fallback; nothing on the bot path reads real depth.
- **`OnPeerJoined`/`OnGroupChanged`** — correctly absent on the pure client
  path; the server does the membership bookkeeping. No code, by design (§scope).

`AllegianceCore/` untouched (read-only). Committed on `port-transport` only
after the §4 green build.
