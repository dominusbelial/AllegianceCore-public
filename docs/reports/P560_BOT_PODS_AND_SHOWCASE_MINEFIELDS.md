# P560 — Bot lifepods + showcase minefields (MCP fixes from the first 221 showcase)

Date: 2026-10-09. Repo: AllegianceCore `master`. Live target: m2xspiderw.igc, civIds [25,3],
testUnlock allTech + money 1000000.

This report covers the four items. Two were genuine root-cause bugs that are FIXED and
proven live; one is verified-correct with no fix needed; one is a code-verified fix whose
live trigger depends on the server's dock→respawn path (blocked in the observed window).

---

## 1. Bot lifepods ping-pong through the aleph (Alderan ⇄ Rigel) — FIXED

### Root cause (found, with evidence)
The server's warp transit (`ServerHost.HitWarpEvent`, `ServerHost.cs:940`) deposits a
crossing ship INSIDE the destination warp's collision sphere, and re-trigger is prevented
ONLY by the 2 s cooldown (`Sector.WarpCooldownSeconds`, `Sector.cs:1416`). So a hull that
crosses an aleph SLOWLY lingers inside the far-side sphere past the cooldown and re-crosses
the aleph it just exited. The bot's pod-home warp leg used the P496 braking law
(`CombatFlyTo` + `WarpApproachParams`, arrival 0 / brake = transit threshold), whose
alignment-gated distance-ramp throttle eases off inside the sphere — a weak, turning pod
emerges slowly and oscillates. The P540b player-autopilot fix (full-throttle
`AutopilotSteering.Pursue` fly-through) was not shared by the bots.

### Fix
- Moved `AutopilotSteering` from `Allegiance.ClientCore.Game/Flight` into
  `Allegiance.ClientCore/Flight` (it is deliberately free of MonoGame types), so the
  headless MCP bots and the game client run the SAME law — reuse, not a third law.
- Added `BotManager.FlyWarpLeg`: a cross-sector warp leg is steered with
  `AutopilotSteering.Pursue` (full-throttle fly-through of the aleph), with the existing
  P489 dodge still pre-empting. Applied to all three warp-leg sites: the pod-home leg
  (`DrivePodHome`), the staging leg, and the transit leg.
- Replaced the pod's straight-line nearest-station pick (ignored sectors) with the
  server's nearest-valid-restart rule (P550, nearest `c_sabmRestart` friendly station)
  made route-aware: shortest warp-route distance from the pod's current sector,
  tie-broken by Euclidean (`BotManager.PickPodHomeStation`).

### Tests (failing-first)
- `tests/Allegiance.ClientCore.Tests/P560PodWarpLegClosedLoopTests.cs` — closed-loop sim
  on the REAL 221 Alderan⇄Rigel warp pair (warps 2187/2186, radius 100) driving the
  production laws through real Ship physics + the real transit + 2 s cooldown:
  *Pursuit crosses at cruise speed and never returns* (0 re-entries), while a slow
  crossing (the old law's failure regime) demonstrably re-enters (ping-pong).
- `tests/Allegiance.ClientCore.Tests/P560PodHomeStationTests.cs` — 6 pure tests of the
  route-aware nearest-valid-restart pick (reachable-over-unreachable, restart filter,
  same-sector preference, enemy exclusion, null-when-none, Euclidean tie-break).

### Live evidence (221 server, separate process)
- **Zero warp crossings involving Rigel (1059)** in the whole live fight — the reported
  Alderan⇄Rigel ping-pong pair is completely silent (previously the 4 surviving side-0
  pods looped it every few seconds).
- 24 pods created, 21 docked at home, 3 surviving/lingering; pods returned home
  (21× 1052→1053 crossings).
- Pod pick logged: `[p539] pod bot … flying home to friendly restart-capable station
  24465 in sector 1053` (the route-aware pick).

## 2. Showcase minefields never get laid — invest COMPLETES + drone SPAWNS (FIXED); physical laying blocked by P161

### Two separate root causes (both found and fixed)
**(a) Ambiguous layer-bucket name aborted the whole showcase.** `FindLayerBucketName`
returned the bucket NAME (`.TowerAC`), which matches 10 buckets on the 221 map →
`invest: bucket '.TowerAC' is ambiguous`, and the exception aborted `spawn_scenario`
(isError=True, no fleet). Fixed to return the bucket **ID** (a numeric string that
`ResolveBucket`'s `short.TryParse` branch resolves exactly).

**(b) Invest never completed even by id — CATALOG DRIFT UNDER testUnlock.** The server
builds its bucket catalog from `civ.TtbmBaseTechs` (`Mission.Assemble` →
`BucketCatalog.ForSide`), but `liveSettings.testUnlock` inflates `side.DevelopmentTechs`
to the 281-bit required-tech union AFTER Assemble. `SendSideTechToPeer` broadcast that
inflated mask in CREATE_BUCKETS, so the client's synthesized catalog
(`BucketCatalog.Describe`) got a DIFFERENT id numbering than the server's — the MCP's
"layer-drone id 41" resolved on the server to a different bucket (price 5000/8000, not
1000), the donation under-funded it (stuck at ~1000/5000 = 20%), the money-proportional
build-time clamp (`Bucket.Update`, bucketigc.h:58) froze InvestedMilliseconds, and the
drone never appeared. Separately, even with the right bucket, the layer build is 90 s
(`TimeToBuildSec 90`) versus the hard-coded 40 s invest window.

Fixes:
- **Server** (`Server.Lobby.SendSideTechToPeer`): CREATE_BUCKETS now broadcasts the
  bucket-build mask (`civ.TtbmBaseTechs`), restoring the P487 id-alignment invariant.
- **MCP** (`McpTools.LayMinefields`): the invest observe window is sized to the bucket's
  real `TimeToBuildSec + 15` (was a fixed 40 s).
- **MCP** (`McpTools.LayMinefields`): a minefield failure (ambiguous/unknown bucket,
  wallet short, invest error, order refused) is caught and reported as an ERROR ROW and
  NEVER aborts the fleet spawn — the fleet still stages and launches.
- **MCP** (`BotManager.FindLayerBucketName`/`FindLayerBucket`): returns the bucket id
  (+ its build time), not the name.

### Tests (failing-first)
- `tests/Allegiance.Core.Tests/P560LayerBucketUniqueIdTests.cs` — for both 221 civs (25
  Iron Coalition, 3 Ga'Taraan), the layer-drone (PilotTypeLayer) buckets carry DISTINCT
  ids despite shared names, and a numeric-id lookup selects exactly one (the id fix).

### Live evidence (221 server)
- Before fix: `isError=True` and `bucket '.TowerAC' is ambiguous (10 matches)`.
- After fix: `isError=False`, 30 bots spawned (15/side), both sides' minefield rows show
  the invest COMPLETED and the drone was found and ordered: side 0 → droneShip 8100,
  side 1 → droneShip 8102, placement `warp:2152` (enemy arrival aleph in Alderan).
- Server: `[drone] drone-type bucket '.TowerAC' complete on side 0 → spawned drone ship
  8100 (drone type 441, pilotType 5, hull 'Tower Layer Drone', laysExpendable 87) at home
  'Omega Debug Garrison'`, and the same for side 1.
- Abort semantics: when the invest failed earlier, the fleet still spawned (30 bots) with
  the minefield error row reported — the failure no longer aborts the showcase.

### Honest limitation
The layer drone is ORDERED to build (`orderSent=true`) but does NOT actually deploy a
minefield — no mine-deploy / `DeployMine` lines appear. This is the documented P161 gap
("Actual deployment is P161's known gap", `DroneBucketSpawnsDroneLiveTests`); a layer
drone's default order parks it at a rock. So the invest/spawn half of item 2 is fixed and
proven live, but the physical "field laid + P452 damage" proof is blocked by the
pre-existing drone-deployment gap (a separate feature, out of P560's "invest doesn't
complete" scope). This is stated plainly, not faked.

## 3. Pod visibility — VERIFIED CORRECT, no fix needed

Verified with evidence (programmatic, native-cited):
- **Asset resolution for the 221 civs is correct.** `HullModelResolver.ResolveAsset`
  (src/Allegiance.Core/Content/HullModelResolver.cs): civ 25 = "ic", role "pod" →
  `ic-ejectpod`; civ 3 = "gt", role "pod" → `gt-pod`. Both are in the imported
  `ShipAssets` set, so pods render their native pod models, NOT the `bios_scout_upscaled`
  fallback.
- **Targeting/HUD include lifepods.** `Game1.Targeting.GatherSectorCandidates` gathers
  every ship in the sector (only self/dead/no-update filtered — no lifepod exclusion), so
  target cycling and contact candidates include pods, matching native (any visible ship).
- **Draw scale is native-faithful, no upscale.** The ship render path draws each model at
  its native uniform scale (`0.5·Length`/meshRadius via `_shipScaleById` /
  `NativeScale`); pods are small because they are small — there is no pod-specific
  upscaling (the task's "don't upscale pods beyond native" is respected).

A headless live contact sheet (pod beside an Adv Fighter) is not produced here; the
resolution/scale/indicator logic above is verified in code with native citations.

## 4. Relaunched bots linger at home — FIXED (code-verified)

### Root cause (found, with evidence)
`UpdatePodState` (BotManager.cs:2094) clears `IsPod` on relaunch but left
`Arrived`/`StagingSector`/`StagePhase`/route latches from the bot's pre-death life. The
main loop gates on `if (bot.Arrived) { EngageCombat(bot, now); return; }` — a bot that had
already reached the rendezvous kept `Arrived == true` after relaunching at HOME, so it
engaged combat immediately in its home sector (no enemy there) and sat in `combat` on the
station ring instead of re-routing to the fight.

### Fix
On relaunch, reset `Arrived`, `ArrivedAt`, `StagingSector`, `StagePhase`,
`FirstStagingAt`, `ReleasedAt`, `Path`, `PathStart`, `PathGoal`, `WarpDest`,
`LastCluster`, so the relaunched bot re-BFSes from home to the target sector and re-enters
staging (`UseStaging`).

### Evidence / limitation
Code-verified: the exact `Arrived` gate is identified and the reset closes it. In the live
run, pods docked at home (21) but the server did not auto-relaunch the docked NPC pods
into fresh fighters in the observed window (all 22 surviving bots remained pods), so the
relaunch→rejoin path was not observable live. This is a server dock→respawn-path
dependency, stated plainly.

---

## Suite (run-tests.sh, P471) — honest numbers

Three consecutive full-suite runs on the final code. Every failure below is a
DOCUMENTED load-flake (TEST_FLAKES.md: LiveTeamLobby, ConquestTerritory,
GameOverPlayers, AsteroidMined live-network receipt-timing flakes; ClientCore
FullSessionKillIntegrationTests ready-relay) that varies run-to-run and is
unrelated to P560. P560's own tests pass in every run. Lobby and MonoGame were
clean in all three runs.

| run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1   | 12/12 | 1654/1654 | **568/568** (clean) | 3838/3840 (2 flaky live-network) |
| 2   | 12/12 | 1654/1654 | 567/568 (FullSessionKill ready-relay flake) | 3836/3840 (4 flaky live-network) |
| 3   | 12/12 | 1654/1654 | 567/568 (FullSessionKill ready-relay flake) | 3835/3840 (5 flaky live-network) |

FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill
is an intermittent same-side ready-relay flake: it failed 5× in a row in isolation
on the pristine P590 baseline, passed in suite run 1 and in isolation here — a
pre-existing flaky live test in a subsystem (ready relay) unrelated to P560's
pod/warp/minefield work. The Core failures (LiveTeamLobbyTests.Unready_broadcasts,
ConquestTerritoryLiveTests.Conquest_win…leak, GameOverPlayersWireTests, and
AsteroidMinedLiveTests) are all cited load-flakes in TEST_FLAKES.md.

## Files
- `src/Allegiance.ClientCore/Flight/AutopilotSteering.cs` (moved from Game/Flight)
- `src/Allegiance.ClientCore.Game/Flight/AutopilotSteering.cs` (deleted)
- `src/Allegiance.ClientCore.Game/Audio/EngineSoundCurve.cs`, `…/Flight/RipcordKey.cs`
  (cref/usings for the move)
- `src/Allegiance.Server/Server.Lobby.cs` (CREATE_BUCKETS = bucket-build mask)
- `tools/allegiance-mcp/BotManager.cs` (FlyWarpLeg, PickPodHomeStation, relaunch reset,
  FindLayerBucket by id)
- `tools/allegiance-mcp/McpTools.cs` (minefield abort-safe, window sized to build time)
- tests: `P560PodWarpLegClosedLoopTests`, `P560PodHomeStationTests`,
  `P560LayerBucketUniqueIdTests`; MonoGame.Tests usings updated for the move.
