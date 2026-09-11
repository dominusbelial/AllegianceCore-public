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

    /// <summary>The flat seconds between paydays, ported from
    /// <c>c_dtBetweenPaydays = 60.0f</c> (<c>missionigc.cpp:2691</c>) and the
    /// mission constant <c>c_fcidSecondsBetweenPaydays</c> it is stored under
    /// (<c>missionigc.cpp:2746</c>). The reference mission <em>overrides</em>
    /// whatever the core's <c>DataConstantsIGC.FloatConstants[6]</c> holds with
    /// this fixed value at init (<c>missionigc.cpp:2773-2774</c>), so — like
    /// <see cref="ServerHost.ExitWarpSpeedBonus"/> and per this repo's
    /// honesty-about-simplifications convention — we use the fixed value and
    /// say so, rather than reading the (overridden) core slot.</summary>
    public const double SecondsBetweenPaydays = 60.0;

    /// <summary>The per-payday income before civilization adjustment, ported
    /// from <c>c_fIncome = 600.0f</c> (<c>missionigc.cpp:2722</c>), also a
    /// mission-set override of the core constant <c>c_fcidIncome=30</c>
    /// (<c>missionigc.cpp:2773</c>; <c>igc.h:95</c>). This is the value
    /// <c>DoTick</c> multiplies by <c>1.0 + IncomeMoney</c>
    /// (<c>fsmission.cpp:2536</c>). Hardcoded for the same reason as
    /// <see cref="SecondsBetweenPaydays"/>.</summary>
    public const float IncomePerPayday = 600.0f;

    // Object-id space for models created by the mission at runtime (projectiles
    // today; ships/stations etc. later) — the analogue of the C++ CreateObject
    // id assignment. Starts well clear of the small ids the flight/bot tests
    // use for ships, so a projectile can never alias a ship in the model list.
    private int _nextObjectId = 71000;

    /// <summary>
    /// The sim time of the <em>next</em> payday fire — the port of the FedSrv
    /// <c>m_timeNextPayday</c> (<c>fsmission.cpp:2253</c>: seeded to
    /// <c>mission-start time + 60</c>; <c>fsmission.cpp:2519</c>: re-armed by
    /// adding the interval, keeping phase). 0 means "not armed yet" — it is
    /// seeded lazily on the first <c>Started</c>/<c>Starting</c> tick, because
    /// this port has no distinct "mission start" moment (Stage flips to
    /// Started externally, after <see cref="Assemble"/>); tying the first
    /// payday to the first Started tick is the faithful stand-in for
    /// mission-start. Subsequent fires preserve the original phase
    /// (<c>+= SecondsBetweenPaydays</c>), so an irregular tick cadence cannot
    /// drift the schedule. Accessible to tests for introspection.
    /// </summary>
    internal double NextPaydayTime { get; set; }

    /// <summary>Allocate the next runtime object id (a monotonically increasing
    /// short, wrapping silently at the short boundary as the C++ id counter
    /// does). Owned by the mission so ids are unique across every model it
    /// creates, not just per weapon.</summary>
    internal short NextObjectId() => unchecked((short)_nextObjectId++);

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
            sideList.Add(new Side(spec.Id, spec.Name, civ, BucketCatalog.ForSide(core, site)));
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
            sectorsById[g.Key].AttachStations(g, core);
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
        // Give each sector its mission's side set (the port of the native
        // pcluster->GetMission()->GetSides() lookup, FedSrv.CPP:10887-10888) so
        // the P132 cross-side visibility sweep can reconcile every side — even
        // one with no presence in the sector — to its correct SeenBySide value.
        foreach (var s in sectors) s.MissionSides = sideList;
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
    /// the tick, <c>clusterIGC.cpp:357</c>). On payday ticks the side pass is
    /// additionally witnessed through <see cref="IIgcSite.PaydayEvent"/>, the
    /// P51 seam (see <see cref="NextPaydayTime"/>).
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

        // ── payday (P51): the reference DoTick payday block (fsmission.cpp:
        // 2514-2552), folded into the same per-Update pass. Seed on the first
        // Started tick, then fire when now crosses the armed time, accruing
        // each side's income and reporting it through the site seam. The
        // station-income term of the reference formula (fsmission.cpp:2538-2546)
        // is omitted — always zero here because the server has no stations.
        // Distribution to players (delta split + FM_S_PAYDAY send + deduction)
        // lives in the host's PaydayEvent handler, mirroring the split between
        // fsmission.cpp:2514-2552 (accrual) and fsmission.cpp:2294-2326 (payout).
        if (NextPaydayTime == 0)
            NextPaydayTime = now + SecondsBetweenPaydays;     // fsmission.cpp:2253 (seeded at start + 60)
        if (now >= NextPaydayTime)
        {
            foreach (var side in Sides)
            {
                long income = (long)(IncomePerPayday * (1.0f + side.Civilization.IncomeMoney));
                side.Money += income;                          // fsmission.cpp:2536
                Site.PaydayEvent(side, income);                // site seam → host payout
            }
            NextPaydayTime += SecondsBetweenPaydays;           // keep phase (fsmission.cpp:2519)
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

    // P163 — runtime-allocated station ids for stations created mid-simulation
    // (the building-effect transformation). The port of native
    // pmission->GenerateNewStationID() (FedSrv.CPP:11397), sharing the same
    // monotonic-counter shape as the server's ship-id allocator (_nextShipId,
    // Server.cs:59). Started
    // comfortably above the low static station ids the map's OT_station records
    // carry so a fresh build can never shadow an assembled station's id.
    private int _nextStationId = 9187;

    /// <summary>Allocate a unique runtime station id — the port of native
    /// <c>pmission-&gt;GenerateNewStationID()</c> (<c>FedSrv.CPP:11397</c>),
    /// the allocator the native <c>BuildStation</c> uses for the station that
    /// replaces a built asteroid (<c>FedSrv.CPP:11397</c>:
    /// <c>ds.stationID = pmission-&gt;GenerateNewStationID()</c>). Mirrors
    /// the server's ship-id allocator <c>_nextShipId</c> (Server.cs:59) as a
    /// simple monotonically-increasing counter. Called by the building-effect
    /// resolver (<see cref="Sector.ResolveBuildingEffects"/>) when a build
    /// completes. A station id is only compared against other station ids in
    /// this port (RemoveStation, Sector.cs:202), so its distinctness from the
    /// shared ship-id space is not required — it just keeps the spaces tidy. A
    /// plain-text mirror of the server's <c>_nextShipId</c> (Server.cs:59).</summary>
    internal short GenerateNewStationID() => (short)(_nextStationId++);

    /// <summary>
    /// Place a <see cref="Ship"/> in the given <see cref="Sector"/>. Routes to
    /// <see cref="Sector.Add"/>, the port of <c>CclusterIGC::AddModel</c>; the
    /// mission exists so it can be given a ship to place at construction
    /// (task's wording) and own the relationship. Seeds the ship's model clock
    /// to the current placement sim-time (<see cref="LastUpdate"/>) via
    /// <see cref="Ship.InitClock"/> so a ship placed mid-simulation does not take
    /// one oversized first <c>dt</c> equal to the mission age (BOT.md latent
    /// bug; the C++ initialises <c>m_lastUpdate</c> at creation). Throws if the
    /// sector is not one of this mission's sectors.
    /// </summary>
    public void PlaceShip(Ship ship, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(ship);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; a ship can only be placed in a sector the mission owns");
        ship.InitClock(LastUpdate);     // seed to placement sim-time (BOT.md ship-clock bug)
        ship.Mission = this;            // the ship's back-ref to its mission (combat needs it)
        ship.Sector = sector;           // the ship's back-ref to its sector (weapon places projectiles there)
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

    /// <summary>
    /// Register a <b>runtime</b> <see cref="Asteroid"/> (a wrapped
    /// <see cref="DataAsteroidIGC"/> carrying its depletable ore pool) with the
    /// given <see cref="Sector"/>, so the mining pass
    /// (<see cref="Sector.ResolveMining"/>) can drain it. This is the synthetic
    /// injection entry the loopback mining tests use — the demo map
    /// (<c>XXV_simple.igc</c>) has <em>zero</em> asteroids (the boot summary's
    /// own <see cref="Sector.Asteroids"/> sum is genuinely 0), so a mining test
    /// constructs its own rock directly here rather than depending on the real
    /// map having one, matching how the ship tests synthesize their placement.
    /// The mineral is implicit: an asteroid wraps its static record and the
    /// model's only runtime state is the ore pool (<see cref="Asteroid.OreRemaining"/>),
    /// position coming from the record. Throws if the sector is not this
    /// mission's (same guard as <see cref="PlaceShip(Ship, Sector)"/>).
    /// </summary>
    public void PlaceAsteroid(Asteroid asteroid, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(asteroid);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; an asteroid can only be placed in a sector the mission owns");
        sector.AttachAsteroid(asteroid);
    }

    /// <summary>
    /// Register a <b>runtime</b> <see cref="Station"/> (a wrapped
    /// <see cref="DataStationIGC"/> + its resolved <see cref="DataStationTypeIGC"/>
    /// carrying the live armor/shield fractions) with the given
    /// <see cref="Sector"/>, so the combat pass (<see cref="Sector.ResolveCombatCollisions"/>)
    /// can hit it and the tick can regenerate it. This is the synthetic
    /// injection entry the loopback station-combat tests use, mirroring
    /// <see cref="PlaceAsteroid"/>: the demo map's real stations are the
    /// production path (assembled in <see cref="Assemble"/>), and a test that
    /// needs full control over <see cref="Station.MaxArmor"/> /
    /// <see cref="Station.MaxShield"/> (for exact fraction assertions) builds its
    /// own. Throws if the sector is not this mission's (same guard as
    /// <see cref="PlaceShip(Ship, Sector)"/>).
    /// </summary>
    public void PlaceStation(Station station, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; a station can only be placed in a sector the mission owns");
        sector.AttachStation(station);
    }
}