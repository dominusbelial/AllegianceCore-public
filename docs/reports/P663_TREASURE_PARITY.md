# P663 — treasure parity

Implemented ambient treasure generation, neutral-artifact respawn, drifting loot,
client motion, and the corresponding SIM-0129 / SIM-0135 matrix rows. Changes are
left in the working tree; no commits or pushes.

## Native rules and implementation

All native paths below are relative to `~/Development/Allegiance/src/`.

| Rule | Native evidence | Port |
|---|---|---|
| At payday use interval + overdue time, keep the payday phase, accrue each sector's home/neutral rate, consume the integer count, retain the fraction | `FedSrv/fsmission.cpp:2514-2519,2554-2581` | `Mission.Update`, `Mission.Treasure.RegenerateTreasures`, `Sector.PendingTreasures` |
| Mission rates default to 0.2/minute home and 0.5/minute neutral; regeneration/start set IDs default to 1; -1 disables a set | `Igc/igc.h:1160-1161,1180-1183,1287-1290,1321-1322`; `Igc/missionigc.cpp:4129-4149` | `TreasureParameters`, `GameConfig.Treasure`; selected tables come from the mission's loaded `StaticCore.TreasureSets` |
| Read payday interval from the mission's constant slot 6 | `Igc/igc.h:65`; `FedSrv/fsmission.cpp:2516` | `Mission.PaydayInterval`, loaded-core value with native 60-second fallback (`Igc/missionigc.cpp:2691,2746`) |
| Select entries by summed chance weights | `Igc/treasuresetigc.h:100-117` | `Mission.GenerateTreasure` |
| Suppress cash when developments are disabled; placement is a squashed disk with radius 5000 and lens 0.2; radius grows 5% on collision; three ordinary placement attempts and effectively unlimited artifact attempts | `Igc/missionigc.cpp:2685-2686,4152-4197`; direction distribution: `zlib/vector.cpp:37-48` | `Mission.GenerateTreasure`, including live models, stations, asteroids and warps in clearance checks |
| Clearance is sqrt(10) times model radius plus 500 for ships or 25 for other objects | `Igc/missionigc.cpp:4172-4186` | Generator clearance checks |
| Ordinary treasure lasts 300–600 seconds, flags/artifacts ten days; initial velocity is zero | `Igc/missionigc.cpp:4200-4214` | Generator data and expiry |
| Random launcher quantities 1..core amount, packs half..full core amount, other parts zero; cash amount equals payload | `Igc/treasureIGC.cpp:46-92`; launcher amount without ship: `Igc/launchertypeigc.h:130-143` | Generator amount normalization; treasure ACK also resolves real core launcher types |
| Initially generate requested home/neutral treasure counts, plus an artifact per neutral sector in artifact games | `Igc/common.cpp:3081-3107` | Server assembly calls `Mission.PopulateTreasures` for procedural maps; existing count config is now functional |
| Respawn artifacts in any random neutral cluster; the native predicate does not exclude clusters already containing flags | `FedSrv/FedSrv.CPP:13842-13868` | `Mission.RespawnArtifact`; server's previously deferred sentinel branch now calls it |
| Respawn on secure, out-of-bounds carrier death, and treasure expiry; pickup clears/suppresses the respawn payload | `FedSrv/FedSrv.CPP:13161,11730-11753,12493-12509,13562-13576` | Dock-secure path, existing death path, expiry site hook; pickup still consumes without spawning another |
| Decelerate at 10 units/s²: p(t)=p0+v0(t−5t²/\|v0\|), stop at \|v0\|/10, final p=p0+v0\|v0\|/20 | `Igc/treasureIGC.cpp:18,95-116,221-259` | Shared `TreasureMotion`; simulation position and instantaneous velocity; average `CollisionVelocity`, relative swept-segment pickup, stationary state |
| Expiry is creation epoch + lifespan, including late imports | `Igc/treasureIGC.cpp:35-42,197,215-218` | `Treasure.CreatedAt` / `ExpireAt`; export time is server simulation milliseconds |
| Export the original p0/v0/time0, then simulate that predictable trajectory on the client; export existing treasures on sector entry | `Igc/treasureIGC.cpp:268-277,221-264`; `FedSrv/fsmission.cpp:4224-4231` | Existing `TREASURE(28)` wire data carries original trajectory; client entity predicts via the shared curve and PING-based simulation clock; join/warp/undock export existing treasure snapshots |

The simulation keeps end-of-step positions, matching this port's model update
convention. Native stores a start position plus average velocity for its later
collision integration (`Igc/treasureIGC.cpp:239-259`). The port retains previous
position and average velocity and tests the same relative swept segment, so a
moving treasure can hit a picker even if neither endpoint overlaps. It becomes
stationary at the analytical stopping position; this port has no native KD-tree
static-set migration to perform.

No new wire message or per-frame treasure position stream is necessary. Creation
and entry exports retain the original trajectory, as native does. The existing
renderer reads the moving `TreasureEntity.Position`. PING samples anchor the
client to server simulation milliseconds. Moving entry exports include an immediate
clock sample; empty and static-only snapshots do not inject unsolicited PINGs
into own-ship clock calibration. Time-scale changes integrate the
previous rate before changing the prediction rate. Entry snapshots clear stale
previous-sector treasure state. Generated treasure routing and destruction are
scoped to the owning mission to handle repeated sector/object IDs across games.

Configuration example (per entry in `games`, rates in treasures/second):

```json
"treasure": {
  "playerSectorTreasureRate": 0.0033333334,
  "neutralSectorTreasureRate": 0.008333334,
  "playerRegenerateSet": 1,
  "neutralRegenerateSet": 1,
  "playerStartSet": 1,
  "neutralStartSet": 1,
  "allowDevelopments": true
}
```

Set a rate to zero to disable that sector class, or a regeneration set to -1 to
consume accrual without creating treasure, matching the native generator's early
return. Missing core set IDs also return without spawning. Rates are validated
against the native maximum 1/60 per second (`Igc/igc.h:1384-1390`). Procedural
`mapGen.playerSectorTreasures`, `neutralSectorTreasures`, and `artifactsGame`
control initial runtime population. Artifact victory still uses the existing
mission enable/goal settings; population does not enable a zero-goal win.
Serialized map files retain their existing
static-map format; runtime treasures are not added as static map records.

## Verification

No rig / 221 / 200v200 runs are included. The final solution build succeeded
with **0 warnings and 0 errors**. Across the focused and affected regression
and load-flake checks, **90 distinct test methods have three completed isolated passes each**
(including every drift theory case on each invocation). New/modified treasure
coverage is 14 methods / 18 executed cases per repetition. The final focused
pass additionally checked the existing PING echo, undock re-seed, and two-warp
pod autopilot regressions.

Verification caught and fixed unsolicited PING samples from empty/static-only
snapshots. After restricting immediate clock samples to moving entry exports,
the echo and three-cycle undock tests both pass 3/3. The pod proof has three
completed isolated passes: two warp transits, successful rescue docking, **zero
pose rejects and zero SHIP_RESETs** each. Two earlier MSBuild-runner invocations
ended without a test-result summary and were not counted as passes; direct
VSTest with diagnostics and a 15-minute session deadline completed the remaining
repetitions. No product timing rule or pod test assertion was relaxed.

The real-core quantity test's initial assumption that stock tables select all
three part categories was corrected: a loaded table referencing real PCore
launcher, pack and weapon types exercises custom-core tables as well.

Final full suites ran sequentially after the build and isolated checks, with no
lane exclusion. The opt-in local 15v15 showcase
(`ALLEGIANCE_LIVE_P660_SHOWCASE=1`) and existing compiled Meshy pack
(`ALLEGIANCE_MESHY_PACK_PATH`) were enabled, so **no tests were skipped**.

| Final suite | Passed | Failed in suite | Skipped | Isolation outcome |
|---|---:|---:|---:|---|
| Core | 4,213 | 0 | 0 | Clean |
| ClientCore | 898 | 0 | 0 | Clean, including the 15v15 MCP showcase |
| MonoGame | 2,122 | 1 | 0 | Ripcord live case passed 3/3 alone: load flake per brief |
| Total | 7,233 | 1 | 0 | No reproducible failure remains |

The MonoGame suite's raw exit code is 1; it is not reported as an unqualified
clean suite. Its only failure was
`RipcordRequestLiveTests.RipcordKey_Initiate_Countdown_Teleport_Deny_Cancel_EndToEnd`
(`client ripcord state clears on RIPCORD_ABORTED`), followed by three individually
filtered successful runs on the same build. The flake is recorded in
`docs/TEST_FLAKES.md`, alongside the initial pass's separately verified load
flakes. No assertions or quarantine traits were changed.

The enabled showcase passed with **30/30 bots migrated, 2/2 human sessions
connected, and 0 sim exceptions** (27.300 simulation seconds, 20.059 wall seconds
for its measured scenario). The final pod case also passed in the full MonoGame
suite.

Reproduction commands (with `local.env`, artwork/native paths, and the two
optional-test environment variables above loaded):

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p663-nuget-http dotnet build -m:8 -v:q
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
# Each selected method was separately invoked three times:
dotnet test <project> --no-build --filter FullyQualifiedName=<method>
```

Final TRX files and suite/retry logs are under `/tmp/p663-suites-final/`;
individual focused logs are under `/tmp/p663-final-run/`. The two diagnostic
pod completions are recorded under `/tmp/p663-pod-direct/` (the third completed
pass is `/tmp/p663-final-run/`'s pod run 3). Matrix CSV parsing verified all
473 unique rows retain the 16-column schema. The whitespace check uses
`git -c core.whitespace=cr-at-eol diff --check` because the existing matrix uses
CRLF line endings.

Tests added:

- `P663TreasureParityTests`: 180-second generation window, fractional home/neutral
  rates, loaded-core payday interval, overdue cadence, weighted core selection,
  cash/set suppression, placement bounds, initial population and random neutral
  respawn, native drift/deceleration at five times, late export, step partition
  invariance, expiry, exact millisecond export preservation, initial collision velocity, blocked
  placement, real-core launcher/pack/hardware amounts, and swept pickup across
  non-overlapping endpoints.
- `P663TreasureMotionTests`: codec + world dispatch, late moving export, client
  curve and stop, destroy, changing simulation time scale, entry snapshot and reset.
- `P663TreasureLiveTests`: real loopback clients receive initial artifact exports,
  payday-generated core treasure and artifact respawns after capture, expiry and
  out-of-bounds death.
- Extended `TreasureDropLiveTests`: a real client sends DROP_PART, receives the
  original trajectory, sees matching moving position and velocity, and a second
  client joining later receives the existing trajectory at its current age.

The generation-window fixture uses a loaded five-second payday interval and
mission rates 1/120 (home) and 1/60 (neutral), with different core set IDs. At
180 seconds it has generated **1 home + 3 + 3 neutral treasures**, retaining
**0.5 / approximately 0 / approximately 0** pending fractions. Before the last
payday each neutral sector still has only two generated treasures
(`FedSrv/fsmission.cpp:2554-2581`).

Drift checks pin these samples for initial speed 100, against
`Igc/treasureIGC.cpp:18,239-259`:

| Elapsed seconds | Distance traveled | Speed |
|---:|---:|---:|
| 0.25 | 24.6875 | 97.5 |
| 2 | 180 | 80 |
| 7 | 455 | 30 |
| 10 | 500 | 0 |
| 13 | 500 | 0 |

Build commands use `NUGET_HTTP_CACHE_PATH=/tmp/p663-nuget-http` because the default
NuGet HTTP cache is outside this sandbox's writable roots. `local.env` supplies
the real artwork and native source paths for tests.
