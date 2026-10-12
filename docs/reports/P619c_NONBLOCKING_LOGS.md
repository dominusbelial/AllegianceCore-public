# P619c — no log line may ever stall the game thread: non-blocking console output, combat-log spam behind verbose, P619b's parallel-path fix, and a gentler liveness rule for real players

Date: 2026-10-12. Branch: `master`, commit (final, amended with suite numbers),
not pushed.

Motivating capture (the 221 server, 2026-10-12, 195v195 showcase at Alderan,
`native` ship-update model, pool, parallelBuild, server GC,
`ALLEGIANCE_SLOW_TICK_MS=250`):

- P619's `[slowtick]` named the old ~2–3.5 s periodic stall
  (`[slowtick] wall=3612ms … bss.apply=3587ms`): P603's parallel builder wrote
  its per-pair diagnostics with **synchronous `Console.WriteLine`** in the serial
  apply phase, bypassing P318's verbose-gated non-blocking `ConsoleLog.Write`.
  Claude hot-fixed it on 221 (uncommitted `p619b-parallel-verbose-log-gate.patch`);
  ticks went 21–26 → 100–101 per 5 s, p95 18–31 ms.
- In combat the same class of bug remained: `[xexport]` 36,402 lines/20s,
  `[teamloadout]` 12,416, `[projectile]` 5,291, `[lead]` 2,548, `[orderchange]`
  1,782, `[projectile-hit]` 1,565 — all plain `Console.WriteLine`. Ticks fell to
  37–68 per 5 s; `step:crossSideExportSweep` led `[slowtick]` up to 461 ms.
- P611's `NoWriteProgress` rule cut a real player (Domingo's MonoGame client,
  briefly stalled while his workstation was overloaded) together with ~170 bots
  (`[disconnect-summary] window: NoWriteProgress=79/=89`).

This task makes **no log line able to stall the game thread**, gates the combat
spam behind verbose, and softens the liveness rule for real humans while keeping
bots strict.

---

## Scope 1 — apply the P619b patch + regression test

The P619b patch (`~/pending-prompts/p619b-parallel-verbose-log-gate.patch`)
moves the parallel builder's high-frequency per-pair diagnostics
(`[shipstatus]`, `[teamshipstatus]`, `[samestidenpcstatus]`) out of the
unconditional `r.Logs` list (written synchronously in the serial apply phase)
into a new `r.VerboseLogs` list, added only `if (ConsoleLog.Verbose)` and
written with `ConsoleLog.Write` in apply. Applied verbatim
(`git apply`, resolved trivially), and mirrored for the combat tags in scope 3.

Regression test — `ParallelBroadcastByteIdentityTests.
Parallel_broadcast_verbose_off_emits_zero_pair_diag_lines_and_bytes_unchanged`:

- With the verbosity gate at its default (OFF), a parallel broadcast at many
  peers emits **zero** `[shipstatus]`/`[teamshipstatus]`/`[samestidenpcstatus]`
  lines, and the serial-vs-parallel wire bytes are unchanged.
- **Failing-first proven**: reverting the P619b patch makes the test fail
  (`[shipstatus] peer 100 sector change …` appears in the gate-off capture);
  re-applying makes it pass.

## Scope 2 — non-blocking process stdout

`ConsoleLog` is now the single bounded, queued sink for ALL process console
output:

- `ConsoleLog.Install()` (called at the process entry `ServerMain.RunAsync`, NOT
  in `Server.Boot`, so in-process tests that capture `Console.Out` are
  untouched) redirects `Console.Out`/`Console.Error` to a
  `QueuedConsoleWriter` bound to the same `BlockingCollection` and the same
  single background drain thread. After install, a bare `Console.WriteLine`
  anywhere on the sim/broadcast/transport paths costs an enqueue, never I/O.
- The drain emits to the installed writer's **Passthrough** (consulted via the
  `_writer` field, not `Console.Out is QueuedConsoleWriter` — `Console.Out`
  wraps the writer in a `SyncTextWriter`, which is why a naive instanceof check
  recursed; caught by the unit tests). When not installed, the drain writes to
  the current `Console.Out`, so existing tests that capture output via
  `Console.SetOut` are unaffected.
- Bounded queue (8192) + **dropped-line counter** (`ConsoleLog.DroppedWrites`),
  reported per window in the `[egress]` line as `logDrops=N`. The `[egress]`
  window line gained `logDrops={delta}`.
- `ConsoleLog.Shutdown()` (called in `ServerMain.RunAsync`'s `finally`) drains
  every enqueued line and restores the synchronous console — nothing is lost on
  shutdown.

Tests — `ConsoleLogNonBlockingTests` (in the serialized `LiveNetwork`
collection, since they mutate the global console):

- `Installed_plain_WriteLine_never_blocks_a_stalled_consumer_and_drops`:
  200,000 plain `Console.WriteLine`s against a writer sleeping 20 ms per write
  complete in bounded wall time (<5 s) and drop lines (counted) instead of
  blocking.
- `Uninstall_restores_the_original_Console_Out`, `Shutdown_flushes_queued_lines
  ..._and_restores_console`, `Queued_writer_splits_multiline_...`.

And the P318 stalled-stdout claim **extended to plain `Console.WriteLine`** in a
live server (`ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_
not_block_a_stalled_stdout`): a booted server with the redirect active and a
stalling sink keeps the mission clock advancing and healthy peers served in
bounded wall time.

## Scope 3 — gate the combat hot tags behind verbose

Every per-tick / per-pair / per-shot combat diagnostic now goes behind
`ConsoleLog.Verbose` (and only builds the string when on):

- `[xexport]` — `Server.Broadcast.cs` (ship + mine cross-side exports);
- `[teamloadout]` — one aggregate line per change (serial + parallel), moved
  from unconditional `r.Logs` to gated `r.VerboseLogs`;
- `[projectile]`, `[projectile-hit]` — `Server.Combat.cs` (all five sites);
- `[lead]` — the per-peer per-tick lead-indicator line;
- `[orderchange]` — the 15 per-order diagnostic sites in `Server.ShipCommands.cs`
  (the two already-gated sites stayed).

One-time lifecycle lines (join/leave/game-over/stage/warp/leader-set/boot)
stay visible. Lifecycle `[lead]` in `Server.Lobby.cs` (a leadership change) stays
visible; only the per-tick lead-indicator line is gated.

The 390-scale A/B additionally exposed the per-**pair** one-shot identity lines
`[teamexport]`, `[teamplayerinfo]`, `[samestidenpc]` (19k each in a join burst —
the cross-side analogue of `[xexport]`) and the per-part spawn `[loadout]` grant
as the dominant join-burst volume, so those were gated too (the pair lines are
O(N²) at scale; the loadout grant is one per part per spawned ship).

Log-volume harness — `CombatLogVolumeTests` boots a real in-process server,
seats two same-side allies + a cross-side enemy in one sector, arms the subject
with a real weapon and tops up energy/ammo (the `CombatShieldTests.ArmShooter`
recipe) so the authoritative sim sustains many shots, then asserts with the gate
OFF that **zero** of the six hot tags appear and with the gate ON that
`[projectile]` (the deterministic combat tag) **does** appear — the gate is a
real switch. **Failing-first proven**: reverting the `[projectile]` gating to a
plain `Console.WriteLine` makes the gate-off capture fail.

## Scope 4 — `crossSideExportSweep` re-measurement

After scope 3, the `[xexport]` write + string-build (the dominant measured cost —
36,402 lines/20 s, `step:crossSideExportSweep` up to 461 ms) is gone from the
sweep's hot path. Measured in the A/B (scale 8 / 241 peers, verbose off,
slow-tick capture armed at 250 ms): `crossSideExportSweep` appears in a
`[slowtick]` top-8 only **once at 2 ms** (its profiler bucket max is 0.86–0.91
ms, mean 0.01–0.03 ms over the run) — down from leading slow ticks at ~461 ms
in the 221 combat capture. The slow ticks that remain are led by `step:drain`
(the inbound-receive burst, P619's lead hypothesis #1) and `step:sideSelection`,
not the export sweep. Because P604b already phase-staggered the periodic
identity self-heal burst and the gating removed the log-write cost, the sweep
needs no further incremental/staggered change; it is no longer a slow-tick
contributor.

## Scope 5 — gentler liveness for real players (P611 revision)

- **Kept**: `NoWriteProgress` for genuinely dead sockets (still disconnects).
- **Human grace**: at logon, the server marks a connection whose character name
  is NOT under the harness prefix (`mcp-`, `ServerConfig.CharacterNamePrefix` /
  `HarnessPrefix`) as human via `TcpTransport.SetPeerHuman(id, true)`
  (`Server.Dispatch.cs` logon handler). A human peer:
  - gets the longer no-write-progress grace `TcpTransport.HumanGraceTimeout`
    (default 60 s, configurable via `transport.humanGraceSeconds` /
    `ALLEGIANCE_TRANSPORT_HUMAN_GRACE_SECONDS`, bound at boot);
  - is **not** overflow-disconnected (coalescing P606 + non-guaranteed shedding
    already keep its queue bounded; only a truly dead socket — no bytes accepted
    for the longer grace — disconnects);
  - is not cut by a write **timeout** (a workstation hiccup wedging the socket
    mid-frame) in either the thread-per-peer `WriterLoop` or the pool
    `TryWriteHead` — the stalled frame is shed and the Enqueue-path grace is the
    disconnect authority. A hard `SendError` (socket reset/disposal) still
    disconnects anyone.
- **Bots keep the strict limit**: every connection never marked human (all MCP
  bots, all raw/synthetic peers) keeps the strict 30 s `NoWriteProgressTimeout`
  + overflow-disconnect.

Native cite: the 30 s bot limit matches native's link-dead tolerance
`g_sDeadTime = 30` (`clintlib.cpp:2047`). The human grace extends it for real
players (see classification table).

Tests (both paths, in both send-draining modes via the abstract
`TcpTransportSendDropSafetyTestsBase`): `Human_marked_peer_survives_the_bot_
no_write_progress_window` (a marked human survives past a stall that cuts a
bot) and `Unmarked_bot_peer_is_still_disconnected_on_stall` (the strict bot
path is preserved). The existing `NonBlockingSendLiveTests` anti-zombie test
was updated to name its stalled peer `mcp-STALLED` so it exercises the strict
bot path (a non-harness name would now correctly get the human grace).

---

## Verification (only numbers actually measured)

### Build
Whole-solution `dotnet build` : **0 warnings, 0 errors**.

### A/B: ~390 players, stdout through a slow consumer
`scripts/p619c-ab.sh <size_scale> <target> <duration_s> <outdir>` — local server
(`broadcast.parallelBuild=true`, pool transport, `shipUpdateModel=native`,
verbose off by default), showcase spawned, server stdout piped through a
throttled reader (~167 lines/s ceiling, emulating a Docker `json-file`/slow
journal consumer). Because the local `XXV_simple.igc` map has no `Alderan`
sector (that is the 221 showcase map's rendezvous; local known sectors are
Arrokoth…Sedna), the local rendezvous sector used is the map's own `Sedna`, the
same choice P604b/P608 make for local reproductions.

Attempt at scale 13 (~390 players, port-mode) did **not** complete its spawn on
this workstation — the box is capacity-bound in port-mode (P619's port-mode
allocation storm, a P619-domain issue orthogonal to logging); `spawn RESULT` was
`seen=0`. The clean, valid measurement is scale 8 in `native` mode (matching the
221 scenario): **241 peers seated**, verbose off, stdout through the throttled
consumer.

| window set | ticks/5 s | tick p95 | tick max |
|---|---|---|---|
| before contact (settle) | 97–101 | 1–9 ms | <42 ms |
| combat (contact burst) | 40–53 | ~240–280 ms | ~300–700 ms |
| post-contact (recovery) | 100–101 | 8–9 ms | <16 ms |

Console lines/s per tag (verbose off, entire run incl. join burst + combat):

| tag | lines | tag | lines |
|---|---|---|---|
| `[xexport]` | 0 | `[lead]` | 2 (visible lifecycle) |
| `[teamloadout]` | 0 | `[orderchange]` | 0 |
| `[projectile]` | 0 | `[projectile-hit]` | 0 |

The residual ~325 lines/s (ALL=16,253 over the run) is entirely lifecycle/join
output — the spawn `[loadout]` part-grants, join/export/ready, `[warp]`
crossings, and per-peer ack summaries — **none** of it a per-tick/per-shot hot
path. (After this run the dominant per-part spawn `[loadout]` grant line was
additionally gated, cutting that join-burst further.) This is the scope-3
log-volume harness result: with verbose off, the combat hot tags contribute
**zero** lines/s.

Dropped-line count (`[egress] … logDrops=…`): **0 across all 33 report windows**
— even with the stdout consumer throttled to ~167 lines/s, the bounded sink
never dropped a line and the sim thread never blocked (ticks held ~100/5 s
before contact). The non-blocking guarantee (scope 2) is measured: the logging
contributed zero stall; the combat dip is pure 241-peer workload.

### Mandatory live bot check
Run 2's spawn (`size_scale 8`, `target_sector Sedna`, staging off — so bots must
transit real warps the moment they spawn) completed `isError=False`. From the
server's `[warp] ship N crossed from 'A' to 'B' via warp W` lines
(`ServerHost.cs:1012`): **108 distinct ships crossed at least one warp** (left
their home sectors) and **72 distinct ships reached the Sedna target**. The
transport/broadcast changes (human liveness grace, non-blocking console) do not
impede bot navigation — bots leave home and reach the rendezvous (contrast
P606's coalesce-key collision, which left every bot idling).

### Full suite (`scripts/run-tests.sh`, 2 consecutive runs on final code)
Both runs on the final code (commit `1e243a4f` + report/suite-number amend).

**Run 1** — `tests/Allegiance.Core.Tests`: **Failed 6, Passed 3987** (of 3993);
all other assemblies clean:
- Lobby 12/12, ClientCore.MonoGame 1775 (+3 skip), ClientCore 603/603.
- The 6 Core failures — `P366LeaderBotCommandLiveTests.Leader_commands_docked…
  _effectively_live`, `WarpBombTests.Live_whole_mission_warp_bomb…`,
  `StationUnderAttackAlertLiveTests.Sub_30_percent…`,
  `StationUnderAttackAlertLiveTests.Second_hit_within_throttle…`,
  `LiveWinConditionTests.Board_refused_once_mission_over`,
  `GameOverPlayersWireTests.Win_by_elimination…` — are ALL in the documented
  ledger (`docs/TEST_FLAKES.md`; load-flake families). Re-run green in isolation
  (5/6 immediately; `Win_by_elimination` green on immediate re-run), and none of
  the six failing test files reference any P619c symbol.

**Run 2** — `tests/Allegiance.Core.Tests`: **Failed 3, Passed 3990** (of 3993);
all other assemblies clean (Lobby 12/12, MonoGame 1775+3skip, ClientCore 603/603).
The 3 Core failures — `LiveRipcordTests.First_activation_…` (ledgered "suspect
(P472)"), `LiveCountdownStartPingTests.StartGame_owner_all_ready…`, and
`GameOverPlayersWireTests.Win_by_elimination…` — again ledgered live-wire
timing flakes; green in isolation, none reference a P619c symbol.

No run showed a P619c regression: every failure is a ledgered load-flake (green
in isolation), and no failing test file references `ConsoleLog.Install`,
`SetPeerHuman`, `HumanGraceTimeout`, `VerboseLogs`, `DroppedWrites`, or the gated
tags. (Reduced to 2 runs per Domingo 2026-10-09; the flakes are known/ledgered.)

---

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Log a line for every ship/team status export | (native logged sparingly) | era constraint | Route per-pair/per-shot diagnostics through a verbose-gated, non-blocking bounded sink (P318 + P619c); zero cost when off |
| Sync stdout write on the sim thread | (native had no such path) | defect | All console output is a bounded queue + single drain thread; the sim thread only enqueues |
| Link-dead tolerance 30 s | `clintlib.cpp:2047` (`g_sDeadTime = 30`) | design | Kept for bots; humans get a longer configurable grace (default 60 s) |

## Modern alternative considered

- **Native**: sparse logging; clients ran a local IGC sim so the server did not
  stream per-tick diagnostics. The port streams telemetry to renderer-only
  clients, so it generates far more log volume.
- **Modern option**: structured/async logging (Serilog-style) with a bounded
  async queue. **Chosen**: the existing `ConsoleLog` bounded queue + single
  drain thread, unified for all console output — same non-blocking guarantee
  with zero new dependencies, and a dropped-line counter for operability.
- **Liveness**: modern servers use application-level heartbeats + congestion
  shedding. **Chosen**: keep the wire's 30 s link-dead tolerance for bots, grant
  a human grace, and rely on P606 coalescing + non-guaranteed shedding to keep a
  lagging human's queue bounded instead of disconnecting it.
