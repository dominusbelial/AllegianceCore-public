using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P379 — the debug-mode test topology, proven live through a real in-process
/// Allegiance.Server on a real loopback socket running the real
/// <c>m2xspiderw.igc</c> map (the exact two-garrison map this session plays),
/// with real <see cref="FltBot"/> peers connecting over real TCP.
///
/// Two complementary proofs:
/// <list type="number">
/// <item><b>Debug ON</b> (<c>liveSettings.debugMode=true</c>): the whole
///      topology activates — side 0's home Omega(1053) and side 1's home
///      Plato(1060) each get one real garrison-type (StationTypeId=15)
///      side-owned station (ids 90001/90002, clear of the real 4328/4329 and
///      of Mission.GenerateNewStationID()'s 9187+ internal allocator), each
///      base's warp network is trimmed to a single Alderan choke point
///      (Omega keeps 2153, Plato keeps 2184; 2154/2156/2179/2190 removed).
///      Alderan(1052) is sealed too (a live-playtest follow-up to the
///      original pass) — its four other real warps (2150/2183/2187/2189)
///      are removed, leaving only its own 2152 (→Omega) and 2185 (→Plato),
///      so the whole 3-sector arena is fully isolated, not just the home
///      bases. Connecting peers spawn at their own home base with the fixed
///      hull mix (each side's first peer → Scout, second → Fighter).</item>
/// <item><b>Debug OFF</b> (regression): with no <c>liveSettings</c> the
///      production behavior is byte-for-byte unchanged — Omega/Plato remain
///      genuinely station-less with their full real warp networks, Alderan
///      keeps all six warps, and peers spawn at their real home sectors.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class LiveP379DebugTopologyTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // The synthetic debug-garrison station ids (must match the server impl's
    // 90000s constants; 90001 wraps past short.MaxValue to a negative short,
    // which is fine — station ids are only ever compared against each other).
    private static readonly short OmegaDebugStationId = unchecked((short)90001);
    private static readonly short PlatoDebugStationId = unchecked((short)90002);

    private static Cfg Config(bool debugMode) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = debugMode ? new Ls { DebugMode = true } : new Ls(),
        Games =
        [
            new Gm { Name = "P379 Debug Topology", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 949u, DwCookie = 0x949u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9790000000000000000ul, DrmHash = "p379",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Sector Sec(Srv server, short id) =>
        server.Missions[0].Sectors.First(s => s.Id == id);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_on_injects_stations_trims_warps_and_spawns_each_side_at_its_base()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: true));
        var mission = server.Missions[0];

        var omega = Sec(server, 1053);
        var plato = Sec(server, 1060);
        var alderan = Sec(server, 1052);

        // 1) Warp isolation: exactly ONE warp per home base, the Alderan choke.
        var omegaWarp = Assert.Single(omega.Warps);
        Assert.Equal((short)2153, omegaWarp.Warp.WarpId);            // Omega -> Alderan kept
        var platoWarp = Assert.Single(plato.Warps);
        Assert.Equal((short)2184, platoWarp.Warp.WarpId);            // Plato -> Alderan kept

        // 2) Alderan sealed too (live-playtest follow-up to the original P379
        //    pass): only its two warps back to Omega/Plato remain, no synthetic
        //    station. The four warps to the rest of the map (Cirrus/Juniper/
        //    Rigel/Gaia) are gone, so the 3-sector arena is fully isolated.
        Assert.Equal(2, alderan.Warps.Count);
        Assert.Contains(alderan.Warps, w => w.Warp.WarpId == 2152);   // Alderan -> Omega kept
        Assert.Contains(alderan.Warps, w => w.Warp.WarpId == 2185);   // Alderan -> Plato kept
        Assert.DoesNotContain(alderan.Warps, w => w.Warp.WarpId is 2150 or 2183 or 2187 or 2189);

        // 3) Each base gained one side-owned garrison-type station.
        var omegaSt = omega.Stations.Single(st => st.StationId == OmegaDebugStationId);
        var platoSt = plato.Stations.Single(st => st.StationId == PlatoDebugStationId);
        Assert.Equal(0, omegaSt.SideId);
        Assert.Equal(1, platoSt.SideId);
        Assert.Equal((short)15, omegaSt.StationTypeId);             // real garrison type
        Assert.Equal((short)15, platoSt.StationTypeId);
        Assert.Contains(omega.StationRuntimes, st => st.StationId == OmegaDebugStationId && st.SideId == 0);
        Assert.Contains(plato.StationRuntimes, st => st.StationId == PlatoDebugStationId && st.SideId == 1);

        // 4) Four real peers, connected one at a time (deterministic spawn order):
        //    side alternates n%2, hull alternates (n/2)%2 (1st per side Scout, 2nd Fighter).
        var boats = new FltBot[4];
        try
        {
            for (int k = 0; k < 4; k++)
            {
                var bot = new FltBot(); // AutoSelectSideAfterLogon defaults true -> POSITIONREQ(-1), the debug auto path
                boats[k] = bot;
                bot.Connect(server.BoundPort, $"p379_{k}");
                bot.SendLogonRequest(Request($"p379_{k}"));
                int ok = PumpUntil(server,
                    () => bot.Ack is { FValidated: true } && bot.ShipAssignment is not null &&
                          ShipOf(server, bot.ShipAssignment.Ship.ShipId).Sector is not null,
                    4000);
                Assert.True(ok < 4000, $"peer {k} never seated");

                var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
                short side = (short)(k % 2);
                var wantSector = side == 0 ? omega : plato;
                var wantScout = (k / 2) % 2 == 0;
                Assert.Equal(side, ship.SideId!.Value);
                Assert.Equal(wantSector.Id, ship.Sector!.Id);
                bool isScout = ship.HullType.Name.Contains("Scout", StringComparison.OrdinalIgnoreCase);
                bool isFighter = ship.HullType.Name.Contains("Fighter", StringComparison.OrdinalIgnoreCase);
                Assert.True(wantScout ? isScout : isFighter,
                    $"peer {k}: side {side} expected {(wantScout ? "Scout" : "Fighter")} hull, got '{ship.HullType.Name}'");
                Console.WriteLine($"[P379-LIVE] peer {k}: ship {ship.Id} side {ship.SideId} hull '{ship.HullType.Name}' in sector {ship.Sector.Id} '{ship.Sector.Name}'");
            }
        }
        finally { foreach (var b in boats) b?.Dispose(); }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_station_is_real_and_dockable()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: true));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // dock pass + status broadcast need a started mission (P180)
        var omega = Sec(server, 1053);
        var st = omega.Stations.Single(s => s.StationId == OmegaDebugStationId);
        var stPos = new Vector3(st.Position[0], st.Position[1], st.Position[2]);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p379_dock");
        bot.SendLogonRequest(Request("p379_dock"));
        int ok = PumpUntil(server,
            () => bot.Ack is { FValidated: true } && bot.ShipAssignment is not null &&
                  ShipOf(server, bot.ShipAssignment.Ship.ShipId).Sector is not null,
            4000);
        Assert.True(ok < 4000, "dock probe peer never seated");

        var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
        Assert.Equal((short)1053, ship.Sector!.Id);       // spawned at Omega
        ship.Position = stPos;                            // bring it inside the station's dock radius
        int d = PumpUntil(server, () => ShipOf(server, ship.Id).StationId is not null, 400);
        Assert.True(d < 400, "the synthetic debug garrison never latched a dock");
        Assert.Equal(OmegaDebugStationId, ShipOf(server, ship.Id).StationId!.Value);
        Console.WriteLine($"[P379-LIVE] docked at the synthetic Omega garrison (stationId {OmegaDebugStationId}, StationTypeId 15) — dock/buy loop reachable.");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_off_leaves_the_real_topology_completely_untouched()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: false));

        var omega = Sec(server, 1053);
        var plato = Sec(server, 1060);
        var alderan = Sec(server, 1052);

        // Real home bases stay genuinely station-less.
        Assert.Empty(omega.Stations);
        Assert.Empty(plato.Stations);
        Assert.Empty(alderan.Stations);

        // Full real warp networks intact (nothing removed).
        Assert.Equal(3, omega.Warps.Count);
        Assert.Equal(3, plato.Warps.Count);
        Assert.Equal(6, alderan.Warps.Count);
        CollectionAssertUnordered(new short[] { 2153, 2154, 2156 }, omega.Warps.Select(w => w.Warp.WarpId).ToArray());
        CollectionAssertUnordered(new short[] { 2179, 2184, 2190 }, plato.Warps.Select(w => w.Warp.WarpId).ToArray());

        // A peer spawns at a REAL home sector (station-owning), not Omega/Plato.
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p379_reg");
        bot.SendLogonRequest(Request("p379_reg"));
        int ok = PumpUntil(server,
            () => bot.Ack is { FValidated: true } && bot.ShipAssignment is not null &&
                  ShipOf(server, bot.ShipAssignment.Ship.ShipId).Sector is not null,
            4000);
        Assert.True(ok < 4000, "debug-off peer never seated");

        var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
        Assert.NotNull(ship.SideId);
        var home = HomeSector(server, ship.SideId!.Value);
        Assert.NotNull(home);
        Assert.Equal(home!.Id, ship.Sector!.Id);
        Assert.NotEqual(1053, ship.Sector.Id);   // never Omega
        Assert.NotEqual(1060, ship.Sector.Id);   // never Plato
        Console.WriteLine($"[P379-LIVE] debug-OFF regression: peer spawned at real home sector {ship.Sector.Id} '{ship.Sector.Name}' (side {ship.SideId}), warps intact.");
    }

    private static Sector? HomeSector(Srv server, short sideId)
    {
        foreach (var s in server.Missions[0].Sectors)
            if (s.Stations.Any(st => st.SideId == sideId))
                return s;
        foreach (var s in server.Missions[0].Sectors)
            foreach (var st in s.StationRuntimes)
                if (st.SideId == sideId)
                    return s;
        return null;
    }

    private static void CollectionAssertUnordered(short[] expected, short[] actual)
    {
        Assert.Equal(expected.OrderBy(x => x).ToArray(), actual.OrderBy(x => x).ToArray());
    }
}
