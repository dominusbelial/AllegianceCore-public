using Allegiance.ClientCore.State;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>selpane.cpp:81-120 binds the selected model; the values come from
/// consoledata.cpp:65-87,202-279,449-460. Read under ConnectionSession.Gate.</summary>
public static class SelectionPaneModel
{
    public readonly record struct Info(string? Name, float? Hull, float? Shield, bool? Enemy, float? Ore);

    public static Info? Build(ClientWorld world, IgcObjectType kind, short id)
    {
        if (kind == IgcObjectType.Ship)
        {
            if (!world.Ships.TryGetValue(id, out var ship) || ship.IsNotVisible || ship.IsGone || ship.IsDead) return null;
            return new(ScoreboardLayout.AsciiSafe(string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName),
                ship.Latest?.HullFraction, ship.Latest?.ShieldFraction,
                world.MySideId is { } mine ? ship.SideId != mine && !world.AreAllied(mine, ship.SideId) : null, null);
        }
        if (kind == IgcObjectType.Station && world.Stations.TryGetValue(id, out var station))
        {
            bool live = world.StationLive.TryGetValue(id, out var state);
            return new(ScoreboardLayout.AsciiSafe(station.Name), live ? state.HullFraction : null,
                live ? state.ShieldFraction : null, null, null);
        }
        if (kind == IgcObjectType.Asteroid && world.AsteroidLive.TryGetValue(id, out var asteroid))
            return new(null, asteroid.Fraction, null, null, asteroid.OreFraction);
        return new(null, null, null, null, null);
    }
}
