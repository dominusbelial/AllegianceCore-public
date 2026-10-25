# P819 — showcase side bias

This change fixes a reproducible bot control conflict and removes the showcase
preset's default leader-only finale. It does **not** change server combat,
economy, movement, purchase gates, civilizations or human-play rules. No commits,
pushes or remote-host access were performed.

Working base: `b8f578e2` on `codex/p819`; the supplied rig batch describes
`6e5c745f`. This report does not claim that the eight wins have a single proven
cause or that 200v200 balance is now established.

## What the copied evidence establishes

The copied batch contains **13 completed deadline matches**, plus the opening of
another match. Side 0 won 11/13, including the last eight. Those last eight average
**1,015.12 kills / 646.00 deaths** for side 0 and **817.50 / 795.88** for side 1.
These are the server's deadline scores; the skills counters have different
semantics and must not replace them.

The **15:3 finale commit ratio is also a 15:3 selection-opportunity ratio**:

| Worker | Finale selections: side 0 / side 1 | Finale commits: side 0 / side 1 |
|---|---:|---:|
| worker 1 (also runs the research commanders) | 5 / 0 | 2 / 0 |
| worker 2 | 10 / 3 | 13 / 3 |
| Total | 15 / 3 | 15 / 3 |

The rig was using `leader`, not simultaneous finales. Each worker chose from
its own kill observations. The larger raw commit count therefore does **not**
establish that side 1's route is five times less successful. Across the copied
staging samples, side 0 had 199 observations, 94 with positive assembly, and a
mean 7.42 armed bombers; side 1 had 52, 17, and 8.65 respectively. These are
repeated, selected observations, not independent trials or transit durations.

There is nevertheless a real routing ownership defect. A flying bomber can
already have an accepted `CidGoto(Warp)` rally order when
`TryDriveResearchFinaleRefit` promotes its existing purchased strike loadout.
Promotion clears `TargetId` and `OrderedTargetId`, but previously retained
`OrderedCommand`. `DriveCommanderOrder` runs before `HoldFinaleStrike` and
ordinary transit. Its `CidGoto(Warp)` branch holds **750 units short of the
aleph**. Assigning a new sector/path consequently cannot make that bomber cross
the aleph. Changing path length or adding a transit timeout would leave the
same higher-priority order in control.

Worker 1 has **30 flying-strike promotions; 29 had a previously accepted rally
order** in the reconstructed accept/lifecycle stream: 21 at warp 2170, four at
2175, three at 2172, one at 2181. These are all entry alephs into target sector
1051, from Tellus, Gaia, Aurora and Juniper respectively. For example ship 8454
accepted `Goto Warp#2170`, then logged `accepted strike takes station duty ...
sector=1051` without clearing that order. Worker 2 has 100 flying promotions and
no accepted-order logs; absence there cannot establish server order state.
This defect affects either side and race. It is not sufficient evidence that
the defect alone caused the eight consecutive wins.

## Civilizations, geometry, economy and losses

Side 0 is **Iron Coalition (25), `plan_a`, 157 steps**, in Ganymede (1050).
Side 1 is **Ga'Taraan (3), `plan_a`, 181 steps**, in Organia (1051). The map is
`m2xspiderw.igc`; Alderan (1052) is the resource clash sector, not the map file.
Both homes have four alephs in the actual map; the initial two visible alephs
in the bot logs are fog-limited observations.

The main shortest route, including straight flight between the arrival and
departure aleph positions inside each sector, is reciprocal:

| Measurement | Side 0 attacking 1051 | Side 1 attacking 1050 |
|---|---|---|
| Sector route | 1050 → 1054 → 1055 → 1051 | 1051 → 1055 → 1054 → 1050 |
| Aleph crossings | 3 | 3 |
| Four flight legs, units | 3,434 + 7,350 + 7,992 + 7,580 | 7,580 + 7,992 + 7,350 + 3,434 |
| Station → station | 26,356 | 26,356 |
| Station → old rally hold (750 before final aleph) | 18,026 | 22,172 |
| Old rally hold → station | 8,330 | 4,184 |
| Approximate flight to target-sector 3,500 standoff | 22,856 | 22,922 |

Distances are geometric, excluding turning, exit clearance, obstacles, combat
and non-home launch positions. The standoff estimate stops at the target-sector
arrival if it is already inside the radius. It is not a measured travel time.
The other three-hop lane through Terra/Aurora is 30,222 units either way.
A four-hop route through Rigel/Alderan/Juniper is 24,961 units either way.
Hop count is therefore not flight distance, but the main route does not have
a large one-direction distance disadvantage. The fix retains the established
shortest visible entry and separate cloak lanes.

A second streaming audit aligns **130 named flying-strike promotions** with
each bot's mission reseating episode and the server's warp timestamps. It
tracks only explicitly named promotions flying a logged Bomber hull, excluding
legal fighter/scout fallbacks and unnamed reserved bombers:

| Recorded transit measurement | Side 0 | Side 1 |
|---|---:|---:|
| Target-sector entries after server finale arm, one per life | 172 | 46 |
| Median server-arm → target-sector entry, simulation seconds | 420.86 | 465.92 |
| Median residence in Bug Nebula between crossings | 84.66 | 85.01 |
| Median residence in Tellus between crossings | 92.30 | 92.74 |
| Recorded final main-lane crossing | 159 × 1055→1051 | 44 × 1054→1050 |
| En-route Bomber ejections in this subset | 19 | 10 |

The corridor traversal times are almost equal. Server-arm-to-entry includes
refits, holding and repeated lives; it is **not** a pure flight-time estimate.
Entries and deaths must not be compared as equal-exposure side totals: side 0
had five times as many worker selection opportunities, and this is a named
subset. Its en-route ejections are side 0: Tellus 11, Alderan 3, Juniper 2,
Gaia/Cirrus/Bug Nebula 1 each; side 1: Bug Nebula 5, Tellus 3, Alderan 2.
These locate real losses; the recorded ejection messages do not identify the
weapon responsible. Missing arrivals remain censored. Together with the
selection counts, this contradicts treating the raw 15:3 commit ratio as
evidence of a fivefold side-1 pathfinding deficit.

The copied logs also show real asymmetries that a race-agnostic bot fix must
not hide:

| Measurement | Side 0 | Side 1 |
|---|---:|---:|
| No-buyable-bomber warnings | 15 | 16 |
| Requested initial roles, fighter/interceptor/scout/bomber/support | 81 / 35 / 35 / 24 / 20 | 81 / 35 / 35 / 24 / 20 |
| Actual initial selected roles, scout/fighter/interceptor/support | 140 / 0 / 35 / 20 | 62 / 113 / 0 / 20 |
| Ore sold across the copied simulation span | 11,668.2 | 3,150.0 |
| Ore sold per simulation minute, including intermissions | 19.78 | 5.34 |
| Median logged research-commander wallet | 37,268 | 99,141 |
| Mean tracked miners remaining at 13 completed-match deadlines | 2.00 | 0.69 |
| Median time to research step 25, bot-wall seconds | 1,044 | 1,028 |
| Median time to step 50 | 1,685 | 1,719 |
| Median time to step 75 | 2,233 | 2,270 |
| Median time to step 90 | 2,617 | 2,620 |
| Last-eight mean research skill events per match | 92.0 | 92.0 |
| Last-eight mean nanite-ship skill events | 839.9 | 510.1 |
| Last-eight mean relaunch skill events | 1,043.9 | 1,194.4 |

Research milestone times are estimated from adjacent timestamped bot lines,
relative to each research episode's first log (roughly minute resolution).
Step numbers name different work in the two race plans; equal step/event
counts do not mean equal tech. Both sides encounter the legal station-local
bomber gate, but equal warning counts do not prove identical tech paths.
Initial fleet composition is a purchase selection count, not a census later
in battle. All 195 requested slots per side were not immediately buyable in
their requested roles.

Pod-trip records locate 9,020/11,743 ejections in Alderan for sides 0/1.
Other main sectors are enemy home (1,566/749), own home (544/420), Tellus
(524/269), Bug Nebula (50/210), and Juniper (448/154). These include the whole
copied span and repeated lives, not just reserved finale bombers. The copied
logs lack the continuous per-bomber pose/order timeline needed to assign all
of these deaths to a particular finale transit. `KILL_SHIP` source IDs alone
also cannot distinguish gun, missile, mine and collision damage by weapon;
the audit preserves that limitation instead of inventing an attribution.

Median successful **dock** trips are 224.84 / 168.91 simulation seconds;
successful ship rescues are 73.15 / 42.68. There are 2,796 / 3,042 oxygen
expirations, each about 300 seconds. Side 0 also has 98 probe-rescued trips
(median 148.95 s), side 1 none. These are trip durations, not purchase-ack
latencies; the server skills counters above are the available relaunch totals.
The logs record 14 initial miners and 14 built miners on each side across the
14 mission openings. **18 side-1 miners were killed, versus zero side-0 miners**.
All 18 were last logged in Alderan and credited to side-0 ships: 14 Scouts,
three light interceptors and one advanced stealth fighter. Initial miners
spawn before countdown; the audit preserves them and clears the old mission's
inventory at GAME_OVER. Spawn-minus-logged-kill tracking at the 13 completed
deadlines gives two miners for side 0 every time, and 0–2 for side 1 (mean
0.69). This is an event-derived census, not a periodic authoritative snapshot.
There is no extraction-rate ledger. The mining asymmetry is real, but the
larger side-1 commander wallet and similar research milestones do not support
a simple claim that ore starvation caused its losses.

Post-trip relaunch is much shorter than pod travel. Bracketing each `[undock]`
with adjacent server ping timestamps (`Server.Dispatch.cs:74` logs simulation
milliseconds), the median dock-end → undock interval is approximately
**0–0.124 / 0–0.100 s**, for 1,363/776 matched dock trips. Rescue-end → undock
medians are approximately **0–0.149 / 0.031–0.178 s**, for 1,090/1,872 matched
rescues. After oxygen expiry, the medians are **0.789–0.972 / 0.773–0.949 s**,
for 2,775/3,015 matches. These are log-bracket estimates; unobserved relaunches
are censored and pod-death endings are excluded. They do not suggest a large
side-1 purchase/launch delay after recovery.

## Changes and fairness

* `AssignResearchSiegeRoute` now clears the superseded accepted command,
  warp-origin bookmark and old target when research-finale duty takes over.
  It covers flying accepted loadouts and the relaunch assignment path, logs a
  bounded `[strike-route] ... supersede=...` receipt, and still accepts future
  orders through the normal handler.
* `ReadFinaleSettings` defaults the **showcase preset** to `both`. Explicit
  `leader`, `0`, `1` and `both` selections keep their behavior; non-showcase
  defaults remain disabled. The skirmish driver uses this showcase preset too.
* The local proof tool and streaming audit/summarizer make the comparison
  repeatable without contacting the rig.

There are no race IDs, side-specific boosts, free loadouts, teleports, economy
adjustments or hidden enemy coordinates in the fix. Native accepted-command
priority (`Igc/shipIGC.cpp:1936-1943`) and queued-order acceptance/clearing
(`WinTrek/WinTrek.cpp:10680-10697`) remain the ordinary order rules. Choosing
when a showcase duty supersedes its own old autonomous rally is bot policy,
not a newly ported native rule. The policy exclusions are recorded in
`docs/parity/p819-bot-policy.csv`.

## Local matches and validation

The primary comparison uses the copied rig's **900-simulation-second finale
window**: four unattended 15v15 matches, two normal and two civ-swapped, on
`m2xspiderw.igc`, research scale 8, income multiplier 5000, native fog, legal
purchases, varied loadouts, staging, both finale sides and eight reserved
bombers. The local maturity interval is 0.5 minutes after registration,
relaunch floor 0 and recent-kill floor 1. A 10 ms ticker accelerates simulation;
the ordinary native game clock is capped at 3,600 simulation seconds.

| 900-second run | Civs, side 0 / side 1 | Kills 0 / 1 | Deaths 0 / 1 | Winner | Finale commits 0 / 1 |
|---|---|---:|---:|---:|---:|
| Normal 1 | 25 / 3 | 62 / 62 | 18 / 39 | 0 | 0 / 0 |
| Normal 2 | 25 / 3 | 18 / 18 | 10 / 12 | 0 | 1 / 1 |
| Swapped 1 | 3 / 25 | 30 / 36 | 12 / 20 | 1 | 0 / 1 |
| Swapped 2 | 3 / 25 | 43 / 35 | 23 / 24 | 0 | 0 / 1 |

All four reached Stage Over with **zero simulation-stage failures**. The win
split is **side 0 3 : side 1 1**, and **Iron Coalition 3 : Ga'Taraan 1**.
All **four commits were massed**, one for side 0 and three for side 1.
Both tied-kill matches awarded the win to the side with fewer deaths (native
`FedSrv/fsmission.cpp:2468-2498`).

Native `[station-damage] ... killed=True` receipts prove three station kills:

* Normal 2: side 1's Ga'Taraan Stealth Bomber (hull 133) destroyed side 0's
  Garrison at sim **1310.85**, using SRM Anti-Base 1; side 0's Iron Coalition
  Stealth Bomber (420) destroyed the Tactical base at **1385.55**, also AB1.
* Swapped 2: side 1's Iron Coalition Stealth Bomber (420) destroyed the
  Ga'Taraan Garrison at **1986.55**, using SRM Anti-Base 2.

Thus side 1 both massed and killed a real Garrison with **either civilization**;
side 0 also massed and destroyed a real base. These are damage-model receipts,
not an inference from a target disappearing at mission reset. Other restart
bases survived, so the native deadline still determined the winners. Targets
are the enemy stations each side actually observed; some local runs first
selected a secondary station rather than the Garrison. The public receipts
retain those target IDs and every commit/kill line.

This demonstrates the corrected orchestration locally. It does **not** prove
50/50 200v200 balance: scale, earlier maturity, accelerated ticking, map
discovery, refit positions and observed station choice differ from the rig.
The side-swapped sample is too small to separate residual civ and position
effects statistically. The 221 recipe below is the remaining same-scale check.

For comparison, an earlier **300-second** matched set used otherwise equivalent
15v15 settings, with an ordinary 1,800-second game clock:

The final two exchanged civs while retaining the map/positions and all other
settings. Neither matched set used relocation or unlock.

| Run | Civs, side 0 / side 1 | Kills 0 / 1 | Deaths 0 / 1 | Winner | Finale commits 0 / 1 |
|---|---|---:|---:|---:|---:|
| Normal 1 | 25 / 3 | 17 / 34 | 20 / 8 | 1 | 0 / 0 |
| Normal 2 | 25 / 3 | 31 / 33 | 20 / 11 | 1 | 0 / 0 |
| Swapped 1 | 3 / 25 | 46 / 49 | 24 / 22 | 1 | 0 / 0 |
| Swapped 2 | 3 / 25 | 42 / 39 | 16 / 29 | 0 | 0 / 0 |

All four reached Stage Over with **zero simulation-stage failures**. The side
split is **1:3**, the civ split **Iron Coalition 1 : Ga'Taraan 3**. These small
runs do not reproduce the rig's side-0 sweep. Four trials, different scale,
accelerated ticking and discovery-dependent finale opportunities cannot
establish equal win probabilities or explain the 200v200 kill gap. Crucially,
**zero commits is a negative assembly result, not proof that the large-scale
finale now works**. Two runs reached the ordinary mission clock before the
finale's own deadline. This local 300-second bound is short for a newly refitted heavy bomber
starting about 26 km from the target. The copied rig actually uses a
900-second bound; the primary comparison above uses that window. The two sets
are reported separately rather than pooled into a balance claim.

Exploratory runs are retained separately rather than mixed into that split:

* Two earlier 15v15, 20 ms pilots finished 21–13 and 45–31 for side 0, both
  with 0/0 finale commits. They lacked the added ordinary game-clock cap.
* An unpatched pilot was censored at 600 wall seconds before both sides
  discovered an enemy station. It is not a completed win-rate baseline.
* A 30v30, 20 ms, 900-second-finale pilot reached its 1,200-wall-second harness
  limit at sim 2,200.95, before the finale deadline at 2,207.6. It is censored,
  not a victory. It recorded four/five sampled armed arrivals in the target
  sector; median sampled transit was **134.93 / 149.81 wall seconds** for
  sides 0/1, with 30-second sampling and missing arrivals censored. Old aleph
  rally orders were superseded on both sides, and the sampled command list
  no longer contained those orders. Peak assembled was only **2 / 2**, so
  neither side committed. This isolates the order handoff from the remaining
  survival/massing limitation; it does not establish a full home-to-staging
  travel distribution. The receipt also retains per-side repeated hull samples.
* An early exploratory swapped run was interrupted when superseded by the
  matched comparison and is excluded from the win split; its raw log remains
  at `.scratch/p819/fixed-swapped/`.

Validation:

* `~/.dotnet/dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
* `tools/build-tools.sh`: **all 78 tool projects built**, zero warnings/errors;
  the proof project was rebuilt after its final harness options were added.
* New `P819` tests: **5/5, three exact-filter isolated invocations**. The four
  side/civ cases exercise real accepted strike promotion and the actual
  command driver. The side-0 cases failed on the original retained order;
  both sides and both civs pass after the fix. The fifth case checks symmetric
  defaults and explicit overrides.
* Focused P801/P745/P798/P682/P656 policy, native-navigation and real-station
  damage checks: **97/97, three invocations**.
* **Exactly one** `tools/run-tests.sh --tier fast`: **10,407 passed, 3 skipped,
  6 failed**, 298.4 s, no timing violations. The six failures are filesystem
  context issues, not reported as a green gate: five already-ledgered
  `PlayerBundlePathsTests` cases discover the real ancestor `local.env` when
  their temporary fixture is under the checkout; one profile editor test
  attempts to write `player.json` outside the sandbox. All nine path tests
  pass **3/3 exact-class invocations** using the established SC2 logical
  `/proc/<parent>/fd/3` alias to the same workspace scratch directory; the
  profile method passes **3/3 exact-method invocations** with
  `XDG_CONFIG_HOME` redirected into scratch. Product code and assertions were
  unchanged. This is recorded in `docs/TEST_FLAKES.md` as environment context,
  not load flakiness. No second fast sweep was run.
* Glossary links, policy CSV validation and `git diff --check`: pass.

The showcase never-parks check passed **5/5 isolated runs** (7:07, 6:25, 5:29,
5:49 and 5:44). Completed live executions:

| Family | Pass / fail | Coverage |
|---|---:|---|
| P801 | 4 / 0 | Native-fog massing and real station destruction |
| P798 | 8 / 0 | Four runs each of native spider and training maps |
| P682 | 16 / 0 | Two runs of all eight next-match/third-match cases, both broadcast models and pipeline settings |
| P745 | 2 / 2 | Station destruction; the failures ended at the native deadline |

Both failed P745 executions **did assemble and release five-bomber finale
volleys**. The first had one massed commit and the second four, but neither
destroyed a station before the deadline. Two isolated executions using
untouched `b8f578e2` MCP bot code, built in scratch against the unchanged
Core/Server assemblies, both passed. The existing master ledger already
documents P745 outcome variance; these samples do not establish an unchanged
pass rate and must not be presented as a green three-pass gate or proof of
reliable garrison destruction. The requested same-scale rig comparison is
still the appropriate follow-up.

An interrupted non-PTY runner continued its child tests and collided with a
reused log filename. Subsequent captures were renamed without disturbing the
running file descriptors, and duplicate future launches were suppressed.
Per-case P745 captures remained separate. The table counts completed test
results, including pass lines observed before the collision; wrapper
tail/redirection errors are excluded from test results. The collision is
recorded in [validation evidence](P819_assets/validation.json).

## Recipe for Claude on 221

Run **two 200v200 matches**, using the existing 200v200 size-scale-13 worker
recipe, `m2xspiderw.igc`, civs `[25,3]`, research scale 8, income multiplier
5000, 30-minute maturity and **`showcase.finaleTimeoutSeconds:900`**. Set
`finale_side:"both"` on **every worker** (remove an explicit old `leader`
override); leave ordinary fog and legal purchases enabled. For the second
match swap the civs to `[3,25]`, preserving the map and other settings.

Merge these arguments into the existing supervisor's `spawn_scenario` recipe,
submitting it once through the supervisor so the fleet is distributed across
workers; do not independently spawn the full fleet on each worker:

```json
{
  "preset": "showcase",
  "size_scale": 13,
  "staging": true,
  "loadout_mode": "varied",
  "finale_after_minutes": 30,
  "finale_side": "both",
  "finale_min_relaunch_share": 0.5,
  "finale_min_kills_last_5_minutes": 5,
  "finale_bombers": 12
}
```

Keep the rig's other worker, gunner, support, map and transport settings. Use
fresh missions for the civ swap and retain each match's native deadline result.

Compare wins and server deadline kills/deaths **both by side and by civ**;
count `[finale] trigger sides=...` separately from `[strike] ... finale commit`
and its `reason`. Check `[strike-route]` receipts for old aleph rally orders,
per-side assembled/armed/pods/elsewhere, arrival timing, ore sold/miner deaths
and research milestone timing. Verify that a promoted bomber's old `Goto Warp`
no longer owns its flight. Two matches are a directional acceptance check,
not a statistical demonstration of 50/50 balance. No rig changes were made
from this lane.

## Evidence and reproducibility

* Small source-derived evidence: [rig audit](P819_assets/rig-audit.json),
  [map coordinates](P819_assets/map-geometry.json), and
  [exploratory run receipts](P819_assets/exploratory-matches.json).
  The four matched results are [local match receipts](P819_assets/local-matches.json).
  The primary 900-second results, including native commit/kill receipts, are
  [rig-window matches](P819_assets/rig-window-matches.json).
  [Named-bomber crossings](P819_assets/rig-transits.json) retain the transit subset
  and its explicit censoring/attribution limits.
* Copied raw inputs, retained as supplied: `.scratch/rig/server.log` and
  `.scratch/rig/bots/err.worker-{1,2}.log`.
* Local raw logs, receipts and gate outputs: `.scratch/p819/` in this worktree.
  P745's per-case captures are `.scratch/p745-siege-test-*.log`.
  They remain ignored; no raw logs or media are added to the public tree.
* Audit command:

  ```sh
  python3 tools/p819-side-bias-proof/audit.py \
    --server .scratch/rig/server.log \
    --bots .scratch/rig/bots/err.worker-1.log .scratch/rig/bots/err.worker-2.log \
    --output docs/reports/P819_assets/rig-audit.json
  ```

* Loopback command (run from the repository root, with the local artwork
  corpus configured):

  ```sh
  ~/.dotnet/dotnet run --project tools/p819-side-bias-proof -- \
    --output .scratch/p819/example --matches 2 --tick-ms 10 \
    --finale-seconds 900 --game-seconds 3600 --seconds 1800
  # Repeat in a different output directory with --swap.
  # --size-scale 2 --finale-minutes 3 --finale-seconds 900 selects the longer run.
  # --game-seconds controls the ordinary mission clock (default 1800).
  ```
