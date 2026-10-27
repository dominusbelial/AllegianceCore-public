using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P745BFinaleTests
{
    private static Mission World()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var m = Mission.Assemble(core, MissionMap.FromLists(
            [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
            [new SideSpec(0, "A", core.Civilizations.Keys.First()), new SideSpec(1, "B", core.Civilizations.Keys.First()),
                new SideSpec(2, "C", core.Civilizations.Keys.First())], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Station Base(Mission m, short id, short side, short capabilities)
    {
        var type = DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
        { StationTypeId = id, SabmCapabilities = capabilities, MaxArmorHitPoints = 100 };
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = id, SideId = side, ClusterId = 0, Position = [0, 0, 0], Name = $"Base {id}" }, type);
        m.PlaceStation(station, m.Sectors[0]); return station;
    }

    // Native fsmission.cpp:2348-2504: every earlier metric ties between A and B;
    // C must still be eligible to win the next metric (all-side restart).
    [SkippableTheory]
    [InlineData("conquest")] [InlineData("territory")] [InlineData("prosperity")]
    [InlineData("artifacts")] [InlineData("flags")] [InlineData("kills")]
    [InlineData("deaths")] [InlineData("draw")]
    public void Finale_uses_native_order_and_restarts_ties_across_all_sides(string deciding)
    {
        Require.Artwork(); var m = World();
        m.Goals.ConquestPercentage = 65; m.Goals.TerritoryPercentage = 50; m.Goals.TeamMoney = 1; m.Goals.Artifacts = 1; m.Goals.Flags = 1;
        string[] order = ["conquest", "territory", "prosperity", "artifacts", "flags", "kills", "deaths", "draw"];
        int stop = Array.IndexOf(order, deciding);
        short id = 10000;
        foreach (var side in m.Sides)
        {
            int Score(int metric) => metric < stop ? (side.Id < 2 ? 1 : 0) : metric == stop && side.Id == 2 ? 2 : 0;
            for (int i = 0; i < Score(0); i++) Base(m, id++, side.Id, DataStationTypeIGC.SabmFlag);
            side.TerritoryCount = (byte)Score(1);
            side.Buckets.First(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId).SetTimeAndMoney(Score(2) * 1000, 100);
            side.Artifacts = (short)Score(3); side.Flags = (short)Score(4);
            // Deaths break a kills tie; for draw both top sides tie on deaths too.
            side.Kills = (short)Score(5); side.Deaths = (short)(deciding == "deaths" ? (side.Id == 2 ? 0 : 3) : 0);
        }
        if (deciding == "deaths") foreach (var side in m.Sides) side.Kills = 1;
        m.CountdownDuration = 123; m.CountdownStart = 100;
        Assert.False(m.EvaluateCountdown(0).Expired);
        var result = m.EvaluateCountdown(0, deadlineReached: true);
        Assert.True(result.Expired);
        Assert.Equal(deciding is "deaths" or "draw" ? "kills" : deciding, result.Criterion);
        if (deciding == "draw") Assert.Null(result.Winner); else Assert.Same(m.Sides[2], result.Winner);
        Assert.Equal(123, m.CountdownDuration); Assert.Equal(100, m.CountdownStart);
        Assert.Contains("2:", m.CountdownValues(result.Criterion));
        m.Stage = MissionStage.Over; Assert.False(m.EvaluateCountdown(1000, true).Expired);
    }

    [SkippableFact]
    public void Objective_counts_restart_bases_only_and_clears_on_loss_build_capture_and_over()
    {
        Require.Artwork(); var m = World();
        var a = Base(m, 10001, 0, DataStationTypeIGC.SabmRestart);
        var b = Base(m, 10002, 0, DataStationTypeIGC.SabmRestart);
        Base(m, 10003, 0, DataStationTypeIGC.SabmFlag);
        Assert.Empty(m.LastRestartBases());
        m.Sectors[0].RemoveStation(a); Assert.Same(b, Assert.Single(m.LastRestartBases()));
        var replacement = Base(m, 10004, 0, DataStationTypeIGC.SabmRestart);
        Assert.Empty(m.LastRestartBases());
        replacement.Capture(1); Assert.Equal(2, m.LastRestartBases().Length);
        m.Sectors[0].RemoveStation(b); Assert.Equal((short)1, Assert.Single(m.LastRestartBases()).SideId);
        m.Sides[1].Active = false; Assert.Empty(m.LastRestartBases());
        m.Stage = MissionStage.Over; Assert.Empty(m.LastRestartBases());
    }
}
