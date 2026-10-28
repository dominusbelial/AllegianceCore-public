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

/// <summary>Exercise actual inbound dispatch, effects and exact recipients.
/// Native case/branch citations are in docs/parity/p867-onappmessage.csv.</summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P867DispatchCertificationTests
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

    // FedSrv.CPP:7025 (mission group, not side group).
    [SkippableFact]
    public void Player_ready_reaches_enemy_and_lobby_but_never_another_mission()
    {
        using var w = new World();
        w.Send(w.A, new PlayerReady { ShipId = w.Owner.Id, FReady = true });
        Assert.Equal(new[] { w.A, w.B, w.C, w.L }, w.Messages(PlayerReady.Fmid).Select(s => s.Peer).ToArray());
        Assert.True(w.Get<Dictionary<ConnectionId, bool>>("_playerReady")[w.A]);
    }

    // FedSrv.CPP:7375 (side group belongs to this mission).
    [SkippableFact]
    public void Highlight_is_scoped_to_the_senders_mission_and_side()
    {
        using var w = new World();
        w.Send(w.A, new HighlightCluster { ClusterId = -123, Highlight = true });
        Assert.Equal(new[] { w.A, w.B }, w.Messages(HighlightCluster.Fmid).Select(s => s.Peer).ToArray());
        Assert.All(w.Messages(HighlightCluster.Fmid), s => Assert.Equal((short)-123, HighlightCluster.Decode(s.Msg).ClusterId));
    }

    // FedSrv.CPP:7610 calls the already certified FAllReady, whose early return
    // at fsmission.cpp:4432 makes the following Ready loop unreachable.
    [SkippableTheory]
    [InlineData(1, 100, 0, false)] // imbalance
    [InlineData(2, 100, 32767, false)] // below min
    [InlineData(1, 1, 32767, false)] // above max
    [InlineData(1, 100, 32767, true)] // balanced by policy, both sides UNREADY
    public void Start_game_uses_native_balance_policy(int min, int max, int imbalance, bool starts)
    {
        using var w = new World();
        w.Config.Games[0].MinimumPlayersPerTeam = min; w.Config.Games[0].MaxPlayersPerTeam = max;
        w.Config.Games[0].MaxTeamImbalance = imbalance;
        Assert.All(w.Mission.Sides, s => s.Ready = false);
        w.Send(w.A, new StartGame());
        Assert.Equal(starts ? MissionStage.Starting : MissionStage.NotStarted, w.Mission.Stage);
        Assert.Equal(starts ? 4 : 0, w.Messages(SetStartTime.Fmid).Count());
    }

    // FedSrv.CPP:7720: this is a silent gate even for cancellation (sid=NA).
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(-2)] // artifact
    public void Flag_or_artifact_carrier_cannot_activate_or_cancel_ripcord(short flag)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        var anchor = new Ship.RipcordTarget(1, 42, w.Mission.Sectors[0].Id);
        w.Owner.SetRipcordModel(anchor); w.Owner.Flag = flag;
        w.Send(w.A, new RipcordRequest { SidRipcord = -1 });
        Assert.Equal(anchor, w.Owner.RipcordModel);
        Assert.Empty(w.Messages(RipcordAborted.Fmid));
        w.Owner.Flag = null;
        w.Send(w.A, new RipcordRequest { SidRipcord = -1 });
        Assert.Null(w.Owner.RipcordModel);
        Assert.NotEmpty(w.Messages(RipcordAborted.Fmid));
    }

    // FedSrv.CPP:5323-5324: lower bound intentionally commented out.
    [SkippableTheory]
    [InlineData(-1)]
    [InlineData(-32768)]
    [InlineData(9)]
    [InlineData(10)]
    public void Set_wing_uses_the_native_upper_bound_only(short wing)
    {
        using var w = new World(); w.Owner.WingId = 5;
        w.Send(w.A, new SetWingId { ShipID = w.Owner.Id, WingId = wing, BCommanded = true });
        Assert.Equal(wing < 10 ? wing : (short)5, w.Owner.WingId);
        Assert.Equal(wing < 10 ? 2 : 0, w.Messages(SetWingId.Fmid).Count());
        Assert.All(w.Messages(SetWingId.Fmid), s => Assert.False(SetWingId.Decode(s.Msg).BCommanded));
    }

    // FedSrv.CPP:6057-6082, newer/older client paths both fRetry=false.
    [SkippableTheory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Version_mismatch_is_not_retryable(int delta)
    {
        using var w = new World();
        w.Send(new ConnectionId(90), new LogonRequest
        { CharacterName = "Mismatch", CDKey = "test", FedsrvVer = (ushort)(LogonRequest.MsgVer + delta) });
        var ack = LogonAck.Decode(Assert.Single(w.Messages(LogonAck.Fmid)).Msg);
        Assert.False(ack.FValidated); Assert.False(ack.FRetry);
        Assert.Empty(w.Get<System.Collections.IDictionary>("_pendingSideSelect"));
    }

    // FedSrv.CPP:7749-7765 includes the requester. Existing P654 mission privacy
    // policy is retained and explicitly classified in the audit.
    [SkippableFact]
    public void Ping_roster_includes_requester_and_debounces_to_marker()
    {
        using var w = new World(); w.Send(w.A, new ReqPingData());
        var entries = w.Messages(PingData.Fmid).Select(s => PingData.Decode(s.Msg)).ToArray();
        Assert.Equal(new short[] { 100, 101, 102, 103, -1 }, entries.Select(e => e.ShipId));
        Assert.All(w.Messages(PingData.Fmid), s => Assert.Equal(w.A, s.Peer));
        w.Sent.Clear(); w.Send(w.A, new ReqPingData());
        var marker = PingData.Decode(Assert.Single(w.Messages(PingData.Fmid)).Msg);
        Assert.Equal((short)-1, marker.ShipId); Assert.Equal(1u, marker.Ping);
    }

    // FedSrv.CPP:6102 and :6148: negative side cannot receive the offered part,
    // but the response consumes its pending offer.
    [SkippableFact]
    public void Team_lobby_treasure_ack_clears_offer_without_mount_or_drop()
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        var part = w.Mission.Core.PartTypes.Values.First();
        w.Lobby.PendingTreasureOffer = (part.PartId, 1);
        w.Send(w.L, new TreasureAck { MountId = TreasureAck.MountNA });
        Assert.Null(w.Lobby.PendingTreasureOffer); Assert.Empty(w.Lobby.Parts);
        Assert.Empty(w.Sent);
    }

    // FedSrv.CPP:6972-6982 and fsmission.cpp:5069-5118.
    [SkippableFact]
    public void Randomize_flushes_non_leaders_and_disables_autoaccept_in_non_balance_mode()
    {
        using var w = new World(); w.Mission.LockSides = true;
        w.Config.Games[0].MaxTeamImbalance = 1;
        foreach (var s in w.Mission.Sides) s.AutoAccept = true;
        w.Send(w.A, new RandomizeTeams());
        Assert.Equal((short)-2, w.Mate.SideId); Assert.Equal((short)-2, w.Lobby.SideId);
        Assert.Equal((short)0, w.Owner.SideId); Assert.Equal((short)1, w.Enemy.SideId);
        Assert.Equal((short)0, w.Outsider.SideId);
        Assert.All(w.Mission.Sides, s => Assert.False(s.AutoAccept));
        Assert.All(w.Messages(QuitSide.Fmid), s => Assert.Equal(QuitSideReason.FlushSides, QuitSide.Decode(s.Msg).Reason));
        Assert.Equal(4, w.Messages(QuitSide.Fmid).Count());
        Assert.DoesNotContain(w.Sent, s => s.Peer == w.X);
    }

    // FedSrv.CPP:6929 and :6944 (immutable server policy).
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Locked_open_games_ignore_autoaccept_and_lock_lobby(bool lockedOpen)
    {
        using var w = new World(); w.Server.TestParamsOf(w.Mission)[MissionParamsMsg.BitLockGameOpen] = lockedOpen;
        w.Mission.Sides[0].AutoAccept = true;
        w.Send(w.A, new AutoAccept { SideId = 0, FAutoAccept = false });
        w.Send(w.A, new LockLobby { FLock = true });
        Assert.Equal(lockedOpen, w.Mission.Sides[0].AutoAccept);
        Assert.Equal(!lockedOpen, w.Mission.LockLobby);
        Assert.Equal(lockedOpen ? 0 : 4, w.Messages(AutoAccept.Fmid).Count());
        Assert.Equal(lockedOpen ? 0 : 4, w.Messages(LockLobby.Fmid).Count());
    }

    // FedSrv.CPP:5198-5204: started-only leader control; original team chat
    // remains public to its original recipients; mission def goes to all.
    [SkippableTheory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Autoaccept_chat_has_native_stage_and_leader_gates(bool started, bool leader, bool staysOn)
    {
        using var w = new World(); w.Mission.Stage = started ? MissionStage.Started : MissionStage.NotStarted;
        w.Mission.Sides[0].AutoAccept = true;
        w.Chat(leader ? w.A : w.B, "#AuToAcCePt");
        Assert.Equal(staysOn, w.Mission.Sides[0].AutoAccept);
        Assert.Equal(staysOn ? 0 : 4, w.Messages(MissionDef.Fmid).Count());
        Assert.NotEmpty(w.Messages(ChatMessage.Fmid));
    }

    // FedSrv.CPP:5207-5223: owner controls 1/2/a/n; public echo + truthful def.
    [SkippableTheory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("a", 32766)]
    [InlineData("n", 32767)]
    [InlineData("?", 32767)]
    public void Autobalance_chat_updates_policy_and_advertisement(string arg, short expected)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        w.Chat(w.A, "#autobalance " + arg);
        Assert.Equal(expected, w.Config.Games[0].MaxTeamImbalance);
        Assert.Equal(expected, w.Server.TestParamsOf(w.Mission).IMaxImbalance);
        Assert.Equal(4, w.Messages(MissionDef.Fmid).Count());
        Assert.Contains(w.Messages(ChatMessage.Fmid), s => s.Peer == w.C);
        Assert.All(w.Messages(ChatMessage.Fmid), s => Assert.Equal((int)ChatTarget.Everyone, ChatMessage.Decode(s.Msg).ChatTarget));
    }

    [SkippableFact]
    public void Autobalance_and_list_respect_native_sender_and_stage_gates()
    {
        using var w = new World();
        w.Chat(w.A, "#autobalance 1"); Assert.Empty(w.Messages(MissionDef.Fmid));
        w.Mission.Stage = MissionStage.Started; w.Sent.Clear();
        w.Chat(w.B, "#autobalance 1"); Assert.Empty(w.Messages(MissionDef.Fmid));
        Assert.Equal(32767, w.Config.Games[0].MaxTeamImbalance);
        w.Sent.Clear(); w.Chat(w.B, "#list");
        var reply = Assert.Single(w.Messages(ChatMessage.Fmid));
        Assert.Equal(w.B, reply.Peer); Assert.Equal((short)-1, ChatMessage.Decode(reply.Msg).SidSender);
        Assert.Equal(OrderChange.CidNone, ChatMessage.Decode(reply.Msg).CommandID); Assert.Contains("#autobalance [1|2|a|n]", ChatMessage.Decode(reply.Msg).Message);
        w.Sent.Clear(); w.Chat(w.L, "#list");
        Assert.DoesNotContain(w.Messages(ChatMessage.Fmid), s => ChatMessage.Decode(s.Msg).Message.Contains("[1|2|a|n]"));
    }

    // FedSrv.CPP:5127-5166: exact case-insensitive lobby-only ban by owner.
    [SkippableFact]
    public void Ban_chat_is_owner_only_and_removes_only_a_named_lobby_player()
    {
        using var w = new World();
        w.Chat(w.B, "#ban seed-4"); Assert.Contains(w.Server.Ships, s => s.Ship == w.Lobby);
        w.Chat(w.A, "#ban seed-3"); Assert.Contains(w.Server.Ships, s => s.Ship == w.Enemy);
        w.Sent.Clear(); w.Chat(w.A, "#BAN SEED-4");
        Assert.DoesNotContain(w.Server.Ships, s => s.Ship == w.Lobby);
        Assert.Contains(w.Messages(QuitMission.Fmid), s => s.Peer == w.L && QuitMission.Decode(s.Msg).Reason == QuitSideReason.OwnerBooted);
        Assert.Empty(w.Messages(ChatMessage.Fmid));
    }

    // FedSrv.CPP:6735-6736: CheckPositionRequest BEFORE RemoveJoinRequest.
    [SkippableFact]
    public void Position_ack_rechecks_before_removing_pending_request()
    {
        using var w = new World();
        var pending = w.Get<Dictionary<ConnectionId, (short ShipId, short SideId)>>("_pendingJoinRequests");
        pending[w.L] = (w.Lobby.Id, 0); w.Mission.LockSides = true;
        w.Send(w.A, new PositionAck { ShipId = w.Lobby.Id, SideId = 0, FAccepted = true });
        Assert.Contains(w.L, pending.Keys); Assert.Equal((short)-2, w.Lobby.SideId);
        w.Mission.LockSides = false;
        w.Send(w.A, new PositionAck { ShipId = w.Lobby.Id, SideId = 0, FAccepted = true });
        Assert.DoesNotContain(w.L, pending.Keys); Assert.Equal((short)0, w.Lobby.SideId);
    }

    // FedSrv.CPP:6744: rejection removes the player's current request, even
    // when the pending destination no longer matches the leader's ACK.
    [SkippableFact]
    public void Position_ack_rejection_clears_a_request_that_moved_to_another_side()
    {
        using var w = new World();
        var pending = w.Get<Dictionary<ConnectionId, (short ShipId, short SideId)>>("_pendingJoinRequests");
        pending[w.L] = (w.Lobby.Id, 1);
        w.Send(w.A, new PositionAck { ShipId = w.Lobby.Id, SideId = 0, FAccepted = false });
        Assert.DoesNotContain(w.L, pending.Keys);
        Assert.All(w.Messages(DelPositionReq.Fmid), s => Assert.Equal((short)1, DelPositionReq.Decode(s.Msg).SideId));
        Assert.Equal(4, w.Messages(DelPositionReq.Fmid).Count());
    }
    // FedSrv.CPP:6429-6446 uses SeenBySide even for an enemy or NPC target.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void View_cluster_uses_actual_visibility_instead_of_ownership(bool visible)
    {
        using var w = new World(); w.Owner.StationId = 42;
        w.Enemy.SideVisibility.SetSideVisibility(0, visible);
        w.Send(w.A, new ViewClusterRequest
        { ClusterId = w.Enemy.Sector!.Id, OtTarget = (short)IgcObjectType.Ship, OidTarget = w.Enemy.Id });
        var reply = ViewClusterReply.Decode(Assert.Single(w.Messages(ViewClusterReply.Fmid)).Msg);
        Assert.Equal(visible, reply.BUsePosition);
        Assert.Equal(visible ? w.Enemy.Position : Vector3.Zero, reply.Position);
        // The same visibility rule applies to a target that has no peer entry.
        w.Sent.Clear();
        var npc = new Ship(300, w.Owner.HullType, w.Owner.Position, Vector3.Zero, Orientation.Identity)
        { IsNpc = true, SideId = 0 };
        w.Mission.PlaceShip(npc, w.Owner.Sector!); npc.SideVisibility.SetSideVisibility(0, visible);
        w.Send(w.A, new ViewClusterRequest
        { ClusterId = npc.Sector!.Id, OtTarget = (short)IgcObjectType.Ship, OidTarget = npc.Id });
        Assert.Equal(visible, ViewClusterReply.Decode(Assert.Single(w.Messages(ViewClusterReply.Fmid)).Msg).BUsePosition);
    }

    // FedSrv.CPP:6388-6389 stage refusal is silent, unlike a failed attempt.
    [SkippableFact]
    public void Board_ship_before_start_is_silent_but_missing_parent_after_start_nacks()
    {
        using var w = new World();
        w.Send(w.A, new BoardShip { SidParent = 300 }); Assert.Empty(w.Sent);
        w.Mission.Stage = MissionStage.Started;
        w.Send(w.A, new BoardShip { SidParent = 300 });
        var nack = Assert.Single(w.Messages(BoardNack.Fmid));
        Assert.Equal(w.A, nack.Peer); Assert.Equal((short)300, BoardNack.Decode(nack.Msg).SidRequestedParent);
    }

    // FedSrv.CPP:6374-6377: a docked promote uses the side group, not sector.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Promote_routes_by_flying_or_docked_state(bool docked)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        w.Owner.StationId = w.Mate.StationId = docked ? (short)42 : null;
        w.Mate.ParentShip = w.Owner; w.Mate.TurretId = 0; w.Owner.AddChildShip(w.Mate);
        w.Send(w.A, new PromoteRequest { MountidPromoted = 0 });
        Assert.Null(w.Mate.ParentShip); Assert.Same(w.Mate, w.Owner.ParentShip);
        var recipients = w.Messages(Promote.Fmid).Select(s => s.Peer).ToArray();
        Assert.Contains(w.A, recipients); Assert.Contains(w.B, recipients);
        Assert.Equal(!docked, recipients.Contains(w.C)); Assert.DoesNotContain(w.X, recipients);
    }

    // FedSrv.CPP:7290-7301, 7323-7328: all three NPC order branches acknowledge.
    [SkippableTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Drone_orders_acknowledge_stay_launch_and_return(bool docked, bool stay)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        var npc = new Ship(300, w.Owner.HullType, w.Owner.Position, Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = 0, StationId = docked ? (short)42 : null };
        w.Mission.PlaceShip(npc, w.Owner.Sector!);
        w.Send(w.A, new OrderChange
        { ShipID = npc.Id, Command = OrderChange.CmdAccepted, CommandId = stay ? OrderChange.CidDoNothing : OrderChange.CidGoto });
        var replies = w.Messages(ChatMessage.Fmid).Where(m => m.Peer == w.A).Select(m => ChatMessage.Decode(m.Msg)).ToArray();
        Assert.Contains(replies, r => r.SidSender == npc.Id && r.VoiceOver == 1178 && r.CommandID == OrderChange.CidNone
            && r.Message == (docked && !stay ? "Affirmative. Preparing to launch." : "Affirmative."));
        Assert.Equal(stay || !docked, npc.StayDocked);
        if (!docked)
        {
            Assert.NotNull(npc.AcceptedTarget);
            Assert.Contains(replies, r => r.Message == "Heading back to base. I'll sit tight.");
            Assert.DoesNotContain(w.Messages(ChatMessage.Fmid), m => m.Peer == w.C || m.Peer == w.X);
        }
    }

    // FedSrv.CPP:7307-7320: carrier search prefers CapLand, including across
    // clusters; no shipyard falls back to repair. Non-carriers choose repair.
    [SkippableTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Return_to_base_selects_carrier_shipyard_or_repair_across_clusters(bool carrier, bool shipyard)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        var template = w.Mission.Sectors.SelectMany(s => s.Stations).First();
        foreach (var sector in w.Mission.Sectors)
            foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
        var repairType = w.Mission.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRepair)
            && !t.HasCapability(DataStationTypeIGC.SabmCapLand));
        var yardType = w.Mission.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmCapLand));
        var remote = w.Mission.Sectors.First(s => s != w.Owner.Sector);
        var repair = template with { StationId = 310, StationTypeId = repairType.StationTypeId, SideId = 0,
            ClusterId = remote.Id, Position = new float[] { w.Owner.Position.X, w.Owner.Position.Y, w.Owner.Position.Z } };
        w.Mission.PlaceStation(new Station(repair, repairType), remote);
        if (shipyard)
        {
            var yard = template with { StationId = 311, StationTypeId = yardType.StationTypeId, SideId = 0,
                ClusterId = remote.Id, Position = new float[] { w.Owner.Position.X + 1000, w.Owner.Position.Y, w.Owner.Position.Z } };
            w.Mission.PlaceStation(new Station(yard, yardType), remote);
        }
        var npc = new Ship(300, w.Owner.HullType, w.Owner.Position, Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = carrier ? Ship.PilotTypeCarrier : Ship.PilotTypeMiner, SideId = 0 };
        w.Mission.PlaceShip(npc, w.Owner.Sector!);
        w.Send(w.A, new OrderChange { ShipID = npc.Id, Command = OrderChange.CmdAccepted, CommandId = OrderChange.CidGoto });
        Assert.NotNull(npc.AcceptedTarget);
        Assert.Equal((short)(carrier && shipyard ? 311 : 310), npc.AcceptedTarget!.Value.StationId);
    }

    // FedSrv.CPP:7811-7814, P855 soft-boot design retains the player.
    [SkippableFact]
    public void Unknown_message_from_player_emits_the_native_trap_record()
    {
        using var w = new World();
        using var capture = new StringWriter(); var original = Console.Out;
        try
        {
            Console.SetOut(capture);
            w.Server.TestProcessDatagram(w.A, FedPacket.Pack([new byte[] { 4, 0, 254, 255 }]));
        }
        finally { Console.SetOut(original); }
        Assert.Contains("[traphack] kind=boot check=\"false\" site=FedSrv.CPP:7814", capture.ToString());
        Assert.Contains(w.Server.Ships, s => s.Ship == w.Owner);
    }

    [SkippableFact]
    public void Mission_params_balance_mode_controls_the_next_randomize_request()
    {
        using var w = new World();
        var edit = MissionParamsMsg.Decode(w.Server.TestParamsOf(w.Mission).Encode());
        edit.IGoalConquestPercentage = 100; edit.IGoalTerritoryPercentage = 100;
        edit.IMaxImbalance = 1;
        w.Send(w.A, edit);
        Assert.Equal(1, w.Config.Games[0].MaxTeamImbalance);
        Assert.Equal((short)1, w.Server.TestParamsOf(w.Mission).IMaxImbalance);
        Assert.Equal(4, w.Messages(MissionDef.Fmid).Count());
        w.Send(w.A, new RandomizeTeams()); Assert.Equal((short)-2, w.Mate.SideId);
    }

    [SkippableFact]
    public void Chat_from_a_ship_without_a_mission_has_no_effect_or_relay()
    {
        using var w = new World();
        var orphan = new Ship(400, w.Owner.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        w.Server.TestSeedPeers([(w.A, orphan, w.Owner.Sector!), (w.B, w.Mate, w.Mate.Sector!)]);
        w.Chat(w.A, "#ban seed-2"); Assert.Empty(w.Sent);
        Assert.Equal(2, w.Server.Ships.Count());
    }

    // FedSrv.CPP:6405: live same-side parents can still be ghosts.
    [SkippableFact]
    public void Boarding_a_ghost_parent_nacks_without_changing_seats()
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started; w.Mission.Lives = 0;
        w.Server.Ships.Single(s => s.Ship == w.Mate).PlayerScore.Deaths = 1;
        w.Owner.StationId = w.Mate.StationId = 42;
        w.Send(w.A, new BoardShip { SidParent = w.Mate.Id });
        Assert.Null(w.Owner.ParentShip);
        Assert.Equal(w.A, Assert.Single(w.Messages(BoardNack.Fmid)).Peer);
    }

    // FedSrv.CPP:7335-7338: side guard + mission-wide GetModel on both slots.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Player_command_resolves_remote_static_targets_for_current_and_accepted_slots(bool accepted)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        var sector = w.Mission.Sectors.First(s => s != w.Owner.Sector && s.Stations.Count > 0);
        var station = sector.Stations[0];
        var command = new OrderChange { ShipID = w.Owner.Id, Command = accepted ? OrderChange.CmdAccepted : OrderChange.CmdCurrent,
            CommandId = OrderChange.CidGoto, ObjectType = IgcObjectType.Station, ObjectID = station.StationId };
        w.Send(w.A, command);
        var target = Assert.IsType<EquipmentTarget>(accepted ? w.Owner.AcceptedShipTarget : w.Owner.CommandTarget);
        Assert.Equal(station.StationId, target.Id); Assert.Equal(sector, target.OwnerSector);
        command.ShipID = w.Lobby.Id; w.Send(w.L, command);
        Assert.Null(accepted ? w.Lobby.AcceptedShipTarget : w.Lobby.CommandTarget);
    }

    // FedSrv.CPP:7063-7077: restart-capable allied bases are valid too.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Teleport_accepts_an_allied_restart_station_and_refuses_an_enemy(bool allied)
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        w.Owner.StationId = w.Mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == 0).StationId;
        var station = w.Mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == 1
            && w.Mission.Core.StationTypes[st.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart));
        w.Mission.Sides[0].AllyGroup = 0; w.Mission.Sides[1].AllyGroup = allied ? (sbyte)0 : null;
        w.Send(w.A, new Docked { StationId = station.StationId });
        var ack = TeleportAck.Decode(Assert.Single(w.Messages(TeleportAck.Fmid)).Msg);
        Assert.Equal(allied ? station.StationId : (short)-1, ack.StationId);
        if (allied) Assert.Equal(station.StationId, w.Owner.StationId);
    }

    // Inc/Messages.h:311-317 tail: 6 short leaders, then six char arrays.
    [SkippableFact]
    public void Autoaccept_chat_announces_the_true_tail_including_a_toggle_back_on()
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        w.Mission.Sides[0].AutoAccept = false; w.Mission.Sides[1].ForceReady = true;
        w.Mission.Sides[0].Ready = true; w.Mission.Sides[1].Active = false;
        w.Mission.Sides[0].AllyGroup = 2; w.Mission.LockSides = true;
        w.Chat(w.A, "#autoaccept");
        var bytes = w.Messages(MissionDef.Fmid).First().Msg;
        Assert.Equal((byte)2, bytes[826]); Assert.Equal((byte)1, bytes[827]);
        Assert.Equal((byte)1, bytes[832]); Assert.Equal((byte)1, bytes[838]);
        Assert.Equal((byte)1, bytes[845]); Assert.Equal((byte)0, bytes[851]);
        Assert.Equal((byte)2, bytes[856]); Assert.Equal((byte)255, bytes[857]);
        var md = MissionDef.Decode(bytes);
        Assert.True(md.RgfAutoAccept[0]); Assert.True(md.RgfForceReady[1]);
        Assert.True(md.Params![MissionParamsMsg.BitLockSides]);
        Assert.Equal(bytes, md.Encode());
        w.Sent.Clear(); w.Chat(w.A, "#autoaccept");
        Assert.All(w.Messages(MissionDef.Fmid), m => Assert.False(MissionDef.Decode(m.Msg).RgfAutoAccept[0]));
    }

}
