using Allegiance.Core.Tests.Transport;
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

/// <summary>
/// P1022 (R1 finding F1) — the relays R1_REPORT.md lists as uncharged are now
/// charged to the P654 per-peer message budget. The two proof tests in
/// <see cref="R1ReviewTests"/> cover POSITIONREQ and SET_WINGID at packet size;
/// these cover the cases the report identified by code reading only
/// (same-side POSITIONREQ, no-op SWAP_PART, self-donation, zero bucket
/// donation, the leader's <c>#autoaccept</c> MISSIONDEF and the owner's
/// SIDE_INACTIVE) plus the legitimate-client side of the limit.
/// <para><b>Native:</b> native relays every one of these (cites in each test) —
/// it charges only chat and REQUEST_MONEY (<c>FedSrv/FedSrv.CPP:3372</c>,
/// <c>:5370</c>) — so charging them is the port's own P654 hardening, never a
/// parity claim. Nothing is dropped for a legitimate client: one request still
/// relays exactly once.</para>
/// </summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public class P1022RelayBudgetTests
{
    private readonly bool _webSocket;
    public P1022RelayBudgetTests() { }
    protected P1022RelayBudgetTests(bool webSocket) => _webSocket = webSocket;

    /// <summary>The largest number of relays a packet may produce: the P654
    /// dedicated profile allows ~50 paid sends from a full budget (1000 / 20);
    /// 64 is the loose bound the R1 proof tests use, so a fix that merely halves
    /// the amplification cannot pass.</summary>
    private const int FairShare = 64;

    /// <summary>The P867 dispatch-certification world (two sides, seeded peers
    /// with real ships, every egress frame captured).</summary>
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
        public readonly StaticCore Core;
        public readonly Ship Owner, Mate, Enemy, Lobby, Outsider;
        public readonly List<(ConnectionId Peer, byte[] Msg)> Sent = [];
        public readonly ConnectionId A = new(1), B = new(2), C = new(3), L = new(4), X = new(5);
        private readonly WebSocketRegressionIngress? _ingress;
        public World(bool webSocket = false)
        {
            Require.Artwork();
            Core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
            Mission = Mission.Assemble(Core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Other = Mission.Assemble(Core, map, [new SideSpec(0, "Other", 1), new SideSpec(1, "Team", 3)], Host);
            Mission.Stage = Other.Stage = MissionStage.NotStarted;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Host.Transport, true);
            Set("_host", Host); Set("_config", Config); Set("_missions", new List<Mission> { Mission, Other });
            var hull = Core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
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
            if (webSocket) _ingress = new WebSocketRegressionIngress(Server, Host, 5);
        }
        private void Dispatch(ConnectionId from, byte[] packet)
        { if (_ingress is null) Server.TestProcessDatagram(from, packet); else _ingress.Send(from, packet); }

        public void Set(string field, object value) => typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public T Get<T>(string field) => (T)typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public Srv.LiveShip Live(ConnectionId peer) => Get<Dictionary<ConnectionId, Srv.LiveShip>>("_peerShips")[peer];
        public void Send(ConnectionId from, IFedMessage message) => Dispatch(from, FedPacket.Pack([message.Encode()]));
        /// <summary>One wire packet carrying <paramref name="count"/> copies of a message.</summary>
        public void SendBurst(ConnectionId from, IFedMessage message, int count) =>
            Dispatch(from, FedPacket.Pack(Enumerable.Repeat(message.Encode(), count)));
        public int Count(ushort fmid) => Sent.Count(s => FedMessageHeader.Read(s.Msg).Fmid == fmid);
        public string Tally() => string.Join(", ", Sent.GroupBy(s => FedMessageHeader.Read(s.Msg).Fmid).Select(g => $"fmid {g.Key} x{g.Count()}"));
        public void Dispose() { _ingress?.Dispose(); Host.Transport!.Shutdown(); Server.Dispose(); }
    }

    /// <summary>One whole-mission relay is what a legitimate client pays for, and
    /// it still happens: a single POSITIONREQ for an auto-accept side seats the
    /// player and announces the join once to the whole mission.</summary>
    [SkippableFact]
    public void A_legitimate_side_request_still_relays_once()
    {
        using var w = new World(_webSocket);
        Assert.True(w.Mission.Sides[0].AutoAccept, "fixture: the sides auto-accept (Side.AutoAccept default)");
        w.Send(w.L, new PositionReq { SideId = 0 });
        Assert.True(4 == w.Count(JoinSide.Fmid), $"JOIN_SIDE frames: {w.Tally()}");   // Owner, Mate, Enemy, Lobby
    }

    /// <summary>The leader-approval path: a led side that does NOT auto-accept
    /// makes native broadcast POSITIONREQ(40) to the whole mission
    /// (<c>Fsmission.cpp:4765-4769</c>); a legitimate request still relays once.</summary>
    [SkippableFact]
    public void A_pending_side_request_still_relays_once()
    {
        using var w = new World(_webSocket);
        w.Mission.Sides[0].AutoAccept = false;
        w.Send(w.L, new PositionReq { SideId = 0 });
        Assert.True(4 == w.Count(PositionReqBroadcast.Fmid), $"POSITIONREQ(40) frames: {w.Tally()}");
    }

    /// <summary>The side the ship is ALREADY seated on: native removes the
    /// player from that side and re-requests it (<c>Fsmission.cpp:4749-4769</c>,
    /// RemovePlayerFromSide then the whole-mission POSITIONREQ relay), so the
    /// relay is charged, never dropped — 256 copies in one packet stay inside
    /// the budget's 50-charge fair share.</summary>
    [SkippableFact]
    public void A_side_request_for_the_ships_own_side_is_charged()
    {
        using var w = new World(_webSocket);
        w.SendBurst(w.B, new PositionReq { SideId = 0 }, 256);
        int relays = w.Count(PositionReqBroadcast.Fmid) + w.Count(JoinSide.Fmid);
        Assert.True(relays >= 4, $"{relays} relays — a legitimate relay must still happen");
        Assert.True(relays <= FairShare * 4, $"256 same-side POSITIONREQs produced {relays} whole-mission frames");
    }

    /// <summary>A SWAP_PART naming the mount the part is already on. Native
    /// relays it (<c>FedSrv.CPP:6480-6498</c>: ppart and ppartNew are the same
    /// object, its SetMountID ends where it started, ForwardMessage still
    /// fires), so the port keeps the relay and charges it.</summary>
    [SkippableFact]
    public void A_swap_part_at_the_same_mount_is_charged()
    {
        using var w = new World(_webSocket);
        w.Mission.Stage = MissionStage.Started;
        var weapon = w.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon && w.Mate.HullType.CanMountPart(p, 0));
        w.Mate.MountPart(weapon, 1, 0, 1);
        w.SendBurst(w.B, new SwapPart { EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = 0 }, 256);
        int relays = w.Count(SwapPart.Fmid);
        Assert.True(relays >= 4, $"{relays} SWAP_PART frames — the legitimate no-op relay must still happen");
        Assert.True(relays <= FairShare * 4, $"256 same-mount SWAP_PARTs produced {relays} sector-wide frames");
    }

    /// <summary>A donation to oneself. Native's same-side test passes, the wallet
    /// is debited and credited back (net zero) and the MONEY_CHANGE still goes to
    /// the sender's side group (<c>FedSrv.CPP:7226-7245</c>), so it is charged,
    /// never dropped.</summary>
    [SkippableFact]
    public void A_self_donation_is_charged()
    {
        using var w = new World(_webSocket);
        w.Mission.Stage = MissionStage.Started;
        w.Live(w.B).Wallet.Money = 5000;
        w.SendBurst(w.B, new PlayerDonate { ShipID = w.Mate.Id, MoneyGiven = 1 }, 256);
        int relays = w.Count(MoneyChange.Fmid);
        Assert.True(relays >= 2, $"{relays} MONEY_CHANGE frames — the legitimate relay must still happen");
        Assert.True(relays <= FairShare * 2, $"256 self-donations produced {relays} side-wide frames");
    }

    /// <summary>A zero bucket donation. Native's <c>moneyGiven &gt;= 0</c> trap
    /// passes, so it debits nothing and still relays the side-wide
    /// MONEY_CHANGE(0) and BUCKET_STATUS (<c>FedSrv.CPP:7146-7200</c>): charged,
    /// never dropped.</summary>
    [SkippableFact]
    public void A_zero_bucket_donation_is_charged()
    {
        using var w = new World(_webSocket);
        w.Mission.Stage = MissionStage.Started;
        var bucket = w.Mission.Sides[0].Buckets.FirstOrDefault(b => w.Mission.Sides[0].CanBuy(b));
        Assert.True(bucket is not null, "the assembled core has no buyable bucket for side 0");
        w.SendBurst(w.B, new BucketDonate { IBucket = bucket!.Id, MoneyGiven = 0 }, 256);
        int relays = w.Count(MoneyChange.Fmid) + w.Count(BucketStatus.Fmid);
        Assert.True(relays >= 2, $"{relays} bucket frames — the legitimate relay must still happen");
        Assert.True(relays <= FairShare * 4 * 2, $"256 zero bucket donations produced {relays} side-wide frames");
    }

    /// <summary>The leader's <c>#autoaccept</c> chat line: native toggles the side
    /// and forwards the whole-mission MISSIONDEF (<c>FedSrv.CPP:5198-5204</c>),
    /// before the chat budget is consulted in the port. Now charged.</summary>
    [SkippableFact]
    public void The_leaders_autoaccept_missiondef_is_charged()
    {
        using var w = new World(_webSocket);
        w.Mission.Stage = MissionStage.Started;
        w.SendBurst(w.A, new ChatMessage { Message = "#autoaccept", ChatTarget = (int)ChatTarget.Everyone }, 256);
        int relays = w.Count(MissionDef.Fmid);
        Assert.True(relays >= 4, $"{relays} MISSIONDEF frames — the legitimate relay must still happen");
        Assert.True(relays <= FairShare * 4, $"256 #autoaccept lines produced {relays} whole-mission MISSIONDEFs");
    }

    /// <summary>The owner's SIDE_INACTIVE relay (<c>FedSrv.CPP:7416</c>
    /// ForwardMessage whole mission) is charged before any side state changes.</summary>
    [SkippableFact]
    public void The_owners_side_inactive_relay_is_charged()
    {
        using var w = new World(_webSocket);
        w.SendBurst(w.A, new SideInactive { SideId = 1, BActive = false }, 256);
        int relays = w.Count(SideInactive.Fmid);
        Assert.True(relays >= 4, $"{relays} SIDE_INACTIVE frames — the legitimate relay must still happen");
        Assert.True(relays <= FairShare * 4, $"256 SIDE_INACTIVEs produced {relays} whole-mission frames");
    }
}

/// <summary>The same R1/R1b assertions through real WebSocket input.</summary>
[Collection("LiveNetwork")]
public sealed class P1048RelayBudgetTests : P1022RelayBudgetTests
{
    public P1048RelayBudgetTests() : base(true) { }
}
