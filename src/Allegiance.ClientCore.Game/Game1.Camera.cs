using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The camera slice of <see cref="Game1"/> (P384 stage-2 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    /// <summary>True once the local ship has a usable real position/orientation
    /// (assigned a ship id AND received at least one update). P343 — also
    /// outputs the ship's real world <paramref name="velocity"/>
    /// (<see cref="ServerShipUpdate.Velocity"/>, already carried by the same
    /// update object) so the player autopilot can brake within stopping
    /// distance.</summary>
    private bool TryOwnPose(out SimVec position, out Orientation orientation, out SimVec velocity)
    {
        position = default;
        orientation = Orientation.Identity;
        velocity = default;
        lock (_session.Gate)
        {
            if (RenderShipId(_session.World) is not { } me) return false;
            if (!_session.World.Ships.TryGetValue(me, out var mine) || mine.Latest is not { } up) return false;
            // P508 — the cameras (chase/cockpit/external), targeting and autopilot all
            // read the own pose through this single seam: the local SIMULATION pose (the
            // client flies its own ship; a control change moves the camera on the same
            // frame, no round trip). Raw server pose otherwise (the --noPrediction debug
            // toggle, or no hull resolved yet).
            if (!LocalSpectating && _ownShipPrediction && _ownSim.HaveHull)
            {
                position = _ownSim.Position;
                orientation = _ownSim.Orientation;
                velocity = _ownSim.Velocity;
            }
            else
            {
                position = up.Position;
                orientation = up.Orientation;
                velocity = up.Velocity;
            }
            return true;
        }
    }

    /// <summary>
    /// P230 — project the server-authoritative lock target (from
    /// <see cref="ClientCore.State.ClientWorld.MissileLock"/>, the owner-only
    /// <c>MISSILE_LOCK_UPDATE(204)</c>) into screen space for the target-box overlay,
    /// or return null when there is no lock target or it lies outside the view
    /// frustum. Same camera-relative projection as <see cref="CollectTargetCandidates"/>
    /// (subtract <paramref name="cameraEye"/>, transform through <c>view*proj</c>, reject
    /// behind-camera and out-of-frustum), then NDC → viewport px.
    /// </summary>
    private LockBracketView? ComputeLockBracket(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        var world = _session.World;
        MissileLockState lockState;
        Vector3 xnaTarget;
        float? lockWorldRadius = null;
        lock (_session.Gate)
        {
            lockState = world.MissileLock;
            if (!lockState.HasTarget) return null;
            // P523 — anchor the lock box to the pose the renderer drew THIS frame
            // for the lock target (_poses), not the server's raw 20 Hz lock-update
            // position, so the box stays glued to the smoothly-drawn hull instead
            // of jittering. Falls back to the wire position only when the target
            // ship was not rendered this frame.
            if (lockState.TargetType == IgcObjectType.Ship && ResolveShipAnchor(_poses, world.Ships, lockState.TargetId, out var lockSimPos))
                xnaTarget = PoseConverter.ToXna(lockSimPos);
            else
                xnaTarget = PoseConverter.ToXna(lockState.TargetPosition);
            // HUD-4 — the lock target's real world radius for the native
            // projected-radius lock flower (radarimage.cpp:296-332).
            lockWorldRadius = lockState.TargetType switch
            {
                IgcObjectType.Ship when world.Ships.TryGetValue(lockState.TargetId, out var lockShip) && HullLength(lockShip.HullId) > 0f => HullLength(lockShip.HullId) * 0.5f,
                IgcObjectType.Station when world.Stations.TryGetValue(lockState.TargetId, out var lockStation) && _shopCatalog?.StationTypes is { } lockStationTypes && lockStationTypes.TryGetValue(lockStation.StationTypeId, out var lockStationType) => lockStationType.Radius,
                IgcObjectType.Asteroid when world.Asteroids.TryGetValue(lockState.TargetId, out var lockAsteroid) && lockAsteroid.Radius > 0 => lockAsteroid.Radius,
                IgcObjectType.Warp when world.Warps.TryGetValue(lockState.TargetId, out var lockWarp) && lockWarp.Radius > 0 => lockWarp.Radius,
                _ => null
            };
        }

        var viewProj = view * proj;
        var rel = xnaTarget - cameraEye;
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        if (clip.W <= 1e-6f) return null;                                   // behind the camera
        float sx = clip.X / clip.W;
        float sy = clip.Y / clip.W;
        if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return null;          // outside the FOV frustum

        var vp = GraphicsDevice.Viewport;
        var screen = new Vector2((sx * 0.5f + 0.5f) * vp.Width, (0.5f - sy * 0.5f) * vp.Height);
        float? lockScreenRadius = lockWorldRadius is { } lr
            ? ProjectedRadius(xnaTarget, lr, proj, cameraEye)
            : null;
        return new LockBracketView(screen, MissileLockFeedback.Classify(lockState.LockFraction), _nowSeconds, lockState.LockFraction, lockScreenRadius);
    }

    /// <summary>
    /// P345 — project the player's OWN <c>T</c>/<c>G</c> autopilot target
    /// (<see cref="_autoTarget"/>, a purely client-side selection) into screen space
    /// for the selection-bracket overlay, or return null when no target is selected
    /// or it lies outside the view frustum. Mirrors <see cref="ComputeLockBracket"/>'s
    /// camera-relative math exactly (subtract <paramref name="cameraEye"/>, transform
    /// through <c>view*proj</c>, reject behind-camera and out-of-frustum, NDC →
    /// viewport px) via the shared <see cref="ProjectToScreen"/>, reusing
    /// <see cref="ResolveTargetPosition"/> to resolve the target's live world position
    /// (all four kinds: Ship/Station/Asteroid/Warp). Kept a SEPARATE method from
    /// <see cref="ComputeLockBracket"/> on purpose — the two concepts are simultaneously
    /// active and visually distinct (a server missile-lock vs the player's own target)
    /// and must not be conflated.
    /// </summary>
    private TargetSelectionView? ComputeTargetSelectionBracket(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        // Native selpane.cpp:81-120 binds a selected model; consoledata.cpp
        // supplies its name, damageable health and asteroid-only ore. Snapshot
        // that data under the gate, then project outside the lock.
        AutopilotTarget target;
        bool engaged;
        string? charName = null;
        float? hull = null, shield = null;
        bool? isEnemy = null;
        float? ore = null;
        HudColor? sideColor = null;
        float? distance = null, closing = null;
        float? targetRadius = null;
        lock (_session.Gate)
        {
            if (_autoTarget is not { } t) return null;
            target = t;
            engaged = _autoPilotEngaged;
            var kind = target.Kind switch
            {
                TargetKind.Ship => IgcObjectType.Ship,
                TargetKind.Station => IgcObjectType.Station,
                TargetKind.Asteroid => IgcObjectType.Asteroid,
                _ => IgcObjectType.Warp,
            };
            if (SelectionPaneModel.Build(_session.World, kind, target.Id) is not { } info) return null;
            charName = info.Name;
            hull = info.Hull;
            shield = info.Shield;
            isEnemy = info.Enemy;
            ore = info.Ore;
            var world = _session.World;
            if (world.MyShipId is { } mine && world.Ships.TryGetValue(mine, out var own) && own.Latest is { } ownUpdate
                && (target.Kind != TargetKind.Ship || world.Ships.TryGetValue(target.Id, out var rangedShip) && rangedShip.SectorId == own.SectorId)
                && (target.Kind != TargetKind.Station || world.Stations.TryGetValue(target.Id, out var rangedStation) && rangedStation.ClusterId == own.SectorId)
                && ResolveTargetPosition(target, out var targetPosition))
            {
                var delta = targetPosition - ownUpdate.Position;
                distance = delta.Length();
                var targetVelocity = target.Kind == TargetKind.Ship && world.Ships.TryGetValue(target.Id, out var other) && other.Latest is { } otherUpdate
                    ? otherUpdate.Velocity : default;
                if (distance > .001f) closing = ((ownUpdate.Velocity - targetVelocity) * delta) / distance.Value;
            }
            short? side = target.Kind == TargetKind.Ship && world.Ships.TryGetValue(target.Id, out var selectedShip) ? selectedShip.SideId
                : target.Kind == TargetKind.Station && world.Stations.TryGetValue(target.Id, out var selectedStation) ? selectedStation.SideId : null;
            if (side is { } sideId && world.TryGetSideColor(sideId, out float sr, out float sg, out float sb))
                sideColor = new(sr, sg, sb);
            // HUD-4 — the target's real world radius, for native's
            // projected-radius bracket (radarimage.cpp:296-311: radiusObject
            // = pts->GetScreenRadius(), capped at sqrt2*80).
            targetRadius = target.Kind switch
            {
                TargetKind.Ship when world.Ships.TryGetValue(target.Id, out var radiusShip) && HullLength(radiusShip.HullId) > 0f => HullLength(radiusShip.HullId) * 0.5f,
                TargetKind.Station when world.Stations.TryGetValue(target.Id, out var radiusStation) && _shopCatalog?.StationTypes is { } stationTypes && stationTypes.TryGetValue(radiusStation.StationTypeId, out var stationType) => stationType.Radius,
                TargetKind.Asteroid when world.Asteroids.TryGetValue(target.Id, out var radiusAsteroid) && radiusAsteroid.Radius > 0 => radiusAsteroid.Radius,
                TargetKind.Warp when world.Warps.TryGetValue(target.Id, out var radiusWarp) && radiusWarp.Radius > 0 => radiusWarp.Radius,
                _ => null
            };
        }

        // Resolve the target's live world position (false when it has died/vanished),
        // then reject it when it does not project into this frame's view.
        if (!ResolveTargetPosition(target, out var simPos)) return null;
        if (!ProjectToScreen(PoseConverter.ToXna(simPos), view, proj, cameraEye, out Vector2 screen)) return null;
        float? screenRadius = targetRadius is { } worldRadius
            ? ProjectedRadius(PoseConverter.ToXna(simPos), worldRadius, proj, cameraEye)
            : null;

        return new TargetSelectionView(screen, engaged, charName, hull, shield, isEnemy, ore, distance, closing, sideColor, screenRadius);
    }

    /// <summary>HUD-4 — project a world-space radius to screen pixels at the
    /// object's depth (vertical-FOV scale from the projection matrix), then cap
    /// it at native's sqrt2×80 px (radarimage.cpp:302-306). HUD-13 — the math
    /// itself lives in <see cref="RadarScreenRadius.Project"/> so the radar
    /// contacts' <c>GetScreenRadius()</c> (radarimage.cpp:307-319) and these
    /// lock/selection brackets are guaranteed to use the SAME projection;
    /// that helper also applies native's <c>CmdViewMaxIconScreenSize</c> 7 px
    /// floor (WinTrek/WinTrek.cpp:7931-7941), which changes nothing here
    /// because every caller already floors its bracket at 8 px.</summary>
    private float? ProjectedRadius(Vector3 worldCenter, float worldRadius, Matrix proj, Vector3 cameraEye)
    {
        float distance = (worldCenter - cameraEye).Length();
        if (distance <= 0.001f || worldRadius <= 0f || MathF.Abs(proj.M22) < 1e-6f) return null;
        return RadarScreenRadius.Project(
            worldRadius, distance, GraphicsDevice.Viewport.Height, proj.M22);
    }

    /// <summary>
    /// HUD-3 — resolve the native bottom target-pane snapshot from the player's
    /// own T/G selection, independent of whether the target projects into the
    /// current view. Values follow <c>WinTrek/consoledata.cpp:40-199</c>:
    /// visible type (or "unknown" — the port has no native last-seen hull/sector
    /// wire fields), side name + actual side colour, damageable hull/shield
    /// fractions (0 when not visible/live), same-cluster range (-1 → the
    /// sector-name fallback the port leaves unavailable), target speed (0 static,
    /// -1 unseen dynamic), asteroid-only ore, and the lead predictor's in-range
    /// flag (<c>WinTrek/indicator.cpp:405</c>).
    /// </summary>
    private HudTargetPane? ComputeTargetPane()
    {
        lock (_session.Gate)
        {
            if (_autoTarget is not { } t) return null;
            var kind = t.Kind switch
            {
                TargetKind.Ship => IgcObjectType.Ship,
                TargetKind.Station => IgcObjectType.Station,
                TargetKind.Asteroid => IgcObjectType.Asteroid,
                TargetKind.Warp => IgcObjectType.Warp,
                TargetKind.Probe => IgcObjectType.Probe,
                TargetKind.Cluster => IgcObjectType.Cluster,
                _ => IgcObjectType.Treasure,
            };
            return TargetPaneModel.Build(_session.World, kind, t.Id, t.Name, _hullById, _shopCatalog?.StationTypes);
        }
    }

    /// <summary>
    /// P359 — the screen-space unit DIRECTION from the camera toward the selected
    /// autopilot target, regardless of whether that target projects into the view
    /// frustum (the bearing arrow's whole point is telling the pilot which way to
    /// turn when the target is OFF-SCREEN). Works for every targetable object kind
    /// (ship/station/asteroid/warp) since it is pure directional math with no
    /// pilot-specific data involved. Returns the target's relative bearing as a
    /// normalized 2-D vector in viewport-y-down screen space (right = +X, up = -Y),
    /// or null when no target is selected / it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Direction math: the world-space offset (target − camera eye) is treated as a
    /// pure DIRECTION (w=0) and transformed through <c>view*proj</c> — the resulting
    /// homogeneous clip point maps to where that direction pierces the infinite view
    /// plane, so the divided <c>(x, -y)</c> IS the correct perspective azimuth/
    /// elevation screen offset, and the sign of <c>clip.W</c> folds the "behind the
    /// camera" case into the mirrored direction (turn-around) with no separate branch.
    /// This mirrors the per-point <see cref="ProjectToScreen"/> but deliberately does
    /// NOT reject out-of-frustum / behind-camera results.
    /// </remarks>
    private Vector2? ComputeTargetBearing(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        AutopilotTarget target;
        lock (_session.Gate)
        {
            if (_autoTarget is not { } t) return null;
            target = t;
        }

        if (!ResolveTargetPosition(target, out var simPos)) return null;

        var rel = PoseConverter.ToXna(simPos) - cameraEye;
        var viewProj = view * proj;
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 0f), viewProj);

        Vector2 dir;
        if (MathF.Abs(clip.W) > 1e-6f)
            dir = new Vector2(clip.X / clip.W, -clip.Y / clip.W);
        else
            dir = new Vector2(0f, 1f);   // degenerate (dead-ahead / dead-behind): fall back upward

        if (dir.LengthSquared() < 1e-6f)
            dir = new Vector2(0f, 1f);
        dir.Normalize();
        return dir;
    }

    /// <summary>
    /// P266 — project the server-authoritative predictive lead (from
    /// <see cref="ClientCore.State.ClientWorld.LeadIndicator"/>, the owner-only
    /// <c>LEAD_INDICATOR_UPDATE(210)</c>) into screen space for the lead reticle
    /// overlay, or return null when there is no granted lead or the point is out
    /// of view. Uses the same camera-relative projection as
    /// <see cref="ComputeLockBracket"/> (subtract <paramref name="cameraEye"/>,
    /// transform through <c>view*proj</c>, reject behind-camera and out-of-frustum,
    /// then NDC → viewport px) for BOTH the lead point and the target's current
    /// centre — the two together expose the real predictive offset on screen.
    /// </summary>
    private LeadIndicatorView? ComputeLeadIndicator(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        LeadIndicatorState state;
        lock (_session.Gate)
        {
            state = _session.World.LeadIndicator;
            if (!state.HasLead) return null;
        }

        if (!ProjectToScreen(PoseConverter.ToXna(state.LeadWorldPos), view, proj, cameraEye, out Vector2 leadPx)) return null;
        if (!ProjectToScreen(PoseConverter.ToXna(state.TargetPos), view, proj, cameraEye, out Vector2 targetPx)) return null;

        return new LeadIndicatorView(leadPx, targetPx, state.InRange, state.Green, _nowSeconds);
    }

    /// <summary>
    /// P266 — the shared world→screen projection used by
    /// <see cref="ComputeLeadIndicator"/>: subtract the camera eye, transform
    /// through <c>view*proj</c>, reject a point behind the camera or outside the
    /// FOV frustum, then map NDC to viewport pixels. Returns false (and nothing in
    /// <paramref name="screen"/>) when the point is not visible.
    /// </summary>
    private bool ProjectToScreen(Vector3 world, Matrix view, Matrix proj, Vector3 cameraEye, out Vector2 screen)
    {
        screen = default;
        var viewProj = view * proj;
        var rel = world - cameraEye;
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        if (clip.W <= 1e-6f) return false;                 // behind the camera
        float sx = clip.X / clip.W;
        float sy = clip.Y / clip.W;
        if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return false;   // outside the FOV frustum
        var vp = GraphicsDevice.Viewport;
        screen = new Vector2((sx * 0.5f + 0.5f) * vp.Width, (0.5f - sy * 0.5f) * vp.Height);
        return true;
    }

    /// <summary>
    /// HUD-8 — project a world-space DIRECTION (w=0) to viewport px, mirroring
    /// native <c>Camera::TransformDirectionToImage</c> + <c>rect.TransformNDCToImage</c>
    /// (indicator.cpp:140-143). Returns null when the direction does not land
    /// inside the view rect (native <c>rect.Inside</c>, indicator.cpp:144).
    /// </summary>
    private Vector2? TransformDirectionToScreen(Vector3 direction, Matrix view, Matrix proj)
    {
        var viewProj = view * proj;
        var clip = Vector4.Transform(new Vector4(direction.X, direction.Y, direction.Z, 0f), viewProj);
        if (MathF.Abs(clip.W) < 1e-6f) return null;
        float sx = clip.X / clip.W;
        float sy = clip.Y / clip.W;
        if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return null;   // outside the view rect
        var vp = GraphicsDevice.Viewport;
        return new Vector2((sx * 0.5f + 0.5f) * vp.Width, (0.5f - sy * 0.5f) * vp.Height);
    }

    /// <summary>
    /// HUD-8 — the velocity-direction marker: project the own source ship's
    /// velocity DIRECTION into the view (cockpit only; the zero-speed and camera
    /// gates are applied by <see cref="HudModel.VelocityMarkerVisible"/>).
    /// </summary>
    private Vector2? ComputeVelocityMarker(Matrix view, Matrix proj)
    {
        if (!TryOwnPose(out _, out _, out var velocity)) return null;
        if ((int)(velocity.Length() + 0.5f) == 0) return null;
        return TransformDirectionToScreen(PoseConverter.ToXna(velocity), view, proj);
    }

    /// <summary>
    /// HUD-8 — chase weapon aim without a target (indicator.cpp:367-405): project
    /// the mounted weapon's forward range point (speed × lifespan) from the real
    /// camera as a direction. Returns null when the weapon/loadout is unresolved.
    /// </summary>
    private Vector2? ComputeChaseAim(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        if (!TryOwnPose(out var position, out var orientation, out _)) return null;
        if (!TryResolveActiveWeaponRange(out float range)) return null;
        var shipPosition = PoseConverter.ToXna(position);
        var forward = PoseConverter.ToXna(orientation.GetForward());
        var tracking = shipPosition + forward * range;
        var aim = tracking - cameraEye;
        if (aim.LengthSquared() < 1e-6f) return null;
        aim.Normalize();
        return TransformDirectionToScreen(aim, view, proj);
    }

    /// <summary>
    /// HUD-8 — resolve the currently-selected weapon's projectile range
    /// (speed × lifespan, indicator.cpp:391-393) from the live loadout + core
    /// projectile table. Mirrors <see cref="BuildWeaponStatusRows"/>'s active
    /// weapon selection (group-fire → first weapon; single → selected mount).
    /// </summary>
    private bool TryResolveActiveWeaponRange(out float range)
    {
        range = 0f;
        var catalog = _shopCatalog;
        if (catalog?.ProjectileTypes is not { } projectiles) return false;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)
                || own.Loadout is not { Count: > 0 } loadout) return false;

            DataPartTypeIGC.Weapon? active = null;
            foreach (var p in loadout)
            {
                DataPartTypeIGC part = default;
                bool found = false;
                foreach (var candidate in catalog.Parts)
                    if (candidate.PartId == p.PartId) { part = candidate; found = true; break; }
                if (!found || part.Type is not DataPartTypeIGC.Weapon weapon) continue;
                if (_groupFire) { active = weapon; break; }
                if (p.MountId == _selectedMount) { active = weapon; break; }
            }

            if (active is not { } resolved || !projectiles.TryGetValue(resolved.ProjectileTypeId, out var projectile)) return false;
            range = projectile.Speed * projectile.Lifespan;
            return range > 0f;
        }
    }

    /// <summary>
    /// HUD-8 — accepted/current order turn arrows (indicator.cpp:411-457): the
    /// normalized centre-relative screen offset toward each off-axis order target,
    /// gated on same cluster, non-self/source, and offset² &gt; 108². Current target
    /// is the native <c>c_cmdCurrent</c> TARGET COMMAND
    /// (<see cref="ClientWorld.MyCurrentCommand"/>, set by <c>SetTarget</c>),
    /// NOT the client-local autopilot target (<c>_autoTarget</c>); accepted is
    /// <see cref="ClientWorld.MyAcceptedCommand"/>.
    /// </summary>
    private HudOrderArrows? ComputeOrderArrows(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        var world = _session.World;
        QueuedCommandState? currentCommand;
        QueuedCommandState? accepted;
        short mySector;
        short me;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } myShip) return null;
            me = myShip;
            if (!world.Ships.TryGetValue(me, out var own)) return null;
            mySector = own.SectorId;
            currentCommand = world.MyCurrentCommand;
            accepted = world.MyAcceptedCommand;
        }

        HudPoint currentDirection = default;
        HudPoint? resolvedCurrentDirection = null;
        if (currentCommand is { } current
            && (current.TargetType != IgcObjectType.Ship || current.TargetId != me)
            && TryResolveCommandPosition(current, out var currentPosition, out var currentCluster)
            && currentCluster == mySector
            && TryProjectOrderOffset(currentPosition, view, proj, cameraEye, out currentDirection))
        {
            resolvedCurrentDirection = currentDirection;
        }

        HudPoint acceptedDirection = default;
        HudPoint? resolvedAcceptedDirection = null;
        if (accepted is { } acceptedOrder
            && (acceptedOrder.TargetType != IgcObjectType.Ship || acceptedOrder.TargetId != me)
            && TryResolveCommandPosition(acceptedOrder, out var acceptedPosition, out var acceptedCluster)
            && acceptedCluster == mySector
            && TryProjectOrderOffset(acceptedPosition, view, proj, cameraEye, out acceptedDirection))
        {
            resolvedAcceptedDirection = acceptedDirection;
        }

        HudPoint? currentDirectionResult = resolvedCurrentDirection;
        HudPoint? acceptedDirectionResult = resolvedAcceptedDirection;
        if (currentDirectionResult is null && acceptedDirectionResult is null) return null;
        bool sameTarget = currentCommand is { } ct && accepted is { } ac && SameCommandObject(ct, ac);
        return new HudOrderArrows(acceptedDirectionResult, currentDirectionResult, sameTarget);
    }

    private static bool SameCommandObject(QueuedCommandState current, QueuedCommandState accepted) =>
        current.TargetType == accepted.TargetType && current.TargetId == accepted.TargetId;

    private bool TryProjectOrderOffset(SimVec world, Matrix view, Matrix proj, Vector3 cameraEye, out HudPoint direction)
    {
        direction = default;
        if (!ProjectToScreen(PoseConverter.ToXna(world), view, proj, cameraEye, out Vector2 screen)) return false;
        var vp = GraphicsDevice.Viewport;
        float dx = screen.X - vp.Width / 2f;
        float dy = screen.Y - vp.Height / 2f;
        float lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 108f * 108f) return false;   // native radiusRing gate, indicator.cpp:425
        float inv = 1f / MathF.Sqrt(lengthSquared);
        direction = new HudPoint(dx * inv, dy * inv);
        return true;
    }

    private bool TryResolveCommandPosition(QueuedCommandState command, out SimVec position, out short clusterId)
    {
        position = default;
        clusterId = -1;
        var world = _session.World;
        lock (_session.Gate)
        {
            switch (command.TargetType)
            {
                case IgcObjectType.Ship when world.Ships.TryGetValue(command.TargetId, out var ship) && !ship.IsDead:
                    if (!ResolveShipAnchor(_poses, world.Ships, command.TargetId, out position)) return false;
                    clusterId = ship.SectorId;
                    return clusterId >= 0;
                case IgcObjectType.Station when world.Stations.TryGetValue(command.TargetId, out var station):
                    position = Vec(station.Position); clusterId = station.ClusterId; return true;
                case IgcObjectType.Asteroid when world.Asteroids.TryGetValue(command.TargetId, out var asteroid):
                    position = Vec(asteroid.Position); clusterId = asteroid.ClusterId; return true;
                case IgcObjectType.Warp when world.Warps.TryGetValue(command.TargetId, out var warp):
                    position = Vec(warp.Position); clusterId = warp.ClusterId; return true;
                case IgcObjectType.Probe when world.Probes.TryGetValue(command.TargetId, out var probe):
                    position = probe.Position; clusterId = probe.ClusterId; return true;
                case IgcObjectType.Treasure when world.Treasures.TryGetValue(command.TargetId, out var treasure):
                    position = treasure.Position; clusterId = treasure.ClusterId; return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// P295 — whether the local pilot is currently boarded as a gunner on a
    /// carrier's turret. Reuses the port's existing turret-boarding state
    /// (<see cref="Allegiance.ClientCore.State.ShipEntity.ParentId"/> ≠ NA/-1),
    /// the same state P109/P273 use to identify a boarded gunner; it is not
    /// re-derived here. Drives which modes the <c>V</c> camera cycle offers.
    /// </summary>
    private bool IsTurretGunner()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var own)) return false;
            return own.ParentId != -1;   // NA when not docked/turreted
        }
    }

    /// <summary>
    /// P295 — the carrier's render pose for the external-turret camera: the
    /// position/rotation of the ship the local pilot is gunning on (its
    /// <see cref="Allegiance.ClientCore.State.ShipEntity.ParentId"/>). Returns
    /// false when not gunning, or when the carrier's pose is not currently
    /// known to the renderer (caller falls back to a normal chase view).
    /// </summary>
    private bool TryGetGunnerCarrierPose(out Vector3 carrierPos, out Matrix carrierRot, out float carrierRadius)
    {
        carrierPos = default;
        carrierRot = Matrix.Identity;
        carrierRadius = 0f;
        short parent;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var own)) return false;
            parent = own.ParentId;
            if (parent == -1) return false;   // NA — not docked/turreted
            // P338 — the carrier's real rendered radius (0.5 × its HullType.Length)
            // so the external-turret chase framing matches its class, not ours.
            if (world.Ships.TryGetValue(parent, out var car))
            {
                float len = HullLength(car.HullId);
                carrierRadius = len > 0f ? 0.5f * len : 0f;
            }
        }
        if (!_poses.TryGetValue(parent, out var cp)) return false;
        carrierPos = PoseConverter.ToXna(cp.Position);
        carrierRot = PoseConverter.ToXnaRotation(cp.Orientation);
        return true;
    }

    /// <summary>
    /// P675 — resolve native's source ship last-fired missile and its current
    /// target (or launcher fallback). WinTrek.cpp:7237-7268. The creation wire
    /// already carries launcher/target ids; retain them through motion updates.
    /// </summary>
    private bool TryGetMissileCameraFrame(out Vector3 missileXna, out Vector3 focusXna)
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && MissileCameraFrame(_session.World, me, out var missile, out var focus))
            {
                missileXna = PoseConverter.ToXna(missile); focusXna = PoseConverter.ToXna(focus); return true;
            }
        }
        missileXna = focusXna = default; return false;
    }

    // WinTrek.cpp:7237-7268: source ship's LAST missile; track its live target,
    // otherwise its launcher. A gunner uses the parent carrier as source.
    internal static bool MissileCameraFrame(ClientWorld world, short me, out SimVec missilePosition, out SimVec focus)
    {
        missilePosition = focus = default;
        if (!world.Ships.TryGetValue(me, out var own)) return false;
        short sourceId = own.ParentId >= 0 ? own.ParentId : me;
        if (!world.Ships.TryGetValue(sourceId, out var source) || source.Latest is not { } pose
            || world.LastMissileFiredBy(sourceId) is not { } missile || missile.ClusterId != source.SectorId) return false;
        missilePosition = missile.Position; focus = pose.Position;
        if (missile.SpoofTargetPosition is { } spoof) { focus = spoof; return true; }
        switch (missile.TargetType)
        {
            case IgcObjectType.Ship when world.Ships.TryGetValue(missile.TargetId, out var ship)
                && !ship.IsNotVisible && ship.SectorId == missile.ClusterId && ship.Latest is { } target:
                focus = target.Position; break;
            case IgcObjectType.Station when world.Stations.TryGetValue(missile.TargetId, out var station) && station.ClusterId == missile.ClusterId:
                focus = Vec(station.Position); break;
            case IgcObjectType.Asteroid when world.Asteroids.TryGetValue(missile.TargetId, out var asteroid) && asteroid.ClusterId == missile.ClusterId:
                focus = Vec(asteroid.Position); break;
            case IgcObjectType.Probe when world.Probes.TryGetValue(missile.TargetId, out var probe) && probe.ClusterId == missile.ClusterId:
                focus = probe.Position; break;
        }
        return true;
    }
}
