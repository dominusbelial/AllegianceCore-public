using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P699bPodTelemetryTests
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("oxygen")]
    [InlineData("docked")]
    [InlineData("death")]
    [InlineData("rescued")]
    [InlineData("station-rescued")]
    public void Trip_keeps_authoritative_route_milestones_position_and_terminal_outcome(string outcome)
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var mission = Mission.Assemble(core, MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "m2xspiderw.igc"))),
            [new SideSpec(0, "Alpha", 25), new SideSpec(1, "Bravo", 25)], new NullIgcSite());
        var homeSector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 0));
        var home = homeSector.StationRuntimes.First(st => st.SideId == 0);
        var start = mission.Sectors.Single(s => s.Id == 1052);
        var pod = new Ship(123, core.HullTypes[444], Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(pod, start); pod.ChangeHull(core.HullTypes[444]);
        var lines = new List<string>(); var tracker = new PodTripTelemetry(lines.Add);
        tracker.Begin(pod, 0);
        Assert.Equal(1, tracker.ActiveCount);
        tracker.Sample(1);
        start.Remove(pod); mission.PlaceShip(pod, homeSector);
        pod.Position = home.Center + new Vector3(5000, 0, 0); tracker.Sample(5);
        // Station center is collision range, but outside real garage geometry.
        pod.Position = home.Center; tracker.Sample(10);
        var result = tracker.End(pod, 12, outcome)!;
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal((short)444, result.PodHull);
        Assert.Equal(pod.HullType.HabmCapabilities, result.PodCapabilities);
        Assert.Equal(home.Type.StationTypeId, result.StationType);
        Assert.Equal(home.Type.ModelName, result.StationModel);
        Assert.Equal(home.Radius, result.StationRadius);
        Assert.Equal(new float[] { home.Center.X, home.Center.Y, home.Center.Z }, result.StationPosition);
        Assert.Equal(new float[] { 0, 0, -1 }, result.StationForward);
        Assert.Equal(new float[] { 0, 1, 0 }, result.StationUp);
        Assert.Equal(new float[] { 0, 0, 0 }, result.EndVelocity);
        Assert.Equal(new float[] { 0, 0, -1 }, result.EndForward);
        Assert.Equal(new float[] { 0, 1, 0 }, result.EndUp);
        Assert.Equal(new float[] { 0, 0, 0 }, result.EndTurnRates);
        Assert.Equal(300, result.OxygenAllowance);
        Assert.Equal(5, result.HomeAt); Assert.Equal(10, result.RadiusAt); Assert.Null(result.BayAt);
        Assert.Equal(2, result.WaitedAtBay);
        Assert.Equal(new short[] { start.Id, homeSector.Id }, result.Route.Select(v => v.Sector));
        Assert.Equal(new float[] { home.Center.X, home.Center.Y, home.Center.Z }, result.EndPosition);
        Assert.Null(tracker.End(pod, 13, "docked")); // restart cannot be recounted
        Assert.Equal(0, tracker.ActiveCount);
        tracker.Flush(60);
        Assert.Contains($"{(outcome == "station-rescued" ? "rescued" : outcome)}=1", lines[^1].Replace("other=1", "death=1"));
        Assert.Contains("p95-trip-s=12.00", lines[^1]);
        tracker.Flush(120); Assert.Contains("docked=0 rescued=0 oxygen=0", lines[^1]);
    }
}
