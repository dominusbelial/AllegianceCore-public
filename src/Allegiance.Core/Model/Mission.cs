namespace Allegiance.Core.Model;

using Allegiance.Core.Sim;

/// <summary>
/// A <b>live, static mission object graph</b> assembled from a
/// <see cref="StaticCore"/> and a <see cref="MissionMap"/>: one <see cref="Sector"/>
/// per <c>OT_cluster</c>, each with its warps (destinations resolved across
/// sectors), stations and asteroids, plus the caller-supplied <see cref="Side"/>s.
/// </summary>
/// <remarks>
/// This is <b>pure data assembly</b> — the portable half of the reference
/// mission build (see <c>MISSION_INIT.md</c> §5). The runtime half — the
/// <see cref="IIgcSite"/> seam (<see cref="Site"/>) and the tick — is bound
/// here but not driven: there is no tick, no network, no mutable
/// ship/physics state. Side skeletons
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

    /// <summary>
    /// The host seam this mission is bound to. A mission cannot tick without
    /// one (<c>missionigc.cpp:3058</c>; cf. the <c>nullptr</c> guard at
    /// <c>:3077</c>), so it is mandatory at assembly and immutable thereafter.
    /// </summary>
    public IIgcSite Site { get; }

    /// <summary>
    /// The mission's last-updated time. Starts at 0 and is advanced by
    /// <see cref="Update"/>; the C++ field is <c>m_lastUpdate</c>
    /// (<c>missionigc.cpp:3127</c>). Before any tick it is 0, matching the
    /// C++ constructor (<c>missionigc.h:397</c> keeps the mission
    /// <c>STAGE_NOTSTARTED</c>). Read-only to callers; written only by
    /// <see cref="Update"/>.
    /// </summary>
    public double LastUpdate { get; private set; }

    /// <summary>
    /// The mission stage, controlling the tick's behaviour: only
    /// <see cref="MissionStage.Started"/> / <see cref="MissionStage.Starting"/>
    /// run the real (chunked) pass; any other stage makes
    /// <see cref="Update"/> just advance <see cref="LastUpdate"/> to <c>now</c>.
    /// Initial value <see cref="MissionStage.NotStarted"/>, matching the C++
    /// constructor (<c>missionigc.h:397</c>).
    /// </summary>
    public MissionStage Stage { get; set; } = MissionStage.NotStarted;

    /// <summary>
    /// The chunk-size upper bound, faithful to <c>c_fTimeIncrement = 0.25f</c>
    /// (<c>src/Igc/igc.h:5120</c>). Each mission tick splits the elapsed time
    /// into chunks of <em>at most</em> this duration (see <see cref="Update"/>).
    /// </summary>
    public const double TimeIncrement = 0.25;

    private Mission(
        StaticCore core,
        MissionMap map,
        IReadOnlyList<Sector> sectors,
        IReadOnlyList<Side> sides,
        IReadOnlyList<ResolvedWarp> allWarps,
        IReadOnlyList<ResolvedWarp> danglingWarps,
        IIgcSite site)
    {
        Core = core;
        Map = map;
        Sectors = sectors;
        Sides = sides;
        Warps = allWarps;
        DanglingWarps = danglingWarps;
        Site = site;
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
        IReadOnlyList<SideSpec> sides,
        IIgcSite site)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(sides);
        ArgumentNullException.ThrowIfNull(site);

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
        return new Mission(core, map, sectors, sideList, allWarps, dangling, site);
    }

    /// <summary>
    /// Advance the mission to <paramref name="now"/>, replicating
    /// <c>CmissionIGC::Update</c> (<c>missionigc.cpp:3127-3185</c>) exactly.
    /// The cadence and ordering are the point of this task; objects come later.
    /// </summary>
    /// <remarks>
    /// Three behaviors, faithful to the C++:
    /// <list type="number">
    /// <item><b>Clock guard</b> — a backwards <c>now</c> throws (the C++
    /// <c>assert (now &gt;= m_lastUpdate)</c>); an unchanged <c>now</c> is a
    /// no-op.</item>
    /// <item><b>Chunking</b> — only <c>Started</c>/<c>Starting</c> missions tick.
    /// Elapsed <c>deltaT = now - LastUpdate</c> splits into
    /// <c>n = 1 + (int)(deltaT / <see cref="TimeIncrement"/>)</c> evenly-divided
    /// chunks (<b>not</b> fixed 250&nbsp;ms steps): each chunk is
    /// <c>startTime + deltaT·(i/n)</c>, so every chunk is <em>at most</em> the
    /// increment and the last lands exactly on <c>now</c>. The C++ guards
    /// <c>now - m_lastUpdate &lt; 600</c> (&quot;never 10 minutes at a time&quot;);
    /// ours throws rather than silently clamping (decision documented in the
    /// README).</item>
    /// <item><b>Cadence</b> — sides update <em>once per Update</em>, clusters
    /// update <em>once per chunk</em>. All side updates precede the first
    /// cluster chunk, per the C++ loop order.</item>
    /// </list>
    /// The postcondition <c>LastUpdate == now</c> holds on every path. Side
    /// updates are witnessed through the <see cref="IIgcSite.SideUpdated"/>
    /// observation hook; each cluster chunk is witnessed through
    /// <see cref="IIgcSite.ClusterUpdateEvent"/> (the C++'s only site call in
    /// the tick, <c>clusterIGC.cpp:357</c>).
    /// </remarks>
    public void Update(double now)
    {
        if (now < LastUpdate)
            throw new InvalidOperationException(
                $"mission clock moved backwards (now = {now:R} < LastUpdate = {LastUpdate:R}); " +
                "Update requires a monotonically increasing time (missionigc.cpp:3129)");

        if (now == LastUpdate) return;                 // nothing to do; postcondition holds

        if (Stage is not (MissionStage.Started or MissionStage.Starting))
        {
            LastUpdate = now;                          // not started: just advance (c++:3180-3181)
            return;
        }

        // ── once per Update: the side pass ────────────────────────────────
        foreach (var side in Sides)
        {
            side.Update(now);
            Site.SideUpdated(side, now);               // observation hook (instrumentation)
        }

        double deltaT = now - LastUpdate;
        if (deltaT >= 600.0)
            throw new InvalidOperationException(
                $"cannot advance {deltaT:R}s in one tick (> 10 min); missionigc.cpp:3148 asserts now - m_lastUpdate < 600.0f");

        // ── chunked: clusters update once per chunk ───────────────────────
        // n = 1 + floor(deltaT / increment); for any deltaT > 0, n >= 1. The
        // even split makes each chunk <= TimeIncrement, never equal everywhere.
        int n = 1 + (int)(deltaT / TimeIncrement);
        double startTime = LastUpdate;

        for (int i = 1; i <= n; i++)
        {
            double timeUpdate = startTime + deltaT * ((double)i / n);
            foreach (var sector in Sectors)
            {
                sector.Update(timeUpdate);
                Site.ClusterUpdateEvent(sector);        // faithful site call (c++:357)
            }
            LastUpdate = timeUpdate;
        }

        // postcondition on the chunked path: the last chunk equals now.
        // LastUpdate == now holds structurally (i == n ⇒ timeUpdate == startTime + deltaT == now).
    }

    /// <summary>
    /// Place a <see cref="Ship"/> in the given <see cref="Sector"/>. Routes to
    /// <see cref="Sector.Add"/>, the port of <c>CclusterIGC::AddModel</c>; the
    /// mission exists so it can be given a ship to place at construction
    /// (task's wording) and own the relationship. Throws if the sector is not
    /// one of this mission's sectors.
    /// </summary>
    public void PlaceShip(Ship ship, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(ship);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; a ship can only be placed in a sector the mission owns");
        sector.Add(ship);
    }

    /// <summary>
    /// Place a ship by sector id (shortcut for <see cref="PlaceShip(Ship, Sector)"/>).
    /// Throws if no sector has that id.
    /// </summary>
    public void PlaceShip(Ship ship, short sectorId)
    {
        var sector = Sectors.FirstOrDefault(s => s.Id == sectorId)
            ?? throw new InvalidOperationException($"no sector with id {sectorId} in this mission");
        PlaceShip(ship, sector);
    }
}