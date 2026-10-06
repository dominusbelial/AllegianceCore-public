using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P550 — a synthetic <c>SabmLand|SabmCapLand</c>-capable friendly "home"
/// station for tests that must dock a CAPITAL (non-<c>c_habmFighter</c>) ship.
/// The native dock gate (<c>shipIGC.cpp:725-727</c>) refuses a non-fighter hull
/// at any station lacking <c>c_sabmCapLand</c>, and the standard map garrison
/// (type 15, <c>ss24a</c>) is <b>not</b> SabmCapLand — so a live test that docks
/// a capital (e.g. hull 792, Adv Devastator) at "the first friendly station" can
/// no longer use the garrison. This helper injects a CapLand home whose model is
/// deliberately ABSENT from the corpus (so the dock uses the <c>DockRadius</c>
/// fallback, not geometry), owned by the same side, and returns it so the test's
/// ShipSpawner docks its ships a small offset from its centre. Idempotent per
/// (side, sector): one home per side.
/// </summary>
public static class P550TestDockHome
{
    /// <summary>Injected homes live in the first sector, clear of the map's own
    /// stations, and carry a <c>CAPHOME_</c> name prefix so repeats are found.</summary>
    public static DataStationIGC CapLandHome(Mission mission, short sideId)
    {
        var sector = mission.Sectors[0];
        foreach (var st in sector.Stations)
            if (st.SideId == sideId && st.Name is not null
                && st.Name.StartsWith("CAPHOME", StringComparison.Ordinal))
                return st;

        var baseType = mission.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmCapLand));
        // ModelName deliberately absent from the corpus -> Bay()=null -> the dock
        // falls back to DockRadius proximity (deterministic; no geometry to be in).
        var type = baseType with
        {
            ModelName = "P550_NO_GEOM",
            SabmCapabilities = DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmCapLand,
        };
        var pos = new Vector3(0f, 0f, -1500f);
        short id = unchecked((short)(92000 + sideId));
        var data = new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: sideId, StationId: id, StationTypeId: type.StationTypeId,
            BpHull: 1, BpShield: 1, Name: $"CAPHOME_{sideId}");
        mission.PlaceStation(new Station(data, type), sector);
        return data;
    }
}
