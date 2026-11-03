using System.Reflection;
using Allegiance.Bot;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
[Trait("Tier", "Live")]
public sealed class P923SeenWarpRoutingLiveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loopback_miner_flies_known_detour_only_when_seen_routing_enabled(bool enabled)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            LiveSettings = new() { SeenWarpRouting = enabled, TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
            Games = [new() { Name = "P923", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0];
        Assert.Equal(enabled, mission.SeenWarpRoutingEnabled);
        // Deliberately exercise the old BFS caller: the production default
        // native planner already gates CanSee with this new flag off (P875).
        // Pin the mission too because the rig-baseline env overrides config.
        mission.NativeGotoPlanEnabled = false;
        using var peer = new Bot.Bot();
        peer.Connect(server.BoundPort, "P923-observer");
        peer.SendLogonRequest(new LogonRequest { CharacterName = "P923-observer", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", CrcFileList = -1,
            DwCookie = uint.MaxValue, SteamAuthTicket = new byte[16], SteamID = 923 });
        for (int i = 0; i < 4000 && peer.ShipAssignment is null; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.NotNull(peer.ShipAssignment);

        var sectors = mission.Sectors.Take(4).ToArray();
        foreach (var warp in mission.Warps.ToArray()) mission.DeleteWarp(warp);
        foreach (var sector in mission.Sectors)
            ((List<ResolvedWarp>)typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sector)!).Clear();
        void Edge(int from, int to, short id, short partner, float x, bool known)
        {
            var data = DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with
            { WarpId = id, DestinationId = partner, ClusterId = sectors[from].Id, Radius = 40,
                Signature = 1, Position = [x, 25000, 0], Forward = [1, 0, 0] };
            var warp = new ResolvedWarp(data, sectors[to]);
            mission.AddWarp(warp);
            ((List<ResolvedWarp>)typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sectors[from])!).Add(warp);
            if (known) warp.SideVisibility.SetSideVisibility(0, true);
        }
        Edge(0, 3, 30000, 30001, 8000, false); Edge(3, 0, 30001, 30000, 8000, false);
        Edge(0, 1, 30002, 30003, -8000, true); Edge(1, 0, 30003, 30002, 8000, true);
        Edge(1, 2, 30004, 30005, -8000, true); Edge(2, 1, 30005, 30004, 8000, true);
        Edge(2, 3, 30006, 30007, -8000, true); Edge(3, 2, 30007, 30006, -8000, true);
        mission.Stage = MissionStage.Started;
        var miner = server.SpawnNpcShip(mission, sectors[0], 0, new(0, 25000, 0));
        Assert.Equal(Ship.PilotTypeMiner, miner.PilotType);
        var targetRock = sectors[3].Mineable.First(r => r.CanMineHe3);
        targetRock.SideVisibility.SetSideVisibility(0, true);
        miner.MiningAsteroidTarget = targetRock;
        int expectedWarp = enabled ? 30002 : 30000;
        var expectedSector = enabled ? sectors[1] : sectors[3];
        var route = new List<short> { sectors[0].Id };
        bool steered = false;
        // Prove the first actual transit selects the three-hop detour rather
        // than the direct shortcut. Later mineral decisions/territory changes
        // are independent of which first warp the pathfinder selected.
        for (int i = 0; i < 30000 && miner.Sector == sectors[0] && !miner.Dead; i++)
        {
            var before = miner.Sector;
            server.TickSim();
            if (before == sectors[0] && miner.Sector == sectors[0] && miner.IntendedNextWarpId >= 0)
            {
                Assert.Equal(expectedWarp, miner.IntendedNextWarpId);
                steered = true;
            }
            if (i % 100 == 0) Thread.Sleep(1);
            if (miner.Sector is { } current && route[^1] != current.Id) route.Add(current.Id);
        }
        Assert.True(steered);
        Assert.False(miner.Dead);
        Assert.Same(expectedSector, miner.Sector);
        Assert.Equal(expectedSector.Id, route[1]);
        Assert.Equal(new[] { sectors[0].Id, expectedSector.Id }, route);
        output.WriteLine($"P923 live flag={enabled}: miner route {string.Join("->", route)}, firstWarp={expectedWarp}, speed={miner.Velocity.Length():F1}");
    }
}
