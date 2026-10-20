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
    private bool _mapRightButtonWasDown;

    // P752 — native's starmap hover state (sectormap.cpp:1413-1461): the
    // cluster under the pointer while the expanded map is up (null when the
    // pointer is over no cluster or off the map), and the info pane's
    // expand-button toggle (native m_pimageBkgnd starts LARGE, sectormap.cpp:166).
    private short? _starmapHoverSector;
    private bool _sectorInfoExpanded = true;
    private Microsoft.Xna.Framework.Rectangle? _sectorInfoPaneBox;

    /// <summary>
    /// P752 — per-frame hover + expand-button handling for the EXPANDED
    /// starmap, the port of native <c>SectorMapPane::MouseMove</c>
    /// (sectormap.cpp:1413-1461) and the info pane's expand button
    /// (<c>SectorInfoPane::Button</c>, sectormap.cpp:203-238). Hovering a
    /// cluster selects it (the info pane follows the pointer) and marks it as
    /// the mouse-over cluster whose warning text the pane draws; hovering empty
    /// map space reverts the selection to the CHAT cluster (native
    /// <c>GetChatCluster()</c>, sectormap.cpp:1446); the pointer leaving the map
    /// entirely changes nothing. A left-button RELEASE inside the info pane's
    /// expand button toggles the small/large view BEFORE the map's own click
    /// routing runs (native <c>SectorInfoPane::Button</c> fires on
    /// <c>button==0 &amp;&amp; !bDown</c>, sectormap.cpp:203-238, and
    /// <c>HitTest</c> checks the button first, sectormap.cpp:186-201) — returns
    /// true when the click was consumed.
    /// </summary>
    private bool HandleStarmapHoverAndExpandClick()
    {
        if (!_minimapExpanded)
        {
            _starmapHoverSector = null;
            return false;
        }

        var mouse = Mouse.GetState();
        var cursor = new NumericsVec2(mouse.X, mouse.Y);
        var vp = GraphicsDevice.Viewport;
        float panelW = MathF.Min(vp.Width * 0.80f, 1040f);
        float panelH = MathF.Min(vp.Height * 0.66f, 640f);
        float panelX = (vp.Width - panelW) / 2f;
        float panelY = (vp.Height - panelH) / 2f;

        // Native toggles on RELEASE (!bDown, sectormap.cpp:205), not on press.
        bool releaseEdge = mouse.LeftButton == ButtonState.Released && _mapLeftButtonWasDown;

        // The expand button is checked first (native HitTest order).
        if (releaseEdge && _sectorInfoPaneBox is { } box)
        {
            var btn = Rendering.SectorInfoPaneRenderer.ExpandButtonRect(box.X, box.Y, box.Width, box.Height);
            if (btn.Contains(mouse.X, mouse.Y))
            {
                _sectorInfoExpanded = !_sectorInfoExpanded;
                Console.WriteLine($"[monogame] sector info pane {(_sectorInfoExpanded ? "EXPANDED" : "collapsed")} (native small/large toggle, sectormap.cpp:203-238)");
                return true;   // consumed — the map must not also act on this click
            }
        }

        // Hover: only while the pointer is inside the map panel.
        bool inside = cursor.X >= panelX && cursor.X <= panelX + panelW
            && cursor.Y >= panelY && cursor.Y <= panelY + panelH;
        short? hit = null;
        short? chatCluster = null;
        if (inside)
        {
            StarmapLayout? layout = BuildExpandedStarmapLayout(panelX, panelY, panelW, panelH);
            if (layout is not null)
                hit = StarmapHitTest.HitTest(layout, cursor);
            lock (_session.Gate)
            {
                // Native GetChatCluster() (clintlib.h:998-1009): the viewed
                // cluster when set, else the ship's own cluster (status sector).
                if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var own))
                    chatCluster = _session.World.ViewedClusterId ?? own.SectorId;
            }
        }
        var (selected, mouseOver) = Rendering.StarmapHover.Move(inside, hit, chatCluster, _selectedSectorId);
        if (_selectedSectorId != selected)
            Console.WriteLine($"[monogame] starmap hover: selected sector {selected ?? -1}");
        // Native plays mouseoverSound when the hovered cluster differs from the
        // current selection (sectormap.cpp:1440-1443).
        if (mouseOver is { } hovered && hovered != _selectedSectorId)
            _sound?.PlayMouseOver();
        _selectedSectorId = selected;
        _starmapHoverSector = mouseOver;
        return false;
    }

    /// <summary>Build the expanded starmap layout with the same maths
    /// <c>DrawStarmap</c>/<c>ExecuteStarmapClick</c> use, so hover hit-testing
    /// always agrees with what is drawn.</summary>
    private StarmapLayout? BuildExpandedStarmapLayout(float panelX, float panelY, float panelW, float panelH)
    {
        lock (_session.Gate)
        {
            if (_session.World.Graph is not { } g) return null;
            var sites = new List<StarmapLayout.ClusterSite>();
            foreach (var ce in g.Clusters)
                if (_session.World.Clusters.TryGetValue(ce.Id, out var dc))
                    sites.Add(new StarmapLayout.ClusterSite(ce.Id, dc.Name, dc.ScreenX, dc.ScreenY));
            return sites.Count > 0 ? StarmapLayout.Fit(sites, panelX, panelY, panelW, panelH, 26f) : null;
        }
    }

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
        bool right = mouse.RightButton == ButtonState.Pressed;
        if (right && !_mapRightButtonWasDown && ActionDown(Keyboard.GetState(), "SectorHighlightModifier"))
            ExecuteStarmapClick(new NumericsVec2(mouse.X, mouse.Y), mark: true);
        _mapRightButtonWasDown = right;
        _mapLeftButtonWasDown = down;
    }

    private void ExecuteStarmapClick(NumericsVec2 cursor, bool mark = false)
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
                        short? current = _session.World.MyShipId is { } me
                            && _session.World.Ships.TryGetValue(me, out var own) ? own.SectorId : null;
                        // P715: clicks must follow the same footer/name reservations
                        // as DrawMini, including a two-line caption for long names.
                        layout = MinimapLayout.Build(sites, g.Warps, current, vp.Width, vp.Height, text =>
                        {
                            var size = _hudSmall.MeasureString(text);
                            return new NumericsVec2(size.X, size.Y);
                        }).Map;
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

        if (!mark)
        {
            // P755 — native plays mouseclickSound for a left click that lands on
            // a cluster, before the action branches (sectormap.cpp:1523).
            _sound?.PlayMapClick();
        }

        if (mark)
        {
            // Native sectormap.cpp:1491-1510: leader broadcasts toggle, other
            // teammates can dismiss their local mark. Echo applies the leader's mark.
            lock (_session.Gate)
            {
                var world = _session.World;
                if (world.IsTeamLeader) _session.SendHighlightCluster(cluster, !world.HighlightedClusters.Contains(cluster));
                else world.Apply(new Allegiance.Core.Wire.HighlightCluster { ClusterId = cluster, Highlight = false });
            }
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
                // (sectormap.cpp:1539-1545), using the server-confirmed sector stream.
                _selectedSectorId = cluster;
                _session.SendViewClusterRequest(cluster);
                _cameraMode = CameraMode.Command;
                _minimapExpanded = false;
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
