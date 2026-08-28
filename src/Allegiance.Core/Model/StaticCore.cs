namespace Allegiance.Core.Model;

/// <summary>
/// Builds the static-core <b>type universe</b>: every static-core object type
/// carried by a <see cref="IgcWrapper.VersionAndDataSize"/> file, memoized by
/// its raw ID field into ID-keyed dictionaries. This is the connection between
/// <see cref="IgcReader"/> (locates records) and <c>Data/*.cs</c> (decodes
/// payloads) — the answer to "give me hull type 42".
/// </summary>
/// <remarks>
/// <para>
/// ID-keyed dictionaries only. <b>No object graph with resolved references is
/// built.</b> Records keep their raw ID fields; lookup happens through the
/// dictionary. Resolving pointers here buys nothing and makes cycles a
/// problem. Cross-reference integrity is a separate, non-throwing concern —
/// see <see cref="CrossRefValidator"/> / <c>Validate</c>.
/// </para>
/// <para>
/// A duplicate ID within one file is a structural error and <b>throws</b>,
/// with the type and ID named. An object type that has no ID field (only
/// <c>OT_constants</c> in a core) is exposed as a single <see cref="Constants"/>.
/// </para>
/// </remarks>
public sealed class StaticCore
{
    /// <summary>The header word — a Unix timestamp (the moment the core was written).</summary>
    public int Version { get; }

    public IReadOnlyDictionary<short, DataProjectileTypeIGC> ProjectileTypes { get; }
    public IReadOnlyDictionary<short, DataMissileTypeIGC>    MissileTypes    { get; }
    public IReadOnlyDictionary<short, DataMineTypeIGC>       MineTypes       { get; }
    public IReadOnlyDictionary<short, DataProbeTypeIGC>      ProbeTypes      { get; }
    public IReadOnlyDictionary<short, DataChaffTypeIGC>      ChaffTypes      { get; }
    public IReadOnlyDictionary<short, DataCivilizationIGC>   Civilizations   { get; }
    public IReadOnlyDictionary<short, DataHullTypeIGC>       HullTypes       { get; }
    /// <summary>
    /// Non-launcher part types (<see cref="DataPartTypeIGC"/>: weapon / shield /
    /// cloak / afterburner / pack), keyed by <c>PartId</c>. The compact 24-byte
    /// launcher form of <c>OT_partType</c> lands in <see cref="Launchers"/>,
    /// not here — the engine distinguishes the two purely by record size
    /// (<c>missionigc.cpp:3344</c>), so this loader does too.
    /// </summary>
    public IReadOnlyDictionary<short, DataPartTypeIGC>       PartTypes       { get; }
    /// <summary>The compact <see cref="DataLauncherTypeIGC"/> form of <c>OT_partType</c>, keyed by <c>PartId</c>.</summary>
    public IReadOnlyDictionary<short, DataLauncherTypeIGC>   Launchers       { get; }
    public IReadOnlyDictionary<short, DataStationTypeIGC>    StationTypes    { get; }
    public IReadOnlyDictionary<short, DataDevelopmentIGC>    Developments    { get; }
    public IReadOnlyDictionary<short, DataDroneTypeIGC>      DroneTypes      { get; }
    public IReadOnlyDictionary<short, DataTreasureSetIGC>    TreasureSets    { get; }

    /// <summary>The single <c>OT_constants</c> record (exactly one per core).</summary>
    public DataConstantsIGC Constants { get; }

    private StaticCore(
        int version,
        Dictionary<short, DataProjectileTypeIGC> projectileTypes,
        Dictionary<short, DataMissileTypeIGC> missileTypes,
        Dictionary<short, DataMineTypeIGC> mineTypes,
        Dictionary<short, DataProbeTypeIGC> probeTypes,
        Dictionary<short, DataChaffTypeIGC> chaffTypes,
        Dictionary<short, DataCivilizationIGC> civilizations,
        Dictionary<short, DataHullTypeIGC> hullTypes,
        Dictionary<short, DataPartTypeIGC> partTypes,
        Dictionary<short, DataLauncherTypeIGC> launchers,
        Dictionary<short, DataStationTypeIGC> stationTypes,
        Dictionary<short, DataDevelopmentIGC> developments,
        Dictionary<short, DataDroneTypeIGC> droneTypes,
        Dictionary<short, DataTreasureSetIGC> treasureSets,
        DataConstantsIGC constants)
    {
        Version = version;
        ProjectileTypes = projectileTypes;
        MissileTypes = missileTypes;
        MineTypes = mineTypes;
        ProbeTypes = probeTypes;
        ChaffTypes = chaffTypes;
        Civilizations = civilizations;
        HullTypes = hullTypes;
        PartTypes = partTypes;
        Launchers = launchers;
        StationTypes = stationTypes;
        Developments = developments;
        DroneTypes = droneTypes;
        TreasureSets = treasureSets;
        Constants = constants;
    }

    /// <summary>
    /// Build the type universe from a <see cref="IgcWrapper.VersionAndDataSize"/>
    /// file. Throws on: the wrong wrapper form, a record type that does not
    /// belong in a static core, or a duplicate ID within one file (the type and
    /// offending ID are named).
    /// </summary>
    public static StaticCore Load(IgcFile file)
    {
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException(
                $"StaticCore.Load expects a VersionAndDataSize core, got {file.Wrapper}");
        if (file.Version is not { } version)
            throw new InvalidDataException("StaticCore: core has no version word");

        var projectiles = new Dictionary<short, DataProjectileTypeIGC>();
        var missiles = new Dictionary<short, DataMissileTypeIGC>();
        var mines = new Dictionary<short, DataMineTypeIGC>();
        var probes = new Dictionary<short, DataProbeTypeIGC>();
        var chaffs = new Dictionary<short, DataChaffTypeIGC>();
        var civs = new Dictionary<short, DataCivilizationIGC>();
        var hulls = new Dictionary<short, DataHullTypeIGC>();
        var parts = new Dictionary<short, DataPartTypeIGC>();
        var launchers = new Dictionary<short, DataLauncherTypeIGC>();
        var stations = new Dictionary<short, DataStationTypeIGC>();
        var devs = new Dictionary<short, DataDevelopmentIGC>();
        var drones = new Dictionary<short, DataDroneTypeIGC>();
        var treasure = new Dictionary<short, DataTreasureSetIGC>();
        DataConstantsIGC? constants = null;

        foreach (var r in file.Records)
        {
            var b = file.Body.Slice(r.PayloadOffset, r.Size);
            switch (r.Type)
            {
                case IgcObjectType.ProjectileType:
                    var p = DataProjectileTypeIGC.Decode(b.Span);
                    Put(ref projectiles, p.ProjectileTypeId, p, "projectile");
                    break;
                case IgcObjectType.MissileType:
                    var mi = DataMissileTypeIGC.Decode(b.Span);
                    Put(ref missiles, mi.ExpendableTypeId, mi, "missile");
                    break;
                case IgcObjectType.MineType:
                    var mn = DataMineTypeIGC.Decode(b.Span);
                    Put(ref mines, mn.ExpendableTypeId, mn, "mine");
                    break;
                case IgcObjectType.ProbeType:
                    var pb = DataProbeTypeIGC.Decode(b.Span);
                    Put(ref probes, pb.ExpendableTypeId, pb, "probe");
                    break;
                case IgcObjectType.ChaffType:
                    var cf = DataChaffTypeIGC.Decode(b.Span);
                    Put(ref chaffs, cf.ExpendableTypeId, cf, "chaff");
                    break;
                case IgcObjectType.Civilization:
                    var cv = DataCivilizationIGC.Decode(b.Span);
                    Put(ref civs, cv.CivilizationId, cv, "civilization");
                    break;
                case IgcObjectType.HullType:
                    var h = DataHullTypeIGC.Decode(b.Span);
                    Put(ref hulls, h.HullId, h, "hull");
                    break;
                case IgcObjectType.PartType:
                    if (r.Size == DataLauncherTypeIGC.Size)
                    {
                        var l = DataLauncherTypeIGC.Decode(b.Span);
                        Put(ref launchers, l.PartId, l, "launcher");
                    }
                    else
                    {
                        var pa = DataPartTypeIGC.Decode(b.Span);
                        Put(ref parts, pa.PartId, pa, "part");
                    }
                    break;
                case IgcObjectType.StationType:
                    var st = DataStationTypeIGC.Decode(b.Span);
                    Put(ref stations, st.StationTypeId, st, "station-type");
                    break;
                case IgcObjectType.Development:
                    var d = DataDevelopmentIGC.Decode(b.Span);
                    Put(ref devs, d.DevelopmentId, d, "development");
                    break;
                case IgcObjectType.DroneType:
                    var dr = DataDroneTypeIGC.Decode(b.Span);
                    Put(ref drones, dr.DroneTypeId, dr, "drone");
                    break;
                case IgcObjectType.TreasureSet:
                    var ts = DataTreasureSetIGC.Decode(b.Span);
                    Put(ref treasure, ts.TreasureSetId, ts, "treasure-set");
                    break;
                case IgcObjectType.Constants:
                    var c = DataConstantsIGC.Decode(b.Span);
                    if (constants is not null)
                        throw new InvalidDataException(
                            $"duplicate Constants record in core (version {version})");
                    constants = c;
                    break;
                default:
                    throw new InvalidDataException(
                        $"StaticCore: unexpected record type {(short)r.Type} ({r.Type}) in a static core " +
                        $"(version {version}); map/runtime objects do not belong here.");
            }
        }

        if (constants is null)
            throw new InvalidDataException($"StaticCore: no Constants record in core (version {version})");

        return new StaticCore(
            version, projectiles, missiles, mines, probes, chaffs, civs, hulls,
            parts, launchers, stations, devs, drones, treasure, constants.Value);
    }

    /// <summary>Add a decoded record to its ID-keyed bucket, rejecting a duplicate ID within the file.</summary>
    private static void Put<T>(ref Dictionary<short, T> bucket, short id, T value, string kind)
    {
        if (!bucket.TryAdd(id, value))
            throw new InvalidDataException($"duplicate {kind} ID {id} in one core");
    }
}
