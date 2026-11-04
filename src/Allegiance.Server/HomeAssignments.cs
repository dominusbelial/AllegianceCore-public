using Allegiance.Core.Model;

namespace Allegiance.Server;

/// <summary>P924 experiment policy, not a native gameplay rule. Reassign ownership
/// before research stations, miners, flags, visibility and player starts are derived.
/// Geometry, IDs, list order and the authored file remain unchanged.</summary>
internal static class HomeAssignments
{
    internal static bool Swapped(GameConfig game, int match, bool debugMode = false)
    {
        if (game.HomeSwap is not ("off" or "on" or "alternate"))
            throw new InvalidDataException("homeSwap must be off, on or alternate");
        if (game.HomeSwap != "off" && game.Teams != 2)
            throw new InvalidDataException("homeSwap requires exactly two teams");
        if (game.HomeSwap != "off" && debugMode)
            throw new InvalidDataException("homeSwap requires liveSettings.debugMode=false; synthetic debug homes override authored homes");
        return game.HomeSwap == "on" || (game.HomeSwap == "alternate" && match % 2 == 0);
    }

    internal static MissionMap Apply(MissionMap map, bool swapped) => !swapped ? map :
        MissionMap.FromLists(map.Clusters, map.Warps,
            map.Stations.Select(s => s.SideId is 0 or 1 ? s with { SideId = (short)(1 - s.SideId) } : s).ToArray(),
            map.Asteroids);

    internal static string Describe(GameConfig game, int match, bool swapped, MissionMap map) =>
        $" homeSwap={game.HomeSwap} homeMatch={match} homes={string.Join(',', Enumerable.Range(0, game.Teams).Select(s => swapped ? 1 - s : s))}"
        + string.Concat(Enumerable.Range(0, game.Teams).Select(side =>
            $" home{side}={string.Join(',', map.Stations.Where(s => s.SideId == side).Select(s => s.ClusterId).Distinct())}"));
}
