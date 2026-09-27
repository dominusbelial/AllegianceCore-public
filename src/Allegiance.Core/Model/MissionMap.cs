namespace Allegiance.Core.Model;

/// <summary>
/// The <b>map-file universe</b> for one <see cref="IgcWrapper.DataSize"/>
/// mission map: the four fixed-size map objects that occur in a shipped map
/// (cluster / warp / station / asteroid), exposed as order-preserving lists.
/// </summary>
/// <remarks>
/// Unlike <see cref="StaticCore"/> these are <b>lists, not ID-keyed
/// dictionaries</b> — the map objects here carry IDs, but the design keeps the
/// map as an ordered scene (a sector contains its warps/stations/asteroids; a
/// planet order matters) rather than a lookup table. Cross-reference integrity
/// with a static core is validated separately and non-throwingly (see
/// <c>Validate</c>). A duplicate <c>ClusterId</c> across sectors is legal and
/// frequent in real maps, so none is enforced here.
/// </remarks>
public sealed class MissionMap
{
    /// <summary>Sectors, in file order.</summary>
    public IReadOnlyList<DataClusterIGC> Clusters { get; }
    /// <summary>Warps, in file order.</summary>
    public IReadOnlyList<DataWarpIGC> Warps { get; }
    /// <summary>Stations, in file order.</summary>
    public IReadOnlyList<DataStationIGC> Stations { get; }
    /// <summary>Asteroids, in file order.</summary>
    public IReadOnlyList<DataAsteroidIGC> Asteroids { get; }

    private MissionMap(
        IReadOnlyList<DataClusterIGC> clusters,
        IReadOnlyList<DataWarpIGC> warps,
        IReadOnlyList<DataStationIGC> stations,
        IReadOnlyList<DataAsteroidIGC> asteroids)
    {
        Clusters = clusters;
        Warps = warps;
        Stations = stations;
        Asteroids = asteroids;
    }

    /// <summary>
    /// P397 — build a <see cref="MissionMap"/> from pre-materialized record
    /// lists, the synthetic in-memory counterpart of <see cref="Load"/>'s
    /// decode. The procedural map generator
    /// (<c>Allegiance.Core.MapGen.MissionMapGenerator</c>) produces exactly
    /// the four map object kinds and hands them in here, so a generated
    /// mission flows through the <b>unchanged</b>
    /// <c>Mission.Assemble(StaticCore, MissionMap, …)</c> path — identical to
    /// a file-decoded map, which is the whole point of the P397 design
    /// decision (one assembly path, no generator-specific code in it).
    /// </summary>
    public static MissionMap FromLists(
        IReadOnlyList<DataClusterIGC> clusters,
        IReadOnlyList<DataWarpIGC> warps,
        IReadOnlyList<DataStationIGC> stations,
        IReadOnlyList<DataAsteroidIGC> asteroids)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(warps);
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(asteroids);
        return new MissionMap(clusters, warps, stations, asteroids);
    }

    /// <summary>
    /// Build the map universe from a <see cref="IgcWrapper.DataSize"/> file.
    /// Throws on the wrong wrapper form or on a record type that does not
    /// belong in a map file (static-core or runtime/savegame object).
    /// </summary>
    public static MissionMap Load(IgcFile file)
    {
        if (file.Wrapper != IgcWrapper.DataSize)
            throw new InvalidDataException(
                $"MissionMap.Load expects a DataSize map, got {file.Wrapper}");

        var clusters = new List<DataClusterIGC>();
        var warps = new List<DataWarpIGC>();
        var stations = new List<DataStationIGC>();
        var asteroids = new List<DataAsteroidIGC>();

        foreach (var r in file.Records)
        {
            var b = file.Body.Slice(r.PayloadOffset, r.Size).Span;
            switch (r.Type)
            {
                case IgcObjectType.Cluster: clusters.Add(DataClusterIGC.Decode(b)); break;
                case IgcObjectType.Warp: warps.Add(DataWarpIGC.Decode(b)); break;
                case IgcObjectType.Station: stations.Add(DataStationIGC.Decode(b)); break;
                case IgcObjectType.Asteroid: asteroids.Add(DataAsteroidIGC.Decode(b)); break;
                default:
                    throw new InvalidDataException(
                        $"MissionMap: unexpected record type {(short)r.Type} ({r.Type}) in a map; " +
                        "static-core and runtime objects do not belong here.");
            }
        }

        return new MissionMap(clusters, warps, stations, asteroids);
    }
}
