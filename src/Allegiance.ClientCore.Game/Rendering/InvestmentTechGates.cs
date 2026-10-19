using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Client view of IsideIGC::CanBuy (Igc/sideigc.h:224-281),
/// and the development predecessor list (Igc/bucketigc.cpp:36-65).</summary>
public static class InvestmentTechGates
{
    // The six native tabs and their mission-path gates, including Show All:
    // WinTrek/loadout.cpp:1002,1042-1056. Hidden group 255 is never a tab.
    public static bool VisibleGroup(int group, ResearchSettings settings) => group switch
    {
        0 or 1 => true,
        2 => settings.AllowSupremacyPath,
        3 => settings.AllowTacticalPath,
        4 => settings.AllowExpansionPath,
        5 => settings.AllowShipyardPath,
        _ => false
    };

    public static bool CanBuy(StaticCore core, InvestmentBucket bucket, TechTreeBitMask developments,
        IReadOnlyList<DataStationTypeIGC> stations)
    {
        var side = developments;
        foreach (var station in stations) side = side.Or(TechTreeBitMask.FromBytes(station.TtbmEffects));
        byte[]? required = null;
        switch (bucket.ItemType)
        {
            case BucketItemType.BitDevelopment:
                if (!core.Developments.TryGetValue(bucket.DevelopmentId, out var dev)) return false;
                return dev.DevelopmentId == Mission.TeamMoneyDevelopmentId
                    || TechTreeBitMask.FromBytes(dev.TtbmRequired).IsSubsetOf(side);
            case BucketItemType.BitStationType:
                if (!core.StationTypes.TryGetValue(bucket.StationTypeId, out var station)) return false;
                if (core.StationTypes.TryGetValue(station.SuccessorStationTypeId, out var next)
                    && TechTreeBitMask.RequiredTechsBuyable(next.TtbmRequired, station.TtbmRequired, developments)) return false;
                required = station.TtbmRequired;
                break;
            case BucketItemType.BitDroneType:
                if (core.DroneTypes.TryGetValue(bucket.DroneTypeId, out var drone)) required = drone.TtbmRequired;
                break;
            case BucketItemType.BitShipType:
                if (core.HullTypes.TryGetValue(bucket.HullIdDrone, out var hull)) required = hull.TtbmRequired;
                break;
        }
        return required is not null && stations.Any(s => TechTreeBitMask.RequiredTechsBuyable(required, s.TtbmLocal, side));
    }

    public static IReadOnlyDictionary<short, short> Predecessors(IReadOnlyDictionary<short, DataDevelopmentIGC> developments, IReadOnlyList<InvestmentBucket> buckets)
    {
        var result = new Dictionary<short, short>();
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            if (!developments.TryGetValue(b.DevelopmentId, out var dev) || DevelopmentLogic.IsTechOnly(dev)
                || !TechTreeBitMask.FromBytes(dev.TtbmEffects).IsEmpty) continue;
            for (int j = i - 1; j >= 0; j--)
                if (developments.TryGetValue(buckets[j].DevelopmentId, out var previous)
                    && !DevelopmentLogic.IsTechOnly(previous) && previous.Price <= dev.Price
                    && TechTreeBitMask.FromBytes(previous.TtbmEffects).IsEmpty && previous.Gas.SequenceEqual(dev.Gas))
                {
                    result[b.Id] = buckets[j].Id;
                    break;
                }
        }
        return result;
    }
}
