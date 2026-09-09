namespace Allegiance.Core.Model;

using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

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

    /// <summary>The sector's name.</summary>
    public string Name => Cluster.Name;

    /// <summary>
    /// This sector's warps, each with a resolved <see cref="ResolvedWarp.Destination"/>.
    /// </summary>
    public IReadOnlyList<ResolvedWarp> Warps => _warps;

    /// <summary>This sector's stations.</summary>
    public IReadOnlyList<DataStationIGC> Stations => _stations;

    /// <summary>This sector's asteroids (static position/identity data).</summary>
    public IReadOnlyList<DataAsteroidIGC> Asteroids => _asteroids;

    /// <summary>This sector's runtime asteroids (live, depletable ore pool —
    /// see <see cref="_mineable"/>'s own doc comment below for detail).</summary>
    public IReadOnlyList<Asteroid> Mineable => _mineable;

    /// <summary>Number of times <see cref="Update"/> has been called.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>The time of the most recent <see cref="Update"/>, or 0 before the first.</summary>
    public double LastUpdate { get; private set; }

    private readonly List<ResolvedWarp> _warps = [];
    // Pending warp-bomb detonations (a scheduled absolute sim-time, resolved by
    // ResolveWarpBombDetonations). See ScheduledWarpBomb.
    private readonly List<ScheduledWarpBomb> _warpBombs = [];
    private readonly List<DataStationIGC> _stations = [];
    private readonly List<DataAsteroidIGC> _asteroids = [];

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

    /// <summary>Construct a sector from its cluster record; contents are attached during assembly.</summary>
    public Sector(DataClusterIGC cluster) => Cluster = cluster;

    internal void AttachStations(IEnumerable<DataStationIGC> stations) => _stations.AddRange(stations);
    internal void AttachAsteroids(IEnumerable<DataAsteroidIGC> asteroids)
    {
        _asteroids.AddRange(asteroids);
        foreach (var a in asteroids) _mineable.Add(new Asteroid(a));   // one runtime model per static rock
    }
    /// <summary>Register a runtime asteroid for the mining pass (assembly wraps
    /// static ones; <see cref="Mission.PlaceAsteroid"/> uses this for synthetic
    /// injection in the loopback mining tests).</summary>
    internal void AttachAsteroid(Asteroid asteroid) => _mineable.Add(asteroid);
    internal void AttachWarp(ResolvedWarp warp) => _warps.Add(warp);

    /// <summary>The models that live in this sector and tick with it, in insertion order.</summary>
    public IReadOnlyList<Model> Models => _models;

    /// <summary>Place a model in this sector (<c>CclusterIGC::AddModel</c>).</summary>
    public void Add(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _models.Add(model);
    }

    /// <summary>Remove a model from this sector; returns whether it was present.</summary>
    public bool Remove(Model model) => _models.Remove(model);

    /// <summary>
    /// Remove a runtime asteroid from the mining pass (and now the combat pass),
    /// mirroring <see cref=\"Remove(Model)\"/>'s shape for the sector's separate
    /// <c>_mineable</c> list. Called by <see cref=\"ResolveCombatCollisions\"/> when
    /// a hit crosses the rock's <see cref=\"Asteroid.Fraction\"/> to zero (the port
    /// of <c>pasteroid-&gt;Terminate()</c> inside <c>KillAsteroidEvent</c>,
    /// <c>FedSrv.CPP:12420</c>): a destroyed rock stops being minable and stops
    /// appearing in future combat/mining passes. Returns whether it was present.
    /// </summary>
    public bool RemoveAsteroid(Asteroid asteroid) => _mineable.Remove(asteroid);

    /// <summary>
    /// Per-chunk sector tick (<c>CclusterIGC::Update</c>, <c>clusterIGC.cpp:97</c>).
    /// Ports exactly the models loop — <c>l-&gt;data()-&gt;Update(now)</c>
    /// (<c>clusterIGC.cpp:241-253</c>). Everything else in the C++ function
    /// (station drone launch, ship plot/execute, cost, the <c>activeF</c>
    /// collision block) is out of scope and deliberately absent.
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

        // Snapshot: the models loop must survive a model that self-terminates
        // during its own Update (C++ capture-next, clusterIGC.cpp:243-250).
        var snapshot = _models.ToArray();
        foreach (var model in snapshot)
            model.Update(timeUpdate);            // l->data()->Update(now) (clusterIGC.cpp:251)

        // The C++ collision block sits AFTER the models loop, still inside the
        // same cluster Update, gated on activeF && bStarted (clusterIGC.cpp:255).
        // Ported as the combat pass below (projectile ↔ ship resolution).
        ResolveCombatCollisions(timeUpdate, (float)dt);

        // Mine detonation proximity pass — an armed, hostile-side mine triggers
        // on the first qualifying ship within its blast radius. See
        // ResolveMineDetonations for the spec.
        ResolveMineDetonations(timeUpdate);

        // Warp-transit proximity pass — a stationary-object test (ship vs the
        // sector's warps), deliberately separate from the moving-object combat
        // collider above. See ResolveWarpCollisions for the spec and scope.
        ResolveWarpCollisions(timeUpdate);

        // Warp-bomb (missile vs warp) trigger pass + its scheduled-detonation
        // half. A real missile reaching a warp arms a WARP_BOMB(188) broadcast
        // and a 15 s-delayed explosion at the aleph — see the two passes below.
        ResolveWarpBombTriggers(timeUpdate);
        ResolveWarpBombDetonations(timeUpdate);

        // Dock proximity pass — ship vs the sector's stations. See
        // ResolveDockCollisions for the spec, the friendly gate and the
        // simplified-radius reasoning.
        ResolveDockCollisions(timeUpdate);

        // Mining proximity pass — ship vs the sector's asteroids. See
        // ResolveMining for the spec (proximity + near-zero velocity, no
        // crowding / no per-side gate) and its simplified-rate reasoning.
        ResolveMining(timeUpdate, (float)dt);

        // Ripcord countdown/revalidation pass — P112/P113. For every ship with
        // an active ripcord and no parent, revalidate the anchor and tick the
        // countdown down; on reaching zero with a legal anchor the ripper is
        // handed to the host's UseRipcord (the teleport, P113). See
        // ResolveRipcordCountdowns.
        ResolveRipcordCountdowns((float)dt);

        // Rescue proximity pass — P115. Every friendly same-side ship pair
        // where one carries c_habmLifepod and the other c_habmRescue, within
        // the sum-of-spheres radius, is handed to the host's PerformRescue
        // (PLAYER_RESCUED(16) + placement). See ResolveRescueEvents.
        ResolveRescueEvents();
    }

    /// <summary>
    /// P112/P113 — the per-tick ripcord pass, called from <see cref="Update"/>
    /// just like the other per-tick passes. Centralizes the per-tick countdown +
    /// revalidation arm of the native ship update block
    /// (<c>shipIGC.cpp:270-293</c>) as a sector pass. For every ship here with
    /// an active ripcord and no parent (<c>m_pshipParent == NULL</c>,
    /// <c>shipIGC.cpp:271</c> — the same not-boarded gate P111's request
    /// handler uses, <c>ParentShip is null</c>), the anchor is revalidated via
    /// <see cref="RipcordAnchorStillLegal"/>; a legal anchor ticks the countdown
    /// down by <c>dt</c>, and <b>P113</b>: when the countdown reaches zero the
    /// ripper is handed to the host's <c>UseRipcord</c> (recorded in
    /// <see cref="DrainRipcordTeleports"/>, drained by Server.PerformRipcordTeleport)
    /// — the actual relocation — rather than being clamped to zero and left
    /// active. An illegal anchor has its ripcord state cleared (mirroring
    /// P111's cancel path) and is recorded in
    /// <see cref="DrainRipcordAborts"/> so the host broadcasts
    /// <c>RIPCORD_ABORTED(187)</c> sector-wide. A boarded ship's ripcord is
    /// simply not ticked (frozen), exactly as the native <c>m_pshipParent ==
    /// NULL</c> gate freezes it — no abort on boarding.
    /// </summary>
    internal void ResolveRipcordCountdowns(float dt)
    {
        foreach (var model in _models)
        {
            if (model is not Ship ship) continue;
            if (!ship.IsRipcordActive || ship.ParentShip is not null) continue;

            var anchor = ship.RipcordModel!.Value;
            if (RipcordAnchorStillLegal(ship, anchor))
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
                    _pendingRipcordTeleports.Add(ship);
            }
            else
            {
                ship.SetRipcordModel(null);
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
    /// <c>RipcordAnchorStillLegal</c> cites for its own bAllowAlliedRip
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

                _pendingRescues.Add((rescuee, rescuer));
                done.Add(a);
                done.Add(b);
            }
        }
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

    /// <summary>
    /// P112 — the port of <c>ContinueRipcord</c>'s revalidation
    /// (<c>FedSrv.CPP:13379-13437</c>), <b>"regular" (same-side) branch only</b>.
    /// This port does not decode <c>MissionDef.misparms.bAllowAlliedRip</c> and
    /// has no alliance/<c>AlliedSides</c> concept at all, so the allied-allowance
    /// <c>if</c> is dropped unconditionally — the same simplification precedent
    /// P107 set for <c>bEjectPods</c> (stated plainly in the P112 report).
    /// Three gates:
    /// <list type="number">
    /// <item><b>Flag gate</b> (<c>pship-&gt;GetFlag() != NA</c>,
    /// <c>FedSrv.CPP:13385</c>) — collapses to always-passing, this port tracks
    /// no flags (same precedent as P111's request gate).</item>
    /// <item><b>Same-side</b> (<c>pship-&gt;GetSide() != pmodel-&gt;GetSide()</c>,
    /// <c>FedSrv.CPP:13387</c>) — the anchor's current side is read off the
    /// resolved model, not cached (native dereferences the pointer each tick).</item>
    /// <item><b>Anchor still has a cluster</b> (<c>pmodel-&gt;GetCluster() == NULL</c>,
    /// <c>FedSrv.CPP:13388</c>) — the anchor left its own cluster (a station was
    /// captured/destroyed, a target ship died/warped away). Read as: the anchor
    /// is still present in its <see cref="RipcordTarget.ClusterId"/> sector.</item>
    /// </list>
    /// For a <b>ship</b> anchor the identical hull-capability re-check
    /// <c>FindRipcordModel</c> Tier 4 already applies (<c>shipIGC.cpp:3900-3906</c>;
    /// the port's copy lives in <c>Server.FindRipcordModel</c>) is performed —
    /// a <b>second read</b> of the same check, not a reimplementation. A station
    /// anchor always passes (native <c>else rc = true</c>, <c>FedSrv.CPP:13396</c>).
    /// </summary>
    private static bool RipcordAnchorStillLegal(Ship ship, Ship.RipcordTarget anchor)
    {
        var mission = ship.Mission;
        if (mission is null) return false;
        var cluster = mission.Sectors.FirstOrDefault(s => s.Id == anchor.ClusterId);
        if (cluster is null) return false;   // requested sector gone → anchor has no cluster

        if (anchor.Ot == (short)IgcObjectType.Station)
        {
            DataStationIGC? station = null;
            foreach (var st in cluster.Stations)
                if (st.StationId == anchor.Oid) { station = st; break; }
            if (station is null) return false;               // captured/destroyed → no cluster (FedSrv.CPP:13388)
            return station.Value.SideId == ship.SideId;      // same-side only (FedSrv.CPP:13387)
        }

        if (anchor.Ot == (short)IgcObjectType.Ship)
        {
            Ship? target = null;
            foreach (var m in cluster.Models)
                if (m is Ship s && s.Id == anchor.Oid) { target = s; break; }
            if (target is null) return false;                // died / removed → no cluster (FedSrv.CPP:13388)
            if (target.Sector?.Id != anchor.ClusterId) return false;  // warped away
            if (target.SideId != ship.SideId) return false;  // same-side only (FedSrv.CPP:13387)

            // Identical hull-capability re-check to FindRipcordModel Tier 4
            // (shipIGC.cpp:3900-3906) — a second read, not a reimplementation.
            short habm = ship.HullType.HasCapability(DataHullTypeIGC.HabmCanLtRipcord)
                ? (short)(DataHullTypeIGC.HabmIsRipcordTarget | DataHullTypeIGC.HabmIsLtRipcordTarget)
                : DataHullTypeIGC.HabmIsRipcordTarget;
            return target.HullType.HasCapability(habm);
        }

        return false;   // unsupported anchor type can never be legal here
    }

    /// <summary>
    /// The warp-transit debounce window, in seconds — the port of the C++
    /// <c>CFSShip::m_warpState</c> re-arm fudge (<c>fsship.cpp:1188</c>:
    /// <c>m_timeNextWarp = g.timeNow + 2.0f</c>). A ship that crosses an aleph
    /// cannot cross again until this much sim-time elapses, mirroring native's
    /// <c>warpNoUpdate → warpWaiting → warpReady</c> cycle collapsed to a single
    /// threshold. Compared against <see cref="Ship.LastWarpTime"/>.
    /// </summary>
    public const double WarpCooldownSeconds = 2.0;

    /// <summary>
    /// The <b>warp-bomb delay</b>, in seconds — the time between a warp being
    /// hit by a warp-bomb missile and the real explosion detonating at the
    /// aleph. This is the mission float constant <c>c_fcidWarpBombDelay</c>
    /// (<c>igc.h:98: <c>const FloatConstantID c_fcidWarpBombDelay = 32;</c></c>),
    /// which native sets at mission init to <c>c_fWarpBombDelay = 15.0f</c>
    /// (<c>missionigc.cpp:2726</c> and the assignment at
    /// <c>missionigc.cpp:2777</c>: <c>floatConstants[32] = c_fWarpBombDelay</c>).
    /// <see cref="FedSrvSiteBase.WarpBombEvent"/> reads it via
    /// <c>GetFloatConstant(c_fcidWarpBombDelay)</c> (<c>FedSrv.CPP:12551</c>)
    /// for <c>timeExplosion = pwarp->GetLastUpdate() + &lt;delay&gt;</c> — the
    /// same slot <see cref="ServerHost.ExitWarpSpeedBonus"/> and
    /// <c>Ship.DamageConstant</c> index into. Like <see cref="Mission.IncomePerPayday"/>,
    /// the mission overwrites whatever the core's <c>FloatConstants[32]</c> holds,
    /// so we use the fixed value <c>15.0f</c> and say so (index and value both
    /// verified directly against the reference source — this session located the
    /// index, which the task's framing left open). Used by both the trigger pass
    /// (schedule) and the server's broadcast (wire timeExplosion tick).
    /// </summary>
    public const double WarpBombDelaySeconds = 15.0;


    /// <summary>
    /// The undock debounce window, in seconds — mirrors
    /// <see cref="WarpCooldownSeconds"/>'s own shape for the analogous
    /// problem on the dock side. Longer than the warp window: a warp
    /// transit relocates the ship well clear of the source aleph as part of
    /// the transit itself, but an undock only clears
    /// <see cref="DockRadius"/> by a fixed margin (<c>Server.cs</c>'s
    /// <c>LaunchClearMargin</c>) with no guarantee the ship's own autopilot
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

    /// <summary>
    /// Flat per-second mining rate (ore/s), standing in for the native
    /// <c>dT · ga.GetAttribute(c_gaMiningRate)</c> term (<c>shipIGC.cpp:2212</c>).
    /// <c>c_gaMiningRate</c> is a per-side tech-tree attribute index
    /// (<c>igc.h:611</c>) whose default lives in the core data this port does not
    /// decode — hence a flat literal, chosen large enough that a test reaches
    /// its assertions in a handful of ticks. Labelled a simplification, not a
    /// verified native value.
    /// </summary>
    public const float MiningRate = 100.0f;

    /// <summary>
    /// The regeneration rate constant in the native formula
    /// <c>dOre = dT · regenConst · (oreMax − ore)</c>
    /// (<c>asteroidigc.h:52-55</c>). <c>c_fcidHe3Regeneration</c> is a mission
    /// float-constant index (<c>igc.h:75</c>) whose actual default is set from
    /// core data, so this port substitutes a flat value and says so. Chosen
    /// <b>small</b> (relative to <see cref="MiningRate"/>) so that a rock the
    /// miner is draining is observably held near-empty within a short test —
    /// the "pool limits mining" behaviour the verification wants — while still
    /// being the faithful native regrowth (continuous, no permanent depletion),
    /// not a fabricated native constant.
    /// </summary>
    public const float He3Regeneration = 0.025f;

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
    private void ResolveCombatCollisions(double now, float dt)
    {
        if (!_models.Any(m => m is Projectile || m is Missile)) return;   // nothing can deal damage

        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            Collider? c = model switch
            {
                IOrdnance o => new Collider(o, o.Position, o.Velocity, Orientation.Identity,
                    hull: null, o.Radius, isStatic: false, HitShape.Sphere),
                Ship s => new Collider(s, s.Position, s.Velocity, s.Orientation,
                    hull: s.Hull, 0.5f * s.HullType.Length, isStatic: false, HitShape.Sphere),
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
                hull: null, ast.Radius, isStatic: true, HitShape.Sphere));
        }

        var events = detector.Detect(dt);
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
            if (ord is null || (ship is null && probe is null && asteroid is null)) continue;
            if (ReferenceEquals(ord, ship) || ReferenceEquals(ord, probe) || ReferenceEquals(ord, asteroid)) continue;
            if (spent.Contains(ord)) continue;            // already terminated this step

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
                if (ord.Launcher is Ship shooter
                    && shooter.SideId is { } shooterSide
                    && ship.SideId == shooterSide)
                    continue;

                var position2 = ship.Position + ship.CollisionVelocity * ev.TCollision;

                // Damage = decoded ordnance Power (× c_gaDamageGuns = 1, no side
                // attribute), of the decoded DamageType; C++ projectileIGC.cpp:177-190.
                // Missiles use the identical single-target call — a missile is
                // single-target this slice; BlastPower/BlastRadius (AoE) are read
                // but not acted on (same honest-deferral as homing, P91).
                ship.ReceiveDamage(
                    ord.DamageType,
                    ord.Power,
                    now,
                    position1,
                    position2,
                    ord.Launcher);

                spent.Add(ord);
                _models.Remove((Model)ord);            // HandleCollision → Terminate (projectileIGC.cpp:230)

                // Blast-on-impact (P97): an area-effect round (BlastPower ≠ 0)
                // additionally creates a REAL explosion at the impact position,
                // separate from and ADDITIVE to the direct single-target hit just
                // above — native projectileIGC.cpp:206-219, "Area effect weapon:
                // create an explosion (no matter what)". See the probe branch
                // below for why this same block lives in both target paths.
                if (ord.BlastPower != 0f)
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }
                if (ship.Dead) _models.Remove(ship);   // killed: leaves the sector (README death contract)
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

                var position2 = probe.Position;         // probe never moves → zero collision velocity (probeigc.cpp:115,142)

                probe.ReceiveDamage(
                    ord.DamageType,
                    ord.Power,
                    now,
                    position1,
                    position2,
                    ord.Launcher);

                spent.Add(ord);
                _models.Remove((Model)ord);            // HandleCollision → Terminate (projectileIGC.cpp:230)

                // Blast-on-impact (P97) fires UNCONDITIONALLY per the native's
                // own comment ("Area effect weapon: create an explosion (no
                // matter what)", projectileIGC.cpp:206) — it must NOT care what
                // the direct target was. A blast-capable round that hits a probe
                // still creates the real Explosion at the impact position
                // exactly as hitting a ship. Duplicated here (rather than a
                // shared helper) to keep each target branch self-contained,
                // per the project's preference for a little duplication over a
                // forced abstraction where the two paths genuinely differ.
                if (ord.BlastPower != 0f)
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }
                if (probe.Dead) _models.Remove(probe); // destroyed: leaves the sector, identical to the ship's death bookkeeping
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
                    ord.Power,
                    now,
                    position1,
                    apos,
                    ord.Launcher);

                spent.Add(ord);
                _models.Remove((Model)ord);            // HandleCollision → Terminate (projectileIGC.cpp:230)

                // Blast-on-impact (P97) fires UNCONDITIONALLY per the native's
                // own comment ("Area effect weapon: create an explosion (no
                // matter what)", projectileIGC.cpp:206) — it must NOT care what
                // the direct target was. A blast-capable round that hits an
                // asteroid still creates the real Explosion at the impact position
                // exactly as hitting a ship or probe. Duplicated here for the
                // same self-containment reason noted in the probe branch.
                if (ord.BlastPower != 0f)
                {
                    var blast = new Explosion(
                        ord.DamageType,
                        ord.BlastPower,
                        ord.BlastRadius,
                        now,
                        position1,
                        ord.Launcher);
                    ResolveExplosion(blast, now);
                }

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
    /// The <b>mine detonation</b> proximity pass, run once per sector tick after
    /// the combat pass. This is the port's chosen integration shape for the
    /// native mine trigger: rather than joining the swept
    /// <see cref="CollisionDetector"/> the way projectiles/missiles do (a mine
    /// is a static, velocity-less body — there is no swept crossing for a ship
    /// that is already inside the radius, or for one sitting still), it is a
    /// deliberate <b>point-in-sphere</b> test, structurally the twin of
    /// <see cref="ResolveDockCollisions"/>/<see cref="ResolveWarpCollisions"/>
    /// (<c>|ship − mine| ≤ MineRadius + 0.5·shipLength</c>). It is the same
    /// explicit simplification this repo already made elsewhere: the native
    /// entry/exit dwell-time integration and the <c>"ship must be moving"</c>
    /// gate (<c>mineigc.h:80-82</c>) are <em>not</em> ported — a ship sitting
    /// still next to a mine still sets it off, exactly once, at full
    /// <see cref="DataMineTypeIGC.Power"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Arm delay.</b> A mine cannot trigger before
    /// <c>now ≥ DeployTime + Mine.ArmTimeSeconds</c> (<c>c_dtArmTime = 1.0f</c>,
    /// <c>mineigc.h:75,77</c>). Until then the mine is inert and no damage is
    /// applied, even if a ship sits inside its radius.</para>
    /// <para><b>Side gate.</b> Mirrors <see cref="ResolveCombatCollisions"/>'s
    /// friendly-fire convention exactly: a mine never damages a ship whose
    /// <see cref="Ship.SideId"/> equals the mine's <see cref="Mine.SideId"/>,
    /// engaging only when <em>both</em> carry a side. Different sides or a
    /// side-less ship still trigger it. Same shape as native
    /// (<c>mineigc.h:77</c>): <c>(pmodel-&gt;GetSide() != GetSide())</c>.</para>
    /// <para><b>Trigger &amp; blast (P96 — extended from single-target).</b> Any
    /// live, placed ship within <see cref="DataMineTypeIGC.MineRadius"/> (+ the
    /// ship's own <c>0.5·length</c> radius, the same target-radius-plus-ship-radius
    /// convention <see cref="ResolveMining"/> uses) of an armed, hostile-side
    /// mine triggers it. The ship's radius is the half-length — the identical
    /// value the combat and warp passes use (<c>Sector.cs:174</c>,
    /// <c>shipIGC.cpp:1419</c>). <b>One mine detonates against the first
    /// qualifying ship found per tick</b> (the mine-ordering of P95 unchanged) —
    /// but instead of the former single-target <c>ReceiveDamage</c>, the
    /// detonation now <b>creates a real <see cref="Explosion"/></b> at the mine's
    /// position (blast params = <c>MineType.DamageType/Power/MineRadius</c>) and
    /// hands it to <see cref="ResolveExplosion"/>, which applies falloff damage to
    /// <em>every</em> ship and mine in blast range. This is a deliberate
    /// <b>behaviour change from P95</b> — mine detonation went from a
    /// single-target hit to a real multi-target blast, which is more faithful to
    /// the real game, not less (a lone target in range still takes full/near-full
    /// falloff-adjusted damage, so P95's single-nearby-target scenarios stand).
    /// The generic blast-resolution pass is this task's core deliverable; the
    /// mine-detonation call site is its one wiring point.</para>
    /// <para><b>Consumption.</b> The detonated mine is removed from the sector and
    /// raises <see cref="IIgcSite.MineDestroyedEvent"/> exactly once before the
    /// blast resolves — the same consistency P91 established for missiles (a
    /// same-side peer who saw the mine deploy always learns of its destruction,
    /// expiry or detonation, no cause field on the wire), reusing <see cref="Mine.OnUpdate"/>'s
    /// own expiry-path calls verbatim. Resolving the blast <em>after</em>
    /// removal means the spent mine is not re-processed by its own explosion
    /// (<see cref="ResolveExplosion"/> snapshots the live models).</para>
    /// <para><b>Safety of the mutation.</b> The pass iterates a snapshot, so
    /// removing the detonated mine mid-iteration cannot invalidate the walk;
    /// <see cref="Ship.Dead"/> guards against hitting a ship killed earlier in
    /// the same pass. The launcher-self exclusion (a ship cannot detonate its
    /// own mine) mirrors the combat pass's own-launcher rule.</para>
    /// </remarks>
    private void ResolveMineDetonations(double now)
    {
        if (!_models.Any(m => m is Mine)) return;   // nothing can detonate

        var snapshot = _models.ToArray();
        foreach (var model in snapshot)
        {
            if (model is not Mine mine) continue;
            if (now < mine.DeployTime + Mine.ArmTimeSeconds) continue;  // not armed (mineigc.h:75,77)

            var blastRadius = mine.MineType.MineRadius;
            foreach (var candidate in snapshot)
            {
                if (candidate is not Ship ship) continue;
                if (ship.Dead) continue;                       // can't hurt the dead
                if (ReferenceEquals(ship, mine.Launcher)) continue; // never hit your own launcher

                // Friendly-fire gate (mineigc.h:77): a mine never damages a
                // ship on its own SideId, engaging only when both carry a side
                // (same convention as ResolveCombatCollisions, Sector.cs:325-328).
                if (mine.SideId is { } mineSide && ship.SideId == mineSide) continue;

                // Point-in-sphere: |ship − mine| ≤ blastRadius + shipRadius
                // (ship radius = half-length, the identical sphere the combat /
                // warp / mining passes build, shipIGC.cpp:1419). Simplified
                // trigger: no velocity requirement, no dwell-time integration.
                float shipRadius = 0.5f * ship.HullType.Length;
                float threshold = blastRadius + shipRadius;
                float dx = ship.Position.X - mine.Position.X;
                float dy = ship.Position.Y - mine.Position.Y;
                float dz = ship.Position.Z - mine.Position.Z;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;

                // Detonate: create a real Explosion at the mine's position with
                // the mine type's blast parameters and let the generic
                // blast-resolution pass apply falloff damage to every ship and
                // mine in range. Launcher = the laying ship, so the pass's
                // friendly-fire gate treats mine splash exactly like any other
                // launcher-sourced damage. (Immediate/synchronous resolution —
                // see ResolveExplosion's remarks.)
                var blast = new Explosion(
                    mine.MineType.DamageType,
                    mine.MineType.Power,
                    mine.MineType.MineRadius,
                    now,
                    mine.Position,
                    mine.Launcher);

                // Consume the mine exactly once and broadcast the destruction —
                // the identical pair of calls Mine.OnUpdate's expiry makes
                // (Mine.cs:183-192), reaching the same same-side audience that
                // saw it deploy. Resolution AFTER removal means the spent mine
                // is not re-processed by its own blast.
                _models.Remove(mine);
                if (mine.Launcher is Ship launcherShip)
                    launcherShip.Mission?.Site.MineDestroyedEvent(mine);

                ResolveExplosion(blast, now);
                break;
            }
        }
    }

    /// <summary>
    /// The <b>explosion / blast-radius resolution</b> pass — the port of the
    /// native <c>CclusterIGC::Update</c> explosion block
    /// (<c>clusterIGC.cpp:362-467</c>), resolved <b>immediately and
    /// synchronously</b> against each target's current position. Given a
    /// <see cref="Explosion"/>, every live <see cref="Ship"/> within
    /// <see cref="Explosion.Radius"/> of <see cref="Explosion.Position"/> takes
    /// the squared-distance-falloff damage <c>amount = e.Amount·f²</c> where
    /// <c>f = 1 − d/radius</c> and <c>d</c> is the distance from the blast
    /// centre to the ship's <em>surface</em> (centre-minus-the-ship's-own
    /// radius, <c>clusterIGC.cpp:420,424-429</c>); every live <see cref="Mine"/>
    /// within <see cref="Explosion.Radius"/> instead gets
    /// <see cref="Mine.ReduceStrength(amount)"/> with the identical falloff
    /// (<c>clusterIGC.cpp:448-458</c>). Targets only <see cref="Ship"/> and
    /// <see cref="Mine"/> (asteroids/stations/probes have no damage/destruction
    /// model in this port yet — the native's list includes them,
    /// <c>clusterIGC.cpp:378-379</c>, but this codebase can't act on that
    /// fidelity without building those models first).
    /// </summary>
    /// <remarks>
    /// <para><b>Immediate-resolution simplification (stated plainly).</b> The
    /// native queues an <c>ExplosionData</c> in <c>m_explosions</c> and resolves
    /// it on a <em>later</em> tick, projecting each target's position by
    /// <c>dt = (e.time − m_lastUpdate) − m_tOffset</c>
    /// (<c>clusterIGC.cpp:409,419</c>). This port resolves the blast the moment
    /// it is created, using each target's <em>current</em> position — the same
    /// honest simplification category as P95's "no swept dwell-time math." With
    /// ships and static mines (both of which stand effectively still on the
    /// tick where the blast resolves, since <see cref="ResolveMineDetonations"/>
    /// fires immediately), the projection would add nothing but a fraction of a
    /// second of displacement.</para>
    /// <para><b>Shared damage path.</b> Ships use the reused
    /// <see cref="Ship.ReceiveDamage"/> — shields, armor matrix and the
    /// friendly-fire contract all flow through the one real path, not a
    /// reimplementation. The friendly gate is the existing one: a ship whose
    /// <see cref="Ship.SideId"/> equals the launcher's (<c>Ship</c> launcher only)
    /// or that <em>is</em> the launcher is skipped, exactly the gate
    /// <see cref="ResolveMineDetonations"/>/<see cref="ResolveCombatCollisions"/>
    /// enforce (no new gate invented). Mines are <em>not</em> side-gated — the
    /// native calls <c>ReduceStrength</c> on every mine in radius regardless of
    /// side (<c>clusterIGC.cpp:441-458</c>), so neither is this pass.</para>
    /// <para><b>Mutation safety.</b> Iterates a snapshot, so a target that
    /// removes itself mid-pass (a <see cref="Ship"/> killed by the blast, a
    /// <see cref="Mine"/> whose <see cref="Mine.ReduceStrength"/> crosses the
    /// <c>0.25f</c> threshold) cannot invalidate the walk. Each target is
    /// processed at most once per blast.</para>
    /// </remarks>
    public void ResolveExplosion(Explosion e, double now)
    {
        var snapshot = _models.ToArray();

        // Ships: falloff ReceiveDamage via the one real damage path, gated by
        // launcher side (self + friendly).
        foreach (var model in snapshot)
        {
            if (model is not Ship ship) continue;
            if (ship.Dead) continue;
            if (e.Launcher is Ship launcherShip)
            {
                if (ReferenceEquals(launcherShip, ship)) continue;      // never hit your own launcher
                if (launcherShip.SideId is { } launcherSide && ship.SideId == launcherSide)
                    continue;                                          // friendly-fire gate
            }

            float shipRadius = 0.5f * ship.HullType.Length;            // shipIGC.cpp:1419
            float d = (e.Position - ship.Position).Length() - shipRadius;
            if (d >= e.Radius) continue;                                // outside blast (clusterIGC.cpp:422)

            float amount = e.Amount;
            if (d > 0f)                                                 // (clusterIGC.cpp:425-429)
            {
                float f = 1f - (d / e.Radius);
                amount *= f * f;
            }

            ship.ReceiveDamage(e.DamageType, amount, e.Time, e.Position, ship.Position, e.Launcher);
            if (ship.Dead) _models.Remove(ship);                        // killed: leaves the sector (death contract)
        }

        // Mines: ReduceStrength with the identical falloff, no side gate.
        foreach (var model in snapshot)
        {
            if (model is not Mine mine) continue;

            // Surface-distance falloff for a mine treats it as point-like
            // (radius 0): the port's decoded base `Radius` for OT_mineType is a
            // constant junk sentinel (−1.07e8 on every PCore001 mine; see
            // ReduceStrength remarks / EXPLOSION_PROPAGATION_P96.md), so it is
            // deliberately NOT fed into the falloff. A mine sits inside the
            // blast if its position is within e.Radius — the native's intent,
            // minus a few units of negligible surface radius vs. blast radii of
            // ~100 (clusterIGC.cpp:448: d = |e.pos − p| − pTarget->GetRadius()).
            float d = (e.Position - mine.Position).Length();
            if (d >= e.Radius) continue;                                // outside blast

            float amount = e.Amount;
            if (d > 0f)
            {
                float f = 1f - (d / e.Radius);
                amount *= f * f;
            }

            mine.ReduceStrength(amount);    // removes + raises MineDestroyedEvent when it crosses 0.25f
        }
    }

    /// <summary>
    /// The <b>warp-transit trigger</b> pass, run once per sector tick after the
    /// combat pass. This is the C# analogue of the warp half of
    /// <c>CshipIGC::HandleCollision</c> (<c>shipIGC.cpp:629-636</c>): a ship
    /// whose position lies within a warp's collision volume is considered to
    /// have hit the aleph, and the host's <see cref="IIgcSite.HitWarpEvent"/>
    /// is raised so the seam can perform the actual transit (position /
    /// orientation / velocity change + sector reassignment,
    /// <c>CFSShip::HitWarp</c>, <c>fsship.cpp:71-137</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately simpler than <see cref=\"ResolveCombatCollisions\"/>.</b>
    /// A warp is stationary — there is no moving-object swept collision to
    /// resolve, just a point-in-sphere test each tick: <c>|ship − warp| ≤
    /// warp.Radius + ship.Radius</c>. No <see cref=\"CollisionDetector\"/> is
    /// needed. This matches the task's own guidance (a warp is not a ship; the
    /// symmetric solver is overkill for a fixed trigger volume).</para>
    /// <para><b>Debounce</b> (native <c>m_warpState</c>,
    /// <c>fsship.cpp:74,1185-1191</c>): a ship that crossed <em>must not</em>
    /// re-trigger every tick while it lingers inside the volume. The transit
    /// repositions the ship away from the source warp and reassigns its
    /// <see cref=\"Ship.Sector\"/>, so the natural geometry largely debounces
    /// it; the <see cref=\"Ship.LastWarpTime\"/> /
    /// <see cref=\"WarpCooldownSeconds\"/> window is the belt-and-braces that
    /// collapses native's <c>warpNoUpdate → warpWaiting → warpReady</c> state
    /// machine into one threshold, kept on the ship so it survives the sector
    /// transfer (the destination sector's own warp must not swallow the ship
    /// right back). The window is set by the host transit on the sim thread,
    /// mirroring native's "only re-arm after a client update + 2 s".</para>
    /// <para><b>Scope.</b> Single-ship, per-sector: only ships physically in
    /// this sector (<see cref=\"_models\"/>) are tested, and each fires at most
    /// one warp per tick (the <c>break</c>), matching the C++ single-owner
    /// collision event. A dangling warp (<see cref=\"ResolvedWarp.Destination\"/>
    /// null) or one lacking a position is skipped, never fabricated.</para>
    /// </remarks>
    private void ResolveWarpCollisions(double now)
    {
        if (_warps.Count == 0) return;              // no alephs to hit
        foreach (var model in _models.ToArray())    // snapshot survives self-transit (remove from this sector)
        {
            if (model is not Ship ship) continue;
            if (ship.Dead) continue;                 // the dead cannot warp
            if (ship.Mission is null) continue;      // unplaced ship has no host to route to
            if (now - ship.LastWarpTime < WarpCooldownSeconds) continue;  // debounce (native warpState)

            // Ship radius is the half-length, exactly as the combat pass builds
            // its sphere (Sector.cs:174, shipIGC.cpp:1419).
            float shipRadius = 0.5f * ship.HullType.Length;
            foreach (var warp in _warps)
            {
                if (warp.Destination is null) continue;              // dangling edge: nothing to cross into
                if (warp.Warp.Position is not { Length: 3 } p) continue;  // no position: can't test
                var wpos = new Vector3(p[0], p[1], p[2]);
                float dx = ship.Position.X - wpos.X;
                float dy = ship.Position.Y - wpos.Y;
                float dz = ship.Position.Z - wpos.Z;
                float threshold = warp.Warp.Radius + shipRadius;
                if (dx * dx + dy * dy + dz * dz <= threshold * threshold)
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
    /// dealing damage. It mirrors <see cref="ResolveWarpCollisions"/>'s shape
    /// (a warp is stationary — a point-in-sphere test each tick:
    /// <c>|missile − warp| ≤ warp.Radius + missile.Radius</c>, the same
    /// target-radius-plus-source-radius convention the mine/warp/mining passes
    /// use) but is a <b>dedicated pass</b>, not a parameterized variant of
    /// <see cref="ResolveWarpCollisions"/>: the two share only the outer
    /// <c>for each model × for each warp</c> loop, and the body is wholly
    /// different (ship debounce/transit vs. missile consumption + scheduling +
    /// two site events), so parameterizing the target type would thread
    /// disjoint branches through one method for no code savings.
    /// </summary>
    /// <remarks>
    /// <para><b>Trigger behaviour (scope P101).</b> On a live
    /// <see cref="Missile"/> within a warp's collision volume: (1) compute
    /// <c>timeExplosion = now + <see cref="WarpBombDelaySeconds"/></c> and
    /// schedule the real delayed detonation at the warp's position;
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
    /// <para><b>No destination gate.</b> Unlike the ship-transit pass
    /// (<see cref="ResolveWarpCollisions"/> skips a dangling warp), the bomb
    /// trigger does <b>not</b> require <see cref="ResolvedWarp.Destination"/>:
    /// native bombards whatever warp it hits — the destination is only consulted
    /// for the (unported) dual-end explosion and the late-joiner re-export, both
    /// out of scope. A warp with no destination still detonates here.</para>
    /// </remarks>
    private void ResolveWarpBombTriggers(double now)
    {
        if (_warps.Count == 0) return;              // no alephs to bomb
        foreach (var model in _models.ToArray())    // snapshot survives removal
        {
            if (model is not Missile missile) continue;

            foreach (var warp in _warps)
            {
                if (warp.Warp.Position is not { Length: 3 } p) continue;  // no position: can't test
                var wpos = new Vector3(p[0], p[1], p[2]);
                float threshold = warp.Warp.Radius + missile.MissileType.Radius;  // target-radius + source-radius
                float dx = missile.Position.X - wpos.X;
                float dy = missile.Position.Y - wpos.Y;
                float dz = missile.Position.Z - wpos.Z;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;

                // (1) schedule the delayed detonation at the warp's position.
                double timeExplosion = now + WarpBombDelaySeconds;
                ScheduleWarpBomb(timeExplosion, wpos, missile.MissileType);

                // (2) consume the missile NOW (at trigger, not at timeExplosion) —
                // the same terminate-on-impact contract every other missile path
                // honors (native falls through to KillMissileEvent, missileigc.cpp:512).
                _models.Remove(missile);

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

    /// <summary>
    /// The <b>warp-bomb detonation</b> pass — the timed half of the pair. Once
    /// per sector tick it scans the sector's <b>scheduled</b> warp bombs (a
    /// <see cref="ScheduleWarpBomb"/> record created by
    /// <see cref="ResolveWarpBombTriggers"/> when a missile hit the warp) and,
    /// for each whose <see cref=\"ScheduledWarpBomb.TimeExplosion\"/> has
    /// arrived, resolves a <b>real</b> <see cref=\"Explosion\"/> at the warp's
    /// position via the reused <see cref=\"ResolveExplosion\"/> engine — nothing
    /// detonates before its scheduled instant. This mirrors how the native warp
    /// polls its <c>m_bombs</c> list each <c>Update</c>
    /// (<c>warpIGC.h:54-90</c>) and pops bombs whose <c>timeExplosion &lt;= now</c>,
    /// creating an explosion at the warp position with the missile type's
    /// <c>DamageType</c>, <c>Power</c> and <c>BlastRadius</c>
    /// (<c>warpIGC.h:65-67,71-77</c>). <b>The amount is the missile's
    /// <c>Power</c>, not <c>BlastPower</c></b> — the task's framing guessed
    /// <c>BlastPower</c> (the normal missile blast, P97), but the native warp-bomb
    /// detonation uses <c>GetPower()</c> (<c>warpIGC.h:66</c>), so this follows
    /// the reference; it is documented in the report. <c>Launcher = null</c>, so
    /// <see cref=\"ResolveExplosion\"/>'s friendly-fire gate is inert — every
    /// ship in the blast takes falloff damage, exactly the native
    /// <c>CreateExplosion(..., NULL)</c> (<c>warpIGC.h:77</c>). The dual-capability
    /// destination-end explosion (<c>warpIGC.h:79-87</c>) and the
    /// <c>WarpBombLink</c>/<c>GetBombs()</c> late-joiner re-export are both out
    /// of scope (see the task report).</summary>
    /// <remarks>Timer shape: a stored absolute sim-time (<c>TimeExplosion</c>)
    /// checked each tick — the same "store a deadline, poll for it" shape the
    /// <see cref=\"Probe\"/>/<see cref=\"Mine\"/>/<see cref=\"Missile\"/> expiry
    /// (<c>now &gt;= stored deadline</c>) uses, and the same shape the native
    /// warp uses for its own bomb list. Iterated backward so a detonated entry
    /// can be removed mid-walk without disturbing pending ones.</remarks>
    private void ResolveWarpBombDetonations(double now)
    {
        for (int i = _warpBombs.Count - 1; i >= 0; i--)
        {
            var b = _warpBombs[i];
            if (now < b.TimeExplosion) continue;      // nothing detonates early
            var blast = new Explosion(
                b.MissileType.DamageType,
                b.MissileType.Power,                  // warpIGC.h:66 — GetPower(), not BlastPower
                b.MissileType.BlastRadius,            // warpIGC.h:67
                b.TimeExplosion,
                b.Position,
                null);                                // warpIGC.h:77 — NULL launcher, no friendly gate
            ResolveExplosion(blast, now);
            _warpBombs.RemoveAt(i);
        }
    }

    /// <summary>
    /// A single pending warp-bomb detonation scheduled at
    /// <see cref="ResolveWarpBombTriggers"/> time and resolved by
    /// <see cref="ResolveWarpBombDetonations"/> when the sim clock reaches
    /// <see cref="TimeExplosion"/>. Carries the missile type's decoded
    /// <c>DamageType</c>/<c>Power</c>/<c>BlastRadius</c> implicitly (via
    /// <see cref="MissileType"/>) so the detonation reuses the <em>same</em>
    /// fields a normal missile blast uses, and the bombed warp's position so
    /// the explosion resolves at the aleph.
    /// </summary>
    private sealed record ScheduledWarpBomb(double TimeExplosion, Vector3 Position, DataMissileTypeIGC MissileType);

    /// <summary>Schedule a warp-bomb detonation at <paramref name="position"/>
    /// for <paramref name="timeExplosion"/>, using the missile type's decoded
    /// blast fields. Called by <see cref="ResolveWarpBombTriggers"/>; later
    /// ticks resolve it via <see cref="ResolveWarpBombDetonations"/>.</summary>
    private void ScheduleWarpBomb(double timeExplosion, Vector3 position, DataMissileTypeIGC missileType)
        => _warpBombs.Add(new ScheduledWarpBomb(timeExplosion, position, missileType));

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
    /// <para><b>Geometry: a point-in-sphere test, like the warp pass, not a
    /// swept collision.</b> Stations are stationary; <c>|ship − station| ≤
    /// <see cref="DockRadius"/></c> each tick, the <b>simplified</b> stand-in
    /// for the native slot-based <c>InGarage</c> (documented at
    /// <see cref="DockRadius"/>). No <see cref="CollisionDetector"/> is used.
    /// Distance-only (no near-zero-velocity clause): arrival in the reference
    /// is decided by geometry on the collision tick (<c>InGarage</c> is a
    /// position test), not by the ship having slowed — so requiring low velocity
    /// would diverge from the native semantics for no benefit, and it would make
    /// the loopback proof need a throttle choreography instead of a deterministic
    /// position check.</para>
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

            foreach (var station in _stations)
            {
                // Friendly-only gate (FedSrv.CPP:13100-13105 — hostile-capture
                // branch explicitly NOT ported; friendly docking only).
                if (station.SideId != side) continue;

                // Point-in-sphere against the station position (simplified
                // InGarage). DataStationIGC.Position is float[3].
                if (station.Position is not { Length: 3 } p) continue;  // no position: can't test
                var spos = new Vector3(p[0], p[1], p[2]);
                float dx = ship.Position.X - spos.X;
                float dy = ship.Position.Y - spos.Y;
                float dz = ship.Position.Z - spos.Z;
                if (dx * dx + dy * dy + dz * dz > DockRadius * DockRadius) continue;

                // Arrived at a friendly station — raise the seam. The host's
                // DockWithStationEvent latches the state (sets Ship.StationId)
                // and logs [dock]; its bool is the permission, consulted
                // (though after the gates above it is effectively always true).
                ship.Mission.Site.DockWithStationEvent(ship, new DockStation(station));
                break;                                  // one dock per tick per ship
            }
        }
    }

    /// <summary>
    /// The <b>mining</b> pass, run once per sector tick after the dock pass. It
    /// is the C# analogue of the reference's mining accrual (<c>shipIGC.cpp:
    /// 2206-2260</c>): a ship adjacent to and effectively stopped beside a
    /// minable asteroid accumulates ore per tick, draining the asteroid's own
    /// depletable pool correspondingly.
    /// </summary>
    /// <remarks>
    /// <para><b>Trigger: arrival-shaped, not command-gated</b> — matching this
    /// repo's established P53 simplification for docking ("arrival-triggered,
    /// not command-gated"). Native gates mining on a client-issued
    /// <c>c_cidMine</c> command targeting a specific rock
    /// (<c>shipIGC.cpp:1952-1955</c>); this server's architecture does not
    /// track client-issued high-level commands server-side, so like docking the
    /// trigger is purely geometric: proximity + near-zero velocity against
    /// <em>any</em> minable asteroid. The native gate itself is reproduced
    /// exactly: <c>(distance² &lt; radius²) &amp;&amp; (velocity² &lt; 1.0)</c>
    /// with <c>radius = asteroid.Radius + ship.Radius + 30.0f</c>
    /// (<c>shipIGC.cpp:1957-1961</c>) — the same "adjacent and stopped" shape
    /// P53's dock check already uses.</para>
    /// <para><b>No crowding, no side gate</b> (explicit task scope): native lets
    /// only one ship/group mine a rock at a time and bumps others
    /// (<c>shipIGC.cpp:2153-2202</c>); this simplified pass does not — any
    /// number of ships near the same rock may mine it. Asteroids are unowned
    /// (unlike stations) so there is no friendly-only gate at all.</para>
    /// <para><b>Accrual</b> — per tick, <c>minedOre = dt · <see cref="MiningRate"/></c>,
    /// clamped by the ship's flat <see cref="Ship.MineCapacity"/>, then drawn
    /// from the rock via <see cref="Asteroid.MineOre"/> which returns the actual
    /// (≤ requested) amount. <c>Ship.Ore</c> gains the actual. If the rock
    /// yields less than requested it is tapped: the pool gates the yield
    /// (<c>shipIGC.cpp:2240-2247</c>), and — because native asteroids
    /// regenerate (<c>asteroidigc.h:47-56</c>) — it is not permanently
    /// depleted, it is held near-empty by the miner (see <see cref="He3Regeneration"/>).</para>
    /// <para><b>Wire</b>: ore rides the existing ship-fractions byte of
    /// <c>FM_S_SINGLE_SHIP_UPDATE(78)</c> (<see cref="Wire.ServerShipUpdate.FromModel"/>
    /// fills <c>byte 45</c> from <see cref="Ship.Ore"/>), plus the
    /// <see cref="IIgcSite.MineAsteroidEvent"/> site seam is raised (throttled
    /// to the native &gt; 3.0 ore-change threshold, <c>asteroidigc.h:246-251</c>)
    /// so a host can observe/relay mining without a new wire message.</para>
    /// <para><b>One rock per ship per tick</b> (the <c>break</c>): a ship mines
    /// the first satisfying asteroid, matching the native single
    /// <c>c_cmdPlan</c> target (no simultaneous multi-rock mining).</para>
    /// <para><b>He3 gate</b>: only <see cref="Asteroid.CanMineHe3"/> rocks are
    /// considered — native asserts the capability on the mining target
    /// (<c>shipIGC.cpp:1955</c>) and only regenerates He3 rocks
    /// (<c>asteroidigc.h:49</c>).</para>
    /// </remarks>
    private void ResolveMining(double now, float dt)
    {
        if (_mineable.Count == 0) return;

        // Regenerate every mineable rock first (asteroidigc.h:47-56), so a
        // rock freshly drained at the previous tick has had a chance to regrow
        // before this tick's mining draws on it.
        foreach (var ast in _mineable) ast.Regenerate(dt, He3Regeneration);

        foreach (var model in _models.ToArray())
        {
            if (model is not Ship ship) continue;
            if (ship.Dead) continue;                      // the dead cannot mine
            if (ship.Mission is null) continue;          // unplaced ship has no host
            if (ship.Ore >= Ship.MineCapacity) continue; // bag full — can't accumulate more
            if (ship.StationId is not null) continue;    // sanity: a docked ship is not mining

            float shipRadius = 0.5f * ship.HullType.Length;
            foreach (var ast in _mineable)
            {
                if (!ast.CanMineHe3) continue;                           // not He3: inert (shipIGC.cpp:1955)
                if (ast.Data.Position is not { Length: 3 } p) continue;  // no position: can't test
                var apos = new Vector3(p[0], p[1], p[2]);
                float dx = ship.Position.X - apos.X;
                float dy = ship.Position.Y - apos.Y;
                float dz = ship.Position.Z - apos.Z;
                float threshold = ast.Radius + shipRadius + MiningProximityPadding;
                if (dx * dx + dy * dy + dz * dz > threshold * threshold) continue;   // not close enough (shipIGC.cpp:1960-1961)
                if (ship.Velocity.LengthSquared() >= 1.0f) continue;                 // not stopped (shipIGC.cpp:1961)

                // ── start-mining log (transition only) ────────────────────────
                if (!ship.IsMining)
                {
                    ship.IsMining = true;
                    var startNote = $"[mine] ship {ship.Id} began mining asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' (ore {ast.OreRemaining:0.##}/{ast.OreMax:0.##}) at {apos}";
                    Console.WriteLine(startNote);
                    ship.Mission.Site.ReportNotable(startNote);   // spectator event feed (same text)
                }

                // ── accrual (shipIGC.cpp:2211-2241) ──────────────────────────
                float minedOre = dt * MiningRate;                        // shipIGC.cpp:2212
                float cap = Ship.MineCapacity;
                bool hitCap = false;
                if (ship.Ore + minedOre >= cap)
                {
                    minedOre = cap - ship.Ore;                           // shipIGC.cpp:2217
                    hitCap = true;
                }
                float actual = ast.MineOre(minedOre);                    // clamp to rock's pool (asteroidigc.h:221-233)
                ship.Ore += actual;

                // ── cap-reached log ──────────────────────────────────────────
                if (hitCap && ship.Ore >= cap)
                    Console.WriteLine($"[mine] ship {ship.Id} is full ({ship.Ore:0.##}/{cap:0.##} ore) — mining paused");

                // ── rock-tapped log ──────────────────────────────────────────
                if (actual != minedOre)
                    Console.WriteLine($"[mine] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' tapped: yielded {actual:0.##} of {minedOre:0.##} requested — pool gates yield (regenerating)");

                // ── host seam (throttled inside Asteroid.MineOre to the
                // native > 3.0 ore-change threshold, asteroidigc.h:246-251) ──
                if (ast.HasPendingReport)
                {
                    ship.Mission.Site.MineAsteroidEvent(ast, ast.PendingReport);
                    ast.ClearPendingReport();
                }

                break;                                    // one rock per ship per tick
            }
        }

    }
}
