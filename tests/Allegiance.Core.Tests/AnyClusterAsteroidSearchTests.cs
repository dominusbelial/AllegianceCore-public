using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P158 — pure-domain (no sockets) proofs of the asteroid cross-cluster search,
/// <see cref="Ship.FindNearestMinableAsteroidAnyCluster"/>: the BFS that walks
/// the sector-warp graph outward from the ship's own sector (hop 0 = own sector,
/// then neighbours, etc.) and returns the first qualifying sector's nearest
/// mineable rock. Fully-controlled synthetic graphs built from bare
/// <see cref="Sector"/>s with warps/asteroids injected via reflection (the same
/// mechanism NpcCrossClusterLiveTests uses for <c>_warps</c>), so every
/// traversal outcome is deterministic. The asteroid search needs no mission, so
/// bare sectors are the honest, sufficient bar here (the station sibling is
/// proven in <see cref="AnyClusterStationSearchTests"/> with a real mission).
/// </summary>
public sealed class AnyClusterAsteroidSearchTests
{
    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static readonly FieldInfo WarpsField =
        typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MineableField =
        typeof(Sector).GetField("_mineable", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Attach(Sector s, ResolvedWarp w) =>
        ((List<ResolvedWarp>)WarpsField.GetValue(s)!).Add(w);

    private static void AddRock(Sector s, Asteroid a) =>
        ((List<Asteroid>)MineableField.GetValue(s)!).Add(a);

    private static DataClusterIGC Cluster(short id, string name) => new(
        StarSeed: 1, LightDirection: [1f, 0f, 0f], LightColor: 1, ScreenX: 0, ScreenY: 0,
        ClusterId: id, Debris: 0, Stars: 0, Name: name, PosterName: "", PlanetName: "",
        PlanetSinLatitude: 0, PlanetLongitude: 0, PlanetRadius: 0, Active: true, HomeSector: false);

    private static DataWarpIGC Warp(short id, short clusterId, Vector3 pos) => new(
        WarpId: id, DestinationId: (short)(id + 1000), Radius: 50, TextureName: "", IconName: "",
        Name: $"W{id}", Position: [pos.X, pos.Y, pos.Z], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Signature: 1f, ClusterId: clusterId);

    private static Sector Sec(short id, string name) => new(Cluster(id, name));

    // He3-capable rock (bit CapMineHe3, igc.h:761) — same synthetic builder
    // MiningTests/MiningSellTests/P156 use.
    private static DataAsteroidIGC Rock(short id, short clusterId, Vector3 pos) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 500f, OreMax: 500f,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: 60,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: clusterId, Name: $"ROID_{id}", Fraction: 0);

    private static Ship NewShip(Vector3 pos) => new(
        9001, Core().HullTypes.Values.OrderBy(h => h.HullId).First(),
        pos, Vector3.Zero, Orientation.Identity);

    /// <summary>⭐ Hop 0 is the ship's own sector — a same-sector rock always
    /// wins over any cross-sector one, even when the cross-sector candidate
    /// measures "closer" by raw coordinate distance (which is meaningless across
    /// sector frames anyway; BFS order is the honest bar).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Own_sector_candidate_wins_over_a_cross_sector_one()
    {
        Require.Artwork();
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        Attach(a, new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), b));

        var localFar = new Asteroid(Rock(1, 1, new Vector3(500f, 0f, 0f)));   // hop 0, far in raw distance
        var crossNear = new Asteroid(Rock(2, 2, new Vector3(0f, 0f, 0f)));   // hop 1, "near" (foreign frame)
        AddRock(a, localFar);
        AddRock(b, crossNear);

        var ship = NewShip(Vector3.Zero);
        ship.Sector = a;

        Assert.Same(localFar, ship.FindNearestMinableAsteroidAnyCluster());
    }

    /// <summary>⭐ Empty own sector, one hop away — the search widens across the
    /// warp and returns the rock in the neighbouring sector.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_sector_rock_found_when_own_sector_is_empty()
    {
        Require.Artwork();
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        Attach(a, new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), b));

        var rock = new Asteroid(Rock(1, 2, new Vector3(50f, 0f, 0f)));
        AddRock(b, rock);

        var ship = NewShip(Vector3.Zero);
        ship.Sector = a;

        Assert.Same(rock, ship.FindNearestMinableAsteroidAnyCluster());
    }

    /// <summary>⭐ "Nearest" across sectors means fewest hops — a one-hop sector's
    /// rock beats a two-hop sector's rock regardless of raw distance (BFS visit
    /// order is the established honest comparator, the same one
    /// <see cref="Model.Sector.FindPathToward"/> uses).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void One_hop_candidate_beats_a_two_hop_one()
    {
        Require.Artwork();
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        var c = Sec(3, "C");
        Attach(a, new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), b));   // A→B
        Attach(b, new ResolvedWarp(Warp(20, 2, new Vector3(0f, 100f, 0f)), c));   // B→C

        var hop1 = new Asteroid(Rock(1, 2, new Vector3(400f, 0f, 0f)));
        var hop2 = new Asteroid(Rock(2, 3, new Vector3(0f, 0f, 0f)));
        AddRock(b, hop1);
        AddRock(c, hop2);

        var ship = NewShip(Vector3.Zero);
        ship.Sector = a;

        Assert.Same(hop1, ship.FindNearestMinableAsteroidAnyCluster());
    }

    /// <summary>⭐ Nothing in the whole reachable component → <c>null</c>, cleanly
    /// (no exception, no infinite loop).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Returns_null_when_the_whole_reachable_component_has_no_rock()
    {
        Require.Artwork();
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        Attach(a, new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), b));

        var ship = NewShip(Vector3.Zero);
        ship.Sector = a;

        Assert.Null(ship.FindNearestMinableAsteroidAnyCluster());
    }

    /// <summary>⭐ A dangling warp (<c>Destination</c> <c>null</c>) is never
    /// traversed — the same rule <c>FindPathToward</c> applies (Sector.cs:370) —
    /// so an unreachable graph returns <c>null</c> rather than crashing or looping.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Does_not_traverse_a_dangling_warp()
    {
        Require.Artwork();
        var a = Sec(1, "A");
        Attach(a, new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), null));   // dangling edge

        var ship = NewShip(Vector3.Zero);
        ship.Sector = a;

        Assert.Null(ship.FindNearestMinableAsteroidAnyCluster());
    }

    /// <summary>⭐ No sector at all → <c>null</c> (trivial guard).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Returns_null_when_the_ship_has_no_sector()
    {
        Require.Artwork();
        var ship = NewShip(Vector3.Zero);
        Assert.Null(ship.FindNearestMinableAsteroidAnyCluster());
    }
}
