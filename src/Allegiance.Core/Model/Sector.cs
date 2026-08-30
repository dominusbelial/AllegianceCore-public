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

    // Models living in this sector and ticking with it. Ships today; stations,
    // asteroids, treasures etc. join later. Mirrors CclusterIGC::m_models.
    private readonly List<Model> _models = [];

    /// <summary>Construct a sector from its cluster record; contents are attached during assembly.</summary>
    public Sector(DataClusterIGC cluster) => Cluster = cluster;

    internal void AttachStations(IEnumerable<DataStationIGC> stations) => _stations.AddRange(stations);
    internal void AttachAsteroids(IEnumerable<DataAsteroidIGC> asteroids) => _asteroids.AddRange(asteroids);
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
    }

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
}
