# TRANSPORT_T10 — fix `TcpTransport`'s Shutdown hang for real (cooperative read loop)

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `71bff41e` (T9)
Scope: `src/_Utility/TcpTransport.cpp` (non-blocking switch + `ReadExactly` select-poll), `src/_Utility/Messages.cpp` (`m_msgList.clear()` lock), `VS2022/TransportLiveTest.cpp` (watchdog 30s + ELAPSED_MS discrimination). `AllegianceCore/` read-only (started/stopped, verified clean).

---

## TL;DR — build GREEN and the hang is GONE. Committed.

| Bar | Result |
|---|---|
| Clean build `Utility;ClintLib;TransportLiveTest` (Win32, v145, Debug ⇒ FZDebug-NoYellowComm) | ✅ `BUILD_EXIT=0`, `ERROR_MATCHES=0`; `Messages.cpp`/`TcpTransport.cpp`/`TransportLiveTest.cpp` all recompiled clean |
| Live re-test reaches `[done] Shutdown complete` and exits **on its own** | ✅ **3 independent runs**, all `PROCESS_EXIT_CODE=0`, `ELAPSED_MS≈281–297`, wall 327–400 ms — far under the 30 s watchdog, which never fired |

T8's conclusion was correct: the hang was that the read thread blocks forever in a blocking `recv()`, and a cross-thread `shutdown()`/`closesocket()` on this MSVC/v145 Winsock stack **serializes behind that in-flight blocking recv and never returns**. The fix removes the block entirely rather than trying to interrupt it.

---

## The fix as applied (file:line read at edit time)

Note: T8's reported `Messages.cpp:1226` had drifted 2 lines by T9 — the actual `m_msgList.clear()` sits at **`Messages.cpp:1228`**. Everything else lined up with the task's quoted ranges. Trusted the code, as instructed.

### 1. Socket flipped to non-blocking in `Join()` (`src/_Utility/TcpTransport.cpp:193-206`)

Inserted immediately after the Greet-frame write succeeds and before the `m_localPlayerId`/`m_fConnected`/`m_fRunning` assignments and the read-thread spawn:

```cpp
// TcpTransport.cpp:193-206 (inside TcpTransport::Join(), after Greet write :184)
    u_long nonBlocking = 1;
    if ( ioctlsocket( m_socket, FIONBIO, &nonBlocking ) == SOCKET_ERROR )
    {
        DWORD err = WSAGetLastError();
        debugf("TcpTransport::Join: ioctlsocket(FIONBIO) failed (%lu).\n", err);
        closesocket( m_socket );
        m_socket = INVALID_SOCKET;
        return HRESULT_FROM_WIN32( err );
    }
```

`WriteFrame` (the Greet, called just before at `:176`) still ran on a **blocking** socket — only the read path changes, matching the task's intent. The mode flip lands right before the `_beginthreadex` read-thread spawn (`:214`).

### 2. `ReadExactly` rewritten to poll with `select()` (`src/_Utility/TcpTransport.cpp:457-488`)

```cpp
HRESULT TcpTransport::ReadExactly( BYTE* dst, DWORD cb )
{
    // Cooperative: the socket is non-blocking (set in Join), so recv() never
    // blocks indefinitely. select() waits up to c_dwPollMs for readability,
    // then we recheck m_fRunning and loop -- this is what makes closesocket()
    // from Shutdown() reliably interrupt this thread (unlike a blocking
    // recv(), see TRANSPORT_T8.md).
    DWORD off = 0;
    while ( off < cb )
    {
        if ( !m_fRunning )
            return E_FAIL;   // cooperative shutdown noticed -- stop promptly

        fd_set readSet;
        FD_ZERO( &readSet );
        FD_SET( m_socket, &readSet );
        timeval tv;
        tv.tv_sec  = 0;
        tv.tv_usec = (long) c_dwPollMs * 1000;   // well under Shutdown's 500 ms join

        int sel = select( 0, &readSet, 0, 0, &tv );   // nfds ignored on Windows
        if ( sel == SOCKET_ERROR )
        {
            DWORD err = WSAGetLastError();
            if ( err == WSAEINTR ) continue;
            return HRESULT_FROM_WIN32( err );
        }
        if ( sel == 0 )
            continue;   // timeout, no data -- loop back, recheck m_fRunning

        int rc = recv( m_socket, (char*) dst + off, (int)( cb - off ), 0 );
        if ( rc == SOCKET_ERROR )
        {
            DWORD err = WSAGetLastError();
            if ( err == WSAEINTR || err == WSAEWOULDBLOCK )
                continue;   // spurious wakeup or benign race -- retry
            return HRESULT_FROM_WIN32( err );
        }
        if ( rc == 0 )
            return E_FAIL;   // peer closed mid-frame
        off += (DWORD) rc;
    }
    return S_OK;
}
```

Poll interval `c_dwPollMs = 200` defined as a named constant in the file's anonymous-namespace constant block (`TcpTransport.cpp:58-61`), matching the existing style (the file already declares `c_dwMaximumFrame`, etc. there). `select()`'s `nfds` argument is ignored on Windows (documented Winsock behavior).

Why it works: `Shutdown()` still sets `m_fRunning=FALSE` then calls `closesocket()`. With the socket non-blocking and the read thread in `select()`, `closesocket()` wakes the wait (or the poll simply times out after ≤200 ms). Either way the read thread exits within ~1 poll interval — measured ~281–297 ms end-to-end in the harness, well inside `Shutdown()`'s 500 ms `WaitForSingleObject` join.

### 3. `Shutdown()` unchanged — kept plain `closesocket()` (`src/_Utility/TcpTransport.cpp:218-222`)

`shutdown(sock, SD_BOTH)` is **not** re-added (T8 proved it is the specific call that hangs when the read thread is blocked in `recv()`; with a non-blocking read path it is no longer needed). The 500 ms `WaitForSingleObject` join and graceful-timeout logging (`:224-236`) are untouched — they now succeed well within 500 ms instead of always timing out.

### 4. Lock `m_msgList.clear()` (`src/_Utility/Messages.cpp:1227-1232`)

```cpp
// Messages.cpp:1227-1232 (inside FedMessaging::Shutdown())
  EnterCriticalSection( &m_csMsgList );
  m_msgList.clear();
  LeaveCriticalSection( &m_csMsgList );
```

The independently-justified hardening T8 found but couldn't verify: every other access to `m_msgList` holds `m_csMsgList` (the five `FMTransportSite` producers and `ReceiveMessages()`'s drain), but `FedMessaging::Shutdown()`'s clear was the sole unlocked touch. A concurrent clear of a `std::list` while the transport read thread pushes is UB. Style matches `ReceiveMessages()` (`&m_csMsgList` directly). It was applied as part of this task and exercised in the same live runs.

---

## Build evidence (real Windows build, VM 192.168.0.240)

Overlayed the three edited files onto the VM tree (byte-identical hashes confirmed both sides), then ran the task's exact MSBuild command via vswhere-resolved MSBuild 18.9.1 (retarget v143→v145 already applied to the working tree):

```
MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
BUILD_EXIT=0
ERROR_MATCHES=0
===== LOG TAIL (minimal verbosity) =====
  Messages.cpp
  TcpTransport.cpp
  Utility.vcxproj -> ...\FZDebug-NoYellowComm\_Utility\Utility.lib
  ClintLib.vcxproj -> ...\FZDebug-NoYellowComm\ClintLib\ClintLib.lib
  TransportLiveTest.cpp
  TransportLiveTest.vcxproj -> ...\FZDebug-NoYellowComm\TransportLiveTest\TransportLiveTest.exe
  ..\src\Lib\steam\steam_api.dll
  1 File(s) copied
===== ERRORS =====
(none)
===== ARTIFACTS =====
OK ...\TransportLiveTest\TransportLiveTest.exe 08/30/2026 09:05:53
OK ...\_Utility\Utility.lib 08/30/2026 09:05:51
OK ...\ClintLib\ClintLib.lib 08/30/2026 08:24:07
```

0 new errors; the only diagnostic is the pre-existing `ztime.cpp C4172` from earlier tasks (not recompiled here). All three units recompiled against the edits and linked into a fresh `TransportLiveTest.exe`.

---

## LIVE re-test — verbatim output, 3 runs, all clean self-exit

Freshly started C# server on this host (bound to `:6073`, confirmed `ss -tlnp | grep 6073` → `LISTEN 0.0.0.0:6073`, pid 3289188). Harness extended the watchdog 5s→**30 s** and added `ELAPSED_MS` so a clean sub-second exit is unmistakable from a watchdog-fire. Three consecutive runs (shipIDs 8096/8097/8098 confirm each reached the real server — server-side log shows peer join → logon validated → peer left per run):

### Run 1 — `transport_live_t10_run1.log`
```
T7 TransportLiveTest -- FedMessaging over TcpTransport vs 192.168.0.140:6073
C++ FMD_C_LOGONREQ fixed size = 1128 bytes
[t0] calling JoinSession...
[t+] JoinSession hr=0x00000000 connected=1
LOGONREQ cbmsg=1153 sizeof(FMD_C_LOGONREQ)=1128 (C# FixedSize expects 1128)
LOGONREQ GenericSend hr=0x00000000
[pump] iter 0 (ack=0 sessionlost=0)
OnAppMessage: fmid=2 cbmsg=40
  LOGONACK: fValidated=1 fRetry=0 shipID=8096 cookie=0xb5 reason='Welcome T7-LiveTest'
OnAppMessage: fmid=7 cbmsg=56
RESULT: LOGON_SUCCESS
[done] Shutting down...
[done] Shutdown complete. ELAPSED_MS=297 Returning 0
```
`PROCESS_EXIT_CODE=0  WALL_MS=400`

### Run 2 — `transport_live_t10_run2.log` (same tail as run 1, shipID 8097)
```
RESULT: LOGON_SUCCESS
[done] Shutting down...
[done] Shutdown complete. ELAPSED_MS=281 Returning 0
```
`PROCESS_EXIT_CODE=0  WALL_MS=347`

### Run 3 — `transport_live_t10_run3.log` (shipID 8098)
```
RESULT: LOGON_SUCCESS
[done] Shutting down...
[done] Shutdown complete. ELAPSED_MS=281 Returning 0
```
`PROCESS_EXIT_CODE=0  WALL_MS=327`

**Interpretation:** in every run the process printed `[done] Shutdown complete` and returned `0` on its own at ~300 ms of its own elapsed time (harness `ELAPSED_MS`) and 327–400 ms wall-clock — nowhere near the 30 000 ms watchdog, which never fired. Compare T8's failing runs: no `Shutdown complete` line and `ELAPSED_MS=30271` (the watchdog). The read thread now exits within ~1 poll interval instead of blocking forever; `Shutdown()`'s 500 ms join succeeds cleanly. Repeated runs all green, ruling out a fluke.

Server stopped afterward (`[shutdown] clean: 875 ticks, 0 overruns`); `ss -tlnp | grep 6073` → **free**. `AllegianceCore/` verified clean via `git status --short` (empty).

---

## What changed vs. T8 (why this works where T8 didn't)

- T8 tried to **interrupt** a blocking `recv()` with `shutdown(SD_BOTH)` before `closesocket()`. On this stack those calls serialize behind the in-flight blocking `recv` and hang.
- T10 makes the read path **cooperative**: the socket is non-blocking and `ReadExactly` polls `select()` with a 200 ms timeout, rechecking `m_fRunning` each iteration. The read thread exits on its own within ~1 poll of `Shutdown()` clearing the flag, so `closesocket()` is never contending with an uninterruptible `recv`, and the join succeeds. `shutdown(SD_BOTH)` was correctly left out.

---

## Scope guards honored

- Touched only `TcpTransport.cpp`, `Messages.cpp` (both exactly as specified), and the test harness (`TransportLiveTest.cpp`). No restructuring of adjacent code.
- `AllegianceCore/` read-only — server started for the test and stopped after.
- No test server left running (port confirmed free).
- Committed on `port-transport` only, never `master`.

`TRANSPORT_T10.md` closed. Commit on `port-transport` only.
