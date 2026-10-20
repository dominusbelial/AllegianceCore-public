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
public sealed class P651EquipmentLiveTests
{
    private static Allegiance.Server.ServerConfig Config() => new()
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games = [new() { Name = "P651 local battle", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }]
    };
    private static LogonRequest Request() => new()
    {
        CharacterName = "P651", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1, DwCookie = 1, CrcFileList = -1,
        SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p651"
    };
    private static void Until(Srv server, Func<bool> condition, Action? drive = null)
    {
        for (int i = 0; i < 8000 && !condition(); i++) { drive?.Invoke(); server.TickSim(); Thread.Sleep(1); }
        Assert.True(condition(), "P651 loopback condition did not arrive");
    }
    private static void SpawnWith(Srv server, Func<StaticCore, Mission, short, (Ship, Sector)> spawn) =>
        typeof(Srv).GetField("ShipSpawner", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(server, spawn);

    private static MountedPart Mount(Ship s, DataMissileTypeIGC mt, byte fraction = 255) => s.MountPart(new(
        0, 0, "", "", "P651 missile", "", 0, [], [], 0, 1, 32000, -1, 0, "",
        new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId)), 10, 0, fraction)!;

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(IgcObjectType.Station)] [InlineData(IgcObjectType.Warp)]
    public void Static_target_order_lock_and_not_ready_state_roundtrip_over_TCP(IgcObjectType type)
    {
        Require.Artwork(); using var server = new Srv(); Ship? ship = null; short targetId = -1;
        SpawnWith(server, (core, m, id) =>
        {
            var sector = type == IgcObjectType.Station
                ? m.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 1))
                : m.Sectors.First(s => s.Warps.Count > 0);
            targetId = type == IgcObjectType.Station ? sector.StationRuntimes.First(st => st.SideId == 1).StationId : sector.Warps[0].Warp.WarpId;
            var target = EquipmentTarget.Resolve(sector, type, targetId)!;
            var hull = core.HullTypes.Values.First() with { ScannerRange = 10000, HabmCapabilities = 0, Thrust = 0,
                CapacityMagazine = checked((short)(10 * Math.Max(1, (int)core.MissileTypes.Values.First().LauncherExpendableSize))) };
            ship = new Ship(id, hull, target.Position + new Vector3(0, 0, 600), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            m.PlaceShip(ship, sector);
            var mt = core.MissileTypes.Values.First() with { LoadTime = 2, LockTime = 0.5f, LockAngle = 1,
                MaxLock = 1, InitialSpeed = 100, Acceleration = 10, Lifespan = 30, EabmCapabilities = (short)(type == IgcObjectType.Warp ? 2 : 0) };
            ((IDictionary<short, DataMissileTypeIGC>)core.MissileTypes)[mt.ExpendableTypeId] = mt;
            Mount(ship, mt, 0); return (ship, sector);
        });
        server.Boot(Config()); server.Missions[0].Stage = MissionStage.Started;
        using var session = new ConnectionSession(); session.Connect(server.BoundPort, "P651"); session.SendLogonRequest(Request());
        Until(server, () => session.World.MyShipId is not null && session.World.MissileLock.MissileName.Length > 0);
        Assert.False(session.World.MissileLock.IsReady);
        session.SendOrderChange(ship!.Id, OrderChange.CidAttack, OrderChange.CmdCurrent, type, targetId);
        // Command selection may precede detection (P655b), while firing
        // rechecks CanSee (H-25). Wait for the real scanner sweep as well as
        // the command echo before sending this one-shot early-fire request.
        Until(server, () => session.World.MissileLock.TargetId == targetId && session.World.MissileLock.TargetType == type
            && EquipmentTarget.CanSee(ship.CommandTarget!, ship.SideId!.Value, ship.Sector!));
        Assert.False(session.World.MissileLock.IsReady);
        Assert.True(ship.Parts.Single().TimeLoaded > ship.Mission!.LastUpdate + 0.5,
            "the request must still be early enough to create a dud");
        session.SendFireMissile();
        Until(server, () => ship.Sector!.Models.OfType<Missile>().Any());
        Assert.True(ship.Sector!.Models.OfType<Missile>().First().IsDud);
        Until(server, () => session.World.MissileLock.IsFullLock);
        Assert.True(session.World.MissileLock.IsReady);
        Assert.Equal(type, session.World.MissileLock.TargetType);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Local_battle_production_bot_missile_decision_fires_over_TCP_and_scores_kills()
    {
        Require.Artwork(); using var server = new Srv(); Ship? shooter = null; Ship? target = null;
        SpawnWith(server, (core, m, id) =>
        {
            var sector = m.Sectors[0];
            var hull = core.HullTypes.Values.First() with { HabmCapabilities = 0, ScannerRange = 10000, Signature = 1,
                HitPoints = 1, Ecm = 1, Thrust = 0, Length = 10,
                CapacityMagazine = checked((short)(10 * Math.Max(1, (int)core.MissileTypes.Values.First().LauncherExpendableSize))) };
            shooter = new Ship(id, hull with { HitPoints = 1000 }, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            target = new Ship(30000, hull, new(0, 0, -250), Vector3.Zero, Orientation.Identity) { SideId = 1 };
            m.PlaceShip(shooter, sector); m.PlaceShip(target, sector); shooter.CommandTarget = target;
            var mt = core.MissileTypes.Values.First() with { LoadTime = 1, LockTime = 0.25f, LockAngle = 1,
                MaxLock = 1, InitialSpeed = 100, Acceleration = 100, ReadyTime = 0, Lifespan = 10, Power = 10000,
                BlastPower = 0, EabmCapabilities = 0 };
            ((IDictionary<short, DataMissileTypeIGC>)core.MissileTypes)[mt.ExpendableTypeId] = mt;
            Mount(shooter, mt); return (shooter, sector);
        });
        server.Boot(Config()); server.Missions[0].Stage = MissionStage.Started;
        using var session = new ConnectionSession(); session.Connect(server.BoundPort, "P651"); session.SendLogonRequest(Request());
        Until(server, () => session.World.MissileLock.IsFullLock);
        // Exercise the actual MCP bot fire/lock/evasion controller with the real
        // connection world. Geometry is fixed so only equipment rules decide fire.
        var bt = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
        var bot = Activator.CreateInstance(bt, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, ["P651-bot", shooter!.HullType.HullId, "fighter", (short)0, (short)0, "combat", 0.0], null)!;
        void Set(string name, object value) => bt.GetField(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(bot, value);
        Set("LauncherPartId", (short)32000); Set("MissileReadyTime", 1.0); Set("ShipId", shooter.Id); Set("TargetId", target!.Id);
        var fire = typeof(BotManager).GetMethod("MaybeFireMissile", BindingFlags.Static | BindingFlags.NonPublic)!;
        int rounds = shooter.Parts.Single().Amount;
        Until(server, () => server.Missions[0].Sides.Single(s => s.Id == 0).Kills > 0,
            () => fire.Invoke(null, [bot, session, server.Missions[0].LastUpdate, true]));
        Assert.True(shooter.Parts.Single().Amount < rounds);
        Assert.Equal(1, server.Missions[0].Sides.Single(s => s.Id == 0).Kills);
        Assert.DoesNotContain(shooter.Sector!.Models.OfType<Missile>(), missile => missile.IsDud);
        Console.WriteLine($"P651-BATTLE bot={shooter.Id} missiles={rounds - shooter.Parts.Single().Amount} kills=1 duds=0");
    }
}
