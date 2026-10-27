# P832b — prosperity opponents

Working-tree implementation on `codex/p832b`, based on public dispatch
`04b860aeb733e0baac490d0a97399a2a6e0f3c0f`. No commits or pushes. No remote
hosts contacted. All verification below ran locally.

## Behaviour

The existing research commander now receives `MissionInfo.Goals` in its
socket-free decision snapshot. Miner growth uses the observing client's live
known-ore state, including depletion, rather than stale map-export ore.
With prosperity disabled, it executes the original research routine unchanged. With prosperity enabled it:

- Buys/replaces miners until two are observed alive, provided known He3
  remains. Purchases use real native drone buckets and wait for production.
- Retains the price of two legal starting fighter loadouts and two replacement
  miners. Growing the miner fleet may spend the miner allowance, while
  retaining the fighter allowance.
- Banks half observed wallet credits. The other half above the reserve
  remains available to the existing research plan, including He3 research,
  stations and combat developments. Savings persist across repeated ticks.
- Starts prosperity funding when surplus plus half the measured income over
  the next 300 simulation seconds can cover the outstanding price. A finish
  affordable from current surplus is funded immediately. Otherwise only the
  banked share is invested. Fully funding the bucket still waits for native
  completion; it never declares victory itself.
- Waits for the wallet debit after an investment, so receiving the bucket
  status before the wallet update cannot authorize spending stale money.
  Unacknowledged requests have the existing five-second retry convention.

The five-minute horizon limits early capital lockup: distant victories first
get productive miners and the normal technology plan. A 50% bank allocation
prevents a long plan from consuming all income forever. The replacement
reserve prevents victory funding from emptying the commander's wallet;
combat pilots retain their own independent purchase/donation paths. This is
an explicit bot strategy, not a native AI algorithm.

Enemy prosperity investment is public in native: donation `BUCKET_STATUS`
uses the mission group for `c_didTeamMoney`
(`FedSrv/FedSrv.CPP:7182-7196`), and a joining player receives every side's
winning bucket (`FedSrv/fsmission.cpp:808-830`). At 10% observed enemy
investment, half the available non-strike fighters, selected by sorted ship
id, prioritize observed enemy miners. Other fighters retain local duties;
all fighters still intercept observed bombers near friendly stations first.
Support retains repair duties and strike pilots retain station strikes.
Orders continue through normal individual chat/attack handling and its
existing progress-aware rate limiter. Logs append `economyRaid=true` while
this response is active.

The bot uses its own received mission rules, public civilization/core data,
its own wallet, own-side production/funding and visible targets. Enemy
bucket identities are reconstructed from public civilization and mission
research settings; enemy amounts come solely from the observing peer's
received `BUCKET_STATUS`. The bot never reads the opponent commander's
private connection, wallet, other investments or hidden miner positions.
No protocol/server cheat path was added.

## Native contracts consumed

| Contract | Re-grepped native source | Implementation/evidence |
|---|---|---|
| Goal and price | `Igc/missionigc.h:770-794`, price at 790 | `ResearchCommander.Prosperity.cs`; configured goal funding test |
| Team-money required-tech exemption | `Igc/sideigc.h:237-243` | Goal funding works with normal research tech gates enabled; no unlock fixture |
| Public enemy economic investment | `FedSrv/FedSrv.CPP:7182-7196`; `FedSrv/fsmission.cpp:808-830` | `ObserveEnemyProsperity`; receive-dispatch and local TCP tests |
| Mining/capacity/income | `Igc/shipIGC.cpp:1988,2165-2213` | Existing miner simulation unchanged; ordinary drone purchases grow both sides |
| Development completes the economic victory | `FedSrv/FedSrv.CPP:11259-11280` | Real local showcase match receives `GAME_OVER`, winner and prosperity reason |

`docs/parity/sim-rules-matrix.csv` records these consumers against SIM-0047,
SIM-0637, SIM-0816 and known-ore SIM-0044. No score/status was promoted:
the full native DevelopmentCompleted body remains partial. The strategic thresholds above
have no native parity claim.

## Verification

- Whole solution: **0 warnings, 0 errors**. All **89** standalone tool
  projects built successfully with `tools/build-tools.sh --drop-bins`.
- Eight new tests: **3/3 each in fresh filtered invocations**. The revised
  preset, wallet-reporting guard and live ore snapshot also passed targeted
  final-source follow-ups. Existing commander/tactics regression group:
  **62 tests × 3 passes**. Existing P832a generic investment TCP test: **3/3**.
- The no-goal commander routine and helpers are byte-identical to dispatch
  after renaming its entry point. The same trace test ran against the
  untouched dispatch MCP assembly and the changed assembly, producing
  SHA-256 `1DFA06A9A3DDFF950DE89ABBC20FFCF913C9065297C3D56300B473B6D08FBD64`
  in both. Empty goals and a combined set of non-prosperity goals also
  produce identical command/telemetry traces.
- The seeded full showcase preset won **3/3**: 13.05–14.40 wall seconds of
  accelerated local ticking; 112.85–114.05 simulation seconds. Bravo (side 1)
  won each measured match. All six showcase pilots were asserted present.
  Both sides reached two live miners; goals and enemy investment were
  observed over TCP. The received reason was
  `Bravo won by achieving the prosperity/artifact victory objective.` The
  winning team's economic-victory bucket was complete, and the fixture
  enabled no artifact goal. No forced finale/game-over or wallet/tech grant.
- Reaction trace: fighter 2 switches from local enemy ship 10 to observed
  miner 11 in another sector at 10% enemy investment; fighter 3 stays on
  local ship 10. Support still repairs and both fighters override raids to
  intercept bomber 13 threatening home. Receive-dispatch coverage checks
  goal-off, allied-side and reset exclusions.
- **ONE Fast-tier run:** 10,969 cases, 313.5 seconds; **10,964 passed,
  3 optional skips, 2 failed**. No timing violations. Failures were the
  ledgered read-only profile configuration path and socket-cleanup count
  race (19 observed instead of 20). Exact follow-ups passed **3/3** with
  scratch `XDG_CONFIG_HOME` for the first, and **3/3** unchanged for the
  second. The broad result remains failed and was not repeated.
- P727/P682b regressions belong to P837; neither implementation was touched
  and the full Live tier was not invoked. The requested individual live
  proofs were run locally. No Core/Server change, hence no AOT publish gate;
  no HUD/interface, services, ports or environment variables were added.
- Glossary paths, parity scorecard and the 351-body mission-rule inventory
  validate. Working-tree hygiene checks find no new media, private paths
  in code, secrets or personal email.

The same-host untouched-base parking cohort passed **5/5**, with a maximum
low-speed-with-target interval of 2.006 seconds against the five-second
assertion. The changed-tree cohort ran sequentially after build/Fast
validation, holding the shared test lock and passing the tier runner's idle
criterion (two load/CPU samples below 0.8; 24 CPUs). Initial normalized
samples were 0.2714 and 0.2530. Base runs overlapped short build/validation
work; their individual host-load readings are retained rather than described
as an otherwise empty host. The changed-tree cohort passed **5/5**,
matching the base's **5/5**.
The longest changed-tree low-speed-with-target interval was **2.019 seconds**
(base **2.006 seconds**), below the unchanged five-second assertion.
All runs reached at least 24/30 showcase pilots' objectives and observed
real gunfire and damage. No parking or combat assertion was changed.

Small public evidence: [`P832B_assets/verification.json`](P832B_assets/verification.json).
Raw local logs/TRX and reproduction scripts remain in
[the ignored task evidence directory](../../.scratch/p832b/); copied baseline
executables have been deleted. The sandbox permits the worktree evidence
fallback; no outside-worktree evidence directory was written.

## Local and 221 recipe for Claude

The tested preset is [`tools/p832b-prosperity.json`](../../tools/p832b-prosperity.json):
seed 832, SingleRing, civs 25/3, `teamMoney: 0.01` (720 credits in
PCore001), income ×5000, research ×16, no test unlock or money grant,
60-second intermission. The local live test loads this file directly.

Claude can run this recipe **on 221**; this lane did not contact it. From the
repository root with the usual artwork environment configured:

```sh
~/.dotnet/dotnet run --project src/Allegiance.Server --no-build -- \
  --config tools/p832b-prosperity.json
```

Against that server use the existing MCP driver, then append this command to
its command file **after initialization**:

```json
{"tool":"spawn_scenario","args":{"preset":"showcase","count_a":3,"count_b":3,"staging":true,"finale_after_minutes":0}}
```

For example, in another terminal:

```sh
mkdir -p .scratch/p832b-221
: > .scratch/p832b-221/commands.jsonl
python3 scripts/mcp_driver.py \
  tools/allegiance-mcp/bin/Debug/net10.0/allegiance-mcp 127.0.0.1 6073 \
  .scratch/p832b-221/commands.jsonl .scratch/p832b-221/replies.log \
  .scratch/p832b-221/bots.log
```

Append the JSON command from a third terminal after `INIT` appears in
`replies.log`. Poll `get_perf_stats` for research statuses. Expect both sides
to grow to two miners, fund the economic-victory bucket, then receive a
prosperity `GAME_OVER` with side 0 or 1 as winner. Allow **three simulation
minutes / about three wall minutes** at normal 20 UPS, plus startup; the
loopback test drives the same fixed ticks faster and has a 150-wall-second
bound. The measured local matches finish in roughly two simulation minutes.
`economyRaid=true` marks reaction orders when fighters can see targets;
the very short proof may end before scouts find enemy miners. The pure
reaction test verifies the changed attack target and preserved home defence.
Use a larger multiplier (e.g. `0.5`) for longer tactical observations, allowing
at least the five-minute funding horizon plus bucket construction. Leave
`finale_after_minutes` zero so a showcase finale cannot supply the win.

For an offline skirmish, the existing `SkirmishOptions.Goals` path accepts the
same `new MissionGoals { TeamMoney = .01f }`; its research driver uses this
commander. No UI/HUD behaviour changed.

## Native-intent classification and modern approach

**Native design:** the prosperity goal, loaded-core multiplier price,
required-tech exemption, public enemy economic investment, ordinary
bucket/drone purchases and the server's completion-driven economic victory.
**Port policy:** two productive miners, two fighter/two miner reserve,
half-income savings, five-minute affordability horizon, 10% reaction
threshold and half-fleet economic raids. These make human native actions
purposeful bot decisions; they do not port a nonexistent native strategic AI.

A modern improvement would estimate income from acknowledged credit events,
price a reserve from recent casualty/replacement demand, and compare time to
victory against visible enemy construction progress and raiding travel time.
The present conservative wallet-credit estimator deliberately underestimates
income when a donation masks a simultaneous credit. Adaptation should retain
native visibility and purchase gates and remain disabled for other goals.
