using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

/// <summary>
/// A minimal <see cref="IStationIgc"/> carrier for the docking seam — wraps a
/// <see cref="DataStationIGC"/> so the empty marker can carry a station's
/// identity into <see cref="IIgcSite.DockWithStationEvent"/>. This mirrors
/// exactly how <see cref="ResolvedWarp"/> implements the empty
/// <see cref="IWarpIgc"/> marker for the warp transit seam (and
/// <see cref="Sector"/> : <c>IClusterIgc</c>, <see cref="Ship"/> :
/// <c>IShipIgc</c>): the marker is deliberately empty, and this class is the
/// concrete data carrier the site (see
/// <see cref="Allegiance.Server.ServerHost.DockWithStationEvent"/>) casts back
/// out of the seam parameter to reach the concrete station.
/// </summary>
/// <remarks>
/// <para><c>DataStationIGC</c> is a readonly <c>record struct</c> (a wire-data
/// record, <c>DataStationIGC.cs</c>) and is not itself an <c>IStationIgc</c>;
/// a struct cannot be the object-pointer the seam passes without an implementor,
/// so this class provides it. It adds no members — only carries the underlying
/// <see cref="DataStationIGC"/> — keeping the marker seam's "empty placeholder"
/// contract intact. Detection happens in <see cref="Sector.ResolveDockCollisions"/>
/// (the proximity + friendly gates); the host's override sets the actual
/// <see cref="Ship.StationId"/>.</para>
/// </remarks>
public sealed class DockStation : IStationIgc
{
    /// <summary>The concrete station record the carrier wraps.</summary>
    public DataStationIGC Station { get; }

    /// <summary>Wrap a station record so it can travel through the seam.</summary>
    public DockStation(DataStationIGC station) => Station = station;
}
