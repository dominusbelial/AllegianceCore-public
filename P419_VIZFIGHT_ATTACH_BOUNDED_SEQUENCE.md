# P419 — vizfight `attach`: bounded fleet, longer varied sequence, more ships

Live-testing P418's `attach` showed the bots drifting to infinity with no
leash and no narrative progression beyond one endless `Flight`. This stage
fixes both, scales the fleet, and turns `attach` into a real load-test rig
against the production server. Everything below is backed by the live run in
this session against `192.168.0.221:6073` (the production test server, with a
real MonoGame client on the same side observing the same fleet).

Relationship to P418: extends the wire-only spectator-client choreography with
real bounded steering, a real repeating phase cycle, and a higher/default
fleet. No change to `showcase`/`explore`/`roster`/`proof` modes or to
`wwwroot/index.html`.

---

## 1. The leash — a real, bounded, tracked limit on the flight

### Root cause (confirmed, P418 code)
`AttachPhase.Flight` (~Program.cs:2610-2637) drove every bot with a pure
sinusoidal weave whose `throttle = 0.55 + 0.25·sin(...)` is always positive
(0.30–0.80), so every bot accumulates net forward distance every tick forever.
There was no boundary check, no "steer back" term, and no state after `Flight`
besides the inert `Held`.

### Formula (`src/Allegiance.Core/Sim/AttachLeashSteer.cs`)
Pure static `Steer(pos, anchor, leashRadius, orient, weaveYaw, weaveThrottle)`
returning `(yaw, throttle)`:

```
toAnchor  = anchor - pos
dist      = |toAnchor|
if dist <= leashRadius or dist <= 1e-3:
    return (weaveYaw, weaveThrottle)          # INSIDE: weave passes through unchanged
blendZone = max(leashRadius * 0.5, 50)
push      = clamp((dist - leashRadius) / blendZone, 0, 1)   # "how far past", capped
dir       = toAnchor / dist
body      = orient.TimesInverse(cross(orient.GetForward(), dir))
steerYaw  = clamp(2.0 * body.Y, -1, 1)                       # AutopilotTurnGain 2.0
yaw       = weaveYaw + (steerYaw - weaveYaw) * push           # ramp the steer in
throttle  = weaveThrottle + (0.75 - weaveThrottle) * push     # firm up while arcing
```

Design points, each matching the task wording:
- **Inactive inside the radius** — the exact pre-P419 weave is returned
  bit-for-bit (proven by unit test).
- **Proportional + capped past it** — the steer term is weighted by `push`,
  the normalized overshoot past the threshold, saturated at 1 (never a
  snap/teleport; ships visibly arc back).
- **Real autopilot law** — the turn signal is the same continuous
  cross-product face-and-thrust law the sim's own NPC autopilot uses
  (`Ship.SteerToward`, Ship.cs:4911-4952), so the sign/geometry is the proven
  one, not a new tuned constant. `AttachFlyTo` (the regroup/mining/dock
  steering) reuses the same law.

### Anchor + radius come from the real world, not a guess
`ComputeFleetLeash` (Program.cs) runs once at the first `Flight` entry:
- **anchor** = the fleet centroid right after undock (the real launch/station
  position — the same place the web page / MonoGame client frame).
- **leashRadius** = the fleet's own cluster footprint
  (max distance from the anchor to the sector's warps/stations/asteroids,
  ClusterId-matched), × 0.85, clamped to [500, 4800]; falls back to 1600 if no
  geometry has arrived. Rationale tied to `wwwroot/index.html`: the camera
  fits the focused sector's geometry bounding sphere and zooms to
  `radius*2.4` (`reframeCamera`), so a fleet kept inside ~85% of that footprint
  provably stays in the rendered view.
- Live measurement: anchor `(4680,730,1400)`, leash **4800u** (Holokine is a
  large cluster, so 4800 = the clamp).

### Unit test (`tests/.../AttachLeashSteerTests.cs`, 5 tests)
- `InsideRadius_ReturnsAmbientWeaveUnchanged` — bit-for-bit.
- `AtBoundary_DoesNotEngage...` — at and just inside radius inactive; just past
  engaged.
- `FarOutside_FullyEngaged_YawEqualsPureSteerLaw` + `EngagementRamps...` — the
  blend endpoint and the proportional/capped ramp.
- `KinematicIntegration_ReducesNetDistanceFromAnchor` — drives a REAL
  `Ship.ExecuteMove` (with a real synthetic `DataHullTypeIGC`) over ~33s:
  start 1600u past a 1000u leash → **leashed ends 815u (inside)**, while a
  leash-less control ends **24769u** (keeps drifting). The same law, verified
  against the real sim mover (frame/sign conventions included), not a
  hand-rolled integrator.

---

## 2. A longer, more varied sequence — real beats, repeated

`AttachPhase` gained `Regroup`, `Mining`, `DockCycle`; the fleet now cycles

```
Flight (bounded weave+fire, 40s)
  → Regroup (converge on the anchor, ≤18s)
  → Mining (2 bots to a real asteroid, 24s; escorts bounded-loiter)
  → DockCycle (fly into the home station → real dock → relaunch, ≤34s)
  → Flight (loop)
```

A full cycle is ~2 minutes (vs P418's single endless flight). Each transition
narrates a real event through `AttachEvent`, matching the existing style. Live,
the log showed the cycle repeat (multiple "Fleet relaunched" lines) with these
real events:

```
t 40.6  Flight window complete — regrouping the fleet toward the home anchor.
t 58.6  Regroup complete — fleet reconverged on the home position.
t 58.7  Mining pass — 2 miner(s) traversing to real asteroid #9 at (890,-49,651).
t 82.6  Mining pass complete — diverting the fleet to the home station ...
t 116.6 Fleet relaunched — returning to bounded flight for the next cycle.
```

Beats and why:
- **Mining (included, real)** — `TryFindMiningTarget` picks the nearest MINABLE
  (`AabmMineHe3`/`AabmMineGold`) `DataAsteroidIGC` in the fleet's sector from
  the live world state and sends 2 bots to traverse to its real position
  (`AttachFlyTo`). Wire-only movement to real ore — no fabricated extraction
  (the wire has no synthetic "make ore appear" command).
- **Dock/Relaunch (included, real)** — the fleet flies into the home station
  (nearest station to the anchor); the server's own `DockWithStationEvent`
  (Sector.ResolveDockCollisions, DockRadius 200) latches each ship, which both
  views see as the ship leaving (SHIP_DELETE SDR_DOCKED); then every bot
  relaunches through the exact `SendBuyLoadout(fLaunch:true)` path the `Launch`
  phase uses. Server-confirmed live: dozens of
  `[dock] ship X docked at station 0 'Garrison' (side 0) in sector 'Holokine'`.
- **Alliance / vote / donate (excluded)** — P418's report listed these as
  wire-possible but "not driven". The concrete four-beat cycle the task's
  example specifies is implemented instead; adding an alliance change would
  re-side a bot and break the same-side / same-cluster visibility that lets one
  web view + one MonoGame client show the whole fleet (the whole point of the
  side-by-side proof), and a `#mutiny` vote would depose the fleet side's leader
  on a live external server. Neither adds to the "same fleet, both views"
  proof, so both are left out and noted — the same honesty P418 applied to flag
  capture and guaranteed kills.
- **Flag capture + guaranteed kills (excluded)** — remain not wire-only
  achievable on an external production server (friendly-fire gate / no
  in-process `SpawnNpcShip`), unchanged from P418's finding.

---

## 3. More ships, and `attach` as a load-test rig

- **Demo default raised 8 → 24** (Program.cs). Verified legible in the
  side-by-side run (28 ships = 24 bots + spectator + 3 in a coherent Holokine
  cluster view) and clean (0 overruns).
- **Legibility with a bigger fleet** — spawn positions are server-controlled
  (a wire-only seat cannot place a bot), so legibility comes from non-lockstep
  weave stagger: prime/golden-ratio spaced phases and a wider freq/amp band
  than P418's tiny `(i%7)/(i%5)/(i%4)`, so a large fleet never collapses into
  synchronized ship-soup.
- **High counts for load testing** — the existing `botCount` CLI arg is already
  unbounded (no new flag). Verified against the production server while
  watching its real per-tick cost signal `[overrun] tick cost X ms exceeded
  period Y ms`:

  | bots | overruns in window | server disconnects | verdict |
  |------|--------------------|--------------------|---------|
  | 24   | 0                  | 0                  | clean (demo default) |
  | **32** | **0 (2+ full cycles)** | **0** | **highest verified clean** |
  | 48   | 1 mild 5.09 ms slip (~110s) | 0 | usable for load tests |
  | 64   | 1 transient 97.89 ms spike at launch burst; sustained clean | 0 | usable, brief spikes |

  **Recommended demo default: 24. Highest count verified running cleanly (no
  overruns, no dropped connections): 32.** Above that, 48/64 keep every
  connection and stay clean except for rare transient <100 ms tick spikes —
  genuinely usable load targets if you're hunting the server's ceiling.

---

## Live verification performed (production server 192.168.0.221:6073)

A real `vizfight attach ... 24` ran for 5+ minutes with a **real MonoGame
`--capture` client** connected to the SAME server on the SAME side (auto-seats
side 0, matching the fleet). Both views showed the same fleet at the same time:

- **Web spectator** (`http://192.168.0.140:7861/`): HUD `ships: 28`, sector
  Holokine. Snapshots at sim 62 / 164 / 357 (~1 min / 2.7 min / 5 min) each show
  the fleet **clustered within the central asteroid/station area — not drifted
  to the map edges** (bounded-over-time confirmed at all three times).
- **MonoGame client** (headless `--capture 192.168.0.221 6073 P419Cam 200 ...`,
  4279 frames): cockpit/overview frames show ~25–30 yellow friendly contact
  chevrons in a loose formation in **Holokine** — the same fleet the web view
  renders.
- **Phase cycle progressed through real narrated events** (Flight →
  Regroup → Mining → Dock → relaunch, repeated), with the server itself logging
  the real docks at Garrison.
- **Server tick cost**: 0 overruns during the 24-bot demo.

Evidence artifacts: `/tmp/p419_attach24.log` (full event timeline),
`/tmp/p419_mono/` (MonoGame frames), web screenshots captured during the run.
(These are ephemeral session artifacts; the durable, reviewable outputs are the
code + this report.)

---

## Build / tests

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
  (vizfight is a standalone project — not in the main `.slnx` — and builds
  clean on its own: `dotnet build tools/vizfight/vizfight.csproj -c Release`.)
- New `AttachLeashSteerTests` (5) pass in isolation. Must-fail verified: with the
  leash steer neutralized (push forced to 0), the `KinematicIntegration` test
  goes red ("leash should return the fleet inside the radius, ended at 24769"),
  proving removal of the change is caught, not an always-pass.
- Full `Allegiance.Core.Tests` suite: run 3× (3502 tests). Across all three runs
  only the pre-existing documented live-wire timing flakes appeared (a rotating
  subset: TurretGunner's documented persistent flake plus intermittents in
  LiveCombatWire/LiveWinCondition/Ripcord/Money/StationRescue/WarpBomb/AddPart/
  CargoBay/LiveRelaunch/ShipAsteroidCollision), each confirmed green in
  isolation; the P419 tests never failed in any run and reference no shared
  symbols with the flaky tests.

## Out of scope (untouched)
- Cross-side kills (not wire-only achievable externally — friendly-fire gate).
- `showcase`/`explore`/`roster`/`proof` modes and `wwwroot/index.html`.
- P368–P418.
