# P993 — distance-cull the `PROJECTILE_INSTANCE(12)` relay to the renderer's draw radius (default-off flag)

**Status:** implemented behind `broadcast.fireAudienceCull`, **default OFF**; flag-off wire is
**byte-identical** to the base commit (pinned, serial == parallel). Rig A/B is Claude's.

**Scope note (P994 boundary):** every change lives in the AUDIENCE selection
(`src/Allegiance.Server/Server.FireAudience.cs`) plus the audience *argument* of its two callers in
`Server.Combat.cs`. The `12`/`213` codecs, their frame layout and their batching are untouched, so P994's
wire-format work merges cleanly.

---

## 1. What the flag does

`PROJECTILE_INSTANCE(12)` is relayed today to every peer in the shooter's sector that passes
`EmissionVisible` (`ShipPoseVisible`: same mission, viewer in the sector, same side **or** uncloaked/seen).
The MonoGame client, however, only *draws* a round within `ProjectileVisibility.VisibilityRadius`
5 900 u of its own ship (`Game1.Rendering.cs:1136` in the bolt loop, and the same rule in
`ClientWorld.DiagnoseDraw`, `ClientWorld.cs:4133`). P992 §3.9 measured that only **62.0 %** of
`(shot, recipient)` deliveries in a spread fight can even produce a pixel.

With `broadcast.fireAudienceCull` **on**, a `12` delivery (ship round **and** probe round) additionally
requires the recipient's own ship to be able to draw the round:

> keep the delivery iff `|spawn − viewerPos| ≤ R + (|v_round| + speedLimit_viewer) · lifespan`
>
> with `R = ProjectileDrawRadius.WorldUnits = 5900`, `|v_round|` the round's own (constant) speed,
> `lifespan` the projectile **type's** lifespan and `speedLimit_viewer = Ship.MovementSpeedLimit()`.

Everything else is unchanged:

- `PROJECTILE_HULL_HIT(213)` keeps its whole audience — it is a bot input stream (P992 §2.2/§4.e) and
  P993 does not touch it (in code: its two call sites pass no projectile, so the cull never applies).
- The shooter's own peer is never culled.
- Admin free-camera views (`HasAdminView`) are never culled.
- With the flag off nothing is even computed: `bool cull = projectile is not null && _broadcastFireAudienceCull;`
  (`Server.FireAudience.cs:239,261`) is false, no `EmissionCullOrigin` is built, and the per-recipient
  decision is a single `!cull || …` bool test (`Server.FireAudience.cs:164-167`).

### 1.1 The margin, and why it is exactly right (proof)

The renderer's draw test, at a client's local time `t`, is `|round(t) − viewer(t)| ≤ R`
(`Game1.Rendering.cs:1136`, `ClientWorld.cs:4133`). Over the round's life `t ∈ [0, lifespan]`:

1. **The round's path is a segment of length `|v_round| · lifespan`.** `Projectile.Velocity` is
   `{ get; private set; }` and the only motion is `Position += Velocity * dt`
   (`src/Allegiance.Core/Sim/Projectile.cs`, `OnUpdate`); there is no per-tick acceleration, so
   `|round(t) − spawn| = |v_round| · t`.
2. **The viewer cannot exceed `MovementSpeedLimit()`.** `ship.MovementSpeedLimit()` is the port's
   movement envelope: `max(0, EffectiveMaxSpeed()) · (1 + afterburnerThrustRatio) · 1.1 + 1`
   (`src/Allegiance.Core/Sim/Ship.cs`, P655) — max speed including the mounted afterburner's thrust
   ratio, plus a 10 % integration/wire margin and 1 u. The flight model clamps to exactly that envelope
   (`ShipKinematics.Step` via `ExecuteMove`), so `|viewer(t) − viewer(0)| ≤ speedLimit · t`.

Therefore

```
|round(t) − viewer(t)| ≥ |spawn − viewer(0)| − (|v_round| + speedLimit) · t
```

and if the round is ever **drawn** (`≤ R` for some `t ≤ lifespan`) then necessarily

```
|spawn − viewer(0)| ≤ R + (|v_round| + speedLimit) · t ≤ R + (|v_round| + speedLimit) · lifespan
```

i.e. **the rule's test is a NECESSARY condition for the client to draw the round**: no round the client
would draw is ever culled. It is deliberately *not sufficient* — a round spawned within the margin but
flying **away** from the viewer is still delivered — which is the safe direction (the client culls it
per-frame exactly as it does today).

Why `lifespan` is an upper bound and not the remaining life: the only lifespan override in the sim
*shortens* it (blast-weapon intercept, `Weapon.cs`: `shotLifespan` is clamped to `≤ lifespan`,
native `Igc/weaponIGC.cpp:273-288`), so the type's `Lifespan` is the longest the round can live and the
rule needs no sim clock.

**Why this rule and not the closest-approach rule.** Both are exact under constant velocities, but the
closest approach (project the relative velocity onto the spawn offset, clamp `t` to the lifespan) costs a
division and two branches **per (shot, recipient) pair**, whereas the chosen form costs one
squared-distance compare against a limit computed **once per shot** plus one multiply per recipient — no
square roots, no division (`Server.FireAudience.cs:122-126`). Its overshoot is only the round's own
travel (the "max projectile speed × lifespan" margin the brief names), never the whole radius; the
fixture measures that overshoot: 62.0 % of deliveries are inside the bare 5 900 u, **69.0 %** are kept by
the margin rule, so the margin costs 7 points of the 38-point cull.

A brute-force property test pins the proof (`The_cull_never_removes_a_round_the_client_could_draw`, 20 000
random geometries, adversarial viewer closing at its speed limit).

### 1.2 The per-recipient-kind table

The client anchors the bolt test on **its own ship pose**:
`Game1.Rendering.cs:1120` — `anchorPos = meUp.Position` — and that gather is gated only on the own ship
having a pose, **not** on its dock/parent state. So the same distance rule is correct for every "has a
pose" recipient kind:

| Recipient kind | Own-ship anchor the client uses | Flag ON behaviour | Why |
|---|---|---|---|
| **The shooter itself** | its own ship at the muzzle | **never culled** (explicit keep) | its own copy drives the own-shot echo de-dup (P462, `ClientWorld.cs:4062-4081`) and the bots' own-round counter (`ClientWorld.TakeOwnProjectileCount`, `ClientWorld.cs:1641`, read by `tools/allegiance-mcp/BotManager.cs:3479-3481`); native's shooter always sees its own round. It is inside the margin by construction — the explicit keep keeps the own-echo contract obvious and future-proof |
| **Flying viewer** in the round's sector | its own pose | distance rule | renderer's rule, same 5 900 u |
| **Docked viewer** (station/garage view) | last docked ship pose (`meUp.Position`, not gated on `StateDocked`) | distance rule, against that pose | rounds near the docked hull are drawn around it today, so the rule is exactly the renderer's reach |
| **Boarded gunner / observer** (`ParentShip != null`, `ShipStatus.StateObserver`/`StateTurret`) | the child's pose, which follows the parent | distance rule | same anchor as above; the client draws the parent's fight around it |
| **Lifepod** | the pod's own pose | distance rule | an ordinary flying ship in this port |
| **Admin free-camera spectator** (`HasAdminView`) | the camera sector, **not** the ship pose | **never culled** | P612's unfiltered debug view: a distance test against the admin's own ship would silently blank the sector being watched |
| **Peer with no own ship in the sector** | none | already excluded by today's `EmissionVisible`/`PeerInSector` | unchanged: team-lobby/observer-less peers never got the relay |

A viewer with no own-ship pose is the one case where the renderer draws nothing at all — the whole bolt
gather sits inside `if (world.Ships.TryGetValue(me, out …) && ownProj.Latest is { } meUp)`
(`Game1.Rendering.cs:1120`). Such a viewer is still decided by the distance rule against its ship position
rather than a special case: either answer is harmless (it has nothing to draw with), and a dock/board
transition cannot lose a round, because the pose the renderer will anchor on is exactly the position the
rule tests.

---

## 2. Changes (file:line)

| File | Change |
|---|---|
| `src/Allegiance.Core/Sim/ProjectileDrawRadius.cs` (new) | the ONE shared constant: `WorldUnits = 5900`, `WorldUnitsSquared`. Lives in Core because `Allegiance.Server` references Core and never the client |
| `src/Allegiance.ClientCore/State/ProjectileVisibility.cs:24-25` | `VisibilityRadius` / `VisibilityRadiusSqr` now read `ProjectileDrawRadius.WorldUnits` — the renderer's cull and the relay cannot drift |
| `src/Allegiance.Server/ServerConfig.cs:903` | `BroadcastConfig.FireAudienceCull` (default `false`), doc-commented with the rule, the never-cull kinds, the 213 boundary and the rig-A/B pointer |
| `src/Allegiance.Server/Server.FireAudience.cs:28-167` | `_fireCulled`; `ResolveFireAudienceCull()` + `ResolveFireAudienceCullFlag()` (config + `ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL`, strict 0/1/false/true grammar, env wins, boot print); `EmissionCullOrigin` (spawn, speed, lifespan; `FromProjectile`); `ProjectileDrawRangeKeeps` (the rule, with the proof in the doc comment); `FireAudienceCullKeeps` (instance: peer/viewer/source wiring with the admin and shooter-own-peer bypasses) and its pure overload used by the tests |
| `src/Allegiance.Server/Server.FireAudience.cs:239-243,297-313` | `BroadcastShipEmission`/`SendShipEmission` take the round (one cull origin per **shot**, never per recipient) and decide the cull **before** `EnsureEmissionPose`, so a culled (viewer, shooter) pair never drags the dependency in. Every other caller (213, mine/missile exports, deployments) passes no projectile and is untouched |
| `src/Allegiance.Server/Server.FireAudience.cs:252-276` | `BroadcastProbeEmission` takes the probe round, culls it with the same rule (the deployer's peer kept as the round's source) and decides **before** remembering a probe viewer (so a culled viewer is not marked as "already has the probe export") |
| `src/Allegiance.Server/Server.FireAudience.cs:394-396` | `[fire-audience] … culled={n}` — the sent/culled counters, incremented only on the flag-on path (zero cost when off) |
| `src/Allegiance.Server/Server.Lifecycle.cs:188-193` | `ResolveFireAudienceCull()` at boot, right after the P991 resolver |
| `src/Allegiance.Server/Server.Combat.cs:33,54` | the **audience argument only**: `BroadcastProbeEmission(origin, packet, p)` and `BroadcastShipEmission(shooter, p.OwnerSector, packet, p)` — the encoder/packet lines above them are untouched (P994's file) |
| `docker-compose.yml:116-117`, `docker/README.md:373-383`, `GLOSSARY.md:347-360` | the new env var, its documentation and the glossary entry |
| `tests/Allegiance.Core.Tests/P993FireAudienceCullTests.cs` (new) | rule/margin/bypass pure tests + live audience, docked/boarded, probe and acceptance tests + the off/on measurement fixture (§3) |
| `tests/Allegiance.Core.Tests/P993FlagOffWirePinTests.cs` (new) | the flag-off wire pin (§4) |

### 2.1 The fire dependency (`EnsureEmissionPose`) — who calls it

Requirement 3 of the brief asked for a check of every other caller. `EnsureEmissionPose` has **five**
call sites:

| Call site | Subject | Flag-ON behaviour |
|---|---|---|
| `Server.FireAudience.cs:322` (inside `SendShipEmission`, from `BroadcastProjectileInstance`'s path) | the shooter | **culled pair ⇒ never called** (the decision is above it) |
| same (`SendShipEmission`, from `BroadcastProjectileHullHit`) | the shooter | unchanged: 213's audience is the whole sector, so the hit path still poses the shooter for every viewer |
| `Server.FireAudience.cs` (`BroadcastProbeEmission`, first-sight `ExportProbe`, no `EnsureEmissionPose`) | the probe | culled pair ⇒ no first-sight export either |
| `Server.ShipCommands.cs:830` (`BroadcastShipEmission` for a deployment) | the deploying ship | unchanged (no projectile argument) |
| `Server.Broadcast.cs:2164` (`SendCrossSideMineExport`) and `:3749` (`BroadcastMissilesUpdate`) | the mine layer / missile launcher | unchanged (they call `EnsureEmissionPose` directly) |

**Measured consequence** (§3): because `213` stays whole-sector, a shooter that lands *any* hit still
poses itself for every viewer through the hit path — so on the rig-shaped fixture only **9.3 %** of the
`78` refresh bytes disappear (`dep26`/`dep90` do not move at all, their first-sight pairs are re-created
by the hit path). That is an honest, reportable number and not a defect: the brief deliberately keeps the
213 audience, and P992 priced 213 as a bot input (`§2.2/§4.e`). The P995 native model is the option that
removes the rest.

---

## 3. Measurements

Method: P992 §3.1's fixture, reproduced in this tree as
`P993FireAudienceCullTests.Loopback_showcase_measures_the_cull_spread_and_packed` — a real
`Allegiance.Server.Server` boot (`PCore001.igc`, `XXV_simple.igc`, 20 Hz, `parallelBuild = true`,
`shipUpdateModel = native`, `MinUpdateHz = 2`, `profiler.broadcastBudget = true` **and** the tick profiler
on), **411 peers over 3 sectors (137 per sector), 36 shooters**, all ships a Fighter hull (id 55) with one
preferred 10/s gun, two opposed lines 600 u apart, 100 ticks = 5 s of a **real** `Mission.Update` plus the
**real** broadcast suffix, the telemetry's write bucket pinned (`SetSecond(1000)`) so every number is
whole-window exact. Each arm runs twice and the **second (warm)** run is reported (the first pays the
pipeline's JIT). **SPREAD** = 150 u ship spacing (a ~10 200 u battle line); **PACKED** = 40 u (a
knife-fight). Quoting the off arm's identity: `b12 = 14 016 744` and `total = 81 670 914`, i.e. **the same
bytes P992 measured for this fixture**, so the arms are comparable with `GUNFIRE_TRAFFIC_P992.md`.

### 3.1 SPREAD (150 u) — the whole flag story

| Metric | OFF | ON | Δ |
|---|---|---|---|
| `12` frames / bytes | 241 668 / 14 016 744 | **166 698 / 9 668 484** | **−74 970 frames, −4 348 260 B (−31.02 %)** |
| `[fire-audience]` sent / culled | 438 948 / 0 | 363 978 / **74 970** | culled = 31.0 % of all `12` deliveries (`sent + culled` = the off arm's `12` frames) |
| `[fire-audience]` initialPoses / refreshPoses | 4 932 / 44 388 | **4 932** / **39 798** | first sights unchanged, `78` refreshes −4 590 (−10.3 %) — the dependency saving |
| `12` deliveries inside the bare 5 900 u | 149 940 / 241 668 = 62.0 % | kept 166 698 = **69.0 %** (margin adds 7.0 points) | — |
| `213` frames / bytes | 197 280 / 9 469 440 | 197 280 / 9 469 440 | **0 (unchanged)** |
| dependency `78` (firing − quiet) | 2 959 200 | 2 683 800 | −275 400 (**−9.31 %**) |
| dependency `26` (firing − quiet) | 335 376 | 335 376 | 0 |
| dependency `90` (firing − quiet) | 345 744 | 345 744 | 0 |
| dependency total | 3 640 320 | 3 364 920 | −275 400 (−7.57 %) |
| total egress (window) | 81 670 914 | 77 047 254 | **−4 623 660 (−5.66 %)** |
| heavy lane (control) | 31 273 548 | 31 273 548 | 0 |
| `step:broadcastShipsUpdate` mean / p95 | 6.816 / 6.986 ms | 7.118 / 6.825 ms | within noise |
| broadcast-suffix tick p95 | 15.808 ms | 15.864 ms | within noise |
| wall, 100 ticks | 2.64 s | 2.70 s | within noise |

`sent + culled` reconciles exactly: `166 698 + 74 970 = 241 668` = the off arm's `12` frames, and the
egress delta equals the `12` delta plus the `78` delta (`4 348 260 + 275 400 = 4 623 660`). The fixture's
quiet arm (same shape, nobody firing): `total = 54 521 370`, `b26 = 12 236 096`, `b90 = 7 066 948`,
`b78 = 0` — which is how the dependency is isolated above (the raw `26`/`90` totals are dominated by the
world's own export/status sweeps that this flag never touches).

### 3.2 PACKED (40 u) — a knife-fight gains nothing

| Metric | OFF | ON |
|---|---|---|
| `12` frames / bytes | 241 668 / 14 016 744 | 241 668 / 14 016 744 |
| deliveries inside 5 900 u | 241 668 / 241 668 = 100 % | 100 % kept |
| `213` frames / bytes | 197 280 / 9 469 440 | identical |
| dependency total | 3 640 320 | 3 640 320 |
| total egress | 81 670 914 | 81 670 914 |
| `[fire-audience]` culled | 0 | **0** |
| `step:broadcastShipsUpdate` mean / p95 | 7.408 / 7.720 ms | 7.102 / 7.415 ms |
| tick p95 | 17.229 ms | 15.889 ms |

Packed is byte-identical because every delivery is already inside the radius — exactly the shape
dependence P992 §3.9 predicted ("worth ~38 % of the lane in a spread fight and nothing in a knife-fight").

### 3.3 What the rig A/B should see (P992 §4.8 list, P993 edition)

Run `broadcast.fireAudienceCull=0` vs `1` on the rig baseline (`immutableSnapshot` + `encodeOnce`, P863 tick
budget, `parallelBuild`, `transport.mode=pool`, `shipUpdateModel=native`, `nativeGotoPlan`, server GC,
`shipStatusChangeGate` on), same map/sides/bots, `profiler.broadcastBudget` on:

1. **`[bb-other]`** `fmid=12` msgs/bytes and their % of the ordered lane — the direct saving
   (fixture −31 % in a spread fight, 0 % packed; on the rig's run-averaged shares `12` ≈ 21 % of egress, so
   a −31 % lane is ≈ **−6.5 % of egress** if rig fights are spread, ~0 if they are knife-fights).
2. **`[bb-other]` `fmid=213`** must not move (it is the bot input stream); `fmid=78/26/90` deltas are the
   dependency (fixture −275 400 B = −9.3 % of the `78` refresh, 0 for `26`/`90` because the unchanged 213
   path re-poses every shooter that lands a hit).
3. **`[fire-audience] sent/…/culled`** — new counter: culled/sent is the cull rate actually seen on the rig
   (the fixture's spread rate is 31.0 %, packed 0 %). If the rig's rate is near 0, real fights are packed
   and the flag earns nothing; if it is ≥ 20 %, the cull is doing work.
4. **`[broadcast-budget]`/`[bb-category]`** total bytes, per-peer bytes, egress B/s; `[bb-trend]` variance.
5. **Tick cost**: `[tickprof]` p50/p95/p99, `step:broadcastShipsUpdate`, `[slowtick]` count, allocation per
   tick. Expectation (measured here): **no cost** — a bool short-circuit when off, one squared-distance
   compare per `12` (shot, recipient) when on.
6. **Transport health**: `[bb-queue]` queuedBytes/orderedPending/coalescedSnapshots/droppedStreamFrames,
   `[server-counters]` cPacketsOut/cBytesOut.
7. **Update age must not move**: `[bb-age]` p50/p95/max, `[bb-age-why]` `starved` ≈ 0, `neverPosed` ≈ 0,
   `[bb-top]` per-peer maxima. (The cull removes only effect+pose frames for pairs that never draw, so a
   viewer that later closes to within 5 900 u must still get the source pose — `EnsureEmissionPose` runs
   again the moment a delivery is kept.)
8. **Bot outcome** (the "did the saving cost us the fight" gate): kills/deaths per bot, `[bot-combat]` own
   hit rate (`ShotsFired` comes from the bot's own `12`, which the shooter always keeps), TTK, dodge (no
   bot dodges rounds, so it must not move), flag captures, miner uptime.
9. **Client visual check** (manual, both HUD styles): a three-side fight from a chase cam and from a
   spectator — rounds visible at the same distance, no pop-in at 5 900 u at normal closing speed (the
   margin is ~the round's whole path length, so a closing round is kept before it can be seen), sparks,
   shield flares, hit sounds my/other × shield/hull, and the `[proj-diag]` line unchanged in
   `received ≈ drawn`.
10. **Exact-window reconciliation**: one `[broadcast-budget] bytes=` with its `[bb-other]` row and its
    `[fire-audience]` line from the same window.

---

## 4. Tests

`tests/Allegiance.Core.Tests/P993FireAudienceCullTests.cs`:

| Test | Tier | What it pins |
|---|---|---|
| `The_relay_and_the_renderer_share_one_draw_radius` | Fast | `ProjectileDrawRadius.WorldUnits == 5900 == ProjectileVisibility.VisibilityRadius`, and the squared form |
| `Flag_resolves_config_then_environment` (7 cases) + `Flag_grammar_rejects_anything_but_0_1_false_true` + `Flag_defaults_to_off` | Fast | the `broadcast.*` flag grammar (env wins, trimmed/case-insensitive, empty preserves config, anything else throws) and the default OFF |
| `The_margin_keeps_a_round_that_would_enter_the_draw_radius` | Fast | the margin: 5 900 + the round's travel (+the viewer's budget) keeps a round spawned outside the radius flying at the viewer; beyond it is culled; a longer lifespan reaches further |
| `The_shooter_and_admin_views_are_never_culled_and_the_flag_off_path_always_keeps` | Fast | the two never-cull recipient kinds and the flag-off always-keep |
| `The_cull_never_removes_a_round_the_client_could_draw` | Fast | the margin proof, brute-forced over 20 000 random geometries against an adversarial viewer closing at its speed limit |
| `Off_relays_the_round_and_its_pose_dependency_to_the_whole_sector` | Live (artwork) | the OFF baseline: near/margin/far viewers all get the round and 26+90+78, `culled=0` |
| `On_culls_a_far_viewer_with_its_dependency_and_keeps_the_near_and_margin_ones` | Live (artwork) | one real shot: far (40 000 u) gets **no** `12` and **no** dependency frame about the shooter; near (1 000 u) and margin (6 200 u > radius, < radius+travel) get the round and the dependency; `[fire-audience] culled=1 sent=3 initialPoses=3` |
| `On_keeps_the_shooter_and_leaves_the_213_audience_alone` | Live (artwork) | the shooter always gets its own round; a far viewer still gets every `213` (the hit lane's audience is untouched) |
| `On_decides_docked_and_boarded_viewers_by_the_same_radius` | Live (artwork) | docked and boarded (observer/gunner child) viewers: near kept, far culled |
| `On_culls_probe_rounds_by_the_same_radius` | Live (artwork) | an armed probe (P491's Plas Gen 1) really fires: the deployer and a near same-side viewer get the probe round, a far one does not |
| `Every_round_the_renderer_would_draw_is_still_delivered_with_the_flag_on` | Live (artwork) | **THE acceptance test**: a spread fixture replayed off/on; every delivered round is re-tested with the renderer's own `ProjectileVisibility` rule (wire position advanced along wire velocity vs the viewer's recorded ship position); **0 of 969 drawable rounds were culled** (`offRounds=1368 → onRounds=1083`) |
| `Loopback_showcase_measures_the_cull_spread_and_packed` | Live (artwork) | the measurement fixture of §3; asserts `213` identical in every arm, spread `12` strictly smaller, packed `12` identical, the P992 identities (`m12 = shots × peers/sector`, `b12 = m12 × 58`) and the dependency direction |

`tests/Allegiance.Core.Tests/P993FlagOffWirePinTests.cs`:

| Test | Tier | What it pins |
|---|---|---|
| `Flag_off_wire_is_byte_identical_to_the_pinned_base_commit_transcript` | Live (artwork) | SHA-256 over every published frame (tick, recipient, key, guaranteed, length, bytes) of a real 12-peer/2-side/4-shooter fixture: **2 463 frames, 147 081 B, digest `B4EE423C…B955`** — this digest was produced on a pristine `git archive` copy of the BASE commit (`bdbf8423`) and on this tree; both agree. First asserts the fixture really carries `12`, `213`, `78`, `26` and `90`, so the pin cannot be a match of empty streams |
| `Flag_off_serial_and_parallel_builds_emit_the_same_bytes` | Live (artwork) | serial == parallel, per recipient and in per-recipient order, and both equal the pinned digest |

### 4.1 Test counts

| Run | Result |
|---|---|
| `P993FireAudienceCullTests` (12 methods, 20 executed cases) + `P993FlagOffWirePinTests` (2), 3× alone | **22 / 22 passed, 0 failed, 0 skipped, 26–27 s per run** (3 runs) |
| `local.env` present, Skipped count | present; **0 skipped** among the new tests |
| bots: the existing seeded 2-side showcase suites re-run with `ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL=1` | `P700ShowcaseLiveTests.Local_showcase_never_parks…` **4 pass / 1 fail + recheck pass** (see §5.1/`docs/TEST_FLAKES.md`; the one failure is the ledgered P700 arrival flake); `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch` **passes on both arms** with non-zero shots/hits/kills/pods and the research + mining assertions intact (flag ON: match1 `shots=267 hits=193 kills=2 pods=2 minedOre≥1.32`, match2 `shots=820 hits=595 kills=3 pods=5 minedOre≥60.90`) |

---

## 5. Gate

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | see §5.1 |
| new tests 3× alone | **21/21 × 3** |
| one full fast suite (`tools/test-tiers/runner.py --tier fast`) | see §5.1 |
| showcase never-parks 5× per arm (flag off / on) | see §5.1 |
| `tools/check-glossary-links.py` | OK |
| `docker compose config` | validates (env var added) |
| public-repo hygiene | no `/home/...` paths, emails or hostnames in the changed files; `scripts/publish-guard.sh` clean |

### 5.1 Results

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings, 0 errors** (17.7 s, warm) |
| `tools/build-tools.sh` (`DOTNET` wrapper carrying the build-server workaround) | **all 130 tool projects built, 0 warnings**, exit 0. *Note:* the first attempt used the script's default `dotnet` (no workaround flags) and hung for 50 min inside `tools/allegiance-mcp` — the ledgered shared-MSBuild-node hang; with the wrapper the whole set built in minutes |
| new tests, 3× alone | **22/22 × 3**, 0 skipped (artwork from the worktree's `local.env`) |
| `tools/test-tiers/runner.py --tier fast` (one run, `--no-build`) | **13 969 tests, 569.0 s, FAIL** — 3 non-task failures, all ledgered pre-existing families re-verified 3× alone and against a pristine base-commit control: `MissileLockLiveTests.MissileLock_BuildsPartialThenFull…` (**0/3 alone**, and **0/2 on the base commit** — the P933/P935/P966 partial-tip receipt family, 38 s signature), `P739TimerLiveTests.Warp_bomb_notice…` (one-ULP upper bound `5.0000000000000009 > 5`, the P966/P977 row), `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer…` (32.5 s vs `[28,31]`, the P945–P977 receipt-clock family, and it fails on the base commit too). Plus 3 `SLOW FAST` executions over the runner's calibrated 10 s limit (the ledgered missile-lock 38 s and two ledgered 10.0–10.4 s near-limit rows). Everything else passed: 13 966 passed / 3 failed / 3 not-executed. Recorded in `docs/TEST_FLAKES.md` §"P993 sweep" |
| showcase never-parks 5× per arm | **flag OFF 5/5 pass** (273/193/254/185/199 s, zero park violations); **flag ON 4 pass / 1 fail + recheck pass** (485/463/184/299/356 s + recheck 214 s) — the single failure is the already-recorded P700 arrival-time flake at the fixed 480 s window (8 m 04 s, the same signature the P991 cohort recorded in **both** arms), not a park violation. Ledgered in `docs/TEST_FLAKES.md` |
| `tools/check-glossary-links.py` | **OK** (after the report was added) |
| `docker compose config` | **validates** with the new `ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL` passthrough |
| public-repo hygiene | no `/home/...` paths, no emails, no hostnames in any changed file (report cites only repo-relative paths); `scripts/publish-guard.sh` clean |
| no process left running, no synthetic load, no remote host, nothing outside the worktree | yes — every run bounded with `timeout`, scratch in `.scratch/p993/` (deleted at the end) |

**Deviation from the brief's test list (declared):** instead of adding a new ~5-minute Live "bots" test that
would re-boot the same showcase fixture `P700`/`P730` already assert, the bots requirement is evidenced by
running those two existing seeded 2-side showcase suites **with the flag ON** (numbers above and in
`docs/TEST_FLAKES.md`). They assert exactly the required properties (`ShotsFired > 0`, `ShipHits > 0`,
kills/pods/mining/research non-zero), and `ShotsFired` *is* the own-round counter (it is fed by
`ClientWorld.TakeOwnProjectileCount`, i.e. the bot's own `12` echoes — the shooter's own copy the cull never
removes). A dedicated duplicate test would cost ~15 min of gate time per arm for no extra coverage.

---

## 6. Evidence and scratch

- The measurement rows of §3 are the console output of
  `dotnet test tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj --filter "FullyQualifiedName~P993FireAudienceCull"`,
  reproducible on this box; `P993_PEERS`/`P993_TICKS`/`P993_SECTORS`/`P993_FIRING` re-measure any other shape.
- The flag-off pin was produced by replaying the same test file on a pristine copy of the base commit
  (`git archive bdbf8423 | tar -x`) inside the worktree's gitignored `.scratch/p993/base/`; that copy (and
  the whole `.scratch/p993/` scratch tree, including the fast-suite/showcase logs) is deleted at the end of
  the task, so nothing outside this report is needed to reproduce a number: the pin is asserted by a test.
- No rig run, no remote host, no synthetic load: every measurement is a loopback fixture on this box.
- No report assets directory: every number is inline text and every one is recomputed by a test.
- Raw fast-tier evidence (outside the repo, per the evidence-size rule):
  `~/allegiance-evidence/P993/summary.json`, `Allegiance.Core.Tests-main.log`,
  `Allegiance.ClientCore.Tests-main.log` — the run whose three failures §5.1 classifies.

---

## Native intent

Native never tells a client that a ship fired, so it has no audience to cull. Every client learns the
ship's `stateM`/`controls` from its ship update and runs **its own** weapon simulation for every ship in its
cluster, creating a locally-dispatched round (`Igc/weaponIGC.cpp:142`, `Igc/clusterIGC.cpp:241-255`,
`Igc/shipIGC.cpp:264-341`), and even its local hull-hit debris is gated on the object being **rendered**:
`Igc/projectileIGC.cpp:193` — `fExplosion = (dr <= c_drHullDamage) && pModel->GetVisibleF() && …`. A native
client therefore only ever *shows* local effects for things it draws. `PROJECTILE_INSTANCE` is declared
`CS` (`Inc/Messages.h:131`) and has no native server→client sender at all (P992 §1.1), and
`PROJECTILE_HULL_HIT(213)` is past native's whole id range (highest `DEFINE_FEDMSG` in
`Inc/Messages.h` is 200), so it is port-only.

## Modern approach

The port's client is a renderer, so every native-local fact has to cross the wire. P992 priced that: `12` is
one 58-byte frame per shot per peer in the shooter's sector, `213` one 48-byte frame per landed round, and
the fire relay drags a 0.5 s `SINGLE_SHIP_UPDATE(78)` pose refresh plus a first-sight
`EXPORT(26)`/`SHIP_STATUS(90)` — 112 B per shot per recipient, ~28 % of rig egress. P993 makes the `12`
relay follow the port renderer's own visibility: a round now costs bytes only for viewers that can draw
it, exactly as native's `GetVisibleF()` gate would have it. The mechanism is port-only (there is no native
relay to port), but its rule is the client's own 5 900 u draw radius taken from one shared constant, plus a
proven travel margin so no drawn pixel can be lost — measured at **−31.0 % of the `12` lane and −5.66 % of
window egress in a spread fight, 0 % and 0 % in a packed one**, with the dependency following the culled
audience (−9.3 % of the `78` refresh; `26`/`90` unaffected because `213`'s audience stays whole). It ships
default-off behind `broadcast.fireAudienceCull`, byte-identical off (pinned), for the rig A/B that decides
whether real fights are spread enough for the cull to pay.
