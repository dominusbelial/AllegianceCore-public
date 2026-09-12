using System.Reflection;
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
/// P110 — the teleport/relocation half of <c>FM_C_DOCKED(48)</c>: a client
/// naming a non-<c>NA</c> <c>stationID</c> is answered with
/// <c>FM_S_TELEPORT_ACK(102)</c>. Extends this port's existing docking recipe
/// (<see cref="DockDockingTests"/>) — the proximity-triggered dock remains
/// untouched; this is only the explicit-<c>stationID</c> request direction the
/// native responder serves (<c>FedSrv.CPP:7054-7131</c>). A real
/// <see cref="Srv"/> boots the 2-team XXV_simple map, a loopback
/// <see cref="FltBot"/> joins, and its ship is planted docked at the friendly
/// garrison (side 0's station 1051). The test then sends a non-<c>NA</c>
/// <c>FM_C_DOCKED(48)</c> and asserts the <c>TELEPORT_ACK</c> the server
/// returns — echoing the real station id, computing <c>bNewHull</c>, and
/// (when <c>bNewHull</c>) applying the refund/part-strip/Lifepod-hull-swap the
/// native performs (<c>FedSrv.CPP:7079-7120</c>), reusing this port's existing
/// helpers. Rejection (nonexistent / enemy / non-restart-capable station)
/// answers <c>stationID = NA</c> and leaves the ship where it is.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTeleportDockTests
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
            new Gm { Name = "Teleport Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0x911u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7700000000000000000ul, DrmHash = "teleport",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>The port's <c>bNewHull</c> decision reproduced independently in
    /// the test so the assertion does not just echo the production helper: a
    /// hull is sellable at a station iff its required-tech mask is contained in
    /// <c>stationType.TtbmLocal | civ.TtbmBaseTechs</c> (native
    /// <c>stationIGC.h:383-391</c>, <c>stationtypeigc.h:159-161</c>).</summary>
    private static bool SellsHull(StaticCore core, short stationTypeId, short civId, DataHullTypeIGC hull)
    {
        var st = core.StationTypes[stationTypeId];
        var civ = core.Civilizations[civId];
        var need = hull.TtbmRequired;
        for (int i = 0; i < need.Length && i < st.TtbmLocal.Length; i++)
        {
            byte avail = (byte)(st.TtbmLocal[i] | (i < civ.TtbmBaseTechs.Length ? civ.TtbmBaseTechs[i] : (byte)0));
            if ((need[i] & ~avail) != 0) return false;
        }
        for (int i = st.TtbmLocal.Length; i < need.Length; i++)
        {
            if (need[i] != 0 && (i >= civ.TtbmBaseTechs.Length || (need[i] & ~civ.TtbmBaseTechs[i]) != 0)) return false;
        }
        return true;
    }

    /// <summary>A single-bot spawner that plants the bot's ship docked beside the
    /// first friendly station (side ≥ 0). <paramref name="friendlyHullId"/> is
    /// applied when given (else the core's lowest hull id). A cheap weapon part
    /// is mounted when <paramref name="equipPart"/> so the bNewHull path has
    /// something to strip/refund. Both the chosen station and hull are stashed
    /// for the test to assert against.</summary>
    private sealed class SpawnResult
    {
        public DataStationIGC Station;
        public Sector? Sector;
        public Ship Ship = null!;
        public DataHullTypeIGC Hull;
        public bool PartEquipped;
    }

    private static SpawnResult InjectDocked(
        Srv server, short? friendlyHullId, bool equipPart, Action<StaticCore, Mission, DataStationIGC, Sector>? extra = null)
    {
        var r = new SpawnResult();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            extra?.Invoke(core, mission, station, sector);

            var hull = friendlyHullId is { } fid && core.HullTypes.TryGetValue(fid, out var h)
                ? h
                : core.HullTypes.Values.OrderBy(x => x.HullId).First();
            var ship = new Ship(shipId, hull, StationPos(station) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            {
                SideId = station.SideId,
            };
            mission.PlaceShip(ship, sector);

            if (equipPart)
            {
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            }

            r.Station = station; r.Sector = sector; r.Ship = ship; r.Hull = hull; r.PartEquipped = equipPart;
            return (ship, sector);
        };
        return r;
    }

    /// <summary>Wait for the bot's ship to be docked server-side (its first
    /// tick latches it inside the friendly station's radius).</summary>
    private static Ship AwaitDocked(Srv server, FltBot bot, short shipId)
    {
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);
        return server.Ships.First(s => s.Ship.Id == shipId).Ship;
    }

    // ── the codec ──────────────────────────────────────────────────────────

    /// <summary><c>FM_S_TELEPORT_ACK</c>(102) round-trips exactly — header plus
    /// the <c>StationID</c> short and the <c>bNewHull</c> bool, cbmsg = 7
    /// (Messages.h:681-684).</summary>
    [Fact]
    public void TeleportAck_message_round_trips()
    {
        var msg = new TeleportAck { StationId = 1051, BNewHull = true };
        var d = TeleportAck.Decode(msg.Encode());
        Assert.Equal(TeleportAck.Fmid, d.GetFmid());
        Assert.Equal((short)1051, d.StationId);
        Assert.True(d.BNewHull);

        var na = new TeleportAck { StationId = -1, BNewHull = false };
        var d2 = TeleportAck.Decode(na.Encode());
        Assert.Equal((short)-1, d2.StationId);
        Assert.False(d2.BNewHull);
    }

    /// <summary><c>FM_S_TELEPORT_ACK</c>(102) emits the exact 7 declared bytes,
    /// hand-composed from <c>Messages.h:681-684</c> — cbmsg=7, fmid=102, then the
    /// <c>StationID</c> short and the <c>bNewHull</c> byte, no padding.</summary>
    [Fact]
    public void TeleportAck_message_emits_exact_declared_bytes()
    {
        var got = new TeleportAck { StationId = 0x4142, BNewHull = false }.Encode();
        Assert.Equal(7, got.Length);
        Assert.Equal(new byte[] { 7, 0, 102, 0, 0x42, 0x41, 0x00 }, got);

        var got2 = new TeleportAck { StationId = 0x4142, BNewHull = true }.Encode();
        Assert.Equal(new byte[] { 7, 0, 102, 0, 0x42, 0x41, 0x01 }, got2);
        // Decode is symmetric.
        Assert.Equal((short)0x4142, TeleportAck.Decode(got).StationId);
        Assert.False(TeleportAck.Decode(got).BNewHull);
        Assert.True(TeleportAck.Decode(got2).BNewHull);
    }

    // ── scenario 1: valid, bNewHull = false ────────────────────────────────

    /// <summary>⭐ A docked ship asks to teleport to its own (friendly,
    /// restart-capable) garrison with a hull the garrison sells → the server
    /// replies <c>TELEPORT_ACK</c> echoing the real stationID and
    /// <c>bNewHull = false</c>, and the ship ends up docked at that station.</summary>
    [Fact]
    public void Teleport_to_friendly_station_with_buyable_hull_acks_bNewHull_false()
    {
        using var server = new Srv();
        SpawnResult r = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var side = mission.Sides.First(s => s.Id == station.SideId);
            // The garrison sells starter hulls for this civ; pick the cheapest
            // buyable one so bNewHull is provably false on real data.
            var hull = core.HullTypes.Values.Where(h => SellsHull(core, station.StationTypeId, side.Civilization.CivilizationId, h))
                                            .OrderBy(h => h.Price).First();
            var ship = new Ship(shipId, hull, StationPos(station) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            r = new SpawnResult { Station = station, Sector = sector, Ship = ship, Hull = hull };
            return (ship, sector);
        };

        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "tp_buyable");
        bot.SendLogonRequest(Request("tp_buyable"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        var live = AwaitDocked(server, bot, shipId);
        Assert.NotNull(live.StationId);

        bot.ResetTeleportAcks();
        bot.SendDocked(r.Station.StationId);          // FM_C_DOCKED(48) with a real stationID (P110 teleport request)
        PumpUntil(server, () => bot.TeleportAcks.Count >= 1, 4000);

        var ack = Assert.Single(bot.TeleportAcks);
        Assert.Equal(r.Station.StationId, ack.StationId);   // echoed, not NA
        Assert.False(ack.BNewHull);                          // the garrison sells this hull

        // Server side: still docked, hull unchanged (no swap).
        var after = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.NotNull(after.StationId);
        Assert.Equal(r.Station.StationId, after.StationId);
        Assert.Equal(r.Hull.HullId, after.HullType.HullId);
    }

    // ── scenario 2: valid, bNewHull = true (refund + strip + lifepod swap) ──

    /// <summary>⭐ A docked ship teleports to its garrison with a hull the
    /// garrison does NOT sell → <c>bNewHull = true</c> in the ACK, and the
    /// server applies the native's three operations (<c>FedSrv.CPP:7079-7120</c>):
    /// refund the hull+part value to the side treasury, strip all parts, and
    /// swap to the side's Lifepod hull — the ship redocked at the target.</summary>
    [Fact]
    public void Teleport_with_non_buyable_hull_refunds_strips_and_swaps_to_lifepod()
    {
        using var server = new Srv();
        SpawnResult r = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var side = mission.Sides.First(s => s.Id == station.SideId);
            // The cheapest hull the garrison does NOT sell, with a positive price
            // so the refund is real money (not a 0-credit no-op).
            var hull = core.HullTypes.Values.Where(h => !SellsHull(core, station.StationTypeId, side.Civilization.CivilizationId, h) && h.Price > 0)
                                            .OrderBy(h => h.Price).First();
            var ship = new Ship(shipId, hull, StationPos(station) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            // Equip a real part so the strip has something to clear and the
            // refund includes its price.
            var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
            ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            r = new SpawnResult { Station = station, Sector = sector, Ship = ship, Hull = hull, PartEquipped = true };
            return (ship, sector);
        };

        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "tp_newhull");
        bot.SendLogonRequest(Request("tp_newhull"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        var live = AwaitDocked(server, bot, shipId);
        Assert.NotNull(live.StationId);
        Assert.True(live.Parts.Count >= 1, "test must have equipped a part for the strip to be proven");

        var side = server.Missions.Single().Sides.First(s => s.Id == live.SideId);
        long moneyBefore = side.Money;
        int partPrice = live.Parts.Sum(p => p.Type.Price);
        int expectRefund = live.HullType.Price + partPrice;
        short lifepodId = side.Civilization.Lifepod;

        bot.ResetTeleportAcks();
        bot.SendDocked(r.Station.StationId);
        PumpUntil(server, () => bot.TeleportAcks.Count >= 1, 4000);

        var ack = Assert.Single(bot.TeleportAcks);
        Assert.Equal(r.Station.StationId, ack.StationId);
        Assert.True(ack.BNewHull);

        var after = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        // Hull swap → the side's Lifepod hull (FedSrv.CPP:7116-7117).
        Assert.Equal(lifepodId, after.HullType.HullId);
        // Redocked at the target.
        Assert.Equal(r.Station.StationId, after.StationId);
        // Parts stripped (FedSrv.CPP:7107-7113).
        Assert.Empty(after.Parts);
        // Refund deposited into the side treasury (this port's money primitive,
        // same as P108's board refund).
        long moneyAfter = side.Money;
        Assert.Equal(expectRefund, (int)(moneyAfter - moneyBefore));

        Console.WriteLine($"TELEPORT-LIVE: ship {shipId} tp to {r.Station.StationId} bNewHull=true refund {expectRefund} (hull {r.Hull.HullId} + part {partPrice}) -> Lifepod {lifepodId}");
    }

    // ── scenario 3: rejections → stationID = NA ─────────────────────────────

    /// <summary>⭐ A request naming a station that does not exist answers
    /// <c>TELEPORT_ACK</c> with <c>stationID = NA</c>, and the ship stays where
    /// it was (FedSrv.CPP:7063,7067-7073).</summary>
    [Fact]
    public void Teleport_to_nonexistent_station_rejects_with_NA()
    {
        using var server = new Srv();
        var r = InjectDocked(server, friendlyHullId: null, equipPart: false);
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "tp_missing");
        bot.SendLogonRequest(Request("tp_missing"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        var live = AwaitDocked(server, bot, shipId);

        bot.ResetTeleportAcks();
        bot.SendDocked((short)9999);                 // no such station in XXV_simple
        PumpUntil(server, () => bot.TeleportAcks.Count >= 1, 4000);
        var ack = Assert.Single(bot.TeleportAcks);
        Assert.Equal((short)-1, ack.StationId);      // NA — rejected
        Assert.False(ack.BNewHull);

        var after = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(live.StationId, after.StationId);   // left where it was
        Assert.Equal(live.HullType.HullId, after.HullType.HullId);
    }

    /// <summary>⭐ A request naming an enemy (other side's) station answers
    /// <c>stationID = NA</c> — the friendly/allied gate, not geometry
    /// (FedSrv.CPP:7064). The ship stays put.</summary>
    [Fact]
    public void Teleport_to_enemy_station_rejects_with_NA()
    {
        using var server = new Srv();
        var r = InjectDocked(server, friendlyHullId: null, equipPart: false);
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "tp_enemy");
        bot.SendLogonRequest(Request("tp_enemy"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        var live = AwaitDocked(server, bot, shipId);

        // The other team's garrison (on the 2-team map the station is on the
        // other side, hence not friendly). It EXISTS and is restart-capable —
        // only the friendly gate rejects it.
        var enemy = server.Missions.Single().Sectors.SelectMany(s => s.Stations)
            .First(st => st.SideId != live.SideId);

        bot.ResetTeleportAcks();
        bot.SendDocked(enemy.StationId);
        PumpUntil(server, () => bot.TeleportAcks.Count >= 1, 4000);
        var ack = Assert.Single(bot.TeleportAcks);
        Assert.Equal((short)-1, ack.StationId);      // NA — rejected
        Assert.False(ack.BNewHull);

        var after = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(live.StationId, after.StationId);   // left where it was
    }

    /// <summary>⭐ A request naming a friendly-but-NOT-restart-capable station
    /// (a synthetic station of station-type 28 'Teleport', sabm=0x0008, no
    /// c_sabmRestart=0x04) answers <c>stationID = NA</c> — the restart-capable
    /// gate (FedSrv.CPP:7065), which has no natural target on XXV_simple (both
    /// real stations are Garrisons). The synthetic station is injected into the
    /// sector's station list via reflection, far from the ship so it never
    /// affects normal docking. The ship stays put.</summary>
    [Fact]
    public void Teleport_to_friendly_non_restart_capable_station_rejects_with_NA()
    {
        using var server = new Srv();
        short fakeId = 9001;
        SpawnResult r = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            // type 28 'Teleport' has sabm=0x0008 (no restart bit 0x04).
            var fake = new DataStationIGC(
                Position: [2000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                ClusterId: sector.Id, SideId: station.SideId, StationId: fakeId,
                StationTypeId: 28, BpHull: 1, BpShield: 1, Name: "FakeTeleport");
            AddStationToSector(sector, fake);

            var hull = core.HullTypes.Values.OrderBy(x => x.HullId).First();
            var ship = new Ship(shipId, hull, StationPos(station) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            r = new SpawnResult { Station = station, Sector = sector, Ship = ship, Hull = hull };
            return (ship, sector);
        };

        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "tp_norestart");
        bot.SendLogonRequest(Request("tp_norestart"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        var live = AwaitDocked(server, bot, shipId);
        Assert.Equal(r.Station.StationId, live.StationId);   // docked at the REAL garrison, not the fake

        bot.ResetTeleportAcks();
        bot.SendDocked(fakeId);                 // friendly, exists, but type 28 not restart-capable
        PumpUntil(server, () => bot.TeleportAcks.Count >= 1, 4000);
        var ack = Assert.Single(bot.TeleportAcks);
        Assert.Equal((short)-1, ack.StationId); // NA — rejected on the restart gate
        Assert.False(ack.BNewHull);

        var after = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(live.StationId, after.StationId);   // left where it was
    }

    // ── scenario 4: undock direction regression ─────────────────────────────

    /// <summary>⭐ Regression: <c>FM_C_DOCKED(48)</c> with <c>stationID == NA</c>
    /// still undocks exactly as before this task — <c>StationId</c> cleared,
    /// a flying status on the wire, no TELEPORT_ACK.</summary>
    [Fact]
    public void Undock_stationID_NA_still_undocks_with_no_teleport_ack()
    {
        using var server = new Srv();
        InjectDocked(server, friendlyHullId: null, equipPart: false);
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "tp_undock");
        bot.SendLogonRequest(Request("tp_undock"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        AwaitDocked(server, bot, shipId);

        bot.ResetShipStatuses();
        bot.ResetTeleportAcks();
        bot.SendDocked();                                    // stationID = -1 = NA (the undock announce)
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 4000);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);

        // No teleport ACK is emitted for the NA direction.
        Assert.Empty(bot.TeleportAcks);

        // The wire carries the freed flying status (P53's undock proof).
        PumpUntil(server,
            () => bot.ShipStatuses.Any(s => s.ShipId == shipId && s.State == ShipStatusMessage.StateFlying && s.StationId == -1),
            4000);
        Assert.Contains(bot.ShipStatuses,
            s => s.ShipId == shipId && s.State == ShipStatusMessage.StateFlying && s.StationId == -1);
    }

    /// <summary>Reflection-free helper note: the Core assembly does not expose
    /// <c>AttachStations</c> to tests, so the synthetic-station injection walks
    /// the private <c>_stations</c> list directly. Test-only, on the sim thread
    /// (ShipSpawner runs during the join), safe because the server disposes
    /// afterward.</summary>
    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var field = typeof(Sector).GetField("_stations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var list = (List<DataStationIGC>)field.GetValue(sector)!;
        list.Add(station);
    }
}
