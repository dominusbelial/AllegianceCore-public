# TRANSPORT_T5 — let `FedMessaging` choose `TcpTransport` vs `DirectPlayTransport`

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `71e1bc39 T4: add TcpTransport (real TCP ITransport, client-side only) + PCH/winsock fix`
Scope: **additive** — `src/Inc/messagecore.h`, `src/_Utility/Messages.cpp`,
`src/_Utility/TcpTransport.h`. `AllegianceCore/` read-only. No WOPR/
`AllegianceInterop`/other file touched. **No existing caller's behavior
changes** (the new ctor parameter defaults to `false`, so every existing
`new FedMessaging(site)` keeps building `DirectPlayTransport`). Committed only
after the real Windows build (§4).

---

## 1. Step 0 — header-hygiene check (FAILED, FIXED)

`src/_Utility/TcpTransport.h` was **not** winsock-clean. Its private member
list declared a raw socket type (`TcpTransport.h:65` before this task):

```cpp
SOCKET  m_socket;   // connected client socket; INVALID_SOCKET when idle
```

`SOCKET` is not intrinsic — it's the winsock typedef of `UINT_PTR`
(`basetsd.h`). Leaving it meant that once `messagecore.h` (included from
dozens of files across the engine) pulled in `TcpTransport.h`, every one of
those TUs would need the `SOCKET` symbol available just to name the type —
dragging the legacy-`<winsock.h>`-first landmine T4 fixed for the single
`TcpTransport.cpp` TU outward to the whole engine.

**Fix applied before anything else** (`TcpTransport.h:65`, member converted to
an opaque storage type):

```cpp
UINT_PTR  m_socket;   // connected client socket; INVALID_SOCKET when idle
```

Because `SOCKET` is literally typedef'd to `UINT_PTR`, this is a pure
storage-type rename with **zero behavior change** in `TcpTransport.cpp` — every
winsock API it calls (`WSASocket`/`connect`/`send`/`recv`/`closesocket`) takes a
`SOCKET`, which is the same underlying type, so `m_socket` still binds directly
with no casts. The only winsock symbol the header now references is none at
all — it's intrinsic. `TcpTransport.cpp` continues to `#include <winsock2.h>`
first, so `SOCKET` is defined normally in that TU. This is a real finding, not
a rubber stamp. A regression-guard comment above the member documents the
choice.

Downside avoided: `TcpTransport.h` transitively includes `ITransport.h` →
`<windows.h>`. `messagecore.h` already included `ITransport.h` at line 36 (T2),
so adding `TcpTransport.h` (line 38) introduces **no new transitive header** —
`messagecore.h` gains only the intrinsic-typed class definition. Verified on
the real build (§4): every `messagecore.h` consumer in `ClintLib`
(`clintlib.cpp`, `appmsg.cpp`, `AutoDownload.cpp`, `badwords.cpp`) compiled
clean.

---

## 2. The selector, as implemented

`messagecore.h` class declaration (`:317`):

```cpp
FedMessaging(IFedMessagingSite * pfmSite, bool fUseTcpTransport = false);
```

The `= false` default is what makes the edit invisible to every existing one-
argument call site across the codebase.

New private member, added right beside `m_transport` (`messagecore.h:634`):

```cpp
ITransport*         m_transport;
bool                m_fUsingDirectPlay;   // false => m_transport is a TcpTransport*
```

`Messages.cpp` — a small static factory above the constructor (initializer
lists can't branch), then the constructor takes the flag and records the
choice. Member-init order matches declaration order (`m_transportSite`,
`m_transport`, `m_fUsingDirectPlay`) so MSVC emits no C5038:

```cpp
static ITransport* CreateTransport(bool fUseTcpTransport, ITransportSite* pSite)
{
  if (fUseTcpTransport)
    return new TcpTransport(pSite);
  return new DirectPlayTransport(pSite);
}

FedMessaging::FedMessaging(IFedMessagingSite * pfmSite, bool fUseTcpTransport) :
  ...
  m_transportSite(this),
  m_transport(CreateTransport(fUseTcpTransport, &m_transportSite)),
  m_fUsingDirectPlay(!fUseTcpTransport)
```

`messagecore.h:38` adds `#include "TcpTransport.h"` beside the existing
`ITransport.h`/`DirectPlayTransport.h` includes (lines 36-37; the transport
seam block survived T3 unchanged).

---

## 3. The two real-UB `static_cast<DirectPlayTransport*>` sites — both guarded

`static_cast` checks nothing at runtime; calling a method through a pointer
cast to the wrong type when `m_transport` is really a `TcpTransport*` would be
silent UB. Both sites call the DirectPlay-only `SetApplicationGuid`. Guarded
on `m_fUsingDirectPlay`; the guid argument each already passed is unchanged.

- `Messages.cpp:1259` (was 1243) inside `JoinSession`:
  ```cpp
  if (m_fUsingDirectPlay)
    static_cast<DirectPlayTransport*>( m_transport )->SetApplicationGuid( guidApplication );
  ```
- `Messages.cpp:1316` (was 1299) inside `JoinSessionInstance`:
  ```cpp
  if (m_fUsingDirectPlay)
    static_cast<DirectPlayTransport*>( m_transport )->SetApplicationGuid( appGuid );
  ```

`TcpTransport` never needs an app GUID — `Join` connects directly by
address:port (its `Join` signature doesn't even take one), so skipping the call
over TCP is correct, not a workaround.

Also guarded the two already-null-safe-but-untyped accessors (these return the
raw DirectPlay pointers; the ternary `m_transport ? ... : 0` was null-safe but
not type-safe once `m_transport` could be a `TcpTransport*`):

- `GetDPlayServer()` `messagecore.h:514`:
  ```cpp
  if (!m_fUsingDirectPlay) return 0;
  return m_transport ? static_cast<DirectPlayTransport*>(m_transport)->GetDirectPlayServer() : 0;
  ```
- `GetDPlayClient()` `messagecore.h:527` (same pattern).

These feed only the five out-of-scope introspection methods from T2's fix D
(`SetSessionDetails`, `GetCountConnections`, `GetConnectionSendQueue`,
`GetConnectionInfo`, `GetSendQueue`), none on the bot's critical path — so
returning `0`/failure when running over TCP is the correct, honest answer (those
DirectPlay-specific introspections genuinely don't apply), not a workaround.

**No third site.** `grep -n "static_cast<DirectPlayTransport"` across
`Messages.cpp` and `messagecore.h` returns exactly the four above (two in
`Messages.cpp`, two in `messagecore.h`) — no other unguarded static_cast exists
to guard.

⛔ No `TcpTransport`-specific branch was added to any other function.
Everything else in `FedMessaging` flows through `m_transport` via the
`ITransport*` interface, unchanged since T2/T3. This task's only edits are the
construction-time selector plus the four static_cast guards above.

---

## 4. Verbatim build evidence (real Windows build, green)

Host `192.168.0.240` (SSH key auth, no ICMP). Repo tar synced
(`--exclude=.git --exclude=Debug --exclude=Release`, 6,949,628 bytes, 1,940
entries); prior `C:\Users\domingo\Allegiance` removed before extracting.
Retarget `v143→v145` on the extracted copy (64 occurrences — host lacks v143,
same as T2/T4). MSBuild 18.9.1 (`...\MS\Visual Studio\18\Community\...\MSBuild.exe`)
via vswhere. Two project-file builds (`Utility.vcxproj` then `ClintLib.vcxproj`,
config `FZDebug-NoYellowComm` — the sln "Debug" map, per T4 §why-two-builds)
with `/p:Platform=Win32 /p:PlatformToolset=v145`.

```
=== T5 build: FedMessaging transport selector ===
MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
REMOVED_OLD=C:\Users\domingo\Allegiance
EXTRACTED
RETARGET_OCCURRENCES=64
UTIL_EXIT=0 CLINT_EXIT=0
ERROR_LINES=0
PRESENT C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib
PRESENT C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\TcpTransport.obj
PRESENT C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ClintLib\ClintLib.lib
RESULT_OK
```

Tail of `build_t5.log` (UTF-16LE transcoded):

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

`Select-String error\s+C\d+|error\s+MSB\d+`: **0 matches** (`ERROR_LINES=0`,
`UTIL_EXIT=0 CLINT_EXIT=0`, `RESULT_OK`). Only diagnostic in the whole run is
the pre-existing `zlib\ztime.cpp(104,1): warning C4172` — present in T1-T4,
unrelated, zero new warnings from this task.

Why the wide-header claim is actually exercised: `messagecore.h` sits in the
include graph of `Messages.cpp` (edited here) **and** of every ClintLib TU
(`clintlib.cpp` is the one that `new FedMessaging(...)`s per T2
`clintlib.cpp:1312` — it compiled clean on its unchanged one-argument call
site, proving the default parameter keeps every existing caller compiling
unchanged). `TcpTransport.cpp` compiled as its standalone PCH-exempt TU
(line 109, after `Generating Code...`). Both `.lib` targets emitted.

**Definition of green met.** Still no live network test possible from this
build-only recipe — that is explicitly a later task, once `TcpTransport` is
selected somewhere real.

---

## 5. Explicit: no existing caller's behavior changed

Every pre-T5 `new FedMessaging(site)` call site across the engine — WinTrek
client, FedSrv server, Pigs, Lobby, and every other construction — passes one
argument, so it binds the `= false` default and constructs
`DirectPlayTransport` exactly as before. Only a caller that explicitly opts in
(`fUseTcpTransport = true`) gets a `TcpTransport`. `clintlib.cpp:1312`
(the only `new FedMessaging(...)` on the client path that the transport seam
touches today) is unchanged and still DirectPlay.

---

## 6. Deferred / flagged for the next task

- **Flip WOPR to TCP**: finding WOPR's actual `FedMessaging` construction
  call site (outside this repo's allowed scope) and setting `fUseTcpTransport
  = true` there is a separate, smaller follow-up — do NOT do it here, do not
  guess at it. It remains OPEN.
- **Real live two-party test**: this `TcpTransport` dialing the C# headless
  server over an actual socket. Needs the C# server + a host outside this
  repo's build pipeline. Remains OPEN; nothing in this task enables a live
  test by itself.

`AllegianceCore/` untouched (read-only). Committed on `port-transport` only
after the §4 green build.
