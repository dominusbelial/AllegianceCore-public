# P832c — flags bots

Implemented on the dispatched `codex/p832c` working tree; no commits, pushes, or remote-host access.

Flags missions now assign a carrier, escorts, pedestal guards and nearest visible-carrier interceptors from the bots' own native client state. The same planner runs standalone and through the worker supervisor. `spawn_scenario` accepts `preset:"flags"`; the server must already advertise a flags goal. Ordinary showcase and skirmish also detect `MissionInfo.Goals.IsFlagsGame`, and suppress the bomber finale for that goal.

## Tactics and information boundary

The raid budget is 10% of eligible combat pilots, capped at 8; fleets of at least four eligible pilots get a three-ship raid, while smaller fleets get one carrier. Guards use 10%, capped at 12, with one guard when at least two eligible pilots exist. Support pilots, bomber roles, pods, docked pilots, pilots accepting orders in explicit human-commander mode and active finale duties are excluded. With 200 eligible pilots there are 1 carrier + 7 escorts, 12 guards/interceptors and 180 pilots keeping their existing clash/strike duties. Actual mixed fleets can have fewer eligible pilots. These are bot-policy choices, not native AI rules: a small wing can attempt the objective while most pilots sustain the battle, and a bounded guard share cannot empty it.

The supervisor merges only candidate decisions derived from participating clients, grouped by mission cookie and side. It applies one fleet-wide quota and ranks interception candidates by topology hops, then local distance. It sends names/duties/anchor identities, never server state. Every pilot revalidates the actual target in its own world before steering or firing. Guards distribute over observed own pedestals; a visible thief temporarily takes the guard budget, and losing the sighting returns the pilot to guard duty.

Offense uses globally advertised CLUSTERINFO topology but requires discovered warp coordinates. Unknown mouths and enemy homes are swept with the existing scout scanner pattern. Only an observed flag treasure supplies its pickup coordinate; the bot never manufactures a flag location from a pedestal offset. Escorts follow their designated carrier, cover visible enemies nearby and use friendly strategic sector status to follow through warps. Research commander attack orders yield to assigned flag duties. Accepted orders in explicit human-commander mode exclude pilots from new allocation; a pilot already carrying a flag still returns to score. A real pickup immediately gives return-home precedence, selecting an observed own dockable base and using the existing native bay approach. Death/pod transitions release the duty, and subsequent assignments reacquire dropped or respawned flags. Secured pilots buy/launch through the ordinary lifecycle for another run.

No flags goal: assignment and driving return immediately, without orders, controls, RNG draws or mutations to the existing decision inputs. Native permits scoring at any own dockable base, without requiring our own flag to be at home. Our pilots cannot pick up our own dropped flag (`FedSrv.CPP:13566`), so defense intercepts the thief rather than inventing an own-flag return mechanic. The no-goal regression exercises 100 ticks for absent, prosperity and artifact goals. New-life resets only clear the new flags bookkeeping.

## Native contracts, re-read locally

References are relative to the read-only native `Allegiance/src` tree. No native bot fleet policy is claimed.

| Contract | Native source | Implementation / verification |
|---|---|---|
| `m_sidFlag` starts NA; one carried value; pickup refuses another flag or our own flag | `Igc/shipIGC.cpp:45`; `Igc/shipIGC.h:289-297,1348-1353`; `FedSrv/FedSrv.CPP:13563-13580` | Existing server collision/pickup; bots target enemy real-side flag treasures only |
| Treasure contact requires a player pilot and a hull with Rescue capability; the carrier planner reserves an actually capable hull | `Igc/treasureIGC.h:85-87` | Existing server pickup gate; capability-aware carrier selection and negative test |
| Docking class is fighter → Land, other hull → CapLand; securing requires the station's original side to be our side | `Igc/shipIGC.cpp:727-739`; `FedSrv/FedSrv.CPP:13113-13114` | Fighter-capable carriers; observed own dockable station; existing bay planner |
| Carrying forbids starting/continuing ripcord | `FedSrv/FedSrv.CPP:7716-7723,13388-13410`; `Igc/common.cpp:743-747` | Flag movement uses thrust and warps; never requests ripcord |
| Death/eject clears the carried value, broadcasts NA and drops a ten-day treasure; outside the 1.5× universe ellipsoid it respawns instead | `FedSrv/FedSrv.CPP:11730-11760` | Existing server death/drop pipeline; bot pod recovery and fresh assignment |
| Leaving a side also drops/respawns possession; reset clears it | `FedSrv/fsmission.cpp:867-889`; `Igc/shipIGC.h:486-498` | Bots do not use a side change to transport possession; mission/life bookkeeping clears |
| Beyond the treasure Rescue gate, no flag-specific speed, hull, cloak, weapon or ordinary-warp restriction | All `GetFlag` / `SetFlag` / `m_sidFlag` references in native Igc/FedSrv/WinTrek/clintlib were re-grepped; setters above do not alter hull physics | Carrier selection prefers a practical dockable fighter; this is policy, not a native restriction |
| Carrier visible to any scanner in the same sector regardless of range/signature/occlusion; other sectors still require knowledge | `Igc/shipIGC.h:341-376`; `Igc/stationIGC.h:308-334`; `Igc/probeigc.h:163-189` | Fixed stale missing-carrier branch in shared `VisibilityMath` and both production sweep paths; pickup/drop/sector tests, cache on/off |
| Carrier identity is GAIN_FLAG to all real sides, and late joiners receive existing carried identities | `FedSrv/FedSrv.CPP:13571-13577`; `FedSrv/fsmission.cpp:738-766` | Existing `ClientWorld` carried identity; identity alone never supplies a tactical pose |
| Native client sets possession from GAIN_FLAG; radar highlights flag treasure and marks carriers | `clintlib/appmsg.cpp:3113-3173`; `WinTrek/radarimage.cpp:636-645`; `WinTrek/console.cpp:848-849` | Existing shared client state; no HUD/interface changes |
| Secure increments score, sends team-specific chat/cues, clears possession and sends GAIN_FLAG before checking victory | `FedSrv/FedSrv.CPP:13113-13149` | Corrected secure branch, distinct cues 1360/1358, scoped team audiences; real TCP tests |
| Flag threshold is checked at secure; winner reason is `<team> wins with the flag recovered by <pilot>` | `FedSrv/fsmission.cpp:3861-3899`; `FedSrv/FedSrv.CPP:13149-13158` | Replaced generic flags/kills reason at the flag secure call site; live GAME_OVER assertions |
| Pickup suppresses respawn; secure respawns at an eligible pedestal with no same-side flag within 300; zero eligible pedestals with a capturer wins with native no-stations text | `FedSrv/FedSrv.CPP:13578-13580,13842-13944`; secure call `:13161` | Added missing real-flag secure respawn; below-goal and empty-candidate tests. Native tests eligible pedestal count even though its message says “no stations” |

Server changes are limited to the missing flag-secure path and native carrier visibility. The shared scanner correction also applies to artifact carriers, as native GetFlag requires; flag tactics remain off in artifact games. Existing artifact network cases were included in the three repeated affected-server runs. The existing generic kills reason and artifact policy are retained. Parity rows SIM-0841–0843 cover these narrow rules; broad native-handler inventory rows remain partial.

## Qualification

Compact receipts are in [verification.json](P832C_assets/verification.json).

| Check | Result |
|---|---|
| Solution build | 0 warnings / 0 errors |
| `tools/build-tools.sh --drop-bins` | All 90 projects pass on final implementation |
| Linux x64 server AOT publish | Pass; no IL2026/IL3050 diagnostics |
| New flags policy + live games | **19/19 × 3**, zero skips; normal research, native tech/purchase gates, no test harness |
| Affected server / visibility / artifact / spotter cases | **26/26 × 3**, zero skips |
| Dispatch decision regression inventory | **151/151** on base; **151/151 × 3** on changed source, identical named outcomes |
| No-flags controller trace | 100 unchanged ticks for each of absent, prosperity and artifact goals, ×3 |
| One Fast tier | **10,979 passed / 6 failed / 3 skipped**, 306.1 s; nine infrastructure checks pass; no timing offenders |
| Six known environment failures | Full nine-case path class and exact profile method each pass **3/3** with workspace-safe isolation; broad run remains failed |
| Glossary / parity / inventory / hygiene | 199 linked paths valid; parity CSV valid (LF); 351 native bodies/cases reconciled; no new media, personal paths/emails or secret-pattern hits in changed code |
| Never-parks comparison | **Baseline 5/5 / changed 5/5 pass**, plus **5/5 additional changed-tree passes**; strict uninterrupted idle-host verification remains unverified because of measured host activity |

Nine local native-rule wins were observed, with winner and exact native flag-recovered message checked over TCP in every game:

| Native topology / goal | Repeated wall times | Results |
|---|---|---|
| Brawl / 1 capture | 44.4, 43.9, 73.1 s | 3/3 native flag wins |
| SingleRing / 1 capture | 146.0, 96.3, 96.1 s | 3/3 native flag wins across real warp legs |
| Brawl / 2 captures | 142.4, 146.0, 143.0 s | 3/3 wins, including below-goal respawn and relaunch; the same carrier secured twice in the first run |

The fixture ticks the fixed-step server every 20 ms (about 2.4× wall-clock simulation advance), so these are accelerated loopback wall times, bounded at 360 s per game. The separate normal out-of-process example below measures ordinary server timing. All nine games also observe a defending pilot target a visible own-flag thief and close by more than 50 units while moving; the pure policy test proves nearest-fighter assignment and rejects a hidden carrier despite its public GAIN_FLAG identity.

The final TCP fixture does not place flags, teleport pilots, set possession or force victory. Server-only rule tests place fixture ships to isolate pickup/dock/respawn contracts. The two early verification issues were fixture setup (a remote test ship had default ParentId=0; a visibility scene lacked an opposing scanner); both were corrected before qualification. The normal research smoke also exposed AI commander orders cancelling raid duties; the production fix makes those orders yield to assigned flag duties and tests explicit human-commander precedence.

The sole Fast sweep hit the existing P819/P817/P821 environment limitations: five path cases inherited the repository marker/local.env from task-local TMPDIR, and the profile editor hit the read-only user configuration. All nine path cases passed 3/3 using a `/proc/<parent>/fd/<directory>` lexical alias to **the same workspace-owned scratch directory**; the profile method passed 3/3 with task-local XDG_CONFIG_HOME. No external temp storage, user preferences, path resolver, UI code, timing assertion or test filter was changed. The original broad run was not repeated or relabelled green. See `docs/TEST_FLAKES.md`. The commit-range publish guard is inapplicable without commits; the working-tree content/media scan and `git diff --check` were used instead.

All ten original P700 runs passed. Baseline observation times were 335.0–410.9 s,
with a worst low-speed spell while holding a target of **1.913 s**; changed-tree
times were 284.7–412.5 s, with a worst spell of **1.818 s** (limit: 5 s).
Every run observed real gunfire/hits and at least 24/30 arrivals. The runs
started after three consecutive 3-second samples at ≥90% CPU idle and ran
sequentially without other local tests or builds.

**Verification limitation:** continuous CPU monitoring nevertheless found
sustained host-activity bursts in baseline run 2 and changed runs 1 and 5.
Five additional changed-tree trials all passed (269.3–382.0 s; maximum target
idle **2.011 s**) but every one encountered further bursts. The additional
series was stopped between completed trials; no active test was cancelled.
I cannot claim the brief's strictly idle-host comparison. The original
five-per-tree comparison is complete and passing, and all fifteen executed
parking cases passed; an uninterrupted idle-host confirmation needs a quiet
host window. All outcomes and CPU receipts are retained, rather than
selecting passing results. No P700 threshold, fixture, flight law or
observation window was changed. Monitoring began during the first baseline
run, so its continuous sample coverage is partial; its pre-run idle check
and later samples passed. See the CPU statistics and explicit
`parkingIdleHostCertified:false` in the receipt.

The dispatch baseline is `1e4c33d4660b12eb9395388ae668e4216c9800c8`; its test binaries were copied before implementation and contain no flag-controller methods. The existing 151 decision regression cases passed on that baseline.

Raw logs/TRX/scripts and the baseline binaries for a future quiet-host rerun are retained locally in `.scratch/p832c/` (baseline DLLs in `.scratch/p832c/base-bin/`), and raw game logs in `.scratch/p832c-flags-*.log` because the sandbox cannot write the normal external evidence directory. They are ignored and are not public assets. Only compact summaries/configuration belong in `docs/reports/P832C_assets/` (under 2 MB).

## Local / 221 recipe for Claude

No remote machine was contacted. A reviewable server configuration is [showcase-flags.json](P832C_assets/showcase-flags.json). It uses fog, a seeded native Brawl topology and one capture to win. Keep the rig's existing artwork, transport capacity and approved hosting settings when merging this game's settings into its configuration; for a 400-pilot run retain the normal 1024 connection / per-IP allowances. No new service, port definition or environment variable was introduced; the example uses an ordinary existing server-port setting.

Local command from the repository root:

```sh
~/.dotnet/dotnet run --project src/Allegiance.Server --no-build -- \
  --config docs/reports/P832C_assets/showcase-flags.json
```

Connect the ordinary MCP driver to that server and call:

```json
{"tool":"spawn_scenario","args":{"preset":"flags","count_a":6,"count_b":6,"loadout_mode":"best"}}
```

For Claude's 221 run, use the same game configuration with the rig's existing bind/port and `count_a:200,count_b:200`; the existing `--workers 4` supervisor applies one shared 8/12 cap. Expected observations are `[flags] duty=Raid/Escort/Guard`, a native `[flag] ... picked up`, `duty=Return`, an opposing `duty=Intercept`, then a secure and GAME_OVER with the flag-recovered reason and winning side's flags=1. The normal out-of-process 6v6 example won in **192.35 wall seconds** (winner Bravo, native flag secure, normal research/economy, no test harness). Expect roughly 3–4 minutes for that smoke configuration on an idle host. For 200v200, budget 15 minutes as a qualification timeout; 5–15 minutes is an estimate, not a measured promise, and the actual time/winner/reason must be recorded on 221. No 200v200 or remote-host result is claimed.

## Native-intent classification

**Native rules restored:** same-sector carrier scanner visibility, flag secure notifications/order/reason and real-flag respawn. **Bot policy added:** role budgets, recon/escort/guard/intercept allocation and retry behavior. Native does not supply this fleet AI, so these tactics are an implementation of native game intent, not a claim of native bot parity. They use the same information and legal pickup, flight and scoring actions available to players.

A modern/better approach would choose routes from observed threat cost, coordinate alternate carrier/extraction lanes and anticipate an observed carrier's escape route. That could make defense and escort cohesion stronger, but should remain an explicit policy layer over native visibility and scoring; it must not gain hidden positions, speed bonuses or a different capture rule.
