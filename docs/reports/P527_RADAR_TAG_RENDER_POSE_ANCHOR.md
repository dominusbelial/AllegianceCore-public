# P527 — Radar contacts + side tags anchor to the render pose

**Date:** 2026-10-04
**Branch:** `master` (committed, not pushed)

## Summary

Domingo's live playtest (2026-10-04) reported a HUD mismatch: *"why the miner and
the hud icon position would not match?"* — the radar-contact bracket and its
in-world side tag were visibly offset behind a steadily-moving miner. This is
P523's exact bug, left half-fixed: P523 anchored the target/lock brackets to the
render pose but the radar-contact loop remained a second, raw-pose path — the
"third path" P523's doc comment claimed did not exist.

The fix routes every radar contact, its in-world side tag, and the loop's own-ship
eye through the same shared render-pose accessor (`ResolveShipAnchor`), closing
the gap between what the hull is drawn at and where the HUD icon is placed.

## Root cause (file:line)

The hull and its HUD icon read **two different positions**:

- **Hull** is drawn at the per-frame **render pose**, `Game1._poses[id]`. For
  remote ships that is the P448b dead-reckoned pose, extrapolated every frame
  (`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:1661-1668`). For the own
  ship it is the local-sim pose (`:1576`, P508).
- **Radar contact bracket and in-world side tag (P372)** were projected from the
  **raw last `ServerShipUpdate`**, `ship.Latest.Position`
  (`src/Allegiance.ClientCore.Game/Game1.Rendering.cs:1898-1935`, pre-fix:
  `RadarProjection.Project(PoseConverter.ToXna(up.Position), ownPos, …)`). The
  side tag reused that projected `site.Point`. The distance opacity also used raw
  positions, and the loop's own-ship eye `ownPos` came from the raw own-ship
  update rather than `_poses[me]`.

Because the render pose advances every frame while the raw 20 Hz `ServerShipUpdate`
steps only every ~50 ms, the icon trails a moving hull by roughly **velocity ×
time-since-last-update** — very visible on big, steadily-moving miners.

P523 (`a56d925f`) introduced `ResolveShipAnchor`
(`src/Allegiance.ClientCore.Game/Game1.Targeting.cs:484-516`) precisely to fix this
class of jitter, but only two callers used it: `Game1.FlightInput.cs:1998` (target
bracket/label) and `Game1.Camera.cs:93` (lock bracket). The radar-contact loop was
the third path.

**Native has a single position per object:** the radar reads
`pmodel->GetPosition()` (`~/Development/Allegiance/src/WinTrek/radarimage.cpp:625`)
and the own ship at `:532` (`vecPositionPlayer = pshipSource->GetPosition()`), the
same model position the renderer draws — so native icons cannot drift from hulls.

**Off-centre mesh already ruled out:** GLB bounding-box centres for
`bios/gt/ic/rix-miner` (and fighters) are all centred on the origin (offset ≈ 0),
so P520's per-asset rotation cannot swing a hull away from its icon.

## The fix

1. **Radar loop own-ship eye** (`Game1.Rendering.cs` ~:1890): the eye now resolves
   through `ResolveRadarContactAnchor(_poses, world.Ships, me, …)` (P508 local-sim
   pose), falling back to the raw update only when absent. The station/warp/asteroid
   distance fades use the same `ownSimPos`.
2. **Per-contact anchor** (`Game1.Rendering.cs` ~:1905): each other ship's position
   is resolved via `ResolveRadarContactAnchor(_poses, world.Ships, id, …)` (P448b
   dead-reckoned pose) and used for the projection, the distance opacity, and
   therefore the side tag. Every existing gate (`IsNotVisible`, same-sector,
   `Latest` present) is unchanged.
3. **New static helper** `Game1.ResolveRadarContactAnchor`
   (`Game1.Targeting.cs`) — delegates to the shared `ResolveShipAnchor`, so there
   is genuinely one render-pose accessor. Kept pure/static and headless-testable
   (same pattern P523 used for `ResolveShipAnchor`).
4. **Doc comment** of `ResolveShipAnchor` updated to list the radar contact +
   side tag as callers.
5. **No per-frame allocations:** no closures or new per-frame buffers added
   (P326's inline discipline preserved); `ResolveRadarContactAnchor` is a static
   pure helper.

## Failing-first test

`tests/Allegiance.ClientCore.MonoGame.Tests/P527RadarTagRenderPoseAnchorTests.cs`
(3 tests), modelled on P523's tests:

- `RadarContactAnchor_ReturnsRenderPose_NotRaw_ForMovingRemote` — a moving remote
  between updates (render pose 150, raw 100) must anchor to the render pose.
- `RadarContactAnchor_FallsBackToRaw_WhenNotRendered` — no render pose → raw.
- `RadarContactAnchor_UsesLocalSimRenderPose_ForOwnShipEye` — own-ship eye (10)
  not the raw own update (720).

Verified **RED** against a raw-pose stand-in of the helper (2 of 3 failed, exactly
the render-pose precedence + own-ship-eye cases; fallback passed), then **GREEN**
once the helper delegated to `ResolveShipAnchor`:

- RED (pre-fix raw behavior): `Failed: 2, Passed: 1`
- GREEN (fix): `Failed: 0, Passed: 3`

## Build

Whole-solution build with `/home/domingo/.dotnet/dotnet build`:

- **0 Warnings, 0 Errors** (Build succeeded).

## Full suite — 3 consecutive runs (`scripts/run-tests.sh`)

The four test assemblies ran sequentially per P471's runner to avoid cross-assembly
live-socket contention.

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | **1228/1228** | 438/438 | 3705/3708 |
| 2 | 12/12 | **1228/1228** | 434/438 | 3705/3708 |
| 3 | 12/12 | **1228/1228** | 438/438 | 3707/3708 |

**The MonoGame assembly — which holds the new P527 tests and all rendering/HUD
tests — passed 1228/1228 in every run.** All remaining failures are in the live
network/sim assemblies (`ClientCore.Tests`, `Core.Tests`) and are the documented
"live-wire timing" **load-flakes** that rotate run-to-run — see
`docs/TEST_FLAKES.md`:

- Run 1 Core: `Transport.InboundFloodSafetyLiveTests.Oversized_packet_…`
  (ledger line 55, live-wire timing / load-flake), `WarpBombTests.Live_whole_mission_…`
  (line 183, live-wire delayed-blast timing), `GameOverPlayersWireTests.Conquest_win_…leak_…`
  (lines 30/31).
- Run 2 ClientCore: `ShipDeleteGoneLiveTests.*` (3×), `FullSessionKillIntegrationTests.…`;
  Core: `AutoReloadOnEmptyTests.…`, `StationUnderAttackAlertLiveTests.Second_hit_…`,
  `LiveTeamLobbyTests.Unready_broadcasts_…` (line documented).
- Run 3 Core: `GameOverPlayersWireTests.Win_by_elimination_…` (line 207).

None of these tests touch radar/rendering; the failing tests differ run-to-run and
are the known rotating live-wire receipt-timing class. P523's
`P523SelfTargetAndBracketJitterTests` remain green (5/5).

## Shield-flare follow-up (out of scope, do not fold in)

`Game1.Rendering.cs:1258-1266` — the shield-hit flare centre also reads
`tgt.Latest.Position` (raw). Same pattern, but it is a 3D particle effect, not a
HUD anchor, so it is intentionally **not** changed here. Follow-up candidate:
resolve its centre through `ResolveShipAnchor` so the flare spawns on the
dead-reckoned hull rather than the raw update.

Also noted (left as-is per scope): target-candidate gathering
(`Game1.Targeting.cs:173-181`) uses raw poses for *selection*, which is correct
(server-authoritative positions are arguably right for picking a target).

## Commit

`P527: radar contacts + side tags anchor to the render pose (finish P523's no-third-path)`
— committed on `master`, not pushed.
