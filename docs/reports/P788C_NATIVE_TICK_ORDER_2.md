# P788c — experimental native broadcast order and warp re-entry

Dispatch base: `dac42b33` (public master at dispatch), branch `codex/p788c`.
Changes remain in the working tree; no commits or pushes. Native references below
are relative to `~/Development/Allegiance/src/`.

The experimental scheduling flag defaults off. The inactive-turret wire gap and
strict player warp handshake are fixed in both flag states. P788b's shelved
simulation/collision ordering is unchanged.

## Native rules and classifications

| Rule | Native source | Classification and result |
|---|---|---|
| Started missions send ship updates before advancing IGC | `FedSrv/FedSrv.CPP:4237-4254` | **Design.** Implemented under `sim.nativeBroadcastOrder`; default off preserves the port scheduling adaptation. |
| Per-side status changes and fresh player/turret updates | `FedSrv/FedSrv.CPP:3662-3700,3762-3798` | **Design.** Existing status/active paths retained; active relay works in the interest-tier path too. |
| Odd-frame and bandwidth-selected HEAVY/LIGHT cadence | `FedSrv/FedSrv.CPP:3664-3666,3830-3985` | **Era constraint.** Retain the port's cadence and P659 per-viewer pose floor. |
| Stopped boarded player turret IDs go to every cluster player, including self | `FedSrv/FedSrv.CPP:3791-3794,4009-4020` | **Design.** Send existing LIGHT(175) IDs in the same coalesced frame as HEAVY; no new fields. The old sender omission was a **port bug**. |
| LIGHT consumer skips self/unknown/non-boarded ships and stops shooting | `clintlib/appmsg.cpp:684-709` | **Design.** Clear `TurretFiring`, retaining facing; the old ignored IDs were a **port bug**. Shared client model; no HUD layout or prediction change. |
| Player enters NoUpdate on warp; accepted updates alone arm Waiting and later Ready | `FedSrv/fsship.cpp:74-82,1173-1190` | **Design.** Remove both OnUpdate timer shortcuts. The compatibility bypass was a **port bug**, not a native bug. |
| Client sends pose, cookie and converted server time at 0.15 s cadence | `Inc/Messages.h:226; clintlib/clintlib.cpp:1832,1858-1863` | **Design.** Scripted clients use their latest authoritative pose and a fresh PING clock; all normal server validation still applies. |
| Adopt updated own-ship cookies on assignment, rejection and ejection | `clintlib/appmsg.cpp:1066,1179-1187,230-232` | **Design.** Scripted updates follow the same existing cookie-bearing messages. |
| NPCs remain Ready, with positive mass-limit exemption; all ships use native exit transform | `FedSrv/fsship.cpp:71-137` | **Design.** Audited below; the port's additional NPC guards remain explicit deviations. |

## Implementation

`Server.NativeBroadcastOrder.cs` resolves JSON plus
`ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER` (environment wins; 0/1 or false/true;
invalid values fail). Compose exposes it with default `0`; the boot banner and
profiler label identify the active mode.

After inbound drain, started-mission membership is captured once. Ship identity,
cross-side status, detected bits, own/teammate poses and HEAVY/LIGHT projection run
before `Mission.Update`. The original post-update calls serve only the other
missions. Membership survives countdown/stage changes and mission rotation, so a
mission never gets both projections in one tick. Statics, warnings and simulation
events retain their existing passes.

The pre-update pose carries the previous completed tick's timestamp: `TickIndex`
has not yet advanced. P317 subject encodes, P604 parallel projection, P659's
per-viewer wall-clock floor and detached immutable snapshots remain in use. The detached ship snapshot is submitted before simulation, followed by a separate
snapshot for simulation events/statics. Complementary snapshots of the same tick
retain each other's streams; newer ticks preserve the existing shedding rules.
Shared pose caches are cleared at the simulation boundary so a simulation callback cannot
reuse a pre-update pose under the same tick key. Inactive turret lists are gathered
once per sector per projection, including when workers project in parallel.

A stopped gunner's saved active update cannot reactivate the remote firing latch.
LIGHT contains only live, flying, boarded player gunners with a turret mount and
no weapons bits. Observers, drones, dead/docked ships and docked carriers are
excluded. Both legacy and interest-tier senders append LIGHT to HEAVY's same
latest-wins frame; packet byte accounting includes the IDs.

`Ship.OnUpdate` never advances player warp state. The existing accepted-update
handler alone performs NoUpdate → Waiting (`mission.LastUpdate + 2`) → Ready;
`ServerHost.HitWarpEvent` now also checks readiness for direct callers.
`ScriptedWarpHandshake`, shared by Bot and ConnectionSession, begins a real update
stream after a flying sector transfer once controls-only scripting is active.
It respects the server's one-second PING reply limit, maps the shared HEAVY
`timeReference` tick sequence into that server-ms epoch, and echoes authoritative
poses at the native 0.15 s simulation cadence. The shared HEAVY timestamp advances
even when an own ship is stationary; its last dirty own-pose timestamp does not.
This lets accelerated scripted hosts complete the real handshake without a timer
shortcut or a PING flood. SET_CLUSTER, SHIP_RESET and EJECT cookies are followed.
Native prediction senders disable this helper and keep their existing stream.

An echo pairs the existing Time=0 controls form with a real SHIP_UPDATE. The latter
passes the same cookie, clock, monotonicity, velocity, movement-credit and envelope
validation. For these server-flown scripted peers, validation preserves the
current full-precision pose, current steering input, server order ownership and
afterburner ramp. The rounded acknowledgement is not a new steering decision.
Ordinary native clients retain the existing accepted-pose behavior. This is an
explicit compatibility adaptation to the port's server-owned scripted flight,
not a new native rule or a bypass of the accepted-update handshake. No bot strategy
or client prediction implementation changed.

## Non-player warp audit and retained differences

Native `Igc/shipIGC.cpp:620-636` delegates the warp collision to the site;
`FedSrv/fsship.cpp:71-137` keeps NPCs Ready and uses the same exit/wobble code as
players. The port matches that state rule, the Andon mass-limit expression and
P777's exit transform/loaded exit-speed bonus.

These differences remain, without claiming exact NPC parity:

- `Sector.ResolveAlephShip` additionally requires two seconds since LastWarpTime
  for NPCs. Native has no NPC cooldown: only players leave Ready (`fsship.cpp:79-82`).
- The opt-in arrival-volume safety latch requires clearance of twice the transit
  volume before re-entry. Native relies on its exit pose/motion and player
  handshake. This is a retained port workaround for routing/collision behavior,
  not a native rule.
- The port uses the warp's initial frame rather than native model spin
  (`Igc/warpIGC.cpp:40-70; modelIGC.cpp:85-100`).
- Native resolves globally sorted collision times; the port still uses its
  class-pass/chunk schedule (SIM-0063, shelved). This is outside this task.

SIM-0141 remains partial for the default-off experiment. SIM-0470's inactive gap
is closed, but the row remains partial for deliberate authoritative pose/floor
cadence and P803's stored-active relay: native resets the last-update discriminator
on each send (`FedSrv/fsship.h:290-293`), while the existing port relays the stored
active update while fresh. SIM-0150/0208's player handshake residual is closed;
rows remain partial for the audited NPC differences.

## Verification

The implementation and tests remain uncommitted. The scheduling experiment is
**off by default**. Functional checks and the final showcase cohort pass; the
single Fast sweep is **not reported as all green**, and local native-mode
performance is **not a no-regression result**.

| Check | Result |
|---|---|
| Whole solution | **0 warnings / 0 errors**, `final-verified-build.log` |
| Every tool project | **79/79 built**, `gate-build-tools-final.log` |
| Server Native AOT, Release/linux-x64/self-contained | **ELF produced; no IL2026/IL3050**, `gate-aot-final.log` |
| Final functional groups | **70 Core cases ×3; 12 ClientCore cases ×3**, no failures/skips |
| Exact class isolation | Fourteen affected/new classes each run ×3; all pass, apart from one known first-warp setup timeout qualified below |
| P694 original simulation matrix | **20/20 ×3** including the six new measurement cases before publication refinement; unchanged 14 sector-only cases retain their existing budgets |
| Final P694 full-tick measurement cases | **6/6 ×3**, including a zero-stage-failures assertion; final performance table below |
| Recorded tapes, tick-order flag off | **4/4 complete SHA256 transcripts match dispatch**, including snapshot off/on/encode-once; stale expected-prefix assertions still fail |
| Final P700 showcase | **5/5 pass**, dispatch baseline **5/5 pass**; additional flag-1 invocation **1/1 pass** |
| Compose / glossary / parity / LF / whitespace | **PASS**; no CR bytes in the edited CSV |
| Working-tree public hygiene | **24 code files scanned; no secret/email/home-path findings; no new media** |

Every .NET command used `~/.dotnet/dotnet`. Builds, TMPDIR, results and caches
were confined to worktree scratch. When the NuGet vulnerability cache expired,
its default user directory was read-only; rerunning with
`NUGET_HTTP_CACHE_PATH` in scratch succeeded with auditing still enabled.

### Functional isolation and comparison to master

The final 70-case Core group covers `P788cConfigTests`,
`P788cNativeTickOrderTests`, `P788cScriptedWarpTests`, `BroadcastPublisherTests`,
`FuelPoolAndAfterburnerTests`, `ShipKinematicsGoldenTests`, `UpdateCadenceTests`,
`P803TurretRelayBroadcastTests`, `P507OwnShipModelLiveTests`, and
`P541GotoGateLiveTests`. The 12-case client group covers
`P803TurretRelayTests`, `ClientWarpApproachLiveTests`, `ShipDeleteGoneLiveTests`,
and `CrossSideCombatVisibilityLiveTests`. Each class was also invoked separately
three times (`class-*.trx`). Existing P760 callback, P650 warp-exit and warp-transit
fixtures passed three isolated invocations earlier in this lane.

The scripted loopbacks exercise both flag states, both Bot and ConnectionSession,
two successive warps, an actual kill/ejection callback while the handshake is
pending, and the rotated EJECT cookie. The negative fixtures prove that time,
controls-only traffic, wrong cookies, stale/replayed or rejected poses cannot
re-arm players. Scripted echo validation retains server steering/pose precision;
a powered afterburner replay test proves that it cannot advance the authoritative
ramp. Complementary snapshot phases retain HEAVY/LIGHT streams, while newer ticks
still replace old streams.

A scratch copy of `git archive dac42b33` ran the original ten-case P788c behavioral
harness. **Eight fail on master:** three flag-on boundary cases, four turret-relay
cases (the tier path also omitted active relay), and the no-timer re-arm case.
The two flag-off pose-boundary cases pass. New flag-parser/echo/publication helper
fixtures use new APIs and were not presented as executable master tests.
The unchanged two-hop client flight also passes **3/3 on dispatch**; after the
scripted protocol corrections it passes **3/3 on the working tree**.

### One Fast sweep and preserved failures

ONE `tools/run-tests.sh --tier fast` ran: **10,473 reported cases; 10,460 pass,
10 fail, 3 existing optional-asset skips**, 354.48 s. All infrastructure checks
passed. It ran before the final scripted-protocol corrections; no second whole
Fast sweep was run.

- Three task-related warp/return failures were fixed by automatic real updates,
  preserving server flight ownership, and simulation-paced timestamp conversion.
  Their final affected groups pass ×3.
- Five `PlayerBundlePathsTests` failures came from worktree TMPDIR ancestors
  finding the checkout's `local.env`. The nine-case class passes ×3 with an open
  directory `/proc` alias to the same scratch storage, without a repository
  ancestor. No product/test assertions changed.
- The profile-editor case attempted to write the read-only user config directory;
  it passes ×3 with XDG_CONFIG_HOME in scratch.
- Reconnect-grace expiry missed an observer removal packet; it passes ×3 alone.
- A later class-only cross-side return invocation timed out on its **initial
  outbound warp**, matching the existing ledger's setup flake. Two immediate
  class repetitions and three fresh exact-method invocations pass. This is
  recorded separately from the fixed real handshake failures.

The four recorded-tape golden-prefix assertions fail **4/4 ×3** here and **4/4**
on dispatch. The actual complete hashes are identical across revisions and across
all three snapshot/encoding variants. This demonstrates a stale expected baseline,
not a P788c byte difference. Assertions and expected hashes were not relaxed or
regenerated; diagnostic JSON is now saved before the failing assertion.

### Performance: real P694 worlds, synthetic viewers

The full-tick extension reuses P694's real core, CVH collision hulls, launch slots,
station-death projectile and concentrated ordnance. There are 195 friendly viewers
per sector (**390 synthetic peers**, two sectors); the death scene also adds
12 enemy bombers per sector and the existing projectile/missile populations.
These are local controlled ticks, **not 200v200 matches or real socket clients**.
The production broadcast settings are native ship-update model, immutable
snapshots, encode-once and parallel building; simulation detection stays serial.

Each invocation has fixed warmup (at least five seconds and ten fresh worlds),
then 12 fresh-world samples. The final table pools all three invocations:
**36 samples per flag per scene**, upper-median p50 and nearest-rank p95; no
samples discarded. Wall time covers the complete `TickSim`, including pre-update
projection and simulation callbacks. Async publication is drained after timing;
real network I/O and real client input/handshake traffic are not measured.

| Scene | Flag 0 p50 / p95 / worst ms | Flag 1 p50 / p95 / worst ms |
|---|---:|---:|
| Native-slot launch | 97.524 / 107.663 / 109.044 | 127.217 / 141.558 / 142.538 |
| Coincident launch point | 94.716 / 172.158 / 180.005 | 89.617 / 114.170 / 118.209 |
| Station death + ordnance | 144.380 / 159.449 / 171.406 | 167.166 / 185.128 / 196.281 |

Native-slot launch median is about **30% higher** in this local first-burst test;
station-death median is about **16% higher**. Coincident-launch results have
substantial host/GC variation. This is not evidence that the experiment is free
of cost, and **a no-regression performance gate is not claimed**. The whole-tick
measurement has no invented 100 ms threshold: P694's unchanged 100 ms Release
budget applies to its original sector-simulation-only fixtures. The new cases
assert world outcomes and zero server stage failures and report timings.

The implementation publishes detached ship snapshots before simulation, releasing
that phase independently of later events/statics; it does not introduce P788b's
global movement/collision rewrite. Keep the flag off until Claude compares the
full two-match groups below. The local measurements expose a launch-burst cost
that must be assessed there, not hidden by the default-off path.

### Showcase qualification

Five final-source 15v15 invocations pass **5/5**, matching the dispatch base's
**5/5**. Each is a separate exact-method process; the cohorts overlap on the
shared workstation, rather than claiming idle-machine isolation. A separate
native-broadcast-order invocation also passes. See `showcase.json` for the
per-run duration, arrival count, stopped-with-target maximum, shots and hits.

Earlier runs are retained: one preceding five-process cohort was **4/5**, with
`botsLastMinute` actual 2 at the final metric assertion (its sampled longest stop
was only 2.006 s); a separate earlier run reached only 22/30 objectives at the
480 s limit. These failures were not erased, waived, or used to change any bot
strategy, assertion, timing budget or quarantine. Final five-run success is the
claimed gate, not proof that the intermittent failures are impossible.

### Evidence and retained boundary

Small public receipts: [verification.json](P788C_assets/verification.json) (Claude review: condensed to per-receipt counts and timing summaries; per-case detail stays in the ignored evidence below),
[showcase.json](P788C_assets/showcase.json),
[source hashes](P788C_assets/source-sha256.json),
[hygiene](P788C_assets/hygiene.json). Raw logs/TRX and Fast manifests remain in
[ignored worktree evidence](../../.scratch/p788c/), specifically `logs/`,
`results/`, `fast/`, and the isolation ledgers. This sandbox could not write the
lane's external evidence directory; scratch build/base/config/cache trees were
removed after recording the results. Public evidence remains below 2 MB.

Remaining verification is the **221 two-match-per-flag comparison**. The flag
remains default off; native-mode performance is not certified. The audited NPC
cooldown/arrival-latch/spin differences and existing cadence adaptations remain
explicit parity residuals. No commits, pushes, remote-machine edits or bot-strategy
changes were performed.

## Claude's 221 recipe (not executed in this lane)

Use the existing distributed 200v200 supervisor recipe, size scale 13, ordinary
fog/legal purchases and native ship-update mode. Keep map, seed, civs, server
binary, worker count, research/income, transport/broadcast optimization flags,
TIDI policy and finale arguments identical between flag groups. In particular,
retain P819's `finale_side:"both"` and the established two-match game-over/next-match
flow; do not spawn a full fleet independently on each worker.

1. Restart the server with `ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER=0`. Confirm the
   boot/profiler label. Run **two complete 200v200 matches** unattended, including
   next-match mass launches and station deaths.
2. Repeat those **two matches** with `ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER=1`,
   changing only that flag. Default stays off until this comparison is reviewed.
3. Enable the five-second tick profiler in both groups. Preserve server and
   supervisor logs and report tick p50/p95/worst, update/broadcast buckets,
   allocations/GC, TIDI scale, offered/written bytes, pose-age floor, disconnects,
   REFUSED, kills/minute, parked ships and game-over handoff. Also inspect accepted
   warp updates and repeated warp routes; silent/rejected updates must never
   re-arm players.
4. Compare corresponding mass-launch/station-death windows as well as steady
   combat. Reject/rework the experiment if tick p95/worst or progress regresses;
   the local measurements below are not a substitute for the two-match A/B.

No remote host was contacted or changed by this lane.
