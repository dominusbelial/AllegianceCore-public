using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (the
// same pattern WarpTransitServerTests uses).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Mg = global::Allegiance.Server.MapGenConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P397 — the live proof that a real server configured for procedural
/// generation (no map file) boots a real <c>SingleRing</c> mission and that a
/// real managed client can fly it: the generated home sectors resolve, a
/// peer's ship can transit along the generated ring warp topology
/// (<c>ServerHost.HitWarpEvent</c>) and dock at a generated home station.
/// Complements the static structural tests (<see cref="MapGenFoundationTests"/>)
/// with the boot-path + wire loopback exercised in-process, exactly like the
/// P41 warp-transit precedent.
/// </summary>
[Collection("LiveNetwork")]
public class MapGenLiveProofTests
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
            new Gm
            {
                Name = "Generated SingleRing Live",
                Map = "XXV_simple.igc",                 // present but IGNORED — MapGen takes the branch
                MapGen = new Mg { Type = "SingleRing" }, // ← the P397 generation path under test
                Teams = 2,
                CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p397gen",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ A peer joins the generated map, is spawned inside the first
    /// generated warp's collision volume, and is transited to a neighbouring
    /// generated sector — SET_CLUSTER over the wire and the server-side sector
    /// change both agree, and the destination is one of the two ring neighbours
    /// (proving transit follows the generated ring, not a file-decoded map).</summary>
    [Fact]
    public void GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour()
    {
        using var server = new Srv();

        short? spawnSectorId = null;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            spawnSectorId = sector.Id;
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();

            float shipRadius = 0.5f * hull.Length;
            float radius = warp.Warp.Radius + shipRadius;
            var spawn = wpos + dir * (0.9f * radius);
            var ship = new Ship(shipId, hull, spawn, -dir * 20f, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass ready-up lobby

        // The generated mission really is a 4-sector SingleRing.
        Assert.Equal(4, server.Missions.Single().Sectors.Count);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p397_warp");
        bot.SendLogonRequest(Request("p397_warp"));

        PumpUntil(server, () => bot.Assignments.Count >= 1 && bot.ShipStatuses.Count >= 1, 4000);
        short initial = bot.Assignments[0].SectorId;
        short shipId = bot.Assignments[0].Ship.ShipId;
        Assert.Equal(spawnSectorId!.Value, initial);          // spawned in a generated sector
        Assert.InRange(initial, (short)0, (short)3);          // ...and it's one of the 4 generated ids

        PumpUntil(server,
            () => bot.Assignments.Count >= 2 && bot.Assignments[^1].SectorId != initial,
            4000);
        Assert.True(bot.Assignments.Count >= 2, $"never crossed: sectors=[{string.Join(",", bot.Assignments.Select(a => a.SectorId))}]");
        short next = bot.Assignments[^1].SectorId;

        var live = server.Ships.First(l => l.Ship.Id == shipId);
        Assert.Equal(next, live.Sector.Id);
        Assert.Equal(next, live.Ship.Sector?.Id);

        // Ring adjacency (4-cluster SingleRing): neighbour of i is (i±1) mod 4.
        short lower = (short)((initial + 3) % 4);
        short upper = (short)((initial + 1) % 4);
        Assert.True(next == lower || next == upper,
            $"transited from generated sector {initial} to {next}, expected a ring neighbour ({lower} or {upper})");
    }

    /// <summary>⭐ A peer joins the generated map with the default spawner, which
    /// seats it in its side's generated home sector (a station-owned cluster).
    /// Driving the ship into the generated home station's dock radius docks it
    /// there — proving the generated home base is genuinely dockable.</summary>
    [Fact]
    public void GeneratedSingleRing_DefaultSpawnedPeer_DocksAtGeneratedHomeStation()
    {
        using var server = new Srv();
        // NOTE: ShipSpawner NOT overridden — exercise the real default spawner,
        // which resolves the peer's side home sector (HomeSectorForSide) and
        // seats the ship to that side.
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p397_dock");
        bot.SendLogonRequest(Request("p397_dock"));

        // Wait until the peer's ship is live, seated, and flying in its home sector.
        PumpUntil(server,
            () => server.Ships.Count > 0
               && server.Ships.First().Ship.SideId is not null
               && server.Ships.First().Ship.Mission is not null,
            4000);
        var live = server.Ships.First();
        var ship = live.Ship;
        short side = ship.SideId!.Value;

        // Locate the generated home station: the single station in this peer's
        // sector belonging to its side (generated home cluster has exactly one).
        var homeStation = live.Sector.Stations.Single(st => st.SideId == side);
        Assert.NotNull(homeStation.Position);
        var stationPos = new Vector3(homeStation.Position[0], homeStation.Position[1], homeStation.Position[2]);

        // The generated home sector is a team cluster (HomeSector true).
        Assert.True(live.Sector.Cluster.HomeSector, "peer spawned in a generated home sector");

        // Drive the ship kinematically into the dock radius, then pump until the
        // dock pass latches it. (Single-threaded pump: writing Position/Velocity
        // between TickSim calls is race-free.)
        var target = stationPos + new Vector3(60f, 0f, 0f);   // inside DockRadius (200)
        ship.Position = target;
        ship.Velocity = Vector3.Zero;
        PumpUntil(server, () => ship.StationId is not null, 400);

        Assert.NotNull(ship.StationId);
        Assert.Equal(homeStation.StationId, ship.StationId!.Value);
        Console.WriteLine($"P397-DOCK-LIVE: peer (side {side}) docked at generated home station {homeStation.StationId} '{homeStation.Name}' in sector {live.Sector.Id} '{live.Sector.Name}'");
    }
}
