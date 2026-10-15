# P689 — crowded-sector station death

Date: 2026-10-15. Base: `integ8`, `86770578784fd2efb84d7d07f541bfa150584dc1`.
Changes are left in the working tree; no commits or pushes.

## Result and scope

The local reproduction meets the **under 100 ms** target. The final three isolated
runs give a 150-ship station-death median of **6.49 ms**, and a deliberately lethal
150-ship / 150-missile stress case with loot and two real TCP observers has a
median of **11.84 ms**, maximum **26.16 ms**. The test ceiling is a generous 250 ms.

**The reported 1.16 s production combat stall was not reproduced or conclusively
attributed.** The original collision path was already below 100 ms in this local
fixture. This patch removes measured duplicate collision work and adds the missing
sub-step instrumentation; it is not evidence that the original clash44b stall is
resolved. No rig / 221 / 200v200 run was attempted, as instructed by the lane brief.

The station itself does not create a damaging death explosion in the current port.
The station-only cases record no blast, kill/eject, or treasure work. The native
station death path also signals the death and terminates the station, while its
client adds a visual explosion; see the native references below. Therefore the
suggested station → ship-death-explosion cascade is not the mechanism demonstrated
by the supplied log. A weapon's **own** area blast can still kill a station and
nearby ships, which the separate lethal stress case exercises.

## Reproduction and measurements

`tests/Allegiance.Core.Tests/P689StationDeathTickTests.cs` boots the real server and
loads `PCore001.igc` and its real CVH hull cache. It replaces the first sector's
stations/asteroids/models with a controlled population, retaining the map's other
sectors and warps. A garrison (`Garrison (Str)`, `ss24a`, radius 423.5) faces
100 or 150 alternating-side ships: `Adv Stl Fighter` (`quizsfv2`, length 25.2) and
`Hvy Bomber` (`faohshbmb`, length 40). Seed 689 concentrates them in a 100-unit
cube outside the base. Hull presence is asserted, so this cannot silently time
sphere fallbacks. Ship velocity is zero; ordinary physical collision handling
still runs.

A loaded projectile record is given lethal direct power and launched from outside
the station towards it. The station must actually die through collision/damage
dispatch. The ordinary case has zero projectile blast power: all ships must remain
alive at full hull, and there must be no kill callbacks. The stress variant gives
that projectile a lethal 5,000-unit blast, enables friendly fire, mounts three real
part records per ship (weapon, afterburner, cloak), fills ammunition/fuel, and arms
150 loaded missile records with explicit synthetic death-blast power/radius. It
asserts all 150 ship deaths in registration order, all 150 missile destruction
callbacks in order, and that secondaries drain during the call. Real randomized
part/ammo/fuel treasure creation runs; loot counts are recorded, not made into a
deterministic checksum. The stress inputs are explicit synthetic overrides, not a
claim that the stock garrison or missile has those explosion statistics.

The loopback variant seats two actual bots on opposite sides, outside the blast.
Both must receive exactly one station destruction and all 150 kills in the same
order. Socket receipt waits are outside the measured interval. Victims are NPCs;
the new stress fixture does not simulate 150 player ejections or a 392-peer fanout.
Existing ejection/respawn tests run in the full suites. Crowding hull drain
(`Igc/clusterIGC.cpp:162-198`, `Igc/shipIGC.cpp:408-424` in the native checkout)
is disabled only in this fixture to isolate combat outcomes.

Measurements use the default Debug / net10.0 build on the shared workstation.
Timing covers one complete **`Sector.Update`**, including synchronous server-site
callbacks, treasure creation, encoding, and sends, with the profiler enabled.
Server boot, core loading, connection setup, and the subsequent receipt wait are
excluded. Each case has one warmup and three fresh measured sectors per test
invocation. Three isolated invocations provide nine final samples per case.
This is not an end-to-end 392-peer `SimPass` or a replay of Organia's original
positions, velocities, weapons, and loadouts.

The matched before measurement temporarily disabled the three new sector pair
filters, retaining instrumentation and the same nonlethal fixture. It passed all
four then-current cases, and the fixed source was restored before the final build
and validation. The lethal before measurements preceded addition of loaded loot
parts, so they are deliberately excluded from the before/after comparison.

| Scenario | Before median (3 samples) | Fixed median (9 samples) | Fixed maximum |
| --- | ---: | ---: | ---: |
| 100 ships, station death only | 4.873 ms | 3.377 ms | 5.611 ms |
| 150 ships, station death only | 8.736 ms | 6.487 ms | 6.859 ms |
| 150 ships + 150 armed missiles + loot | — | 7.756 ms | 21.994 ms |
| Same stress, two real TCP observers | — | 11.836 ms | 26.155 ms |

The station-only combat detection bucket fell from **1.448 to 0.235 ms** at
100 ships and **3.026 to 0.443 ms** at 150 ships. This was the dominant measured
combat cost in those cases. The remaining whole-sector cost includes legitimate
ship/ship physics, which is retained.

Median stress buckets (milliseconds, nine samples each):

| Bucket | No peers | Two loopback peers |
| --- | ---: | ---: |
| `combat.build` | 0.366 | 0.354 |
| `combat.detect` | 2.435 | 2.608 |
| `combat.dispatch` | 4.579 | 8.125 |
| `blast.resolve` | 4.523 | 8.014 |
| `blast.apply` | 4.479 | 7.957 |
| `blast.evaluate` | 0.020 | 0.065 |
| `blast.damage` | 2.200 | 5.049 |
| `blast.secondary` | 2.012 | 2.355 |
| `combat.killEject` | 1.965 | 4.534 |
| `combat.shipEvents` | 1.917 | 4.353 |
| `combat.treasure` | 1.612 | 2.247 |
| `combat.stationEvents` | 0.010 | 0.018 |

These are **inclusive, nested buckets**, not additive phases. For example,
`blast.damage` includes kill callbacks, and `combat.shipEvents` includes treasure
and sends. `blast.secondary` measures application of queued secondary blasts;
`blast.resolve` also includes nested enqueue-only calls. No part of these small
numbers explains the observed production second. The new buckets remain available
to the existing tick profiler for the separate rig investigation.

Raw per-sample bucket values and loot counts: [profiles.json](P689_assets/profiles.json).
Final commands, exit statuses, counters, and failure details:
[verification.json](P689_assets/verification.json). Full logs/TRX are in
`/tmp/p689-verification/` on this machine.

## Fix and preserved behavior

The port previously ran the same overlapping ship/ship hull combinations in its
combat detector, then discarded them because neither collider was ordnance.
Ship/asteroid and ship/station passes similarly solved type combinations their
own dispatch loops immediately discarded. Dense sectors paid for repeated convex
hull/GJK work, candidate storage, collision queue insertion, and sorting.

`BroadPhase.OverlappingPairs` now accepts an optional pure pair predicate. It
applies that predicate after spatial AABB rejection, before storing/sorting
candidates and before the narrow phase. `CollisionDetector.Detect` forwards it;
default callers retain their original behavior. Combat selects only ordnance
against ships, probes, asteroids, or stations. The two physical passes select
only their respective cross-type pairs. Ship/ship physics remains in its owning
pass. This reuses the existing spatial index and batches the removal of irrelevant
pairs instead of adding a second index or changing the convex solver.

Only immutable type categories move into that filter. Friendly-fire, death,
launcher, damage, dud, and other mutable gameplay checks remain in dispatch, where
earlier events can change their inputs. Retained colliders keep their original
ordinals; predicates receive registration order even when the spatial sweep visits
them in reverse. Contact times, sub-shapes, and collision tie ordering are unchanged.
No damage, treasure, network audience/order, explosion scheduling, or native caps
were altered. Profiling scopes are no-ops when the existing profiler is disabled.

`P689CollisionPairTests` compares the filtered real-hull event sequence against
**the original unfiltered detector followed by an independent copy of the old
combat dispatch type gate**, at 30 and 150 mixed bodies. The comparison includes
collider identity, contact times, sub-shapes, and exact ties. It also checks the
candidate stream against the existing brute-force AABB oracle and exercises an
asymmetric filter with spatial order reversed. Rejected type combinations must
actually be present, and the default unfiltered API must still emit all 780 pairs
in the 40-body overlap fixture.

## Native rules checked

All paths below are relative to `~/Development/Allegiance/src/`; line numbers were
read directly from that checkout.

- **Station death has no damage-radius blast of its own:**
  `Igc/stationIGC.cpp:189-194` invokes `KillStationEvent` after armor reaches zero.
  `FedSrv/FedSrv.CPP:12237-12392` vacates the station, credits/broadcasts its death,
  and terminates it, without `CreateExplosion`. The visible station explosion is
  the client-side `AddExplosion` in `clintlib/appmsg.cpp:520-528`.
- **Ship death visuals are not automatically damaging blasts:**
  `clintlib/appmsg.cpp:2848-2857` adds the small/big ship visual on the kill message.
  The server's root-ship loot and eject decision are in
  `FedSrv/FedSrv.CPP:11644-11785`; this patch does not replace those with blasts.
- **Explosion inputs and scheduling:** `Igc/clusterIGC.h:340-365` stores the supplied
  damage type, amount, radius, time, position, and launcher and increments the
  explosion count. `Igc/clusterIGC.cpp:403-467` iterates against the current
  `m_nExplosions`, not a captured initial count, and clears it only at `:480`.
  Thus explosions appended during damage processing run in that **same pass**, in
  append order. Deferring them to a later tick would change that rule; no such
  deferral was introduced. Native starts this pass after collision dispatch
  (`clusterIGC.cpp:289-362`); the port's existing root `ResolveExplosion` calls
  remain synchronous at their call sites. This change preserves that pre-existing
  phase difference rather than claiming a complete scheduling-parity rewrite.
  The native fixed explosion-array capacity in `clusterIGC.h:348-349` is
  unchanged/outside this optimization.
- **Damage recipients:** `Igc/clusterIGC.cpp:369-400` snapshots ships, asteroids,
  stations, missiles, probes, and a separate mine list (native snapshot capacity
  500 per list). Membership is checked before damage at `:416` and `:444`.
  This patch does not introduce that cap or change the port's existing recipient
  snapshot behavior (a fresh snapshot per blast, with static stations/asteroids
  visited after the dynamic model list). Native caches its list once for the
  explosion pass; that existing port/native difference is not changed here.
- **Radius and falloff:** `Igc/clusterIGC.cpp:409-435` projects the target to the
  explosion timestamp, subtracts its radius to obtain surface distance, requires
  strict `d < radius`, gives full damage for `d <= 0`, otherwise multiplies by
  `(1 - d/radius)^2`, and passes `NoWarn | NoDebris`. Mines use the corresponding
  falloff and `ReduceStrength` at `:441-458`. All of that math is unchanged.
- **Missile secondaries:** `FedSrv/FedSrv.CPP:12514-12529` explodes killed missiles
  except the zero-position resonator sentinel; `Igc/missileigc.cpp:516-531` uses
  missile blast power, the side missile-damage attribute, and blast radius, or
  emits only a visual if blast power is zero. The existing FIFO drain is retained.
- **Collision ordering:** `Igc/clusterIGC.cpp:274-321` generates the shared native
  collision list, sorts by time, then dispatches it. This port splits combat and
  physical response into separate passes. Removing combinations a particular
  pass never dispatches avoids duplicate work without changing its retained list.

## Validation

The final build passed with zero warnings/errors. NuGet's default HTTP-cache
location was read-only in this sandbox; using
`NUGET_HTTP_CACHE_PATH=/tmp/p689-nuget-http` allowed a normal restore/build, including
vulnerability metadata, without weakening audit settings.

All seven new theory/fact cases passed **three separate method-filtered runs**:
four station/perf/loopback cases, two real-hull oracle cases, and one ordering case.
The related 118-case group passed **118/118 ×3**, including real live-socket tests:
`CollisionDetectionTests`, `P621ConvexHullCollisionTests`, `ShipAsteroidCollisionTests`,
`ShipShipCollisionTests`, `ShipStationCollisionTests`, `P649CombatBlastParityTests`,
`ExplosionPropagationTests`, `BlastOnImpactTests`, `StationCombatTests`,
`StationCombatLiveTests`, and `TickProfilerTests`. This retains existing squared
falloff, FF/repair, matrix/flag, moving-target, strict-boundary, lethal-once,
missile-secondary, zero-position-sentinel, and expiry semantics assertions.

Additionally, **all 82 distinct affected methods were each run alone three times**
(method filters include their theory rows): **246 fresh test invocations,
354 passing cases, zero failures/skips**. This is separate from the 118-case
three-run related-test batch and the seven new cases' three isolated repetitions.
The exact per-method ledger is [isolated-methods.json](P689_assets/isolated-methods.json).
No load flake was observed, so `docs/TEST_FLAKES.md` required no new entry.

The unfiltered suites ran sequentially after the build:

| Suite | Passed | Failed | Existing skips | Wall time |
| --- | ---: | ---: | ---: | ---: |
| Core | 4,562 | 0 | 0 | 548.6 s |
| ClientCore | 1,001 | 0 | 0 | 414.6 s |
| MonoGame | 2,186 | 0 | 3 | 265.9 s |
| Lobby | 19 | 0 | 0 | 1.0 s |

The three MonoGame skips are the pre-existing optional `MeshyPackTests`; their
private asset pack is absent. `ALLEGIANCE_LIVE_P660_SHOWCASE=1` enabled the local
15v15 MCP showcase, which passed, rather than leaving that opt-in case skipped.
The paced two-warp pod-rescue proof also completed: two real transits, successful
docking, zero pose rejects, zero resets. No suite failure required a load-flake
audit, and no assertions, timeouts, quarantine traits, or skip conditions changed.

To rerun the local reproduction with the workstation's existing corpus config:

```bash
set -a
. ./local.env
set +a
NUGET_HTTP_CACHE_PATH=/tmp/p689-nuget-http dotnet build -m:8
NUGET_HTTP_CACHE_PATH=/tmp/p689-nuget-http dotnet test tests/Allegiance.Core.Tests --no-build --filter 'FullyQualifiedName~P689' --logger 'console;verbosity=detailed'
```
