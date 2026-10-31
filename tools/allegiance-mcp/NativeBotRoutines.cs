using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

// Typed C# routines, not a script interpreter. Native Pig.cpp:587-659 keeps
// suspended behavior instances and reactivates the previous one on Pop.
internal sealed class BotRoutineStack(string initial)
{
    private readonly List<BotRoutine> _frames = [new(initial)];
    internal BotRoutine Top => _frames[^1];
    internal int Count => _frames.Count;
    // Last dispatch already seen, kept across lives and role changes: native
    // Miner.cs only reacts to a dispatch as it arrives, never to history.
    internal object? LastBuoy;
    internal void Push(string behavior)
    {
        if (!BotRoutine.Valid(behavior)) throw new ArgumentException("Unknown bot behavior", nameof(behavior));
        _frames.Add(new(behavior));
    }
    internal void NewLife()
    {
        for (int i = 0; i < _frames.Count; i++) _frames[i] = new(_frames[i].Behavior);
    }
    internal bool Pop()
    {
        if (_frames.Count == 1) return false;
        _frames.RemoveAt(_frames.Count - 1);
        return true;
    }
}

internal sealed class BotRoutine(string behavior)
{
    internal string Behavior { get; } = behavior;
    internal static bool Valid(string value) => value is "idle" or "weave" or "combat" or "miner" or "docker" or "deathmatch" or "chatter";
    internal readonly OrderedMinerRoutine Miner = new();
    internal readonly DockerRoutine Docker = new();
    internal readonly DeathmatchRoutine Deathmatch = new();
    internal readonly RoutineTimer Chat = new();
    internal long ChatCursor;
    internal double LaunchAt = double.NegativeInfinity;
    internal QueuedCommandState? SavedOrder;
    internal short? SavedTarget;
    internal short? SavedOrderedTarget;
}

// PigTimer.cpp:37-95,145-150: strict expiry, coalesced missed intervals,
// finite/infinite repetitions, resetting interval and explicit cancellation.
internal sealed class RoutineTimer
{
    private double _interval, _next;
    private int _remaining;
    internal void Set(double now, double interval, int repetitions = -1)
    {
        if (interval < 0 || repetitions == 0 || repetitions < -1) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval; _next = now + interval; _remaining = repetitions;
    }
    internal void Kill() => _remaining = 0;
    internal bool Pulse(double now)
    {
        if (_remaining == 0 || now <= _next) return false;
        if (_interval > 0) _next += Math.Ceiling((now - _next) / _interval) * _interval;
        if (_remaining > 0) _remaining--;
        return true;
    }
}

internal sealed class OrderedMinerRoutine
{
    internal short? Cluster { get; private set; }
    internal short Command { get; private set; }
    internal bool Returning { get; private set; }
    // Wopr/Strategies/Miner.cs:141-179: identical flying dispatch is a no-op;
    // docking permits a new dispatch of the very same destination.
    internal bool Dispatch(short cluster, short command, bool docked)
    {
        if (!docked && Cluster == cluster && Command == command) return false;
        Cluster = cluster; Command = command; Returning = false;
        return true;
    }
    // Miner.cs:244-284: strict absolute ore > 80, nearest friendly station.
    internal bool ShouldReturn(float ore, bool docked) => !docked && ore > 80;
    internal void Return() => Returning = true;
}

internal enum DockerAction { Wait, Face, Fly, Dock }
internal sealed class DockerRoutine
{
    private readonly RoutineTimer _timer = new();
    private int _phase;
    internal short? Rock;
    internal void Flying(double now) { _phase = 0; _timer.Set(now, 3, 1); Rock = null; }
    // docker.pig:36-83: 3s, face a random rock, throttle 80%, then stop
    // after 80-Random()%60 seconds; repeat after 120-Random()%15 seconds.
    internal DockerAction Tick(double now, bool faced, Func<int, int> random)
    {
        if (_phase == 1)
        {
            if (!faced) return DockerAction.Face;
            _phase = 2; _timer.Set(now, 80 - random(60), 1);
        }
        if (_timer.Pulse(now))
        {
            if (_phase is 0 or 3) { _phase = 1; Rock = null; return DockerAction.Face; }
            _phase = 3; _timer.Set(now, 120 - random(15), 1); return DockerAction.Dock;
        }
        return _phase == 2 ? DockerAction.Fly : DockerAction.Wait;
    }
}

internal static class PigFaceRoutine
{
    // PigShipEvent.cpp:234-236,253-296; Igc/common.cpp:152-251 (skill=1).
    internal static (ControlData Controls, bool Complete) Face(Vector3 delta, Orientation orientation,
        float radius, DockApproach.TurnState hull, float speed, float dt)
    {
        float length = delta.Length();
        if (length == 0) return (ControlData.Neutral, true);
        var direction = delta / length;
        var local = orientation.TimesInverse(direction);
        float cosine = Math.Clamp(-local.Z, -1, 1);
        if (cosine <= -.999f) return (new(1, 0, 0, -1), MathF.PI <= radius * .25f / length);
        float angle, yaw, pitch;
        if (cosine < .98f)
        {
            angle = MathF.Acos(cosine);
            var twist = orientation.TimesInverse(Vector3.CrossProduct(orientation.GetBackward(), direction).Normalize());
            yaw = -twist.Y * angle; pitch = twist.X * angle;
        }
        else
        {
            yaw = MathF.Acos(Math.Clamp(local.X, -1, 1)) - MathF.PI / 2;
            pitch = MathF.Acos(Math.Clamp(local.Y, -1, 1)) - MathF.PI / 2;
            angle = MathF.Sqrt(yaw * yaw + pitch * pitch);
        }
        float fraction = hull.MaxSpeed > 0 ? speed / hull.MaxSpeed : 0;
        float tm = .5f + fraction / (fraction + 1);
        float Axis(float error, float rate, float torque, float maximum) =>
            dt > 0 && maximum > 0 ? (error - (torque > 0 ? MathF.Abs(rate) * .5f * rate * hull.Mass / (tm * torque) : 0)) / (dt * maximum) : 0;
        yaw = Axis(yaw, hull.RateYaw, hull.TorqueYaw, hull.MaxYaw);
        pitch = Axis(pitch, hull.RatePitch, hull.TorquePitch, hull.MaxPitch);
        float magnitude = MathF.Sqrt(yaw * yaw + pitch * pitch);
        if (magnitude > 1) { yaw /= magnitude; pitch /= magnitude; }
        return (new(yaw, pitch, 0, -1), angle <= radius * .25f / length);
    }
}

internal readonly record struct DeathmatchCandidate(short Id, float DistanceSquared, bool Pod);
internal sealed class DeathmatchRoutine
{
    private readonly RoutineTimer _find = new(), _update = new();
    private double _lastDamage = double.NegativeInfinity;
    internal readonly RoutineTimer Rearm = new();
    internal short? Target { get; private set; }
    internal bool Close { get; private set; }
    internal bool Reached { get; private set; }
    internal void Flying(double now, IReadOnlyList<DeathmatchCandidate> ships)
    {
        // dm.pig:65-96: the last enumerated enemy is the initial target.
        Target = ships.Count == 0 ? null : ships[^1].Id;
        Close = Reached = false;
        _find.Kill(); _update.Kill();
        if (Target is null) _find.Set(now, 3); else _update.Set(now, 1);
        Rearm.Set(now, 3);
    }
    internal bool Tick(double now, IReadOnlyList<DeathmatchCandidate> ships)
    {
        if (Target is null)
        {
            if (!_find.Pulse(now)) return false;
            Target = Nearest(ships)?.Id;
            if (Target is not null) { _find.Kill(); _update.Set(now, 1); }
            return true;
        }
        if (!_update.Pulse(now)) return false;
        var nearest = Nearest(ships);
        DeathmatchCandidate? current = null;
        foreach (var ship in ships) if (ship.Id == Target) current = ship;
        // dm.pig:120-174: retain a close valid target; reconsider a pod,
        // a missing target, or one beyond 1500m. No fleet engager cap/tiers.
        if (nearest is { } n)
        {
            if ((current is null || current.Value.Pod || current.Value.DistanceSquared > 2250000) && n.Id != Target)
            { Target = n.Id; return true; }
            Close = current is { DistanceSquared: <= 2250000 };
            if (Close) Reached = true;
        }
        else
        {
            Target = null; Close = false; _update.Kill(); _find.Set(now, 3);
        }
        return true;
    }
    // dm.pig:247-271 OnShipDamaged: a hit over 2s after the previous one adopts
    // the attacker only when it is also the nearest enemy, or there is no target.
    internal void Damaged(double now, short attacker, IReadOnlyList<DeathmatchCandidate> ships)
    {
        double delta = now - _lastDamage;
        _lastDamage = now;
        if (delta <= 2 || Nearest(ships) is not { } nearest) return;
        if (Target is not null && (nearest.Id != attacker || nearest.Id == Target)) return;
        Target = nearest.Id;
        _find.Kill(); _update.Set(now, 1); Rearm.Set(now, 3);
    }
    private static DeathmatchCandidate? Nearest(IReadOnlyList<DeathmatchCandidate> ships)
    {
        DeathmatchCandidate? best = null;
        foreach (var s in ships) if (!s.Pod && (best is null || s.DistanceSquared < best.Value.DistanceSquared)) best = s;
        return best;
    }
    internal bool MustRearm(float hull, float ammo, float fuel) => hull < .15f || ammo == 0
        || (!Close && (hull < .30f || ammo < .10f || (Reached && fuel < .10f)));
}

// chatter.pig:217-244 repeats this alphabet; include/eliza.js:1 is only
// a placeholder. The 30s manager-wide budget is P905's explicit spam limit.
internal sealed class ScriptedBotChat
{
    private double _next = double.NegativeInfinity;
    internal const string Text = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    internal bool TrySend(double now)
    {
        if (now < _next) return false;
        _next = now + 30;
        return true;
    }
}
