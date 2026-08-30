# TRANSPORT_T8 — fix `FedMessaging::Shutdown()` / `TcpTransport::Shutdown()` hang after a TCP session

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `5cde59fd` (T7)
Scope: `src/_Utility/Messages.cpp` (Bug 1), `src/_Utility/TcpTransport.cpp` (Bug 2), `VS2022/TransportLiveTest.cpp` (watchdog extend). `AllegianceCore/` read-only (started/stopped, not modified).

---

## TL;DR — build GREEN, but the hang PERSISTS. Not committed.

| Bar | Result |
|---|---|
| Clean build `Utility;ClintLib;TransportLiveTest` (Win32, v145, FZDebug-NoYellowComm) | ✅ 0 new errors (only the pre-existing `ztime.cpp C4172`) |
| Both prescribed fixes applied, compile clean (incl. `shutdown` name-hiding resolves to Winsock fn, no recursion/ambiguity) | ✅ verified by compiler, 0 errors |
| Live re-test: process exits on its own | ❌ **no** — still hangs, 30s watchdog fires |

**Per the task rule ("commit only when build clean AND the live re-test confirms the hang is actually gone"), I did NOT commit.** The two fixes were applied and verified to compile; the live hang was re-confirmed THREE independent ways and precisely localized. The residual hang contradicts the Bug-2 premise and needs a diagnosis revisit (details below).

---

## The two fixes as applied (file:line read at edit time)

### Bug 1 — lock `m_msgList.clear()` (`src/_Utility/Messages.cpp:1226`)
Every other access to `m_msgList` in `Messages.cpp` holds `m_csMsgList`: the five `FMTransportSite` producers (`:276/286/296/306/323` → push_back under `m_pfm->m_csMsgList`) and `ReceiveMessages()`'s drain (`:895-902`, `&m_csMsgList`). `FedMessaging::Shutdown()`'s `m_msgList.clear()` (`:1226`) was the sole unlocked touch. Applied:

```cpp
// Messages.cpp:1222-1230 (inside FedMessaging::Shutdown())
  // Flush message queue. Guarded like every other access to m_msgList
  // (cf. ReceiveMessages():895-902 and the FMTransportSite producers at
  // :275-325): the transport's read thread can still be pushing into this
  // list during Shutdown, and clearing an unlocked std::list concurrently
  // is UB that can manifest as a corrupted list walked by the consumer.
  EnterCriticalSection( &m_csMsgList );
  m_msgList.clear();
  LeaveCriticalSection( &m_csMsgList );
```
Style matches `ReceiveMessages()` `:895-902` (`&m_csMsgList` directly, not the `&m_pfm->` qualifier — that is only used inside the nested adapter class).

### Bug 2 — `shutdown(SD_BOTH)` before `closesocket()` (`src/_Utility/TcpTransport.cpp:218-222`)
Applied:

```cpp
// TcpTransport.cpp:218-236 (inside TcpTransport::Shutdown())
    if ( m_socket != INVALID_SOCKET )
    {
        shutdown( m_socket, SD_BOTH );
        closesocket( m_socket );
        m_socket = INVALID_SOCKET;
    }
```
(with an explanatory comment; note `shutdown` lowercase is the Winsock function, deliberately not the enclosing `Shutdown()` member — verified by clean compile, no recursion/ambiguous-call error; `SD_BOTH`/`winsock2.h` are the TU's first includes, TcpTransport.cpp:25-26.)

### Harness change (`VS2022/TransportLiveTest.cpp:123-138`)
Extended T7's 5s watchdog to 30s for this verification pass so a clean sub-second exit is distinguishable from a watchdog-papered hang.

---

## Build evidence (real Windows build, VM 192.168.0.240)

Same recipe as T1-T7: `tar` the working tree → scp → powershell → `MSBuild Allegiance.sln /t:Utility;ClintLib;TransportLiveTest /p:Configuration=Debug /p:Platform=Win32`. Ran three times (fixes, then fixes+diag1, then fixes+diag2); **all three compiled clean**, e.g.:

```
===== SURFACE B: Allegiance.sln /t:Utility;ClintLib;TransportLiveTest
SURFACE_B_EXIT=0
```
Compiled units included `Messages.cpp`, `TcpTransport.cpp`, `TransportLiveTest.cpp`; emitted `Utility.lib`, `ClintLib.lib`, `TransportLiveTest.exe`. Only diagnostic present is the pre-existing `ztime.cpp C4172` (returning local address). `grep -c 'error C\d+'` = 0. `shutdown(m_socket, SD_BOTH)` compiled without error or ambiguity → name-hiding question answered: MSVC resolves it to the global Winsock `shutdown`, not a recursive call.

---

## LIVE re-test — verbatim captured output (authoritative)

Freshly started C# server on this host (192.168.0.140:6073, server.json bind → listening, confirmed `ss -tlnp | grep 6073` shows 0.0.0.0:6073). VM exe against it. **All three configurations hang identically.** Verbatim, fixes-only run (from VM `transport_live_t8_run.log`):

```
T7 TransportLiveTest -- FedMessaging over TcpTransport vs 192.168.0.140:6073
C++ FMD_C_LOGONREQ fixed size = 1128 bytes
[t0] calling JoinSession...
[t+] JoinSession hr=0x00000000 connected=1
LOGONREQ cbmsg=1153 sizeof(FMD_C_LOGONREQ)=1128 (C# FixedSize expects 1128)
LOGONREQ GenericSend hr=0x00000000
[pump] iter 0 (ack=0 sessionlost=0)
OnAppMessage: fmid=2 cbmsg=40
  LOGONACK: fValidated=1 fRetry=0 shipID=8098 cookie=0xb5 reason='Welcome T7-LiveTest'
OnAppMessage: fmid=7 cbmsg=56
RESULT: LOGON_SUCCESS
[done] Shutting down...
LIVE_EXIT=0 ELAPSED_MS=30271
```
Logon is fully green (`LOGON_SUCCESS`, fresh shipID 8098). But `[done] Shutdown complete` is **never reached**; `ELAPSED_MS=30271 ≈ 30 s` = the extended 30s watchdog firing. The process does not exit on its own. debug.log is 0 bytes (same as T7 — the FileLogger buffers and the watchdog `ExitProcess` discards it, so it cannot tell us anything).

---

## The key new evidence — WHERE it actually hangs (temporary diagnostics, now reverted)

T7 *reasoned* the hang was "upstream of `TcpTransport::Shutdown` returning." That turned out to be **wrong**. I added temporary console markers inside `TcpTransport::Shutdown()` (reverted before finishing), rebuilt, and re-ran. Two runs:

Run A (markers at entry / join-result / exit):
```
[done] Shutting down...
[diag] TcpTransport::Shutdown ENTER
```
— then hangs; `[diag] join result=...` never prints. `ELAPSED_MS=30260`.

Run B (markers split across shutdown vs closesocket):
```
[done] Shutting down...
[diag] TcpTransport::Shutdown ENTER
[diag] before shutdown()
```
— then hangs; `[diag] after shutdown()...` never prints. `ELAPSED_MS=30263`.

**Conclusion: the hang is AT `shutdown( m_socket, SD_BOTH )` itself (`TcpTransport.cpp:232`), before `closesocket`, before the 500 ms join. It does NOT time out at 500 ms — it never reaches the join.**

What this means:
- The Bug-2 premise ("closesocket alone doesn't reliably unblock a cross-thread recv; shutdown(SD_BOTH) does") is **falsified on this stack**. Empirically, `shutdown(SD_BOTH)` from the main thread does **not** return while the read thread is blocked in `recv()` on that socket — it blocks. (With T7's plain `closesocket` it also hung, ELAPSED_MS=5277 at 5 s.) So on this Windows/MSVC/v145 Winsock stack, a blocking `recv()` in the read thread is not interruptible from another thread via `shutdown()`/`closesocket()` at all — those calls themselves serialize behind the in-flight blocking `recv` and never return.
- Bug 1 (locking `m_msgList.clear()`) is a legitimate latent-race hardening and is worth keeping, but it is not the hang — the read thread is stuck in `recv()`, not in an `OnReceive` push, so it does not hold `m_csMsgList` and the clear is not contended.
- Because the join is never reached, the "read thread still running after 500 ms → leak it and proceed" fallback never executes — the code simply dies inside `shutdown()`.

Answers to the task's explicit questions:
- "Does it still time out at 500 ms?" — No. It never reaches the join.
- "Does it hang somewhere else entirely now?" — Yes, and it always did: **inside `shutdown()`/`closesocket()`, before the join.** T7's "upstream of TcpTransport::Shutdown" placement was incorrect.

---

## Suggested direction for the revisit (not implemented — out of the "two precise fixes" scope)

The blocking `recv()` must be made interruptible. Options, in increasing invasiveness:
1. Read the socket non-blocking (ioctlsocket FIONBIO) and poll with `select()`/`WSAPoll()` on a short timeout, checking `m_fRunning` between polls — the read loop becomes cooperative and exits promptly when `m_fRunning` drops, so `Shutdown()`'s join succeeds and `shutdown()`/`closesocket()` are never called while a `recv` is in flight.
2. Have the read thread close the socket itself on a signal (event/flag), so the thread that owns the blocking `recv` is the one that tears the handle down.
3. Do the socket teardown from the read thread: signal it to exit, and let it be the one to call `shutdown`/`closesocket` after its `recv` returns naturally.

Any of these means `Shutdown()` still sets `m_fRunning=false`, then waits (bounded) for the read thread, then frees. `shutdown(SD_BOTH)` first is not, by itself, sufficient here.

---

## Status

- Branch `port-transport`. **Nothing committed.**
- Working tree carries exactly the three edits above (Bug 1, Bug 2, watchdog 30s); temporary diagnostics fully reverted (verified `git diff` shows only the intended changes).
- C# server started for the test and **stopped afterwards** (`kill` on the `dotnet run` pid; `ss -tlnp | grep 6073` free). `AllegianceCore/` untouched.
- `TRANSPORT_T7.md`'s finding is refined: the hang is now precisely located at `TcpTransport::Shutdown`'s `shutdown()`/`closesocket()` call, not in `FedMessaging::Shutdown` teardown. The race that Bug 1 fixes remains a valid, separate hardening, but neither fix addresses the actual hang.
