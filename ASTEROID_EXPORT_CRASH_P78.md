# P78 — root cause of the live 2-bot asteroid-export crash (native client, Application Verifier)

**Scope:** diagnosis only. No fix attempted. `AllegianceCore` untouched;
`~/Development/Allegiance` untouched except this report is not in that repo.
`Miner.cs` (P77 fix) not disturbed; `asteroidigc.cpp` clean (`git diff --stat`
shows nothing for it).

## TL;DR — the root cause

The crash is **not** heap corruption. Under Application Verifier `Heaps`
(full-page heap + guard pages + verifier, all active) the process faulted
with a single, sharp, deterministic **access violation inside the assert
implementation**, not a page-heap overrun/underrun/UAF stop. No verifier
heap stop fired anywhere in the run.

The definitive call chain (verbatim from `cdb`, symbols + source lines):

```
AllegianceInterop!ZAssertImpl+0x68         [src/zlib/Win32app.cpp @ 117]
AllegianceInterop!CshipIGC::ReInitialize+0x324 [src/Igc/shipIGC.cpp @ 118]
AllegianceInterop!CshipIGC::Initialize+0xae    [src/Igc/shipIGC.cpp @ 176]
AllegianceInterop!CmissionIGC::CreateObject+0xe00 [src/Igc/missionigc.cpp @ 3369]
AllegianceInterop!BaseClient::HandleMsg+0x982d [src/clintlib/appmsg.cpp @ 2264]
```

- `ZAssertImpl` (`Win32app.cpp:105-127`), when `!bSucceeded` and `g_papp`
  is null, executes `(*(int*)nullptr) = 0;` (`Win32app.cpp:117`). That is
  the `c0000005` "write to address 00000000". It is the assert's deliberate
  fault, not an out-of-bounds write by the application.
- The assert that fails is **`else assert(false);` at `shipIGC.cpp:118`**
  (inside `CshipIGC::ReInitialize`).

**Why that assert fires** (fully traced, both ends of the wire):

1. `CshipIGC::ReInitialize` starts (`shipIGC.cpp:105`):
   ```cpp
   if (dataShip->baseObjectID == NA)      // NA == -1 (igc.h:471); ObjectID == signed short
       m_pbaseData = NULL;
   else if (m_pilotType == c_ptBuilder)   // 6
       ...
   else if (m_pilotType == c_ptLayer)     // 5
       ...
   else
       assert (false);                    // shipIGC.cpp:118  <-- FIRES
   ```
   It fires whenever `dataShip->baseObjectID != NA` **and** `pilotType` is
   neither builder nor layer.
2. The triggering `DataShipIGC` is built in the native `FM_S_PLAYERINFO(10)`
   handler, `appmsg.cpp:2248-2260`:
   ```cpp
   DataShipIGC ds;
   ds.hullID = NA;
   ds.shipID = pfmPlayerInfo->shipID;            // real ship id
   ds.sideID = NA;
   ds.pilotType    = pfmPlayerInfo->pilotType;   // c_ptPlayer = 10
   ds.baseObjectID = pfmPlayerInfo->baseObjectID; // <-- 0, NOT NA
   ...
   m_pCoreIGC->CreateObject(m_lastSend, OT_ship, &ds, sizeof(ds)); // appmsg.cpp:2264
   ```
3. The backend emits `baseObjectID = 0` at wire offset 64:
   `src/Allegiance.Core/Wire/PlayerInfo.cs` — `w.WriteI16(0); // baseObjectID @64`
   (offset 46 comment), `pilotType = c_ptPlayer (10)` (offset 42).

Because `baseObjectID == 0 != NA (−1)`, `ReInitialize` takes the `else`
branch for a player/miner ship (`c_ptPlayer`=10 / `c_ptMiner`=0 are neither
6 nor 5) and trips `assert(false)` at `shipIGC.cpp:118`.

**Net:** the client asserts that "only builders/layers may carry a non-NA
baseObjectID." The server sends `PLAYERINFO` with `baseObjectID=0` for a
normal `c_ptPlayer` ship, violating that invariant. The assert (rather than
the model's guarded graceful handling) is what turns a benign wire value
into a hard `c0000005` crash.

The `!heap -p -a` walk of the faulting address returned nothing / no heap
segment, consistent with a fault in the code/image (the assert), not inside
allocated heap storage.

## Why the design comment predicted "no crash" — and why it is wrong

`PlayerInfo.cs` (lines 60-65) documents the intent: the codec is sent
*after* the ship `EXPORT`, so `appmsg.cpp:2264`'s `CreateObject` "fails (id
exists) and takes the else branch" (`GetShip(shipID)`), reusing the real
hulled ship and discarding the `PLAYERINFO` dummy — so `baseObjectID=0`
would never reach a ship `ReInitialize`. That assumption is **false**.

`CmissionIGC::CreateObject` (missionigc.cpp:3364-3379) does not fail on the
existing id. It calls `pBase->Initialize(...)` regardless, and
`CshipIGC::Initialize` (shipIGC.cpp:165) detects the pre-existing ship at
line 172 (`pshipReplace = pMission->GetShip(dataShip->shipID)`) and calls
`pshipReplace->ReInitialize(dataShip, now)` at line 176 — which is exactly
the `ReInitialize` whose line-118 assert fires. In the other ordering (no
existing ship) `Initialize` still calls `ReInitialize` at line 183, hitting
the same assert. Either way the PLAYERINFO dummy reaches the `else
assert(false)`.

## Relationship to the pre-rebuild "intermittent / varying-crash-site" observations

Honest assessment (per this project's convention of stating what is *not*
resolved): under Application Verifier the crash is now **deterministic and
identical** across independent runs (two runs, both the exact assert at
shipIGC.cpp:118). The previously observed *variable* crash sites (sometimes
very early, before PLAYERINFO) were NOT reproduced under AppVerifier, and no
heap-corruption stop was found. That earlier variability is therefore not
explained by this finding. Plausibly it reflects the earlier backend/build
emitting different PLAYERINFO values or different export ordering (making
the assert less reliably reachable and other code paths run further), i.e.
the fault was already latent and the current chain simply exposes it
deterministically. I cannot rule out that the earlier build also had genuine
heap corruption on a *different* path; AppVerifier's Heaps check on *this*
reproducible crash shows no corruption. This is not over-claimed.

The two pre-session Windows event-log entries (Sept 1-3) — an
`AllegianceInterop.dll` AV at fault offset `0x0006ec7a` and a
`KERNELBASE.dll` CLR exception `0xe0434352` — are noted but NOT tied to
this crash. The `AllegianceInterop` offset here is `0xa2088`
(`ZAssertImpl`), and the module is ASLR-rebased per process, so a raw
`0x6ec7a` is not directly comparable without that old dump. They are left as
separate history.

## Reproduction environment (so the finding is reproducible)

- Fresh backend: `dotnet run --project tools/vizfight -- explore 8765`
  (spectator/web on `127.0.0.1:8765`; federation transport listener on
  `0.0.0.0:40109`, the port that must go into the client config).
- VM `Wopr.exe.config` rewritten fresh (`scp`) with
  `ServerAddress=192.168.0.140`, `ServerPort=40109`, everything else
  preserved.
- Binary used: the already-rebuilt
  `...\bin\x86\FZDebug-NoYellowComm\Wopr.exe` (v145 toolset; carries the
  uncommitted P77 `Miner.cs` fix; `WOPR_EXIT=0`). Not rebuilt.
- Launch: `Wopr.exe -me 2` spawned by `run_2bot.ps1` (explorer0@BOT
  commander + .Miner1@BOT miner).

## How Application Verifier was obtained (Step 1)

It was **already present** — no install needed. The Windows SDK Debugging
Tools install this session brought it as a sibling. `appverif.exe` is on
PATH at `C:\WINDOWS\system32\appverif.exe` (version **10.0.28000.2114**,
"Application Verifier User Interface Utility"). The dedicated kit folder
`C:\Program Files (x86)\Windows Kits\10\Application Verifier\` does NOT
exist, but the system32 utility is the full CLI and worked. The SDK-only
`winsdksetup` `/features` route was therefore not needed.

## Steps 2-5 — what was actually run and the evidence

Enabled scoped to `Wopr.exe` only:
`appverif -enable Heaps -for Wopr.exe` → IFEO:
```
HKEY_LOCAL_MACHINE\...\Image File Execution Options\Wopr.exe
    VerifierDlls  REG_SZ   vrfcore.dll
    GlobalFlag    REG_SZ   0x100
    VerifierFlags REG_DWORD 0x80000001
    ...\Wopr.exe\Core-{E5DCDFAA-...}
    ...\Wopr.exe\Heaps-{4D056CEB-D8E3-4b85-B148-B543D56D9BDE}
```

Ran under `cdb.exe` (x86 Debuggers kit). Verifier loaded in-process
(verbatim from cdb logo log):
```
Page heap: pid 0x11C4: page heap enabled with flags 0x3.
AVRF: Wopr.exe: pid 0x11C4: flags 0x80000001: application verifier enabled
ModLoad: 73e90000 73ef3000   C:\WINDOWS\SysWOW64\verifier.dll
ModLoad: 73e50000 73e84000   C:\WINDOWS\SysWOW64\vrfcore.dll
```

Crash block (verbatim, first run — identical shape across runs):
```
(11c4.25a0): Access violation - code c0000005 (first chance)
...
EXCEPTION_RECORD:  (.exr -1)
ExceptionAddress: 6fa42088 (AllegianceInterop!ZAssertImpl+0x00000068)
   ExceptionCode: c0000005 (Access violation)
  ExceptionFlags: 00000000
NumberParameters: 2
   Parameter[0]: 00000001
   Parameter[1]: 00000000
Attempt to write to address 00000000
FAULTING_THREAD:  000025a0
WRITE_ADDRESS:  00000000
ERROR_CODE: (NTSTATUS) 0xc0000005 - The instruction at 0x%p referenced memory at 0x%p...
FAILURE_BUCKET_ID:  NULL_POINTER_WRITE_AVRF_c0000005_AllegianceInterop.dll!ZAssertImpl
```

Second run with source-line info (`.symopt+ 0x80000000; .lines`):
```
1766d7b0 6fafe2d4 ... AllegianceInterop!ZAssertImpl+0x68 [Win32app.cpp @ 117]
1766d834 6faf92be ... AllegianceInterop!CshipIGC::ReInitialize+0x324 [shipIGC.cpp @ 118]
1766d868 6fa9a5b0 ... AllegianceInterop!CshipIGC::Initialize+0xae [shipIGC.cpp @ 176]
1766da58 6fa2154d ... AllegianceInterop!CmissionIGC::CreateObject+0xe00 [missionigc.cpp @ 3369]
1766ef24 16d2e7e6 ... AllegianceInterop!BaseClient::HandleMsg+0x982d [appmsg.cpp @ 2264]
...
(managed frames) Wopr.TeamDirector.CreatePlayerThread → SendAndReceiveUpdate
    → ReceiveMessages → OnAppMessage → HandleMsg
```
`!ip2md` resolved the managed frames to
`Wopr.TeamDirector.CreatePlayerThread(System.Object)` calling
`AllegianceInterop.ClientConnection.SendAndReceiveUpdate()` →
`NativeClient.SendAndReceiveUpdate` → `OnAppMessage` → (P/Invoke stub)
`BaseClient::HandleMsg`. So it runs on the managed player-creation thread the
moment it drains the JOINED_MISSION / PLAYERINFO burst.

## Left-behind state

- **Application Verifier `Heaps` is left enabled** for `Wopr.exe` (per task:
  fine, useful for whoever fixes this next). To disable for full-speed runs:
  ```
  appverif -disable Heaps -for Wopr.exe
  appverif -delete Heaps -for Wopr.exe
  ```
  (equivalently `appverif -disable * -for Wopr.exe`).
- The background `vizfight explore 8765` backend on this box was started for
  reproduction and left running (harmless; kill with
  `pkill -9 -f "vizfight explore"` if unwanted).

## Verbatim log files kept

- `~/Development/Allegiance/../` is not where logs live; they are on the VM:
  - `C:\Users\domingo\p78_av_2.log` / `.out` — first deterministic run
  - `C:\Users\domingo\p78_av_3.log` — run with source-line resolution
  Copies staged at `/tmp/p78logs/p78_av_2.log`, `p78_av_3.log`.

## Out-of-scope notes honored

- No fix made (root cause + report only).
- `Miner.cs` (P77) untouched; `asteroidigc.cpp` shows no diff; no `git
  clean`; no untracked files deleted.
- Nothing in `AllegianceCore` modified (verified `git status --short` clean
  apart from its pre-existing untracked `*_P*.md` files).
