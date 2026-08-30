# TRANSPORT_T7 — flip WOPR onto TCP + live test against the real C# server

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `3758e159` (T7 Part A) then `ff8551e9` (T7 Part B)
Scope: `src/Wopr/Wopr/{Configuration.cs,TeamDirector.cs}`, `VS2022/{Allegiance.sln,TransportLiveTest.vcxproj,TransportLiveTest.cpp}`. `AllegianceCore/` untouched (read-only). `TcpTransport.h/.cpp`, `ITransport.h`, `DirectPlayTransport.*`, `messagecore.h`, `Messages.cpp` untouched (the one suspected layer issue is documented in §5, not fixed here, per task rules).

---

## TL;DR — both parts green, including a REAL runtime result

| Bar | Result |
|---|---|
| Part A — Wopr.sln builds clean (AllegianceInterop + Wopr csproj) | ✅ 0 errors |
| Part B — Utility;ClintLib;TransportLiveTest build+link clean | ✅ 0 errors |
| Part B — live exe actually ran against the real server | ✅ `RESULT: LOGON_SUCCESS` (proven twice) |

**This is the first T1–T5 code to run against something real, and it completes the full FEDMSG logon handshake over TCP against the live C# server.**

The C++ `FedMessaging` + `TcpTransport` (with the app-GUID-independent TCP path) connected to `192.168.0.140:6073`, exchanged a real `FM_C_LOGONREQ`/`FM_S_LOGONACK`, was **validated** (`fValidated=1`), got a **shipID assigned** (fresh id each run: 8097 then 8098), and received the post-logon `FM_S_SET_CLUSTER` (fmid 7). Actual stdout reproduced at the bottom (§4).

One caveat: the running server accepts the connection and assigns ship ids, so the two independent runs returned **different** ship ids (8097, 8098) — each a live assignment, not a cached reply.

---

## Part A — flip WOPR's real call site to config-driven `UseTcpTransport`

Status: **done, committed `3758e159`**.

### Exact diffs (file:line read at edit time)

- `src/Wopr/Wopr/Configuration.cs` — new property following the exact `AppSettings.Get(key)` idiom of `ArtPath`/`ServerName`/`LobbyAddress`:
  ```csharp
  public static bool UseTcpTransport
  {
      get
      {
          var raw = System.Configuration.ConfigurationManager.AppSettings.Get("UseTcpTransport");
          return bool.TryParse(raw, out var v) && v;   // absent/unparseable => false, same as today
      }
  }
  ```
- `src/Wopr/Wopr/TeamDirector.cs:364` — the single real construction site (confirmed by grep: every other `new ClientConnection` in `src/Wopr/` is commented out — `Program.cs:35`, `TeamDirector.cs:310,379`). Changed to the two-arg overload T6 added:
  ```csharp
  AllegianceInterop.ClientConnection clientConnection = new AllegianceInterop.ClientConnection(Configuration.ArtPath, Configuration.UseTcpTransport);
  ```

Default behavior unchanged: `src/Wopr/Wopr/App.config` has no `UseTcpTransport` key, so `bool.TryParse(null,…)` → false → DirectPlay, identical to today.

### Part A build evidence (real Windows build, VM 192.168.0.240)

Because AllegianceInterop links `clintlib.lib`, and `clintlib.lib` only exists in `Allegiance.sln`, the pipeline builds Utility;ClintLib first, then the two Wopr surfaces. All three verbatim:

Surface A.1 — AllegianceInterop (`Wopr.sln`, FZDebug-NoYellowComm|x86):
```
  AllegianceInterop.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\AllegianceInterop\AllegianceInterop.dll
SURFACE_A_EXIT=0
```

Surface A.2 — Wopr csproj (`Wopr.sln /t:Wopr`):
```
  Wopr -> C:\Users\domingo\Allegiance\src\Wopr\Wopr\bin\x86\FZDebug-NoYellowComm\Wopr.exe
WOPR_EXIT=0
```

Dep — `Allegiance.sln /t:Utility;ClintLib` → `CLINT_EXIT=0`, both `.lib`s emitted (evidenced in §3).

`grep -c 'error C\d+' / error MSB / fatal error` across both Part A logs = **0**. The only remaining diagnostics in the AllegianceInterop log are the two benign `LNK4099` PDB warnings for the prebuilt `AllegianceSecurityd.lib` (seen in T2/T6, not errors).

### Host-only build accommodations (extracted copy on 192.168.0.240 only — not git-tree)

1. **Toolset retarget.** The VM registers only v145 and v180 MSVC toolsets. Every `VS2022/*.vcxproj` hardcodes `v143`, and `src/Wopr/AllegianceInterop/AllegianceInterop.vcxproj` FZDebug-NoYellowComm|Win32 hardcodes **`v141_xp`** (others v141). The Prep step replaced `v141_xp`,`v141`,`v143` → `v145` across all `*.vcxproj` on the extracted copy only (`RETARGET_FILES_MODIFIED=57` files). This is the same accommodation T2/T4/T5/T6 documented, extended to cover the v141 family that Part A's Interop config uses.
2. **Stale post-build xcopy disabled.** `AllegianceInterop.vcxproj` has a hardcoded post-build `xcopy ..\objs…\FZDebug\AllegianceInterop\AllegianceInterop.* …\Wopr\bin\FZDebug` that references a directory layout this build never produces (T6 §3 proved it pre-existing). Crucially, that `MSB3073` failure **aborts the Wopr.csproj build** (Wopr has a project reference to the DLL). On the extracted copy only, the `<Command>` was replaced with `rem T7 host-only: stale xcopy disabled` so it cannot abort the dependent C# build. This is a build-environment fix, not a repo change.
3. **Newtonsoft.Json vendored (untracked, not committed).** `Wopr.csproj` (`:124-125`) references `packages\Newtonsoft.Json.11.0.2\lib\net45\Newtonsoft.Json.dll`; the VM has no restored `packages/`, so the C# compile failed with `CS0246 Newtonsoft`. The 11.0.2 net45 DLL was downloaded and dropped at that path in the **working tree only**. `**/packages/*` is gitignored (repo `.gitignore`), so it rides in the build tar but is never committed. This is a dependency-restoration gap, not a code change.

---

## Part B — TransportLiveTest: real TCP handshake against the running C# server

Status: **done, committed `ff8551e9`**. Live runtime = **`LOGON_SUCCESS`** (twice).

### New project (additive)

`VS2022/TransportLiveTest.vcxproj`:
- `<ConfigurationType>Application</ConfigurationType>`, platform Win32, config `FZDebug-NoYellowComm`, toolset **v145** (matches host; not touched by the v143 retarget since it's already v145).
- Console subsystem, no PCH (`NotUsing`), `LanguageStandard` c++14.
- `<ProjectReference Include="Utility.vcxproj">` (same pattern as `mdlc.vcxproj:154`) — pulls `Utility.lib` + transitive `Guids.lib`/`Zlib.lib` via MSBuild, exactly like ClintLib. No hand-listed `Utility.lib`.
- Explicit link deps for the first real `.exe` link step in the series:
  `ws2_32.lib;ole32.lib;uuid.lib;dxguid.lib;rpcrt4.lib;winmm.lib;version.lib;..\src\Lib\steam\steam_api.lib`
  (this is the first place `ws2_32` mattered — every prior task built static `.lib`s). Plus a post-build `xcopy` of `steam_api.dll` beside the exe.
- Added to `VS2022/Allegiance.sln` (project GUID `{7C0E9A5B-2F4D-4C8A-9B3E-1A2B3C4D5E6F}`, + ProjectDependencies on Utility, and Debug/FZDebug-NoYellowComm config mappings → `FZDebug-NoYellowComm|Win32`).

Link iteration history (each fixed, documented):
1. missing `__MODULE__` (zassert.h needs it; added `#define __MODULE__ "TransportLiveTest"` before all includes, mirroring `TcpTransport.cpp:33`) + missing `igc.h` include dir (added `..\src\igc` etc., mirroring ClintLib's include list) + wrong `OnSessionLost` return type (messagecore.h:284 is `HRESULT`, fixed from `void`).
2. `CLOCKS_PER_SEC` undeclared at messagecore.h:187 — added `<time.h>` BEFORE messagecore.h.
3. unresolved externals — `g_rgszMsgNames` (defined via `ALLOC_MSG_LIST;` in the TU, idiom of clintlib.cpp:10/lobbyapp.cpp:20), `timeGetTime` (winmm.lib), `GetFileVersionInfo*` (version.lib), `SteamAPI_*` (steam_api.lib + postbuild copy of the DLL).

### TransportLiveTest.cpp notes

- Real signatures read from source, not guessed: `FedMessaging(IFedMessagingSite*, bool fUseTcpTransport=false)` (messagecore.h:317), `JoinSession(GUID, const char* szServer, const char* szName, DWORD dwPort=6073)` (messagecore.h:321), logon-ack `FM_S_LOGONACK=2` (Messages.h:53) and its `FMD_S_LOGONACK` struct (Messages.h:53-60).
- LOGONREQ built with `BEGIN_PFM_CREATE_PREALLOC` into a zeroed buffer so the STEAM fixed region (which the C# `LogonRequest.Decode` reads at FixedSize=1128) is zeroed; `sizeof(FMD_C_LOGONREQ)==1128` matches `LogonRequest.FixedSize`. `fedsrvVer=MSGVER(216)`, `dwCookie`, `time=Time::Now()`. Sent via `GenericSend` which appends `MemoryCRC` → one FedPacket, exactly what `FedPacket.Unpack` expects.
- Manual `ALLOC_MSG_LIST;` defines the message-name table (consumer-TU idiom).
- 5s watchdog `ExitProcess(rc)` armed after `RESULT` is printed, because teardown hangs (§5). Detached `_beginthreadex` thread.

---

## §4. LIVE RUNTIME — verbatim captured stdout (authoritative)

Reproducible; ship id is freshly assigned each connection. Run on VM 192.168.0.240, connecting to the live server at `192.168.0.140:6073` (independent `Test-NetConnection ... -Port 6073` = `True` from the VM). Full output, verbatim from `transport_live_run.log`:

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
LIVE_EXIT=0 ELAPSED_MS=5277
```

Decoding the wire messages seen (`Messages.h`): `fmid=2` = `FM_S_LOGONACK` (validated → `fValidated=1`, `fRetry=0`, `shipID=8098`, `cookie=0xb5`, failure reason field carried the welcome string); `fmid=7` = `FM_S_SET_CLUSTER` — the post-logon ship assignment. So the C++ side not only logged on but received the follow-on cluster assignment, matching the C# Bot's own post-logon expectation (`SetCluster`, AllegianceCore Bot.cs).

Second independent run produced `shipID=8097` with the same `LOGON_SUCCESS` — confirming the server assigns a fresh ship id each connection (not a cached reply).

### Definition of green — both bars
1. **Everything compiles/links clean.** Part A: AllegianceInterop.dll + Wopr.exe, 0 errors. Part B: Utility.lib + ClintLib.lib (unchanged, re-built clean) + TransportLiveTest.exe links, 0 errors. ✅
2. **Part B exe ran and reported its real result.** `RESULT: LOGON_SUCCESS`, live, reproduced. ✅

---

## §5. FINDING (not fixed here, per task rules): `FedMessaging::Shutdown` does not return after a successful TCP logon

Evidence: after `RESULT: LOGON_SUCCESS` and `[done] Shutting down...`, the process never reaches `[done] Shutdown complete` nor exits. `ELAPSED_MS=5277` on the successful run is the 5s watchdog firing (RESULT printed ~t≈+0.3s, watchdog `ExitProcess` at +5.0s). Two independent runs behaved identically. Details gathered:
- The hang is inside the explicit `fm.Shutdown()` → `FedMessaging::Shutdown` → `TcpTransport::Shutdown` path; nothing between `RESULT` and `Shutdown` prints is missing (all preceding prints present).
- No assert minidump was produced and `debug.log` (created by the Zlib `FileLogger` at static init) is **0 bytes** — so the stuck point did not hit `ZAssertImpl`'s `(*(int*)nullptr)=0`/`GenerateDump` path (Win32app.cpp:114-122). i.e. it is not a failing assert.
- `TcpTransport::Shutdown` (TcpTransport.cpp:209-241) is bounded (~500 ms read-thread join + closesocket), so the block is upstream of that returning — i.e. in `FedMessaging::Shutdown`'s post-transport teardown (CFMConnection deletes / outbox reset / map clears) or a shared-state stall with the still-alive read thread (the `m_msgList.clear()` in FedMessaging::Shutdown at Messages.cpp:1226 is done without `m_csMsgList`, a latent data race with the transport thread's `FMTransportSite::OnReceive` push — a plausible contributor).

Per the task rule ("if the live test reveals an actual bug in that layer, stop, document it precisely and do not fix it in this task"), this is recorded as a finding for a reviewed follow-up. The test-harness workaround is a detached watchdog that `ExitProcess`s 5 s after `RESULT` is printed, so the exe always terminates with its captured output and correct exit code. The transport/messaging layer is byte-for-byte untouched in this task.

---

## §6. Commit note (minor)

Because Part A's commit was made with `git commit -- .` after staging only Configuration.cs/TeamDirector.cs, the working-copy `Allegiance.sln` changes (the +17-line TransportLiveTest project scaffolding) were swept into the **Part A** commit `3758e159` rather than Part B. Functionally immaterial — everything is committed, both halves build independently, and the sln is clean at HEAD. Noted for completeness.

Untracked and intentionally not committed: `packages/Newtonsoft.Json.11.0.2/…` (gitignored build-dep droppings) and the pre-existing `BOOT_PATH.md`.

---

## §7. Status

- Branch `port-transport`. Part A: `3758e159`. Part B: `ff8551e9`.
- `AllegianceCore/` untouched; running server on `.140` not touched. `TcpTransport.*`/`ITransport.h`/`DirectPlayTransport.*`/`messagecore.h`/`Messages.cpp` untouched.
- Nothing unfinished: both halves landed green, including the live `LOGON_SUCCESS`. The only open item is the §5 teardown finding, deliberately deferred per scope.
