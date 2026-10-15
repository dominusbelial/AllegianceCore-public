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
/// P153 (the generic non-NPC ORDER_CHANGE path) — live loopback proof that a
/// REAL player's own <c>c_cmdCurrent</c> ORDER_CHANGE (the client's
/// <c>SetTarget</c> "lock onto this target" action, <c>WinTrek.cpp:8820-8852</c>)
/// finally has a production write path for <see cref="Ship.CommandTarget"/>, via
/// the new cross-side resolver <c>Server.FindShipAnySide</c> and the extended
/// final <c>else</c> in <c>Server.HandleOrderChange</c> (native
/// <c>FedSrv.CPP:7335-7339</c>). Builds on P147/P148/P150's booted 2-bot
/// harness (no Windows VM): two REAL connected peers on DIFFERENT sides, the
/// common "lock an enemy ship" case.
///
/// Four tests:
///   1. ⭐ cross-side lock — A (side Alpha) commands its own ship to
///      c_cmdCurrent-target B (side Bravo); server sets <c>A.CommandTarget</c>
///      to B's real <c>Ship</c> reference (proves cross-side resolution — the
///      same-side-only <c>FindSameSideShip</c> would never find B).
///   2. clearing — after locking B, A sends objectID=NA (the SetTarget
///      pmodel==NULL sentinel, WinTrek.cpp:8834-8836) → <c>A.CommandTarget</c>
///      becomes null.
///   3. NPC target — A c_cmdCurrent-targets an ownerless NPC
///      (P147 <c>SpawnNpcShip</c>, which has NO <c>_peerShips</c> entry) →
///      resolved via the sector model-list scan and set on <c>A.CommandTarget</c>.
///   4. unknown station target — GetModel returns NULL and clears the slot.
/// </summary>
[Collection("LiveNetwork")]
public sealed class OrderChangeCurrentLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Order Change Current Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98900000000000001ul, DrmHash = "orderchange153",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 2-bot / 2-side live server. Slot 0 = <b>side A</b> at
    /// the origin; slot 1 = <b>side B</b> a little way off — both in
    /// <c>Sectors[0]</c>, both real connected peers on different sides (the
    /// P139/P153 cross-side harness).</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Sector Sector = null!;
        public Mission Mission = null!;

        public void Dispose()
        {
            ABot?.Dispose();
            BBot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatTwo()
    {
        var seat = new Seated();
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            seat.Mission = mission;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First() with { Signature = 100, ScannerRange = 20000 };
            var ship = placed switch
            {
                0 => new Ship(id, hull, new Vector3(5000, 5000, 5000), Vector3.Zero, Orientation.Identity),                     // side A at origin
                _ => new Ship(id, hull, new Vector3(5500f, 5000f, 5000f), Vector3.Zero, Orientation.Identity),        // side B, same cluster
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = { "p153_a", "p153_b" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            for (int i = 0; i < bots.Length; i++)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship;
                if (i == 0) { seat.A = ship; seat.ABot = bots[i]; }
                else { seat.B = ship; seat.BBot = bots[i]; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B);
            Assert.True(seat.A.SideId != seat.B.SideId, "A and B must be on different sides (cross-side resolution)");
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            seat.Sector.ResolveCrossSideVisibility();
            Assert.True(seat.B.SeenBySide(seat.A.SideId!.Value), $"target must be visible: A={seat.A.Position} B={seat.B.Position}, dock={seat.A.StationId}/{seat.B.StationId}, sig={seat.B.EffectiveSignature}, scan={seat.A.EffectiveScannerRange()}");
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>⭐ THE P153 proof. A real player (A) issues a real
    /// <c>c_cmdCurrent</c> ORDER_CHANGE naming the OTHER side's peer ship (B) —
    /// the everyday SetTarget "lock an enemy ship" action. The server resolves
    /// B cross-side (<c>FindShipAnySide</c> — same-side-only would fail) and
    /// sets <c>A.CommandTarget</c> to B's real <c>Ship</c> reference.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_player_locks_enemy_peer_across_sides_via_c_cmdCurrent()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        Assert.Null(seat.A.CommandTarget);

        // SetTarget semantics: ShipID = the COMMANDED ship = A's own ship;
        // objectType/objectID = the TARGET = B (the enemy, other side).
        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, IgcObjectType.Ship, seat.B.Id);

        var pumped = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.CommandTarget, seat.B), 8000);
        Assert.True(ReferenceEquals(seat.A.CommandTarget, seat.B),
            $"A commanding c_cmdCurrent toward B({seat.B.Id}, side {seat.B.SideId}) must set A.CommandTarget to B server-side (pumped {pumped} ticks; FedSrv.CPP:7335-7339)");

        // Sanity: A's own target was mutated, not B's (native SetCommand writes
        // the commanded ship pship = A's ship, FedSrv.CPP:7338).
        Assert.Null(seat.B.CommandTarget);

        Console.WriteLine($"P153-CROSS: A({seat.A.Id}, side {seat.A.SideId}) c_cmdCurrent -> enemy B({seat.B.Id}, side {seat.B.SideId}) : A.CommandTarget now points at B (reference equal)");
    }

    /// <summary>Clearing. After A locks B, A re-issues <c>c_cmdCurrent</c> with
    /// the <c>objectID=NA</c> sentinel (SetTarget's <c>pmodel==NULL</c> case,
    /// WinTrek.cpp:8834-8836) — <c>A.CommandTarget</c> reverts to null.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_player_clears_command_target_with_na_sentinel()
    {
        Require.Artwork();
        using var seat = SeatTwo();

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, IgcObjectType.Ship, seat.B.Id);
        var locked = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.CommandTarget, seat.B), 8000);
        Assert.True(locked > 0, $"precondition: A.CommandTarget must lock B first ({seat.B.Id}); FedSrv.CPP:7335-7339");

        // Clear: objectID = NA(-1), objectType = OT_invalid(-1) — the exact pair
        // the client sends when pmodel is null (WinTrek.cpp:8834-8836).
        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, (IgcObjectType)(-1), -1);

        var cleared = PumpUntil(seat.Server, () => seat.A.CommandTarget is null, 8000);
        Assert.True(cleared > 0,
            $"c_cmdCurrent with objectID=NA must clear A.CommandTarget to null (pumped {cleared} ticks; WinTrek.cpp:8834-8836)");
        Assert.Null(seat.A.CommandTarget);

        Console.WriteLine($"P153-CLEAR: A({seat.A.Id}) cleared CommandTarget via NA sentinel -> null");
    }

    /// <summary>An ownerless NPC target. A <c>c_cmdCurrent</c>-targets a P147
    /// <c>SpawnNpcShip</c> (which has NO <c>_peerShips</c> entry — the port's
    /// distinguishing gap the P148/P153 sector-scan closes). The NPC is on the
    /// other side, so this also exercises the cross-side half against an NPC.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_player_locks_ownerless_npc_ship_via_c_cmdCurrent()
    {
        Require.Artwork();
        using var seat = SeatTwo();

        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.B.SideId!.Value, new Vector3(900f, 880f, 870f));
        Assert.True(npc.IsNpc, "spawned NPC must carry the IsNpc marker");
        npc.ChangeHull(npc.HullType with { Signature = 100 });
        npc.Position = seat.A.Position + new Vector3(500, 0, 0);
        PumpUntil(seat.Server, () => npc.SeenBySide(seat.A.SideId!.Value), 1000);
        Assert.True(npc.SeenBySide(seat.A.SideId!.Value), "target must be detected before commanding it");
        Assert.False(seat.Server.Ships.Any(s => ReferenceEquals(s.Ship, npc)), "NPC must have no _peerShips entry (P147)");

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, IgcObjectType.Ship, npc.Id);

        var pumped = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.CommandTarget, npc), 8000);
        Assert.True(ReferenceEquals(seat.A.CommandTarget, npc),
            $"A c_cmdCurrent toward ownerless NPC {npc.Id} (side {npc.SideId}) must set A.CommandTarget to it via the sector scan (pumped {pumped} ticks; FedSrv.CPP:7335-7339)");

        Console.WriteLine($"P153-NPC: A({seat.A.Id}) c_cmdCurrent -> ownerless NPC {npc.Id} (side {npc.SideId}) : A.CommandTarget set (sector model-list scan)");
    }

    /// <summary>An unknown station id resolves to NULL and clears the current
    /// target, matching GetModel + SetCommand (FedSrv.CPP:7335-7339).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unknown_station_target_clears_current_target()
    {
        Require.Artwork();
        using var seat = SeatTwo();

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, IgcObjectType.Ship, seat.B.Id);
        var locked = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.CommandTarget, seat.B), 8000);
        Assert.True(locked > 0, $"precondition: A.CommandTarget must lock B first ({seat.B.Id})");

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, IgcObjectType.Station, 9199);
        PumpUntil(seat.Server, () => seat.A.CommandTarget is null, 8000);
        Assert.Null(seat.A.CommandTarget);
    }
}
