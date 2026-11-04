using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Allegiance.Tools.P925;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

public sealed class P925HomeGeometryTests(ITestOutputHelper output)
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Fixture_pins_generated_home_rocks_and_preserves_every_authored_rock()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "m2xspiderw.igc")));
        using var mission = Mission.Assemble(core, map, [new SideSpec(0, "A", 18), new SideSpec(1, "B", 4)], new NullIgcSite());
        mission.ReplaceUnavailableStationTypes();
        FixtureMap.PinRocks(mission, map);
        var first = mission.Sectors.SelectMany(s => s.Asteroids).SelectMany(a => a.Encode()).ToArray();
        Assert.All(map.Asteroids, authored => Assert.Contains(mission.Sectors.SelectMany(s => s.Asteroids),
            rock => rock.AsteroidId == authored.AsteroidId && rock.Encode().SequenceEqual(authored.Encode())));
        Assert.All(mission.Sectors.Where(s => s.Cluster.HomeSector), home => Assert.NotEmpty(home.Asteroids));
        FixtureMap.PinRocks(mission, map);
        Assert.Equal(first, mission.Sectors.SelectMany(s => s.Asteroids).SelectMany(a => a.Encode()).ToArray());
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Logged_pod_poses_dock_without_a_control_change_and_replay_deterministically()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        using var source = typeof(P925HomeGeometryTests).Assembly.GetManifestResourceStream(
            "Allegiance.ClientCore.Tests.Fixtures.P925Pods.json")!;
        using var fixtures = JsonDocument.Parse(source);
        Assert.Equal(24, fixtures.RootElement.GetArrayLength());
        foreach (var pose in fixtures.RootElement.EnumerateArray())
        {
            var baseline = PodReplay.Run(core, art, pose, false);
            var repeat = PodReplay.Run(core, art, pose, false);
            Assert.True(baseline.Docked, $"{baseline.Ship}: {baseline.Stage}");
            Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(repeat));
            output.WriteLine($"ship={baseline.Ship} pod={baseline.Pod} home={baseline.Home} dock={baseline.Seconds:F2}s");
        }
    }

    [Fact]
    public void Navigation_receipt_handles_missing_pose_and_target_without_inventing_geometry()
    {
        using var session = new Allegiance.ClientCore.Session.ConnectionSession();
        var line = BotManager.HomeNavigationReceipt(session, 12, 99, "target");
        Assert.Contains("ship=12 edge=target target=99 type=-1", line);
        Assert.Contains("targetSector=-1 center=unknown", line);
        Assert.Contains("position=unknown velocity=unknown", line);
        Assert.Contains("forward=unknown up=unknown turnRates=unknown", line);
        Assert.Empty(session.World.Ships);
        Assert.Empty(session.World.Stations);
    }
}
