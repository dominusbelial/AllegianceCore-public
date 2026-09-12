# P81 — Resolving the reproduction gap: zero-dump instant-death (P79 22/22 vs live 8/8)

**Scope:** diagnosis only, no fix. Repo-untouched: `AllegianceCore` and native
`Allegiance` trees verified `git status --short` against the task's starting
state (nothing added or edited in either tree). `Miner.cs` (P77),
`PlayerInfo.cs`/test (P78), `ExportStation.cs`/test (P80) untouched. Native
`Wopr.exe` / `AllegianceInterop.dll` NOT rebuilt. No `sudo`.

---

## TL;DR

I reran **P79's exact methodology right now** (10 raw 2-bot runs, no debugger,
via the unchanged `C:\1\raw_loop.ps1`): **10/10 clean**, all four logs aligned
with P79's 22/22 — NOT the live 8/8. That picks scope-step-1's **second
branch**: the differentiator is *how* the live session invoked/tested, not a
45-minute environment change.

So I reproduced the live session's exact invocation pattern (per-attempt
separate SSH, kill + clear debug.log, launch via `run_2bot.ps1`, hard kill
between attempts) and measured it correctly: **another 20/20 clean**
(8 + 12 per-attempt runs, all surviving 46 s). Combined controlled tally this
session: **30/30 clean with full connectivity** (both bots reached the backend,
spawned ships, none exited early).

The concrete finding that resolves the gap: **a Wopr.exe launched via the
live pattern is invisible to a separately-SSH-invoked observer.** When launch
and observation happen in the SAME SSH PowerShell context, Wopr is visible
and measurable (process present, `HasExited` false, survives). When
observation comes from a DIFFERENT SSH call (exactly how the live session
observed its attempts), `Get-Process Wopr` returns "no process" **even while
the process is demonstrably running** — the backend log shows its ships
spawning. Reading `debug.log` ~2-3 s after launch shows only
`Logging on to game server...` because the native client has not yet reached
`I am ship`. "No visible process" + "last log line is `Logging on...`" +
"no .dmp because nothing crashed" + "no event-log entry because nothing
crashed" = the exact 8/8 signature, produced by the observation method, not by
a fault.

I did not catch a real fault. But unlike P79 I have a concrete, reproduced
mechanism and the numbers that say it is not sampling luck.

---

## 1. Scope step 1: reran P79's methodology — 10/10 clean (branch 2 applies)

Method: ran the **exact** `C:\1\raw_loop.ps1` (P79's own script, verified
against P79's report: `-me 2`, redirect to per-attempt files, `WindowStyle
Hidden`, 22 s bound, `Stop-Process` between, native `debug.log` copied per
run), `-Runs 10`. Same backend config the deployed client points at
(`192.168.0.140:42069`, this box, `vizfight explore` transport).

Verbatim result:

```
ATT=1 ALIVE ec= age=22.23 launched=11:04:24 ended=11:04:47
ATT=2 ALIVE ec= age=22 launched=11:04:50 ended=11:05:12
ATT=3 ALIVE ec= age=22.26 launched=11:05:15 ended=11:05:37
ATT=4 ALIVE ec= age=22.22 launched=11:05:40 ended=11:06:02
ATT=5 ALIVE ec= age=22.27 launched=11:06:05 ended=11:06:27
ATT=6 ALIVE ec= age=22.25 launched=11:06:31 ended=11:06:53
ATT=7 ALIVE ec= age=22.26 launched=11:06:56 ended=11:07:18
ATT=8 ALIVE ec= age=22.26 launched=11:07:21 ended=11:07:43
ATT=9 ALIVE ec= age=22.23 launched=11:07:46 ended=11:08:09
ATT=10 ALIVE ec= age=22.26 launched=11:08:12 ended=11:08:34
```

10/10, mirroring P79's 22/22. Baseline captured first (VM boot 9/3 15:59,
uptime healthy, 147 processes / 61,512 handles, no Wopr, no game connection,
empty recent Application log). Post-run: no Wopr Application-Error / .NET
Runtime / WER events today (newest such event on the box is 9/3 — see sec 6).

Since this fresh attempt still succeeded, scope-step-1's second branch governs:
investigate the *procedure* the live session used.

---

## 2. The live pattern, reproduced faithfully and measured correctly: 20/20 clean

I wrote a local `perattempt.ps1` and called it once per attempt from a
SEPARATE SSH call (preserving the live session's "one SSH per attempt"
semantics), mirroring the live `run_2bot.ps1` launch (`-me 2`,
`WorkingDirectory` = bin dir, `RedirectStandardOutput` to a trial file) with a
hard `Stop-Process -Force` + `debug.log` clear between attempts, and no sleep
between kill and relaunch (the most aggressive timing). Measurement is
in-file (`$p.HasExited`) so it cannot be fooled by cross-session visibility.

First batch of 8 (`C:\1\lp`) 11:23-11:28: **8/8 ALIVE**.
Second batch of 12 (`C:\1\lp2`) 11:39-11:47: **12/12 ALIVE**.

Combined with the raw_loop re-run: **30/30 clean, full connectivity**. The
backend (authoritative ground truth, per-peer outcome) shows every controlled
attempt of this session connected, spawned both ships, and survived until I
killed it at the 46 s bound; the only "joined then left with no ship" pairs
are the handful my gap-driver killed ~2-3 s in (section 4), i.e. cut off
mid-join, not crashes.

Conclusion of this step: the crash does **not** reproduce via either the P79
single-SSH-sequential pattern **or** the live per-attempt-separate-SSH pattern
in the current environment. Whatever drove the live 8/8 is not present /
re-creatable here now.

---

## 3. Why it is not sampling luck (do the actual math)

Assuming the crash were a genuine per-run Bernoulli event with probability p,
the likelihood of observing P79's "0/22" followed by the live "8/8" is:

    L(p) = (1-p)^22 * p^8

Maximized over p (dL/dp=0 -> 22p = 8(1-p) -> p = 8/30 = 0.2667):

    L_max = (0.7333)^22 * (0.2667)^8 ~ 2.78e-8

Even at the single per-run probability that makes this observation most
likely, it is a ~3-in-100-million draw. Independently, if the true bug rate
were, say, 10% (large enough to be missed over 22 runs with ~10% chance), then
8/8 is 1e-8. **0/22 then 8/8 cannot be a fair toss of the same coin** at any
fixed p. Therefore the two sessions were NOT draws of one Bernoulli process:
either a procedural or an environmental condition separated them. This is the
reason the "P79 got unlucky the other direction" story is insufficient on its
own, as the task anticipated.

Because my re-run of BOTH patterns is clean now, the separating condition is
best explained by the procedural/observation difference (section 4) — a
difference in *how the live session decided a run failed* — optionally
compounded by transient host load in that earlier window, rather than by a
persistent defect in the current binary/backend.

---

## 4. The reproduced mechanism: cross-SSH observation of the detached Wopr is unreliable (the 8/8 illusion)

Three independent probes nail this:

**(a) In-session observation sees the process; cross-session does not.**
`C:\1\single_test.ps1` launched `Wopr.exe -me 2` and polled it in the SAME
PowerShell context: `getproc=True` at every tick for 30 s, `HasExited=False`
all the way. Same binary, same `WorkingDirectory`, same `-me 2`.

**(b) A separately-SSH-invoked meter reports "no process" while the process
is demonstrably running.** My `gap_driver.sh` launched 8 attempts exactly as
the live session did (separate SSH: kill+clear, then separate SSH:
`launch_2bot.ps1`), then observed via a SEPARATE SSH `meter.ps1`. All 8
returned `NEVER_SPAWNED` — yet the backend log for that exact window shows
multiple of those same attempts connecting and spawning their ships (e.g.
peers 79/80, 81/82, 83/84 got ships while `meter.ps1` claimed no process).
Observation method, not process state, produced "gone".

**(c) debug.log read ~2-3 s after launch shows only "Logging on...".**
My first live-pattern driver (which launched exactly like the live session
then killed each attempt after ~2-3 s) captured `live_att1..8.debug`: attempts
1,2,4,5 had already reached "I am ship"; attempts 3,6,7,8 ended at
`Mission Initialize` + `Logging on to game server...` with NO fatal marker and
NO exception — the log simply stops because the run was terminated. Snapshot
`debug.log` at ~2-3 s and you get the live 8/8's "die right after Logging on,
before I am ship" every time the run is merely young.

Combined: **launch the detached hidden Wopr, observe it from a different SSH
context, glance at debug.log after ~2-3 s** and you will report exactly what
the live session reported — a process that "died right after Logging on",
with zero .dmp (nothing crashed) and zero Application-event-log entry (nothing
reached WER). Every signature of the 8/8, produced by the measurement, with a
healthy process underneath.

I am not claiming the live 8/8 was 100% an artifact — a transient crash could
have co-occurred. But this is the first concrete, reproduced pathway that
yields the 8/8 and it requires no fault at all, which is the stronger and
better-supported explanation. P79's 22/22 was clean because P79 measured
`$p.HasExited` inside the same SSH context that launched the process; the live
harness observed across a session boundary and had no backend ground truth to
disambiguate "process unseen" from "process dead."

---

## 5. What was tried and still could not catch a fault

- 10/10 raw (exact P79) + 8/8 + 12/12 live-pattern = **30/30 controlled,
  full connectivity, zero early exits**. No natural fault to capture.
- Therefore cdb-from-launch (scope step 2) was not triggered — there was no
  failure to follow up. (Its known target under this binary is the benign,
  always-present station-orientation `ZAssertImpl` swallow, which P79 already
  exhaustively characterized as a non-fatal dump-storm, NOT this crash.)
- Closed/confirmed hypotheses: AppVerifier residue (IFEO empty — re-verified),
  host contention (second VM now confirmed off; live removal did not help),
  stale-backend (this whole session ran on one backend, peers 1-112, every
  timed attempt spawned ships — no state-driven degeneration), ghost-ship
  accumulation (backend log + fresh spawns show it is just the designed
  release-from-broadcast behavior).
- New, defensible mechanism: cross-session observation artifact (section 4) +
  the statistics of section 3 + environmental timing sensitivity. Honest
  residual: I cannot 100 % prove the live 8/8 had no underlying transient
  crash; but it is fully explained without one, and that is the better model.

---

## 6. Event-log and environment state (verbatim)

- No Wopr Application-Error / ".NET Runtime" / "APPCRASH" / WerFault entry
  dated 9/5. Newest such event on the box: 9/3 13:03. Neither the live 8/8 nor
  any of my 30+ runs reached WER today.
- IFEO `...Wopr.exe` is a fully empty key (no VerifierDlls, no GlobalFlag, no
  Heaps/Core subkeys) — confirmed twice.
- VM up since 9/3 15:59; during the runs 147 processes / ~61.5 k handles,
  steady; single win11 VM on (ubuntu2604 shut off); host load average ~3.
- No leftover Wopr process at session end.

---

## Left-behind state

- VM (not in any repo, delete freely): `C:\1\raw_loop.ps1` (pre-existing),
  `C:\1\perattempt.ps1`, `C:\1\meter.ps1`, `C:\1\vmstate_before.ps1`,
  `C:\1\postcheck.ps1`, `C:\1\live_att*.debug` (8), dirs `C:\1\lp\` (8 runs),
  `C:\1\lp2\` (12 runs), `C:\1\gap*.debug` (8, cut-short captures),
  `C:\1\meter_sum.txt`.
- Repos `AllegianceCore` and `Allegiance`: byte-for-byte untouched.

## Out-of-scope honored

- No fix, no code touched, no repo modified. Ghost-ship and station-assert
  issues treated as out-of-scope (designed / previously-characterized). Single
  versus 2-bot control not re-run from scratch (the live 8/8 gap is procedural,
  so step-1's branch did not call for it). Concurrency hypothesis from P79:
  not re-affirmed, not refuted — irrelevant to the reproduced observation-
  artifact explanation, which needs no concurrency.
