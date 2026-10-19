# P745 — research finale siege

Completed: **5/5 finales ended by native station elimination**. Changes remain
in the working tree; no commit or push was made.

## Diagnosis

Primary classification: **(a) bot duty and navigation**, including spurious carrier relaunches. The native damage path
works; **(b) is not the explanation**. **(c) also applies**: thin volleys are
repaired, and an unreplenished four-missile loadout can run dry before the last
research base falls.

The unchanged local base was `93dec9c0`. The baseline loopback used PCore001, civs **25 versus 3**, research mode,
`researchTimeScale=64`, income multiplier 5000, the normal showcase 15v15 roster,
`m2xspiderw.igc`, and a two-minute finale maturity threshold. It used ordinary
research commanders, purchases, pod recovery, controls and warps. There were
no test-unlock grants or injected damage. It failed to destroy any station in
600.6 wall seconds (sim 1000.8); both original stations stayed at full health.

One traced bomber was `mcp-b002-8737`, ship **8100**:

- The research commander completed Bomber. The next normal purchase was accepted
  as hull **124, Bomber**, with **33 PW Gat Gun 1** on fixed mounts 0 and 1,
  **157 SRM Anti-Base 1** (four missiles), shield 43 and chaff 85.
- The finale reserved it, then made it active on its next pod purchase. Its
  station objective was **4328**, in enemy sector **1050**.
- It stayed in its own sector **1051**, orbiting the home asteroid field.
  The later instrumented reproduction showed the route
  `1051 -> 1055 -> 1054 -> 1050`; its first gate was warp 2171. No station
  weapon reached the target during this baseline; station shield/hull remained
  `1/1`.
- Reservations were also ineffective on surviving ships: preparation happened
  only on a future pod purchase. A fighter that had relaunched before bomber
  tech could retain that fallback life for minutes after the finale armed.
- Travel could be interrupted by a fighter hit. Collision avoidance steered the
  nose away from the route instead of applying native directional thrust.
  Finale preparation also discarded the showcase's distinct entry routes.
- The dodge feed included retained poses of departed ships. Native moves these
  cached objects into the null cluster; the research duty now uses only actual
  posed, visible, undocked ships.
- At the station, avoidance included the station being attacked and suppressed
  fire. Native attack explicitly skips its command target.

The next live run destroyed the enemy Garrison, then exposed the remaining
station-discovery failure. `OnStationBuilt` exported a new station only to its
owner, while `STATIONS_UPDATE` later sent an attacker its health fractions
without ever exporting its identity. The attacker's `ClientWorld.Stations`
therefore contained no replacement target, and its bombers switched to fighters.
The new loopback regression observes health for a newly spotted enemy station,
then requires its identity on both members of the seeing side, exactly once.
It failed before the correction and passes afterward. Station discovery now
exports each newly seen identity before its health update, respecting mission
and side visibility; initial map exports and owner construction exports seed the
same cache. Disconnects, mission changes and projection resets retire the cache.
The research duty can continue to an observed base in another sector when its
current sector has no remaining known enemy stations.

The carrier contact prerequisite was another directly reproduced delivery bug.
`ResolveCarrierLandings` called `InGarage` for every eligible pair in the sector.
The actual carrier bay planes are open-ended (the corpus `faohshcar` bay extends
through positive z), so a ship 3,000 units away passed `InGarage` and generated a
landing event. The new regression failed before the correction. Live traces
showed hundreds of repeated relaunches beside carrier 8140, keeping the bomber
wing near its own home. Native calls `InGarage` inside an actual ship collision;
the port now applies the hull-radius contact broadphase before the bay test.
It changes landing-event eligibility, not the flight or collision response laws.

After delivery and discovery were corrected, all five intermediate runs killed
a home station, but only one eliminated the side before the deadline. The
remaining fleets had spent their missiles on the earlier bases. In one trace,
eleven bombers targeted the last Supremacy while every launcher's last confirmed
server event read `ammo 1 -> 0`; their anti-ship guns could not damage it.
The research duty now recalls a missile-dependent strike when confirmed own
`FIRE_MISSILE` quantities exhaust the amount in its granted loadout (including
spare missiles). It continues fighting if it still has a loaded station gun.
The pilot flies home, docks, uses the native restock/purchase pricing and refund gates, and launches
through the same ACK path. A new full grant resets the observation baseline.
Requests, cooldowns, fire-control bits and missing lock never certify depletion.
The observation counter persists across warps and resets with the mission.

An additional generated-ring run produced real anti-base hits from bomber hull
404: at sim 493.3 the station shield went `1.0000 -> 0.6000`; at 508.75 another
hit overflowed shield into hull (`0.3405 -> 0`, hull `1 -> 0.9702`). Without a
sustained wing the station recovered. This independently rules out blanket
station immunity or missing station weapon damage.

## Native rules checked

All references below are relative to `~/Development/Allegiance/src/`.

- **Discover a newly seen station:** `FedSrv/FedSrv.CPP:10750-10778` exports
  newly visible predictable objects to the seeing side; the visibility edge
  calls `ShowObject` at `10945-10949`. Owner visibility also calls it
  (`10906-10910`); construction relies on that export (`11431-11432`).
- **End by station elimination:** `FedSrv/fsmission.cpp:3697-3715` checks for
  any remaining restart station after a loss; `5318-5427` deactivates the side
  and checks the winner. A single home-base kill is insufficient when research
  has built other restart bases. The proof requires native Stage Over and a
  deactivated side with no remaining restart station, not just a damaged or
  destroyed first target.
- **Carrier landing requires contact:** `Igc/clusterIGC.cpp:292-315` processes
  collision entries and verifies both models belong to this cluster before
  calling `HandleCollision`; `Igc/shipIGC.cpp:1023-1055` then checks friendship,
  carrier/lander capabilities and `InGarage`. Open bay planes alone never
  qualify a distant ship for landing. The added broadphase uses the existing
  native radius `0.5 * hull length` (`Igc/shipIGC.cpp:1419`) and no arbitrary
  exclusion distance.
- **Retired ships leave the dodge cluster:** `clintlib/appmsg.cpp:475-488` sets
  their cluster to null; `Igc/common.cpp:1715` scans cluster pickables only.
- **Station gates, matrix and overflow:** `Igc/stationIGC.cpp:126-133,137-145,159-204`.
  Friendly/allied non-repair damage is gated; the shield matrix entry must be
  nonzero and the station must have a shield pool. Shield overflow reaches hull
  with the exact native ratio at line 186. Crossing zero invokes the real kill
  event at lines 189-193. No shieldless damage fallback has been added.
- **Damage type flags:** `Igc/igc.h:559-565` and `Igc/missionigc.h:150-155`.
  The matrix indexes `type & 0x1f`; NoWarn/NoDebris/NoFlare are flags, not extra
  damage rows. `FedSrv/FedSrv.CPP:12185` applies NoWarn to alerts. It is not a
  station-immunity rule. The port's station damage lookup already masks correctly.
- **Actual ordnance:** `Igc/projectileIGC.cpp:185-194` applies the decoded power
  and existing side gun attribute before ReceiveDamage. The existing missile
  path and ammunition gates remain authoritative. Unit checks exercise real
  bomber gun/missile records against both civilizations' real home types.
- **Repair:** `Igc/stationIGC.h:189-208`: hull regeneration first; only when
  hull is full does shield regeneration run, divided by the effective maxima.
- **DamageTrack:** `Igc/common.cpp:2570-2665` records/ages attribution;
  `Igc/shipIGC.cpp:1320-1323` uses its leading bucket for ship kill credit.
  It is not a multiplier or a prerequisite for Station.ReceiveDamage; the station
  kill path above directly invokes KillStationEvent.
- **Attack target and dodge:** `Igc/shipIGC.cpp:2363,2470-2489,2600` skips the
  attack target, keeps the lead-facing attack law, and gates firing while dodging.
  `Igc/common.cpp:1754-1786` supplies directional thruster state from the closest
  approach vector. `common.cpp:2000-2016` faces the route while applying that state.
- **Angular stopping drift:** `Igc/common.cpp:152-246`, especially lines 210-246,
  brakes the current angular velocity before crossing the desired bearing. The
  old cross-product-only servo keeps commanding torque in the same direction
  and makes heavy hulls oscillate around launch areas. The research duty now
  supplies the hull/side dynamics and observed turn rates to the existing native
  turn controller; ship physics itself is unchanged.
- **Finale funding:** `FedSrv/FedSrv.CPP:5535-5544` accepts target NA to keep
  a pilot's own payday. A reserved pilot uses that native opt-out only when a
  legal refit exists but its wallet is short. The native next-match wallet reset
  (`FedSrv/fsmission.cpp:3493-3521`) prevents carrying this funding choice forward.
- **Static obstacle course and seek controls:** `Igc/common.cpp:2120-2224` chooses
  the nearest static/stationary blocker and constructs the tangent, using the
  native 2-unit ship padding and 10-unit diversion margin. Lines 2231-2235,2309
  gate forward throttle during wide turns and apply directional seek thrust.
- **Finite missile ammunition:** `clintlib/clintlib.cpp:3114-3120,3206-3229`
  subtracts the launched salvo quantity, then reloads from cargo or reports
  depletion. Server handling likewise consumes ammunition
  (`FedSrv/FedSrv.CPP:5509-5510`). The added client counter observes actual
  server launch echoes; it never changes the magazine or grants ammunition.
- **Ordinary dock/refit/launch:** `FedSrv/FedSrv.CPP:6189-6190,6247-6259,6282-6303`.
  The finale recall/refit decision is explicitly port orchestration, not a claim
  that native automatically schedules this showcase wing.

## Changes

Research finale duties now immediately use an already accepted station strike,
or return to an owned dock for an ordinary legal purchase once a strike is
available. They use the existing purchase pacer, CanBuy gates, full grant ACK,
launch and life reset. Partial grants resume flight without inventing a pod
cycle; they cannot certify the strike. No ship death or relocation forms the wing.

The committed station duty is preserved through incidental fighter hits.
An accepted fallback fighter is rechecked for a newly available legal bomber;
it can return to dock without a manufactured death.
Research strikes retain distinct entry wings, use the native tangent/RCS
navigation around home obstacles, and skip their station target during attack
dodge. Other modes retain their existing bot duty and steering policy. Empty station
missiles use that same real dock/refit duty; no direct restock or free reload is
introduced.

`[station-damage]` reports actual resolved hits: simulation time, defending side,
station, attacker side/ship/hull, projectile/part or missile identity, and shield
and hull fractions before/after. It is limited to one line per station per five
simulation seconds, with lethal hits always emitted. It also uses the existing
notable-event feed. No damage formula, repair rate, ship physics, research speed
or unlocked-mode setting is changed.

## Local proof and gate

The five final loopback runs used a native SingleRing research
showcase with civs 25/3, ordinary research branch rocks/constructors, and no
optional generic/He3 asteroid population. The dense ring and spider reproductions
are separate diagnostic runs. There are no station-health edits or damage boosts.
Native conquest/territory
flags retain their defaults; victory requires eliminating the enemy's actual
remaining restart stations.

All **5/5** ended by destruction of the last enemy restart station. Bravo
won each match through native side elimination; none used the deadline.
All five isolated invocations of the new live proof passed. Each asserts the
original mission reached Stage Over, a side was deactivated, no restart station
remained for that side, and no sim-pass stage failed. This cannot be satisfied
by P733's deadline, which leaves the sides active.

| Run | First home kill, wall s | Observed game over, wall s | Finale → last restart kill, sim s | Enemy stations destroyed |
| --- | ---: | ---: | ---: | ---: |
| 1 | 308.3 | 516.4 | 817.35 | 7 |
| 2 | 288.3 | 508.4 | 806.20 | 7 |
| 3 | 312.3 | 536.4 | 864.75 | 8 |
| 4 | 308.3 | 512.4 | 810.00 | 7 |
| 5 | 294.3 | 530.4 | 848.90 | 8 |

Wall times start after normal scenario launch and are sampled every two seconds.
The finale arms at two wall minutes; its 900-second backstop uses simulation
time. The final-column kill count includes research-built bases. Exact lethal
hit times, armed/deadline times, native elimination messages and complete fixture
settings are in [five-runs.json](P745_assets/five-runs.json); compact actual
station damage/discovery event logs accompany each run.

A fixed pilot trace is [ship 8103 in run 3](P745_assets/selected-pilot.log),
`mcp-b005-5632`, side 1. Its fully granted native **Bomber 124** carried **157
SRM Anti-Base 1** (four missiles) and **33 PW Gat Gun 1**. It received station
duty and crossed `2 → 3 → 0` through real warps. At sim **598.55**, its missile
hit the Garrison's hull (`0.2555 → 0.0974`); shield was already down. After
confirmed magazine depletion it took the resupply duty, survived the return
trip, docked at owned **9189 Outpost**, and bought/launched through the normal
ACK path. Its new loadout contained native **158 SRM Anti-Base 2** and **71 PW
Gat Gun 2**. It returned to sector 0 and at sim **1073.60** killed station
**9192 Supremacy** (`shield 0 → 0`, `hull 0.2499 → 0`). The server immediately
emitted `SIDE_INACTIVE(68)` for Alpha's last restart-station loss and declared
Bravo's native elimination win. An earlier gunfire death occurred before this
bomber life; no death occurred between its resupply order and the successful
return/refill/final strike.

**Rig recommendation: keep `finaleTimeoutSeconds: 900`.** The slowest final
local elimination took **864.75 simulation seconds after arming**, leaving
about 35 seconds. Do not shorten it to 300 based on the small map. The actual
107v107/spider rig still needs the dispatcher's run; local proof used 15v15,
fast research and income scaling, so this is a recommendation for that
validation, not a claim that larger-map timings were measured here. The timeout
remains the backstop; real duty, discovery and ordinary dock resupply produced these wins.

Validation:

- Whole solution: **0 warnings / 0 errors** (serial MSBuild; NuGet HTTP cache
  in `/tmp` because the workstation's default cache is read-only in this lane).
- Required linux-x64 self-contained **AOT publish succeeded**, no IL2026/IL3050.
- New/affected duty, navigation, native damage, telemetry, carrier contact,
  station discovery, projection/replay, construction and ammunition checks were
  run **3× separately per method**. The live siege results above also qualify
  the new live proof. P733's no-bomber regression passed three invocations,
  each including two deadline/next-match cycles.
- Never-parks: unchanged base **5/5**, final complete change **5/5**.
  Intermediate gates also passed 5/5.
- **Core:** initial sweep aborted after **173 passes** in the unchanged
  TcpTransport accept/stop race (`AcceptTcpClient`: "Not listening"). The last
  completed loopback method passed 3/3 alone. Only incomplete methods were
  resumed: **4653 passed, 25 failed**. An inventory audit then executed four
  missing rows from partially completed theories, all passing. No second full
  sweep was run. Twenty existing functional/timing failures qualified 3/3
  alone; five P694 200 ms performance rows also reproduced failures on the
  unchanged base. The alternating 150-ship parallel comparison failed 2/3 in
  both trees. This is a documented base/load performance limitation, not a
  clean performance gate.
- **ClientCore:** **1342 passed, 1 failed, 1 opt-in rig scenario skipped**.
  The failed case was the new siege proof run before the carrier/discovery/
  ammunition corrections. Final changed cases were qualified separately;
  there was no second full sweep.
- **MonoGame:** **2430 passed, 1 failed, 3 optional private Meshy-pack skips**.
  The existing pod-purchase timing failure then passed **3/3** isolated
  invocations (both theory cases, six passes).

See [verification ledger](P745_assets/verification.json) and
[flake ledger](../TEST_FLAKES.md) for the exact isolated cases and base results.
The dispatcher-only rig/221/200v200 runs were not performed. No commit or push
was made.
