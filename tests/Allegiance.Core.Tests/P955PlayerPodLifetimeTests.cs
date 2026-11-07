using System.Collections.Concurrent;
using System.Diagnostics;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>Real player connections, without Bot or pod autopilot. Native's
/// ReceiveDamage returns c_drKilled AFTER the synchronous hull swap
/// (Igc/shipIGC.cpp:1333-1334; FedSrv/FedSrv.CPP:11918-12021).
/// That result suppresses bounce correction; it cannot decide survivor lifetime.</summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P955PlayerPodLifetimeTests(ITestOutputHelper output)
{
    private sealed class Player : ITransportSite, IDisposable
    {
        private readonly ITransport _transport;
        private readonly ConcurrentQueue<byte[]> _incoming = new();
        internal readonly ClientWorld World = new();
        internal readonly List<IFedMessage> Messages = [];
        internal Player(bool udp) => _transport = udp ? new UdpTransport(this) : new TcpTransport(this);
        internal void Connect(ushort port)
        {
            _transport.Join("127.0.0.1", port, "P955-player");
            Send(new LogonRequest
            {
                CharacterName = "P955-player", FedsrvVer = LogonRequest.MsgVer,
                DwCookie = uint.MaxValue, CrcFileList = -1, CDKey = "ABCDEFGHIJKL",
                MissionPassword = "", ZoneTicket = [], SteamAuthTicket = new byte[16], DrmHash = "p955",
            });
        }
        internal void Send(IFedMessage message) => _transport.Send(ConnectionId.Server, FedPacket.Pack(message.Encode()), true);
        internal void Drain()
        {
            while (_incoming.TryDequeue(out var packet))
                foreach (var bytes in FedPacket.Unpack(packet))
                {
                    IFedMessage? message = FedMessageHeader.Read(bytes).Fmid switch
                    {
                        SetCluster.Fmid => SetCluster.Decode(bytes),
                        ShipStatusMessage.Fmid => ShipStatusMessage.Decode(bytes),
                        Eject.Fmid => Eject.Decode(bytes),
                        KillShip.Fmid => KillShip.Decode(bytes),
                        ShipDelete.Fmid => ShipDelete.Decode(bytes),
                        EnterLifepod.Fmid => EnterLifepod.Decode(bytes),
                        SingleShipUpdate.Fmid => SingleShipUpdate.Decode(bytes),
                        DockedNotify.Fmid => DockedNotify.Decode(bytes),
                        _ => null,
                    };
                    if (message is null) continue;
                    Messages.Add(message);
                    World.Apply(message);
                }
        }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => _incoming.Enqueue(data.ToArray());
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void Dispose() => _transport.Shutdown();
    }

    private sealed class Game : IDisposable
    {
        internal readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        internal readonly Player Player;
        internal readonly Mission Mission;
        internal readonly Ship Ship;
        internal readonly Sector Sector;
        internal readonly Station Enemy;

        internal Game(bool udp)
        {
            Server.ShipSpawner = (core, mission, id) =>
            {
                var sector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 1));
                var hull = core.HullTypes[121]; // same Fighter as the incident; no mounted shield
                var ship = new Ship(id, hull, new Vector3(4000, 4000, 0), Vector3.Zero, Orientation.Identity)
                    { SideId = 0 };
                mission.PlaceShip(ship, sector);
                return (ship, sector);
            };
            Server.Boot(new Allegiance.Server.ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
                UpdatesPerSecond = 20, SideSelectAutoSeatSeconds = 30,
                Transport = new() { Protocol = udp ? "udp" : "tcp", Mode = "pool" },
                Games = [new() { Name = "P955", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            Mission = Server.Missions.Single();
            Mission.Stage = MissionStage.Started;
            // Use deterministic sphere contacts to isolate lifetime from CVH
            // separation. Real landing bays remain enabled for the dock proof.
            foreach (var sector in Mission.Sectors) sector.CollisionHulls = null;
            Player = new Player(udp);
            Player.World.LifepodHullResolver = _ => Mission.Sides.First(s => s.Id == 0).Civilization.Lifepod;
            var port = udp ? Assert.IsType<DualTransport>(Server.Host.Transport).UdpPort : Server.BoundPort;
            Player.Connect(port);
            Until(() => Player.World.MyShipId is not null);
            Ship = Server.Ships.Single().Ship;
            Sector = Ship.Sector!;
            Enemy = Sector.StationRuntimes.First(st => st.SideId != Ship.SideId);
            Assert.Equal(Ship.PilotTypePlayer, Ship.PilotType);
            Assert.False(Ship.IsNpc);
        }

        internal void Until(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < 15)
            {
                Server.TickSim();
                Player.Drain();
                Thread.Sleep(1);
            }
            Assert.True(done(), "wire/simulation condition timed out");
        }
        internal double EjectByStation()
        {
            double armedAt = Mission.LastUpdate;
            Ship.SetFraction(.001f);
            Ship.Position = Enemy.Center + new Vector3(Enemy.Radius + Ship.HullType.Length / 2 - 1, 0, 0);
            Ship.Velocity = new Vector3(-300, 0, 0);
            Server.TickSim(); // actual collision detection -> ReceiveDamage -> KillShipEvent -> EJECT
            Assert.True(Ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "station ram must eject");
            return armedAt;
        }
        internal void AssertLivePod()
        {
            // Name all four telemetry predicates so a pre-fix failure identifies
            // the precise invalid state instead of just the 'removed' outcome.
            Assert.False(Ship.Dead, "removed predicate: Dead");
            Assert.Same(Sector, Ship.Sector);
            Assert.Same(Mission, Ship.Mission);
            Assert.Contains(Mission, Server.Missions);
            Assert.Contains(Ship, Sector.Models); // incident: ONLY this predicate is false
            Assert.Same(Ship, Mission.GetShip(Ship.Id));
            Assert.Null(Ship.StationId);
        }
        internal void ParkInOpenSpace()
        {
            Ship.Position = new Vector3(4000, 4000, 0);
            Ship.Velocity = Vector3.Zero;
        }
        public void Dispose() { Player.Dispose(); Server.Dispose(); }
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Station_ram_survivor_ticks_until_native_oxygen_deadline_then_respawns(bool udp)
    {
        Require.Artwork();
        using var game = new Game(udp);
        double armedAt = game.EjectByStation();
        output.WriteLine($"After ram: Dead={game.Ship.Dead}; SectorNull={game.Ship.Sector is null}; InSectorModels={game.Sector.Models.Contains(game.Ship)}; InLiveMission={game.Server.Missions.Contains(game.Ship.Mission!)}; InMissionRegistry={game.Mission.GetShip(game.Ship.Id) is not null}");
        game.AssertLivePod();
        var ship = game.Ship;
        double deadline = Assert.IsType<double>(ship.LifepodTimeToDie);
        Assert.Equal(300, deadline - armedAt, 6); // real core endurance, no compressed timer
        game.ParkInOpenSpace();
        game.Until(() => game.Player.Messages.OfType<Eject>().Any());
        var entity = game.Player.World.Ships[ship.Id];
        Assert.False(entity.IsDead);
        Assert.False(entity.IsGone);
        Assert.Equal(ship.HullType.HullId, entity.HullId);
        Assert.False(Assert.Single(game.Player.Messages.OfType<KillShip>()).BDeathCredit);
        Assert.DoesNotContain(game.Player.Messages.OfType<ShipDelete>(), d => d.ShipId == ship.Id);
        var eject = Assert.Single(game.Player.Messages.OfType<Eject>());
        uint time = (uint)(game.Mission.LastUpdate * 1000);
        game.Player.Send(new ShipUpdate
        {
            Time = time, Cookie = eject.Cookie,
            Payload = ClientShipUpdate.FromControls(time, 0, new ControlData(.4f, 0, 0, -1)),
        });
        game.Until(() => ship.Controls.Yaw > .3f); // new eject cookie accepts real player input
        double last = ship.LastUpdate;
        game.Server.TickSim();
        Assert.True(ship.LastUpdate > last, "surviving pod must keep receiving sector updates");
        while (game.Mission.LastUpdate + .051 < deadline)
        {
            game.Server.TickSim();
            Assert.True(ship.StationId is null, $"unexpected dock at {game.Mission.LastUpdate:F3}, oxygen deadline {deadline:F3}, hit {ship.KillingHit.Kind}");
        }
        Assert.Null(ship.StationId);
        game.Until(() => ship.StationId is not null && game.Player.Messages.OfType<EnterLifepod>().Any());
        var death = Assert.Single(game.Player.Messages.OfType<KillShip>(), k => k.BDeathCredit);
        Assert.Equal(ship.Id, death.ShipId);
        Assert.Contains(game.Player.Messages.OfType<ShipDelete>(), d => d.ShipId == ship.Id && d.Sdr == ShipDelete.SdrKilled);
        Assert.InRange(game.Mission.LastUpdate - deadline, 0, 2);
        Assert.False(ship.Dead);
        Assert.Contains(ship, ship.Sector!.Models);
        Assert.Equal(ship.SideId, ship.Sector.StationRuntimes.Single(s => s.StationId == ship.StationId).SideId);
        game.Until(() => game.Player.World.Ships[ship.Id].State == ShipStatusMessage.StateDocked);
        Assert.Equal(ship.StationId, game.Player.World.Ships[ship.Id].StationId);
        output.WriteLine($"{(udp ? "UDP" : "TCP")}: live player -> station collision -> eject -> 300s oxygen -> KILL_SHIP/SHIP_DELETE/ENTER_LIFEPOD -> friendly docked restart; deadline={deadline:F2}, at={game.Mission.LastUpdate:F2}");
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Station_ram_pod_refuses_enemy_bay_and_docks_in_friendly_bay(bool udp)
    {
        Require.Artwork();
        using var game = new Game(udp);
        game.EjectByStation();
        game.AssertLivePod();
        game.Until(() => game.Player.Messages.OfType<Eject>().Any());
        var ship = game.Ship;
        Assert.NotNull(game.Sector.StationLandingBays?.Bay(game.Enemy.Type));
        ship.Position = DockTestPlacement.Inside(game.Mission, game.Enemy.Data, ship.HullType);
        ship.Velocity = Vector3.Zero;
        for (int i = 0; i < 5; i++) game.Server.TickSim();
        game.AssertLivePod(); // the same bay geometry on an enemy station cannot dock a pod
        var homeSector = game.Mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == ship.SideId));
        var home = homeSector.StationRuntimes.First(st => st.SideId == ship.SideId);
        Assert.NotNull(homeSector.StationLandingBays?.Bay(home.Type));
        // Test placement only: navigation/autopilot is outside this regression.
        game.Sector.Remove(ship);
        game.Mission.PlaceShip(ship, homeSector);
        ship.Position = DockTestPlacement.Inside(game.Mission, home.Data, ship.HullType);
        ship.Velocity = Vector3.Zero;
        game.Until(() => ship.StationId == home.StationId);
        game.Until(() => game.Player.World.Ships[ship.Id].State == ShipStatusMessage.StateDocked);
        Assert.Equal(home.StationId, game.Player.World.Ships[ship.Id].StationId);
        Assert.False(ship.Dead);
        Assert.Equal(1, ship.LifepodEnduranceFraction(game.Mission.LastUpdate));
        Assert.DoesNotContain(game.Player.Messages.OfType<KillShip>(), k => k.BDeathCredit);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Weapon_kill_control_already_preserves_player_pod(bool udp)
    {
        Require.Artwork();
        using var game = new Game(udp);
        var projectileType = game.Mission.Core.ProjectileTypes.Values.First(p => p.Power > 0 && p.BlastPower == 0)
            with { Power = 100000, DamageType = 0, Lifespan = 5 };
        var projectile = new Projectile(-200, game.Sector, projectileType, game.Ship.Position,
            Vector3.Zero, new Vector3(0, 0, 1), game.Mission.LastUpdate);
        game.Sector.Add(projectile);
        game.Until(() => game.Player.Messages.OfType<Eject>().Any());
        game.AssertLivePod();
        double before = game.Ship.LastUpdate;
        game.Server.TickSim();
        Assert.True(game.Ship.LastUpdate > before);
        Assert.False(game.Player.World.Ships[game.Ship.Id].IsDead);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData("asteroid")]
    [InlineData("ship")]
    public void Other_lethal_collision_paths_preserve_the_same_player_survivor(string obstacle)
    {
        Require.Artwork();
        using var game = new Game(false);
        var ship = game.Ship;
        ship.SetFraction(.001f);
        if (obstacle == "asteroid")
        {
            var rock = game.Sector.Mineable.First();
            var p = rock.Data.Position;
            ship.Position = new Vector3(p[0], p[1], p[2]) + new Vector3(rock.Radius + ship.HullType.Length / 2 - 1, 0, 0);
        }
        else
        {
            var target = new Ship(-201, ship.HullType with { HitPoints = 100000 },
                ship.Position, Vector3.Zero, Orientation.Identity) { SideId = 1 };
            game.Mission.PlaceShip(target, game.Sector);
            ship.Position += new Vector3(ship.HullType.Length - 1, 0, 0);
        }
        ship.Velocity = new Vector3(-100, 0, 0);
        game.Server.TickSim();
        Assert.True(ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod),
            $"{obstacle} collision must eject: length={ship.HullType.Length}, fraction={ship.Fraction}, pairs={game.Sector.LastShipShipEvents}, resolved={game.Sector.LastShipShipResolved}, debounced={game.Sector.LastShipShipDebounced}");
        game.AssertLivePod();
        game.ParkInOpenSpace();
        game.Until(() => game.Player.Messages.OfType<Eject>().Any());
        double before = ship.LastUpdate;
        game.Server.TickSim();
        Assert.True(ship.LastUpdate > before);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Software_bot_uses_same_survivor_path_for_weapons_and_station_rams(bool stationRam)
    {
        Require.Artwork();
        using var game = new Game(false);
        using var bot = new Allegiance.Bot.Bot { TransportProtocol = "tcp" };
        bot.Connect(game.Server.BoundPort, "P955-bot");
        bot.SendLogonRequest(new LogonRequest
        {
            CharacterName = "P955-bot", FedsrvVer = LogonRequest.MsgVer,
            DwCookie = uint.MaxValue, CrcFileList = -1, CDKey = "ABCDEFGHIJKL",
            MissionPassword = "", ZoneTicket = [], SteamAuthTicket = new byte[16], DrmHash = "p955",
        });
        game.Until(() => bot.ShipAssignment is not null);
        var ship = game.Server.Ships.Single(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
        Assert.False(ship.IsNpc); // showcase software bots are player-class network peers
        Assert.Equal(Ship.PilotTypePlayer, ship.PilotType);
        if (stationRam)
        {
            ship.SetFraction(.001f);
            ship.Position = game.Enemy.Center + new Vector3(game.Enemy.Radius + ship.HullType.Length / 2 - 1, 0, 0);
            ship.Velocity = new Vector3(-300, 0, 0);
            game.Server.TickSim();
        }
        else
        {
            ship.Position += new Vector3(1000, 0, 0); // keep the pod away from the other peer
            ship.ReceiveDamage(0, 100000, game.Mission.LastUpdate, ship.Position, Vector3.Zero, null);
        }
        game.Until(() => bot.Ejects.Any());
        Assert.False(ship.Dead);
        Assert.Contains(ship, game.Sector.Models);
        Assert.Same(ship, game.Mission.GetShip(ship.Id));
        double before = ship.LastUpdate;
        game.Server.TickSim();
        Assert.True(ship.LastUpdate > before);
    }
}
