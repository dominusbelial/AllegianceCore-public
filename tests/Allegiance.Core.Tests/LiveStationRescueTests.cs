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
/// P301 — the LIVE end-to-end proof of the station-side pod rescue
/// (<c>Sector.ResolveStationRescueEvents</c> → <c>Server.PerformStationRescue</c>,
/// the port of native <c>shipIGC.cpp:862-867</c> → <c>RescueShipEvent(pod, NULL)</c>,
/// <c>FedSrv.CPP:13033</c>): a drifting real player lifepod that reaches a
/// same-side <c>c_sabmRescue</c> station is docked there (which cancels the
/// P279 oxygen timer) with <b>no</b> <c>PLAYER_RESCUED</c>(16) wire — native's
/// NULL-rescuer branch skips it. A non-rescue station refuses the same pod.
///
/// A synthetic station (real garrison type #15, Radius 423.5, capability mask
/// overridden) is injected into the no-station fly sector 1052; the pod parks
/// 300u out (inside the 425.5 rescue threshold, outside the 200 DockRadius) so
/// the outcome is attributable to the rescue gate alone, not the ordinary dock.
/// </summary>
[Collection("LiveNetwork")]
public class LiveStationRescueTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short FlySector = 1052;   // no real station in XXV_simple

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Station Rescue Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 661u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7788000000000000000ul, DrmHash = "stat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    /// <summary>Boot a one-bot server with a synthetic station (given
    /// <paramref name="stationSabm"/> on <paramref name="stationSide"/>) at the
    /// origin of the fly sector, and a real player lifepod on
    /// <paramref name="podSide"/> parked 1500u away. Returns the live pod +
    /// station so the test asserts the dock / no-wire outcome.</summary>
    private static (Srv server, FltBot bot, Ship pod, Station station) Setup(
        short stationSabm, short stationSide, short podSide)
    {
        var server = new Srv();
        var pod = null as Ship;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var hull = core.HullTypes[core.Civilizations[1].Lifepod];   // real dreghpod (length 4, HabmLifepod)
            pod = new Ship(shipId, hull, new Vector3(0, 0, 300), Vector3.Zero, Orientation.Identity)
            { SideId = podSide };
            mission.PlaceShip(pod, fly);
            return (pod, fly);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        // Inject the synthetic station into the fly sector (public seam).
        var mission = server.Missions.Single();
        var baseType = mission.Core.StationTypes[15];
        var type = baseType with { SabmCapabilities = stationSabm };
        var sector = mission.Sectors.First(s => s.Id == FlySector);
        var data = new DataStationIGC(
            Position: [0f, 0f, 0f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: FlySector, SideId: stationSide, StationId: 7799,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SYN_RESCUE");
        var station = new Station(data, type);
        mission.PlaceStation(station, sector);

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "SRESC");
        bot.SendLogonRequest(Request("SRESC"));
        // P647 — wait only until the SERVER has spawned the pod (pod != null),
        // NOT for the client ShipAssignment ack. The pod is a lifepod that
        // homes toward the rescue station; under full-suite load the client ack
        // can lag for many ticks while the server-side pod flies to the (300u
        // away) station and docks — so a pump conditioned on ShipAssignment
        // would return an already-docked pod and the "starts flying" assertion
        // below would spuriously fail. Exiting the moment the pod exists keeps
        // the pod ~300u away and un-docked for the precondition assert. The
        // individual tests drive the docking themselves.
        PumpUntil(server, () => pod is not null, 4000);

        Assert.NotNull(pod);
        Assert.Null(pod!.StationId);            // starts flying, not docked
        Assert.Equal(FlySector, pod.Sector!.Id);
        return (server, bot, pod, station);
    }

    /// <summary>⭐ Positive — a real player lifepod reaches a same-side
    /// c_sabmRescue station: it is DOCKED at that station (StationId latched =
    /// the P279 oxygen-cancel) with NO PLAYER_RESCUED(16) wire (native
    /// NULL-rescuer branch, FedSrv.CPP:13037).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lifepod_reaching_rescue_station_is_docked_without_player_rescued()
    {
        Require.Artwork();
        var (server, bot, pod, station) = Setup(DataStationTypeIGC.SabmRescue, 0, 0);

        int seen = PumpUntil(server,
            () => ServerShip(server, pod.Id).StationId == station.StationId, 4000);
        Assert.True(seen < 4000, "pod was never docked at the c_sabmRescue station");

        // Latched docked AT the rescue station (the P279 oxygen-cancel latch).
        var live = ServerShip(server, pod.Id);
        Assert.Equal(station.StationId, live.StationId);
        Assert.Equal(FlySector, live.Sector!.Id);

        // Null-rescuer fidelity: NO PLAYER_RESCUED reaches the rescued pod's bot.
        Assert.Empty(bot.PlayerRescueds);

        Console.WriteLine($"STATION-RESCUE-LIVE+: lifepod {pod.Id} reached c_sabmRescue station {station.StationId} -> docked (StationId={live.StationId}), PLAYER_RESCUED count = {bot.PlayerRescueds.Count} (none)");
    }

    /// <summary>⭐ Negative — the SAME geometry but the station lacks
    /// c_sabmRescue (real garrison #15 caps): the pod is NOT docked and no
    /// rescue occurs, even parked right in front of it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lifepod_at_non_rescue_station_is_not_docked()
    {
        Require.Artwork();
        var (server, bot, pod, station) = Setup(0x0377, 0, 0);   // real garrison caps, no rescue

        for (int i = 0; i < 120; i++) server.TickSim();

        Assert.Null(ServerShip(server, pod.Id).StationId);   // refused
        Assert.Empty(bot.PlayerRescueds);

        Console.WriteLine($"STATION-RESCUE-LIVE-: lifepod {pod.Id} parked at non-rescue garrison {station.StationId} (sabm=0x377) -> NOT docked, no rescue");
    }
}
