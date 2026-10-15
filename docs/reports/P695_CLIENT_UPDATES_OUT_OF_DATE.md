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
