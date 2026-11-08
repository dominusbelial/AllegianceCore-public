# P953 — Showcase regressions

**Light gate FAIL; original validation failures are retained without waiving them.** Changes remain uncommitted in `codex/p953`, based on `05ede8f4`.
No commits, pushes, stash, remote-machine runs or public-host access were used.
Artwork and native-source configuration are present. Last report refresh:
2026-11-08 00:51 UTC.

## Culprits and classification

P952's P872 defender change does not explain P798: its parent/culprit comparison
already passes both P798 maps. The current P798 null `Clash` comes from **P926
`f8f3687c`**, integrated by `00475636`: the derived topology rendezvous is stored
as `_scenarioTargetOverride`, causing `AssignClashTargets` to return before any
resource choice. This is a showcase-policy regression.

The supplied `a2f73c13..48cdb2de` Live window does not reliably bracket P798.
Six training midpoints and the endpoint passed in early workstation-GC
diagnostics; the endpoint also passes with server GC (77 seconds total, both
sides 100% sectors, 4/4 alephs, 67 rocks and sector 2081/resource 36.3).
The original spider test fails even before the window: it exits with zero rocks
and zero-resource enemy-home choices before its final rock audit. Applying the
same corrected polling predicate at both historical endpoints, with all final
assertions and time bounds retained, still fails both: at the start side 1
never observes resources; at the end choices remain split across 1054/1058.
No first-bad Live commit is claimed from that invalid bracket. One infrastructure
exit after a successful historical test is also excluded from classification.

A separate deterministic **actual-map observation replay** identifies **P864c
`4873bc315490ddee2829acf83c95c84653306ecb`**, direct parent
`f6e9e480fab8f446d0fafa0ec3caed61675ee3c6` passing. It boots each revision's
real mission and supplies its actual frontier then central asteroid observations
to each side's selector. Before native top-ups the frontiers are empty and both
sides choose central sector 1052/resource 878.2884. Afterwards the frontiers
1054/1058 each contain 25 rocks/resource 0.226415; the old positive-choice latch
ignores the later 134 central rocks and keeps the two separate frontier choices.
This is a **correct native population change exposing an existing preset-policy
assumption**. Native resource population is preserved. The replay establishes
that selection mechanism, rather than claiming to reproduce every Live failure.
[Replay results](P953_assets/resource-replay-bisect.json),
[bisect log](P953_assets/resource-replay-bisect.log),
[exact fixture](P953_assets/resource-replay-fixture.txt),
[map audit](P953_assets/map-resource-audit.json).

SC4's independent Live bisect selects **P877
`f52bd5590c164f139128c28a30852ecd000176fe`**, integrated by `72f04522`.
Under server GC, parent `ea132a30` passes in 513.0 seconds with per-side
chaff/probe/mine counts **251/28/44** and **74/35/58**. The candidate fails at
600.2 seconds despite **192/30/38** and **78/30/26**: it has both contested
probes but no observed home mine. P877 intentionally removes undirected
short-lived player mine drops. Quiet-aleph pre-laying requires a field lasting
at least 60 seconds, whereas these civilizations' ordinary player mines are
short-lived. The long-lived player launcher needs unavailable Rixian base tech.
The fixture's authored plans already produce native mine-layer drones, but the
fixture never supplies their requested deployment location. This is a **coverage
fixture missing a precondition after an intentional policy change**; deployment
minimums remain valid. [SC4 bisect log](P953_assets/sc4-bisect.log).

**P942 does not reorder this fixture.** Actual receipts select authored `plan_a`
for civ 25 (157 steps) and civ 3 (181 steps). `ResearchSchedule` returns authored
plans unchanged before generated-plan recognition; both files are byte-identical
to their versions before P942. [Plan hashes and audit](P953_assets/p942-audit.json).

## Native check and fix

These native citations were reread at revision `052f5179`:

- `FedSrv/fsmission.cpp:4122-4174` exports topology and home flags globally;
  `:4206-4208` exports asteroid objects only when `SeenBySide` permits it.
- `Igc/missionigc.cpp:3978-4007,4048-4051` budgets He3 and populates empty
  file-map asteroid lists. Those correct native top-ups remain unchanged.
- `Igc/shipIGC.cpp:2651-2665` is the unchanged native wingman mine trigger.
  Showcase player-bot policy remains distinct from that NPC simulation rule.
- `FedSrv/FedSrv.CPP:11518-11543` produces the funded layer and requests a
  location; `Igc/shipIGC.cpp:2823-2828` lays at the completed Build destination;
  `Igc/mineigc.cpp:111-114` uses the authored lifespan.

The P798 policy now distinguishes an automatic rendezvous from an explicit
operator target. It completes the existing automatic march before allowing an
early frontier to divert it. Once enemy-station recon and the opening miner raid
(or its existing 90-second opportunity) have happened, the same recon slot
visits the still-unobserved rendezvous. The recon transit takes precedence over
repeated miner attacks at the enemy base. No new destination, crew, strategy
preset or performance switch is added. Explicit and independent orders keep
ownership; small meeting engagements keep their existing travel behavior.

A per-side discovery-count cache reconsiders a positive choice when new rocks
are observed, allowing only a richer resource commitment. Ore depletion and
miner movement cannot churn the fleet back to a poorer frontier. Scoring and
topology fallback are unchanged; every resource observation remains side-visible.
The Live polling loop now waits for the positive shared objective already
required by its final assertions, within the original 360/480-second bounds.

SC4 sends ordinary commander Build orders to the mine layers normally produced
by its authored plans, targeting each side's home aleph. It requires affirmative
orders and actual long-lived home fields, retaining all original per-side
10 chaff / 10 probes / 5 mines minimums and contested-probe checks. Civilizations,
research scale, 120 combat peers and the 600-second bound are unchanged. There
are no grants, technology bypasses, direct model insertion or simulation changes.
Policy/audit rows are in `docs/parity/p953-showcase-policy.csv`; they claim no
new native parity credit. `GLOSSARY.md` indexes the pattern.

## Before and after

The dispatch binaries were copied before source edits. With the required
baseline and actual server/concurrent GC, dispatch P798 fails **0/1/0** on
each map: training reaches full coverage and 67 rocks per side but both clashes
stay null at 360 seconds; spider observes 378/170 rocks but both clashes stay
null at 480 seconds. Its authoritative audits find zero visibility leaks.
Dispatch SC4 fails **0/1/0** at 600.2 seconds with counts **241/25/35** and
**114/30/26**, contested probes on both sides, home mines only on side 0 and no
long-lived home-field sample.

Final isolated measurements (resources are side-observed):

| Run | Observation seconds | Side 0 | Side 1 | P/F/S |
| --- | ---: | --- | --- | --- |
| p798-fixed-training-1 | ? | sector 2081, resource 36.3, 67 rocks | sector 2081, resource 36.3, 67 rocks | 1/0/0 |
| p798-fixed-training-2 | ? | sector 2081, resource 36.3, 67 rocks | sector 2081, resource 36.3, 67 rocks | 1/0/0 |
| p798-fixed-training-3 | ? | sector 2081, resource 36.3, 67 rocks | sector 2081, resource 36.3, 67 rocks | 1/0/0 |
| p798-fixed-spider-1 | ? | sector 1052, resource 878.3, 280 rocks | sector 1052, resource 849.5, 273 rocks | 1/0/0 |
| p798-fixed-spider-2 | ? | sector 1052, resource 878.3, 283 rocks | sector 1052, resource 852.8, 298 rocks | 1/0/0 |
| p798-fixed-spider-3 | ? | sector 1052, resource 878.3, 320 rocks | sector 1052, resource 849.5, 274 rocks | 1/0/0 |
| sc4-fixed-1 | 71.0 | 34/11/5 | 19/12/7 | 1/0/0 |
| sc4-fixed-2 | 75.1 | 32/13/7 | 13/13/7 | 1/0/0 |
| sc4-fixed-3 | 79.6 | 44/14/7 | 16/13/7 | 1/0/0 |

SC4 numbers are chaff/probes/mines. Each passing SC4 run observes both
contested probes and long-lived home fields; deployed layer IDs, affirmative
orders, actual field lifespans and distances are in the validation JSON.
Each P798 run retains the authoritative per-side rock visibility audit.

## Validation

Passed/Failed/Skipped per isolated invocation:

| Coverage | Run 1 | Run 2 | Run 3 |
| --- | --- | --- | --- |
| Ten new policy cases | 10/0/0 | 10/0/0 | 10/0/0 |
| 43 affected cases | 43/0/0 | 43/0/0 | 43/0/0 |
| P798 training_6 | 1/0/0 | 1/0/0 | 1/0/0 |
| P798 m2xspiderw | 1/0/0 | 1/0/0 | 1/0/0 |
| SC4 Live | 1/0/0 | 1/0/0 | 1/0/0 |

Solution build: **0 warnings / 0 errors**. `tools/build-tools.sh`: **all 120
projects, zero warnings/errors**. No Core/Server or training implementation is
changed, so AOT publish and the Live-training gate do not apply.

Parking comparison: five isolated original-binary runs against five isolated
changed-tree runs, on identical baseline settings. Measurements and starting
load averages are retained for every run:

| Run | Base P/F/S | Base longest target idle (s) | Fixed P/F/S | Fixed longest target idle (s) |
| --- | --- | ---: | --- | ---: |
| 1 | 1/0/0 | 0.802 | 1/0/0 | 0.0 |
| 2 | 1/0/0 | 1.112 | 1/0/0 | 0.301 |
| 3 | 1/0/0 | 2.508 | 0/1/0 | 1.914 |
| 4 | 1/0/0 | 1.62 | 1/0/0 | 0.1 |
| 5 | 1/0/0 | 1.713 | 1/0/0 | 1.604 |

The original changed-tree comparison includes an arrival-assertion failure at
480.1 seconds (20/30 reached; longest target idle 1.914 seconds). It did not
violate the five-second parking limit. The failure is retained in the light
gate result. P952 already records an intermittent arrival-assertion failure;
this result is recorded as another occurrence rather than silently replaced.

Required isolated arrival rechecks: **1/0/0 ×3**, with observation times
233.2/261.1/463.9 seconds and longest target idles 2.311/2.306/1.815 seconds.
The assertion passes all three rechecks; the original failure remains ledgered.

Parking-limit violations: **0/5 base, 0/5 fixed**. Overall test outcomes differ
because of the arrival assertion; this comparison does not establish equal
arrival performance. Starting loads and the full combat/arrival measurements
are retained for each run.

Exactly **one Fast-tier sweep**: **13,475 Passed / 3 Failed / 3 Skipped**, 944.1 seconds.
Infrastructure: **9 passed**, 0.064 seconds. Timing offenders: **4 executions**.
The structured validation records the original errors and outcomes.

Skipped Fast cases: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists`.

Every failed or over-time method from that sweep was rerun three times alone.
The six methods produce **25 Passed / 2 Failed / 0 Skipped** across 18 isolated
invocations (27 parameter-case executions):

- Full-session chat receipt: the existing `ChatTarget` ordering race, expected
  `-2`, received `0`; isolated **fail/pass/fail**, so it remains intermittent.
- Silent-peer side seating: a ship already exists when the fixture expects
  none near the 30-second boundary; isolated **pass/pass/pass**.
- Warp-bomb timer, delay 7: `7.0000000000000009` exceeds the inclusive upper
  bound `7`; all three delay rows pass in each isolated invocation.
- P814 gunner preference, SC2 support selection (both civilizations), and P777
  warp-exit measurement: all pass three isolated invocations below the
  calibrated 10-second limit. Their maximum case times are respectively
  3.712, 4.680 and 9.847 seconds.

These signatures are already recorded in `docs/TEST_FLAKES.md`; their chat,
seating and timer implementations/fixtures are unchanged. The ledger retains
all individual outcomes and timings. Neither the original Fast failure nor
the parking-fixture arrival failure is waived.

All final Live qualification and parking runs use immutable snapshots,
encode-once, parallel broadcast construction, pool transport, native ship
updates, native GotoPlan and verified server/concurrent GC. No optional
performance experiments are enabled. Process overrides are removed for the
Fast sweep's fixtures that explicitly select legacy comparison paths; production
defaults select the required baseline. A task-local lock serializes Live runs.
Initial diagnostics used a wrongly capitalized GC variable and workstation GC;
those and the superseded first implementation's failed spider qualification
are excluded from final acceptance and retained in raw evidence.

The supplied SDK and local package cache are used throughout, with package
auditing disabled. Fast fixtures use writable workspace configuration and a
`/proc` alias into workspace scratch for temporary paths, so bundle-path tests
do not accidentally discover the checkout's ancestor `local.env`. A local
Tailscale shim prevents external host probing.

[Validation counts, runtime receipts, loads and models](P953_assets/validation.json).
[Raw local TRX, logs and scripts](../../.scratch/p953/) are ignored workspace
evidence; disposable historical clones and isolated build artifacts were removed.
Public evidence is below the 2 MB task limit; no media or secrets are added.

## Native intent

Preserve side-visible knowledge, native file-map resources, legal equipment
and native layer travel/build/lifetimes. Showcase must observe real resource
clashes, chaff, probes and defensive mine fields on both sides.

## Modern approach

Repair ownership and completion of the existing opening, and supply the
fixture's ordinary commander orders. Keep the original coverage minimums and
authoritative model checks, with independent causal comparisons and measured
acceptance evidence. Leave the changes in the working tree for review.
