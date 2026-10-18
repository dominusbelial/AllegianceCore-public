using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P629PenaltyLiveTests
{
    private static Allegiance.Server.ServerConfig Config(string mode) => new()
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        Sim = new() { CrowdingPenalty = mode }, UpdatesPerSecond = 20,
        LiveSettings = new() { TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
        Games = [new() { Name = "P629 local", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["A", "B"] }]
    };
    private static LogonRequest Request() => new()
    {
        CharacterName = "P629", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1, DwCookie = 1, CrcFileList = -1,
        SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p629"
    };
    private static void Until(Srv server, Func<bool> done, int ticks = 5000)
    {
        for (int i = 0; i < ticks && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "P629 live condition timed out");
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("native")] [InlineData("off")]
    public void Crowded_local_sector_damages_and_ejects_only_in_native_mode(string mode)
    {
        Require.Artwork(); using var server = new Srv(); Ship? owner = null; Sector? arena = null;
        var spawn = new Func<StaticCore, Mission, short, (Ship, Sector)>((core, m, id) =>
        {
            arena = m.Sectors.Single(s => s.Id == 1052);
            var hull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.HitPoints > 0)
                with { Thrust = 0, Length = 1, HabmCapabilities = 0, MaxWeapons = 0, MaxFixedWeapons = 0, Hardpoints = [] };
            owner = new Ship(id, hull, new(0, 0, 2000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            m.PlaceShip(owner, arena);
            for (short i = 0; i < 389; i++)
            {
                var s = new Ship((short)(30000 + i), hull, new((i % 20 - 10) * 100, (i / 20 - 10) * 100, 0),
                    Vector3.Zero, Orientation.Identity) { SideId = (short)(i % 2) };
                m.PlaceShip(s, arena);
            }
            return (owner, arena);
        });
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, spawn);
        server.Boot(Config(mode)); var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        using var session = new ConnectionSession(); session.Connect(server.BoundPort, "P629"); session.SendLogonRequest(Request());
        Until(server, () => session.World.MyShipId is not null);
        if (mode == "native")
        {
            Until(server, () => session.World.ShipWarnings.Mask.HasFlag(ShipWarningMask.CrowdedSector));
            Assert.True(owner!.Fraction < 1);
            Until(server, () => owner.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
            Assert.True(owner.Fraction > 0); Assert.Equal(0, mission.Sides.Sum(s => s.Kills));
            Console.WriteLine($"P629-LIVE mode=native ejectAt={mission.LastUpdate:F2}s owner={owner.Id} podHP={owner.Fraction:F3}");
        }
        else
        {
            for (int i = 0; i < 100; i++) server.TickSim();
            Assert.Equal(1, owner!.Fraction); Assert.Equal(0, arena!.Cost);
            Assert.Equal(ShipWarningMask.None, session.World.ShipWarnings.Mask);
            Console.WriteLine($"P629-LIVE mode=off 390 ships intact after {mission.LastUpdate:F2}s");
        }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Beyond_boundary_live_owner_takes_hull_damage_and_receives_warning_with_off()
    {
        Require.Artwork(); using var server = new Srv(); Ship? owner = null;
        var spawn = new Func<StaticCore, Mission, short, (Ship, Sector)>((core, m, id) =>
        {
            var fc = core.Constants.FloatConstants; var arena = m.Sectors.Single(s => s.Id == 1052);
            var hull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod)) with { Thrust = 0 };
            owner = new Ship(id, hull, new(fc[1] * MathF.Sqrt(fc[2]) * 2, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            m.PlaceShip(owner, arena); return (owner, arena);
        });
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, spawn);
        server.Boot(Config("off")); server.Missions[0].Stage = MissionStage.Started;
        using var session = new ConnectionSession(); session.Connect(server.BoundPort, "P629"); session.SendLogonRequest(Request());
        Until(server, () => session.World.ShipWarnings.Mask == ShipWarningMask.OutOfBounds);
        Assert.True(owner!.Fraction < 1); Assert.True(session.World.ShipWarnings.LoopActive);
        Assert.Equal(ShipWarningMask.OutOfBounds, session.World.ShipWarnings.TakeRisingEdges());
        owner.Position = Vector3.Zero;
        Until(server, () => session.World.ShipWarnings.Mask == ShipWarningMask.None);
        Assert.False(session.World.ShipWarnings.LoopActive);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Production_bots_reach_target_sector_with_crowding_off()
    {
        Require.Artwork(); using var server = new Srv(); server.Boot(Config("off"));
        var m = server.Missions[0]; m.Stage = MissionStage.Started;
        var target = m.Sectors.Single(s => s.Id == 1052);
        var hull = m.Core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod)) with { Thrust = 0, Length = 1 };
        for (short i = 0; i < 74; i++)
            m.PlaceShip(new Ship((short)(30000 + i), hull, new((i % 10 - 5) * 100, (i / 10 - 4) * 100, 2000),
                Vector3.Zero, Orientation.Identity) { SideId = 0 }, target);
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(10); } });
        ticker.Start();
        try
        {
            using var bots = new BotManager("127.0.0.1", server.BoundPort);
            var fighter = Catalog.DefaultFighterHull(); var parts = Catalog.NativePreferredLoadout(fighter.Id, fighter.Name).Parts;
            var a = bots.SpawnBot(fighter.Id, fighter.Name, 0, parts, "combat", target.Id);
            var b = bots.SpawnBot(fighter.Id, fighter.Name, 1, parts, "combat", target.Id);
            var reached = new HashSet<short>(); var deadline = DateTime.UtcNow.AddSeconds(90);
            while (reached.Count < 2 && DateTime.UtcNow < deadline)
            {
                foreach (var bot in bots.ActiveBots()) if (bot.Sector == target.Id) reached.Add(bot.ShipId);
                Thread.Sleep(20);
            }
            Assert.Contains(a.ShipId, reached); Assert.Contains(b.ShipId, reached); Assert.Equal(0, target.Cost);
            Console.WriteLine($"P629-BOTS mode=off reached={reached.Count}/2 target={target.Id} population={target.Models.OfType<Ship>().Count()}");
        }
        finally { stop.Cancel(); ticker.Join(5000); }
    }
}
