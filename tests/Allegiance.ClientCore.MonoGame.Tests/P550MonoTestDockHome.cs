using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P550 — a synthetic <c>SabmLand|SabmCapLand</c>-capable friendly "home"
/// station for MonoGame live tests that must dock a CAPITAL (non-fighter, e.g.
/// hull 792 'Adv Devastator'). The native dock gate (<c>shipIGC.cpp:725-727</c>)
/// refuses a non-fighter at a station lacking <c>c_sabmCapLand</c>, and the map
/// garrison (type 15, <c>ss24a</c>) is not SabmCapLand — so a live test that
/// docks a capital at "the first friendly station" can no longer use the
/// garrison. This injects a CapLand home (model absent from the corpus → the
/// <c>DockRadius</c> fallback, not geometry) owned by the same side and returns
/// it so the spawner docks ships a small offset from its centre. Idempotent per
/// (side, sector). Twin of the Core.Tests helper <c>P550TestDockHome</c> (test
/// assemblies don't reference one another).
/// </summary>
public static class P550MonoTestDockHome
{
    public static DataStationIGC CapLandHome(Mission mission, short sideId)
    {
        var sector = mission.Sectors[0];
        foreach (var st in sector.Stations)
            if (st.SideId == sideId && st.Name is not null
                && st.Name.StartsWith("CAPHOME", StringComparison.Ordinal))
                return st;

        var baseType = mission.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmCapLand));
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
