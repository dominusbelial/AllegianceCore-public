# P707 — showcase parking after parity waves 8–10

Entry: `5556eb0f`, branch `codex/p707`. No commits or pushes; changes remain in
the working tree. Native citations are relative to
`~/Development/Allegiance/src/` and were checked against the local source.

## Root cause and native rules

The showcase's existing P700 two-second escape had two cancellation problems:

1. `SampleCadence` refreshed `LastMoveAt` on **any observer position change**,
   although the attack controller and idle report define movement using the
   **owning client's speed**, with a 1 m/s threshold. Tiny corrections and
   subthreshold drift could reset the escape timer while reported velocity was
   exactly zero. They could also conceal that interval in the idle metric.
2. Correcting that clock alone still let the **first moving echo cancel the
   escape immediately**. Native close-band speed matching then braked the ship
   back to zero. This produced short movement pulses between zero-speed holds;
   a 100 ms observer could miss the moving echo. A deterministic encoded-control
   regression reproduces the immediate cancellation without a busy machine.

Native explains the legal stop and the reported zero:

- Close-band matching sets throttle to `-1` when target forward speed is
  nonpositive: `Igc/shipIGC.cpp:2492-2511`.
- Compact velocity exports speeds below 1 m/s as zero; position is a separate
  vector: `Igc/igc.h:2282-2310,2444-2454`.
- Lateral thrust and the aimed/in-range fire gate use native controls:
  `Igc/shipIGC.cpp:2589-2602`.
- Direction buttons take the manual-thrust branch ahead of throttle cruise:
  `Igc/shipIGC.cpp:2965-3005`.

The two-second escape is an **existing showcase harness policy**, not a native
attack rule. Its new half-second hold is also harness policy. Native speed
matching, steering bands, firing predicates, thrust integration, and parity
waves P690/P691/P692 remain in place.

The entry's instrumented 15v15 run `repro-01` failed on Adv Scout 8122 targeting
Hvy Interceptor 8106. Its server clock kept advancing; it was alive, undocked,
with no parent or ripcord. It received throttle `-1` / `StateM=32768`
(`AllWeapons`), with server velocity decaying to approximately
`[8.99e-32,-4.39e-31,-1.20e-30]` and reported velocity `[0,0,0]`. A brief lateral
pulse reached only about 0.75 m/s in the sampled trace before stopping again.
Other failures occurred on different hulls with guns still mounted, so the
original two-dispenser-only inventory does not explain all observed holds.

This defect predates the waves: `BotManager.cs` is identical between pre-wave
`59fb0495` and entry `5556eb0f`. All five initial clock/control regression cases
fail on pre-wave production code. Pre-wave live run `base-02` also reproduced a
five-second stop on Adv Scout 8124: throttle `-1`, `AllWeapons`, advancing clock,
no docking/parent/ripcord, with a gun and shield mounted and three missiles
remaining. The earlier review's 0/7 base sample did not exclude this intermittent
base bug. These observations do not attribute a frequency difference to a
particular parity wave.

## Final change

`tools/allegiance-mcp/BotManager.cs` keeps all observer position changes in its
cadence statistics, but refreshes `LastMoveAt` from owning-peer speed **at least
1 m/s** when that sample is available. Peers without it retain the existing
position-based fallback.

When the existing stationary/friendly-line escape triggers, the controller now
keeps its existing lateral command for **0.5 seconds after the last qualifying
stationary sample**. A first moving echo therefore cannot immediately cancel
it. After the hold, normal native steering and speed matching resume. This
uses the existing native directional controls; it does not change ship velocity
directly or refill ammunition.

`tests/Allegiance.ClientCore.Tests/P707ParkingTests.cs` uses real wire-applied
client worlds and the existing private-controller fixture. Four methods / six
cases cover drift and idle reporting, the inclusive speed boundary, motion
without a position change, close-range escape despite observer drift, and a
first moving echo followed by sustained escape and return to speed matching.
The new echo regression fails on the initial candidate (`pulse-red`) and passes
with the held escape. The P700 showcase's five-second assertion, combat window,
objective, gunfire, damage and simulation-error checks are unchanged; only
server-state diagnostics were added.

## Investigation and rejected candidate

All repetitions used .NET 10.0.400, Debug builds, external paths from
`local.env`, real local TCP, and a fresh process per standalone invocation.
`ALLEGIANCE_LIVE_P660_SHOWCASE=1` enabled the local bots/humans next-match proof.
The pre-wave tree was exported with `git archive 59fb0495`; only diagnostic and
regression test files were added to that temporary tree. Runs were serial in
groups. Historical review counts (waves 3/12 failures; base 0/7) are separate.

| Production revision | Showcase passes | Parking failures |
| --- | ---: | ---: |
| Pre-wave `59fb0495` | 9 | 1 |
| Entry waves `5556eb0f` | 7 | 3 |
| Clock-only candidate | 10 | 0 |

The clock-only candidate passed 73 methods / 120 data cases three times alone
(219 test invocations / 360 case passes), including every wave test. It then
ran two diagnostic full-suite sweeps: **16,035 passes / 1 failure / 6 existing
optional Meshy-pack skips**. The second ClientCore sweep failed the original
parking test. Its three fresh isolated reruns were **2 passes / 1 failure**, so
it was **not classified as a load flake**. That recurrence led to the separate
moving-echo cancellation regression and the held escape above.

The initial candidate's two sweeps are retained as diagnostic attempts. They
are not final-code certification. The corrected revision was validated with the prescribed
two complete final sweeps; all four sweeps across both revisions remain in the
ledger. No failed attempt was discarded or assertion relaxed.

One preliminary selection filter mistakenly matched a nested `Dispose` helper
and ran zero cases. The verifier stopped on that empty selection. It was removed
from the corrected test cohort, and the empty result remains as a selection
audit in the ledger.

## Final verification

`NUGET_HTTP_CACHE_PATH=/tmp/p707/nuget-http dotnet build AllegianceCore.slnx --nologo -m:1 -v:q`
completed with **0 warnings / 0 errors** after the held-escape fix. NuGet audit
remained enabled. See the [final build output](P707_assets/build-final.log).

**Final affected cohort: 74 methods / 121 data cases**, each run three
times alone: **222 invocations / 363 case passes**, zero failures/skips.
This includes all four new P707 methods, every wave's own tests, P700 defence
and idle regressions, routing/cadence checks, real TCP weapon and order tests,
MCP death/rebuy/relaunch, and local 15v15 bots/humans next-match rotation.

**Corrected showcase: 10/10 standalone passes, zero failures/skips.** The largest
measured low-speed-with-target interval was **2.706 seconds**. The original
five-second assertion and 45-second combat observation were retained. Each pass
also checks objective arrival, actual gunfire/hits, and zero simulation-stage
failures. These ten runs certify the final held-escape revision; the earlier
clock-only candidate's ten are separately retained.

The final-code full suite ran **exactly two complete sweeps**, serially through
all four test projects in `AllegianceCore.slnx`. Each MonoGame sweep includes the
existing ten-minute human-flight soak. These are additional to the two rejected
candidate's diagnostic sweeps described above; all attempts are preserved.
The final sweeps total **16,037 passes / 1 failure / 6 optional skips**; the
single failed transport method subsequently passed all three isolated reruns.

| Final sweep | Assembly | Passed | Failed | Skipped | Wall seconds |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Core | 4686 | 1 | 0 | 760.118 |
| 1 | ClientCore | 1079 | 0 | 0 | 661.043 |
| 1 | ClientCore.MonoGame | 2234 | 0 | 3 | 934.800 |
| 1 | Lobby | 19 | 0 | 0 | 0.918 |
| 2 | Core | 4687 | 0 | 0 | 748.577 |
| 2 | ClientCore | 1079 | 0 | 0 | 654.881 |
| 2 | ClientCore.MonoGame | 2234 | 0 | 3 | 934.637 |
| 2 | Lobby | 19 | 0 | 0 | 0.930 |

Raw final-suite failures remain above and in the ledger. Each failed method
subsequently passed **3/3 fresh exact-method isolated runs**, with unchanged
assertions/deadlines, meeting the brief's load-flake rule. See `docs/TEST_FLAKES.md`.

- `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` — 3/3 isolated passes.

The 6 final-suite skips are the three pre-existing optional private
Meshy-pack checks in each MonoGame sweep. The pack is not configured. Native
assets were available; no required new/affected or selected local test was
skipped. Skip counts use per-test TRX outcomes; names/reasons are in the ledger.

The [complete invocation ledger](P707_assets/results.json) includes every test
command, counter, failure message/stack, source fingerprint and raw artifact
path across the investigation and final verification. Entry/pre-wave snapshots
are in [parking traces](P707_assets/parking-traces.log); the rejected candidate's
full-suite recurrence is in [its sampled trace](P707_assets/suite-2-parking-trace.json).
Raw TRX/console files are under `/tmp/p707/<run>/`. No failed attempt was discarded.

All live/loopback checks were local. Rig / 221 / 200v200 work was left to Claude
as instructed. Source fingerprints and `git diff --check` form the final audit.
No commits or pushes were made.
