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
/// does (<c>clusterIGC.cpp:357</c>). <see cref="Update"/> is a recording stub:
/// there are no ships/models yet, so it only counts the tick and remembers the
/// last tick time.
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

    /// <summary>Start a sector from its cluster record; contents are attached during assembly.</summary>
    public Sector(DataClusterIGC cluster) => Cluster = cluster;

    internal void AttachStations(IEnumerable<DataStationIGC> stations) => _stations.AddRange(stations);
    internal void AttachAsteroids(IEnumerable<DataAsteroidIGC> asteroids) => _asteroids.AddRange(asteroids);
    internal void AttachWarp(ResolvedWarp warp) => _warps.Add(warp);

    /// <summary>
    /// Recording stub for the per-chunk sector tick (<c>CclusterIGC::Update</c>,
    /// <c>clusterIGC.cpp:97</c>). With no ships or models yet it only records
    /// that it was ticked; once the object model exists the fleet/collision/
    /// combat passes land here.
    /// </summary>
    public void Update(double timeUpdate)
    {
        UpdateCount++;
        LastUpdate = timeUpdate;
    }
}
