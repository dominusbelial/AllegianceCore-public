using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Host = Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

/// <summary>
/// P845 — SIM-0638 <c>SideDevelopmentTechChange</c>: the station-successor half
/// of the native body (<c>FedSrv/FedSrv.CPP:11304-11313</c>). Native, when a
/// side's development techs change, upgrades every station the side owns in
/// place to its buyable successor station type — <c>SetName(successor-&gt;GetName())</c>
/// and <c>SetBaseStationType(successor)</c> — unconditionally (outside the
/// <c>Stage == Started</c> gate that guards only the SIDE_TECH_CHANGE broadcast).
/// This is <see cref="Side.UpgradeStationsToSuccessors"/>.
/// </summary>
public sealed class P845SideDevelopmentTechChangeTests
{
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));

    private static Mission World(Host site, StaticCore core) => Mission.Assemble(core,
        MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
        [new SideSpec(0, "A", core.Civilizations.Keys.First()), new SideSpec(1, "B", core.Civilizations.Keys.First())], site);

    /// <summary>A real core station type with a genuine (different) successor —
    /// the pair the native station loop upgrades a built station into.</summary>
    private static DataStationTypeIGC BaseWithSuccessor(StaticCore core)
        => core.StationTypes.Values.First(st =>
            st.SuccessorStationTypeId >= 0 && st.SuccessorStationTypeId != st.StationTypeId
            && core.StationTypes.ContainsKey(st.SuccessorStationTypeId));

    // FedSrv/FedSrv.CPP:11304-11313: upgrading to the successor sets the base
    // type AND the display name, fires StationTypeChange, and leaves the station
    // in place (same id/side/position).
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Side_development_tech_change_upgrades_owned_stations_to_successor_type()
    {
        Require.Artwork();
        var core = Core();
        var host = new Host();
        var mission = World(host, core);
        var side = mission.Sides[0];

        var baseType = BaseWithSuccessor(core);
        var expectedSuccessor = SuccessorUpgrade.StationSuccessor(core, baseType, TechTreeBitMask.FromBytes(
            core.StationTypes[baseType.SuccessorStationTypeId].TtbmRequired));
        Assert.NotEqual(baseType.StationTypeId, expectedSuccessor.StationTypeId);   // guard: pair must actually advance

        var station = new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 555, SideId = side.Id, ClusterId = 0, Position = [123, 456, 789], Name = "OldBase" },
            baseType);
        mission.PlaceStation(station, mission.Sectors[0]);

        // Baseline: not yet upgraded.
        Assert.Equal(baseType.StationTypeId, station.Type.StationTypeId);
        Assert.Equal("OldBase", station.Data.Name);

        var notified = new List<int>();
        host.StationTypeChanged += s => notified.Add(s.StationId);

        // Grant the successor's required techs, then fire the tech-change pass.
        side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(
            core.StationTypes[baseType.SuccessorStationTypeId].TtbmRequired));
        side.UpgradeStationsToSuccessors();

        // Upgraded to the effective (deepest reachable) successor: base type +
        // name replaced in place; identity/pose unchanged.
        Assert.Equal(expectedSuccessor.StationTypeId, station.Type.StationTypeId);
        Assert.Equal(expectedSuccessor.Name, station.Data.Name);
        Assert.Equal(555, station.StationId);
        Assert.Equal(side.Id, station.SideId);
        Assert.Equal(0, station.Data.ClusterId);
        Assert.Equal(new[] { 555 }, notified);   // native fires StationTypeChange once per upgraded station
    }

    // FedSrv/FedSrv.CPP:11306 — a station whose effective successor is unchanged
    // (no new dev bits unlock anything) is left untouched.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void No_dev_bits_unlocked_leaves_stations_unchanged()
    {
        Require.Artwork();
        var core = Core();
        var host = new Host();
        var mission = World(host, core);
        var side = mission.Sides[0];
        var baseType = BaseWithSuccessor(core);

        // A side with NO dev techs cannot unlock the successor → stays as-is.
        side.DevelopmentTechs = TechTreeBitMask.Empty;
        var station = new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 556, SideId = side.Id, ClusterId = 0, Position = [1, 2, 3], Name = "StillOld" },
            baseType);
        mission.PlaceStation(station, mission.Sectors[0]);
        var before = station.Type.StationTypeId;

        side.UpgradeStationsToSuccessors();

        Assert.Equal(before, station.Type.StationTypeId);
        Assert.Equal("StillOld", station.Data.Name);
    }

    // Scoped to the owning side: the other side's identical station type is not
    // upgraded by side 0's tech gain (FedSrv.CPP:11305 — iterate THIS side's stations).
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Only_the_tech_gaining_sides_stations_are_upgraded()
    {
        Require.Artwork();
        var core = Core();
        var host = new Host();
        var mission = World(host, core);
        var sideA = mission.Sides[0];
        var sideB = mission.Sides[1];
        var baseType = BaseWithSuccessor(core);

        var stA = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 557, SideId = sideA.Id, ClusterId = 0, Position = [1, 2, 3], Name = "A" }, baseType);
        var stB = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 558, SideId = sideB.Id, ClusterId = 0, Position = [4, 5, 6], Name = "B" }, baseType);
        mission.PlaceStation(stA, mission.Sectors[0]);
        mission.PlaceStation(stB, mission.Sectors[0]);

        sideA.DevelopmentTechs = sideA.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(
            core.StationTypes[baseType.SuccessorStationTypeId].TtbmRequired));
        sideA.UpgradeStationsToSuccessors();

        Assert.NotEqual(baseType.StationTypeId, stA.Type.StationTypeId);   // A upgraded
        Assert.Equal(baseType.StationTypeId, stB.Type.StationTypeId);       // B untouched
    }
}
