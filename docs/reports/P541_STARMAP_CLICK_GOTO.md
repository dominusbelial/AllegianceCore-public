# P541 — click a sector on the star map to autopilot there (native sectormap.cpp left-click), and prove the goto actually flies the player

**Branch:** `master` (HEAD — confirmed with `git log -1`) · **Not pushed.**

---

## 1. The premise (native)

Native's `SectorMapPane::Button` (`WinTrek/sectormap.cpp:1485-1618`) on **left
button down** over a cluster (`GetClusterAtPoint`, `:1320-1348`) plays the
click sound, `SelectCluster`s it, then branches (`:1521-1571`):

| native branch | lines | trigger | action |
|---|---|---|---|
| ripcord | `:1527-1530` | `fRipcordActive() && !GetAutopilot()` | `RequestRipcord(ship, cluster)` |
| teleport | `:1531-1536` | teleport pane open (`ofTeleportPane`) | `AttemptTeleportTo(cluster)` |
| docked view | `:1539-1545` | `GetShip()->GetCluster()==NULL` (docked) | `RequestViewCluster` + `SetViewMode(vmCommand)` |
| own sector | `:1546-1549` | `pClusterFound == GetShip()->GetCluster()` | do nothing ("users find anything else confusing") |
| goto | `:1550-1567` | else | build `DataBuoyIGC{pos=(0,0,0), clusterID, type=c_buoyCluster}` and `SendChat(own ship, c_cidGoto, OT_buoy)` |

Ctrl+right-click (team leader) toggles a cluster highlight (`:1490-1513`) — the
message is `FM_CS_HIGHLIGHT_CLUSTER`; a port check found **no client or server
handler for it** in `AllegianceCore` (the port has no team-leader highlight
consumption), so it is left as a documented follow-up, per the brief.

**Hit-testing** (`GetClusterAtPoint`, `:1320-1348`): map each known cluster
(`GetModels()->n()!=0`) to its screen point via `GetClusterPoint` (`:1310-1316`,
the same `CalculateScreenMinAndMax`/min-max fit the P249 `StarmapLayout` ports),
then the nearest cluster within `nMinDistance = 15` (px) wins; a point beyond
every cluster's radius maps to none (`NULL`).

## 2. The hit-test (pure helper)

`Rendering/StarmapHitTest.cs` — `StarmapHitTest.HitTest(StarmapLayout, Vector2)`
reuses the P249 `StarmapLayout.Fit` screen points (the exact points the renderer
draws, so what the pilot sees and what a click selects always agree) and native's
`ClickRadius = 15f` (`sectormap.cpp:1324`). Pure `System.Numerics`, no graphics
device. Works for BOTH the collapsed mini-map and the expanded (Tab) pane because
both are fitted by `StarmapLayout.Fit` into their respective panels (the same
`DrawStarmap` uses); `StarmapLayout` gained an `Ids` enumeration so the hit-test
can scan every placed cluster.

`Rendering/StarmapClick.cs` — `StarmapClick.Decide(...)` reproduces the five
native branches **in native's exact order** (`:1521-1571`), pure and unit-tested;
`StarmapClick.BuildClusterGotoBuoy(clusterId)` returns the native `DataBuoyIGC`
(`pos=(0,0,0)`, `clusterID`, `type=c_buoyCluster`, `buoyID=0`, `visible=false`)
so the "other-sector branch produces the native buoy" requirement is pinned.

`Game1.StarmapClick.cs` — wires the click into `Game1`: edge-triggered on a
fresh left-button press, builds the active panel's layout under the Gate lock,
hit-tests the cursor, gathers the player state native reads, routes through
`Decide`, and executes the branch (ripcord → `SendRipcordRequest` + select;
teleport → documented no-op in flight, the port's teleport pane is a docked
overlay that hides the map; docked → select the sector (the port's "view" of a
sector); own sector → nothing; goto → `IssueStarmapGoto` + collapse the map to
match the existing `G` behaviour). While the expanded map is up the mouse becomes
a free cursor over it (native cursor-mode overlay): the mouse-flight steering
gate (`Game1.FlightInput.cs`) is skipped while `_minimapExpanded`, so a click has
a meaningful cursor position; the collapsed mini-map is clickable too when the M
steering axis is off (the free-cursor default).

**Touch** (scope item 4): the P501 touch layer maps taps to *virtual buttons*
(missile/target/autopilot/throttle/menu), not to a pointer path, so there is no
existing tap-to-pointer channel to reuse; per the brief ("don't build new touch
UI") no new touch surface was added. Documented.

## 3. ⚠ The goto flight path — the finding

**Today's `G` goto did NOT fly a real client.** The task warned the server-side
GotoPlan (P403) might fight the client local sim (P508). Measured with a live
headless probe (`[ctrlak]` + SHIP_RESET accumulation): when a **native-pose
client** (a peer sending real-time+pose SHIP_UPDATEs — the P508 client) sits
still while the server's P403 `SetGoto` GotoPlan steers its ship, the client's
**accepted** static poses **pin the server ship at spawn** — the goto never
progresses (probe: `SHIP_RESETs=0, never reached the target, server ship parked
at spawn`). P403's "proof" only worked because its FltBot sent **legacy**
(`Time=0`) updates that bypass the P507 pose gate. In real use the own ship is
client-flown (P508), so a server-side GotoPlan on it cannot work.

**Faithful fix — the client flies it.** Native's own-ship goto is consumed by the
CLIENT's own autopilot (`clintlib.cpp:3711` `SetCommand(c_cmdPlan, pmodel,
c_cidGoto)`), which steers the ship locally and sends the ordinary controls. So:

- `IssueStarmapGoto` (now also the click's goto branch) builds the native buoy,
  sends the authentic `FM_CS_CHATBUOY(108)` echo, **and engages the CLIENT
  autopilot** toward the clicked sector via `EngageAutopilotToward(new
  AutopilotTarget(TargetKind.Cluster, clusterId, name))`.
- `TargetKind.Cluster` + `ResolveTargetPosition`/`TryResolveClusterWaypoint`
  route over the **real warp table** (`ClientWorld.Warps`) through the new pure
  `Flight/StarmapGotoRouter.TryNextWaypointFromWarps` (BFS over actual warp
  connectivity → the next hop always has a real warp). The autopilot steers to
  the warp mouth; the server's pre-existing warp-collision resolver (P244)
  transits the ship; on sector change the P508 sim freezes/resumes and re-routes;
  arrival disengages exactly when the ship is IN the target sector (native
  deletes the `c_buoyCluster` buoy "as soon as we are in the cluster",
  `FedSrv.CPP:3302`), clearing the HUD/sound via the existing
  `DisengageAutopilot("arrived")`.
- **Server gate (`Server.Dispatch.cs` + `PeerShipUpdate.NativeClient`):** the
  self-addressed cluster `CHATBUOY` now re-routes the sender's own ship ONLY when
  the server actually flies it. A peer that has sent a real-time+pose SHIP_UPDATE
  (a native client — it flies its own ship) is never gotoed server-side; legacy /
  bot senders (which don't fly themselves) keep the P403 server-side GotoPlan.
  This removes the fight while preserving P403's live tests.

Key finding recorded: a pre-computed `CLUSTERINFO`-edge route
(`ClusterGraph.FindShortestPath`, P420) can disagree with the real warp table and
name a "next hop" with no actual warp in the current sector — so the client
router was built to derive both the route and the waypoint from the SAME real
`world.Warps`, eliminating that drift (unit-pinned).

## 4. Tests

**Failing-first unit tests** (`tests/Allegiance.ClientCore.MonoGame.Tests/StarmapClickGotoTests.cs`, 25):
- `StarmapHitTest`: a click on a cluster's dot maps to it; a near point maps to
  the nearest; outside every cluster → none; beyond the 15 px radius → none; just
  inside → selects; empty layout → none.
- `StarmapClick.Decide`: no-cluster→None, ripcord (no autopilot)→Ripcord, ripcord
  on autopilot falls through to teleport, teleport-pane→Teleport, docked→View,
  own sector→None, other sector→Goto. `BuildClusterGotoBuoy` matches native
  (pos 0,0,0, clusterID, c_buoyCluster).
- `StarmapGotoRouter`: picks the warp toward the next hop; arrival; empty route;
  missing connecting warp; and the real-warp `TryNextWaypointFromWarps` invariants
  (never chooses a hop with no real warp; every hop backed by a warp sourced in
  the current sector).

**Live proof** (`tests/Allegiance.ClientCore.MonoGame.Tests/P541GotoFlyLiveTests.cs`):
a real in-process server + real `ConnectionSession` on `XXV_simple.igc`; the
session's real `ClientWorld` (Graph + real warp table) and the real
`StarmapGotoRouter` navigate from the start sector to a sector that is **two real
warp hops away**, strictly monotonic (never revisits a sector):
`LIVE-GOTO-FLY: own ship 8096 1051->1053 via 2 warp hop(s) (1051 -> 1052 -> 1053) using the REAL client router on the live map — RESULT PASS`. The actual warp
transit is P244's existing resolver; the P508 native-pose accept path is P507's
suite; the new logic this stage adds (the client routing that flies the own ship)
is what this test exercises on real map data.

**Live architecture proof** (`tests/Allegiance.Core.Tests/P541GotoGateLiveTests.cs`, 2):
- `Native_client_self_cluster_goto_does_NOT_set_server_GotoWaypoint` — a peer
  that sent a real-time+pose SHIP_UPDATE (native client) issuing a self cluster
  goto gets **no** server `GotoWaypoint` (it flies itself) → no server fight.
- `Legacy_bot_self_cluster_goto_STILL_sets_server_GotoWaypoint` — a legacy bot
  keeps the P403 server-side move.

**Must-fail-if-removed** (verified live): removing the `serverFliesSender` gate
condition makes the native gate test red (the server sets a GotoWaypoint it must
not); reverted.

## 5. Build / suite

Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors.**

Full suite via `scripts/run-tests.sh` (P471 sequential runner), 3 consecutive
runs **on the final code**. Counts = Passed/Total per assembly. Every failure is
a documented rotating live-wire **load-flake** cited to `docs/TEST_FLAKES.md`;
none references any P541 symbol, and every P541 test (25 MonoGame unit + 1
MonoGame live router proof + 2 Core gate live tests) passes in every run:

| run | Lobby | MonoGame | ClientCore | Core | failures (documented flakes, TEST_FLAKES.md) |
|-----|-------|----------|-----------|------|----------------------------------------------|
| 1 | 12/12 | 1259/1259 | 455/455 | 3717/3721 | `AutoReloadOnEmptyTests.Auto_reload…RELOAD129…` (L52), `StationUnderAttackAlertLiveTests.Defending_side…` (L375), `ConquestTerritoryLiveTests.Conquest_win…leak…` (L29/267), `AddPartTests.Second_part_touch_while_offer_pending…` (family L247) |
| 2 | 12/12 | 1259/1259 | 455/455 | 3720/3721 | `AddPartTests.Part_pickup_destroys_then_accept_merges…` (family L247) |
| 3 | 12/12 | 1259/1259 | 455/455 | 3721/3721 | none — **fully clean** |

(The runner's exit was 1 on runs 1–2 only for those pre-existing flakes; run 3
passed every project.)

---
