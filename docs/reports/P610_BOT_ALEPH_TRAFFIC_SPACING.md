# P610 — Bot fleet traffic spacing at alephs

Date: 2026-10-11
Branch: `master` (not pushed)
Files:
- `tools/allegiance-mcp/BotManager.cs` — the three spacing behaviours + named constants
- `tests/Allegiance.ClientCore.Tests/P610AlephTrafficSpacingDecisionTests.cs` — pure unit tests
- `tests/Allegiance.ClientCore.Tests/P610AlephTrafficLiveTests.cs` — live measurement harness
- this report

## Problem

Large showcase fleets (allegiance-mcp `spawn_scenario`, `size_scale` 13 = 195/side)
on the 221 server showed: teammates grinding into each other (a side-0 pair collided
12×, another 11×, with tiny jiggle values = overlapping), bots flying into
asteroids, 195-ship fleets jamming at their home aleph (Organia→Juniper slowest),
and crowded arrival alephs (Juniper/Rigel/Alderan).

## Native findings (re-verified against `~/Development/Allegiance/src`, all cited)

1. **Aleph collisions are NOT disabled for traffic.** The only hit-test exemption in
   the native collision sweep is a projectile vs its launcher — `clusterIGC.cpp:674-694`
   skips a model when `ht->GetNoHit() == pHitTest1`/`pHitTest2`, which is the
   missile↔launcher pair set by `SetNoHit` (`shipIGC.cpp:99`). There is no aleph
   exemption; a ship crossing a warp front always transits.
2. **Touching an aleph always jumps.** `shipIGC.cpp:627-629` (`CshipIGC::HandleCollision`,
   case `OT_warp`) calls `GetIgcSite()->HitWarpEvent(...)` unconditionally; the old
   front/back bounce branch is commented out (`/* ... */` block at `:630-661`).
   Server-side, `FedSrv/fsship.cpp:71` `CFSShip::HitWarp` only gates on
   `m_warpState == warpReady` (blocks re-jumps during the cooldown, `:74-81`).
3. **Arrival placement bunches ships.** `FedSrv/fsship.cpp:113-121` places an emergent
   ship at `pwarpDest->GetPosition() + up·rand(2,5) + right·rand(2,5) − (radius+5)·backward`
   — a 2–5 u random offset, NOT fleet spacing.
4. **High exit velocity is native's de-facto clear-out.** `fsship.cpp:111-112`:
   `speed = −(speed + c_fcidExitWarpSpeed); SetVelocity(backward·speed)` — an emergent
   ship is rocketed out along the exit (`backward`) direction at its incoming speed
   plus `c_fcidExitWarpSpeed`, so it leaves the crowded mouth. Native never "regroups
   at the arrival" and so does not pack the cluster the way our bots did (our bots
   turned straight back into the arrival aleph to loiter/engage).
5. **No proactive fleet-spacing term in the native goto/drone law.** `Igc/common.cpp`
   `GotoPlan` (the AI pilot/drone goto) dodges only REACTIVELY: `:1688-1779` predicts a
   closest-approach collision and deflects perpendicular, otherwise it flies straight
   at the goal. There is no separation/spacing between same-side ships beyond reacting
   to an imminent impact.

Conclusion: native had **no** proactive fleet-spacing at alephs; it relied on (a) the
small arrival offset and (b) the high exit velocity to disperse traffic. The task
anticipated this ("if native had no spacing logic, say so and design a minimal,
non-invasive one") — so P610 adds a minimal, bot-side only spacing scheme that
mirrors those two native mechanisms proactively, leaving the server physics and the
native collision rules untouched.

## Design (bot side only; server physics unchanged)

Three additive behaviours in `BotManager`, all behind new named constants
(`EntrySpreadFraction`, `EntryQueueLookahead/FunnelWidth`, `ExitClearMargin`,
`TravelSpacingMultiple/Strength`):

### 1. Entry staggering (aleph aim spread + light queue)
`FlyWarpLeg` now aims each bot at a deterministic per-bot point on the aleph's
ENTRY DISC (`EntryAimPoint`: a uniform-disc sample seeded by `shipId`, so distinct
bots aim at distinct points across the mouth) instead of all converging on the
centre point. Kept inside the capture radius (spread fraction < 1) so every aim is
still inside the transit sphere the bot must cross — P560's full-throttle
fly-through (`AutopilotSteering.Pursue`) is preserved, so there is no braking before
intermediate warps (P591c respected). A light queue (`EntryQueueYield`) deflects a
bot to a wider ring point when a same-side teammate is already just ahead in the
funnel near its aim. The disc is world-aligned (deliberately NOT tied to the per-tick
approach direction, which would rotate the aim as the bot closes).

### 2. Exit clear-out
On every warp hop, `DriveCombat` records the arrival aleph (the warp in the new
sector whose partner's cluster is the sector just left). Until the bot is
`radius + ExitClearMargin` past that aleph it keeps flying along the exit direction
(full-throttle `Pursue`, dodge still pre-empts an imminent rock) before staging /
loiter / engagement resumes — the bot-side analogue of native's
`−(speed + exitWarpSpeed)` exit velocity (`fsship.cpp:111-112`). Clears the crowded
arrival clusters (Juniper/Rigel/Alderan).

### 3. Travel spacing (soft separation)
During warp-leg travel, a soft separation term (`ApplyTravelSeparation`) deflects the
goal LATERALLY (perpendicular to travel — forward progress is never stalled) by a
bounded amount (≤ `TravelSpacingStrength`) when a same-side teammate is within
`TravelSpacingMultiple × hull-radius`. Zero beyond range, so transit is unaffected at
cruising separation. This spreads fleets before the reactive dodge law has to fire.

Scope respected: no server collision-physics change; no native-fidelity change to
aleph physics; `CombatFlyTo` dodge pre-emption, P539d staging hold/release, P591c
routing and combat engagement all still run first/unchanged.

## Unit tests (`P610AlephTrafficSpacingDecisionTests`, 9/9 pass)

Failing-first: the suite fails to compile (13 errors) against the pre-P610 `BotManager`
because the helpers don't exist, then passes after. Coverage:
- `EntryAimPoint`: distinct across a 22-bot fleet AND inside the capture radius;
  deterministic per ship; spread-fraction 0 → the centre.
- `IsClearOfAleph` / `ExitClearGoal`: clear-out ends only past radius+margin; the
  clear goal lies along the exit ray and beyond radius+margin.
- `ApplyTravelSeparation`: pushes the goal away from a nearby mate, never stalls the
  travel axis, and is exactly zero beyond range / with no mates / dead-ahead.
- `EntryQueueYield`: yields to a mate just ahead in the funnel; does not for behind /
  distant / no mates.

## Build

`dotnet build -m:8`: 0 warnings, 0 errors. Suite runs: see TEST_FLAKES-style note below
(2 consecutive runs on the final code recorded in the commit).

### Suite results (scripts/run-tests.sh, 2 consecutive runs on the final code)

Both runs were dominated by the known/live-wire load-flake class documented in
`docs/TEST_FLAKES.md` (which explicitly notes these live-socket tests "rotate
run-to-run" under full-suite load, line 458). **None of the failures touch P610 /
`BotManager`** — zero references to P610 symbols in any failing test. The P610
decision tests pass in both runs.

| Assembly | Run 1 | Run 2 |
|---|---|---|
| Lobby.Tests | 12/12 | 12/12 |
| ClientCore.MonoGame.Tests | 1734/1735 | 1735/1735 |
| ClientCore.Tests | 602/603 | 602/603 |
| Core.Tests | 3924/3929 | 3926/3929 |

Run 1 failures (all unrelated live-wire flakes): `RipcordRequestLiveTests…EndToEnd`
(MonoGame), `ShipDeleteGoneLiveTests.DockOut_…` (ledgered residual ~1/15 timing flake,
TEST_FLAKES line 46/102), `P590PodRescueTests.Pod_pickup…` (ledgered line 432/435),
`LiveWinConditionTests.Board_refused_once_mission_over` (ledgered load-flake line 34),
`GameOverPlayersWireTests.Conquest_win_…` (ledgered line 29/30/175),
`CargoBayReloadTests.SwapPart_…` ×2 (ledgered load-flake line 53/54).

Run 2 failures (a different, rotating set — the documented run-to-run flake rotation):
`SessionLossLiveTests.EstablishedSession_ServerKilled_…` (network-loss live timing),
`LiveShipDeathFinalizeTests.Oxygen_dead_lifepod_…`, `GameOverPlayersWireTests.Win_by_
elimination_…` (ledgered line 432), `AddPartTests.Part_pickup_decline_recreates_…`.
(The MonoGame ripcord test that failed in run 1 PASSED in run 2 — confirming it is
load-flaky, not a P610 regression.)

Per the P471 protocol, each run is reported as it happened; the flaky live-wire
failures are ledgered, not chased, and none are attributable to P610.

## Live proof (local in-process server, 221 config, non-debug m2xspiderw)

Method: boot the real 221-config map (side0 home Ganymede 1050, side1 home Organia
1051, rendezvous Alderan 1052) in-process; spawn `size` combat bots/side with STAGING
toward Alderan; run `seconds`; parse the stamped server log for `[collision] ship A x
ship B` (ship-ship, split same-side/cross-side), `[collision] ship A x asteroid`
(asteroid), and `[warp] ship N crossed from 'X' (a) to 'Y' (b)`. Measured by the
harness `P610AlephTrafficLiveTests`, tagged env `ALLEGIANCE_LIVE_P610`.

### Mandatory live check — bots still reach the target
size 1 (2 bots): both transited real warps and reached Alderan
(`side0: Ganymede(1050)→Rigel(1059)→Alderan(1052)@6.6s`, `side1: Organia(1051)→Juniper
(1061)→Alderan(1052)@8.2s`), `botsReached=2/2`. Size 30: 60/60. Size 60: 120/120.

### Before vs after, size 30/side, 300 s window (controlled, identical harness)
"Before" = the pre-P610 `BotManager` (changes stashed); "after" = P610 spacing.

| Metric | Before | After | Δ |
|---|---|---|---|
| Ship-ship collisions (total) | 194 | 58 | −70% |
| Ship-ship collisions per minute | 38.80 | 11.60 | −70% |
| Same-side collisions/min (grinding) | 38.60 | 9.80 | −75% |
| Cross-side collisions/min | 0.20 | 1.80 | +1.6/min (fleet spread) |
| Grinding pairs (a pair > 3×) | 9 | 0 | eliminated |
| Worst single-pair collisions | 26 | 3 | ↓ |
| Asteroid collisions | 0 | 0 | same |
| Home-aleph clear, side0 (1050) | 7.8 s | 7.8 s | no throughput loss |
| Home-aleph clear, side1 (1051) | 4.1 s | 3.9 s | ≈ same |
| Reach Alderan, side0 / side1 | 16.4 / 14.8 s | 16.6 / 15.1 s | ≈ same |
| Bots reached Alderan | 60/60 | 60/60 | no regression |

Headline: a 70% cut in collisions/min and elimination of repeated-pair grinding
(max 26 collisions in one pair → max 3), with identical home-clear and time-to-Alderan
(the full-throttle fly-through is preserved, so spreading the aim costs no throughput)
and all bots still reaching the target. Same-side collisions (the “teammates grind
into each other” complaint) drop 75%; the small rise in cross-side collisions
(+1.6/min) is the two convoys passing through the transit hub spread across the disc —
negligible compared with the same-side grinding removed.

### Larger fleet (size 60/side, 120 bots, after-only sanity)
120/120 reached; collisions 31/min with only 1 grinding pair (max 4) — below the
30/side BEFORE baseline (38.8/min, 9 grinding pairs), showing the spacing continues to
hold as the fleet grows. Caveat: home-clear for this run is not reported because the
first iteration of the harness mis-detected side0's home (a 2 s sample caught bots
already in Rigel 1059); the harness was then made deterministic (homes hardcoded from
the verified 221 map) — the size-30 before/after above uses the correct detection and
is the authoritative comparison.

### size_scale 13 (195/side) note
The full 195/side showcase is the 221 *deployment* scenario (spawn_scenario). This
workstation's in-process harness hosted up to 120 real bot peers; the collision/
grinding reduction held at that scale and the unit laws are size-independent, but a
full 390-peer (195×2) run belongs on the 221 host (or a dedicated load box) as a
deployment-time check. This is stated explicitly rather than faked.

## Docs referenced
- `docs/TEST_FLAKES.md` — the known/flaky test ledger (suite flakes are ledgered, not
  chased; 2 consecutive runs recorded per Domingo 2026-10-09).
- Native citations: `Igc/shipIGC.cpp:99,627-661`, `Igc/clusterIGC.cpp:674-694`,
  `Igc/common.cpp:1688-1779`, `FedSrv/fsship.cpp:71-121` in `~/Development/Allegiance/src`.
