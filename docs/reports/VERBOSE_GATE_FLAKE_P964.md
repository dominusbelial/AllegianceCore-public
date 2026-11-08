# P964 — `ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines` made deterministic

Base: Claude's local `master` at dispatch (`2cdfd100`). Work is in the P964 worktree
(`allegiance-pi-p964`); nothing is committed or pushed. Line numbers are the working tree at
the end of the task. There is no native counterpart for any of this (test infrastructure +
a console-sink seam), so the parity footer is at the end.

| Item | Verdict |
| --- | --- |
| Cause of the flake | **Not** "the process-global gate under load" (what the ledger said). The LiveNetwork collection is `DisableParallelization = true`, so no other test in the same process can write to `ConsoleLog` while this one runs. The real cause is `ConsoleLog.Flush()` returning when the drain **dequeued** a line instead of when it **wrote** it: 4 of 70 isolated runs failed with `Assert.Contains() Failure … Not found: "[p433-diag]"` while the preceding `Assert.Equal(1, EmittedWrites - emBefore2)` had already passed. |
| Production bug? | **Yes, in a test seam only** — `ConsoleLog.Flush` (`src/Allegiance.Core/ConsoleLog.cs`, old line 187-196) did not honour its own contract ("the background thread has written everything enqueued so far"). No server/broadcast/sim behaviour is changed. Two smaller sink-lifetime defects in the same seam are fixed with it (`ResetForTest` vs a drain still stuck on a stalled sink). |
| Test fix | The proof now asserts on lines/counts **attributable to this run**: a per-run unique marker counted in the capture sink, plus a new per-thread counting scope (`ConsoleLog.OpenProbeForTest`) for the counted-but-not-emitted half. Every count is still exact — no `≥`. |
| Sibling hazard | `P655GameStateIntegrityTests.H31_Verbose_turret_flood_has_bounded_logs_and_still_applies_valid_input` was the only other test asserting a global `ConsoleLog.TotalWrites` delta; it now uses the same scope. `grep -rn "TotalWrites\|EmittedWrites" tests/` finds no other consumer. |
| Gate | 0 warnings/0 errors solution build + `tools/build-tools.sh` (121 projects); both fixed tests **50/50 alone**; 3 × whole `Allegiance.Core.Tests` assembly with parallel Core processes, **0 failures of either fixed test**; AOT publish clean; solution CI filter run **incomplete** (four of five projects: only three ledgered/observed non-task failures, last project still running at hand-off — §4). |

---

## 1. Reproduction and failure classification

All runs on this workstation (24 CPUs, shared with other lanes; load average 7-11 during the
sessions, never synthetic). Scratch logs are in the worktree's ignored `.scratch/p964/`.

### 1.1 Alone (fresh process per invocation, `--filter FullyQualifiedName~Verbose_gate_silences_and_reenables`)

**3 of 50** invocations failed (runs 19, 21, 39), and **1 of 20** more in a second batch taken
while another Core test process was running — **4 of 70**. Every failure has the same signature,
captured verbatim:

```
Assert.Contains() Failure: Sub-string not found
String:    ""
Not found: "[p433-diag]"
   at …ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines()
     in …/Transport/ConsoleLogNonBlockingTests.cs:line 247      (pre-fix line numbering)
```

The assertions *before* it passed, which is the diagnostic: `Assert.Equal(1, ConsoleLog.EmittedWrites - emBefore2)`
had just confirmed the line was **enqueued and counted**, and one millisecond later the capture
sink was still **completely empty** — the line was in flight between the drain thread's dequeue
and its `WriteLine`. That is failure mode A, and it is the only mode observed in isolation.

### 1.2 In one process, with a parallel writer collection

The brief's hypothesis (a parallel test moving `TotalWrites`/`EmittedWrites`) was tested directly
with a scratch collection that wrote `[p433-diag]` lines at ~300k/s from 8 threads while the test
ran (`P964ScratchNoiseTests`, deleted afterwards):

* Single-writer noise (25 invocations): 2 failures; eight-writer noise at ~300k lines/s (15
  invocations): 1 failure; class-level runs with 25 s of noise (8 invocations): 0. **Only mode A**,
  same signature each time (7 mode-A failures in the whole investigation, 0 of mode B).
* The counters were never observed to move during the measured window, and the noise test never
  even saw `ConsoleLog.Verbose == true` or `ConsoleLog.IsInstalled == true` — 0 samples out of
  **7.4 million** — although a control pair of two ordinary collections *did* overlap (893,684
  samples). The LiveNetwork collection is declared
  `[CollectionDefinition("LiveNetwork", DisableParallelization = true)]`
  (`tests/Allegiance.Core.Tests/LiveCombatWireTests.cs:28`), so xunit runs it **exclusively**:
  no other collection in the process can write to `ConsoleLog`, capture `Console.Out` or flip the
  gate while this test runs.

So failure mode B (global-counter attribution) is **prevented by configuration** in
`Allegiance.Core.Tests`, not reproduced by any real parallel test, and could not explain a failure
in the release gate either. It remains a latent hazard only for a **leftover background thread**
from an earlier test in the same exclusive collection (a server sim loop or bot worker that was
never stopped) — which is exactly why the fix removes the dependency on the global counters rather
than leaving it to the collection attribute.

One full `Allegiance.Core.Tests` assembly run before the fix (6,311 tests, 31:15, load ~8) failed
only its known `P739TimerLiveTests` delay-7 case; this test passed there, i.e. the flake is
low-probability per run but responds to preemption pressure — consistent with the sweep/nightly/
release-gate sightings and with the 1-in-20 isolated failures.

## 2. Root cause

`src/Allegiance.Core/ConsoleLog.cs` (pre-fix line numbers):

* `Drain()` — the drain thread takes a line off the bounded queue and then performs the I/O
  (`_writer.Passthrough.WriteLine(line)` / `Console.Out.WriteLine(line)`, pre-fix 173-191).
  Between the dequeue and that write the line is *nowhere observable* but already counted.
* `Flush(timeoutMs)` — pre-fix 193-197 — waited on `_queue.Count > 0`, i.e. on the **queue being
  empty**, which happens at the dequeue. It therefore returned while the last line was still in
  flight, violating its own documented contract ("wait until … the background thread has written
  everything enqueued so far to the current sink"). A test that asserts on the sink right after
  `Flush()` reads it empty. Under load (a full suite, a busy CI runner) the preemption window
  between dequeue and write widens, which is why it also failed in the v0.1.5 sweep and in the
  release workflow's test gate.
* `ResetForTest(joinMs)` — pre-fix 200-212 — completes the queue and joins the drain **with a
  bounded timeout**. When the drain is stuck in a slow sink (the 20 ms/write `StallingWriter` in
  this very class) the join times out and the old drain keeps running against a queue whose
  `Written`/`Enqueued` bookkeeping now belongs to the *previous* test, replaying its backlog into
  whatever sink the **next** test installs. Any "wait for the drain" implemented on top of a
  single global pair of counters would be corrupted by that; it had to be fixed in the same change.

No lost-line, no counter race between `Flush` and `Write`, and no product-path bug was found: the
sanitised gateway (`Write` → bounded queue → one drain) behaves exactly as designed; the defect was
in the test seam's completion signal.

## 3. The fix

### 3.1 `src/Allegiance.Core/ConsoleLog.cs` — sink generations + a real delivery barrier + a test scope

1. **Queue generation** (`private sealed class Gen`, line 280, field `_gen` line 39):
   the bounded queue plus its own `Enqueued`/`Written` counters. `StartDrain(gen)` binds the drain
   thread to the generation it serves, and `ResetForTest` installs a fresh generation. Progress is
   therefore never attributed to the wrong queue, and a superseded drain stops by itself
   (`if (!ReferenceEquals(Volatile.Read(ref _gen), gen)) return;`, line 208) instead of replaying a
   stale backlog into a later test's sink.
2. **`Flush` is a real delivery barrier** (line 229): it captures the enqueue count and waits until
   the drain's `Written` counter — incremented **after** the sink takes the line (line 223) —
   reaches it. `Flush()` now means what its docstring always claimed.
3. **Per-thread counting scope** (`OpenProbeForTest`/`CloseProbeForTest`, lines 250-265;
   `Probe` class line 269): while a test has a scope open on its thread, `Write` tallies
   `Attempts` (every call), `Emitted` (enqueued past the gate) and `Dropped` (gate let it through
   but the queue was full) for **that thread's** calls. `Write` pays one volatile `int` read
   (line 159); the `[ThreadStatic]` scope is only touched while a test holds one. Production
   behaviour is unchanged — the counters are additional observation, not a new path. `ResetForTest`
   closes a leaked scope so it can never count a later test.
4. `QueuedConsoleWriter` enqueues onto the **current** generation (line 343) instead of a queue
   captured in its constructor, so a redirected console survives a `ResetForTest` that replaced the
   queue under an active `Install()`.

### 3.2 `tests/Allegiance.Core.Tests/Transport/ConsoleLogNonBlockingTests.cs`

`Verbose_gate_silences_and_reenables_routed_diag_lines` (line 232) now proves the same behaviour on
lines attributable to this run only:

* a per-run token, `[p433-diag] P964-<guid>` (line 234), included in every line the test issues, and
  `MarkedLines(sink, marker)` (line 283) counts only those;
* OFF: `probe.Attempts == 100`, `probe.Emitted == 0`, `probe.Dropped == 0` and **zero** marked lines
  in the sink — the gated lines are counted but neither enqueued nor written;
* control: a plain `Console.WriteLine` with the gate OFF still reaches the same sink
  (exactly one marked line) — the un-gated lifecycle path is unaffected by the gate;
* ON: `probe.Attempts == 101`, `probe.Emitted == 1`, `probe.Dropped == 0`, and exactly two marked
  lines (the plain control + the routed verbose line);
* the waits use the fixed `ConsoleLog.Flush()`, so "not in the sink" now means "not emitted"
  rather than "not emitted yet".

Every count stayed exact (`Assert.Equal`), nothing was relaxed to `≥`, and the global
`TotalWrites`/`EmittedWrites` deltas are gone.

### 3.3 `tests/Allegiance.Core.Tests/P655GameStateIntegrityTests.cs` — the one sibling with the same hazard

`H31_Verbose_turret_flood_has_bounded_logs_and_still_applies_valid_input` (line 593) asserted
`Assert.InRange(ConsoleLog.TotalWrites - before, 1, 8)` while flipping the global `Verbose` flag —
a process-global delta on a test that boots an in-process server. It now opens a probe scope for the
205 `w.Send(ActiveTurretUpdate …)` calls it makes on its own thread and asserts
`Assert.InRange(probe.Attempts, 1, 8)` (line 610). Same bound, same behaviour, attributable count.

`grep -rn "TotalWrites\|EmittedWrites" tests/` (`src/` only reads `DroppedWrites` for the server's
`[egress]` line) finds no other test: the rest of the file (`Installed_plain_WriteLine…`,
`Shutdown_flushes…`, `Queued_writer_splits…`, `Uninstall_restores…`,
`Installed_concurrent_stdout_and_stderr…`) asserts on its own captured sink with `Contains`, on a
`Shutdown` that joins the drain, or on marker-filtered lines, so they do not share the hazard.
`ConsoleVerbosityLiveTests` measures lines/sec through its own `CountingWriter` (no counter deltas).

## 4. Verification

Workstation: 24 CPUs, shared with other lanes; measured load average 3.9-11.0 during these runs
(no synthetic load was created — parallel `dotnet test` processes only). Raw logs:
`~/allegiance-evidence/P964/`.

| Gate step | Command | Result |
| --- | --- | --- |
| Solution build (0 warnings) | `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors**, 16.7 s |
| Tool projects | `tools/build-tools.sh` | **all 121 tool projects built**, exit 0 (3:28) |
| Fixed test alone ×50 | `dotnet test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~Verbose_gate_silences_and_reenables` | **50 passed / 0 failed / 0 skipped** (final tree). Before the fix the same loop was 47/50 with 3 × mode A. |
| Sibling test alone ×50 | `… --filter FullyQualifiedName~H31_Verbose_turret_flood` | **50 passed / 0 failed / 0 skipped** |
| Whole `Allegiance.Core.Tests` assembly ×3, each with two more Core test processes in parallel | `dotnet test tests/Allegiance.Core.Tests --no-build` (3 concurrent processes, 10 s apart) | **round 1 6306/6311, round 2 6309/6311, round 3 6310/6311** (31:49 / 34:49 / 31:45). The fixed test **passed in all three**; 8 other methods failed once each — all ledgered families, untouched by this change, each re-run 3× alone (7 × 3/3 pass, `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` fail/pass/pass, its existing unscoped-assertion entry). Details in §5 of `docs/TEST_FLAKES.md`. |
| AOT publish | `dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true -o …` | **exit 0**, **0 IL2026/IL3050** (23.7 s) |
| CI filter (the release-gate configuration) | `dotnet test AllegianceCore.slnx --no-build --filter "Requires!=Artwork&Requires!=NativeSource&Lane!=LiveTiming"` | **not a green run, and not finished at hand-off.** `Allegiance.Update.Tests` 28/28 pass; `Allegiance.Lobby.Tests` 142 pass / 3 optional Meshy skips; `Allegiance.ClientCore.MonoGame.Tests` 4477 pass / 3 skips / **1 failed** (`P695HumanShipUpdatesLiveTests.Human_flies_for_ten_minutes…`, NRE in the eject/autopilot path — new observation, **3/3 pass alone**, §5 of the ledger); `Allegiance.Core.Tests` 1823 pass / **2 failed** (the ledgered `P739TimerLiveTests` floating upper bound at delay 7 and delay 5, 3/3 pass alone). The last project (`Allegiance.ClientCore.Tests`, ~80 min of CPU so far) was **still executing** at hand-off (2 h 13 m into the gate, 81 % CPU, still making progress, not hung), so its result is unknown and the gate is reported as **incomplete rather than passed**. Its live log continues in the worktree's ignored `.scratch/p964/ci-filter-solution.log`; the copy at hand-off is `~/allegiance-evidence/P964/ci-filter-solution.log`. |

### 4.1 Negative controls (does the new test still catch the bug it proves?)

Two one-line mutations of `ConsoleLog` were applied, run once, and reverted (the working
tree is the un-mutated fix; `grep -c MUTATION src/Allegiance.Core/ConsoleLog.cs` = 0):

| Mutation | Run | Result |
| --- | --- | --- |
| `Flush` back to the old dequeue barrier (`gen.Queue.Count > 0`) | the fixed test ×50 | **1 failure / 50**: `Assert.Equal() Failure: Values differ / Expected: 2` on the marked-line count (line 270) — the gated line had not reached the sink. This is the original mode A, now caught by the deterministic assertions instead of surviving as a flake. |
| `Write` ignores the gate (`if (!Verbose && Environment.TickCount64 < 0) return;`) | the fixed test ×1 | **fails as required**: `Assert.Equal() Failure: Values differ / Expected: 0 / Actual: 100` on `probe.Emitted` (line 250) — the OFF half still proves the gate. |

## 5. Files changed

* `src/Allegiance.Core/ConsoleLog.cs` — sink generations, delivery-barrier `Flush`, per-thread probe
  scope, generation-aware queued writer (no production behaviour change).
* `tests/Allegiance.Core.Tests/Transport/ConsoleLogNonBlockingTests.cs` — the fixed test (+
  `MarkedLines` helper) and its doc comment.
* `tests/Allegiance.Core.Tests/P655GameStateIntegrityTests.cs` — H31 attributable count.
* `docs/TEST_FLAKES.md` — real cause + resolution for the ledgered entry.

## Native intent

Native Allegiance has no counterpart: this is test infrastructure plus a test-only seam in the
port's non-blocking console sink. Native writes its diagnostics synchronously to the console
(`FedSrv.CPP` log paths) and has no bounded queue, no verbosity gate, no flush barrier and no
"emitted vs dropped" bookkeeping, so there is no native `file:line` rule to port and no parity row
to score. The **observable** behaviour the test protects is still the ported intent of P318/P633:
with the gate off, the per-tick diagnostic lines are not written anywhere (native would print them
unconditionally; the gate is a documented port deviation that removes the freeze vector), and
turning the gate on restores the line-for-line pre-gate output. The seam added here changes nothing
a server does: it only lets a test observe the gate decision and wait for the sink to have taken a
line, which the drain thread already guaranteed — just later than `Flush` used to admit.

## Modern approach

A test that measures a process-global singleton must attribute what it measures, and a "wait until
flushed" primitive must be a **delivery** barrier, not a "queue looks empty" poll. The modern shape,
implemented here: the sink owns a generation (queue + explicit enqueue/written counters), tests
count the lines and write-attempts **they** produced (per-run marker + per-thread scope), and
collection-level exclusivity stays a performance measure rather than a correctness crutch. That
removes the whole class of "it passed alone, failed in the gate" outcomes for this test: the proof
no longer depends on how many other things the machine was doing.
