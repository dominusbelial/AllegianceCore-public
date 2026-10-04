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
            if (_session.World.MyShipId is not { } me) return false;
            if (!_session.World.Ships.TryGetValue(me, out var mine) || mine.Latest is not { } up) return false;
            // P508 — the cameras (chase/cockpit/external), targeting and autopilot all
            // read the own pose through this single seam: the local SIMULATION pose (the
            // client flies its own ship; a control change moves the camera on the same
            // frame, no round trip). Raw server pose otherwise (the --noPrediction debug
            // toggle, or no hull resolved yet).
            if (_ownShipPrediction && _ownSim.HaveHull)
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
        lock (_session.Gate)
        {
            lockState = world.MissileLock;
            if (!lockState.HasTarget) return null;
            // P523 — anchor the lock box to the pose the renderer drew THIS frame
            // for the lock target (_poses), not the server's raw 20 Hz lock-update
            // position, so the box stays glued to the smoothly-drawn hull instead
            // of jittering. Falls back to the wire position only when the target
            // ship was not rendered this frame.
            if (ResolveShipAnchor(_poses, world.Ships, lockState.TargetId, out var lockSimPos))
                xnaTarget = PoseConverter.ToXna(lockSimPos);
            else
                xnaTarget = PoseConverter.ToXna(lockState.TargetPosition);
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
        return new LockBracketView(screen, MissileLockFeedback.Classify(lockState.LockFraction), _nowSeconds);
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
        // Snapshot the selection under the gate lock (P319 discipline: grab state
        // under the lock, do no unlocked world reads). AutopilotTarget is a struct,
        // so copying the selection and the engaged flag is race-safe. P359 — when
        // the target is a SHIP, also snapshot its pilot name + live hull/shield
        // fractions from the SAME per-ship ServerShipUpdate the player's own gauges
        // read (works for any ship the server exported to this client, friendly or
        // enemy — the client only ever knows ships the server actually sent it).
        // P423 — the health-bar readout is extended to ANY damageable target, not
        // just ships: a station (live STATIONS_UPDATE hull/shield) and an asteroid
        // (live ASTEROIDS_UPDATE structural fraction) both feed the generic health
        // bar; an asteroid additionally carries its live remaining-He3 ore fraction
        // (OreFraction), rendered as the distinct ore readout. Only a ship has a
        // pilot name; station/asteroid have none, so the name stays null for them.
        AutopilotTarget target;
        bool engaged;
        string? charName = null;
        float? hull = null, shield = null;
        bool? isEnemy = null;
        float? ore = null;
        lock (_session.Gate)
        {
            if (_autoTarget is not { } t) return null;
            target = t;
            engaged = _autoPilotEngaged;
            switch (target.Kind)
            {
                case TargetKind.Ship:
                    if (_session.World.Ships.TryGetValue(target.Id, out var targetShip)
                        && !targetShip.IsNotVisible)
                    {
                        charName = string.IsNullOrEmpty(targetShip.CharacterName)
                            ? $"ship {target.Id}" : targetShip.CharacterName;
                        if (targetShip.Latest is { } sup)
                        {
                            hull = sup.HullFraction;
                            shield = sup.ShieldFraction;
                        }
                        // P376 — friendly collapses to "same side as me" (no alliance concept
                        // in this port), so ANY ship on a different side is an enemy. Same
                        // resolution point (and same lock) as the pilot readout above.
                        isEnemy = !(_session.World.MySideId is { } mySide && targetShip.SideId == mySide);
                    }
                    break;
                case TargetKind.Asteroid:
                    // P423 — a rock is a real damageable target: its live structural
                    // fraction feeds the generic health bar, and its remaining He3 ore
                    // (OreFraction) drives the distinct asteroid ore readout.
                    if (_session.World.AsteroidLive.TryGetValue(target.Id, out var astLive))
                    {
                        hull = astLive.Fraction;
                        ore = astLive.OreFraction;
                    }
                    break;
                case TargetKind.Station:
                    // P423 — a station is a real damageable target with an armor pool.
                    if (_session.World.StationLive.TryGetValue(target.Id, out var stLive))
                    {
                        hull = stLive.HullFraction;
                        shield = stLive.ShieldFraction;
                    }
                    break;
            }
        }

        // Resolve the target's live world position (false when it has died/vanished),
        // then reject it when it does not project into this frame's view.
        if (!ResolveTargetPosition(target, out var simPos)) return null;
        if (!ProjectToScreen(PoseConverter.ToXna(simPos), view, proj, cameraEye, out Vector2 screen)) return null;

        return new TargetSelectionView(screen, engaged, charName, hull, shield, isEnemy, ore);
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
    /// P295 — the world position of the missile the external-missile camera
    /// should watch: the most recently created live missile in the local
    /// player's sector (cluster). The client has no per-ship "last missile
    /// fired" pointer and the missile wire carries no shooter id
    /// (<see cref="Allegiance.ClientCore.State.MissileEntity"/>), so — faithful
    /// in spirit to native's <c>GetLastMissileFired()</c> — it watches the
    /// newest in-sector missile (missile ids are monotonically increasing
    /// shorts). Returns false when no missile is flying in-sector (caller falls
    /// back to a chase view, matching native WinTrek.cpp:7259-7268).
    /// </summary>
    private bool TryGetLatestMissilePos(out Vector3 missileXna)
    {
        missileXna = default;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var own)) return false;
            short mySector = own.SectorId;
            MissileEntity? best = null;
            foreach (var (_, m) in world.Missiles)
            {
                if (m.ClusterId != mySector) continue;
                if (best is null || (short)(m.MissileId - best.MissileId) > 0)
                    best = m;
            }
            if (best is null) return false;
            missileXna = PoseConverter.ToXna(best.Position);
        }
        return true;
    }
}
