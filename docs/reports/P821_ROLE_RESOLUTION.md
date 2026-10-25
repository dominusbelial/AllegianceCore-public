# P821 — showcase role resolution across civilizations

Base: `9d78ec6b221f4d123e2e9568feb6d7bb04feaca9`, branch `codex/p821`.
Codex lane: working-tree changes only; no commits, pushes, or remote-host access.

## Cause and catalogue

The suspected cause is confirmed. Native `Igc/igc.h:725-741` defines hull
ability bits, including fighter, station threat and no-ripcord; it has no
scout/interceptor class. `TechLoadoutSelector.Classify` is a bot catalogue
policy: scanner >= 12× speed becomes Scout; no-ripcord or low-ammo/short-scan
becomes Interceptor; the remaining small combat craft become Fighter.

The rig matchup is side 0 **Iron Coalition (25)** versus side 1 **Ga'Taraan
(3)**, both `plan_a`, on `m2xspiderw.igc`. Iron Coalition's starting Scout and
Lt Interceptor leave no Fighter class; Ga'Taraan's Scout and Fighter leave no
Interceptor class. The fixed Fighter → Scout → Interceptor fallback therefore
sent missing Fighters to Scouts even when a stronger interceptor was legal.
The other side's missing Interceptors flew Fighters. Equal requested slots
never implied equal hull-class counts.

All ten civilizations in the actual `PCore001.igc`, version `990051329`, were
queried through `ResearchState`/`ResearchTree.Unlocks`, using their initial
station's local mask and starting techs. These are player-buyable combat hulls;
no drones, pods or unavailable successor hulls are added. Inputs are raw core
values. Full machine-readable results: [catalogue](P821_assets/catalogue.json).

| Civilization (id) | Hull | Classify | Scanner | Speed | HP | Ammo | Fixed/total guns | Ability bits |
|---|---|---|---:|---:|---:|---:|---:|---|
| Dreghklar (1) | 14 Scout | Scout | 2400 | 160 | 200 | 800 | 1/1 | 0x2092 |
| Dreghklar (1) | 21 Lt Interceptor | Interceptor | 400 | 75 | 600 | 450 | 2/2 | 0x00B2 |
| Technoflux (2) | 51 Scout | Scout | 2400 | 160 | 200 | 600 | 2/2 | 0x2092 |
| Ga'Taraan (3) | 114 Scout | Scout | 2400 | 160 | 200 | 960 | 2/2 | 0x2092 |
| Ga'Taraan (3) | 121 Fighter | Fighter | 600 | 100 | 300 | 600 | 2/2 | 0x2092 |
| Phoenix (4) | 187 Scout | Scout | 2400 | 160 | 200 | 800 | 1/1 | 0x2092 |
| Phoenix (4) | 190 Fighter | Fighter | 600 | 110 | 300 | 600 | 2/2 | 0x2092 |
| Omicron (5) | 710 Scout | Scout | 2400 | 160 | 200 | 960 | 1/1 | 0x2092 |
| Omicron (5) | 715 Fighter | Fighter | 600 | 100 | 300 | 720 | 2/2 | 0x2092 |
| Gigacorp (18) | 210 Scout | Scout | 2400 | 160 | 200 | 800 | 1/1 | 0x2092 |
| Iron Coalition (25) | 410 Scout | Scout | 2400 | 160 | 200 | 960 | 1/1 | 0x2092 |
| Iron Coalition (25) | 415 Lt Interceptor | Interceptor | 400 | 75 | 600 | 540 | 2/2 | 0x00B2 |
| Bios (35) | 310 Scout | Scout | 2400 | 160 | 200 | 800 | 1/1 | 0x2092 |
| Bios (35) | 315 Fighter | Fighter | 600 | 100 | 300 | 600 | 2/2 | 0x2092 |
| Belters (101) | 510 Scout | Scout | 2400 | 160 | 400 | 800 | 1/1 | 0x2092 |
| Belters (101) | 515 Fighter | Fighter | 600 | 100 | 600 | 600 | 2/2 | 0x2092 |
| Rixian (201) | 610 Scout [SR] | Scout | 2400 | 160 | 200 | 800 | 1/1 | 0x3092 |
| Rixian (201) | 615 Fighter | Interceptor | 600 | 110 | 300 | 600 | 3/3 | 0x2092 |

First **legal equipped purchase** per role, with unlimited wallet and each
owned station tested separately (never a merged local mask). Step 0 means
starting tech. The rig civs use the unchanged authored `plan_a`; other civs
use the deterministic generated plan. Different step numbers are different
work, and these serial plans do not predict live elapsed research time.

| Civilization | Plan | Scout | Fighter | Interceptor | Bomber | Heavy |
|---|---|---|---|---|---|---|
| Dreghklar | generated | 0: Scout | 73: Stealth Fighter | 0: Lt Interceptor | 23: Bomber | 83: Corvette |
| Technoflux | generated | 0: Scout | 53: Lt Stl Fighter | 21: Fighter | 19: Bomber | 63: Corvette |
| Ga'Taraan | plan_a | 0: Scout | 0: Fighter | 52: Enh Fighter | 22: Bomber | 95: Corvette |
| Phoenix | generated | 0: Scout | 0: Fighter | 40: Enh Fighter | 21: Bomber | 89: Corvette |
| Omicron | generated | 0: Scout | 0: Fighter | 43: Interceptor | 20: Bomber | 26: Corvette |
| Gigacorp | generated | 0: Scout | 21: Patroller | 51: Enh Fighter | 26: Bomber | 97: Gunship |
| Iron Coalition | plan_a | 0: Scout | 16: Stealth Fighter | 0: Lt Interceptor | 19: Bomber | 71: Corvette |
| Bios | generated | 0: Scout | 0: Fighter | 69: Enh Fighter | 30: Bomber | 32: Gunship |
| Belters | generated | 0: Scout | 0: Fighter | 37: Enh Fighter | 18: Bomber | 71: Corvette |
| Rixian | generated | 0: Scout [SR] | 65: Stealth Fighter | 0: Fighter | 19: Bomber | 71: Attack Carrier [SR] |

At the home dock too, Iron Coalition first buys Fighter at **step 16,
station 113 Tactical**, hull 411 Stealth Fighter; Ga'Taraan first buys
Interceptor at **step 52, development 20 Enh Fighter**, hull 122 Enh Fighter.
Their first home-dock Bombers are steps 19/22, and Heavies 95/178. The full
catalogue records the completing step, dock, and chosen hull for every role.

## Chosen change and fairness

Chosen option: **requested-duty fallback scoring** over every legal small
combat hull, instead of the fixed class-label traversal. An unavailable
Fighter uses hit points × fixed guns, an unavailable Interceptor uses hit
points × speed (and its existing gun-reach preference), and an unavailable
Scout uses scanner × speed and its existing cloak/equipment priorities.
Locked Bomber/Heavy slots temporarily use Fighter duty. Ties retain stable
hull IDs. No civ names or IDs occur in the production decision.

For example, Iron Coalition’s starting Fighter-duty scores are **1,200**
for its Lt Interceptor (600 HP × 2 fixed guns) versus **200** for its Scout.
Ga’Taraan’s missing Interceptor duty scores its Scout at **32,000**
(200 HP × 160 speed) versus its Fighter at **30,000**. These are the same
existing duty equations applied to each side’s legal data; native stat
differences are retained. Varied mode rotates the legal pool rather than
always choosing that deterministic maximum.

Exact-role selections still take priority. Native purchase gates remain the
same: global tech plus the actual dock-local mask, successor obsolescence,
mount fit, energy/ammo, stock capacity and wallet. The P796 varied path now
also rotates its missing-role pool with the existing least-used groups and
requested-duty score weights. This matters: previously every missing-role
purchase bypassed variety entirely. The deterministic best path selects the
highest score; varied mode deliberately covers weaker legal alternatives.
Fairness means the same objective and legality for either civilization in
either slot, not fabricated identical abilities or identical class labels.

Alternatives considered: a relative classifier would relabel existing hulls
and alter exact-role, scout, medevac and coverage semantics across every tech
state. Immediate refitting would add travel and purchase policy and does not
resolve a missing class at start. Ordinary bots already repick the requested
role on a later purchase/relaunch (`RepickRelaunchLoadout`); flying survivors
do not automatically change hull on a tech notification. P745 finale bomber
refits are a separate existing exception and are retained. Research plans,
strike routing and massing thresholds are unchanged.

The defensive mine-duty branch still first buys a mine-carrying Scout;
medevac Scouts keep the rescue launcher and best selector; support keeps
negative-power repair guns; Bombers retain station-damage ranking and the
P814b turret preference; turret mount arming and gunner seating are unchanged.

## Copied telemetry and temporary-role duration

The copied batch 28 audit is reproduced in
[rig-b28-audit.json](P821_assets/rig-b28-audit.json); per-worker role purchase
and timing bounds are in [copied-roles.json](P821_assets/copied-roles.json).
Initial selections are purchase counts, not a later census. Batch 28 reproduces
140 Scout / 0 Fighter / 35 Interceptor / 20 Support for side 0, and
62 Scout / 113 Fighter / 0 Interceptor / 20 Support for side 1. It also
reproduces 11,668.2 versus 3,150.0 ore sold, 0 versus 18 recorded miner kills,
and all 18 victims last logged in Alderan. Those data do not locate surviving
side-0 miners continuously.

Batch 31 repeats the same initial class totals. Across both workers there are
**81 Fighter → Scout** and **36 Interceptor → Fighter** temporary purchase
lines, plus 34 Bomber → Scout and 41 Bomber → Fighter lines (40 on side 1 and
one later Iron Coalition purchase of an unlocked Fighter; these include
later re-purchases, so they are not all initial slots). First exact
Iron Coalition Fighter purchases appear between worker-clock **646–679 s**
on worker 2 and **679–693 s** on worker 1. Ga’Taraan has no observed exact
Interceptor purchase before the copies end at worker clocks 679/693 s; its
last commander status has not reached step 52. This is right-censored evidence,
not proof that the role never upgrades.

In the longer batch 28 worker-1 copy, 14 Iron Coalition research episodes all
have an observed exact Fighter purchase: median first-purchase bracket
**712.5–722 s** from the episode’s first research log (range 633–879 s).
Ga’Taraan has 13 observed exact Interceptor purchases in 14 episodes:
median **1,846–1,847 s**, range 1,664–2,976 s; the unfinished last episode is
censored. These observations confirm that temporary roles do end on
subsequent purchases, but at different research times. Fallbacks can recur
later under the current dock/wallet/obsolescence gates; neither a first exact
purchase nor research completion implies every surviving pilot has changed
hull. The JSON preserves per-episode bounds and the lack of exact timestamps.

## Local before/after and side swap

[Match receipts](P821_assets/local-matches.json) contain per-side initial
classes, later hull/pod samples, ore sales, authoritative miner census, winner
and kills/deaths. **All eight matches finished with zero simulation-pass
failures.** Each phase contains two original-position and two side-swapped
matches, on the real map with ordinary client purchases and native fog.

Size scale 1 is 15 slots per side: 6 Fighter, 3 Interceptor, 2 Scout, 2 Bomber
and 2 Support. Core/Server DLLs are byte-identical before/after;
[binary hashes](P821_assets/binaries.json) identify the archived policies.
The 20 ms ticker advances nominal 50 ms simulation ticks; receipts record
both wall and simulation time. Each deadline is 600 simulation seconds,
with income multiplier 5000, research time scale 8, and the existing P819
proof's both-side finale settings unchanged. P796's usage counters/ordinals
persist within each two-match process, so match 2 inherits its process's
purchase history; the opposite orientation starts a fresh process. Seed 796
and this protocol are the same before/after. These small, short matches are
policy/lifecycle evidence, not independent samples of a 200v200 win probability.

Civs: **25 = Iron Coalition; 3 = Ga'Taraan**. All paired values are side 0 /
side 1. Initial class columns are **Scout/Fighter/Interceptor/Support**;
Bomber/Heavy are initially locked. All 30 initial hull IDs per match were
checked against the first authoritative
server snapshot, with **zero selected/observed mismatches across all eight**.
Support is identified from the negative-power purchase policy (its hull is
otherwise a Scout), and later sample counters identify pods separately from
combat hulls using the native lifepod ability bit. Ore is sold units, not
multiplied income. K:D is the server's per-side kills:deaths counter.

| Match | Civs | Initial side 0 | Initial side 1 | Ore sold | Miners alive at end | Winner side | K:D side 0 / side 1 |
|---|---|---|---|---|---|---|---|
| Before normal-1 | 25 / 3 | 10/0/3/2 | 4/9/0/2 | 67.5 / 0 | 2 / 1 | 1 | 6:7 / 12:2 |
| Before normal-2 | 25 / 3 | 10/0/3/2 | 4/9/0/2 | 67.5 / 90 | 2 / 2 | 1 | 14:4 / 17:9 |
| Before swapped-1 | 3 / 25 | 4/9/0/2 | 10/0/3/2 | 90 / 67.5 | 2 / 2 | 0 | 14:5 / 12:4 |
| Before swapped-2 | 3 / 25 | 4/9/0/2 | 10/0/3/2 | 0 / 67.5 | 1 / 2 | 0 | 14:4 / 10:8 |
| After normal-1 | 25 / 3 | 8/0/5/2 | 7/6/0/2 | 67.5 / 90 | 2 / 2 | 0 | 18:6 / 10:9 |
| After normal-2 | 25 / 3 | 9/0/4/2 | 8/5/0/2 | 67.5 / 90 | 2 / 2 | 0 | 14:2 / 11:7 |
| After swapped-1 | 3 / 25 | 7/6/0/2 | 8/0/5/2 | 90 / 67.5 | 2 / 2 | 0 | 8:3 / 6:4 |
| After swapped-2 | 3 / 25 | 8/5/0/2 | 8/0/5/2 | 90 / 67.5 | 2 / 2 | 1 | 4:4 / 5:2 |

Before, the opening Scout-count gap was six ships in every match. After,
it was 1, 1, 1 and 0. The native class labels remain different (Iron
Coalition's combat hull is initially Interceptor; Ga'Taraan's is Fighter).
The same requested-duty objective and legal-hull variety policy now govern
both. The best-path all-civ test additionally proves that a missing Fighter
chooses the stronger legal interceptor rather than a Scout by label order.

Across four matches per phase, Iron Coalition sold **270 → 270 ore**;
Ga'Taraan sold **180 → 360**. Mean end-of-match miners were **2 → 2** for
Iron Coalition and **1.5 → 2** for Ga'Taraan. Initial-miner deaths were
**0 → 0 / 2 → 0** by civ. This is consistent with changed combat exposure,
not proof that a miner routing rule needed correction.

Win split by civ: **before Iron Coalition 0 / Ga'Taraan 4; after 3 / 1**.
By position: **before side 0 2 / side 1 2; after 3 / 1**. In the fixed
side-swapped pair each civ won once; in the original positions Iron
Coalition won both. The changed split is an observation, not a claim of
balanced win probability. Four short 15v15 games cannot replace the requested
rig follow-up; native stats and battle exposure remain substantial differences.

## Miner finding and native behaviour

The [local map census](P821_assets/map-mining.json) has **nine He3 rocks, all in Alderan**, total initial ore
100,602; neither Ganymede nor Organia has He3. Both miners choose Alderan
at the first mining decision. Routes differ because their homes differ:
Ganymede can reach it through Rigel/Plato, Organia through Juniper/Gaia.
The first observed targets are rocks 3194/3199, selected by position after the
home sector offers no qualifying rock. A local authoritative target census
therefore does not reproduce the premise that only side 1 goes there.

Native `Igc/shipIGC.h:2228-2243` checks damaged miners' local refuge;
`:2250-2327` honors a queued player sector, then finds neutral mineable rocks
with nearest / least-targeted / any-cluster / cowardly flags;
`:2328-2340` searches for a safe friendly unload station and then any friendly
unload station. `Igc/common.cpp:608-613` subtracts other miners' reserved
capacity and rejects rocks with <= quarter capacity available. The port's
`Ship.FindNearestMinableAsteroidIn` applies He3, reservation and distance gates;
`FindNearestMinableAsteroidAnyCluster` searches the home sector first and then
breadth-first reachable sectors. This target selection runs in server drone
AI, not the MCP bot. The home-without-He3 → other-sector mining rule is native
design. The fixed runs still show both sides mining in Alderan; the observed
survival/sale improvement does not establish a different target-selection
law. No bot/preset miner-order defect was found, and no miner/server rule,
core or map is changed.

This does not claim complete equivalence of every port mining search flag:
the port's BFS and its local distance loop do not implement all of native
FindTarget's cowardly/least-targeted ranking. That retained implementation
is outside this bot-only change. The observed availability forces Alderan
regardless of that ranking; it does not justify changing human server rules.

## P704 fixture correction found by the gate

The original 60-peer P704 research test timed out at its captured-station
ownership wait **3/3 on the changed selector and 3/3 on saved `9d78ec6b`
binaries**. Its one-peer and refusal-recovery cases passed. The full initial
launch ACKs were successful. An instrumented run showed side-0 clients with
expansion owner 0 and enemy side-1 clients with the expansion unseen; the
fixture required every client to know an undiscovered enemy station.

Native always reveals owned objects (`FedSrv/FedSrv.CPP:10905-10910`), while
station updates to enemies are visibility-gated (`FedSrv/fsship.cpp:820-845`).
The fixture now waits for ownership on the buying/researched side, matching
its following development-tech check. All 60-peer purchase counts, full ACKs,
real research, actual hull upgrades, death/rescue relaunches, pacing and
intentional-refusal assertions remain. Timeout diagnostics now include the
client side and expansion knowledge. No server/client visibility rule changed.
This deterministic fixture error is recorded in `docs/TEST_FLAKES.md` as a
corrected fixture, not relabeled as a load flake. The corrected group passed
**3/3 repetitions, all nine cases**, including
three full 60-peer upgrade/relaunch runs. The original failures and baseline
reproductions remain in the verification receipts.

## Retained differences

Hull labels still differ by civ: a missing Fighter can fly an Interceptor
hull, and a missing Interceptor can fly a Fighter. The patch aligns purchase
objectives, not native ship stats. Sensor reach, fixed mounts, speed, hit
points, rescue-probe access, obsolescence and local station gates still come
from core data. The local initial miner capacities are 67.5 / 90 ore for Iron
Coalition / Ga’Taraan; native capacity is the shared He3 constant multiplied
by the side’s mining-capacity attribute (`Igc/shipIGC.h:2248-2249`). Different
capacity, travel geometry and combat exposure can therefore produce unequal
sales even when both miners target Alderan. Both home positions, all research
orders and the P819 finale routes remain as dispatched.

## Native design / era constraint / bug classification

| Behaviour examined | Classification | Treatment |
|---|---|---|
| Ability bits rather than Scout/Interceptor classes (`Igc/igc.h:725-741`) | Design | Read core capabilities; bot role labels remain policy. |
| Per-station/global-tech purchase, mount and budget constraints (`Igc/stationIGC.h:383-433`, `Igc/hullTypeIGC.h:163-213`, `WinTrek/load.cpp:1603-1606`) | Design | Retain existing selectors and legality checks. |
| Missing-role fixed label order | Port bot policy bug | Replace with requested-duty scoring; there is no native role order to port. CSV records it as native N/A. |
| Nearest eligible mining target in another sector when home has none (`Igc/shipIGC.h:2286-2327`) | Design | Diagnose only; leave server drone AI intact. |
| Own-object knowledge / enemy station visibility (`FedSrv/FedSrv.CPP:10905-10910`, `FedSrv/fsship.cpp:820-845`) | Design | Correct the P704 fixture ownership wait; keep production visibility unchanged. |
| Raw ability masks and dock-local tech masks | Era representation constraint | Decode as data; do not create synthetic Scout/Interceptor bits or bypass local masks. |

## Modern approach review

A modern data-driven duty selector is preferable to civilization-specific
exceptions. This patch reuses the existing transparent role scores, legality
and seeded coverage rotation, so the choice can be inspected and replayed.
A future improvement could normalize several combat metrics and rank full
loadouts (effective DPS, acceleration, survivability and sensing) against
explicit duties; that needs tuning and separate balance evidence rather than
unreviewed weights here. Reclassifying hulls at each unlock risks role churn.
Automatically refitting every survivor changes battle exposure and economics.
For mining, a bot commander could issue risk-aware escort/mining orders, but
changing the shared autonomous drone law would affect humans and is outside
the authorized scope. The native availability rule is not treated as a bug
merely because both fleets fight over the only He3 sector.

## Verification method

The early pure tests, SC4/fallback-targeting runs and five baseline parking
runs completed before a launcher interruption left child runners active.
Those later overlapping receipts are retained as extra evidence and excluded
from the final isolation gate. A process-owned sequential runner repeats the
affected live groups and fixed parking with task-local `TMPDIR`; its stop
marker terminates its own child process group. Temporary launch blocking was
removed and the original build targets restored before this final sequence.
Blocked legacy launch attempts ran no test cases; only one completed fast-tier
suite is counted. The final verification receipt identifies each result root.



[Full verification receipt](P821_assets/verification.json); [working-tree hygiene](P821_assets/hygiene.json).
All commands used the explicit installed .NET CLI (`~/.dotnet/dotnet`, or
`DOTNET` set to that same executable for repository scripts).

| Check | Result |
|---|---|
| Solution build | 0 warnings / 0 errors; final source also built by the fast runner |
| Tool builds | All 83 projects; final census tool rebuild 0 warnings / 0 errors |
| New P821 tests | 3/3 final-source repetitions, four cases each; all ten civs covered |
| Pure affected regressions | 45/45 filtered invocations; 309 test executions, zero failures |
| `BotFixRegressionTests.P704_` | 3/3 filtered repetitions; 6 cases |
| `SC4ShowcaseLiveTests` | 3/3 filtered repetitions; 3 cases |
| `P704TechLoadoutLiveTests` | 3/3 filtered repetitions; 9 cases |
| `P796CoverageLiveTests` | 3/3 filtered repetitions; 3 cases |
| `P779b1MedevacLiveTests` | 3/3 filtered repetitions; 3 cases |
| `P801StrikeMassLiveTests` | 3/3 filtered repetitions; 3 cases |
| `P801StrikeLoopbackLiveTests` | 3/3 filtered repetitions; 3 cases |
| `P814GunnerLiveTests` | 3/3 filtered repetitions; 3 cases |
| `P745FinaleSiegeLiveTests` | 1/3 filtered repetitions; 3 cases; known master deadline/no-station failure |
| Baseline never-parks | 5/5; longest observed low-speed-with-target interval 2.205s |
| Fixed never-parks | 5/5; longest observed low-speed-with-target interval 2.021s |
| Fast tier | One completed run: 10,662 passed, 7 failed, 3 skipped; no timing offenders. Seven failures individually accounted for below |
| Fast failure follow-ups | All three groups pass 3/3 in isolation: allocation method, profile method, nine-case bundle-path class (33 test executions) |
| Repository checks | Glossary links, parity validation, LF CSV, `git diff --check`, working-tree public hygiene: PASS |

P745 remains the pre-existing exception explicitly identified in the brief.
Repetition 1: PASS, station destroyed=True, stage=Over, reason=The clock has stopped. The winner: Alpha by kills.
Repetition 2: FAIL, station destroyed=False, stage=Over, reason=The clock has stopped. The winner: Alpha by kills.
Repetition 3: FAIL, station destroyed=False, stage=Over, reason=The clock has stopped. The winner: Alpha by kills.
The assertion and production finale/research/routing rules were retained.
The controlled P801 massing and massed-versus-trickle tests passed 3/3;
no waiver or artificial station kill was introduced. See `docs/TEST_FLAKES.md`.

The single fast sweep was **not green**. Five failing bundle-path cases
inherited this worktree's `local.env` through task-local `TMPDIR` ancestors;
the nine-case class passed 3/3 using an open-directory `/proc` alias to that
same workspace scratch. The profile-editor method hit the sandbox's read-only
user configuration directory and passed 3/3 with task-local `XDG_CONFIG_HOME`.
Both environment patterns are already recorded by P819/P817 in the flake
ledger. The unrelated P804 zero-allocation method observed 1,800 bytes under
the full suite and passed 3/3 unchanged in isolation. Its allocation source
is unknown; this meets the lane's three-isolated-passes flake rule. No UI,
profile, path resolver, allocation assertion or timing budget was changed.
The original fast summary remains `passed: false`; no second sweep was run.

## Reproduction and evidence

```sh
~/.dotnet/dotnet run --project tools/p819-side-bias-proof -- --catalogue --output .scratch/p821-census
~/.dotnet/dotnet run --project tools/p819-side-bias-proof -- --output .scratch/p821-repro --matches 2 --size-scale 1 --game-seconds 600 --seconds 720
# Repeat the match command with --swap and a separate output directory.
python3 tools/p819-side-bias-proof/p821-summary.py .scratch/p821-repro/*-result.json --hull-roles .scratch/p821-census/hull-roles.json --output .scratch/p821-repro/summary.json
```

Baseline bot and test binaries were archived before changing the selector.
Baseline proof runs use that bot DLL with the same verbose purchase/mining
instrumentation. Raw local logs, receipts, geometries, archived binaries,
isolated TRX and build logs are retained under `.scratch/p821/` in this
worktree; copied rig evidence remains in `.scratch/rig-b28/`,
`.scratch/rig-b30/`, `.scratch/rig-b31/`. These are git-ignored. Only compact
JSON evidence under `docs/reports/P821_assets/` is part of this change.

## Recipe for Claude on 221

Run two 200v200 showcase matches from the final working-tree version, preserving
the batch 30/31 server/core/map, `plan_a` for both sides, native fog and existing
showcase settings. Use the existing exact-count options:

```json
{"preset":"showcase","target_sector":"Alderan","count_a":200,"count_b":200,"mode":"march","loadout_mode":"varied","staging":true}
```

Confirm **200 spawned per side** in the result (and allow room for research
commanders in the server team cap); `size_scale:13` is only 195 per side, as
batch 31 demonstrated. Run Iron Coalition /
Ga'Taraan once and exchange civ positions for the second match. Do not change
income, research order, strikes or miner rules between runs.

Capture spawn receipts, each worker's full loadout and research logs, and the
server mine-sell/combat/game-over logs. Resolve **actual hull ID → Classify**;
`role=...` in the new fallback reason is the scoring duty, not the hull class.
The existing `tools/p819-side-bias-proof/audit.py` now reads the actual
`temporary ...` label when auditing new fallback lines; a three-run smoke
fixture verifies requested Fighter / actual Interceptor.
At spawn and each minute record per-side actual hull classes (separate support
and pods), ore sold (units, not multiplied credit income), authoritative miners
alive and miner target/sector. At each deadline record winner/reason and
kills/deaths; compare with batch 30's side-0 2/3 wins and batch 31's role
fallback counts. Report by position and by civilization; two matches alone
cannot establish a balanced win probability. All remote execution is left to
Claude; this lane has not contacted the rig or Mac.
