# P244 — Decouple the warp transit-trigger radius from the visual funnel scale

Stage landing: P-series master, not pushed (Domingo pushes). No side branch.
Follows P243 (projectile bolt width). This stage is the "gameplay sense" fix for
Domingo's live complaints that "the aleph seems to disappear as you approach it"
and that travelling between sectors gives no visual feedback you travelled.

## 1. Relationship to P238 — a deliberate decoupling, not a revert

P238 widened the real transit/warp-bomb trigger radii to `WarpVisualScale ·
warp.Radius + X` so the whole ~950-unit visible vortex was crossable/interactable.
Its ORIGINAL bug (transit never firing when flying into the enlarged funnel) was
real and confirmed live — that must not regress.

P244 does **not** revert that. It **decouples** the two sizes that P238 had
merged:

- The **visual** funnel stays at `Sector.WarpVisualScale` = 9.5f — untouched
  (the rendered rim of a 100-unit warp is a 950-unit-radius funnel).
- The **real transit trigger** is re-tightened to the warp's raw wire radius:
  `threshold = warp.Warp.Radius + shipRadius` (no `WarpVisualScale` multiplier),
  mirroring native's real physics collision at the warp's `GetRadius()`
  (`shipIGC.cpp:629-636`). A ship must now actually reach the warp's real body to
  cross — so the aleph only stops rendering once the player is deep inside the
  huge funnel, not while still approaching its rim.

### Exact new threshold formula and constants

`src/Allegiance.Core/Model/Sector.cs`:

- Ship transit — `ResolveWarpCollisions`:
  `float threshold = warp.Warp.Radius + shipRadius;`
  where `shipRadius = 0.5f * ship.HullType.Length`. For a typical 100-unit warp
  and a ~38-unit-long scout this is **~118.7 units** (vs P238's ~965).
- Warp-bomb (missile) trigger — `ResolveWarpBombTriggers`:
  `float threshold = warp.Warp.Radius + missile.MissileType.Radius;`
  tightened by the same decoupling for cross-consumer consistency (both real
  gameplay consumers of `warp.Radius` now mean "the warp's real physical size",
  matching native's collision at `missileigc.cpp:459-467`).
- `Sector.WarpVisualScale = 9.5f` and `WarpRenderer.VisualScaleMultiplier`
  (aliased) are unchanged — they are now used **only** to scale the render mesh.
- `ServerHost.HitWarpEvent` exit-velocity/direction (lines 675-702) untouched
  (out of scope, P246 tunes it). The P238 exit offset at line 723
  (`exitClearance = WarpVisualScale · rw.Warp.Radius + shipRadius + 5`) is **kept**
  as-is: with the re-tightened ~119-unit trigger it is far outside the destination
  sphere, so the ship emerges clear and **cannot ping-pong** — the P238
  no-oscillation guarantee is preserved (and re-confirmed live, §5).

## 2. Render-filter cliff — evaluated, no grace window needed

The render filter at `Game1.cs:1082-1085` (`if (wp.ClusterId != myWarpSector)
continue;`) removes the source sector's warps the instant the ship's `SectorId`
changes. Under P238 the trigger fired at ~965 units, so the aleph vanished while
the player was still approaching the funnel's rim — the "disappearing out of
nowhere" bug. After §1 the trigger fires at ~119 units, which is ~8× inside the
funnel's 950-unit rim; the player is already deeply enveloped by the vortex.

The decision to **not** add a grace window (no keep-rendering-for-N-frames, no
fade) rests on the live evidence in §5: the real funnel's glowing ring grows
monotonically from ~45% of the frame (at 2000 units) to ~55-65% (~975), ~70-80%
(~940), and ~85-95% (filling the frame, ~855) — and **completely engulfs the
player** by ~500 units, i.e. hundreds of units before transit fires at ~119. At
the vanish moment the aleph is screen-filling/enveloping, so the cut reads as
"I flew through it," not "a distant dot vanished." A linger/fade would only soften
an already-natural cut and would tread on P245 (screen transitions), which is
explicitly out of scope.

## 3. Test changes and the "must-fail-if-removed" proof

Rewrote the three P238-era "between old-and-scaled" proofs into P244 regression
guards, and added a tight-boundary positive:

- `WarpProximityTests.ShipJustInsideRawRadius_TriggersHitWarpEvent` (**new**) — a
  ship at `0.9·(warp.Radius + shipRadius)` triggers: the tight trigger engages near
  its boundary (anti-regression half — transit still fires).
- `WarpProximityTests.ShipOutsideWarpRadius_NeverTriggersHitWarpEvent` — now placed
  just beyond the RAW radius (pins the trigger's upper bound; was scaled-outside).
- `WarpProximityTests.ShipBetweenRawAndVisualRadius_DoesNotTriggerHitWarpEvent`
  (renamed from the P238 "now triggers") — a ship at the P238-between distance must
  **no longer** raise HitWarpEvent.
- `WarpTransitServerTests.ShipBetweenRawAndVisualRadius_DoesNotPrematurelyTransit_AndPeerSeesNoSectorChange`
  (renamed from the P238 live proof) — live wire negative: the between-radius
  spawn stays in its sector; the positive (tight-radius crossing) remains
  `ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`.
- `WarpBombTests.Domain_missile_between_raw_and_visual_warp_radius_does_not_trigger_warp_bomb`
  (renamed from the P238 "now arms") — a missile at the between distance no longer
  arms a bomb; the dist-0 positive stays.

**Must-fail proven**: with the two thresholds temporarily reverted to the P238
widened forms, all four discriminating tests went red (transit/bomb/events fired
at the rim where P244 forbids). Reverted clean after.

The cross-cluster NPC navigation tests (`NpcCrossClusterLiveTests`,
`NpcCrossClusterMinerDecisionLiveTests`, `NpcRipcordTravelLiveTests`) were
unaffected: their `WarpClearance = WarpVisualScale·radius + margin` placements are
even further outside the re-tightened trigger, and all passed in the full suite.

## 4. Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Core.Tests: **3066 total** (baseline 3065 at task start; +1 net test added),
  2 consecutive full-suite runs **3065 passed / 1 failed**, the single failure both
  times being the documented pre-existing `LiveTurretGunnerBindingTests` machine-
  state flake (fails even on the pristine tree; not this stage's regression).
- MonoGame.Tests: **242 passed** (the P237 `VisualScaleMultiplier` calibration test
  stays green on the aliased shared constant).
- ClientCore.Tests: **83 passed**.

## 5. Live proof (real server + real client world + real renderer)

Phase A — real in-process `Allegiance.Server` (XXV_simple.igc) + a real
`ConnectionSession`. The client's ship was spawned 830 units from a real warp
(#1051, radius 100) and flown in with a measured kinematic approach. The distance
sequence descends 802 → 774 → … → 186 → 158 → **130** (sector Arrokoth 1051
throughout — under P238 this spawn at 830, inside the ~965 rim, would have
transited immediately), then:

    [warp] ship 8096 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
    [P244] tick 51: dist=9328.0 sector=1057  <<< TRANSITED   (tight trigger 118.7)

**(a) transit still reliably fires** — at the tight ~118.7 radius, not at the rim.
Full log: `.verify_p244/p244_run.log`.

Phase B — the real `WarpRenderer` (same funnel Game1 draws, 9.5× scale) rendered
through the real `CockpitCamera` at measured distances to offscreen RTs. The
glowing ring's apparent size (PNGs vision-verified; `rt.GetData` is unreliable on
this Wayland backend, so the PNG is the source of truth) grows monotonically:

    dist 2000 → ~45% of frame      dist  ~870 → ~85-95% (fills frame)
    dist  ~975 → ~55-65%           dist  ≤~500 → engulfs player (ring beyond FOV)
    dist  ~940 → ~70-80%           dist  ~130  → deep inside (transit ~119)

**(b)** at the moment transit removes the aleph from the render list the player is
enveloped by the screen-filling vortex — the opposite of "a distant/small dot."
Frames: `.verify_p244/warp_{2000,855,757,670,597,538,130}u.png`,
`.verify_p244/post_transit_empty.png` (source-warp-absent reference).
The filesize identity among the dark frames (15791 B) is the P207 lesson's frozen/
uniform-scene check — those are the engulfed (inside-the-ring) views, not an
artifact.

## 6. Scope boundary / exclusions

- Screen flash/fade/transition effect: **P245**, not built here (even partially).
- `ExitWarpSpeedBonus` tuning: **P246**.
- Warp visual mesh/colour/translucency (`WarpRenderer.cs`, P227/P242): untouched.
- `ServerHost.HitWarpEvent` exit-velocity/direction (675-702): untouched.
- Native reference (`~/Development/Allegiance`): read-only, untouched.
- Raw wire `DataWarpIGC.Radius`: never multiplied (decode/encode byte-faithful).
