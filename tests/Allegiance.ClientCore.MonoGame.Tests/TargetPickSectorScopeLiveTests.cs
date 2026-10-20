using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Live-network collection gate (parallelization off) for tests that boot a real
/// in-process server over the real TCP transport — mirrors the same gate P370
/// uses in Allegiance.ClientCore.Tests so heavy live suites don't contend.
/// </summary>
[CollectionDefinition("LiveNetwork", DisableParallelization = true)]
public sealed class LiveNetworkCollectionDefinition;

/// <summary>
/// P371 — LIVE proof that the target-pick candidate pool is narrowed to the
/// player's OWN current sector. Two real clients connect to one real in-process
/// server (map <c>m2xspiderw.igc</c>) and are spawned in TWO DIFFERENT sectors:
/// one in Ganymede (1050: 4 warps, 1 station, 0 asteroids) and one in Alderan
/// (1052: 6 warps, 0 stations, ALL 134 asteroids — P218). Every peer still
/// receives the client's full, map-wide, unconditionally-exported warp/station/
/// asteroid sets at join (44 warps / 2 stations / 134 asteroids in BOTH worlds),
/// which is exactly the pool the bug used to offer to everyone. We then invoke
/// the REAL production gather (<see cref="Game1.GatherSectorCandidates"/>) for
/// each peer's own sector and assert the per-kind candidate counts match the
/// REAL per-sector map data (verified independently via the <c>missiondump</c>
/// tool against PCore001.igc+m2xspiderw.igc), i.e. no cross-sector warps,
/// stations or asteroids ever appear.
/// </summary>
[Collection("LiveNetwork")]
public sealed class TargetPickSectorScopeLiveTests
{
    private const short Ganymede = 1050;   // 4 warps, 1 station, 0 asteroids
    private const short Alderan = 1052;    // 6 warps, 0 stations, 134 asteroids

    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = name, Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 971u, DwCookie = 0x971u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97500000000000001ul, DrmHash = "p371",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }

    /// <summary>⭐ Two real clients in DIFFERENT sectors, sharing the SAME
    /// map-wide export pool, each offered exactly ITS OWN sector's warps/stations/
    /// asteroids — never the other's, never the whole map. Assertions are
    /// ORDER-INDEPENDENT (which peer lands in which of the two spawn sectors is
    /// determined by server seating, not connect order), but they pin the
    /// decisive invariants: every candidate belongs to that peer's own sector,
    /// the per-kind counts equal the same-sector subset of ITS map-wide pool, the
    /// two peers are in different sectors, and the ~134-map-wide asteroid dump
    /// reaches exactly one peer (the one in Alderan) — not both.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TwoClientsInDifferentSectors_EachSeesOnlyItsOwnSectorCandidates()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            short sectorId = placed == 0 ? Ganymede : Alderan;
            var sector = mission.Sectors.First(s => s.Id == sectorId);
            var ship = new Ship(id, hull, SimVec.Zero, SimVec.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        });

        server.Boot(Config("P371 Two-Sector Target-Pick"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var alpha = Connect(server, "AlphaGanymede");
        using var bravo = Connect(server, "BravoAlderan");

        // Both peers seated.
        int seated = PumpUntil(server,
            () => alpha.World.MyShipId is { } && bravo.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");

        // Both peers hold the FULL map-wide export pool at join (the "before"
        // buggy pool) — 44 warps / 2 stations / 134 asteroids in EACH world.
        int exported = PumpUntil(server,
            () => alpha.World.Warps.Count >= 44 && alpha.World.Stations.Count >= 2 && alpha.World.Asteroids.Count >= 134
               && bravo.World.Warps.Count >= 44 && bravo.World.Stations.Count >= 2 && bravo.World.Asteroids.Count >= 134,
            6000);
        Assert.True(exported < 6000, "map-wide exports reached both clients");

        // Snapshot both worlds under their gates.
        short aMe, bMe, aSector, bSector;
        IReadOnlyList<Game1.CandidateObject> aGot, bGot;
        lock (alpha.Gate)
        {
            aMe = alpha.World.MyShipId!.Value;
            aSector = alpha.World.Ships[aMe].SectorId;
            aGot = Game1.GatherSectorCandidates(alpha.World.Ships, alpha.World.Warps,
                                                alpha.World.Stations, alpha.World.Asteroids, aMe, aSector);
        }
        lock (bravo.Gate)
        {
            bMe = bravo.World.MyShipId!.Value;
            bSector = bravo.World.Ships[bMe].SectorId;
            bGot = Game1.GatherSectorCandidates(bravo.World.Ships, bravo.World.Warps,
                                                bravo.World.Stations, bravo.World.Asteroids, bMe, bSector);
        }

        // The two peers are in DIFFERENT sectors (one spawns in Ganymede 1050,
        // the other in Alderan 1052 — seating order, not connect order, decides who).
        Assert.NotEqual(aSector, bSector);
        Assert.Contains(aSector, new short[] { Ganymede, Alderan });
        Assert.Contains(bSector, new short[] { Ganymede, Alderan });

        // ⭐ No candidate leaks across sectors, and per-kind candidate counts EQUAL
        // the same-sector subset of that peer's own map-wide pool.
        foreach (var (got, world, sector) in new[]
                 {
                     (aGot, (ClientWorld)alpha.World, aSector),
                     (bGot, (ClientWorld)bravo.World, bSector),
                 })
        {
            foreach (var c in got)
            {
                if (c.Kind == Game1.TargetKind.Warp) Assert.Equal(sector, world.Warps[c.Id].ClusterId);
                else if (c.Kind == Game1.TargetKind.Station) Assert.Equal(sector, world.Stations[c.Id].ClusterId);
                else if (c.Kind == Game1.TargetKind.Asteroid) Assert.Equal(sector, world.Asteroids[c.Id].ClusterId);
            }
            Assert.Equal(world.Warps.Count(w => w.Value.ClusterId == sector),
                         got.Count(c => c.Kind == Game1.TargetKind.Warp));
            Assert.Equal(world.Stations.Count(s => s.Value.ClusterId == sector),
                         got.Count(c => c.Kind == Game1.TargetKind.Station));
            Assert.Equal(world.Asteroids.Count(a => a.Value.ClusterId == sector),
                         got.Count(c => c.Kind == Game1.TargetKind.Asteroid));
        }

        // ⭐ The decisive anti-map-dump invariant: the ~134-map-wide asteroid dump
        // reaches exactly ONE peer (the one in Alderan, 1052) and ZERO reach the
        // other (Ganymede, 1050) — the bug that motivated P371 offered all 134 to
        // every sector. Likewise the 44 map-wide warps resolve to the two sectors'
        // real 4+6=10, and the single Ganymede garrison station to exactly one.
        Assert.Equal(134, aGot.Count(c => c.Kind == Game1.TargetKind.Asteroid)
                        + bGot.Count(c => c.Kind == Game1.TargetKind.Asteroid));
        Assert.True(aGot.Count(c => c.Kind == Game1.TargetKind.Asteroid) == 0
                 || bGot.Count(c => c.Kind == Game1.TargetKind.Asteroid) == 0);
        Assert.Equal(10, aGot.Count(c => c.Kind == Game1.TargetKind.Warp)
                       + bGot.Count(c => c.Kind == Game1.TargetKind.Warp));
        Assert.Equal(1, aGot.Count(c => c.Kind == Game1.TargetKind.Station)
                      + bGot.Count(c => c.Kind == Game1.TargetKind.Station));
    }
}
