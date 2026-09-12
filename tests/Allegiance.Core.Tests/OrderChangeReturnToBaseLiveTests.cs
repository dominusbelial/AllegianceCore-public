using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P150 (Stage 4) — live loopback proof of the ORDER_CHANGE(93) "return to
/// base" branch: a real connected player issues a real ORDER_CHANGE to a real
/// FLYING (undocked) NPC of their side, and the server picks the NPC's nearest
/// friendly repair-capable station and stores it in the command slot named by
/// the decoded <c>command</c> field (native FedSrv.CPP:7304-7332). This is the
/// second of the three docked-vs-flying sub-branches; the only remaining piece
/// beyond the carrier-specific search (deferred) and launch/PickDefaultOrder
/// (deferred) is the generic non-NPC fallback (out of scope).
///
/// Builds on P147/P148's harness (booted bot peer, no Windows VM). The seated
/// peer occupies <c>Sectors[0]</c>; the NPC is spawned into the P111-documented
/// no-station fly sector 1052 so the ONLY qualifying station in play is the one
/// each test injects via the <see cref="Mission.PlaceStation"/> seam. Because
/// <see cref="Ship.FindNearestFriendlyStation"/> is own-sector-scoped (P149),
/// station and NPC live in the same sector; the peer issues the command
/// remotely (FindSameSideShip is mission-wide, P148).
///
/// Three tests:
///   1. station found → <c>StayDocked=true</c>, Queued cleared,
///      <c>AcceptedTarget</c> resolves to the placed station with the decoded
///      commandID stored (primary path — the real client sends
///      <c>c_cmdAccepted</c>, WinTrek.cpp:3778).
///   2. no qualifying station → the state still updates (<c>StayDocked</c>,
///      Queued cleared) but <c>Accepted</c> stays unset — native's
///      <c>if (pmodel)</c> gate around only the slot-store half (FedSrv.CPP:7323).
///   3. the <c>c_cmdCurrent</c> edge → the port does NOT misroute the station
///      into the weapon-aim <see cref="Ship.CommandTarget"/> (which is
///      Model?-typed and cannot hold a station); it logs and leaves both slots
///      unset. Proves the decoded-<c>command</c> branch (FedSrv.CPP:7330) is real
///      and not a hardcoded Accept.
/// </summary>
[Collection("LiveNetwork")]
public sealed class OrderChangeReturnToBaseLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    /// <summary><c>XXV_simple.igc</c>: the no-station fly sector (P111 report).</summary>
    private const short FlySector = 1052;

    /// <summary><c>c_sabmRepair = 0x40</c> (Wopr/Wopr/Constants/StationAbilityBitMask.cs:16).</summary>
    private const short RepairBit = 0x40;

    /// <summary><c>c_cidGoto = 5</c> (igc.h:903) — the commandID the real
    /// client sends for the "return to base" drone action (WinTrek.cpp:3782).
    /// The flying branch does not gate on commandID (FedSrv.CPP:7304-7332 never
    /// reads pfmOC-&gt;commandID), so any value works; this one is representative.</summary>
    private const short CidGoto = 5;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Order Change RTB Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 995u, DwCookie = 0x995u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99100000000000004ul, DrmHash = "orderchange150",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector PeerSector = null!;
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
            seat.PeerSector = sector;
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
            bot.Connect(port, "p150_order");
            bot.SendLogonRequest(Request("p150_order"));
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

    /// <summary>The fly sector of the seated mission (guard the map assumption).</summary>
    private static Sector FlySectorOf(Mission m) =>
        m.Sectors.FirstOrDefault(s => s.Id == FlySector)
        ?? throw new InvalidOperationException($"XXV_simple has no sector {FlySector}");

    /// <summary>Place a real-type, friendly (<paramref name="seat"/>.PeerSide),
    /// repair-capable station into the fly sector via the public
    /// <see cref="Mission.PlaceStation"/> seam. The placed <see cref="Station"/>'s
    /// DataStationIGC becomes visible to <see cref="Ship.FindNearestFriendlyStation"/>
    /// through <c>Sector.Stations</c> (Sector.AttachStation → _stations, Sector.cs:181-183).</summary>
    private static void PlaceRepairStation(Seated seat, short stationId, float x, float y, float z)
    {
        var sector = FlySectorOf(seat.Mission);
        var repair = seat.Mission.Core.StationTypes.Values.FirstOrDefault(t => (t.SabmCapabilities & RepairBit) != 0);
        Assert.False(EqualityComparer<DataStationTypeIGC>.Default.Equals(repair, default),
            "PCore001 must contain a repair-capable station type (c_sabmRepair data-driven)");
        var spot = new Vector3(x, y, z);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: seat.PeerSide, StationId: stationId,
            StationTypeId: (short)repair.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{stationId}");
        seat.Mission.PlaceStation(new Station(data, repair), sector);
    }

    /// <summary>⭐ THE P150 proof. A real connected peer commands a FLYING
    /// (undocked) NPC of its side to return to base; the server finds the NPC's
    /// nearest friendly repair-capable station (same sector) and stores it in
    /// the <c>c_cmdAccepted</c> slot with the decoded commandID — while setting
    /// <c>StayDocked=true</c> and clearing the Queued slot
    /// (native FedSrv.CPP:7304-7332).</summary>
    [Fact]
    public void Flying_npc_return_to_base_sets_accepted_target_to_nearest_repair_station()
    {
        using var seat = SeatOne();
        var sector = FlySectorOf(seat.Mission);
        PlaceRepairStation(seat, stationId: 5101, x: 100f, y: 0f, z: 0f);

        var npcPos = new Vector3(700f, 720f, 680f);
        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, seat.PeerSide, npcPos);
        Assert.True(npc.IsNpc, "spawned NPC must carry the IsNpc marker");
        Assert.Null(npc.StationId);   // flying/undocked — the branch selector
        Assert.False(npc.StayDocked, "NPC begins with StayDocked=false");
        Assert.Null(npc.AcceptedTarget);
        Assert.Equal(OrderChange.CidNone, npc.AcceptedCommandId);

        // The seated peer issues ORDER_CHANGE targeting the NPC with the real
        // client's slot selector for this action (c_cmdAccepted, WinTrek.cpp:3778)
        // and commandID c_cidGoto (the undock branch's value, WinTrek.cpp:3782).
        seat.Bot.SendOrderChange(npc.Id, CidGoto, OrderChange.CmdAccepted);

        var pumped = PumpUntil(seat.Server, () => npc.AcceptedTarget is not null, 8000);
        Assert.True(npc.AcceptedTarget is not null,
            $"flying NPC commanded to return to base must have AcceptedTarget set to the nearest repair station server-side (pumped {pumped} ticks; FedSrv.CPP:7323-7331)");

        Assert.True(npc.StayDocked, "return-to-base must set StayDocked=true (FedSrv.CPP:7321)");
        Assert.Null(npc.QueuedTarget);   // SetCommand(c_cmdQueued,NULL,c_cidNone), FedSrv.CPP:7322
        Assert.Equal(OrderChange.CidNone, npc.QueuedCommandId);
        Assert.Null(npc.StationId);   // return-to-base does NOT dock the ship

        Assert.Equal(5101, npc.AcceptedTarget!.Value.StationId);
        Assert.Equal(CidGoto, npc.AcceptedCommandId);   // decoded commandID stored (FedSrv.CPP:7330)

        Console.WriteLine($"P150-RTB: peer {seat.Peer.Id}(side {seat.PeerSide}) ORDER_CHANGE(93) -> flying NPC {npc.Id} " +
                          $"(sector {sector.Id}) -> StayDocked=true, Queued cleared, AcceptedTarget=station 5101 cmdID={npc.AcceptedCommandId} (FedSrv.CPP:7304-7332)");
    }

    /// <summary>⭐ The no-qualifying-station case. The state still updates
    /// (<c>StayDocked</c>, Queued cleared) but the Accepted slot stays unset —
    /// native's <c>if (pmodel)</c> gate wraps ONLY the slot-store half
    /// (FedSrv.CPP:7323-7331).</summary>
    [Fact]
    public void Flying_npc_return_to_base_with_no_station_leaves_accepted_unset()
    {
        using var seat = SeatOne();
        var sector = FlySectorOf(seat.Mission);
        // No station placed in 1052 — nothing qualifies (own-sector scoped, P149).

        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, seat.PeerSide, new Vector3(310f, 290f, 255f));
        Assert.Null(npc.StationId);
        Assert.False(npc.StayDocked);

        seat.Bot.SendOrderChange(npc.Id, CidGoto, OrderChange.CmdAccepted);

        var pumped = PumpUntil(seat.Server, () => npc.StayDocked, 8000);
        Assert.True(npc.StayDocked,
            $"flying NPC with no qualifying station must still set StayDocked=true (unconditional in branch; pumped {pumped} ticks; FedSrv.CPP:7321)");
        Assert.Null(npc.QueuedTarget);
        Assert.Equal(OrderChange.CidNone, npc.QueuedCommandId);
        Assert.Null(npc.AcceptedTarget);   // no station -> Accepted unset (native if(pmodel) gate, FedSrv.CPP:7323)
        Assert.Equal(OrderChange.CidNone, npc.AcceptedCommandId);

        Console.WriteLine($"P150-RTB-NOSTATION: peer {seat.Peer.Id} -> flying NPC {npc.Id} in empty sector {sector.Id}: StayDocked=true, Queued cleared, Accepted unset (FedSrv.CPP:7323 gate)");
    }

    /// <summary>The <c>c_cmdCurrent</c> edge. The decoded-command branch is real:
    /// a command slot of 2 (<c>c_cmdCurrent</c>) must NOT be routed into the
    /// weapon-aim <see cref="Ship.CommandTarget"/> (Model?-typed; cannot hold a
    /// station) nor into <see cref="Ship.AcceptedTarget"/> — the port logs and
    /// stores nothing, matching the documented, reachability-safe treatment.</summary>
    [Fact]
    public void Flying_npc_return_to_base_current_slot_is_logged_not_misrouted()
    {
        using var seat = SeatOne();
        var sector = FlySectorOf(seat.Mission);
        PlaceRepairStation(seat, stationId: 5199, x: 260f, y: 40f, z: 0f);

        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, seat.PeerSide, new Vector3(500f, 460f, 430f));
        Assert.Null(npc.StationId);

        seat.Bot.SendOrderChange(npc.Id, CidGoto, OrderChange.CmdCurrent);

        var pumped = PumpUntil(seat.Server, () => npc.StayDocked, 8000);
        Assert.True(npc.StayDocked, $"StayDocked must still be set (pumped {pumped} ticks; unconditional, FedSrv.CPP:7321)");
        Assert.Null(npc.QueuedTarget);
        Assert.Null(npc.AcceptedTarget);   // c_cmdCurrent must not be misrouted into Accepted
        Assert.Equal(OrderChange.CidNone, npc.AcceptedCommandId);
        Assert.Null(npc.CommandTarget);    // the Model?-typed weapon-aim slot must stay untouched

        Console.WriteLine($"P150-RTB-CURRENT: peer {seat.Peer.Id} -> flying NPC {npc.Id} with command slot c_cmdCurrent: StayDocked=true, neither Accepted nor CommandTarget set (logged, documented)");
    }
}
