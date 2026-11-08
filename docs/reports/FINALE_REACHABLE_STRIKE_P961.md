# P961 — reachable finale strikes

Finale wings retain their destination and prepare each pilot at most once per
cycle. The opt-in planner uses reachable granted hardware and nearby threats,
retargets infeasible kills, and commits useful assembly plateaus.

Base `48950527`, branch `codex/p961`. Working-tree changes only: no commits,
pushes, remote hosts, server/simulation/wire edits, damage-table changes or
research-plan changes. The strategy experiment is **opt-in**; the new default is
`finale_reachable_strike: false` so the rig A/B can decide whether to enable it.

## Verified rig evidence

The supplied b76/b77/b78 worker stderr and server summaries were read locally.
The following counts are independently reproduced, rather than copied from the
brief:

| Arm | Station kills, matches 1 / 2 | Finale commits | Preparation refit trips |
| --- | --- | --- | ---: |
| b76 | 12 / 2 | 6 masses of 2–3 bombers | 120 |
| b77 | 10 / 3 | one 18/18 mass; 6, 8, 6 of 18 deadline-forced | 328 |
| b78 | 22 / 1 | 5, 7, 6 of 18, all deadline-forced | 217 |

The b77 line `required=18 assembled=6 ... plannerKill=3 shotsNeeded=9 ...
survival=14(margin=1.00,losses=3,defenders=65)` is present verbatim. So are
`reserved=18 armed=18 assembled=0 ... elsewhere=1:18 ... sector=2` snapshots.
One pilot made **seven** preparation trips with the same ship ID and target
inside one match, plus one more in the next match. Each of the first six
inter-trip ranges contains zero `DEATH` and zero `relaunched into hull` receipts
for that pilot, confirming same-life preparation churn.

**Correction to the brief:** b78's 13 small `wave=N commit` lines belong to the
ordinary adaptive strike coordinator. They are not early-pressure finale
commits. There are only three `finale commit` lines, all deadline-forced, so
those logs do not demonstrate an early-pressure finale volley. One ordinary
commit sends one bomber at a full type-61 **Garrison (Str)**, with 25,000 armor
and 15,000 shield. This pre-existing adaptive-strike behavior is identified
separately; the P961 finale policy refuses a lone bomber at any garrison.

Raw verification is in `.scratch/p961/rig-verification.json`; the core catalogue
used to classify the station types is `.scratch/p961/station-catalog.json`.

## Diagnosis and implementation

| Problem | Evidence and resulting change | Working-tree source |
| --- | --- | --- |
| A crowd pins demand at 18 | The old survival term counts every visible enemy hull in the sector. P961 counts armed-capable hulls near each staging/release corridor, plus recent actual wing attackers. Idle unarmed hulls do not count unless they actually attacked. Sector crowd, nearby threats and actual observed engagements are reported separately. | `tools/allegiance-mcp/FinaleAssembly.cs:30`; `tools/allegiance-mcp/BotManager.ReachableStrike.cs:55` |
| The cap promises hulls that cannot arrive | Count only live, armed, reserved, unspent hulls with an observed paired-aleph route and enough time for travel plus release. Docked/refitting hulls are not promised. Route distance budgets 65% of ordinary cruise speed and five seconds per transition. This is an explicit planning estimate, not a new native navigation law. | `tools/allegiance-mcp/BotManager.ReachableStrike.cs:98` and `:120` |
| A short roster is incorrectly called a kill | Search the damage model independently of the current roster; preserve `CanKill` when even 18 weakest usable loadouts cannot kill. A reachable cap never rewrites a four-bomber kill as a two-bomber kill. | `tools/allegiance-mcp/StrikeVolley.cs:208`; `tools/allegiance-mcp/FinaleAssembly.cs:15` |
| The current target is unreachable | Consider only stations observed by this side. Evaluate reserved *granted* hardware against softer candidates even if it is ineffective against the original garrison. Keep the original finale bound, preparation latch and wing; do not re-arm the server. Candidate changes have a 30-second cooldown. | `tools/allegiance-mcp/BotManager.ReachableStrike.cs:161` |
| An almost-complete useful mass waits for the deadline | A new assembly high-water mark restarts a 45-second timer. A plateau may commit only when the assembled volley strictly exceeds kill count divided by landing share, passes the reachable-kill gate, and has at least two members for a garrison. Massed and deadline-forced commits pass the same usefulness gate. | `tools/allegiance-mcp/FinaleAssembly.cs:47`; `tools/allegiance-mcp/BotManager.StrikeMass.cs:428` |
| Armed bombers go to the wrong home sector | `AssignClashTargets` overwrites a bomber's selected station sector with `ResolveEnemyHome`. Committed destinations now survive clash assignment, scout reassignment and incidental meeting engagements. A stale destination is repaired before the flight driver. Escorts use the same order reset while keeping their fighter role. | `tools/allegiance-mcp/BotManager.Exploration.cs:483` and `:678`; `tools/allegiance-mcp/BotManager.cs:4919` and `:4993`; `tools/allegiance-mcp/BotManager.StrikeMass.cs:108`; `tools/allegiance-mcp/BotManager.Finale.cs:437` |
| Legal fallback hardware keeps being recalled | Remove the unconditional fallback-to-bomber upgrade trip. One preparation latch spans prearm and arm, survives death and retargeting, and clears for the next mission/early-pressure cycle. A death uses the existing normal purchase/relaunch path. Actual empty-magazine resupply remains distinct from preparation refits. Once prepared, a pilot suppresses ordinary firing until the existing arm so the purchased magazine is preserved. | `tools/allegiance-mcp/BotManager.Siege.cs:41`, `:86`, `:105`; `tools/allegiance-mcp/BotManager.cs:6284`; `tools/allegiance-mcp/BotManager.Finale.cs:1238` |
| Desired or already-fired rounds count as armed | Use granted parts, subtract confirmed missile launches since the purchase ACK, and omit ammo-fed guns when the observed ammo pool is empty. Partial grants cannot turn a requested six-round magazine into six available rounds. | `tools/allegiance-mcp/BotManager.ReachableStrike.cs:12` |
| Telemetry loses the end of a wave | Keep the tail before starting a new wave; close a killed target before replacing its plan; record actual pre-release casualties. Refresh observed station type/position when it upgrades. The proof harness observes every damage event through the existing host delegate, because production traces are throttled to one event per five simulation seconds. | `tools/allegiance-mcp/BotManager.StrikeMass.cs:505`; `tools/allegiance-mcp/BotManager.Finale.cs:362` and `:374`; `tools/p959-strike-proof/Program.cs:87` |

The requirement is `min(reachable, ceil(ideal / landing) + threatExtra)`.
The minimum useful mass is `ceil(plannerKill / landing)`, with a two-hull floor
for garrisons. A failed full search, no damaging granted loadout, or fewer
reachable hulls than that minimum blocks every commit reason and permits
retargeting. A plateau additionally needs `assembled > plannerKill / landing`.
The existing P959 landing floor (0.2), damage model and legal purchasing remain.
The reachable planner also supplies the logged kill count, shots needed and
per-round feedback coefficients (`tools/allegiance-mcp/BotManager.StrikeMass.cs:376`). A regression uses real AB3 and AB1 grants: the reachable five-round AB3
heavy subset needs two hulls and seven shots at 5000 damage per pool. The
excluded two-round AB1 member would yield five hulls and nine shots at
4000 damage per pool. Interim changed
runs with the mismatched feedback are excluded from the final comparison.

Threat observations and candidate stations come from this side's client worlds.
The observer's authoritative simulation reads are diagnostic only; they never
feed the bot's decisions. The original arming/maturity criterion and server
900-second timeout are unchanged. The experimental planner uses the original
trigger's existing bound after target changes.

## Complete-window local comparison

Both arms run real research showcase servers and real MCP bots, with the rig's
baseline defaults: immutable snapshots, encode-once, parallel build, pool
transport, native ship-update model, native GotoPlan and server GC. Both use
15 bots per side, varied legal purchases, income multiplier 5000, research
scale 64, finale-after 1 minute, prearm 2 minutes, and a 900-simulation-second
finale. The ticker is paced at 50 ms so the simulation and bot coordinator
clocks agree; every selected run must reach game over. The game clock is 2400
seconds and the wall cap 1800 seconds, allowing an entire finale to finish. Two-side uses `m2xspiderw.igc`, civilizations 25/3;
three-side uses DiamondRing seed 959, civilizations 18/4/5.

As in P959, `--reveal-stations` seeds station visibility for measurement; bots
retain native fog for other knowledge. The early local finale clamps the
2-minute prearm lead to match start in **both** arms. This accelerates the
mechanism proof; it is not a replacement for the full-size rig A/B.

Kill/refit window counts begin at the first coordinator trigger in each run;
the authoritative server arm follows its normal command/receipt delay. Time to
first garrison kill is measured from that authoritative arm, derived from its
logged deadline minus 900 seconds.

Before uses the archived original base MCP assembly. After uses the final
working-tree MCP assembly with `--reachable-strike`. The isolated archives
are `.scratch/p961/observed-base/` and `.scratch/p961/observed-after-build/`. Server/core assemblies
and fixture parameters are shared. Hashes are in
`.scratch/p961/build-manifest.json`; each final run also records its bot
assembly hash and server-GC state. Initial 420-second windows, accelerated 20-ms full windows and interim binaries
were discarded from the comparison. The
inherited 20-ms ticker advanced 50-ms simulation steps; that clock mismatch
would distort the very deadline and plateau decisions P961 measures. The
50-ms paced comparison avoids it.

All **20/20** selected runs reached game over, with native 50-ms pacing,
server GC and the expected bot assembly hashes. Every enabled commit met its
logged useful minimum and reachable cap; every changed pilot made at most one
preparation trip in its cycle.

| Teams | Arm | Station kills / 5 finales | Garrison kills | Commit sizes (reason) | Prep trips in / out | Resupply trips in / out | First garrison after arm |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2 | base | 0/1/4/0/3 = **8** | 5 | 2–12; 10 massed, 3 deadline-forced | 73 / 565 | 71 / 0 | 473.8 s median; 2 no-kill |
| 2 | P961 | 0/2/4/3/1 = **10** | 2 | 2–9; 7 massed, 3 plateau | 9 / 119 | 56 / 0 | 423.1 s median; 4 no-kill |
| 3 | base | 2/1/1/2/6 = **12** | 7 | 4–13; 18 massed | 183 / 0 | 21 / 0 | 468.9 s median; 0 no-kill |
| 3 | P961 | 4/3/5/5/5 = **22** | 10 | 1–13; 21 massed, 4 plateau | 180 / 0 | 16 / 0 | 397.6 s median; 0 no-kill |

2-side: station kills **8 → 10**, garrisons **5 → 2**, preparation trips **638 → 128**, maximum preparations per pilot **38 → 1**.

3-side: station kills **12 → 22**, garrisons **7 → 10**, preparation trips **183 → 180**, maximum preparations per pilot **1 → 1**.

Preparation trips total **821 → 308** across ten finales per arm.
The one-trip-per-pilot bound holds; genuine resupply remains separately visible.
The reduced early-prearm fixture still does not establish a full-size rig default.

First-garrison medians use only runs with a garrison kill; the no-kill count
is shown explicitly. The two-side policy can choose outposts over garrisons
when its granted reachable hardware cannot meet the garrison kill requirement.
This is a trade-off to assess in Claude's rig A/B, rather than a promise of
uniformly faster garrison kills.

Complete evidence: [per-run results](FINALE_REACHABLE_STRIKE_P961_assets/runs.csv),
[every commit](FINALE_REACHABLE_STRIKE_P961_assets/commits.csv),
[comparison and provenance](FINALE_REACHABLE_STRIKE_P961_assets/comparison.json),
and [aggregates](FINALE_REACHABLE_STRIKE_P961_assets/aggregate.json).
The following rows illustrate the full commit CSV; pool values are fractions.

| Run / side / target | Kill + extra (input) | Required / assembled / released; deaths pre / post | Hits | Shield; hull first→last hit | Sector / within 2500 / targeting |
| --- | --- | --- | --- | --- | --- | --- |
| qualified-before-2-1 / 0 / garrison | 4 + 6 (6) | 11 / 11 / 11; 0 / 0 | 1 | 1.0000->0.7338; 1.0000->1.0000 | 6 / 5 / 1 |
| qualified-after-2-1 / 0 / outpost | 1 + 2 (2) | 2 / 2 / 2; 0 / 0 | 1 | 1.0000->0.0000; 1.0000->0.7600 | 8 / 1 / 0 |
| qualified-before-3-1 / 0 / garrison | 2 + 2 (2) | 5 / 5 / 5; 0 / 0 | 0 | no hit; no hit | 2 / 1 / 0 |
| qualified-after-3-1 / 2 / garrison | 2 + 12 (12) | 12 / 12 / 12; 0 / 0 | 329 | 1.0000->0.0000; 1.0000->0.0000 | 14 / 12 / 1 |
| qualified-after-2-4 / 0 / garrison | 6 + 3 (3) | 10 / 7 / 7; 0 / 0 | 11 | 1.0000->0.0000; 1.0000->0.0000 | 7 / 6 / 0 |
| qualified-after-2-3 / 1 / outpost | 3 + 9 (9) | 9 / 4 / 4; 0 / 0 | 7 | 1.0000->0.0000; 1.0000->0.0000 | 7 / 2 / 0 |

Raw selected evidence is retained in `.scratch/p961/observed-before/` and
`.scratch/p961/observed-after/`; analysis helpers are
`.scratch/p961/qualified_summarize.py` and `.scratch/p961/write_evidence.py`.
Reproduction helper: `bash .scratch/p961/replay-arm.sh before 2 <unused-output-dir>`;
choose `before|after` and `2|3` for the other streams.
Raw evidence stays ignored in this worktree because the lane's writable roots
do not include the external evidence directory. Compact report assets total
516505 bytes (below 2 MB).

The damage observer counts every damaging seam event. Its post-hit health is
sampled at the end of the simulation tick; hits in the same tick share the
final pool values. A target-dead flag therefore identifies the hit tick, not
exclusive final-blow credit for a particular ship or side. Aggregate station
kills are deduplicated by station ID. Commit hit attribution uses the released ships through
that side's next commit, so another side's simultaneous commit cannot truncate
the volley. Actual bot/NPC target selections and the number within 2500 units
are reported separately from the policy's conservative threat envelope. Transit-sector
threats can exceed the crowd in the target sector. Observation snapshots occur
within the harness's 250-ms polling interval, rather than atomically with commit.
Targeting means the current bot/NPC attack target at commit, not a confirmed
hit or a whole-wave count of attackers. The CSV distinguishes un-released members from confirmed pre-release deaths;
the inherited raw wave-end `killedBeforeRelease` shorthand was committed minus
released. P961's survival feedback uses the confirmed death counter.

The final proof sink writes only its file and ordered in-memory log. A discarded
baseline attempt exposed a proof-harness console-monitor deadlock: the original
progress writer waited for the current console writer while the forwarding tee
held the current writer and waited for the original. A local dump confirmed
those stacks. Removing forwarding removes the reversed lock edge; both arms
use that corrected sink. This was a measurement-harness failure, not a
server/simulation bug, and no P960/P807 source or tests were changed. Raw dump
and stack evidence remain in `.scratch/p961/hang/` and are excluded from public
report assets.

An observer audit also corrected the flying-ship filter to `StationId is null`:
server station IDs are nullable, so `< 0` had incorrectly reported zero
defenders. That batch is discarded; every selected summary requires
`DefenderObservationVersion: 2`. Legacy membership comes from a read-only
coordinator snapshot, and new builds log their committed ship IDs directly.
Older child runners continued publishing after parent cancellation, so final
outputs and archives use isolated `observed-*` directories with version/hash
validation. Old runner entrypoints were disabled to prevent further iterations.

One discarded pre-fix console-deadlock process still answers on its original
local diagnostic socket, `/tmp/dotnet-diagnostic-364-9629894-socket`. Its parent
session is no longer addressable; a verified peer-process handle returned
`EINVAL` on termination in the isolated command environment. This process is
excluded from every result and needs host-side cleanup by the dispatcher. The
socket identifies it; **364 is its namespace PID, not a host PID to kill**.

## Settings for the rig A/B

| Setting | Shipped default | Meaning |
| --- | --- | --- |
| `finale_reachable_strike` | `false` | Enables threat-based reachable demand, softer retargeting and useful plateaus. |
| `finale_plateau_seconds` | `45` | No new assembly high-water mark for this interval; `0` disables plateau commits. Range 0–600. Requires reachable strike. |
| `finale_engagement_range` | `2500` | Threat envelope around the staging/release path. Range (0, 10000]. Actual recent attackers count regardless. Requires reachable strike. |

Schema/parser: `tools/allegiance-mcp/McpTools.cs:109,1503`; defaults/validation:
`tools/allegiance-mcp/ShowcaseFinale.cs:21,44`. Server GC for both proof arms is
explicit in `tools/p959-strike-proof/p959-strike-proof.csproj:2`.

`finale_survival_margin: 1`, `finale_max_wing: 18`,
`finale_prearm_minutes: 0`, and `finale_early_pressure_minutes: 0` retain their
existing shipped defaults. The local comparison sets prearm 2 in both arms.
The routing and preparation-churn repairs apply to configured finale wings
without requiring the strategy flag. To isolate the strategy in the rig,
compare the new working tree with reachable strike false/true; compare the
archived base against enabled P961 for the combined change. Claude decides
rig settings and defaults; this lane does not contact or configure the rig.

## Tests and gate

New coverage uses real type-315 garrison and type-2 outpost profiles plus the
real AB1/AB2/AB3 magazines. It hand-pins kill/spare/landing arithmetic; idle crowds versus
armed threats; unarmed nearby hulls; reachable caps and failed full searches;
massed/plateau/deadline/never-useless decisions; arrival/deadline eligibility;
observed softer retargeting for both active and currently ineffective wings;
partial-grant/confirmed-launch inventory; active-planner/feedback coherence with
an unreachable weak wing member; prearm magazine preservation without
another purchase; destination ownership for bomber and
escort; and preparation scheduling across death and mission reset.

New P961 coverage contributes **21 cases**, all passed in each focused repeat.
Source: `tests/Allegiance.ClientCore.Tests/P961ReachableStrikeTests.cs:10,146,181,214,238,259,272`.

Only one existing behavior pin changes: the P745 fallback-upgrade test becomes
`P745_P961_Accepted_legal_strike_fallback_keeps_flying_without_an_upgrade_trip`.
Source: `tests/Allegiance.ClientCore.Tests/P745SiegeDutyTests.cs:115`.
Its old expected recall is now an expected continued flight with the granted
loadout. P959, P896 and P801 assertion contracts remain intact; the new strategy
is tested through its separate opt-in policy.

| Check | Passed / Failed / Skipped or result |
| --- | --- |
| Whole solution build | PASS, 0 warnings / 0 errors; final qualification build also 0/0 |
| `tools/build-tools.sh` | PASS, all 121 projects, final qualification 0 warnings / 0 errors; one intermediate MSBuild child-node failure passed three isolated rebuilds and is ledgered |
| New/affected pure tests, three isolated runs | **132 / 0 / 0** each, including P959, P896, P801, P745, P819, P656, P682b and P961 |
| P959 class alone, three runs | **14 / 0 / 0** each |
| Required P745/P778 live suites, three isolated runs | **3 / 0 / 0** each (15m44, 14m52, 15m26) |
| Never-parks baseline, five runs | **5 / 0 / 0** |
| Never-parks changed, five runs | **4 / 1 / 0**; one known arrival-quota failure, **3 / 0 / 0** isolated repeats; parking bound passed 5/5 |
| One full Fast tier | **13,527 / 8 / 3** out of 13,538; 9 infrastructure checks passed. All eight failures are ledgered non-task fixture/timing signatures, qualified below. |
| Glossary links | PASS, 656 distinct paths |
| Parity CSVs | PASS, registered matrices plus the policy matrix |
| Whitespace / public hygiene | PASS; no media, secrets or private code paths added |
| AOT publish / Compose | Not required: no server/core source, service, application env var, port or store change |

Machine-readable receipts: [gate results](FINALE_REACHABLE_STRIKE_P961_assets/gate-results.json)
and [final validation](FINALE_REACHABLE_STRIKE_P961_assets/validation.json).
Raw TRX and test logs are retained in `.scratch/p961/test-results/` and the
matching `.scratch/p961/*-final*.log` / qualification logs. The archived
Live/never-parks assemblies are in `.scratch/p961/final-tests/` and
`.scratch/p961/base-tests/`.

`local.env` is present; focused tests have zero skips. The three broad-sweep skips are optional Meshy-pack
fixtures, not missing artwork. Builds use cached local
packages with NuGet audit disabled after the initial audit cache write failed
on the read-only host path. The publish guard accepts committed ranges only;
no WIP commit was made to run it. Its relevant content/media checks were
applied to changed/untracked working-tree files instead.

The broad sweep's non-task failures are explicit: five `PlayerBundlePathsTests`
executions reproduce under the mandatory scratch-local temporary directory
because simulated external installs discover the checkout's `local.env`; their
Core runtime is byte-identical to the baseline. The profile editor's cleanup
fails on the read-only default profile and passes three times with a writable
scratch profile. The debug-pod cases pass all three isolated repeats. The seeded-session case
is **pass / fail / pass**: the middle repeat assigns the third peer to side 0
after logon-drain reordering. Three further method-only runs pass 1/0/0 each;
the initial failure remains in the qualification record. The wobble body passed in the sweep but exceeded the Fast
budget; isolated repeats pass at 9.899/9.831/9.911 seconds. Full details and
receipts are recorded in `docs/TEST_FLAKES.md`. The final task-specific repeats
pass, including the regression for reachable/all-armed planner coherence.

The one Fast sweep preceded the final opt-in planner/feedback correction and
its new regression. The archived Live/never-parks fixtures leave reachable
strike disabled, so that correction is outside their executed path. After
the correction, the final 132-case set ran three times on the final MCP
assembly, and the 20 selected showcases record that same assembly hash.
No second broad sweep is claimed.

The policy matrix also had two pre-existing format problems: an invalid
`changed-for-bots` status and an unquoted comma. They are normalized to `na`
and valid LF CSV while adding P961's **no-native-credit** row. The same matrix's
stale prearm-default description is corrected from 2 to the actual 0.

## Native intent

Station damage and shield-to-armor overflow remain native
`Igc/stationIGC.cpp:159-193`; armor-first regeneration is
`Igc/stationIGC.h:192-210`. Missile lock still follows the actual off-bow
calculation in `Igc/magazineigc.cpp:148-175`. Purchases remain dock-only,
wallet/tech checked, and ACK/launch controlled by
`Igc/shipIGC.h:989-1005` and
`FedSrv/FedSrv.CPP:6151-6158,6189-6200,6247-6259`. These citations were re-read
in the local native source. No native rule is ported or changed here; the
parity row explicitly classifies the additions as showcase policy.

## Modern approach

A commander prepares each pilot once, preserves the wing's destination, sizes
against threats that can reach the wing, and releases a useful mass when
waiting stops helping. Actual granted inventory, observed routes and the
existing deadline constrain the promise. The complete local comparison and
Claude's rig A/B determine whether this experimental strategy should become
a default.
