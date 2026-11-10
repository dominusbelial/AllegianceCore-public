# P987 (P986b) — recorded bot decision corpus, slice 2

## Summary

P987 adds the real-match missing half of P986: compact input snapshots recorded
from one seeded 2-side and one seeded 3-side loopback showcase, then replayed
through the same pure decision cores. The reviewed corpus has **614 records,
1,649,805 bytes, and 30 distinct replayed decision types**. Both recording runs
reached combat, armed a finale, rotated through game over, and started the next
match; the 3-side run also released real massed and deadline-forced strike waves.

No server, simulation, goal-stack, profile, wing-formation, or intended bot
behaviour changed. Changes are the opt-in recorder, small behaviour-preserving
pure-core extractions, replay adapters, the opt-in recording fixture, and tests.
Raw match streams are not checked in.

## Inventory conventions and coverage

Every action/target/order/duty/lifecycle decision in `tools/allegiance-mcp/`
is accounted for below. Telemetry formatting, transport ownership, caching,
spatial indexing and JSON/result merging are not decisions and are not counted
as such. **Pure** means the recorded inputs determine the output; **adapter**
means a small pure core was extracted from a live-world caller without changing
the call's result; **live** means the complete decision still depends on world
and/or historical state. “2+3” means independently recorded and replayed in
both scenarios. “Instrumented / unexercised” is **not** claimed as corpus
coverage.

### Recorded and replayed decisions

| Decision / native source line | Inputs read | Output | Form | Coverage |
|---|---|---|---|---|
| Combat target pick — `CombatTargetSelector.cs:132` | Visible candidate id/class/threat/distance, own cluster, teammates, engager counts | Target id, hold, class | Pure | **2+3** |
| Role loadout — `TechLoadoutSelector.cs:43` | Real core, civ, full 50-byte tech mask, dock/target station, role, budget, equipment flags | Hull, mounted parts, price, reason | Pure + real core | **2+3** |
| Nanite loadout — `TechLoadoutSelector.cs:424` | Core, civ, full tech mask, dock, budget | Cheapest legal support hull/parts | Pure + real core | **2+3** |
| Medevac scout assignment — `BotManager.Medevac.cs:50` | Stable bot roster ids, side, role, rescue-probe legality | Chosen scout ids per side | Pure | **2+3** |
| Medevac probe drop gate — `BotManager.Medevac.cs:82` | Own station/pod/eject observations, nearby rescue probe, last drop and load time | Drop / wait | Pure | **2+3** |
| Defense budget — `BotManager.Defense.cs:43` | Population, already assigned, cap, share | Additional defender budget | Pure | **3** |
| Defense assignment ranking — `BotManager.Defense.cs:50` | Candidate ship, topology hops/local distance, budget | Selected ship ids | Adapter; pure rank | **3** |
| Distinct-entry route enumeration — `BotManager.cs:1080` (pure `RouteEnumeration` at :1010) | Actual observed warp pairs, home, rendezvous, route cap | Ordered cluster paths | Adapter; pure route search | **2+3** |
| Least-loaded route — `BotManager.cs:1127` | Per-route assignments | Route index, lowest-index tie | Pure | **2+3** |
| Travel next hop — `BotManager.cs:2697` | Current cluster and assigned path | Next cluster / hold | Pure | **2+3** |
| Scenario/no-fog role target — `BotManager.Lifecycle.cs:464` | Role, explicit rendezvous, observed enemy home | Target sector or null | Pure | **2+3** |
| Support roster slot — `BotManager.Support.cs:21` | Side ordinal/count/share | Support duty yes/no | Pure | **2+3** |
| Support phase — `BotManager.Support.cs:45` | Finale/wave state, fighter rendezvous/presence counts | Opening/strike and anchor | Pure | **2+3** |
| Support destination — `BotManager.Support.cs:151` | Damaged station, phase, own fallback target | Sector and phase-driven flag | Pure | **2+3** |
| Repair hold/drop — `BotManager.Support.cs:168` | Time, hold age, current/best hull, pose age, last improvement, limits | Keep/full/stale/no-improve/leash | Pure | **3** |
| Constructor rock — `ResearchCommander.cs:275` | Observed rocks/capabilities/availability, asteroid class, reservations | Rock id or null | Pure | **2+3** |
| Constructor travel vs build — `ResearchCommander.cs:288` | Constructor and rock sectors | `goto` / `build` | Pure | **2+3** |
| Research donation — `ResearchCommander.cs:298` | Wallet, bucket price, already funded | Donation amount | Pure | **2+3** |
| Finale arming — `ShowcaseFinale.cs:69` | Age, selection, side maturity/relaunch share/kills, recent kills | Armed side ids | Pure | **2+3** |
| Strike volley requirement — `StrikeVolley.cs:206` | Real station profile, actual loadouts, wing cap, losses/defenders, landing share | Required/kill hulls, shots, window, can-kill | Pure + real core | **3** |
| Wing growth cap — `BotManager.StrikeMass.cs:156` | Required, reserved, cap | Target wing size | Pure | **3** |
| Wave commit reason — `BotManager.StrikeMass.cs:643` | Assembled/required, staging time, deadline/window/lead | Hold/massed/deadline-forced | Pure | **3** |
| Wave release — `StrikeVolley.cs:369` | Now/go time, committed membership, already released | Release / hold | Pure | **3** |
| Enemy faction pick — `MultiSideEnemy.cs:18` | Own side plus observed active/allied candidates, resolved hops, kills/stations | Enemy side or null | Adapter; pure rank | **3** |
| Frontier enumeration — `SideIntel.cs:119` | Observed graph, known/swept/done lanes, warp knowledge, own home | Ranked frontier lanes | Pure | **2+3** |
| Scout lane assignment — `SideIntel.cs:191` | Lanes, scout posts/scanner ranges, current lanes, pair hops | Scout-to-lane map | Pure with recorded hop inputs | **2+3** |
| Enemy-home recon scout — `SideIntel.cs:327` | Scout posts/scanner ranges, target, hops | Scout id or null | Pure with recorded hop inputs | **2+3** |
| Main clash location — `SideIntel.cs:383` | Graph, homes, known sectors, rocks, miners, enemy stations, fallback flag | Sector, score and explanation | Pure | **3** |
| Pod/death/relaunch transition — `BotManager.cs:3642` | Previous pod/docked flags, current lifepod hull, dock station | Combat/enter/fly/dock/await/relaunch | Adapter; pure state core | **2+3** |
| Respawn vs game-over reset — `BotManager.Lifecycle.cs:253` | Previous pod state and mission-change classification | Reset and reinforcement flags | Adapter; pure reset core | **2+3** |

### Instrumented, but not exercised by these matches

These have replay adapters, but no raw record was produced, so they remain
explicitly uncovered in `recorded.json` rather than being supplemented with
hand-made evidence.

| Decision | Inputs → output | Form / why not covered |
|---|---|---|
| Commander defend target — `BotManager.Orders.cs:33` | Enemy positions, protectee, current target → retained/new threat | Pure; no accepted `CidDefend` with a visible candidate occurred. |
| Finale escort target — `ShowcaseFinale.cs:139` | Escort/bomber/enemy geometry, attacking flag, radius/leash → target/null | Pure; configured escort budget rounded to zero. |
| Research-wave staging — `ResearchTactics.cs:108` | Visible/wolf counts, quota, wait → ready | Pure; these runs used the finale coordinator, not a `ResearchTactics` mass wave. |
| Research-wave station — `ResearchTactics.cs:751` | Stations, strike pilots, abandon times, geometry → station | Pure; same unexercised path. |
| Research-wave rally — `ResearchTactics.cs:799` | Friendly stations, targets/warps, strike sector → rally | Pure; same unexercised path. |

### Uncovered decisions and the reason

| Decision / file:line | Inputs and output | Form / why not covered |
|---|---|---|
| Full engagement, aim, fire, boost and stand-off — `BotManager.cs:2885`, `EngageShipAt` :3114 | Live ships, recent hits, weapons, poses → target and `ControlData` | Live; whole `ClientWorld`/weapon snapshot and control sequencing is too large for a compact extraction. Target pick is covered. |
| Warp travel execution — `BotManager.cs:2609` | Own pose, next sector, visible warp, traffic → warp leg/loiter | Live; path and next-hop cores are covered, wire/control execution is not. |
| Defense incident ingestion/drive — `BotManager.Defense.cs:65`, :232, :264 | Chat/hit history, live stations/ships, existing deployment → incidents and combat detour | Live/historical; pure budget/rank are covered. |
| Repair target selection — `BotManager.Support.cs:199` and support driver :265 | Live own assets, fractions, pose ages, finale/cooldown state → repair target and flight | Live `ClientWorld`; phase/destination/hold are covered instead of shrinking this large selection incorrectly. |
| Medevac pod observation/drop execution — `BotManager.Medevac.cs:140`, :163 | Live pods/probes/stations and launcher → observation counts, fire expendable | Live; pure drop gate is covered. |
| Purchase/retry/next-match launch — `BotManager.Lifecycle.cs:60`, :166, :255, :383 | Dock/ACK/grant/lives/wallet and attempt history → reset, buy, launch | Live/historical; pure life/reset classification is covered, not BUY_LOADOUT sequencing. |
| Scout sweep/raid execution — `BotManager.Exploration.cs:849` | Own world, lane/recon state, visible miners, sweep cursor → travel/engage/sweep | Live/historical; frontier/assignment/recon/clash choices are covered. |
| Varied loadout rotation — `TechLoadoutSelector.cs:126` | Legal candidates plus hull/part use, seed/ordinal → weighted selection | Pure but not recorded; both scenarios deliberately used `loadout_mode=best` for strike comparability. |
| Fallback/available-strike loadouts — `TechLoadoutSelector.cs:326`, :614 | Requested role, fallback pool/target, tech/budget → selection | Pure but separate selection paths were not exercised in the stored records. |
| Rescue/aleph launcher ranking — `TechLoadoutSelector.cs:65`, :89 | Core and tech mask → launcher id | Pure but not separately instrumented; legality feeds the covered medevac roster input. |
| Mine/missile/cloak policy — `Showcase.cs:351`, :369, :387, :397; `CloakPolicy.cs:12` | Pursuer geometry, speeds, range, signature/energy, parts → fire/drop/cloak bits | Pure helpers exist but were not extracted into this corpus; live dispenser driver is `BotManager.Dispensers.cs:21`. |
| Aleph-field duty — `BotManager.AlephField.cs:43` | Observed field launcher, threatened/arrival aleph, duty state → travel/loiter/drop | Live/historical; no small core extracted. |
| Turret recruitment/aim/fire — `GunnerPolicy.cs:124`, :204; `BotManager.TurretGunner.cs:72` | Roles/free seats, occupied mounts, aim angle/range → board, controls, fire | Pure policy + live boarding flow; gunner flow was off and not recorded. |
| Full research plan — `ResearchCommander.cs:96`; reserved prosperity plan `ResearchCommander.Prosperity.cs:107` | Snapshot plus work index, donations, reservations, announcements, completions → command list | Snapshot is explicit but commander is stateful across ticks; pure rock/order/donation cores are covered, full sequence is not. |
| Full tactical order loop — `ResearchTactics.cs:198` | Pilots/targets plus sent-order, wave, rally and escort history → bounded orders | Live/historical; three pure helpers have adapters but were unexercised. |
| Finale coordinator bookkeeping — `BotManager.Finale.cs:936`; `BotManager.StrikeMass.cs:668`, :752 | Live sightings and per-wave history → target, wing mutation, go members | Live/historical; arming, sizing, commit and release cores are covered. |
| Experimental reachable-strike policy — `FinaleAssembly.cs:15`, :30, :41, :47, :64, :67 | Reachability, demand, path threats, arrival/deadline, refit state | Pure but `finale_reachable_strike=false`; no experimental coverage claimed. |
| Flags/artifacts/conquest/kills duties — `BotManager.Flags.cs:97`, :214; `BotManager.Artifacts.cs:21`; `BotManager.Conquest.cs:60` | Objective contacts/cargo, candidates and quota state → duty assignment and travel | Pure planners + live execution; scenarios were ordinary showcase, not objective presets. |
| Supervisor coordination — `ScenarioCoordination.cs:42` | Worker stage/defense/objective snapshots and incidents → merged deployments/releases | Pure over merged state but historical and multi-worker; recording used one in-process manager. |
| Worker recovery/refill — `WorkerSupervisor.Recovery.cs:179`, :241; `SpawnBatch.cs:22` | Desired slots, corroborated worker losses and liveness → respawn/refill | Live/distributed and out of the single-manager recording fixture. |
| Ordered miner routine — `NativeBotRoutines.cs:85` | Actual carried ore/docked state → return-to-station | Pure rule but no `behavior=miner` peers; server-owned showcase miners are not MCP decisions. Constructor duty is covered. |
| Docker/deathmatch routines — `NativeBotRoutines.cs:98`, :169, :214 | Random/timer/face state, enemies, hull/ammo/fuel → face/fly/dock/target/rearm | Stateful routines were not spawned in these showcase matches. |
| Authored special scenarios — `ScenarioPlanner.cs:48`, :64; `BotManager.Scenarios.cs:292`, :678 | Core/hull preferences and scripted scenario state → bomber/capture plan | Pure plan + live orchestration; not `run_scenario` fixtures. |
| Showcase fleet composition — `Showcase.cs:135` | Core roles and side count → weighted fleet slots | Pure but not separately recorded; resulting real loadout/route decisions are covered. |
| Public-topology battle sector — `ShowcaseBattleSector.cs:11` | Graph homes and topology → neutral rendezvous | Pure but the 3-side target resolution did not call this helper (home-station targets won). |
| Docker/chat/buoy order dispatch — `BotManager.Routines.cs:55`, :132 | Chat/buoy/order state and current dock/pose → native order/mining/docker action | Live/historical routine peers were not spawned. |
| MCP drone/order translation — `DroneCommands.cs:25`, :68 | Operator command/target text → command id/object type/id | Pure but operator parsing, not an autonomous showcase decision. |
| Matrix combinations — `MatrixPlanner.cs:47` | Hulls/weapons/launcher matrix request → combinations | Pure planning for a different opt-in tool, not a showcase-match policy. |
| Spawn connection/ACK retries — `SpawnLaunch.cs:54`, :85 | Connection, own launch and BUY_LOADOUT ACK → confirmation/retry | Live network state, not a pure in-match decision. |
| Reconnect watchdog — `StallWatchdog.cs:87`, :127 | Receive ages, stall reports, grace setting → reconnect/log/dispose | Operational live state; no reconnect was induced. |

## Record/replay design

- `BotDecisionRecorder` writes version-2 canonical JSONL only when
  `ALLEGIANCE_BOT_RECORD_DECISIONS=1`. Scenario and output path come from
  `ALLEGIANCE_BOT_RECORD_SCENARIO` and `ALLEGIANCE_BOT_RECORD_PATH`.
- Each raw item is `{version, scenario, family, decision, source, inputs,
  outputs}`. Integral ids/prices remain exact; floats are rounded to six places.
  The recorder never mutates bot state.
- `BotDecisionReplay` dispatches a stored input to the **same** helper or small
  pure core used live. The replay test compares canonical expected and actual
  output JSON and prints the record id, full input, and expected/actual diff.
- `UPDATE_CORPUS=1` with `P987_RAW_PATHS` dedupes exact input/output decisions.
  It first preserves distinct outputs (so a rare armed/release/pod outcome is
  not lost), then fills each scenario/family/decision to at most 16 records by
  stable content hash. Stable ids are
  `scenario/family.decision/<first-16-SHA256-hex>` over scenario, decision,
  inputs and outputs; there is no timestamp, run number, source line or peer id
  in the id. Match order is discarded.
- The test enforces both scenarios, required decision families, version/id
  shape, and a 2 MiB maximum. All PCore001-loading tests have
  `[Trait("Requires", "Artwork")]`.
- `tools/p987-bot-decision-corpus/record.sh` performs two bounded recording
  processes, regeneration, and a normal replay. Corpus-folder `README.md`
  documents retained-raw regeneration and when a diff is a real behaviour
  change. The two P987 live theories skip unless `UPDATE_CORPUS=1`.

## Scenarios recorded

| Scenario | Fixture | Observed outcome |
|---|---|---|
| `seeded-2side` | `BenchmarkSeed=987`; `m2xspiderw.igc`; 10v10; research x64; explicit Alderan rendezvous; both-side finale after 3 s plus one kill; 240 s match cap. Static station visibility was seeded once as in P733, then all decisions ran live. | **123.9 s wall**: 989 shots, 613 ship hits, 5 kills, 5 pods; finale sides `[0,1]`; natural cap game over and next Started. 87,356 raw records / 50,030 bot ticks. |
| `seeded-3side` | `BenchmarkSeed=987` and `SingleRing Seed=987`; 12v12v12; research mode with native starting-tech overrides; all-side finale; survival margin 0; 60 s wave deadline; 600 s cap. | **289.4 s wall**: 9,735 shots, 2,218 ship hits, 44 kills, 31 pods (18 docked, 13 restarted); real massed and deadline-forced wave releases; natural cap game over and next Started. 348,465 raw records / 207,131 bot ticks. |

The raw streams are 23,223,979 and 82,630,617 bytes respectively. Only the
614 sampled records are checked in. Raw JSONL/TRX/logs are retained outside the
repository under `~/allegiance-evidence/P987/`.

## Proving the corpus catches drift

Each edit below was made alone, the replay was rebuilt and run, and the edit
was immediately reverted. The final tree was replayed again after all reverts.
A preliminary nearest-tie `<`→`<=` check passed because these real samples had
no equal-distance candidate pair; it is not counted as proof. The fighter-cap
case below is a real recorded cap-sensitive input instead.

### 1. Combat family — fighter engager cap 3 → 1

`CombatTargetSelector.CapFighter`: expected target and class became a hold.

```diff
--- line 2
expected:   "chosenClass": 1,
actual:     "chosenClass": 0,
--- line 3
expected:   "hold": false,
actual:     "hold": true,
--- line 4
expected:   "targetId": 8122
actual:     "targetId": null
```

Record: `seeded-3side/CombatTarget.Select/0f9de1867686a6d6` (one fighter
candidate, already one engager). Replay failed 1/0 as required.

### 2. Finale family — arming threshold +20 s

`elapsedSeconds < settings.AfterMinutes * 60` gained `+ 20`; the actual armed
input at 16.396174 s with three mature sides and one recent kill no longer armed.

```diff
--- line 2
expected:   "selected": [
actual:     "selected": []
--- line 3
expected:     0,
actual:   }
--- line 4
expected:     1,
actual:   <missing>
--- line 5
expected:     2
actual:   <missing>
--- line 6
expected:   ]
actual:   <missing>
--- line 7
expected: }
actual:   <missing>
```

Record: `seeded-3side/Finale.TriggerSides/2aff7e0f1b495a28`. Replay failed 1/0.

### 3. Defense family — budget ceiling → floor

`Math.Ceiling(population * share)` became `Math.Floor(...)`; population 9 and
share 0.15 produced a different budget.

```diff
--- line 2
expected:   "budget": 2,
actual:     "budget": 1,
```

Record: `seeded-3side/Defense.Assignment/1348181ba286d0ca`. Replay failed 1/0.

## Overhead

With recording off, every recording seam returns at a static Boolean check;
snapshot arrays, JSON and files are not created. Existing pure decisions and
their callers retain their result flow. No whole-tick timing claim is made from
code inspection alone; the unchanged never-parks gate is the behavioural/perf
check below.

Recorder ON metrics are measured inside `Record` (canonical serialization plus
JSONL I/O) and divided by `DriveCombat` calls:

| Run | Records | Bot ticks | Recorder cost per bot tick |
|---|---:|---:|---:|
| 2-side | 87,356 | 50,030 | **49.342288 µs** |
| 3-side | 348,465 | 207,131 | **32.335865 µs** |
| Tick-weighted combined | 435,821 | 257,161 | **35.644420 µs** |

The metric excludes the single integer `BotTick` increment and the final metrics
write. Concurrent bot threads account for recorder work exceeding wall time in
aggregate; these are attributable CPU/I/O microseconds per bot decision call,
not a wall-time slowdown percentage.

## Verification and gate

The report and corpus were written before the full gate. Final results are
recorded here after running it.

| Step | Result |
|---|---|
| Baseline replay after all drift reverts | **PASS 2 / 0 / 0**; final cleanup replay also passed 3× alone (**6 / 0 / 0**) |
| Existing bot decision/unit cohort | **PASS 376 / 0 / 0**; final cleanup cohort **75 / 0 / 0** |
| New Live recording theories | **3/3 per scenario** (6 / 0 / 0 total); each required combat, finale, wave-release (3-side), rotation and next Started |
| Whole-solution build | **PASS**, 0 warnings / 0 errors (final tree) |
| `tools/build-tools.sh` | **PASS**, 128 tool projects, 0 warnings / 0 errors (final tree) |
| One `tools/run-tests.sh --tier fast` | **13,858 passed / 1 failed / 3 skipped**, 13,862 total in 609.9 s; failure/timing offenders isolated below |
| Showcase never-parks 5× vs base rate | Initial cohort **5/5**. Final-code cohort **3/5** at load ~24; both failures passed **3/3 each** in isolation (**6/6**). Base: P985 **4/5** initial with 3/3 retries. |

The Fast failure was the ledgered P739 delay-5 one-ULP boundary; it passed 3/3
alone (five theory cases each). The three passing-but-over-budget executions
(P814 turret selection, SC2 support selection, P777 warp-exit timing) also passed
3/3 alone. All four families are recorded in `docs/TEST_FLAKES.md`. Fast's three
skips are the optional private Meshy pack. No P987 replay or recording test
failed. `local.env` was present and used for every artwork-backed run.

The final-code showcase passes reached 24/30 with real hits. The arrival-only
failure reached 18/30 while retaining a 2.209 s parking bound; the other failure
was a support-idle bound. Six exact retries all reached 24/30 with real hits and
longest speed-below-one-with-target intervals of 1.215–3.523 s. These outcomes
are reported without converting the final cohort into a notional 5/5.

One P986 golden compatibility diff is intentional and non-behavioural: version-2
canonical JSON preserves integral values, so two `long.MaxValue` inputs in
`loadout.json` are written `9223372036854775807` rather than the old lossy double
spelling `9.223372036854776E+18`. All 376 existing bot-policy tests pass after
that serialization correction.

## Native intent

These MCP showcase choices are modern harness policy, not claims of native AI.
Native supplies the observed world, legal purchase/order mechanics, combat and
station damage, lifepod/reseat and game-over transitions. The recorder does not
alter those rules. Where native wingman behaviour has a small pure equivalent
(for example commander defend target retention), the existing native citations
remain in the decision source; no new native parity claim or parity CSV row is
introduced by this instrumentation task.

## Modern approach

Whole-match loopback traces are deliberately not golden: scheduling and wire
timing change between runs. Instead, real inputs are captured at decision
boundaries and replayed independently through pure cores. Small extractions
(`RankDefenseAssignments`, `ChooseRanked`, `NextHopInPath`,
`ClassifyLifeTransition`, `ReinforcementLife`, construction/order/donation
helpers) preserve the live result while making that safety net possible.
Uncovered live/historical paths are listed above rather than being represented
by synthetic records.
