namespace Allegiance.Core.Model;

using Allegiance.Core.Collision;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using CompactQuant = Allegiance.Core.Wire.CompactQuant;

/// <summary>
/// A <b>sector</b> of a static <see cref="Mission"/>: one <see cref="DataClusterIGC"/>
/// (an <c>OT_cluster</c> record) plus its <b>resolved</b> contents — the warps,
/// stations and asteroids whose <c>ClusterId</c> equals this sector's id. The
/// reference engine builds one <c>CclusterIGC</c> per <c>OT_cluster</c> and
/// aggregates its contents by <c>clusterID</c> (<c>warpIGC.cpp:29,49</c>;
/// <c>missionigc.cpp:3325,3683</c>), which is exactly this relation. Warp
/// destinations are resolved to the target <see cref="Sector"/> through
/// <see cref="ResolvedWarp.Destination"/>.
/// </summary>
/// <remarks>
/// The public surface is read-only; the lists are filled during
/// <see cref="Mission.Assemble"/> via the internal <c>Attach*</c> methods so the
/// sector graph (including a sector's own <see cref="Warps"/> whose destinations
/// point at sibling sectors) can be built without a second construction pass.
/// The <see cref="IClusterIgc"/> marker lets the tick route this sector to the
/// host's <c>ClusterUpdateEvent</c> the way the C++ <c>CclusterIGC::Update</c>
/// does (<c>clusterIGC.cpp:357</c>). <see cref="Update"/> counts the tick and
/// walks <see cref="Models"/>, invoking each model's <c>Update</c> exactly as
/// the C++ models loop does (<c>clusterIGC.cpp:241-253</c>).
/// </remarks>
public sealed class Sector : IClusterIgc
{
    /// <summary>The underlying <c>OT_cluster</c> record.</summary>
    public DataClusterIGC Cluster { get; }

    /// <summary>The sector's id (<c>ClusterId</c>).</summary>
    public short Id => Cluster.ClusterId;

    /// <summary>Fractional payday treasure accrual (FedSrv/fsmission.cpp:2563-2580).</summary>
    public double PendingTreasures { get; internal set; }

    /// <summary>The sector's name.</summary>
    public string Name => Cluster.Name;

    /// <summary>
    /// This sector's warps, each with a resolved <see cref="ResolvedWarp.Destination"/>.
    /// </summary>
    public IReadOnlyList<ResolvedWarp> Warps => _warps;

    /// <summary>This sector's stations.</summary>
    public IReadOnlyList<DataStationIGC> Stations => _stations;

    /// <summary>This sector's <b>runtime</b> stations — the live combat/damage
    /// counterparts to the static <see cref="Stations"/> records (P125). One
    /// <see cref="Station"/> per static record whose <c>StationTypeId</c>
    /// resolves in the core, the station half of the same static-vs-runtime
    /// split <see cref="Mineable"/> makes for asteroids (P102). The static
    /// <see cref="Stations"/> list is unchanged so the docking / ripcord-anchor /
    /// undock seams keep resolving stations exactly as before; this list is
    /// where the combat pass hits a station and the tick runs its regen.</summary>
    public IReadOnlyList<Station> StationRuntimes => _stationRuntimes;

    /// <summary>This sector's asteroids (static position/identity data).</summary>
    public IReadOnlyList<DataAsteroidIGC> Asteroids => _asteroids;

    /// <summary>This sector's runtime asteroids (live, depletable ore pool —
    /// see <see cref="_mineable"/>'s own doc comment below for detail).</summary>
    public IReadOnlyList<Asteroid> Mineable => _mineable;

    /// <summary>Number of times <see cref="Update"/> has been called.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>Native clusterIGC.cpp:162-198,231: cost for this chunk, including dt.</summary>
    public float Cost { get; private set; }

    /// <summary>
    /// The mission's full side set, when this sector is mission-bound — the
    /// port of the native <c>pcluster-&gt;GetMission()-&gt;GetSides()</c> iteration
    /// in <c>UpdateSideVisibility</c> (<c>FedSrv.CPP:10887-10888</c>), set by
    /// <see cref="Mission.Assemble"/> after construction so the per-tick
    /// cross-side visibility sweep (<see cref="ResolveCrossSideVisibility"/>)
    /// knows every side it must reconcile to <c>false</c> (not just the sides
    /// with presence in the sector) — the mechanism that clears a stale
    /// <c>SeenBySide</c> when a side's last scanner leaves. <c>null</c> for a
    /// sector constructed outside assembly (some isolated tests); the sweep
    /// then falls back to the sides actually present.
    /// </summary>
    internal IReadOnlyList<Side>? MissionSides { get; set; }

    internal Mission? Mission { get; set; }
    private Model? _updatingModel;
    private double _modelStateTime;
    private readonly Queue<Explosion> _pendingExplosions = new();
    private bool _resolvingExplosions;

    /// <summary>The time of the most recent <see cref="Update"/>, or 0 before the first.</summary>
    public double LastUpdate { get; private set; }

    private readonly List<ResolvedWarp> _warps = [];
    // Pending warp-bomb detonations (a scheduled absolute sim-time, resolved by
    // ResolveWarpBombDetonations). See ScheduledWarpBomb.
    private readonly List<ScheduledWarpBomb> _warpBombs = [];
    private readonly List<DataStationIGC> _stations = [];
    private readonly List<DataAsteroidIGC> _asteroids = [];

    // P125 — the sector's runtime stations, one per static DataStationIGC whose
    // StationTypeId resolves in the core (the parallel to _mineable for
    // asteroids). Lives beside _stations (not inside it) so the static list and
    // every pre-existing seam that reads it stay untouched; the combat pass
    // iterates THIS list and the tick regenerates it. See StationRuntimes.
    private readonly List<Station> _stationRuntimes = [];

    /// <summary>
    /// The sector's <b>runtime</b> asteroids — one <see cref="Asteroid"/> per
    /// static <see cref="DataAsteroidIGC"/> attached at assembly (plus any
    /// injected by <see cref="Mission.PlaceAsteroid"/> for the synthetic-map
    /// mining tests). This is what the mining pass
    /// (<see cref="ResolveMining"/>) drains and regenerates; the public
    /// <see cref="Asteroids"/> static list is unchanged so the boot summary /
    /// corpus validator / mission dump keep counting rocks they already did.
    /// Mirrors how stations keep a static <see cref="Stations"/> list while the
    /// docked <em>state</em> lives on the ship — here the state lives on the
    /// asteroid because it is intrinsically the depletable object.
    /// </summary>
    private readonly List<Asteroid> _mineable = [];

    // Models living in this sector and ticking with it. Ships today; stations,
    // asteroids, treasures etc. join later. Mirrors CclusterIGC::m_models.
    private readonly List<Model> _models = [];

    // P706 — diagnostics: the number of ship↔ship collision events this sector's
    // most recent ResolveShipShipCollisions pass dispatched (retained — i.e. that
    // reached ResolveShipShipCollision). Measurement/testing seam only; not part
    // of gameplay state.
    internal int LastShipShipEvents { get; private set; }
    // ...and how many of those ran the full Separate+resolution versus were
    // rejected by the post-Separate approach debounce (which wastes the Separate).
    internal int LastShipShipResolved { get; private set; }
    internal int LastShipShipDebounced { get; private set; }

    // P324 (M3) — ship-update-candidate cache bookkeeping. GetShipUpdateCandidates
    // is called from many hot sites (BroadcastShipsUpdate per seated peer, plus the
    // NPC arbitration searches), each of which used to allocate a fresh list and
    // re-scan _models every call. P324 caches the candidate list once per sector
    // per tick, shared by all of the tick's callers (computed lazily on first read,
    // reused until the tick advances or the sector's model membership changes).
    // _memberVersion is bumped by Add/Remove so a mid-tick membership change (a
    // ship dying, warping out, or docking — all of which Remove from _models)
    // invalidates the in-tick cache, so the host's post-tick broadcasts still see
    // the true post-tick membership (dead/departed ships dropped).
    private int _memberVersion;
    private List<Ship>? _shipUpdateCandidates;
    private long _shipCandidatesTick = -1;        // sector UpdateCount the cache was built for
    private int _shipCandidatesMemberVersion = -1; // _memberVersion at build

    /// <summary>
    /// P324 (M2) — how often the cross-side visibility sweep
    /// (<see cref="ResolveCrossSideVisibility"/>) runs relative to the sector
    /// tick. Port of the native throttle constant <c>c_nPassesPerUpdate = 10</c>
    /// (<c>clusterIGC.h:22</c>): native calls each model's <c>UpdateSeenBySide</c>
    /// (and the per-cluster site callback) only on every 10th pass
    /// (<c>clusterIGC.cpp:348</c>), and the port chose to run the sweep every
    /// tick instead. P324 restores the faithful every-10 cadence so a target
    /// becomes visible/invisible on the same real-world timescale as native.
    /// </summary>
    public const int c_nPassesPerUpdate = 10;
    private int _visPass;

    /// <summary>
    /// P762b — native <c>CclusterIGC</c>'s <c>activeF</c> gate
    /// (<c>Igc/clusterIGC.cpp:255</c>, <c>clusterIGC.h:379-382</c>). Native sets
    /// every real mission cluster active during generation
    /// (<c>missionigc.cpp:4053</c>); only the wire <c>Export</c> deliberately
    /// exports <c>activeF = false</c> so clients treat exported clusters as
    /// inactive (<c>clusterIGC.cpp:492-496</c>). A server-side sector is
    /// therefore active by default. The collision/mine/visibility block is
    /// gated on <c>activeF &amp;&amp; bStarted</c>; this port reproduces the gate for
    /// the mine pass and the every-10th visibility pass, and leaves the default
    /// true so ordinary missions are unaffected.
    /// </summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// Whether the Update pass that just completed was one of the every-10th
    /// active started passes on which native runs each model's
    /// <c>UpdateSeenBySide</c> and then raises the per-cluster
    /// <c>ClusterUpdateEvent</c> (<c>clusterIGC.cpp:347-357</c>). The mission
    /// tick reads this immediately after <see cref="Update"/> and forwards the
    /// callback only then, so the port raises the site event on native's
    /// cadence rather than once per chunk. Not an allocation: a plain bool.
    /// </summary>
    public bool ClusterUpdateDue { get; private set; }

    // P112 — ships whose active ripcord this tick's revalidation
    // (ResolveRipcordCountdowns) found illegal, so the sector cleared their
    // countdown state. Accumulated per tick (across a Mission.Update's chunks,
    // hence appended not cleared here) and drained by the host right after
    // Mission.Update so it can broadcast RIPCORD_ABORTED(187) sector-wide via
    // its wire helper — the Core side never sends wire.
    private readonly List<Ship> _pendingRipcordAborts = [];

    // P113 — ships whose ripcord countdown reached zero this tick
    // (ResolveRipcordCountdowns) with a still-legal anchor. Same accumulate-
    // per-tick / drain-after-Mission.Update split as _pendingRipcordAborts; the
    // host performs the actual teleport (Server.PerformRipcordTeleport, the
    // port of UseRipcord) on each drained ripper.
    private readonly List<Ship> _pendingRipcordTeleports = [];

    // P115 — ship pairs whose rescue this tick's proximity pass
    // (ResolveRescueEvents) found: every friendly same-side pair where one
    // ship carries c_habmLifepod and the other c_habmRescue, within the sum-of-
    // spheres proximity radius. Same accumulate-per-tick /
    // drain-after-Mission.Update split as the ripcord pending lists; the host
    // performs the rescue (Server.PerformRescue) and sends PLAYER_RESCUED(16)
    // — the Core side never sends wire.
    private readonly List<(Ship Rescuee, Ship Rescuer)> _pendingRescues = [];

    /// <summary>P301 — this tick's station-side pod rescues (detected by
    /// <see cref="ResolveStationRescueEvents"/>: a drifting <c>c_habmLifepod</c>
    /// within proximity of a same-side station whose type carries
    /// <c>c_sabmRescue</c>), drained by <see cref="DrainStationRescues"/> on the
    /// sim thread. P780 adds the third producer: <see cref="ResolveProbeRescueEvents"/>
    /// queues <c>(pod, Station: null)</c> for a lifepod that touched a rescue
    /// probe — a <c>null</c> <c>Station</c> means "NULL-rescuer probe rescue
    /// (native <c>RescueShipEvent(pod, NULL)</c>, shipIGC.cpp:907): the host
    /// finds the destination itself (native's <c>FindTarget(... c_sabmRestart)</c>
    /// search, ported as <c>Server.FindFriendlyStation</c>) instead of docking at
    /// a touched station".</summary>
    private readonly List<(Ship Rescuee, Station? Station)> _pendingStationRescues = [];

    // P780 — the sector's live count of rescue-capable probes (a Probe whose
    // type carries c_eabmRescue 0x2000 or c_eabmRescueAny 0x4000, igc.h:781-782).
    // Maintained by Add/Remove so ResolveProbeRescueEvents can early-out O(1)
    // on every sector that has none (the ordinary case — most sectors hold no
    // rescue probe) with zero per-tick cost.
    private int _rescueProbeCount;

    /// <summary>P780 test seam (InternalsVisibleTo): the current rescue-probe
    /// count — 0 for a sector with no rescue-capable probe, back to 0 after the
    /// last one expires or is destroyed.</summary>
    internal int RescueProbeCount => _rescueProbeCount;

    // P780 — reusable probe gather buffer for ResolveProbeRescueEvents:
    // cleared and refilled per tick, never re-allocated (no per-tick garbage).
    private readonly List<Probe> _rescueProbes = [];

    // P116 — ship pairs whose carrier landing this tick's geometric pass
    // (ResolveCarrierLandings) found: every friendly same-side pair where one
    // ship carries c_habmCarrier (igc.h:736) and the other c_habmLandOnCarrier
    // (igc.h:730), either direction, and the lander sits genuinely InGarage
    // (inside the carrier's reconstructed bay half-spaces) at this instant.
    // P754b: for a MISSION-bound sector (every production sector) the landing is
    // performed inside the site callback itself — ServerHost.LandOnCarrierEvent →
    // Server.PerformRelaunch, native's atomic detect+resupply+reposition+
    // RELAUNCH_SHIP body (FedSrv.CPP:12814-13031) — so nothing is queued here.
    // This list therefore only feeds the mission-less detection fixtures (P116's
    // bare-sector geometry tests): a stationary lander legitimately re-fires each
    // tick (no native "docked at carrier" latch); the relaunch ends the condition
    // by moving the ship out of the bay.
    private readonly List<(Ship Carrier, Ship Lander)> _pendingCarrierLandings = [];

    // P123 — treasure pickups this tick's proximity pass
    // (ResolveTreasurePickups) found: a rescue-capable, player-typed ship (the
    // native HandleCollision gate, treasureIGC.h:83-98) overlapping a c_tcCash
    // treasure. Same accumulate-per-tick / drain-after-Mission.Update split as
    // the other pending lists; the host credits the ship's side, broadcasts
    // MONEY_CHANGE side-wide and DESTROY_TREASURE whole-mission
    // (Server.PerformTreasurePickup) — the Core side never sends wire.
    private readonly List<(Ship Ship, Treasure Treasure)> _pendingTreasurePickups = [];

    // P163 — in-progress building effects in this sector (a Builder committed
    // to constructing a station at an asteroid). Advanced by the per-tick
    // ResolveBuildingEffects pass (the same accumulate/resolve shape as the
    // other sector passes): a build whose Builder has died aborts (asteroid
    // untouched, no station), and a build past its completion time fires the
    // asteroid→station transformation. One BuildingEffect per simultaneous
    // construction; cleared as each completes or aborts.
    private readonly List<BuildingEffect> _buildingEffects = [];
    private readonly List<BuildingEffect> _fadingBuildingEffects = [];

    // P164 — building-effect interruptions this tick's ResolveBuildingEffects
    // pass detected (an in-progress build whose Builder died mid-build): the
    // target asteroid's object id, accumulated so the host can broadcast
    // BUILDINGEFFECT_DESTROYED(181) whole-mission. Same accumulate-per-tick /
    // drain-after-Mission.Update split as _pendingTreasurePickups and the
    // ripcord pending lists; the Core side never sends wire.
    private readonly List<short> _pendingBuildingEffectInterruptions = [];

    // P294 — building-effect creations this tick's StartBuildingEffect
    // registered (a Builder committed to a new construction): the effect
    // itself, accumulated so the host can broadcast the creation
    // EXPORT(26)/OT_buildingEffect to peers — the exact companion of
    // _pendingBuildingEffectInterruptions above (accumulate at start /
    // drain-after-Mission.Update; the Core side never sends wire).
    private readonly List<BuildingEffect> _pendingBuildingEffectCreations = [];

    /// <summary>Construct a sector from its cluster record; contents are attached during assembly.</summary>
    public Sector(DataClusterIGC cluster, double initialTime = 0)
    {
        Cluster = cluster;
        // Native CclusterIGC::Initialize seeds m_lastUpdate = now (clusterIGC.cpp:27-37).
        LastUpdate = initialTime;
    }

    internal void AttachStations(IEnumerable<DataStationIGC> stations, StaticCore core)
    {
        _stations.AddRange(stations);
        // One runtime Station per static record whose StationTypeId resolves in
        // the core (P125) — the station parallel of AttachAsteroids building the
        // _mineable list. A record with an unresolvable type stays a static-only
        // station (existing docking/ripcord behaviour, no combat state).
        foreach (var s in stations)
            if (core.StationTypes.TryGetValue(s.StationTypeId, out var t))
                _stationRuntimes.Add(new Station(s, t));
    }
    /// <summary>Register a runtime station (its static record + combat state)
    /// — synthetic injection for the loopback tests, mirroring
    /// <see cref="Mission.PlaceAsteroid"/>. Adds the static record to
    /// <see cref="Stations"/> (so the docking/ripcord seams see it) and the
    /// runtime <see cref="Station"/> to <see cref="StationRuntimes"/>.</summary>
    internal void AttachStation(Station station)
    {
        _stations.Add(station.Data);
        _stationRuntimes.Add(station);
    }

    internal void SyncStationData(Station station)
    {
        int index = _stations.FindIndex(s => s.StationId == station.StationId);
        if (index >= 0) _stations[index] = station.Data with { SideId = station.SideId };
    }
    /// <summary>Remove a destroyed station: drop it from the runtime list AND
    /// remove its static record from <see cref="Stations"/> so the docking /
    /// ripcord-anchor / undock seams stop resolving it — the station parallel of
    /// <see cref="RemoveAsteroid"/> (the death contract, P125). Called by
    /// <see cref="ResolveCombatCollisions"/> when a hit crosses the station's
    /// <see cref="Station.ArmorFraction"/> to zero (the port of the native
    /// station's Terminate → DeleteStation, stationIGC.h:183).</summary>
    public bool RemoveStation(Station station)
    {
        _stationRuntimes.Remove(station);
        bool removed = _stations.RemoveAll(s => s.StationId == station.StationId) > 0;
        station.Mission?.Sides.FirstOrDefault(s => s.Id == station.SideId)?.NotifyBuildingTechsChanged();
        return removed;
    }
    /// <summary>Remove a resolved warp from this sector's <see cref="Warps"/>
    /// list — the debug-test-topology warp-isolation parallel of
    /// <see cref="RemoveStation"/> (P379). The removal is strictly
    /// one-directional: a sector's warp list is its own, so removing Omega's
    /// warp-to-Tellus object does <b>not</b> touch Tellus's separate
    /// warp-to-Omega object (the P379 topology trims each home base's
    /// OUTBOUND gates down to a single Alderan choke point while leaving the
    /// contested sector's own list untouched). Every real consumer reads
    /// <see cref="Warps"/> directly — the CLUSTERINFO graph build, the
    /// per-peer warp export, warp-hit/warp-bomb collision
    /// (<see cref="Sector.ResolveWarpCollisions"/>) and NPC pathfinding BFS
    /// (<see cref="FindPathToward"/>) — so removing the warp from this source
    /// list is sufficient on its own; nothing needs special-casing. Returns
    /// <c>true</c> if the warp was present and removed.</summary>
    public bool RemoveWarp(ResolvedWarp warp)
    {
        ArgumentNullException.ThrowIfNull(warp);
        return _warps.Remove(warp);
    }
    internal void AttachAsteroids(IEnumerable<DataAsteroidIGC> asteroids)
    {
        _asteroids.AddRange(asteroids);
        foreach (var a in asteroids) _mineable.Add(new Asteroid(a) { Sector = this, Mission = Mission });   // one runtime model per static rock
    }
    /// <summary>Register a runtime asteroid for the mining pass (assembly wraps
    /// static ones; <see cref="Mission.PlaceAsteroid"/> uses this for synthetic
    /// injection in the loopback mining tests).</summary>
    internal void AttachAsteroid(Asteroid asteroid)
    {
        _asteroids.Add(asteroid.Data);
        _mineable.Add(asteroid);
    }
    internal void AttachWarp(ResolvedWarp warp) => _warps.Add(warp);

    /// <summary>The models that live in this sector and tick with it, in insertion order.</summary>
    public IReadOnlyList<Model> Models => _models;

    /// <summary>
    /// P116 — the production resolver/cache for carrier landing-bay geometry,
    /// keyed by hull <c>ModelName</c> (see <see cref="Collision.HullLandingBayCache"/>).
    /// When set, the per-tick <see cref="ResolveCarrierLandings"/> pass runs the
    /// real <see cref="Ship.InGarage"/> geometric test against genuine
    /// <c>.cvh</c> bay data; when <c>null</c> (unattached) that detection is
    /// unavailable and the pass is a no-op. Attached by the host at mission
    /// assembly (src/Allegiance.Server/Server.cs, an art-path-backed loader) and
    /// set directly by tests; the sim thread never mutates it after attach.
    /// </summary>
    public HullLandingBayCache? LandingBays { get; set; }

    /// <summary>
    /// P550 — the production resolver/cache for <b>station-type</b> landing-bay
    /// geometry, keyed by station type model-name (see
    /// <see cref="Collision.StationLandingBayCache"/>). When set, the per-tick
    /// <see cref="ResolveDockCollisions"/> pass runs the real
    /// <see cref="Sim.Station.InGarage"/> geometric test against genuine
    /// landing-bay data from the station's own model — the native
    /// station docking gate (<c>shipIGC.cpp:843,853</c> → <c>CstationIGC::InGarage</c>,
    /// <c>stationIGC.cpp:385-436</c>) — instead of a crude 200-unit dock sphere.
    /// When <c>null</c> (unattached) or a station type has no resolvable bay,
    /// that station cannot be docked at (the native no-land-slots no-op).
    /// Attached by the host at mission assembly (same art-path-backed loader as
    /// <see cref="LandingBays"/>) and set directly by tests; the sim thread
    /// never mutates it after attach.
    /// </summary>
    public StationLandingBayCache? StationLandingBays { get; set; }

    /// <summary>
    /// P621 — the production resolver/cache for a model's <c>.cvh</c> collision
    /// hull, keyed by model <c>ModelName</c> (see
    /// <see cref="Collision.CollisionHullCache"/>). When set, the ship↔ship,
    /// ship↔asteroid and ship↔station collision passes collide against each
    /// body's real convex hulls — the native <c>LoadCVH</c>/<c>HitTest::Create</c>
    /// shape (<c>modelIGC.cpp:105-118</c>) — instead of a bounding sphere, so an
    /// elongated rock only contacts on its actual surface, not on the empty
    /// corners of its enclosing sphere. When <c>null</c> (unattached, e.g. a
    /// test mission) every collider falls back to the sphere primitive, exactly
    /// as before P621. Attached by the host at mission assembly (the same
    /// art-path-backed loader as <see cref="LandingBays"/>) and set directly by
    /// tests; the sim thread never mutates it after attach.
    /// </summary>
    public CollisionHullCache? CollisionHulls { get; set; }


    /// <summary>Place a model in this sector (<c>CclusterIGC::AddModel</c>).
    /// P780 — also maintains <see cref="_rescueProbeCount"/>: a Probe whose
    /// type carries a rescue capability bit (<c>c_eabmRescue</c> 0x2000 or
    /// <c>c_eabmRescueAny</c> 0x4000, <c>Igc/igc.h:781-782</c>) is counted on
    /// entry so the probe-rescue pass can early-out when there is none.</summary>
    public void Add(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _models.Add(model);
        if (model is Probe { } addedProbe &&
            (addedProbe.ProbeType.EabmCapabilities & (DataProbeTypeIGC.EabmRescue | DataProbeTypeIGC.EabmRescueAny)) != 0)
            _rescueProbeCount++;
        _memberVersion++;                       // P324 (M3): invalidate the in-tick candidate cache
    }

    /// <summary>Remove a model from this sector; returns whether it was present.
    /// This is the port's <c>SetCluster(NULL)</c> list edit and is used both for
    /// destruction and for sector-to-sector MOVES (ripcord, docking, respawn,
    /// capture), so it raises <b>no</b> site notification — a move is not a
    /// termination (P754b: <c>CmodelIGC::Terminate</c> is the only native
    /// <c>TerminateModelEvent</c> producer, <c>Igc/modelIGC.cpp:55-61</c>). Use
    /// <see cref="Terminate"/> to destroy a model.</summary>
    public bool Remove(Model model)
    {
        if (!_models.Remove(model)) return false;
        if (model is Probe probe)
        {
            // P780 — mirror Add's counter maintenance: every probe removal path
            // (expiry in Probe.OnUpdate, lethal damage in Probe.ReceiveDamage,
            // ammo exhaustion in Probe.FireIfArmed — all via Terminate below —
            // and manual removal) funnels through here, so the count returns to
            // zero when the last rescue probe leaves the sector.
            if ((probe.ProbeType.EabmCapabilities & (DataProbeTypeIGC.EabmRescue | DataProbeTypeIGC.EabmRescueAny)) != 0)
                _rescueProbeCount--;
            probe.OnRemoved();
        }
        _memberVersion++;                       // P324 (M3): invalidate the in-tick candidate cache
        return true;
    }

    /// <summary>
    /// P754b — destroy a model: raise <see cref="IIgcSite.TerminateModelEvent"/>,
    /// then evict it from the sector. The exact native order in
    /// <c>CmodelIGC::Terminate</c> (<c>Igc/modelIGC.cpp:55-61</c>): the site is
    /// notified <b>before</b> the model's cluster link is cleared, which is why
    /// the notification happens here and not inside <see cref="Remove"/>
    /// (a sector move also removes the model from the old cluster's list, and
    /// native fires no event for a move — <c>SetCluster</c> only detaches).
    /// Returns whether the model was still present (idempotent, so a death path
    /// and the sector's own dead-model sweep cannot double-notify).
    /// </summary>
    public bool Terminate(Model model)
    {
        if (!_models.Contains(model)) return false;
        Mission?.Site.TerminateModelEvent(model);
        return Remove(model);
    }

    /// <summary>
    /// Remove a runtime asteroid from the mining pass (and now the combat pass),
    /// mirroring <see cref=\"Remove(Model)\"/>'s shape for the sector's separate
    /// <c>_mineable</c> list. Called by <see cref=\"ResolveCombatCollisions\"/> when
    /// a hit crosses the rock's <see cref=\"Asteroid.Fraction\"/> to zero (the port
    /// of <c>pasteroid-&gt;Terminate()</c> inside <c>KillAsteroidEvent</c>,
    /// <c>FedSrv.CPP:12420</c>): a destroyed rock stops being minable and stops
    /// appearing in future combat/mining passes. Returns whether it was present.
    /// </summary>
    public bool RemoveAsteroid(Asteroid asteroid)
    {
        // Native Terminate removes the rock from the cluster, including future join exports
        // (FedSrv.CPP:11459-11462). Keep the port's record and runtime lists in agreement.
        _asteroids.RemoveAll(a => a.AsteroidId == asteroid.Data.AsteroidId);
        bool removed = _mineable.Remove(asteroid);
        if (removed && asteroid.BuildingEffect is { } effect)
            TerminateBuildingEffect(effect);
        return removed;
    }

    /// <summary>
    /// P163 — the sector's in-progress building effects (a Builder committed to
    /// constructing a station at an asteroid), for inspection/assertion by the
    /// host and the loopback tests.
    /// </summary>
    public IReadOnlyList<BuildingEffect> BuildingEffects => _buildingEffects;

    /// <summary>
    /// P163 — explicitly start a building effect: a Builder ship commits to
    /// constructing <paramref name="stationType"/> at <paramref name="target"/>,
    /// completing <see cref="BuildingEffect.BuildDuration"/> sim-seconds from
    /// <paramref name="now"/>. This is the test-authored trigger point that
    /// Stage 3's future Builder AI decision layer will call instead of a test
    /// setting it directly. The target station type is supplied directly (no
    /// <c>m_abmOrders</c>/<c>c_aabmBuildable</c> discovery — that decision logic
    /// is Stage 3). The side of the finished station is captured from the
    /// Builder's <see cref="Ship.SideId"/> at start, mirroring native's
    /// initialize-time capture (<c>buildingeffectigc.h:68</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Preconditions.</b> The Builder must already be in this sector
    /// (placed via <see cref="Mission.PlaceShip"/>), and the target asteroid
    /// must be one of this sector's runtime rocks (<see cref="Mineable"/> /
    /// <c>_mineable</c>). Both are asserted here as the port's honest
    /// precondition: a build whose Builder is not in the sector cannot be
    /// advanced by this sector's resolver, and a build whose asteroid is not in
    /// <c>_mineable</c> cannot be transformed (removed) on completion. The
    /// native likewise asserts a non-null Builder/asteroid at Initialize
    /// (<c>buildingeffectigc.h:65,72</c>).</para>
    /// <para><b>Returned effect.</b> The newly-registered <see cref="BuildingEffect"/>
    /// is returned so a caller can track it; it is otherwise reachable via
    /// <see cref="BuildingEffects"/>.</para>
    /// </remarks>
    public BuildingEffect StartBuildingEffect(Ship builder, Asteroid target, DataStationTypeIGC stationType, double now)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        // One constructor and one asteroid per in-progress build. This entry
        // point also protects autonomous builders from duplicate commitments.
        if (builder.Dead || builder.IsBuilding || builder.StationId is not null
            || _buildingEffects.Any(effect => ReferenceEquals(effect.Target, target)))
            throw new InvalidOperationException("builder or target asteroid is unavailable for construction");

        if (!_models.Contains(builder))
            throw new InvalidOperationException(
                $"builder '{builder.Id}' is not in this sector; a building effect can only be started in the builder's own sector");
        if (!_mineable.Contains(target))
            throw new InvalidOperationException(
                $"target asteroid '{target.Data.AsteroidId}' is not in this sector's mineable set; a station can only be built at a rock in this sector");

        // P280 — the techbase→asteroid-type construction gate. Native only
        // allows a Builder to construct a station type on an asteroid whose
        // capability mask satisfies the station type's build AABM
        // (<c>m_abmOrders</c> = <c>GetBuildAABM()</c>, shipIGC.h:2053-2057;
        // the c_cidBuild legality check rejects a build on a mismatched rock,
        // shipIGC.h:1695-1707). Rejecting here — the port's sole construction
        // entry point — is the direct port of that legality gate: a Garrison
        // (AabmBuild 0x08) cannot be raised on a carbon/special rock any more
        // than a Supremacy Center (AabmBuild 0x40) on a generic one. The data
        // both sides already decode (asteroidigc.h:259-262 /
        // stationtypeigc.h:207-210); only the enforcement was missing.
        if (!target.Data.HasCapability(stationType.AabmBuild))
            throw new InvalidOperationException(
                $"refusing to start construction of station type '{stationType.Name}' (type {stationType.StationTypeId}, AabmBuild 0x{stationType.AabmBuild:x}) at asteroid {target.Data.AsteroidId} '{target.Data.Name}' (AabmCapabilities 0x{target.Data.AabmCapabilities:x}) — the asteroid is the wrong type for this techbase (native gates this, shipIGC.h:1695-1707)");

        var side = builder.SideId
            ?? throw new InvalidOperationException("builder has no SideId; a built station must belong to a side");
        var effect = new BuildingEffect(target, builder, stationType, side, now + BuildingEffect.BuildDuration / (builder.Mission?.ShowcaseResearchTimeScale ?? 1), builder.Mission?.ShowcaseResearchTimeScale ?? 1);
        _buildingEffects.Add(effect);
        target.BuildingEffect = effect;
        effect.InformedSides.Add(side);
        var seenSides = ConstructionSeenSides(builder, target);
        foreach (var other in builder.Mission!.Sides)
            if (target.GetCurrentEye(other.Id) || (other.Id < seenSides.Length && seenSides[other.Id]))
                effect.InformedSides.Add(other.Id);

        // P294 — record the creation for the host to broadcast the
        // EXPORT(26)/OT_buildingEffect (drained via
        // DrainBuildingEffectCreations, the companion of the interruption
        // drain). Mirrors native FedSrvSiteBase::CreateBuildingEffect's
        // broadcast trigger (<c>FedSrv.CPP:13733-13767</c>).
        _pendingBuildingEffectCreations.Add(effect);

        // P165 — mark the Builder as committed so its decision cycle (which
        // gates on !IsBuilding) does not re-decide every idle tick and stack a
        // duplicate build. Cleared in both ResolveBuildingEffects terminal
        // branches (interruption abort and CompleteBuild).
        builder.IsBuilding = true;

        return effect;
    }

    /// <summary>
    /// P138 — build this sector's ship-update candidate list: every ship
    /// <b>physically in the cluster</b>, any side, that is eligible to appear
    /// in a <c>LIGHT_SHIPS_UPDATE</c>(175)/<c>HEAVY_SHIPS_UPDATE</c>(176)
    /// broadcast. This is the real selection mechanism the native
    /// <c>SendShipUpdates</c> uses for this message family, ported as a query
    /// over this sector's own model membership.
    /// </summary>
    /// <remarks>
    /// <para><b>Cluster-membership, not <c>SeenBySide</c>.</b> Native builds
    /// the candidate array straight from <c>pcluster-&gt;GetShips()</c>
    /// (<c>FedSrv.CPP:3727</c>), the cluster's own ship list — <b>no
    /// <c>SeenBySide</c> check anywhere in the whole block</b>
    /// (<c>FedSrv.CPP:3738-4216</c>). Every non-boarded ship in the cluster is
    /// a candidate regardless of side or of what any observing side can see;
    /// per-recipient culling (and target-lock promotion) happens later, at
    /// per-player assembly. This deliberately corrects the P136/P137 breakdown
    /// that framed P138 as "make foreign ships per-side visibility targets" —
    /// that is the <c>SeenBySide</c>-gated <c>STATIONS/ASTEROIDS/PROBES</c>
    /// model (P133-135), a different family this message does not share. So the
    /// list does <b>not</b> consult <see cref="Model.SeenBySide"/> at all.</para>
    /// <para><b>Gate decisions (plainly stated).</b> Native excludes
    /// boarded ships — a ship with <c>GetParentShip() != NULL</c> is routed to
    /// the turret relay lists, not to <c>rgShips</c> (<c>FedSrv.CPP:3779</c>) —
    /// so the port drops <c>ParentShip != null</c>. The native "recent-client-
    /// update timestamp" gate (<c>ucLastUpdate == c_ucShipUpdate</c> with
    /// <c>dt ∈ [-1.5,1.5]</c>, <c>FedSrv.CPP:3767-3776</c>) is <b>skipped</b>:
    /// it is a bandwidth-throttling detail tied to the C→S <c>ClientShipUpdate</c>
    /// cadence, and this server-authoritative port has no per-ship "a fresh
    /// update arrived this interval" concept — its codecs already "send every
    /// supplied ship" (P136). Likewise the drone throttle's frame-hash schedule
    /// (<c>FedSrv.CPP:3804-3805</c>) is <b>commented out in native itself</b>,
    /// so the native already sends every drone each frame; the port has no
    /// player/drone subtype and sends every <see cref="Ship"/>. The deviation
    /// sort (<c>FedSrv.CPP:3923</c>) and the heavy/light budget are
    /// <b>deferred</b> to the dedicated budget task; this list keeps native's
    /// unordered membership order (native's own order is immaterial because it
    /// immediately qsorts by deviation).</para>
    /// <para><b>Membership order is stable</b> (<c>_models</c> insertion order),
    /// so the caller's assembly is deterministic given the same sector — the
    /// honest stand-in for native's then-qsort'd array until the budget task
    /// reintroduces prioritisation. Ships are only ever <i>excluded</i> here,
    /// never duplicated or reordered, matching "candidate selection" not
    /// "prioritisation".</para>
    /// </remarks>
    public IReadOnlyList<Ship> GetShipUpdateCandidates()
    {
        // P324 (M3): the list is cached once per sector per tick, shared by all
        // of the tick's callers (BroadcastShipsUpdate per seated peer plus the
        // NPC arbitration searches). Invalidated when the tick advances
        // (UpdateCount) or the model membership changes (Add/Remove bump
        // _memberVersion — a ship dying or warping out Remove from _models; a
        // docked ship does NOT (it must still tick and undock), so docking calls
        // InvalidateShipUpdateCandidates() instead — see ResolveDockCollisions /
        // P482). Because the host's broadcasts run after Mission.Update and a
        // membership change invalidates the cache, a ship that dies this tick is
        // recomputed out of the broadcast exactly as before; otherwise every
        // caller in the tick shares one snapshot instead of reallocating +
        // re-scanning on each call.
        if (_shipUpdateCandidates is not null
            && _shipCandidatesTick == UpdateCount
            && _shipCandidatesMemberVersion == _memberVersion)
            return _shipUpdateCandidates;

        var candidates = new List<Ship>();
        foreach (var model in _models)
        {
            if (model is not Ship ship) continue;          // only ships carry a ship update
            if (ship.ParentShip is not null) continue;     // boarded (turret/observer) → turret lists, not rgShips (FedSrv.CPP:3779)
            if (ship.Dead) continue;                       // not flying; dead ships leave the sector anyway (Sector.cs:1105), belt-and-suspenders like the rescue pass
            if (ship.StationId is not null) continue;      // docked ships are not physically in the cluster (native: absent from cluster->GetShips())
            candidates.Add(ship);
        }
        _shipUpdateCandidates = candidates;
        _shipCandidatesTick = UpdateCount;
        _shipCandidatesMemberVersion = _memberVersion;
        return candidates;
    }

    /// <summary>
    /// P482 — discard the in-tick ship-update-candidate cache so the next
    /// <see cref="GetShipUpdateCandidates"/> call recomputes from the live
    /// <c>_models</c>. This is the invalidate-half of the cache's P324 contract,
    /// reserved for a mid-tick state change that alters candidate membership
    /// WITHOUT an Add/Remove bump of <c>_memberVersion</c>. Currently only
    /// docking needs it: a ship that docks this tick keeps its place in
    /// <c>_models</c> (it must still tick and undock), but its <c>StationId</c>
    /// latch must take the ship out of the SAME tick's ship-update broadcast —
    /// the native invariant that a docked ship is absent from the cluster
    /// (cluster->GetShips()). Without this, the cache built earlier in the tick
    /// (during the models loop, before the dock pass) would still feed the
    /// docked ship to HEAVY_SHIPS_UPDATE, and the client's unconditional
    /// Apply(HeavyShipsUpdate) would clear the SHIP_DELETE(SDR_DOCKED) IsGone
    /// latch in the same tick. <c>_memberVersion</c> is consulted ONLY by the
    /// candidate cache, so bumping it here is a pure invalidation signal — no
    /// other state reads it.
    /// </summary>
    private void InvalidateShipUpdateCandidates()
    {
        _memberVersion++;
    }


    /// <summary>
    /// P157 — find the first warp to head toward from <b>this</b> sector in
    /// order to ultimately reach <paramref name="targetSector"/>: a
    /// breadth-first search over the sector warp graph (<see cref="Warps"/> →
    /// <see cref="ResolvedWarp.Destination"/>), returning the <em>first</em>
    /// warp of a shortest (by hop count) path, or <c>null</c> when the target
    /// is <see langword="this"/> sector or unreachable.
    /// <para>This is the port of native <c>FindPath(IshipIGC*, IclusterIGC*,
    /// bool bCowardly)</c> (<c>Igc/common.cpp:894-1012</c>) reduced to its
    /// traversal core. Native returns <c>path.pwarpStart</c> — the first warp
    /// of the winning path — which is exactly what <see cref="Ship.ApplyNpcAutopilot"/>
    /// needs to steer a ship toward the next aleph; the return value maps 1:1
    /// onto that (a <see cref="ResolvedWarp"/>, this port's concrete realisation
    /// of native's <c>IwarpIGC*</c>, including the transit seam
    /// <c>ResolveWarpCollisions</c> consumes).</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Plain unweighted BFS.</b> Native's frontier is
    /// distance-sorted (the current sector's warps queued nearest-first,
    /// common.cpp:914-960) — a best-first search preferring the physically
    /// nearest warp. This port deliberately simplifies to plain FIFO BFS. Both
    /// find a shortest path by hop count; the only loss is native's tie-break
    /// that prefers the nearer warp among equal-hop alternatives. Documented,
    /// accepted simplification.</para>
    /// <para><b>No <c>bCowardly</c>/friendly-territory filter.</b> Native's
    /// cowardly mode (common.cpp:924-926, 991-993) prunes warps that neither
    /// lead toward the target nor into a friendly cluster
    /// (<c>IsFriendlyCluster</c>). Investigated directly this session: this
    /// port's sector model has no ownership/territory notion to filter on — no
    /// <c>Sector.SideId</c> or analogous field exists (a sector is a neutral
    /// <c>OT_cluster</c>; its <see cref="Id"/> is a layout id, not an owner) —
    /// so all warps are traversed unconditionally, the non-cowardly native
    /// behavior, stated as the applied default.</para>
    /// <para><b>No visibility gate.</b> Native also skips warps the ship
    /// cannot <c>CanSee</c> (common.cpp:920, 987). An autonomous NPC pathfinder
    /// in this pure-sim port navigates the full static graph regardless of
    /// scan-state; combined with the cowardly filter dropping above, this is
    /// the unconditional-traversal simplification.</para>
    /// <para><b>Cost / re-evaluation.</b> Each sector is visited at most once
    /// (<see cref="HashSet{T}"/> by reference identity) and each warp examined
    /// at most twice (once from each endpoint sector), so the search is O(E)
    /// and trivially cheap on the small per-sector warp lists of the real
    /// corpus. The autopilot re-resolves its goal every tick, so no caching is
    /// built here — a ship that warps mid-journey simply re-paths from its new
    /// sector on the next tick (P157 scope), which this method supports for
    /// free.</para>
    /// </remarks>
    public ResolvedWarp? FindPathToward(Sector targetSector)
    {
        ArgumentNullException.ThrowIfNull(targetSector);
        if (ReferenceEquals(this, targetSector)) return null;      // already there (common.cpp:904-905)

        // Frontier of (warp, firstWarpOnPath). Seeded with every warp of THIS
        // sector, each carrying itself as the first warp of its own path; when
        // a warp's destination sector is expanded we push that sector's warps,
        // carrying the seed's first warp forward — the pwarpStart propagation
        // of common.cpp:933 and 1001-1002 — so the returned edge is always one
        // to fly toward from THIS sector, however many hops remain.
        var visited = new HashSet<Sector> { this };
        var frontier = new Queue<(ResolvedWarp warp, ResolvedWarp first)>();
        foreach (var w in Warps)
            if (w.Destination is { } seedDest && visited.Add(seedDest))
                frontier.Enqueue((w, w));

        while (frontier.TryDequeue(out var cur))
        {
            if (cur.warp.Destination is not { } dest) continue;          // dangling edge: never transit (Sector.cs:1668)
            if (ReferenceEquals(dest, targetSector)) return cur.first;   // found a path (common.cpp:972-977)
            foreach (var w in dest.Warps)
                if (w.Destination is { } d2 && visited.Add(d2))
                    frontier.Enqueue((w, cur.first));
        }
        return null;                                                     // unreachable (common.cpp:966-968)
    }

    /// <summary>
    /// SIM-0107 — port of the LIVE native <c>IsFriendlyCluster</c>
    /// (<c>common.cpp:388-433</c>), not the commented-out station-only block at
    /// <c>common.cpp:357-386</c>. A cluster is friendly for cowardly pathing when
    /// it is the side's territory (<see cref="IsTerritory"/>) AND the weighted
    /// count of ships seen in it is non-negative: enemy carriers −6, enemy
    /// players/wingmen −2, other enemies −1; friendly carriers +2, friendly
    /// players/wingmen +2, other friendlies +1.
    /// </summary>
    public bool IsFriendlyCluster(short sideId)
    {
        if (!IsTerritory(sideId)) return false;             // pside->IsTerritory(pcluster), common.cpp:388-391

        int friendlyShipCount = 0;
        foreach (var ship in GetShipUpdateCandidates())
        {
            if (!ship.SeenBySide(sideId)) continue;         // common.cpp:397
            bool enemy = ship.SideId is not { } shipSide || !IsAlliedSide(sideId, shipSide);
            int weight = ship.PilotType == Ship.PilotTypeCarrier ? (enemy ? 6 : 2)
                : ship.PilotType >= Ship.PilotTypePlayer || ship.PilotType == Ship.PilotTypeWingman ? 2
                : 1;
            friendlyShipCount += enemy ? -weight : weight;
        }
        return friendlyShipCount >= 0;                      // common.cpp:424-431
    }

    /// <summary>
    /// SIM-0107 — native <c>CsideIGC::IsTerritory</c> (<c>sideigc.h:603-606</c>)
    /// computed on the fly from <c>UpdateTerritory</c> (<c>sideigc.cpp:421-493</c>):
    /// a cluster is a side's territory when the side has a station here and no
    /// enemy station is seen, or when it is an empty cluster fully surrounded
    /// (within three hops) by territory.
    /// </summary>
    public bool IsTerritory(short sideId) => IsTerritory(sideId, new HashSet<Sector>(), 0);

    private bool IsTerritory(short sideId, HashSet<Sector> linked, int depth)
    {
        if (!linked.Add(this)) return false;

        bool hasOwnStation = false;
        foreach (var station in _stationRuntimes)
        {
            if (station.SideId == sideId)
            {
                hasOwnStation = true;                       // sideigc.cpp:436-438
            }
            else if (station.SeenBySide(sideId) && !IsAlliedSide(sideId, station.SideId))
            {
                return false;                               // seen enemy station → not territory
            }
        }
        if (hasOwnStation) return true;

        // Empty clusters may still be territory when fully surrounded
        // (sideigc.cpp:471-492).
        return IsSurroundedByTerritory(sideId, linked, depth);
    }

    private bool IsSurroundedByTerritory(short sideId, HashSet<Sector> linked, int depth)
    {
        if (depth >= 3) return false;                       // sideigc.cpp:325 currentDepth < 3

        foreach (var station in _stationRuntimes)
            if (station.SeenBySide(sideId)) return false;   // sideigc.cpp:328-333 — not empty

        int checkedWarpCount = 0;
        foreach (var warp in Warps)
        {
            if (warp.Destination is not { } dest) continue;
            checkedWarpCount++;                             // sideigc.cpp:335-343
            if (linked.Contains(dest)) continue;
            if (!dest.IsTerritory(sideId, linked, depth + 1)) return false;   // sideigc.cpp:345 currentDepth + 1
        }
        return checkedWarpCount > 1;                        // sideigc.cpp:340
    }

    private bool IsAlliedSide(short a, short b)
    {
        if (a == b) return true;
        if (Mission is null) return false;
        // P780: plain indexed scans, not LINQ/foreach — the probe-rescue pass
        // calls this per pod × probe per tick, and a lambda closure + delegate
        // (or the boxed struct enumerator a foreach over the IReadOnlyList
        // interface produces) per call would make it allocate (the whole pass
        // must be allocation-free).
        var sides = Mission.Sides;
        Side? sideA = null;
        for (int i = 0; i < sides.Count; i++)
            if (sides[i].Id == a) { sideA = sides[i]; break; }
        if (sideA?.AllyGroup is not { } group) return false;
        for (int i = 0; i < sides.Count; i++)
            if (sides[i].Id == b && sides[i].AllyGroup == group) return true;
        return false;
    }

    /// <summary>
    /// Per-chunk sector tick (<c>CclusterIGC::Update</c>, <c>clusterIGC.cpp:97</c>).
    /// Calculates the native crowding cost before the models loop
    /// (<c>clusterIGC.cpp:162-198,241-253</c>), then resolves collisions.
    /// </summary>
    /// <remarks>
    /// The whole body is gated on <c>now &gt; m_lastUpdate</c> (<c>clusterIGC.cpp:99</c>).
    /// Crucially, the C++ models loop sits <b>outside</b> the <c>bStarted</c> gate
    /// — it runs for every tick of a live cluster regardless of mission stage, and
    /// only the station/ship/cost block is gated on <c>STAGE_STARTED</c>
    /// (<c>clusterIGC.cpp:103-231</c>). A sector does not carry the mission stage,
    /// and the tick only reaches sectors when the mission is started, so this is
    /// reproduced exactly: the models loop runs whenever <c>now &gt; LastUpdate</c>,
    /// unconditionally, with no stage check.
    /// <para>
    /// The C++ captures the successor link before calling <c>Update</c> because
    /// models may self-terminate (<c>lNext = l-&gt;next()</c>, <c>clusterIGC.cpp:243</c>).
    /// The port honours that with a snapshot of the live list: a model that
    /// removes itself from the sector during its own <c>Update</c> (a
    /// <see cref="Projectile"/> that expires) cannot invalidate the iteration,
    /// and a model <b>added</b> during the walk (a projectile spawned by a
    /// firing ship, <c>weaponIGC.cpp:290</c>) ticks from the <b>next</b> chunk —
    /// its clock is seeded to creation time, so that is a genuine full chunk,
    /// exactly as a C++ model created mid-pass waits for the next pass. This is
    /// the one structural deviation the slice's predecessor explicitly deferred
    /// (its models could not self-terminate); now they can.
    /// </para>
    /// </remarks>
    public void Update(double timeUpdate)
    {
        if (timeUpdate <= LastUpdate) return;   // now > m_lastUpdate guard (clusterIGC.cpp:99)
        double dt = timeUpdate - LastUpdate;    // chunk length — the collision sweep width
        UpdateCount++;
        LastUpdate = timeUpdate;

        // clusterIGC.cpp:162-198,231. The native inactive reset is the
        // !bStarted branch, independent of the later activeF collision gate.
        Cost = 0f;
        if (Mission is { Stage: MissionStage.Started, CrowdingPenaltyEnabled: true } mission)
        {
            var fc = mission.Core.Constants.FloatConstants;
            Cost = fc[37];
            foreach (var ship in _models.OfType<Ship>())
            {
                // Docked ships are absent from native GetShips(). Parent
                // occupants remain members and contribute their turret weight.
                if (ship.StationId is not null) continue;
                Cost += ship.PilotType < Ship.PilotTypePlayer ? fc[35]
                    : ship.ParentShip is not null ? fc[34]
                    : ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) ? fc[33] : fc[36];
            }
            if (Cost > 0f) Cost *= (float)dt / fc[38];
        }

        if (Mission?.Stage == MissionStage.Started) ResolveConsumedRockVisibility();

        // Snapshot: the models loop must survive a model that self-terminates
        // during its own Update (C++ capture-next, clusterIGC.cpp:243-250).
        var snapshot = _models.ToArray();
        foreach (var model in snapshot)
        {
            if (!_models.Contains(model)) continue; // a previous model's blast may destroy it
            _updatingModel = model;
            _modelStateTime = timeUpdate;
            try { model.Update(timeUpdate); }
            finally { _updatingModel = null; }
        }

        // The C++ collision block sits AFTER the models loop, still inside the
        // same cluster Update, gated on activeF && bStarted (clusterIGC.cpp:255).
        // Ported as the combat pass below (projectile ↔ ship resolution).
        // P600 — every pass below is wrapped in a profiling scope (bucket per
        // subsystem, aggregated across all sectors). No-op when the profiler off.
        { using (TickProfiler.Enter("sim.combat")) ResolveCombatCollisions(timeUpdate, (float)dt); }

        // P254 — ship↔asteroid elastic collision pass. Deliberately a SEPARATE
        // pass rather than a case inside ResolveCombatCollisions: that method is
        // gated on ordnance presence (its early-return at the top requires a live
        // projectile/missile in the sector) and its event loop requires one side
        // of every pair to be IOrdnance — neither holds for a ship bouncing off a
        // rock in an otherwise-empty sector. See ResolveShipAsteroidCollisions.
        { using (TickProfiler.Enter("sim.collision")) ResolveShipAsteroidCollisions(timeUpdate, (float)dt); }

        // P354 — ship↔ship real elastic-collision pass. Grouped here with the
        // P254 asteroid pass because both are the elastic-collision PHYSICS
        // passes of this sector (one is a moving-vs-infinite-mass body, this one
        // is the general two-finite-mass case) — keeping the physics block
        // cohesive. Mutual exclusivity with the rescue pass is NOT achieved by
        // ordering: ResolveRescueEvents still runs later (below, line 651) and
        // this pass independently re-checks the same rescue-eligibility
        // predicate for every detected pair and skips physics, exactly as the
        // native rescue check pre-empts the physics via break
        // (shipIGC.cpp:1027-1042). See ResolveShipShipCollisions.
        { using (TickProfiler.Enter("sim.collision")) ResolveShipShipCollisions(timeUpdate, (float)dt); }

        // P466 — ship↔station elastic collision pass, grouped here with the P254
        // asteroid and P354 ship-ship physics passes. Like the asteroid bounce it
        // is a moving-vs-infinite-static-mass bounce (the station's mass is
        // treated as infinite, so only the ship's velocity changes) — the native
        // OT_station bounce, reached for enemy / non-docking ships via the
        // OT_station → OT_buildingEffect/OT_asteroid → OT_probe fallthrough
        // (shipIGC.cpp:868-960, the same shared block P254 ported). Same-side
        // pairs (about to dock) and shield-down board pairs (about to capture)
        // are excluded inside the pass so the dock/rescue/capture passes are never
        // See ResolveShipStationCollisions.
        { using (TickProfiler.Enter("sim.collision")) ResolveShipStationCollisions(timeUpdate, (float)dt); }

        // P762b / SIM-0064 — apply accumulated mine damage AFTER every collision
        // pass, matching native's collision → mines → Move order
        // (clusterIGC.cpp:322-345). The port's Move runs inside each model's
        // OnUpdate before these collision passes, so only the collision→mine
        // relative order is reproduced here; this method is deliberately NOT
        // claimed to run before movement (see ResolveMineDetonations remarks).
        // Native also gates the block on activeF && bStarted
        // (clusterIGC.cpp:255); the stage/Active check lives inside.
        if (Mission?.Stage == MissionStage.Started)
            using (TickProfiler.Enter("sim.mines"))
                ResolveMineDetonations(timeUpdate, (float)dt);

        // P125 — station health regeneration. Stations are static bodies (not
        // Models), so their regen is a pass of its own here rather than a model
        // Update; native does the same math in CstationIGC::Update
        // (stationIGC.h:189-213) — see Station.Regenerate for the
        // armor-then-shield sequencing.
        { using (TickProfiler.Enter("sim.station")) ResolveStationRegen((float)dt); }

        // Warp-transit proximity pass — a stationary-object test (ship vs the
        // sector's warps), deliberately separate from the moving-object combat
        // collider above. See ResolveWarpCollisions for the spec and scope.
        { using (TickProfiler.Enter("sim.warp")) ResolveWarpCollisions(timeUpdate, (float)dt); }

        // Warp-bomb (missile vs warp) trigger pass + its scheduled-detonation
        // half. A real missile reaching a warp arms a WARP_BOMB(188) broadcast
        // and a 15 s-delayed explosion at the aleph — see the two passes below.
        { using (TickProfiler.Enter("sim.combat")) ResolveWarpBombTriggers(timeUpdate); }
        { using (TickProfiler.Enter("sim.combat")) ResolveWarpBombDetonations(timeUpdate); }

        // Dock proximity pass — ship vs the sector's stations. See
        // ResolveDockCollisions for the spec, the friendly gate and the
        // simplified-radius reasoning.
        { using (TickProfiler.Enter("sim.docking")) ResolveDockCollisions(timeUpdate); }

        // Mining proximity pass — ship vs the sector's asteroids. See
        // ResolveMining gathers ready miners, arbitrates each rock, then pays
        // only the winner (Igc/shipIGC.cpp:2092-2247).
        { using (TickProfiler.Enter("sim.mining")) ResolveMining(timeUpdate, (float)dt); }

        // Building-effect pass — P163. Every in-progress build is checked each
        // tick: a Builder that died aborts the build (asteroid untouched, no
        // station), and a build past its 40 s completion deadline fires the
        // asteroid→station transformation. See ResolveBuildingEffects for the
        // spec and its port of the native CbuildingEffectIGC lifecycle.
        { using (TickProfiler.Enter("sim.economy")) ResolveBuildingEffects(timeUpdate); }

        // Ripcord countdown/revalidation pass — P112/P113. For every ship with
        // an active ripcord and no parent, revalidate the anchor and tick the
        // countdown down; on reaching zero with a legal anchor the ripper is
        // handed to the host's UseRipcord (the teleport, P113). See
        // ResolveRipcordCountdowns.
        { using (TickProfiler.Enter("sim.navigation")) ResolveRipcordCountdowns((float)dt); }

        // Rescue proximity pass — P115. Every friendly same-side ship pair
        // where one carries c_habmLifepod and the other c_habmRescue, within
        // the sum-of-spheres radius, is handed to the host's PerformRescue
        // (PLAYER_RESCUED(16) + placement). See ResolveRescueEvents.
        { using (TickProfiler.Enter("sim.rescue")) ResolveRescueEvents(); }

        // P301 — station-side pod rescue proximity pass. A drifting
        // c_habmLifepod within proximity of a SAME-side station whose type
        // carries c_sabmRescue is rescued (native shipIGC.cpp:862-867 →
        // RescueShipEvent(pod, NULL)). Complements the P115 ship-ship pass:
        // this is how a pod can be saved by reaching a rescue-capable friendly
        // base (docking cancels the P279 oxygen timer). Detected here, drained
        // and performed by the host (Server.PerformStationRescue) — no wire
        // half in Core. See ResolveStationRescueEvents for the spec.
        { using (TickProfiler.Enter("sim.rescue")) ResolveStationRescueEvents(); }

        // P780 — rescue-probe pod rescue pass. A drifting lifepod that touches
        // a probe whose type carries c_eabmRescue (own/allied pods) or
        // c_eabmRescueAny (any pod) is rescued — native shipIGC.cpp:897-909,
        // the OT_probe collision branch the port was missing. Queues a
        // NULL-rescuer rescue (Station: null) through the same station-rescue
        // drain; the host's probe branch (Server.PerformStationRescue) does the
        // placement. O(1) early-out when the sector holds no rescue probe.
        // See ResolveProbeRescueEvents for the spec.
        { using (TickProfiler.Enter("sim.rescue")) ResolveProbeRescueEvents((float)dt); }

        // Carrier-landing geometric pass — P116. Every friendly same-side ship
        // pair where one carries c_habmCarrier and the other c_habmLandOnCarrier,
        // either direction, is tested against the carrier's real reconstructed
        // landing-bay geometry (Ship.InGarage) rather than a sphere proximity
        // shortcut. P754b: the landing is handled (and the relaunch performed)
        // inside the site callback, as native does (FedSrv.CPP:12814-13031). See
        // ResolveCarrierLandings.
        { using (TickProfiler.Enter("sim.rescue")) ResolveCarrierLandings(); }

        // Treasure pickup proximity pass — P123. A rescue-capable, player-typed
        // ship overlapping a c_tcCash treasure picks it up: the sector records
        // the (ship, treasure) pair; the host credits the ship's side, broadcasts
        // MONEY_CHANGE side-wide and DESTROY_TREASURE whole-mission, and removes
        // the treasure. See ResolveTreasurePickups for the spec.
        { using (TickProfiler.Enter("sim.economy")) ResolveTreasurePickups(timeUpdate, (float)dt); }

        // Cross-side visibility sweep — P132. Every ship/station/probe in the
        // sector has its SeenBySide state recomputed against every side's
        // scanners (own side always visible; others pass VisibilityMath.
        // InScannerRange). See ResolveCrossSideVisibility for the spec.
        // P324 (M2): throttled to every c_nPassesPerUpdate (10) passes, the
        // faithful port of native's `if ((m_nPass++) % c_nPassesPerUpdate == 0)`
        // gate that runs each model's UpdateSeenBySide only on every 10th pass
        // (clusterIGC.cpp:348 / clusterIGC.h:22) — a target becomes visible/
        // invisible on the same real-world timescale as native, not instantly-
        // every-tick.
        // SIM-0065 — only an ACTIVE STARTED pass advances the counter: native's
        // whole visibility/callback block sits inside `if (bStarted)`
        // (clusterIGC.cpp:348). Starting-stage chunks must not shift the phase.
        ClusterUpdateDue = false;
        // P762b — native's visibility/callback block is gated on
        // `m_data.activeF && bStarted` (clusterIGC.cpp:255-348); an inactive
        // sector neither advances the pass phase nor raises the callback.
        if (Active && Mission?.Stage == MissionStage.Started
            && (_visPass++ % c_nPassesPerUpdate) == 0)
        {
            ClusterUpdateDue = true;
            using (TickProfiler.Enter("sim.visibility"))
                ResolveCrossSideVisibility();
        }
    }

    /// <summary>
    /// P112/P113 — the per-tick ripcord pass, called from <see cref="Update"/>
    /// just like the other per-tick passes. Centralizes the per-tick countdown +
    /// revalidation arm of the native ship update block
    /// (<c>shipIGC.cpp:270-293</c>) as a sector pass. For every ship here with
    /// an active ripcord and no parent (<c>m_pshipParent == NULL</c>,
    /// <c>shipIGC.cpp:271</c> — the same not-boarded gate P111's request
    /// handler uses, <c>ParentShip is null</c>), the anchor object is resolved by
    /// identity (<see cref="ResolveRipcordAnchor"/>) and its legality is the
    /// SITE's <see cref="IIgcSite.ContinueRipcord"/> answer (P754b:
    /// <see cref="RipcordRules.AnchorStillLegal"/>, <c>FedSrv.CPP:13379-13437</c>).
    /// A legal anchor ticks the countdown down by <c>dt</c>, and <b>P113</b>: when
    /// the countdown reaches zero the ripper is handed to the host's
    /// <c>UseRipcord</c>, which performs the relocation inline (P754b — native's
    /// always-true consume, <c>shipIGC.cpp:285-289</c>); a site that answers false
    /// defers it, recorded in <see cref="DrainRipcordTeleports"/>, drained by
    /// Server.PerformRipcordTeleport. The countdown is never clamped to zero and
    /// left active. An illegal anchor has its ripcord state cleared (mirroring
    /// P111's cancel path) and is recorded in
    /// <see cref="DrainRipcordAborts"/> so the host broadcasts
    /// <c>RIPCORD_ABORTED(187)</c> sector-wide. A boarded ship's ripcord is
    /// simply not ticked (frozen), exactly as the native <c>m_pshipParent ==
    /// NULL</c> gate freezes it — no abort on boarding.
    /// </summary>
    internal void ResolveRipcordCountdowns(float dt)
    {
        foreach (var model in _models.ToArray())
        {
            if (model is not Ship ship) continue;
            if (!ship.IsRipcordActive || ship.ParentShip is not null) continue;

            var anchor = ship.RipcordModel!.Value;
            var site = ship.Mission!.Site;
            // P754b — the anchor's legality IS the native ContinueRipcord
            // callback (FedSrv.CPP:13379-13437): flag carrier, side/alliance
            // mismatch, vanished/docked anchor and the LtRipcord hull rule all
            // live in that site method now (RipcordRules.AnchorStillLegal, used
            // by ServerHost.ContinueRipcord). Resolve the anchor object by
            // identity first (ResolveRipcordAnchor), then let the site decide —
            // no second, sector-side copy of the rule.
            if (ResolveRipcordAnchor(ship, anchor) is { } modelAnchor &&
                site.ContinueRipcord(ship, modelAnchor))
            {
                ship.AdvanceRipcordCountdown(dt);
                // P113 — the real trigger: the countdown reached zero with a
                // still-legal anchor. Hand the ripper to the host's
                // UseRipcord (Server.PerformRipcordTeleport), which relocates
                // it (station/ship anchor), pays the energy cost, broadcasts
                // RIPCORD_ABORTED on energy-insufficiency, and clears the
                // ripcord state — the attempt is consumed either way (the
                // native UseRipcord always returns true), never retried.
                if (ship.RipcordTimeLeft <= 0.0f)
                {
                    // shipIGC.cpp:285-289: false leaves an active zero countdown
                    // for a later retry; true consumes it. Queued hosts drain it.
                    bool used = site.UseRipcord(ship, modelAnchor);
                    if (used) ship.SetRipcordModel(null);
                    else
                    {
                        // Native false-use path explicitly clamps zero, even if
                        // the callback reset the countdown (shipIGC.cpp:289).
                        ship.AdvanceRipcordCountdown(ship.RipcordTimeLeft);
                        if (site.QueueRipcordUse && !_pendingRipcordTeleports.Contains(ship))
                            _pendingRipcordTeleports.Add(ship);
                    }
                }
            }
            else
            {
                ship.SetRipcordModel(null);
                ship.StateM &= ~0x1000; // droneRipMaskIGC, FedSrv.CPP:13427
                _pendingRipcordAborts.Add(ship);
            }
        }
    }

    /// <summary>P113 — return this tick's zero-crossing rippers (ships whose
    /// countdown reached zero with a still-legal anchor) and clear the
    /// accumulation. Called by the host right after <c>Mission.Update</c> so
    /// the teleport (Server.PerformRipcordTeleport) rides the sim thread
    /// exactly like the P112 abort broadcast.</summary>
    public IReadOnlyList<Ship> DrainRipcordTeleports()
    {
        var taken = _pendingRipcordTeleports.ToArray();
        _pendingRipcordTeleports.Clear();
        return taken;
    }

    /// <summary>P115 — return this tick's rescue pairs (detected by
    /// <see cref="ResolveRescueEvents"/>: friendly same-side
    /// lifepod/rescue-capable pairs within proximity) and clear the
    /// accumulation. Called by the host right after <c>Mission.Update</c> so
    /// the rescue (Server.PerformRescue) rides the sim thread exactly like the
    /// ripcord teleport drain.</summary>
    public IReadOnlyList<(Ship Rescuee, Ship Rescuer)> DrainRescues()
    {
        var taken = _pendingRescues.ToArray();
        _pendingRescues.Clear();
        return taken;
    }

    /// <summary>P301 — return this tick's station-side pod rescues (detected by
    /// <see cref="ResolveStationRescueEvents"/>: a drifting lifepod within
    /// proximity of a same-side station whose type carries <c>c_sabmRescue</c>)
    /// and clear the accumulation. Called by the host right after
    /// <c>Mission.Update</c> so the rescue (Server.PerformStationRescue) rides
    /// the sim thread exactly like the ripcord teleport and ship-rescue drains.</summary>
    public IReadOnlyList<(Ship Rescuee, Station? Station)> DrainStationRescues()
    {
        var taken = _pendingStationRescues.ToArray();
        _pendingStationRescues.Clear();
        return taken;
    }

    /// <summary>
    /// P115 — the per-tick rescue proximity pass, called from <see cref="Update"/>
    /// alongside the other per-tick passes. The port's stand-in for the native
    /// ship-vs-ship collision branch of <c>CshipIGC::HandleCollision</c>
    /// (<c>shipIGC.cpp:1022-1058</c>): this port has no ship-vs-ship physics
    /// collision system, and this pass deliberately does not build one — it is
    /// the same point-in-sphere proximity-sweep convention
    /// <see cref="ResolveDockCollisions"/> establishes for docking (the
    /// <c>// Point-in-sphere</c> simplification, <c>Sector.cs:1269</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Pair trigger</b> — for every distinct same-side pair of ships
    /// in this sector, if exactly one carries <c>c_habmLifepod</c> and the
    /// other <c>c_habmRescue</c> (<c>DataHullTypeIGC.HabmLifepod</c>/<c>HabmRescue</c>,
    /// the native capability bits <c>igc.h:726-727</c>), and the two are within
    /// proximity, the pair is queued for the host. This reproduces the native's
    /// symmetric rescue branch verbatim (<c>shipIGC.cpp:1027-1035</c>): whether
    /// the pair's lifepod is the loop's outer or inner ship does not matter —
    /// the other ship (if rescue-capable) rescues it, direction-free. Exactly
    /// one side of the pair must be the lifepod; a pair where both (or neither)
    /// are lifepods, or where the partner lacks <c>c_habmRescue</c>, is not a
    /// rescue pair.</para>
    /// <para><b>Friendly gate</b> — same-side only (<c>bFriendly =
    /// GetSide() == pModel-&gt;GetSide()</c>, <c>shipIGC.cpp:1023-1026</c>);
    /// this port has no alliance/AlliedSides concept, so the allied leg
    /// collapses unconditionally (the same simplification P112's
    /// ripcord rule (<see cref="RipcordRules.AnchorStillLegal"/>) cites for its bAllowAlliedRip
    /// drop). Opposing sides can never rescue each other.</para>
    /// <para><b>Proximity</b> — sum of the two ships' <c>0.5 × HullType.Length</c>
    /// sphere radii (the port's established ship-extent convention, the same
    /// P113/P114 use), mirroring how the dock/mining passes measure
    /// proximity as <c>objectRadius + shipRadius</c> and the native's
    /// ship-ship collision would use <c>radius_a + radius_b</c>.</para>
    /// <para><b>Latches</b> — a ship that is dead, unplaced, or already docked
    /// is excluded (a docked lifepod is not adrift to be rescued), and each
    /// ship participates in at most one rescue per tick (a successful rescue
    /// docks the pod and clears the <c>StationId</c> latch for subsequent
    /// ticks, mirroring how the dock pass's latch prevents re-fire).</para>
    /// </remarks>
    private void ResolveRescueEvents()
    {
        if (_models.Count < 2) return;               // nothing to pair

        // Candidate ships: flying (not docked), placed, alive, sided — the
        // same eligibility the dock pass applies before its own gates.
        var ships = new List<Ship>();
        foreach (var model in _models)
            if (model is Ship s && s.Mission is not null && s.SideId is not null
                && !s.Dead && s.StationId is null)
                ships.Add(s);
        if (ships.Count < 2) return;

        // P667 — parallel detection path. The expensive part is the read-only
        // O(n²) per-pair qualification (side / lifepod↔rescue capability /
        // proximity). Qualifying (i,j) pairs are collected in parallel, sorted
        // back into the serial scan order, then the one-rescue-per-ship latch
        // (which decides WHICH pair wins and mutates _pendingRescues) runs serially
        // in that exact order — so the result is byte-identical to the serial loop
        // below for any thread count. Ships' positions are not mutated during
        // qualification, so the concurrent reads are safe.
        if (ParallelSectorSim.Enabled && ParallelSectorSim.ThreadCount > 1)
        {
            ResolveRescueEventsParallel(ships);
            return;
        }

        var done = new HashSet<Ship>();              // one rescue per ship per tick
        for (int i = 0; i < ships.Count; i++)
        {
            var a = ships[i];
            if (done.Contains(a)) continue;
            for (int j = i + 1; j < ships.Count; j++)
            {
                var b = ships[j];
                if (done.Contains(b)) continue;

                // Friendly gate (shipIGC.cpp:1023-1026): same side only.
                if (a.SideId != b.SideId) continue;

                // Symmetric capability match (shipIGC.cpp:1027-1035): exactly
                // one is the lifepod, the other is rescue-capable.
                Ship rescuee, rescuer;
                if (a.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                    && b.HullType.HasCapability(DataHullTypeIGC.HabmRescue))
                {
                    rescuee = a; rescuer = b;
                }
                else if (b.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                    && a.HullType.HasCapability(DataHullTypeIGC.HabmRescue))
                {
                    rescuee = b; rescuer = a;
                }
                else continue;

                // Proximity: sum of the two ships' 0.5×Length sphere radii
                // (the established extent convention, P113/P114 precedent).
                float ra = 0.5f * a.HullType.Length;
                float rb = 0.5f * b.HullType.Length;
                float threshold = ra + rb;
                float dx = a.Position.X - b.Position.X;
                float dy = a.Position.Y - b.Position.Y;
                float dz = a.Position.Z - b.Position.Z;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;

                // The accept/refuse decision is the SITE's (native
                // FedSrvSiteBase::RescueShipEvent, FedSrv.CPP:13033-13076: the
                // rescue is only legal when a friendly restart station exists).
                // P754b: the serial path is gated too — P754 gated only the
                // parallel latch, so the callback was bypassed on the default
                // (non-parallel) path.
                if (Mission?.Site.RescueShipEvent(rescuee, rescuer) ?? true)
                {
                    _pendingRescues.Add((rescuee, rescuer));
                    done.Add(a);
                    done.Add(b);
                }
            }
        }
    }

    /// <summary>
    /// P667 — the parallel form of <see cref="ResolveRescueEvents"/>' detection
    /// phase. Qualifies every (i,j) ship pair (read-only) on up to
    /// <see cref="ParallelSectorSim.ThreadCount"/> workers, collects the
    /// qualifying pairs, sorts them back into the exact serial scan order
    /// (ascending i, ascending j), then applies the one-rescue-per-ship latch
    /// and <c>_pendingRescues</c> accumulation serially in that order. The
    /// accepted rescue set — and the order they land in <c>_pendingRescues</c> —
    /// is therefore identical to the serial loop for any thread count, satisfying
    /// the P667 determinism gate. Ship positions are never mutated during
    /// qualification (a rescue only latches and drains later), so the concurrent
    /// reads are safe.
    /// </summary>
    private void ResolveRescueEventsParallel(List<Ship> ships)
    {
        int n = ships.Count;
        var qualifying = new System.Collections.Concurrent.ConcurrentBag<(int I, int J)>();
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ParallelSectorSim.ThreadCount) }, i =>
        {
            var a = ships[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = ships[j];
                if (a.SideId != b.SideId) continue;              // friendly gate (shipIGC.cpp:1023-1026)

                // Symmetric capability match (shipIGC.cpp:1027-1035).
                if (a.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                    && b.HullType.HasCapability(DataHullTypeIGC.HabmRescue))
                { }
                else if (b.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                    && a.HullType.HasCapability(DataHullTypeIGC.HabmRescue))
                { }
                else continue;

                float ra = 0.5f * a.HullType.Length;
                float rb = 0.5f * b.HullType.Length;
                float threshold = ra + rb;
                float dx = a.Position.X - b.Position.X;
                float dy = a.Position.Y - b.Position.Y;
                float dz = a.Position.Z - b.Position.Z;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;

                qualifying.Add((i, j));
            }
        });

        // Serial latch in the identical (i,j) scan order the serial loop uses.
        var done = new HashSet<Ship>();
        foreach (var (i, j) in qualifying.OrderBy(p => p.I).ThenBy(p => p.J))
        {
            var a = ships[i];
            var b = ships[j];
            if (done.Contains(a) || done.Contains(b)) continue;

            Ship rescuee, rescuer;
            if (a.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                && b.HullType.HasCapability(DataHullTypeIGC.HabmRescue))
            {
                rescuee = a; rescuer = b;
            }
            else
            {
                rescuee = b; rescuer = a;
            }
            if (Mission?.Site.RescueShipEvent(rescuee, rescuer) ?? true)
            {
                _pendingRescues.Add((rescuee, rescuer));
                done.Add(a);
                done.Add(b);
            }
        }
    }

    /// <summary>
    /// P301 — the per-tick station-side pod-rescue pass, called from
    /// <see cref="Update"/> alongside the other per-tick passes. The port's
    /// stand-in for the native station-rescue branch of
    /// <c>CshipIGC::HandleCollision</c> (<c>shipIGC.cpp:862-867</c>): a
    /// <c>c_habmLifepod</c> ship reaching a friendly station whose type carries
    /// <c>c_sabmRescue</c> is rescued — the pod is docked at that station
    /// (which cancels the P279 oxygen timer). This is the <b>station</b> half
    /// of pod rescue, complementary to the P115 ship-ship pass
    /// (<see cref="ResolveRescueEvents"/>); together they reproduce native's
    /// two rescue triggers. It is detected here, accumulated into
    /// <see cref="_pendingStationRescues"/>, drained by
    /// <see cref="DrainStationRescues"/> and performed by the host
    /// (<c>Server.PerformStationRescue</c>) — the Core side never sends wire.
    /// </summary>
    /// <remarks>
    /// <para><b>Trigger</b> — native <c>shipIGC.cpp:862-867</c>: a
    /// <c>c_habmLifepod</c> hull within collision of a station whose type
    /// <c>HasCapability(c_sabmRescue)</c> and whose side is the pod's own or an
    /// ally's → <c>RescueShipEvent(pod, NULL)</c>. This port has no
    /// ship-vs-object physics collision system, so the pass uses the same
    /// point-in-sphere proximity-sweep convention the dock/rescue passes
    /// establish (<c>objectRadius + shipRadius</c>, cf.
    /// <c>ResolveDockCollisions</c> at <c>Sector.cs:1269</c>).</para>
    /// <para>The native rescue branch is an else-if after the hull-class
    /// landing-capability branch (shipIGC.cpp:727-729,861-866). A station that
    /// offers the ship's landing class requires ordinary bay entry instead.</para>
    /// <para><b>Rescue bit only</b> — the remaining station gate tests
    /// <c>c_sabmRescue</c> (0x2000) alone, faithful to native: the shipIGC.cpp:862
    /// branch never tests <c>c_sabmRescueAny</c> (0x4000), which native reserves
    /// &quot;for pods&quot; and only the client-side target classifiers and the
    /// expendable/probe twin actually consume.</para>
    /// <para><b>Friendly gate</b> — native requires same-side or allied
    /// (<c>shipIGC.cpp:865</c>); this port has no alliance/AlliedSides concept,
    /// so the allied leg collapses to same-side (the same simplification the
    /// P115 ship-rescue pass and every AlliedSides/bAllowAlliedRip gate in this
    /// arc apply). An opposing-side pod can never be rescued at an enemy
    /// station.</para>
    /// <para><b>Proximity</b> — sum of the pod's <c>0.5 × HullType.Length</c>
    /// sphere radius (the port's ship-extent convention, P113/P114 precedent)
    /// and the station's decoded collision <c>Radius</c> (<c>Station.Radius</c> =
    /// <c>DataStationTypeIGC.Radius</c>, which native assigns the station's
    /// collision sphere from, <c>stationIGC.cpp:249</c>) — the same
    /// <c>objectRadius + shipRadius</c> measure the dock/mining passes use.</para>
    /// <para><b>Latches</b> — a dead, unplaced, unsided, missionless or already
    /// docked ship is excluded (a docked pod is not adrift to be rescued), and
    /// a station that is itself destroyed is excluded. The host's placement
    /// (docking) clears the <c>StationId</c> latch for subsequent ticks; until
    /// then the pair can be re-detected, which is harmless (the host re-docks
    /// the already-docked pod idempotently).</para>
    /// </remarks>
    // P780 — reusable "one rescue per ship per tick" latch for
    // ResolveStationRescueEvents (was a new HashSet<Ship> per tick per sector
    // with stations — Domingo's standing no-per-tick-allocation rule; cleared
    // at entry, behaviour identical).
    private readonly HashSet<Ship> _stationRescueDone = [];

    private void ResolveStationRescueEvents()
    {
        if (_stationRuntimes.Count == 0) return;

        var done = _stationRescueDone;
        done.Clear();                             // one rescue per ship per tick
        foreach (var station in _stationRuntimes)
        {
            if (station.Dead) continue;
            // The native gate (shipIGC.cpp:862-867): the station type must
            // carry c_sabmRescue; RescueAny is not consulted for stations.
            if (!station.Type.HasCapability(DataStationTypeIGC.SabmRescue)) continue;

            foreach (var model in _models)
            {
                if (model is not Ship s) continue;
                if (done.Contains(s)) continue;
                // Flying, placed, alive, sided, in-mission — same eligibility
                // the P115 rescue pass applies before its own gates.
                if (s.Mission is null || s.SideId is null || s.Dead || s.StationId is not null) continue;
                // Lifepod hull (the rescued side of the pair).
                if (!s.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)) continue;
                // Rescue is the ELSE of the hull-class landing capability gate
                // (Igc/shipIGC.cpp:727-729,861-866), not a radius shortcut past InGarage.
                short landing = s.HullType.HasCapability(DataHullTypeIGC.HabmFighter)
                    ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand;
                if (station.Type.HasCapability(landing)) continue;
                // Friendly gate (shipIGC.cpp:865): same side only (allied leg
                // collapses unconditionally — no alliance concept in this port).
                if (s.SideId != station.SideId) continue;

                // Proximity: pod sphere radius + station collision radius.
                float shipR = 0.5f * s.HullType.Length;
                float dx = s.Position.X - station.Center.X;
                float dy = s.Position.Y - station.Center.Y;
                float dz = s.Position.Z - station.Center.Z;
                float threshold = shipR + station.Radius;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;

                if (Mission?.Site.RescueShipEvent(s, null) ?? true)
                {
                    _pendingStationRescues.Add((s, station));
                    done.Add(s);
                }
            }
        }
    }

    /// <summary>
    /// P780 — the per-tick rescue-PROBE pod-rescue pass, called from
    /// <see cref="Update"/> right after <see cref="ResolveStationRescueEvents"/>
    /// inside the same <c>sim.rescue</c> profiling bucket. The port of the
    /// native OT_probe collision branch of <c>CshipIGC::HandleCollision</c>
    /// (<c>Igc/shipIGC.cpp:897-909</c>), which the port was missing: a
    /// <c>c_habmLifepod</c> ship that collides with a probe whose type carries
    /// a rescue capability bit is rescued — <c>RescueShipEvent(pod, NULL)</c>.
    /// Detected here, accumulated into <see cref="_pendingStationRescues"/>
    /// (with a <c>null</c> <c>Station</c>, meaning "probe rescue — find the
    /// destination"), drained by <see cref="DrainStationRescues"/> and
    /// performed by the host's probe branch of <c>Server.PerformStationRescue</c>
    /// — the Core side never sends wire.
    /// </summary>
    /// <remarks>
    /// <para><b>Native trigger</b> — verbatim from <c>shipIGC.cpp:899-907</c>
    /// (reached directly for OT_probe, or by fallthrough from station /
    /// asteroid / building effect — the port models the direct probe contact;
    /// the fallthrough legs are the other passes' business):
    /// <c>if (m_myHullType.HasCapability(c_habmLifepod)) { eabm =
    /// probe-&gt;GetProbeType()-&gt;GetCapabilities(); if ((eabm &amp; c_eabmRescueAny) ||
    /// ((eabm &amp; c_eabmRescue) &amp;&amp; (GetSide() == pModel-&gt;GetSide() ||
    /// IsideIGC::AlliedSides(GetSide(), pModel-&gt;GetSide()))))
    /// if (GetIgcSite()-&gt;RescueShipEvent(this, NULL)) break; }</c></para>
    /// <para><b>Bit gate</b> — <c>c_eabmRescue</c> (0x2000) rescues a pod whose
    /// side is the probe's own or an ALLIED side (native uses
    /// <c>AlliedSides</c>, shipIGC.cpp:905, so this pass uses the real
    /// <see cref="IsAlliedSide"/> — same side included; the stale "no alliance
    /// concept" remarks on the two older rescue passes do not apply here);
    /// <c>c_eabmRescueAny</c> (0x4000) rescues ANY pod, including an enemy's.
    /// A probe with a null side only counts for RescueAny (native compares
    /// sides, and a sideless probe can never equal or ally with one).</para>
    /// <para><b>No activation delay, not consumed</b> — native's probe
    /// <c>m_time0</c> only gates ripcord use (<c>probeigc.h:248-254</c>), so a
    /// probe rescues from the moment it is created, and rescuing changes
    /// nothing on the probe: it keeps its hit points and lifespan and can
    /// rescue again (each contact is independent).</para>
    /// <para><b>Shape</b> — native loads the probe as
    /// <c>c_mtDamagable | c_mtHitable | c_mtStatic</c> with
    /// <c>SetRadius(dataProbeType-&gt;radius)</c> (<c>Igc/probeigc.cpp:112-118</c>)
    /// and hits it continuously over the tick (<c>HitTest::Collide</c>), using
    /// the probe's <c>.cvh</c> hull when the art ships one, else a sphere
    /// (<c>_Utility/HitTest.cpp:827-863</c>). The port has no ship-vs-probe
    /// collision system, so this pass approximates the probe with its decoded
    /// <c>ProbeType.Radius</c> sphere — the same radius the combat pass already
    /// uses as the probe's collision sphere — and sweeps
    /// the POD over the chunk like <see cref="ResolveTreasurePickups"/> does
    /// (closest point on the segment <c>Position − CollisionVelocity·dt →
    /// Position</c>; the probe is static): real rescue probes have radius 6,
    /// pods length 4–5 and max speed 50–60 (the Observer lifepod 160), so a
    /// point sample would mostly work but a fast ejected pod could tunnel
    /// past in one 50 ms chunk, and the segment test costs a few flops.</para>
    /// <para><b>Site gate</b> — like both older rescue passes, the pass ASKS
    /// the site first: <c>Mission?.Site.RescueShipEvent(s, null) ?? true</c>
    /// (production: <c>ServerHost.RescueShipEvent</c> →
    /// <c>RescueDestination</c> = <c>FindFriendlyStation(pod) is not null</c>,
    /// P754b). A refusal mirrors native's fallthrough to the generic
    /// probe-collision response (a bounce, shipIGC.cpp:911-1010 — which the
    /// port does not model for any hull), so the pod simply keeps flying.</para>
    /// <para><b>Latches</b> — eligibility matches the station pass exactly
    /// (missioned, sided, alive, flying, lifepod hull), a pod already queued
    /// this tick in <see cref="_pendingRescues"/> or
    /// <see cref="_pendingStationRescues"/> is skipped (no double dock), and
    /// one rescue per pod per tick (<c>break</c>, native shipIGC.cpp:907).</para>
    /// <para><b>Cost model</b> — the pass is free unless the sector actually
    /// holds a rescue probe: <see cref="_rescueProbeCount"/> (maintained by
    /// <see cref="Add"/>/<see cref="Remove"/>) gives an O(1) early-out for the
    /// ordinary sector, and the gather reuses the
    /// <see cref="_rescueProbes"/> buffer (cleared, never re-allocated) so a
    /// pass that runs allocates nothing. With K rescue probes and S ships the
    /// body is O(S + S·K) with a linear scan of the small pending lists per
    /// ship — all no-allocation.</para>
    /// </remarks>
    private void ResolveProbeRescueEvents(float dt)
    {
        if (_rescueProbeCount == 0) return;         // O(1) early-out: no rescue probe in this sector

        ProbeRescuePass(dt);
    }

    /// <summary>P780 test seam (InternalsVisibleTo): runs the probe-rescue
    /// gather+contact loop alone, bypassing the <see cref="_rescueProbeCount"/>
    /// early-out, so the allocation test can measure THIS pass's garbage in
    /// isolation rather than the whole tick's (other passes allocate).</summary>
    internal void ProbeRescuePass(float dt)
    {
        _rescueProbes.Clear();                      // reusable buffer — no per-tick allocation
        foreach (var m in _models)
            if (m is Probe p && !p.Dead &&
                (p.ProbeType.EabmCapabilities & (DataProbeTypeIGC.EabmRescue | DataProbeTypeIGC.EabmRescueAny)) != 0)
                _rescueProbes.Add(p);
        if (_rescueProbes.Count == 0) return;       // all rescue probes dead but not yet removed

        foreach (var model in _models)
        {
            if (model is not Ship s) continue;
            // Same eligibility as the station pass (shipIGC.cpp:899's
            // c_habmLifepod conjunct + the flying/alive/sided/missioned gates).
            if (s.Mission is null || s.SideId is null || s.Dead || s.StationId is not null) continue;
            if (!s.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)) continue;
            // A pod already queued this tick (ship-pickup or station pass) must
            // not be docked twice — scan the small pending lists linearly.
            bool queued = false;
            foreach (var pr in _pendingRescues)
                if (ReferenceEquals(pr.Rescuee, s)) { queued = true; break; }
            if (!queued)
                foreach (var sr in _pendingStationRescues)
                    if (ReferenceEquals(sr.Rescuee, s)) { queued = true; break; }
            if (queued) continue;

            float shipR = 0.5f * s.HullType.Length;
            foreach (var probe in _rescueProbes)
            {
                short eabm = probe.ProbeType.EabmCapabilities;
                // Bit gate (shipIGC.cpp:903-905): RescueAny takes any pod;
                // Rescue takes the probe's own or an ALLIED side (AlliedSides
                // includes same side, hence IsAlliedSide). A sideless probe
                // only counts for RescueAny.
                if ((eabm & DataProbeTypeIGC.EabmRescueAny) == 0 &&
                    !((eabm & DataProbeTypeIGC.EabmRescue) != 0 &&
                      probe.SideId is { } ps && IsAlliedSide(s.SideId.Value, ps)))
                    continue;

                // Swept contact: pod sphere (0.5 × Length) vs the static
                // probe's ProbeType.Radius sphere, over the chunk — the same
                // segment test ResolveTreasurePickups uses,
                // the port's stand-in for native's continuous HitTest::Collide.
                var rel = s.Position - probe.Position;
                var start = rel - s.CollisionVelocity * dt;
                var travel = s.CollisionVelocity * dt;
                float length2 = travel.LengthSquared();
                float fraction = length2 == 0 ? 0 : Math.Clamp(-(start * travel) / length2, 0, 1);
                float threshold = shipR + probe.ProbeType.Radius;
                if ((start + travel * fraction).LengthSquared() > threshold * threshold) continue;

                if (Mission?.Site.RescueShipEvent(s, null) ?? true)
                {
                    Console.WriteLine($"[rescue] lifepod ship {s.Id} touched rescue probe {probe.Id} (eabm 0x{eabm:X4}, probe side {probe.SideId ?? (short)-1}) -> queued NULL-rescuer rescue");
                    _pendingStationRescues.Add((s, null));
                    break;                              // one rescue per pod per tick (native break, shipIGC.cpp:907)
                }
                // Site refused: native falls through to the generic
                // probe-collision bounce (shipIGC.cpp:911-1010), which the port
                // does not model — the pod simply keeps flying.
            }
        }
    }

    /// <summary>
    /// P116 — the per-tick carrier-landing pass, called from <see cref="Update"/>
    /// alongside the other per-tick passes. The port's stand-in for the carrier
    /// branch of <c>CshipIGC::HandleCollision</c>'s ship-vs-ship block
    /// (<c>shipIGC.cpp:1022-1053</c>): this port has no ship-vs-ship physics
    /// collision system, and this pass deliberately does not build one — but
    /// unlike the dock/rescue sphere-proximity sweeps, it does <b>not</b> fall
    /// back to a sphere shortcut either. It runs the real geometric predicate
    /// <see cref="Ship.InGarage"/> (the carrier's reconstructed landing-bay
    /// half-spaces, served from the cached <see cref="LandingBays"/> geometry) —
    /// the whole point of this slice. What happens to a detected pair is the
    /// SITE's business: a mission-bound sector hands it to
    /// <see cref="IIgcSite.LandOnCarrierEvent"/> — the production host performs
    /// the native landing there (P754b: resupply, launch-slot repositioning,
    /// <c>RELAUNCH_SHIP</c>, <c>FedSrv.CPP:12814-13031</c>) — while a mission-less
    /// sector merely records the pair for the detection fixtures.
    /// </summary>
    /// <remarks>
    /// <para><b>Pair trigger</b> — for every distinct same-side pair of ships in
    /// this sector, if one carries <c>c_habmCarrier</c> and the other
    /// <c>c_habmLandOnCarrier</c> (<c>DataHullTypeIGC.HabmCarrier</c>=0x400
    /// igc.h:736, <c>HabmLandOnCarrier</c>=0x10 igc.h:730), and the lander is
    /// geometrically inside the carrier's bay, the pair is recorded. This is the
    /// native's symmetric carrier branch verbatim (<c>shipIGC.cpp:1038-1048</c>):
    /// which ship of the pair is the carrier does not matter — exactly one must
    /// carry <c>c_habmCarrier</c> and the other <c>c_habmLandOnCarrier</c>;
    /// a pair where both (or neither) is the carrier, or where the partner lacks
    /// <c>c_habmLandOnCarrier</c>, is not a landing pair.</para>
    /// <para><b>Friendly gate</b> — same-side only (<c>bFriendly =
    /// GetSide() == pModel-&gt;GetSide()</c>, <c>shipIGC.cpp:1023-1026</c>), the
    /// identical gate P115's <see cref="ResolveRescueEvents"/> applies; opposing
    /// sides can never land on each other.</para>
    /// <para><b>Real geometry</b> — the carrier's bay is resolved through
    /// <see cref="LandingBays"/> (a <see cref="HullLandingBayCache"/> keyed by the
    /// carrier's hull <c>ModelName</c>, reconstructed from the real <c>.cvh</c>
    /// once and cached), then <see cref="Ship.InGarage(Ship, float, HullLandingBayGeometry)"/>
    /// tests the lander's swept-sphere extreme against every bay plane. When no
    /// cache is attached, or the carrier hull has no resolvable bay, the pass is
    /// inert (no detection) — matching the native <c>HitTest::Load</c>-failed /
    /// no-land-slots no-op.</para>
    /// <para><b>No latch</b> — detection only. Native <c>LandOnCarrierEvent</c>
    /// (<c>FedSrv.CPP:12814-13031</c>) is a single atomic function — detect,
    /// resupply, reposition, broadcast <c>RELAUNCH_SHIP</c> — with no persistent
    /// "docked at carrier" state, so a ship genuinely <c>InGarage</c> across
    /// consecutive ticks re-fires each tick. That is correct for this slice and
    /// tested (case 5); the relaunch (P117) ends the condition by moving the
    /// ship out of the bay. No cooldown/latch is modelled.</para>
    /// </remarks>
    private void ResolveCarrierLandings()
    {
        if (_models.Count < 2) return;                 // nothing to pair
        if (LandingBays is null) return;               // no resolver attached → detection unavailable

        // Candidate ships: flying (not docked), alive, sided — the same
        // eligibility the dock/mining passes apply before their own gates
        // (Sector.cs ResolveDockCollisions/ResolveMining). Unlike the rescue
        // pass there is no Mission requirement here: this pass is detection-only
        // (InGarage reads carrier/lander kinematics, never Mission), so a ship
        // placed in a bare Sector — the real-corpus unit-test style P114 used —
        // is fully eligible.
        var ships = new List<Ship>();
        foreach (var model in _models)
            if (model is Ship s && s.SideId is not null && !s.Dead && s.StationId is null)
                ships.Add(s);
        if (ships.Count < 2) return;

        for (int i = 0; i < ships.Count; i++)
        {
            var a = ships[i];
            for (int j = i + 1; j < ships.Count; j++)
            {
                var b = ships[j];

                // Friendly gate (shipIGC.cpp:1023-1026): same side only.
                if (a.SideId != b.SideId) continue;

                // Symmetric capability match (shipIGC.cpp:1038-1048): exactly
                // one is the carrier, the other is a land-on-carrier ship.
                Ship carrier, lander;
                if (a.HullType.HasCapability(DataHullTypeIGC.HabmCarrier)
                    && b.HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier))
                {
                    carrier = a; lander = b;
                }
                else if (b.HullType.HasCapability(DataHullTypeIGC.HabmCarrier)
                    && a.HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier))
                {
                    carrier = b; lander = a;
                }
                else continue;

                // Native reaches InGarage only inside HandleCollision's OT_ship
                // branch (Igc/shipIGC.cpp:1023-1055). Bay half-spaces can be open
                // beyond the hull; testing every sector pair without the collision
                // broadphase otherwise teleports distant strikes back to a carrier.
                float contactRadius = (carrier.HullType.Length + lander.HullType.Length) * .5f;
                if ((lander.Position - carrier.Position).LengthSquared() > contactRadius * contactRadius)
                    continue;

                // Real geometric test (the point of this slice): the lander's
                // swept sphere against the carrier's reconstructed bay planes.
                // tCollision=0 — instantaneous per-tick sweep, the same value
                // the port's other passes use for their point-in-space tests.
                var bay = LandingBays.Bay(carrier.HullType);
                if (bay is null) continue;
                if (!carrier.InGarage(lander, 0f, bay)) continue;

                // Native: `... && igcsite->LandOnCarrierEvent(...)` then
                // `break` (shipIGC.cpp:1053-1057) — true means the site handled
                // the landing (FedSrv restores the expendables, repositions the
                // lander and broadcasts RELAUNCH_SHIP, FedSrv.CPP:12814-13031).
                // The production host does exactly that from its site method
                // (ServerHost.LandOnCarrierEvent, P754b), so the sector queues
                // nothing for a mission-bound landing. A mission-less sector
                // (the P116 detection fixtures) keeps the detection queue.
                if (Mission?.Site is { } site)
                {
                    site.LandOnCarrierEvent(carrier, lander, 0f);
                    continue;
                }
                _pendingCarrierLandings.Add((carrier, lander));
            }
        }
    }

    /// <summary>P116 — return this tick's carrier-landing detections (found by
    /// <see cref="ResolveCarrierLandings"/>: friendly same-side carrier/lander
    /// pairs where the lander is geometrically InGarage) and clear the
    /// accumulation. P754b: production no longer drains this — the landing (and
    /// its resupply/relaunch/RELAUNCH_SHIP, FedSrv.CPP:12814-13031) is performed
    /// inside the site callback, so only a mission-less sector accumulates here;
    /// the mission-less detection fixtures drain it directly.</summary>
    public IReadOnlyList<(Ship Carrier, Ship Lander)> DrainCarrierLandings()
    {
        var taken = _pendingCarrierLandings.ToArray();
        _pendingCarrierLandings.Clear();
        return taken;
    }

    /// <summary>
    /// P123 — the per-tick treasure-pickup proximity pass, called from
    /// <see cref="Update"/> alongside the other per-tick passes. The port's
    /// stand-in for <c>CtreasureIGC::HandleCollision</c>'s ship branch
    /// (<c>treasureIGC.h:78-99</c>): this port has no ship-vs-object physics
    /// collision system, and this pass deliberately does not build one — it is
    /// the same point-in-sphere proximity-sweep convention
    /// <see cref="ResolveDockCollisions"/> establishes for docking (the
    /// <c>// Point-in-sphere</c> simplification, <c>Sector.cs:1269</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Qualifying-ship gate</b> — reproduces the native
    /// <c>HandleCollision</c> gate verbatim (<c>treasureIGC.h:83-98</c>):
    /// <c>GetPilotType() &gt;= c_ptPlayer</c> <em>and</em>
    /// <c>GetBaseHullType()-&gt;HasCapability(c_habmRescue)</c>. The pilot-type
    /// leg collapses to always-true in this port: every <see cref="Ship"/> is a
    /// player ship (the port models no non-player pilot type — <c>Ship.cs</c>
    /// has no pilot-type field, and the wire messages hard-code <c>c_ptPlayer</c>=10,
    /// ExportShip.cs:74-75), so <c>10 &gt;= 10</c> always holds. The operative
    /// gate is <c>HullType.HasCapability(DataHullTypeIGC.HabmRescue)</c>
    /// (bit 0x02, the <em>same</em> bit P114/P115 already decoded and used for
    /// <c>RescueShipEvent</c> — no new capability decode needed).</para>
    /// <para><b>Side-agnostic — confirmed.</b> The native gate has <em>no</em>
    /// side check at all (<c>treasureIGC.h:86-87</c> only tests pilot type and
    /// the rescue capability); a cash treasure itself carries no side
    /// (<c>treasureIGC.cpp:128-149</c> sets a side only for <c>c_tcFlag</c>).
    /// So <em>any</em> rescue-capable player ship on <em>any</em> side picks up
    /// a cash treasure — there is deliberately no same-side requirement here
    /// (unlike the dock/rescue passes). The only side involvement is on the
    /// credit side: the <b>picking ship's own side</b> is credited
    /// (<c>HitTreasureEvent</c> uses <c>pfsShip-&gt;GetSide()</c>,
    /// FedSrv.CPP:13589-13615), so the pass still requires the ship to be
    /// sided (and in a mission) so the host has a side to credit — a defensive
    /// guard that never trips for a real server ship.</para>
    /// <para><b>Cash + part this slice (P123 then P145).</b> The pass accumulates
    /// <c>c_tcCash</c> and <c>c_tcPart</c> treasures: for cash,
    /// <c>HitTreasureEvent</c> (<c>FedSrv.CPP:13589-13616</c>) credits the side and
    /// returns true; for parts it buffers an offer + sends <c>ACQUIRE_TREASURE</c>.
    /// Either way <c>KillTreasureEvent</c> then destroys the treasure
    /// (DESTROY_TREASURE, FedSrv.CPP:12493-12509) — a part treasure is destroyed
    /// on touch, before the player ever chooses a mount (see
    /// <c>Server.PerformTreasurePickup</c>). The other codes (<c>c_tcPowerup</c> /
    /// <c>c_tcDevelopment</c> / <c>c_tcFlag</c>) are still the separately-scoped
    /// follow-up: a treasure of one of those overlapped by a qualifying ship is
    /// <em>left in place</em> this slice (detected but not consumed), the honest
    /// "build the cheapest real slice" boundary. A loot counter is not needed
    /// here; a single treasure is consumed at most once because the pickup
    /// removes it from the sector.</para>
    /// <para><b>Proximity</b> — point-in-sphere with the treasure's fixed
    /// <see cref="Treasure.PickupRadius"/> (5.0, treasureIGC.cpp:193) plus the
    /// ship's <c>0.5 × HullType.Length</c> sphere radius — the same
    /// <c>objectRadius + shipRadius</c> measure the dock/mining/rescue passes
    /// use. A dead or docked ship is excluded (a docked ship is not in the
    /// flight sim), mirroring the rescue pass's latches.</para>
    /// </remarks>
    private void ResolveTreasurePickups(double timeUpdate, float dt)
    {
        if (_models.Count < 2) return;                 // nothing to pair
        var treasures = new List<Treasure>();
        foreach (var m in _models)
            if (m is Treasure t) treasures.Add(t);
        if (treasures.Count == 0) return;              // no treasure to pick up

        var done = new HashSet<Treasure>();            // one pickup per treasure per tick
        foreach (var model in _models)
        {
            if (model is not Ship ship) continue;
            if (ship.Dead || ship.StationId is not null) continue;   // must be flying
            if (ship.SideId is null || ship.Mission is null) continue; // need a side to credit
            // The native gate (treasureIGC.h:86-87): pilot type >= c_ptPlayer
            // (collapses to always-true here — see remarks) AND the rescue
            // capability. No side check (side-agnostic — see remarks).
            if (!ship.HullType.HasCapability(DataHullTypeIGC.HabmRescue)) continue;

            float shipR = 0.5f * ship.HullType.Length;
            foreach (var tr in treasures)
            {
                if (done.Contains(tr)) continue;
                // P145 — the shared cash/part gate: a rescue-capable ship
                // overlaps EITHER a c_tcCash treasure (credit the side) or a
                // c_tcPart treasure (buffer an offer + ACQUIRE_TREASURE).
                // P201 adds c_tcFlag, P412 c_tcPowerup. The flag-specific
                // refusal rules (already carrying a flag; own side's flag) are
                // NOT decided here: they are the native site rule inside
                // HitTreasureEvent (FedSrv.CPP:13562-13568), enforced by
                // ServerHost.HitTreasureEvent below (P754b); a refused pickup
                // returns false, is not queued, and the treasure stays.
                if (tr.Code is not (TreasureCode.Cash or TreasureCode.Part or TreasureCode.Flag or TreasureCode.Powerup)) continue;

                float threshold = Treasure.PickupRadius + shipR;
                // Native uses the average trajectory velocity for the collision sweep
                // (treasureIGC.cpp:239-259). Test the relative segment, including
                // a treasure crossing a ship between endpoints.
                var end = ship.Position - tr.Position;
                var start = ship.Position - ship.CollisionVelocity * dt - tr.PreviousPosition;
                var travel = end - start;
                float length2 = travel.LengthSquared();
                float fraction = length2 == 0 ? 0 : Math.Clamp(-(start * travel) / length2, 0, 1);
                if ((start + travel * fraction).LengthSquared() > threshold * threshold) continue;

                // The accept/refuse decision is the SITE's (native
                // FedSrvSiteBase::HitTreasureEvent, FedSrv.CPP:13558-13650): a
                // refused pickup returns false, nothing is queued and the treasure
                // stays (P754b moved the native flag gates into that callback).
                if (Mission?.Site.HitTreasureEvent(timeUpdate, ship, tr) ?? true)
                {
                    _pendingTreasurePickups.Add((ship, tr));
                    done.Add(tr);
                }
            }
        }
    }

    /// <summary>P123 — return this tick's treasure pickups (found by
    /// <see cref="ResolveTreasurePickups"/>: a rescue-capable player ship
    /// overlapping a c_tcCash treasure) and clear the accumulation. Called by
    /// the host right after <c>Mission.Update</c> so the credit + broadcasts
    /// (Server.PerformTreasurePickup) ride the sim thread exactly like the
    /// rescue/teleport drains.</summary>
    public IReadOnlyList<(Ship Ship, Treasure Treasure)> DrainTreasurePickups()
    {
        var taken = _pendingTreasurePickups.ToArray();
        _pendingTreasurePickups.Clear();
        return taken;
    }

    /// <summary>P164 — return this tick's interrupted building effects (found by
    /// <see cref="ResolveBuildingEffects"/>: an in-progress build whose Builder
    /// died mid-build) as the target asteroid's object id, and clear the
    /// accumulation. Called by the host right after <c>Mission.Update</c> so
    /// the BUILDINGEFFECT_DESTROYED(181) broadcast
    /// (Server.BroadcastBuildingEffectDestroyed) rides the sim thread exactly
    /// like the treasure/ripcord drains.</summary>
    public IReadOnlyList<short> DrainBuildingEffectInterruptions()
    {
        var taken = _pendingBuildingEffectInterruptions.ToArray();
        _pendingBuildingEffectInterruptions.Clear();
        return taken;
    }

    /// <summary>P294 — return this tick's newly-registered building effects
    /// (registered by <see cref=\"StartBuildingEffect\"/>: a Builder committed
    /// to a new construction) and clear the accumulation. Called by the host
    /// right after <c>Mission.Update</c> so the creation EXPORT(26)/OT_buildingEffect
    /// broadcast (Server.BroadcastBuildingEffectCreated) rides the sim thread
    /// exactly like the interruption drain above.</summary>
    public IReadOnlyList<BuildingEffect> DrainBuildingEffectCreations()
    {
        var taken = _pendingBuildingEffectCreations.ToArray();
        _pendingBuildingEffectCreations.Clear();
        return taken;
    }

    /// <summary>P112 — return this tick's ripcord aborts (ships whose anchor
    /// turned illegal mid-countdown) and clear the accumulation. Called by the
    /// host right after <c>Mission.Update</c> so the abort broadcast rides the
    /// sim thread exactly like P111's cancel path.</summary>
    public IReadOnlyList<Ship> DrainRipcordAborts()
    {
        var taken = _pendingRipcordAborts.ToArray();
        _pendingRipcordAborts.Clear();
        return taken;
    }

    /// <summary>Native ContinueRipcord's anchor resolution, FedSrv/FedSrv.CPP:13379-13437.
    /// The anchor is resolved by identity every tick: a flying carrier may warp
    /// during the countdown. The <b>legality</b> decision itself is the site's
    /// <see cref="IIgcSite.ContinueRipcord"/> (P754b moved it out of the sector;
    /// see <see cref="RipcordRules.AnchorStillLegal"/>), so this helper only
    /// looks the anchor object up and refreshes its cluster id.</summary>
    private static IModelIgc? ResolveRipcordAnchor(Ship ship, Ship.RipcordTarget anchor)
    {
        foreach (var cluster in ship.Mission!.Sectors)
        {
            IModelIgc? model = anchor.Ot switch
            {
                (short)IgcObjectType.Ship => cluster.Models.OfType<Ship>().FirstOrDefault(s => s.Id == anchor.Oid && s.Sector == cluster),
                (short)IgcObjectType.Probe => cluster.Models.OfType<Probe>().FirstOrDefault(p => p.Id == anchor.Oid),
                (short)IgcObjectType.Station => cluster.StationRuntimes.FirstOrDefault(s => s.StationId == anchor.Oid),
                _ => null
            };
            // Historical data-only station fixtures use the same marker as docking.
            if (model is null && anchor.Ot == (short)IgcObjectType.Station)
                foreach (var station in cluster.Stations)
                    if (station.StationId == anchor.Oid) { model = new DockStation(station); break; }
            if (model is null) continue;
            ship.SetRipcordModel(anchor with { ClusterId = cluster.Id });
            return model;
        }
        return null;
    }

    /// <summary>
    /// The warp-transit debounce window, in seconds — native's <b>player</b>
    /// re-arm (<c>CFSShip::m_warpState</c>, <c>fsship.cpp:1185-1191</c>:
    /// <c>m_timeNextWarp = g.timeNow + 2.0f</c> after the first accepted update
    /// following a jump). A ship that crosses an aleph cannot cross again until
    /// this much sim-time elapses, collapsing native's <c>warpNoUpdate →
    /// warpWaiting → warpReady</c> cycle into one threshold. Compared against
    /// <see cref="Ship.LastWarpTime"/>.
    /// <para>P776: this window is <b>not</b> what prevents re-entry — native's
    /// geometry does (the exit places the ship 5&nbsp;u in front of the
    /// destination disc, outside the aleph's cone; <c>fsship.cpp:129-132</c>,
    /// <see cref="AlephConeHitTest"/>). Native drones never leave
    /// <c>warpReady</c> (<c>fsship.cpp:79-82</c>) and have no cooldown at all;
    /// the NPC use of this constant is <b>port-only</b> and is left in place
    /// (removing it is out of P776's scope).</para>
    /// </summary>
    public const double WarpCooldownSeconds = 2.0;

    /// <summary>
    /// P591c — the P591b warp re-entry guard is a <b>non-native safety net</b>,
    /// not a faithful port of anything in native: native has the player re-arm
    /// timer (<c>fsship.cpp:1185-1191</c>), no cooldown at all for drones
    /// (<c>fsship.cpp:79-82</c>), and no arrival-warp memory — a native ship
    /// simply does not re-trigger because the exit geometry leaves it clear of
    /// the aleph's cone (<c>fsship.cpp:129-132</c>; <see cref="AlephConeHitTest"/>,
    /// P776). The guard was added in P591b to stop a real, measured bounce caused
    /// by a routing bug this port had; once that root cause is fixed (P591c) the
    /// guard should fire ≈0 times and acts purely as insurance. It is therefore
    /// <b>OPT-IN and OFF by default</b> (native needs no such rule): opt in via
    /// the server config flag <c>ServerConfig.WarpReEntryGuard</c> (a.k.a.
    /// <c>--warpReEntryGuard</c>) or by setting this static directly (the
    /// <b>test seam</b> that lets tests and the live proof switch the guard for
    /// demonstration). The guard's code and the <c>Ship.NumWarpReEntryBlocks</c>
    /// counter remain in place so opting in is a one-line switch and a regression
    /// that reintroduces re-entry still surfaces as a non-zero block counter.</summary>
    public static bool WarpReEntryGuardEnabled { get; set; } = false;

    /// <summary>Native default (missionigc.cpp:2777), retained for existing
    /// host callers. Simulation reads the loaded slot through EffectiveWarpBombDelay.</summary>
    public const double WarpBombDelaySeconds = 15.0;

    /// <summary>Loaded c_fcidWarpBombDelay, slot 32 (igc.h:98;
    /// FedSrv.CPP:12551). Standalone sectors use the native default.</summary>
    public double EffectiveWarpBombDelay => Mission?.Core.Constants.FloatConstants is { Length: > 32 } fc
        ? fc[32] : WarpBombDelaySeconds;


    /// <summary>
    /// The single source of truth for how much larger the <b>rendered</b>
    /// warp funnel is than the real wire <c>DataWarpIGC.Radius</c>. The shared
    /// funnel mesh has a unit rim (<c>WarpMeshFactory.DefaultRimRadius</c> = 1),
    /// so the rendered rim <b>radius</b> = <c>Radius × multiplier</c>. P237
    /// calibrated this against the ACTUAL rendered ship model (~190 world units
    /// — the single <c>bios_scout_upscaled.fbx</c>) to Domingo's "very large,
    /// many ships could cross at the same time" target: rim diameter 10 × 190 =
    /// 1900 ⇒ multiplier = 1900 / (2 × 100) = <c>9.5</c>, i.e.
    /// <c>WarpRenderer.VisualScaleMultiplier</c> = 9.5f. This constant is used
    /// <b>only</b> to scale the render mesh (see <c>WarpRenderer</c>); it is
    /// deliberately <b>not</b> applied to the real gameplay trigger thresholds,
    /// which use the warp's raw wire <c>Radius</c> (P244 decoupled the visual
    /// funnel size from the trigger so the aleph does not vanish while the
    /// player is still approaching its rim). P238 had widened the transit and
    /// warp-bomb triggers by this factor so the whole visible vortex was
    /// crossable; P244 reverts that — a ship or missile must reach the warp's
    /// real raw <c>Radius</c> (matching native's tight physics collision at
    /// <c>GetRadius()</c>, shipIGC.cpp:629-636 / missileigc.cpp:459-467), not
    /// the funnel's outer edge. The raw wire <c>Radius</c> in <c>DataWarpIGC</c>
    /// is never multiplied anywhere — only the render scale reads it.
    /// </summary>
    // P247 (2026-09-17): shrunk from 9.5f to 2.5f — a real, confirmed
    // correction to the P237 "~10x a ship" figure, not a cosmetic tweak.
    // P244 decoupled the real transit trigger from this constant (tightened
    // it to the raw warp.Radius, matching native's real physics collision,
    // shipIGC.cpp:629-636) but left this render-only scale at 9.5x — which
    // live flight-testing proved genuinely broken: Domingo flew to 436u
    // (well inside the 950u visible funnel rim) without transiting, because
    // the real trigger only fires at ~108u, under a quarter of that visible
    // approach — most of what looked crossable did nothing ("not sure if I
    // traveled or not"). Asked directly which permanent fix he wanted —
    // shrink the funnel to roughly match the real crossable size, or keep
    // the epic 9.5x scale and add a distinct bright "core" marker at the
    // true trigger point (matching native's own tutorial line, "a big
    // swirling vortex with a bright spot in the center", mission4.cpp:494)
    // — he chose the shrink. 2.5x was live-verified end to end at this exact
    // value: a real flight from 2997u down through the funnel, trigger fired
    // at the tightened P244 threshold, sector changed (1050->1054), and the
    // P245 transit flash armed — confirmed working, not just built.
    public const float WarpVisualScale = 2.5f;


    /// <summary>
    /// The undock debounce window, in seconds — mirrors
    /// <see cref="WarpCooldownSeconds"/>'s own shape for the analogous
    /// problem on the dock side. Longer than the warp window: a warp
    /// transit relocates the ship well clear of the source aleph as part of
    /// the transit itself, but a station launch has no guarantee the ship's own autopilot
    /// commits to a heading away from the station quickly — confirmed live
    /// (P74/P75) that a ship reorienting toward a distant target can drift
    /// back within <see cref="DockRadius"/> well past a 2 s window before
    /// settling onto a real course. Compared against
    /// <see cref="Ship.LastLaunchTime"/>.
    /// </summary>
    public const double LaunchCooldownSeconds = 8.0;

    /// <summary>
    /// The simplified dock radius, in world units — the <b>approximation</b> of
    /// the reference engine's real landing-slot test that this server adopts
    /// for the dock trigger. <c>DataStationIGC</c> carries only a
    /// <see cref="DataStationIGC.Position"/> (no size/radius), so the native
    /// <c>IstationIGC::InGarage(ship, pos)</c> slot-geometry approach
    /// (<c>shipIGC.cpp:843,853</c>) cannot be ported faithfully; this constant
    /// stands in for it. It is chosen large enough to comfortably encompass a
    /// station's model plus a fighter's approach envelope (ship half-length is
    /// a few tens of units, cf. <see cref="ResolveCombatCollisions"/>'s
    /// <c>0.5·length</c> sphere), yet small against the map scale (warps are ~100
    /// units and sectors/spacing are thousands — e.g. Arrokoth's warps at
    /// <c>[320,-5155,1613]</c> and <c>[3733,3911,607]</c>, P45/VizExplore),
    /// so a cruising ship crossing a sector is not falsely docked. On the
    /// <c>XXV_simple.igc</c> map this map's two stations sit at ~<c>[0,0,0]</c>
    /// in their own sectors, far from any spawn (ships spawn at <c>x≈720·(n+1)</c>,
    /// Server.cs <c>SpawnDefaultShip</c>) or warp — so the constant's job is to
    /// draw a defensible dock "arrival sphere", not to tune against cramped
    /// geometry. Deliberately <b>not</b> a faithful port of <c>InGarage</c>;
    /// stated plainly per this repo's honesty-about-simplifications convention.
    /// </summary>
    public const float DockRadius = 200.0f;

    /// <summary>Loaded c_fcidDownedShield (slot 5), shipIGC.cpp:851.</summary>
    private float DownedShieldFraction => Mission?.Core.Constants.FloatConstants[5] ?? 0.1f;

    /// <summary>Loaded c_fcidHe3Regeneration (slot 14), asteroidigc.h:52-55.
    /// Standalone sectors use the native constructor default (missionigc.cpp:2702).</summary>
    public float He3Regeneration => Mission?.Core.Constants.FloatConstants[14] ?? (0.01f / 60f);

    /// <summary>
    /// The proximity padding added to an asteroid's radius when deciding whether
    /// a ship is close enough to mine — <c>+ 30.0f</c>, the native "are we close
    /// enough" margin at <c>shipIGC.cpp:1960</c>. Combined with the asteroid's
    /// own <c>Radius</c> and the ship's half-length into the mining threshold
    /// (the same <c>0.5·length</c> ship radius the combat and warp passes use).
    /// </summary>
    public const float MiningProximityPadding = 30.0f;


    /// <summary>
    /// The <b>combat</b> collision→damage pass, run once per sector tick after
    /// the models loop — the slice that closes the loop the first half opened.
    /// It uses the existing <see cref="CollisionDetector"/> (no second detection
    /// path): build one <see cref="Collider"/> per model, <see cref="CollisionDetector.Detect"/>
    /// the step once, and resolve each ordnance↔non-ordnance event in time order.
    /// <c>Projectile</c>, <c>Missile</c> (via <see cref="IOrdnance"/>) and
    /// <see cref="Probe"/> are the collider-bearing models today, so a collider
    /// is a sphere — the ordnance's <c>DataProjectileTypeIGC.Radius</c>, the
    /// ship's half-length (<c>shipIGC.cpp:1419</c>: <c>SetRadius(0.5·length)</c>),
    /// and since P100 the probe's <c>DataProbeTypeIGC.Radius</c> (the exact field
    /// the native sets the probe's own collision radius from, <c>probeigc.cpp:118</c>).
    /// A probe collider is <c>isStatic</c> with zero velocity — a probe never
    /// moves (the same fact <c>Probe.cs</c>'s remarks establish).
    /// </summary>
    /// <remarks>
    /// <para><b>Gated on there being a projectile or missile.</b> Only a piece of
    /// ordnance deals damage in this slice (<c>CprojectileIGC::HandleCollision</c>,
    /// the sole damagable path; a ship↔ship or ship↔probe that never meets an
    /// ordnance cannot hurt anyone — <c>CshipIGC::HandleCollision</c> only
    /// separates/bounces). Without ordnance the whole pass is skipped and the
    /// sector tick is a pure model loop, so the flight/bot slices (which have
    /// ships but never ordnance) are untouched.</para>
    /// <para><b>Resolution, in C++ order.</b> The queue is time-ordered; each
    /// event is handled once. The ordnance that hits terminates immediately
    /// (<c>HandleCollision</c> calls <c>Terminate()</c>, <c>projectileIGC.cpp:230</c>),
    /// so a piece of ordnance can strike at most the earliest target in a step
    /// (later events for a spent round are skipped). The non-ordnance side can be
    /// a <see cref="Ship"/> or, since P100, a <see cref="Probe"/> (a probe is a
    /// valid direct-hit target — nothing special-cases <c>OT_probe</c> out of
    /// the native dispatch, <c>projectileIGC.cpp:166-169</c> only excludes
    /// <c>OT_mine</c>). Ordnance never damages its own
    /// <see cref="IOrdnance.Launcher"/>, and (friendly-fire gate,
    /// <c>shipIGC.cpp:1222-1228</c>) never damages a <em>ship</em> on the same
    /// <see cref="Ship.SideId"/>; both cases pass the round through (see
    /// <see cref="Ship.ReceiveDamage"/>). A <b>probe</b> has no such gate — an
    /// unarmed probe takes direct fire from any side (<c>probeigc.h:74-89</c>;
    /// see <see cref="Probe.ReceiveDamage"/>). A ship whose hull reaches zero
    /// emits <c>KillShipEvent</c> <b>once</b> and is removed from the sector; a
    /// probe whose fraction reaches zero removes itself and raises
    /// <see cref="IIgcSite.ProbeDestroyedEvent"/> <b>once</b>, inside
    /// <see cref="Probe.ReceiveDamage"/> (the same pair <c>Probe.OnUpdate</c>'s
    /// expiry branch makes). Blast-on-impact (P97) fires for a probe target
    /// exactly as for a ship (see the branch note in the loop body).</para>
    /// </remarks>
    /// <summary>
    /// P621b — populate the <see cref="Model.Hull"/> hook (P621 left it null, so
    /// the combat pass's ordnance colliders fell back to the ship's bounding
    /// sphere). Resolve the ship's real <c>.cvh</c> once from this sector's
    /// <see cref="CollisionHulls"/> cache and cache it on the ship, so weapon
    /// fire collides against the real hull exactly as native resolves projectile
    /// hits against the target's <c>LoadCVH</c> body (<c>modelIGC.cpp:105-118</c>,
    /// <c>projectileIGC.cpp:166-190</c>). A ship with no resolvable model or no
    /// cache keeps <c>null</c> → the sphere fallback, matching native's
    /// <c>HitTest::Create</c> default (<c>HitTest.cpp:844-860</c>).
    /// </summary>
    private CvhFile? EnsureShipHull(Ship s)
    {
        if (s.Hull is null && CollisionHulls is not null)
            s.Hull = CollisionHulls.Resolve(s.HullType.ModelName);
        return s.Hull;
    }

    // P689: reject only type combinations this pass unconditionally discards.
    // Damage/FF/death/launcher gates stay in dispatch: earlier events can mutate
    // that state. Native resolves the shared collision list once (clusterIGC.cpp:
    // 289-321); this port has separate combat and physical-collision passes.
    internal static bool CombatPair(Collider a, Collider b) =>
        (a.Data is IOrdnance && b.Data is Ship or Probe or Asteroid or Station)
        || (b.Data is IOrdnance && a.Data is Ship or Probe or Asteroid or Station);

    private void ResolveCombatCollisions(double now, float dt)
    {
        if (!_models.Any(m => m is Projectile || m is Missile)) return;   // nothing can deal damage

        var buildScope = TickProfiler.Enter("combat.build");
        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            Collider? c = model switch
            {
                IOrdnance o => new Collider(o, o.Position, o.Velocity, Orientation.Identity,
                    hull: null, o.Radius, isStatic: false, HitShape.Sphere),
                Ship s => new Collider(s, s.Position, s.Velocity, s.Orientation,
                    hull: EnsureShipHull(s), 0.5f * s.HullType.Length, isStatic: false, HitShape.Sphere),
                Probe p => new Collider(p, p.Position, Vector3.Zero, Orientation.Identity,
                    hull: null, p.ProbeType.Radius, isStatic: true, HitShape.Sphere),
                _ => null,
            };
            if (c is not null) detector.Add(c);
        }

        // Asteroids (P102): a THIRD static target type, exactly as P100 added the
        // probe as the second. An asteroid is genuinely c_mtDamagable| c_mtHitable
        // (asteroidigc.cpp:36) and the native dispatch excludes only OT_mine
        // (projectileIGC.cpp:166-169, missileigc.cpp:435-447), so a rock is hit
        // through the same swept detector. Asteroid deliberately is NOT a Model (it
        // lives in _mineable, not _models — see Asteroid's doc comment), so it cannot
        // ride the foreach above; walk _mineable and add one collider per rock.
        // Collider's own constructor takes explicit position/velocity/radius, not a
        // Model reference (Collider.cs:29-31), so no Model inheritance is needed.
        foreach (var ast in _mineable)
        {
            if (ast.Data.Position is not { Length: 3 } ap) continue;   // no position: can't collide
            detector.Add(new Collider(
                ast, new Vector3(ap[0], ap[1], ap[2]), Vector3.Zero, Orientation.Identity,
                hull: CollisionHulls?.Resolve(ast.Data.ModelName), ast.Radius, isStatic: true, HitShape.Sphere));
        }

        // Stations (P125): a FOURTH static target type, exactly as P100 added the
        // probe and P102 added the asteroid. A station is genuinely
        // c_mtDamagable | c_mtHitable (stationIGC.cpp:244) and the native
        // dispatch excludes only OT_mine (projectileIGC.cpp:166-169), so a base
        // is hit through the same swept detector. Station deliberately is NOT a
        // Model (it lives in _stationRuntimes, not _models), so it cannot ride
        // the foreach above; walk _stationRuntimes and add one static collider
        // per station at its record position with the type's collision radius.
        foreach (var st in _stationRuntimes)
        {
            if (st.Data.Position is not { Length: 3 } sp) continue;   // no position: can't collide
            detector.Add(new Collider(
                st, new Vector3(sp[0], sp[1], sp[2]), Vector3.Zero, Orientation.Identity,
                hull: CollisionHulls?.Resolve(st.Type.ModelName), st.Radius, isStatic: true, HitShape.Sphere));
        }

        buildScope.Dispose();
        IReadOnlyList<CollisionEvent> events;
        using (TickProfiler.Enter("combat.detect")) events = detector.Detect(dt, ParallelSectorSim.Enabled, ParallelSectorSim.ThreadCount, CombatPair);
        using var dispatchScope = TickProfiler.Enter("combat.dispatch");
        var spent = new HashSet<IOrdnance>();
        foreach (var ev in events)
        {
            // Identify the ordnance and the non-ordnance target in whatever
            // registration order the detector returned them. A combat event is
            // an ordnance↔non-ordnance pair; since P100 the non-ordnance side can
            // be a Ship OR a Probe, and since P102 it can also be an Asteroid
            // (a rock is a valid direct-hit target — the Asteroid collider case
            // above, itself only a third leaf of the same detector the ship path
            // already uses).
            var ord = ev.A.Data as IOrdnance ?? ev.B.Data as IOrdnance;
            var ship = ev.B.Data as Ship ?? ev.A.Data as Ship;
            var probe = ev.B.Data as Probe ?? ev.A.Data as Probe;
            var asteroid = ev.B.Data as Asteroid ?? ev.A.Data as Asteroid;
            var station = ev.B.Data as Station ?? ev.A.Data as Station;
            if (ord is null || (ship is null && probe is null && asteroid is null && station is null)) continue;
            if (ReferenceEquals(ord, ship) || ReferenceEquals(ord, probe) || ReferenceEquals(ord, asteroid) || ReferenceEquals(ord, station)) continue;
            if (ord is Missile { IsDud: true }) continue; // missileigc.cpp:428
            if (spent.Contains(ord)) continue;            // already terminated this step

            // P467 — the round's damage multiplier from its LAUNCHER's side
            // global attributes: a gun (Projectile) round scales by
            // c_gaDamageGuns, a missile by c_gaDamageMissiles. Native reads the
            // ordnance's OWN GetSide()->GetGlobalAttributeSet().GetAttribute(..)
            // (projectileIGC.cpp:132,186; missileigc.cpp:474,523), and an
            // ordnance's side is its launcher's side (m_launcher). The launcher
            // in this port is a Ship (probes/stations never launch; cf the
            // self-guards), so resolve through Ship.GlobalAttributeFactor;
            // identity when sideless/unresolvable. Applies to BOTH the direct
            // hit and the blast — and, matching native, to negative-power
            // (nanite/repair) rounds too: native multiplies GetPower() by the
            // attribute regardless of sign, so a repaired hull heals × the same
            // factor (projectileIGC.cpp:186).
            float gaMul = ord is Missile
                ? (ord.Launcher as Ship)?.GlobalAttributeFactor(GlobalAttributeSet.DamageMissiles) ?? 1f
                : (ord.Launcher as Ship)?.GlobalAttributeFactor(GlobalAttributeSet.DamageGuns) ?? 1f;

            // Impact positions at the collision time, exactly as the C++
            // HandleCollision computes them (projectileIGC.cpp:174-175):
            // position1 = ordnance pos + vel·tCollision, position2 = target pos + vel·tCollision.
            var position1 = ord.Position + ord.Velocity * ev.TCollision;

            if (ship is not null)
            {
                if (ship.Dead) continue;                  // already killed this step
                if (ReferenceEquals(ord.Launcher, ship)) continue; // never hit your own launcher

                // Friendly-fire gate (shipIGC.cpp:1222-1228). Friendly fire is
                // always off — ordnance never damages another ship on the same
                // side. Mirrors the launcher self-exclusion directly above (a
                // friendly round passes through without landing and without
                // terminating), and engages only when BOTH ships carry a side, so
                // a side-less ship (flight/bot slices, unit tests) fights everyone
                // exactly as before. Different sides / no side still deal damage.
                //
                // P277 (nanite gun): the reference's friendly-fire gate is
                // `... && (amount >= 0.0f)` — the <b>sign</b> of the round
                // decides the gate, so a NEGATIVE-power (repair/nan) round is
                // NOT suppressed and lands on the same-side target (shipIGC.cpp:1222-1228
                // returns c_drNoDamage only for amount >= 0). Repairing a
                // friendly's hull is the whole point of the nan weapon, so a
                // negative-power round must reach ReceiveDamage's repair branch
                // instead of sailing through the ally. Positive-power (damaging)
                // rounds keep the exact prior same-side suppression.
                if (ord.Launcher is Ship shooter
                    && shooter.SideId is { } shooterSide
                    && ship.SideId == shooterSide
                    && ord.Power >= 0f)
                    continue;

                // P549 — a warp-bomb missile striking a NON-warp target does NO
                // damage (native missileigc.cpp:459-466): the `eabm & c_eabmWarpBomb`
                // branch only calls WarpBombEvent when the target is a warp and
                // otherwise skips the damage path entirely. The missile is still
                // consumed on impact (native falls through to KillMissileEvent,
                // missileigc.cpp:512-513); the shared destroy notice at the bottom
                // of the loop is not reached on `continue`, so raise it here.
                if (ord is Missile warpBombMissile && warpBombMissile.MissileType.HasWarpBombCapability)
                {
                    spent.Add(ord);
                    Terminate(warpBombMissile);                            // HandleCollision → Terminate (P754b: notifies the site)
                    if (warpBombMissile.Launcher is Ship wbLauncher)
                        wbLauncher.Mission?.Site.MissileDestroyedEvent(warpBombMissile);
                    continue;
                }

                var position2 = ship.Position + ship.CollisionVelocity * ev.TCollision;

                // Damage = decoded ordnance Power × the launcher side's
                // c_gaDamageGuns factor (P467); C++ projectileIGC.cpp:177-190.
                // Missiles use the identical single-target call — a missile is
                // single-target this slice; BlastPower/BlastRadius (AoE) are read
                // but not acted on (same honest-deferral as homing, P91).
                DamageResult shipDr = ship.ReceiveDamage(
                    ord.DamageType,
                    ord.Power * gaMul,
                    now,
                    position1,
                    position2,
                    ord.Launcher);

                // P623 — attribute the hit to the VICTIM's own client so its
                // TK_TargetHostile window knows who hurt it (the port of native's
                // same-process DamageBucket population, which a pure-renderer
                // client cannot do for itself). Raised when a SHIP launcher dealt
                // real damage (hull or shield); covers both the projectile and
                // missile single-target calls above (a missile uses this identical
                // ReceiveDamage call). The server unicasts VictimDamaged to the
                // victim's peer only — see Server.Combat.BroadcastVictimDamaged.
                if (shipDr != DamageResult.NoDamage && ord.Launcher is Ship attacker)
                    attacker.Mission?.Site.VictimDamagedEvent(attacker.Id, ship.Id, ord.Power * gaMul);

                spent.Add(ord);
                Terminate((Model)ord);                 // HandleCollision → Terminate (projectileIGC.cpp:230; P754b notifies the site)

                // Blast-on-impact (P97): an area-effect round (BlastPower ≠ 0)
                // additionally creates a REAL explosion at the impact position,
                // separate from and ADDITIVE to the direct single-target hit just
                // above — native projectileIGC.cpp:206-219, "Area effect weapon:
                // create an explosion (no matter what)". See the probe branch
                // below for why this same block lives in both target paths.
                if (ord.BlastPower != 0f && ord is not Missile { MissileType.HasCaptureCapability: true })
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower * gaMul,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }

                // P364 — projectile impact spark. Native fires AddHullHit here
                // (projectileIGC.cpp:220-226): the round has no blast power, dealt
                // hull-or-worse damage, and the damage type lacks c_dmgidNoDebris.
                // position1/position2 are the round/target impact points; the world
                // normal is normalize(position1 − position2) (projectileIGC.cpp:225).
                MaybeRaiseProjectileHit(ord, position1, position2, Id,
                    0.5f * ship.HullType.Length, ship.Id, shipDr);

                if (ship.Dead) Terminate(ship);        // killed: leaves the sector (README death contract; P754b notifies the site)
            }
            else if (probe is not null)
            {
                // ── PROBE TARGET (P100) ──
                // Deliberately NO friendly-fire gate: the native probe damage
                // formula has none for an unarmed probe (probeigc.h:74-89 blocks
                // only an ARMED probe's same-side fire), so a probe takes direct
                // fire from ANY side — a real, cited difference from the ship
                // path above (shipIGC.cpp:1222-1228). See Probe.ReceiveDamage.
                if (probe.Dead) continue;              // already destroyed this step
                if (ReferenceEquals(ord.Launcher, probe)) continue; // symmetric self-guard (never fires: a probe is never a launcher)
                // P491: the armed probe's OWN round emerges from its own body
                // (no emission-point offset in this port) — pass it through to
                // its real target exactly as native's self-launch does (the
                // round's launcher IS the probe there, excluded by the self-guard
                // above; here the port keeps the deployer ship as the round's
                // launcher for the wire broadcast, so the probe's round is
                // tagged via Projectile.OriginProbe instead).
                if (ReferenceEquals((ord as Projectile)?.OriginProbe, probe))
                    continue;

                var position2 = probe.Position;         // probe never moves → zero collision velocity (probeigc.cpp:115,142)

                DamageResult probeDr = probe.ReceiveDamage(
                    ord.DamageType,
                    ord.Power * gaMul,
                    now,
                    position1,
                    position2,
                    ord.Launcher);

                spent.Add(ord);
                Terminate((Model)ord);                 // HandleCollision → Terminate (projectileIGC.cpp:230; P754b notifies the site)

                // Blast-on-impact (P97) fires UNCONDITIONALLY per the native's
                // own comment ("Area effect weapon: create an explosion (no
                // matter what)", projectileIGC.cpp:206) — it must NOT care what
                // the direct target was. A blast-capable round that hits a probe
                // still creates the real Explosion at the impact position
                // exactly as hitting a ship. Duplicated here (rather than a
                // shared helper) to keep each target branch self-contained,
                // per the project's preference for a little duplication over a
                // forced abstraction where the two paths genuinely differ.
                if (ord.BlastPower != 0f && ord is not Missile { MissileType.HasCaptureCapability: true })
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower * gaMul,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }

                // P364 — projectile impact spark (projectileIGC.cpp:220-226); a
                // probe is a valid direct-hit target exactly as a ship.
                MaybeRaiseProjectileHit(ord, position1, position2, Id,
                    probe.ProbeType.Radius, probe.Id, probeDr);

                if (probe.Dead) Terminate(probe);      // destroyed: leaves the sector, identical to the ship's death bookkeeping (P754b notifies the site)
            }
            else if (station is not null)
            {
                // ── STATION TARGET (P125) ──
                // The friendly-fire gate is INSIDE Station.ReceiveDamage (native
                // puts it there too, stationIGC.cpp:128-130): a same-side hit
                // returns NoDamage. Unlike a ship (whose friendly round passes
                // through un-terminated in the pass above), a round that strikes
                // a station ALWAYS terminates — native kills the projectile on
                // impact regardless of the damage result ("In any case, kill the
                // projectile", projectileIGC.cpp:229-230), so the termination
                // below is unconditional, exactly as the probe/asteroid branches.
                if (station.Dead) continue;            // already destroyed this step
                if (ReferenceEquals(ord.Launcher, station)) continue; // symmetric self-guard (never fires: a station is never a launcher)

                // Spawned-inside guard (P125): a round whose collision the swept
                // detector reports at tCollision ≤ 0 began this step ALREADY inside
                // the station's (large) collision sphere — it spawned embedded in a
                // base, not flown into it. Such a round does not damage the station
                // it emerged from; it passes through to its real target, mirroring
                // the friendly-ship pass-through above. This is the degenerate case
                // the pre-existing ship/missile combat tests hit: the map's starting
                // Garrison occupies the sector origin with a ~423-unit collision
                // radius, and synthetic fire spawning within it (a shooter at the
                // garrison centre, a launcher-less missile created in the sector)
                // would otherwise self-destruct against the base at t=0. Native never
                // sees this — a ship inside a station is docked and does not fire.
                if (ev.TCollision <= 0f)
                    continue;

                // Station never moves → zero collision velocity (stationIGC.cpp:244
                // c_mtStatic; a static body). position2 is its fixed centre.
                var spos = station.Center;

                float shieldBefore = station.ShieldFraction, hullBefore = station.ArmorFraction;
                DamageResult killed = station.ReceiveDamage(
                    ord.DamageType,
                    ord.Power * gaMul,
                    now,
                    position1,
                    spos,
                    ord.Launcher);
                station.TraceDamage(now, ord.Launcher, ord.DamageType, shieldBefore, hullBefore, ord);

                // Native missileigc.cpp:471-496: damage FIRST, then test the
                // resulting shield fraction. Capture missiles suppress their
                // explosion even when the capture gate fails (position1 = zero).
                if (ord is Missile { MissileType.HasCaptureCapability: true } captureMissile
                    && !captureMissile.MissileType.HasWarpBombCapability
                    && ord.Launcher is Ship captor && captor.Mission is { } captureMission
                    && captor.SideId is { } captureSide && !station.Dead
                    && station.SideId != captureSide
                    && !station.Type.HasCapability(DataStationTypeIGC.SabmPedestal)
                    && station.ShieldFraction < DownedShieldFraction)
                {
                    short oldSide = station.SideId;
                    captureMission.Site.CaptureStationEvent(captor, station);
                    station.Capture(captureSide);
                    SyncStationData(station);
                    var (winner, changed) = captureMission.RecomputeConquestAndTerritory(null);
                    if (changed || winner is not null)
                        captureMission.Site.VictoryByStationsComputed(captureMission, changed, winner);
                    captureMission.Site.SideLostRestartStation(captureMission, oldSide);
                }

                spent.Add(ord);
                Terminate((Model)ord);                 // HandleCollision → Terminate (projectileIGC.cpp:230; P754b notifies the site)

                // Blast-on-impact (P97) fires UNCONDITIONALLY per the native's
                // own comment ("Area effect weapon: create an explosion (no
                // matter what)", projectileIGC.cpp:206) — it must NOT care what
                // the direct target was, nor the damage result. Same
                // self-containment reasoning as the probe/asteroid branches.
                if (ord.BlastPower != 0f && ord is not Missile { MissileType.HasCaptureCapability: true })
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower * gaMul,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }

                // P364 — projectile impact spark (projectileIGC.cpp:220-226); a
                // station is a valid direct-hit target exactly as a ship.
                MaybeRaiseProjectileHit(ord, position1, spos, Id,
                    station.Radius, station.StationId, killed);

                // Death bookkeeping on the FIRST crossing to zero (ReceiveDamage
                // returns Killed only then, stationIGC.cpp:189-194): remove the
                // station from the sector (so it stops being dockable/ripcordable
                // and vanishes from future passes) and raise the host seam's
                // KillStationEvent, which the host answers with VacateStation
                // (eject docked ships) + the whole-mission STATION_DESTROYED(59)
                // broadcast (FedSrv.CPP:12237-12279). Routed through the ordnance
                // launcher's mission site, exactly like the asteroid destroy
                // below; a launcher-less round raises nothing.
                if (killed == DamageResult.Killed)
                {
                    RemoveStation(station);             // port of the station's Terminate → DeleteStation (stationIGC.h:183)
                    if (ord.Launcher is Ship ls && ls.Mission is { } m)
                    {
                        m.Site.KillStationEvent(station, ord.Launcher, ord.Power);
                        // P191 — after the station is gone, recompute the
                        // conquest/territory victory state. The dead station is
                        // already removed from the sector's runtime list, so
                        // native's pstationKilled exclusion (fed via
                        // CheckForVictoryByStationKill(pstation, ...),
                        // FedSrv.CPP:12344) is naturally satisfied; surface the
                        // result so the host can broadcast GAME_STATE and declare
                        // the win via the P178 path.
                        var (gWinner, gChanged) = m.RecomputeConquestAndTerritory(null);
                        if (gChanged || gWinner is not null)
                            m.Site.VictoryByStationsComputed(m, gChanged, gWinner);
                        // P455 — a restart-capable station just died for its owner
                        // (station.SideId is still the owning side; a kill never
                        // re-sides). The host checks whether that was the side's
                        // LAST restart station and, if so, eliminates the side —
                        // native's station-elimination win (fsmission.cpp:3698-3714).
                        m.Site.SideLostRestartStation(m, station.SideId);
                    }
                }
            }
            else
            {
                // ── ASTEROID TARGET (P102) ──
                // Even simpler than the probe: the native asteroid formula has NO
                // side/friendly-fire check at all (asteroidigc.h:126-171 reads
                // nothing but type/amount and the rock's own hitpoints — no
                // SideId, no gated nested branch), so a rock takes direct fire
                // from ANYONE, unconditionally, always. Do not add a gate that
                // does not exist natively. See Asteroid.ReceiveDamage.
                if (asteroid!.Dead) continue;             // already destroyed this step
                if (ReferenceEquals(ord.Launcher, asteroid)) continue; // symmetric self-guard (never fires: an asteroid is never a launcher)

                // Asteroid never moves → zero collision velocity (asteroidigc.cpp:36
                // c_mtStatic; SetRadius/SetPosition at asteroidigc.cpp:39-40, no
                // velocity). position2 is its fixed centre.
                var apos = new Vector3(asteroid.Data.Position[0], asteroid.Data.Position[1], asteroid.Data.Position[2]);

                DamageResult killed = asteroid.ReceiveDamage(
                    ord.DamageType,
                    ord.Power * gaMul,
                    now,
                    position1,
                    apos,
                    ord.Launcher);

                spent.Add(ord);
                Terminate((Model)ord);                 // HandleCollision → Terminate (projectileIGC.cpp:230; P754b notifies the site)

                // Blast-on-impact (P97) fires UNCONDITIONALLY per the native's
                // own comment ("Area effect weapon: create an explosion (no
                // matter what)", projectileIGC.cpp:206) — it must NOT care what
                // the direct target was. A blast-capable round that hits an
                // asteroid still creates the real Explosion at the impact position
                // exactly as hitting a ship or probe. Duplicated here for the
                // same self-containment reason noted in the probe branch.
                if (ord.BlastPower != 0f && ord is not Missile { MissileType.HasCaptureCapability: true })
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower * gaMul,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }

                // P364 — projectile impact spark (projectileIGC.cpp:220-226); an
                // asteroid is a valid direct-hit target exactly as a ship. No
                // portable short id on the wire target (DataAsteroidIGC has none),
                // so targetId is NA(-1) — the effect renders regardless.
                MaybeRaiseProjectileHit(ord, position1, apos, Id,
                    asteroid.Radius, -1, killed);

                // Death bookkeeping on the FIRST crossing to zero (ReceiveDamage
                // returns Killed only then, asteroidigc.h:160-163): remove the
                // rock from _mineable (so it stops being minable and stops
                // appearing in future combat/mining passes — the port of the
                // native pasteroid->Terminate() in KillAsteroidEvent,
                // FedSrv.CPP:12420) and raise the host seam's KillAsteroidEvent
                // with explodeF=true (asteroidigc.h:162; the wire ASTEROID_DESTROYED(86)
                // explodeF field). Route through the ordnance launcher's mission
                // site, exactly like the missile-destroy broadcast below; a
                // launcher-less round raises nothing.
                if (killed == DamageResult.Killed)
                {
                    RemoveAsteroid(asteroid);                       // the port of pasteroid->Terminate() (FedSrv.CPP:12420)
                    if (ord.Launcher is Ship ls)
                        ls.Mission?.Site.KillAsteroidEvent(asteroid, true);   // asteroidigc.h:162
                }
            }

            // P91 consistency fix: the destroy notice must be broadcast for the
            // same reason a hit removes the missile — a peer who saw the launch
            // learns it is gone regardless of why (expired vs. hit something),
            // and the wire message (FM_S_MISSILE_DESTROYED(83)) has no "cause"
            // field to distinguish them. Same event, exactly once, as the expiry
            // path (Missile.OnUpdate) raises. No-op for a launcher-less missile
            // (site derived from a Ship launcher). Applies whether the missile
            // struck a ship or a probe (target-independent, so hoisted out of
            // both branches).
            if (ord is Missile struckMissile && struckMissile.Launcher is Ship launcherShip)
                launcherShip.Mission?.Site.MissileDestroyedEvent(struckMissile);
        }
    }

    /// <summary>
    /// P364 — the port of native's <c>AddHullHit</c> firing gate
    /// (<c>projectileIGC.cpp:220-226</c>): raise
    /// <see cref="IIgcSite.ProjectileHitEvent"/> when a <b>projectile</b> (not a
    /// missile — native only <c>CprojectileIGC::HandleCollision</c> calls
    /// AddHullHit) struck a damagable target, dealt hull-or-worse damage
    /// (<c>dr &lt;= c_drHullDamage</c>, projectileIGC.cpp:193), had no blast power
    /// (the native fires AddHullHit in the <em>else</em>-of-the-blast branch), and
    /// carried no <c>c_dmgidNoDebris</c> flag (<c>0x40</c>, igc.h:560). The port's
    /// "visible &amp;&amp; in a cluster" halves of <c>fExplosion</c> are always true
    /// (every sector model is visible; this pass runs per-sector), so they collapse
    /// to the damage-result gate. P463 additionally admits a fully
    /// shield-absorbed strike (<c>DamageResult.ShieldDamage</c>) so the same wire
    /// signal carries the shield-hit sound/flash discriminator; the hull spark is
    /// still gated on the wire's absorbed flag client-side. The world normal is
    /// <c>normalize(position1 − position2)</c> — the direction from the target's
    /// centre toward the round at the impact instant (<c>projectileIGC.cpp:225</c>),
    /// the exact normal the spark bits are launched along. Routed through the
    /// launcher's mission site (the same launcher-route convention as the
    /// neighbouring destruction notices); a launcher-less round raises nothing.
    /// </summary>
    private void MaybeRaiseProjectileHit(IOrdnance ord, Vector3 position1, Vector3 position2,
        short clusterId, float targetRadius, short targetId, DamageResult dr)
    {
        // Native fires AddHullHit only for a projectile impact (projectileIGC.cpp:224),
        // never for a missile (missileigc.cpp has its own blast VFX).
        if (ord is not Projectile p) return;
        if (ord.BlastPower != 0f) return;                       // blast branch, not the debris branch
        // P463 — widened from "dr <= c_drHullDamage" to ALSO raise for a
        // fully shield-absorbed strike (ShieldDamage): the P364 hit signal now
        // feeds the shield-hit sound/flash too (scope 1), not just the hull
        // spark. The absorbed flag is carried on the wire; the CLIENT keeps the
        // native AddHullHit gate by spawning the hull spark only when the flag
        // is clear. Unchanged: the spark still never fires on a pure shield hit.
        if (dr != DamageResult.Killed && dr != DamageResult.HullDamage && dr != DamageResult.ShieldDamage) return;
        if ((ord.DamageType & (byte)0x40) != 0) return;         // c_dmgidNoDebris (igc.h:560)

        var delta = position1 - position2;
        float lenSq = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
        if (lenSq <= 0f) return;                                // degenerate: no usable normal
        float inv = 1f / MathF.Sqrt(lenSq);
        var normal = new Vector3(delta.X * inv, delta.Y * inv, delta.Z * inv);

        // P463 — discriminate a shield-absorbed strike: the server's
        // ReceiveDamage returns ShieldDamage when the round was fully absorbed
        // by the target's shield (nothing leaked to hull). That same choice keys
        // native RegisterHit's shield-vs-hull hit sound (trekigc.cpp:2368) and
        // the shield-hit flash. Flag carried on the P364 wire message.
        bool absorbedByShield = dr == DamageResult.ShieldDamage;

        if (p.Launcher is Ship ls && ls.Mission is { } m)
            m.Site.ProjectileHitEvent(p, position1, normal, targetRadius, targetId, clusterId, absorbedByShield);
    }

    /// <summary>
    /// P254 — the <b>ship ↔ asteroid elastic collision</b> pass, run once per
    /// sector tick (called from <see cref="Update"/>). A ship that overlaps an
    /// asteroid is bounced off it: pushed out to just outside the rock's surface
    /// and given a reflected velocity scaled by the native coefficient of
    /// elasticity (0.75), plus real impact damage, exactly the shared
    /// collision-response block native runs for <c>OT_asteroid</c> via the
    /// station/building-effect/probe fallthrough (<c>shipIGC.cpp:906-960</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a separate pass, not a case in
    /// <see cref="ResolveCombatCollisions"/>.</b> That method is structurally
    /// unsuited to a ship-vs-asteroid pair in three ways: (1) its whole method
    /// is gated by an early-return that demands a live <c>Projectile</c>/
    /// <c>Missile</c> in the sector (<c>Sector.cs:1272</c>) — a ship must be able
    /// to bounce off a rock in an otherwise-empty sector, so this pass cannot ride
    /// that gate; (2) its event loop discards any pair where neither side is
    /// <c>IOrdnance</c> (<c>Sector.cs:1334-1340</c>) — a ship-asteroid pair is
    /// exactly that; and (3) the mining regression below needs a place to consult
    /// <see cref="Ship.MiningAsteroid"/> that is independent of ordnance. The
    /// established per-tick-pass convention (<c>ResolveMining</c>,
    /// <c>ResolveWarpCollisions</c>, <c>ResolveDockCollisions</c> — each its own
    /// method called from <see cref="Update"/>) is therefore followed: this is a
    /// dedicated resolver that reuses the same
    /// <see cref="CollisionDetector"/>/<see cref="Collider"/> geometry the combat
    /// pass already builds (ships moving, asteroids static), keeping the
    /// no-ordnance-required bounce logically separate and testable.</para>
    /// <para><b>Debounce — velocity-based, chosen over the warp time-cooldown.</b>
    /// The warp pattern (<see cref="WarpCooldownSeconds"/>) stamps a wall-clock
    /// window so a re-entering body pauses; that is wrong for a bounce, where a
    /// genuine re-approach must re-trigger promptly. Here a ship is only bounced
    /// while it is actually approaching the rock — its velocity projected on the
    /// outward normal is negative (<c>v · n &lt; 0</c>). After a bounce the
    /// reflected velocity points away (<c>v · n &gt; 0</c>), and the separation
    /// push clears the overlap, so a resting or outgoing ship is never re-slammed
    /// tick after tick; the instant it turns around and heads back in it bounces
    /// again naturally. This needs no timestamps and cannot stall a real
    /// approach.</para>
    /// <para><b>Mining exclusion (mandatory regression guard).</b> An
    /// actively-mining ship sits <em>inside</em> the mining-proximity envelope of
    /// its rock (near-zero velocity, within
    /// <c>Radius + 0.5·length + MiningProximityPadding</c> — the exact gate
    /// <see cref="ResolveMining"/> enforces), so without an exclusion the new
    /// bounce would violently eject every miner the instant mining starts. Native
    /// skips this whole shared block for a drilling builder
    /// (<c>shipIGC.cpp:872-884</c>); the port reproduces that with
    /// <c>ship.MiningAsteroid == ast</c> (the rock the ship is currently working),
    /// so a miner never bounces off the specific rock it drills — while still
    /// bouncing normally off any <em>other</em> rock it happens to touch. Because
    /// the velocity gate above also swallows a fully-stopped miner, the live
    /// regression test injects a small sub-mining-threshold velocity into the rock
    /// to prove the exclusion itself, not just the velocity gate, holds.</para>
    /// <para><b>Faithful scalars.</b> <c>cElasticity = 0.75</c>,
    /// <c>c_impactDamageCoefficient = 1/128</c> (<c>shipIGC.cpp:920,948</c>);
    /// damage is the native <c>|v₁ − v₁′|² · (1/128)</c> reflected-velocity
    /// metric, doubled for the ship and split between the two hit-point pools by
    /// the native <c>hp·d/(hp+d)</c> share (<c>shipIGC.cpp:949-956</c>). The
    /// damage type is the native <c>c_dmgidCollision</c> row — <c>0xE0</c> masked
    /// by <c>c_dmgidMask = 0x1f</c> selects matrix row 0
    /// (<c>missionigc.h:148-156</c>), the same row this port's already-masked
    /// <see cref="Ship.ReceiveDamage"/> indexes with <c>type = 0</c>. A lifepod
    /// bounces but neither deals nor takes damage, matching native's
    /// <c>c_habmLifepod</c> gate (<c>shipIGC.cpp:956</c>). The client-side
    /// collision sound / FF / jiggle effects are out of scope (a separate task);
    /// the position/velocity correction itself rides real server updates to the
    /// client automatically.</para>
    /// </remarks>
    private void ResolveShipAsteroidCollisions(double now, float dt)
    {
        if (_models.Count == 0 || _mineable.Count == 0) return;

        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            if (model is not Ship s) continue;
            if (s.Dead) continue;
            // P621 — collide against the ship's real .cvh hull (native LoadCVH),
            // not a bounding sphere; falls back to a sphere when no .cvh exists.
            detector.Add(new Collider(s, s.Position, s.Velocity, s.Orientation,
                CollisionHulls?.Resolve(s.HullType.ModelName), 0.5f * s.HullType.Length, isStatic: false, HitShape.Sphere));
        }
        foreach (var ast in _mineable)
        {
            if (ast.Data.Position is not { Length: 3 } ap) continue;   // no position: can't collide
            detector.Add(new Collider(ast, new Vector3(ap[0], ap[1], ap[2]), Vector3.Zero,
                Orientation.Identity, CollisionHulls?.Resolve(ast.Data.ModelName), ast.Radius, isStatic: true, HitShape.Sphere));
        }

        IReadOnlyList<CollisionEvent> events;
        using (TickProfiler.Enter("collision.asteroid.detect")) events = detector.Detect(dt, ParallelSectorSim.Enabled, ParallelSectorSim.ThreadCount, static (a, b) =>
            (a.Data is Ship && b.Data is Asteroid) || (b.Data is Ship && a.Data is Asteroid));
        using var dispatchScope = TickProfiler.Enter("collision.asteroid.dispatch");
        foreach (var ev in events)
        {
            var ship = ev.A.Data as Ship ?? ev.B.Data as Ship;
            var ast = ev.A.Data as Asteroid ?? ev.B.Data as Asteroid;
            if (ship is null || ast is null) continue;        // only ship↔asteroid pairs interest this pass
            if (ReferenceEquals(ship, ast)) continue;
            if (ship.Dead) continue;
            ResolveShipAsteroidCollision(now, ev, ship, ast);
        }
    }

    private const float CollisionElasticity = 0.75f;            // shipIGC.cpp:935 (cElasticity)
    private const float ImpactDamageCoefficient = 1.0f / 128.0f;// shipIGC.cpp:618 (c_impactDamageCoefficient)
    private const byte CollisionDamageType = 0;                 // c_dmgidCollision (0xE0) & c_dmgidMask (0x1f) = row 0 (missionigc.h:155)
    private const float ImpactJiggleCoefficient = 1.0f / 20.0f; // shipIGC.cpp:624 (c_impactJiggleCoefficient)

    private void ResolveShipAsteroidCollision(double now, CollisionEvent ev, Ship ship, Asteroid ast)
    {
        var apos = new Vector3(ast.Data.Position![0], ast.Data.Position[1], ast.Data.Position[2]);

        // P621 — the separating-plane normal and the collision-time positions come
        // from native Separate (Separate.SeparateColliders, shipIGC.cpp:514-616):
        // the convex-hull contact normal, not a radial centre-to-centre line (that
        // was the bounding-sphere reduction). For a sphere fallback (no .cvh) it
        // degenerates to the radial direction, so pre-P621 behaviour is unchanged.
        bool shipIsA = ReferenceEquals(ev.A.Data, ship);
        var shipC = shipIsA ? ev.A : ev.B;
        var astC = shipIsA ? ev.B : ev.A;
        var htsShip = shipIsA ? ev.HtsA : ev.HtsB;
        var htsAst = shipIsA ? ev.HtsB : ev.HtsA;

        var sep = Separate.SeparateColliders(shipC, astC, ev.TCollision, fEqualBounce: false, htsShip, htsAst);
        var normal = sep.Normal;
        var position1 = sep.Position1;

        // P254 mining exclusion (mandatory): an actively-mining ship is NOT
        // bounced off the specific rock it is drilling — the port of the native
        // drilling-builder skip (shipIGC.cpp:872-884). Two predicates cover the
        // whole engagement: MiningAsteroid == ast (currently working this rock,
        // set by ResolveMining) AND, for the autopilot NPC miners whose plan is
        // committed BEFORE arrival (P156 MiningAsteroidTarget), the rock the
        // drone is set to drill — without the latter an NPC would be violently
        // ejected the instant it decelerates onto its target rock, before
        // ResolveMining latches MiningAsteroid. A different rock still bounces
        // normally. P652c: constructors now retain a separate build target
        // during travel. Give that target the same approach exemption, then
        // retain it through the registered effect while drilling (native
        // shipIGC.cpp:872-884). Otherwise an arrival/launch impact can destroy
        // the ordered rock before TryStartBuilderBuild registers construction.
        // P762 / row 7 — native CshipIGC::HandleCollision's drilling-builder
        // branch (shipIGC.cpp:871-893): a builder colliding with the asteroid
        // it is drilling creates the building effect ONCE when autopiloted and
        // the rock has no effect yet, then adds drilling hull-hit debris. Both
        // happen before the generic asteroid bounce, which is skipped entirely
        // (native `break` from the OT_asteroid case).
        if (ReferenceEquals(ship.BuilderAsteroidTarget, ast)
            && ship.IsNpc && ship.PilotType == Ship.PilotTypeBuilder
            && ship.Mission is { } builderMission)
        {
            // P769/SIM-0109 (a) — native gates the whole drill branch on the
            // drilling state mask (shipIGC.cpp:874,
            // `(m_stateM & drillingMaskIGC) != 0`): ONLY a builder latched into
            // drilling creates the building effect (m_bAutopilot && no effect
            // yet, :876-878) and raises the drilling debris (:888-890). A
            // builder that is not drilling (still flying its approach) gets
            // neither — but it is still exempt from the bounce (the port's
            // approach exemption below, kept as-is per P652c/P762). Once
            // latched, the ship pushes in with the drilling hold
            // (Ship.ApplyBuilderDrillingControls, shipIGC.cpp:2280-2296) and
            // the contact happens here — the only effect-creation path.
            if (ship.IsDrilling)
            {
                if (ast.BuildingEffect is null)
                {
                    builderMission.Site.CreateBuildingEffect(now, ast, ship);
                    if (ast.BuildingEffect is null && ship.BuilderStationType is { } stationType)
                        StartBuildingEffect(ship, ast, stationType, now);
                    if (ast.BuildingEffect is not null)
                        ship.BuilderAsteroidTarget = null;   // effect registered: building state takes over
                }

                // Drilling debris: AddHullHit at the ship's nose on the rock surface,
                // normal = the ship's backward vector (shipIGC.cpp:888-890). Native
                // offsets by GetRadius(), the hit-test radius set to
                // 0.5 * hull length at construction (shipIGC.cpp:1419, 888-890);
                // Ship.Radius is that value (P762b).
                Vector3 backward = ship.Orientation.GetBackward();
                Vector3 impact = ship.Position - backward * ship.Radius - apos;
                builderMission.Site.HullHitEvent(ast, impact, backward);
            }

            // Approach exemption (P652c/P762): a builder colliding with the
            // rock it is ordered to build on is NEVER bounced off it — native
            // `break`s out of the OT_asteroid case for a drilling builder
            // (shipIGC.cpp:891) and the port keeps that for the not-yet-
            // drilling flying approach too, so the ship can reach the rock
            // and latch instead of being ejected on first contact.
            return;
        }

        if (ReferenceEquals(ship.MiningAsteroid, ast)
            || ReferenceEquals(ship.MiningAsteroidTarget, ast)
            || ReferenceEquals(ship.BuilderAsteroidTarget, ast)
            || (ship.IsBuilding && _buildingEffects.Any(e => ReferenceEquals(e.Builder, ship) && ReferenceEquals(e.Target, ast)))) return;

        var velocity1 = ship.Velocity;

        // Velocity-based debounce: only an approaching ship (v·n < 0) bounces.
        // See the pass remarks for why this beats a wall-clock cooldown.
        if (velocity1 * normal >= 0f) return;

        // Elastic bounce — the native reflection scaled by cElasticity=0.75
        // (shipIGC.cpp:936-946): invert the normal component, keep the tangent.
        float oldSpeed1 = velocity1 * normal;
        var perpendicular1 = velocity1 - oldSpeed1 * normal;
        var vElastic1 = perpendicular1 - (oldSpeed1 * normal);     // reflection of the normal component
        var newVelocity1 = vElastic1 * CollisionElasticity;

        // Real impact damage (shipIGC.cpp:948-958): damage2 = |Δv|² · (1/128),
        // damage1 = 2·damage2, each split against the counterpart's hit-point pool.
        float damage2 = (velocity1 - newVelocity1).LengthSquared() * ImpactDamageCoefficient;
        float damage1 = 2.0f * damage2;

        // P463c — collision signal. Native plays collisionSound + the camera
        // jiggle for the SHIP only in this shared bounce block (the rock is
        // never a camera): intensity = 2·damage1·c_impactJiggleCoefficient
        // (shipIGC.cpp:836,1006). Raised for every resolved bounce, even a
        // lifepod's (the sound/jiggle sit OUTSIDE native's damage gate,
        // shipIGC.cpp:825-838) and even a lethal one (outside the kill check
        // too) — the intensity is a function of the velocity change, already
        // known before ReceiveDamage. party2 is null (a rock: no audience, no
        // jiggle); the wire carries its id as NA(-1). See IIgcSite.CollisionEvent.
        if (ship.Mission is { } m)
            m.Site.CollisionEvent(ship, null, Id, position1,
                2.0f * damage1 * ImpactJiggleCoefficient, 0f);

        float hp1 = CollisionHpPool(ship);   // ship effective collision-HP pool (incl. ShieldFraction × MaxStrength when shielded) — same additive helper the ship-ship pass uses (Sector.cs:2311-2312); native static branch shipIGC.cpp:962-965 is shield-only ('=', hull discarded), deliberately not reproduced here — see CollisionHpPool remark / P408
        float hp2 = ast.Fraction * ast.Data.HitPoints; // asteroid current HP (asteroidigc GetHitPoints)

        DamageResult dr1 = DamageResult.NoDamage;
        DamageResult dr2 = DamageResult.NoDamage;

        bool lifepod = ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        if (!lifepod)
        {
            if (hp2 > 0f)
                dr1 = ship.ReceiveDamage(CollisionDamageType, hp2 * damage1 / (hp2 + damage1),
                    now, position1, apos, launcher: null);
            if (hp1 > 0f)
                dr2 = ast.ReceiveDamage(CollisionDamageType, hp1 * damage2 / (hp1 + damage2),
                    now, apos, position1, launcher: null);
        }

        // Native skips the position/velocity correction if either body was killed
        // by the impact (shipIGC.cpp:963-966). Port death bookkeeping then applies
        // (a dead ship / destroyed rock leaves the sector) exactly as the combat
        // pass does for its own kills.
        if (dr1 == DamageResult.Killed || dr2 == DamageResult.Killed)
        {
            if (dr1 == DamageResult.Killed) Terminate(ship);
            if (dr2 == DamageResult.Killed) RemoveAsteroid(ast);
            return;
        }

        // Separate: push the ship out to just-touching + native's 0.5 clearance
        // (s = distanceBest − 0.5, shipIGC.cpp:601). Separate already displaced
        // position1 along the convex-hull contact normal; apply it and the
        // reflected velocity.
        ship.Position = position1;
        ship.Velocity = newVelocity1;
    }

    /// <summary>
    /// P354 — the per-tick ship-vs-ship elastic-collision pass, called from
    /// <see cref="Update"/> alongside <see cref="ResolveShipAsteroidCollisions"/>.
    /// This is the general two-finite-mass generalisation of the P254
    /// moving-vs-infinite-mass asteroid bounce: it ports the <c>OT_ship</c> branch
    /// of <c>CshipIGC::HandleCollision</c> (<c>shipIGC.cpp:1023-1202</c>) — the
    /// real mass-weighted elastic/inelastic collision blend, collision damage,
    /// the shield-boosted hit-point pools, the friendly/lifepod damage exemption
    /// and the two-body position correction — for every ship-ship pair the
    /// detector reports this tick. This is the port's missing ship-vs-ship
    /// physics that <see cref="ResolveRescueEvents"/> explicitly declined to
    /// build (<c>Sector.cs:784-786</c>, P115).
    /// </summary>
    /// <remarks>
    /// <para><b>Pair detection.</b> All flying ships (alive, placed, not docked —
    /// a docked ship is not an active body in the cluster and must not be knocked
    /// out of its station) become dynamic sphere colliders, radius
    /// <c>0.5 × HullType.Length</c>, exactly like the ship colliders in
    /// <see cref="ResolveShipAsteroidCollisions"/>; every reported pair of
    /// distinct ships is resolved.</para>
    /// <para><b>Mutual exclusivity with rescue.</b> Native runs the friendly
    /// rescue/carrier special cases first and <c>break</c>s out of the collision
    /// switch so physics never applies to that pair this tick
    /// (<c>shipIGC.cpp:1027-1060</c>). The port reproduces this by re-checking
    /// the <b>same</b> predicate <see cref="ResolveRescueEvents"/> uses (same
    /// side, exactly one lifepod with the other rescue-capable, in proximity) for
    /// each detected pair and skipping physics when it matches — the pair is then
    /// picked up by the later rescue pass instead of being bumped/damaged. The
    /// call order vs. the rescue pass is therefore irrelevant to correctness: the
    /// in-pass re-check enforces the native rescue-priority regardless of
    /// ordering (this pass is grouped with the asteroid physics block purely for
    /// cohesion).</para>
    /// <para><b>Physics.</b> The exact native two-body blend
    /// (<c>shipIGC.cpp:1093-1124</c>): <c>cElasticity = 0.75</c>,
    /// <c>c_impactDamageCoefficient = 1/128</c> (reused from P254), the
    /// inelastic shared velocity <c>vInelastic</c>, the one-dimensional elastic
    /// normal speeds <c>newSpeed1/2</c>, and the final blend
    /// <c>vInelastic·(1−ε) + vElastic·ε</c> for <b>both</b> bodies — unlike P254
    /// the rock never moved, so only the ship's velocity changed there.</para>
    /// <para><b>Damage.</b> The native two-term split
    /// (<c>shipIGC.cpp:1141-1145</c>): <c>base1/2 = |v − v′|² · (1/128)</c>, then
    /// <c>damage1 = 2·base1 + base2</c>, <c>damage2 = base1 + 2·base2</c> (P254's
    /// simpler one-term form is only valid because the asteroid's base2 ≈ 0). The
    /// HP pools (<c>shipIGC.cpp:1147-1158</c>) are each the hull hit-points plus,
    /// when a <c>Shield</c> part is mounted, <c>ShieldFraction × MaxStrength</c> —
    /// the shield-boosted pool that lets a shielded ship absorb repeated bumps.
    /// Damage is split via the native <c>hp·d/(hp+d)</c> share
    /// (<c>shipIGC.cpp:1170-1186</c>) with the counterpart ship as launcher.</para>
    /// <para><b>Damage exemption.</b> Distinct from P254's asteroid version
    /// (<c>shipIGC.cpp:1166-1168</c> vs the asteroid <c>c_habmLifepod</c> gate):
    /// native skips damage only when <c>bFriendly AND (either ship is a
    /// lifepod)</c> — a same-side lifepod bump is damage-free, but an <b>enemy</b>
    /// (different-side) lifepod still takes real collision damage. The port's
    /// <c>bFriendly</c> collapses to same-side-only (no alliance concept, exactly
    /// as <see cref="ResolveRescueEvents"/> documents).</para>
    /// <para><b>Position correction.</b> Native <c>Separate</c> passes
    /// <c>fEqualBounce = true</c> for ship-ship (<c>shipIGC.cpp:526</c>), pushing
    /// <b>both</b> bodies apart symmetrically (<c>shipIGC.cpp:610-615</c>). As
    /// P254 already established for this port's simplified spherical colliders
    /// (<c>Sector.cs:1961-1964</c>), <c>Separate</c>'s output reduces to the plain
    /// radial direction between the two centres: here <c>normal</c> =
    /// <c>(position1 − position2)</c> normalized (from ship2's centre toward
    /// ship1's, matching the sign convention of the velocity math above — a bump
    /// accelerates the ship ahead of the bumper). Both ships are then pushed half
    /// the required separation each along that normal so they end up
    /// <c>0.5·len1 + 0.5·len2 + 0.5</c> apart (the same <c>+0.5</c> clearance as
    /// P254, <c>Sector.cs:2032-2033</c>). Symmetric half-and-half is the simplest
    /// documented choice of the allowed splits.</para>
    /// <para><b>Debounce.</b> The same velocity-projection debounce as P254
    /// (<c>Sector.cs:1985-1987</c>) adapted to the relative velocity
    /// <c>(v1 − v2)·normal &lt; 0</c> — bounce only while at least one ship is
    /// actually approaching; after the bounce the relative normal velocity is
    /// positive so no re-slam, and a genuinely re-approaching pair re-triggers.</para>
    /// <para><b>Death handling.</b> Identical to P254 (<c>Sector.cs:2018-2027</c>):
    /// if either <c>ReceiveDamage</c> returns <c>Killed</c>, the position/velocity
    /// correction for that pair is skipped and the port's death bookkeeping
    /// (<c>_models.Remove(...)</c>) applies instead.</para>
    /// <para><b>Carrier-landing exclusion.</b> Native's third friendly special
    /// case (<c>shipIGC.cpp:1046-1059</c>, <c>LandOnCarrierEvent</c>/<c>InGarage</c>)
    /// is a ship-vs-ship carrier-landing mechanism. This port's carrier handling
    /// (<c>ResolveCarrierLandings</c>, P116) is a separate resolution pass, so
    /// this physics pass has no carrier-landing pair to exclude — a pre-existing
    /// separate gap, noted but not built (out of scope).</para>
    /// <para><b>Client-side effects.</b> Sound/FF/jiggle (<c>shipIGC.cpp:1189-1195</c>)
    /// are out of scope (a separate task, the same exclusion P254 established);
    /// the position/velocity corrections ride real server updates to the client.</para>
    /// </remarks>
    private void ResolveShipShipCollisions(double now, float dt)
    {
        if (_models.Count < 2) return;

        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            if (model is not Ship s) continue;
            if (s.Dead) continue;
            if (s.Mission is null || s.StationId is not null) continue;   // flying placed ships only
            detector.Add(new Collider(s, s.Position, s.Velocity, s.Orientation,
                CollisionHulls?.Resolve(s.HullType.ModelName), 0.5f * s.HullType.Length, isStatic: false, HitShape.Sphere));
        }

        // With one uniform velocity there can be no approach response in this
        // pass, so none of its events can change that invariant. This does not
        // exclude equal-velocity pairs in a mixed moving fleet: an earlier
        // impact there can change a later pair's relative velocity.
        if (detector.Colliders.Count < 2) return;
        var velocity = detector.Colliders[0].Velocity;
        if (detector.Colliders.All(c =>
        {
            var d = c.Velocity - velocity;
            return d.X == 0 && d.Y == 0 && d.Z == 0;
        })) return;

        IReadOnlyList<CollisionEvent> events;
        using (TickProfiler.Enter("collision.ship.detect")) events = detector.Detect(dt, ParallelSectorSim.Enabled, ParallelSectorSim.ThreadCount);
        using var dispatchScope = TickProfiler.Enter("collision.ship.dispatch");
        var probeCache = new Separate.ProbeCache();
        LastShipShipResolved = 0;
        LastShipShipDebounced = 0;
        int dispatched = 0;
        foreach (var ev in events)
        {
            var ship1 = ev.A.Data as Ship;
            var ship2 = ev.B.Data as Ship;
            if (ship1 is null || ship2 is null) continue;   // only ship↔ship pairs interest this pass
            if (ReferenceEquals(ship1, ship2)) continue;
            if (ship1.Dead || ship2.Dead) continue;
            dispatched++;
            ResolveShipShipCollision(now, ev, ship1, ship2, probeCache);
        }
        LastShipShipEvents = dispatched;
    }

    private void ResolveShipShipCollision(double now, CollisionEvent ev, Ship ship1, Ship ship2, Separate.ProbeCache probeCache)
    {
        // Equal current velocities give zero relative normal speed for EVERY
        // separating normal. The existing approach gate below would return
        // without applying separation, damage, or events. Check at dispatch
        // (not detection): an earlier collision may have changed either ship.
        var relativeVelocity = ship1.Velocity - ship2.Velocity;
        if (relativeVelocity.X == 0 && relativeVelocity.Y == 0 && relativeVelocity.Z == 0) return;

        // Collision-time positions (unsmeared) — the rescue-gate proximity uses
        // these (the same convention the old code did).
        var rawPos1 = ship1.Position + ship1.Velocity * ev.TCollision;
        var rawPos2 = ship2.Position + ship2.Velocity * ev.TCollision;

        // P621 — the separating-plane normal and the separated positions come
        // from native Separate (Separate.SeparateColliders, shipIGC.cpp:514-616),
        // the convex-hull contact normal rather than the radial centre line. The
        // sign convention is unchanged: from ship2 toward ship1, so a rear ship
        // bumping the one ahead has v1 − v2 along +normal and the normal-speed
        // formulas below accelerate the forward ship.
        Separate.Result sep;
        using (TickProfiler.Enter("collision.ship.separate"))
            sep = Separate.SeparateColliders(ev.A, ev.B, ev.TCollision, fEqualBounce: true, ev.HtsA, ev.HtsB, probeCache);
        var normal = sep.Normal;

        // Mutual exclusivity with the rescue pass: a rescue-eligible pair is
        // skipped here (the rescue pass handles it) — native's rescue check
        // pre-empts physics via break (shipIGC.cpp:1027-1042).
        if (IsRescueEligiblePair(ship1, ship2, rawPos1, rawPos2)) return;

        var velocity1 = ship1.Velocity;
        var velocity2 = ship2.Velocity;

        // Velocity-projection debounce: bounce only while at least one ship is
        // approaching (relative approach (v1 − v2)·normal < 0) — the P254
        // debounce (Sector.cs:1985-1987) adapted to the two-body relative
        // velocity. After the bounce the relative normal velocity is positive,
        // so no re-slam this tick.
        if ((velocity1 - velocity2) * normal >= 0f)
        {
            LastShipShipDebounced++;
            return;
        }
        LastShipShipResolved++;

        float mass1 = ship1.Mass;                 // shipIGC.cpp:1095
        float mass2 = ship2.Mass;                 // shipIGC.cpp:1098

        // Elastic + inelastic blend (shipIGC.cpp:1093-1124): solve the inelastic
        // shared velocity and the one-dimensional elastic normal speeds, then
        // blend each body's velocity toward elasticity by cElasticity = 0.75.
        float oldSpeed1 = velocity1 * normal;                       // shipIGC.cpp:1084
        float oldSpeed2 = velocity2 * normal;                       // shipIGC.cpp:1085
        var vInelastic = velocity1 * (mass1 / (mass1 + mass2))
                       + velocity2 * (mass2 / (mass1 + mass2));     // shipIGC.cpp:1102-1103
        float newSpeed1 = (oldSpeed1 * (mass1 - mass2) + 2.0f * oldSpeed2 * mass2) / (mass1 + mass2);   // shipIGC.cpp:1107
        float newSpeed2 = (oldSpeed2 * (mass2 - mass1) + 2.0f * oldSpeed1 * mass1) / (mass1 + mass2);   // shipIGC.cpp:1108
        var perpendicular1 = velocity1 - oldSpeed1 * normal;        // shipIGC.cpp:1115
        var perpendicular2 = velocity2 - oldSpeed2 * normal;        // shipIGC.cpp:1116
        var vElastic1 = perpendicular1 + newSpeed1 * normal;        // shipIGC.cpp:1119
        var vElastic2 = perpendicular2 + newSpeed2 * normal;        // shipIGC.cpp:1120
        var newVelocity1 = vInelastic * (1.0f - CollisionElasticity) + vElastic1 * CollisionElasticity; // shipIGC.cpp:1122
        var newVelocity2 = vInelastic * (1.0f - CollisionElasticity) + vElastic2 * CollisionElasticity; // shipIGC.cpp:1124

        // Collision damage (shipIGC.cpp:1141-1145): the two-term split —
        // damage1 = 2·base1 + base2, damage2 = base1 + 2·base2 — generalising
        // P254's one-term form (there the rock's base2 ≈ 0 because it never moved).
        float base1 = (velocity1 - newVelocity1).LengthSquared() * ImpactDamageCoefficient;
        float base2 = (velocity2 - newVelocity2).LengthSquared() * ImpactDamageCoefficient;
        float damage1 = 2.0f * base1 + base2;
        float damage2 = base1 + 2.0f * base2;

        // P463c — collision signal. Native plays collisionSound + the camera
        // jiggle for BOTH ships, each with its own intensity folding the two
        // parties (shipIGC.cpp:1191,1195): party1 (this) = (2·damage1+damage2)·
        // c_impactJiggleCoefficient, party2 (pModel) = (damage1+2·damage2)·coeff.
        // Raised for every resolved collision even a friendly-lifepod one (the
        // sound/jiggle sit OUTSIDE native's damage-exemption branch,
        // shipIGC.cpp:1189-1195) — the intensities are functions of the velocity
        // change, already known before the exemption. Routed through whichever
        // party has a mission site. See IIgcSite.CollisionEvent.
        using (TickProfiler.Enter("collision.ship.event"))
            (ship1.Mission ?? ship2.Mission)?.Site.CollisionEvent(ship1, ship2, Id, sep.Position1,
                (2.0f * damage1 + damage2) * ImpactJiggleCoefficient,
                (damage1 + 2.0f * damage2) * ImpactJiggleCoefficient);

        // HP pools for the damage split (shipIGC.cpp:1147-1158): each is the hull
        // hit-points plus ShieldFraction × MaxStrength when a Shield is mounted —
        // the shield boost that makes a shielded ship survivable under repeated
        // bumps while an unshielded one takes real hull damage from the same hit.
        float hp1 = CollisionHpPool(ship1);
        float hp2 = CollisionHpPool(ship2);

        // Damage exemption (shipIGC.cpp:1166-1168): native skips damage only when
        // bFriendly AND either ship is a lifepod. bFriendly collapses to
        // same-side-only (no alliance concept). Crucially this is NOT P254's
        // unconditional lifepod gate — an enemy lifepod still takes real damage.
        bool bFriendly = ship1.SideId == ship2.SideId;
        bool damageExempt = bFriendly
            && (ship1.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                || ship2.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));

        DamageResult dr1 = DamageResult.NoDamage;
        DamageResult dr2 = DamageResult.NoDamage;
        if (!damageExempt)
        {
            if (hp2 > 0f)
                dr1 = ship1.ReceiveDamage(CollisionDamageType, hp2 * damage1 / (hp2 + damage1),
                    now, sep.Position1, sep.Position2, launcher: ship2);   // shipIGC.cpp:1172-1176
            if (hp1 > 0f)
                dr2 = ship2.ReceiveDamage(CollisionDamageType, hp1 * damage2 / (hp1 + damage2),
                    now, sep.Position2, sep.Position1, launcher: ship1);   // shipIGC.cpp:1181-1185
        }

        // Death handling (P254 pattern, Sector.cs:2018-2027): if either ship was
        // killed by the impact, skip the correction and do the port's death
        // bookkeeping instead.
        if (dr1 == DamageResult.Killed || dr2 == DamageResult.Killed)
        {
            if (dr1 == DamageResult.Killed) Terminate(ship1);
            if (dr2 == DamageResult.Killed) Terminate(ship2);
            return;
        }

        // Position correction — Separate has already pushed BOTH bodies apart
        // symmetrically (fEqualBounce = true, half-and-half, shipIGC.cpp:526,
        // 610-615) along the convex-hull contact normal to just-touching + 0.5
        // clearance (s = distanceBest − 0.5, shipIGC.cpp:608). Apply it.
        ship1.Position = sep.Position1;
        ship2.Position = sep.Position2;

        ship1.Velocity = newVelocity1;            // shipIGC.cpp:1161
        ship2.Velocity = newVelocity2;            // shipIGC.cpp:1164
    }

    /// <summary>
    /// P466 — the <b>ship ↔ station elastic collision</b> pass, run once per
    /// sector tick (called from <see cref="Update"/> alongside
    /// <see cref="ResolveShipAsteroidCollisions"/> and
    /// <see cref="ResolveShipShipCollisions"/>). A ship that overlaps a station
    /// is bounced off it exactly as P254 bounces a ship off an asteroid: the
    /// station's mass is treated as infinite, so only the ship's velocity changes
    /// — reflect the normal component and scale by <c>cElasticity = 0.75</c> — plus
    /// real impact damage to <b>both</b> ship and station via the native
    /// <c>|Δv|² · (1/128)</c> metric. Before P466 the port had no ship-vs-station
    /// physics at all: a ship flew straight through an enemy base (stations were
    /// only ever <em>projectile</em> colliders in <see cref="ResolveCombatCollisions"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Native provenance.</b> <c>CshipIGC::HandleCollision</c> has an
    /// <c>OT_station</c> case (<c>shipIGC.cpp:719-868</c>): it tries to dock a
    /// friendly land-capable ship, rescue a friendly lifepod, or board a
    /// shield-down enemy base first; a collision that takes none of those paths
    /// falls through (no <c>break</c>) into the <c>OT_buildingEffect →
    /// OT_asteroid → OT_probe</c> shared bounce block
    /// (<c>shipIGC.cpp:868-960</c>) — the same block P254 ported for asteroids.
    /// That shared block is the faithful source of the infinite-mass elastic
    /// bounce (0.75) and the <c>damage2 = |Δv|²·c_impactDamageCoefficient</c>,
    /// <c>damage1 = 2·damage2</c> split against the two hit-point pools
    /// (<c>shipIGC.cpp:760-790</c>); a lifepod bounces but neither deals nor takes
    /// damage (<c>shipIGC.cpp:803</c>). This pass reproduces it for stations,
    /// reusing the exact constants and helpers the asteroid pass introduced
    /// (<see cref="CollisionElasticity"/>, <see cref="ImpactDamageCoefficient"/>,
    /// <see cref="CollisionDamageType"/>, <see cref="CollisionHpPool"/>).</para>
    /// <para><b>Integration shape.</b> Identical to the asteroid pass — a
    /// separate per-tick resolver with its own <see cref="CollisionDetector"/>
    /// (ships moving, stations static spheres of <see cref="Station.Radius"/>),
    /// deliberately independent of the ordnance-gated <see cref="ResolveCombatCollisions"/>,
    /// so a ship can bounce off a base in an otherwise-empty sector. Docked ships
    /// (<see cref="Ship.StationId"/> set) are not active collision bodies and are
    /// skipped, exactly as the ship-ship pass skips them.</para>
    /// <para><b>Dock / rescue / board exclusion (the "don't break docking"
    /// gate).</b> The native <c>OT_station</c> case pre-empts physics for the
    /// pairs it would dock, rescue or board. The port detects a friendly landing
    /// in <see cref="ResolveDockCollisions"/> as <em>any</em> same-side ship
    /// within <see cref="DockRadius"/> of the station's centre — a
    /// capability-agnostic simplification of native's land-capability + friendly
    /// test (<c>shipIGC.cpp:727-740</c>). Because <see cref="DockRadius"/> (200)
    /// is routinely <em>smaller</em> than a base's collision radius (302-540 in
    /// PCore001), a dock-bound ship must overlap the hull to reach its dock point;
    /// a bounce at the hull surface would therefore make friendly docking at any
    /// large base impossible. So the pass excludes <b>every same-side pair</b>
    /// here (the port's own friendly-dock gate) and lets
    /// <see cref="ResolveDockCollisions"/> / <see cref="ResolveStationRescueEvents"/>
    /// handle it — no friendly ship is ever bounced off its own base. The rescue
    /// case (same-side lifepod at a <c>c_sabmRescue</c> station) is subsumed by
    /// that same-side exclusion. Separately, a <b>different-side</b>
    /// <c>c_habmBoard</c> hull with the station's shields <em>down</em> boards /
    /// captures the base (native <c>shipIGC.cpp:849-858</c>; the port's capture
    /// path in <see cref="ResolveDockCollisions"/>, <c>Sector.cs:3133-3152</c>) and
    /// is likewise excluded, so an infiltrator is not knocked off a downed base
    /// before it can dock. Everything else — an enemy ship, an enemy lifepod, a
    /// board hull with shields up — bounces.</para>
    /// <para><b>Debounce.</b> The P254 velocity-projection debounce: only an
    /// approaching ship (<c>v · n &lt; 0</c>) bounces, so a resting ship inside a
    /// base (freshly placed, settling) is never hammered, and after a bounce
    /// <c>v · n &gt; 0</c> prevents a same-tick re-slam. The separation push clears
    /// the overlap so an outgoing ship is not re-detected.</para>
    /// <para><b>Death handling.</b> Identical to P254: if either
    /// <c>ReceiveDamage</c> returns <c>Killed</c>, the position/velocity
    /// correction is skipped and the port's death bookkeeping applies instead. A
    /// ship death is the plain <c>_models.Remove</c> (the P254 ship-death
    /// handling); a <b>station</b> death reuses the combat pass's station-kill
    /// bookkeeping (<c>Sector.cs:1853-1877</c>) — <see cref="Sector.RemoveStation"/>
    /// plus the host <see cref="IIgcSite.KillStationEvent"/> seam and the P191
    /// victory recompute — routed through the ramming ship's mission so the host
    /// can broadcast STATION_DESTROYED and vacate docked ships.</para>
    /// <para><b>Client-side effects</b> (collision sound / FF / camera jiggle)
    /// are out of scope — P463c may feed this resolver's event. The
    /// position/velocity correction rides real server updates to the client.</para>
    /// </remarks>
    private void ResolveShipStationCollisions(double now, float dt)
    {
        if (_models.Count == 0 || _stationRuntimes.Count == 0) return;

        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            if (model is not Ship s) continue;
            if (s.Dead) continue;
            if (s.Mission is null || s.StationId is not null) continue;  // flying placed ships only — a docked ship is not a collision body
            detector.Add(new Collider(s, s.Position, s.Velocity, s.Orientation,
                CollisionHulls?.Resolve(s.HullType.ModelName), 0.5f * s.HullType.Length, isStatic: false, HitShape.Sphere));
        }
        foreach (var st in _stationRuntimes)
        {
            if (st.Data.Position is not { Length: 3 } sp) continue;   // no position: can't collide
            detector.Add(new Collider(st, new Vector3(sp[0], sp[1], sp[2]), Vector3.Zero,
                Orientation.Identity, CollisionHulls?.Resolve(st.Type.ModelName), st.Radius, isStatic: true, HitShape.Sphere));
        }

        IReadOnlyList<CollisionEvent> events;
        using (TickProfiler.Enter("collision.station.detect")) events = detector.Detect(dt, ParallelSectorSim.Enabled, ParallelSectorSim.ThreadCount, static (a, b) =>
            (a.Data is Ship && b.Data is Station) || (b.Data is Ship && a.Data is Station));
        using var dispatchScope = TickProfiler.Enter("collision.station.dispatch");
        foreach (var ev in events)
        {
            var ship = ev.A.Data as Ship ?? ev.B.Data as Ship;
            var station = ev.A.Data as Station ?? ev.B.Data as Station;
            if (ship is null || station is null) continue;   // only ship↔station pairs interest this pass
            if (ReferenceEquals(ship, station)) continue;
            if (ship.Dead || station.Dead) continue;
            ResolveShipStationCollision(now, ev, ship, station);
        }
    }

    /// <summary>
    /// P466 — resolve a single detected ship↔station overlap: bounce the ship off
    /// the infinite-mass station (reflect the normal component, scale by
    /// <see cref="CollisionElasticity"/>), and apply the native collision damage
    /// to both bodies. Port of the shared bounce block native reaches for a
    /// non-docking ship hitting a station (<c>shipIGC.cpp:758-830</c>,
    /// <c>868-960</c>), reusing the P254 physics/damage helpers verbatim with the
    /// station playing the role of the static rock.
    /// </summary>
    private void ResolveShipStationCollision(double now, CollisionEvent ev, Ship ship, Station station)
    {
        var apos = station.Center;

        // Dock / rescue / board exclusion — see the pass remarks. A same-side pair
        // (the port's friendly-dock gate) is handled by ResolveDockCollisions /
        // ResolveStationRescueEvents; a different-side shield-down board hull is
        // handled by the capture path. Neither may be bounced or it would break
        // docking / rescue / capture.
        if (IsDockOrBoardCandidate(ship, station)) return;

        // Native gate: bounce only while the station is alive (GetFraction() > 0,
        // shipIGC.cpp:742). A destroyed base is a corpse the ship flies through.
        if (station.ArmorFraction <= 0f) return;

        // P621 — the separating-plane normal and the collision-time position come
        // from native Separate (Separate.SeparateColliders, shipIGC.cpp:514-616):
        // the convex-hull contact normal, not a radial centre-to-centre line (the
        // spherical-station reduction). For a sphere fallback (no .cvh) it
        // degenerates to the radial direction, so pre-P621 behaviour is unchanged.
        bool shipIsA = ReferenceEquals(ev.A.Data, ship);
        var shipC = shipIsA ? ev.A : ev.B;
        var stC = shipIsA ? ev.B : ev.A;
        var htsShip = shipIsA ? ev.HtsA : ev.HtsB;
        var htsSt = shipIsA ? ev.HtsB : ev.HtsA;

        var sep = Separate.SeparateColliders(shipC, stC, ev.TCollision, fEqualBounce: false, htsShip, htsSt);
        var normal = sep.Normal;
        var position1 = sep.Position1;

        var velocity1 = ship.Velocity;

        // Velocity-based debounce: only an approaching ship (v·n < 0) bounces.
        // A resting ship inside a base is never hammered; after a bounce v·n > 0.
        if (velocity1 * normal >= 0f) return;

        // Elastic bounce — infinite-mass station (shipIGC.cpp:760-778): reflect
        // the normal component, keep the tangent, scale by cElasticity = 0.75.
        float oldSpeed1 = velocity1 * normal;
        var perpendicular1 = velocity1 - oldSpeed1 * normal;
        var vElastic1 = perpendicular1 - (oldSpeed1 * normal);     // reflection of the normal component
        var newVelocity1 = vElastic1 * CollisionElasticity;

        // Real impact damage (shipIGC.cpp:789-790): damage2 = |Δv|² · (1/128),
        // damage1 = 2·damage2, split against the counterpart's hit-point pool.
        float damage2 = (velocity1 - newVelocity1).LengthSquared() * ImpactDamageCoefficient;
        float damage1 = 2.0f * damage2;

        // P463c — collision signal. Native plays collisionSound + the camera
        // jiggle for the SHIP only in this shared bounce block (the base is
        // never a camera): intensity = 2·damage1·c_impactJiggleCoefficient
        // (shipIGC.cpp:836). Raised for every resolved ram, even a lifepod's
        // (the sound/jiggle sit OUTSIDE native's damage gate) — the intensity is
        // a function of the velocity change, already known before ReceiveDamage.
        // party2 is null (a station: no audience, no jiggle); the wire carries
        // its id as NA(-1). See IIgcSite.CollisionEvent / P466.
        if (ship.Mission is { } m)
            m.Site.CollisionEvent(ship, null, Id, position1,
                2.0f * damage1 * ImpactJiggleCoefficient, 0f);

        float hp1 = CollisionHpPool(ship);   // ship effective collision-HP pool (incl. shield term — P254/P354 convention)
        float hp2 = station.ArmorFraction * station.MaxArmor;   // station current armor HP (IdamageIGC GetHitPoints)

        DamageResult dr1 = DamageResult.NoDamage;
        DamageResult dr2 = DamageResult.NoDamage;

        // Lifepod gate (shipIGC.cpp:803): a lifepod bounces but neither deals nor
        // takes damage — the same unconditional c_habmLifepod gate P254 applies.
        bool lifepod = ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        if (!lifepod)
        {
            if (hp2 > 0f)
                dr1 = ship.ReceiveDamage(CollisionDamageType, hp2 * damage1 / (hp2 + damage1),
                    now, position1, apos, launcher: null);
            if (hp1 > 0f)
                dr2 = station.ReceiveDamage(CollisionDamageType, hp1 * damage2 / (hp1 + damage2),
                    now, apos, position1, launcher: null);
        }

        // Death handling (P254 pattern): if either body was killed by the impact,
        // skip the correction and do the port's death bookkeeping instead. A ship
        // death reuses P254's _models.Remove; a STATION death reuses the combat
        // pass's station-kill bookkeeping (RemoveStation + KillStationEvent + P191
        // victory recompute, Sector.cs:1853-1877) routed through the ramming
        // ship's mission, so the host broadcasts STATION_DESTROYED and vacates
        // docked ships exactly as a weapon kill does.
        if (dr1 == DamageResult.Killed || dr2 == DamageResult.Killed)
        {
            if (dr1 == DamageResult.Killed) Terminate(ship);
            if (dr2 == DamageResult.Killed)
            {
                RemoveStation(station);
                if (ship.Mission is { } sm)
                {
                    float stationShare = hp1 * damage2 / (hp1 + damage2);
                    sm.Site.KillStationEvent(station, ship, stationShare);
                    var (gWinner, gChanged) = sm.RecomputeConquestAndTerritory(null);
                    if (gChanged || gWinner is not null)
                        sm.Site.VictoryByStationsComputed(sm, gChanged, gWinner);
                    sm.Site.SideLostRestartStation(sm, station.SideId);
                }
            }
            return;
        }

        // Separate: push the ship out to just-touching + native's 0.5 clearance
        // (s = distanceBest − 0.5, shipIGC.cpp:601). Separate already displaced
        // position1 along the convex-hull contact normal; apply it and the
        // reflected velocity.
        ship.Position = position1;
        ship.Velocity = newVelocity1;
    }

    /// <summary>
    /// P466 — whether a ship↔station pair is one the port's DOCK, RESCUE or BOARD
    /// passes handle (and so must be excluded from the ship↔station bounce, lest
    /// the bounce pre-empt an existing behaviour). This is the port-side mapping
    /// of native's <c>OT_station</c> special cases that precede the shared bounce
    /// (<c>shipIGC.cpp:719-868</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Dock / rescue — same side.</b> Native pre-empts physics for a
    /// friendly land-capable ship (dock, <c>shipIGC.cpp:727-740</c>) and a
    /// friendly lifepod at a <c>c_sabmRescue</c> station (rescue,
    /// <c>shipIGC.cpp:861-867</c>). The port detects a friendly landing as
    /// <em>any</em> same-side ship within <see cref="DockRadius"/> of a station
    /// (<see cref="ResolveDockCollisions"/> — capability-agnostic, a documented
    /// simplification), so "would dock" collapses to "same side". Because a base's
    /// collision radius commonly exceeds <see cref="DockRadius"/> (a dock-bound
    /// ship must overlap the hull to reach its dock point), the exclusion must be
    /// blanket over same-side pairs or no large base could ever be docked at. This
    /// is the same-side collapse every AlliedSides / friendly gate in this port
    /// makes (there is no alliance concept).</para>
    /// <para><b>Board — different side, shields down.</b> A different-side
    /// <c>c_habmBoard</c> hull with the station's shields below
    /// <see cref="DownedShieldFraction"/> boards / captures the base (native
    /// <c>shipIGC.cpp:849-858</c>; the port's capture path in
    /// <see cref="ResolveDockCollisions"/>, <c>Sector.cs:3133-3152</c>) and is
    /// excluded so an infiltrator reaches its dock point. With shields up it is
    /// not a board candidate and bounces like any other enemy ship.</para>
    /// <para><b>Side-less ships are excluded outright.</b> Every real ship is
    /// sided; a <see langword="null"/>-side ship is an inert synthetic / test body
    /// (e.g. a physics test ship placed deep inside a base) that neither docks nor
    /// attacks a base, so it is never bounced — zero gameplay cost, and it keeps
    /// the pass from disturbing such bodies.</para>
    /// </remarks>
    private bool IsDockOrBoardCandidate(Ship ship, Station station)
    {
        // DOCK / RESCUE: the port's friendly-dock gate is same-side-only.
        if (ship.SideId is { } side && side == station.SideId) return true;

        // A SIDE-LESS ship is excluded outright: every real ship is sided, so a
        // null-side ship is an inert synthetic / test body (e.g. a physics test
        // ship placed deep inside a base) that must not be bounced off a base it
        // neither docks at nor attacks. This is the port's protection for such
        // bodies — it carries zero gameplay cost because no real ship is side-less.
        if (ship.SideId is null) return true;

        // BOARD: only a different-side board hull with the station's shields DOWN.
        if (!ship.HullType.HasCapability(DataHullTypeIGC.HabmBoard)) return false;
        if (station.ShieldFraction >= DownedShieldFraction) return false;
        return true;
    }

    /// <summary>
    /// P354 — the port of the native rescue-check half of the ship-ship collision
    /// special cases (<c>shipIGC.cpp:1027-1042</c>), re-verified inside
    /// <see cref="ResolveShipShipCollision"/> so a rescue-eligible pair never
    /// also takes a physics bump/damage the same tick. Mirrors the predicate
    /// <see cref="ResolveRescueEvents"/> uses verbatim (same side, exactly one
    /// lifepod with the other rescue-capable, in proximity).
    /// </summary>
    private static bool IsRescueEligiblePair(Ship a, Ship b, Vector3 pa, Vector3 pb)
    {
        // Friendly gate (shipIGC.cpp:1023-1026): same side only.
        if (a.SideId != b.SideId) return false;

        // Symmetric capability match (shipIGC.cpp:1027-1042): exactly one is the
        // lifepod, the other is rescue-capable.
        bool aPod = a.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        bool bPod = b.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        if (aPod == bPod) return false;
        bool aCanRescue = a.HullType.HasCapability(DataHullTypeIGC.HabmRescue);
        bool bCanRescue = b.HullType.HasCapability(DataHullTypeIGC.HabmRescue);
        if (!((aPod && bCanRescue) || (bPod && aCanRescue))) return false;

        // Proximity: sum of the two 0.5×Length sphere radii (the same extent
        // convention and threshold as ResolveRescueEvents, Sector.cs:860-868).
        // Evaluated at the collision-time positions so it is consistent with the
        // swept detection that triggered this pair.
        float ra = 0.5f * a.HullType.Length;
        float rb = 0.5f * b.HullType.Length;
        float threshold = ra + rb;
        float dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
        return dx * dx + dy * dy + dz * dz <= threshold * threshold;
    }

    /// <summary>
    /// P354 — the port of the native collision HP-pool computation: the ship's
    /// current hull hit-points plus, when a <c>Shield</c> part is mounted,
    /// <c>ShieldFraction × MaxStrength</c>. This boosted pool is what makes a
    /// shielded ship able to absorb repeated bumps (each collision's damage is
    /// split against the counterpart's pool), so an unshielded ship takes
    /// proportionally more real hull damage from the same collision.
    /// </summary>
    /// <remarks>
    /// The additive form is taken from native's <b>ship↔ship</b> branch
    /// (<c>shipIGC.cpp:1147-1151</c>): <c>hp1 = GetHitPoints(); if (shield)
    /// hp1 += fraction × maxStrength;</c> — the "+=" sum of hull HP and the
    /// shield term, carrying native's own corrective comment ("terralthra fix,
    /// this was an = should be a +="). Both collision call sites use it.
    /// <para>By contrast native's <b>static</b>-body branch (ship↔asteroid /
    /// station / building-effect, <c>shipIGC.cpp:962-965</c>) still uses the
    /// OLD shield-only (<c>=</c>) <em>replacement</em> form: <c>hp1 = fraction ×
    /// maxStrength</c>, discarding the hull term — an unremediated leftover of
    /// the original "=" bug the ship↔ship branch later fixed. The port
    /// deliberately does NOT reproduce that inconsistency; it uses the single
    /// corrected additive formula for both branches (decision + rationale:
    /// <c>P408_ASTEROID_COLLISION_HP_POOL_DECISION.md</c>).</para>
    /// </remarks>
    private static float CollisionHpPool(Ship ship)
    {
        float hp = ship.HitPoints;
        foreach (var part in ship.Parts)
        {
            if (part.EquipmentType != EquipmentType.Shield) continue;
            if (part.Type.Type is DataPartTypeIGC.Shield shieldType)
                hp += ship.ShieldFraction * ship.EffectiveMaxShieldStrength(shieldType);
            break;
        }
        return hp;
    }

    /// <summary>
    /// P125 — tick each runtime station's health regeneration. Stations are
    /// static bodies (not <see cref="Model"/>s), so their regen is a dedicated
    /// pass run once per sector tick (called from <see cref="Update"/>) rather
    /// than a model <see cref="Model.Update"/>; native folds the identical math
    /// into <c>CstationIGC::Update</c> (<c>stationIGC.h:189-213</c>). The per-tick
    /// armor-then-shield sequencing lives in <see cref="Station.Regenerate"/>.
    /// </summary>
    private void ResolveStationRegen(float dt)
    {
        foreach (var st in _stationRuntimes)
            st.Regenerate(dt);
    }

    /// <summary>
    /// The <b>mine attrition</b> proximity pass, run once per sector tick after
    /// the combat pass — the port of the native minefield behaviour in
    /// <c>CmineIGC::HandleCollision</c> (<c>src/Igc/mineigc.h:68-145</c>), which
    /// this task converts from the one-shot proximity bomb of P95/P96 into the
    /// native's <b>persistent area denial</b>: an armed, hostile-side mine chews
    /// on <em>every</em> moving enemy ship inside its radius for as long as the
    /// ship is inside it, wearing itself down as it deals damage, and persisting
    /// across hits and ships until its endurance is exhausted (or it expires).
    /// </summary>
    /// <remarks>
    /// <para><b>Integration shape.</b> Native enters this via a swept
    /// <see cref="CollisionDetector"/> hit that yields the <c>tCollision</c>
    /// entry time, then integrates the full in-field dwell of that one event
    /// (<c>mineigc.h:84-113</c>). This port keeps its existing point-in-sphere
    /// proximity pass (a mine is a static, velocity-less body) and instead
    /// discretises the native integral per tick: every tick that a qualifying
    /// ship is found inside the sphere (<c>|ship − mine| ≤ rMe + 0.5·length</c>),
    /// it solves the <b>same native quadratic</b> against the ship's motion over
    /// that tick to find the in-field time, clamped to <c>[0, dt]</c>. Summed
    /// over a full transit the per-tick partials reproduce the native dwell
    /// integral, so the total damage a ship takes crossing the field matches
    /// native within the per-tick discretisation error — a deliberate, documented
    /// simplification (the port has no swept position feed here, exactly the
    /// rationale the old single-hit pass already recorded).</para>
    /// <para><b>Arm delay &amp; side gate.</b> Unchanged: a mine cannot trigger
    /// before <c>now ≥ DeployTime + Mine.ArmTimeSeconds</c>
    /// (<c>c_dtArmTime = 1.0f</c>, <c>mineigc.h:75,77</c>), and it never damages
    /// a ship on its own <see cref="Ship.SideId"/> — engaging only when <em>both</em>
    /// carry a side and they differ (same convention as
    /// <see cref="ResolveCombatCollisions"/>; native <c>mineigc.h:77</c>).</para>
    /// <para><b>Velocity gate.</b> A ship must be <b>moving</b> to be affected:
    /// <c>|v|² &gt; 1</c> (<c>mineigc.h:80-82</c>). A ship parked inside an armed
    /// field takes <b>zero</b> damage and the field survives — the core semantic
    /// the old one-shot port got wrong.</para>
    /// <para><b>Dwell &amp; damage.</b> The in-field time for the tick is the
    /// exit root of the native sphere-intersection quadratic
    /// (<c>tExit = (halfB + s)/a</c>, with <c>dP = pMe − pHim</c> so the sign is
    /// native's; <c>mineigc.h:84-113</c>), clamped to <c>[0, dt]</c>. The wear the
    /// field suffers is <c>amount = dt·speed²·Power·rHim²·c_damageMultiplier/(2·rMe)</c>
    /// (<c>mineigc.h:116-118</c>, <c>c_damageMultiplier = 1/(5·5·100)</c>), where
    /// <c>rMe = MineType.MineRadius</c> is the mine's collision radius
    /// (<c>SetRadius(dataMineType-&gt;radius)</c>, <c>mineigc.cpp:106</c> — the
    /// mine-specific field, native <c>DataMineTypeIGC::radius</c> at
    /// <c>igc.h:1992</c>, port byte offset 464 → <see cref="DataMineTypeIGC.MineRadius"/>,
    /// NOT the base model <see cref="DataMineTypeIGC.Radius"/> at offset 16) and
    /// <c>rHim</c> is the ship's half-length radius. The field then decays through
    /// the shared native endurance math (<see cref="Mine.TakeAttritionDamage"/>,
    /// <c>mineigc.h:120-128</c>) and the ship takes the derived <c>damage</c>
    /// (<c>oldEndurance − newEndurance</c>).</para>
    /// <para><b>Worst-hit-per-tick application.</b> The ship does not take the hit
    /// immediately. Mirroring native <c>AddMineDamage</c>/<c>ApplyMineDamage</c>
    /// (<c>shipIGC.h:1998-2035</c>, applied at <c>clusterIGC.cpp:334</c>), each
    /// ship records its <b>largest</b> mine hit this tick and applies it exactly
    /// once, walking the ship list back to front (children before parents,
    /// <c>clusterIGC.cpp:325-335</c>), through the reused
    /// <see cref="Ship.ReceiveDamage"/> — shields and the armor matrix included.
    /// No area <see cref="Explosion"/> is created; a mine never blasts.</para>
    /// <para><b>Ordering deviation, stated plainly (P762b).</b> Native's order is
    /// collisions → mines → <c>Move</c> (<c>clusterIGC.cpp:322-345</c>). This
    /// port performs a ship's <c>Move</c> inside <see cref="Ship.OnUpdate"/>
    /// during the models loop, which runs <em>before</em> the collision passes;
    /// the mine pass now runs after every collision pass (P762b moved it), so
    /// collisions precede mines as native requires, but a ship killed by the
    /// mine has already moved once this tick. P762's claim that a mine-killed
    /// ship never moves is withdrawn (see the P762b report).</para>
    /// <para><b>Field lifetime.</b> The mine persists across hits and ships — any
    /// number of ships in the same tick all take damage. When its remaining
    /// endurance drops below 1 (<c>newEndurance &lt; 1.0f</c>, <c>mineigc.h:139-142</c>)
    /// it is destroyed and <see cref="IIgcSite.MineDestroyedEvent"/> is raised
    /// through the same path every other removal uses. Expiry by
    /// <see cref="Mine.TimeExpire"/> is unchanged.</para>
    /// <para><b>Mutation safety.</b> Iterates a snapshot, so a mine destroyed
    /// mid-pass cannot invalidate the walk; once destroyed it is skipped for the
    /// rest of the tick (no further ships hit a spent field).</para>
    /// </remarks>
    private void ResolveMineDetonations(double now, float dt)
    {
        // Native applies mine damage inside the `activeF && bStarted` block
        // (clusterIGC.cpp:255 gates the mine loop at :322-335; bStarted is also
        // required by the enclosing stage guard at :103).
        if (!Active || Mission?.Stage != MissionStage.Started) return;
        if (!_models.Any(m => m is Mine)) return;   // nothing to chew on

        var snapshot = _models.ToArray();

        // Worst-hit-per-tick accumulation (native AddMineDamage, shipIGC.h:1998-2016):
        // each ship records its largest mine hit this tick, applied once at the
        // end of the pass (native ApplyMineDamage, shipIGC.h:2017-2035 /
        // clusterIGC.cpp:334).
        var worst = new Dictionary<Ship, MineHit>();

        foreach (var model in snapshot)
        {
            if (model is not Mine mine) continue;
            if (now < mine.DeployTime + Mine.ArmTimeSeconds) continue;  // not armed (mineigc.h:75,77)

            // rMe = the mine's collision/model radius — native
            // SetRadius(dataMineType->radius) (mineigc.cpp:106), the
            // mine-specific DataMineTypeIGC::radius (igc.h:1992, port offset 464
            // → MineRadius), NOT the base model Radius at offset 16.
            float rMe = mine.MineType.MineRadius;

            foreach (var candidate in snapshot)
            {
                if (candidate is not Ship ship) continue;
                if (ship.Dead) continue;                        // can't hurt the dead
                if (ReferenceEquals(ship, mine.Launcher)) continue; // never hit your own launcher

                // Friendly-fire gate (mineigc.h:77): a mine never damages a ship
                // on its own SideId, engaging only when both carry a side.
                if (mine.SideId is { } mineSide && ship.SideId == mineSide) continue;

                // Velocity gate (mineigc.h:80-82): |v|² > 1 — a parked ship is
                // immune even while inside the field.
                float vx = ship.Velocity.X, vy = ship.Velocity.Y, vz = ship.Velocity.Z;
                float a = vx * vx + vy * vy + vz * vz;
                if (a <= 1f) continue;

                // Point-in-sphere overlap at tick start: |ship − mine| ≤ rMe + rHim
                // (ship radius = half-length, shipIGC.cpp:1419).
                float rHim = 0.5f * ship.HullType.Length;
                float r = rMe + rHim;
                float dx = ship.Position.X - mine.Position.X;
                float dy = ship.Position.Y - mine.Position.Y;
                float dz = ship.Position.Z - mine.Position.Z;
                float dP2 = dx * dx + dy * dy + dz * dz;
                if (dP2 > r * r) continue;

                // In-field dwell for THIS tick — the native quadratic
                // (mineigc.h:84-113), solved with the ship inside at tick start
                // (entry tCollision = 0), clamped to [0, dt]. dP = mine − ship
                // (sense reversed wrt the velocity, exactly native's comment at
                // mineigc.h:90), so halfB = dP·v carries no extra negation.
                float pmx = mine.Position.X, pmy = mine.Position.Y, pmz = mine.Position.Z;
                double halfB = (double)(pmx - ship.Position.X) * vx
                             + (double)(pmy - ship.Position.Y) * vy
                             + (double)(pmz - ship.Position.Z) * vz;
                double c = dP2 - (double)r * r;
                double b2ac = halfB * halfB - (double)a * c;
                if (b2ac < 0) continue;
                double s = Math.Sqrt(b2ac);
                float tExit = (float)((halfB + s) / a);
                if (tExit <= 0f) continue;
                float inFieldDt = tExit < dt ? tExit : dt;   // clamp to [0, dt]

                // Wear the field + derive the ship's damage (mineigc.h:116-128),
                // sharing ReduceStrength's exponential decay. c_damageMultiplier = 1/(5·5·100).
                const float c_damageMultiplier = 1.0f / (5.0f * 5.0f * 100.0f);
                float amount = inFieldDt * a * mine.MineType.Power * rHim * rHim * c_damageMultiplier / (2.0f * rMe);
                float damage = mine.TakeAttritionDamage(amount);
                if (damage <= 0f) continue;

                // Record the ship's WORST mine hit this tick (AddMineDamage).
                if (!worst.TryGetValue(ship, out var cur) || damage > cur.Damage)
                {
                    // Native P1/P2 (mineigc.h:129-132), with tCollision = 0 because
                    // the ship is inside the field at tick start:
                    //   position1 = pHim (+ dV·tCollision ≡ pHim)
                    //   position2 = position1 + dV·((c_radiusPlacement + rHim)/|dV|)
                    //               + Vector::RandomPosition(c_radiusPlacement)
                    // where c_radiusPlacement = 10. The deterministic random offset
                    // (sim-determinism) replaces native's global RandomPosition.
                    Vector3 p1 = ship.Position;
                    const float c_radiusPlacement = 10.0f;
                    float vLen = MathF.Sqrt(a);
                    Vector3 p2 = p1 + new Vector3(vx, vy, vz) * ((c_radiusPlacement + rHim) / vLen)
                                  + DeterministicRandomPosition(c_radiusPlacement, now, ship.Id, mine.Id);
                    worst[ship] = new MineHit(
                        mine,
                        mine.MineType.DamageType,
                        damage,
                        now,
                        p1,
                        p2,
                        mine.Launcher);
                }

                // A spent field is not scanned further this tick.
                if (!_models.Contains(mine)) break;
            }
        }

        // Apply each ship's worst mine hit once (ApplyMineDamage) — through the
        // reused Ship.ReceiveDamage, never an area Explosion. Mirroring native
        // ApplyMineDamage (shipIGC.h:2017-2025), the per-ship 0.5 s mine-pop
        // throttle (m_timeLastMineExplosion) first decides whether this hit
        // plays the c_etMine explosion — at the hit's native P2 placement —
        // raised as IIgcSite.MineHitEvent for the host to broadcast.
        // Walk the ship list BACK TO FRONT, exactly native's `for (l = m_ships.last();
        // l; l = lTxen)` (clusterIGC.cpp:325-335): children follow parents in the
        // list, so applying a child's hit before its parent's means killing the
        // parent can never leave us applying damage to an entry that a death
        // handler already retired. The snapshot preserves the list order without
        // allocating on this pass beyond the array the detection already took.
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i] is not Ship ship) continue;
            if (!worst.TryGetValue(ship, out var hit)) continue;
            if (!_models.Contains(ship)) continue;   // a child's death handler may retire it
            if (hit.Time > ship.TimeLastMineExplosion + 0.5f)
            {
                ship.TimeLastMineExplosion = hit.Time;
                ship.Mission?.Site.MineHitEvent(hit.Mine, hit.P1, hit.P2);
            }
            ship.ReceiveDamage(hit.Type, hit.Damage, hit.Time, hit.P1, hit.P2, hit.Launcher);
        }
    }

    /// <summary>A single ship's worst mine hit in a tick — the port of native
    /// <c>AddMineDamage</c>'s <c>m_maxMine*</c> fields (<c>shipIGC.h:1998-2016</c>):
    /// the largest <see cref="Damage"/> from any mine this tick, with its
    /// <see cref="Type"/>, <see cref="Launcher"/>, the <see cref="Mine"/> that
    /// dealt it and the native P1/P2 placements, applied once by
    /// <see cref="ResolveMineDetonations"/>.</summary>
    private readonly record struct MineHit(
        Mine Mine, byte Type, float Damage, double Time, Vector3 P1, Vector3 P2, Model? Launcher);

    /// <summary>
    /// P574 — a deterministic uniform point within a sphere of
    /// <paramref name="radius"/>, the sim-deterministic replacement for native
    /// <c>Vector::RandomPosition(radius)</c> used by the mine-hit P2 placement
    /// (<c>mineigc.h:132</c>). Seeded from the hit time plus the ship and mine
    /// ids so two ticks with the same inputs reproduce the same offset (the sim
    /// is deterministic) while distinct hits scatter. Marsaglia direction times a
    /// cube-root-biased radial distance for uniform volume density, matching
    /// native RandomPosition's uniform-in-ball distribution.
    /// </summary>
    private static Vector3 DeterministicRandomPosition(float radius, double seedTime, int shipId, int mineId)
    {
        ulong s = (ulong)(long)BitConverter.DoubleToInt64Bits(seedTime)
                  ^ (ulong)(uint)shipId * 0x9E3779B97F4A7C15UL
                  ^ (ulong)(uint)mineId * 0xBF58476D1CE4E5B9UL;
        if (s == 0UL) s = 0x9E3779B97F4A7C15UL;
        float u1 = NextSplitMix(ref s);
        float u2 = NextSplitMix(ref s);
        float u3 = NextSplitMix(ref s);
        float theta = u1 * MathF.PI * 2f;
        float z = 2f * u2 - 1f;
        float sr = MathF.Sqrt(Math.Max(0f, 1f - z * z));
        float r = radius * MathF.Cbrt(u3);
        return new Vector3(sr * MathF.Cos(theta) * r, sr * MathF.Sin(theta) * r, z * r);
    }

    // SplitMix64 step → uniform float in [0,1). Deterministic, small, adequate.
    private static float NextSplitMix(ref ulong s)
    {
        s += 0x9E3779B97F4A7C15UL;
        ulong z = s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z & 0xFFFFFFUL) / 16777216f;   // 24 bits of entropy in [0,1)
    }


    /// <summary>Native clusterIGC.cpp:378-435: damage ships, asteroids, stations,
    /// missiles and probes at the explosion timestamp, with surface-distance
    /// squared falloff and NoWarn/NoDebris flags. Mines use ReduceStrength
    /// (clusterIGC.cpp:441-458). Each recipient owns its friendly/repair rules.</summary>
    public void ResolveExplosion(Explosion e, double now)
    {
        // A missile killed by splash can create another blast (FedSrv.CPP:12524-12529).
        // Drain those after the current blast, like native's explosion list, to
        // avoid recursive damage walks and process each death exactly once.
        using var scope = TickProfiler.Enter("blast.resolve");
        _pendingExplosions.Enqueue(e);
        if (_resolvingExplosions) return;
        _resolvingExplosions = true;
        try
        {
            bool secondary = false;
            while (_pendingExplosions.TryDequeue(out var blast))
            {
                using var chained = secondary ? TickProfiler.Enter("blast.secondary") : default;
                ApplyExplosion(blast);
                secondary = true;
            }
        }
        finally { _resolvingExplosions = false; }
    }

    private void ApplyExplosion(Explosion e)
    {
        using var scope = TickProfiler.Enter("blast.apply");
        byte type = (byte)(e.DamageType | BlastDamage.NoWarn | BlastDamage.NoDebris); // clusterIGC.cpp:431
        var models = _models.ToArray();
        var stations = _stationRuntimes.ToArray();
        var asteroids = _mineable.ToArray();
        foreach (var target in models)
        {
            if (!_models.Contains(target)) continue; // native GetCluster()==this (clusterIGC.cpp:416)
            float radius = target switch
            {
                Ship ship when !ship.Dead => .5f * ship.HullType.Length,
                Missile missile when !missile.Dead => missile.MissileType.Radius,
                Probe probe when !probe.Dead => probe.ProbeType.Radius,
                _ => -1
            };
            if (radius < 0) continue;
            // Native dt=(e.time-m_lastUpdate)-m_tOffset (clusterIGC.cpp:409,419).
            // The port integrates models separately; each clock identifies the
            // timestamp of its stored position, including a model mid-OnUpdate.
            double stateTime = ReferenceEquals(target, _updatingModel) ? _modelStateTime : target.LastUpdate;
            Vector3 p = target.Position + target.CollisionVelocity * (float)(e.Time - stateTime);
            if (!ExplosionAmount(e, p, radius, out float amount)) continue;
            using (TickProfiler.Enter("blast.damage"))
            switch (target)
            {
                case Ship ship:
                    ship.ReceiveDamage(type, amount, e.Time, p, e.Position, e.Launcher);
                    if (ship.Dead) Remove(ship);
                    break;
                case Missile missile:
                    missile.ReceiveDamage(type, amount, e.Time, p, e.Position, e.Launcher);
                    break;
                case Probe probe:
                    probe.ReceiveDamage(type, amount, e.Time, p, e.Position, e.Launcher);
                    break;
            }
        }
        // Static runtime stations/asteroids live outside Models in this port.
        foreach (var station in stations)
        {
            if (station.Dead || !_stationRuntimes.Contains(station)
                || !ExplosionAmount(e, station.Center, station.Radius, out float amount)) continue;
            DamageResult stationResult;
            using (TickProfiler.Enter("blast.damage"))
            {
                float shieldBefore = station.ShieldFraction, hullBefore = station.ArmorFraction;
                stationResult = station.ReceiveDamage(type, amount, e.Time, station.Center, e.Position, e.Launcher);
                station.TraceDamage(e.Time, e.Launcher, type, shieldBefore, hullBefore);
            }
            if (stationResult == DamageResult.Killed)
            {
                RemoveStation(station);
                var mission = Mission ?? station.Mission ?? (e.Launcher as Ship)?.Mission;
                // The native launcher can be NULL (warpIGC.h:77,87).
                mission?.Site.KillStationEvent(station, e.Launcher!, amount);
                if (mission is not null)
                {
                    var (winner, changed) = mission.RecomputeConquestAndTerritory(null);
                    if (changed || winner is not null) mission.Site.VictoryByStationsComputed(mission, changed, winner);
                    mission.Site.SideLostRestartStation(mission, station.SideId);
                }
            }
        }
        foreach (var asteroid in asteroids)
        {
            var p = new Vector3(asteroid.Data.Position[0], asteroid.Data.Position[1], asteroid.Data.Position[2]);
            if (asteroid.Dead || !_mineable.Contains(asteroid)
                || !ExplosionAmount(e, p, asteroid.Radius, out float amount)) continue;
            DamageResult asteroidResult;
            using (TickProfiler.Enter("blast.damage"))
                asteroidResult = asteroid.ReceiveDamage(type, amount, e.Time, p, e.Position, e.Launcher);
            if (asteroidResult == DamageResult.Killed)
            {
                RemoveAsteroid(asteroid);
                (Mission ?? (e.Launcher as Ship)?.Mission)?.Site.KillAsteroidEvent(asteroid, true);
            }
        }
        foreach (var mine in models.OfType<Mine>())
        {
            if (!_models.Contains(mine)) continue;
            // SIM-0067: native subtracts the mine's collision radius
            // (mineigc.cpp:106 sets SetRadius(dataMineType->radius)). The
            // stock corpus's base model Radius is a junk sentinel, but
            // MineType.MineRadius is the real decoded collision radius used
            // by ResolveMineDetonations, so use it for falloff distance.
            if (ExplosionAmount(e, mine.Position, mine.MineType.MineRadius, out float amount)) mine.ReduceStrength(amount);
        }
    }

    private static bool ExplosionAmount(Explosion e, Vector3 p, float radius, out float amount)
    {
        using var scope = TickProfiler.Enter("blast.evaluate");
        float d = (e.Position - p).Length() - radius; // clusterIGC.cpp:420
        amount = e.Amount;
        if (!(d < e.Radius)) return false;          // strict boundary (clusterIGC.cpp:422)
        if (d > 0)
        {
            float f = 1 - d / e.Radius;
            amount *= f * f;                        // clusterIGC.cpp:424-429
        }
        return true;
    }

    /// <summary>
    /// The <b>warp-transit trigger</b> pass, run once per sector tick after the
    /// combat pass. This is the C# analogue of the warp half of
    /// <c>CshipIGC::HandleCollision</c> (<c>shipIGC.cpp:629-636</c>): a ship
    /// whose bounding sphere touches a warp's hit-test shape is considered to
    /// have hit the aleph, and the host's <see cref="IIgcSite.HitWarpEvent"/>
    /// is raised so the seam can perform the actual transit (position /
    /// orientation / velocity change + sector reassignment,
    /// <c>CFSShip::HitWarp</c>, <c>fsship.cpp:71-137</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Native shape: a cone, not a sphere (P776).</b> Native loads an
    /// aleph's hit test with <c>c_htsCone</c> (<c>Igc/modelIGC.cpp:127-130</c>);
    /// <c>BoundingCone::GetExtreme</c> (<c>_Utility/HitTest.cpp:254-283</c>) is
    /// the support function of a base disc of radius <c>warp.Radius</c> through
    /// the centre (perpendicular to forward) plus a 45° cone behind it whose
    /// apex is at <c>centre + Radius·backward</c> (<c>_Utility/HitTest.cpp:878-879</c>;
    /// <c>zlib/orientation.h:83-92</c> — local +z is backward). Nothing of the
    /// solid lies in front of the disc. The trigger here is the ship bounding
    /// sphere vs that cone, via <see cref="AlephConeHitTest"/>; the old
    /// point-in-sphere test (<c>|ship − warp| ≤ warp.Radius + shipRadius</c>)
    /// was a ~110&nbsp;u ball on BOTH sides of the disc, so a native-exact exit —
    /// centre <c>shipRadius+5</c> in front of the destination disc
    /// (<c>fsship.cpp:129-132</c>) — was inside it and re-triggered as soon as
    /// the re-arm fired (the measured 1.9–2.0&nbsp;s Tellus↔Omega bounce).</para>
    /// <para><b>Swept per chunk.</b> Native's test is continuous over the tick
    /// interval (<c>_Utility/HitTest.cpp:114-207</c>, <c>HullCollide</c>); a
    /// single end-of-chunk sample can tunnel through the cone's thin rim. The
    /// trigger therefore samples the segment the ship swept this chunk
    /// (<c>Position − CollisionVelocity·dt</c> to <c>Position</c>, the same
    /// pattern as <see cref="ResolveTreasurePickups"/>) at a spacing of at most
    /// the ship radius (capped at 16 samples) and fires on the first sample that
    /// touches. A cheap broad phase — <c>|ship − warp| ≤ Radius + shipRadius +
    /// |v|·dt</c> — skips the cone math when even the sweep cannot reach.</para>
    /// <para><b>No closing-velocity gate.</b> Native produces a collision even
    /// while the objects separate (<c>_Utility/HitTest.cpp:166-185</c>); the
    /// "not closing: ignore / hit the back: bounce" branch is commented out
    /// (<c>Igc/shipIGC.cpp:638-715</c>) and <c>HitWarpEvent</c> fires
    /// unconditionally (<c>Igc/shipIGC.cpp:629-636</c>). A ship parked at the
    /// centre warps; no velocity test is applied here either.</para>
    /// <para><b>Trigger radius (P244 / P776).</b> The warp's real raw
    /// <c>warp.Radius</c> plus the ship radius, with <em>no</em>
    /// <see cref="WarpVisualScale"/> multiplier. P238 had widened this to
    /// <c>WarpVisualScale·warp.Radius + shipRadius</c> (~950&nbsp;units for a
    /// 100-unit warp), which fired transit while the player was still at the
    /// funnel's outer edge (the "it disappears as you approach it" bug). P244
    /// reverted to the raw radius; P776 replaced the sphere with native's cone.</para>
    /// <para><b>Debounce</b> (native player re-arm,
    /// <c>fsship.cpp:74,1185-1191</c>): a player that crossed goes
    /// <c>warpNoUpdate → warpWaiting → warpReady</c> after an accepted update and
    /// another ~2&nbsp;s. The <see cref="Ship.LastWarpTime"/> /
    /// <see cref="WarpCooldownSeconds"/> window stands in for that handshake and
    /// is kept on the ship so it survives the sector transfer. It is <b>not</b>
    /// what prevents re-entry: geometry does (the native exit sits 5&nbsp;u in
    /// front of the disc, clear of the cone). Native drones never leave
    /// <c>warpReady</c> (<c>fsship.cpp:79-82</c>) and have no cooldown at all; the
    /// NPC cooldown here is port-only (see <see cref="WarpCooldownSeconds"/>).</para>
    /// <para><b>Scope.</b> Single-ship, per-sector: only ships physically in
    /// this sector (<see cref="_models"/>) are tested, and each fires at most
    /// one warp per tick (the <c>break</c>), matching the C++ single-owner
    /// collision event. A dangling warp (<see cref="ResolvedWarp.Destination"/>
    /// null) or one lacking a position is skipped, never fabricated.</para>
    /// </remarks>
    private void ResolveWarpCollisions(double now, float dt)
    {
        if (_warps.Count == 0) return;              // no alephs to hit
        foreach (var model in _models.ToArray())    // snapshot survives self-transit (remove from this sector)
        {
            if (model is not Ship ship) continue;
            if (ship.Dead) continue;                 // the dead cannot warp
            if (ship.Mission is null) continue;      // unplaced ship has no host to route to
            // P760 — players use the native update handshake (WarpState); NPCs
            // keep the existing LastWarpTime cooldown (fsship.cpp:79-82).
            if (!ship.WarpReady) continue;
            if (now - ship.LastWarpTime < WarpCooldownSeconds) continue;

            // Ship radius is the half-length, exactly as the combat pass builds
            // its sphere (Sector.cs:174, shipIGC.cpp:1419).
            float shipRadius = 0.5f * ship.HullType.Length;

            // P591b/P591c — release the re-entry latch once the ship has clearly left
            // the vicinity of the aleph it arrived through (it may then
            // legitimately re-cross it). The release threshold is a HEALTHY
            // MARGIN (2× the transit volume radius), not the bare sphere edge:
            // a drone emerging from an aleph often skirts the edge of its
            // transit volume as it turns toward its next goal, and releasing
            // the moment it crosses the bare edge lets it swing back and
            // re-cross the very aleph it just came through (the Debug-geometry
            // variant of the measured mine-layer ping-pong). Requiring it to
            // move well clear first gives its steering time to commit to the
            // onward route. The arrival warp lives in THIS sector (the
            // destination); if absent (e.g. the ship moved on), release too.
            // The guard is the P591c-documented NON-NATIVE safety net, gated by
            // WarpReEntryGuardEnabled so tests/proof can show the routing fix
            // holds on its own.
            if (Sector.WarpReEntryGuardEnabled && ship.LastArrivalWarpId >= 0)
            {
                var aw = _warps.FirstOrDefault(w => w.Warp.WarpId == ship.LastArrivalWarpId);
                bool cleared = true;
                if (aw is not null && aw.Warp.Position is { Length: 3 } ap)
                {
                    var av = new Vector3(ap[0], ap[1], ap[2]);
                    float adx = ship.Position.X - av.X, ady = ship.Position.Y - av.Y, adz = ship.Position.Z - av.Z;
                    float clearDist = 2f * (aw.Warp.Radius + shipRadius);
                    cleared = adx * adx + ady * ady + adz * adz > clearDist * clearDist;
                }
                if (cleared) ship.LastArrivalWarpId = -1;
            }
            // P591 — a Layer drone that has ARRIVED at its lay target sector is
            // deploying: it must fly to its lay point and LayExpendable without
            // warping through the very aleph it was ordered to lay at (measured
            // live: the layer ping-ponged Omega<->Alderan through its target
            // warp, never laying). Suppress transit while it is in its lay
            // target cluster — it still transits freely while travelling cross-
            // sector to GET there, matching native where the drone lays at the
            // buoy on arrival rather than being whisked away by the aleph.
            if (ship.LayerBuildOrder is { } lb && ship.Sector?.Id == lb.ClusterId) continue;
            foreach (var warp in _warps)
            {
                if (warp.Destination is null) continue;              // dangling edge: nothing to cross into
                if (warp.Warp.Position is not { Length: 3 } p) continue;  // no position: can't test
                // P591b/P591c — the re-entry guard (NON-NATIVE safety net, see
                // WarpReEntryGuardEnabled): a ship must not immediately
                // bounce back out through the SAME aleph it just arrived via
                // while still inside its transit volume. Native never needs such
                // a rule: the exit geometry leaves the ship 5 u in front of the
                // destination disc, clear of the cone (fsship.cpp:129-132; P776
                // AlephConeHitTest), and the player re-arm timer
                // (fsship.cpp:1185-1191) merely re-arms — it is not what
                // prevents re-entry. This guard predates that geometry fix; the
                // P591c routing fix makes it fire ≈0 times in practice, so it is
                // OPT-IN and OFF by default (P594), armed only when the operator
                // enables WarpReEntryGuard. The latch is released above the
                // moment the ship clears the volume.
                if (Sector.WarpReEntryGuardEnabled && ship.LastArrivalWarpId == warp.Warp.WarpId
                    && ship.IntendedNextWarpId != warp.Warp.WarpId)
                {
                    // Only hold the UNINTENTIONAL drift back through the arrival
                    // aleph (the ship's routing goal is a DIFFERENT warp, or none
                    // — it is not choosing to go back). An INTENTIONAL re-crossing
                    // (ship.IntendedNextWarpId == this warp — e.g. a combat bot
                    // pursuing an enemy back across the border it entered by, or a
                    // return-home through the same door) is permitted: blocking it
                    // would trap the ship at the arrival aleph forever, because its
                    // goal lies THROUGH the warp so it can never clear the 2× volume
                    // to release the latch. Native likewise lets a ship that chooses
                    // to go back re-cross after the ~2 s re-arm.
                    // Count (and log) each real block so a regression that
                    // reintroduces re-entry shows up as a non-zero guard-block
                    // count even while the guard itself is preventing the harm.
                    ship.NumWarpReEntryBlocks++;
                    ConsoleLog.Write($"[warp] P591b re-entry guard BLOCKED ship {ship.Id} (block #{ship.NumWarpReEntryBlocks}) " +
                                     $"from re-crossing arrival warp {ship.LastArrivalWarpId} (not its route goal) — routing should have carried it clear (non-native safety net)");
                    continue;
                }
                var wpos = new Vector3(p[0], p[1], p[2]);
                var wfwd = warp.Warp.Forward is { Length: 3 } wf
                    ? new Vector3(wf[0], wf[1], wf[2])
                    : new Vector3(0f, 0f, -1f);
                float warpRadius = warp.Warp.Radius;            // P244: raw radius, no WarpVisualScale

                // P776 — swept ship sphere vs the aleph cone. Native's
                // HullCollide is continuous over the tick interval
                // (HitTest.cpp:114-207), so walk the segment this chunk's
                // motion swept (Position − CollisionVelocity·dt → Position, the
                // ResolveTreasurePickups pattern) and fire on the first
                // contact; an end-of-chunk sample alone can tunnel through the
                // cone's thin rim.
                var end = ship.Position;
                var travelVec = ship.CollisionVelocity * dt;
                var start = end - travelVec;
                float travel = travelVec.Length();
                // Cheap broad phase: no cone point is farther than
                // Radius+shipRadius from the centre, so a sweep that never gets
                // within that plus its own travel cannot touch.
                if ((end - wpos).Length() > warpRadius + shipRadius + travel) continue;
                int steps = Math.Clamp((int)MathF.Ceiling(travel / MathF.Max(shipRadius, 1f)), 1, 16);
                bool touched = false;
                for (int i = 0; i <= steps; i++)
                {
                    var sample = start + travelVec * (i / (float)steps);
                    if (AlephConeHitTest.Touches(wpos, wfwd, warpRadius, sample, shipRadius))
                    {
                        touched = true;
                        break;
                    }
                }
                if (touched)
                {
                    ship.Mission.Site.HitWarpEvent(ship, warp);
                    break;                          // one warp per tick per ship
                }
            }
        }
    }

    /// <summary>
    /// The <b>warp-bomb trigger</b> pass — the missile half of a warp being
    /// hit, run once per sector tick. This is the C# analogue of the native
    /// trigger <c>CmissileIGC::HandleCollision</c> (<c>missileigc.cpp:459-467</c>):
    /// a missile that has the warp-bomb capability and collides with a warp
    /// (instead of a ship) raises <c>IIgcSite.WarpBombEvent</c> rather than
    /// dealing damage. Unlike <see cref="ResolveWarpCollisions"/> (which since
    /// P776 uses native's cone, <see cref="AlephConeHitTest"/>), this pass is
    /// still a point-in-sphere test each tick:
    /// <c>|missile − warp| ≤ warp.Radius + missile.Radius</c>, the target-radius-
    /// plus-source-radius convention the mine/warp/mining passes use. Native's
    /// warp hit test is the same cone for missiles (P776 follow-up candidate).
    /// This is a <b>dedicated pass</b>, not a parameterized variant of
    /// <see cref="ResolveWarpCollisions"/>: the two share only the outer
    /// <c>for each model × for each warp</c> loop, and the body is wholly
    /// different (ship debounce/transit vs. missile consumption + scheduling +
    /// two site events), so parameterizing the target type would thread
    /// disjoint branches through one method for no code savings.
    /// </summary>
    /// <remarks>
    /// <para><b>Trigger behaviour (scope P101).</b> On a live
    /// <see cref="Missile"/> within a warp's collision volume: (1) compute
    /// <c>timeExplosion = now + <see cref="EffectiveWarpBombDelay"/></c> and
    /// schedule the real delayed detonation for the warp pair;
    /// (2) <b>consume the missile</b> — remove it from the sector at trigger
    /// time, not at <c>timeExplosion</c>, honoring the same terminate-on-hit
    /// contract every other missile-impact path honors; (3) raise
    /// <see cref="IIgcSite.WarpBombEvent"/> so the host can broadcast a real
    /// <c>FM_S_WARP_BOMB(188)</c>; and (4) raise the missile's own
    /// <see cref="IIgcSite.MissileDestroyedEvent"/>. The last is the native's
    /// own behaviour: after <c>WarpBombEvent</c> returns, the missile collision
    /// handler falls through to the unconditional <c>KillMissileEvent(this,…)</c>
    /// (<c>missileigc.cpp:512</c>), so the <c>WARP_BOMB</c> and the missile-destroy
    /// broadcast BOTH fire — <c>WARP_BOMB</c> alone does not cover the missile's
    /// removal (the wire <c>FM_S_MISSILE_DESTROYED(83)</c> has no cause field,
    /// same reasoning as the combat-pass destroy at <see cref="ResolveCombatCollisions"/>).</para>
    /// <para><b>Whole-mission broadcast.</b> The <c>WARP_BOMB</c> goes to the
    /// whole mission (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12558</c>), not a
    /// same-side gate — a genuine, verified departure from every prior ordnance
    /// broadcast in this project (P88/P90/P94/P99 all same-side). The host bridge
    /// (<c>ServerHost.WarpBombEvent</c>) implements that; the pass only raises the
    /// event. The event route is the missile's launcher site, exactly like the
    /// destroy event — a launcher-less missile raises nothing.</para>
    /// <para>A dangling warp can consume a bomb, but supplies no fabricated
    /// destination. Only a dual bomb can then explode at its source.</para>
    /// </remarks>
    private void ResolveWarpBombTriggers(double now)
    {
        if (_warps.Count == 0) return;              // no alephs to bomb
        foreach (var model in _models.ToArray())    // snapshot survives removal
        {
            if (model is not Missile missile) continue;
            // P549 — only a missile whose type carries c_eabmWarpBomb may
            // detonate an aleph (native missileigc.cpp:459-466: `if (eabm &
            // c_eabmWarpBomb)`). Any other missile touching an aleph does
            // nothing — it does not arm a delayed blast.
            if (missile.IsDud || !missile.MissileType.HasWarpBombCapability) continue;

            foreach (var warp in _warps)
            {
                if (warp.Warp.Position is not { Length: 3 } p) continue;  // no position: can't test
                var wpos = new Vector3(p[0], p[1], p[2]);
                float threshold = warp.Warp.Radius + missile.MissileType.Radius;  // P244: raw collision radius (no WarpVisualScale); see remarks
                float dx = missile.Position.X - wpos.X;
                float dy = missile.Position.Y - wpos.Y;
                float dz = missile.Position.Z - wpos.Z;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;

                // (1) schedule the pair's detonation using the loaded core delay.
                double timeExplosion = now + EffectiveWarpBombDelay;
                missile.WarpBombTime = timeExplosion;
                ScheduleWarpBomb(timeExplosion, warp, missile.MissileType);

                // (2) consume the missile NOW (at trigger, not at timeExplosion) —
                // the same terminate-on-impact contract every other missile path
                // honors (native falls through to KillMissileEvent, missileigc.cpp:512).
                Terminate(missile);

                // (3)+(4) raise the site events. Route through the launcher's
                // mission site, exactly like the destroy path (Sector.cs:459-460);
                // a launcher-less missile has no site and raises nothing.
                if (missile.Launcher is Ship launcherShip)
                {
                    var site = launcherShip.Mission?.Site;
                    if (site is not null)
                    {
                        site.WarpBombEvent(warp, missile);
                        site.MissileDestroyedEvent(missile);
                    }
                }
                break;                              // one warp per missile per tick
            }
        }
    }

    /// <summary>Native warpIGC.h:58-89: pop due bombs in time order. Power
    /// and BlastRadius come from the missile type, launcher is NULL, destination
    /// always explodes and source explodes only for WarpBombDual (0x02).</summary>
    private void ResolveWarpBombDetonations(double now)
    {
        while (_warpBombs.Count > 0 && _warpBombs[0].TimeExplosion <= now)
        {
            var bomb = _warpBombs[0];
            _warpBombs.RemoveAt(0); // remove before damage callbacks can add bombs
            if ((bomb.MissileType.EabmCapabilities & 0x02) != 0) // warpIGC.h:70
                DetonateWarpBomb(this, bomb.Warp, bomb, now);
            if (bomb.Warp.Partner() is { } partner && bomb.Warp.Destination is { } destination)
                DetonateWarpBomb(destination, partner, bomb, now); // warpIGC.h:79-87
        }
    }

    private static void DetonateWarpBomb(Sector sector, ResolvedWarp warp, ScheduledWarpBomb bomb, double now)
    {
        if (warp.Warp.Position is not { Length: 3 } p) return;
        sector.ResolveExplosion(new Explosion(bomb.MissileType.DamageType,
            bomb.MissileType.Power, bomb.MissileType.BlastRadius, bomb.TimeExplosion,
            new Vector3(p[0], p[1], p[2]), null), now); // warpIGC.h:65-87
    }

    private sealed record ScheduledWarpBomb(double TimeExplosion, ResolvedWarp Warp, DataMissileTypeIGC MissileType);

    private void ScheduleWarpBomb(double timeExplosion, ResolvedWarp warp, DataMissileTypeIGC missileType)
    {
        var bomb = new ScheduledWarpBomb(timeExplosion, warp, missileType);
        int index = _warpBombs.FindIndex(b => b.TimeExplosion > timeExplosion);
        if (index < 0) _warpBombs.Add(bomb);
        else _warpBombs.Insert(index, bomb);
    }

    /// <summary>
    /// The <b>dock trigger</b> pass, run once per sector tick after the warp
    /// pass. This is the C# analogue of the arrival half of the reference's
    /// docking detection: a ship that comes within a station's landing
    /// footprint is considered to have arrived and the host's
    /// <see cref="IIgcSite.DockWithStationEvent"/> is raised so the seam can
    /// latch the docked state (<c>FedSrvSiteBase::DockWithStationEvent</c>,
    /// <c>FedSrv.CPP:13087-13140</c>; native arrival detection at
    /// <c>shipIGC.cpp:843,853</c> fires it on the collision tick via
    /// <c>pStation-&gt;InGarage(ship, predictedPos)</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Friendly-only (the task's explicit scope gate).</b> The native
    /// function interleaves hostile capture (<c>CaptureStation</c>,
    /// <c>FedSrv.CPP:13100-13105</c>) in the same body; this port does not —
    /// only same-side docking. A ship docks only when a station's
    /// <see cref="DataStationIGC.SideId"/> equals the ship's own
    /// <see cref="Ship.SideId"/>, and a ship with no side (<see langword="null"/>
    /// <c>SideId</c>) or a station with no side (<c>-1</c>) can never satisfy
    /// that, so both are skipped. A hostile (different-side) station simply
    /// never triggers a dock — the ship flies on (out of scope: capture).</para>
    /// <para><b>Geometry.</b> When artwork bays are available, require collision
    /// range and the hull-class-specific <c>Station.InGarage</c> half-spaces.
    /// Native calls InGarage only from a collision (Igc/shipIGC.cpp:843), so
    /// an unbounded entry half-space alone must not dock a distant ship.
    /// Without bay data the documented <see cref="DockRadius"/> fallback applies.
    /// Neither path requires low velocity: native arrival is geometric.</para>
    /// <para><b>Gates.</b> The dead cannot dock; an unplaced ship has no host
    /// to route to; an already-docked ship is skipped (the latch, so a ship that
    /// stays in the sphere does not re-fire every tick); and the native
    /// <c>OkToWarp()</c> eligibility gate (<c>FedSrv.CPP:13093</c>, =
    /// <c>m_warpState == warpReady</c>, <c>fsship.h:186-188</c>) is mirrored by
    /// the <see cref="WarpCooldownSeconds"/> window on <see cref="Ship.LastWarpTime"/>
    /// — a ship that just warped (native <c>warpNoUpdate/warpWaiting</c>, not
    /// ready) cannot dock until the window elapses, so it cannot instantly dock
    /// onto a station it transited onto. Freshly spawned ships have
    /// <c>LastWarpTime == -∞</c> so are ready immediately.</para>
    /// </remarks>
    private void ResolveDockCollisions(double now)
    {
        if (_stations.Count == 0) return;               // nothing to dock at
        foreach (var model in _models.ToArray())
        {
            if (model is not Ship ship) continue;
            if (ship.Dead) continue;                      // the dead cannot dock
            // Boarded children dock with their parent, never from an independent
            // collision at their old position (native Igc/shipIGC.cpp:271;
            // FedSrv/FedSrv.CPP:13165-13173 ChangeStation).
            if (ship.ParentShip is not null) continue;
            if (ship.Mission is null) continue;          // unplaced ship has no host
            if (ship.StationId is not null) continue;    // already docked (latch)
            if (ship.SideId is not { } side) continue;   // no side ⇒ nothing is friendly (native pside == psideOld impossible)

            // OkToWarp() gate (FedSrv.CPP:13093, fsship.h:186-188): a ship that
            // just warped must be warpReady before it can dock. Here, the warp
            // debounce window collapsing native's warpReady state machine.
            if (now - ship.LastWarpTime < WarpCooldownSeconds) continue;

            // P75: a ship that just undocked cannot re-dock until
            // LaunchCooldownSeconds elapses — see Ship.LastLaunchTime and
            // LaunchCooldownSeconds for why this needed its own, longer
            // window than the warp debounce above.
            if (now - ship.LastLaunchTime < LaunchCooldownSeconds) continue;

            for (int si = 0; si < _stations.Count; si++)
            {
                var station = _stations[si];

                // P550 — the dock's real arrival + type gates (native OT_station
                // case, shipIGC.cpp:719-867). Resolve the station's runtime (its
                // resolved type) — a static-only record with an unresolvable type
                // (P125) cannot dock (native stations always carry a type).
                if (station.Position is not { Length: 3 } p) continue;  // no position: can't test
                var spos = new Vector3(p[0], p[1], p[2]);
                var rt = _stationRuntimes.FirstOrDefault(s => s.StationId == station.StationId);
                if (rt is null) continue;

                float dx = ship.Position.X - spos.X;
                float dy = ship.Position.Y - spos.Y;
                float dz = ship.Position.Z - spos.Z;

                // P550 — the native station-ability gate (shipIGC.cpp:725-727):
                // a fighter hull (c_habmFighter) requires the station to carry
                // c_sabmLand; a non-fighter hull requires c_sabmCapLand.
                // Real player lifepods (including hull 444) carry c_habmFighter. A
                // station lacking the required ability is simply not dockable by
                // this hull, whatever its side. Applies in BOTH the geometry
                // path and the corpus-gap fallback below.
                short need = ship.HullType.HasCapability(DataHullTypeIGC.HabmFighter)
                    ? DataStationTypeIGC.SabmLand
                    : DataStationTypeIGC.SabmCapLand;
                if (!rt.Type.HasCapability(need)) continue;

                // P550 — the native landing GEOMETRY gate (shipIGC.cpp:843,853 →
                // CstationIGC::InGarage, stationIGC.cpp:385-436): when the
                // station type's real landing geometry is resolvable (the
                // corpus supplies its model), the definitive arrival test is the
                // geometric <c>InGarage</c> predicate (reused from the carrier
                // bays) — the old 200-unit DockRadius sphere alone is no longer
                // sufficient. When the geometry is NOT resolvable (no
                // StationLandingBays cache attached, or the type's model lacks
                // landing frames — a corpus gap native never has, since its
                // art always ships), the port falls back to the classic
                // <see cref="DockRadius"/> proximity stand-in, the documented
                // substitute for the unavailable InGarage; the capability gate
                // above still applies. This keeps docking working for the
                // real-map stations whose models are absent from the available
                // artwork while enforcing strict native geometry wherever the
                // data exists.
                bool geometryAvailable = false;
                if (StationLandingBays is { } cache)
                {
                    var bay = cache.Bay(rt.Type);
                    if (bay is not null)
                    {
                        geometryAvailable = true;
                        // InGarage is called FROM a native collision, not for any
                        // point on a bay's unbounded entry half-space (Igc/clusterIGC.cpp:298-315;
                        // _Utility/HitTest.cpp:130). Keep the collision-sphere prerequisite.
                        float contactRadius = rt.Radius + 0.5f * ship.HullType.Length;
                        if (dx * dx + dy * dy + dz * dz > contactRadius * contactRadius) continue;
                        // Native passes the SHIP's predicted position to InGarage
                        // (shipIGC.cpp:843,853: GetPosition()+GetVelocity()*tCollision),
                        // not the station centre.
                        if (!rt.InGarage(ship, ship.Position, bay)) continue;
                    }
                }
                if (!geometryAvailable
                    && dx * dx + dy * dy + dz * dz > DockRadius * DockRadius) continue;

                // Dock gate (FedSrv.CPP:13100-13105). P110's own doc comment on
                // this pass flagged the hostile-capture branch as explicitly NOT
                // ported; P126 builds it now: a DIFFERENT-side ship whose hull has
                // c_habmBoard captures the station before docking. Capture is gated
                // on (a) a different side, (b) the docking ship's hull having
                // c_habmBoard (DataHullTypeIGC.HabmBoard), (c) "not allied" — this
                // port has no alliance concept, so that term is always true (the
                // same precedent every AlliedSides/bAllowAlliedRip gate this arc
                // simplified), and (d) the station's shields DOWN — P450. A hostile
                // board hull reaches DockWithStationEvent→CaptureStation only when
                // the base's shields are BELOW c_fcidDownedShield; that is the IGC
                // COLLISION gate (shipIGC.cpp:849-851), and FedSrv.CPP:13100-13105
                // is only the SECOND half of the gate — the older comment read just
                // the FedSrv half and wrongly claimed "NO shield/hull gate".
                // bInvulnerableStations (shipIGC.cpp:850) is a mission param this
                // port does not decode → default = false (stations vulnerable), the
                // same precedent stated at Sim/Station.cs:57,239-241 and
                // Sim/ClusterWarnings.cs:15-18. When the shield is UP the hostile
                // board ship falls through with a `continue` (below) — this port's
                // stand-in for native's collision bounce — so it neither captures
                // nor docks, matching the non-board rejection.
                if (station.SideId != side)
                {
                    // A hostile ship can only capture with a c_habmBoard hull;
                    // otherwise it is rejected (the pre-P126 behaviour: no dock,
                    // no capture — the normal cross-side dock rejection).
                    if (!ship.HullType.HasCapability(DataHullTypeIGC.HabmBoard)) continue;

                    // Capture mutates the live Armor/Shield pools on the runtime
                    // Station, so a static-only record (unresolvable type, P125 —
                    // no live state) is not capturable; reject it. (rt was
                    // resolved by the gates above.)
                    if (rt is null) continue;

                    // P450 shield gate (shipIGC.cpp:851): a hostile board hull only
                    // boards when the station's shields are DOWN (ShieldFraction
                    // strictly below c_fcidDownedShield). Shields up → the boarding
                    // attempt is rejected with the same `continue` fall-through as a
                    // non-board hull — no capture, no dock (the port's stand-in for
                    // native's collision bounce, which a separate pass covers).
                    if (rt.ShieldFraction >= DownedShieldFraction) continue;

                    // Raise the capture seam BEFORE committing the re-side, so the
                    // host's STATION_CAPTURE broadcast reads sidOld from the still-
                    // enemy station (native: CFSShip::CaptureStation reads psideOld
                    // then SetSide, fsship.cpp:620-631; the wire half is
                    // FedSrvSiteBase::CaptureStationEvent, FedSrv.CPP:13080-13084).
                    short oldSideId = rt.SideId;         // P455: the side LOSING the station (pre-reside owner)
                    ship.Mission.Site.CaptureStationEvent(ship, rt);

                    // Commit the sim-side capture: re-side the static record (so the
                    // friendly-fire gate and this dock seam see the new owner) and
                    // apply the post-capture fudge + side change on the runtime.
                    _stations[si] = station with { SideId = side };
                    station = _stations[si];            // refresh the loop's snapshot
                    rt.Capture(side);
                    // P191 — after the re-side, recompute the conquest/territory
                    // victory state and surface it to the host (native re-sides
                    // THEN calls CheckForVictoryByStationCapture,
                    // fsship.cpp:631 then :686; capture passes pstationKilled =
                    // NULL, so the freshly-captured station counts for its NEW
                    // owner — a divergence from the task brief's "exclude the
                    // captured station" assumption, verified against the live
                    // native source). The host broadcasts GAME_STATE(155) and
                    // declares the win via the P178 GameOver path.
                    var (gWinner, gChanged) = ship.Mission.RecomputeConquestAndTerritory(null);
                    if (gChanged || gWinner is not null)
                        ship.Mission.Site.VictoryByStationsComputed(ship.Mission, gChanged, gWinner);
                    // P455 — the OLD side (pre-capture owner) just lost this
                    // restart-capable station to a capture. The host checks whether
                    // that was its LAST restart station and, if so, eliminates the
                    // side (native's station-elimination win, fsmission.cpp:3698-3714;
                    // on capture the captured station now belongs to the captor, so
                    // it no longer counts for the loser).
                    ship.Mission.Site.SideLostRestartStation(ship.Mission, oldSideId);
                    // The station is now the ship's own side — fall through to the
                    // ordinary friendly dock below (native docking continues
                    // unconditionally after the capture, FedSrv.CPP:13107 onward).
                }

                // Arrived at a (now, after a capture, friendly) station — raise the
                // seam. The host's DockWithStationEvent latches the state (sets
                // Ship.StationId) and logs [dock]; its bool is the permission,
                // consulted (though after the gates above it is effectively always
                // true).
                ship.LastDockTime = now;   // P381: stamp the dock clock here, in the one funnel where normal docks latch
                ship.Mission.Site.DockWithStationEvent(ship, new DockStation(station));
                // P482 — the ship just docked. Native REMOVES a docked ship from
                // its cluster (a docked ship's cluster is NULL, so it is absent
                // from cluster->GetShips(); see modelIGC.cpp:246-275 SetCluster →
                // site AddModel/DeleteModel, and the native ship-update assembly
                // FedSrv.CPP:3727/4150 that reads pcluster->GetShips()). This port
                // keeps a docked ship in _models (it must still tick and be able
                // to undock), approximating native's removal with the StationId
                // filter in GetShipUpdateCandidates — but that filter only takes
                // effect on a cache REBUILD, and the per-tick candidate cache can
                // already hold this ship: it is built lazily on the first
                // GetShipUpdateCandidates call of the tick, which for an NPC-
                // arbitrating sector happens during the models loop ABOVE, before
                // this dock pass latches StationId. Docking bumps neither
                // UpdateCount nor _memberVersion, so that same-tick cache (still
                // containing the just-docked ship) would feed the HEAVY/light/
                // single ship-update broadcast and the client's unconditional
                // Apply(HeavyShipsUpdate) would clear the SHIP_DELETE(SDR_DOCKED)
                // IsGone latch in the same tick (P482). Invalidate the cache at
                // the point the dock takes effect so the broadcast recomputes and
                // excludes the docked ship, restoring native's "docked ship is not
                // in the cluster" invariant.
                if (ship.StationId is not null)
                    InvalidateShipUpdateCandidates();
                break;                                  // one dock per tick per ship
            }
        }
    }

    /// <summary>
    /// P652 — native miner readiness, arbitration, then ore accrual
    /// (Igc/shipIGC.cpp:1950-1967,2092-2247), preserving the port's player
    /// proximity trigger. Gather every ready contender
    /// before awarding any ore: a traveling miner cannot evict a working one,
    /// and an enemy lockout cannot be bypassed by the accrual pass.
    /// </summary>
    private void ResolveMining(double now, float dt)
    {
        if (_mineable.Count == 0)
        {
            foreach (var ship in _models.OfType<Ship>()) { ship.MiningAsteroid = null; ship.IsMining = false; }
            return;
        }

        // Regenerate every mineable rock first (asteroidigc.h:47-56), so a
        // rock freshly drained at the previous tick has had a chance to regrow
        // before this tick's mining draws on it.
        // A completion removes its rock and constructor from their live lists (native capture-next).
        bool constructing = _buildingEffects.Count != 0;
        IEnumerable<Asteroid> rocks = constructing ? _mineable.ToArray() : _mineable;
        foreach (var ast in rocks)
            if (!constructing || _mineable.Contains(ast)) ast.Update(now, dt, He3Regeneration);

        var previouslyMining = _models.OfType<Ship>().Where(s => s.MiningAsteroid is not null).ToHashSet();
        var contenders = new Dictionary<Asteroid, List<Ship>>();
        foreach (var ship in _models.OfType<Ship>())
        {
            ship.MiningAsteroid = null;
            ship.IsMining = false;
            // NPCs use their ordered rock; players retain the established
            // first eligible rock in sector order, without needing a claim.
            // Feed both paths into the same arbitration, so geometric mining
            // cannot bypass an enemy lockout or the least-ore selection.
            var rock = ship.IsNpc ? ship.MiningAsteroidTarget :
                _mineable.FirstOrDefault(ship.IsReadyToMine);
            if (rock is null || !_mineable.Contains(rock) || !ship.IsReadyToMine(rock)) continue;
            if (!contenders.TryGetValue(rock, out var list)) contenders.Add(rock, list = []);
            list.Add(ship); // sector insertion order breaks equal-ore ties (native strict <)
        }

        foreach (var (ast, miners) in contenders)
        {
            // Allies count as friendly (shipIGC.cpp:2115). An enemy among the
            // ready contenders locks out EVERYONE; keep the command targets,
            // only clearing this tick's mining/wants state (2189-2203).
            var first = miners[0];
            var side = first.Mission!.Sides.FirstOrDefault(s => s.Id == first.SideId);
            bool friendly(Ship other) => other.SideId == first.SideId ||
                (side?.AllyGroup is { } group && first.Mission.Sides.Any(
                    s => s.Id == other.SideId && s.AllyGroup == group));
            if (miners.Any(m => !friendly(m)))
            {
                foreach (var miner in miners) miner.ComplainMiningCrowded(now);
                continue;
            }

            var ship = miners[0];
            foreach (var miner in miners)
                if (miner.Ore < ship.Ore) ship = miner;

            // The native redirect loop only changes miners on the arbitrating
            // side (2148-2150); allies retain their orders rather than being
            // ordered to another team's base.
            foreach (var miner in miners)
                if (miner != ship && miner.IsNpc && miner.SideId == first.SideId)
                    miner.RedirectDisplacedMiner();

            var apos = new Vector3(ast.Data.Position[0], ast.Data.Position[1], ast.Data.Position[2]);
            // ── start-mining log (transition only) ────────────────────────
            ship.IsMining = true;
            if (!previouslyMining.Contains(ship))
            {
                var startNote = $"[mine] ship {ship.Id} began mining asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' (ore {ast.OreRemaining:0.##}/{ast.OreMax:0.##}) at {apos}";
                Console.WriteLine(startNote);
                ship.Mission!.Site.ReportNotable(startNote);   // spectator event feed (same text)
            }

            // ── accrual (shipIGC.cpp:2211-2241) ──────────────────────────
            float minedOre = dt * ship.EffectiveMiningRate();     // shipIGC.cpp:2212 (× GA[c_gaMiningRate])
            float cap = ship.EffectiveMineCapacity();             // shipIGC.cpp:2213 (× GA[c_gaMiningCapacity])
            bool hitCap = false;
            if (ship.Ore + minedOre >= cap)
            {
                minedOre = cap - ship.Ore;                           // shipIGC.cpp:2217
                hitCap = true;
            }
            float actual = ast.MineOre(minedOre);                    // clamp to rock's pool (asteroidigc.h:221-233)
            if (actual > 0 && !previouslyMining.Contains(ship))
                ship.Mission?.Skills?.Record(ship.SideId, ShowcaseSkill.DroneMine);
            ship.Ore += actual;

            // ── cap-reached log ──────────────────────────────────────────
            if (hitCap && ship.Ore >= cap)
                Console.WriteLine($"[mine] ship {ship.Id} is full ({ship.Ore:0.##}/{cap:0.##} ore) — mining paused");

            // ── rock-tapped log ──────────────────────────────────────────
            if (actual != minedOre)
            {
                ConsoleLog.Write($"[mine] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' tapped: yielded {actual:0.##} of {minedOre:0.##} requested");
                // shipIGC.cpp:2240-2244: a tapped NPC miner picks a new order.
                if (ship.IsNpc && ship.PilotType == Ship.PilotTypeMiner)
                    ship.RedirectTappedMiner();
            }

            // ── host seam (throttled inside Asteroid.MineOre to the
            // native > 3.0 ore-change threshold, asteroidigc.h:246-251) ──
            if (ast.HasPendingReport)
            {
                ship.Mission!.Site.MineAsteroidEvent(ast, ast.PendingReport);
                ast.ClearPendingReport();
            }

            if (!ship.IsNpc || ship.MiningAsteroidTarget == ast) ship.MiningAsteroid = ast; // P254: the specific rock this ship is now drilling (collision-exclusion consult)

        }
    }

    /// <summary>A ship/station/probe participating in the cross-side visibility
    /// sweep: its side, body geometry and scanner attributes, plus the live
    /// object to write <see cref="SideVisibilityState"/> onto.</summary>
    private readonly record struct VisibilityParticipant(
        ISideVisible Body, short? SideId, Vector3 Position,
        float Radius, float ScannerRange, float Signature);

    /// <summary>
    /// P132 — the per-tick cross-side visibility sweep, called from
    /// <see cref="Update"/> alongside the other per-tick passes. Wires P131's
    /// three primitives together for the first time: for every
    /// ship/station/probe in this sector, for every side other than its own,
    /// it tests every potential scanner of that side in the same cluster via
    /// <see cref="VisibilityMath.InScannerRange"/> (which itself calls
    /// <see cref="VisibilityMath.LineOfSightExist"/> over this sector's
    /// asteroids) and writes the resulting boolean with
    /// <see cref="SideVisibilityState.SetSideVisibility"/>. The model's own
    /// side is always visible to itself, no scan needed.
    /// </summary>
    /// <remarks>
    /// <para><b>Call-site fidelity.</b> Native drives this from
    /// <c>Cluster::Update</c> — the throttled <c>if ((m_nPass++) %
    /// c_nPassesPerUpdate == 0)</c> block (<c>clusterIGC.cpp:347-356</c>)
    /// iterates every model calling <c>UpdateSeenBySide()</c> →
    /// <c>ThingSiteImpl::UpdateSideVisibility</c> (FedSrv.CPP:10876-10945,
    /// modelIGC.h:134-137). The port runs the sweep <b>every</b> sector tick,
    /// matching the cadence of this port's other per-tick passes
    /// (<c>ResolveMining</c>, <c>ResolveRescueEvents</c>,
    /// <c>ResolveCarrierLandings</c>, … — all unthrottled in
    /// <see cref="Update"/>), rather than importing native's separate
    /// every-10 counter (c_nPassesPerUpdate = 10, clusterIGC.h:22). No new
    /// update-frequency mechanism is invented; the port's established
    /// convention (run each pass every tick) is followed.</para>
    /// <para><b>Scanner enumeration — query, not a registered index.</b> The
    /// native <c>ClusterSite::GetScanners(sideID)</c> (<c>FedSrv.CPP:10909</c>)
    /// is a registered, incrementally-updated per-side scanner list
    /// (<c>AddScanner</c>/<c>DeleteScanner</c> on cluster join/leave). This
    /// port does <b>not</b> build that registration lifecycle: instead the
    /// sweep filters this sector's existing model collections (ships and
    /// probes in <see cref="Models"/>, stations in
    /// <see cref="StationRuntimes"/>) by <c>SideId</c> on the fly — confirmed
    /// functionally equivalent here (every scanner must be a model present in
    /// its own sector anyway, since each registers on cluster join and this is
    /// a per-sector pass) and strictly simpler, the same
    /// "query instead of maintain an index" substitution this port already
    /// makes by reusing existing collections elsewhere.</para>
    /// <para><b>"Last spotter" cache omitted.</b> The native
    /// <c>sv.pLastSpotter()</c> shortcut (<c>FedSrv.CPP:10896-10903</c>) is a
    /// pure performance optimization — it skips re-scanning every scanner when
    /// the same one that saw the target last tick still can. Deliberately
    /// omitted: the full scanner loop re-runs every sweep. If profiling ever
    /// shows a real cost, the caching layer can be added then; it is not a
    /// behavior difference.</para>
    /// <para><b>Alliance simplification.</b> The native allied-leg
    /// (<c>pside-&gt;AlliedSides(...) &amp;&amp; bAllowAlliedViz</c>,
    /// FedSrv.CPP:10894-10895) is dropped unconditionally — this port has no
    /// alliance/<c>AlliedSides</c> concept, the same simplification every
    /// other <c>AlliedSides</c>/<c>bAllowAlliedViz</c> gate in this arc has
    /// already made (P107/P112/P115 precedent). Own-side is always visible;
    /// every other side must pass the scanner-range test.</para>
    /// <para><b>Side universe.</b> Iterates <see cref="MissionSides"/> (the
    /// mission's full side set) when bound, so a side whose last scanner left
    /// the sector is still reconciled to <c>false</c>; falls back to the sides
    /// actually present for an isolated (non-mission) sector.</para>
    /// <para><b>Scope boundary.</b> No <c>ShowObject</c>/<c>HideObject</c>
    /// effects, no territory refresh, no broadcasts (PROBES_UPDATE(92) /
    /// ASTEROIDS_UPDATE(179) / STATIONS_UPDATE(57) / Phase 5 are separate,
    /// now-unblocked tasks) and no "last spotter" cache — this pass only flips
    /// <see cref="SideVisibilityState"/>'s flags, exactly P131's stated
    /// boundary.</para>
    /// </remarks>
    internal void ResolveCrossSideVisibility()
    {
        // Enumerate the sector's visibility participants (ships + probes in
        // _models, stations in _stationRuntimes) and, in the same pass, bucket
        // every participant as a scanner for its own side. Each participant is
        // both a potential scanner (for its side) and a potential target.
        var targets = new List<VisibilityParticipant>();
        var scannersBySide = new Dictionary<short, List<VisibilityParticipant>>();
        var skills = MissionSides?.FirstOrDefault()?.Mission?.Skills;

        // P476 — the TARGET side's c_gaSignature resistance factor (the
        // `m /= pside->GetGlobalAttributeSet().GetAttribute(c_gaSignature)`
        // divisor of native shipIGC.h:357 / stationIGC.h:322 / probeigc.h:177,
        // and bSimpleEye's SigMod, clusterIGC.cpp:152): higher = a target of
        // that side is harder to detect. Identity 1.0 for a sideless target
        // (asteroid) or an unresolvable side, exactly native's `if (pside)`
        // guard.
        float SideSignatureFactor(short sideId)
        {
            if (MissionSides is null) return 1.0f;
            foreach (var side in MissionSides)
                if (side.Id == sideId) return side.GlobalAttributes.Get(GlobalAttributeSet.Signature);
            return 1.0f;
        }

        void Add(ISideVisible body, short? sideId, Vector3 pos, float radius, float scannerRange, float sig, bool isScanner = true)
        {
            // The participant's stored signature is consumed ONLY as a TARGET
            // signature in VisibilityMath.InScannerRange (the scanner role uses
            // scannerRange/radius/position only), so the target side's
            // signature-resistance divisor can be folded in once here: the
            // effective detection signature = raw / side c_gaSignature. The
            // scanner side's c_gaScanRange boost, by contrast, arrives already
            // folded into <paramref name="scannerRange"/> via the models'
            // EffectiveScannerRange() accessors (Ship/Station/Probe).
            float detSig = sideId is { } ts ? sig / SideSignatureFactor(ts) : sig;
            var p = new VisibilityParticipant(body, sideId, pos, radius, scannerRange, detSig);
            targets.Add(p);
            // P453 — mines are targets with a side but NOT scanners (native
            // mines carry no c_mtScanner flag; they only take part in the
            // sweep as c_mtSeenBySide targets, mineigc.cpp:103-107). isScanner
            // distinguishes the target-only kinds (mines; and asteroids, which
            // already pass sideId:null) from the scanner-and-target kinds
            // (ships/probes/stations).
            if (isScanner && sideId is { } sid)
            {
                if (!scannersBySide.TryGetValue(sid, out var list))
                    scannersBySide[sid] = list = [];
                list.Add(p);
            }
        }

        foreach (var m in _models)
        {
            switch (m)
            {
                // Ship sphere radius via the established 0.5×Length convention
                // (P113/P114/P116); scanner range from the hull, signature from
                // the ship's EFFECTIVE signature (P269: HullType.Signature scaled
                // by the cloak's Cloaking visible-fraction, per native
                // GetSignature() = base·GetCloaking(), shipIGC.h:268-270) so a
                // cloaked ship shrinks its own detection radius in this sweep —
                // the same per-object mechanic native's InScannerRange applies.
                case Ship s:
                    Add(s, s.SideId, s.Position, 0.5f * s.HullType.Length,
                        s.EffectiveScannerRange(), s.EffectiveSignature);
                    break;
                // Probe radius / range / signature from the probe type. The
                // probe's scan range is the side-GA-boosted value
                // (EffectiveScannerRange, P476 — native probeigc.h:165
                // multiplies by c_gaScanRange).
                case Probe p:
                    Add(p, p.SideId, p.Position, p.ProbeType.Radius,
                        p.EffectiveScannerRange(), p.ProbeType.Signature);
                    break;
                // P453 — mines are side-visible static objects (native mines are
                // created with c_mtSeenBySide | c_mtPredictable | c_mtHitable |
                // c_mtStatic and SetSignature(dataMineType->signature),
                // mineigc.cpp:103-107), so they join the per-side visibility
                // sweep as TARGETS with the owner side, the P452 collision radius
                // (Mine.MineType.MineRadius — the mine-specific radius at
                // DataMineTypeIGC offset 464, NOT the base Radius; see P452) and
                // the type's signature. isScanner:false because native mines are
                // not c_mtScanner — a mine never reveals anything to its side,
                // only its own visibility is scored. Passing the owner SideId
                // (nullable, from the layering ship) keeps the own-side-always-
                // visible rule (the owner sees its own field, as today).
                case Mine mn:
                    Add(mn, mn.SideId, mn.Position, mn.MineType.MineRadius,
                        0f, mn.MineType.Signature, isScanner: false);
                    break;
            }
        }
        foreach (var st in _stationRuntimes)
            Add(st, st.SideId, st.Center, st.Radius, st.EffectiveScannerRange(), st.Type.Signature);

        // P134 — asteroids join the sweep as visibility targets. Native gives a
        // rock c_mtSeenBySide (asteroidigc.cpp:36) and scores it in
        // UpdateSideVisibility like any other model; the port's asteroid is not
        // a Model, so it joins here from _mineable. An asteroid has no SideId,
        // so passing sideId:null means it is a target only (never added to
        // scannersBySide — it is not c_mtScanner) and the own-side rule never
        // applies: it is seen by a side only via that side's scanner in range
        // with line of sight. Signature from the record (native
        // SetSignature(dataAsteroid->signature), asteroidigc.cpp:47). Asteroids
        // also remain occluders below (a rock can hide another rock behind it,
        // exactly as native's single GetAsteroids() list serves both roles).
        foreach (var a in _mineable)
            if (a.Data.Position is { Length: 3 } ap)
                Add(a, null, new Vector3(ap[0], ap[1], ap[2]), a.Radius, 0f, a.Data.Signature);

        // The cluster's asteroid occluders — computed once per sweep, not per
        // pair (native iterates pcluster->GetAsteroids(), FedSrv.CPP:10910 +
        // common.cpp:2324-2398). Positions are read off the runtime asteroid's
        // static record; a record without a usable position contributes nothing.
        var occluders = new List<VisibilityMath.SightBody>(_mineable.Count);
        foreach (var a in _mineable)
            if (a.Data.Position is { Length: 3 } ap)
                occluders.Add(new VisibilityMath.SightBody(new Vector3(ap[0], ap[1], ap[2]), a.Radius));

        // The side universe to reconcile: the mission's full side set when
        // bound (faithful to the native GetSides() iteration), else the
        // distinct sides present among the participants (isolated-sector fallback).
        IReadOnlyList<short> sideIds = MissionSides is { Count: > 0 } ms
            ? ms.Select(s => s.Id).Distinct().ToArray()
            : scannersBySide.Keys.ToArray();

        foreach (var t in targets)
        {
            // Own side is always visible to itself — no scan needed
            // (FedSrv.CPP:10890-10892).
            if (t.SideId is { } own)
                t.Body.SideVisibility.SetSideVisibility(own, true);

            foreach (var sideId in sideIds)
            {
                if (sideId == t.SideId) continue;

                bool seen = false;
                if (scannersBySide.TryGetValue(sideId, out var scns))
                    foreach (var sc in scns)
                    {
                        if (VisibilityMath.InScannerRange(
                                new VisibilityMath.SightBody(sc.Position, sc.Radius), sc.ScannerRange, Id,
                                new VisibilityMath.SightBody(t.Position, t.Radius), t.Signature, Id, occluders))
                        {
                            seen = true;
                            break;
                        }
                    }

                // Flip the flags on the computed result (FedSrv.CPP:11153-11171
                // via SideVisibilityState.SetSideVisibility). Writing false for
                // every unseen side is what turns a previous true into a false
                // when a scanner moves out of range / leaves.
                //
                // P349 — the false→true edges are NO LONGER queued here. The
                // per-tick cross-side export sweep on the host
                // (Server.Broadcast.SweepCrossSideExports) reads the resulting
                // LATCHED SeenBySide state each tick and catches up EVERY peer on
                // the seeing side — including ones who connect after visibility
                // was established — so the Core only has to keep this flag
                // correct (the visibility determination itself is untouched).
                if (seen && !t.Body.GetCurrentEye(sideId)) skills?.Record(sideId, ShowcaseSkill.Scouting);
                t.Body.SideVisibility.SetSideVisibility(sideId, seen);
            }
        }
    }

    /// <summary>Unlink an interrupted effect once, leaving its asteroid intact.
    /// Native shipIGC.cpp:209-218; buildingeffectigc.h:112-133.</summary>
    public void TerminateBuildingEffect(BuildingEffect effect)
    {
        if (effect.IsTerminated) return;
        effect.Terminate();
        _buildingEffects.Remove(effect);
        _fadingBuildingEffects.Remove(effect);
        _pendingBuildingEffectCreations.Remove(effect); // never resurrect an effect after its interruption drain
        if (ReferenceEquals(effect.Target.BuildingEffect, effect)) effect.Target.BuildingEffect = null;
        effect.Builder.IsBuilding = false;
        _pendingBuildingEffectInterruptions.Add(effect.Target.Data.AsteroidId);
    }

    private void ResolveBuildingEffects(double now)
    {
        foreach (var effect in _fadingBuildingEffects.ToArray())
        {
            effect.Update(now);
            if (effect.IsTerminated) _fadingBuildingEffects.Remove(effect);
        }
        foreach (var effect in _buildingEffects.ToArray())
        {
            if (effect.IsTerminated) continue;
            if (effect.Builder.Dead || !_models.Contains(effect.Builder)
                || effect.Builder.Sector != this || !_mineable.Contains(effect.Target))
            {
                (Mission ?? effect.Builder.Mission)?.Site.KillBuildingEffectEvent(effect);
                TerminateBuildingEffect(effect); // domain fallback for a mute site
                continue;
            }
            effect.Update(now);
            effect.AsteroidUpdate(now);
        }
    }

    internal void CompleteConstruction(BuildingEffect effect, double now)
    {
        if (effect.ConstructionCompleted || !_buildingEffects.Contains(effect) || effect.Builder.Dead
            || !_models.Contains(effect.Builder) || effect.Builder.Sector != this || !_mineable.Contains(effect.Target)) return;
        _buildingEffects.Remove(effect);
        effect.ConstructionCompleted = true;
        effect.Target.BuildingEffect = null;
        CompleteBuild(effect, now);
        if (!effect.IsTerminated) _fadingBuildingEffects.Add(effect);
    }

    /// <summary>Consume the constructor and invoke the native BuildStation callback at shrink time
    /// (Igc/buildingeffectigc.h:312-351). Domain-only sites keep the shared transformation adapter.</summary>
    private void CompleteBuild(BuildingEffect effect, double now)
    {
        var asteroid = effect.Target;
        var builder = effect.Builder;
        var mission = builder.Mission!;
        var side = mission.Sides.Single(s => s.Id == effect.SideId);
        var seen = ConstructionSeenSides(builder, asteroid);
        foreach (var id in effect.InformedSides) if (id < seen.Length) seen[id] = true;
        // Native buildingeffectigc.h:328-345: consume before creating the station.
        builder.ConsumeForConstruction();
        Remove(builder);
        mission.Site.BuildStation(asteroid, side, effect.StationType, now, seen);
        // Existing domain-only missions can use NullIgcSite; production hosts execute the callback.
        var station = asteroid.ConstructedStation ?? BuildStation(asteroid, side, effect.StationType, now, seen);
        mission.Skills?.Record(builder.SideId, ShowcaseSkill.DroneBuild);
        mission.Site.StationBuiltEvent(asteroid, builder, station);
    }

    // FedSrv/FedSrv.CPP:11373-11462: native BuildStation world transformation.
    public Station BuildStation(Asteroid asteroid, Side side, DataStationTypeIGC requested, double now, bool[] seen)
    {
        if (asteroid.ConstructedStation is { } existing) return existing;
        var mission = side.Mission!;
        var stationType = SuccessorUpgrade.StationSuccessor(mission.Core, requested, side.DevelopmentTechs);
        var data = BuildStationRecord(asteroid, side.Id, stationType, mission);
        var station = new Station(data, stationType) { ConsumedAsteroid = asteroid };
        station.SetConstructionFractions(asteroid.Fraction);
        foreach (var probe in _models.OfType<Probe>())
        {
            float distance2 = (probe.Position - station.Center).LengthSquared();
            float radius = stationType.Radius + probe.ProbeType.Radius;
            if (distance2 != 0 && distance2 < radius * radius) probe.SetExpiration(now + 1);
        }
        mission.PlaceStation(station, this);
        // Resolve initial station knowledge from actual scanner geometry before deciding rock receipts.
        foreach (var other in mission.Sides)
        {
            bool visible = other.Id == side.Id || AnyScannerSees(other.Id, station.StationId,
                station.Center, station.Radius, station.Type.Signature / side.GlobalAttributes.Get(GlobalAttributeSet.Signature));
            station.SideVisibility.SetSideVisibility(other.Id, visible);
            bool informed = other.Id == side.Id || asteroid.GetCurrentEye(other.Id)
                || (other.Id >= 0 && other.Id < seen.Length && seen[other.Id]) || station.SeenBySide(other.Id);
            if (!informed) station.StaleRockSides.Add(other.Id);
        }
        asteroid.ConstructedStation = station;
        RemoveAsteroid(asteroid);
        return station;
    }

    // Igc/missionigc.cpp:3658-3681: an eye on the builder AND knowledge of the rock.
    private bool[] ConstructionSeenSides(Ship builder, Asteroid rock)
    {
        var mission = builder.Mission!;
        var result = new bool[Math.Max(6, mission.Sides.Max(s => s.Id) + 1)];
        var obstacles = _mineable.Select(a => new VisibilityMath.SightBody(
            new Vector3(a.Data.Position[0], a.Data.Position[1], a.Data.Position[2]), a.Radius)).ToArray();
        var target = new VisibilityMath.SightBody(builder.Position, builder.HullType.Length * .5f);
        float resistance = mission.Sides.First(s => s.Id == builder.SideId).GlobalAttributes.Get(GlobalAttributeSet.Signature);
        void Scan(short id, short? sideId, Vector3 position, float radius, float range)
        {
            if (id == builder.Id || sideId is not { } side || side == builder.SideId || !rock.SeenBySide(side)) return;
            if (VisibilityMath.InScannerRange(new(position, radius), range, Id,
                target, builder.EffectiveSignature / resistance, Id, obstacles)) result[side] = true;
        }
        foreach (var ship in _models.OfType<Ship>())
            if (ship.ParentShip is null && ship.StationId is null)
                Scan(ship.Id, ship.SideId, ship.Position, ship.HullType.Length * .5f, ship.EffectiveScannerRange());
        foreach (var probe in _models.OfType<Probe>())
            Scan(probe.Id, probe.SideId, probe.Position, probe.ProbeType.Radius, probe.EffectiveScannerRange());
        foreach (var station in _stationRuntimes)
            Scan(station.StationId, station.SideId, station.Center, station.Radius, station.EffectiveScannerRange());
        return result;
    }

    private bool AnyScannerSees(short side, short targetId, Vector3 position, float radius, float signature)
    {
        var obstacles = _mineable.Select(a => new VisibilityMath.SightBody(
            new Vector3(a.Data.Position[0], a.Data.Position[1], a.Data.Position[2]), a.Radius)).ToArray();
        var target = new VisibilityMath.SightBody(position, radius);
        bool Sees(short id, short? owner, Vector3 center, float size, float range) => owner == side && id != targetId
            && VisibilityMath.InScannerRange(new(center, size), range, Id, target, signature, Id, obstacles);
        return _models.OfType<Ship>().Any(s => s.ParentShip is null && s.StationId is null
                && Sees(s.Id, s.SideId, s.Position, s.HullType.Length * .5f, s.EffectiveScannerRange()))
            || _models.OfType<Probe>().Any(p => Sees(p.Id, p.SideId, p.Position, p.ProbeType.Radius, p.EffectiveScannerRange()))
            || _stationRuntimes.Any(s => Sees(s.StationId, s.SideId, s.Center, s.Radius, s.EffectiveScannerRange()));
    }

    // Igc/clusterIGC.cpp:141-155; clusterIGC.h:493-545: only flying ships reveal stale rock locations.
    private void ResolveConsumedRockVisibility()
    {
        if (Mission is not { } mission) return;
        foreach (var station in _stationRuntimes)
        {
            if (station.ConsumedAsteroid is not { } rock || rock.Data.Signature == 0 || rock.Radius == 0
                || station.StaleRockSides.Count == 0) continue;
            var target = new VisibilityMath.SightBody(station.Center, rock.Radius);
            var obstacles = _mineable.Select(a => new VisibilityMath.SightBody(
                new Vector3(a.Data.Position[0], a.Data.Position[1], a.Data.Position[2]), a.Radius)).ToArray();
            float resistance = mission.Sides.Single(s => s.Id == station.SideId).GlobalAttributes.Get(GlobalAttributeSet.Signature);
            foreach (var ship in _models.OfType<Ship>())
            {
                if (ship.ParentShip is not null || ship.StationId is not null || ship.SideId is not { } side
                    || side == station.SideId || station.SeenBySide(side) || !station.StaleRockSides.Contains(side)) continue;
                // Native bSimpleEye applies ScanRange in addition to the hull's effective range.
                float scan = mission.Sides.Single(s => s.Id == side).GlobalAttributes.Get(GlobalAttributeSet.ScanRange);
                float range = ship.EffectiveScannerRange() * scan;
                if (!VisibilityMath.InScannerRange(new(ship.Position, ship.HullType.Length * .5f), range, Id,
                    target, rock.Data.Signature / resistance, Id, obstacles)) continue;
                station.StaleRockSides.Remove(side);
                mission.Site.KillAsteroidEvent(rock.Data.AsteroidId, Id, mission.Sides.Single(s => s.Id == side));
            }
        }
    }

    /// <summary>
    /// Build the static <see cref="DataStationIGC"/> record the completed build
    /// injects — the port of native <c>BuildStation</c>'s payload assembly
    /// (<c>FedSrv.CPP:11386-11402</c>), kept as a pure record builder for clarity.
    /// Every field is set explicitly to mirror native, not left to a default.
    /// </summary>
    private DataStationIGC BuildStationRecord(Asteroid asteroid, short sideId, DataStationTypeIGC stationType, Mission mission)
    {
        var pos = asteroid.Data.Position is { Length: 3 } p ? p : new float[3];
        return new DataStationIGC(
            Position: pos,                                   // ds.position = pasteroid->GetPosition() (FedSrv.CPP:11389)
            Up: [0f, 1f, 0f],                                // ds.up.y = 1 (FedSrv.CPP:11392-11393)
            Forward: [0f, 0f, 1f],                           // ds.forward.z = 1 (FedSrv.CPP:11390-11391)
            Rotation: [0f, 0f, 1f, 0f],                      // native axis = forward, angle = 0 (FedSrv.CPP:11394-11395)
            ClusterId: Id,                                   // ds.clusterID = pcluster->GetObjectID() (FedSrv.CPP:11388)
            SideId: sideId,                                  // ds.sideID = pside->GetObjectID() (FedSrv.CPP:11396)
            StationId: mission.GenerateNewStationID(),       // ds.stationID = GenerateNewStationID() (FedSrv.CPP:11397)
            StationTypeId: stationType.StationTypeId,        // ds.stationTypeID = pstationtype->GetObjectID() (FedSrv.CPP:11399)
            BpHull: CompactQuant.Percent(asteroid.Fraction),                                       // BytePercentage::operator=(float), _Utility/Utility.h:1341-1358 (FedSrv.CPP:11401)
            BpShield: 0,                                     // ds.bpShield = 0 (FedSrv.CPP:11402)
            Name: stationType.Name);                         // ds.name = pstationtype->GetName() (FedSrv.CPP:11387)
    }
}
