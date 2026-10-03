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
    /// P433 — resolve a real launcher <c>PartId</c> into a mountable missile-launcher
    /// <see cref="DataPartTypeIGC"/> (via <see cref="DataPartTypeIGC.FromMissileLauncher"/>),
    /// the shared primitive the server's <c>ApplyLoadout</c> and both tools
    /// (allegiance-mcp + vizfight) use to grant / pre-validate a real missile
    /// launcher in a loadout. Requires BOTH a real <see cref="Launchers"/> record
    /// for <paramref name="partId"/> AND a real missile type for that launcher's
    /// <c>ExpendabletypeId</c> in <see cref="MissileTypes"/> — chaff / mine / probe
    /// dispensers share the <see cref="Launchers"/> table but their expendable id is
    /// not a missile, so they correctly fail here (they are not missile launchers).
    /// Delegates to the P435-generalized <see cref="TryBuildExpendableLauncher"/>,
    /// restricting the result to the missile kind.
    /// </summary>
    public bool TryBuildMissileLauncher(short partId, out DataPartTypeIGC part)
    {
        if (TryBuildExpendableLauncher(partId, out part) && part.Type is DataPartTypeIGC.MissileLauncher)
            return true;
        part = default;
        return false;
    }

    /// <summary>
    /// P435 — resolve a real launcher <c>PartId</c> into a mountable
    /// <see cref="DataPartTypeIGC"/> of ANY of the four real expendable launcher
    /// kinds, generalizing P433's <see cref="TryBuildMissileLauncher"/> to the other
    /// 67 real chaff/mine/probe dispensers that share the <see cref="Launchers"/>
    /// table. The mounted subtype (and its <see cref="EquipmentType"/>) is chosen by
    /// WHICH expendable table the launcher's <c>ExpendabletypeId</c> resolves in:
    /// missile → <see cref="DataPartTypeIGC.MissileLauncher"/> (<c>ET_Magazine</c>),
    /// mine → <see cref="DataPartTypeIGC.MineLauncher"/> (<c>ET_Dispenser</c>),
    /// chaff → <see cref="DataPartTypeIGC.ChaffLauncher"/> (<c>ET_ChaffLauncher</c>),
    /// probe → <see cref="DataPartTypeIGC.ProbeLauncher"/> (<c>ET_Dispenser</c>) —
    /// each exactly the mount shape the server's <c>FIRE_EXPENDABLE(82)</c> /
    /// <c>FIRE_MISSILE(70)</c> handlers resolve. This is the shared primitive the
    /// server's <c>ApplyLoadout</c> and both tools use to grant / pre-validate a
    /// real launcher of any kind. Returns false (no fabricated part) when the
    /// <c>PartId</c> is not a real <see cref="Launchers"/> record or its expendable
    /// resolves in none of the four tables.
    /// </summary>
    public bool TryBuildExpendableLauncher(short partId, out DataPartTypeIGC part)
    {
        if (!Launchers.TryGetValue(partId, out var launcher))
        {
            part = default;
            return false;
        }
        if (MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var missile))
        {
            part = DataPartTypeIGC.FromMissileLauncher(partId, launcher, missile);
            return true;
        }
        if (MineTypes.TryGetValue(launcher.ExpendabletypeId, out var mine))
        {
            part = DataPartTypeIGC.FromMineLauncher(partId, launcher, mine);
            return true;
        }
        if (ChaffTypes.TryGetValue(launcher.ExpendabletypeId, out var chaff))
        {
            part = DataPartTypeIGC.FromChaffLauncher(partId, launcher, chaff);
            return true;
        }
        if (ProbeTypes.TryGetValue(launcher.ExpendabletypeId, out var probe))
        {
            part = DataPartTypeIGC.FromProbeLauncher(partId, launcher, probe);
            return true;
        }
        part = default;
        return false;
    }

    /// <summary>
    /// P509 — the <b>required-tech union</b> of every real core buyable: OR-ing
    /// every hull, part, launcher, station-type, drone and development
    /// <c>TtbmRequired</c>. This is the exact bit set the <c>testUnlock</c>
    /// option grants a side, and the same derivation the client-level shop
    /// test measures against. Derived from the core (not every bit blindly set)
    /// so a fresh side granted it can buy <em>every hull, part and launcher any
    /// real core buyable requires</em> — the union, not the full 400-bit space.
    /// <para>Launchers contribute the tech their <em>built</em> mountable part
    /// carries (from the expendable's <c>LauncherTtbmRequired</c>, via
    /// <see cref="TryBuildExpendableLauncher"/> — the exact requirement the
    /// shop's launcher offer and the server's BUY_LOADOUT gate check), so a
    /// granted side can actually buy every real launcher/dispenser. A
    /// development's <c>TtbmRequired</c> (its research prerequisite) is included
    /// so the investment screen agrees every development is researchable —
    /// buyability only; no development is <em>completed</em> here, so no
    /// global-attribute delta is applied (flight/combat stats stay unmodified).</summary>
    public TechTreeBitMask ComputeRequiredTechUnion()
    {
        var acc = TechTreeBitMask.Empty;
        void Or(byte[]? req)
        {
            if (req is { Length: TechTreeBitMask.ByteCount })
                acc = acc.Or(TechTreeBitMask.FromBytes(req));
        }

        foreach (var h in HullTypes.Values) Or(h.TtbmRequired);
        foreach (var p in PartTypes.Values) Or(p.TtbmRequired);
        foreach (var l in Launchers.Values)
            if (TryBuildExpendableLauncher(l.PartId, out var lp))
                Or(lp.TtbmRequired);
        foreach (var s in StationTypes.Values) Or(s.TtbmRequired);
        foreach (var d in DroneTypes.Values) Or(d.TtbmRequired);
        foreach (var dev in Developments.Values) Or(dev.TtbmRequired);
        return acc;
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
