using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P148 Stage 2 — live loopback proof that a real connected player issuing a
/// real <c>ORDER_CHANGE(93)</c> command to a real (docked) NPC ship produces a
/// real server-side state change. Builds on P147's harness (booted bot peer, no
/// Windows VM): one connected bot is seated in <c>Sectors[0]</c> on a side,
/// then an NPC ship is spawned into the SAME sector via
/// <see cref="Srv.SpawnNpcShip"/> and docked at a real station
/// (<see cref="Ship.StationId"/> set directly, the same settable property the
/// dock seam uses). The seated bot issues <c>ORDER_CHANGE</c> targeting the
/// NPC's <c>shipID</c> with <c>commandID = c_cidDoNothing</c> (-3); the claim
/// under test is that the server's new stay-docked slice (FedSrv.CPP:7285-7294)
/// sets <see cref="Ship.StayDocked"/> = true on the NPC server-side — the first
/// real player→NPC command resulting in real state.
///
/// A second test proves the <see cref="Ship.IsNpc"/> gate: issuing the same
/// <c>ORDER_CHANGE</c> against the peer's OWN (non-NPC) ship is a documented
/// no-op — <c>StayDocked</c> is never touched (it is meaningless on a real
/// player ship), with no crash.
/// </summary>
[Collection("LiveNetwork")]
public sealed class OrderChangeStayDockedLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Order Change Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 994u, DwCookie = 0x994u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99100000000000003ul, DrmHash = "orderchange148",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted single-bot live server in <c>Sectors[0]</c>, with the
    /// mission/sector captured so the test can spawn an NPC alongside.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public Ship Peer = null!;
        public FltBot Bot = null!;
        public short PeerSide;

        public void Dispose()
        {
            Bot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatOne()
    {
        var seat = new Seated();
        seat.Server = new Srv();
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            seat.Mission = mission;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);  // peer at origin
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        var bot = new FltBot();
        try
        {
            bot.Connect(port, "p148_order");
            bot.SendLogonRequest(Request("p148_order"));
            PumpUntil(seat.Server, () => bot.ShipAssignment is not null, 4000);
            Assert.True(bot.ShipAssignment is not null, "bot must be assigned a ship");
            var peer = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
            peer.Velocity = Vector3.Zero;
            seat.Peer = peer;
            seat.PeerSide = peer.SideId ?? throw new InvalidOperationException("peer must have a side");
            seat.Bot = bot;
            return seat;
        }
        catch
        {
            bot.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>⭐ THE P148 proof. A real connected peer commands a docked NPC of
    /// its own side to <c>c_cidDoNothing</c>; the server sets the NPC's
    /// <see cref="Ship.StayDocked"/> = true server-side — the first real
    /// player→NPC ORDER_CHANGE producing real state.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_npc_command_do_nothing_sets_stay_docked_true()
    {
        Require.Artwork();
        using var seat = SeatOne();

        // Spawn an NPC on the peer's side and dock it at a real station
        // (StationId set directly — the same settable property the dock seam
        // uses, per Server.cs:3529/4134-4135).
        var npcPos = new Vector3(700f, 720f, 680f);
        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.PeerSide, npcPos);
        Assert.True(npc.IsNpc, "spawned NPC must carry the IsNpc marker (FedSrv pilotType < c_ptPlayer)");
        Assert.False(seat.Server.Ships.Any(s => s.Ship.Id == npc.Id),
            $"NPC ship {npc.Id} must have no peer-connection entry (ownerless)");

        var allStations = seat.Mission.Sectors.SelectMany(s => s.Stations).ToList();
        Assert.True(allStations.Count > 0, "mission must expose at least one real station to dock at");
        npc.StationId = allStations[0].StationId;   // dock the NPC at a real station (FedSrv GetStation() != NULL)
        npc.LastDockTime = seat.Server.SimNow;      // P381: mirror the real dock seam, which stamps the dock clock — otherwise the auto-launch pass reads "docked forever ago" and relaunches before the ORDER_CHANGE lands
        Assert.NotNull(npc.StationId);
        Assert.False(npc.StayDocked, "NPC begins with StayDocked=false");

        // The seated peer issues ORDER_CHANGE targeting the NPC with
        // commandID = c_cidDoNothing (-3).
        seat.Bot.SendOrderChange(npc.Id, OrderChange.CidDoNothing);

        // Pump until the server processed the message and set the latch.
        var pumped = PumpUntil(seat.Server, () => npc.StayDocked, 8000);
        Assert.True(npc.StayDocked,
            $"docked NPC commanded c_cidDoNothing must have StayDocked=true server-side (pumped {pumped} ticks; OrderChange pipeline + FedSrv.CPP:7290-7294)");

        // The command went to a genuinely connected peer and mutated real server state.
        Assert.Same(npc.Sector, seat.Sector);
        Console.WriteLine($"P148-OC: peer {seat.Peer.Id}(side {seat.PeerSide}) ORDER_CHANGE(93) -> NPC {npc.Id} " +
                          $"(docked@{npc.StationId}) commandID=c_cidDoNothing -> StayDocked=true (FedSrv.CPP:7290-7294)");
    }

    /// <summary>The <see cref="Ship.IsNpc"/> gate. The same ORDER_CHANGE issued
    /// against the peer's OWN (non-NPC) ship must be a documented no-op: the
    /// native generic SetCommand fallback is out of scope (FedSrv.CPP:7335-7339),
    /// so the port logs and no-ops. Delivery is proven by then issuing a valid
    /// NPC command in the same test — FIFO over the connection guarantees the
    /// earlier non-NPC message was processed before the NPC one, so observing
    /// the NPC latch set proves the peer's own StayDocked was never touched.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Order_change_on_non_npc_own_ship_is_documented_no_op()
    {
        Require.Artwork();
        using var seat = SeatOne();

        Assert.False(seat.Peer.IsNpc, "the seated peer is a real player, not an NPC");
        Assert.False(seat.Peer.StayDocked, "a real player ship starts with StayDocked=false (flag meaningless for non-NPC)");

        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.PeerSide, new Vector3(820f, 810f, 798f));
        var allStations = seat.Mission.Sectors.SelectMany(s => s.Stations).ToList();
        Assert.True(allStations.Count > 0, "mission must expose a real station to dock at");
        npc.StationId = allStations[0].StationId;
        npc.LastDockTime = seat.Server.SimNow;   // P381: mirror the real dock seam's clock stamp (else auto-launch undocks before the sentinel command)

        // 1) Command the peer's OWN (non-NPC) ship to c_cidDoNothing — expect no-op.
        seat.Bot.SendOrderChange(seat.Peer.Id, OrderChange.CidDoNothing);
        // 2) Delivery sentinel: command the (valid) NPC — this succeeds only if
        //    messages are reaching the handler, and FIFO guarantees #1 preceded it.
        seat.Bot.SendOrderChange(npc.Id, OrderChange.CidDoNothing);
        var pumped = PumpUntil(seat.Server, () => npc.StayDocked, 8000);
        Assert.True(npc.StayDocked, $"sentinel NPC command must land (pumped {pumped} ticks) — proves the earlier peer command reached the handler too");

        // The peer's own ship must remain untouched: StayDocked is meaningless on
        // a non-NPC ship and the IsNpc gate keeps it false (FedSrv pilotType >= c_ptPlayer).
        Assert.False(seat.Peer.StayDocked,
            $"ORDER_CHANGE targeting the peer's own non-NPC ship must be a no-op (IsNpc gate, FedSrv.CPP:7285) — peer.StayDocked must stay false");

        Console.WriteLine($"P148-OC: peer {seat.Peer.Id} ORDER_CHANGE(93) targeting its OWN (non-NPC) ship -> no-op " +
                          $"(IsNpc gate, FedSrv.CPP:7285); NPC {npc.Id} still processed normally");
    }
}
