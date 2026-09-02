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

    /// <summary>This sector's asteroids.</summary>
    public IReadOnlyList<DataAsteroidIGC> Asteroids => _asteroids;

    /// <summary>Number of times <see cref="Update"/> has been called.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>The time of the most recent <see cref="Update"/>, or 0 before the first.</summary>
    public double LastUpdate { get; private set; }

    private readonly List<ResolvedWarp> _warps = [];
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

        // Warp-transit proximity pass — a stationary-object test (ship vs the
        // sector's warps), deliberately separate from the moving-object combat
        // collider above. See ResolveWarpCollisions for the spec and scope.
        ResolveWarpCollisions(timeUpdate);

        // Dock proximity pass — ship vs the sector's stations. See
        // ResolveDockCollisions for the spec, the friendly gate and the
        // simplified-radius reasoning.
        ResolveDockCollisions(timeUpdate);

        // Mining proximity pass — ship vs the sector's asteroids. See
        // ResolveMining for the spec (proximity + near-zero velocity, no
        // crowding / no per-side gate) and its simplified-rate reasoning.
        ResolveMining(timeUpdate, (float)dt);
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
    /// the step once, and resolve each projectile↔ship event in time order.
    /// Ships and projectiles are the only models today, so a collider is a
    /// sphere — the projectile's <c>DataProjectileTypeIGC.Radius</c>, the ship's
    /// half-length (<c>shipIGC.cpp:1419</c>: <c>SetRadius(0.5·length)</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Gated on there being a projectile.</b> Only a projectile deals
    /// damage in this slice (<c>CprojectileIGC::HandleCollision</c>, the sole
    /// damagable path; a ship↔ship or ship↔projectile that never exists cannot
    /// hurt anyone — <c>CshipIGC::HandleCollision</c> only separates/bounces).
    /// Without a projectile the whole pass is skipped and the sector tick is a
    /// pure model loop, so the flight/bot slices (which have ships but never a
    /// projectile) are untouched.</para>
    /// <para><b>Resolution, in C++ order.</b> The queue is time-ordered; each
    /// event is handled once. The projectile that hits terminates immediately
    /// (<c>HandleCollision</c> calls <c>Terminate()</c>, <c>projectileIGC.cpp:230</c>),
    /// so a projectile can strike at most the earliest target in a step (later
    /// events for a spent projectile are skipped). A projectile never damages
    /// its own <see cref="Projectile.Launcher"/>, and (friendly-fire gate,
    /// <c>shipIGC.cpp:1222-1228</c>) never damages a ship on the same
    /// <see cref="Ship.SideId"/>; both cases pass the round through (see
    /// <see cref="Ship.ReceiveDamage"/>). A ship whose hull reaches zero emits
    /// <c>KillShipEvent</c> <b>once</b> and is removed from the sector (see
    /// README for the death contract).</para>
    /// </remarks>
    private void ResolveCombatCollisions(double now, float dt)
    {
        if (!_models.Any(m => m is Projectile)) return;   // nothing can deal damage

        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            Collider? c = model switch
            {
                Projectile p => new Collider(p, p.Position, p.Velocity, Orientation.Identity,
                    hull: null, p.ProjectileType.Radius, isStatic: false, HitShape.Sphere),
                Ship s => new Collider(s, s.Position, s.Velocity, s.Orientation,
                    hull: s.Hull, 0.5f * s.HullType.Length, isStatic: false, HitShape.Sphere),
                _ => null,
            };
            if (c is not null) detector.Add(c);
        }

        var events = detector.Detect(dt);
        var spent = new HashSet<Projectile>();
        foreach (var ev in events)
        {
            // Identify the projectile and the ship in whatever registration
            // order the detector returned them (only projectile↔ship pairs act).
            var proj = ev.A.Data as Projectile ?? ev.B.Data as Projectile;
            var ship = ev.B.Data as Ship ?? ev.A.Data as Ship;
            if (proj is null || ship is null || ReferenceEquals(proj, ship)) continue;
            if (spent.Contains(proj)) continue;               // already terminated this step
            if (ship.Dead) continue;                          // already killed this step
            if (ReferenceEquals(proj.Launcher, ship)) continue; // never hit your own launcher

            // Friendly-fire gate (shipIGC.cpp:1222-1228). Friendly fire is
            // always off — a projectile never damages another ship on the same
            // side. Mirrors the launcher self-exclusion directly above (a
            // friendly round passes through without landing and without
            // terminating), and engages only when BOTH ships carry a side, so
            // a side-less ship (flight/bot slices, unit tests) fights everyone
            // exactly as before. Different sides / no side still deal damage.
            if (proj.Launcher is Ship shooter
                && shooter.SideId is { } shooterSide
                && ship.SideId == shooterSide)
                continue;

            // Impact positions at the collision time, exactly as the C++
            // HandleCollision computes them (projectileIGC.cpp:174-175):
            // position1 = projectile pos + vel·tCollision, position2 = target pos + vel·tCollision.
            var position1 = proj.Position + proj.Velocity * ev.TCollision;
            var position2 = ship.Position + ship.CollisionVelocity * ev.TCollision;

            // Damage = decoded projectile Power (× c_gaDamageGuns = 1, no side
            // attribute), of the decoded DamageType; C++ projectileIGC.cpp:177-190.
            ship.ReceiveDamage(
                proj.ProjectileType.DamageType,
                proj.ProjectileType.Power,
                now,
                position1,
                position2,
                proj.Launcher);

            spent.Add(proj);
            _models.Remove(proj);                  // HandleCollision → Terminate (projectileIGC.cpp:230)
            if (ship.Dead) _models.Remove(ship);   // killed: leaves the sector (README death contract)
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
                    Console.WriteLine($"[mine] ship {ship.Id} began mining asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' (ore {ast.OreRemaining:0.##}/{ast.OreMax:0.##}) at {apos}");
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
