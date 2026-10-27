# P832e — conquest bots

Work is on `codex/p832e`, based on dispatch master
`47d1ac8a0040589dc15eecc20cedfe57e15fff14`. Changes are left in the working
tree; no commits or pushes. No remote host was contacted.

## Result and implementation

Conquest is now an objective selected from each pilot's received
`MissionInfo.Goals`, including ordinary showcase/skirmish fleets. The new
`conquest` preset rejects a server without that goal and disables the timed
showcase finale. The actual conquest rule decides the match.

`BotManager.Conquest.cs` extends the existing `PlanFlagDuties` planner and
`FlagCandidate`/`FlagDeployment` worker messages. It does not introduce another
budget. Stations come exclusively from each observing client's station exports;
percentages come from its GAME_STATE. Hidden station coordinates and server
mission objects are never inputs to bot decisions. Native CLUSTERINFO home
sectors guide reconnaissance until a real station is discovered. Travel retains
the existing discovered-mouth sweep and warp controller.

A side chooses an observed enemy/neutral **flag-capable** station, favoring a
station an available boarding hull can enter, then distance and station ID.
One conquest fleet slot per side prefers a legally buyable boarding hull;
locked/unaffordable boarding equipment falls back to ordinary fighter selection
and is reconsidered on later purchases. The boarding selector retains research,
local station tech, obsolescence, mounts and wallet checks. Gunless troop
transports are valid boarding purchases. The loopback proof received complete
BUY_LOADOUT_ACKs for the real troop transports.

Strike pilots reuse the existing legal finale refit/resupply flow and
`EngageShipAt(... stationTarget: true)` bomber firing/lead/range/missile law.
The small strike context is never registered as a timed finale and sends no
finale deadline request. Small/custom fleets without a bomber reserve a pilot
for that same legal refit. Escorts use the existing P778
`ShowcaseFinale.EscortTarget` screen and leash. Boarders wait outside the base
until a received station update reports shields strictly below the loaded
`c_fcidDownedShield`; they then use the existing landing-bay approach. Strike
fire pauses below that threshold while the assigned boarder remains alive;
otherwise the strike kills the station. There is no direct damage, teleport,
forced death, capture command or purchase bypass in this controller.

The planner refreshes once a second. Dead/podded pilots are replaced; ownership,
destruction and newly constructed/exported stations change the next target.
A below-goal capture relaunches its docked boarder even if the planner already
cleared the previous duty. Releasing a refitted strike restores its ordinary
role and route while retaining the actual granted hardware for aiming and firing. When reported own conquest is within one observed station
percentage step (or 20 points, whichever is larger) of the goal, a received station attack warning/recent hit or a fresh
visible enemy within 2,500 units recalls nearby free fighters. This makes a two-base 100% game defend already at 50%, when one
station decides the match. The step uses only distinct observed station IDs;
fog conservatively recalls earlier, without exposing hidden stations. Boarding hulls
are preserved where other defenders exist. Guards return to the station and
fight within its bounded defensive volume; retired/hidden enemy poses are
rejected.

### Shared allocation

The existing `FlagShares` allocation still supplies **all** objective duties.
Its denominator is eligible living combat pilots; support, pods and explicit
human orders retain their existing exclusions.

| Eligible population / goals | Offensive duties | Defensive duties |
|---|---:|---:|
| 200, conquest | at most 8 board/strike/escort/recon | at most 12 threatened-station guards |
| 200, conquest + flags/artifacts | conquest at most 4; cargo receives the unused portion of 8 | conquest at most 6; cargo denial receives the unused portion of 12 |
| No conquest | dispatch-master flag/artifact planner | dispatch-master guard/intercept allocation |

At 200 eligible pilots, at least 180 normally remain available for regular
clash/support behaviour; unused defensive slots remain there too. Physical
cargo returns keep the existing mandatory-return priority. Flags and artifacts
already share one cargo slot and one allocation. Candidates are deduplicated
by mission cookie, side and name, and assignment removes a pilot from the pool.
The supervisor calculates the same quota over all workers, rather than per
worker. Conquest assignments also exclude those pilots from ordinary defense
recalls. Unassigned conquest bombers follow the normal clash objective instead
of independently attacking an unbudgeted station.

## Native contracts checked and parity corrections

All native citations below were checked against the local read-only source.

| Contract | Native source |
|---|---|
| Only `c_sabmFlag` base types count; `100 * owned / total` uses integer division; changed percentages win at `>=` the configured goal; killed station excluded | `FedSrv/fsmission.cpp:3581-3625` |
| Recompute at Started, or Starting with `bAllowEmptyTeams`; one active team suppresses a conquest winner when empty teams are allowed | `FedSrv/fsmission.cpp:3575-3576`, `3628-3630` |
| Capture/build call the same station tally; capture counts the newly re-sided station | `FedSrv/fsmission.cpp:3558-3566`; `FedSrv/fsship.cpp:686` |
| Fighter/other landing-bay capability, boarding hull, non-invulnerable station, shields strictly below downed threshold and `InGarage`; no station hull-fraction threshold | `Igc/shipIGC.cpp:725-729`, `849-861`; constant slot 5: `Igc/igc.h:64` |
| Hostile boarding dispatch and allied/friendly-fire condition | `FedSrv/FedSrv.CPP:13087-13104` |
| Capture halves armor and sets shields to seven times regen/max-shield; preserves station name/type while transferring side/scanners | `FedSrv/fsship.cpp:579-596`, `631`; `Igc/stationIGC.h:253-282` |
| After a non-winning check, vacate only an active old side; real pilots undock and ghosts move to another restart base; captor docks afterwards | `FedSrv/fsship.cpp:686-695`; `FedSrv/fsmission.cpp:3698-3715`, `4783-4850`; `FedSrv/FedSrv.CPP:13107-13110` |
| Native capture, destruction, unattributed/allied domination and construction victory text | `FedSrv/fsship.cpp:686-691`; `FedSrv/FedSrv.CPP:12344-12374`, `11509-11515` |
| Initial public rankings on join; whole-mission rankings after changes; client applies all four counters | `FedSrv/fsmission.cpp:4324-4339`, `3685-3697`; `clintlib/appmsg.cpp:2392-2407` |
| Enemy station export requires the observing side's sighting | `FedSrv/fsmission.cpp:4215-4222` |
| Purchase acknowledgment contains the actual granted loadout and controls launch | `FedSrv/FedSrv.CPP:6247-6261`, `6282-6292` |

The review found and corrected four missing production contracts: the
Starting/allow-empty-team gates; native event-specific victory reasons and the
post-construction tally; non-winning capture vacating; and initial GAME_STATE
rankings in join sync. A typed `StationVictory` context carries station/actor
information through the existing site callback; the server resolves the actual
pilot name. The shared `VacateStation` peer/ghost operation is reused by
capture and destruction and is scoped to the station's mission. A capture's
last-restart deactivation is checked before testing whether the old side is
still active. Existing post-capture armor/shield and station type/name semantics
are retained.

Parity rows **SIM-0847–SIM-0850** record these narrow contracts. Existing full
native handler rows remain partial: this task does not claim to close all
native station-handler residuals. `GLOSSARY.md` indexes the new controller and
station event context. No service, port, store, environment variable or media
asset was added to the application.

## Verification

| Final check | Result |
|---|---|
| Solution build | 0 warnings / 0 errors |
| `tools/build-tools.sh` | all 90 projects built, 0 warnings / 0 errors |
| Linux x64 server AOT | publish passed; no IL2026/IL3050; bounded binary boot clean |
| Focused bot / native / conquest wire cases | 82 / 52 / 4 cases, each group passed 3/3 |
| Final conquest games | 3/3; 100.6, 245.5, 122.9 wall seconds; winners 1, 1, 0; exact capture reasons |
| Existing flags/artifacts loopbacks | all six variants passed 3/3: 18 successful games |
| Single Fast sweep | 11,095 passed / 8 failed / 3 optional skips; 321.5 s; no timing offenders |
| Isolated Fast follow-ups | profile method, nine-case paths class and side-switch method each passed 3/3; corrected 31-case fanout class passed 3/3 |
| Glossary / parity / native inventory | passed; 224 glossary paths; 351 native bodies/cases inventoried |

The original Fast result remains **failed**. Six failures were the documented
sandbox path/preferences environment limitations; one was the existing
`P775...Side_switch_sends_no_playerinfo` seating flake. The eighth was the
known P784 auto-seating/incomplete-roster fixture: it failed three isolated
trials, so the fixture was corrected to request explicit sides and wait for the
teammate's roster data. Its entire class then passed three times. No product
chat code changed. The path/preferences follow-ups used existing Linux XDG and
`/proc` directory-alias mechanisms pointing entirely inside this workspace.
These receipts are ledgered in `docs/TEST_FLAKES.md`; the full sweep was not
rerun, as required.

Final results and timings are listed in `P832E_assets/validation.json`.
Raw logs/TRX are retained locally in the ignored `.scratch/p832e/` directory;
raw live logs are under its `live-logs/` child (TRX stdout retains the original
log basenames and generation paths).
The sandbox does not permit writing the preferred external evidence directory;
only the small recipe, summary and this report belong to the public change.

The live conquest proof requests 15v15 pilots, fog enabled, seed 832 and a Brawl
map. Each side has two flag/restart stations and the goal is 65%. It uses the
existing explicit all-tech/wallet exhibition fixture; TestHarness is disabled.
No combat damage or capture is injected. The fixture pumps one fixed 50-ms
simulation tick per 20-ms sleep (approximately 2.5x simulation speed when the
host keeps up), with the production bot loop running normally. Reported wall
times are accelerated loopback measurements, not normal-cadence server timings.
The first successful development run
captured a real Garrison, reached 75%, and received
`Bravo won because mcp-b003-6662 captured Garrison` in 102.3 wall seconds.
The final repeated proof has a 360-second wall bound and asserts exact native
reason text, the reported winner, conquest percentage, surviving restart bases
for **both** sides, no timed finale and zero sim-stage exceptions. Retaining
restart bases on both teams distinguishes conquest from elimination.

The focused regressions cover visible-station filtering, hidden-pose rejection,
actual defense control emission from 5,000 units away, capture preference, replacement/re-targeting,
small-fleet legal strike reservation, same-life hardware preservation, below-goal relaunch, legal boarding
purchases, combined-goal quotas and worker deduplication. Native tests cover
stage/empty-team gates, all reason variants including allies, capture vacating
and initial rankings received before a station event.

The two 100-frame, 200-pilot flag-only/artifact-only decision hashes were
recorded from an isolated copy of **unmodified dispatch master**. The new
`P832eMasterDecisionTraceTests` assert those hashes; the existing P832d no-goal
100-tick tests also cover absent, prosperity and kills goals. Thus the no-conquest
planner comparisons use an actual master baseline. Existing P832c/P832d and
finale/refit regressions are included in the repeated focused checks.

The first tools build hit MSBuild **MSB4166** (a prematurely exited child node)
in `hud17-proof`. A complete rerun with MSBuild server/node reuse disabled built
all 90 tools. The Linux x64 AOT binary was also booted locally with the attached
recipe and shut down cleanly.

### Never-parks qualification limit

The requested idle-host five-run comparison against master was not attempted.
With this task's builds/tests stopped, the host measured **24 CPUs**, load
**10.76 / 11.14 / 8.83**, **3 runnable tasks**, and **6.98% CPU busy** over five
seconds, at `2026-10-27T19:41:56Z`. This was not an idle host. The brief explicitly
asks for load numbers instead of blindly retrying this load-sensitive test.
The baseline source used for decision hashes is the same dispatch commit.
No five-run never-parks pass rate, base comparison or remote qualification is
claimed.

## Claude's 221 recipe

No connection to 221, the Mac or another remote machine was made. On the target
host, use the existing artwork location and transport setup with
`docs/reports/P832E_assets/showcase-conquest.json`. It is an **unlocked
exhibition** fixture with native purchase validation, a wallet/tech grant,
seed 832, fog, and conquest 100%. Its two native home bases make the first
capture/destruction reach 100%; the four-base local test above separately proves
conquest independently of elimination.

```sh
~/.dotnet/dotnet run --project src/Allegiance.Server --no-build -- \
  --config docs/reports/P832E_assets/showcase-conquest.json
```

Call the usual MCP driver:

```json
{"tool":"spawn_scenario","args":{"preset":"conquest","count_a":15,"count_b":15,"loadout_mode":"best"}}
```

Expect `[flags] duty=Recon/Board/Siege/Wing/Hold`, accepted troop transport and
strike purchases/refits, station ownership or destruction, GAME_STATE reaching
100%, and GAME_OVER with the same winner and native capture/destruction reason.
There should be no server finale timeout request. Allow **15 minutes** at normal server cadence for the
small full-tech qualification; this is a simulation-cadence-based allowance,
not a separately measured normal-cadence bound. Observed local final timings
are in the summary. The accelerated final runs span 100.6–245.5 wall seconds;
a rough normal-cadence expectation is 4–12 minutes, not a measured guarantee.
For 200v200, use the existing `--workers 4` supervisor and the recipe's 220-player
team limits. The single global 8/12 allocation remains capped across workers.
An initial **20-minute** window for that large match is an unmeasured
qualification allowance, not a measured win-time promise. Record the actual
winner, native reason, counts and time. Research-start economy/tech pacing is
not established by the all-tech live fixture.

On an idle host, separately compare five isolated runs of
`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
against dispatch master and this tree, using identical artwork, environment and
configuration. Do not run the two batches simultaneously.

## Native-intent classification

**Native rules restored:** selected conquest stage/active-team gates, public
initial rankings, post-build tally, event-specific victory reasons and the
active-old-side capture vacate branch. The scoring arithmetic, capture
capabilities, shield/bay constraints, ownership mutation and wire visibility
remain native contracts. **Bot policy added:** station ranking, troop transport
preference, strike/refit assignment, escorting, recall threshold, bounded guards
and the shared duty allocation. Native Allegiance does not provide this fleet
AI; these are player-like tactics using native-visible information and legal
actions, without a timed win substitute.

A modern/better approach could score the observed percentage gain of capture
versus destruction, add assignment hysteresis and estimate the time a boarder
needs inside a sustained shield-down window. That could improve large-fleet
coordination while retaining native fog, purchase, flight and victory rules.
