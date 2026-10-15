using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.TestInfra;

/// <summary>Explicit shop fixtures for tests of mechanics other than tech research.
/// H-06 still requires a real dock; H-07 still checks the actual hardpoint.
/// Native purchase: shipIGC.h:992-1034.</summary>
internal static class EconomyLoadoutTestSetup
{
    public static DataHullTypeIGC WeaponHull(StaticCore core)
    {
        var weapon = core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon).OrderBy(p => p.PartId).First();
        return core.HullTypes.Values.Where(h => h.GroupId >= 0 && h.MaxAmmo > 0 && h.CanMountPart(weapon, 0)).OrderBy(h => h.HullId).First();
    }

    public static DataHullTypeIGC Sell(Mission mission, DataHullTypeIGC hull)
    {
        var sellable = hull with { TtbmRequired = new byte[50], SuccessorHullId = -1 };
        ((Dictionary<short, DataHullTypeIGC>)mission.Core.HullTypes)[hull.HullId] = sellable;
        return sellable;
    }

    public static DataPartTypeIGC Sell(Mission mission, DataPartTypeIGC part)
    {
        var sellable = part with { TtbmRequired = new byte[50], SuccessorPartId = -1 };
        ((Dictionary<short, DataPartTypeIGC>)mission.Core.PartTypes)[part.PartId] = sellable;
        return sellable;
    }
}
