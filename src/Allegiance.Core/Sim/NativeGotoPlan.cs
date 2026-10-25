using Allegiance.Core.Collision;
using M = Allegiance.Core.Sim.DockApproach.Mask;

namespace Allegiance.Core.Sim;

/// <summary>
/// P817: pure native drone travel rules (Igc/common.cpp:1157-2333).
/// Callers supply visible graph edges, friendly clusters, target geometry and
/// Ignore-filtered obstacles. No world mutation, random source or tick allocation.
/// All rules are design unless specifically marked as a native bug below.
/// </summary>
public static class NativeGotoPlan
{
    public enum Kind { Point, Asteroid, Station, Ship, Warp, Probe, Mine }
    public enum Objective { Goto, Enter }
    public readonly record struct Target(int Key, short Cluster, Kind Kind, Objective Objective,
        Vector3 Position, float Radius = 0, Vector3 Velocity = default, Vector3 Axis = default,
        bool Pickup = false, short WarpDestination = -1, bool Present = true);
    public readonly record struct Mover(Vector3 Position, Vector3 Velocity, Orientation Orientation,
        float Radius, DockApproach.TurnState Turn, float MaxRoll = 0, float TorqueRoll = 0,
        float RateRoll = 0, bool Experimental = false);
    public readonly record struct Obstacle(int Key, Kind Kind, Vector3 Position, Vector3 Velocity,
        float Radius, bool Ignore = false);
    public readonly record struct Edge(short From, short To, short WarpId, Vector3 Position, bool Visible);
    public readonly record struct Cluster(short Id, bool Friendly);
    public readonly record struct Path(short Cluster, short FirstWarp);
    public readonly record struct Result(ControlData Controls, int Buttons, bool Done, bool WantBoost,
        DockApproach.Step Step);

    /// <summary>Per-ship waypoint state, igc.h:5285-5362, common.cpp:1845-1933.
    /// Replan on Set, our cluster change, or target cluster change; a target
    /// entering our cluster clears the warp immediately (the native Xynth fix).</summary>
    public struct Waypoints
    {
        public int Mask { get; private set; }
        public short WarpId { get; private set; }
        private int _key;
        private short _cluster, _targetCluster;
        private Objective _objective;
        public bool Refresh(short cluster, in Target target)
        {
            bool changed = Mask == 0 || _key != target.Key || _objective != target.Objective ||
                           _cluster != cluster || _targetCluster != target.Cluster;
            if (!changed) return false;
            bool entered = Mask != 0 && _key == target.Key && _cluster != cluster &&
                target.Kind == Kind.Warp && target.Objective == Objective.Enter && target.WarpDestination == cluster;
            _key = target.Key; _objective = target.Objective;
            _cluster = cluster; _targetCluster = target.Cluster;
            Mask = entered ? 0 : 1; WarpId = -1;
            return !entered;
        }
        public void SetWarp(short warp) { WarpId = warp; Mask = warp < 0 ? 1 : 3; }
        public void Reset() { Mask = 0; WarpId = -1; }
    }

    /// <summary>FindPath, common.cpp:894-1011: sort ONLY initial exits by
    /// distance, then append expansions in warp order. The final cluster is
    /// allowed in coward mode; retry brave for pilots below carrier (:1904-1907).
    /// Workspace belongs to the caller and can be reused. Each cluster is
    /// expanded once (duplicate queued native entries cannot change first path).</summary>
    public static short FindPath(short from, short to, in Vector3 position, bool coward,
        ReadOnlySpan<Edge> edges, ReadOnlySpan<Cluster> clusters, Span<Path> queue, Span<short> explored)
    {
        if (from == to) return -1;
        int count = 0, seen = 1;
        explored[0] = from;
        for (int i = 0; i < edges.Length; i++)
        {
            var e = edges[i];
            if (e.From != from || !Allowed(e, to, coward, clusters)) continue;
            int at = count;
            float distance = (e.Position - position).LengthSquared();
            while (at > 0 && distance < InitialDistance(queue[at - 1].FirstWarp, position, edges))
            { queue[at] = queue[at - 1]; at--; }
            queue[at] = new(e.To, e.WarpId); count++;
        }
        for (int head = 0; head < count; head++)
        {
            var p = queue[head];
            if (p.Cluster == to) return p.FirstWarp;
            bool visited = false;
            for (int j = 0; j < seen; j++) if (explored[j] == p.Cluster) { visited = true; break; }
            if (visited) continue;
            explored[seen++] = p.Cluster;
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                if (e.From != p.Cluster || !Allowed(e, to, coward, clusters)) continue;
                bool done = false;
                for (int j = 0; j < seen; j++) if (explored[j] == e.To) { done = true; break; }
                if (!done) queue[count++] = new(e.To, p.FirstWarp);
            }
        }
        return -1;
    }
    private static bool Allowed(in Edge edge, short target, bool coward, ReadOnlySpan<Cluster> clusters)
    {
        if (!edge.Visible) return false;
        if (!coward || edge.To == target) return true;
        foreach (var cluster in clusters) if (cluster.Id == edge.To) return cluster.Friendly;
        return false;
    }
    private static float InitialDistance(short warp, in Vector3 position, ReadOnlySpan<Edge> edges)
    {
        foreach (var e in edges) if (e.WarpId == warp) return (e.Position - position).LengthSquared();
        return float.MaxValue;
    }

    /// <summary>Drag rest solve common.cpp:1936-1983, including the .1 speed cutoff.</summary>
    public static float StoppingDistance(in Mover mover, float backMultiplier) => DockApproach.StoppingDistance(
        mover.Velocity.Length(), mover.Turn.Thrust, mover.Turn.Mass, mover.Turn.MaxSpeed, backMultiplier);

    public static Result Execute(in Mover mover, in Target target, ReadOnlySpan<DockApproach.DockBay> bays,
        ReadOnlySpan<Obstacle> obstacles, float dt, float backMultiplier, bool wantBoost = false,
        bool dodge = true, bool active = true, bool pending = false, Vector3? boostTargetPosition = null)
    {
        // A pending plan with no reachable waypoint still waits/dodges without
        // completing; Execute's boost cutoff uses the ORIGINAL target (:1817).
        bool hasPlan = active || pending;
        // A hidden/dead target (null site cluster) skips movement AND Dodge
        // (common.cpp:1873-1874). Idle/no-path Dodge belongs to Ship's wrapper.
        dodge &= !hasPlan || target.Present;
        active &= target.Present;
        float speed = mover.Velocity.Length();
        float distanceRest = DockApproach.StoppingDistance(speed, mover.Turn.Thrust,
            mover.Turn.Mass, mover.Turn.MaxSpeed, backMultiplier);
        Vector3 rest = speed > .1f ? mover.Position + mover.Velocity * (distanceRest / speed) : mover.Position;
        var step = active ? GetGotoPosition(mover, target, bays, distanceRest, rest) : default;
        int skip = active && SkipTarget(step, target) ? target.Key : int.MinValue;
        var controls = ControlData.Neutral;
        int buttons = 0;
        bool done = !hasPlan;
        if (dodge && (step.Mask & M.NoDodge) == 0 &&
            Dodge(mover, obstacles, skip, (step.Mask & M.DodgeShips) != 0, out buttons))
        {
            // Dodge holds throttle killed, but continues facing the waypoint (:2000-2016).
            Vector3 direction = (step.Mask & (M.Pivot | M.Enter)) != 0 ? step.Facing : step.Goal - mover.Position;
            if (active) controls = Face(mover, direction, dt);
        }
        else if (active)
        {
            if ((step.Mask & M.Finished) != 0 && target.Kind != Kind.Ship) done = true;
            else if ((step.Mask & M.Pivot) != 0) controls = Face(mover, step.Facing, dt);
            else if ((step.Mask & M.KillThrottle) != 0)
            {
                controls = Face(mover, step.Goal - mover.Position, dt);
                if (target.Kind == Kind.Ship)
                {
                    double vMax = mover.Turn.MaxSpeed * (double)backMultiplier;
                    double k = mover.Turn.Thrust / (mover.Turn.Mass * (double)mover.Turn.MaxSpeed);
                    double mkt = speed > .1f ? Math.Log(vMax / (speed + vMax)) : 0;
                    float distance = (step.Goal - mover.Position).Length() - mover.Radius - target.Radius;
                    float targetSpeed = MathF.Min(mover.Orientation.GetForward() * target.Velocity, (float)(k * distance - vMax * mkt));
                    controls = controls with { Throttle = MathF.Max(-1, targetSpeed >= mover.Turn.MaxSpeed ? 1 : 2 * targetSpeed / mover.Turn.MaxSpeed - 1.01f) };
                }
            }
            else if ((step.Mask & M.Enter) != 0)
            {
                controls = Face(mover, step.Facing, dt) with { Throttle = 1 };
                if ((step.Mask & M.Roll) != 0) controls = controls with { Roll = Roll(mover, controls, dt) };
            }
            else
            {
                Vector3 originalPath = step.Goal - mover.Position;
                Vector3 path = Divert(mover, originalPath, obstacles, skip);
                if (path.LengthSquared() >= .1f)
                {
                    controls = Face(mover, path, dt);
                    if (Angle(mover.Orientation, path) < MathF.PI / 8)
                    {
                        float throttle = 1;
                        if ((step.Mask & M.Fast) == 0 && originalPath.LengthSquared() < distanceRest * distanceRest) throttle = -1;
                        else if (target.Kind == Kind.Ship && (originalPath.Length() < distanceRest * 2 || originalPath.Length() < 200))
                        {
                            double vMax = mover.Turn.MaxSpeed * (double)backMultiplier;
                            double k = mover.Turn.Thrust / (mover.Turn.Mass * (double)mover.Turn.MaxSpeed);
                            double mkt = speed > .1f ? Math.Log(vMax / (speed + vMax)) : 0;
                            float targetSpeed = MathF.Max(mover.Orientation.GetForward() * target.Velocity, (float)(k * originalPath.Length() - vMax * mkt));
                            throttle = MathF.Min(1, targetSpeed >= mover.Turn.MaxSpeed ? 1 : 2 * targetSpeed / mover.Turn.MaxSpeed - .99f);
                        }
                        else if (MinefieldAhead(mover, path, originalPath.LengthSquared(), distanceRest, obstacles)) throttle = -.5f;
                        controls = controls with { Throttle = throttle };
                    }
                    else
                    {
                        float k = mover.Turn.MaxSpeed > 0 && mover.Turn.Mass > 0 ? mover.Turn.Thrust / (mover.Turn.Mass * mover.Turn.MaxSpeed) : 0;
                        buttons = Direction(k > 0 ? path - mover.Velocity / k : path, mover.Orientation);
                    }
                }
            }
        }
        // Execute's boost latch: near ORIGINAL target, not intermediate warp (:1813-1821).
        if (wantBoost)
        {
            buttons |= ShipControlState.AfterburnerButton;
            if (hasPlan && !done && ((boostTargetPosition ?? target.Position) - mover.Position).LengthSquared() < 4000000)
            { buttons &= ~ShipControlState.AfterburnerButton; wantBoost = false; }
        }
        return new(controls, buttons, done, wantBoost, step);
    }

    /// <summary>Waypoint::GetGotoPosition, common.cpp:1324-1559; bay/strip
    /// approach common.cpp:1157-1320. No-bay targets power-glide, never brake.</summary>
    public static DockApproach.Step GetGotoPosition(in Mover mover, in Target target,
        ReadOnlySpan<DockApproach.DockBay> bays, float distanceRest, in Vector3 rest)
    {
        Vector3 centers = target.Position - mover.Position;
        if (target.Objective == Objective.Goto)
        {
            float radius = mover.Radius + target.Radius;
            if (centers.LengthSquared() <= (radius + distanceRest) * (radius + distanceRest))
                return new(M.Pivot, -1, target.Position, centers);
            if ((target.Position - rest).LengthSquared() < (radius + 10) * (radius + 10))
                return new(distanceRest < .5f ? M.Pivot | M.Finished : M.KillThrottle, -1, target.Position, centers);
            return new(0, -1, target.Position, default);
        }
        if (target.Kind == Kind.Asteroid && target.Axis.LengthSquared() > 0)
        {
            // Native BUG: integer division makes every positive ship ID offset zero (:1392-1398).
            Span<DockApproach.DockBay> strips = stackalloc DockApproach.DockBay[2];
            strips[0] = new(default, target.Axis); strips[1] = new(default, -target.Axis);
            var approach = Approach(mover, target, strips, distanceRest, rest);
            return (approach.Mask & M.Enter) != 0 ? approach with { Mask = approach.Mask | M.Finished } : approach;
        }
        if (target.Kind == Kind.Ship && target.Pickup)
        {
            float t = ShipDodgeLaw.SolveForImpact(centers, target.Velocity, mover.Turn.MaxSpeed, 0, out _);
            return new(M.Fast, -1, target.Position + target.Velocity * t, default);
        }
        if (target.Kind is Kind.Station or Kind.Ship && bays.Length > 0)
        {
            var approach = Approach(mover, target, bays, distanceRest, rest);
            return (approach.Mask & M.Enter) != 0 ? approach with { Mask = approach.Mask | M.Roll } : approach;
        }
        return new(M.Fast, -1, target.Position, default);
    }
    private static bool SkipTarget(in DockApproach.Step step, in Target target) =>
        target.Objective == Objective.Goto ? (step.Mask & M.Pivot) == 0 || (step.Mask & M.Finished) != 0
        : (step.Mask & (M.NoDodge | M.Fast)) != 0;

    private static DockApproach.Step Approach(in Mover mover, in Target target,
        ReadOnlySpan<DockApproach.DockBay> bays, float distanceRest, in Vector3 rest)
    {
        Vector3 now = mover.Position - target.Position, end = rest - target.Position;
        float radius = mover.Radius + target.Radius + 10, bestError = float.MaxValue, bestOffset = 0;
        int best = 0;
        Vector3 goal = default;
        for (int i = 0; i < bays.Length; i++)
        {
            var bay = bays[i];
            float b = bay.Center * bay.Direction, c = bay.Center.LengthSquared() - radius * radius;
            float t = b * b < c ? .1f : MathF.Sqrt(b * b - c) - b;
            Vector3 candidate = bay.Center + bay.Direction * t;
            Vector3 deltaNow = now - bay.Center, deltaRest = end - bay.Center;
            float dotNow = deltaNow * bay.Direction, dotRest = deltaRest * bay.Direction;
            bool inFront = dotNow >= 0 || dotRest >= radius;
            float error = inFront ? deltaRest.LengthSquared() - dotRest * dotRest : (end - candidate).LengthSquared();
            if (error >= bestError) continue;
            bestError = error; bestOffset = inFront ? deltaNow.LengthSquared() - dotNow * dotNow : float.MaxValue;
            best = i; goal = candidate;
        }
        if (bestError > 36)
        {
            float d2 = (now - goal).LengthSquared();
            uint mask = d2 > distanceRest * distanceRest * 2 || (end - goal).LengthSquared() <= d2 + 1
                ? ((now - bays[best].Center) * bays[best].Direction >= 0 ? M.DodgeShips : 0) : M.Pivot;
            return new(mask, best, goal + target.Position, goal + target.Position - mover.Position);
        }
        if (bestOffset > 40) return new(M.KillThrottle | M.DodgeShips, best, goal + target.Position, default);
        Vector3 facing = bays[best].Center + target.Position - mover.Position;
        float rate = mover.Experimental ? .1f : .01f;
        bool charge = mover.Turn.RateYaw * mover.Turn.RateYaw + mover.Turn.RatePitch * mover.Turn.RatePitch < rate * rate &&
                      DockApproach.CosForward2(mover.Orientation, facing) > .999f * .999f;
        return new((charge ? M.Enter : M.Pivot) | M.NoDodge, best, bays[best].Center + target.Position, facing);
    }

    /// <summary>Dodge common.cpp:1693-1790, getDirection :1575-1636. Mines
    /// at speed &lt;=50 and ignored/no-hit objects are excluded before impact solve.</summary>
    public static bool Dodge(in Mover mover, ReadOnlySpan<Obstacle> obstacles, int skip, bool shipsOnly, out int buttons)
    {
        float best = float.MaxValue;
        Obstacle winner = default;
        foreach (var o in obstacles)
        {
            if (o.Ignore || o.Key == skip || shipsOnly && o.Kind != Kind.Ship ||
                o.Kind == Kind.Point || o.Kind == Kind.Probe && o.Radius <= 0 ||
                o.Kind == Kind.Station && o.Radius <= 0) continue;
            if (o.Kind == MineKind && mover.Velocity.LengthSquared() <= 2500) continue;
            float r = mover.Radius + 1 + o.Radius;
            Vector3 dp = o.Position - mover.Position;
            float t = dp.LengthSquared() > r * r ? ShipDodgeLaw.SolveForImpact(dp, o.Velocity - mover.Velocity, 0, r, out _) : 0;
            float accel = mover.Turn.Mass > 0 ? mover.Turn.Thrust / mover.Turn.Mass : 0;
            if (t < best && ShipDodgeLaw.IsImminent(t, r, accel, float.PositiveInfinity)) { best = t; winner = o; }
        }
        if (best == float.MaxValue) { buttons = 0; return false; }
        Vector3 away = mover.Position - winner.Position;
        if (best > 0)
        {
            Vector3 dv = mover.Velocity - winner.Velocity;
            away = dv.LengthSquared() > .1f ? away - dv * ((away * dv) / dv.LengthSquared()) : away;
            if (away.LengthSquared() < .1f) away = ShipDodgeLaw.OrthogonalVector(mover.Position - winner.Position);
        }
        buttons = Direction(away, mover.Orientation);
        return true;
    }
    public const Kind MineKind = Kind.Mine;

    /// <summary>getDirection common.cpp:1575-1636, including native tie order.
    /// Three value locals replace the allocation/sort used by older dock callers.</summary>
    public static int Direction(in Vector3 direction, in Orientation orientation)
    {
        Vector3 d = orientation.TimesInverse(direction);
        float x = MathF.Abs(d.X), y = MathF.Abs(d.Y), z = MathF.Abs(d.Z);
        int sx = d.X >= 0 ? ShipControlState.RightButton : ShipControlState.LeftButton;
        int sy = d.Y >= 0 ? ShipControlState.UpButton : ShipControlState.DownButton;
        int sz = d.Z <= 0 ? ShipControlState.ForwardButton : ShipControlState.BackwardButton;
        if (x > y)
            return y > z ? Select(x, y, z, sx, sy, sz) : x > z ? Select(x, z, y, sx, sz, sy) : Select(z, x, y, sz, sx, sy);
        return x > z ? Select(y, x, z, sy, sx, sz) : y > z ? Select(y, z, x, sy, sz, sx) : Select(z, y, x, sz, sy, sx);
    }
    private static int Select(float a, float b, float c, int sa, int sb, int sc) =>
        a > 2 * b ? sa : a > 2 * c ? sa | sb : sa | sb | sc;
    private static ControlData Face(in Mover mover, in Vector3 direction, float dt)
    {
        if (direction.LengthSquared() < .1f) return ControlData.Neutral;
        var (yaw, pitch) = DockApproach.Face(mover.Orientation, direction, 2, mover.Turn, mover.Velocity.Length(), dt);
        return new(yaw, pitch, 0, -1);
    }
    private static float Angle(in Orientation orientation, in Vector3 direction)
    {
        float dot = Math.Clamp(orientation.GetForward() * direction / direction.Length(), -1, 1);
        if (dot < .98f) return MathF.Acos(dot);
        Vector3 local = orientation.TimesInverse(direction / direction.Length());
        float y = MathF.Acos(Math.Clamp(local.X, -1, 1)) - MathF.PI / 2;
        float p = MathF.Acos(Math.Clamp(local.Y, -1, 1)) - MathF.PI / 2;
        return MathF.Sqrt(y * y + p * p);
    }
    private static float Roll(in Mover mover, in ControlData controls, float dt)
    {
        float cos = mover.Orientation.GetUp().Z;
        if (cos >= .99f || cos <= -.99f || mover.MaxRoll <= 0 || dt <= 0) return 0;
        float roll = cos >= 0 ? -MathF.Acos(cos) : MathF.Acos(-cos);
        if (mover.Orientation.GetRight().Z < 0) roll = -roll;
        float fraction = mover.Turn.MaxSpeed > 0 ? mover.Velocity.Length() / mover.Turn.MaxSpeed : 0;
        float tm = .5f + fraction / (fraction + 1);
        if (mover.TorqueRoll > 0) roll -= MathF.Abs(mover.RateRoll) * .25f * mover.RateRoll * mover.Turn.Mass / (tm * mover.TorqueRoll);
        float r = roll / (dt * mover.MaxRoll);
        float d2 = controls.Pitch * controls.Pitch + controls.Yaw * controls.Yaw;
        return d2 + r * r > 1 ? MathF.CopySign(MathF.Sqrt(1 - MathF.Min(d2, 1)), r) : r;
    }

    /// <summary>Stationary-obstacle tangent diversion common.cpp:2136-2226.
    /// Native random fallback is unreachable with nonzero separation; degenerate
    /// coincident centres use +X deterministically rather than a global RNG.</summary>
    public static Vector3 Divert(in Mover mover, in Vector3 path, ReadOnlySpan<Obstacle> obstacles, int skip)
    {
        float distance2 = path.LengthSquared(), closestDistance = distance2;
        Obstacle block = default;
        bool found = false;
        foreach (var o in obstacles)
        {
            if (o.Ignore || o.Key == skip || o.Kind == Kind.Point || o.Kind == MineKind || o.Kind == Kind.Ship && o.Velocity.LengthSquared() >= 1) continue;
            Vector3 dp = o.Position - mover.Position;
            float d2 = dp.LengthSquared(), dot = dp * path;
            if (d2 >= closestDistance || dot < 0 || distance2 == 0) continue;
            float r = mover.Radius + 2 + o.Radius + 10;
            if ((path * (dot / distance2) - dp).LengthSquared() >= r * r) continue;
            closestDistance = d2; block = o; found = true;
        }
        if (!found) return path;
        Vector3 displacement = block.Position - mover.Position;
        Vector3 offset = path * ((displacement * path) / distance2) - displacement;
        Vector3 cross = offset.LengthSquared() >= .5f
            ? Vector3.CrossProduct(displacement, Vector3.CrossProduct(offset, displacement))
            : ShipDodgeLaw.OrthogonalVector(displacement);
        cross = cross.LengthSquared() > 0 ? cross.Normalize() : new Vector3(1, 0, 0);
        double radius = mover.Radius + 2 + block.Radius + 10;
        double sin2 = radius * radius / displacement.LengthSquared();
        if (sin2 >= .99) return cross * (float)radius;
        Vector3 tangent = displacement + cross * (float)Math.Sqrt(radius * radius / (1 - sin2));
        Vector3 pca = tangent * ((displacement * tangent) / tangent.LengthSquared());
        return pca + (pca - displacement).Normalize() * (float)(radius * .02);
    }
    private static bool MinefieldAhead(in Mover mover, in Vector3 path, float distance2, float distanceRest, ReadOnlySpan<Obstacle> obstacles)
    {
        foreach (var o in obstacles)
        {
            if (o.Ignore || o.Kind != MineKind) continue;
            Vector3 dp = o.Position - mover.Position;
            float d2 = dp.LengthSquared(), dot = dp * path, r = mover.Radius + 2 + o.Radius;
            if (d2 < distance2 && dot >= 0 && d2 <= (r + distanceRest) * (r + distanceRest) &&
                (path * (dot / distance2) - dp).LengthSquared() < r * r) return true;
        }
        return false;
    }
}
