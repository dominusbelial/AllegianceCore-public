using System.Text.Json;
using Allegiance.Server;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P733ResearchTimeTests
{
    [Theory]
    [InlineData("{}")] [InlineData("{\"showcase\":{}}")]
    public void Json_omissions_keep_native_scale(string json)
    { Assert.Equal(1, JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)!.Showcase.ResearchTimeScale); }

    [Theory]
    [InlineData(0)] [InlineData(65)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void Invalid_scale_fails_before_listening(double scale)
    {
        using var server = new Allegiance.Server.Server();
        Assert.Contains("showcase.researchTimeScale", Assert.Throws<InvalidDataException>(() => server.Boot(new()
        { Showcase = new() { ResearchTimeScale = scale } })).Message);
        Assert.Equal((ushort)0, server.BoundPort);
    }

    [Fact]
    public void Finale_request_roundtrips_and_rejects_bad_frames()
    {
        var request = new ShowcaseFinaleRequest { Epoch = Guid.NewGuid(), Winner = 1 };
        var bytes = request.Encode(); var decoded = ShowcaseFinaleRequest.Decode(bytes);
        Assert.Equal(request.Epoch, decoded.Epoch); Assert.Equal(request.Winner, decoded.Winner);
        Assert.Throws<ArgumentException>(() => ShowcaseFinaleRequest.Decode(bytes.AsSpan(0, 21)));
        bytes[2] = 0;
        Assert.Throws<ArgumentException>(() => ShowcaseFinaleRequest.Decode(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(3601)] [InlineData(double.NaN)]
    public void Invalid_finale_bound_fails_validation(double seconds) =>
        Assert.Contains("showcase.finaleTimeoutSeconds", Assert.Throws<InvalidDataException>(() =>
            new ShowcaseConfig { FinaleTimeoutSeconds = seconds }.Validate()).Message);

    [SkippableTheory]
    [InlineData("research", 1, 1)] [InlineData("research", 8, 8)]
    [InlineData("unlocked", 8, 1)] [InlineData("unlocked", 1, 1)]
    public void Only_research_mode_accelerates_native_bucket_timers(string mode, double scale, double effective)
    {
        Require.Artwork();
        using var server = new Allegiance.Server.Server();
        server.Boot(new() { Port = 0, Showcase = new() { TechMode = mode, ResearchTimeScale = scale },
            Games = [new() { Teams = 2, CivIds = [25, 3], SideNames = ["A", "B"], Map = "m2xspiderw.igc" }] });
        var mission = server.Missions[0];
        Assert.Equal(effective, mission.ShowcaseResearchTimeScale);
        if (mode == "research")
            Assert.All(mission.Sides, side => Assert.Contains(mission.Sectors.SelectMany(s => s.StationRuntimes),
                st => st.SideId == side.Id && st.Type.StationTypeId == side.Civilization.InitialStationTypeId));
        else
        {
            var originalMap = Allegiance.Core.Model.MissionMap.Load(IgcReader.ParseFile(Path.Combine(
                ExternalPaths.Resolve(ExternalResourceKind.Artwork), "m2xspiderw.igc")));
            Assert.Equal(originalMap.Stations.Select(st => st.StationTypeId), mission.Sectors.SelectMany(s => s.Stations).Select(st => st.StationTypeId));
        }
        var bucket = mission.Sides[0].Buckets.First(b => b.Development.HasValue && b.TimeToBuild >= 30 && b.Price > 0);
        uint native = bucket.Development!.Value.TimeToBuild;
        uint original = bucket.TimeToBuild;
        bucket.Update(server.SimNow); bucket.AddMoney(bucket.Price);
        bucket.Update(server.SimNow + 1);
        Assert.Equal((long)(1000 * effective), bucket.InvestedMilliseconds);
        Assert.Equal(original, bucket.TimeToBuild);
        Assert.Equal(native, bucket.Development.Value.TimeToBuild);
        var sector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 0));
        var type = mission.Core.StationTypes.Values.First(st => st.AabmBuild != 0 && st.ConstructionDroneTypeId >= 0
            && mission.Core.DroneTypes.ContainsKey(st.ConstructionDroneTypeId));
        var rock = new Asteroid(new(1, [2000, 2000, 2000], [0,1,0], [1,0,0], [0,0,0,1], 0, 0,
            type.AabmBuild, 30000, 1000, 60,
            NativeAsteroidTypes.GetTypeDefaults(NativeAsteroidTypes.ResolveTypeIndex(type.AabmBuild)).ModelName,
            "", "", sector.Id, "P733 timer fixture", 255));
        mission.PlaceAsteroid(rock, sector);
        var builder = server.SpawnNpcShip(mission, sector, 0, new(2000, 2000, 2000), type.ConstructionDroneTypeId);
        var effect = sector.StartBuildingEffect(builder, rock, type, server.SimNow);
        Assert.Equal(30 / effective, effect.CompletionTime - server.SimNow, 8);
        Assert.Equal(30, BuildingEffect.BuildDuration);
    }
}
