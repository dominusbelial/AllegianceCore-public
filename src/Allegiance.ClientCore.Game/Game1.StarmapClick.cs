using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame;

// P541 — left-click a sector on the star map to act on it, faithful to native's
// SectorMapPane::Button left-button branches (WinTrek/sectormap.cpp:1485-1571):
// hit-test the cluster under the cursor with the SAME layout maths the renderer
// uses (StarmapLayout.Fit + native's 15px click radius, sectormap.cpp:1324),
// then route through StarmapClick.Decide (the five native branches) and execute
// the chosen one. The expanded map (Tab) is a cursor-mode overlay (see the
// mouse-flight gate in Game1.FlightInput), so the cursor's position is
// meaningful here; the collapsed mini-map is clickable too when the steering
// axis (M) is off (the free-cursor default).
public sealed partial class Game1
{
    private bool _mapLeftButtonWasDown;

    /// <summary>
    /// P541 — edge-triggered left-button handler: on a fresh left-button press,
    /// hit-test the cursor against the currently-shown starmap (mini or expanded)
    /// and run native's click branches on the hit cluster. A press over no cluster,
    /// or over the ship's OWN sector, does nothing (native's "users find anything
    /// else confusing", sectormap.cpp:1546-1549).
    /// </summary>
    private void HandleStarmapClick()
    {
        var mouse = Mouse.GetState();
        bool down = mouse.LeftButton == ButtonState.Pressed;
        if (down && !_mapLeftButtonWasDown)
            ExecuteStarmapClick(new NumericsVec2(mouse.X, mouse.Y));
        _mapLeftButtonWasDown = down;
    }

    private void ExecuteStarmapClick(NumericsVec2 cursor)
    {
        // Build the layout for whichever map is up (mini or expanded) under the
        // Gate lock (reads world.Graph/world.Clusters, mutated by the receive
        // thread), exactly as DrawStarmap does, then hit-test the cursor.
        StarmapLayout? layout = null;
        lock (_session.Gate)
        {
            if (_session.World.Graph is { } g)
            {
                var sites = new List<StarmapLayout.ClusterSite>();
                foreach (var ce in g.Clusters)
                    if (_session.World.Clusters.TryGetValue(ce.Id, out var dc))
                        sites.Add(new StarmapLayout.ClusterSite(ce.Id, dc.Name, dc.ScreenX, dc.ScreenY));
                if (sites.Count > 0)
                {
                    var vp = GraphicsDevice.Viewport;
                    if (_minimapExpanded)
                    {
                        float panelW = MathF.Min(vp.Width * 0.80f, 1040f);
                        float panelH = MathF.Min(vp.Height * 0.66f, 640f);
                        float panelX = (vp.Width - panelW) / 2f;
                        float panelY = (vp.Height - panelH) / 2f;
                        layout = StarmapLayout.Fit(sites, panelX, panelY, panelW, panelH, 26f);
                    }
                    else
                    {
                        var (px, py, pw, ph) = MinimapGeometry.CornerPanel(vp.Width, vp.Height);
                        layout = StarmapLayout.Fit(sites, px, py, pw, ph, 8f);
                    }
                }
            }
        }
        if (layout is null)
            return;

        short cluster = StarmapHitTest.HitTest(layout, cursor);
        if (cluster < 0)
        {
            Console.WriteLine("[monogame] starmap click: empty map (no cluster) — nothing");
            return;
        }

        // Gather the player state native's branches read, then decide.
        bool autopilot = _autoPilotEngaged;
        bool ripcord, docked, teleportOpen;
        short currentSector = -1;
        lock (_session.Gate)
        {
            ripcord = _session.World.RipcordActive;
            teleportOpen = _teleportActive;                       // docked teleport pane flag
            docked = _session.World.MyShipId is { } me
                && _session.World.Ships.TryGetValue(me, out var own) && own.StationId != -1;
            if (_session.World.MyShipId is { } me2 && _session.World.Ships.TryGetValue(me2, out var own2))
                currentSector = own2.SectorId;
        }
        bool isCurrent = cluster == currentSector;

        switch (Rendering.StarmapClick.Decide(true, ripcord, autopilot, teleportOpen, docked, isCurrent))
        {
            case StarmapClick.Action.Ripcord:
                // native RequestRipcord(ship, cluster) — set the ripcord target to
                // this sector (sectormap.cpp:1527-1530). The port's ripcord is the
                // L-key RIP_CORD_REQUEST; selecting the clicked sector first mirrors
                // the ,/. selection the L key requires.
                _selectedSectorId = cluster;
                _session.SendRipcordRequest(cluster);
                Console.WriteLine($"[monogame] starmap click: ripcord target -> sector {cluster}");
                break;

            case StarmapClick.Action.Teleport:
                // native AttemptTeleportTo(cluster) needs the teleport pane open
                // (sectormap.cpp:1531-1536). In this port the teleport pane is part
                // of the DOCKED screen, which overlays/hides the starmap, so a real
                // in-flight teleport-pane click is unreachable (documented scope
                // boundary); the routing is still pinned by the unit test.
                Console.WriteLine("[monogame] starmap click: teleport pane — no in-flight teleport-to-station (docked teleport pane overlay hides the map); nothing");
                break;

            case StarmapClick.Action.ViewCluster:
                // native docked: RequestViewCluster(cluster) + SetViewMode(vmCommand)
                // (sectormap.cpp:1539-1545). The port has no docked 3D "view into
                // another sector" camera (it renders only the pilot's own sector),
                // so the faithful available effect is to select that sector on the
                // map (the port's "view" of a sector) — the highlight + info pane.
                _selectedSectorId = cluster;
                Console.WriteLine($"[monogame] starmap click (docked): view sector -> {cluster}");
                break;

            case StarmapClick.Action.Goto:
                IssueStarmapGoto(cluster);
                _minimapExpanded = false;   // match the G-key goto: collapse so the pilot watches the flight
                Console.WriteLine($"[monogame] starmap click: goto -> sector {cluster}");
                break;

            default:   // None — own sector (do nothing, native sectormap.cpp:1546-1549)
                if (isCurrent)
                    Console.WriteLine($"[monogame] starmap click: own sector {cluster} — nothing (native: users find anything else confusing)");
                break;
        }
    }
}
