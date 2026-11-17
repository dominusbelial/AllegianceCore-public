using Allegiance.Core.Tests.Transport;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>R1b F1: real boot/logon/side-select and SET_TEAM_INFO dispatch,
/// with verified identity claims supplied through the existing lobby test seam.</summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public class P1033SquadSideTests
{
    private readonly bool _webSocket;
    public P1033SquadSideTests() { }
    protected P1033SquadSideTests(bool webSocket) => _webSocket = webSocket;

    private static readonly SquadClaim Alpha = new("alpha", "Squad Alpha", true);
    private static readonly SquadClaim Beta = new("beta", "Squad Beta", true);

    private sealed class World : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(int Peer, byte[] Message)> Sent = [];
        public Mission Mission => Server.Missions[0];
        public Side Side => Mission.Sides[0];

        private readonly WebSocketRegressionIngress? _ingress;
        public World(bool webSocket, bool squadGame = true)
        {
            Require.Artwork();
            Server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Games = [new GameConfig
                {
                    Name = "P1033", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                    SideNames = ["Alpha", "Beta"], SquadGame = squadGame, AutoStart = false,
                }],
            });
            if (webSocket) _ingress = new WebSocketRegressionIngress(Server, 5);
            Server._testSendCapture = (peer, packet) => Sent.AddRange(
                FedPacket.Unpack(packet).Select(m => ((int)peer.Value, m)));
        }

        public void Send(int peer, IFedMessage message)
        {
            var packet = FedPacket.Pack([message.Encode()]);
            if (_ingress is null) Server.TestProcessDatagram(new ConnectionId((uint)peer), packet);
            else _ingress.Send(new ConnectionId((uint)peer), packet);
            // Initial POSITIONREQs drain in logon order on the next server tick.
            if (message is PositionReq) Server.TickSim();
            Server.FlushBroadcastSnapshots();
        }

        public IEnumerable<byte[]> Messages(int peer, ushort fmid) => Sent
            .Where(m => m.Peer == peer && FedMessageHeader.Read(m.Message).Fmid == fmid).Select(m => m.Message);

        public short Logon(int peer, SquadClaim[]? squads, bool verified = true)
        {
            string name = $"Pilot{peer}";
            Send(peer, new LogonRequest
            {
                CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue,
                Time = 1, CrcFileList = -1, SteamAuthTicket = new byte[16], DrmHash = "p1033",
            });
            var ack = LogonAck.Decode(Assert.Single(Messages(peer, LogonAck.Fmid)));
            Assert.True(ack.FValidated, ack.CharNameOrFailureReason);
            if (verified)
                Server.TestSetIdentity(new ConnectionId((uint)peer), new JoinIdentity(
                    "https://lobby.test", "game", $"account{peer}", name, 0, long.MaxValue, $"token{peer}", squads));
            return ack.ShipID;
        }

        public Ship Seat(int peer, SquadClaim[]? squads, short side = 0, bool verified = true)
        {
            short id = Logon(peer, squads, verified);
            Send(peer, new PositionReq { SideId = side });
            return Assert.Single(Server.Ships, s => s.Ship.Id == id).Ship;
        }

        // Isolate the initial-membership regression from the initial-leader regression.
        public void RestrictOccupiedSide()
        {
            Mission.SquadGame = false;
            Seat(1, [Alpha]);
            Mission.SquadGame = true;
            SetSquad(Alpha);
            Assert.Equal(Alpha.Id, Side.SquadId);
            Sent.Clear();
        }

        public void SetSquad(SquadClaim squad) => Send(1, new SetTeamInfo
        { SideId = 0, SquadId = WireSquadId(squad.Id), SideName = squad.Name });

        public void Dispose() { _ingress?.Dispose(); Server.Dispose(); }
    }

    // Same stable FNV-1a mapping as the existing P853 wire fixture.
    private static int WireSquadId(string id)
    {
        uint hash = 2166136261;
        foreach (char c in id) { hash ^= c; hash *= 16777619; }
        return unchecked((int)hash);
    }

    [SkippableTheory]
    [InlineData((short)0)]
    [InlineData((short)-1)]
    public void Initial_leader_establishes_preferred_squad_and_receives_team_info(short requestedSide)
    {
        using var w = new World(_webSocket);
        // First claim is membership only; prefer the squad the pilot can lead.
        var leader = w.Seat(1, [Beta with { CanLead = false }, Alpha], requestedSide);
        Assert.Equal(leader.Id, w.Side.LeaderShipId);
        Assert.Equal(Alpha.Id, w.Side.SquadId);
        Assert.Equal(Alpha.Name, w.Side.SquadName);
        Assert.Contains(w.Messages(1, SetTeamInfo.Fmid).Select(m => SetTeamInfo.Decode(m)),
            m => m.SideId == 0 && m.SquadId == WireSquadId(Alpha.Id) && m.SideName == Alpha.Name);
    }

    [SkippableTheory]
    [InlineData(false, "rival")]
    [InlineData(true, "rival")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "anonymous")]
    [InlineData(true, "anonymous")]
    public void Initial_nonmember_is_refused_on_an_occupied_squad_side(bool autoPick, string kind)
    {
        using var w = new World(_webSocket);
        w.RestrictOccupiedSide();
        // Rival membership alone cannot lead the other, empty side on auto-pick.
        SquadClaim[]? squads = kind == "rival" ? [Beta with { CanLead = false }]
            : kind == "empty" ? [] : null;
        short id = w.Logon(2, squads, verified: kind != "anonymous");
        w.Send(2, new PositionReq { SideId = autoPick ? (short)-1 : (short)0 });
        Assert.Contains(w.Messages(2, DelPositionReq.Fmid).Select(m => DelPositionReq.Decode(m)),
            m => m.ShipId == id && m.Reason == DelPositionReqReason.WrongSquad);
        Assert.DoesNotContain(w.Server.Ships, s => s.Ship.Id == id);
        Assert.Empty(w.Messages(2, SetCluster.Fmid));
        Assert.Equal(Alpha.Id, w.Side.SquadId);

        // Refusal leaves the peer pending; a legitimate membership can retry.
        w.Server.TestSetIdentity(new ConnectionId(2), new JoinIdentity(
            "https://lobby.test", "game", "account2", "Pilot2", 0, long.MaxValue, "retry", [Alpha with { CanLead = false }]));
        w.Send(2, new PositionReq { SideId = 0 });
        Assert.Equal((short)0, Assert.Single(w.Server.Ships, s => s.Ship.Id == id).Ship.SideId);
        Assert.NotEmpty(w.Messages(2, SetCluster.Fmid));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Members_can_join_and_non_squad_games_still_accept_squadless_peers(bool squadGame, bool autoPick)
    {
        using var w = new World(_webSocket, squadGame: false);
        w.Seat(1, [Alpha]);
        // Occupy both sides so auto-pick selects side 0 by equal population.
        w.Seat(3, [Beta], side: 1);
        w.Mission.SquadGame = squadGame;
        if (squadGame) w.SetSquad(Alpha);
        w.Sent.Clear();
        var joiner = w.Seat(2, squadGame ? [Alpha with { CanLead = false }] : [], autoPick ? (short)-1 : (short)0);
        Assert.Equal((short)0, joiner.SideId);
        Assert.Empty(w.Messages(2, DelPositionReq.Fmid));
        Assert.NotEmpty(w.Messages(2, SetCluster.Fmid));
    }

    [SkippableFact]
    public void Auto_pick_skips_a_rival_squad_side_and_establishes_its_own_empty_side()
    {
        using var w = new World(_webSocket);
        w.RestrictOccupiedSide();
        var rival = w.Seat(2, [Beta], side: -1);
        Assert.Equal((short)1, rival.SideId);
        Assert.Equal(Beta.Id, w.Mission.Sides[1].SquadId);
        Assert.Equal(Alpha.Id, w.Side.SquadId);
        Assert.Empty(w.Messages(2, DelPositionReq.Fmid));
    }

    [SkippableTheory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("rival")]
    public void Squad_change_boots_every_verified_nonmember_but_keeps_members_and_unverified_peers(string kind)
    {
        using var w = new World(_webSocket, squadGame: false);
        var leader = w.Seat(1, [Alpha, Beta]);
        SquadClaim[]? squads = kind == "rival" ? [Alpha] : kind == "empty" ? [] : null;
        var nonmember = w.Seat(2, squads);
        var member = w.Seat(3, [Beta with { CanLead = false }]);
        var anonymous = w.Seat(4, null, verified: false);
        var otherSide = w.Seat(5, [], side: 1);
        w.Mission.SquadGame = true;
        w.Sent.Clear();
        w.SetSquad(Beta);

        Assert.Equal((short)-2, nonmember.SideId);
        Assert.Equal((short)0, leader.SideId);
        Assert.Equal((short)0, member.SideId);
        Assert.Equal((short)0, anonymous.SideId);
        Assert.Equal((short)1, otherSide.SideId);
        Assert.Equal(leader.Id, w.Side.LeaderShipId);
        Assert.Equal(Beta.Id, w.Side.SquadId);
        foreach (int peer in Enumerable.Range(1, 5))
        {
            var quit = Assert.Single(w.Messages(peer, QuitSide.Fmid).Select(m => QuitSide.Decode(m)));
            Assert.Equal(nonmember.Id, quit.ShipId);
            Assert.Equal(QuitSideReason.SquadChange, quit.Reason);
        }
        w.Sent.Clear();
        w.SetSquad(Beta); // unchanged squad never boots again (native :1617-1619).
        Assert.DoesNotContain(w.Sent, m => FedMessageHeader.Read(m.Message).Fmid == QuitSide.Fmid);
    }

    [SkippableTheory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("member")]
    [InlineData("anonymous")]
    public void Empty_squad_side_still_requires_a_squad_leader(string kind)
    {
        using var w = new World(_webSocket);
        SquadClaim[]? squads = kind == "member" ? [Alpha with { CanLead = false }]
            : kind == "empty" ? [] : null;
        short id = w.Logon(1, squads, verified: kind != "anonymous");
        w.Send(1, new PositionReq { SideId = 0 });
        Assert.Equal(DelPositionReqReason.CantLeadSquad,
            DelPositionReq.Decode(Assert.Single(w.Messages(1, DelPositionReq.Fmid))).Reason);
        Assert.DoesNotContain(w.Server.Ships, s => s.Ship.Id == id);
        Assert.Null(w.Side.SquadId);
    }
}

/// <summary>The same R1b assertions through real WebSocket input.</summary>
[Collection("LiveNetwork")]
public sealed class P1048SquadSideTests : P1033SquadSideTests
{
    public P1048SquadSideTests() : base(true) { }
}
