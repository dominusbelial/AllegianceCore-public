# P927 — opt-in N-side research showcase

Changes remain in this working tree. No commits, pushes, remote connections,
rig runs or rig configuration changes were made. `local.env` is present and
resolves the external artwork/native corpus. Raw evidence is retained under
`.scratch/p927/` in this worktree; compact evidence is in [verification.json](P927_assets/verification.json).

## Native multi-team audit

All citations below were re-read from the local read-only native checkout and
line numbers checked with `rg`/`sed`. No native server win rule needed changing.
The server and Core implementation sources are untouched.

| Native rule | Port comparison | Verdict |
|---|---|---|
| `FedSrv/fsmission.cpp:3568-3630`: count flag-capable stations across every side; integer conquest percentage; Started/Starting+AET gates; suppress the lone-active-side conquest win with AET. | `Mission.RecomputeConquestAndTerritory` and `Server.OnVictoryByStationsComputed`; existing SIM-0847 and the station-victory inventory. | Parity for the audited multi-team gates; the broader native body retains its existing matrix qualifications. |
| `FedSrv/fsmission.cpp:3633-3695`: territory considers each sector and side, and broadcasts six per-side ranking arrays. | Shared mission recomputation and `GameState.ForMission`; already loops all sides. | Parity for N-side iteration. |
| `FedSrv/fsmission.cpp:3698-3716`: losing the last restart station deactivates the former owner, even when other teams still fight. | `Server.Lobby.cs` `OnSideLostRestartStation`/`DeactivateSide`; strips ships, kills drones/players, marks inactive and broadcasts SIDE_INACTIVE. | Parity; no two-side winner substitution. |
| `FedSrv/fsmission.cpp:3749-3763`: the credited side reaches the deathmatch team-kill goal. | Core team counters plus `Server.OnFlagsKillsComputed`; existing three-side live-win coverage. | Parity for N-side victory selection. |
| `FedSrv/fsmission.cpp:3767-3857`: count active sides with players, allow-empty-team starting guard, zero sides draw, one surviving side wins; active-allied override. | `CheckForVictoryByInactiveSides`, `CheckForVictoryByInactiveSidesAfterDeactivation`, `AllAlliedWinner`. Existing unattended-arena policy uses Active alone after station elimination, as already documented in the port. | Multi-team parity; unattended zero-player arena is an existing port policy, unchanged. |
| `FedSrv/fsmission.cpp:2691-2711`: save winner or null/draw; deferred processing broadcasts results. `:5318-5426`: deactivation disarms, destroys/reset ships, announces the side, then evaluates remaining sides. | `DeclareGameOver`, native GAME_OVER and automatic fresh-mission rotation; side activity persists until rebuilding. | Multi-team parity for winner/activity; the port rebuilds rather than resetting the same native world in place. |
| `FedSrv/fsmission.cpp:5288-5308,5639-5807`: six activity/alliance slots; validate/merge groups, remove inactive members, require at least three active teams, reject a sole all-team group at alliance creation, broadcast CHANGE_ALLIANCES. | Existing `Server.UpdateAlliances`, Side.AllyGroup and native messages. The port's existing owner authorization is stricter than native. | Already supported with documented authorization divergence; report only, no alliance implementation changed. |
| `FedSrv/fsmission.cpp:3719-3743,3823-3857`: if all remaining active sides are allied, they win together; the returned representative is one side, not summed alliance kill/conquest totals. | `AllAlliedWinner`, allied winner reasons and result processing already recognize allies. | Already supported; no new alliance showcase configuration/proof is claimed. |
| `Igc/igc.h:1126-1127,1242-1243`; `FedSrv/FedSrv.CPP:10728,10789,10839-10913`; `Igc/shipIGC.cpp:3807-3852`: optional allied visualization and ripcord, default off; same/allied target relationships. | Existing allied visibility, damage/friendliness and ripcord paths. Research buckets and wallets remain per side. No faction-wide shared research/economy was found in the audited alliance update body. | Existing options, not automatic shared tech or money; no new work. |
| `FedSrv/fsmission.cpp:3178-3213,3238-3239,3274-3275`: rank ratio stays 1.0 with more than two mission sides; allied players can receive win credit. | Allied win/loss bookkeeping exists. `Server.FinalizeMissionScores` counts occupied sides for its ratio guard, rather than every mission side (`Server.Lobby.cs:1306-1337`). | Parity for the populated three-team proof; existing scoring gap if a three-team mission has only two occupied sides. It cannot alter the winner or prevent game over, so is left outside this task's server-fix scope. |
| `FedSrv/fsmission.cpp:2348-2504`: countdown criteria examine all sides, try the next criterion on ties, finally kills then fewer deaths, with a draw on equal kills/deaths. | `Mission.EvaluateCountdown` and `CountdownValues` already iterate the complete side list. The optional P914 station-kill criterion precedes ship kills in preset scope. | Native countdown parity plus the explicitly authored station-kill extension; certified for three sides here. |

Matrix inventory is unchanged; no server gap blocking the three-faction run was
found. The new `docs/parity/p927-showcase-policy.csv` documents orchestration
without claiming native AI parity or increasing parity credit.

## API and compatibility

`spawn_scenario` now accepts:

```json
{"preset":"showcase","sides":[{"count":10},{"count":10},{"count":10}],"staging":true,"finale_side":"all"}
```

The list must contain 2–6 entries and equal the **actual connected mission's**
roster length. Entries follow roster order. `count` is an integer 1–200.
Optional `fleet` is the existing `{hull,count,tech?}` array; its total must equal
`count`. Optional side-level `tech` must be a nonnegative integer and is ignored,
like the legacy tech fields: real technology and dock availability decide every
purchase. Mixed `sides` plus legacy size/count/fleet/tech arguments are rejected
before spawning. A missing preset and fleet uses the ordinary requested/default
fighter role; showcase composes its existing mixed roles separately for every
side.

Plans and merged results expose a `sides` list of side/count rows. Actual grants
and failures retain their side ids. Explicit two-side requests additionally
retain `sideA`/`sideB`; with `sides` absent, existing plan/result JSON is unchanged,
including property order and text. Recovery keys remain `a:N` and `b:N` for the
first two positions and add `s2:N`…`s5:N`. Partitioning remains independently
round-robin per side, including synthesized plans for a missing worker.

The internal owned driver accepts 2–6 slash-separated counts, e.g.
`--skirmish 10/10/10`; its two-count request and player-seat behavior stay on the
old path. No single-player chooser, lobby UI or human commander ownership was
expanded. The operator reference and complete configuration are in
[GUIDE.md](../GUIDE.md#three-faction-showcase).

## Enemy selection and finale

The three-plus-side strategic rule is:

1. Consider reachable, active, non-allied enemies that the requesting side has
   observed. Use its own restart home as origin and the public CLUSTERINFO hop
   graph; faction distance is the minimum distance to its observed stations.
2. Prefer fewer hops, then more observed player kills, then more observed
   stations, then lower side id. Strength is knowledge available on the client,
   not hidden authoritative server counters.
3. Latch the choice for a wave. Re-evaluate at a wave boundary/finale commitment;
   drop inactive/unobserved targets immediately. Targetless sides retry on fresh
   sightings. Scout recon/home intent stays latched until elimination; frontier
   routing still explores all live threats. Aleph duty makes a new commitment
   at its existing ten-second resolution interval.

Native wingmen search visible hostile ships and optionally choose nearest by
local distance (`Igc/shipIGC.cpp:2030-2055`); shared target checks exclude allies
(`:2422`). Native route search requires visible alephs
(`Igc/common.cpp:920,987`). The reference Wopr commander chooses the first home
cluster other than its own, without ranking factions
(`Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:763-769`); its TeamDirector
combat operations end at a constructor-target TODO
(`Wopr/Wopr/TeamDirector.cs:174-190`). These are grounding, **not** a native
multi-enemy faction-ranking algorithm. The audited native AI/commander paths do
not implement the proposed hop/leader preference. The new ranking is authored showcase policy, and public
hop reachability never grants a ship an unseen route or a station export.

Strike waves filter station objectives to the chosen faction while defenses
still observe every living hostile. Finale targets retain the existing
restart-station/lowest-station-id preference **within** the chosen faction.
Each side's stations come only from that side's own bot worlds. Destroyed or
eliminated targets can be replaced by another living enemy; a new massed wave
can select again. Aleph duties use their own side's station/warp observations.
Scout intel prunes eliminated factions from station/ship/miner threats.

Home flags do not encode ownership: when none of a side's enemies has been
observed, recon may visit the nearest anonymous other home, tie by sector id.
It does not assign that home to faction 1 or 2. Shared fighter rendezvous keeps
P926's minimax over **all** flagged homes; an explicit target remains an operator
override. Two-team home inference and enemy choices retain the old path.

`finale_side` adds `all` and actual integer ids 0–N−1; `both` is the two-team
alias and is rejected for larger missions. `all` selects every living faction;
`leader` uses the existing kill/side-id ranking. Maturity gates are unchanged
and apply to living participants. For N-side fog, initial and post-rotation registration may use
empty plans so actual combat can trigger the bounded server deadline before
all sides discover an enemy base. A blind side receives no strike target and
never receives an opponent's private sightings. Two-side pending registration and post-rotation fog grace remain unchanged.

No new server deadline algorithm was necessary. Native goal precedence remains,
then the explicitly enabled P914 station-kill criterion, then kills/fewer deaths.
A tie falls through across **all** sides, not just tied station-kill leaders.
Inactive teams are excluded from bot target selection, while native countdown
continues to examine all side scores, just as native does. Receipts already
include `side2=...` and every numeric side id in finale `values`; no two-side
receipt format changed.

## Two-side assumption audit

| Area | Disposition |
|---|---|
| MCP modern showcase composition, slots/routes, bomber objective, worker summaries | Generalized on explicit `sides`; legacy arguments retain the previous shape and ordering. |
| Worker synthesized plans, failed-worker counts, desired-fleet recovery | Per-side rows, stable third-through-sixth slot prefixes; connected counts come from final worker inventory. |
| Finale validation, readiness, registration, target/faction choice and wave renewal | Generalized, with inactive-side filtering and targetless initial fog registration only for N-side missions. |
| Research autonomous commander | Already loops every valid side; now skips eliminated N-side factions. HumanCommands still belongs only to side 0. |
| Research tactical targets | Owner side tags enable faction filtering; living hostile ships remain available for ordinary defense. |
| Scouts / SideIntel | Every hostile already carries its side id. Eliminated threats are pruned; selected enemy home is explicit, rather than the first station forever. Anonymous-home recon is scoped to N-side missions, including multi-home two-team compatibility. |
| Aleph field and minefields | N-side resolution uses nearest living observed enemy; all/numeric minefield participation, with no hidden-home fallback. Legacy two-side field geometry is retained. |
| Shared staging / rendezvous / defense / support | Side-keyed dictionaries already cover N; P926 rendezvous includes all homes. Support follows its own side's anchor. No balance/distance/hold retuning. |
| Core victory, deactivation, GAME_OVER, deadline, matchup receipts | Already N-side; certified and left unchanged. |
| P924 homeSwap | Still rejects anything other than two teams. |
| `run_scenario` bomber/capture/elimination choreography and `run_hull_matrix` | Remain explicitly pairwise verification tools; they are not the persistent showcase driver. |
| Owned skirmish chooser, HUD and human commanders | Existing two-team UI/side-0 human commander scope retained; internal driver can consume N counts. |
| Legacy per-side time-to-kill telemetry | `get_perf_stats.combat.timeToKill` still exposes sideA/sideB only, and the perf merger retains that schema. Total combat counts, per-side finale plans, N spawn results and authoritative N matchup/tally data are complete. Extending this optional historical perf view is left as a telemetry follow-up; it does not drive strategy, scoring or rotation. |
| Performance, transport and rig config | No changes; prescribed pool/native/parallel immutable baseline used. |

## Generators

The existing generator validates 2–6 teams. With three teams, all 15 generated
topologies except **EastWest** are accepted: SingleRing, DoubleRing, BigRing,
HiLo, HiHigher, LargeSplit, InsideOut, Star, PinWheel, DiamondRing, SnowFlake,
Grid, SplitBase and Brawl. DiamondRing requires at least three. The constraints
are `MissionMapGenerator.ValidateTopology`; native
`Igc/mapmakerigc.cpp:2416-2427` restricts EastWest to two. Generated entries still
require homeSwap=off. The loopback proof rotates DiamondRing / SingleRing /
DiamondRing into a fresh fourth SingleRing mission.

## Validation and local proof

Evidence: [verification.json](P927_assets/verification.json). **Skipped: zero**
in every focused, worker, generator, deadline and three-faction proof execution;
**three in Fast**, all existing optional private Meshy-pack cases. No native
artwork-dependent test was skipped.

| Gate | Result |
|---|---|
| Solution build | **PASS**, final `dotnet build AllegianceCore.slnx`, 0 warnings / 0 errors. |
| All tools build | **PASS**, all 115 projects, 0 warnings / 0 errors. |
| Focused certification | **63/63 × 3 PASS**, zero skips: 58 client policy/regression/generator cases, four actual-roster/real-worker cases, one three-side deadline case. Includes legacy JSON and receipt pins, 2/3/6-side plans, invalid inputs, nearest/strength/neutral/allied/inactive/unreachable choices, committed target latching, targetless fog, elimination retargeting and finale all/numeric selection. |
| Python tally | **5/5 × 3 PASS**, including three-faction civ/home/map wins and honest partial placement. |
| Fogged rotating research showcase | **3/3 PASS**; nine completed matches, nine game-over rotations, a fresh fourth match in each proof, zero simulation-stage failures. |
| Two-side P700 never-parks, unchanged | **QUALIFIED FAIL**, 4/5 required attempts; attempt 5 exceeded the parking bound. Subsequent filtered repetitions pass 3/3; the required gate remains failed. |
| Exactly one Fast sweep | **QUALIFIED FAIL**: 13,055 passed, eight failed, three skipped; five timing overruns against 10.000 s. Infrastructure 9/9 passed. No new focused test failed. |
| Glossary / parity / Compose / hygiene | **PASS**: 544 glossary paths valid; scorecard before/after byte-identical; `docker compose config --quiet`; LF policy CSV; clean whitespace; no private home paths in changed code or new media. |

Server Native AOT and the training cohort are not applicable: no Server/Core
runtime or training code changed. The Fast exceptions and their three filtered
repetitions are retained in [TEST_FLAKES.md](../TEST_FLAKES.md#p927-validation--one-fast-sweep-and-filtered-isolations-2026-11-04).
Five bundle-path cases and the existing wingman assertion still fail 3/3; miner
and draw-ballot failures pass 3/3 in isolation. Four timing cases improve below
the guard; P777 remains functionally passing but over 10 s in all three isolated
samples. No failed gate is presented as green, and no second Fast sweep ran.
The last N-only fog/re-arm/neutral guards were checked by the focused threefold
certification and Live proofs rather than another broad sweep.

The loopback proof uses **10/10/10**, civs **18/4/5**, showMap=false,
showHomeSector=true, research income ×5000 / time ×16, staging and small-fleet
meeting engagement, `loadout_mode=best`, finale all after 0.5 minutes with
relaunch share 0 / recent-kill floor 1 / three bombers, and a 120-simulation-second
finale timeout. No goals override the finale. Test-harness wallets/tech grants
are disabled; research, ships, purchases, hits and rotation are ordinary live
server/client actions. The existing fixture disables initial miners at boot;
commanders still fund their plans. The preserved baseline is server GC,
immutableSnapshot/encodeOnce, parallelBuild, pool transport, native ship updates
and nativeGotoPlan. The ticker sleeps 20 ms after each simulation tick; these
wall times are functional evidence, not rig throughput or balance measurements.

Re-run one proof with `DOTNET_gcServer=1 ~/.dotnet/dotnet test
tests/Allegiance.ClientCore.Tests --no-build --filter
FullyQualifiedName~P927MultiSideLiveTests` after building with local artwork.

Every row below ended by the **deadline's ship-kill criterion**; all three sides
remained active, so **no station elimination is claimed**. Each side completed
real research (1 / 7 / 7 developments in side order in every match), and every
side landed real projectile hits on at least one other side. `a↔b` means hits
in both directions; an arrow is the single measured direction. K/D tuples use
side order 0=Gigacorp, 1=Phoenix, 2=Omicron. Each winner was checked against the
final scores and the authoritative GAME_OVER receipt, including all side ids.

| Proof.match | Map / layout seed | Wall s | Fought whom | K/D: side 0, 1, 2 | Winner |
|---|---|---:|---|---|---:|
| 1.1 | DiamondRing / 31158964 | 93.2 | 0↔1, 0↔2, 1↔2 | 9/10, 6/4, 11/0 | 2 |
| 1.2 | SingleRing / 1753171001 | 85.0 | 0↔1, 0↔2, 1↔2 | 9/8, 14/7, 5/1 | 1 |
| 1.3 | DiamondRing / 1567426543 | 94.4 | 0↔1, 0↔2, 1↔2 | 4/11, 9/4, 13/1 | 2 |
| 2.1 | DiamondRing / 31158964 | 92.9 | 0↔1, 0↔2, 1↔2 | 10/6, 11/2, 6/3 | 1 |
| 2.2 | SingleRing / 1753171001 | 84.7 | 0↔1, 0↔2, 2→1 | 5/9, 15/3, 2/1 | 1 |
| 2.3 | DiamondRing / 1567426543 | 96.2 | 0↔1, 0↔2, 1↔2 | 6/2, 8/7, 14/1 | 2 |
| 3.1 | DiamondRing / 31158964 | 91.7 | 0↔1, 0↔2, 1↔2 | 5/7, 11/3, 9/3 | 1 |
| 3.2 | SingleRing / 1753171001 | 84.6 | 0↔1, 0↔2, 1↔2 | 7/14, 20/9, 5/0 | 1 |
| 3.3 | DiamondRing / 1567426543 | 90.9 | 0↔1, 0↔2, 1↔2 | 5/5, 10/4, 11/1 | 2 |

The tally contains nine completed results: DiamondRing six (Omicron four,
Phoenix two) and SingleRing three (Phoenix three). Losing places are explicitly
unranked. Replayable map seeds stay the same across the three independent proof
runs; combat outcomes and timing vary. Exact directed hit counts, research
counts, scores, receipts and per-map tallies are in the JSON evidence.

The initial diagnostic stalled for 540 s while combat continued because one
side had not discovered a base; N-side targetless initial/post-rotation finale
registration fixes that controller gate. A subsequent receipt read caught the
middle of the game-over tick; the fixture now joins that tick before checking
its receipt. Another diagnostic required all three sides to reach the same
neutral sector, which is stronger than this task and incompatible with a
legitimate separate-front engagement: the new proof instead requires each
side's real hits, research and correct game over. These diagnostics are not
counted as successful proofs. No existing P700 assertion, deadline, movement law
or two-side policy was weakened.

Two-side results (the original single-method P700 filter, serial invocations):

| Attempt | Outcome | Observation s | Arrived / 30 | Longest idle with target s |
|---|---|---:|---:|---:|
| 1 | Passed | 212.1 | 24 | 2.005 |
| 2 | Passed | 239.4 | 24 | 1.905 |
| 3 | Passed | 372.3 | 24 | 0.801 |
| 4 | Passed | 249.8 | 25 | 0.0 |
| 5 | Failed | not reported | not reported | 5.01 |
| 6 | Passed | 262.1 | 24 | 0.0 |
| 7 | Passed | 242.9 | 24 | 1.914 |
| 8 | Passed | 471.3 | 24 | 1.904 |

Attempt 5 stopped at the real >5-second parking assertion: a side-0 bomber-role
bot on a light interceptor in sector 1051 held target 8117 with zero velocity.
Its trace reaches 5.01 s, with live AI decisions and throttle −1; this is not
merely an arrival-limit miss. Attempts 6–8 are three subsequent filtered
repetitions, all passing, and do not replace the required five-attempt result.
The exact parking cause remains unestablished; the existing stopped-target
signature and these isolations are recorded in the
[parking ledger](../TEST_FLAKES.md#p927-validation--two-side-parking-gate-2026-11-04).

The dispatch baseline already records 4/5 full P926 passes with 5/5 parking
bounds held, and a separate swapped-map arrival-limit qualification. These runs
use the same assertions and prescribed GC/transport/simulation settings; there
is no fresh pre-change five-run A/B or rig performance claim.

## Rig pilot recommendation and remaining limits

After the current two-side P926 trial ends, use the GUIDE configuration with
civs 18/4/5, fog on, generated DiamondRing/SingleRing cycle and a recorded seed.
Start at **128/128/128**, `maxPlayersPerTeam=160`, four workers: 384 fleet clients
plus four probes and three commander clients = **391**, leaving 121 of the
512-player ceiling. Native NPC drones do not open player TCP sessions. The corresponding rig spawn
request is:

```json
{"preset":"showcase","sides":[{"count":128},{"count":128},{"count":128}],"staging":true,"finale_side":"all"}
```

Preserve
server GC, immutableSnapshot, encodeOnce, parallelBuild, pool transport, native
ship updates, nativeGotoPlan and the existing clash tick budget. Keep existing
optional routing/broadcast/receive switches at their established rig values.
No rig throughput or balance result is inferred from local 10/10/10 proof.

Alliances are audited but not exercised as a showcase feature. Strength under
fog is observed strength; it cannot count unseen stations or every NPC kill.
Disconnected/unseen enemy bases can leave a side without a strike objective;
the maturity-triggered deadline still bounds combat once its gates are met.
As before, no-combat configurations that never meet the kill gate do not trigger
a finale automatically. Historical logs do not carry a complete elimination
order: the tally records a known winner prefix and leaves losing places
explicitly unranked. There are no new services, ports, environment variables or
media assets.

## Native intent

Classification: **design extension**. Multi-team server victory/activity already
existed in native and in the port; the missing feature was showcase orchestration
and client-observed strategic choice. The fog-registration stall is a bot
controller bug exposed by N-side use, not a native simulation bug or an era
constraint. The modern approach is an explicit per-side API, deterministic
observation-based strategic commitments, stable recovery identities, and
replayable per-map receipts while preserving the two-side production path.
