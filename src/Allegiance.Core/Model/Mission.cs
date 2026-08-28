namespace Allegiance.Core.Model;

/// <summary>
/// A <b>live, static mission object graph</b> assembled from a
/// <see cref="StaticCore"/> and a <see cref="MissionMap"/>: one <see cref="Sector"/>
/// per <c>OT_cluster</c>, each with its warps (destinations resolved across
/// sectors), stations and asteroids, plus the caller-supplied <see cref="Side"/>s.
/// </summary>
/// <remarks>
/// This is <b>pure data assembly</b> — the portable half of the reference
/// mission build (see <c>MISSION_INIT.md</c> §5). There is no tick, no
/// <c>IIgcSite</c>, no network, no mutable ship/physics state. Side skeletons
/// are taken from the caller (<see cref="SideSpec"/>) because the reference
/// engine derives them from <c>MissionParams</c> + host-supplied names, which
/// the two documents do not carry (<c>missionigc.cpp:3734</c>). Sector
/// contents are attributed by <c>ClusterId</c> membership; a warp's
/// destination is the sector containing its partner warp (or the sector named
/// directly), matching <c>warpIGC.h:129-141</c> and the corpus validator.
/// </remarks>
public sealed class Mission
{
    /// <summary>The type universe the mission draws on.</summary>
    public StaticCore Core { get; }

    /// <summary>The raw map this mission is assembled from.</summary>
    public MissionMap Map { get; }

    /// <summary>Sectors, one per <c>OT_cluster</c>, in map order.</summary>
    public IReadOnlyList<Sector> Sectors { get; }

    /// <summary>The caller-supplied sides, in the order given.</summary>
    public IReadOnlyList<Side> Sides { get; }

    /// <summary>Every warp across every sector, in map order.</summary>
    public IReadOnlyList<ResolvedWarp> Warps { get; }

    /// <summary>
    /// The subset of <see cref="Warps"/> whose destination could not be
    /// resolved (a dangling edge). Reported, never thrown.
    /// </summary>
    public IReadOnlyList<ResolvedWarp> DanglingWarps { get; }

    private Mission(
        StaticCore core,
        MissionMap map,
        IReadOnlyList<Sector> sectors,
        IReadOnlyList<Side> sides,
        IReadOnlyList<ResolvedWarp> allWarps,
        IReadOnlyList<ResolvedWarp> danglingWarps)
    {
        Core = core;
        Map = map;
        Sectors = sectors;
        Sides = sides;
        Warps = allWarps;
        DanglingWarps = danglingWarps;
    }

    /// <summary>
    /// Assemble the mission object graph from a core, a map and the side
    /// specifications. Throws on a side naming a civilization absent from the
    /// core, or on a member (warp/station/asteroid) naming a sector id that has
    /// no <c>OT_cluster</c> record in the map. Warp <i>destinations</i> that
    /// cannot resolve are reported in <see cref="DanglingWarps"/>, not thrown —
    /// they never occur in the shipped corpus (measured 0 / 5212).
    /// </summary>
    public static Mission Assemble(
        StaticCore core,
        MissionMap map,
        IReadOnlyList<SideSpec> sides)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(sides);

        // 1. Resolve the sides' civilizations from the core.
        var sideList = new List<Side>(sides.Count);
        foreach (var spec in sides)
        {
            if (!core.Civilizations.TryGetValue(spec.CivilizationId, out var civ))
                throw new InvalidOperationException(
                    $"side {spec.Id} ('{spec.Name}') names civilization {spec.CivilizationId} " +
                    $"absent from core {core.Version}; a side's civilization must exist in the core");
            sideList.Add(new Side(spec.Id, spec.Name, civ));
        }

        // 2. One sector per OT_cluster, keyed by ClusterId. ClusterIds are unique
        //    across the whole shipped corpus (measured 0 duplicates in 111 maps);
        //    MissionMap tolerates a duplicate, in which case every member naming
        //    that id attaches to every sector sharing it.
        var clusterIds = new HashSet<short>(map.Clusters.Select(c => c.ClusterId));
        short RequireSector(short clusterId, string kind, string name)
        {
            if (clusterIds.Contains(clusterId)) return clusterId;
            throw new InvalidOperationException(
                $"{kind} '{name}' names cluster {clusterId} which has no OT_cluster record " +
                "in this map; a member must belong to a real sector");
        }

        var sectorsById = new Dictionary<short, Sector>(map.Clusters.Count);
        foreach (var cluster in map.Clusters)
            sectorsById.Add(cluster.ClusterId, new Sector(cluster));

        // 3. Attribute stations and asteroids by ClusterId.
        foreach (var g in map.Stations.GroupBy(s => RequireSector(s.ClusterId, "station", s.Name)))
            sectorsById[g.Key].AttachStations(g);
        foreach (var g in map.Asteroids.GroupBy(a => RequireSector(a.ClusterId, "asteroid", a.Name)))
            sectorsById[g.Key].AttachAsteroids(g);

        // 4. Attribute warps by ClusterId and resolve each warp's destination to
        //    a sector. A warp's DestinationId names its partner warp; the
        //    destination sector is that partner warp's owning cluster. The
        //    validator also accepts a DestinationId that is itself a sector id
        //    (CrossRefValidator.cs:139-143), handled here too. Unresolvable
        //    destinations are collected into DanglingWarps, not thrown.
        var warpOwner = new Dictionary<short, short>(map.Warps.Count);
        foreach (var w in map.Warps)
            warpOwner[w.WarpId] = RequireSector(w.ClusterId, "warp", w.Name);

        var allWarps = new List<ResolvedWarp>(map.Warps.Count);
        var dangling = new List<ResolvedWarp>();
        foreach (var w in map.Warps)
        {
            Sector? destination = null;
            if (warpOwner.TryGetValue(w.DestinationId, out var partnerOwner))
                destination = sectorsById[partnerOwner];
            else if (sectorsById.TryGetValue(w.DestinationId, out var directSector))
                destination = directSector;

            var resolved = new ResolvedWarp(w, destination);
            allWarps.Add(resolved);
            if (destination is null) dangling.Add(resolved);
            sectorsById[w.ClusterId].AttachWarp(resolved);
        }

        var sectors = map.Clusters.Select(c => sectorsById[c.ClusterId]).ToList();
        return new Mission(core, map, sectors, sideList, allWarps, dangling);
    }
}
