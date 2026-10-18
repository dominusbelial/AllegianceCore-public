using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using Cfg = Allegiance.Server.ServerConfig;
using Gm = Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P650ServerRulesTests
{
    private static Cfg Config(string map = "XXV_simple.igc") => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P650 local battle", Map = map, Teams = 2, CivIds = [25, 3], SideNames = ["A", "B"] }]
    };

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Warp_exit_uses_loaded_slot_and_preserves_entry_speed()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        var m = server.Missions[0]; m.Core.Constants.FloatConstants[3] = 123;
        var warp = m.Warps.First(w => w.Partner() is not null && w.Destination is not null);
        var sector = m.Sectors.First(s => s.Warps.Contains(warp));
        var ship = new Ship(30000, m.Core.HullTypes.Values.First(), Vector3.Zero, new Vector3(0, 0, 17), Orientation.Identity);
        m.PlaceShip(ship, sector);
        server.Host.HitWarpEvent(ship, warp);
        Assert.Equal(warp.Destination, ship.Sector);
        Assert.Equal(140, ship.Velocity.Length(), 3);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Station_launch_over_loopback_uses_loaded_exit_speed()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors.First(s => s.Stations.Any(st => st.SideId >= 0));
            var st = sector.Stations.First(st => st.SideId >= 0);
            var hull = core.HullTypes.Values.First() with { Thrust = 0.01f, Speed = 1000, Mass = 100 };
            var ship = new Ship(id, hull, DockTestPlacement.Inside(mission, st, hull), Vector3.Zero, Orientation.Identity) { SideId = st.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config()); var m = server.Missions[0]; m.Stage = MissionStage.Started;
        m.Core.Constants.FloatConstants[4] = 123;
        using var bot = new Allegiance.Bot.Bot(); bot.Connect(server.BoundPort, "p650launch");
        bot.SendLogonRequest(new LogonRequest
        {
            CharacterName = "p650launch", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 1, DwCookie = 1, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p650"
        });
        void Until(Func<bool> done)
        {
            for (int i = 0; i < 4000 && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
            Assert.True(done());
        }
        Until(() => bot.ShipAssignment is not null);
        var live = server.Ships.Single(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
        Until(() => live.StationId is not null);
        bot.SendDocked(); Until(() => live.StationId is null);
        Assert.Equal(123, live.Velocity.Length(), 2);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Local_mining_battle_reports_income_and_leaves_drained_rocks()
    {
        Require.Artwork();
        foreach (bool oldRate in new[] { true, false })
        {
            using var server = new Srv(); server.Boot(Config("m2xspiderw.igc"));
            var m = server.Missions[0]; m.Stage = MissionStage.Started;
            if (oldRate) m.Core.Constants.FloatConstants[14] = 0.025f;
            var miners = m.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner).ToList();
            Assert.Equal(2, miners.Count);
            var prev = miners.ToDictionary(s => s.Id, s => s.Ore);
            float credits = 0;
            for (int i = 0; i < 24000; i++) // 1200 sim seconds; real server/miner/warp/dock loops
            {
                server.TickSim();
                foreach (var miner in miners)
                {
                    if (miner.Ore < prev[miner.Id]) credits += prev[miner.Id] * miner.EffectiveOreCreditPerUnit();
                    prev[miner.Id] = miner.Ore;
                }
            }
            Console.WriteLine($"P650-INCOME rate={m.Core.Constants.FloatConstants[14]:G9}: sold={credits:F2} credits in {m.LastUpdate:F2}s = {credits / (float)m.LastUpdate:F4} cr/s (two miners)");
            Assert.True(credits > 0);
            var drilling = miners.FirstOrDefault(s => s.MiningAsteroidTarget is not null && s.IsMining);
            for (int i = 0; i < 12000 && drilling is null; i++)
            { server.TickSim(); drilling = miners.FirstOrDefault(s => s.MiningAsteroidTarget is not null && s.IsMining); }
            Assert.NotNull(drilling);
            if (oldRate) continue;
            // A small depleted rock exposes the low regeneration supply limit;
            // large stock rocks can still regrow faster than a single miner.
            var previousRock = drilling.MiningAsteroidTarget!;
            var tapped = new Asteroid(previousRock.Data with { Ore = 0, OreMax = 100 });
            var rockSector = m.Sectors.Single(s => s.Id == previousRock.Data.ClusterId);
            rockSector.RemoveAsteroid(previousRock);
            m.PlaceAsteroid(tapped, rockSector);
            drilling.MiningAsteroidTarget = tapped;
            for (int i = 0; i < 20 && drilling.MiningAsteroidTarget == tapped; i++) server.TickSim();
            Assert.NotSame(tapped, drilling.MiningAsteroidTarget);
            Assert.True(drilling.MiningAsteroidTarget is not null || drilling.PlanTarget is not null,
                "a drained miner must pick a new rock or return its partial load");
            Console.WriteLine($"P650-TAPPED rate={m.Core.Constants.FloatConstants[14]:G9}: miner={drilling.Id} left rock={tapped.Data.AsteroidId}; nextRock={drilling.MiningAsteroidTarget?.Data.AsteroidId}, unload={drilling.PlanTarget?.StationId}");
        }
    }
}
