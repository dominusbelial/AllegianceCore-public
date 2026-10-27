# P832f — bots play Kills / deathmatch

Working tree on `codex/p832f`, based on dispatch master `208e73fc04c4c9300d03f28d389fb76fbc533923`. No commits or pushes; no remote hosts contacted.

## Behavior

`BotManager.Kills.cs` extends the existing objective planner and supervisor decisions. Kills is detected from the observer's MISSIONDEF, including when skirmish starts the ordinary showcase preset. The new `kills` scenario preset requires a server `games[].goals.teamKills` value. Timed finales are suppressed, and ordinary bombers join the clash instead of pursuing unrelated station kills in a pure deathmatch. Mixed conquest still owns its station strike/refit duties.

Each observer maintains a cursor over public KILL_SHIP events, using `bKillCredit` and `sideidKiller`, and seeds previously unobserved player credits once per identity from visible PLAYERINFO counters. Repeated player snapshots and known side changes cannot re-add or transfer old kills. Repeated reads do not duplicate credits, older decision snapshots cannot rewind the cursor, and the cursor survives the 500-event rolling window; mission changes reset the tally. The planner uses the highest enemy score reported by the same side's observers, partitioned by mission cookie and side. It never reads server objects or an enemy commander's private state. GAME_STATE contains conquest, territory, flags and artifacts, **no kill counter** (`Inc/Messages.h:854-859`).

**Visibility limit:** a connection joining late cannot reconstruct earlier non-player/non-ship credits from this port's join stream. Absent earlier team changes, its score is an observed lower bound; an older teammate can supply a higher bound through the existing coordination channel. A player who changed teams before our first observation also has ambiguous historical side attribution in PLAYERINFO. Starting observers count mission-wide credits exactly while continuously consuming the stream. Losing more than 500 events between observations also loses unseeded credits. No invented score or new public protocol field is introduced.

One planner allocates one duty per pilot. In a kills game the shared objective allocation expands to 60% of available pilots (120 of 200), so a majority can hunt deliberately. Returning cargo and damaged pilots take priority; existing flag/artifact/conquest planners receive first refusal on the remaining slots, then hunters receive the residue. Emergency returns can occupy the full fleet. There is no independent per-worker kills quota. Healthy support pilots retain their repair work, damaged support can return for repair, and miners and research commanders continue independently. Defense recruitment and AI research attack orders yield to occupied kills duties; explicit human commands retain control.

Hunters rank visible, fresh enemy hulls by remaining hull fraction, then travel distance and id, filling the existing target-class engager caps (fighter 3, bomber 4, capital 6, drone 2). They reuse the ordinary native attack driver and discovered-warp travel. Enemy drones are legal targets; pods and allies are excluded. Stale/disappeared poses are discarded, and ordinary unassigned combat and AI orders also stop chasing pods in a kills game. No hidden station, enemy pose, debug order, injected damage or scripted victory is used in the live proof.

### Policy thresholds

These are bot tactics, not claimed native constants:

- **60% shared duty share:** a majority gets deliberate kill duties while ordinary clash, scouting and support retain room; other goals consume this same allocation first. Emergency safety can use all slots.
- **Withdraw below 50% hull:** retain roughly half the armor for escape rather than fighting down to an unavoidable ejection. Hull, rather than regenerating shields, identifies lasting damage.
- **Enemy within the last 20% of the goal, at least one kill:** raise the withdrawal bar to 70% and require one extra observed friendly hull before hunting. A goal of three becomes cautious at enemy score two; a goal of one is cautious from the start.
- **Resume at 95%:** a wide hysteresis band prevents attack/return oscillation and tolerates native byte-percentage quantization. Successful docking completes the repair sortie and uses the ordinary paced legal purchase/relaunch path, even if the planner has already cleared Recover.
- **Observed numerical parity ordinarily, superiority near defeat:** avoid knowingly outnumbered hunting. This sector-level heuristic cannot predict reinforcements, loadout effectiveness or enemies hidden by fog.
- **Existing engager caps:** retain established anti-swarm limits rather than introducing another attack controller. Weak hull ranking seeks a credited hull loss quickly; partial-damage score shares do not multiply team kills.
- **Travel fallbacks:** recovery reuses the cargo-return approach (zero arrival radius and 100-unit braking band) when bay geometry is unavailable. With no compatible base, a rolling 1,000-unit escape leg and 50/100-unit arrival/braking bands keep the damaged pilot separating rather than stopping to fight. Healthy regrouping uses an 800-unit home offset with 150/400-unit arrival/braking bands, keeping the station as a nearby repair/support anchor without deliberately docking. These coarse fallback distances are policy; ordinary collision avoidance and the native bay solver remain authoritative.

## Native contracts checked locally

All references below were re-grepped in the local native source tree.

| Contract | Native source |
| --- | --- |
| Deathmatch predicate/advertised goal | `Igc/igc.h:1521-1523`; `FedSrv/fsmission.cpp:2777-2783` |
| Started/no-winner gate and team threshold | `FedSrv/fsmission.cpp:3749-3764` |
| Player pod detection versus NPC victim classification | `FedSrv/FedSrv.CPP:11770-11784` |
| Credit requires a non-pod victim and a different side | `FedSrv/FedSrv.CPP:11805-11814` |
| Ship credit cascades once to the team; other launchers credit the team directly | `FedSrv/fsship.h:44-48`; `Igc/shipIGC.h:1778-1782`; `FedSrv/FedSrv.CPP:11867-11869` |
| Partial shares affect personal scoring, not extra team kills | `FedSrv/FedSrv.CPP:11826-11861` |
| Native victory reason | `FedSrv/FedSrv.CPP:11790,11846,11872-11880` |
| Mission-wide public kill event | `FedSrv/FedSrv.CPP:11914-11915` |
| Player counter seed and live increment | `clintlib/appmsg.cpp:2256,2807-2811` |
| Docking class capability gates reused by recovery | `Igc/shipIGC.cpp:727-729` |

An enemy hull loss can score even when the pilot ejects. Killing that lifepod adds no team kill. NPC hull losses count. Team kills, suicides without enemy credit, and zero-amount voluntary ejections add no team kill. Native's different-side credit condition can credit an allied hull when friendly fire is enabled; bot policy deliberately avoids attacking allies.

The sole server correction replaces the generic flags/kills victory text with the native deathmatch team reason. Native's outer `pshipCredit` remains null because the partial-share loop declares a different local variable, so even a ship-credited final kill uses `<team> won by killing enough of their opponents`. This narrow correction is tracked by **SIM-0851**; the full native handler and threshold-body audit remain separately partial. Flag scoring and its event-specific reason retain their existing path.

## Verification

Machine-readable results: [verification.json](P832F_assets/verification.json). Raw build/AOT logs and TRX receipts are retained in [local task evidence](../../.scratch/p832f/); per-match proof logs are at the `.scratch/` paths recorded in that JSON. This is the writable-worktree fallback for the lane's external evidence directory. No media added.

| Final check | Result |
| --- | --- |
| Whole solution, final source | **0 warnings / 0 errors**, final build 10.03 s (fast preflight and initial build also 0/0) |
| `tools/build-tools.sh` | **90/90 projects**, no warnings/errors; affected MCP rebuilt again by the final solution/test builds |
| Linux x64 server AOT publish | **passed**, no IL2026/IL3050 or other warnings emitted |
| Final bot controller/planner, worker JSON, checked-in config loading, no-goal/master traces, damaged flight, score identity/snapshot regressions | **64/64 × 3**, zero skips |
| Native flag/kill rules and corrected kill victory reason | **12/12 × 3**, zero skips |
| Existing flag/artifact/conquest games | **7/7 × 3 = 21 native wins**, zero skips |
| Kills games, production cadence | **3/3 native wins**, measured below |
| One Fast sweep | **11,155 passed / 2 failed / 3 optional skips**, 318.2 s; no timing offenders; nine infrastructure checks passed |
| Exact-method Fast follow-ups | Both failed methods **3/3** in isolation; profile method uses writable XDG scratch |
| Glossary / parity / native inventory / diff hygiene | **passed**; 227 glossary paths, 351 native bodies/cases inventoried; parity CSV has no CR bytes |

The original Fast sweep remains **failed**; it was not repeated. The profile-editor method hit the already-ledgered read-only preferences-directory restriction and passed three exact-method runs with `XDG_CONFIG_HOME` in task scratch. `ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over` saw the initial 50% GAME_STATE at its post-reset 80% event assertion; the authoritative 80/20 state assertions had already passed. It then passed three exact-method runs alone with unchanged source, assertions and deadlines, meeting the lane's load/receive-order-flake criterion. Both observations and the original failures are retained in [the ledger](../TEST_FLAKES.md) and raw receipts. No product conquest or profile code was changed for these follow-ups.

The gate uses `~/.dotnet/dotnet build AllegianceCore.slnx`, `DOTNET=~/.dotnet/dotnet tools/build-tools.sh`, and `~/.dotnet/dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o <scratch>/aot`. Test receipts record three separate invocations per group. The fast sweep uses `tools/run-tests.sh --tier fast` once. Its temporary directory is the same NVMe worktree scratch through an open-directory `/proc` alias, preventing bundle-path fixtures from discovering the real checkout's `local.env` while walking above their temporary fixture (the environment-context workaround already ledgered by P832a). Assertions and product path resolution are unchanged.

The existing P832d no-objective test's old TeamKills case is now an enabled objective, so that case was changed to a countdown-only goal. Its no-goal and prosperity checks remain; P832f adds an explicit 100-tick no-goal target/order/assignment trace, disabled-kills tests across other goal combinations, and retains the pre-existing dispatch-master flag/artifact SHA-256 fixtures. These are controller/planner traces, not a claim of bit-identical nondeterministic whole-match combat histories.

### Kill match timing at production cadence

All three final 15v15 matches used seed 832, research start, no test grants, a three-kill goal, and a 50 ms tick sleep at configured 20 UPS. Times below start after fleet spawning. Each asserted the exact native GAME_OVER reason, winner kills = 3, a living miner observed on both sides, research advancement on both sides, hunt trace entries, zero simulation-stage failures and no timed finale.

| Repetition | Wall seconds | Winner | Native reason |
| --- | ---: | --- | --- |
| 1 | 53.89 | Alpha | Alpha won by killing enough of their opponents |
| 2 | 52.67 | Alpha | Alpha won by killing enough of their opponents |
| 3 | 48.16 | Bravo | Bravo won by killing enough of their opponents |

The 221 small-fixture expectation is approximately **one minute after spawning**, with a **three-minute qualification allowance**. This is based on these normal-cadence local matches, not a measured 200v200 result.

### Never-parks comparison

Before this task's build/test work, the shared host reported 24 CPUs and load **6.20 / 4.79 / 4.19**. During a gap with this task's processes stopped it reported **3.05 / 4.65 / 4.68**. The brief permits load numbers instead of a five-run comparison on a busy host. No five-run pass rate or base-versus-change never-parks qualification is claimed. Claude should run the two five-run batches sequentially on an idle host.

## Claude's 221 recipe

No connection to 221, the Mac, or any other remote host was made. Apply the existing transport/artwork setup and run the checked-in **research-start, no-cheat** fixture:

```sh
~/.dotnet/dotnet run --project src/Allegiance.Server --no-build -- \
  --config tools/p832f-kills.json
```

The fixture uses the existing showcase pacing: income ×5000 and research ×16, with ordinary wallet/tech validation and both test-grant mechanisms disabled. The timing qualification applies to those settings.

Use the usual MCP driver with:

```json
{"tool":"spawn_scenario","args":{"preset":"kills","count_a":15,"count_b":15,"loadout_mode":"best"}}
```

Expect seed 832, miners and both research commanders advancing, `[kills] duty=Hunt/Recover` decision traces with observed scores, real credited KILL_SHIP events, and GAME_OVER at three kills with winner Alpha or Bravo and exactly `<winner> won by killing enough of their opponents`. There should be no finale timeout. The final local test ticks at the configured 20 UPS (50 ms sleep, plus tick work) and allows **180 seconds after spawning**; final measured times are recorded above. Earlier exploratory runs used a faster 20 ms loop and are not the final timing evidence. This is a small Brawl qualification, not a 200v200 timing promise. For a larger exhibition keep the existing supervisor/worker transport settings and raise teamKills to 20 or more; measure the actual time and winner. On an idle host compare five isolated runs of `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` against dispatch master and this tree with identical settings.

## Native-intent classification and modern/better approach

**Native behavior preserved/corrected:** credit eligibility, team threshold, native-visible observations, pod exclusion, ordinary attack/docking/purchase controls, and the corrected GAME_OVER team text. **Port bot policy:** 60% shared allocation, weak-target ranking, engager caps, numerical-odds check, endgame caution and repair hysteresis. Native Allegiance delegates this fleet strategy to players; these tactics are not presented as a native AI port.

A better policy could estimate time-to-kill from observed shields, hull type and legal weapons; compare that with escape/repair time; and add target-assignment hysteresis with local rather than sector-wide reinforcement estimates. It should retain the same single duty allocator, fog boundary and native scoring path, and be qualified at fleet scale before replacing these explicit heuristics.
