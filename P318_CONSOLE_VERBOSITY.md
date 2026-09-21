# P318 — Gate per-tick/per-message console logging behind verbosity (removes a second freeze vector)

## Finding & scope
Review finding **H6** (`allegiance-server-review-glm53-full-codebase.md`): numerous
per-tick/per-message `Console.WriteLine` calls ran unconditionally on the sim
thread — `BroadcastShipsUpdate`'s `[ships]`, `ShipUpdate` dispatch's `[ctrl]`,
`BroadcastShipStates`'s `[teamshipstatus]`/`[shipstatus]`, the per-tick missile
broadcasts (`[missilelock]`, `[missiletrack]`, `[missile-update]`), every refused
inbound message, and several NPC "standing by"/mining lines with no transition
guard. At 150 players / 20 Hz this is ~8–10k formatted console writes/sec on the
sim thread — real CPU/jitter cost, **and** if stdout is a stalled pipe/journal
(`Console.WriteLine` blocks), an independent full-freeze vector with no sockets
involved.

Scope: (1) gate those lines behind a verbosity setting read from
`ServerConfig`, off by default for production; (2) fix the NPC stand-by lines to
log only on state transition; (3) route the still-enabled lines through a
buffered/non-blocking sink so a stalled stdout consumer can't block the sim
thread. Explicitly out of scope: the H3 attacker refusal-flood (P315) and all
other H/M findings.

## Mechanism chosen
- **`ServerConfig.ConsoleVerbose`** (bool, default **false**): when false (the
  production default) the named high-frequency lines are not written at all.
  When true they are emitted. Also exposed as CLI `--verbose`/`--consoleverbose`.
  `Server.Boot` copies it to a static gate.
- **`Allegiance.Core.ConsoleLog`** (new): the shared, static gate + a bounded
  **non-blocking async sink**. Gated lines call `ConsoleLog.Write(...)`, which:
  - returns immediately if `Verbose` is false (silent by default);
  - otherwise `TryAdd`s onto a bounded `BlockingCollection` (8192) drained by a
    single background thread that does the real `Console.WriteLine`. When the
    consumer is stalled the queue fills and further lines are **dropped** —
    the sim thread never blocks, even with verbosity on and stdout stalled.
  - test seams: `TotalWrites` (every write attempt = the pre-fix per-tick
    volume), `EmittedWrites`, `DroppedWrites`, `Flush()`, `ResetForTest()`.
- **NPC transition guard** (`Ship._npcStuckLogged` + `LogNpcStuckOnce`): the
  Miner/Layer/Wingman/Builder "standing by" branches now log only on ENTERING the
  stuck state; the productive branches (mining/selling/layer/wingman/build)
  re-arm it. Mirrors the codebase's own `LogNoPath` (already correct, review's
  own contrast example).
- **What stays synchronous**: the low-frequency event lines (boot/join/kills/
  one-shot handler replies) and the P315 H3 lines (framing failure, unsupported-
  fmid rate-limit summary) — they are not the per-tick flood, and the P315/
  PartMask live tests depend on their synchronous appearance in a captured
  StringWriter. These are excluded from gating by construction.

### Files changed
- `src/Allegiance.Server/ServerConfig.cs` — `ConsoleVerbose` + CLI `--verbose`.
- `src/Allegiance.Server/Server.cs` — `ConsoleLog.Verbose = _config.ConsoleVerbose`
  at boot; 158 per-tick/per-message + refused-inbound lines switched from
  `Console.WriteLine` to `ConsoleLog.Write`.
- `src/Allegiance.Core/ConsoleLog.cs` — new gate + bounded non-blocking sink.
- `src/Allegiance.Core/Sim/Ship.cs` — NPC stand-by lines routed through
  `ConsoleLog.Write`; per-ship `_npcStuckLogged` transition guard.
- `tests/Allegiance.Core.Tests/Transport/ConsoleVerbosityLiveTests.cs` — 3 new
  live proofs (below).

## Live proof (real, measured)
Harness: real in-process server + real loopback bots (24), same code for the
before/after pair; wall-clock measured. `ConsoleLog.TotalWrites` == the pre-fix
per-tick volume (one write attempt per gated line, exactly what old code wrote).

- **Volume, gate default (off):** verbosity ON reproduces the pre-change
  unconditional lines at **~33,000 lines/sec**; verbosity OFF (default) collapses
  the same run to **~34 lines/sec** — a **~970× measured reduction** on the same
  harness. `ConsoleVerbosityLiveTests.Default_gate_off_...`.
- **No freeze on stalled stdout:** with verbosity ON (worst case: every per-tick
  line enabled) and stdout redirected to a `TextWriter` that **sleeps 30 ms on
  every write** (a slow/stalled pipe consumer), the sim thread still advanced the
  full window and healthy peers kept receiving per-tick updates, completing in
  bounded wall time — it does not block. `...Stalled_stdout_does_not_block...`.
- **NPC stand-by on transition only:** driving the real private
  `DecideMiningOrSelling` on a sector-less Miner 200 times (always the stand-by
  branch) emits **exactly 1** "standing by" line, not 200.
  `...Stuck_npc_standby_logs_once_not_per_tick`.

### Must-fail (each live test proven red when its fix is removed)
- Volume test: disabling the `if (!Verbose) return` gate → before == after
  (~33k/s each), reduction assertion **fails**.
- Stall test: making `ConsoleLog.Write` write synchronously (blocking) → the sim
  thread hangs on the stalled stdout, test **times out/fails**.
- NPC test: disabling the `_npcStuckLogged` guard → 200 lines over 200 calls,
  test **fails** (asserts ==1).

## Build / tests
- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.
- `ConsoleVerbosityLiveTests` (new): 3/3 pass in isolation.
- Full `Allegiance.Core.Tests` suite run **2×**: only the documented pre-existing
  flakes appeared. `LiveTurretGunnerBindingTests` (documented persistent flake)
  reproduced in both runs; the other failures rotated between runs
  (LiveChatSectorWireTests, LiveWinConditionTests, LiveMoney, ConquestTerritory,
  CargoBayReload, BotFlight, AsteroidMined) — all pass in isolation, none
  reference any P318 symbol (verified), matching this box's documented live/timing
  flake pattern (P317, P306).

## Scope boundary / exclusions
- H3 attacker refusal-flood (P315) untouched. All other H/M findings are separate
  tasks.
- Native reference (`~/Development/Allegiance`) untouched (read-only).
- Low-frequency event lines and P315/PartMask-asserted lines stay synchronous by
  design (not the flood, and tests require their synchronous capture).
- Deliberate non-faithful simplification vs "make every write async": the bounded
  drop-on-full sink trades worst-case log completeness for the stability
  guarantee the finding demands — a stalled consumer loses log lines, never the
  server.

## Commits
Single commit on `master` (not pushed): `P318: gate per-tick/per-message console logging behind verbosity + non-blocking sink (freeze vector H6)`.
