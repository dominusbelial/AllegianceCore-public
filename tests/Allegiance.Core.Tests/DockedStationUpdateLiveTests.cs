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
/// SIM-0144/P768 — a docked player must receive its station's hull/shield
/// fractions in <c>FM_S_STATIONS_UPDATE(57)</c> at least every 0.75 s of sim
/// time. Native sends a one-station message to each docked player
/// (FedSrv/FedSrv.CPP:8247-8273, gated on <c>dtimStationUpdateInterval = 0.75f</c>
/// at :8162). The port batches every station a side sees into one per-side
/// <see cref="StationsUpdate"/> sent every <see cref="Sector.c_nPassesPerUpdate"/>
/// ticks (P133/P324). This test proves the docked player's own station is in
/// that batch on the correct cadence.
/// </summary>
[Collection("LiveNetwork")]
public sealed class DockedStationUpdateLiveTests
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
            new Gm { Name = "Docked Station Update Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name,
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 768u,
        DwCookie = uint.MaxValue,
        CrcFileList = -1,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 76800000000000000ul,
        DrmHash = "dockedstationupdate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static byte Percent(float f) => (byte)(f * 240.0f + 0.5f);

    private static Station CreateSyntheticStation(
        StaticCore core, Sector sector, short stationId, Vector3 pos, DataStationTypeIGC baseType, bool zeroScanner)
    {
        // Regen is disabled so fractions stay stable while we measure the wire;
        // with zeroScanner the only reason the side sees the station is P132's
        // own-side rule, not a live scanner.
        var type = baseType with
        {
            ArmorRegeneration = 0f,
            ShieldRegeneration = 0f,
            ScannerRange = zeroScanner ? 0f : baseType.ScannerRange,
            SabmCapabilities = (short)(baseType.SabmCapabilities | DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmCapLand),
        };
        var data = new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id,
            SideId: 0,
            StationId: stationId,
            StationTypeId: baseType.StationTypeId,
            BpHull: 1,
            BpShield: 1,
            Name: "SYN_DOCK");
        var station = new Station(data, type);
        sector.Mission!.PlaceStation(station, sector);
        return station;
    }

    private static (Ship Ship, Sector Sector, Station Station) SeatDockedAtSyntheticStation(
        StaticCore core, Mission mission, short shipId, short stationId, bool zeroScanner)
    {
        var pos = new Vector3(0f, 0f, 0f);
        var sector = mission.Sectors[0];

        // Reuse the map's first real station type (a garrison with working
        // landing-bay geometry) so DockTestPlacement.Inside succeeds reliably.
        var mapStation = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var baseType = core.StationTypes[mapStation.StationTypeId];
        var station = CreateSyntheticStation(core, sector, stationId, pos, baseType, zeroScanner);

        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var spawnPos = DockTestPlacement.Inside(mission, station.Data, hull);
        var ship = new Ship(shipId, hull, spawnPos, Vector3.Zero, Orientation.Identity)
        {
            SideId = 0,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector, station);
    }

    private static StationsUpdateEntry EntryOf(StationsUpdate up, short stationId)
        => up.Entries.First(e => e.StationId == stationId);

    private static bool HasEntry(StationsUpdate up, short stationId)
        => up.Entries.Any(e => e.StationId == stationId);

    /// <summary>
    /// ⭐ Case 1 — the docked station is naturally seen by its own side. A
    /// player docked at a friendly synthetic station receives a
    /// <c>STATIONS_UPDATE</c> carrying that station's id and the changed
    /// hull/shield fractions within native's 0.75 s sim-time budget, and keeps
    /// receiving updates while docked.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_player_receives_seen_own_station_update_within_native_cadence()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var (ship, sector, station) = SeatDockedAtSyntheticStation(core, mission, shipId, 9601, zeroScanner: false);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p768_seen");
        bot.SendLogonRequest(Request("p768_seen"));

        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);
        var live = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        var station = server.Missions[0].Sectors[0].StationRuntimes.Single(s => s.StationId == 9601);

        Assert.NotNull(live.StationId);
        Assert.Equal((short)9601, live.StationId!.Value);
        // P324 (M2): the visibility sweep runs every c_nPassesPerUpdate sector
        // ticks, so wait for it before asserting the own-side rule.
        PumpUntil(server, () => station.SeenBySide(live.SideId!.Value), 2000);
        Assert.True(station.SeenBySide(live.SideId!.Value),
            "the docked player's own station must be visible to its own side");

        // Wait out any join-dirty immediate burst, then reset so every update
        // we measure is post-damage.
        PumpUntil(server, () => bot.StationsUpdates.Count > 0, 2000);
        bot.ResetStationsUpdates();

        typeof(Station).GetProperty(nameof(Station.ArmorFraction))!.SetValue(station, 0.70f);
        typeof(Station).GetProperty(nameof(Station.ShieldFraction))!.SetValue(station, 0.55f);

        double period = 1.0 / server.Config.UpdatesPerSecond;
        int maxTicks = (int)Math.Ceiling(0.75 * server.Config.UpdatesPerSecond);
        int ticks = 0;
        bool arrived = false;
        while (!arrived && ticks < maxTicks)
        {
            server.TickSim();
            Thread.Sleep(1);
            ticks++;
            arrived = bot.StationsUpdates.Any(u => HasEntry(u, station.StationId));
        }
        // The broadcast publisher (snapshot default, f4d9263d) sends on its own
        // thread: an update the server emitted on the last allowed tick may still be
        // in flight. Wait briefly in wall time WITHOUT ticking — the cadence bound
        // (sent within maxTicks) is unchanged.
        var drain = System.Diagnostics.Stopwatch.StartNew();
        while (!arrived && drain.ElapsedMilliseconds < 500)
        {
            Thread.Sleep(5);
            arrived = bot.StationsUpdates.Any(u => HasEntry(u, station.StationId));
        }

        Assert.True(arrived,
            $"STATIONS_UPDATE for docked station did not arrive within {maxTicks} ticks ({ticks * period:F3}s sim time)");

        var update = bot.StationsUpdates.First(u => HasEntry(u, station.StationId));
        var entry = EntryOf(update, station.StationId);
        Assert.Equal(Percent(station.ArmorFraction), entry.BpHullFraction);
        Assert.Equal(Percent(station.ShieldFraction), entry.BpShieldFraction);

        // Keep arriving while docked: run three more cadence windows and expect
        // additional batches.
        int updatesBefore = bot.StationsUpdates.Count;
        int keepTicks = 0;
        int keepMax = 3 * Sector.c_nPassesPerUpdate;
        while (keepTicks < keepMax)
        {
            server.TickSim();
            Thread.Sleep(1);
            keepTicks++;
        }
        Assert.True(bot.StationsUpdates.Count > updatesBefore,
            "STATIONS_UPDATE must keep arriving while the player remains docked");

        double cadenceSeconds = Sector.c_nPassesPerUpdate * period;
        Console.WriteLine($"P768-SEEN: docked station {station.StationId} update delivered in {ticks} ticks " +
                          $"({ticks * period:F3}s); cadence = {cadenceSeconds:F3}s @ {server.Config.UpdatesPerSecond}Hz");
    }

    /// <summary>
    /// ⭐ Case 2 — a side's own station is visible to itself through P132's
    /// own-side rule even when it carries no scanner and there are no other
    /// friendly units nearby. The docked player still receives the station's
    /// health on the same cadence. (This is the "could be unseen" case the row
    /// flags; after reading ResolveCrossSideVisibility it turns out the own-side
    /// flag is always set to true, so the per-side batch already covers it.)
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_player_receives_own_station_update_when_own_station_has_no_scanner()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var (ship, sector, station) = SeatDockedAtSyntheticStation(core, mission, shipId, 9602, zeroScanner: true);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p768_unseen");
        bot.SendLogonRequest(Request("p768_unseen"));

        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);
        var live = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        var station = server.Missions[0].Sectors[0].StationRuntimes.Single(s => s.StationId == 9602);

        Assert.Equal((short)9602, live.StationId!.Value);
        Assert.Equal(0f, station.Type.ScannerRange);
        PumpUntil(server, () => station.SeenBySide(live.SideId!.Value), 2000);
        Assert.True(station.SeenBySide(live.SideId!.Value),
            "P132 own-side rule must keep the station visible even with zero scanner range");

        PumpUntil(server, () => bot.StationsUpdates.Count > 0, 2000);
        bot.ResetStationsUpdates();

        typeof(Station).GetProperty(nameof(Station.ArmorFraction))!.SetValue(station, 0.65f);
        typeof(Station).GetProperty(nameof(Station.ShieldFraction))!.SetValue(station, 0.50f);

        double period = 1.0 / server.Config.UpdatesPerSecond;
        int maxTicks = (int)Math.Ceiling(0.75 * server.Config.UpdatesPerSecond);
        int ticks = 0;
        bool arrived = false;
        while (!arrived && ticks < maxTicks)
        {
            server.TickSim();
            Thread.Sleep(1);
            ticks++;
            arrived = bot.StationsUpdates.Any(u => HasEntry(u, station.StationId));
        }
        // The broadcast publisher (snapshot default, f4d9263d) sends on its own
        // thread: an update the server emitted on the last allowed tick may still be
        // in flight. Wait briefly in wall time WITHOUT ticking — the cadence bound
        // (sent within maxTicks) is unchanged.
        var drain = System.Diagnostics.Stopwatch.StartNew();
        while (!arrived && drain.ElapsedMilliseconds < 500)
        {
            Thread.Sleep(5);
            arrived = bot.StationsUpdates.Any(u => HasEntry(u, station.StationId));
        }

        Assert.True(arrived,
            $"STATIONS_UPDATE for zero-scanner own station did not arrive within {maxTicks} ticks ({ticks * period:F3}s sim time)");

        var update = bot.StationsUpdates.First(u => HasEntry(u, station.StationId));
        var entry = EntryOf(update, station.StationId);
        Assert.Equal(Percent(station.ArmorFraction), entry.BpHullFraction);
        Assert.Equal(Percent(station.ShieldFraction), entry.BpShieldFraction);

        Console.WriteLine($"P768-NO-SCANNER: zero-scanner own station {station.StationId} still delivered in {ticks} ticks " +
                          $"({ticks * period:F3}s) thanks to P132 own-side visibility");
    }
}
