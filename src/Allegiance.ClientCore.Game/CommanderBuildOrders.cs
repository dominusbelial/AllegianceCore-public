using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Carry one human Build intention across the port's legal Goto/Build
/// sequence. Native builder accepted orders route to their asteroid
/// (Igc/shipIGC.cpp:1936-1939; Igc/shipIGC.h:2080-2087); the port starts
/// construction only in the rock's cluster. No research/funding policy runs here.</summary>
public sealed class CommanderBuildOrders
{
    private readonly Dictionary<short, short> _travelling = new();
    private (long Generation, short? Side) _context = (-1, null);
    public void Cancel(short ship) => _travelling.Remove(ship);

    public short Begin(ClientWorld world, short ship, short rock)
    {
        Refresh(world);
        Cancel(ship);
        if (!world.Ships.TryGetValue(ship, out var builder) || !world.Asteroids.TryGetValue(rock, out var asteroid)
            || builder.SectorId == asteroid.ClusterId) return OrderChange.CidBuild;
        _travelling[ship] = rock;
        return OrderChange.CidGoto;
    }

    public IReadOnlyList<(short Ship, short Rock)> Tick(ClientWorld world)
    {
        Refresh(world);
        var ready = new List<(short Ship, short Rock)>();
        foreach (var (id, rock) in _travelling.ToArray())
        {
            if (!world.Ships.TryGetValue(id, out var ship) || ship.IsDead || ship.SideId != world.MySideId
                || !world.Asteroids.TryGetValue(rock, out var asteroid)) { Cancel(id); continue; }
            if (ship.SectorId != asteroid.ClusterId) continue;
            ready.Add((id, rock));
            Cancel(id);
        }
        return ready;
    }

    private void Refresh(ClientWorld world)
    {
        var context = (world.MissionGeneration, world.MySideId);
        if (_context == context) return;
        _context = context;
        _travelling.Clear();
    }
}
