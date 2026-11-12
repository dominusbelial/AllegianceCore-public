# P1001 — Civilization strategy commander and fleet

`bots.civStrategy` remains **off by default**. ON executes the P1000 portfolio
through ordinary donations, constructor orders and legal purchases. OFF retains
the serial commander, existing fleet composition and existing loadout policy.
No authored plan/core asset, wire schema, HUD or training changes are included.
Changes remain uncommitted and unpushed. No rig or other host was contacted.

This report was created before implementation and gates. The final qualification
counts and measured results below are updated from task-local evidence.

## Commander

`CivExecutionPlan.Build` assembles primary and compatible counter routes followed
by an exact-replayed reachable research/station/drone tail. The runtime plan name
carries the policy identity, opening boundary and coverage share through the
existing briefing. No new protocol fields or authored plan data are introduced. In unlocked
showcases the marker enables portfolio funding and purchases while the existing
scenario routes retain tactical ownership. The research tactical coordinator
runs for the new marker in research mode; legacy plan behavior is unchanged.
The server installs this plan only with the flag ON; lobby civ changes rebuild
all affected choices and re-send the existing side briefing. Rotation creates a
fresh policy and economy/coverage epoch. Briefings are sent only to peers with a
valid side, including when lobby civ changes rebuild every side's portfolio.
Duty refresh keys on epoch plus plan,
so an identical portfolio in a repeated seed still clears the old coverage sortie.

`ResearchCommander.CivStrategy.cs` separates up to **four research jobs**, **two
construction jobs**, one prerequisite repair opportunity and one fleet delivery
job. A maturing bucket or travelling constructor does not block independent useful
work. Current native buyability is checked from observed side/global techs and
individual owned stations. Completion comes from actual development, station or
unit observations, never an elapsed-time estimate.

Two complete legal defensive fits plus two replacement miners remain reserved.
A lost income source can use its miner allowance while keeping the defensive
reserve. Only side-observed He3 supports the replacement decision. An aggregate
in-flight wallet ledger prevents parallel jobs spending the same stale balance;
self-debit/refund MONEY_CHANGE observations settle requests even when simultaneous
income masks the absolute balance decrease. Refused funding earns no coverage
credit. Partial refunds are conservatively attributed to coverage before opening
investment. Prosperity retains its existing victory savings and miner policy.

Construction targets come only from the side's recorded rock observations or
its own ordinary peer's exported statics. Reservations are private to that side.
A 180-simulation-second travel lease and cooldown let an unavailable/consumed site
be replaced without freezing other work. Observed construction shells retain their
reservation. Dead builders release their site, newly available successors do not
orphan an issued constructor, and delivered prerequisites can be repaired after a
loss. A blocked opening can spend on other ready useful work while exploration
continues. Deferred jobs are visible in a bounded 24-job ON telemetry list; OFF
omits that optional field.

These priorities, limits, reserves and leases are modern bot policy. They do not
change native prices, completion times, construction mechanics or donation gates.

## Native gates retained

The locally rechecked native bodies are tracked in
[the gate ledger](../parity/p1001-native-gates.csv); these rows claim no additional
native parity completion credit.

| Gate | Native source | Application |
|---|---|---|
| Frozen development price/time | `Igc/bucketigc.cpp:70-74` | Catalog uses civ base GA, independently of later research GA. |
| Independent money-proportional bucket time | `Igc/bucketigc.h:44-67` | Parallel jobs fund existing buckets; native update/completion remains authoritative. |
| Started mission, visible nonnegative buyable group, real player wallet, amount, CanBuy, capped spend and refund | `FedSrv/FedSrv.CPP:7137-7208`, `Igc/igc.h:639` | Ordinary BUCKET_DONATE; no treasury, grant or direct simulation shortcut. |
| Global development masks; per-station local gates; obsolete station exclusion | `Igc/sideigc.h:237-280` | Rechecked from each observed snapshot. |
| Docked purchase, local tech, successors and mount masks | `FedSrv/FedSrv.CPP:6151-6161`, `Igc/stationIGC.h:383-433`, `Igc/hullTypeIGC.h:163-175,199-213` | Complete fits pass the existing selector legality and actual server purchase path. |
| Side-discovered statics and constructor delivery | `FedSrv/fsmission.cpp:4169-4250`, `FedSrv/FedSrv.CPP:11343-11381` | Own-side knowledge only; native goto/build orders and automatic station successor at build. |
| Mounted mass | `Igc/shipIGC.cpp:1402,1468`, `Igc/magazineigc.h:80-82`, `Igc/dispenserigc.h:81-83` | Add mounted parts and each expendable; shared simulator hull-only mass remains a stated limitation. |
| Damage flags, armor/shield matrix, repair and shield bypass | `Igc/missionigc.h:148-155`, `Igc/stationIGC.cpp:138-182`, `Igc/shipIGC.cpp:1244-1267` | Scoring uses loaded damage data and keeps negative repair guns separate from damaging guns. |
| Behind-goal turning and directional thrust | `Igc/common.cpp:169-176`, `Igc/shipIGC.cpp:2965-2981` | ON liveness uses the existing native primitives and P707 timing. |
| Equipped turret seats and ripcord anchors | `FedSrv/FedSrv.CPP:2347-2402,13319-13378` | Existing board/ripcord handlers own all eligibility and completion. |

## Fleet

`CivLoadoutPolicy` ranks complete, affordable, nonobsolete fits at one actual dock.
A bounded beam retains up to 24 gun sets per mount from up to 12 promising guns;
optional equipment is scored in the complete fit. This is a bounded optimizer,
not a claim of globally exhaustive equipment optimization.

The score includes live owning GA, shared 30-second energy/ammo supply, missile
magazine size/readiness/lock/tracking, projectile speed/range, armor and shield
matrix effectiveness, mounted mass, acceleration/booster fuel, turning, speed,
scan and signature. Combined gun/missile output is evaluated across target layers;
a zero shield multiplier bypasses shields. Strike fits can use a zero-station-
damage aim gun alongside damaging missiles. Unmanned turrets earn no damage.

Enemy hulls and public GA come from the purchasing pilot's observations. Before
contact the scorer uses public opponent starting profiles. Enemy inventory is
not public: a core preferred shield fit is an explicit assumption for observed
hulls, and the strongest fitting starting shield is assumed for starting profiles.
Actual observed station types/public GA inform station targets. Hit probability,
blast falloff and sustained delivery remain estimates, not simulated guarantees.
The shared simulator's existing hull-only `Ship.Mass` also limits how literally
its current motion can validate the policy's mounted-mass estimate; no physics
change is included here.

The same profile/spikes derive strike and support shares; reachable sensor and
mobility envelopes derive scout/recon shares. Fighters retain the main line.
A reachable anti-base spike gets the existing SC7 three-pilot strike quorum
when at least six seats leave room for a scout and two other duties. Support
comes from the fighter allocation; it cannot consume scout, recon or strike
seats. Its rounded quota keeps at least one pilot outside support, including
single-pilot fleets. Smaller fleets retain a feasible
strike seat and scout; a locked strike pilot flies an available
combat fit until real hardware can be bought. Native station-threat hardware and
actual accepted inventory still decide strike eligibility. An ON carrier with
native station-threat capability and granted station-damaging hardware can take
its strike duty even when the legacy classifier calls that hull Heavy. Repair, rescue, mine
layers, boarding, finale refits and crew gates retain their obligations. Per-side
global slot ordinals survive worker partition/recovery and recompute duties for
a new match/civ, while each pilot reselects hardware at its current dock.

## Coverage budget and evidence

`bots.civStrategyCoverageShare` defaults to **0.15**, valid range **0..0.25**.
The flexible purchase sequence allows at most `floor(selections * share)`
exploratory selections. Its full-fit resource ledger bounds exploratory accepted
fits plus outstanding reservations by `share/(1-share)` times acknowledged
strategic procurement. Public successor-chain maximum prices reserve against
an unlock racing the purchase ACK. A refusal cannot create strategic credit;
abandoned exploration reservations are conservatively retained on retry.
These are procurement-footprint estimates, not net wallet expenditure after
native hardware refunds.

Unexercised hulls/parts and feasible repair, rescue, mine, probe, chaff, turret/gunner,
boarding and ripcord duties enter this bounded pool. Coverage can change a
flexible pilot's duty for a sortie, then restore its intended role for the next
purchase. Crew coverage needs room for both a carrier and a gunner; it uses a
short bounded gather, at most one recruited gunner per carrier, and the existing
minimum-free-pilot and equipped-seat gates. An explicit `ALLEGIANCE_GUNNERS=off`
remains authoritative. Small crews can therefore defer crewing without losing
strike/scout seats. Ripcord uses a known friendly anchor, no visible local enemy,
no carried objective and no overriding order; ordinary flight continues while
the native request resolves. Mandatory rescue and enabled goal routines retain
priority rather than waiting for an exploratory quota.

Opening research earns its coverage allowance from acknowledged expenditure.
After the opening, the legal reachable tail proceeds. No unsuitable/unknown rock,
unreachable civ mechanism or infeasible crew creates a failing coverage quota.
Pilots retain their combat, travel, support or objective duty while a mechanism
waits; native denials and losses remain observable rather than counted as success.

`[civ-coverage]` distinguishes worker **selected** decisions from bounded server
**granted / launched / fired / hit / completed** events. First windows include
profile-reachable hull/part/mechanism catalogues. The server's actual gun/missile/
expendable/repair/rescue/research counters, damage applications, constructor
completion, gunner binding, ripcord teleport and goal counters supply evidence.
Worker request and full-repair/gunner edges use `[civ-mechanism]`. A trigger or
request alone never proves a server shot, hit or completed routine. Main grant
windows remain inventory grants; additional action stages have their own window.
Explicit overflow is preserved.

`tools/p999-civ-strategy/coverage_receipts.py` audits the stage separation,
window sequence, cardinality and per-worker slot/resource bounds. It never
fills missing mechanisms with fabricated success or failure.

## Local ON versus OFF

All three final repetitions passed. Each arm observed 600.05–600.45 simulation
seconds after Started. Values below list trials 1, 2 and 3; K:D means native
side kills:deaths. These are accelerated observation windows, not full-match wins.

| Civ | OFF research | ON research | OFF K:D | ON K:D | OFF → ON station kills |
|---|---|---|---|---|---|
| Gigacorp | 93, 93, 89 | 149, 137, 137 | 27:14, 17:10, 14:10 | 12:25, 15:14, 14:15 | 0→3, 0→2, 0→4 |
| Phoenix | 15, 41, 54 | 162, 143, 162 | 29:9, 26:5, 26:5 | 27:4, 17:9, 16:3 | 0→0, 0→0, 1→1 |
| Omicron | 43, 40, 33 | 93, 112, 120 | 25:7, 19:4, 22:6 | 23:12, 33:12, 26:14 | 2→2, 1→0, 2→8 |

Maximum simultaneous research was **1 OFF / 4 ON** for every civ in every
trial. Longest measured stationary-target intervals were OFF **2.105 / 2.006 / 2.007 s**,
ON **2.018 / 2.212 / 2.311 s**. The final balanced skirmishes credited station kills at
**89.5 / 58.8 / 60.3 wall seconds** after spawn, with **5091 / 3812 / 4859**
observed hits and zero simulation-pass failures.

Native research milestones, seconds after Started (`37` is Bomber):

| Civ | First completion OFF | First completion ON | Bomber OFF | Bomber ON |
|---|---|---|---|---|
| Gigacorp | 4.55, 3.00, 2.75 | 61.80, 61.35, 61.50 | 108.25, 110.45, 108.05 | 62.35, 61.95, 62.05 |
| Phoenix | 4.55, 3.05, 2.80 | 61.85, 62.55, 61.50 | 103.65, 102.60, 103.45 | 121.20, 120.25, 213.80 |
| Omicron | 4.75, 3.20, 2.95 | 62.00, 62.70, 61.65 | 124.30, 123.20, 124.15 | 308.50, 377.70, 218.80 |

The replacement reserve delays ON’s first donations until the native payday.
ON completes more research in **9/9** civ/trial comparisons; its K:D ratio is
lower in **7/9**. Research priorities also move individual hardware milestones
in different directions. These samples do not establish a balance gain or justify
default ON. Full milestone arrays are in [the loopback evidence](P1001_assets/loopbacks.json).

The comparative fixture uses seed 1001, six pilots per side, the same generated
Brawl map, native purchases/donations, research scale 64 and income multiplier
5000. It records a 600-simulation-second observation window from the actual
Started transition (or an earlier real game end), not a reset clock or an equal
wall-time approximation. Native completion receipts supply research counts and
milestones relative to Started; creation-time known/obsolete buckets are excluded.
Kills/deaths are the native side counters. These accelerated loopbacks are
indicative engineering checks; they are not rig balance or win-rate evidence.

The separate balanced six-per-side 64× research fixture requires a real strike-wave
commit and a credited station kill. It enables the ordinary one-minute,
three-bomber finale on both sides; a real battle kill is still required to arm
it. Its 2400-simulation-second finale deadline lies outside the unchanged
420-wall-second station-kill bound. No target teleport, synthetic damage,
hidden construction intel or post-start wallet/tech top-up is used.

Development findings were corrected before final qualification: ordinary bombers
had bypassed the new scorer through the turret preference; Heavy station-threat
carriers needed the ON granted-hardware admission; identical portfolios needed
an epoch key to clear old sorties; and feasible strike allocations needed the
existing SC7 three-pilot quorum, protected from support assignment. An earlier
balanced Live attempt was **2/0/0, 2/0/0, 1/1/0** with insufficient strike seats.
Unscaled `TechMode=unlocked` diagnostics also missed the station-kill bound:
that mode deliberately uses research scale 1 despite requesting 64. A brief
six-versus-two investigation was interrupted to apply the quorum fix; final
qualification uses the balanced fixture. These are development outcomes, not
passing gates or reclassified non-task flakes. Logs are retained under
`.scratch/p1001/{balanced-six-per-side,controlled-skirmish,nonfinale-diagnostic,unscaled-diagnostic}`.

A subsequent strict comparative run caught a stationary Omicron bomber in the
**OFF** arm before ON ran. The comparative fixture now measures OFF idle duration
and retains the strict five-second assertion for ON. The existing P700/P707
cohort remains unchanged and strict for both arms. OFF-only reproductions on
both current and dispatch-base trees are recorded in the gate/flake evidence;
no OFF movement behavior or idle threshold is changed. Socket scheduling and
controller phases remain live, so seeded map/loadout repetitions vary.

## Gate

The final solution build passed with **0 warnings / 0 errors**. All **135** tool
projects built successfully. Baseline production code and original showcase tests
are archived dispatch HEAD `16e166a1`; its five default P700 never-parks runs
passed **5 / 0 / 0**. An extra OFF-guard diagnostic class exists only in the
ignored baseline archive and was not selected by the original showcase filters.

| Cohort | Cases/run | Three runs: Passed / Failed / Skipped |
|---|---:|---:|
| New commander: all civs, funding, prerequisites, leases and wallet settlement | 17 | 51 / 0 / 0 |
| New fleet: all civs, complete fits, quorum/duties, budgets and repeated-portfolio epoch | 16 | 48 / 0 / 0 |
| New Live: paired three-side windows and six-per-side strike/station kill | 2 | 6 / 0 / 0 |
| New flight-goal liveness: OFF pin, rearward turn and uncancelled pulse | 2 | 6 / 0 / 0 |
| Modified P1000 socket tape: exact OFF pin and ON policy/evidence | 2 | 6 / 0 / 0 |
| New Python coverage parser | 4 | 12 / 0 / 0 |

ON navigation also reuses P707's two-second lateral escape and half-second hold
for flight/dodge goals and close attacks. It resolves directly rearward goals
with the existing native turn helper and prevents opposite lateral buttons
cancelling the pulse. No speed is injected, and dodge still suppresses fire.
The regression test pins OFF controls, rearward recovery, pulse retention, fire
suppression and deliberate holds without a combat target. An ON stationary-target
Live failure before this fix is retained under `.scratch/p1001/pre-flight-liveness/`.
The first official ON showcase attempt then exposed a mode integration defect:
research tactical orders had also activated in unlocked showcases and superseded
their march routes. Seeded/swapped arrival misses are retained under
`.scratch/p1001/pre-march-tactics/`; tactical ownership is now scoped as above.
The exact seeded showcase then passed three times alone (**3 / 0 / 0**),
with no research tactical orders in its unlocked-mode logs.
That alone did not clear arrival qualification: the subsequent swapped run
missed at **21/30**, and the first three default runs were **2 / 1 / 0**, with
the failure at **18/30**. Maximum idle intervals on those failures were
2.013 and 1.803 seconds. A fourth default trial was interrupted during review.
Review found support placement could overwrite a profile-allocated scout seat;
support now comes from the fighter line. These development receipts are retained
under `.scratch/p1001/pre-support-seats/`; all affected cohorts were repeated for
final qualification. The earlier three-side measurements are also retained there.

The five original default OFF showcase trials finished **4 / 1 / 0**. The first
missed arrival at **20/30** against 24 required after 480.1 seconds; it still
produced 3,416 shots, 1,211 hits and 13 kills, with maximum stationary-target time
**1.903 s**. The next four passed, with maximum idle intervals
1.917 / 2.012 / 1.912 / 1.112 s. Both additional showcase variants and all six
P707 cases passed in OFF's first cohort (**8 / 1 / 0** including the arrival miss).
Three further isolated dispatch-base runs passed **3 / 0 / 0**; current trials
2–4 supply the corresponding three isolated current repetitions. Base did not
reproduce the miss. This remains an unresolved intermittent OFF arrival
observation, not a waived failure or a confirmed base flake. The
[flake ledger](../TEST_FLAKES.md) retains it and the earlier OFF comparison
observation separately. No P700/P707 source or thresholds were changed.

All **37 new .NET cases passed three runs alone: 111 / 0 / 0**. The modified
socket cohort adds **6 / 0 / 0**, and Python adds **12 / 0 / 0**. All three
coverage receipt audits passed with no overflow.

| Unchanged showcase qualification | Passed / Failed / Skipped |
|---|---|
| Dispatch base, five original default trials | 5 / 0 / 0 |
| Current OFF, five original default trials | 4 / 1 / 0 |
| Current ON, five original default trials | 5 / 0 / 0 |
| Current OFF, two additional P700 variants and all six P707 cases | 8 / 0 / 0 |
| Current ON, two additional P700 variants and all six P707 cases | 8 / 0 / 0 |
| Current ON, swapped method repeated three times alone | 3 / 0 / 0 |
| Dispatch base, swapped method repeated three times alone | 3 / 0 / 0 |

Every original trial is retained. The original OFF arrival miss and the Fast
failures/overruns keep the overall gate **FAIL**. Native parking thresholds and
arrival/combat requirements are unchanged. Final per-case idle intervals, hits,
kills, arrival counts and durations are in the qualification JSON.

Development failures and interrupted diagnostic attempts are excluded from final
qualification; their logs remain
in task-local scratch. Machine-readable evidence: [qualification](P1001_assets/qualification.json)
and [comparative loopbacks](P1001_assets/loopbacks.json).

The single complete Fast sweep used a **5400-second timeout**, parallelism 8,
and configured artwork. It ran **14,149 cases in 560.3 s: 14,123 passed /
6 failed / 20 skipped**. All **33 P1001 Fast cases passed**; all **9 infrastructure
checks passed**. The calibrated limit was **11.517 s**. Failures were missile
partial-tip receipt, warp status receipt, warp-bomb deadline rounding, NPC defender
return-to-charge, auto-seat elapsed time and roster stint elapsed time. Four
executions exceeded the limit: missile lock **37.752 s**, support selection civ 25
**13.594 s**, turret-carrier selection **13.054 s**, and publisher scan-work
measurement **11.641 s**. Every distinct failed or slow method was repeated
three times alone on each tree: **54 invocations**, with **49 / 8 / 0** current
case outcomes and **52 / 5 / 0** base outcomes. Missile lock failed 2/3 current
and 3/3 base; auto-seat failed 2/3 on each tree. The floating deadline boundary
failed in each current repetition but not in the three base repetitions.
Other repeated methods passed; the three non-missile duration overruns were
below the limit alone on both trees. Exact theory-case counts, durations and
qualification are in [the flake ledger](../TEST_FLAKES.md). These observations
do not clear the original failures. This is a **failed full-suite gate**.

The 20 skips are 13 GPU cases, one audio case, three optional private asset cases,
one corpus git-metadata packaging pin and two map-dependent enemy-sector fixtures.
No P1001 test skipped. The temporary writable profile directory avoided the known
read-only-home profile restoration failure without changing profile source.

The pinned OFF wire transcript remains
`06BAB376D13E480CFAECDC83A2D4CB38F28F400588FE705D186CED0C7FCD4611`.

Artwork/native references are supplied by ignored `local.env`. Docker is not
installed, so `docker compose config` is unavailable; the compose change is a
comment and there is no new service, port, store or environment variable. NuGet
audit remains enabled; package sources are the existing local cache, with no
fresh public advisory lookup under this lane's no-public-host restriction.
No training/HUD changes require lesson or visual gates. No new media is included.
Fast uses a writable task-local `XDG_CONFIG_HOME` for profile-test isolation;
the lane's original ignored `local.env` is restored afterwards.

## Rig A/B handoff for Claude

Run at least six matches per arm with paired seeds, every civilization in every
side/home slot, and a two-side control. Expand the schedule beyond six as needed
to complete that civ/slot coverage. Freeze build, actual bot population,
worker count/partition, map/goal rotation, income/research settings and crew
options. Use the same feature binary in both arms and record its exact build/source
fingerprint; this lane intentionally has no new commit. Set only `bots.civStrategy` differently between paired arms; record
coverage share and explicit gunner policy. Restart server and workers between
arms so coverage and duty state begin fresh. Preserve server and worker logs from
one server lifetime together, with match/cookie identity.

Compare per-civ win rates with intervals, match length, K/D and damage,
first/primary/counter research and infrastructure milestones, miner/ore/payday
income, selected versus actually delivered/fired/hit/completed coverage, and
never-parks. Audit queued jobs/leases, reserved funding and explicit receipt
overflow. Tick p95 and combat decision cadence must not regress. Treat incomplete
windows and unexercised/disabled/unreachable mechanisms explicitly. Domingo owns
the default-ON decision; the local proofs do not make it.

## Native intent

Keep the native economy, availability, visibility, physical equipment and action
gates. Native GA multiplication (`Igc/igc.h:935-945`) and core specifications
provide the numerical inputs; the strategy is not a new native AI rule.

## Modern approach

Use one loaded-core portfolio for funding and fielding, keep construction and
combat independently actionable, protect replacements, bound exploration and
measure actual events separately from choices and requests.
