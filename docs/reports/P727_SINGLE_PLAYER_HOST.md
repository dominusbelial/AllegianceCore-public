# P727 — embedded single-player host and Skirmish

Base: `4a2fd32b` at dispatch. Its ancestry contains P712 (`d0acd9ec`), P726
(`5694cb64`), and P731 (`766999c6`). Changes are left in the working tree;
no commit, push, deployment, or rig run was performed.

## Player flow

P731's **Single Player** entry now opens Skirmish setup. A no-argument player
launch always preserves that intro choice, including when a multiplayer server
was remembered; explicit host/port arguments still connect directly. The keyboard menu offers
both races (all ten PCore001 civilizations), Small 5v5, Medium 15v15, Large
30v30, Epic 60v60, research/unlocked technology, and Easy/Normal/Hard bots.
Epic displays a CPU/time-dilation warning before starting. The player flies on
side 0; in research mode P703 manages both research commanders. The team sizes count combat
pilots, including the player: the driver spawns 4/5, 14/15, 29/30, or 59/60 bots.
P658's idle probes and P703's commander connections have reserved hangar seats.

Setup remains responsive during startup and Escape cancels it. Local ephemeral
ports are not saved as multiplayer favorites. The in-game menu offers pause,
resume, and End skirmish; Pause/Ctrl+P and Ctrl+F10 also work. Ending returns to
setup. A dead worker, dead driver, or missing heartbeat appears in the game HUD.
The existing hangar, flight, economy, mining, technology, finale, game-over,
intermission and next-match paths remain the actual game paths.

Real DesktopGL capture: [Skirmish setup, 1280×720](P727_assets/skirmish_setup_1280x720.png).

## Ownership and scheduling

`Allegiance.SinglePlayer.SkirmishHost` boots `Allegiance.Server.Server` in the
client process, binds **127.0.0.1:0**, disables discovery, and drives ordinary
`Server.Tick()` on a dedicated BelowNormal thread. Thus the operating system
chooses a free port, and automatic time dilation observes real tick cost.
Each session writes its generated `server.json` and driver/worker logs to a
unique `allegiance-skirmish-*` temporary directory.

The configuration supplies the map, two civilizations, explicit
`maxTeamImbalance=0x7fff`, team capacity, `showcase.techMode`, auto-next-match,
and automatic dilation. Research uses ordinary economy and legal loadouts;
unlocked uses the existing whole-match test-unlock grant so the human shop and
bot selectors see the same unlocked technology. No external game server,
lobby, Python installation, MCP client, or installed .NET runtime is required
by a player bundle.

The game starts `bot-driver/allegiance-mcp` with a private stdio handshake and
P658 supervisor/worker mode. Even a one-worker budget uses supervision.
On Windows the game assigns the supervisor to a **KILL_ON_JOB_CLOSE** job
before allowing it to spawn workers. On Unix the supervisor creates a new
session/process group and watches the game PID; owner death kills that group.
P658's workers independently watch their supervisor. Normal disposal closes
stdio, drains output, and escalates to tree termination if necessary. Every
startup failure/cancellation disposes the partially started host.

Rendering retains normal priority; the simulation thread is BelowNormal and
the bot supervisor/workers use Idle OS priority. With `available=max(1, cores-2)`,
worker count is `clamp(available/2, 1, 4)` and each worker receives
`max(1, available/workers)` logical processors via `DOTNET_PROCESSOR_COUNT`.
`ALLEGIANCE_BOT_COMPUTE_THREADS` also explicitly caps each worker's parallel
decision scan; absent this setting, the standalone driver's default is unchanged.
This bounds parallel bot compute and reserves two cores where available;
one/two-core machines share a core under the priority policy. Bot processes
use workstation GC to avoid a GC heap per host core. This is a compute budget,
not a claim that per-connection network/receive threads disappear.

## Difficulty and pause

Driver knobs are `ALLEGIANCE_BOT_ACCURACY`, `ALLEGIANCE_BOT_REACTION` (seconds),
and `ALLEGIANCE_BOT_AGGRESSION`. The game sets them for each owned launch.

| Difficulty | Accuracy | Reaction | Aggression |
| --- | ---: | ---: | ---: |
| Easy | 0.55 | 0.4 s | 0.75 |
| Normal | 1 | 0 s | 1 |
| Hard | 1.5 | 0 s | 1.25 |

Accuracy tightens/loosens the lead solver's intercept radius, reaction delays
fire on a newly acquired target, and aggression scales the gun opening range.
Normal preserves the previous solver/fire gate. These are explicit skirmish
policies, not claimed native difficulty levels.

Pause first acknowledges quiescence of every bot worker's combat and research
passes, then sets the embedded server's advertised time scale to **zero**.
The server stops world mutation, including inbound flight application, while
still draining/responding to keepalive PINGs. Other gameplay packets are dropped
during pause so stale flight commands cannot accumulate. Bot
policy clocks and the supervisor's defense/coordination clock also stop, and the cadence is reanchored so resume does not owe
a long pause's worth of delayed decisions. Health/ownership IPC stays live.
The pilot's server clock accepts zero and local flight input/simulation stops.
Resume restores the prior automatically selected dilation rate. Dedicated
servers cannot acquire this capability through configuration or network
commands; their existing 0.1–1 admin timescale range is unchanged.

## Native basis

All native paths below are relative to `~/Development/Allegiance/src/`.
Local process ownership, budgeting, difficulty, exact fleet presets and the
approved zero-scale single-player pause are port orchestration policy.

| Reused native rule | Native source |
| --- | --- |
| Intro training/single-player entry | `WinTrek/introscreen.cpp:1221`, `:1558`; training launch `WinTrek/trainingscreen.cpp:168-209` |
| Side civilization IDs and side construction | `Igc/missionigc.cpp:3734`; enabled starting development paths `:3869-3899` |
| Rank-balance default `0x7ffe` | `FedSrv/fslobby.cpp:33` |
| No-limit sentinel `0x7fff`, team cap, numeric bot exception, separate rank-balance gate | `FedSrv/fsmission.cpp:4612-4671` (bot exception `:4640`, rank gate `:4646`) |
| Fresh player enters hangar; launch uses purchase/loadout acknowledgement | `FedSrv/fsmission.cpp:791-796`; `clintlib/appmsg.cpp:3176-3221` |
| Readying and starting a real mission | `FedSrv/fsmission.cpp:5485-5528`; `clintlib/appmsg.cpp:2330-2346` |
| Research uses side techs and real wallets/donations | `Igc/sideigc.cpp:102-117`, `Igc/sideigc.h:194-210`; `FedSrv/fsmission.cpp:2294-2324`, `FedSrv/FedSrv.CPP:5535-5564`, `:7137-7166` |
| Wingman lead, steering and gun/missile gates reused by Normal | `Igc/shipIGC.cpp:2470-2600`; intercept solver `Igc/common.cpp:68-114` |
| Restart/intermission proceeds into the normal start countdown | `FedSrv/fsmission.cpp:3539-3546` |
| Keepalive must continue across pause (six-second PING, more than five unanswered and 30 seconds silent means lost) | `clintlib/clintlib.cpp:2050-2087` |
| Player update timestamps must stay within the server clock gate | `Igc/shipIGC.cpp:3145-3146` |

Native training missions remain a separate track, as requested.

## Bundles and benchmark

P726 packaging includes the managed server, SinglePlayer host, authored research
plans, and a self-contained bot-driver subtree for Linux, Windows and macOS.
Player publication disables the dedicated executable's AOT default and removes
its standalone host runtime/dependency files; the client owns the runtime.
This avoids cross-RID project-reference publish collisions without changing
standalone server publication. The manifest checksum inventory also covers bot
runtime files and research plans.

`packaging/smoke.py` now runs the existing asset census followed by
`Allegiance --skirmish-smoke 60`. This enters the same host before SDL/GL/audio,
seats a real side-0 test pilot, readies/launches it, and flies ordinary autopilot
controls for sixty seconds. It requires a running match, live bot workers,
all nine bot pilots, and pilot travel. JSON reports wall/sim time, combat
counts and process ownership. Optional `Small|Medium|Large|Epic` selects the
same presets for local headless benchmarks. Startup/intermission is not
misrepresented as time spent in combat.

## Validation

The final whole-solution Debug build and a second no-restore build both
completed with **0 warnings / 0 errors**:
[build log](P727_assets/whole-solution-build.log). Packaging's eight Python
checks passed, then each ran 3× alone (**24/24** isolated passes):
[packaging unit ledger](P727_assets/packaging-unit-validation.json).
`git diff --check` also passed.

New and affected tests ran three times per method in fresh test processes,
including all theory cases. This covered P727, P658 supervision, exact showcase
fleets, pilot/server clocks, time dilation, the intro model/layout and saved
endpoint, P660 restart, P703 research and P705 tech modes. P705's four live
civilization-pair cases each completed two matches in all three invocations
(24 match cycles). After the final
compute-budget/pause changes, P727/P658/fleet checks ran again: **135
invocations, 201 passed cases, zero failures**. The four P727 live methods
passed three times on the final host/bot code, including both one-worker and
multi-worker crash-cleanup cases. There are 23 P727 cases plus three new
remembered-endpoint cases.

The loopback lifecycle test flies a real pilot for 60 seconds, observes actual
shots and hits, removes the pilot, pauses for 40 seconds (past native keepalive
timeout), verifies exact simulation clock/ship state and surviving bot peers,
resumes, forces ordinary game-over, observes the next started match, and checks
all owned PIDs exit and the listener port can be rebound. Separate tests cover
research commanders for civilizations 101/35 and dead-worker HUD health.

The final **never-parks 5/5** passed in 98.94, 97.49, 99.38, 97.78 and
96.59 seconds. The preceding five-run set also passed 5/5; there was no
never-parks failure requiring a base-rate comparison.

One authored P703 case timed out in the third earlier focused invocation
while its constructor was travelling/building. It then passed **3/3 fresh
exact-case isolated runs** in 91.16, 92.99 and 90.91 seconds. This meets the
brief's load-flake rule; no deadline or assertion changed. Details are in
[TEST_FLAKES.md](../TEST_FLAKES.md#p727-local-validation-2026-10-18).

The single unfiltered sweep completed (one invocation per project):

| Project | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Core | 4,853 | 1 | 0 | 955.36 |
| ClientCore | 1,321 | 0 | 1 | 2,647.15 |
| MonoGame | 2,432 | 1 | 3 | 973.91 |
| Lobby | 19 | 0 | 0 | 1.58 |

Core's raw failure was the existing peer-join timeout in
`TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`;
it passed **3/3 fresh exact-method isolated retries** in 2.28, 2.38 and
2.28 seconds, meeting the brief's load-flake rule. MonoGame caught a
deterministic P727 regression: the paused menu read raw Escape, bypassing
P625b's action map. The guard failed 3/3 before correction
([confirmation ledger](P727_assets/action-guard-before-fix.json)). The path
now uses `ActionDown(ks, "MainMenu")`, preserving rebinding; the corrected
source guard passed 3/3 fresh exact-method reruns. After rebuilding the
corrected source, all 24 P625 guard/behavior methods ran 3× alone: **72
invocations, 255 passed cases, zero failures**. The final bundles were also
rebuilt to include the correction. No second broad sweep ran.

All four default skips were then exercised successfully. The three private
Meshy-pack methods each passed 3× with the already installed pack explicitly
configured (**9/9**). ClientCore's opt-in
`P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms`
passed **3/3** with `ALLEGIANCE_LIVE_P660_SHOWCASE=1` in 26.46, 26.62 and
26.36 seconds. This runs the real 15v15 MCP scenario, rotates all thirty bots
and both human connections into the next mission, and verifies finale
rearming. No network/live test was omitted because of sandbox access.

All final player archives built successfully:
[bundle ledger](P727_assets/bundles.json),
[Linux metadata](P727_assets/Allegiance-linux-x64.tar.gz.json),
[Windows metadata](P727_assets/Allegiance-win-x64.zip.json),
[macOS metadata](P727_assets/Allegiance-osx-arm64.zip.json).
The extracted Linux bundle passed asset census and the real **60-second Small
Skirmish**: match running, nine bots alive, pilot travelled 2,749.77 m;
[smoke JSON](P727_assets/linux-bundle-smoke.json). Shots/hits are zero in that
short smoke's travel window; actual combat is required by the longer lifecycle
test above.

Windows x64 and macOS arm64 archives passed
[structural inventory](P727_assets/cross-platform-inventory.json), including
server/host/driver runtime files, all four authored plans, apphost architecture,
and executable modes. The other eight civilizations use P702's generated
plans. Native Windows/macOS execution remains a CI check; this Linux machine
did not run those executables. No external rig, /221, or standalone 200v200
run was launched.

[Saved-server launch proof](P727_assets/remembered-startup.txt) and
[its DesktopGL capture](P727_assets/skirmish_setup_remembered_server.png) verify
that remembering a multiplayer server does not hide Single Player on the next
no-argument launch.

The machine-readable invocation ledger is
[validation.json](P727_assets/validation.json), with a compact
[gate summary](P727_assets/validation-summary.json); final focused, never-parks,
full-suite and failed TRX evidence is retained alongside it as gzip files.
Detailed scratch logs and the three distributable archives remain under
ignored `.verify-p727/` and `dist/`.


Raw TRX/log evidence for this task is archived outside the repo at `~/allegiance-evidence/P727/` (Claude, 2026-10-18, evidence size rule).
