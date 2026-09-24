# P363 — Ship side-coloured motion trail (native TrailGeo ribbon)

Native effect ported: the ship's continuous **ribbon/strip** motion trail —
`ThingSite::SetTrailColor` → `CreateTrailGeo` → `TrailGeo`
(`effect/thinggeo.cpp:921-954`, `effect/efgeo.cpp:492-661`). This is a
genuinely distinct third visual system from the two particle-puff systems this
port already had — P297's damage-vent smoke (`DamageDebrisParticles.cs`) and
P358's engine-thrust fire/smoke (`ShipEngineTrailParticles.cs`) — and the only
one that is real continuous **strip geometry** built from a rolling history of
anchor positions, tinted by the ship's real **side/team colour**. It is what
Domingo remembered as every ship leaving a long bluish trail: it is motion-gated
(not thrust-gated), applies to every ship, and is coloured by the side — native
side 1 is `(0, 138, 217)` blue, so a player habitually on that side saw bluish
trails.

## The real native ribbon algorithm (read from efgeo.cpp)

`TrailGeo` (efgeo.cpp:492-661) is a quad strip with up to
`maxSections = 16` sections, `timeDelta = 0.5 s`:

- **Geometry** — `(maxSections+1)·2` vertices (each section is a left/right
  endpoint pair) and `maxSections·6` indices. Consecutive section-pairs are
  joined into quads (efgeo.cpp:516-539). Texcoords are `(0,0)`/`(1,0)` per
  endpoint (efgeo.cpp:540-543) — the trail image spans the ribbon's **width**.
- **Evaluate** (efgeo.cpp:596-637) runs every frame the anchor is non-zero.
  Every `timeDelta` (0.5 s) the whole history shifts one section back
  (`for(index = m_section*2+1; index > 1; index--) vertices[index]=vertices[index-2]`,
  efgeo.cpp:621-626) and a new head is placed at the current anchor
  `± m_vecRightLast`. The right vector is refreshed from the ship's orientation
  **only while moving** (`m_pbooleanMoving`, set from `m_vecPosition != vec` in
  `SetPosition`, efgeo.cpp:1149-1159), and is
  `orient.GetRight() · m_radius · 0.25` (`SetOrientation`, efgeo.cpp:1161-1183).
- **Anchor** — the head sits at `m_vecPosition + TransformOffset(m_vecTrailOffset)`
  where `m_vecTrailOffset` is the local position of the model's **"trail"-named
  hardpoint frame** (`SetTrailColor`, thinggeo.cpp:935-938:
  `if (data.m_strName == "trail")`).
- **Colour** — `SetTrailColor` halves the side colour before building the geo
  (thinggeo.cpp:941: `Color(color.R()*0.5f, color.G()*0.5f, color.B()*0.5f)`),
  then `UpdateColors` (efgeo.cpp:570-592) tapers each section by
  `bright = 1 − dtime − k/maxSections` where
  `dtime = (time − m_timeLast)/(timeDelta·maxSections)` — head bright → tail
  transparent. Rendered **additively** (`BlendModeAdd`), Gouraud, cull-none,
  no z-write (efgeo.cpp:641-657).
- **Stop behaviour** — when the ship stops, `m_vecRightLast` freezes and the
  sections keep advancing on the 0.5 s cadence but pile at the stationary
  anchor; the older laid-down sections slide toward the (dim) tail, so the
  trail collapses back into the ship and fades over ~16·0.5 s rather than
  extending. This is the "stops extending / fades when stopped".

## Where the real side colour came from

Native hands the trail its colour from the live side:
`pThingSite->SetTrailColor(pside->GetColor())` (shipIGC.cpp:149) and
`ship->GetThingSite()->SetTrailColor(ship->GetSide()->GetColor())`
(trekigc.cpp:4419). `Side::GetColor()` returns `m_data.color`
(sideigc.h:369) — the per-side RGBA in `DataSideIGC` (igc.h:2064) that the
mission setup fills by **side id** from a fixed six-slot palette
(`UpdateSides`, missionigc.cpp:3734-3746:
`ds.color.SetRGBA(sideColors[sid][0..2])`): side 0 Gold, 1 Blue, 2 Purple,
3 icky-yellow, 4 icky-orange, 5 icky-magenta. **The colour is a static property
of the side slot, not of the civilization.**

The port had **no** per-side colour anywhere (the `GameOver` endgame codec
carried the `SideEndgameInfo.Color` field but `ForMission` wrote the neutral
gray, documented "this port has no per-side color to read"). So, per the task's
"scope it honestly rather than faking a hardcoded colour" rule, this was real
(small) new work — the colour now lives server-side and reaches the client:

1. **`Side.ColorR/G/B/A`** (Model/Side.cs) — the port of `m_data.color`,
   seeded in `Mission.Assemble` from the new `SidePalette.ColorFor(sideId)`
   (Model/SidePalette.cs), the exact port of native `UpdateSides`'s palette.
2. **`SIDE_ROSTER(211)` widened 30 → 46 B** (Wire/SideRoster.cs) — each
   `SideRecord` now carries the side's RGBA (four floats, the native 16-byte
   zlib `Color`). `Server.SendSideRoster` fills it from `Side.ColorR..A`; the
   roster is already pushed to every validated peer at logon (Server.Dispatch.cs
   :112), so the colour reaches the client before flight.
3. **`GameOver.ForMission`** (Wire/GameOver.cs) now writes the sides' **real**
   colours instead of gray — generalising the very decode the task pointed at
   (the existing `SideEndgameInfo.Color` field was designed for exactly this,
   fsmission.cpp:3073).
4. **`ClientWorld.TryGetSideColor(sideId, out r,g,b)`** — the client resolves a
   ship's colour from the roster it already holds; `Game1.UpdateMotionTrail`
   feeds the pool per visible ship via its real `SideId` (a neutral-gray
   fallback if no roster arrived yet).

No new wire *message* was needed — the existing join-time `SideRoster` was the
natural, already-delivered vessel, and `GameOver` was made truthful for free.

## Hardpoint-position approach — aft-centre/centre approximation (consistent with P358)

Native anchors the ribbon at the model's **"trail"-named hardpoint** offset
(`thinggeo.cpp:938`), and the ships in this port genuinely carry that frame
(e.g. `quizfigv2.mdl` has `thrust`/`trail`/`smoke` hardpoints). P358 faced the
same category of question for its engine exhaust and investigated real per-hull
`.mdl` hardpoint extraction, rejecting it: this port renders **Meshy-regenerated
content hulls** (P287/P351-356) whose coordinate frame (nose −Z) and per-asset
scale differ from the native `.mdl` geometry, so mapping a native hardpoint
offset onto 70+ regenerated hulls is not cleanly portable. **This task reuses
that decision for consistency**: the ribbon anchor is the ship's **centre**
(position), i.e. `m_vecTrailOffset = 0`. For a *motion* trail (as opposed to the
aft-directed engine exhaust) the centre anchor traces the flight path cleanly on
the actually-rendered hull, and the native's other trail parameters are ported
exactly: width `= orientation.GetRight() · radius · 0.25` (radius
`= 0.5·HullType.Length`, the P358/P338 rendered-radius convention), 16 sections,
0.5 s cadence, and the half-intensity side colour with the linear head→tail
brightness taper.

## Implementation

- **`Rendering/ShipMotionTrailRibbons.cs`** — the pure, MonoGame-free ribbon
  pool (the MissileTrail/DamageDebris/ShipEngineTrail conventions). `SetAnchor`
  reproduces `ThingSite::SetPosition`/`SetOrientation`/`SetTrailColor` (motion
  from anchor-vs-previous-head, right frozen while not moving, 0.5 s section
  cadence, 16-section cap); `Advance(dt)` is the native mission `Time`;
  `PruneUntouched` tears down vanished ships' ribbons (like a destroyed
  ThingSite). `BrightnessFor(dTime, k)` exposes the native taper for testing.
- **`Rendering/ShipMotionTrailRenderer.cs`** — additive soft-strip quad renderer
  (native `BlendModeAdd`, Gouraud, cull-none, no z-write): each section quad's
  colour is `side · 0.5 · brightness` with alpha = brightness, so the head glows
  and the tail dissolves; restores all three GPU states after (P339 B2).
- **`Game1.cs`** — feed every visible ship's anchor/right/side-colour in the
  pose-gather (local ship outside the cockpit, matching P358/P297; all remote
  ships), `PruneUntouched` after the gather, advance + draw additively after the
  engine-trail pass.
- **Tests** — `ShipMotionTrailRibbonsTests.cs` (9 pure pool tests: 0.5 s cadence,
  head-track, right-freeze-on-stop, stop-collapses-to-anchor, 16-cap, per-ship
  colour independence, prune, brightness taper, reset); `ShipTrailSideColorTests.cs`
  (8: palette vs native table, Assemble seeding, GameOver real colours, SideRoster
  codec + RecordSize 46); `ClientWorldUnitTests.TryGetSideColor` (1).

## Verification — live multi-side proof (real server → real wire → real pool → real renderer)

Evidence: `.verify_p363/frames/*.png` + `p363_run.log`. Harness (`/tmp/p363verify`):
real in-process `Allegiance.Server` (`PCore001` / `XXV_simple`, **teams:2** →
side 0 = Gold, side 1 = Blue) + two real `ConnectionSession` peers (ships
8096/8097, one on each side). Each frame reads the server-authoritative ship
Position/Orientation, resolves the ship's real `Side.ColorR..A`, feeds the REAL
`ShipMotionTrailRibbons` (SetAnchor with native right `= GetRight()·radius·0.25`),
and renders the REAL `ShipMotionTrailRenderer` offscreen (960×640, top-down
camera perpendicular to the flight plane so the flat ribbon is seen face-on).
Metrics: per-half lit-pixel counts + average RGB (the dominant-colour proof) and
the PNG size.

- **Phase A (both stationary)** — `strips=0`, 0 lit, black baseline (24719 B):
  no motion → no trail.
- **Phase B (both fly apart)** — both ships trail: **Gold ribbon** (ship 8096,
  side 0, screen-right in the top-down mirror) `avg=(39.7,33.5,0.0)` (R+G
  dominant, B=0) and **Blue ribbon** (ship 8097, side 1, screen-left)
  `avg=(0.0,28.7,45.5)` (B dominant, R=0). Vision confirms *two distinct ribbons,
  cool blue/cyan on the left, warm gold/amber on the right*. → the trail renders
  for a remote ship too, tinted by **its own** real side colour — the two sides
  differ, so the colour genuinely varies by side (not hardcoded).
- **Phase C (local ship 8096 stops, remote 8097 keeps flying)** — the stopped
  ship's gold ribbon fades from **568 → 0 lit pixels** over ~89 frames (visual:
  "extremely faint, nearly invisible"), while the still-moving blue ribbon holds
  at **606 lit**. → the trail correctly stops extending / fades when the ship
  stops, and only a moving ship's trail persists.

## Build / tests

- Whole-solution build: **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **678 passed** (9 new).
- `Allegiance.ClientCore.Tests`: **169 passed** (1 new).
- `Allegiance.Core.Tests`: my 8 new tests green. The full Core suite shows only
  the pre-existing documented live-wire flakes (set varies run to run) **plus**
  one deterministic failure — `GameMessageRoundTripTests.SetCluster_EmitsExactDeclaredBytes`
  — which is **not** a P363 regression: it is caused by unrelated **uncommitted
  P362 (weapon-status-HUD) work already sitting in the working tree** (HEAD is
  P361), whose `ServerShipUpdate.cs` change makes the ammo byte real. Proven by
  isolating: the three non-documented Core live failures fail identically with
  P363's changes removed; `SetCluster` fails from P362's `ServerShipUpdate`
  diff alone. P362 is explicitly out of scope ("don't touch") and is left as the
  in-progress working-tree state it was in. This task commits **only its own
  files**.
