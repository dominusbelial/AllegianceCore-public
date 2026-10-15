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
/// P444 — owning a techbase unlocks its tier <b>side-wide</b>: the server's
/// buy-gate (<see cref="Srv"/>'s <c>StationSellsHull</c> / P441's BUY_LOADOUT
/// gate) now consults <c>Side.Techs = BuildingTechs | DevelopmentTechs</c>
/// (native <c>side.GetTechs()</c>, sideigc.h:133-136) instead of
/// <c>DevelopmentTechs</c> alone, where <c>BuildingTechs</c> (P444) is the OR
/// of every owned station type's <c>TtbmEffects</c> — the recompute-on-access
/// port of native <c>ttbmBuildingTechs</c>. Real PCore001 example (measured
/// by the P444 probe): civ 5 'Omicron' owns garrison type 15 'Garrison';
/// building a Supremacy (type 11, class 6) unlocks hull 714 'Enh Fighter',
/// which the garrison does NOT sell while the side only owns its garrison.
/// Three live loopback proofs over a real in-process server + real TCP bots:
///   (1) docked at the home garrison, the hull is refused by the teleport
///       buy-gate (<c>TELEPORT_ACK bNewHull = true</c>); after placing the
///       owning Supremacy for that side it becomes sellable
///       (<c>bNewHull = false</c>) — building tech is now side-wide;
///   (2) <c>Side.Techs</c> tracks ownership: place → unlocked; capture →
///       relocked; re-place → unlocked; remove → relocked;
///   (3) the P441 BUY_LOADOUT path (a DOCKED funded buyer buying the hull)
///       is refused before the station is placed and granted after.
/// Each must FAIL if <c>StationSellsHull</c>/the P441 gate is reverted to
/// <c>side.DevelopmentTechs</c> (the bNewHull flip would never happen).
/// </summary>
[Collection("LiveNetwork")]
public sealed class BuildingTechsLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Civ 5 'Omicron' for side 0 so the map's type-15 garrison + a Supremacy
    // unlocks hull 714 'Enh Fighter' (the P444 probe-verified pair).
    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P444 BuildingTechs", Map = "XXV_simple.igc", Teams = 2, CivIds = [5, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 571u, DwCookie = 0x571u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88900000000000000ul, DrmHash = "p444building",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static Ship LiveShip(Srv server, short id) =>
        server.Ships.First(x => x.Ship.Id == id).Ship;

    private static void AwaitDocked(Srv server, short shipId)
    {
        PumpUntil(server, () => LiveShip(server, shipId).StationId is not null, 400);
    }

    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    private static void Start(Srv server)
    {
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby
    }

    /// <summary>The side's own home garrison + the real (S, H) unlock pair for
    /// it: station type S (a techbase class) whose <c>TtbmEffects</c> added to
    /// the side's live <see cref="Side.Techs"/> makes hull H buyable at the
    /// garrison, while H is NOT buyable there with today's <see cref="Side.Techs"/>
    /// (the side owns only its garrison). Derived from the real core so the test
    /// is self-evident; prefers a Supremacy/Shipyard-class S.</summary>
    private static (DataStationTypeIGC s, DataHullTypeIGC h) UnlockPair(Mission m, Side side)
    {
        var g = m.Sectors.SelectMany(x => x.Stations).First(st => st.SideId == side.Id);
        var gtype = m.Core.StationTypes[g.StationTypeId];
        foreach (var s in m.Core.StationTypes.Values
            .Where(s => s.ClassId is 2 or 5 or 6 or 7)
            .OrderBy(s => s.ClassId == 6 ? 0 : (s.ClassId == 2 ? 1 : 2)))
        {
            var effects = TechTreeBitMask.FromBytes(s.TtbmEffects);
            foreach (var h in m.Core.HullTypes.Values.OrderBy(h => h.HullId))
            {
                bool before = TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, gtype.TtbmLocal, side.Techs);
                bool after = TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, gtype.TtbmLocal, side.Techs.Or(effects));
                if (!before && after) return (s, h);
            }
        }
        throw new InvalidOperationException("PCore001 has no techbase-unlocked hull for this side/garrison");
    }

    /// <summary>Place a runtime station of the given type, owned by
    /// <paramref name="sideId"/>, into the side's garrison sector. The runtime
    /// <see cref="Station"/> object is returned for later capture/removal.</summary>
    private static Station PlaceOwningStation(Mission m, Side side, DataStationTypeIGC st, short sideId)
    {
        var g = m.Sectors.SelectMany(x => x.Stations).First(x => x.SideId == side.Id);
        var sector = m.Sectors.First(s => s.Stations.Contains(g));
        var data = new DataStationIGC(
            Position: [g.Position[0], g.Position[1] + 220f, g.Position[2]],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: sideId, StationId: 8866,
            StationTypeId: st.StationTypeId, BpHull: 240, BpShield: 240, Name: st.Name);
        var station = new Station(data, st);
        m.PlaceStation(station, sector);
        return station;
    }

    private static (DataStationTypeIGC st, Side side, DataStationIGC garrison) Garrison(Mission m)
    {
        var garrison = m.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var st = m.Core.StationTypes[garrison.StationTypeId];
        var side = m.Sides.First(s => s.Id == garrison.SideId);
        return (st, side, garrison);
    }

    /// <summary>⭐ (1) The core discriminating wire proof: the techbase-unlocked
    /// hull is refused at the home garrison (<c>TELEPORT_ACK bNewHull =
    /// true</c>) while the side owns only its garrison; after placing an owning
    /// Supremacy the SAME garrison sells it (<c>bNewHull = false</c>) — building
    /// techs are side-wide. Two fresh bots each hold the hull (a teleport that
    /// finds the hull unsellable converts the ship to a Lifepod, changing its
    /// current hull — hence P283's two-bot pattern). Under the old
    /// <c>DevelopmentTechs</c>-only gate the flip would never happen — the
    /// must-fail-if-reverted assertion.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owning_a_supremacy_makes_the_garrison_sell_its_hull_on_the_wire()
    {
        Require.Artwork();
        var server = new Srv();
        Mission? capM = null; Side? capSide = null;
        DataStationTypeIGC sT = default; DataHullTypeIGC hH = default;
        DataStationIGC garrison = default;
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            capM = mission;
            var (st, side, g) = Garrison(mission);
            capSide = side; sT = UnlockPair(mission, side).s; hH = UnlockPair(mission, side).h;
            garrison = g;
            var sector = mission.Sectors.First(x => x.Stations.Contains(g));
            var ship = new Ship(shipId, hH, StationPos(g) + new Vector3(25f, 0f, seq++ == 0 ? 0f : -25f),
                Vector3.Zero, Orientation.Identity) { SideId = side.Id, StationId = g.StationId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        using (server)
        {
            Start(server);
            using var a = new FltBot();
            using var b = new FltBot();
            SeatOne(server, a, "p444_a");
            SeatOne(server, b, "p444_b");
            Assert.NotNull(capM); Assert.NotNull(capSide);
            var m = capM!; var side = capSide!; var sType = sT; var hHull = hH;
            Assert.Equal("Omicron", side.Civilization.Name);
            Assert.Equal((short)714, hHull.HullId);   // 'Enh Fighter' (PCore001 probe)

            short aId = a.ShipAssignment!.Ship.ShipId;
            short bId = b.ShipAssignment!.Ship.ShipId;
            AwaitDocked(server, aId);
            AwaitDocked(server, bId);
            Assert.Equal(hHull.HullId, LiveShip(server, aId).HullType.HullId);
            Assert.Equal(hHull.HullId, LiveShip(server, bId).HullType.HullId);

            // BEFORE: hull 714 unsellable at the garrison → bNewHull = true.
            a.ResetTeleportAcks();
            a.SendDocked(garrison.StationId);
            int b4 = PumpUntil(server, () => a.TeleportAcks.Count >= 1, 4000);
            Assert.True(b4 < 4000, "BEFORE teleport ACK never arrived");
            var beforeAck = Assert.Single(a.TeleportAcks);
            Assert.Equal(garrison.StationId, beforeAck.StationId);
            Assert.True(beforeAck.BNewHull, "hull 714 must NOT be buyable at the garrison before the Supremacy is built");

            // Place the owning Supremacy → BuildingTechs (hence side.Techs) gains it.
            PlaceOwningStation(m, side, sType, side.Id);
            Assert.True(TechTreeBitMask.RequiredTechsBuyable(hHull.TtbmRequired,
                m.Core.StationTypes[garrison.StationTypeId].TtbmLocal, side.Techs),
                "after placing the Supremacy, side.Techs must contain the hull's requirement");

            // AFTER (fresh bot B still holding 714): the SAME garrison now sells it.
            b.ResetTeleportAcks();
            b.SendDocked(garrison.StationId);
            int aft = PumpUntil(server, () => b.TeleportAcks.Count >= 1, 4000);
            Assert.True(aft < 4000, "AFTER teleport ACK never arrived");
            var afterAck = Assert.Single(b.TeleportAcks);
            Assert.Equal(garrison.StationId, afterAck.StationId);
            Assert.False(afterAck.BNewHull, "hull 714 MUST be buyable at the garrison after the Supremacy is built");

            Console.WriteLine($"P444-WIRE: {side.Civilization.Name} at garrison {garrison.StationId}: hull {hHull.HullId} '{hHull.Name}' no Supremacy -> bNewHull=true; +station {sType.StationTypeId} '{sType.Name}' -> bNewHull=false (building techs side-wide)");
        }
    }

    /// <summary>⭐ (2) Deterministic mutation proof (no wire): <see cref="Side.Techs"/>
    /// gains the Supremacy's <c>TtbmEffects</c> on placement, loses them on
    /// capture to the enemy, regains on re-placement, and loses again on
    /// removal — <see cref="Side.BuildingTechs"/> stays current through all
    /// four station-state changes the native keeps it current through
    /// (AddStation / DeleteStation / capture re-side).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuildingTechs_tracks_place_capture_and_remove()
    {
        Require.Artwork();
        using var server = new Srv();
        Start(server);
        var (st, side, garrison) = Garrison(server.Missions[0]);
        var m = server.Missions[0];
        var sType = UnlockPair(m, side).s;
        var hHull = UnlockPair(m, side).h;
        short gid = garrison.StationId;
        var glocal = m.Core.StationTypes[garrison.StationTypeId].TtbmLocal;
        bool Sellable() => TechTreeBitMask.RequiredTechsBuyable(hHull.TtbmRequired, glocal, side.Techs);

        // No owning Supremacy yet → not sellable at the garrison.
        Assert.False(Sellable());

        // Place → sellable (BuildingTechs gained the effects).
        var placed = PlaceOwningStation(m, side, sType, side.Id);
        Assert.True(Sellable(), "placing the Supremacy must make its hull sellable at the garrison");

        // Capture to the enemy → no longer sellable.
        short enemy = m.Sides.First(x => x.Id != side.Id).Id;
        placed.Capture(enemy);
        Assert.False(Sellable(), "capturing the Supremacy to the enemy must relock its hull");

        // Re-place for the side → sellable again.
        var placed2 = PlaceOwningStation(m, side, sType, side.Id);
        Assert.True(Sellable(), "re-placing the Supremacy must unlock again");

        // Remove (destruction) → relocked.
        var sector2 = m.Sectors.First(x => x.StationRuntimes.Contains(placed2));
        sector2.RemoveStation(placed2);
        Assert.False(Sellable(), "removing the Supremacy must relock its hull");

        Console.WriteLine($"P444-MUT: {side.Civilization.Name} garage {gid}: hull {hHull.HullId} '{hHull.Name}' + station {sType.StationTypeId} '{sType.Name}': place=unlock, capture=relock, replace=unlock, remove=relock");
    }

    /// <summary>⭐ (3) The P441 BUY_LOADOUT gate (a DOCKED funded buyer buying
    /// the Supremacy-unlocked hull) refuses before the station is placed and
    /// grants the hull after — proving the P441 hull gate reads
    /// <c>Side.Techs</c> too.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_gate_grants_the_techbase_hull_after_the_station_is_placed()
    {
        Require.Artwork();
        Mission? m = null; Side? side = null;
        DataStationTypeIGC sT = default; DataHullTypeIGC hH = default;
        short spawnHullId = 0;

        var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var (st, sd, g) = Garrison(mission);
            m = mission; side = sd; sT = UnlockPair(mission, sd).s; hH = UnlockPair(mission, sd).h;
            var sector = mission.Sectors.First(x => x.Stations.Contains(g));
            // spawn a DIFFERENT free, garrison-sellable hull so the buy changes the hull.
            var gtype = core.StationTypes[g.StationTypeId];
            var starter = core.HullTypes.Values
                .Where(h => h.Price == 0 && TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, gtype.TtbmLocal, sd.Techs))
                .OrderBy(h => h.HullId).First();
            spawnHullId = starter.HullId;
            var ship = new Ship(shipId, starter, StationPos(g) + new Vector3(25f, 0f, 0f),
                Vector3.Zero, Orientation.Identity) { SideId = g.SideId, StationId = g.StationId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        using (server)
        {
            Start(server);
            using var bot = new FltBot();
            SeatOne(server, bot, "p444_buy");
            short bid = bot.ShipAssignment!.Ship.ShipId;
            AwaitDocked(server, bid);

            var mi = m!; var sd = side!; var sType = sT; var hHull = hH;
            Assert.NotEqual(hHull.HullId, spawnHullId);

            // BEFORE: funded + docked, but the hull isn't sellable yet → refused.
            server.Ships.First(x => x.Ship.Id == bid).Wallet.Money = 1_000_000;
            bot.SendBuyLoadout(hHull.HullId, fLaunch: false);
            for (int i = 0; i < 1500; i++) server.TickSim();
            Assert.Equal(spawnHullId, LiveShip(server, bid).HullType.HullId);

            // AFTER placing the owning Supremacy → the same BUY_LOADOUT grants the hull.
            // P488 — the requested hull (Enh Fighter) is superseded by its buyable
            // successor (Adv Fighter, also unlocked by the Supremacy), and native
            // PurchaseShipLoadout upgrades the request (shipIGC.h:1004), so the ship
            // lands on the SUCCESSOR, not the requested hull. This still proves the
            // P441 gate read Side.Techs (the base was refused before, unlocked after).
            PlaceOwningStation(mi, sd, sType, sd.Id);
            bot.SendBuyLoadout(hHull.HullId, fLaunch: false);
            var grant = mi.Core.StationTypes.TryGetValue(Garrison(mi).garrison.StationTypeId, out var gt)
                ? SuccessorUpgrade.HullSuccessor(mi.Core, hHull, gt.TtbmLocal, sd.Techs)
                : hHull;
            Assert.NotEqual(hHull.HullId, grant.HullId);   // genuinely upgraded by P488
            int done = PumpUntil(server, () => LiveShip(server, bid).HullType.HullId == grant.HullId, 4000);
            Assert.True(done < 4000, $"BUY_LOADOUT of hull {hHull.HullId} was not granted as its successor {grant.HullId} after placing the Supremacy (still {LiveShip(server, bid).HullType.HullId})");
            Console.WriteLine($"P444-BUY: docked buyer bought hull {hHull.HullId} '{hHull.Name}' as its successor {grant.HullId} after the Supremacy '{sType.Name}' was placed");
        }
    }
}
