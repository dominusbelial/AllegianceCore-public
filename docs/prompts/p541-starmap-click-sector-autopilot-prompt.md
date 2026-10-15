# P541 — click a sector on the star map to autopilot there (native sectormap.cpp left-click), and prove the goto actually flies the player

## Why this task exists

**Domingo (2026-10-04):** "I should be able to click any sector on the star
map and it should auto pilot me into it."

**Native** (`~/Development/Allegiance/src/WinTrek/sectormap.cpp:1485-1570`,
`SectorMapPane::Button`). On **left button down** over a cluster
(`GetClusterAtPoint`), it plays `mouseclickSound` and calls
`SelectCluster(pClusterFound)`, then:
- **if ripcording and not on autopilot:** `RequestRipcord(ship, cluster)`
  (`:1525-1528`);
- **else if the teleport pane is open:** `AttemptTeleportTo(cluster)`
  (`:1531-1535`);
- **else if docked** (`GetCluster()==NULL`): `RequestViewCluster(cluster)` +
  `SetViewMode(vmCommand)` (`:1539-1544`);
- **else if it's the current cluster:** do nothing, "users find anything else
  confusing" (`:1545-1548`);
- **else:** create a `DataBuoyIGC { position=(0,0,0), clusterID, type=c_buoyCluster }`
  and `SendChat(ship, CHAT_INDIVIDUAL, own ship id, …, c_cidGoto, OT_buoy, b)`,
  a goto order to *yourself* (`:1549-1567`).
- **Ctrl+right-click** (team leader) toggles the cluster highlight and sends
  `FM_CS_HIGHLIGHT_CLUSTER`. A non-leader can only clear it (`:1490-1507`).
  Port it too if the message already exists in the port; otherwise leave it
  as a follow-up.

**The port today:**
- The keyboard equivalent exists (P403): `Tab` (expand) → `,`/`.` select →
  `G` calls `IssueStarmapGoto` (`src/Allegiance.ClientCore.Game/Game1.Panes.cs:765`).
  It builds the same `c_buoyCluster` buoy and sends `FM_CS_CHATBUOY(108)`,
  addressed `ChatTarget.Individual` to the own ship
  (`Game1.FlightInput.cs:~1225-1236`). Its own doc comment says the `,/.`
  selection "stands in for the mouse click (see P249/P272)". **There's no
  mouse hit-testing on the star map.** The layout lives in
  `Rendering/StarmapLayout.cs` / `SectorInfoPaneLayout.cs`.
- ⚠️ **Unverified flight path:** P403 says "the server translates the
  received order into a real autopilot move", but the player's own ship is
  **client-simulated** (P508 local sim; P522 re-seed; P447 prediction). In
  native, the client receives its own goto command and **its own autopilot**
  flies the ship (check `clintlib` command receipt, e.g. `clintlib.cpp:~3711`
  `SetCommand(c_cmdPlan, pmodel, c_cidGoto)` and how the client autopilot
  consumes it). Before adding the click, **prove live whether today's `G`
  goto actually flies the player to another sector** (including warp
  transits) without the server and local sim fighting (snaps, SHIP_RESETs,
  `[ctrlak]` rejects). If it doesn't, fix the path so the client-side
  autopilot (P524's, with warp-path routing to the target cluster) flies it,
  faithful to native.

## Scope

1. **Mouse hit-testing** on the star map: both the minimap and the expanded
   (`Tab`) map, the cluster under the cursor, using the same layout math the
   renderer uses. Make it a pure helper.
2. **Left-click behaviour, exactly as native's branches above:** select
   (+ click sound), then ripcord target / teleport / docked view /
   own-sector no-op / goto self. The goto reuses `IssueStarmapGoto`. Should
   the expanded map collapse after a goto click? Match the existing `G`
   behaviour, which collapses it.
3. **The goto must actually fly the player there** (see ⚠️). Verify and, if
   needed, fix it: multi-warp routing to the target cluster, arrival
   behaviour, manual-input disengage, and the autopilot sound/HUD state.
4. Touch input: if the Android/touch input layer maps taps to the same
   pointer path, a tap should behave like a left-click. Don't build new
   touch UI.

## Explicitly out of scope

- Wingman/squadron selection (the port has none, P403).
- Map visuals and restyling.
- Server-side NPC goto behaviour.

## Verification

- **Failing-first unit tests:**
  - hit-test maps a screen point to the right cluster (and to none outside
    any cluster)
  - the click decision returns the correct branch for ripcording,
    teleport-pane, docked, own-sector and other-sector states
  - the other-sector branch produces the native buoy (pos 0,0,0,
    c_buoyCluster, clusterID) addressed to the own ship
- **Live proof** (headless client or loopback with a real session):
  - a click on a sector two warps away flies the ship through both warps
    and arrives, with zero SHIP_RESETs and zero `[ctrlak]` rejects
  - an own-sector click does nothing
  - a docked click switches the view sector
  - quote the client and server log lines
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs **on the final code**. Cite `docs/TEST_FLAKES.md`. **Report only
  numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and live proof are done, BEFORE the
  3× suite run**, then amend with the suite numbers. Confirm with
  `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P541_STARMAP_CLICK_GOTO.md`: native citations,
   the hit-test design, the goto flight-path finding (did the server-side
   path work, or what was fixed), tests and live proof.
