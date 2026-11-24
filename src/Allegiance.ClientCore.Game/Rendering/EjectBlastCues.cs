using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Local presentation for KILL_SHIP without death credit. Native adds
/// the old hull's blast before EJECT replaces it (clintlib/appmsg.cpp:2846-2857).
/// Call BeginFrame once per rendered frame, then observe only drawn hulls.
/// World state and the authoritative 207 path are never modified.</summary>
public sealed class EjectBlastCues
{
    private readonly record struct ShipSnapshot(SimVec Position, SimVec Velocity, bool Fighter, float Length, long Frame);
    private readonly Dictionary<short, ShipSnapshot> _ships = new();
    private readonly List<short> _staleShips = new();
    private readonly List<ExplosionEntity> _expired = new();
    private readonly Dictionary<ExplosionEntity, float> _pendingSounds = new();
    public Dictionary<ExplosionEntity, short> Explosions { get; } = new();
    private long _frame, _killSequence;
    private long _mission = -1, _view = -1;
    private short _sector = -1;

    public void Reset()
    {
        _ships.Clear(); _staleShips.Clear(); Explosions.Clear(); _expired.Clear(); _pendingSounds.Clear();
        _frame = _killSequence = 0;
        _mission = _view = -1; _sector = -1;
    }

    public void BeginFrame(ClientWorld world, short sector, long nowLocalMs)
    {
        if (_mission != world.MissionGeneration || _view != world.SectorGeneration || _sector != sector)
        {
            Reset(); _mission = world.MissionGeneration; _view = world.SectorGeneration; _sector = sector;
            _killSequence = world.Events.KillSequence;
        }
        _frame++;
        if (_killSequence != world.Events.KillSequence)
        {
            foreach (var kill in world.Events.KillsAfter(_killSequence))
                if (!kill.BDeathCredit && _ships.TryGetValue(kill.ShipId, out var ship) && ship.Frame == _frame - 1
                    && world.Ships.TryGetValue(kill.ShipId, out var present) && present.CanDrawInSector(sector))
                {
                    var blast = new ExplosionEntity { ClusterId = sector,
                        Type = ship.Fighter ? ExplosionType.SmallShip : ExplosionType.BigShip,
                        Position = ship.Position, Velocity = ship.Velocity, ReceivedLocalMs = nowLocalMs };
                    Explosions.Add(blast, kill.ShipId);
                    // Native uses 2 * model radius for SOUND scale, independently
                    // of the category's visual preset (WinTrek/trekigc.cpp:180-192).
                    _pendingSounds.Add(blast, ship.Length);
                }
            _killSequence = world.Events.KillSequence;
        }
        _staleShips.Clear();
        foreach (var entry in _ships) if (entry.Value.Frame < _frame - 1) _staleShips.Add(entry.Key);
        foreach (short id in _staleShips) _ships.Remove(id);
        _expired.Clear();
        foreach (var blast in Explosions.Keys)
            if (nowLocalMs - blast.ReceivedLocalMs >= ExplosionPresets.For(blast.Type).TotalDurationSeconds * 1000)
                _expired.Add(blast);
        foreach (var blast in _expired) { Explosions.Remove(blast); _pendingSounds.Remove(blast); }
    }

    public void ObserveShip(short id, SimVec position, SimVec velocity, DataHullTypeIGC? hull)
    {
        if (hull is { } type)
            _ships[id] = new(position, velocity, type.HasCapability(DataHullTypeIGC.HabmFighter), type.Length, _frame);
    }

    private static bool Replaced(ClientWorld world, ExplosionEntity blast)
    {
        // Preserve P1079's exact-overlap rule for hosts that supply the eject
        // blast themselves. Authoritative visuals and audio take precedence.
        foreach (var server in world.Explosions)
            if (server.ClusterId == blast.ClusterId && server.Type == blast.Type
                && Math.Abs(server.ReceivedLocalMs - blast.ReceivedLocalMs) < 1000
                && (server.Position - blast.Position).LengthSquared() < 1)
                return true;
        return false;
    }

    public void AppendExplosions(ClientWorld world, List<ExplosionEntity> destination)
    {
        foreach (var blast in Explosions.Keys)
            if (!Replaced(world, blast)) destination.Add(blast);
    }

    /// <summary>Drain each cue's sound request once, including when audio is
    /// unavailable. Uses the same ExplosionSoundRouter as server 207 sounds;
    /// tier thresholds are native WinTrek/trekigc.cpp:97-101.</summary>
    public void TakeSoundEvents(ClientWorld world, List<ExplosionSoundEntity> destination)
    {
        foreach (var (blast, scale) in _pendingSounds)
            if (!Replaced(world, blast))
                destination.Add(new(blast.Type, blast.ClusterId, blast.Position, scale));
        _pendingSounds.Clear();
    }
}
