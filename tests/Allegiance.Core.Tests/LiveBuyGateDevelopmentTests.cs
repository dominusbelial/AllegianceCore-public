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
/// P283 — the P110/P189 disconnect closure: the station buy-gate
/// (<see cref="Srv"/>'s <c>StationSellsHull</c>, the port's
/// <c>station.CanBuy</c>) now consults the side's live
/// <see cref="Side.DevelopmentTechs"/> (grown by real development completion,
/// P189) instead of only the civ's static base-tech mask. Real corpus data
/// (verified against PCore001.igc in the probe): dev 37 'Bomber'
/// (ttb=120s, price=10000, adds 1 new effect bit to civ 1 Dreghklar's 28-base
/// mask) unlocks hull 25 'Bomber' (price 500) at the garrison — which the
/// garrison does NOT sell before the development is researched. Two live
/// loopback proofs over a real in-process server + real TCP bots:
///   (1) BEFORE — a docked ship holding hull 25 teleports to its garrison
///       while the development is unresearched → <c>TELEPORT_ACK bNewHull =
///       true</c> (garrison does not sell it yet) — real wire evidence;
///   (2) the development is completed FOR REAL (<c>BUCKET_DONATE</c> →
///       build time → <c>CompleteDevelopment</c> ORs the new bit into
///       <c>Side.DevelopmentTechs</c> and broadcasts <c>SIDE_TECH_CHANGE</c>);
///   (3) AFTER — a second docked ship holding hull 25 teleports to the same
///       garrison → <c>TELEPORT_ACK bNewHull = false</c> (garrison sells it
///       now) — the discriminating real-wire proof that the buy-gate now
///       reflects research (must fail if the fix is reverted).
/// </summary>
[Collection("LiveNetwork")]
public class LiveBuyGateDevelopmentTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Real corpus data (measured in the P283 probe, PCore001.igc): dev 37
    // 'Bomber' unlocks hull 25 'Bomber' at a garrison for civ 1 (Dreghklar).
    private const short BombDevId = 37;
    private const short BombHullId = 25;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 1,   // dt = 1s per TickSim → dev 37's 120s build ≈ 120 ticks
        Games =
        [
            new Gm { Name = "BuyGate Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 883u, DwCookie = 0x883u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8800000000000000000ul, DrmHash = "buygate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Connect + logon + pump one bot to a seated ship so the
    /// ShipSpawner invocation order (hence the ship the bot receives) is
    /// deterministic rather than dependent on seating races.</summary>
    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Two-ship spawner: both ships get the target hull 25 'Bomber',
    /// placed beside the first friendly garrison on its side (side 0, civ 1
    /// Dreghklar) so each docks there.</summary>
    private sealed class TwoSpawn
    {
        public DataStationIGC Station;
        public Ship A = null!, B = null!;
    }

    private static TwoSpawn InjectTwoBombers(Srv server)
    {
        var sp = new TwoSpawn();
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[BombHullId];
            var pos = StationPos(station) + new Vector3(25f, 0f, seq == 0 ? 0f : -25f);
            var ship = new Ship(shipId, hull, pos, Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            if (seq == 0) sp.A = ship; else sp.B = ship;
            sp.Station = station;
            seq++;
            return (ship, sector);
        };
        return sp;
    }

    /// <summary>Wait for a ship to latch docked at its station (the proximity
    /// dock at spawn).</summary>
    private static Ship AwaitDocked(Srv server, short shipId)
    {
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);
        return server.Ships.First(s => s.Ship.Id == shipId).Ship;
    }

    private static Bucket DevBucket(Side side, short devId) =>
        side.Buckets.Single(b => b.ItemType == BucketItemType.BitDevelopment
                                 && b.Development is { } dd && dd.DevelopmentId == devId);

    /// <summary>⭐ The discriminating live proof: hull 25 'Bomber' is NOT
    /// sold at the garrison before dev 37 'Bomber' is researched (wire
    /// <c>bNewHull = true</c>), becomes sold AFTER the development is completed
    /// for real (wire <c>bNewHull = false</c>), and completing the dev ORs its
    /// effect bit into <see cref="Side.DevelopmentTechs"/> + broadcasts
    /// <c>SIDE_TECH_CHANGE(75)</c>. Under the old static-civ-mask gate the
    /// AFTER teleport would still report <c>bNewHull = true</c> — the
    /// must-fail-if-reverted assertion.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Researched_development_flips_the_station_buy_gate_on_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectTwoBombers(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        using var a = new FltBot();
        using var b = new FltBot();
        SeatOne(server, a, "BG_A");   // seq 0 → ship A (hull 25)
        SeatOne(server, b, "BG_B");   // seq 1 → ship B (hull 25)

        // Map each bot to its spawned ship; both must be hull 25 on the
        // friendly (civ 1) side, docked at the garrison.
        short aId = a.ShipAssignment!.Ship.ShipId;
        short bId = b.ShipAssignment!.Ship.ShipId;
        Assert.Contains(aId, new[] { sp.A.Id, sp.B.Id });
        Assert.Contains(bId, new[] { sp.A.Id, sp.B.Id });
        Assert.NotEqual(aId, bId);
        var shipA = server.Ships.First(s => s.Ship.Id == aId).Ship;
        var shipB = server.Ships.First(s => s.Ship.Id == bId).Ship;
        Assert.Equal(BombHullId, shipA.HullType.HullId);
        Assert.Equal(BombHullId, shipB.HullType.HullId);
        Assert.Equal(sp.Station.SideId, shipA.SideId);
        Assert.Equal(sp.Station.SideId, shipB.SideId);
        Assert.Equal((short)1, server.Missions[0].Sides.Single(s => s.Id == shipA.SideId).Civilization.CivilizationId);
        AwaitDocked(server, aId);
        AwaitDocked(server, bId);

        short stationId = sp.Station.StationId;
        var side = server.Missions[0].Sides.Single(s => s.Id == shipA.SideId);
        var dev = server.Missions[0].Core.Developments[BombDevId];
        var hull = server.Missions[0].Core.HullTypes[BombHullId];
        int initialBits = side.DevelopmentTechs.PopCount;

        // ---- BEFORE: dev 37 unresearched → garrison does not sell hull 25. ----
        a.ResetTeleportAcks();
        a.SendDocked(stationId);                                   // FM_C_DOCKED(48) teleport request
        int b4 = PumpUntil(server, () => a.TeleportAcks.Count >= 1, 4000);
        Assert.True(b4 < 4000, "BEFORE teleport ACK never arrived");
        var beforeAck = Assert.Single(a.TeleportAcks);
        Assert.Equal(stationId, beforeAck.StationId);              // echoed, not NA
        Assert.True(beforeAck.BNewHull, "hull 25 must NOT be buyable before dev 37 is researched");

        // ---- COMPLETE dev 37 FOR REAL: BUCKET_DONATE → build time → CompleteDevelopment. ----
        var bucket = DevBucket(side, BombDevId);
        b.ResetSideTechChanges();
        server.Ships.First(s => s.Ship.Id == bId).Wallet.Money = 10_000_000;   // P198: fund the donor's PLAYER wallet
        b.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), 700);
        Assert.True(done < 700, $"dev {BombDevId} bucket {bucket.Id} did not complete (invested {bucket.InvestedMoney}/{bucket.Price}, ms {bucket.InvestedMilliseconds})");

        // The bit landed on the side (real mutation)…
        Assert.Equal(initialBits + 1, side.DevelopmentTechs.PopCount);
        // …and the post-OR side techs now satisfy hull 25's requirement at the
        // garrison (the exact composition StationSellsHull now uses).
        var local = TechTreeBitMask.FromBytes(
            server.Missions[0].Core.StationTypes[sp.Station.StationTypeId].TtbmLocal);
        Assert.True(TechTreeBitMask.FromBytes(hull.TtbmRequired).IsSubsetOf(local.Or(side.DevelopmentTechs)),
            "after dev completion, hull 25 must be contained in the garrison's local | side dev techs");

        // SIDE_TECH_CHANGE(75) reached the donor peer (side-group broadcast).
        int stc = PumpUntil(server, () => b.SideTechChanges.Count >= 1, 400);
        Assert.True(stc < 400, "SIDE_TECH_CHANGE(75) never reached the donor peer");
        Assert.Equal(side.Id, Assert.Single(b.SideTechChanges).SideId);
        Assert.Equal(side.DevelopmentTechs.ToBytes(), Assert.Single(b.SideTechChanges).TtbmDevelopments);

        // ---- AFTER: dev 37 researched → the same garrison NOW sells hull 25. ----
        b.ResetTeleportAcks();
        b.SendDocked(stationId);                                   // FM_C_DOCKED(48) teleport request
        int aft = PumpUntil(server, () => b.TeleportAcks.Count >= 1, 4000);
        Assert.True(aft < 4000, "AFTER teleport ACK never arrived");
        var afterAck = Assert.Single(b.TeleportAcks);
        Assert.Equal(stationId, afterAck.StationId);               // echoed, not NA
        Assert.False(afterAck.BNewHull, "hull 25 MUST be buyable after dev 37 completes — the P283 fix is what makes this flip");

        Console.WriteLine($"BUYGATE-P283: hull {BombHullId} '{hull.Name}' at garrison {stationId}: dev {BombDevId} '{dev.Name}' unresearched -> bNewHull=true (wire); BUCKET_DONATE completed it (+1 bit, {initialBits}->{side.DevelopmentTechs.PopCount}, SIDE_TECH_CHANGE(75)); same garrison -> bNewHull=false (wire)");
    }
}
