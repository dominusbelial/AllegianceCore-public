# P776 — aleph trigger is native's cone, not a 110 u sphere

Base: master `57b930bd` (worktree `allegiance-pi-p776`, branch `pi/p776`). No commit/push; changes
left in the working tree. P775's five files (`Server.Broadcast.cs`, `Server.Lobby.cs`, `Server.cs`,
`Server.Combat.cs`, `TrainingHost.cs`) were not touched and were not needed.

## Native rule

**The aleph trigger is a real collision with the aleph's hit-test shape, and that shape is a cone,
not a sphere.**

- `CwarpIGC::Initialize` loads the warp with `LoadWarp` (`Igc/warpIGC.cpp:33`), radius
  `warpDef.radius` (`:39`), orientation from `dataWarp->forward` (`:45`).
- `CmodelIGC::LoadWarp` creates the hit test with `c_htsCone` (`Igc/modelIGC.cpp:127-130`) — not
  `c_htsSphere`.
- `BoundingCone::GetExtreme` (`_Utility/HitTest.cpp:254-283`) is the support function: local extreme
  is the tip `(0,0,R)` when `direction.z >= sqrt(2)/2`, else a rim point of the radius-`R` circle at
  `z = 0`. World mapping is `local * orientation` (`_Utility/HitTest.cpp:878-879`); in the native
  row-vector convention local `+z` is the orientation's **backward** row
  (`zlib/orientation.h:83-92`: `GetForward() = -row2`, `GetBackward() = row2`).
- So the solid is: **a base disc of radius R through the aleph centre, perpendicular to forward,
  plus a 45° cone behind it with its apex at `centre + R·backward`.** Nothing lies on the forward
  side of the disc. Its cross-section in `(rho, z)` (`z` backward, `rho` perpendicular distance) is
  the triangle `(0,0), (R,0), (0,R)`.
- `HitTest::Collide` (`_Utility/HitTest.cpp:114-207`) uses bounding spheres only as a broad phase;
  overlap produces a candidate even while separating (`:166-185`), and the real test is `HullCollide`
  over the tick interval (`:199-200`, `:921-1015`) with **no closing-velocity condition**.
- `CshipIGC::HandleCollision` `case OT_warp` calls `HitWarpEvent` unconditionally
  (`Igc/shipIGC.cpp:629-636`); the "not closing: ignore / hit the back: bounce" logic is commented
  out (`:638-715`). A ship parked at the centre with zero velocity therefore warps natively. A
  velocity·(centre−ship) gate would be non-native and was **not** implemented.
- `CFSShip::HitWarp` (`FedSrv/fsship.cpp:71-136`) only fires when `m_warpState == warpReady` (`:74`);
  players go to `warpNoUpdate` (`:79-82`), **drones never leave `warpReady`** (no cooldown);
  orientation = destination warp + random pitch/yaw (`:90-118`); velocity =
  `backward * -(speed + c_fcidExitWarpSpeed)` (`:123-127`); position =
  `dest + up·rand(2,5) + right·rand(2,5) − (GetRadius()+5)·backward` (`:129-132`).
- Player re-arm: NoUpdate → Waiting on an accepted update, then Ready after `+2.0 s`
  (`fsship.cpp:1185-1191`).

**Consequence:** the native exit puts the ship's centre `GetRadius()+5` **in front of** the base
disc — its hull is ≥ 5 u clear of the cone, moving forward. No hull overlap ⇒ `HullCollide` false ⇒
no re-warp, however long it lingers. The 2 s re-arm is not what prevents re-entry.

## Port before / after

**Before (`57b930bd`).** `Sector.ResolveWarpCollisions` point-sampled a **sphere** once per chunk:
`|ship − warp| ≤ warp.Radius + shipRadius` (`Sector.cs`, old `threshold`). For `m2xspiderw` every
warp has `Radius = 100`, so the trigger was a ~110 u ball on both sides of the disc.
`ServerHost.HitWarpEvent` placed the ship natively — centre `shipRadius+5` in front of the
destination disc — i.e. **inside that ball**. The P406 comment claimed native also exited inside its
raw collision sphere and relied on the 2 s debounce; that claim was wrong. The only thing stopping an
immediate re-warp was the 2 s window (`WarpCooldownSeconds` for NPCs, the `PlayerWarpState`
handshake/fallback for players). A hull still within ~110 u when it re-armed warped straight back.

**After.** `src/Allegiance.Core/Model/AlephConeHitTest.cs` implements the native cone as a pure
helper: normalise `Forward` (zero falls back to identity's `(0,0,-1)`), `b = −forward`, `z = (c−P)·b`,
`rho = |(c−P) − z·b|`, contact iff the 2-D distance from `(rho,z)` to the triangle
`(0,0),(R,0),(0,R)` is `≤ shipRadius`. The ship is the same bounding sphere `0.5·Length` the combat
pass uses (`Sector.ResolveCombatCollisions`; native `Igc/shipIGC.cpp:1419`) — native uses the hull's
convex shape, which remains an approximation.

`ResolveWarpCollisions(now, dt)` now:
- takes `dt` from the call site (`dt` from `Sector.Update`);
- does a cheap broad phase `|ship−warp| > Radius + shipRadius + |v|·dt` → skip;
- sweeps the chunk segment `Position − CollisionVelocity·dt` → `Position` (the
  `ResolveTreasurePickups` pattern) at spacing `≤ max(shipRadius,1)`, capped at 16 samples, firing on
  the first sample that touches (native's test is continuous over the tick, `HitTest.cpp:114-207`);
- leaves `WarpReady`, `WarpCooldownSeconds`, the P591b/P591c guard block and its release threshold,
  layer suppression, and the one-warp-per-tick `break` unchanged.

Comments that stated the wrong rule were rewritten in `Sector.cs` (trigger remarks,
`WarpCooldownSeconds`, guard summary/inline) and in `ServerHost.cs` (P406 block, comment only).
Optional comment-only wording fixes were made in `tools/allegiance-mcp/BotManager.cs`,
`BotManager.Orders.cs` and `src/Allegiance.Server/Server.ShipCommands.cs`; their logic and their
~110 u standoff numbers are unchanged. `ClientWorld.cs` was not edited.

### Step 0 finding — legacy bots and the 2 s fallback

Confirmed: the MCP bots are **legacy controls-only** peers. They never send a posed SHIP_UPDATE
(`src/Allegiance.Server/Server.Dispatch.cs:286-297`, legacy branch). Their `PlayerWarpState`
therefore leaves `NoUpdate` only through `Ship.OnUpdate`'s fallback at exactly
`LastWarpTime + Sector.WarpCooldownSeconds` (`src/Allegiance.Core/Sim/Ship.cs:8798-8809`). That is
why every rig bounce is 1.9–2.0 s (Claude's log histogram: 1.9 s = 6,590; 2.0 s = 8,663;
2.1–5.0 s = 0; all 15,253 sub-2.2 s warps are reversals).

## Rig evidence (Claude's batch-16 analysis, quoted not re-derived)

Batch-16 (200v200, `m2xspiderw`): side-1 ships 9459, 9471, 9447 bounced Tellus (1055) ↔ Omega (1053)
through warps 2157/2156 every ~1.99 s from 17:03 to 17:07 UTC (9459: 128 crossings in one run).
Across the log **15,253 of 39,916 warps (38 %) by 320 ships** were a reversal of the same ship's
previous warp less than 2.2 s earlier. Warp 2157 is at `(-1080, 720, -340)`, 2156 at
`(-2960, 2400, -259)`, both `R = 100`; the ships' collision positions stayed 20–35 u from the aleph
centre on the forward side (the native exit spot: inside the old 110 u ball, outside the cone). Peer
123 logged `accepted=0` all run (legacy). All three had relaunched into hull 126 Guardian (length 20,
max speed 30, thrust 200); in 2 s a Guardian moves at most ~75 u from a 15 u exit, so under the
sphere rule it always re-warped. Bot routing was not the cause (the next-hop alephs lie in front of
each arrival aleph, cos ≈ 0.97 and 0.55).

## Tests

New file `tests/Allegiance.Core.Tests/P776AlephConeTriggerTests.cs`.

| # | Test | On unfixed base |
|---|---|---|
| 1 | `Cone_section_matches_native_geometry` (9 theory cases, unit + non-unit + tilted forward) | new API; cannot run on base |
| 2 | `Exited_ship_lingering_in_front_of_the_destination_aleph_does_not_rewarp` (Guardian 30, Fighter 10) | **FAIL** (6 crossings vs expected 1) |
| 3 | `Two_ships_warping_together_do_not_bounce` | **FAIL** (both ships 6 crossings) |
| 4 | `Npc_exited_slowly_does_not_rewarp` | **FAIL** (6 crossings) |
| 5 | `Ship_reaching_the_aleph_still_warps` (front / back / slow 5 u/s / parked at centre) | passes both |
| 6 | `Fast_ship_crossing_the_rim_is_caught_by_the_sweep` (50 u/tick near `rho = 0.95R`) | passes both; **fails when the sweep is disabled** (`steps = 1`), demonstrated once |
| 7 | `WarpProximityTests.Ship_in_front_of_the_disc_inside_the_old_sphere_does_not_trigger` | **FAIL** (5 `HitWarpEvent` calls vs 0) |

Base-failure check was run with the old `Sector.cs` restored from `HEAD` while keeping the new helper
compiled: failures were exactly tests 2 (both rows), 3, 4 and the new case in 7. Tests 5 and 6 passed
on both. The sweep demonstration (temporary `steps = 1`) failed test 6 and was then restored.

### `WarpProximityTests` placement changes

- `ShipInsideWarpRadius_TriggersHitWarpEvent`: `warpPos + (0,1,0)·0.3R` → `warpPos + backward·0.3R`
  (inside the cone; an arbitrary axis can be in front of the disc).
- `ShipJustInsideRawRadius_TriggersHitWarpEvent`: `normalize(1,1,0)·0.9(R+r)` → `side·0.9R` on the
  base disc (inside the triangle; keeps the "near the boundary" intent).
- `ShipOutsideWarpRadius_NeverTriggersHitWarpEvent`: arbitrary diagonal → `backward·(R+r+margin)`
  (just beyond the apex, the only axis the cone extends).
- `ShipBetweenRawAndVisualRadius_DoesNotTriggerHitWarpEvent`: arbitrary `(0,1,0)` → `backward·dist`
  (beyond the apex; still inside the P238 visual volume).
- `RecentlyWarpedShip_InsideVolume_IsDebounced`: `(0,1,0)·0.3R` → `backward·0.3R` (inside the cone,
  where the trigger would fire without the debounce). The test itself is unchanged.
- Added `Ship_in_front_of_the_disc_inside_the_old_sphere_does_not_trigger` (fails on base).

### Fixture changes in the item-8 classes (and fast-tier discoveries)

All are "the fixture entered at the old sphere's front rim; move it onto the cone", never a widened
trigger:

- `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`: spawn
  `forward·0.9(R+r)` → `backward·0.9R`, velocity toward the centre.
- `P616TrackerNativeBugTests.AlephTransit_ChangesNeitherFuelNorCargo`: same change.
- `GeneratedWarpEntry.Find` (used by `MapGenLiveProofTests.GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour`
  and `MapGenRingLiveProofTests.GeneratedHiLo_PeerFliesThroughWarp_ToTracedNeighbour`): candidates
  moved from eight arbitrary directions to three points on the cone axis (`z = 0.25R, 0.5R, 0.75R`),
  holding moved to the backward axis.
- ClientCore `ShipDeleteGoneLiveTests` (`SameSide_...`, `CrossSide_...`): teleport into the warp
  `forward·0.9(R+r)` → `backward·0.5R` (and the return-warp pose equivalently).
- ClientCore `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`:
  same change (fast-tier failure; not in the brief's item-8 list).
- MonoGame `P698StaleShipsLoopbackTests.Departed_ship_...` `warp-and-die`: same change (fast-tier
  failure).
- `ThreePeerSeededSessionTests.SeedThreePeers...` failed once in the first fast-tier run with a logon
  ordering assertion; it passed 3/3 alone and in the final fast-tier run — load flake, no change.

No fixture needed a longer pump, and no assertion used the P406 "exit lands inside the destination
sphere" idea.

## Measurements

### Repro (scratch `P776ReproTests`, 10 s at 20 Hz, bot defaults)

The repro spawns just inside the old ~110 u sphere on the front side (~108 u in front of the disc)
and flies in. Before/after on the same test:

| hull, entry speed | before | after |
|---|---|---|
| 126 Guardian, 30 | 6 | 0 |
| 121 Fighter, 10 | 6 | 0 |
| 121 Fighter, 100 | 1 | 1 |

The two slow rows become 0 because that spawn is outside the cone and the bot's default controls are
full-reverse throttle — the hull brakes to a stop ~28 u (Fighter 10: ~103 u) in front of the disc and
never transits. That is a different outcome from the bounce. The bounce itself is test 2, which
spawns at the native exit spot (5 u in front): **before 6 → after 1** for both hulls; test 3 before
6/6 → after 1/1; test 4 before 6 → after 1. This is the meaningful before/after for the reported bug.
The brief's rig numbers (15,253 / 38 % / 320 ships, 1.9 s 6,590, 2.0 s 8,663) are Claude's log
analysis and were not re-derived here.

### Gates

- Whole-solution build (the fast-tier runner's "Building solution"): **success**.
- AOT publish `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64
  --self-contained -p:PublishAot=true -o .scratch/aot`: **success**, 14 MB native binary, 0 warnings,
  0 IL2026/IL3050.
- 3× alone (every run green; counts are tests per run), all listed classes: 18/18 for
  `P776AlephConeTriggerTests`, 6/6 for `WarpProximityTests`, then:
  `WarpTransitServerTests` 4/4, `P591bLayerPingPongTests` 3/3, `P591cDroneRoutingTests` 6/6,
  `P760ServerCallbacksTests` 13/13, `P650ServerRulesTests` 3/3, `LiveP379DebugTopologyTests` 3/3,
  `MultiHopDroneLiveTests` 1/1, `NpcCrossClusterLiveTests` 7/7,
  `NpcCrossClusterMinerDecisionLiveTests` 2/2, `MapGenLiveProofTests` 2/2,
  `MapGenRingLiveProofTests` 3/3, `P616TrackerNativeBugTests` 5/5; ClientCore
  `ClientWarpApproachDodgeLiveTests` 1/1, `ClientWarpApproachSlowHullLiveTests` 2/2,
  `P560PodWarpLegClosedLoopTests` 1/1, `P699PodHomeLiveTests` 2/2, `P539dFairContactLiveTests` 2/2,
  `P682NextMatchCombatLiveTests` 8/8, `ShipDeleteGoneLiveTests` 3/3,
  `CrossSideCombatVisibilityLiveTests` 4/4, `ThreePeerSeededSessionTests` 1/1; MonoGame
  `P698StaleShipsLoopbackTests` 3/3.
- Fast tier (`tools/run-tests.sh --tier fast`): **PASS**, 9,859 tests in 270.8 s, fast infrastructure
  PASS. (The first run, before the fixture fixes above, failed on `P698` warp-and-die,
  `CrossSideCombatVisibility...`, and the `ThreePeerSeeded` load flake; all are addressed/confirmed.)
- Showcase never-parks 5×: **5/5 pass** after; 5/5 pass on the base too. Each run prints
  `[warp] ... crossed` lines (352 warps over five after runs; 356 over five base runs). The lines
  carry no timestamp or tick stamp, so the requested "same-ship reversal under 2.2 s" count cannot be
  computed from them without adding logging (not done). As a proxy, same-ship **consecutive**
  reversals were 0/352 after and 0/356 on the base — the showcase ships are fast and did not
  ping-pong in either build.

## Matrix

`docs/parity/sim-rules-matrix.csv` (LF, 16 fields/row): `SIM-0208` and `SIM-0150` notes appended as
specified; both statuses stay **partial** (SIM-0208: exit wobble/roll and the hull-accurate ship shape
are still approximations; SIM-0150: the NoUpdate→Ready fallback remains). `parity-matrix.csv` was not
edited (it has no warp-trigger row). Verified: 474 rows, every row 16 fields, `parity-matrix.csv` 666
rows/6 fields unchanged, `git diff --stat docs/parity` shows exactly 2 changed lines.

## Out of scope / follow-ups

- **Missile-vs-warp** (`ResolveWarpBombTriggers`) is still a sphere. Native uses the same cone for
  missiles; this is a follow-up candidate. Its doc comment now says so.
- The **NPC `WarpCooldownSeconds`** is port-only (native drones never leave `warpReady`,
  `fsship.cpp:79-82`) and the **player NoUpdate→Ready fallback** for legacy clients
  (`Ship.cs:8798-8809`) remain. Removing either is a separate change.
- Exit wobble/roll (`fsship.cpp:90-118`) and hull-accurate (convex) ship shapes remain
  approximations.
- Bot side: the 407 reversals in the 5–15 s band and the loadout picking the 30 m/s Guardian as a
  "temporary Fighter" (`hull role score=1600.00`) are separate candidates; no bot logic changed.
- The P591b guard stays opt-in and off.
