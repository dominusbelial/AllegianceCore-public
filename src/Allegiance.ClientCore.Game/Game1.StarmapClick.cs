using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core;
using Allegiance.Core.Sim;
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
        // P764b — the post-hit-test hover half lives in RunStarmapHover so the
        // headless tests can drive the PlayMouseOver call site itself (SIM-0330)
        // without a graphics device — not only the pure StarmapHover.Move rule.
        RunStarmapHover(inside, hit, chatCluster);
        return false;
    }

    /// <summary>P764b — the post-hit-test half of the starmap hover, split from
    /// <see cref="HandleStarmapHoverAndExpandClick"/> (which needs the mouse
    /// and the graphics viewport) so a headless test can drive the
    /// <c>UiSounds?.PlayMouseOver()</c> call at its trigger — native
    /// <c>SectorMapPane::MouseMove</c>'s
    /// <c>trekClient.PlaySoundEffect(mouseoverSound)</c>
    /// (sectormap.cpp:1440-1443) — and pin the call, not only the pure rule.
    /// Applies <see cref="Rendering.StarmapHover.Move"/> and updates the
    /// selected / mouse-over cluster state exactly as the live path does.
    /// </summary>
    private void RunStarmapHover(bool inside, short? hit, short? chatCluster)
    {
        var (selected, mouseOver, hoverCue) = Rendering.StarmapHover.Move(inside, hit, chatCluster, _selectedSectorId);
        if (_selectedSectorId != selected)
            Console.WriteLine($"[monogame] starmap hover: selected sector {selected ?? -1}");
        // Native plays mouseoverSound when the hovered cluster differs from the
        // current selection (sectormap.cpp:1440-1443); the pure rule returns the
        // cue so the headless test can pin it (SIM-0330).
        if (hoverCue)
            UiSounds?.PlayMouseOver();
        _selectedSectorId = selected;
        _starmapHoverSector = mouseOver;
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
        if (right && !_mapRightButtonWasDown)
        {
            // P764b — the Ctrl+RIGHT-click path is native's highlight mark
            // (sectormap.cpp:1491-1510, silent); P787b — SIM-0332: a PLAIN
            // right-click on a cluster is the console PickCluster path
            // (sectormap.cpp:1583-1587): mouseclickSound + PickCluster.
            if (ActionDown(Keyboard.GetState(), "SectorHighlightModifier"))
                ExecuteStarmapClick(new NumericsVec2(mouse.X, mouse.Y), mark: true);
            else
                ExecuteStarmapClick(new NumericsVec2(mouse.X, mouse.Y), consolePick: true);
        }
        _mapRightButtonWasDown = right;
        _mapLeftButtonWasDown = down;
    }

    private void ExecuteStarmapClick(NumericsVec2 cursor, bool mark = false, bool consolePick = false)
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
        if (consolePick)
            RunConsolePickCluster(cluster);
        else
            RunStarmapClick(cluster, mark);
    }

    /// <summary>
    /// P787b — the port of native's plain RIGHT-click on a starmap cluster
    /// (sectormap.cpp:1583-1587):
    /// <c>trekClient.PlaySoundEffect(mouseclickSound);
    /// pWindow-&gt;GetConsoleImage()-&gt;GetConsoleData()-&gt;PickCluster(pClusterFound,
    /// button);</c>. The console's <c>PickCluster</c> (console.cpp:1739-1791)
    /// starts a chat compose addressed to the clicked sector when not composing
    /// (<c>StartComposing(c_csComposeChat)</c> +
    /// <c>SetRecipient(CHAT_FRIENDLY_SECTOR, clusterID, pcluster)</c>), and while
    /// already composing switches to the command compose and issues a
    /// <c>c_cidGoto</c> order with a cluster buoy for the clicked sector. Split
    /// from <see cref="ExecuteStarmapClick"/> (which needs the graphics viewport)
    /// so headless tests can pin the sound call and the compose recipient
    /// (SIM-0332).
    /// </summary>
    private void RunConsolePickCluster(short cluster)
    {
        if (cluster < 0)
        {
            Console.WriteLine("[monogame] starmap right-click: empty map (no cluster) — nothing (native: no PickCluster)");
            return;
        }

        // Native sectormap.cpp:1586 — the click cue fires on the plain
        // right-click cluster hit, BEFORE the console action.
        UiSounds?.PlayMapClick();

        if (!_chatActive)
        {
            // Native console.cpp:1743-1751: start composing a chat whose
            // recipient is CHAT_FRIENDLY_SECTOR addressed to the clicked
            // cluster. The port mirrors it on its compose model: typing state
            // Chat, target FriendlySector, sector recipient = the cluster (used
            // by the send path, Game1.Session.SendChatLine).
            SetComposeState(NativeComposeState.Chat);
            _chatTarget = ChatTarget.FriendlySector;
            _chatSectorRecipient = cluster;
            Console.WriteLine($"[monogame] starmap right-click: compose chat to sector {cluster} (PickCluster, console.cpp:1743-1751)");
            return;
        }

        // Native console.cpp:1753-1791: already composing — the compose becomes
        // a command compose and PickCluster issues a c_cidGoto order with a
        // cluster buoy for the clicked sector, sent to the CURRENT compose
        // recipient. The builder special case (console.cpp:1758-1777, a builder
        // individual recipient gets a build order on the best build target in
        // the sector) needs native's FindTarget over the sector's asteroids,
        // which the port's command model does not expose; the goto leg is
        // ported (documented remainder in the P787b report).
        if (_composeState != NativeComposeState.Command)
            _composeState = NativeComposeState.Command;
        var db = Rendering.StarmapClick.BuildClusterGotoBuoy(cluster);
        short oidRecipient;
        lock (_session.Gate)
        {
            var world = _session.World;
            oidRecipient = world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)
                ? _chatTarget switch
                {
                    ChatTarget.Individual => own.ShipId,
                    ChatTarget.Wing => own.WingId,
                    ChatTarget.Team => own.SideId,
                    _ => _chatSectorRecipient ?? world.ViewedClusterId ?? own.SectorId,
                }
                : (short)0;
        }
        _session.SendChatBuoy("goto", _chatTarget, oidRecipient, db);
        Console.WriteLine($"[monogame] starmap right-click (composing): goto order for sector {cluster} via compose recipient {_chatTarget} (console.cpp:1782-1791)");
    }

    /// <summary>
    /// P764 — the post-hit-test half of the starmap click, split from
    /// <see cref="ExecuteStarmapClick"/> (which needs the graphics viewport to
    /// build the layout) so the headless tests can drive the click branches —
    /// and pin the sound calls at the trigger — without a graphics device.
    /// </summary>
    private void RunStarmapClick(short cluster, bool mark)
    {
        if (cluster < 0)
        {
            Console.WriteLine("[monogame] starmap click: empty map (no cluster) — nothing");
            return;
        }

        // P764b — SIM-0332 correction: the port's `mark` path is the
        // Ctrl+RIGHT-click highlight (native sectormap.cpp:1491-1510), which
        // plays NO sound. Native's mouseclickSound on a right-button cluster
        // hit (sectormap.cpp:1583-1587) belongs to the plain right-click
        // console PickCluster — a path this port does not implement. Only the
        // LEFT-click cluster hit cues, before the branch routing
        // (sectormap.cpp:1523).
        if (!mark)
            UiSounds?.PlayMapClick();

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
                // native AttemptTeleportTo(cluster) (sectormap.cpp:1355-1390):
                // the teleport-pane overlay is up, so the click is a teleport
                // request. P764 — the port implements the native error branches
                // (already-in-cluster / no-appropriate-station) with errorSound.
                AttemptTeleportTo(cluster);
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

    /// <summary>
    /// P764 — the port of native <c>SectorMapPane::AttemptTeleportTo</c>
    /// (WinTrek/sectormap.cpp:1355-1390): a cluster click while the teleport
    /// pane is open. The branch decision (and the native feedback lines) are
    /// pure — <see cref="Rendering.StarmapTeleport.Decide"/> — so the headless
    /// tests can pin both error branches (SIM-0328/0329) and the errorSound
    /// call this method makes for them.
    /// </summary>
    private void AttemptTeleportTo(short cluster)
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own))
                return;

            short stationId = own.StationId;
            short stationCluster = -1;
            if (stationId >= 0 && world.Stations.TryGetValue(stationId, out var docked))
                stationCluster = docked.ClusterId;

            // Native FindFriendlyStation(pcluster, GetSide()) (sectormap.cpp:872-891):
            // the first own-side station in the clicked cluster that is of a
            // RESTART-capable station type (sectormap.cpp:881:
            // pstation->GetStationType()->HasCapability(c_sabmRestart) — the
            // check TeleportPaneModel.cs already applies for the pane list).
            // A station type the client cannot resolve is not appropriate,
            // matching the pane's keep-only-known-restart-types rule.
            var stationTypes = _shopCatalog?.StationTypes;
            short friendlyStation = -1;
            foreach (var s in world.Stations.Values)
            {
                if (s.ClusterId != cluster || s.SideId != own.SideId) continue;
                if (stationTypes?.TryGetValue(s.StationTypeId, out var stype) != true
                    || !stype.HasCapability(DataStationTypeIGC.SabmRestart)) continue;
                friendlyStation = s.StationId;
                break;
            }

            var outcome = Rendering.StarmapTeleport.Decide(stationId, stationCluster, cluster, friendlyStation);
            if (outcome.PlaysError)
            {
                world.PostSystemLine(outcome.Message!);
                UiSounds?.PlayError();   // native trekClient.PlaySoundEffect(errorSound)
                Console.WriteLine($"[monogame] starmap teleport refused: {outcome.Message} (errorSound)");
                return;
            }
            if (outcome.Kind != Rendering.StarmapTeleport.OutcomeKind.DockRequest)
                return;   // native race-condition branch: not docked — nothing

            // Native sectormap.cpp:1372-1388: the DOCKED fm for the target
            // station, with the teleport lockdown line. Same wire send (and
            // latch discipline) as the teleport pane's own station branch
            // (ActivateTeleport, Game1.Panes.cs).
            // P764b — the turret branch (native sectormap.cpp:1369-1374): a
            // ship with a ParentShip disembarks first — SendBoardShip(NA)
            // before the DOCKED fm, exactly like ActivateTeleport's
            // DisembarkAndTeleport leg (Game1.Panes.cs) — native's
            // DisembarkAndTeleport(pstation).
            if (own.ParentId != -1)
            {
                _session.SendBoardShip(Rendering.TeleportPaneModel.Na);
                Console.WriteLine("[monogame] starmap teleport: leave current ship (DisembarkAndTeleport)");
            }
            _session.SendDocked(friendlyStation);
            _teleportActive = false;
            _launchSent = true;
            _teleportBoardFailure = world.BoardFailureRevision;
            Console.WriteLine($"[monogame] starmap teleport: to station {friendlyStation} -> DOCKED(stationID)");
        }
    }
}
