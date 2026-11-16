using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1032 — R1b F6: constructor <c>CidBuild</c> orders leaked unexplored
/// asteroid data and the drone-chat path skipped the order path's authority
/// rule. Every test drives the real production dispatcher
/// (<c>TestProcessDatagram</c>) with the bytes a remote peer would send and
/// reads the frames the server puts on the wire, so the fog-of-war assertions
/// are about what a client can actually learn.
/// <list type="number">
/// <item><b>Unseen-rock oracles.</b> A rock the side has never seen must not be
/// resolvable through an order: the refusal carries no asteroid id, capability
/// mask, sector name or construction state (native <c>CommandToDrone</c> sends
/// no refusal text at all, FedSrv.CPP:3286-3292, and the port now logs the
/// reason server-side only).</item>
/// <item><b>Seen-rock play unchanged.</b> The same orders at a scouted rock
/// still start construction through both entry points.</item>
/// <item><b>Native authority.</b> Native's constructor command is the drone
/// chat path (<c>CommandToDrone</c>) and it is open to any same-side player;
/// the port's port-added direct order path now uses the same rule.</item>
/// <item><b>Native decoy.</b> A rock replaced by an enemy station makes a
/// builder reply <c>"Affirmative."</c> and fly to the station's position
/// (Imago's fog-of-war guard, FedSrv.CPP:3213-3235) instead of revealing that
/// the rock is gone.</item>
/// <item><b>Native wing echo.</b> An accepted drone order is echoed as
/// <c>CHAT_INDIVIDUAL_ECHO</c> to the Command/Charlie/Delta/Echo wings
/// (FedSrv.CPP:3563-3570).</item>
/// </list>
/// Harness mirrors <c>R1ReviewTests</c>' seeded world: real mission assemblies,
/// real ships and a real Builder drone, no socket round trip.
/// </summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P1032BuildOrderOracleTests
{
    /// <summary>Two-side world with a leader commander, a non-leader teammate,
    /// an enemy peer and a real side-0 Builder drone, over <c>XXV_simple</c>:
    /// <c>Home</c> (sector 0, where the side-0 ships and the constructor live)
    /// and <c>Remote</c> (sector 1, where side 0 has nothing). Asteroids are
    /// placed but never swept, so their <c>SeenBySide</c> starts false exactly
    /// like an unexplored rock; a test that needs a scouted rock reveals it
    /// explicitly.</summary>
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly ServerConfig Config = new()
        {
            StaticCore = "PCore001.igc",
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff, MinimumPlayersPerTeam = 1 }, new GameConfig()],
        };
        public readonly Mission Mission;
        public readonly StaticCore Core;
        public readonly Sector Home, Remote;
        public readonly Ship Commander, Mate, WingZeroMate, Enemy, Builder;
        public readonly Asteroid HomeRock, RemoteRock, RemoteWrongRock, DecoyRock;
        public readonly DataStationTypeIGC StationType;
        public readonly short BuilderDroneTypeId;
        public readonly List<(ConnectionId Peer, byte[] Msg)> Sent = [];
        public readonly ConnectionId A = new(1), B = new(2), D = new(3), C = new(4);

        public World()
        {
            Require.Artwork();
            Core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
            Mission = Mission.Assemble(Core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Mission.Stage = MissionStage.Started;   // FM_CS_ORDER_CHANGE's native gate, FedSrv.CPP:7266
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Host.Transport, true);
            Set("_host", Host);
            Set("_config", Config);
            Set("_missions", new List<Mission> { Mission });

            Home = Mission.Sectors[0];
            Remote = Mission.Sectors[1];

            // A real buildable techbase station type (Garrison-class) and the
            // Builder drone type whose PilotType is native c_ptBuilder.
            StationType = Core.StationTypes.Values.First(s =>
                s.ClassId == 0 && (s.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
            BuilderDroneTypeId = Core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeBuilder).Key;

            // A capability that shares no bit with the station type — provably
            // unbuildable, so the capability gate is the only failing one.
            short wrongCap = StationType.AabmBuild switch
            {
                DataAsteroidIGC.AabmBuildable => (short)(DataAsteroidIGC.AabmSpecial << 2),
                (short)(DataAsteroidIGC.AabmSpecial << 2) => DataAsteroidIGC.AabmBuildable,
                (short)(DataAsteroidIGC.AabmSpecial << 0) => (short)(DataAsteroidIGC.AabmSpecial << 1),
                (short)(DataAsteroidIGC.AabmSpecial << 1) => (short)(DataAsteroidIGC.AabmSpecial << 0),
                _ => DataAsteroidIGC.AabmBuildable,
            };
            Assert.Equal(0, StationType.AabmBuild & wrongCap);

            HomeRock = new Asteroid(MkAsteroid(7101, new Vector3(300f, 0f, 0f), 60, Home.Id, StationType.AabmBuild));
            RemoteRock = new Asteroid(MkAsteroid(7102, new Vector3(300f, 0f, 0f), 60, Remote.Id, StationType.AabmBuild));
            RemoteWrongRock = new Asteroid(MkAsteroid(7103, new Vector3(380f, 0f, 0f), 60, Remote.Id, wrongCap));
            // Far outside any side-0 scanner: a station built here stays unseen.
            DecoyRock = new Asteroid(MkAsteroid(7104, new Vector3(40000f, 2000f, 0f), 60, Home.Id, StationType.AabmBuild));
            Mission.PlaceAsteroid(HomeRock, Home);
            Mission.PlaceAsteroid(RemoteRock, Remote);
            Mission.PlaceAsteroid(RemoteWrongRock, Remote);
            Mission.PlaceAsteroid(DecoyRock, Home);

            var hull = Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            Commander = Place(100, 0, Home, hull);
            Mate = Place(101, 0, Home, hull);
            WingZeroMate = Place(102, 0, Home, hull);
            Enemy = Place(103, 1, Remote, hull);
            Mission.MissionOwnerSideId = 0;
            Mission.Sides[0].LeaderShipId = Commander.Id;
            Mission.Sides[1].LeaderShipId = Enemy.Id;

            // A real constructor: side 0, PilotType == c_ptBuilder, configured
            // station type, standing by for a commander (never picks on its own).
            Builder = Server.SpawnNpcShip(Mission, Home, 0, new Vector3(320f, 0f, 0f), BuilderDroneTypeId);
            Assert.Equal(Ship.PilotTypeBuilder, Builder.PilotType);
            Builder.BuilderStationType = StationType;
            Builder.WaitsForBuildOrder = true;

            Server.TestSeedPeers([(A, Commander, Home), (B, Mate, Home), (D, WingZeroMate, Home), (C, Enemy, Remote)]);
            Server._testSendCapture = (c, p) => Sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
            Set("_simNow", 10d);
        }

        private Ship Place(short id, short side, Sector sector, DataHullTypeIGC hull)
        {
            var ship = new Ship(id, hull, new Vector3(5000f + id, 5000f, 5000f), Vector3.Zero, Orientation.Identity) { SideId = side };
            Mission.PlaceShip(ship, sector);
            ship.InitClock(10);
            return ship;
        }

        public void Set(string field, object value) =>
            typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);

        public void Route(ConnectionId from, IFedMessage message) =>
            Server.TestProcessDatagram(from, FedPacket.Pack([message.Encode()]));

        public void ResetCapture() => Sent.Clear();

        /// <summary>Every CHATMESSAGE(23) frame the server sent to
        /// <paramref name="peer"/>.</summary>
        public List<ChatMessage> ChatsTo(ConnectionId peer) => Sent
            .Where(s => s.Peer == peer && FedMessageHeader.Read(s.Msg).Fmid == ChatMessage.Fmid)
            .Select(s => ChatMessage.Decode(s.Msg))
            .ToList();

        public OrderChange Order(Ship commandee, short asteroidId) => new()
        {
            ShipID = commandee.Id,
            CommandId = OrderChange.CidBuild,
            Command = OrderChange.CmdAccepted,
            ObjectType = IgcObjectType.Asteroid,
            ObjectID = asteroidId,
        };

        public ChatMessage ChatOrder(Ship commandee, short asteroidId) => new()
        {
            ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = commandee.Id,
            CommandID = OrderChange.CidBuild,
            OtTarget = (short)IgcObjectType.Asteroid,
            OidTarget = asteroidId,
            Message = "",
        };

        public Side EnemySide => Mission.Sides.Single(s => s.Id == 1);

        public void Dispose()
        {
            Host.Transport!.Shutdown();
            Server.Dispose();
        }
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 1f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    // ── F6 (1): the whole-map asteroid oracles ────────────────────────────

    /// <summary>⭐ Repro — a leader orders a constructor to build at a rock in
    /// a sector the side has never seen. Before the fix the refusal chat
    /// carried the server diagnostic `"Negative. BUILD of '&lt;station&gt;'
    /// (AabmBuild 0x8) refused at asteroid 7103 (cap 0x…)"`, i.e. the exact
    /// unexplored capability mask; afterwards the commander gets no chat at
    /// all and nothing is constructed.</summary>
    [SkippableFact]
    public void Leader_build_order_at_an_unseen_wrong_type_rock_reveals_no_capability_mask()
    {
        Require.Artwork();
        using var w = new World();
        Assert.False(w.RemoteWrongRock.SeenBySide(0), "precondition: the remote rock is unexplored");

        w.Route(w.A, w.Order(w.Builder, w.RemoteWrongRock.Data.AsteroidId));

        Assert.Empty(w.ChatsTo(w.A));
        Assert.Empty(w.ChatsTo(w.B));
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
        Assert.False(w.Builder.IsBuilding);
    }

    /// <summary>⭐ Repro — the same scan with an id that names nothing on the
    /// map. Before the fix the refusal said `"BUILD target asteroid 32700 not
    /// found in any sector"`, which told the commander that an asteroid
    /// anywhere had been consumed by an unseen enemy build; now it is silent.</summary>
    [SkippableFact]
    public void Leader_build_order_at_a_nonexistent_asteroid_id_reveals_nothing()
    {
        Require.Artwork();
        using var w = new World();

        w.Route(w.A, w.Order(w.Builder, 32700));

        Assert.Empty(w.ChatsTo(w.A));
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
    }

    /// <summary>⭐ Repro — a teammate (not the leader) sends the native drone
    /// chat order at an unexplored rock in another sector. Before the fix the
    /// reply leaked the unseen sector's name and membership
    /// (`"…is not in builder ship N's sector '&lt;name&gt;'"`).</summary>
    [SkippableFact]
    public void Chat_build_order_at_an_unseen_asteroid_reveals_no_sector_name()
    {
        Require.Artwork();
        using var w = new World();
        Assert.False(w.RemoteRock.SeenBySide(0), "precondition: the remote rock is unexplored");

        w.Route(w.B, w.ChatOrder(w.Builder, w.RemoteRock.Data.AsteroidId));

        var chats = w.ChatsTo(w.B);
        Assert.Empty(chats);   // no leak and no refusal (native sends none)
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
        Assert.False(w.Builder.IsBuilding);
    }

    /// <summary>The order path at an unexplored *buildable* rock in another
    /// sector: the SeenBySide gate must come before the sector gate, so the
    /// reply says nothing about the rock or the sector.</summary>
    [SkippableFact]
    public void Leader_build_order_at_an_unseen_asteroid_reveals_no_sector_name()
    {
        Require.Artwork();
        using var w = new World();
        Assert.False(w.RemoteRock.SeenBySide(0), "precondition: the remote rock is unexplored");

        w.Route(w.A, w.Order(w.Builder, w.RemoteRock.Data.AsteroidId));

        Assert.Empty(w.ChatsTo(w.A));
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
    }

    /// <summary>A scouted rock in another sector is still refused by the
    /// port's same-sector build simplification (a documented P422 gap, see the
    /// report), but the refusal is silent: the reply used to name the
    /// constructor's sector and the rock.</summary>
    [SkippableFact]
    public void Seen_rock_in_another_sector_is_refused_silently()
    {
        Require.Artwork();
        using var w = new World();
        w.RemoteRock.SideVisibility.RevealToSide(0);
        Assert.True(w.RemoteRock.SeenBySide(0), "precondition: the side has scouted the rock");

        w.Route(w.A, w.Order(w.Builder, w.RemoteRock.Data.AsteroidId));

        Assert.Empty(w.ChatsTo(w.A));
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
    }

    /// <summary>The "already under construction" answer (an unseen enemy
    /// builder's activity) is the fourth old diagnostic. It stays server-side:
    /// a rock another constructor has committed to is refused with no chat.</summary>
    [SkippableFact]
    public void Rock_already_under_construction_refusal_is_silent()
    {
        Require.Artwork();
        using var w = new World();
        w.HomeRock.SideVisibility.RevealToSide(0);
        var rivalBuilder = w.Server.SpawnNpcShip(w.Mission, w.Home, 1, new Vector3(280f, 0f, 0f), w.BuilderDroneTypeId);
        rivalBuilder.BuilderStationType = w.StationType;
        var effect = w.Home.StartBuildingEffect(rivalBuilder, w.HomeRock, w.StationType, 1d);

        w.Route(w.A, w.Order(w.Builder, w.HomeRock.Data.AsteroidId));

        Assert.Empty(w.ChatsTo(w.A));
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Same(effect, Assert.Single(w.Home.BuildingEffects));   // the rival's commitment stands
    }

    // ── F6 (2): legit clients still build; authority matches native ───────

    /// <summary>Regression — a scouted rock in the constructor's sector still
    /// starts the timed construction from the leader's order path AND from the
    /// native drone-chat path (the bot/showcase build flows).</summary>
    [SkippableFact]
    public void Seen_asteroid_build_orders_still_start_construction_from_both_paths()
    {
        Require.Artwork();
        using (var w = new World())
        {
            w.HomeRock.SideVisibility.RevealToSide(0);
            w.Route(w.A, w.Order(w.Builder, w.HomeRock.Data.AsteroidId));
            Assert.Same(w.HomeRock, w.Builder.BuilderAsteroidTarget);   // accepted: travel before drilling
            Assert.Empty(w.ChatsTo(w.A));                              // the order path has no native reply
        }
        using (var w = new World())
        {
            w.HomeRock.SideVisibility.RevealToSide(0);
            w.Route(w.A, w.ChatOrder(w.Builder, w.HomeRock.Data.AsteroidId));
            Assert.Same(w.HomeRock, w.Builder.BuilderAsteroidTarget);
            var ack = Assert.Single(w.ChatsTo(w.A));
            Assert.Equal("Affirmative.", ack.Message);
        }
    }

    /// <summary>Native parity — <c>CommandToDrone</c> has no leader gate
    /// (FedSrv.CPP:3190-3191 only requires a real command id and the same
    /// side), and native's <c>FM_CS_ORDER_CHANGE</c> resolves any same-side
    /// ship (<c>pside-&gt;GetShip</c>, FedSrv.CPP:7271-7278). Both paths must
    /// give a teammate the same authority over a constructor.</summary>
    [SkippableFact]
    public void Non_leader_teammate_commands_a_constructor_on_both_paths()
    {
        Require.Artwork();
        using (var w = new World())
        {
            w.HomeRock.SideVisibility.RevealToSide(0);
            w.Route(w.B, w.Order(w.Builder, w.HomeRock.Data.AsteroidId));
            Assert.Same(w.HomeRock, w.Builder.BuilderAsteroidTarget);
        }
        using (var w = new World())
        {
            w.HomeRock.SideVisibility.RevealToSide(0);
            w.Route(w.B, w.ChatOrder(w.Builder, w.HomeRock.Data.AsteroidId));
            Assert.Same(w.HomeRock, w.Builder.BuilderAsteroidTarget);
            Assert.Equal("Affirmative.", Assert.Single(w.ChatsTo(w.B)).Message);
        }
    }

    // ── F6 (3): native's decoy for a rock an enemy station replaced ───────

    /// <summary>⭐ Native decoy — when the ordered asteroid id no longer
    /// resolves because an enemy station consumed the rock, native finds that
    /// station, builds a buoy at its position, bypasses LegalCommand and still
    /// replies <c>"Affirmative."</c> (Imago 7/10, FedSrv.CPP:3213-3235), so
    /// the commander never learns that the rock is gone. Feasible here: the
    /// constructor receives a travel objective at the station's position.</summary>
    [SkippableFact]
    public void Replaced_rock_gets_the_native_decoy_affirmative()
    {
        Require.Artwork();
        using var w = new World();
        var station = w.Home.BuildStation(w.DecoyRock, w.EnemySide, w.StationType, 10d, new bool[w.Mission.Sides.Count]);
        Assert.False(station.SeenBySide(0), "precondition: the replacement station is unseen by side 0");
        Assert.DoesNotContain(w.DecoyRock, w.Home.Mineable);

        w.Route(w.A, w.Order(w.Builder, w.DecoyRock.Data.AsteroidId));

        var ack = Assert.Single(w.ChatsTo(w.A));
        Assert.Equal("Affirmative.", ack.Message);          // native pcReply, never a diagnostic
        Assert.NotNull(w.Builder.GotoWaypoint);
        Assert.Equal(w.Home.Id, w.Builder.GotoWaypoint!.Value.ClusterId);
        Assert.Equal(station.Center.X, w.Builder.GotoWaypoint.Value.Position.X, 1f);
        Assert.Equal(station.Center.Y, w.Builder.GotoWaypoint.Value.Position.Y, 1f);
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
        Console.WriteLine($"P1032-DECOY: asteroid {w.DecoyRock.Data.AsteroidId} replaced by station {station.StationId} — builder replies Affirmative and flies to ({station.Center.X:0},{station.Center.Y:0},{station.Center.Z:0})");
    }

    /// <summary>Native's decoy guard skips stations on the drone's OWN side
    /// (<c>if (pStation-&gt;GetSide() == pshipTo-&gt;GetSide()) continue;</c>,
    /// FedSrv.CPP:3223-3225). An own-side station on that rock is therefore no
    /// decoy: the order is refused silently and the constructor stays put.</summary>
    [SkippableFact]
    public void Replaced_rock_of_the_drones_own_side_is_not_a_decoy()
    {
        Require.Artwork();
        using var w = new World();
        var ownSide = w.Mission.Sides.Single(s => s.Id == 0);
        w.Home.BuildStation(w.DecoyRock, ownSide, w.StationType, 10d, new bool[w.Mission.Sides.Count]);

        w.Route(w.A, w.Order(w.Builder, w.DecoyRock.Data.AsteroidId));

        Assert.Empty(w.ChatsTo(w.A));
        Assert.Null(w.Builder.GotoWaypoint);
        Assert.Null(w.Builder.BuilderAsteroidTarget);
        Assert.Empty(w.Home.BuildingEffects);
    }

    /// <summary>Native wing echo — an accepted drone order is resent with
    /// <c>chatTarget = CHAT_INDIVIDUAL_ECHO</c> to wings 0/7/8/9
    /// (<c>ForwardChatMessageToWing</c>, FedSrv.CPP:3563-3570), so the
    /// commander's wingmates see the order. Same side, sender excluded, other
    /// side untouched.</summary>
    [SkippableFact]
    public void Accepted_drone_command_is_echoed_to_the_command_wings()
    {
        Require.Artwork();
        using var w = new World();
        w.HomeRock.SideVisibility.RevealToSide(0);
        w.Mate.WingId = 7;         // Charlie
        w.WingZeroMate.WingId = 0; // Command
        w.Enemy.WingId = 7;        // same wing number, other side: never receives

        w.Route(w.A, w.ChatOrder(w.Builder, w.HomeRock.Data.AsteroidId));
        Assert.Same(w.HomeRock, w.Builder.BuilderAsteroidTarget);

        var charlie = Assert.Single(w.ChatsTo(w.B));
        Assert.Equal((int)ChatTarget.IndividualEcho, charlie.ChatTarget);
        Assert.Equal(w.Commander.Id, charlie.SidSender);
        var command = Assert.Single(w.ChatsTo(w.D));
        Assert.Equal((int)ChatTarget.IndividualEcho, command.ChatTarget);
        Assert.Empty(w.ChatsTo(w.C));
        // The commander gets the drone's reply but no echo of his own order.
        Assert.All(w.ChatsTo(w.A), c => Assert.NotEqual((int)ChatTarget.IndividualEcho, c.ChatTarget));
    }
}
