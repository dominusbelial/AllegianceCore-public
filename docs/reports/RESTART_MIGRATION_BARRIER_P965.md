# P965 — Restart migration barrier

P963-01 is reproduced and fixed. Base: `6d43a6f2`, branch `codex/p965`.
Changes remain uncommitted in the working tree; no pushes or remote-host work.
The report was created before qualification. Builds and the new correctness
checks pass. The complete light gate remains red on existing bundle-path
fixtures and the P807 phase-time budget; all failures received isolated checks
and are recorded below and in `docs/TEST_FLAKES.md`.

## Classification and native rule

**Bug in the port.** Deferring migration is a modern implementation choice;
starting with an incomplete reset is neither an intentional design difference nor
an era constraint. Native flushes GAME_OVER, resets every player's score state,
vacates the old world and resets sides/the mission in one synchronous call
(`FedSrv/fsmission.cpp:3433-3461,3490-3527`). It arms the next restart's absolute
`timeStart` only after that reset (`:3539-3547`). The next update evaluates
restart/auto-start (`FedSrv/FedSrv.CPP:8178-8229`) and the strict Started gate
(`:8234-8241`). All cited locations were checked against the local read-only
native source. Native sets NotStarted at `fsmission.cpp:3490`, *before*
ResetMission at `:3527`; the relevant guarantee is synchronous completion before
another start evaluation, rather than that internal assignment's position.

## Reproduction before the fix

The new `P965RestartMigrationBarrierTests` ran against the unchanged production
server first: **0 Passed / 2 Failed / 0 Skipped**. Each case connects actual
loopback `ConnectionSession` clients, enables `GameConfig.AutoRestart`, disables
P660 AutoNextMatch, and uses `RestartCountdownSeconds = 0` (the minimum
nonnegative value accepted by GameConfig; no validation rejects zero).

| Peers | Starting | Started | Final reseat/join completion | Result |
|---|---|---|---|---|
| 24 | migration tick 1 | tick 2 | tick 20 | early start; join bursts arrive Started |
| 64 | migration tick 1 | tick 2 | tick 35 | early start; join bursts arrive Started |

Before the first prep batch, the server logged countdown/Started broadcasts to
**zero** peers. It then prepared all peers, reseated them in the existing ordered
batches, and sent their join bursts. Each joiner was told Started and received
its individual join-sync ENTER_GAME while later migrants were still waiting.
The wire recorder reports ENTER_GAME after only 1/24 or 1/64 completed join
markers for the first recipient. This is a deterministic ordering failure,
without artificial CPU load or a slow transport.

Every migrant's reseat and full join-function completion is recorded in the
server console; a separate publication-sink recorder captures GAME_OVER,
JOINED_MISSION, the final join GAME_STATE marker, Starting/Started and ENTER_GAME
in sequence. Guaranteed per-peer sends preserve publication order. Server-side
stage samples independently detect transitions even when there are zero current
recipients. Raw before evidence:
`.scratch/p965/base-repro/base-repro.trx` and `.scratch/p965/base-repro.log`.

## Fix and event order after the fix

All behavior changes are in `src/Allegiance.Server/Server.Lobby.cs`:

- `HasPendingRotation` at **:848** checks the fresh mission's object identity
  against *every* queued tail. It does not block unrelated missions.
- `TryAutoStart` (**:1903**), the common manual/ready countdown entry
  `StartCountdownAt` (**:2081**), `AdvanceStartCountdowns` (**:2127**) and
  `AdvanceAutoRestarts` (**:2152**) hold while that tail exists. The auto-restart
  guard also prevents cancellation/warnings based on a partially migrated roster.
- The debug auto-start call moves after migration setup (**:786-788**), so it
  cannot start a vacant fresh mission before its tail is registered.
- `FinishPendingRotation` removes the fully finished tail, then retries readiness
  once (**:1214-1220**). Preserved ready flags therefore work without another
  ready edge, including the existing synchronous handoffs of eight or fewer peers.
- `StartCountdownAt` takes an absolute target (**:2081-2095**).
  `AdvanceAutoRestarts` forwards the original restart deadline (**:2196-2200**),
  or that deadline plus P660's existing 15-second briefing. Waiting never
  extends either deadline or starts a fresh full briefing after an overdue hold.
  Strict expiry and the existing SimPass order remain in place.

Compact event-order receipts: [validation.json](P965_assets/validation.json).

P765's batch size of eight, latch sweep, preparation, leadership-preserving
reseating, burst phases, FIFO drain and transport are unchanged. The barrier
lasts through trailing readiness/unlock/countdown work. No new performance
path, configuration, service, environment variable or flag was added.

The first fixed 24/64-peer run passed **2/0/0**; the expanded seven-case pilot
passed **7/0/0**. The same migration takes 20 and 35 ticks. Every peer gets
GAME_OVER before reset; every reseat finishes before the first burst; all full
bursts finish in NotStarted before Starting; the next driver pass announces
Starting to all 24/64 peers, and the following strict-expiry pass announces
the Started transition to all of them. On the wire the existing order is
Starting → ENTER_GAME → MISSION_STAGE Started, after every migrant's final
join GAME_STATE. The original auto-restart target is retained
exactly, including when it is already expired. Evidence:
`.scratch/p965/expanded-pilot/expanded-pilot.trx`.

The seven cases also cover P660 absolute intermission + briefing targets both
before and after briefing expiry; preserved-ready and common manual start paths
with debug off/on; an independent mission starting while migration is pending;
and an injected already-armed countdown whose expired target cannot complete
until migration finishes. Tests use the current pool/native/snapshot/encode-once/
parallel-build/native-GotoPlan defaults, explicitly keep interest tiers off,
and qualification requests server GC. P807's unchanged 394-peer fixture retains
its explicitly documented historical interest-tier calibration.

## Existing handoff guarantees

P807/P960's tests are unchanged. The 394-peer case continues to check every
GAME_OVER precedes that peer's reset, all peers retain their sides, every peer
gets a fresh ship in the new mission, migration is sliced across ticks, and the
existing phase/setup/simulation budgets. The two-handoff case still proves two
complete deferred handoffs with a real ready/countdown start between them.
The 24-peer two-handoff case passes **3/3**. The 394-peer timing assertion
remains red: initial changed-tree phase samples are **104.5 / 86.4 / 41.0 ms**
against **76.5 / 78.0 / 78.1 ms** budgets. Alternating three unchanged-base and
three changed-tree controls yields **base 2 passed / 1 failed**, **changed
0 passed / 3 failed**. The failing base phase is **89.4 ms** with **76 ms** GC
pause on its matching tick; changed follow-up phases are **90.2 / 109.3 /
93.4 ms** with **79 / 97 / 81 ms** GC pauses. The initial changed failures
record **92 / 75 ms** GC pause. Every invocation passes correctness before
its budget assertions; no wait hangs or sim-pass failures occur. These small
workstation controls reproduce the existing timing family; they are not a rig
performance A/B or a claim of equal failure rates. Migration batch sizes, join-burst work and
timing budgets remain unchanged, per scope. See the prior
[P960 evidence](P807_HANDOFF_HANG_P960.md) for the same final 3/3 timing failures.

P960's bounded waits and receipt-only pumping remain intact; this task does not
claim to identify its historical workstation hang. P660 covers its intermission,
briefing, preserved-ready and consecutive-match paths unchanged.

## Qualification

`local.env` was already present and artwork is loaded. Logs/TRX are in the
ignored `.scratch/p965/` directory; external evidence storage is outside this
lane's writable roots. Final Passed / Failed / Skipped totals follow below.

| Check | Passed / Failed / Skipped or result |
|---|---|
| Unchanged-server regression | 0 / 2 / 0 (expected reproduction) |
| Fixed pilot, original two cases | 2 / 0 / 0 |
| Expanded pilot | 7 / 0 / 0 |
| Solution build (including closing build) | PASS: 0 warnings / 0 errors |
| All tools build | PASS: all 121 projects, 0 warnings / 0 errors |
| Server Release linux-x64 Native AOT | PASS: exit 0; no warnings, IL2026 or IL3050 |
| New cases three times alone | 21 / 0 / 0 (7 cases ×3) |
| P807 initial 3× each | 4 / 2 / 0; both failures are 394-peer phase timing |
| P807 394-peer unchanged-base control 3× | 2 / 1 / 0; same phase-budget failure |
| P807 394-peer changed-tree follow-up 3× | 0 / 3 / 0; same phase-budget failure |
| P660 countdown/next-match/15v15 showcase 3× each | 33 / 0 / 0; optional showcase enabled |
| P765 deferred migration correctness 3× | 3 / 0 / 0 |
| P598 / P739 / P762 / P480 three times alone | 108 / 0 / 0 |
| One full Fast tier | FAIL: 13,508 / 6 / 4; 4 calibrated timing offenders; 9 infrastructure checks pass |
| Failed/slow Fast methods, each 3× alone | 30 / 15 / 0; only known bundle-path fixture cases fail |
| Showcase never-parks five times | 5 / 0 / 0; matches saved P956 5/5 cohort |
| Glossary / parity / diff hygiene | glossary and parity scorecard PASS; LF-only CSV; diff check PASS |

The first tools attempt hit NuGet mutex creation under a read-only runtime
directory. A writable task-local `XDG_RUNTIME_DIR` resolves that environment
obstruction; the complete tools rerun passed all 121 projects. Test processes also use
task-local temporary and XDG data/config directories.

The single Fast sweep took 525.4 seconds after an additional shared-runner
lock wait. Its six failures are five known bundle-path fixture failures
(scratch-local temporary directories expose the ancestor `local.env`) and
`NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire`.
Four passing executions exceed the calibrated 10-second limit (P800 hull GPU,
P796 varied purchases, SC2 nanite selection and P814b gunner preferences).
All nine failed or slow methods ran three times alone: **30 passed / 15 failed /
0 skipped** including theory companions. The five failing bundle-path cases
each fail 3/3 for the previously documented fixture-ancestry reason; this task
changes no resolver or fixture. Nanite credit passes **3/3**. All four timing
offenders pass every isolated case, with maximum individual durations
**4.000 / 5.945 / 4.591 / 3.603 seconds** (P800 / P796 / SC2 / P814b), below
the same 10-second guard. The four
skips are three optional private Meshy-pack tests and P940 voice playback
without an audio device; artwork is available for all task/handoff tests.

The five normal-home P700 showcase runs pass **5/5**, all reach **24/30**
bots, observe real shots/hits and zero sim-pass failures. Process durations
are **250.4 / 261.2 / 265.2 / 179.0 / 174.0 seconds**; the largest stopped-with-
target interval is **1.915 seconds**, below the unchanged five-second limit.
Comparison is the saved [P956 qualification](DRONE_LOADOUT_P956.md): its
repeated cohort passed 5/5; its first cohort was 3/5 due to unseeded arrival
thresholds, with no parking violation, and its bare-spawn base control was 3/3.
This is a saved baseline comparison, not a fresh rig performance A/B.

Commands (all `dotnet` invocations used the installed user SDK):

```sh
dotnet build AllegianceCore.slnx
DOTNET=<installed-dotnet> tools/build-tools.sh
dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/p965/aot-publish
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter FullyQualifiedName~P965RestartMigrationBarrierTests
# Other isolated cohorts use the same command with their exact method/class filter.
# P660 optional showcase is enabled with ALLEGIANCE_LIVE_P660_SHOWCASE=1.
tools/run-tests.sh --tier fast --no-build --results-dir .scratch/p965/fast-tier
```

The unchanged dispatch-base server was built from a scratch-only copy of HEAD
source (499 tracked files), then substituted into a separate test output
directory. The unchanged test assembly and dependencies were reused; only the
Server DLL differs. Raw control/isolated evidence: `.scratch/p965/p807-base-*`,
`.scratch/p965/p807-retry-*`, `.scratch/p965/fast-isolation-*`; summary:
`.scratch/p965/isolation.json`. Compact public receipts include all event orders,
qualification counts, GC timing samples and the AOT binary size/hash; new
report evidence is below 2 MB. Scratch build inputs, AOT output and
temporary/XDG state are removed after qualification; logs, TRX and receipts stay.

No whole Live/performance sweep or remote rig run is required by the lane's
light-gate override. The full suite gate is one complete Fast tier run, with
selected Live cases qualified alone. No training or HUD code changes.

## Native intent

Complete player and mission reset before the next start can be evaluated;
preserve GAME_OVER-before-reset and evaluate restart through the mission's
absolute `timeStart`.

## Modern approach

Keep bounded work per tick, use the existing per-mission pending tail as the
barrier, and resume lifecycle evaluation after all migrants' join bursts and
trailing reset work have finished. Deadlines describe scheduled time rather
than a duration repeatedly extended while migration waits.
