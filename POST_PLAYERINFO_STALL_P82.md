# P82 — Post‑PlayerInfo stall / hang diagnostic (native client)

**Status: diagnosis only — no fix. Repo-untouched.** Both `AllegianceCore` and
native `Allegiance` were verified `git status --short` before work (only
pre-existing unrelated `*_P*.md` docs in `AllegianceCore`; the known P77
`Miner.cs` + P66/P68/etc. untracked docs + modified TeamDirector/AllMessages
etc. in `Allegiance`). `Miner.cs`, `PlayerInfo.cs`(+test), `ExportStation.cs`
(+test) untouched. No rebuild of native `Wopr.exe`/`AllegianceInterop.dll`.
No `sudo`. Diagnosis only.

> **Confound declared up front (important):** the live backend that produced
> tonight's 3/3 stall bound its transport directly on `0.0.0.0:37099`. The
> current `AllegianceCore` HEAD (`P80`, last commit `2091201`) binds the
> transport on `127.0.0.1:<ephemeral>` (`Server.cs:266`), which is NOT
> reachable from the VM client (it points at `192.168.0.140:37099`). To run the
> client at all against HEAD I had to relay through a local TCP forwarder
> (`37099 → 127.0.0.1:<ephemeral>`), and I replaced the original direct-bound
> backend (which I had killed to capture its stdout) with a fresh HEAD build.
> So every reproduction below runs client→forwarder→HEAD-backend, a fidelity
> difference from the live session. That forwarder (both bots' connections
> traverse it) is a plausible contributor to some of the drops — especially the
> near-simultaneous double drops — and is called out as such throughout.

---

## TL;DR

- **Reconfirmed with reliable signals only** (same-session `$p.HasExited` +
  `debug.log` growth/`LastWriteTime` + the backend's own `[conn]` log): the
  anomaly is real but **highly intermittent and backend-environment dependent**,
  and my captures stopped EARLIER than the task's described post‑PlayerInfo
  point. I did **not** reproduce the exact "both bots get `PlayerInfo` then
  silence" timing; in every abnormal capture the connections dropped during the
  **logon/mission-join** stage (before `I am ship`/`PlayerInfo`), which is a
  different, earlier slice of the same session-loss family.
- **Mechanism captured verbatim (native `debug.log`):** on an abnormal run the
  client logs `$$MSRGuard:Set:UserName=…` then
  `OnSessionLost. DPlay session terminated.` for BOTH bots, `debug.log` then
  stops growing (frozen at ~1.2–1.4 KB, vs ~9–11 KB for a clean run), the
  managed side prints `[econdemo] all players created; idling until cancelled.`
  and the whole `Wopr.exe` process **silently exits ~2 minutes later** (in‑session
  `HasExited` flips, no crash dialog, no `.dmp`).
- **Live all‑thread stack captured (native + SOS managed):** the "hung" client is
  **NOT spinning, deadlocked, or busy** — it is parked in ordinary CLR waits. The
  SOS capture names it precisely: the main managed thread is inside
  `Wopr.EconomyDemoTest.Run` → `System.Threading.Thread.Sleep`, i.e. the app is
  sitting in its `idling until cancelled` loop. No socket/join/TeamDirector code
  is running at all. The stall is therefore the aftermath of a dropped
  connection, not a lock, deadlock, or spin.
- **Independent server‑side confirmation:** for the anomalous peers the backend
  logs `[conn] peer N joined` then `[conn] peer N left` with **no intervening
  ship-assign/EXPORT/PLAYERINFO lines** (contrast a healthy pair which logs the
  full `[launch]/[join]/[sides]/[create]/[playerinfo]/[export]/[docked]…`
  sequence). The server itself never initiates a protocol disconnect in this
  code (`TcpTransport.cs` only closes on a peer's read-loop ending), so the
  client's socket is the one going away.
- **Not resolved:** the exact post‑PlayerInfo point (as the live session framed
  it) and the ultimate reason the client process self‑terminates ~2 min after
  session loss (the idle loop alone would keep it alive). The drop's origin is
  ambiguous here because my reproduction had to relay through a forwarder (see
  confound). Honest residual and a concrete next step in §6.

---

## 1. Reconfirmation method (reliable signals only)

Per the task's CRITICAL constraint, nothing below concludes anything from a
cross‑session `Get-Process`. Classification used only:

- The **same PowerShell session's** `Start-Process -PassThru` handle →
  `$p.HasExited` (authoritative for process liveness).
- `debug.log` **file growth + `LastWriteTime`** vs `Get-Date` (P81-confirmed
  reliable across SSH sessions).
- The **backend's own log** (`Server.cs:687/698` `[conn] … joined/left`) as the
  independent ground truth for connection state.

Harness scripts (all local, `scp`'d to the VM, run via
`powershell -ExecutionPolicy Bypass -File …`):
`swarm_monitor.ps1`, `swarm_loop.ps1`, `p82_catch.ps1`, `holdloop.ps1`,
`selfkb.ps1`, `selfkb_loop.ps1`, `p82_attach.ps1`, `p82_startcdb.ps1`.

## 2. Reconfirmed hit rate (reliable-signal classifications, this session)

| Time | Backend | Attempt | Outcome (reliable signals) | Evidence |
|---|---|---|---|---|
| 16:5x | orig→HEAD fwd | 1 | CLEAN | log grew to 8255 B, delta ≤6 s |
| 16:00–16:02 | HEAD+fwd | a1 | **STALL** | maxDelta 33.5 s, log froze at 1210 B, alive; server `[conn] … left` |
| 16:0x | HEAD+fwd | a2 | CLEAN | — |
| 16:05 | HEAD+fwd | catch | **EXITED silently** | `HasExited` flipped, no stall first |
| 16:03–16:08 | HEAD+fwd | a1–a3 | CLEAN | full traffic to teardown |
| 16:08 | HEAD+fwd | a4 | **STALL** then silent exit | maxDelta 35.3 s, held; gone before attach |
| 16:22 | HEAD+fwd (warm) | selfkb | CLEAN | |
| 16:24–16:35 | HEAD+fwd (warm) | 8× | CLEAN | |
| 16:50–16:5x | cold HEAD+fwd | 4× | **ATT4 STALL_CAPTURE kb=True** | live stack (§4) |
| 16:5x–17:0x | cold2 HEAD+fwd | 1–5 | CLEAN | |
| 17:10–17:1x | cold2 HEAD+fwd | 8 | **ATT8 STALL_CAPTURE kb=True (SOS)** | managed main = `EconomyDemoTest.Run → Thread.Sleep` (§4) |

Normalising: ~5 abnormal outcomes out of ~33 capable attempts, clustered
heavily in the first few minutes of a freshly booted backend (16:00–16:08 on
the first backend; a cold `selfkb_loop` stalled on attempt 4 and again on
attempt 8 of a second cold cycle). After a backend warms up, 20+ consecutive
clean. This is why the live session (fresh backend each night) saw a high rate
while later loops look "clean": **the rate collapses after backend warm‑up**,
so persistent loop runs under‑sample it. The rate is too small and
environment-dependent to quote a stable number, and the forwarder confound
prevents me from attributing a percentage.

## 3. The abnormal sequence, verbatim

Anomalous pair (cold backend, `selfkb_loop` cycle 2, attempt 4 — the one with
the captured stack). Native `debug.log` tail (also identical in `holdloop a4`):
```
2026-09-05 16:52:02 Mission Initialize id=0 this=4f3fbd8 now=175923798
2026-09-05 16:52:02 Logging on to game server "192.168.0.140"...
2026-09-05 16:52:02 Mission Initialize id=0 this=4fa5be8 now=175924297
2026-09-05 16:52:02 Logging on to game server "192.168.0.140"...
2026-09-05 16:52:22 Timer: 19.768000s spent in transport Send, 19.768000s total
2026-09-05 16:52:22 ^--dpidTo=       1, guaranteed=1, hr=0, 1st message (fmid)=1
2026-09-05 16:52:22 $$MSRGuard:Set:UserName=explorer0@BOT
2026-09-05 16:52:22 OnSessionLost. DPlay session terminated. lastUpdate=175923796 now=175944103
2026-09-05 16:52:25 Timer: 22.589001s spent in transport Send, 42.356998s total
2026-09-05 16:52:25 ^--dpidTo=       1, guaranteed=1, hr=0, 1st message (fmid)=1
2026-09-05 16:52:25 $$MSRGuard:Set:UserName=.Miner1@BOT
2026-09-05 16:52:25 OnSessionLost. DPlay session terminated. lastUpdate=175924297 now=175946910
```
Managed console:
```
[econdemo] EconomyDemoTest.Run(count=2)
[econdemo] Configuration.ServerAddress='192.168.0.140' ServerPort=37099
[econdemo] creating player 'explorer0@BOT' (side 0, commander)
Creating player: explorer0@BOT
[econdemo] creating player '.Miner1@BOT' (side 0, miner)
Creating player: .Miner1@BOT
[econdemo] all players created; idling until cancelled.
```
Server's own log for those same peers — **no handshake progress at all**:
```
[conn] peer 7 'explorer0@BOT' joined
[conn] peer 8 '.Miner1@BOT' joined
[conn] peer 7 left
[conn] peer 8 left
```
versus a healthy pair (peers 17/18), which logs the full
`[launch] … got ship` → `[join] → MISSIONDEF` → `[sides] → MISSIONDEF(38)…`
→ `[create] → EXPORT` → `[playerinfo] → PLAYERINFO(10)…` → `[clusterinfo]` →
`[export] 13 cluster(s)… 44 warp(s), 2 station(s), 134 asteroid(s)` → `[stage]`
→ `[shipstatus]` → `[docked] peer … <- FM_S_DOCKED(61) station 4328` →
`[teamexport]`… chain. Clean runs reach `Station initialize 4328/4329` in the
native log (seen in the pre-existing `debug.log` and in every clean capture);
anomalous pairs never get there.

Reading: the server accepted the TCP connect and greeted both peers, but the
connection vanished **before** the join/export sequence completed. The client's
own transport read then returned EOF (`ReadExactly` `recv==0` → `E_FAIL` →
`OnSessionLost`, `TcpTransport.cpp:494-495`), so the native message loop stops
(generates no further `debugf`, i.e. `debug.log` freezes), the managed
`EconomyDemoTest` idles, and ~2 min later the whole process exits silently (no
crash dialog, no `.dmp`, no WER). The "silence after `PlayerInfo`" in the live
session is the same family but at a slightly later slice; mine consistently
stopped one step earlier (at `MSRGuard`, just before `I am ship`).

## 4. Live all-thread stacks (native + SOS) — this is the decisive evidence

Attached `cdb -pv -p <pid>` in the SAME script that detected the stall
(delta ≥20 s twice), command file `~*kb 9999` (capture #1, `s4_kb.log`) and
`.loadby sos clr; !threads; ~*e !clrstack` (capture #2, `s8_kb.log`). Full text
preserved in `p82_logs/s4_kb.log` and `C:\1\p82\s8_kb.log`.

**Capture #1 (`s4_kb.log`, plain `~*kb`)** — all 9 threads in ordinary CLR waits:
thread 0 (main) `NtDelayExecution←SleepEx←clr←…←mscorlib_ni`, thread‑pool
workers in `NtWaitForWorkViaWorkerFactory`, GC threads in
`WaitForMultipleObjects`. Nothing spinning, no lock held.

**Capture #2 (`s8_kb.log`, SOS)** — `!threads` shows 4 managed threads
(1 main + Finalizer + 2 workers), and the only clean managed stack is the main
thread's:
```
OS Thread Id: 0x454 (0)
Child SP       IP Call Site
004fef38 76fc9e1c [HelperMethodFrame: 004fef38] System.Threading.Thread.SleepInternal(Int32)
004fefb4 730c2e3b System.Threading.Thread.Sleep(Int32)
004fefbc 01011e67 Wopr.EconomyDemoTest.Run(Int32)
004ff074 01010f37 Wopr.Program.Run(System.String[])
004ff2dc 01010894 Wopr.Program.Main(System.String[])
004ff458 74162526 [GCFrame: 004ff458]
```
(The other managed threads printed no `clrstack` — only the main thread had a
clean managed frame; `clr.dll` PDB was absent so `~*e` covered the threads that
resolved.)

**Interpretation:** the main managed thread is blocked in
`System.Threading.Thread.Sleep` inside `Wopr.EconomyDemoTest.Run` — that is the
managed `idling until cancelled` loop. It is literally doing nothing after the
session drop: no socket recv, no `TeamDirector`, no `CreateObject`, no game
loop. The native `debug.log` for that same instant freezes at the
`OnSessionLost` lines and the server shows the peers joined→left with no
handshake. The "stall" is the **aftermath of a dropped connection**, and the
process is *waiting* (not deadlocked). The subsequent ~2-min silent exit is a
separate, unexplained termination (the idle loop alone should keep the process
alive indefinitely).

## 5. What did NOT reproduce / could not capture

- **Post‑PlayerInfo silence:** never reproduced at that exact point; every
  abnormal capture dropped at the logon/mission-join boundary instead. The live
  session's post‑PlayerInfo timing is not contradicted, just not independently
  reproduced with the current HEAD backend + forwarder.
- **The silent exit's cause:** the client's `HasExited` flip ~2 min after
  session-loss (observed twice) has no stack, `.dmp`, or Application-log entry.
  Because `EconomyDemoTest` itself idles ("idling until cancelled"), the exit is
  NOT a graceful managed return — it is a separate termination (second-chance
  exception in a background thread that bypasses `ZAssert`'s dump handler, an
  `Environment.Exit`, or the OS). Candidates documented, not proven.
- **cdb from the very start** (`p82_startcdb.ps1`, `sxe -c "…~*kb;.dump;q" eh; g`)
  did not yield a capture: under the debugger the client never even connected in
  300 s (cdb cdb halts on the CLR loader break and the logo went silent after
  `$><`), so from-start debugging perturbs the very window we need. The
  task's preferred option (a) was instead satisfied via the self-attach
  capture in §4.

## 6. Next step (honest, not-resolved)

Two concrete follow-ups that should converge on the exact line:

1. **Eliminate the forwarder confound.** Rebuild/run the backend so the
   transport binds `0.0.0.0:37099` directly (the task environment's own binding)
   and connect the client with **no relay**. The cleanest path is to revert the
   loopback/ephemeral bind on the deployed backend (a small, deliberately local
   change to `Server.cs:254-267` or a real `ServerConfig` port) — i.e. run the
   *same backend topology the live session used* — and re-measure the hit rate
   and, on a stall, capture both sides: a cdb `-pv` `~*kb` + `.loadby sos clr;
   !threads; ~*e !clrstack` of the client, AND a managed dump of the backend
   (`dotnet-dump collect -p <backend>`; not yet installed) so we can see whether
   the server's per-peer handler is the one that drops, and whether the drop is
   server-side or forwarder-induced. This single change converts every §2/§3
   observation from "possible forwarder artifact" to "the real bug."
2. **Catch the ~2-min silent exit.** Arm `sxe eh` (break-on-process-exit) from
   the start, but fix the §5 perturbation (give cdb a much longer bound, use
   `-g` to skip the loader break, and `-y` for the CLR/SOS symbols). On the exit
   break, `~*kb` + a `.dump /ma` will show both the stuck state and which thread
   faulted/exited the process, closing the last gap (why the process dies).

If instead the premise that HEAD's current backend reproduces the live
post‑PlayerInfo stall is weak (my sample says the rate collapses after warm‑up
and never hit that exact point), item 1 is the necessary first step before any
further capture — otherwise every anomaly remains ambiguously attributable to
the relay.

## Left-behind state
- VM `C:\1\p82\`: `*.ps1` harnesses, `aggregate.txt`, `holdloop.txt`,
  `selfkb_report.txt`, per-attempt `aN.debug`, `s4_kb.log` (the captured stack),
  `s4.debugtail`, `sN.console`, `live_kb.log`.
- Linux `~/p82_logs/`: `backend.log`, `backend_cold.log`, `backend_cold2.log`,
  `forward.log`, `forward.py`, `s4_kb.log`/`s4_kb_preserved.txt`, all `.ps1` sources.
- The running HEAD backend + forwarder are test-only and deletable; the original
  direct-bound backend that I replaced is not recoverable from disk (note in §top).
- Repos `AllegianceCore` and `Allegiance`: byte-for-byte untouched (re-verified
  at session end).

## Out-of-scope honored
- No fix, no code touched, no repo modified.
- `Miner.cs` / `PlayerInfo.cs`(+test) / `ExportStation.cs`(+test) untouched.
- P79/P81's "instant death" re-litigation: none — that was conclusively an
  observation artifact; this is a different, later symptom, and my cross-session
  reliability discipline follows P81's lesson.
- No conclusion drawn from any cross-session `Get-Process`.
