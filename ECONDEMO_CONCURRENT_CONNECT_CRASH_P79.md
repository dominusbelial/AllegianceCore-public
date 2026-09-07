# P79 — root-cause hunt for the intermittent 2-bot early-connect crash (diagnosis report)

**Scope:** diagnosis only, no fix attempted. `AllegianceCore` and the native
`Allegiance` repo were left byte-for-byte untouched (verified `git status
--short` against the task's starting state; nothing added or edited in either
tree). `Miner.cs` (P77), `PlayerInfo.cs` + its test (P78) untouched. Native
client `Wopr.exe` and `AllegianceInterop.dll` were NOT rebuilt.

---

## TL;DR

The specific **zero-dump instant-death before "I am ship", 2-bot-only,
~50%-per-run** crash that this task set out to root-cause **did not reproduce**
across a genuine, multi-condition attempt this session (10 raw + 12
Application-Verifier raw 2-bot runs = **22 consecutive 2-bot runs, 0 instant
deaths**; plus zero Wopr/Allegiance Application event-log entries in the whole
window). The task's proposed cross-thread-native-concurrency hypothesis is
therefore **plausible but NOT confirmed** — it is not falsified by the
single-bot control, but I have no crash capture to validate the mechanism, and
I decline to claim a diagnosis I could not observe. Per this project's
convention, that is stated plainly.

What I *did* firmly establish (the closest thing to a real, verifiable finding):

1. **The single-bot control passed decisively** (12/12 survived into "I am
   ship"; no instant death). This is consistent with a 2-bot-specific trigger
   but does not itself prove concurrency.
2. **A separate, previously-unrecognized, fully-deterministic native assert**
   fires on **every** client connection (single- **and** two-bot) at station
   creation and floods the exe directory with `.dmp` files while the process
   stays alive. It is a **swallowed** assert, not the fatal crash. This almost
   certainly accounts for the "got further and died with real `.dmp` files"
   half of the task's P78-observation mix, and it contaminates the "1-2 bot
   intermittent crash" picture with a huge dump-noise floor.
3. Under the `cdb` debugger the station-orientation assert becomes the thing
   `cdb` breaks on (first-chance AV), so a naive "2-bot under cdb shows a
   reproducible crash" reading is a **false positive**: it is the benign,
   always-present station assert, not the target intermittent crash. I nearly
   reported it as the root cause and walked it back by confirming it also fires
   in the single-bot control.

Net: the exact target crash is **not root-caused** (not reproduced). A real,
reproducible native bug is documented below with verbatim evidence; it is worth
fixing but is a different defect from the one sought.

---

## 1. Single-bot control test (step 1 of the task) — result: PASSED

Method: `Wopr.exe -me 1` (confirmed via `Program.cs:104-106` that `-me N` →
`EconomyDemoTest.Run(N)`, and `EconomyDemoTest.cs:27-50` creates **only
`explorer0@BOT` the commander** for `count=1`, no miner — the lone-bot control
the task asked for). Ran 12 attempts, each bounded 45 s, launched detached on
the VM and logged. Every attempt was declared ALIVE at the 45 s cutoff (no
instant exit), and the native `debug.log` snapshots show both "I am ship …" and
"Station initialize 4328/4329" being reached.

```
ATT=1 ALIVE_TIMEOUT age=45.2 ...   (… all 12 the same …)
ATT=12 ALIVE_TIMEOUT age=45.1 ...
```
Verbattim results file: `C:\1\p79_control\results.txt` (12/12
`ALIVE_TIMEOUT`), archived in the capture set below.

A lone bot **never** showed the instant-death pattern across 12 attempts. That
is what the task predicted for a concurrency cause. **But** it is a weak probe:
a single thread cannot exercise any two-thread racing, and it cannot rule out a
server-side-two-peer-ordering cause either. Treat it as: supports, not proof.

---

## 2. What the native debugger actually finds (and the trap it hides)

I reproduced the task's own P78 debug recipe: launch `Wopr.exe -me 2` under
`cdb` (x86, `C:\Program Files (x86)\Windows Kits\10\Debuggers\x86\cdb.exe`),
`-y` for PDBs (`...\bin\x86\FZDebug-NoYellowComm` and
`...\objsv145\FZDebug-NoYellowComm`), `sxe` on access violations to dump all
thread stacks. Seven independent 2-bot captures, and **six of seven faulted on
the identical instruction**:

```
(…).Access violation - code c0000005 (first chance)
...  ZAssertImpl+0x68             [Win32app.cpp @ 117]   <-- the deliberate null-write
...  Orientation::SetUp+0x60      [orientation.h @ 117]  ZAssert(vec.LengthSquared() != 0)
...  Orientation::Set+0x62        [orientation.cpp @ 373/345]
...  Orientation::Orientation+0x1e
...  CstationIGC::Initialize+0x31b [stationIGC.cpp @ 59]  Orientation o(data->forward, data->up);
...  CmissionIGC::CreateObject+0xe00 [missionigc.cpp @ 3364]
...  BaseClient::HandleMsg        [appmsg.cpp FM_S_EXPORT / CreateObject]
```
(Representative verbatim stack — m1.log through m6.log and run3.log are
identical in shape; the faulting thread is one of the two bot message-pump
threads, `SendAndReceiveUpdate -> ReceiveMessages -> OnAppMessage ->
BaseClient::HandleMsg`, exactly as P78 found for its ship assert.)

So `cdb` "reproduces a 2-bot crash" easily — 6/6 of my first captures. **This
looked like the concurrency story being confirmed.** It is not; it is a
constant, deterministic station assert (below). A lone bot hits it too; it just
doesn't kill anything.

### The assert and its data (why it fires every time)

The failing assert is `Orientation::SetUp` in `src/zlib/orientation.h:115-122`:

```cpp
void SetUp(const Vector& vec) {
    ZAssert(vec.LengthSquared() != 0);   // orientation.h:117  <-- fails
    ...
}
```

It is reached from `CstationIGC::Initialize` (`src/Igc/stationIGC.cpp:58-61`),
the new-station branch:

```cpp
Orientation o(dataStation->forward, dataStation->up);   // stationIGC.cpp:59
SetOrientation(o);
```
→ `Orientation(Vector,Vector)` → `Set(fwd,up)` (orientation.cpp:373) →
`SetUp(CrossProduct(rightAxis, forwardAxis))` — which is the zero vector when
the wire-supplied `forward` and `up` are zero or parallel.

I dumped the actual payload out of a full-memory dump (`/ma`) of a faulting
2-bot run (faulting thread's `CstationIGC::Initialize` arg → `data` =
`0x01019204`):

```
dc 01019204 L9 / df 01019204 L9 :
Position  = ( 1120.0, 1560.0, 0.0 )
Up        = (    0.0,    0.0, 0.0 )   <-- degenerate
Forward   = (    0.0,    0.0, 0.0 )   <-- degenerate
Name      = "Garrison", StationId 4328 (fields beyond offset 35 intact/valid)
```

I then decoded the map file `m2xspiderw.igc` itself (the `vizfight explore`
map, Server.cs/Program.cs:132) with the repo's own `MissionMap.Load` and
`DataStationIGC.Decode` (throwaway tool in `/tmp`, not in any repo):

```
map: 2 stations
station 4328 'Garrison' pos=(1120,1560,0) up=(0,0,0) fwd=(0,0,0)
station 4329 'Garrison' pos=(-1840,-1800,0) up=(0,0,0) fwd=(0,0,0)
```

**The map legitimately stores `Up = Forward = (0,0,0)` for both Garrisons.**
The client received those bytes faithfully — it is not a cross-thread race
corrupting the buffer. The `Position` (1120,1560,0) in the live dump matches
map station 4328 exactly. The server (Server.cs:1135-1136) forwards the
immutable map `DataStationIGC` unchanged. So the zero orientation is **data, not
corruption**.

### Why it doesn't kill the process (the "dies with .dmp files" red herring)

`ZAssertImpl` (`Win32app.cpp:105-127`) is **not** a hard abort on this build.
With `g_papp` set it calls `g_papp->OnAssert()` (returns true) then
`OnAssertBreak()` (`Win32app.cpp:376-383`) which runs the deliberate
`(*(int*)nullptr)=0` **inside a `__try/__except` whose filter calls
`GenerateDump()` and returns `EXCEPTION_EXECUTE_HANDLER`**. The exception is
caught, a `.dmp` is written, and the thread continues. So every connection
that creates a Garrison station hits this assert, writes a dump, and keeps
running.

Proof it fires on the **single** bot too (not 2-bot-specific): a lone
`Wopr.exe -me 1` run produced **10 `.dmp` files in 10 s** and stayed alive. A
2-bot run under Application Verifier produced **418 `.dmp` files** and still
survived to my timeout. The exe directory (the dump write path in
`Win32app.cpp:39-65`) accumulates these continuously:

```
Wopr.exe-1.00.00.0000-20260905094054-6964-5040.dmp ...  (many, ~2/sec from both bot threads)
```

### What this means for the task's framing

- The task's "other 3 of 6 runs got much further … before eventually dying with
  real `.dmp` files" is **explained**: those runs are generating these
  station-assert dumps constantly while *remaining alive*. The presence of many
  `.dmp` files is not itself evidence of the fatal intermittent crash; it is
  this always-on assert.
- It is a **real latent bug** worth fixing (the reference engine assumes a
  station export carries a usable orientation; this map violates that, and the
  client's assert turns a wire value into a null-write + dump storm). But it is
  a different defect than the zero-dump instant-death, and it must not be
  reported as the P79 root cause.
- It is also why the naive cdb result ("6/6 2-bot crashes at station init")
  was a trap: it reproduces with a single bot too and never terminates the
  process.

---

## 3. The actual target (zero-dump instant-death before "I am ship") — not reproduced

Reproduction attempts, all on the fixed client binary (`-me 2`, backend
`vizfight explore` on this box, config `192.168.0.140:33151`):

| Condition | Runs | Outcome |
|---|---|---|
| Raw (no Verifier) 2-bot | 10 | all ALIVE ≥22 s, both bots reached "I am ship" + station init |
| Application Verifier Heaps **enabled**, 2-bot | 12 | all ALIVE ≥22 s, same progression |
| Single-bot control | 12 | all ALIVE 45 s |

Across the 22 two-bot runs **no process exited within the early logon window**
(the instant-death the task saw at ~3/6): every run was still running at its
bound and had logged both bots "I am ship" in `debug.log`. The Windows
**Application** event log for the whole ~2-hour window contains **no** Wopr /
AllegianceInterop / ".NET Runtime" / "Application Error" Level-1/2 entries
(queried with `wevtutil`, filtered to the last 2 h) — i.e., nothing reached WER
either.

The task hypothesised the two bots' dedicated native `ClientConnection`s
(each `TeamDirector.cs:544-551` spawns one `Thread`; `CreatePlayerThread`
builds `new ClientConnection(...)` at `TeamDirector.cs:372`, own native
`BaseClient`/`m_fm`/socket) overlap in process-global native state (message
dispatch, `s_cores` static-core cache `missionigc.cpp:2388`, `g_papp`, a shared
allocator). I examined the logon path and the interop boundary and found **each
bot owns a separate native client and its own FedMessenger buffer** — so the
two do not share a socket or a receive buffer. The mechanism would have to be a
process-global native singleton. I could not obtain a live fault at the logon
stage to pin one down, because the crash did not occur.

I also confirmed the project's honest-reading point from the raw runs' native
`debug.log` (kept per-run): the two clients' `Mission Initialize`,
"Logging on…", and `LOGONREQ`-adjacent phases interleave and overlap as the
hypothesis predicts — but in no captured run did that overlap coincide with a
crash before "I am ship".

**Bottom line:** the zero-dump/instant-death mode is either genuinely
rare/timing- or build-condition-dependent (it did not recur across 22 fresh
attempts today), or its trigger depended on state I did not replicate. I did
not observe it, so I report it **unresolved** rather than claim the concurrency
story as proven. The concurrency hypothesis is not contradicted by the
evidence (single-bot never shows it; the two-bot runs are the only ones with
dump-storms and intermittent behaviour), but confirming it will require
catching the actual early fault — see "how to resume" below.

---

## 4. Application Verifier state (and how to change it)

At the end of this session, **Application Verifier `Heaps` is left ENABLED for
`Wopr.exe`** (I re-enabled it; P78 had left it enabled too, and it had since
been removed). Current IFEO state:

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\Wopr.exe
    VerifierDlls  REG_SZ     vrfcore.dll
    GlobalFlag    REG_SZ     0x100
    VerifierFlags REG_DWORD  0x80000001
    ...\Wopr.exe\Heaps-{4D056CEB-D8E3-4b85-B148-B543D56D9BDE}
    ...\Wopr.exe\Core-{E5DCDFAA-3B35-46A8-B370-9D3575A68E53}
```

Disable (full-speed runs):

```
appverif -disable Heaps -for Wopr.exe
appverif -delete Heaps -for Wopr.exe
```
(or `appverif -disable * -for Wopr.exe`). Note: with page heap on, the
station-assert dump storm is amplified (418 dumps in a 12-run batch) and the
client is much slower; it did not reproduce the target crash either way.

`appverif.exe` is at `C:\WINDOWS\system32\appverif.exe` (10.0.28000). The
dedicated SDK "Application Verifier" folder does not exist; the system32 CLI is
full-featured and sufficient.

---

## 5. How to resume chasing the real (unresolved) crash

The crash to catch is the one that kills the process with **no** `.dmp` in the
exe dir and **no** Application event-log entry, at the logon stage. Suggested
next step (not performed — no repro occurred): run `-me 2` under `cdb` with the
`sxe`-script from the capture set below and look for a fault **before** the
first "I am ship" in the log (all seven captured faults today were *after*
"I am ship", at station create). A run that faults at `BaseClient::ConnectToServer`/
`LOGONREQ`/`ReceiveMessages`-time, or a second-chance AV on a thread whose
`~*kb` shows the *other* bot mid-native-init, would be the concurrency smoking
gun. Because `ZAssertImpl` swallows, pay attention to second-chance
(unhandled) exceptions and to WER-less hard tears (stack overflow
`0xc00000fd`, `FailFast`) rather than the swallowed first-chance station
assert.

---

## Left-behind state / artifacts

- Application Verifier `Heaps`: **enabled** for `Wopr.exe` (documented above).
- VM working files (not in any repo): `C:\1\p79_control.ps1`, `C:\1\p79_*.cmd`,
  `C:\1\run_*.ps1`, `C:\1\raw_loop.ps1`, `C:\1\cdb_init.txt`, dirs
  `C:\1\p79_control\`, `C:\1\p79_raw\`, `C:\1\p79_cdb\`. Harmless; can be
  deleted.
- The native client's `debug.log` in the bin dir is a per-run overwrite; each
  captured run's snapshot is archived below.
- Captured logs (copies kept under `/tmp/p79/` on this box): `run3.log`
  (first full fault), `m1..m6.log` (six reproducible station-assert faults),
  `dmp5.log` + `p79_fault.dmp` (full-memory dump used to read the
  `DataStationIGC` bytes), `dmp4.log` (a no-fault-under-cdb run showing
  intermittency even under the debugger), the 10+12 raw `p79_raw\attN.debug`
  snapshots, and the single-bot control `results.txt` (12/12 ALIVE).
- The single-bot/2-bot dump torrent remains in
  `C:\Users\domingo\Allegiance\src\Wopr\Wopr\bin\x86\FZDebug-NoYellowComm\*.dmp`
  (hundreds of files) as the physical evidence of the constant assert; delete
  freely.

## Out-of-scope honored

- No fix. `AllegianceCore` untouched; native `Allegiance` untouched; `Miner.cs`
  (P77), `PlayerInfo.cs` + its test (P78) not disturbed.
- I do not claim the concurrency hypothesis as confirmed; it is explicitly left
  open with the single-bot control as its supporting (weak) evidence.
- The station-orientation assert is reported as a **separate** finding, not as
  the P79 root cause, with the caveat that it likely explains the
  ".dmp noise / died-with-dumps" half of the P78 observation mix.
