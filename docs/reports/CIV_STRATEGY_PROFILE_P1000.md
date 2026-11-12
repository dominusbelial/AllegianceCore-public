# P1000 — Core-derived civilization profiles and observation-only portfolios

`bots.civStrategy` defaults **off**. Enabled servers build immutable loaded-core
profiles, choose a primary and compatible counter using public opponent civs,
and emit bounded receipts. P1000 installs no chosen research plan and changes
no commander funding, fleet role mix or loadout selection. P1001 can consume the
new shared policy model later.

Base: `67c950eb`. Changes remain in the working tree; no commits, pushes, stash
operations or rig runs. Gameplay proofs use local loopback only. Corpus resources
come from ignored `local.env`. This report was written before the gate.

## Profile schema and ownership

`src/Allegiance.Core/Strategy/CivProfile.cs` lives with the shared commander/core
research model, independently of the offline tool. `Get(core, civ, settings)`
uses weak core ownership and caches by immutable settings/civ. A canonical
SHA-256 identity includes version, ordered core records, constants, all part
subtype tails, projectiles, missiles and deployables. Same-version mods therefore
have distinct identities; equal content has equal identity regardless of record
insertion order. The loaded static core is treated as immutable, as elsewhere in
the runtime. Each returned profile contains only immutable arrays/records and
read-only JSON snapshots; it retains no live mission or mutable core arrays.

The schema includes:

- Civilization name/ID, version/identity, base/no-development/effective starting
  techs, starting station and purchases, and base GA.
- Every core prerequisite node: required/global/local masks, frozen price/time,
  attribute factors, constructor/rock mask, successor, reachability and all bit
  providers. This is a compact AND/OR graph of prerequisite alternatives, not
  an enumeration of factorially many independent completion interleavings.
- Goal-directed legal route witnesses for each reachable target and its direct
  alternative providers. Recursive prerequisite search backtracks, never merges
  station locals, prunes incidental steps by replay, and retains the complete
  provider graph for later searches. Routes store funding, serial/critical time
  estimates and multiplicatively accumulated GA. This is not a globally minimal
  route optimizer.
- All hulls: mobility/mass/turning/scan/signature, armor/defense, energy/recharge,
  ammo, equipment masks, magazine/dispenser/chaff capacity, five cargo slots,
  fixed weapons, turret/crew requirements, per-mount compatibility and full raw
  specification snapshots.
- Parts with subtype specs, requirements/successors, mass/price, per-defense
  direct and blast damage vectors; full projectile/missile/drone snapshots and the damage matrix.
- Economy: additive payday/starting-money factors, loaded payday/He3 constants,
  mining rate/yield/capacity, development cost/time and legal miner/constructor
  identities. Concrete miner cycle times and constructor delivery enter scoring.

No civilization ID or path name selects a strategy. Tests rename and renumber a
civilization to an otherwise unknown ID, perturb core identity without changing
version, close paths, disable developments, verify every route's GA and funding,
and verify the chosen routes through actual Side bucket and station upgrade gates.
The shared ResearchState replay avoids computing its entire purchase catalogue
when a step has no expected-unlock assertions; this changes no state or rule.

## Scoring and public assumptions

`src/Allegiance.Core/Strategy/CivPortfolio.cs` evaluates four concrete capabilities:
anti-ship loadouts, anti-base wings, miner turnaround and rescue/repair. Every
loadout is legal at one actual station context, including automatic station
successors and obsolete hull/part rejection. Guns share a 30-second energy/ammo
budget; magazines respect size, readiness/lock cycle, and missile damage. Hull
handling includes mounted mass, acceleration, speed, turning and signature; gun
speed/range use the appropriate GA. The delivery heuristic adds half the authored
blast power to direct damage (an explicit falloff estimate), excludes capture-only
missiles from anti-base damage, and scales repair by gun GA. Target resistance uses armor/shield HP and
harmonic damage-matrix effectiveness against the actual opponents' starting legal
hulls, fitting shields and home stations, with their base GA and mobility.
No enemy positions, hidden research, units, rocks, wallets or server intelligence
are read by the policy API.

Default public planning assumptions: 30,000 procurement funding, six human crew,
maximum three crew per ship, 1,000m travel distance, 30s sustained fire. Crew is
partitioned into three anti-ship, two strike and one support seats. Eighty percent
of procurement funding is divided by those seats; twenty percent reserves an NPC
miner option. This is an explicit feasibility scenario, not a new live fleet mix.
The returned combined witnesses respect the total crew and procurement budget.
Each duty uses a concrete hull, station, mounted parts, quantity, crew and price;
unmanned turrets earn no damage. Mining includes loaded capacity/rate/yield,
round-trip travel and actual station income. Rescue-capable hulls and negative-
power repair weapons receive explicit utility.

Each capability is normalized against the greater of initial capability, 10% of
the attainable candidate envelope, and 1. Weighted logarithmic gains use
0.45 anti-ship / 0.30 anti-base / 0.15 economy / 0.10 support, divided by
`1 + researchFunding/30000 + criticalSeconds/300`. Independent prerequisite
buckets may overlap in the critical-time DAG. Constructors add public-distance
travel and the existing 30s construction shell. The research accelerator divides
bucket and shell times; it does not change frozen costs.

The primary shortlist contains at most three candidates within 80% of the best
score. Counter candidates must replay legally after the primary and improve its
capabilities. Joint primary+counter utility charges shared prerequisites once
and is recomputed from the combined GA and loadouts. At most three close complete
portfolios remain. The stable SHA-256 input includes canonical core identity,
version, seed, own civ/settings, sorted opponent profiles and public planning
assumptions (including goals/map metadata). Hash words choose primary/counter
shortlists without consuming gameplay randomness. A uniquely stronger feasible
opening can retain a single primary; counter alternatives provide portfolio
variety. Empty/disabled trees report no improving choice rather than inventing one.

Choice logs include inputs, chosen primary/counter, bounded alternatives and
rejection counts/examples (non-improving, unfieldable, incompatible counters and
outside the close set). Profiles build at assembly; choices publish once per
match/side when play starts, after lobby civilization changes. Rotations receive
fresh match/seed/map identities.

These are conditional delivery estimates. Suitable rocks, funding availability,
known safe routes, realized crews, constructor survival, hitboxes and battle
attrition are not predicted. The assumed distances do not claim to be discovered
routes. No balance or win-rate improvement is claimed by an observation-only task.

## Every PCore001 civilization and today's plan

Core version `990051329`; seed 1000, default settings, each civilization against
the other nine public profiles. The table below is regenerated from the production
API. All first 40 steps, selected-spike serial funding/times and concrete fielding
witnesses are in [the comparison appendix](P1000_assets/plan-comparison.md);
[portfolios.csv](P1000_assets/portfolios.csv) preserves identities and input hashes.
The comparison ordering is an offline validation witness, never a live plan.

| Civ | Primary | Counter | Primary funding | Critical s | Routes |
|---|---|---|---:|---:|---:|
| Dreghklar | Upgrade Medium Class | Sm Shield 2 | 62500 | 447.8 | 200 |
| Technoflux | EW Hvy A-B Cannon 2 | Large Shield 3 | 66250 | 1945.6 | 171 |
| Ga'Taraan | Upgrade Medium Class | GA: Ship Acceleration 1 | 50000 | 573.3 | 295 |
| Phoenix | Upgrade Medium Class | GA: Ship Speed 1 | 40000 | 526.7 | 307 |
| Omicron | Upgrade Heavy Class | GA: PW&EW Damage 1 | 55000 | 946.7 | 173 |
| Gigacorp | Bomber | ER Nanite 2 | 10000 | 120.0 | 207 |
| Iron Coalition | Upgrade Medium Class | ER Nanite 2 | 33000 | 346.7 | 183 |
| Bios | Shipyard (Dry) | Upgrade Heavy Class | 35000 | 213.3 | 171 |
| Belters | Upgrade Medium Class | GA: Missile Damage 1 | 29375 | 569.0 | 198 |
| Rixian | Bomber | ER Nanite 2 | 10000 | 12.0 | 178 |

Serial milestone columns in the appendix exclude constructor travel/shells and
income waits; its critical-time column includes the documented planning scenario.
Neither is a measured live unlock time. Reproduce with the tool's `--production`
mode and `tools/p999-civ-strategy/render_production.py`.

## Receipts and consumption

Server JSON `"bots": { "civStrategy": true }`; environment override
`ALLEGIANCE_BOTS_CIV_STRATEGY=1|true|on` or `0|false|off` (case-insensitive;
empty preserves JSON). Invalid values fail before listeners open. Startup logs
the effective value. Compose passes the override through; operator references
are updated in `docker/README.md` and `docs/GUIDE.md`. OFF creates no profiles or
strategy receipt entries and installs no observation callbacks.

`[civ-strategy]` uses AOT-safe, source-generated JSON:

| Kind | Content | Bound |
|---|---|---|
| choice | Match/cookie/seed, civ, home slot/sectors, actual map/seed, core/settings/opponents/input hash, primary/counter and alternatives/rejections | Once per side; 3 primary + 3 counter alternatives; route preview 40 steps with full count |
| research | Exact server simulation completion time, match/cookie, side/civ/development | One native completion event per finite development |
| window grants | Actual hull/inventory increases, including batched loadouts, counts/quantities, first/last sim times | 256 keys per 60s window |
| window damage | Attacker/victim side, source kind/core weapon identity, victim hull, raw input and removed hull+shield HP | 256 keys per window; lethal hits included before ejection |
| window economy | All living registered miners including docked ones, ore aboard, actual dock/tele-refinery ore sold and credited treasury amounts, native periodic payday count/income | One row per side per window |
| window stations | Station kill credits and victim-side destruction losses, including uncredited losses | One row per side per window |

One window per simulation minute plus a final flush precedes rotation. Capped
key dictionaries report overflow event/amount totals explicitly; they do not
silently present incomplete detail as complete. `overflowAmount` is a diagnostic
sum of dropped grant quantities and removed HP, not a per-side capability or
economy total. Source identity is gun part ID,
missile/mine expendable ID, or -1 when unavailable (synthetic/probe/environmental
sources). Explosions carry observation metadata without changing original kill-
cause labels, damage, RNG or message encoding. Inventory observation follows native loadout revisions, including silent quantity
changes and refills. Separate observation batching keeps cargo reloads/remounts
from becoming false grants without changing native wire batching. Payday counters observe the periodic mission
accrual separately from ore sales, donations and treasure income.

`tools/p999-civ-strategy/receipts.py` is the successor receipt parser. It preserves
exact completion times, sums interval quantities and keeps miner samples as
samples. Completion times use the absolute server simulation clock. Ore measures sales
and ore aboard, not a reconstructed total harvest including lost cargo.
Missing observations remain absent; duplicate/gapped/post-final windows
fail closed. Separate server lifetimes stay in separate input files. Four parser
fixtures cover identity, partial data, bounds/overflow, duplicates and station losses.

Measured examples from the seeded ON replay (match 1, cookie 1):

```json
{"kind":"research","match":1,"cookie":1,"side":0,"civ":18,"tech":222,"sim":0.65}
{"kind":"research","match":1,"cookie":1,"side":1,"civ":4,"tech":222,"sim":0.9500000000000001}
```

Its final window at sim 70 reports gun part 33, damage type 2, side 0 against
side 1's hull 190: 684 damage applications, raw 2188.80003, removed HP
1094.40320. These are server damage applications, distinct from the 4,140
network hit receipts received by both peers. The two sides record one native
payday each, 750 and 600 treasury income. Separate dock/tele-refinery fixtures
each sell 10 ore, retain a living registered miner, record one station kill and
one opposing loss, and exercise 256 damage keys plus explicit overflow. They
also verify empty-to-full replenishment and that cargo reloads do not become
additional grants. The replay's idle-admission clock is reset before its tape;
its pre-tape receipt window is retained, so it is not a normal monotonic-clock
match-duration benchmark. The parser reports one match with a final flush.

The first autonomous ON trial supplies a normal advancing-clock example: seed
1356040297, Iron Coalition side 0 against Ga'Taraan, map `m2xspiderw.igc`, home
slot 0 / sector 1050. Its first window records 16 hull-13 grants between sim
0.2 and 8.4. Window 4 at sim 240.1 records gun part 120 / damage type 5 against
opposing hull 121: 162 applications, raw 712.80002, removed HP 491.59984.
The parser accepts six windows with 52 grant and 30 damage rows and no overflow.
It marks the capture **incomplete** because this fixture stops a running match;
no terminal-window or full-match totals are invented.

## Validation and gate

Final new-test qualification, each class run three times alone with artwork:

| Cohort | Cases per run | Combined Passed / Failed / Skipped |
|---|---:|---:|
| Core profiles, legal routes, identity, modded civ and opponent sensitivity | 15 | 45 / 0 / 0 |
| Flag/configuration | 11 | 33 / 0 / 0 |
| Dock/tele-refinery, inventory, damage bounds and station receipts | 2 | 6 / 0 / 0 |
| Seeded real-loopback OFF pin and ON/OFF equivalence | 2 | 6 / 0 / 0 |
| **New .NET tests total** | **30** | **90 / 0 / 0** |
| Successor receipt parser (Python) | 4 | 12 / 0 / 0 |

The final solution build completed with **0 warnings / 0 errors**. All **135**
projects discovered by `tools/build-tools.sh` built successfully. An additional
existing integration cohort (configuration JSON defaults, sim updates and native
builder flows) passed **81 / 0 / 0**, three runs of 27 cases.

The corrected full Fast tier ran 14,116 cases in 541.1s:
**14,090 passed / 6 failed / 20 skipped**. Infrastructure checks passed (9/9).
The gate remains **FAIL**, including four calibrated Fast-duration overruns.
The failures are launcher mount receipt, missile partial-tip receipt, chased-bot
mine receipt, remote death latch, roster elapsed time and warp status receipt.
The duration overruns are missile lock (38.132s), chased mine (30.664s), varied
loadouts civ 35 (12.207s) and existing P999 routes civ 5 (16.603s); the calibrated
limit was 11.554s. These are qualified on both the archived base and changed tree,
three runs alone per method; details are in `docs/TEST_FLAKES.md`.

An earlier diagnostic Fast run had **14,077 / 19 / 20**. It exposed twelve
task regressions (legacy unbooted-server config access and JSON default
serialization), which were corrected before the final build and qualification.
The second full run verifies those fixes; it does not replace or hide the first.
Non-task cases appearing only in that first run are also included in the isolated
base/current qualification. No P1000 test failed in the corrected sweep.

The 20 skips comprise 13 GPU-dependent cases, one audio case, three optional
private Meshy asset cases, one artwork-packaging pin requiring corpus git metadata,
and two fixtures requiring an enemy in the same sector for the loaded map.
No P1000 case skipped. All runs use timeouts and task-local evidence under
`.scratch/p1000/`. NuGet audit remains enabled, with its HTTP cache redirected
into writable scratch. The autonomous qualification below also passes.

[Gate evidence](P1000_assets/gate-evidence.json) preserves per-run new-test
counts, all isolated base/current outcomes and the corrected Fast calibration.
Glossary links pass (783 distinct paths), the registered parity scorecards
validate, and `git diff --check` passes. An equivalent working-tree content/path
scan of 44 changed/new files found zero publish-hygiene issues and no new media;
the supplied untracked task brief is excluded. The commit-range publish guard
is not claimed as a working-tree check, and no commit or push was made.

### Autonomous showcase qualification

The existing `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
ran five times each on archived base `67c950eb`, OFF and ON, serially alternating
arms. **Each arm: 5 passed / 0 failed / 0 skipped**; **15 / 0 / 0** overall,
matching the base success rate. Every trial reached 24/30 objectives and recorded
ship hits. No trial crossed the five-second parking limit: maxima were base
2.804s, OFF 2.005s, ON 4.007s. All original outcomes are retained; no retries were
needed. This existing fixture randomizes run conditions, so it is not a paired
seed comparison, a win-rate study or a performance estimate. Wall times include
startup/cleanup. Zero kills in ON trial 5 is recorded as observed; this fixture
requires hits, arrival and the parking bound, and does not require a kill.

| Trial | Arm | Outcome | Wall s | Reached | Longest park s | Shots / hits / kills |
|---:|---|---|---:|---:|---:|---|
| 1 | base | Passed | 155 | 24 | 2.804 | 5610 / 2884 / 17 |
| 1 | off | Passed | 156 | 24 | 2.005 | 5109 / 2439 / 21 |
| 1 | on | Passed | 166 | 24 | 2.21 | 5816 / 3267 / 21 |
| 2 | base | Passed | 231 | 24 | 2.004 | 6111 / 2788 / 16 |
| 2 | off | Passed | 355 | 24 | 1.209 | 2216 / 793 / 6 |
| 2 | on | Passed | 191 | 24 | 4.007 | 6666 / 3374 / 20 |
| 3 | base | Passed | 378 | 24 | 2.005 | 3207 / 1113 / 8 |
| 3 | off | Passed | 286 | 24 | 1.814 | 2107 / 628 / 4 |
| 3 | on | Passed | 152 | 24 | 2.006 | 6205 / 2901 / 18 |
| 4 | base | Passed | 355 | 24 | 0.603 | 2564 / 1131 / 7 |
| 4 | off | Passed | 296 | 24 | 1.511 | 3112 / 1518 / 12 |
| 4 | on | Passed | 174 | 24 | 1.616 | 5036 / 1929 / 14 |
| 5 | base | Passed | 395 | 24 | 1.807 | 4870 / 2013 / 15 |
| 5 | off | Passed | 186 | 24 | 1.907 | 3627 / 1890 / 12 |
| 5 | on | Passed | 272 | 24 | 0.301 | 1050 / 197 / 0 |

All five ON logs pass the successor parser with two side choices per capture,
typed combat damage and no overflow. Across them: 33 windows, 244 grant rows and
112 damage rows. Four captures remain incomplete; one contains a terminal flush.
No research completion was observed in this autonomous fixture; exact research
completion coverage comes from the seeded research-enabled replay. All ten
base/OFF captures contain no strategy receipt lines. These are observation counts,
not inferred harvest, damage, wins or unobserved match totals.

### Seeded wire equivalence

The seeded real-loopback proof admits two clients, then replays a fixed showcase
input tape (normal loadout selectors, research buckets and combat simulation).
The idle admission clock is normalized before recording; no captured combat frame
is filtered or normalized. Base `67c950eb`, OFF and ON each produce **21,410 frames,
1,061,622 bytes and 4,140 hit receipts**. SHA-256 over tick, recipient, delivery
metadata, lengths and every packet byte:
`06BAB376D13E480CFAECDC83A2D4CB38F28F400588FE705D186CED0C7FCD4611`.
The base archive runs the same OFF fixture; this digest is pinned in the test.
The ON/OFF test also compares generated research plans and checks completion,
grant, typed damage and native payday receipts. It uses scripted inputs for
reproducibility; the separate never-parks gate exercises autonomous MCP bots.

Environment limitation: `docker compose config` cannot run because this container
has no Docker/Compose executable. No substitute is reported as that gate passing.
The Compose edit is one environment pass-through matching the documented override.
All task build/test processes have exited; changes remain uncommitted in the
working tree.

## Native intent

No new native gameplay rule is ported or parity-completion credit claimed. The
profile reuses native-equivalent mechanics: starting techs
`Igc/missionigc.cpp:3869–3883`; station-local purchases
`Igc/stationIGC.h:383–391`; mount masks/cargo
`Igc/hullTypeIGC.h:163–175,199–212`, `Igc/igc.h:845`; frozen development buckets
`Igc/bucketigc.cpp:70–73`; accumulated GA `Igc/igc.h:935–945`; mining rate/capacity
and sale `Igc/shipIGC.cpp:2211–2213,1985–1988`; periodic income
`FedSrv/fsmission.cpp:2535–2546`; damage flag masking
`Igc/missionigc.h:149–155`. Each citation was re-read locally and is tracked
in `docs/parity/p1000-profile-inputs.csv` with production consumers and tests.

## Modern approach

Build strategy inputs from each loaded civilization's specifications, compare
feasible fielding portfolios against public opponents and retain deterministic
bounded variety. Preserve native actions and today's live policies, and measure
actual completions, grants, damage, economy and station losses before P1001 acts.
