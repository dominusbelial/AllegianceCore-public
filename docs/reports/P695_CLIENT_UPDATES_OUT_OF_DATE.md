# P695 — human client ship timestamps and simulation time

Base: `master a0533ea631a9f176e3ec175b5110f22e96d22adf`; working branch
`codex/p695`. No commits or pushes. The server's hostile-client gates are unchanged.

The human flight path now anchors its server clock to the latest PING, advances
that estimate and its local ship simulation at the advertised simulation rate,
and resets the clock when connecting to a different server epoch. It requests
one initial PING immediately; subsequent PINGs remain on the session's native
six-second keep-alive cadence.

## Diagnosis and native audit

There were three client-side problems:

- `OwnShipServerClock` fitted one line through 32 PING samples. P671 changed the
  sampling interval to six seconds, making that history span 186 seconds.
  A changing simulation rate invalidates that single-line model. Even a fresh
  PING did not establish the current time: the historical implementation's
  regression test measured **1,977 ms** of error at the instant of an echo,
  exceeding the server's 1,500 ms window. This reproduces a timestamp rejection
  without changing the server check.
- The own ship advanced by unscaled `ElapsedGameTime`, while the server and
  remote ships advanced in scaled simulation time. Fixing only timestamps
  would still leave a human flying ahead of the movement budget under TiDi.
- `ConnectionSession.Connect` cleared the world but retained queued PING echoes;
  `Game1` retained its independent clock indefinitely. An older server epoch
  could therefore survive a reconnect. The clock ignored every new echo whose
  server timestamp was below its old sample. This can produce minutes of
  exclusively out-of-date updates until the new server catches up. Mission
  rotation itself preserves the server's process simulation epoch in this base
  revision; it must not reset a connected client's clock.

Native source was read locally under `~/Development/Allegiance/src/`:

| Rule | Native source |
| --- | --- |
| Require the current cookie, an in-cluster ship, and no parent ship before processing a human pose | `FedSrv/FedSrv.CPP:4851-4864` |
| The embedded payload timestamp must increase; advance the remembered time only on acceptance; send a fresh-cookie SHIP_RESET on pose rejection | `FedSrv/fsship.cpp:1173-1201` |
| Judge the embedded timestamp against the ship's simulation update time, using the ±1.5-second window | `Igc/shipIGC.cpp:3139-3142,3199-3200` |
| Return server time in PING | `FedSrv/FedSrv.CPP:4897-4903` |
| Derive a local-to-server offset from PING, accounting for half RTT and weighting offsets by lag | `clintlib/appmsg.cpp:394-438` |
| Invalidate the old server-clock offset on a new server connection | `clintlib/clintlib.cpp:1358-1361` |
| Convert client time through that offset | `clintlib/clintlib.h:860-869` |
| Stamp both the embedded pose time and outer message time through ServerTimeFromClientTime | `Igc/shipIGC.h:1173-1178`; `clintlib/clintlib.cpp:1858-1863` |
| Send own-ship updates at c_dsecUpdateClient = 0.15 seconds | `Inc/Messages.h:226`; `clintlib/clintlib.cpp:1832,1858-1863` |
| Send keep-alives at maxLagCheck = 6 seconds | `clintlib/clintlib.cpp:2052-2085` |

The port's PING timestamp is `SimNow * 1000`, and its acceptance gate compares
against that same `SimNow`; it is not comparing wall time with simulation time.
The outer/payload clock agreement rule remains intact. The fix retains the
existing receive-time PING observation convention; it does not add native RTT
weighting. Scaled extrapolation is required by the port's TiDi extension, rather
than a claim that native implements this extension.

P655 correctly rejects the conflicting clocks/replays and bounds movement; it
was not necessary to weaken it. P671 changed the estimator's effective history
length, rather than adding another own-ship clock. P682 preserves the global
server epoch and initializes rotated mission clocks from it; retaining an
accepted timestamp on that same connection is consistent with that epoch.
P643's frame scopes do not supply the timestamp: this path uses
`Environment.TickCount64`, independently of the frame elapsed value.

The original rig summaries contain neither PING timestamps nor client clock
history. They establish the rejection symptom but cannot identify which clock
history or reconnect produced it. The rate-change failure and stale-epoch
lifecycle defect are independently reproducible here; no rig run is claimed.

## Changes

- `OwnShipServerClock`: replace the historical regression with a latest-echo
  anchor; apply rate changes continuously; subtract integer local epochs before
  extrapolating; retain duplicate/reordering rejection; add explicit reset.
- `ConnectionSession`: expose a connection generation and clear queued old PING
  echoes on reconnect. Mission changes do not change this generation.
- `Game1.Prediction`: consume echoes under the session gate, adopt TimeScale,
  reset on a new connection generation, and bootstrap one PING per connection.
- `Game1.FlightInput`: advance the existing own-ship integrator by wall elapsed
  multiplied by the server's live TimeScale. Controls and the native 0.15-second
  send cadence are unchanged.

No server acceptance window, replay/cookie check, duplicate-clock check,
same-instant limit, velocity bound, movement envelope, or travel budget was
relaxed.

## Reproduction and coverage

`P695HumanShipUpdatesLiveTests` boots real loopback servers and connects through
`ConnectionSession`. Its headless fixture runs the **actual Game1 clock pump,
flight-step method, and SHIP_UPDATE send method**, with the production clock,
simulator, cadence and wire codec. It avoids constructing the graphics device;
its unconstructed Game object's graphics/disposal finalizer is explicitly
suppressed. It does not use bot controls-only updates or substitute a server
clock timestamp when sending the human pose.

The scenarios join a fresh mission, join after one automatic game-over rotation
at scale 0.85, and join after two rotations at scale 0.5. The fresh case starts at process time zero; rotated cases begin at a late
process epoch (2,636 seconds). Rotations use the production game-over,
intermission and automatic countdown/start path. Fixed scales exercise the same
simulation rate and TimeScale wire message as automatic TiDi without relying on
machine load to select a repeatable rate. The pure clock regression separately
changes the rate after a full window of six-second samples.

Each scenario asserts accepted > 20, rejected = out-of-date = invalid = 0,
physical server movement, a real warp and the client's received sector change.
Flights extend beyond the actual session keep-alive interval. A deliberately
future-dated packet then increments out-of-date exactly once, and the emitted
production summary must agree with the actual acceptance counters. The real
reconnect test reuses the same session/Game1 against servers at different ages
and requires the younger server's epoch to replace the older one.

Historical comparison rebuilt the original four product files from `a0533ea6`
into a separate artifact directory. The baseline fixture only accommodates the
old private method/field surface and allows eight seconds for the first native
keep-alive. That historical comparison used the late epoch for all three cases;
the final zero-rotation fixture additionally exercises process time zero. The historical clock fails the 1,499 ms regression bound with
1,977 ms error. The historical flight path fails the two-rotation, scale-0.5
case by receiving SHIP_RESET; fresh/scale-1 and one-rotation/scale-0.85 pass this
short route. Product files were restored before final verification.

During fixture development, the first counter probe used an old timestamp and
correctly hit the existing replay gate before the out-of-date gate. It was
corrected to use a future timestamp. An early historical headless run also
exposed the unconstructed Game finalizer; the fixture suppresses it now. These
were fixture defects, not load flakes or relaxed product checks.

## Verification

The solution builds successfully with **0 warnings and 0 errors**. Each selected
new/affected method was run in a fresh filtered invocation three times before
the full suites: **111 isolated invocations / 126 passing case executions**,
with no focused failures or skips. Coverage includes H-11 movement protections,
P507 accept/reject/cookie/invalid/time gates, own flight/reset, dock/undock,
death/respawn, keep-alive, turret clocks/fire, and the two-warp pod rescue.

| Focused assembly | Invocations | Passed case executions |
| --- | ---: | ---: |
| Core | 48 | 51 |
| ClientCore | 48 | 54 |
| MonoGame | 15 | 21 |

All three isolated human-flight repetitions produced the same normal-flight
counters below, with actual server movement, warp transit, client sector
notification, and zero corrections:

| Join scenario | TimeScale | Accepted in each run | Rejected / out-of-date / invalid |
| --- | ---: | ---: | --- |
| Fresh mission, process time zero | 1.0 | 53 | 0 / 0 / 0 |
| After one automatic rotation, late process epoch | 0.85 | 65 | 0 / 0 / 0 |
| After two automatic rotations, late process epoch | 0.5 | 118 | 0 / 0 / 0 |

The intentional future-dated probe then changed the summary's out-of-date
counter to exactly one in each case. The real reconnect test also passed 3/3.
Every new regression passed again in its full assembly run.

The final **unfiltered, sequential workspace-output** suites reported:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,557 | 1 | 0 |
| ClientCore | 1,012 | 2 | 1 |
| MonoGame | 2,199 | 0 | 3 |

ClientCore's default skip was the opt-in local 15v15 MCP showcase. It was then
explicitly enabled with `ALLEGIANCE_LIVE_P660_SHOWCASE=1` and passed **3/3**,
including 30 bot migrations, both human sessions staying connected, and finale
rearming. MonoGame's three skips are the existing absent optional Meshy-pack
cases; no skip condition was changed.

After all suites finished, both ClientCore failures passed **3/3 exact-method
isolated reruns**: `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`
and `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`.
These meet the brief's load-flake rule. The lobby relay failure seen in the
discarded temporary-output Core attempt also passed 3/3 individually.

**Remaining test limitation:** Core's
`StationBucketSpawnsConstructorLiveTests.Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds`
timed out waiting for construction to start. The first isolated batch passed
2/3; a fresh second batch passed 1/3, for **3 passes / 3 failures across six
isolated runs**. This test was already recorded in `docs/TEST_FLAKES.md` under
P655b. It uses the server/bot construction path, not the changed client clock.
It is recorded as intermittent and unresolved, **not** relabeled a verified
load flake. No batch failed 3/3. Its fixture, assertion, deadlines and the
server/bot construction code remain unchanged.

The full suite failures and all isolated attempts are retained in the
[invocation ledger](P695_assets/results.json) and
[flake ledger](../TEST_FLAKES.md). The historical failing-first logs are
[clock](P695_assets/baseline-clock.log) and
[flight](P695_assets/baseline-flight.log).

Final suite commands (after loading `local.env`):

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p695/nuget-http-cache dotnet build AllegianceCore.slnx -m:8 -p:UseSharedCompilation=false
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
```

Exact focused and rerun filters are in the invocation ledger. The task's
three-full-suite sequence completed; the remaining constructor intermittence
is disclosed rather than represented as a clean full-suite pass.

Build/test evidence and raw TRX files: `/tmp/p695/`. The initial shared
compiler could not write default workspace output, so the focused runs used
`--artifacts-path /tmp/p695/artifacts`. An initial unfiltered Core attempt exposed
repository guards that require their assemblies beneath the repository root;
that temporary-output attempt was superseded by a workspace-output run.
The interrupted runner continued in a child process; its temporary-output
results are excluded from the final suite totals. Its first Core result is
preserved as `/tmp/p695/verification/outside-repo-Core.trx`. Final TRX assembly
paths are checked explicitly to identify the workspace-output runs. This was an output-layout error, not a product
failure or a load flake. Final suites use normal workspace output after a clean
successful build with `-p:UseSharedCompilation=false` and
`NUGET_HTTP_CACHE_PATH=/tmp/p695/nuget-http-cache`. No guard, audit, assertion or
suite filter was relaxed. The affected repository guards are also repeated in
the corrected layout.

Commands use `local.env` for the installed artwork/native corpus. Rig/221/200v200
runs are reserved for the other lane and were not run here.


## P695b — persistent small-error corrections

Base: `master 5b046d80`, working branch `codex/p695b`. Changes remain in the
working tree; no commits or pushes. No rig / 221 / 200v200 run was attempted.

### Rejection check and native comparison

`Server.Dispatch.ProcessPeerShipUpdate` checks three independent movement
conditions after replaying the reported pose to `SimNow`: speed, distance from
the current authoritative pose, and travel credit measured from the previous
accepted pose. The spatial envelope starts at **20m**, with the existing bounded
latency allowance. A **1.8m mean rejection distance is not a 1.8m threshold**:
that diagnostic describes distance from the server, even when speed or travel
credit caused the rejection.

Native source was read locally under `~/Development/Allegiance/src/`:

| Native rule | Source |
| --- | --- |
| Match the current cookie; require a ship in a cluster without a parent | `FedSrv/FedSrv.CPP:4851-4864` |
| Require increasing embedded timestamps; remember a timestamp only after acceptance | `FedSrv/fsship.cpp:1173-1183` |
| Compare the embedded server timestamp with the ship clock, within 1.5s in either direction | `Igc/shipIGC.cpp:3139-3148` |
| Replay to server time, always accept orientation, then accept position/velocity when squared position error is below 400 (distance below 20m) | `Igc/shipIGC.cpp:3175-3192` |
| On rejection export the authoritative pose and send a new-cookie SHIP_RESET | `FedSrv/fsship.cpp:1192-1201` |
| The client applies the reset pose and adopts its cookie | `clintlib/appmsg.cpp:1179-1190` |
| Integrate velocity using seconds and thrust × dt / mass; ramp turns before computing thrust/drag | `Igc/shipIGC.cpp:2852-2917` |
| Use GA-scaled engine thrust and booster thrust in the boost ratio | `Igc/shipIGC.cpp:2863,2942-2956`; `Igc/afterburnerIGC.h:113-115` |
| Replay a future pose linearly backward; replay a past pose in bounded steps with velocity updated before position | `Igc/shipIGC.cpp:3244-3287` |

The cumulative travel budget and speed guard are port hostile-client additions;
native's acceptance path does not impose this cumulative budget. This change
retains those guards and their existing margins rather than claiming native has
a matching anti-teleport algorithm.

### Why corrections could continue forever

Two independently reproduced defects were found:

1. **A correction left the server's travel anchor behind.** Game1 already
   consumes `HasPendingReset`, seeds position/velocity/orientation/turn rates
   from the reset and echoes the new cookie. The server, however, retained
   `LastPosePosition` from the last accepted client update. Its ship continues
   flying during rejected updates. Once that authoritative ship travels farther
   from the old anchor than the bounded credit cap, even a perfectly corrected
   client cannot pass. A 1.8m error against the current server pose can coexist
   with an arbitrarily large distance from that old anchor. Resetting only the
   client cannot remove this bias.
2. **The boost speed guard used inconsistent thrust units.** Movement physics
   scales both engine thrust and afterburner thrust by the side's thrust GA.
   `MovementSpeedLimit` scaled only engine thrust. For a thrust GA above one,
   its legal boost ceiling was too low. At GA=2 with booster thrust equal to
   raw engine thrust, physics targets twice cruise, while the old guard allowed
   only 1.5 × cruise before its existing margin. The regression's legitimate
   1.9 × cruise update was rejected on the starting code.

The supplied clash54 client log contains 1,107 reset messages (the brief
quoted an earlier 960-reset snapshot). It shows hull 15; the server log shows
frozen accepted=1138 and no out-of-date/invalid updates during the rejection
storm.
They contain summaries rather than per-packet speed/travel data, so they do
not prove which condition initiated that session's first rejection. The stale
anchor explains a reproducible persistent rejection state; the GA mismatch is
a separate reproducible legitimate-flight trigger. New non-exclusive
`reject-reasons[speed=… envelope=… travel=…]` summary counters make future
sessions distinguish these cases without verbose packet logging.

P695's time-scale multiplication was audited: Game1 steps by wall seconds ×
advertised TimeScale, and the server advances SimNow by its period × TimeScale.
Both use `ShipKinematics.Step`, effective hull/side stats, turn-rate integration
before thrust/drag, and updated velocity before position. Own-ship frames are
subdivided to at most 0.05s; server replay uses the existing mission quantum.
Their discretization/control-arrival differences can create bounded pose
errors, which the native tolerance accommodates. The wire clock uses
milliseconds and the integrators use seconds. This task makes no further clock
or integration-order change.

### Changes and protection checks

On rejection the travel anchor now becomes the same **authoritative server
position** sent in SHIP_RESET. Remaining travel credit and its refill clock are
retained. Neither the rejected client position nor a fresh 20m allowance is
adopted. The boost bound now applies the same thrust GA as the real flight
model. Native spatial tolerance, bounded latency envelope, time/cookie/replay
checks, same-tick gate, speed margin, coasting cap and total travel budget are
retained.

Two failing-first regressions demonstrate acceptance of a corrected pose after
a stale anchor and acceptance of legal GA-scaled boost. The hostile regression
alternates small moves with rejected teleports at 10ms intervals and verifies
that repeated new cookies cannot refill the travel allowance. Existing P655
teleport, speed, replay, packet-loss, coasting-rounding and reposition tests are
included in focused verification.

The new loopback soak runs an actual ConnectionSession, real production Game1
clock/step/send/reset methods, and a real server at 20Hz for at least **605 wall
seconds**. It uses clash54's hull 15 and a real mounted booster, delivered by the
owner loadout wire message, with thrust GA=2. Continuous turns, throttle cuts,
boost/fuel depletion, scales 1 / 0.85 / 0.5, and an injected 500m divergence
exercise acceptance and actual Game1 correction recovery. It checks that
acceptance never stalls for five seconds, corrections stay below 20, accepted
exceeds 3000 and rejected × 100, clocks remain in window, and no invalid pose or
simulation-stage failure occurs. The full suite retains the same ten-minute
duration.

### Verification

The final solution build succeeds with **0 warnings and 0 errors**. NuGet's
HTTP cache was redirected to `/tmp/p695b/nuget-http-cache` because the default
cache is outside this sandbox's writable roots; audit/restore remained enabled.

Short focused verification passed **81 exact-method invocations / 90 case
executions**, each selected method three times in a separate process invocation:

| Assembly | Invocations | Passed cases |
| --- | ---: | ---: |
| Core (all H-11 methods and P507 acceptance/wire tests) | 57 | 60 |
| ClientCore (native flight/reset and own-ship simulator) | 21 | 21 |
| MonoGame (all P695 rotation/scale cases) | 3 | 9 |

The first two soak fixture attempts exposed fixture defects: reflection selected
an overloaded SendPeer ambiguously, then an outside-universe spawn caused real
boundary damage/ejection. The final fixture selects the byte-array overload and
flies a horizontal circle near the sector centre in hull 15. Neither attempt
was a product regression or classified as a load flake. The initial compile
also caught an unavailable server TimeScale accessor; the fixture now tracks
its requested rate changes. The failing-first product regressions are separate
from these setup failures.

All **three exact-method soak invocations passed**. Each flew **605.0 real wall
seconds**, with **3,739 accepted / 1 rejected / 0 out-of-date / 0 invalid**, one
received/applied reset, and 406 frames of actual booster power. The only
rejection in each run was the injected 500m divergence (envelope and travel
reasons); there were **zero spontaneous corrections**. Acceptance continued for
more than nine minutes after recovery, including both slower time scales.
The three isolated invocations used separate test processes and ran concurrently
on the same machine; no other test method ran within any soak invocation.

Together with the short checks: **84 focused invocations / 93 passing case
executions**, with no final focused failure or skip. Flight counters and regular
server summaries are retained in [run 1](P695B_assets/soak-3-flight.log),
[run 2](P695B_assets/soak-4-flight.log), and
[run 3](P695B_assets/soak-5-flight.log). The two failing-first product regressions
are in [baseline output](P695B_assets/baseline.log). The
[invocation ledger](P695B_assets/results.json) records commands and counters;
raw TRX files and all scratch logs remain under `/tmp/p695b/`.

The **unfiltered full suites ran sequentially after focused verification**:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,561 | 0 | 0 |
| ClientCore | 1,026 | 0 | 0 |
| MonoGame | 2,207 | 0 | 3 |

ClientCore enabled `ALLEGIANCE_LIVE_P660_SHOWCASE=1`, so the local 15v15 showcase
ran and passed. MonoGame's three skips are the existing optional private Meshy
pack tests; that pack is not configured on this workstation. No skip condition
was changed. There were **no full-suite failures**, so no load-flake
classification or full-failure rerun was needed, and `docs/TEST_FLAKES.md` is
unchanged.

The fourth ten-minute flight, inside the full MonoGame suite, also passed:
**605.0 real wall seconds, 3,740 accepted / 1 intentionally rejected / 0
out-of-date / 0 invalid**, one applied reset and 406 booster-power frames. Its
[flight output](P695B_assets/full-Allegiance.ClientCore.MonoGame.Tests-flight.log)
shows continued acceptance across all three scales. Across all four long runs
there were **zero spontaneous corrections**.

The [ledger](P695B_assets/results.json) includes the exact commands, full-suite
counters, skipped case names, baseline failures, fixture-development attempts,
and isolated results. Raw full-suite output is retained for
[Core](P695B_assets/full-Allegiance.Core.Tests.log),
[ClientCore](P695B_assets/full-Allegiance.ClientCore.Tests.log), and
[MonoGame](P695B_assets/full-Allegiance.ClientCore.MonoGame.Tests.log).
[Build output](P695B_assets/build.log) confirms the clean final build. Raw TRX
files remain in `/tmp/p695b/`.

Build and suite commands (logger/result-path arguments are in the ledger):

```sh
set -a
. ./local.env
set +a
NUGET_HTTP_CACHE_PATH=/tmp/p695b/nuget-http-cache dotnet build AllegianceCore.slnx -m:8 -p:UseSharedCompilation=false
dotnet test tests/Allegiance.Core.Tests --no-build
ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
```
