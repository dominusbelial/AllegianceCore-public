# P832a — reachable native game goals and honest simulation-rule coverage

Base: `62a5b50fe0b1d300a0086632dbb98ff6a4fe408b`, branch `codex/p832a`.
Changes are left in the working tree. No commits, pushes, remote-host access, bot
strategy changes, or per-match goal rotation were performed.

## Result and compatibility

`MissionGoals` is now the source of the numeric native goal parameters. The old
independently writable Stage-1 booleans are gone: the existing read-side names
are predicates over that model. Conquest and territory use configured thresholds
instead of the old hardcoded 65/50 substitutes. A supported per-game JSON path,
server CLI presets/JSON, environment override, skirmish options/preferences, and
the skirmish smoke command can select the rules. Fixed goals survive the normal
mission rebuild/rotation.

**Absent or empty `games[].goals` preserves this port's existing gameplay:** no
event-driven goal is selected; the optional existing `gameLengthSeconds` clock
and showcase finale/deadline remain available. `MissionGoals.NativeDefaults()`
explicitly supplies native Reset's 100% conquest, rather than silently turning
it on for existing server configurations. README's endgame line is unchanged.

Prosperity uses the configured multiplier in the actual bucket, the human
investment catalog, the generic MCP investment catalog, and native briefing
amount. MISSIONDEF and briefing goal values are scoped to the joining ship's
mission, including a peer seated in a second configured world. Flags create the native
two pedestal/flag pairs per Start-capable home station. Artifact games get initial neutral artifacts on loaded and generated
maps. Neither these objects nor initial conquest/territory rankings are added
for the default no-goal policy. This is necessary initial-world plumbing;
scouting, collection, economic strategy and goal rotation remain P832b–g work.

## Native evidence (line numbers re-read in the local reference)

Reference checkout: `~/Development/Allegiance/src/` (read-only).

| Rule | Native evidence | Port |
|---|---|---|
| Actual parameters and widths | `Igc/igc.h:1158-1179` | `src/Allegiance.Core/Model/MissionGoals.cs` |
| Reset: conquest 100, territory 100, other selected goals zero | `Igc/igc.h:1286-1311` | Explicit `NativeDefaults()`; compatibility constructor selects no goals |
| Conquest != 0; territory != 100; kills/countdown/money/artifacts/flags != 0; NoWin includes all seven predicates | `Igc/igc.h:1511-1554` | Shared predicates; `Mission`, lobby and briefing consume them |
| Flags/artifacts 0..100; money multiplier 0..9 | `Igc/igc.h:1408-1422` | Field-specific validation; additionally rejects invalid percentages, negative kills and nonfinite/negative clocks |
| Prosperity requires developments; territory excludes Brawl and two-team SingleRing/DoubleRing | `Igc/igc.h:1484-1503` | `GameConfig.ResolveGoals()` |
| Native preset goal values: Conquest 100, Death Match 20, Flags 10, Artifacts 10, Territorial 70 | `WinTrek/gametypes.cpp:114-144` | `MissionGoals.Preset`; these are the goal portion, not all unrelated game-type settings |
| Create-game selections read/write all fields | `WinTrek/newgamescreen.cpp:195-201,1010-1016` | Creation-time JSON/CLI/skirmish paths |
| AGC enabling prosperity defaults to multiplier 1 | `AGC/AGCGameParameters.cpp:642-652` | Prosperity convenience preset 1; countdown's 1,800-second convenience preset is a port choice |
| Conquest compares configured percent; territory uses rounded `(50 + clusters * percent)/100` | `FedSrv/fsmission.cpp:3624,3663` | Existing recompute now reads `Goals` thresholds |
| Credited kill threshold and secured flag/artifact threshold | `FedSrv/fsmission.cpp:3749-3764,3861-3899` | Existing event paths; configured live proofs |
| Economic-victory bucket price is multiplier times loaded win-money constant | `Igc/missionigc.h:770-794` (price at 790) | `Mission.Assemble` → `BucketCatalog.ForSide` |
| Economic-victory development completion ends game | `FedSrv/FedSrv.CPP:11259-11280` | Existing `Server.Economy.cs CompleteDevelopment` |
| Pedestal offsets ±2,000; flag offset ±(50+radius), two pairs per Start station, ten-day lifetime | `Igc/common.cpp:2895-3001`; `Igc/igc.h:26-27` | `Mission.GoalObjects.cs PopulateGoalObjects` |
| One neutral artifact per empty neutral sector at treasure population | `Igc/common.cpp:3090-3095`; `Igc/missionigc.cpp:4048-4051` | Goal setup; existing generation/respawn code |
| Initial conquest share and unanimous territory rankings without a premature win | `Igc/missionigc.cpp:3859-3896,4074-4101` | Goal setup, selected ranking goals only |
| Native client reads goal predicates from MISSIONDEF | `clintlib/clintlib.h:540-546` | Codec + detached `MissionInfo.Goals`; stage updates retain them |
| Briefing thresholds, minutes, prosperity amount | `WinTrek/teamscreen.cpp:739-793` | Existing shared generator receives authoritative values; NoWin now includes countdown and territory |
| Query/result goal flags and values | `FedSrv/fsmission.cpp:2775-2784`; `FedSrv/srvqueries.h:209-218` | Lobby goal flags remain live projections of the mission |

The full station-victory and secured-flag native bodies contain additional
stage/alliance/empty-team details. Those broad rows are **partial**, not a claim
that wiring configuration has certified every existing victory-rule branch.

## Configuration and selection

Example game fragment (goals may combine):

```json
{
  "goals": {
    "conquestPercentage": 73,
    "territoryPercentage": 60,
    "teamKills": 21,
    "flags": 4,
    "artifacts": 7,
    "teamMoney": 2.5,
    "gameLengthSeconds": 600
  }
}
```

All unspecified fields disable their goal; **territory's disabled default is
100**. The counts are native signed shorts; team kills must fit 0..32767.
`teamMoney` and the native game-length field are floats. Bad JSON/type/range,
null goals, invalid goal combinations, and conflicting top-level/nested clock
settings fail with an actionable field/game name. The old top-level
`gameLengthSeconds` remains an alias and is normalized to the native float
clock; duplicate nonzero differing clocks are rejected.

Precedence is per-game JSON, then nonempty `ALLEGIANCE_GAME_GOALS` JSON, then
ordered CLI options. Environment and CLI overrides replace goals in **all**
configured games with detached copies; their replacement clears the old clock
alias. Empty environment values preserve JSON. Examples:

```bash
# On an existing configured local server:
~/.dotnet/dotnet run --project src/Allegiance.Server -- --config server.json --goal conquest
~/.dotnet/dotnet run --project src/Allegiance.Server -- --config server.json --goals '{"teamMoney":2.5}'
ALLEGIANCE_GAME_GOALS='{"teamKills":20}' docker compose up -d server

# Embedded skirmish smoke: existing duration, size, then optional goal preset.
~/.dotnet/dotnet run --project src/Allegiance.ClientCore.MonoGame -- --skirmish-smoke 60 Small flags
```

Preset names: `none`, `conquest`, `territory`, `kills`, `flags`, `artifacts`,
`prosperity`, `countdown`. In embedded API callers use
`new SkirmishOptions { Goals = MissionGoals.Preset("flags") }`; remembered
options retain detached goals. This slice does not add another setup-screen row
or give bots a goal-specific strategy. Server serialization remains source
generated (`ServerJsonContext`); Compose passes the environment override and
`docker/README.md` documents it.

## Wire and real-window proof

The message remains 864 bytes, with the existing 252-byte MissionParams block.
The native-layout anchors are the independently compiler-confirmed offsets in
`docs/P29_MISSIONDEF_JOINED_MISSION.md`; the new golden test uses literal bytes,
not a codec encode/decode round-trip as its layout oracle.

| Field | Params offset | Absolute MISSIONDEF offset |
|---|---:|---:|
| `fGoalTeamMoney` float | 116 | 120 |
| `dtGameLength` float | 132 | 136 |
| `rgCivID[6]` | 168 | 172 |
| conquest short | 180 | 184 |
| territory short | 182 | 186 |
| unused artifact percentage short (Reset 100) | 184 | 188 |
| flags short | 186 | 190 |
| artifacts short | 188 | 192 |
| team kills short | 190 | 194 |
| existing lives short | 204 | 208 |

Default wire bytes now explicitly advertise disabled territory (100), and the
unused artifact percentage's native Reset value (100), rather than leaving
those cells zero. This corrects native-client interpretation without enabling
a server victory rule. Existing civ, lives, version and trailing MISSIONDEF
fields retain their offsets.

`tools/p832a-goals-proof` boots a real server, connects the production Game1 over
TCP, launches through the normal hangar action and captures the actual shared
briefing renderer in both styles. No briefing rows/packets are fabricated.
The retained captures were opened and visually inspected:

- [Classic](P832A_assets/briefing-goals-classic.png): Iron Coalition briefing,
  Alpha in Ceto; readable objectives for **73% bases, 7 artifacts, 4 flags,
  10 minutes, 21 enemy ships and Reinforcement costing 180000 credits**.
  Classic cockpit typography, minimap and nearby real flag labels are visible.
- [Modern](P832A_assets/briefing-goals-modern.png): the same complete objective
  text and amount over Modern's circular gauges and larger speed readout.

The amount is `2.5 * 72000`, the loaded PCore001 economic-victory constant,
and matches the server bucket. The proof also checks received goal values
before taking screenshots. Terrain/strategy/transport performance is not
certified by these pictures.

## Event-driven inventory and reachability

The previous audit's six families sampled constants, IGC updates/moves,
periodic server units, warnings, callback contracts and sounds. A callback being
bound or GAME_STATE carrying counters did not enumerate the rule body it called.
A tested default-off parameter with no real setter could still receive full
implementation credit. Both are now explicit audit dimensions.

The new **FedSrv mission rules (event-driven)** family has a reconciled inventory
of **351** active source bodies/cases:

- **90 CFSMission methods**, **67 CmissionIGC methods**, **63 FedSrvSiteBase
  methods**, and **20 Ballot/subclass methods** (240 method bodies total).
- **49 active OnAppMessage FM_* cases**, including full sender/stage/ownership
  guards and consequences, rather than just codec/dispatch presence.
- **47 local helper bodies** reached from these methods/cases, including money,
  invitations/results, chat/drone commands, boarding/leaving, treasure creation,
  part refill and leader selection.

`tools/p832a-mission-rules-audit.py --native-root <native-src> --check` masks
comments/strings, balances bodies, follows local named helper calls, explicitly
includes Ballot subclasses for virtual cancellation, and reconciles exact
file/line/method keys against the CSV. The 15 inline ThingSiteImpl/ClusterSiteImpl bodies are included explicitly for virtual visibility/scanner entry points. The old PickNewCiv body at
`FedSrv/fsmission.cpp:4532` is inside a block comment and is excluded. Registry,
serialization, platform facilities and already-counted periodic bodies remain
visible as n/a inventory aliases rather than earning duplicate completion
credit. Inline header bodies outside the three requested source files are
cited when ported, but this is not an inventory of every inline method in the
entire engine.

There are five additional narrow rule rows: goal parameters, goal
advertisement, initial objective/ranking setup, friendly-fire-on policy and
allied-visualization-on policy. Thus the new family has **356 rows**: 241 partial,
102 n/a, eight missing and five implemented. Only three of the implemented rows
are production. The missing body seams are invitations (add/query), squad result persistence,
invitation retrieval, native trap/hack event reporting, mutable side-name editing,
and the C->S SET_TEAM_INFO / MISSIONPARAMS handlers. The existing wire matrix
marks MISSIONPARAMS n/a and SET_TEAM_INFO done for the S->C squad relay; neither
means that native client-initiated editing exists. Creation-time goals are
supported; mutable host editing remains a separate gap.

Broad bodies are deliberately left partial when an executable seam exists but
all native policy branches have not been certified. This does **not** assert
that every one of these bodies contains a known gameplay defect; it prevents
named methods, codec support or narrow callback tests from claiming whole-body
parity. Existing row scopes remain intact; the probe firing-reveal row now
explicitly describes its production scanner branch, with the unreachable
allied-on branch tracked separately.

Every one of the existing 483 sim rows now has reachability:

| Value | Meaning and findings |
|---|---|
| `production` | A supported gameplay configuration/client/event path reaches the stated scope. A native default-false policy is not demoted when that default behavior itself is the row's scope. NPC ammo recovery remains production because normal offline skirmish enables its native offline case. |
| `config-only` | Declared experimental opt-in, not accepted/default production behavior: SIM-0141 native broadcast order, and SIM-0475–0483 native GotoPlan. Nine implemented GotoPlan rows lose headline credit; the existing broadcast row was already partial. |
| `test-only` | Only test-side mutation reaches the rule, or no executable production counterpart exists. `Mission.AllowFriendlyFire=true` and `Mission.AllowAlliedViz=true` have test setters but no GameConfig/lobby setter; they are now explicit non-credit rows. Excluded platform/body aliases may also carry this value. |

The new supported goal configuration path makes the selected goal scopes
production, even though absent/default goals intentionally remain disabled.
The matrix does not confuse an operator-selectable supported goal with an
experimental controller that has not completed its production acceptance.

Scorecard completion credit now requires `production`. Eligible config/test
rows stay in the denominator, and separate counts/implemented weights print
below the headline. Malformed/missing sim reachability is rejected.

| Score | Before | After |
|---|---:|---:|
| Weighted simulation headline | 1279/1288 = **99.30%** | 1279/1880 = **68.03%** |
| Unweighted completed/eligible | 424/427 = **99.30%** | 418/681 = **61.38%** |

The weighted headline falls **31.27 percentage points**: nine old experimental
weight units lose credit, while the newly enumerated family adds 592 eligible
weight units and only nine production completion units. The numerator happens
to end at its old value. This is a stricter coverage lower bound, not a claim
that gameplay became 31% less functional. Overall reachability: 777 production,
10 config-only, 52 test-only rows (839 total; n/a excluded from denominators).

## Verification

Raw evidence is kept in the ignored worktree scratch area,
linked here, because this lane's writable filesystem does not include the
external `~/allegiance-evidence` directory. Build trees are removed after use.

- Whole solution build: 0 warnings / 0 errors.
- Native AOT server publish: completed; no IL2026/IL3050 diagnostics.
- Tools: all **88 projects** built, including the new real-window proof. The
  initial audit-cache attempt hit a read-only user cache; the final build used
  worktree-local `NUGET_HTTP_CACHE_PATH`, with vulnerability auditing enabled.
- Final targeted checks: **504/504 case executions** across 21 test filters, each
  run three times alone (63 invocations). This includes a real territory capture,
  separate-mission goal delivery and generic MCP full-price investment.
- ONE Fast-tier sweep: **10,860 passed / 5 failed / 3 optional Meshy skips**,
  10,868 cases, 304.7 seconds; nine infrastructure checks passed and no timing
  violations were reported. The five failures are the already-ledgered unchanged
  `PlayerBundlePathsTests` environment-context cases: mandatory worktree-local
  TMPDIR makes their temporary installs inherit the real checkout's local.env.
  The original sweep FAIL is retained. All nine cases then passed **3/3 isolated
  runs (27/27)** using a `/proc/<parent>/fd/<directory>` logical alias to the same
  NVMe scratch, with assertions unchanged. This is an environment limitation,
  not a product regression or a load flake; the full sweep was not repeated.
- The expanded P682b diagnostic observed the same match-2 fresh-finale timeout
  in three theory variants; the fourth was interrupted to investigate the base.
  The passing 504-case cohort includes the other P682 combat method, not this
  long scenario. The unchanged dispatch base has reproduced the exact timeout in
  **3/3 isolated runs** of `port, pipeline=false` (310.20, 310.34 and
  311.61 seconds). Its 240-second wait is shorter
  than the existing 300-second rearm grace, and its two bombers do not establish
  fresh enemy-base discovery. ShowMap reveals warps, not enemy bases. No bot
  strategy, rearm setting, assertion, timeout or quarantine was changed.
- Showcase never-parks: **final 5/5 passed; unchanged base 5/5 passed**.
  Runs alternated base/final, one at a time with no overlapping lane builds or
  suites. Before each run the runner required two load samples at or below
  0.25 load per available CPU (24 CPUs); a higher sample delayed the start.
  Longest observed stop with a target was **2.004 s final / 2.012 s base**,
  below the five-second assertion. Base durations: 340.65, 321.54, 282.66,
  354.22, 384.72 s; final: 391.00, 397.38, 375.61, 348.34, 433.80 s.
  Scouting makes these runs long and variable. This cohort establishes the
  requested local parking comparison; it does not certify throughput or rig
  performance. Assertions, observation windows and strategy were unchanged.
- Glossary: 188 paths valid; all parity matrices valid; native inventory check:
  351 bodies/cases; Compose config and whitespace checks passed. CSV is LF.
  Added/changed public source files contain no absolute home paths or personal
  emails. The report assets are under 2 MB. No publish-guard commit-range run
  is claimed because this lane created no commits.

Small report receipts: [native inventory](P832A_assets/native-inventory.json),
[before score](P832A_assets/score-before.txt),
[after score](P832A_assets/score-after.txt),
[targeted runs](P832A_assets/targeted-tests.json),
[original Fast sweep](P832A_assets/fast-summary.json),
[path-fixture recovery](P832A_assets/path-recovery.json),
[unchanged-base timeout repeats](P832A_assets/base-rearm.json),
[parking comparison](P832A_assets/parking.json), and
[verification summary](P832A_assets/verification.json).
Full logs/TRX are retained in [worktree evidence](../../.scratch/p832a/evidence/).

## Recipe for Claude on the rig (not executed by this lane)

Use a copy of the rig's existing known-good configuration. Preserve its artwork,
ports, civ tuple and showcase settings. Replace only the selected game's goals;
remove its legacy top-level `gameLengthSeconds` for a goal-only experiment.
Each row is a complete `goals` object; other goals retain disabled defaults.

| Config | `games[0].goals` | Own-rule trigger |
|---|---|---|
| Conquest | `{"conquestPercentage":100}` | Capture/destroy enough Flag-capable bases to reach the threshold |
| Territory | `{"territoryPercentage":70}` | Unanimously own enough sectors; use the existing larger map, not Brawl/small two-team rings |
| Team kills | `{"teamKills":20}` | Credit the twentieth enemy kill to a side |
| Prosperity | `{"teamMoney":1}` | Fully fund and complete the actual Reinforcement/team-money development; price comes from the selected core |
| Artifacts | `{"artifacts":10}` | Return ten neutral artifacts to friendly stations |
| Flags | `{"flags":10}` | Return ten enemy flags to friendly stations; native initial pedestals/flags now exist |
| Countdown | `{"gameLengthSeconds":600}` | Advance past ten simulation minutes; the existing native timeout ranking selects the winner/draw |
| Control | `{}` | Existing finale/deadline behavior; no new event-driven victory goal |

For strict goal-only observations, do not let a bot-finale deadline disguise a
goal failure: use human pilots or spawn the existing scenario with
`finale_after_minutes: 0`. Bots do not yet deliberately pursue prosperity,
flags or artifacts. The generic `invest` command and human investment pane use
the configured prosperity amount; completing the bucket still requires real
build time. Use the appropriate source-generated config/CLI invocation, e.g.
`Allegiance.Server --config <copy>.json --goal flags` or `--goals '{"flags":1}'`
for a short human pickup/return proof. Watch GAME_STATE counters and GAME_OVER
reason/winner, then confirm the fresh mission retained the same fixed goals.
No changes were made on the rig, Mac or another host.

## Native-intent classification

- **Design:** numeric combinable goals, zero/100 disable sentinels, threshold
  checks, native goal presets, prosperity development pricing/completion,
  flag pedestals, neutral artifacts, initial rankings and briefing values. The
  port implements the creation/configuration path and retains the existing
  event-trigger machinery; the broad-body CSV does not claim unverified guards.
- **Era constraint:** fixed packed MissionParams/MISSIONDEF layout and native
  DirectPlay/COM/SQL host plumbing. The layout remains interoperable; managed
  TCP/HTTP, source-generated JSON and existing stores handle the modern seams.
- **Bug / omission:** the port's unreachable Stage-1 goal switches, hardcoded
  threshold substitutes, zero prosperity briefing amount, and implementation
  credit without reachability were port omissions. No native gameplay bug was
  deliberately copied or silently repaired as a new victory rule. The unused
  native artifact-percentage cell is retained only as its Reset value.

A better modern approach is the typed, detached goal model now shared by config,
mission, wire and catalogs, with validation at assembly and explicit production
reachability in the scorecard. Future lobby editing and per-match rotation
should replace that same model atomically; strategies should consume it rather
than introduce another family of mode booleans or infer goals from finale timers.
