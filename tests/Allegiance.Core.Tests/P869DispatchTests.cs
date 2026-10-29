using System.Reflection;
using System.Runtime.ExceptionServices;
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

[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P869DispatchTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly ServerConfig Config = new()
        {
            StaticCore = "PCore001.igc",
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff, MinimumPlayersPerTeam = 1 }, new GameConfig()],
        };
        public readonly Mission Mission, Other;
        public readonly Ship Owner, Mate, Enemy, Lobby, Outsider;
        public readonly List<(ConnectionId Peer, byte[] Msg)> Sent = [];
        public readonly ConnectionId A = new(1), B = new(2), C = new(3), L = new(4), X = new(5);
        public World()
        {
            Require.Artwork();
            var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
            Mission = Mission.Assemble(core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Other = Mission.Assemble(core, map, [new SideSpec(0, "Other", 1), new SideSpec(1, "Team", 3)], Host);
            Mission.Stage = Other.Stage = MissionStage.NotStarted;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Host.Transport, true);
            Set("_host", Host); Set("_config", Config); Set("_missions", new List<Mission> { Mission, Other });
            var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                with { HabmCapabilities = 0 };
            Ship Place(short id, short side, Mission m)
            {
                var ship = new Ship(id, hull, new Vector3(5000 + id, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = side };
                m.PlaceShip(ship, m.Sectors[0]); ship.InitClock(10); return ship;
            }
            Owner = Place(100, 0, Mission); Mate = Place(101, 0, Mission); Enemy = Place(102, 1, Mission);
            Lobby = Place(103, -2, Mission); Outsider = Place(104, 0, Other);
            Server.TestSeedPeers([(A, Owner, Owner.Sector!), (B, Mate, Mate.Sector!), (C, Enemy, Enemy.Sector!),
                (L, Lobby, Lobby.Sector!), (X, Outsider, Outsider.Sector!)]);
            Mission.MissionOwnerSideId = 0; Mission.Sides[0].LeaderShipId = Owner.Id; Mission.Sides[1].LeaderShipId = Enemy.Id;
            Server._testSendCapture = (c, p) => Sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
            Set("_simNow", 10d);
        }
        public void Set(string field, object value) => typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public T Get<T>(string field) => (T)typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public object? Call(string method, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public void Send(ConnectionId from, IFedMessage message) => Server.TestProcessDatagram(from, FedPacket.Pack([message.Encode()]));
        public IEnumerable<(ConnectionId Peer, byte[] Msg)> Messages(ushort fmid) => Sent.Where(s => FedMessageHeader.Read(s.Msg).Fmid == fmid);
        public void Chat(ConnectionId from, string text) => Send(from, new ChatMessage
        { Message = text, ChatTarget = (int)ChatTarget.Team, OtTarget = -1, OidTarget = -1, CommandID = -1 });
        public void Dispose() { Host.Transport!.Shutdown(); Server.Dispose(); }
    }


    // SIM-0631: FedSrv.CPP:7690-7715; fsmission.cpp:1495-1507.
    [SkippableTheory]
    [InlineData("bot-secret", 0, 1, true)]
    [InlineData("wrong", 0, 1, false)]
    [InlineData("", 0, 1, false)]
    [InlineData("bot-secret", -2, 1, false)]
    [InlineData("bot-secret", 0, -2, false)]
    [InlineData("bot-secret", 0, 6, false)]
    [InlineData("bot-secret", 0, 2, false)]
    public void Owner_transfer_requires_bot_role_real_sender_side_and_a_target_leader(string credential, short senderSide, short targetSide, bool transfers)
    {
        Require.Artwork(); using var w = new World();
        w.Config.BotAuthenticationSecret = "bot-secret";
        w.Owner.SideId = senderSide;
        w.Call("AuthenticatePrivileges", w.A, new LogonRequest { CDKey = credential });
        // Admin/harness authentication never implies bot ownership privileges.
        w.Get<HashSet<ConnectionId>>("_adminAuthenticated").Add(w.A);
        w.Get<HashSet<ConnectionId>>("_harnessAuthenticated").Add(w.A);
        w.Send(w.A, new SetMissionOwner { SideId = targetSide, ShipId = w.Outsider.Id });
        Assert.Equal(transfers ? targetSide : (short)0, w.Mission.MissionOwnerSideId);
        if (transfers)
        {
            Assert.Equal(new[] { w.A, w.B, w.C, w.L }, w.Messages(SetMissionOwner.Fmid).Select(m => m.Peer));
            Assert.All(w.Messages(SetMissionOwner.Fmid), m =>
            {
                var owner = SetMissionOwner.Decode(m.Msg);
                Assert.Equal((short)1, owner.SideId); Assert.Equal(w.Enemy.Id, owner.ShipId);
            });
        }
        else Assert.Empty(w.Messages(SetMissionOwner.Fmid));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_validated_logon_binds_bot_role_and_sends_catalog(bool valid)
    {
        Require.Artwork(); using var w = new World();
        w.Config.BotAuthenticationSecret = "bot-secret";
        var peer = new ConnectionId(90);
        w.Set("_staticMaps", new StaticMapRecord[] { new("arena", "Arena", 2) });
        w.Send(peer, new LogonRequest { CharacterName = "P869 bot", CDKey = "bot-secret",
            FedsrvVer = valid ? LogonRequest.MsgVer : (ushort)0 });
        Assert.Equal(valid, w.Get<HashSet<ConnectionId>>("_botAuthenticated").Contains(peer));
        Assert.Equal(valid ? 1 : 0, w.Messages(StaticMapInfo.Fmid).Count());
        if (valid)
        {
            var map = Assert.Single(StaticMapInfo.Decode(Assert.Single(w.Messages(StaticMapInfo.Fmid)).Msg).Maps);
            Assert.Equal("arena", map.FileName);
            Assert.True(w.Get<Dictionary<ConnectionId, int>>("_peerStandaloneIds")[peer] > 0);
        }
    }

    [SkippableFact]
    public void File_catalog_preserves_order_and_provisions_only_matching_team_selection()
    {
        Require.Artwork(); using var w = new World();
        string folder = Path.Combine(Path.Combine(Environment.CurrentDirectory, ".scratch"), "p869-maps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "maps.txt"), "2\n6,Six,arena6\n2,Arena,arena2\n");
            w.Config.ArtPath = folder; w.Call("LoadStaticMapCatalog");
            var maps = w.Get<StaticMapRecord[]>("_staticMaps");
            Assert.Equal(new[] { "arena6", "arena2" }, maps.Select(m => m.FileName));
            Assert.Equal("arena2.igc", w.Call("ResolveCatalogMap", new GameConfig { Map = "Arena", Teams = 2 }));
            Assert.Equal("arena6.igc", w.Call("ResolveCatalogMap", new GameConfig { Map = "arena6", Teams = 6 }));
            Assert.Equal("Arena", w.Call("ResolveCatalogMap", new GameConfig { Map = "Arena", Teams = 6 }));
            File.Delete(Path.Combine(folder, "maps.txt")); w.Call("LoadStaticMapCatalog");
            Assert.Empty(w.Get<StaticMapRecord[]>("_staticMaps"));
        }
        finally { Directory.Delete(folder, true); }
    }

    // SIM-0739/0740: a wire command reads the mission registry through
    // FindShipAnySide, even when the target has no cluster and no peer.
    [SkippableFact]
    public void Wire_target_resolution_reads_unclustered_mission_ship_and_reflects_registry_removal()
    {
        Require.Artwork(); using var w = new World();
        w.Mission.Stage = MissionStage.Started;
        var npc = new Ship(30001, w.Owner.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1, IsNpc = true };
        w.Mission.PlaceShip(npc, w.Mission.Sectors[1]);
        w.Mission.Sectors[1].Remove(npc); npc.Sector = null;
        void Target() => w.Send(w.A, new OrderChange { ShipID = w.Owner.Id, ObjectID = npc.Id,
            ObjectType = IgcObjectType.Ship, Command = OrderChange.CmdCurrent, CommandId = OrderChange.CidAttack });
        Target(); Assert.Same(npc, w.Owner.CommandTarget);
        w.Mission.DeleteShip(npc);
        Target(); Assert.Null(w.Owner.CommandTarget);
        w.Mission.AddShip(npc);
        Target(); Assert.Same(npc, w.Owner.CommandTarget);
        // The native join PLAYERINFO walk consumes GetShips, including this
        // unclustered NPC, rather than rediscovering ships through sectors.
        var ownLive = w.Server.Ships.Single(s => ReferenceEquals(s.Ship, w.Owner));
        w.Call("CompleteJoinAndLaunch", w.A, new LogonRequest { CharacterName = "Owner" }, w.Owner.Id, (byte)0, ownLive, true, false);
        Assert.Contains(w.Messages(PlayerInfo.Fmid).Where(m => m.Peer == w.A), m => PlayerInfo.Decode(m.Msg).ShipId == npc.Id);
    }

    [SkippableTheory]
    [InlineData("bad\n")]
    [InlineData("-1\n")]
    [InlineData("999999999\n")]
    [InlineData("2\nmalformed\n2,Arena,arena2\n")]
    [InlineData("2\n2,Arena,arena2\n")]
    public void Malformed_catalog_does_not_abort_boot_and_keeps_readable_entries(string text)
    {
        Require.Artwork();
        string folder = Path.Combine(Environment.CurrentDirectory, ".scratch", "p869-maps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(TestArtRoot.Value, "PCore001.igc"), Path.Combine(folder, "PCore001.igc"));
            File.Copy(Path.Combine(TestArtRoot.Value, "XXV_simple.igc"), Path.Combine(folder, "XXV_simple.igc"));
            File.WriteAllText(Path.Combine(folder, "maps.txt"), text);
            var config = UpdateCadenceTests.Config(); config.ArtPath = folder;
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            Assert.NotEmpty(server.Missions);
            var maps = (StaticMapRecord[])typeof(Srv).GetField("_staticMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            Assert.Equal(text.Contains("Arena") ? 1 : 0, maps.Length);
        }
        finally { Directory.Delete(folder, true); }
    }

    [SkippableFact]
    public void Standalone_rejoin_reuses_numeric_identity_and_retained_score()
    {
        Require.Artwork(); using var w = new World();
        string name = "P869 score";
        var first = new ConnectionId(90); var second = new ConnectionId(91);
        // Use the production spawner seam solely for deterministic placement;
        // logon, selection, score binding, departure and restoration are real.
        w.Server.ShipSpawner = (core, mission, id) =>
        {
            var ship = new Ship(id, core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]); return (ship, mission.Sectors[0]);
        };
        void Join(ConnectionId peer)
        {
            w.Send(peer, new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer });
            Assert.True(LogonAck.Decode(w.Messages(LogonAck.Fmid).Last(m => m.Peer == peer).Msg).FValidated);
            w.Send(peer, new PositionReq { SideId = 0 });
            w.Call("AdvanceSideSelection", 10d);
        }
        Join(first);
        var live = w.Server.Ships.Single(s => s.Ship.Id == LogonAck.Decode(w.Messages(LogonAck.Fmid).Last().Msg).ShipID);
        Assert.Equal((short)0, live.Ship.SideId);
        int id = w.Get<Dictionary<ConnectionId, int>>("_peerStandaloneIds")[first];
        Assert.True(id > 0);
        var score = live.PlayerScore;
        score.AddKill();
        w.Call("ReleasePeerWithReason", first, QuitSideReason.Quit, true, "");
        Assert.False(w.Get<Dictionary<ConnectionId, int>>("_peerStandaloneIds").ContainsKey(first));
        Assert.Same(score, w.Get<Dictionary<(Mission, string), PlayerScore>>("_oldPlayerScores")[(w.Mission, name)]);
        Join(second);
        var restored = w.Server.Ships.Single(s => s.Ship.Id == LogonAck.Decode(w.Messages(LogonAck.Fmid).Last().Msg).ShipID);
        Assert.NotEqual(live.Ship.Id, restored.Ship.Id);
        Assert.Equal(id, w.Get<Dictionary<ConnectionId, int>>("_peerStandaloneIds")[second]);
        Assert.Same(score, restored.PlayerScore); Assert.Equal(1, restored.PlayerScore.Kills);
        Assert.False(w.Get<Dictionary<(Mission, string), PlayerScore>>("_oldPlayerScores").ContainsKey((w.Mission, name)));
        var ownInfo = w.Messages(PlayerInfo.Fmid).Where(m => m.Peer == second).Select(m => PlayerInfo.Decode(m.Msg))
            .Last(m => m.ShipId == restored.Ship.Id);
        Assert.Equal(1, ownInfo.NKills);
    }
}
