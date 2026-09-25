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
/// P152 ORDER_CHANGE Stage 6 — the second (launch) half of native
/// <c>HandleOrderChange</c>'s docked branch (<c>FedSrv.CPP:7296-7302</c>), which
/// P148 explicitly deferred. A real connected peer commands a docked NPC with a
/// non-<c>c_cidDoNothing</c> <c>commandID</c>; the server must <c>StayDocked =
/// false</c> AND actually leave the station — clear <see cref="Ship.StationId"/>,
/// push the ship clear of <see cref="Sector.DockRadius"/> via the shared
/// <see cref="Srv"/> undock core (the P152 <c>UndockShipState</c> extraction from
/// <c>PerformUndock</c>), and start the undock debounce.
///
/// No <c>PickDefaultOrder</c>-equivalent call is made — that call is a deliberate
/// no-op in this port's scope (the only ported sub-block, P151's take-cover,
/// gates on <c>!bDocked</c>, <c>shipIGC.h:2230</c>, which <c>bDocked=true</c>
/// makes false; the rest of the native switch is mining/ore/unload/join logic
/// this port doesn't build). See ORDER_CHANGE_P152.md.
///
/// The companion real-peer undock tests (<c>LaunchUndockTests</c>) guard the
/// extraction refactor's behavior-preservation: <c>PerformUndock</c> now calls
/// the same <c>UndockShipState</c> helper, and the full suite re-verifies that
/// path unmodified.
/// </summary>
[Collection("LiveNetwork")]
public sealed class OrderChangeLaunchLiveTests
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
            new Gm { Name = "Order Change Launch Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 992u, DwCookie = 0x992u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88100000000000006ul, DrmHash = "orderchangel152",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static float SqDist(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
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
            bot.Connect(port, "p152_launch");
            bot.SendLogonRequest(Request("p152_launch"));
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

    /// <summary>⭐ THE P152 proof. A real connected peer commands a docked NPC of
    /// its own side with a non-<c>c_cidDoNothing</c> <c>commandID</c>; the server
    /// must (a) <c>StayDocked=false</c>, (b) clear <c>StationId</c>, (c) actually
    /// reposition the ship clear of <c>DockRadius</c> (proving the shared
    /// undock core ran, not just the flags), and (d) start the undock debounce.
    /// The NPC is placed in the same sector as its docked station, starting well
    /// INSIDE <c>DockRadius</c>, so the post-launch distance moving outside it is
    /// a real observation of <c>RepositionForLaunch</c> — not a pre-existing
    /// far position.</summary>
    [Fact]
    public void Commanding_a_docked_npc_to_launch_undocks_and_repositions_it()
    {
        using var seat = SeatOne();

        // Choose a sector that has a real station; place the NPC there and dock
        // it at that station, so the undock core's station lookup (which is
        // scoped to the ship's own sector, matching PerformUndock's
        // live.Sector.Stations search) finds it and RepositionForLaunch fires.
        var dockSector = seat.Mission.Sectors.First(s => s.Stations.Count > 0);
        var station = dockSector.Stations[0];
        var stationPos = new Vector3(station.Position[0], station.Position[1], station.Position[2]);

        // Spawn the NPC well INSIDE DockRadius of the station (offset 50 < 200),
        // so the pre-launch distance is inside the dock sphere.
        var npc = seat.Server.SpawnNpcShip(seat.Mission, dockSector, seat.PeerSide,
            stationPos + new Vector3(50f, 0f, 0f));
        Assert.True(npc.IsNpc, "spawned NPC must carry the IsNpc marker (FedSrv pilotType < c_ptPlayer)");

        npc.StationId = station.StationId;   // dock the NPC (FedSrv GetStation() != NULL)
        npc.LastDockTime = seat.Server.SimNow;   // P381: mirror the real dock seam's clock stamp (else auto-launch undocks before the ORDER_CHANGE lands)
        Assert.NotNull(npc.StationId);
        Assert.False(npc.StayDocked, "NPC begins with StayDocked=false");

        float preDistSq = SqDist(npc.Position, stationPos);
        Assert.True(preDistSq <= Sector.DockRadius * Sector.DockRadius,
            $"pre-launch NPC must start inside the dock sphere (dist {Math.Sqrt(preDistSq):0.#} <= {Sector.DockRadius}) so the post-launch clearance is observable");
        Assert.Equal(double.NegativeInfinity, npc.LastLaunchTime);

        // The seated peer issues ORDER_CHANGE targeting the docked NPC with a
        // non-DoNothing commandID (c_cidGoto=5). The command slot is irrelevant
        // to the launch arm (native never reads it in this branch) — use the
        // default (c_cmdQueued) the 2-arg helper sends.
        seat.Bot.SendOrderChange(npc.Id, OrderChange.CidGoto);

        // Pump until the server processed the message and undocked the NPC.
        var pumped = PumpUntil(seat.Server, () => npc.StationId is null, 8000);
        Assert.True(npc.StationId is null,
            $"docked NPC commanded to launch must have StationId cleared server-side (pumped {pumped} ticks; FedSrv.CPP:7296-7302)");

        // (a) StayDocked cleared — the opposite of P148's stay-docked arm.
        Assert.False(npc.StayDocked,
            "commanded-to-launch NPC must have StayDocked=false (FedSrv.CPP:7298)");

        // (b) StationId cleared by the shared undock core.
        Assert.Null(npc.StationId);

        // (c) Position actually moved clear of DockRadius — proves
        // RepositionForLaunch ran (RepositionForLaunch pushes to
        // station + dir*(DockRadius + LaunchClearMargin) ≈ 250 > 200), not just
        // the two state flags.
        float postDistSq = SqDist(npc.Position, stationPos);
        Assert.True(postDistSq > Sector.DockRadius * Sector.DockRadius,
            $"launched NPC must be repositioned clear of DockRadius (post-launch dist {Math.Sqrt(postDistSq):0.#} > {Sector.DockRadius}; RepositionForLaunch ran)");

        // (d) The undock debounce started (P75) — Sector.ResolveDockCollisions
        // won't re-latch the ship for LaunchCooldownSeconds.
        Assert.True(npc.LastLaunchTime >= 0,
            "launched NPC must have the undock debounce (LastLaunchTime = sim-now) set");

        Console.WriteLine($"P152-OC: peer {seat.Peer.Id}(side {seat.PeerSide}) ORDER_CHANGE(93) -> docked NPC {npc.Id} " +
                          $"commandID=c_cidGoto -> StayDocked=false, StationId=null, repositioned to dist {Math.Sqrt(postDistSq):0.#} > DockRadius={Sector.DockRadius}, debounce set " +
                          $"(FedSrv.CPP:7296-7302; no PickDefaultOrder call)");
    }

    /// <summary>Regression guard on the restructured branch: a docked NPC
    /// commanded <c>c_cidDoNothing</c> must STILL stay docked (P148's arm), with
    /// <c>StationId</c> untouched and no reposition — proving the new launch
    /// <c>else</c> did not disturb the existing stay-docked half. Issued after a
    /// launch-order in a separate seat to confirm both arms coexist.</summary>
    [Fact]
    public void Docked_npc_command_do_nothing_still_stays_docked_after_launch_arm_added()
    {
        using var seat = SeatOne();

        var dockSector = seat.Mission.Sectors.First(s => s.Stations.Count > 0);
        var station = dockSector.Stations[0];
        var stationPos = new Vector3(station.Position[0], station.Position[1], station.Position[2]);

        var npc = seat.Server.SpawnNpcShip(seat.Mission, dockSector, seat.PeerSide,
            stationPos + new Vector3(50f, 0f, 0f));
        npc.StationId = station.StationId;
        npc.LastDockTime = seat.Server.SimNow;   // P381: mirror the real dock seam's clock stamp (else auto-launch undocks before the sentinel command)
        float preDistSq = SqDist(npc.Position, stationPos);
        Assert.True(preDistSq <= Sector.DockRadius * Sector.DockRadius);

        seat.Bot.SendOrderChange(npc.Id, OrderChange.CidDoNothing);
        var pumped = PumpUntil(seat.Server, () => npc.StayDocked, 8000);
        Assert.True(npc.StayDocked,
            $"docked NPC commanded c_cidDoNothing must still set StayDocked=true (pumped {pumped} ticks; P148 arm intact after P152 restructure)");

        // Stay-docked does NOT undock: StationId remains set and the ship is NOT
        // repositioned (the native stay-docked arm never clears the station).
        Assert.NotNull(npc.StationId);
        float postDistSq = SqDist(npc.Position, stationPos);
        Assert.True(Math.Abs(postDistSq - preDistSq) < 0.01f,
            "stay-docked NPC must not be repositioned (only the launch arm calls the undock core)");

        Console.WriteLine($"P152-OC: peer {seat.Peer.Id} ORDER_CHANGE -> docked NPC {npc.Id} c_cidDoNothing still stay-docks " +
                          $"(StationId={npc.StationId}, StayDocked={npc.StayDocked}, position unchanged) — P148 arm preserved");
    }
}
