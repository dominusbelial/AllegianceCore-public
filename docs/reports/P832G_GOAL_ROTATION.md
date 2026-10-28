# P832g — per-match showcase/skirmish goal rotation

Implemented on `codex/p832g`; changes remain in the working tree. No commits,
pushes, remote-host access, or media additions. Rotation is **off by default**.

## Configuration and application

`showcase.goalRotation` is an opt-in creator-preset policy. It is available to
server showcases and through `SkirmishOptions.GoalRotation` for embedded hosts.
It is not a new setup-screen control and does not change saved skirmish defaults.
The complete runnable server example is
[`tools/p832g-goal-rotation.json`](../../tools/p832g-goal-rotation.json).

```json
"showcase": {
  "techMode": "research", "incomeMultiplier": 5000, "researchTimeScale": 16,
  "goalRotation": {
    "mode": "cycle",
    "entries": [
      { "name": "flags", "goals": { "flags": 1 } },
      { "name": "artifacts", "goals": { "artifacts": 1 } }
    ]
  }
}
```

Omit `entries` to use the eight-entry coverage preset below. `cycle` starts at
entry zero and wraps. `random` draws from every entry except the previous one;
its independent `Random(matchSeed)` does not consume civ or simulation draws.
`games[].civSeed` pins the existing match-seed sequence. Random replay requires
the ordered entries, logged `seed`, and logged `previousGoalIndex` (the first
match uses -1). Cycle replay uses `goalMatch`, a one-based **per-game** ordinal;
it is independent of the process-wide skills `match` number. Restarting the
server restarts the sequence. Rotation state is not persisted across restarts.

All entries must have unique ASCII names and distinct, enabled goal sets. Invalid
values, prosperity without developments, and territory on unsupported generated
maps fail before transport boot. Nonempty fixed goals/nonzero legacy countdowns
conflict with rotation rather than being silently combined. Source-generated JSON
covers the entire nested config, including absent-member defaults.

`AssembleGameMission` selects goals before map generation, native objective
population, research buckets and initial standings. The same function assembles
boot and post-game-over worlds. The selected goals travel through ordinary
MISSIONDEF, lobby/briefing information and client worlds; bots receive no goal
selection side channel. Exact goal values, entry index, per-game match ordinal
and previous index join the existing `[matchup]` seed/civ log. Example:

```text
[matchup] mission #0 match=2 seed=824858509 goals=artifacts(artifacts=1) goalIndex=1 goalMatch=2 previousGoalIndex=0 side0=25:Iron Coalition(plan_a) side1=25:Iron Coalition(plan_a)
```

The option's absence follows the old goal resolution path, does not select an
entry, does not draw another seed, and preserves the old matchup log format.
Fixed multi-goal worlds retain their parameters through repeated mission rebuilds.
The unchanged master decision-trace regression remains in the focused checks.

Rotation rejects the legacy server research-finale deadline request: each match
must end through native game rules. Spawn the ordinary `showcase` fleet with
`finale_after_minutes:0`, especially for custom lists starting with prosperity,
territory or countdown. The built-in list starts with conquest, for which the
existing native-goal bot policy already disables timed bomber finales. Native
survival/inactive-side/station-loss outcomes remain possible in every mode.

## Default coverage preset and values

| Cycle match | Goal parameters | Value justification | Rig planning estimate, simulation time |
|---:|---|---|---|
| 1 | conquest 100% | Native `MissionParams::Reset`, `Igc/igc.h:1307` | 10–45+ min |
| 2 | flags 1 | Native CTF preset uses 10 (`WinTrek/gametypes.cpp:123`); one is a deliberately shorter showcase variant within native 0–100 validation (`Igc/igc.h:1412-1415`) | 3–15 min |
| 3 | artifacts 1 | Native preset uses 10 (`WinTrek/gametypes.cpp:128`); one exercises real search/pickup/secure without requiring ten returns; native validation `Igc/igc.h:1408-1411` | 3–20 min |
| 4 | team kills 20 | Native Death Match preset, `WinTrek/gametypes.cpp:118` | 3–15 min |
| 5 | prosperity multiplier 1 | One loaded-core WinTheGameMoney price; native field semantics `Igc/igc.h:1158`, legal range 0–9 at `:1420-1423`; convenience value, not a native named preset | 2–15 min at income ×5000 |
| 6 | territory 70% | Native Territorial preset, `WinTrek/gametypes.cpp:133` | 15–60+ min |
| 7 | countdown 1800 s | Explicit 30-minute convenience preset; native clock field defaults off (`Igc/igc.h:1305`), countdown handling `FedSrv/fsmission.cpp:2337-2424` | 30 min unless another native outcome wins earlier |
| 8 | flags 1 + artifacts 1 | Native independent goal predicates permit simultaneous goals (`Igc/igc.h:1511-1554`); combines the two short return goals above | 3–20 min |

These rig times are **planning estimates, not measurements**. Fog, fleet size,
civs, map, station construction and time dilation affect duration; territory and
conquest have no invented timeout. A random sequence is reproducible and avoids
adjacent repeats but does not guarantee complete coverage in eight matches; cycle
does. Use cycle for coverage qualification.

## Bots and reset boundaries

The production lifecycle already uses mission generation/cookie/new ship identity
to reset flag task/anchor/trace, artifact search round, kill recovery/trace, targets,
orders and routes. Its objective pass selects the P832c/d/e/f strategy from each
bot's received goal fields. P832b research commanders rebuild on the fresh research
epoch and read prosperity goals from their received world snapshot.

One retained reference needed cleanup: conquest's temporary, **unregistered**
strike context kept the previous world's station plan after migration. The
migration now clears that reference after restoring the pilot's ordinary loadout;
registered timed finale re-arm behavior stays intact. A regression builds a real
conquest strike context, migrates the same controller into the next goal, and
asserts the context, reserved/active bomber flags and objective state are cleared.

The wire-world regression then activates flags, artifacts, conquest and kills in
sequence through the production candidate/strategy selector, and funds the
prosperity goal from `ResearchSnapshot`. It finishes with no objective and verifies
no previous duty remains. Existing P832b–f regressions additionally cover funding,
visible targets, shared quotas, legal loadouts and unchanged master decision traces.

## Goal-aware coverage map

The existing SC-1 skill coverage report now joins `[matchup] goals=...` to the
`[skills]` counters by process-local match number. Its Markdown Goals column and
JSON `goals` field retain exact selected parameters. Old logs remain readable as
`fixed/unspecified`. Repeated snapshots still do not accumulate into final counts.
See [`docs/showcase/README.md`](../showcase/README.md) and the parser regression.
This records the selected goal alongside observed mechanisms; selection alone
never certifies that a mechanism was exercised.

The local natural-victory proof uses six ordinary showcase pilots per side, a
seed-832 Brawl, native research/purchase gates, and the two-entry flags→artifacts
list. It retains connected pilots across rotation and requires each match's own
score threshold, winner and exact native recovery reason. It also checks fresh
MISSIONDEF goals, active Raid then Collect duties, no finale, and zero sim-pass
failures. The full eight-entry cycle/random advertisement test separately checks
all selected values before the fresh countdown; its controlled game-over calls
are lifecycle probes, **not natural-victory evidence**.

## Verification

Focused checks: five new config/reset/strategy cases passed **3/3**, and 71
existing goal/decision regressions passed **3/3**. Four live cases (cycle, random,
fixed advertisement plus the natural two-match bot run) passed **3/3**; the
strengthened fresh-ship advertisement assertion also passed an extra isolated run.
The coverage parser's five tests passed **3/3**. No focused test was skipped.

| Natural run | Flags: wall / winner | Artifacts: wall / winner |
|---:|---|---|
| 1 | 46.4 s / Alpha | 27.6 s / Alpha |
| 2 | 46.0 s / Alpha | 35.1 s / Alpha |
| 3 | 46.2 s / Alpha | 23.2 s / Bravo |

Every victory had the exact native `wins with the flag/artifact recovered by
mcp-…` reason, one recovered objective, and zero sim-pass failures. These are
small-map local measurements, not extrapolations to 200v200. The two-match fleet
was spawned once and migrated normally; no score/object/ship position was injected
and no forced game-over call was used in this natural-victory test.

Small public evidence: [verification.json](P832G_assets/verification.json) and
[coverage.json](P832G_assets/coverage.json) (the third run's actual per-match skill
coverage, with selected goals). Full gate results:

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | passed, 0 warnings / 0 errors |
| `tools/build-tools.sh --drop-bins` | all 90 projects built, 0 warnings / 0 errors |
| Linux x64 server Native AOT publish | passed, no warnings, no IL2026 / IL3050 |
| Published native binary + example config smoke | booted conquest 100%; clean bounded shutdown |
| New focused tests and existing goal regressions | 5 + 71 cases per run, each group 3/3 |
| New loopback rotation tests | 4 cases per run, 3/3 |
| One `tools/run-tests.sh --tier fast` | 11,203 passed / 7 failed / 3 optional skips; all failed cases passed 3/3 in isolation (details below) |
| Exact P730 two-match showcase regression | **5/5**, no retries or modified assertions |
| Coverage parser tests | 5 cases, 3/3 |
| Glossary / parity / Compose / whitespace | passed; 236 glossary paths, CSV remains LF |

The AOT command was the prescribed Release/linux-x64/self-contained publish with
`PublishAot=true`; LLVM 21's bin directory was added to PATH because this host
installs versioned Clang. No AOT warnings were suppressed.

| P730 run | Duration | Load averages before (1/5/15 min) | CPU idle before (1 s sample) |
|---:|---|---|---:|
| 1 | 00:04:18.7289787 | 4.38 / 3.82 / 3.29 | 93% |
| 2 | 00:03:11.4045814 | 3.96 / 4.27 / 3.63 | 95% |
| 3 | 00:04:13.3080769 | 3.97 / 4.04 / 3.65 | 96% |
| 4 | 00:04:07.7280936 | 3.59 / 3.48 / 3.5 | 93% |
| 5 | 00:04:09.7979447 | 1.53 / 2.83 / 3.27 | 91% |

The host has 24 logical CPUs. These runs were serial, after tool/AOT compilation
finished, with no concurrent test/build workload from this lane and no synthetic
load. All pre-run CPU samples were 91–96% idle. No flake ledger update was needed
for the standalone/focused checks. The single Fast-tier sweep retained its **FAIL** result: **11,203 passed / 7
failed / 3 optional skips**, 313.0 s, no timing offenders. It was not repeated.
Every new P832g case in Fast passed, and every ClientCore Fast case passed.

- Five `PlayerBundlePathsTests` cases inherited the real checkout's `local.env`
  because TMPDIR is beneath the worktree. This is the already-ledgered fixture
  environment-context issue, not a product defect or a load flake. The entire
  nine-case class passed **3/3 (27/27 cases)** through an open-directory
  `/proc/<shell-pid>/fd/3` alias to the **same NVMe scratch directory**, giving
  the simulated install a path without repository ancestors.
- `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  hit the already-ledgered read-only default user config directory. It passed
  **3/3** with `XDG_CONFIG_HOME` set to task scratch. No HOME override or external
  preferences write was used for the isolated checks.
- `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`
  saw the server grant before the client's loadout acknowledgement (default
  `ExpandedPartData` at line 252). This matches the existing launcher receipt
  flake. It passed **3/3 exact-method invocations alone**, without source,
  assertion, deadline, tier or quarantine changes.

These observations and receipts are also recorded in
[`docs/TEST_FLAKES.md`](../TEST_FLAKES.md). They are not reported as a clean Fast
sweep or attributed to goal rotation.
Raw evidence is retained locally in [gate evidence](../../.scratch/p832g/evidence/)
and [loopback logs](../../.scratch/p832g/tmp/) (`p832g-*.log`); only the small
verification summary is included in the public report assets. The sandbox cannot
write the usual external evidence directory. NuGet's HTTP cache and temporary
files were redirected into worktree scratch because the default user cache is
read-only. All .NET commands use `~/.dotnet/dotnet`.

## Recipe for Claude on 221

No connection to 221, the Mac, or another machine was made in this lane.
On the rig, copy/review the example config or apply its `showcase.goalRotation`
block to the existing showcase config. The example reserves 220 seats per side and 1024 total/per-IP connections for
200v200 plus commanders/probes. Keep the existing artwork path/ports and
research plans, remove conflicting fixed `goals`/`gameLengthSeconds`, and retain
auto-next-match. The example's authored `m2xspiderw.igc` supports the full list;
Brawl/two-team small rings do not support territory. Set `civSeed:832` to reproduce
the match seed sequence, or omit it and retain each logged seed.

```sh
~/.dotnet/dotnet run --project src/Allegiance.Server --no-build -- --config tools/p832g-goal-rotation.json
```

Connect the existing rig bot supervisor/MCP driver and call `spawn_scenario` with:

```json
{"preset":"showcase","count_a":200,"count_b":200,"staging":true,"finale_after_minutes":0}
```

Use the full eight-entry cycle first. Preserve `[matchup]`, native GAME_OVER
winner/reason, `[flags]`/research decision logs and final `[skills]` blocks for
each match. Parse the combined server log with:

```sh
python3 scripts/showcase-skills.py /path/to/server.log --json
```

Check the Goals column/field against the cycle table, fresh goals during each
intermission, no old objective anchors after migration, and actual goal actions
versus early native elimination. Run a second sequence with `mode:"random"` and
the same seeds to compare selections; allow enough matches to observe every
entry. Expectations for duration are estimates in the table, not a rig pass claim.

## Native intent and approach

Native intent: creator-selected numeric goals, their validation, advertisement,
initial goal objects, win predicates/reasons and fresh-world lifecycle are
**preserved design**. Re-grepped native references: `Igc/igc.h:1158-1179,1286-1311,1408-1423,1484-1503,1511-1554`;
`FedSrv/fsmission.cpp:1954` (GenerateMission with creator params),
`:2604-2622` (vacate/reset), `:2775-2789` (goal result columns);
`FedSrv/FedSrv.CPP:13149-13161` (flag/artifact victory and reason strings).
The existing parity rows SIM-0816/0817 now link rotation/advertisement evidence;
no automatic-rotation parity credit was invented.

Automatic goal rotation, short showcase target counts, non-repeating random
selection, coverage labels, and suppression of the synthetic deadline are
**preset orchestration**, not native rules or a native bug port. No behavior
parity exception is being hidden behind the word native.

Modern/better approach: keep one typed list of ordinary mission parameters and
reuse the fresh mission/advertisement/reset paths. This avoids separate bot goal
configuration and duplicate victory mechanisms. A future coverage scheduler could
prefer under-observed mechanisms, but should remain an explicit preset policy and
retain seeded, auditable selections; it should never replace native win predicates.
