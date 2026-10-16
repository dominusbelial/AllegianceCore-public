using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly CommandCamera _commandCamera = new();
    private readonly HashSet<string> _commandHeld = new();
    private bool _commandCentered;
    private long _commandViewVersion;
    private bool CommandCameraActive => _cameraMode is CameraMode.Command or CameraMode.DroneFollow or CameraMode.DroneCockpit;

    // WinTrek.cpp:9894-9953. Use the live action map for edges AND held controls.
    private void HandleCommandKeys(KeyboardState keys, bool composing, float dt)
    {
        foreach (var action in new[] { "ViewCommand", "ToggleCommand", "TrackCommandView", "DroneFollow", "DroneCockpit" })
        {
            bool down = ActionDown(keys, action);
            bool fresh = down && !_commandHeld.Contains(action);
            if (down) _commandHeld.Add(action); else _commandHeld.Remove(action);
            if (!fresh || composing || _state != ClientState.Flying) continue;
            if (action == "ToggleCommand")
            {
                _commandCamera.TopDown = !_commandCamera.TopDown;
                _commandCentered = false;
            }
            else if (action == "TrackCommandView")
            {
                _commandCamera.Tracking = !_commandCamera.Tracking;
                lock (_session.Gate) _session.World.PostSystemLine(_commandCamera.Tracking
                    ? "Command view tracking enabled" : "Command view tracking disabled");
            }
            else
            {
                var mode = action == "ViewCommand" ? CameraMode.Command
                    : action == "DroneFollow" ? CameraMode.DroneFollow : CameraMode.DroneCockpit;
                if (mode != CameraMode.Command)
                {
                    if (_commandee is null) SelectCommandee(false);
                    if (!TryCommandedPose(out _, out _, out _)) continue;
                }
                ApplyConsoleMode("ViewCommand");
                _cameraMode = CommandCameraActive && mode == CameraMode.Command || _cameraMode == mode ? CameraMode.Cockpit : mode;
                _directorActive = false;
                _freeCamActive = _orbitCamActive = false;
                _minimapExpanded = false;
                _commandCentered = false;
                IsMouseVisible = CommandCameraActive || !_mouseFlightEnabled;
                if (_cameraMode == CameraMode.Command && _dockedActive)
                {
                    lock (_session.Gate)
                        if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var ship))
                            _session.SendViewClusterRequest(ship.SectorId);
                }
            }
        }
        if (_cameraMode != CameraMode.Command || composing || _ptrLeftDown) return;
        float Axis(string positive, string negative) => (ActionDown(keys, positive) ? 1 : 0) - (ActionDown(keys, negative) ? 1 : 0);
        float right = Axis("YawRight", "YawLeft") + .5f * Axis("ThrustRight", "ThrustLeft");
        float downAxis = Axis("PitchDown", "PitchUp") + .5f * Axis("ThrustDown", "ThrustUp");
        var mouse = Mouse.GetState();
        var vp = GraphicsDevice.Viewport;
        // Native 8px gutter / 12px divisor; suppress pan while selecting (8469).
        if (IsActive && mouse.LeftButton != ButtonState.Pressed && mouse.RightButton != ButtonState.Pressed
            && mouse.X >= 0 && mouse.Y >= 0 && mouse.X < vp.Width && mouse.Y < vp.Height && !_minimapExpanded)
        {
            right += CommandEdgePan(mouse.X, vp.Width);
            downAxis += CommandEdgePan(mouse.Y, vp.Height);
        }
        _commandCamera.Step(dt, right, downAxis, Axis("ZoomOut", "ZoomIn"), Axis("RollRight", "RollLeft"));
    }
    internal static float CommandEdgePan(int cursor, int extent) => cursor < 8 ? (cursor - 8) / 12f
        : extent - cursor < 8 ? (8 - (extent - cursor)) / 12f : 0;

    private void UpdateCommandFraming(Vector3 ownPosition)
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            if (!_commandCentered)
            {
                Vector3 center = ownPosition;
                float radius = _myRenderRadius;
                if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)
                    && own.StationId >= 0 && world.Stations.TryGetValue(own.StationId, out var station))
                { center = new Vector3(station.Position[0], station.Position[1], station.Position[2]); radius = ResolveStationRadius(station); }
                _commandCamera.Center(center, radius);
                _commandCentered = true;
            }
            if (world.ViewClusterVersion != _commandViewVersion && _dockedActive)
            {
                _commandViewVersion = world.ViewClusterVersion;
                _commandCamera.Center(world.ViewedClusterPosition is { } position ? PoseConverter.ToXna(position) : Vector3.Zero,
                    0, explicitPosition: true);
            }
            if (!_ptrLeftDown && _commandCamera.Tracking && _autoTarget is { } target)
            {
                short sector = RenderSector(CurrentOwnSector() ?? -1);
                if (target.Kind == TargetKind.Ship)
                {
                    var tracked = CommandCamera.FollowSubject(world, target.Id, sector, sameSideOnly: false);
                    if (tracked?.Latest is { } pose)
                        _commandCamera.Follow(_poses.TryGetValue(tracked.ShipId, out var rendered)
                            ? PoseConverter.ToXna(rendered.Position) : PoseConverter.ToXna(pose.Position), HullLength(tracked.HullId) / 2);
                }
                else
                {
                    short targetSector = target.Kind switch
                    {
                        TargetKind.Station when world.Stations.TryGetValue(target.Id, out var stationTarget) => stationTarget.ClusterId,
                        TargetKind.Asteroid when world.Asteroids.TryGetValue(target.Id, out var asteroid) => asteroid.ClusterId,
                        TargetKind.Warp when world.Warps.TryGetValue(target.Id, out var warp) => warp.ClusterId,
                        TargetKind.Probe when world.Probes.TryGetValue(target.Id, out var probe) => probe.ClusterId,
                        TargetKind.Treasure when world.Treasures.TryGetValue(target.Id, out var treasure) => treasure.ClusterId,
                        _ => -1,
                    };
                    if (targetSector == sector && ResolveTargetPosition(target, out var position))
                        _commandCamera.Follow(PoseConverter.ToXna(position), TargetRadiusHint());
                }
            }
        }
    }

    private bool TryCommandedPose(out Vector3 position, out Matrix rotation, out float radius)
    {
        position = default; rotation = Matrix.Identity; radius = 10;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (_commandee is not { } target || world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return false;
            var ship = CommandCamera.FollowSubject(world, target.Id, RenderSector(own.SectorId));
            if (ship?.Latest is not { } update) return false;
            position = _poses.TryGetValue(ship.ShipId, out var rendered) ? PoseConverter.ToXna(rendered.Position) : PoseConverter.ToXna(update.Position);
            rotation = PoseConverter.ToXnaRotation(_poses.TryGetValue(ship.ShipId, out var posed) ? posed.Orientation : update.Orientation);
            if (_shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == ship.HullId) is { } hull) radius = hull.Length / 2;
            return true;
        }
    }

    // Only a server-confirmed docked sector preview changes the drawn cluster.
    private short RenderSector(short ownSector) => _cameraMode == CameraMode.Command && _dockedActive
        ? _session.World.ViewedClusterId ?? ownSector : ownSector;
    private bool ShipInDrawnSector(short id, short me)
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            if (!world.Ships.TryGetValue(me, out var own) || !world.Ships.TryGetValue(id, out var ship)) return false;
            if (_cameraMode == CameraMode.DroneCockpit && _commandee is { } target
                && CommandCamera.FollowSubject(world, target.Id, RenderSector(own.SectorId))?.ShipId == id) return false;
            return ship.ParentId < 0 && ship.SectorId == RenderSector(own.SectorId) && !ship.Unknown;
        }
    }

    private bool PointerOverStarmap(MouseState mouse)
    {
        if (_minimapExpanded) return true;
        if (_directorActive) return false;
        var vp = GraphicsDevice.Viewport;
        var (x, y, w, h) = MinimapGeometry.CornerPanel(vp.Width, vp.Height);
        return mouse.X >= x && mouse.X <= x + w && mouse.Y >= y && mouse.Y <= y + h;
    }

    private void AnchorCommandCandidates(List<CandidateObject> candidates)
    {
        lock (_session.Gate)
        {
            for (int i = candidates.Count - 1; i >= 0; i--)
                if (candidates[i].Kind == TargetKind.Ship)
                {
                    if (_session.World.Ships.TryGetValue(candidates[i].Id, out var ship) && (ship.Unknown || ship.ParentId >= 0))
                        candidates.RemoveAt(i);
                    else if (_poses.TryGetValue(candidates[i].Id, out var pose))
                        candidates[i] = candidates[i] with { Position = pose.Position };
                }
        }
    }

    private readonly List<AutopilotTarget> _commandGroup = new();

    // Native console.cpp:2081-2178: select visible friendly ships in a drag
    // rectangle; Shift-click toggles individual recipients. The port uses its
    // existing screen-pick radius for overlap, and its existing order path per ship.
    internal static List<AutopilotTarget> ResolveCommandRectangle(IReadOnlyList<CandidateObject> candidates,
        Func<Allegiance.Core.Collision.Vector3, (float Sx, float Sy)?> project, Vector2 start, Vector2 stop,
        short? side, short self)
    {
        var selected = new List<AutopilotTarget>();
        float minX = MathF.Min(start.X, stop.X) - PointerPickRadius, maxX = MathF.Max(start.X, stop.X) + PointerPickRadius;
        float minY = MathF.Min(start.Y, stop.Y) - PointerPickRadius, maxY = MathF.Max(start.Y, stop.Y) + PointerPickRadius;
        foreach (var ship in candidates)
            if (ship.Kind == TargetKind.Ship && ship.SideId == side && ship.Id != self
                && project(ship.Position) is { } screen && screen.Sx >= minX && screen.Sx <= maxX && screen.Sy >= minY && screen.Sy <= maxY)
                selected.Add(new AutopilotTarget(ship.Kind, ship.Id, ship.Name));
        return selected;
    }

    private void SelectCommandRectangle(short me, Allegiance.ClientCore.State.ShipEntity own,
        Allegiance.Core.Collision.Vector3 position, Allegiance.Core.Sim.Orientation orientation, MouseState mouse)
    {
        if (!TryPointerProjection(position, orientation, out var project)) return;
        List<CandidateObject> candidates;
        lock (_session.Gate)
            candidates = GatherSectorCandidates(_session.World.Ships, _session.World.Warps, _session.World.Stations,
                _session.World.Asteroids, me, RenderSector(own.SectorId), ShipRoleBits, StationRoleBits);
        AnchorCommandCandidates(candidates);
        var vp = GraphicsDevice.Viewport;
        var start = new Vector2(_ptrDragStart.X / vp.Width * 2 - 1, 1 - _ptrDragStart.Y / vp.Height * 2);
        var end = new Vector2(mouse.X / (float)vp.Width * 2 - 1, 1 - mouse.Y / (float)vp.Height * 2);
        _commandGroup.Clear();
        _commandGroup.AddRange(ResolveCommandRectangle(candidates, project, start, end, _session.World.MySideId, me));
        _commandee = _commandGroup.Count > 0 ? _commandGroup[0] : null;
    }

    private void TryIssueOrderToCommandee()
    {
        if (_cameraMode != CameraMode.Command || _commandGroup.Count == 0)
        { TryIssueOrderToSingleCommandee(); return; }
        var primary = _commandee;
        try
        {
            foreach (var ship in _commandGroup)
            {
                _commandee = ship;
                TryIssueOrderToSingleCommandee();
            }
        }
        finally { _commandee = primary; }
    }

    private CommandGridRenderer? _commandGridRenderer;
    private void DrawCommandGrid(Matrix view, Matrix projection, Vector3 eye)
    {
        if (_cameraMode != CameraMode.Command || !_commandGridVisible) return;
        var objects = new List<Vector3>();
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return;
            short sector = RenderSector(own.SectorId);
            foreach (var (id, pose) in _poses)
                if (ShipInDrawnSector(id, me)) objects.Add(PoseConverter.ToXna(pose.Position));
            foreach (var station in world.Stations.Values)
                if (station.ClusterId == sector) objects.Add(new Vector3(station.Position[0], station.Position[1], station.Position[2]));
            foreach (var asteroid in world.Asteroids.Values)
                if (asteroid.ClusterId == sector) objects.Add(new Vector3(asteroid.Position[0], asteroid.Position[1], asteroid.Position[2]));
            foreach (var warp in world.Warps.Values)
                if (warp.ClusterId == sector) objects.Add(new Vector3(warp.Position[0], warp.Position[1], warp.Position[2]));
        }
        _commandGridRenderer ??= new CommandGridRenderer(GraphicsDevice);
        _commandGridRenderer.Draw(objects, view, projection, eye);
    }

    private void DrawCommandHud()
    {
        if (_hudHidden || _directorActive) return;
        var lines = new List<string>();
        lock (_session.Gate)
        {
            var world = _session.World;
            if (CommandCameraActive)
            {
                string camera = _cameraMode == CameraMode.Command ? (_commandCamera.TopDown ? "COMMAND: TOP DOWN" : "COMMAND: THREE QUARTER")
                    : $"COMMAND: {_cameraMode} - {_commandee?.Name}";
                lines.Add(camera);
                if (_cameraMode == CameraMode.Command) lines.Add($"RECIPIENTS: {Math.Max(_commandGroup.Count, _commandee is null ? 0 : 1)} | LEFT SELECT / DRAG | RIGHT ORDER");
            }
            foreach (short id in world.HighlightedClusters.OrderBy(id => id))
                if (world.Clusters.TryGetValue(id, out var sector)) lines.Add($"COMMANDER MARK: {sector.Name}");
        }
        if (lines.Count == 0) return;
        _spriteBatch.Begin();
        try
        {
            if (_cameraMode == CameraMode.Command)
            {
                if (_camSmoothInit && ProjectToScreen(new Vector3(0, 3 * CommandGridRenderer.Radius / 4, 0),
                    _camDisplay.RelativeView(), _camDisplay.Projection(GraphicsDevice.Viewport.AspectRatio, 10, 80000), _camDisplay.Eye, out var north))
                    _spriteBatch.DrawString(_hudSmall, "NORTH", north, Color.White);
                var mouse = Mouse.GetState();
                if (_ptrLeftDown)
                {
                    int x = (int)MathF.Min(_ptrDragStart.X, mouse.X), y0 = (int)MathF.Min(_ptrDragStart.Y, mouse.Y);
                    int w = (int)MathF.Abs(_ptrDragStart.X - mouse.X), h = (int)MathF.Abs(_ptrDragStart.Y - mouse.Y);
                    _spriteBatch.Draw(_whitePixel, new Rectangle(x, y0, w, 1), Color.White);
                    _spriteBatch.Draw(_whitePixel, new Rectangle(x, y0 + h, w, 1), Color.White);
                    _spriteBatch.Draw(_whitePixel, new Rectangle(x, y0, 1, h), Color.White);
                    _spriteBatch.Draw(_whitePixel, new Rectangle(x + w, y0, 1, h), Color.White);
                }
            }
            float y = 60;
            foreach (var line in lines)
            {
                var size = _hudSmall.MeasureString(line);
                _spriteBatch.DrawString(_hudSmall, line, new Vector2((GraphicsDevice.Viewport.Width - size.X) / 2, y), Color.Cyan);
                y += size.Y + 4;
            }
        }
        finally { _spriteBatch.End(); }
    }

}
