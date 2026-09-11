using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P158 — pure-domain (no sockets) proofs of the station cross-cluster search,
/// <see cref="Ship.FindNearestFriendlyStationAnyCluster"/>. Unlike the asteroid
/// sibling (bare sectors — <see cref="AnyClusterAsteroidSearchTests"/>), the
/// station search needs a real <see cref="Mission"/> (its per-sector core
/// resolves station types via <c>mission.Core.StationTypes</c>), so these tests
/// assemble a real mission from <c>XXV_simple.igc</c>, place the ship in the
/// P111-documented no-station fly sector <c>1052</c> (guaranteeing an empty
/// own sector), CLEAR that sector's real map warps via reflection and attach only
/// a single synthetic warp to a chosen target sector — making the reachable graph
/// exactly one edge and every outcome deterministic regardless of map churn.
/// </summary>
public sealed class AnyClusterStationSearchTests
{
    /// <summary><c>XXV_simple.igc</c>: the no-station fly sector (P111 report).</summary>
    private const short FlySector = 1052;

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap DemoMap()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("XXV_simple.igc not in corpus");
        return MissionMap.Load(IgcReader.ParseFile(path));
    }

    private static Mission NewMission(StaticCore core)
    {
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, DemoMap(), [new SideSpec(0, "Team 1", civ)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static readonly FieldInfo WarpsField =
        typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Replace a sector's warp list wholesale with the given warps — the
    /// deterministic-connectivity trick: the no-station fly sector's real map
    /// warps are discarded so the reachable graph is exactly what the test
    /// authors (one synthetic edge), immune to map topology.</summary>
    private static void SetWarps(Sector s, params ResolvedWarp[] warps)
    {
        var list = (List<ResolvedWarp>)WarpsField.GetValue(s)!;
        list.Clear();
        list.AddRange(warps);
    }

    private static DataWarpIGC Warp(short id, short clusterId, Vector3 pos) => new(
        WarpId: id, DestinationId: (short)(id + 1000), Radius: 50, TextureName: "", IconName: "",
        Name: $"W{id}", Position: [pos.X, pos.Y, pos.Z], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Signature: 1f, ClusterId: clusterId);

    private static DataStationTypeIGC UnloadType(StaticCore core) =>
        core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);

    private static void PlaceFriendlyUnload(Mission m, Sector sector, short id, Vector3 pos)
    {
        var core = m.Core;
        var type = UnloadType(core);
        var data = new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: 0, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: $"SELL_{id}");
        m.PlaceStation(new Station(data, type), sector);
    }

    private static Ship NewFriendlyShip(Mission m, Sector sector)
    {
        var ship = new Ship(9001, m.Core.HullTypes.Values.OrderBy(h => h.HitPoints).First(),
                            Vector3.Zero, Vector3.Zero, Orientation.Identity);
        ship.SideId = 0;
        m.PlaceShip(ship, sector);   // gives it Mission + Sector
        return ship;
    }

    private static Sector Fly(Mission m) =>
        m.Sectors.FirstOrDefault(s => s.Id == FlySector)
        ?? throw new InvalidOperationException($"XXV_simple has no sector {FlySector}");

    /// <summary>⭐ Cross-sector friendly unload found: the ship's own sector has
    /// none (hop 0 <c>null</c> — the same-sector search agrees), but a friendly
    /// unload one synthetic warp away is found by the widening search.</summary>
    [Fact]
    public void Finds_cross_sector_friendly_unload_when_own_sector_has_none()
    {
        var core = Core();
        var mission = NewMission(core);
        var a = Fly(mission);                                          // no-station fly sector (1052)
        var b = mission.Sectors.First(s => s.Id != a.Id);              // a distinct target sector
        SetWarps(a, new ResolvedWarp(Warp(10, a.Id, new Vector3(100f, 0f, 0f)), b));

        PlaceFriendlyUnload(mission, b, id: 9701, new Vector3(200f, 0f, 0f));

        var ship = NewFriendlyShip(mission, a);

        Assert.Null(ship.FindNearestFriendlyStation(DataStationTypeIGC.SabmUnload));   // own sector: none
        var found = ship.FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload);
        Assert.True(found.HasValue, "a cross-sector friendly unload must be found");
        Assert.Equal(9701, found.Value.StationId);
    }

    /// <summary>⭐ Hop 0 wins: a same-sector friendly unload is returned over a
    /// cross-sector one, even when the cross-sector one is "closer" by raw
    /// coordinate distance (meaningless across frames; BFS order is the bar).</summary>
    [Fact]
    public void Own_sector_unload_beats_a_cross_sector_one()
    {
        var core = Core();
        var mission = NewMission(core);
        var a = Fly(mission);
        var b = mission.Sectors.First(s => s.Id != a.Id);
        SetWarps(a, new ResolvedWarp(Warp(10, a.Id, new Vector3(100f, 0f, 0f)), b));

        PlaceFriendlyUnload(mission, a, id: 9801, new Vector3(500f, 0f, 0f));   // own, far in raw distance
        PlaceFriendlyUnload(mission, b, id: 9802, new Vector3(0f, 0f, 0f));     // cross, "near"

        var ship = NewFriendlyShip(mission, a);

        var found = ship.FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload);
        Assert.True(found.HasValue);
        Assert.Equal(9801, found.Value.StationId);
    }

    /// <summary>⭐ Nothing qualifying reachable → <c>null</c>: a same-sector
    /// ENEMY unload is excluded by the friendly filter, and with the reachable
    /// graph reduced to just that sector there is nothing else — the widening
    /// search respects the same per-sector filters as the same-sector one.</summary>
    [Fact]
    public void Returns_null_when_no_friendly_unload_is_reachable()
    {
        var core = Core();
        var mission = NewMission(core);
        var a = Fly(mission);
        SetWarps(a);                                                    // reachable graph = just sector 1052

        // An enemy-side (side 1) unload in the ship's own sector — excluded.
        var type = UnloadType(core);
        var data = new DataStationIGC(
            Position: [50f, 0f, 0f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: a.Id, SideId: 1, StationId: 9851,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: "ENEMY");
        mission.PlaceStation(new Station(data, type), a);

        var ship = NewFriendlyShip(mission, a);

        Assert.Null(ship.FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload));
    }
}
