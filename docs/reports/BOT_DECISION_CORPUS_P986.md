# P986 — Bot decision golden corpus (baseline)

## Summary

This task records the CURRENT bot driver’s decisions in a compact, versioned
golden corpus before any goal-stack/profile refactor. The corpus is behaviour-
neutral: it only adds an opt-in recorder and tests that replay deterministic
scenarios. No bot decision logic was changed.

- Recorder seam: `tools/allegiance-mcp/BotDecisionRecorder.cs` (default-off).
- Golden tests: `tests/Allegiance.ClientCore.Tests/P986BotDecisionCorpusTests.cs`.
- Corpus files: `tests/Allegiance.ClientCore.Tests/GoldenCorpus/BotDecisions/*.json`
  (6 files, ~11 kB).
- Regeneration: `UPDATE_CORPUS=1 dotnet test --filter FullyQualifiedName~P986BotDecisionCorpusTests`.

## Decision inventory

The MCP bot driver (`tools/allegiance-mcp/`) makes decisions across 31 partial
files. The families recorded here are the ones that are already pure or have
been made recordable without changing behaviour:

| Family | Decision | Inputs | Output | File:line |
|--------|----------|--------|--------|-----------|
| CombatTarget | Target pick for one bot | Self ship id, cluster, visible candidates (id, class, threat flag, distance), current engager counts, teammate count | `targetId` / `hold` / chosen class | `CombatTargetSelector.cs:132` |
| Loadout | Role-based hull + parts | Civ, role, tech tree, dock station, target station, budget, mine/rescue probe flags | Hull id, parts, price, reason | `TechLoadoutSelector.cs:43` |
| ResearchCommander | Next research/econ command | Side, epoch, plan, step, wallet, developments, stations, units, rocks | Command list + telemetry status | `ResearchCommander.cs:96` (`TickPlan`) |
| Medevac | Scout assignment per side | Roster (bot id, side, role, can-buy-rescue-probe) | Set of chosen bot ids | `BotManager.Medevac.cs:50` |
| Medevac | Drop gate | Own station, pod count, recent ejects, nearby probe, time, load time | Boolean drop decision | `BotManager.Medevac.cs:82` |
| Defense | Budget for an incident | Population, already assigned, cap, share | Available defenders | `BotManager.Defense.cs:43` |
| Route | Distinct entry routes | Warp table, from/to cluster, max routes | Ordered route list | `BotManager.cs:1010` |
| Route | Least-loaded route | Per-route usage counts | Selected route index | `BotManager.cs:1111` |

Other decision families are present in the driver but not yet represented in the
corpus because they require a live server or a full `ConnectionSession`/`ActiveBot`
fixture:

- Finale/strike arming and wing ordering (`BotManager.Finale.cs`,
  `ShowcaseFinale`, `TechLoadoutSelector.SelectBomber`).
- Defense incident assignment and `TryDriveDefense`
  (`BotManager.Defense.cs:140`).
- Miner/support duty and `TryDriveSupport` (`BotManager.Support.cs`,
  `BotManager.Routines.cs`).
- Full travel/route execution (`TransitWarp`, `CombatNextHop`, staging).
- Lifecycle/dead/respawn/rotation handling (`BotManager.Lifecycle.cs`,
  `UpdatePodState`, `LaunchNextMission`).
- No-fog and commanded-target overrides (`DriveCommanderOrder`,
  `EngageCombat` with `PreparedEngagement`).

## Review corrections (Claude, 2026-11-09)

- Citations in the inventory table re-grepped and corrected (six of eight were off by 2-10 lines).
- The route "tie-break" scenario (`{3,3,1}`) had no tie at the minimum, so flipping the tie rule in
  `PickLeastLoadedRoute` (`<` to `<=`) passed unnoticed. Replaced with `{2,1,1}`; the same tweak now fails
  with `expected "pick": 1 / actual "pick": 2`.
- The `CapMinerDrone` 2 to 1 tweak is caught, but through the scenario's echoed input (`engagerCounts` is built
  from the constant), not through a changed output.
- `RecordDefenseBudget` logged `alreadyAssigned = population - budget`; it now logs the real assigned count.
- The live recorder no longer writes a wall-clock `timestamp` (records must be diffable); the unused `RecordTo`
  was removed.
- The live recorder (`ALLEGIANCE_BOT_RECORD_DECISIONS=1`, env rather than a `bot.recordDecisions` config key) is
  NOT used to build the corpus: the goldens are hand-built synthetic scenarios calling pure helpers. There is no
  seeded loopback 2-side/3-side match recording, no game-over → next-match scenario, and no recording-off
  overhead measurement. The showcase never-parks gate was not run.

## Corpus format

Each file is a pretty-printed JSON array of scenario objects:

```json
[
  {
    "name": "lone-miner-first-bot",
    "inputs": { ... },
    "outputs": { ... }
  }
]
```

- Keys are sorted alphabetically.
- Floating-point numbers are rounded to 6 decimal places.
- `null` is preserved as JSON `null`.
- The format is intentionally line-oriented so `git diff` is readable.

## Scenarios

| File | Count | Notes |
|------|-------|-------|
| `combat-target.json` | 8 | Lone miner cap, threat priority, combat-before-miner, focus-fire, 2-side, 3-side, empty sector |
| `loadout.json` | 5 | Fighter default, scout cloak preference, bomber strike, support nanite, rescue-probe scout |
| `research-commander.json` | 4 | Waiting for start, first donation, empty wallet, drone cap block |
| `medevac.json` | 5 | 2-side/3-side scout picks, no scouts, drop gate yes/no |
| `defense-budget.json` | 4 | Small pop, cap hit, already-assigned exhausts, zero share |
| `route.json` | 3 | Distinct entry routes, least-loaded route, a real tie at the minimum (lowest index wins) |

Total: 29 scenarios across 6 decision families; corpus size ≈ 11 kB.

## Determinism

All scenarios are deterministic:

- They use synthetic geometry and fixed inputs.
- `CombatTargetSelector.Select` is pure static.
- `TechLoadoutSelector.Select` is pure over `(core, civ, techs, dock, …)`.
- `ResearchCommander.TickPlan` is pure over its `Snapshot` input.
- `BotManager.PickMedevacScouts`, `ShouldDropMedevacProbe`, `DefenseBudgetFor`,
  `RouteEnumeration`, and `PickLeastLoadedRoute` are pure static helpers.

No `Random` or wall-clock state participates in the decisions under test.

## Regeneration

```bash
env UPDATE_CORPUS=1 \
  dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj \
  --filter "FullyQualifiedName~P986BotDecisionCorpusTests"
```

Review the diff before committing regenerated files.

## Proving the corpus catches drift

A scratch script at `.scratch/p986/prove-corpus-catches-tweak.sh` temporarily
changes `CombatTargetSelector.CapMinerDrone` from 2 to 1, builds, and runs the
combat-target corpus test. The test fails with a readable diff (the lone-miner
at-cap scenario no longer matches). The script restores the original source and
is not committed.

## Test counts

- New tests: 6 xUnit facts in `P986BotDecisionCorpusTests` (Fast tier).
- New tests 3× alone: passed all three runs.
- Full fast suite: 13,772 tests; failures are existing known flakes (see below).

## Gate

| Step | Result |
|------|--------|
| `dotnet build AllegianceCore.slnx` | Passed, 0 warnings |
| `tools/build-tools.sh` | Passed, 125 tool projects |
| New tests 3× alone | Passed |
| One full fast suite | Failed on 4 existing Core.Tests flakes |
| Showcase never-parks | Not changed by this task |

Non-task fast-suite failures observed:

- `Allegiance.Core.Tests.Transport.P895UdpTransportTests.Mixed_listener_uses_unique_peer_ids_and_routes_both_protocols`
- `Allegiance.Core.Tests.LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds`
- `Allegiance.Core.Tests.GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster`
- `Allegiance.Core.Tests.BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission`

All four are already ledgered in `docs/TEST_FLAKES.md` as timing/receipt
boundary flakes. Running each alone 3× showed: P895 passed 3/3,
LiveSideSelect failed 3/3 (clock-boundary issue at 32 s vs 28–31 fixture),
GameOver passed 3/3, Ballot passed 3/3. None touch bot decision code.

## What the corpus does NOT cover

- Live server integration (network receive order, mission rotation, real human
  orders).
- Full `DriveCombat` sequencing (defense assignment, support duty, staging,
  travel, pod routing).
- Finale/strike arming logic and wing ordering.
- Difficulty/accuracy knobs (`BotDifficulty`).
- Visual/no-fog rendering paths.

These are intentionally out of scope for the baseline; they will be added once
pure fixtures exist or once the refactor preserves their outputs.

## Native intent

Native Allegiance has no built-in AI for most of these showcase policies; the
native equivalents are human wingman discipline and server-side drone rules.
This corpus records the MODERN bot driver’s current decisions so a later
refactor can prove default behaviour is unchanged.

## Modern approach

The implementation adds a small, zero-cost-when-off recorder seam and uses
pure, synthetic scenarios for a fast, deterministic golden corpus. This avoids
needing a live server or rig for the baseline while still giving a readable
diff on regression.
