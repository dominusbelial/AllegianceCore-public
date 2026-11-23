# P1079 — Browser native combat effects

Status: browser implementation and proof complete. Final broad Fast gate remains red on existing receipt/timing flakes; details below.
This report was created before implementation and updated during the work.
All changes remain in the working tree. No commits, pushes, installs or deployments.

## Implementation and native references

Base: `ba2d4ada0a477970b3f17480daf96e4d1b2c0de2` (after P1076).

The browser now calls the desktop's existing native renderers in
`src/Allegiance.ClientCore.Game.Browser/Game1.Combat.cs`. Particle lifecycle,
sector resets and density admission live in `BrowserCombatEffects.cs` alongside
it. Both HUD styles use this one scene. Native ejection blasts are prepared from the existing kill-event sequence in that browser model. No desktop rendering source changed.

| Item / renderer | Desktop pass in `Game1.Rendering.cs` | Native source and retained inputs | KNI/WebGL result |
|---|---|---|---|
| Missiles — `NativeMissileRenderer` | 1192–1270 | `Igc/missileigc.cpp:49-51`; type-selected model/texture, authoritative position/heading, sector gate | Unchanged native BasicEffect mesh/material path; small solid missile stand-in until verified mesh promotion |
| Missile trails — `MissileTrailRenderer` | 1206–1231, 1453–1469 | `effect/thinggeo.cpp:758-917`, `effect/particlegeo.cpp:24-129`; `TrailMissile` samples and unchanged `NativeParticleCurves` | Unchanged native fire/smoke billboard path; no enhanced smoke/shader |
| Explosions — `ExplosionRenderer` / `ExplosionAssets` | 1575–1620 | `WinTrek/trekigc.cpp:95-177`, `effect/explosion.cpp:104-202`, `engine/image.cpp:588-625`; all live current-sector entries plus the native eject cue (`clintlib/appmsg.cpp:2850-2857`), type presets, receipt-relative ages and emitter velocity | Unchanged native animated BasicEffect billboards. `exp22bmp` is sliced into square frames **before** GPU upload; no oversized strip texture |
| Hull hits — `HullHitSparkRenderer` | 1623–1657 | `effect/thinggeo.cpp:1324-1345`, `effect/efgeo.cpp:655-846`, `Igc/projectileIGC.cpp:193,224`; one drained hit, actual impact/normal/radius, hull-only two bits and surface flash | Unchanged native fx11/f103 decals and textured utl10 geometry |
| Shield hits — `ShieldFlareRenderer` | 1662–1711 | `Igc/shieldIGC.cpp:170-171`, `effect/thinggeo.cpp:1243-1305`, `WinTrek/trekigc.cpp:1624-1645,2517-2523`; shield-only hits, target pose, world-scaled native mesh ellipse/radius when available, spherical fallback otherwise | Unchanged native lens30 curved cap/fx09 animation; 0.75-second pool and existing per-target cap |
| Damage debris — `DamageDebrisRenderer` | 1485–1500 | `effect/thinggeo.cpp:699-755,1358-1395`; actual drawn scanner-visible ship's hull fraction, render pose, radius and velocity | Unchanged native smoke path; existing damage threshold/vent rule |
| Lifepods — existing `NativeShipRenderer` | Existing ship geometry pass | `clintlib/appmsg.cpp:170-242`, `Igc/shipIGC.cpp:1412-1419`; unchanged EJECT hull/pose application and roster | Existing mesh pass, corrected browser near plane and priority-1 pod prefetch |
| Probes — `ProbeRenderer` | 1274–1319 | `Igc/probeigc.cpp:112-115,142`; per-type model/texture and radius, current-sector position; side tint only for missing-texture fallback | Unchanged native BasicEffect path; solid stand-in until verified art arrives |
| Mines — `MineRenderer` | 1321–1356 | `Igc/mineigc.cpp:103`, `effect/efgeo.cpp:15-87`; actual field radius/strength/id and native decal count/distance brightness | Unchanged source-alpha native cloud |
| Chaff — `ChaffRenderer` | 1358–1385 | `Igc/chaffigc.cpp:42-55`; real coasting position, lifespan/radius growth, texture, roll and desktop 5900-unit proximity gate (wire has no sector) | Unchanged native BasicEffect decal |
| Treasures — `NativeTreasureRenderer` | 1387–1442 | `Igc/treasureIGC.cpp:118-193`; full core visual resolution, per-treasure age/tumble, current-sector position, native model/decal | Unchanged native BasicEffect path; existing gem fallback |

The order is missiles → probes → mines → chaff → treasures → missile trails →
teleport sparks → damage debris → engine effects/strobes/ribbons → explosions →
hull sparks → shield flares → HUD. Existing browser construction/glimpse work
remains in its scene pass. No custom desktop shader was ported and no shared
renderer required a KNI-specific modification. The pre-existing camera-facing
shockwave and other documented desktop-native rendering approximations remain.

### Why the own pod was invisible

Hull 33 (`dreghpod`) is 4 units long. The radius-scaled browser chase eye is
about 7.61 units from its centre. Its entire radius-2 sphere lies inside the old
**10-unit near plane**. This reproduces the capture's vanished own pod even
with a resident mesh. The browser now uses the shared chase camera's
**0.5-unit default**. The geometry regression checks native pod lengths 4 and 5
(the latter's centre was also clipped, though part of its sphere could survive).
No EJECT application, own-ship eligibility or native camera mode was changed.
Both sides' pod closures are requested at priority 1 during flight before an
ejection; the normal roster also requests each actual remote/own pod hull.

### Shield-cap scale correction found by visual review

A nonzero flare submission count initially produced no visible own-ship cap.
The desktop helper passed raw authored mesh extents/radius directly to a
world-space cap renderer. Native instead applies its outer
`m_scale = radius / m_radiusMesh` before rendering the flare group
(`effect/thinggeo.cpp:1185-1187,1482-1495`). The measured starter scout (`quizscoutv2`, hull 13) has authored radius
3.53447 but rendered radius 8.5. Its raw-input cap's long axis was only **1.28387**
world units, compressed inside the hull; its correctly scaled axis is **7.42523**.
Larger authored meshes have the opposite failure and can put the cap far away.

The browser caller now converts authored extents to the actual hull's world
scale and supplies its rendered radius to the unchanged cap renderer. The
regression checks the independent native parent-scale result for the measured
scout and two larger authored mesh sizes, reproducing both size failures. This is an explicit browser
input correction to the desktop reference pass; desktop output remains unchanged.
A focused capture also exposed a server/predicted-pose offset while coasting.
The browser now rebases the impact onto the same predicted/interpolated pose
used by the mesh, retaining the authoritative impact offset and copying the
orientation at admission. Native draws flares under the thing's render transform
(`effect/thinggeo.cpp:1482-1495`); the new regression pins that input agreement
and prevents later orientation mutation. The shared renderer and its existing
short-lived world-space flare snapshot remain unchanged. The reviewed captures show the small blue-white cap at the wing tip,
in addition to the nonzero submission counts.

### Native ejection blast

Native `KILL_SHIP` adds a hull explosion even when `bDeathCredit` is false
(`clintlib/appmsg.cpp:2850-2857`). The current host's eject branch supplies no
207 explosion packet, so drawing only `ClientWorld.Explosions` would still miss
the hull lost when a pilot ejects. The browser now consumes the existing kill
event sequence once, retaining the previous drawn scanner-visible hull's
position, velocity and fighter/capital classification before EJECT replaces it.
It emits a local presentation cue, without mutating the world or sending packets.
True deaths retain their authoritative 207 path; an exactly overlapping server
blast replaces an inferred cue. View changes, stale/hidden/unknown hulls and
expired effects are guarded by dedicated tests. Desktop output is unchanged.

### Artwork and budgets

`tools/p1066-scene-packs.py` adds `scene-combat`; the battle pack builder adds
all 53 probe types and available core-selected part/development/launcher treasure
models as separate packs. Unavailable original models retain the desktop
stand-in/gem fallback. Missing mesh/texture caches retry at P1075 content revisions.
Cap/bit/explosion constructors run only after the complete verified combat pack
is resident, so an early miss cannot permanently select a constructor fallback.
Flight never awaits these downloads.

| Delivery | Raw bytes | Gzip bytes | Notes |
|---|---:|---:|---|
| Added join artwork | **0** | **0** | Verified join manifest byte-for-byte identical to the P1076 generation |
| `scene-combat` nonresident closure | 24,277,306 | 2,679,812 | 18 source names; shared/resident files are not fetched twice |
| Entire `scene-combat` closure including join-resident files | 25,183,334 | 2,855,780 | Original bytes, no art re-encoding |
| Newly published unique combat/probe/treasure assets versus P1076 | 3,633,516 | 1,088,507 | 98 new hashes; much of the explosion art was already present in existing packs |
| Existing hull-33 pod closure | 135,168 | 18,741 | Already published; now prefetched in flight |
| Existing hull-113 pod closure | 532,084 | 115,790 | Already published; now prefetched in flight |
| Base probe-72 closure | 164,928 | 77,075 | Per-type request when present |
| Cash/power-up model closure | 35,540 | 9,445 | Per-model request when present |

The browser game assembly grows from 3,510,041 to 3,536,153 raw bytes
(**+26,112**), or 1,283,776 to 1,294,821 gzip bytes (**+11,045**). Its
Brotli transfer grows from 967,025 to 977,127 bytes (**+10,102**). These code bytes
are separate from the zero artwork growth in the join manifest. The before
generation was rebuilt from an owned clean archive of the base commit.

All combat requests use priority 1. The sole added visual filename outside the
existing MDL/PNG formats is **exactly `lens30.x`**, required by the unchanged
native shield renderer. Other `.x`, shader and script files are still rejected.
SHA-256, byte limits, dependency closure checks, `StagedManifest.Canonical`,
staging atomicity and CSP are unchanged. The new allowlist tests pin those gates.

The browser uses Native effect quality (the other shader tiers remain unavailable).
`ParticleDensityBudget` caps missile trail plus damage smoke submissions at **512**:
384 trails + 128 damage puffs. Reduced `_fullEffectLoad` keeps **128** missile
trail particles, clears continuous damage smoke, and retains hit feedback. The
newest particles survive pressure through a reusable min heap. Native hit/flare
pool capacities and exact mine-cloud geometry remain bounded by their existing
implementations; every live current-sector explosion still draws.

## Proof and measurements

The existing P1065 host's `--bots N` means **N ships per side**, reserving a
friendly slot for the browser pilot (`BattleBots.cs:49-60`). The brief calls
`--bots 10` a 5v5; that flag actually produces a 10v10 roster. Both are exercised:
`--bots 10` for the requested stress flow, and `--bots 5` for the true 5v5
before/after timing comparison. No existing CLI meaning is changed.

`tools/p1079-browser-combat.cjs` uses that authenticated P1065 flow,
flies through the real aleph and waypoint to within 180 units of the rendezvous,
then samples **120 seconds per HUD style**. Live missile/trail/explosion/flare/
spark/debris assertions precede any visual fixture. The host's proof-only
`CombatProof` records actual `[explosion]` log lines with its monotonic clock;
the harness translates browser clock origins through bracketed samples and
checks the upper latency bound, including clock uncertainty, against one second.

The scratch-only fixture creates real sector mine/probe/chaff/treasure models,
uses production broadcasts/codecs and drives actual `ReceiveDamage` → eject for
own death. The pilot alone is kept healthy during passive observation. It also
provides controlled near-pilot missile/explosion/shield cues for **250 ms capture
bursts**, separate from the live assertions. Before the own-death hook, ordinary
waypoint input moves the pilot to a quiet point in the same sector, allowing a
real fragile pod to remain visible throughout the burst. The own-death hook sends **no synthetic explosion**: the harness requires a
first-draw receipt carrying the actual ejected ship ID, proving the browser's
native KILL_SHIP cue supplied it. Production server rules remain unchanged.

Metrics report each frame's submitted missiles, trail particles, explosions,
shield flares, hull sparks, debris, pods, own pod meshes, mines, chaff, probes and
treasures. A bounded first-draw explosion history provides type/sector/position/
receipt/draw timestamps for correlation. Counts describe render submissions;
they do not claim every offscreen primitive produces visible pixels.

The requested `--bots 10` stress proof **passed both styles**, each with a
120-second live sample before fixtures:

| HUD | Sampled frames | Server explosions matched | Maximum latency upper bound | Max missiles / trails / explosions | Max flares / spark components / damage puffs / pods |
|---|---:|---:|---:|---|---|
| Classic | 7,196 | 55/55 | 122 ms | 10 / 384 / 10 | 10 / 95 / 128 / 7 |
| Modern | 7,183 | 7/7 | 104 ms | 4 / 384 / 6 | 8 / 68 / 128 / 7 |

The frame probe observed **9,330 Classic / 9,318 Modern presented frames**
through the sample and fixtures, with **zero discarded purple frames and zero
probe errors**. There were no browser page errors. Own-eject blasts drew **3 ms after local presentation-cue creation**
in each style; both real pods used
hull 33, remained alive, and drew their native mesh. The fixture also submitted
all four deployed-object categories in each style.

The matched **true 5v5 before/after runs passed**, with 120 seconds per style:

| HUD | Before rAF p50 / p95 | After rAF p50 / p95 | Before / after timed frames |
|---|---|---|---:|
| Classic | 16.7 / 16.7 ms | 16.7 / 16.7 ms | 7,205 / 7,205 |
| Modern | 16.7 / 16.7 ms | 16.7 / 16.7 ms | 7,191 / 7,207 |

All four windows observed live combat. The existing cumulative gun-bolt draw
counter increased by 26,100 / 7,415 before and 22,954 / 55,550 after
(Classic / Modern). Both after windows also submitted live missiles, trails,
explosions, hull sparks and damage smoke; the six-family nonzero gate belongs
to the larger stress run. All four frame probes reported zero purple frames.
Seven server explosions in the smaller after samples were matched within a
115 ms upper bound. Own-eject presentation cues reached a draw **2 / 1 ms after local creation**;
the own-pod mesh was already present in the first Classic/Modern burst captures
(**122 / 124 ms after the harness observed the host hook reply**).

The ten reviewed screenshots below are original canvas captures from **eight-frame
bursts scheduled at 250 ms**; actual offsets and SHA-256 hashes are in
[P1079 evidence](P1079_assets/evidence.json). The shield shot precedes the
controlled blast so its small blue-white cap at the left wing tip is visible.
The own-pod shot shows the real native hull 33 after `ReceiveDamage` / EJECT.
Full bursts remain in ignored local proof storage.

| Capture | Classic | Modern |
|---|---|---|
| Native missile mesh and exhaust/trail | [image](P1079_assets/classic-missile.jpg) | [image](P1079_assets/modern-missile.jpg) |
| Native animated explosion | [image](P1079_assets/classic-explosion.jpg) | [image](P1079_assets/modern-explosion.jpg) |
| Blue-white shield cap | [image](P1079_assets/classic-shield.jpg) | [image](P1079_assets/modern-shield.jpg) |
| Own native lifepod | [image](P1079_assets/classic-pod.jpg) | [image](P1079_assets/modern-pod.jpg) |
| Mine cloud, chaff, probe and treasure | [image](P1079_assets/classic-props.jpg) | [image](P1079_assets/modern-props.jpg) |

An initial varied 5v5 window produced no server explosion or missile activity and
was rejected by the unchanged live-effect assertion. A homogeneous-interceptor diagnostic also stalled on home-sector route discovery.
For the matched timing runs, the unchanged native varied fleet first discovers
the route and reaches actual combat, then the existing MCP commands reduce it
to four friendly bots plus the browser pilot and five opposing bots. The receipt
records the retained rosters; both timing builds use this same preparation.
Every timing window must observe actual gun bolts or combat effects. The six
specific effect-family assertions remain mandatory for the requested varied
10v10 run above, which is separate. Headless SwiftShader rAF timings are limited by the
browser's approximately 60 Hz presentation ceiling and are not workstation GPU
benchmarks.

## Validation

- Final solution build: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **153/153 projects built**, zero warnings/errors.
- New C# cases: **25/25**, with **75/75 isolated executions** (every case 3× alone after the final pose correction).
- New Python pack cases: **3/3**, each **3× alone** (9/9 executions), with original artwork.
- Browser head: **52/52**; browser game: **302/302**.
- Related existing MonoGame cases: final repeated filter **283 passed / 2 pre-existing skips**. The initial run hit one unchanged training chat-count flake; 3× isolated lane and clean-base reruns both passed 4/6 cases, reproducing the same Classic failure twice. Details are in the flake ledger and evidence.
- P1073 pack coverage regression: passed.
- Glossary links and new 8-row parity matrix: passed.
- P1073/P1074/P1075/P1076 Chromium regressions: **all passed** on the final browser generation; P1076 lookback artwork was ready in 3.187 / 3.285 / 3.336 / 3.383 seconds, within its unchanged five-second limit for both HUDs and both sides.
- Initial Fast sweep: **16,057 passed / 4 failed / 7 not executed** (16,068 cases; 580.4 seconds). All seven failed/over-limit methods ran 3× alone: **53/54 passed**; one unchanged moving-target wire test failed again. Final Fast sweep: **16,060 passed / 2 failed / 7 not executed** (16,069 cases; 561.2 seconds), with **9/9 infrastructure checks passed**. Both browser projects, full MonoGame and ClientCore passed. Four failed/over-limit methods ran 3× alone: **40/42 passed**. The existing mining receipt test failed twice again; the probe receipt and both timing methods passed every isolated process. The final broad gate remains **red**.

[The P1079 flake ledger](../TEST_FLAKES.md#p1079--browser-combat-qualification-2026-11-23) records the unchanged wire/echo failures and exact isolated outcomes; no assertion, deadline, tier or quarantine was weakened.
All requested browser features, captures, timing samples and regression proofs
are complete. The unresolved broad-suite receipt/timing instability is outside
this browser-rendering scope and remains visible in the ledger. No training,
service, store, port, environment-variable, wire or server-rule change.

## Native intent

Make the already-ported native combat scene visible in browser flight with the
same type, sector, pose, hit routing, size, timing and pass ordering as the desktop
native branch. References are above and in `docs/parity/p1079-browser-combat.csv`.
Original artwork retains its FreeAllegiance/MIT attribution in the verified packs.

## Modern approach

Verified on-demand delivery and promotion-safe caches keep launch independent of
combat art. Browser-only density admission, metrics and scratch-only proof hooks
make the native LDR paths measurable in both HUD styles without changing desktop
output, admission, wire contracts or authoritative game rules.
