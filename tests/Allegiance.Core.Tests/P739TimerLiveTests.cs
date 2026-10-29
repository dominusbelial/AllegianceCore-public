using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using FltBot = Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P739TimerLiveTests
{
    private static ServerConfig Config() => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new() { Name = "A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] },
            new() { Name = "B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["C", "D"] }]
    };
    private static void Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(new()
        {
            CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 1, DwCookie = uint.MaxValue, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p739"
        });
        Until(server, () => bot.ShipAssignment is not null);
    }
    private static void Until(Srv server, Func<bool> condition)
    {
        for (int i = 0; i < 4000 && !condition(); i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(condition());
    }

    // FedSrv/FedSrv.CPP:12543-12562: loaded delay, native milliseconds and
    // real-side mission audience. Uses the real collision producer, host,
    // encoder, TCP transport, and bot decoder; cross-mission peer sees nothing.
    [SkippableTheory]
    [InlineData(5f)] [InlineData(7f)] [InlineData(10f)]
    public void Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(float delay)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        int spawned = 0;
        server.ShipSpawner = (core, _, id) =>
        {
            int n = spawned++;
            var mission = server.Missions[n == 2 ? 1 : 0];
            var ship = new Ship(id, core.HullTypes.Values.First(), new Vector3(80000, 0, 0), Vector3.Zero, Orientation.Identity)
                { SideId = (short)(n == 1 ? 1 : 0) };
            mission.PlaceShip(ship, mission.Sectors[0]); return (ship, mission.Sectors[0]);
        };
        server.Boot(Config());
        using var owner = new FltBot(); using var enemy = new FltBot(); using var elsewhere = new FltBot();
        Seat(server, owner, "p739owner"); Seat(server, enemy, "p739enemy"); Seat(server, elsewhere, "p739elsewhere");
        var m = server.Missions[0]; m.Stage = MissionStage.Started;
        using var warpBombDelay = new CoreConstantOverride(m, 32, delay);
        var launcher = server.Ships.First(s => s.Ship.Id == owner.ShipAssignment!.Ship.ShipId).Ship;
        var sector = m.Sectors.First(s => s.Warps.Count > 0); var warp = sector.Warps[0];
        var type = m.Core.MissileTypes.Values.First(t => t.HasWarpBombCapability) with { ReadyTime = 100, Lifespan = 200 };
        var missile = new Missile(30000, sector, type, new Vector3(warp.Warp.Position[0], warp.Warp.Position[1], warp.Warp.Position[2]),
            Vector3.Zero, new Vector3(0, 0, -1), m.LastUpdate, launcher);
        sector.Add(missile);
        // Record the mission clock on the tick that arms the bomb: waiting for both clients afterwards keeps
        // ticking the sim, so comparing against LastUpdate after the wait fails under machine load.
        double? armedAt = null;
        Until(server, () =>
        {
            if (armedAt is null && missile.WarpBombTime is not null) armedAt = m.LastUpdate;
            return owner.WarpBombs.Count > 0 && enemy.WarpBombs.Count > 0;
        });
        double captured = Assert.IsType<double>(missile.WarpBombTime);
        Assert.Equal((uint)(captured * 1000), owner.WarpBombs.Single().TimeExplosion);
        Assert.Equal(owner.WarpBombs.Single().TimeExplosion, enemy.WarpBombs.Single().TimeExplosion);
        Assert.Equal(warp.Warp.WarpId, owner.WarpBombs.Single().WarpIdBombed);
        Assert.Equal(type.ExpendableTypeId, owner.WarpBombs.Single().ExpendableIdMissile);
        Assert.InRange(captured - armedAt!.Value, delay - .1, delay);
        m.Core.Constants.FloatConstants[32] = 123; // later core edits cannot rewrite the event
        for (int i = 0; i < 40; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.Empty(elsewhere.WarpBombs); Assert.Single(owner.WarpBombs);
    }

    [SkippableFact]
    public void Non_arena_countdown_starts_when_a_connected_player_remains()
    {
        Require.Artwork(); var config = Config(); config.Games[0].AllowEmptyTeams = false;
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false; server.Boot(config);
        using var bot = new FltBot(); Seat(server, bot, "p739start");
        var mission = server.Ships.Single().Ship.Mission!;
        mission.Stage = MissionStage.Starting;
        var deadlines = (Dictionary<Mission, double>)typeof(Srv)
            .GetField("_startCountdownDeadline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        deadlines[mission] = server.SimNow;
        typeof(Srv).GetMethod("AdvanceStartCountdowns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [server.SimNow + .001]);
        Assert.Equal(MissionStage.Started, mission.Stage);
    }

    // FedSrv.CPP:8279-8287: refill belongs to the eligible player's tick,
    // including lobby/briefing; completed missions do not gain budget.
    [SkippableFact]
    public void Chat_recovery_ticks_for_seated_players_and_stops_at_mission_over()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false; server.Boot(Config());
        using var bot = new FltBot(); Seat(server, bot, "p739budget");
        var peer = server.Ships.Single(); var m = peer.Ship.Mission!;
        var budgets = (Dictionary<ConnectionId, (int Budget, long LastRefillTick)>)typeof(Srv)
            .GetField("_chatBudgets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var recover = typeof(Srv).GetMethod("AdvancePeriodicRules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int refill = (int)typeof(Srv).GetField("_chatBudgetRefillPerTick", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        int maximum = (int)typeof(Srv).GetField("_chatBudgetMax", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var peers = (Dictionary<ConnectionId, Srv.LiveShip>)typeof(Srv)
            .GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var connection = peers.Single().Key;
        foreach (var stage in new[] { MissionStage.NotStarted, MissionStage.Starting, MissionStage.Started })
        {
            m.Stage = stage; budgets[connection] = (0, 0L);
            recover.Invoke(server, [server.SimNow]); Assert.Equal(refill, budgets[connection].Budget);
            budgets[connection] = (maximum - 1, 0L);
            recover.Invoke(server, [server.SimNow]); Assert.Equal(maximum, budgets[connection].Budget);
        }
        m.Stage = MissionStage.Over; budgets[connection] = (0, 0L);
        recover.Invoke(server, [server.SimNow]); Assert.Equal(0, budgets[connection].Budget);
    }
}
