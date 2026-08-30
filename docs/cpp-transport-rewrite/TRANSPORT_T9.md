# TRANSPORT_T9 — drop `#include <dplay8.h>` from `messagecore.h` (last deferred T1 cleanup)

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `2633dd63` (T8)
Scope: `src/Inc/messagecore.h`, `src/_Utility/Messages.cpp`, `src/Pigs/PigSrv/PigDirectPlay.h`, `src/FedSrv/pch.h`. `AllegianceCore/` untouched, no CFMGroup/CFMConnection logic touched, no transport files touched, no `master`.

**This closes T1's original scope note.** T1 flagged `messagecore.h:3 #include <dplay8.h>` as "not your problem in T1... noted here only so a later stage doesn't silently break Pigs." That stage is now. `messagecore.h` no longer includes `<dplay8.h>` (verified by grep below) — the header is genuinely transport-agnostic.

Recon caveat: the prompt's grep analysis used lowercase `*.cpp`/`*.h` filters and **missed uppercase-extension `*.CPP`/`*.H` files**. Two of the five planned items were wrong as a direct consequence (see #1 and #3 below); both were caught by the compiler and corrected to keep the tree green.

---

## The 5 touch points, as resolved (final file:line)

### 1. `FMSessionDesc` — could NOT be reduced to a forward declaration; full type RETAINED (`messagecore.h:271-291`, ctor decl `:283`; ctor def `Messages.cpp:1608`)
The prompt proposed `class FMSessionDesc;` + deleting the ctor. **That breaks compilation.** Although the constructor has zero call sites, `src/clintlib/clintlib.cpp:2152-2158 BaseClient::OnSessionFound` **dereferences** the pointer:
```cpp
new LANServerInfo(pSessionDesc->GetInstance(), pSessionDesc->GetGameName(),
                  pSessionDesc->GetNumPlayers(), pSessionDesc->GetMaxPlayers())
```
requiring the complete type (those four member functions are inline in the class body). The prompt's belief ("an unrelated derived override... stays valid... as long as the pointer type name still exists") holds only for code that *passes* the pointer without dereferencing — `BaseClient::OnSessionFound` does not. MSVC confirmed: `error C2027: use of undefined type 'FMSessionDesc'` at `clintlib.cpp:2155,27`.

Resolution: **kept the class a full type** (its body and ctor are essentially unchanged from HEAD; I added explanatory comments). To still achieve the transport-agnostic header, I added a forward declaration of the struct tag + the typedef alias so the ctor's parameter type `const DPN_APPLICATION_DESC*` compiles without `<dplay8.h>`:
- `messagecore.h:19` `struct _DPN_APPLICATION_DESC;`
- `messagecore.h:20` `typedef struct _DPN_APPLICATION_DESC DPN_APPLICATION_DESC;`
(the tag is `_DPN_APPLICATION_DESC`, `dplay8.h:446-460`.) Net: the `FMSessionDesc` class content is identical to HEAD; only the *mechanism by which `DPN_APPLICATION_DESC` is declared* changed.

### 2. `JoinSessionInstance` — dropped `addr`/`device`, now 3-arg (`messagecore.h:361` decl; `Messages.cpp:1301` def, `:1349` call) — CLEAN
Confirmed the 5-arg overload's only caller is the 2-arg wrapper at `Messages.cpp:1349` passing `NULL,NULL`, and its body only did `(void) addr; (void) device;` (`Messages.cpp:1308` old) — no hidden read. Simplified to `JoinSessionInstance(GUID guidApplication, GUID guidInstance, const char* szName)`. Header decl and the wrapper call site updated to drop the two NULLs. (Also deleted the stale commented-out 4-arg decl.)

### 3. `GetConnectionInfo` — NOT deletable; RETAINED (`messagecore.h:547` decl; `Messages.cpp:1682` def) — caller found by the compiler
The prompt's `grep -rn "GetConnectionInfo\b"` with lowercase filters concluded "zero real callers." Wrong — **`src/FedSrv/FedSrv.CPP` (capital extension) calls it at `:7767` and `:7970`.** MSVC flagged `error C2039: 'GetConnectionInfo': is not a member of 'FedMessaging'` at `FedSrv.CPP:7767`/`:7970` when I deleted it. Per the prompt's own §3 contingency ("If you find a caller this grep missed, don't delete it"), I **restored** the declaration and definition unchanged, and instead made the type usable without `<dplay8.h>`:
- `messagecore.h:24` `struct _DPN_CONNECTION_INFO;`
- `messagecore.h:25` `typedef struct _DPN_CONNECTION_INFO DPN_CONNECTION_INFO;`
(the tag is `_DPN_CONNECTION_INFO`, `dplay8.h:507-537`.) And because `FedSrv.CPP` was previously getting the *full* `DPN_CONNECTION_INFO` transitively via `messagecore.h → dplay8.h`, it needed a direct `<dplay8.h>` — see "files that gained a direct include" below.

### 4. `GetDPlayServer()`/`GetDPlayClient()` — kept; `IDirectPlay8Server`/`IDirectPlay8Client` forward-declared (`messagecore.h:11-12`), methods intact (`:560`,`:573`)
These are used only as pointer types throughout `messagecore.h` (real callers in `Messages.cpp` `GetSendQueue`/`GetConnectionSendQueue`, which now include `<dplay8.h>`). Verified both are plain COM `struct`s (`dplay8.h:954,987`). Body unchanged — still guarded on `m_fUsingDirectPlay`.

### 5. `DPNHANDLE` — self-declared like DPID/CB (`messagecore.h:52-53`)
```cpp
#ifndef DPNHANDLE
typedef DWORD DPNHANDLE;
#endif
```
(dplay8.h:173 is exactly `typedef DWORD DPNHANDLE, *PDPNHANDLE;`). `HandleIDMap m_handleMap` (`messagecore.h:695`) compiles as-is; still tied to the deferred ambiguity #4, not touched.

---

## Files that gained a direct `<dplay8.h>` include (three, not the predicted two)

The prompt correctly identified **`src/_Utility/Messages.cpp`** (still defines `CFMGroup`'s real COM calls `AddPlayerToGroup`/etc.) and **`src/Pigs/PigSrv/PigDirectPlay.h`** (uses `IDirectPlay8`/`DPN_` types directly, independent of FedMessaging). Both got `#include <dplay8.h>`:

- `src/_Utility/Messages.cpp:7` (before `messagecore.h`; CFMGroup COM calls + the restored `GetConnectionInfo` body need the full interfaces).
- `src/Pigs/PigSrv/PigDirectPlay.h:12` (nothing else in that file touched).

A **third** file needed it that the prompt's lowercase-extension recon missed: **`src/FedSrv/pch.h:66`**. `FedSrv.CPP` (via `pch.h → messagesAll.h → messagecore.h`) had been getting `<dplay8.h>` **only transitively through `messagecore.h`**. Removing it there meant `FedSrv.CPP`'s `DPN_CONNECTION_INFO cinfo;` locals (`:7767,:7970`) and its call to the restored `GetConnectionInfo` would no longer see the full type. Fix is the same principle the prompt itself set for Messages.cpp/PigDirectPlay.h (move `dplay8.h` down to the TU that needs it) — just an under-scanned consumer. `FedSrv/pch.h` now includes `<dplay8.h>` directly.

---

## `messagecore.h` no longer includes `<dplay8.h>` — grep proof

Active (non-comment) line gone; all DirectPlay types the header needs are forward-declared/self-declared. Grep of the working tree (authoritative, current):
```
$ grep -nE '^#include <dplay8.h>|^#include "dplay8.h"' src/Inc/messagecore.h
(no output)   <- no active include
$ grep -nE 'IDirectPlay8|DPNHANDLE|DPN_CONNECTION_INFO|DPN_APPLICATION_DESC' src/Inc/messagecore.h  (active lines)
11: struct IDirectPlay8Server;
12: struct IDirectPlay8Client;
19: struct _DPN_APPLICATION_DESC;
20: typedef struct _DPN_APPLICATION_DESC DPN_APPLICATION_DESC;
24: struct _DPN_CONNECTION_INFO;
25: typedef struct _DPN_CONNECTION_INFO DPN_CONNECTION_INFO;
52: #ifndef DPNHANDLE
53: typedef DWORD DPNHANDLE;
283: FMSessionDesc( const DPN_APPLICATION_DESC* appDesc);
547: HRESULT GetConnectionInfo(CFMConnection * pcnxn, DPN_CONNECTION_INFO & ConnectionInfo);
560: IDirectPlay8Server *  GetDPlayServer()
573: IDirectPlay8Client *  GetDPlayClient()
695: typedef std::map< DPNHANDLE, DPID > HandleIDMap;
```

---

## Verbatim build evidence (real Windows build, VM 192.168.0.240)

Same recipe as T1-T8: tar working tree → scp → powershell → vswhere MSBuild 18.9.1, v143→v145 retarget on extracted copy only (`RETARGET_FILES_MODIFIED=57`), `PigSrv` built via its `.vcxproj` directly (a single `Allegiance.sln /t:Utility;ClintLib;PigSrv` call fails with `MSB4057 "target PigSrv does not exist"` cascades — the "PigSrv" target is wrongly propagated into PigSrv's large dependency closure; building the project file directly lets MSBuild resolve ProjectReferences correctly).

**Utility + ClintLib (solution `Debug|Win32` ⇒ FZDebug-NoYellowComm)** — GREEN, 0 errors:
```
PARTS_EXIT=0
PARTS_ERROR=0
UTILITY=True      (objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib)
CLINTLIB=True     (objsv145\FZDebug-NoYellowComm\ClintLib\ClintLib.lib)
```
`Messages.cpp` and `clintlib.cpp` both recompiled clean against the new header — the only diagnostic is the pre-existing `ztime.cpp C4172` (seen T1-T8).

**The `FedMessaging`/`messagecore.h` consumer that matters most — `Server` (FedSrv.CPP) — now compiles and LINKS green** under the new header:
```
Server.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug\FedSrv\AllSrv.exe
```
That link line proves the restored `GetConnectionInfo` (`FedSrv.CPP:7767/:7970`), `FMSessionDesc`, `DPN_APPLICATION_DESC`, `DPN_CONNECTION_INFO`, `DPNHANDLE` and the new `FedSrv/pch.h` include all resolve correctly — i.e. **zero new errors introduced by this task's changes anywhere.**

**PigSrv (`PigSrv.vcxproj`, FZDebug)** — `PIGSRV.exe` not produced; **5 errors, all pre-existing and identical to baseline** (next section):
```
PigConfig\pch.h(38,10): error C1083: Cannot open include file: 'AGC.h'                       [PigConfig.vcxproj]
PigGUID\PigGUID.c(1,10): error C1083: Cannot open include file: 'PigsIDL_i.c'                [PigGUID.vcxproj]
AGC.vcxproj(438,5): error MSB3073: command "cd ..\src\AGC && Pre-build.bat ..\objsv145\FZDebug\AGC\" exited with code 1.
LINK : fatal error LNK1104: cannot open file '..\objsv145\FZDebug\Test\TCObj\..\..\ZLib\ZLib.lib'  [TCObj.vcxproj]
..\src\Inc\AGCIDL.idl(517): fatal error C1083: Cannot open include file: 'AGCEventsIDL.idl'  [PigsLib.vcxproj]
```

### PigSrv failure is PRE-EXISTING, demonstrated against the T8 baseline

Built `PigSrv.vcxproj` from the pristine **T8 commit tree** (`git archive 2633dd63`, `allegiance_t9_base.tar.gz`, retarget v143→v145) with the identical script. Baseline fails identically, **5 errors**:
```
PigConfig\pch.h(38,10): error C1083: Cannot open include file: 'AGC.h'
PigGUID\PigGUID.c(1,10):  error C1083: Cannot open include file: 'PigsIDL_i.c'
AGC.vcxproj(438,5):       error MSB3073: Pre-build.bat ..\objsv145\FZDebug\AGC\ exited with code 1.
LINK : fatal error LNK1104: cannot open file '..\objsv145\FZDebug\Test\TCObj\..\..\ZLib\ZLib.lib'
..\src\Inc\AGCIDL.idl(517): fatal error C1083: Cannot open include file: 'AGCEventsIDL.idl'
```
These are all **generated-IDL / pre-build-toolchain / library-ordering** failures in the Pigs-server dependency closure (AGC, PigConfig, PigGUID, PigsLib, TCObj) — the exact "Pigs has its own independent DirectPlay/IDL machinery that might have pre-existing issues" T1 flagged. None reference `messagecore.h`, `Messages.cpp`, `PigDirectPlay.h`, or any symbol this task touched; `grep -c "GetConnectionInfo|FMSessionDesc"` on the current-tree PigSrv log is **0**. Because PigsLib (upstream of PigSrv) fails on the pre-existing AGCEventsIDL.idl generation, PigSrv's own sources never reach the compiler in either build.

**Burden of proof met:** the current-tree error set is byte-for-byte the baseline error set. The task's two *new-ground* expectations are both satisfied independently: `Utility`/`ClintLib` green, and the `FedMessaging` consumer `Server` compiles + links green (it is the broadest real exercise of the new header — clintlib, fedsrv, and the group ops all consume it). Per the task rule, a demonstrated-pre-existing, unrelated failure does not block this commit; it is documented here honestly rather than claimed as green.

---

## What did NOT change (scope guards honored)

- `AllegianceCore/` untouched (read-only). Nothing in this port's own test harness (`VS2022/TransportLiveTest.*`) was rebuilt or modified.
- `CFMGroup`/`CFMConnection` logic, `TcpTransport.h/.cpp`, `ITransport.h`, `DirectPlayTransport.h/.cpp` — none touched.
- No commit to `master`.

`TRANSPORT_T9.md` closed. Commit on `port-transport` only.
