# TRANSPORT_T6 — thread a "use TCP" choice from WOPR's managed API down to `FedMessaging`

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `fc61046b T5: FedMessaging transport selector (TcpTransport vs DirectPlayTransport), default unchanged`
Scope: **additive** — three constructor levels, exactly six files: `src/clintlib/clintlib.{h,cpp}`, `src/Wopr/AllegianceInterop/{NativeClient,ClientConnection}.{h,cpp}`. `AllegianceCore/` untouched. `TcpTransport.h`/`.cpp`, `ITransport.h`, `DirectPlayTransport.{h,cpp}`, `messagecore.h`, `Messages.cpp` untouched (T1-T5 own that layer). **No existing call site's behavior changes** — every new parameter defaults to (or explicitly passes) `false`, so WinTrek's `BaseClient()` and any existing WOPR C# caller keep DirectPlay exactly as before.

---

## 1. The three-level threading, as implemented

Level 1 — `BaseClient` (native, `clintlib`). Declaration gains a defaulted bool; definition takes it and passes it **only** to `m_fm` (the main game `FedMessaging`); `m_fmLobby`/`m_fmClub` stay on DirectPlay unconditionally.

- `src/clintlib/clintlib.h:841`
  ```cpp
  BaseClient(bool fUseTcpTransport = false);
  ```
  (was `BaseClient();` at 841)
- `src/clintlib/clintlib.cpp:1101` (was `BaseClient::BaseClient(void)` at 1101):
  ```cpp
  BaseClient::BaseClient(bool fUseTcpTransport)
  :
      m_pCoreIGC(CreateMission()),
      ...
      m_fm(this, fUseTcpTransport),   // clintlib.cpp:1117  (was m_fm(this))
      m_fmLobby(this),                // clintlib.cpp:1118  unchanged — DirectPlay
      m_fmClub(this),                 // clintlib.cpp:1119  unchanged — DirectPlay
      ...
  ```
  Every other member initializer (1103–1136+) is byte-for-byte unchanged. `m_fmLobby`/`m_fmClub` deliberately remain `FedMessaging(this)` ⇒ DirectPlay (main game connection `m_fm` only per `TRANSPORT_SEAM.md` §4).

Level 2 — `NativeClient` (native, `AllegianceInterop`). Gains a defaulted bool and now **explicitly** lists `BaseClient` in its initializer list (previously relied on implicit default construction).

- `src/Wopr/AllegianceInterop/NativeClient.h:58`
  ```cpp
  NativeClient(const char * artpath, bool fUseTcpTransport = false);
  ```
  (was `NativeClient(const char * artpath);` at 58)
- `src/Wopr/AllegianceInterop/NativeClient.cpp:57`:
  ```cpp
  NativeClient::NativeClient(const char * artpath, bool fUseTcpTransport)
      : BaseClient(fUseTcpTransport)
  {
      // body unchanged
  }
  ```

Level 3 — `ClientConnection` (C++/CLI managed, the C#-visible entry point). One existing 1-arg ctor kept as a forwarding shim + one new 2-arg ctor carrying the flag.

- `src/Wopr/AllegianceInterop/ClientConnection.h:134-135`:
  ```cpp
  ClientConnection(String ^ artpath);
  ClientConnection(String ^ artpath, bool fUseTcpTransport);
  ```
- `src/Wopr/AllegianceInterop/ClientConnection.cpp:13-24`:
  ```cpp
  ClientConnection::ClientConnection(String ^ artpath)
      : ClientConnection(artpath, false)
  {
  }

  ClientConnection::ClientConnection(String ^ artpath, bool fUseTcpTransport)
  {
      ...
      m_nativeClient = new NativeClient(context->marshal_as<const char*>(artpath), fUseTcpTransport);
      ...
  }
  ```

### Which managed-constructor form I used, and why

I used **C++/CLI constructor delegation** (`: ClientConnection(artpath, false)` in the 1-arg ctor's initializer list, `ClientConnection.cpp:14`). The rationale: this repo already relies on C++/CLI-specific facilities everywhere (`marshal_context`, `__gc`, `^` handles), and the delegation form is the closest match to the classic C# constructor-chaining idiom, so it was the natural first choice per the task brief. It **compiled and linked cleanly** under MSVC v145 in the real Wopr/x86 build (`ClientConnection.cpp` is in the compile list and the DLL linked; no C++ error about the delegating ctor) — so no fallback to the explicit-body form was needed. `ARTTRANSPORT_NOTED`: had delegation errored (it didn't), the documented fallback was two bodies where the 1-arg ctor calls the 2-arg ctor explicitly in its body.

---

## 2. Verbatim build evidence (real Windows build, host 192.168.0.240)

Sync: repo tar (top-level `Allegiance/` component, `--exclude=.git --exclude=Debug --exclude=Release`, 8,699,009 bytes, 1,955 entries); prior `C:\Users\domingo\Allegiance` removed before extracting. Retarget `v143→v145` on the extracted copy only (`RETARGET_OCCURRENCES=64`, same as T5; host lacks v143). MSBuild 18.9.1 via vswhere.

Two surfaces, both in `Allegiance.sln`→`Wopr.sln` mapping: `Debug|Win32` in `Allegiance.sln` maps to `FZDebug-NoYellowComm|Win32` for both `Utility` (`:312-313`) and `ClintLib` (`:420-421`), so surface 1 builds the same config T5 proved. Wopr `FZDebug-NoYellowComm|x86` → `FZDebug-NoYellowComm|Win32` (`Wopr.sln:94-95`).

Build summary block (verbatim from `build_t6.ps1` run):

```
===== MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
===== EXTRACTED
===== RETARGET_OCCURRENCES=64
===== UTIL_CLINT_EXIT=0        <- surface 1 (Utility + ClintLib) green
===== INTEROP_EXIT=1           <- see §3: pre-existing post-build xcopy only
===== ERROR_LINES_1=0          <- zero errors, surface 1
===== ERROR_LINES_2=2          <- exactly the two pre-existing xcopy MSB3073 lines, nothing else
===== PRESENT_UTILITY=True
===== PRESENT_CLINTLIB=True
===== PRESENT_INTEROP_DLL=False  <- false NEGATIVE: my check path had a spurious \x86\; actual DLL exists (§3)
===== RESULT_CHECK_FAIL
===== LOG1=C:\Users\domingo\AppData\Local\Temp\build_t6_surface1.log
===== LOG2=C:\Users\domingo\AppData\Local\Temp\build_t6_surface2.log
```

### Surface 1 — `Allegiance.sln /t:Utility;ClintLib` (Win32, v145, Debug⇒FZDebug-NoYellowComm) — verbatim tail

Full run in `build_t6_surface1.log` (UTF-16LE). Relevant tail, verbatim (only diagnostics + the two target emissions):

```
C:\Users\domingo\Allegiance\src\zlib\ztime.cpp(104,1): warning C4172: returning address of local variable or temporary : timeString [...\ZLibrary.vcxproj]
  ZLibrary.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ZLib\Zlib.lib
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

`UTIL_CLINT_EXIT=0`, `ERROR_LINES_1=0`. The only diagnostic is the pre-existing `ztime.cpp C4172` (seen in T1–T5, unrelated). `clintlib.cpp` recompiled against the new `BaseClient(bool)` — the threading compiles and links clean. **Green.**

### Surface 2 — `Wopr.sln /t:AllegianceInterop` (x86, v145, FZDebug-NoYellowComm) — verbatim

Compile + link of the edited TU's and the DLL emission all succeed; the **only** failures are the two pre-existing post-build `xcopy` MSB3073 lines (§3). Verbatim (trimmed of the Igc/Guids dependency-compile noise):

```
  stdafx.cpp
  AllegianceInterop.cpp
  AssemblyInfo.cpp
  ClientConnection.cpp       <- edited this task; compiled clean
  clintlibWrapper.cpp
  igcWrapper.cpp
  ManagedObject.cpp
  NativeClient.cpp           <- edited this task; compiled clean
  SimpleClient.cpp
  Generating Code...
  .NETFramework,Version=v4.7.2.AssemblyAttributes.cpp
  AllegianceInterop.obj : MSIL module encountered; incremental linking is disabled for MSIL; performing full link
     Creating library ..\..\..\objsv145\FZDebug-NoYellowComm\AllegianceInterop\AllegianceInterop.lib and object ..\..\..\objsv145\FZDebug-NoYellowComm\AllegianceInterop\AllegianceInterop.exp
AllegianceSecurityd.lib(DrmChecker.obj) : warning LNK4099: PDB 'AllegianceSecurity.pdb' was not found ... linking object as if no debug info [...]
AllegianceSecurityd.lib(stdafx.obj) : warning LNK4099: PDB 'AllegianceSecurity.pdb' was not found ... linking object as if no debug info [...]
  AllegianceInterop.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\AllegianceInterop\AllegianceInterop.dll
  0 File(s) copied
  File not found - AllegianceInterop.*
C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Microsoft\VC\v180\Microsoft.CppCommon.targets(166,5): error MSB3073: The command "xcopy C:\Users\domingo\Allegiance\src\Wopr\AllegianceInterop\..\..\objsv141_xp\FZDebug\AllegianceInterop\AllegianceInterop.* C:\Users\domingo\Allegiance\src\Wopr\AllegianceInterop\..\Wopr\bin\FZDebug /s /d /y [...vcxproj]
C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Microsoft\VC\v180\Microsoft.CppCommon.targets(166,5): error MSB3073: :VCEnd" exited with code 4. [...vcxproj]
```

DLL confirmed on host: `objsv145\FZDebug-NoYellowComm\AllegianceInterop\AllegianceInterop.dll`, 3,376,640 bytes, timestamped 2026-08-30 this build. So the thread-through itself **compiles and links green**.

---

## 3. The AllegianceInterop failure is PRE-EXISTING, unrelated to this task — demonstrated

The ONLY errors in surface 2 are the two `MSB3073` lines, both from the project's **post-build `xcopy`** in `src/Wopr/AllegianceInterop/AllegianceInterop.vcxproj:253-254` (FZDebug-NoYellowComm config; identical hardcoded command in every config at lines 216, 235, 254, 307, 325):

```
xcopy $(ProjectDir)..\..\objsv141_xp\FZDebug\AllegianceInterop\AllegianceInterop.* $(ProjectDir)..\Wopr\bin\FZDebug /s /d /y
```

Why it is provably pre-existing and **not** caused by this task:

1. **It does not depend on any of my changes.** The `xcopy` source path is hardcoded to `$(ProjectDir)..\..\objsv141_xp\FZDebug\AllegianceInterop\AllegianceInterop.*`. No build we run here ever produces that directory: our output goes to `objsv145\FZDebug-NoYellowComm\AllegianceInterop\` (v145 toolset, FZDebug-NoYellowComm config). `objsv141_xp\FZDebug` is a *legacy* layout (v141_xp toolset, FZDebug config, `objsv141_xp` prefix) that this checkout's build pipeline never generates. `xcopy` therefore finds nothing (`0 File(s) copied` / `File not found - AllegianceInterop.*`) and returns exit code 4 → `MSB3073`. This fails identically on the pristine repo because the source dir can never exist.
2. **My edited files are not implicated.** `ClientConnection.cpp` and `NativeClient.cpp` both compiled and the DLL linked (evidence above). The only failures are in the post-build `xcopy` command itself, which I did not touch — it is byte-for-byte identical to `HEAD`.
3. **This is the exact pre-existing issue flagged in the task brief** ("a post-build command failure was noted once" in earlier session notes). `grep` on the vcxproj shows the hardcoded `objsv141_xp\FZDebug` target predates and is orthogonal to the transport seam.
4. The two `LNK4099` PDB warnings for `AllegianceSecurityd.lib` are benign (missing PDB for a prebuilt lib), not errors.

Verifying the ONLY 2 error lines are the xcopy ones: `ERROR_LINES_2=2` from `error\s+(C\d+|MSB\d+)`, and the surface-2 log shows exactly those two `MSB3073` lines and nothing else of kind `error C\d+/error MSB\d+`. No `error C` (compiler/linker) at all in either log.

Per the task rules, this pre-existing, unrelated failure does not block the commit; it is documented here honestly rather than claimed as green.

---

## 4. Explicit: nothing passes `true` anywhere yet

Every new parameter is defaulted to `false` (`clintlib.h:841`, `NativeClient.h:58`) or the forwarding shim passes `false` explicitly (`ClientConnection.cpp:14`). `git grep 'fUseTcpTransport' src/` returns only these plumbing sites plus T5's `messagecore.h`/`Messages.cpp` factory — **no call site anywhere passes `true`**. WOPR's C# side now has the *option* (`new ClientConnection(artpath, true)` will eventually reach `m_fm`'s TcpTransport), but nothing uses it yet.

Still OPEN (deliberately, per scope):
- **Flip one WOPR call site** to `fUseTcpTransport = true` once the two-party plumbing is stable.
- **Real live two-party test** of `TcpTransport` dialing the running C# server over an actual socket.

Both are explicit follow-ups for the next task, not this one.

`AllegianceCore/` untouched (read-only). Commit on `port-transport` only.
