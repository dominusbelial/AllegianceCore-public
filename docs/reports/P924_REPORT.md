# P924 — home assignments and side-order audit

P924 adds the instrument for a controlled home experiment. The copied evidence
confirms five side-1 wins, **four decided by station kills**, but does not establish
a side-index bug. Side 0 actually wins the ship-kill count in match 2. The main
repeatable difference is pressure on side 0's stations, usually before the finale.
With side 0 always in Ganymede and side 1 always in Organia, these captures cannot
separate side from home. Gigacorp/Phoenix exchanging sides excludes a simple
“Phoenix always wins” explanation, not a home or scheduling explanation.

Base `8a2f35f3`, branch `codex/p924`. All changes remain in this working tree.
No commits, pushes, remote-host access or authored map-file edits were performed.
Civ balance, bot strategy and finale policy were not modified. No index-bias gameplay fix is claimed.

## Setting and implementation

`games[].homeSwap` accepts exactly these strings:

| Mode | First assembly | Second assembly | Subsequent rotations |
|---|---|---|---|
| `off` (absent/default) | Authored | Authored | Authored |
| `on` | Swap owners 0 and 1 | Swap | Swap |
| `alternate` | Authored | Swap | Authored, swap, … |

Non-off modes require exactly two teams and `liveSettings.debugMode=false`;
invalid modes fail boot. Synthetic debug topology injects separate bases and
overrides home resolution, so combining it with the experiment is rejected,
including the authored first phase of `alternate`. Default-off debug behavior
is unchanged. Counters belong
to each configured game, not the server-wide skill match number, and restart at
one after a server restart. No new environment variable or service is introduced.
JSON uses the existing source-generated server context and works in AOT.

[HomeAssignments.cs](../../src/Allegiance.Server/HomeAssignments.cs:10) remaps
**every** station owned by side 0/1, preserving neutral owners, station IDs,
positions, orientation, map-list order, clusters, warps and authored asteroids.
It returns the identical map object when disabled. The authored file is never
written. This takes place before research-map preparation and mission assembly
([Server.Lifecycle.cs](../../src/Allegiance.Server/Server.Lifecycle.cs:1363)), so
research home types/resources, runtime ownership, initial miners, flag pedestals
and flags, side-selection starting positions and home visibility all use the
receiving side's home. Civilization, team name, tech, plan and wallet belong to
the side. On generated maps the civilization input to map generation is reversed
before owner remapping, so generated home types match their receiving civs too.
This is additional support; the rig experiment must use the unchanged file map.

The existing `[matchup]` and game-over receipts retain civ seed/tuple and append:

```text
homeSwap=alternate homeMatch=2 homes=1,0 home0=1051 home1=1050
```

`homes` lists the authored owner assigned to each side; `home0`/`home1` list that
side's initial station sectors. For the supplied map, authored home 0 is Ganymede
1050, home 1 is Organia 1051. `seed` remains the P825 civilization seed; a home
permutation consumes no RNG. File map identity, civ tuple, mode and effective
`homes` must all be retained for replay. A civ seed alone does not replay an
alternating rotation's home phase. Log metadata is captured at assembly, so a
station loss during the match cannot rewrite its home receipt.

The existing [matchup tally](../../tools/p819-side-bias-proof/matchups.py) now
accepts the trailer without dropping the last civilization and returns
`byAuthoredHome` alongside `byPosition` and `byCiv`. Old logs without assignments
have an empty home tally; the tool never guesses their home assignments.

## Evidence scope and method

Inputs remain read-only in the dispatch evidence directory. Small public
artifacts are [105-summary.json](P924_assets/105-summary.json) and
[195-summary.json](P924_assets/195-summary.json), including input SHA-256s.
No raw logs or media were added. Reproduce the detailed analysis locally:

```bash
python3 tools/p924-side-bias-audit.py "$EVIDENCE/rig-105v105.log.gz" \
  docs/reports/P924_assets/105-summary.json
```

`EVIDENCE` denotes the supplied P924 evidence directory. The parser makes two
streaming passes, learning ship sides from explicit chat/pod/final-score receipts
before attributing scoring events. Reused IDs with conflicting sides fail closed.
It counts explicit `killer … kill +1` and `victim … death +1`, **not** ejections
or partial-kill shares. Five-minute UTC bins, pre/finale phase counts, transaction
deltas, station alerts/destructions and first observed orders/undocks are retained.
A rebuild is not a new competitive result, and EOF never becomes a victory.

Native ejection scoring differs from true death: see verified
`FedSrv/FedSrv.CPP:11770-11798,11811-11819,11918-11921,12025-12032` and the
production branches in `Server.Combat.cs:1751-1802,1834-1887,1905-1935`.
Those rules are unchanged. The filtered file lacks spawn, full ship-warp, damage, purchase
and worker logs; structured pod-trip records do retain pod routes/outcomes. There is no basis for reconstructing full fight occupancy,
continuous commander balances, exact launch/arrival times or strike survival.

Match 1 starts mid-observation at 18:10:12 UTC, without its matchup/opening.
Its result supplies its actual civ tuple and seed. Match 6 opens at 22:34:09
and remains unfinished at the capture's end, after its finale arms. It is
excluded from the win count. Matches 2–5 have complete assembly-to-result windows.
Every completed round reports 214 peers: 105 combat pilots per side plus the
observers/commanders, rather than 107 combat pilots per side.

The earlier headlines contain 21 completed results, matches **27–47**, side 0
**10**, side 1 **11**. Their last nine have **3/6** wins, not the brief's 2/7;
the brief apparently describes another cutoff. This capture starts in match 27
and ends during a later opening, so use its explicit results rather than a guessed
21-match window. It does not reproduce a sustained overall 195v195 side-1 majority.

## Scores over time

Every pair below is **side 0 / side 1**. Kills are observed scoring receipts;
deaths are true-death receipts. “Pre” ends at the server's finale arm; these
windows have unequal durations and are counts, not rates.

| Match; civ 0 / civ 1 | Pre kills | Finale kills | Total kills | Pre deaths | Finale deaths | Station kills | Winner / criterion |
|---|---:|---:|---:|---:|---:|---:|---|
| 1; Bios / Omicron | 352 / 535 | 123 / 99 | 475 / 634 | 515 / 226 | 108 / 95 | 1 / 1 | 1, kills |
| 2; Omicron / Iron Coalition | 372 / 341 | 145 / 135 | 517 / 476 | 363 / 331 | 145 / 124 | 0 / 1 | 1, stations |
| 3; Gigacorp / Phoenix | 382 / 433 | 132 / 108 | 514 / 541 | 396 / 327 | 101 / 102 | 0 / 3 | 1, stations |
| 4; Omicron / Dreghklar | 373 / 366 | 96 / 122 | 469 / 488 | 374 / 344 | 129 / 65 | 0 / 1 | 1, stations |
| 5; Phoenix / Gigacorp | 374 / 362 | 66 / 95 | 440 / 457 | 335 / 319 | 66 / 55 | 0 / 2 | 1, stations |

The first deadline's authoritative mission totals are kills **475/634**, deaths
**625/322**. Observed death receipts give **623/321**; final connected-player
scores give **621/321**. Do not silently substitute either player census for
mission deaths: NPC/departed scores and the censored opening are not fully exposed.
For matches 2–5, final player K/D totals reconcile exactly with scoring receipts;
the station-priority deadline lines do not expose independent mission K/D totals.
There are no unattributed explicit scoring receipts in these six observed windows.
Five wins for a preselected side have probability 1/32 under independent fair
outcomes; five for either side have probability 1/16. A retrospectively selected
streak across many runs is not a predeclared significance test.

Cumulative K/D at UTC minutes after each matchup, for the complete openings:

| Match | 10 min | 20 min | 30 min | 40 min | Result (~50 min) |
|---|---|---|---|---|---|
| 2 | 146/40 : 53/114 | 226/132 : 132/188 | 313/288 : 284/275 | 427/419 : 381/374 | 517/508 : 476/455 |
| 3 | 69/152 : 188/48 | 226/251 : 285/173 | 319/345 : 387/263 | 416/437 : 474/357 | 514/497 : 541/429 |
| 4 | 144/52 : 81/104 | 280/212 : 219/234 | 334/334 : 314/298 | 402/419 : 406/375 | 469/503 : 488/409 |
| 5 | 151/45 : 68/99 | 266/194 : 218/206 | 337/288 : 316/274 | 407/368 : 398/345 | 440/401 : 457/374 |

Here `side0 K/D : side1 K/D` avoids treating death as an opposing kill. Side 0
has strong opening kill leads in 2, 4 and 5; side 1 overtakes late in 4/5. There
is no universal “side 1 wins the opening” or “the finale boosts side 1” pattern.
Match 3's finale actually reduces side 1's kill lead from 51 to 27.

## Station pressure, money, starts and fight locations

All UTCs below are November 3. Alerts are rate-limited notifications, not hit or
damage totals. Station kill counters and actual destruction receipts agree.

| Match | First alert against side 0 / 1 | Alert counts against 0 / 1 | Station destructions, credited attacker @ UTC |
|---|---|---:|---|
| 1 | 18:41:58 / 18:50:12 | 5 / 4 | 1 @18:46:56; 0 @18:53:12 |
| 2 | 19:31:57 / none | 9 / 0 | 1 @19:32:01 |
| 3 | 20:32:05 / none | 11 / 0 | 1 @20:37:01, 20:37:16, 20:44:45 |
| 4 | 21:14:58 / none | 7 / 0 | 1 @21:18:35 |
| 5 | 22:03:10 / none | 9 / 0 | 1 @22:12:43, 22:17:23 |

The first alerted targets are an Outpost (match 1), Shipyard (2), SpecMine (Si)
(3), Shipyard (4), and Refinery (5), all in Ganymede. The later targets include
garrisons as well as expansions. Every completed-match destroyed station ID is
in the non-authored 9188–9201 range; the authored garrisons 4328/4329 are not
logged destroyed in those five results. Match 6 does destroy 4328 while censored.
Thus a station-kill victory here is not synonymous with destroying the original
enemy home garrison. The one-sided margin includes vulnerable research/expansion
stations, a useful target for the paired experiment's damage/arrival telemetry.

Every winning station margin is established before finale arm, except match 3's
third destruction five minutes into the finale. Thus `armedBy` cannot explain
the onset of the station advantage. `armedBy` is **1,0,0,0,1** for these matches,
while every winner is side 1. Match 6 also shows only side-0 station alerts (45)
and a side-1 station kill before arm, but has no result.

| Match | First undock, seconds after matchup 0 / 1 | First queued station Attack, seconds after matchup 0 / 1 | Logged money delta 0 / 1 |
|---|---:|---:|---:|
| 1, partial opening | observed 18:22:06 / 18:23:15 UTC | observed 18:41:26 / 18:49:21 UTC | −182,750 / −468,300 |
| 2 | 44.778 / 44.779 | none logged / 1,026.723 | −286,810 / −783,410 |
| 3 | 44.804 / 44.805 | 1,027.967 / 1,512.638 | −728,260 / −488,010 |
| 4 | 44.876 / 44.876 | none logged / 1,279.499 | −293,010 / −1,046,887 |
| 5 | 44.879 / 44.928 | 1,454.210 / 1,055.477 | −470,510 / −761,010 |

First undocks differ by microseconds to 49 ms in complete openings. There is no
measured large side-1 launch lead. Queued station Attack orders are **commandID 1**; commandID 6 is Repair
(native `Igc/igc.h:902,907`). Side 0's first queued Attack is earlier in matches
1/3, side 1's in 5; only side 1 has such a receipt in 2/4. These are not proof
of acceptance, strike commitment or arrival. In match 1, side 1 actually destroys
a station at 18:46:56 **before** its first queued station Attack at 18:49:21:
autonomous attacks are not a complete commander-queue stream. First station
alerts above are the first observed pressure, with unknown notification delay;
actual first strike launch/entry cannot be reconstructed without the missing
worker/warp/damage logs. A fixture explicitly checks that an earlier Repair is
not mislabeled as an Attack.
Money deltas predominantly record negative `MONEY_CHANGE` transactions; side 1
has larger net deductions except match 3. In match 4 it has −1,049,887 negative
and +3,000 positive deltas. This is neither a treasury balance nor proof of higher
income, mining or spending efficiency. The missing wallet/research telemetry is
a material gap, especially for interpreting station construction/replacement.

Ejection locations give a direct, limited view of where combat hulls were lost:

| Match | Alderan 1052 losses 0 / 1 | Ganymede 1050 losses 0 / 1 | Organia 1051 losses 0 / 1 | Other sectors 0 / 1 |
|---|---:|---:|---:|---:|
| 1 | 488 / 372 | 68 / 53 | 68 / 66 | 69 / 41 |
| 2 | 390 / 410 | 104 / 48 | 42 / 105 | 47 / 40 |
| 3 | 446 / 414 | 119 / 38 | 62 / 81 | 63 / 48 |
| 4 | 396 / 351 | 99 / 69 | 42 / 110 | 37 / 33 |
| 5 | 358 / 343 | 117 / 54 | 46 / 95 | 42 / 59 |

Alderan dominates observed hull losses, but both homes and the corridors also
see combat. Side 0 repeatedly loses more hulls in its own Ganymede home than
side 1 does there; home defense, return traffic and siege coexist. These are
victim-side locations, **not** kills credited to the other side; oxygen deaths
and other true deaths are deliberately not assigned to a guessed sector. There
are no warp receipts to measure reinforcement or bomber commute times here.
The reciprocal siege distance and unequal clash commutes established by P826
remain geometry evidence, not newly measured travel in this capture.

Pod-trip endings add an important qualification to the death margins. Counts
below are **ending receipts in the observed match window**, not survival rates;
unfinished trips and match-cleanup removals are not deaths or failed recoveries.
They do not add another death to the K/D census.

| Match | Oxygen expirations 0 / 1 | …ending in Ganymede / Organia respectively | Other death-labelled pod endings 0 / 1 | Docked + rescued + station-rescued endings 0 / 1 |
|---|---:|---:|---:|---:|
| 1 | 171 / 54 | 141 / 38 | 439 / 263 | 55 / 204 |
| 2 | 189 / 65 | 188 / 61 | 318 / 375 | 43 / 141 |
| 3 | 48 / 114 | 47 / 111 | 445 / 314 | 171 / 140 |
| 4 | 199 / 93 | 197 / 93 | 302 / 310 | 40 / 138 |
| 5 | 144 / 73 | 143 / 68 | 251 / 298 | 146 / 154 |

Side 1 has fewer observed true deaths in all five completed matches. In matches
2/4/5, side 0 has **fewer other death-labelled pod endings** but far more oxygen
expirations, mostly already in Ganymede. Thus its death disadvantage cannot be
read simply as “more hulls lost to enemy gunfire.” Different endpoint populations
and direct non-pod deaths prevent exact reconciliation from these endings alone.
Omicron's oxygen count is 54 from Organia in match 1 versus 189/199 from Ganymede
in matches 2/4. Opponents and match trajectories also change, so this is a
home/return-risk hypothesis, not a controlled civ or docking defect. The
Gigacorp/Phoenix match 3 has the **opposite oxygen margin** (48/114), while side
1 still wins three station kills; oxygen is not a universal explanation for the
five wins. This makes both station pressure and pod-return outcomes useful
secondary measurements for the paired trial. No pathing or return rule changed.

## Side-index and ordering audit

Paths below are relative to the repository except explicitly marked native.
Line numbers were checked against the current sources. “Can matter” describes
an opportunity for an effect at 100v100+, not evidence it caused these results.

| Path:line | Order / index treatment | Can matter at 100v100+? | Native comparison and disposition |
|---|---|---|---|
| `src/Allegiance.Server/MatchCivilizations.cs:47` | Random civ draw visits low side first, swap-removes pool entries. | Changes which draw lands on a side; uniform draw alone gives no favored civ distribution. Fixed tuples eliminate it in trials. | Same ascending draw/swap-remove intent, native `FedSrv/fsmission.cpp:175-198`. No fix. |
| `tools/allegiance-mcp/McpTools.cs:710,815` and `SpawnBatch.cs:49` | Fleets initially append A then B, but **actual spawn slots interleave by ordinal**, non-probe side first; attempts are serial per worker. | Small startup/ID ordering skew; no all-105-A-then-105-B launch. Real complete first undocks are nearly simultaneous. Role-based probe preference can correlate with side. | Port harness policy, no native bot-batch analogue. Keep fixed and record probe side; no strategy rewrite. |
| `tools/allegiance-mcp/WorkerPartition.cs:6,13` and `WorkerSupervisor.cs:288` | Modulo offsets restart independently for each side. Worker 1 gets each side's odd remainder. | 105 splits 53/52 **on both sides**; 195 splits 98/97. Shared worker cost may matter, not unequal side membership by construction. | Port worker architecture. No evidence of unequal side allocation; no fix. |
| `tools/allegiance-mcp/BotManager.Research.cs:43,68,105` | Worker zero owns both commanders; non-probe side ticks first, then probe side. | Yes: sequential funding/orders and shared rock reservations can resolve contention first for a role correlated with side 1. | Port orchestration, not native commander scheduling. Not direct side-index preference; retain for experiment and expose as a remaining hypothesis. |
| `tools/allegiance-mcp/BotManager.Research.cs:206`; `ResearchCommander.cs:202-215` | Rocks sort own-home first, then asteroid ID; first available unreserved compatible rock wins. Builders use observed unit order; reservations shared within a manager. | Yes if both sides seek the same neutral rock or their home supplies are exhausted. Initial home-first supply reduces overlap; logs do not expose reservations. | Port research-plan policy; native human decisions have no matching shared reservation loop. Altering it would be strategy redesign without a demonstrated defect. |
| `src/Allegiance.Server/Server.Lifecycle.cs:691,708` | Initial miners visit sides in order and choose first buyable miner type. | IDs/startup order can differ; each side still receives its own count and home. | Native starts miners per side at the home station, `FedSrv/fsmission.cpp:2196-2217`. Same broad rule; keep. |
| `src/Allegiance.Server/Server.Showcase.cs:60,78` | Research home/resource preparation sorts side IDs and allocates new rock IDs in that order. | Yes: tie-break IDs reflect side processing; geometry/resources derive from the home's plan. Swapping before this preparation is necessary. | Port preset extension. No global resource budget is consumed by the first side; no evidenced denial to side 1. No fix. |
| `tools/allegiance-mcp/BotManager.Finale.cs:265,282,323` | Target restart base first, then lowest station ID; plans iterate sorted side IDs; bomber reservation uses ship ID within each side. | Yes: equivalent-ID ties can choose different geometry/ship cohorts. No first side takes the other side's bomber budget. | Port finale policy, not native win/ranking rule. Keep unchanged; trials retain target IDs/commit receipts. |
| `tools/allegiance-mcp/ShowcaseFinale.cs:39-50` | `both` selects both sorted sides. Optional `leader` ties choose lower side ID. | Explicit `leader` has a low-index tie preference; not the simultaneous baseline. | Existing opt-in preset policy, finale redesign out of scope. Do not silently switch the rig to leader. |
| `tools/allegiance-mcp/BotManager.Finale.cs:792`; `src/Allegiance.Server/Server.Showcase.cs:15-44` | First selected research peer sends arm; first valid request fixes one shared deadline. `armedBy` is metadata. | Deadline timing can matter; the label grants no score, attacker privilege or winner selection. All selected sides still arm. | Port bounded-finale policy. Native timeout ranking is a separate rule. Keep. |
| `tools/allegiance-mcp/BotManager.cs:1696-1716,1869` | Each decision pass snapshots bot-list order, resets engagement counts, computes read-only inputs in parallel, then drives bots serially in snapshot order. | Yes: earlier bots choose before later bots update engagement counts. Balanced spawn order interleaves sides; no all-side-0 decision sweep, and no shared target between opposing sides in a two-team enemy-only selection. Within-side role/ID cohorts can still matter. | Port bot policy, not a native per-side combat loop. No demonstrated index defect; preserve strategy. |
| `src/Allegiance.Core/Model/Mission.cs:1020,1052` | Side economic updates then sector updates in map order. No side-wide combat pass. | Sequential research completion or sector migration boundaries can matter under load. | Same broad side-then-cluster traversal, native `Igc/missionigc.cpp:3138-3146,3168-3175`. Preserve. |
| `src/Allegiance.Server/ServerHost.cs:2298-2386`; `Server.Broadcast.cs:123` | Drain fairly selects from rotating peer lanes, then **sorts the selected batch by peer ID** before applying it. | Yes: first shared-resource/order effects within a tick can correlate with spawn/probe order. Rotation avoids budget starvation, but does not rotate application order. This is peer-index order, not a direct side comparison. | Native pumps its message queue (`FedSrv/FedSrv.CPP:7939-7941`); equivalence to the port's explicit peer-ID sort is not established. No copied intake trace proves an advantage; retain as a concrete hypothesis, no transport reorder here. |
| `src/Allegiance.Server/Server.BroadcastBuffers.cs:66-74`; `Server.Broadcast.cs:1249,1332-1343` | Baseline parallel build uses a peer-dictionary snapshot; results apply serially in snapshot order. | Yes: earlier queue entry can affect receipt latency under congestion, although encoding is immutable and no side gets a different score/budget. Dictionary reuse/removal can change that order; do not assume a stable fairness guarantee. | Native broadcast visits sides in list order (`FedSrv/FedSrv.CPP:3662-3675`) and sends before mission update (`:4237-4254`). Port relative broadcast order is already a separate default-off experiment. This is not full order parity; leave the rig baseline unchanged. |
| `src/Allegiance.Core/Model/Sector.cs:496,1016,1028` | Models append, then update in that order; optional global ship phases remain separate/default off. | Yes: same-tick damage, targeting and membership changes can depend on insertion order. **Not fully native-equivalent.** | Native ships append (`Igc/clusterIGC.h:144-149`, `Igc/igc.h:5130-5136`), but native **models prepend** (`Igc/clusterIGC.cpp:500-506`), followed by model walk at `:241-253` and ship phases at `:172-229`. This is a real order difference, not proof of a systematic side advantage. Broad tick-order work is already P906; no isolated index-bias fix justified here. |
| `src/Allegiance.Core/Model/Sector.cs:4915-5004` | Mining resolves model-list order; shared depleted ore can go to the earlier processed miner. | Yes for simultaneous competing miners at an almost-depleted rock; unmeasured here. | Native also serially updates models, but model-list direction differs as above. Preserve pending a controlled replay; no invented economy correction. |
| `src/Allegiance.Core/Model/Mission.Countdown.cs:32-42,67-77` | Unique maxima clear the winner on a tie; kills tie-break with fewer deaths, then draw. | No unconditional side-0 or side-1 win on a two-side tie. | Native `FedSrv/fsmission.cpp:2348-2370,2468-2504` follows the same tie intent. P914 station-kill precedence is explicit preset policy, unchanged. |
| `src/Allegiance.Server/Server.Lobby.cs:4224`; `tools/allegiance-mcp/SideIntel.cs:543` | GetBase/reposition follows first owned restart base; bot home memory starts from first observed owned station. | Multiple-base export/dictionary order can affect which base anchors a route after rebuilding. Ownership must be reassigned before initial exports. | Native GetBase returns first restart station (`FedSrv/fsmission.cpp:1433-1447`). Client dictionaries are not a native linked-list ordering guarantee. Intel is keyed by fresh mission cookie (`SideIntel.cs:596-606`), so rotations obtain a fresh home anchor; retain the ordering ambiguity, do not randomize exports. |
| `tools/allegiance-mcp/BotManager.Research.cs:20,30` | Human commander is explicitly side 0 in the single-player role. | Irrelevant to the automated rig baseline without that opt-in role. | Intentional human-side binding, outside this experiment and human rules unchanged. |

The important distinction is between verified order **differences** and an
established incorrect side-index **advantage**. Neither the native prepend/port
append difference nor commander scheduling has a controlled counterfactual in
these logs. Reversing either now could change combat/strategy and contaminate
the home experiment. No native parity row is changed or newly claimed.

## Exact paired rig trial for Claude

This is a runbook, not a request to change the rig from this lane. Preserve the
rig's winning settings: immutable snapshots + encode-once, parallel broadcast,
P863 clash budget, pooled transport, native ship-update model and native GotoPlan,
server GC. Keep interest tiers, native broadcast order and receive decoupling off.
Use the existing showcase march, worker count, fleet composition, research speed,
income multiplier, simultaneous finale settings and 900-second bound. Record the
actual complete configuration and binary/map/core hashes; do not substitute the
accelerated Brawl/kill-goal local proof settings below.

1. **Instrument smoke:** with `m2xspiderw.igc`, fixed `civIds:[18,4]`, and
   `homeSwap:"alternate"`, run two 15v15 matches, observing ordinary rotation.
   Expect matchup `homeMatch=1 homes=0,1 home0=1050 home1=1051`, then
   `homeMatch=2 homes=1,0 home0=1051 home1=1050`. Check actual stations, player
   starts, initial miners and research resources, both worker populations and
   a fresh finale arm/epoch on each rotation. Do not count smoke results as
   balance evidence. A failed readiness/re-arm/worker gate is censored and fixed
   operationally before the experiment, never counted as a loss.
2. **Primary:** 52 completed competitive matches at **105v105**: 13 blocks,
   each containing the following four cells exactly once. **Control:** the same
   52-match design at **195v195**. Total: **104 competitive matches**; at the
   copied ~50-minute duration this is ~87 hours plus startup/intermissions.
   Interleave population blocks as 105,195,195,105,105,195,… in paired blocks,
   rather than putting all large games on a different time/day regime.

| Cell | Side 0 civ / side 1 civ | Effective homes 0 / 1 | JSON mode for an isolated match |
|---|---|---|---|
| A | 18 Gigacorp / 4 Phoenix | Ganymede / Organia | `off` |
| B | 18 Gigacorp / 4 Phoenix | Organia / Ganymede | `on` |
| C | 4 Phoenix / 18 Gigacorp | Ganymede / Organia | `off` |
| D | 4 Phoenix / 18 Gigacorp | Organia / Ganymede | `on` |

For odd block numbers, run **A,B,D,C**. For even blocks, run **C,D,B,A**.
Every block contains one authored-first pair and one swapped-first pair, and
the order of the two civ tuples alternates between blocks.
Cold-start the server and workers for **every competitive cell**, with explicit
`off`/`on` from the table and the same baseline configuration. This counterbalances
both time order and which home assignment starts a pair without mixing warm
rotations with cold boots. The smoke above verifies `alternate`; for an
operational paired run with fixed civs it yields normal/swapped pairs on odd/even
rotations, but always starts normal. Competitive counterbalanced cells therefore
use the explicit modes. Record the extra opening after each counted result and
exclude it unless it is the assigned next competitive cell.

Pin `civSeed=924000+b` for every cell of block `b` (1–13), and keep fixed tuples
rather than random civs. This seed is a replay label for fixed civs, not a claim
of fully deterministic network/combat replay. Pin any existing benchmark/map
seed identically within a block; the authored map is fixed and must not regenerate.
Label each capture by population, block and cell (for example
`105-b01-A-server.log`), because cold starts reuse match number 1. Analyze
full scoring/identity logs per cell; the matchup tally may aggregate their
matchup/result lines without assuming globally unique match numbers.
Keep all other settings identical between A/B/C/D, including research plans
selected by civ. Retain the rig's default generated plans for 18/4; do not invent
new authored ones. Repeat the whole design with other civ tuples only as a
separately registered follow-up.

To make local configuration copies without reconstructing or overwriting the
rig baseline, Claude can apply this overlay to a saved rig JSON (paths below
are placeholders on the operator's machine):

```bash
python3 - BASELINE.json TRIAL.json 18,4 alternate 924001 <<'PY'
import json, sys
source, target, civs, mode, seed = sys.argv[1:]
config = json.load(open(source))
game = config['games'][0]
game.update(map='m2xspiderw.igc', teams=2,
            civIds=[int(c) for c in civs.split(',')],
            civSeed=int(seed), homeSwap=mode)
game.pop('mapGen', None)
with open(target, 'w') as stream:
    json.dump(config, stream, indent=2)
    stream.write('\n')
PY
```

Launch the existing rig server with `--config TRIAL.json` using its normal
operator procedure. Keep its existing transport/profile environment too. Send
the existing `spawn_scenario` request with the baseline's parameters and
`count_a=count_b=105` (195 for control), `preset:"showcase"`, and its existing
march/staging/finale settings. Keep the existing two workers if that is the
baseline: expected per-side splits are 53/52 and 98/97, respectively. Do not
replace that request with this lane's kill-proof preset or silently choose new
strategy defaults. Stop after the assigned result count using the existing
operator procedure; an extra opening is not a completed trial.

3. **Retain receipts:** UTC timestamped full server and **both worker** logs,
   matchup and game-over home trailers, civ plans, phase K/D, station IDs and
   damage/losses, first accepted strikes and arrival timings, treasury/research
   telemetry, worker counts, crashes/reconnects and finale re-arms. The copied
   filtered logs are insufficient for arrival/wallet causality. A match without
   the intended fleet, valid home receipt, normal start and bounded completion
   is censored. Preserve its reason and rerun the entire affected four-cell
   block; never discard a legitimate competitive loss or ordinary ship loss.
4. **Read results:** strip only the timestamp prefix when using the existing
   matchup tally:

```bash
python3 - SERVER_TIMESTAMPED.log SERVER.log <<'PY'
import sys
with open(sys.argv[1]) as source, open(sys.argv[2], 'w') as target:
    for line in source:
        first, sep, rest = line.partition(' ')
        target.write(rest if first.endswith('Z') and 'T' in first and sep else line)
PY
python3 tools/p819-side-bias-proof/matchups.py --server SERVER.log --output tally.json
```

`byPosition` measures side wins; `byAuthoredHome` measures Ganymede (0) versus
Organia (1); `byCiv` measures civilization. Evaluate them **within each population**
and each fixed/reversed tuple, retaining the four cells and pair-order strata.
If side 1 keeps winning in B/D, it now does so from Ganymede: that supports an
index/role/scheduling hypothesis. If Organia wins from side 0 in B/D as well as
side 1 in A/C, that supports a home-associated mechanism: geometry, content
or sector traversal order, which stays with the authored home. It does not
isolate geometry alone. If the same civ wins across
both homes and both side indices, it supports a civ/plan trajectory. Mixed or
interacting patterns are allowed; do not force one global explanation. Compare
station pressure and pre/finale K/D, not just final winner labels.

**Sample size:** exact two-sided binomial enumeration at alpha 0.05 gives
**52 independent decisive matches**, rejection at ≥34 wins or ≤18 wins for a
predeclared side/home, with **81.12% power** for a true 70/30 split. Fifty matches
only give 78.22% under the exact rejection rule. At 104 matches, four cells have
26 repeats each and power is 98.46% (reject ≥63 or ≤41).
The committed [power calculator](../../tools/p924-trial-power.py) reproduces
these values without external libraries.

Predeclare **105v105 Organia win probability** as the primary contrast; the
195v195 and side/civ contrasts are controls/exploratory unless multiplicity is
explicitly accounted for. Draws remain reported; either use an outcome model
including draws or collect 52 decisive outcomes under a rule registered before
starting. Paired/rotated matches can be correlated: 52 is an independence
benchmark, not guaranteed power for 13 correlated blocks. Report block results
and a paired/block uncertainty estimate; a single streak or a nominal pooled
binomial p-value cannot certify the causal mechanism. A stronger follow-up can
predeclare 26 blocks per size (104 matches each); do not repeatedly peek and
stop as soon as significance appears.

## Verification

Validation receipts are in `.scratch/p924/` and the compact
[validation artifact](P924_assets/validation.json). `local.env` was present and
loaded for tests, supplying artwork and native-source roots. No test skip was
accepted as equivalent to proof. The first build needed a writable NuGet HTTP
cache inside scratch; its sandbox-cache failure was infrastructure, not a code
warning. 
The new tests pin absent/default-off parsing, invalid modes/team counts, the
synthetic-debug incompatibility (with off still allowed), map
identity/byte preservation when off, all-station ownership including extra bases
and neutral stations, immutable geometry/source bytes, double-swap inversion,
four actual rotations in each mode, both civ starting types/plans, miners,
starting positions, research rocks, home visibility and flags. Generated-map
coverage catches reversal of civ station types. The network test runs a real
swapped 15v15 Brawl combat match to a native three-kill result without forcing
scores/game-over. It is a bounded orchestration proof, not an authored-map siege
or performance/balance measurement. The existing authored-map never-parks test
uses the same assertions with explicit off/on configurations. These are stock
in-process test-host checks, not server-GC performance measurements; the server
and MCP application projects retain their server-GC defaults for deployment.
The final synthetic-debug guard was added after the sole Fast sweep and then
qualified by the repeated full build/tool/AOT gate and three isolated P924
core/loopback cohorts. The normal home-assignment path in the showcase checks
uses debug mode off.

A later restore exposed **NU1903** for the SDK's implicit
`Microsoft.DotNet.ILCompiler` 10.0.11. The [Microsoft runtime advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9)
identifies 10.0.14 as patched. Qualification therefore uses official compiler
10.0.14 and, for self-contained AOT, runtime 10.0.14 in a **workspace-only NuGet
cache**. Auditing stays enabled. No SDK files, project dependencies or repository
toolchain policy were changed. The installed SDK remains 10.0.400: ordinary
unqualified restores can still select 10.0.11 and fail until the operator updates
the toolchain. This environmental qualification is part of the gate result,
not a claim that the unqualified SDK command now passes.

The scratch `patched-toolchain.targets` contains:

```xml
<Project>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <KnownILCompilerPack Update="Microsoft.DotNet.ILCompiler">
      <ILCompilerPackVersion>10.0.14</ILCompilerPackVersion>
    </KnownILCompilerPack>
  </ItemGroup>
</Project>
```

Use `CustomAfterMicrosoftCommonTargets` pointing to that scratch file and a
writable `NUGET_PACKAGES` cache for build/tool qualification; existing package
versions were linked read-only into that cache. The AOT command additionally uses
`-p:RuntimeFrameworkVersion=10.0.14`. Exact local commands are retained in
`.scratch/p924/final-builds.sh`; no `NuGetAudit=false` or advisory suppression is
used. A normally updated SDK/compiler/runtime is the preferred operator fix.

| Check | Final result |
|---|---|
| Solution build | **PASS**, 0 warnings / 0 errors with the scratch patched compiler qualification above. |
| `tools/build-tools.sh` | **PASS**, all 113 projects. |
| Release linux-x64 self-contained AOT publish | **PASS** with compiler/runtime 10.0.14, no IL2026/IL3050 diagnostics. |
| P924 home-assignment cases | **18/18 passed**, six cases × three final-source isolated runs, zero skips. |
| Swapped 15v15 combat completion | **3/3 passed**, 51.413 / 57.718 / 65.962 s; native kill result observed over loopback. |
| P825 affected civ/rotation suite | **27/27 passed**, nine cases × three runs, zero skips. |
| Python evidence/tally regressions | **9/9 evidence-parser and 9/9 matchup-tally cases passed**, three cases × three isolated repetitions each; includes Attack-versus-Repair classification and no double-counting pod endings. |
| Exactly one Fast sweep | **FAIL**: 12,983 passed / 12 failed / **3 optional-pack skips**, 12,998 executions in 555.1 s; all nine infrastructure checks pass. No P924 failure. Two executions exceed the 10 s guard: missile tips 38.317 s and warp exit wobble 10.772 s. |
| Showcase, switch off | **4 pass / 1 fail**, five serial runs. Durations 484.249 / 214.617 / 205.647 / 229.672 / 212.670 s. Every parking check held; the first composite test failed with 27/30 reached, 144 shots, **zero hits**, and 0.000 s parked-with-target. |
| Clean unmodified base showcase control | **PASS**, 213.851 s; 24/30 reached, 520 shots / 142 hits, 0.000 s parked-with-target. |
| Showcase, switch on | **3/3 passed**, 184.250 / 305.193 / 455.669 s; all reach 24/30, observe real damage and stay within the parking bound (maximum 2.006 s). |

The failed switch-off composite signature is already documented under P895:
29/30 arrivals, shots but zero hits at 480 s, followed by mixed qualification
outcomes. Here the next **three isolated off runs passed 3/3**, and the fifth
also passed. This is a stochastic combat-coverage limitation, not a parking
violation or a claim that the composite gate passed five times. The clean base
control was built from `git archive HEAD`; its temporary source copy was removed
after use while the build log/TRX remain. It is additional context, not a deterministic match replay or statistical
proof of unchanged win balance.

Fast failures and their three-run isolations are ledgered in
[TEST_FLAKES.md](../TEST_FLAKES.md). The ancestry fixtures conflict with mandatory
worktree-local scratch plus `local.env`; the profile test attempts a blocked
write outside the sandbox. Other observed failures are the known NPC defender,
ship-status/lifepod/missile/ballot receipts plus a TCP port-bind collision.
No assertion, timeout, tier, transport or bot policy was changed to make these
checks pass, and the Fast sweep was not rerun.

No native gameplay rule was ported or altered, so no parity rows changed;
scorecard before/after outputs are identical. Glossary links and new-source
public hygiene pass; no new media needs review.

## Remaining gaps and native-intent footer

The logs confound side and home, do not contain strike arrival/worker or wallet
exposure, and cannot settle the effect of probe-role scheduling, neutral-rock
reservation races, peer-ID-sorted intake/egress, or native-versus-port model order. The local smoke/gates prove
correct reassignment and include normal match completion; **only Claude's rig paired trial**
can answer the population-dependent bias question. Random civ rotation remains
independent of `alternate`: random tuples will not automatically form matched
pairs. Use fixed tuples as above. Game restart resets the alternate phase; match
receipts are the authority. No full deterministic combat replay is promised.

**Design:** authored home geometry and first restart-base selection are native
content/rules (`FedSrv/fsmission.cpp:1433-1447`); this permutation is a modern
experiment policy around them. Native home reveal uses station ownership
(`Igc/mapmakerigc.cpp:969-997`), so changing ownership before assembly preserves
the downstream intent. **Era constraint:** native linked-list traversal and
single-threaded ordering are observable but do not imply fairness or a universal
side preference. **Bug:** no new index-specific advantage has been demonstrated;
the verified model-list direction gap remains a parity investigation, not a
P924 balance fix. **Modern approach:** reproducible assignment receipts,
counterbalanced fixed-civ pairs, complete worker/economy telemetry and explicit
uncertainty provide a stronger test than randomizing strategies or editing the
native map in response to five selected wins.
