namespace Allegiance.Core.MapGen;

/// <summary>
/// P397 — port of the native <c>CMapData</c> (<c>mapmakerigc.h:40-175</c>):
/// the per-generation-run state plumbing a map maker reads and writes. It is
/// deliberately <em>not</em> persisted mission state — it is constructed,
/// used to drive one <see cref="MissionMapGenerator"/> run, then discarded.
/// Nothing but trivial counters and per-slot arrays lives here; all real
/// algorithm lives in the generator (native's is likewise all in the map-maker
/// subclass methods, not here).
/// </summary>
internal sealed class MapGenContext
{
    /// <summary><c>c_MaxClustersPerMap = 50</c> (<c>mapmakerigc.h:25</c>).</summary>
    public const int MaxClustersPerMap = 50;
    /// <summary><c>c_NumClusterNames = 72</c> (<c>mapmakerigc.h:26</c>).</summary>
    public const int NumClusterNames = 72;

    /// <summary>The 72-name cluster-naming pool, transcribed verbatim from
    /// <c>CMapData::smszClusterName</c> (<c>mapmakerigc.cpp:21-41</c>).</summary>
    private static readonly string[] ClusterNames =
    [
        "Aegis","Aeroflex","Ahto","Aredne",
        "Blenhem","Borsonis","Bragi","Caladonia",
        "Cirt","Claytow","Denom","Dunatis",
        "Eleesh","Enlil","Fejfar","Fenris",
        "Fierro","Freya","Gala","Gery",
        "Gobo","Goron","Holokine","Jol",
        "Karlo","Kassano","Kassimier","Kik",
        "Kilvero","Kishoten","Kuat","L'Sau",
        "Larchis","Lemminakean","Leighton","Louhi",
        "Loviatar","Luerentia","Meesh","Mercor",
        "Miekko","Mielikki","Monmar","MrChaos",
        "Nadir","Night","Oblivion","Okanagon",
        "Oxyl","Pohjola","Raiko","Resnik",
        "Rib'zki","Rimin","Sleipener","Solitaire",
        "Soron","Sri Metsa","Surma","Tathlum",
        "Tierankeen","Tjeerd","Torc","Touni",
        "Tauceti","Tj Butler","Titty Baby","Turakeen",
        "Udar","Virkinow","Wasp","Xuping",
    ];

    /// <summary>The 25-poster planet-name pool
    /// (<c>CmapMakerIGC::smszPlanetName</c>, <c>mapmakerigc.cpp:43-70</c>).</summary>
    internal static readonly string[] PlanetNames =
    [
        "nebplnt36bmp","nebplnt35bmp","nebplnt02bmp","nebplnt23bmp",
        "nebplnt19bmp","nebplnt01bmp","nebplnt03bmp","nebplnt04bmp",
        "nebplnt05bmp","nebplnt06bmp","nebplnt07bmp","nebplnt08bmp",
        "nebplnt09bmp","nebplnt10bmp","nebplnt11bmp","nebplnt12bmp",
        "nebplnt16bmp","nebplnt19bmp","nebplnt24bmp","nebplnt25bmp",
        "nebplnt29bmp","nebplnt30bmp","nebplnt31bmp","nebplnt33bmp",
        "nebplnt37bmp",
    ];

    /// <summary><c>NA = -1</c>, the sentinel "no side" for a neutral cluster slot
    /// (<c>msideIDCluster[sID] = NA</c>, <c>mapmakerigc.cpp:667,675</c>).</summary>
    public const short NaSide = -1;

    public MapGenParams Params { get; }

    /// <summary>The generation time (native <c>CMapData::mNow</c>). Unused by the
    /// port's record-producing generator (records carry no timestamps) but kept
    /// for faithfulness and future use.</summary>
    public double Now { get; }

    private readonly string[] _clusterNamesLeft;
    private int _clusterNamesLeftCount;

    private readonly DataClusterIGC?[] _clusters = new DataClusterIGC?[MaxClustersPerMap];
    private readonly short[] _clusterSides = new short[MaxClustersPerMap];

    private int _nextWarpId;

    public MapGenContext(MapGenParams pmp, double now)
    {
        Params = pmp;
        Now = now;
        _clusterNamesLeft = (string[])ClusterNames.Clone();
        _clusterNamesLeftCount = NumClusterNames;
        Array.Fill(_clusterSides, NaSide);
    }

    // ── reader/writer surface (port of CMapData's accessors) ──────────────

    public int Teams => Params.Teams;
    public short TeamClustersPerTeam { get; private set; }
    public short NeutralClustersPerTeam { get; private set; }
    public short OtherClusters { get; private set; }

    public void SetTeamClustersPerTeam(short v) => TeamClustersPerTeam = v;
    public void SetNeutralClustersPerTeam(short v) => NeutralClustersPerTeam = v;
    public void SetOtherClusters(short v) => OtherClusters = v;

    public short ClustersPerTeam => (short)(TeamClustersPerTeam + NeutralClustersPerTeam);
    public short TeamClusters => (short)(TeamClustersPerTeam * Teams);
    public short NeutralClusters => (short)(NeutralClustersPerTeam * Teams);
    public short TotalClusters => (short)(ClustersPerTeam * Teams + OtherClusters);

    public DataClusterIGC GetCluster(short id) =>
        _clusters[id] ?? throw new InvalidOperationException($"no cluster generated at slot {id}");
    public short GetClusterSide(short id) => _clusterSides[id];
    public void SetCluster(short id, DataClusterIGC cluster, short sideId)
    {
        _clusters[id] = cluster;
        _clusterSides[id] = sideId;
    }

    /// <summary>Pull a fresh, unused cluster name from the pool
    /// (<c>CMapData::GetNewClusterName</c>, <c>mapmakerigc.cpp:119-128</c>).</summary>
    public string GetNewClusterName()
    {
        int n = Random.Shared.Next(0, _clusterNamesLeftCount);
        string name = _clusterNamesLeft[n];
        _clusterNamesLeftCount--;
        _clusterNamesLeft[n] = _clusterNamesLeft[_clusterNamesLeftCount];
        return name;
    }

    /// <summary>Monotonic warp-id counter (<c>CMapData::GetNextWarpID</c>,
    /// <c>mapmakerigc.h:148-149</c>), giving every warp in the map a globally
    /// unique id — the same property native relies on for each ring link to
    /// point its two warps at each other (consecutive ids).</summary>
    public int GetNextWarpId() => _nextWarpId++;
}
