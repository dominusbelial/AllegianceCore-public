namespace Allegiance.Core.Model;

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
    /// unconditionally, with no stage check. The C++ captures the successor link
    /// before calling <c>Update</c> because models may self-terminate; no model
    /// self-terminates in this slice, so the loop is a plain forward walk (the
    /// list is not mutated during it).
    /// </remarks>
    public void Update(double timeUpdate)
    {
        if (timeUpdate <= LastUpdate) return;   // now > m_lastUpdate guard (clusterIGC.cpp:99)
        UpdateCount++;
        LastUpdate = timeUpdate;

        foreach (var model in _models)
            model.Update(timeUpdate);            // l->data()->Update(now) (clusterIGC.cpp:251)
    }
}
