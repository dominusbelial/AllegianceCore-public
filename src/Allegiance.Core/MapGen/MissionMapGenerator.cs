namespace Allegiance.Core.MapGen;

using Allegiance.Core.Model;

/// <summary>
/// P397 — the procedural mission-map generator: the C# port of native
/// <c>ImapMakerIGC::Create</c> + the shared <c>CmapMakerIGC::GenerateMission</c>
/// algorithm (<c>mapmakerigc.cpp:168-203, 340-483</c>) and its pieces
/// (<c>GenerateLayout</c>/<c>GenerateRingClusters</c>/<c>GenerateTeamCluster</c>/
/// <c>GenerateNeutralCluster</c>/<c>LinkClusters</c>/<c>CrossLinkClusters</c>/
/// <c>GenerateStarbase</c>/<c>PopulateClusters</c>), reduced to <b>data
/// records</b> because the port assembles a <see cref="Mission"/> from a
/// <see cref="MissionMap"/> rather than native's live
/// <c>CreateObject(OT_*)</c> side effects.
///
/// <para>The single architecture decision this task makes: the generator
/// produces a synthetic <see cref="MissionMap"/> (via
/// <see cref="MissionMap.FromLists"/>) and that flows through the
/// <b>unchanged</b> <see cref="Mission.Assemble"/> path — exactly like a
/// file-decoded map. So a generated map reuses every assembly concern
/// (civilization→side resolution, <c>DestinationId</c>→destination-sector warp
/// resolution, station/asteroid runtime wrapping, sector/side back-links) with
/// zero generator-specific code in the assembly path. See the P397 report for
/// the reasoning.</para>
///
/// <para><b>Scope simplifications, stated plainly:</b> (1) the cosmetic
/// 2D screen-position pass and the "random encounters" warp-pruning
/// (<c>mapmakerigc.cpp:219-337, 362-424</c>) are deliberately skipped — no
/// map-editor screen exists and <c>iRandomEncounters</c> is not modelled;
/// only a minimal <em>functional</em> 3D warp placement runs so the transit
/// sim has real positions. (2) The reveal/activate side steps
/// (<c>mapmakerigc.cpp:969-1070</c>) are N/A — the port's visibility is
/// dynamic per-tick (P132) and sides are already active by default (P202).</para>
/// </summary>
public sealed class MissionMapGenerator
{
    // Aleph-kind ids (mapmakerigc.h:29-34).
    private const int FriendlyAleph = 0;
    private const int EnemyAleph = 1;
    private const int PlayerToNeutralAleph = 2;
    private const int NeutralToPlayerAleph = 3;
    private const int NeutralAleph = 4;
    private const int HiddenToPlayerAleph = 5;

    private const string NeutralIcon = "neutralaleph";
    private const string PlayerIcon = "homealeph";
    private const string NeutralTexture = "plnt19";
    private const string PlayerTexture = "plnt42";

    /// <summary>Native mission default <c>c_radiusUniverse = 5000.0f</c>
    /// (<c>missionigc.cpp:2686</c>) — the warp-ring radius basis. Native places
    /// warps at ~<c>position.z (0.6..0.85) × majorRadius</c> from a sector's
    /// origin (<c>mapmakerigc.cpp:404-413</c>); this is the functional stand-in
    /// for that (the cosmetic randomization itself is out of scope).</summary>
    internal const float MajorRadius = 5000.0f;

    // EastWest's per-sector asteroid-count tables (mapmakerigc.cpp:2547-2548),
    // indexed by the sector's position within its 13-sector team block (local
    // 0/1 = the team's two home sectors, 2..12 = its 11 neutrals). EastWest's
    // PopulateCluster override passes these to the free ::PopulateCluster as
    // the mineable/special multipliers (0 = none, 1 = base count, 2 = double);
    // the generic/buildable multiplier stays 1 (the free function's default
    // nAsteroidsMultiplier, igc.h:4657).
    private static readonly short[] EastWestMineable = [2, 0, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1];
    private static readonly short[] EastWestSpecial  = [0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 0, 0, 0];

    private readonly MapGenContext _ctx;
    private readonly StaticCore _core;
    private readonly MapGenType _type;

    private readonly List<DataClusterIGC> _clusters = [];
    private readonly List<DataWarpIGC> _warps = [];
    private readonly List<DataStationIGC> _stations = [];
    private readonly List<DataAsteroidIGC> _asteroids = [];

    private int _nextAsteroidId;
    private int _nextStationId;

    private MissionMapGenerator(MapGenContext ctx, StaticCore core)
    {
        _ctx = ctx;
        _core = core;
        // Native carries the selected map maker id as CmapMakerIGC::mMMID and the
        // shared HiHigher layout/link code branches on it (mapmakerigc.cpp:1382,
        // 1412, 1485, 1511). The port has no per-class state, so the topology is
        // held here and the shared methods branch on it identically.
        _type = ctx.Params.Type;
    }

    /// <summary>
    /// Generate a whole mission map for <paramref name="pmp"/>. Dispatches on
    /// <see cref="MapGenParams.Type"/> exactly like native
    /// <c>ImapMakerIGC::Create</c>'s switch (<c>mapmakerigc.cpp:182-201</c>):
    /// each topology runs its own <c>GenerateLayout</c> (cluster-count setup +
    /// ring-cluster generation) followed by its own <c>LinkClusters</c>, then the
    /// shared minimal <see cref="PlaceWarps"/> and <see cref="PopulateClusters"/>
    /// run for every topology (as native's shared
    /// <c>CmapMakerIGC::GenerateMission</c> does, <c>mapmakerigc.cpp:340-483</c>).
    /// <see cref="MapGenType.LargeSplit"/>/<see cref="MapGenType.InsideOut"/>
    /// are zero-override native subclasses of <c>CmapMakerHiHigherIGC</c>
    /// (<c>mapmakerigc.h:523-540</c>), so they share HiHigher's layout/link
    /// code path, differing only in their pinned cluster counts.
    /// </summary>
    /// <exception cref="InvalidDataException">On an invalid param set (fewer
    /// than two teams — native asserts <c>nTeams >= 2</c> at
    /// <c>mapmakerigc.cpp:172</c> — or too few civ ids) or an as-yet
    /// unimplemented topology.</exception>
    public static MissionMap Generate(MapGenParams pmp, StaticCore core, double now)
    {
        ArgumentNullException.ThrowIfNull(pmp);
        ArgumentNullException.ThrowIfNull(core);
        if (pmp.Teams < 2)
            throw new InvalidDataException($"map generation requires >= 2 teams, got {pmp.Teams}");
        if (pmp.Teams > 6)
            throw new InvalidDataException($"map generation supports at most 6 teams (c_cSidesMax), got {pmp.Teams}");
        if (pmp.CivIds.Length < pmp.Teams)
            throw new InvalidDataException(
                $"map generation needs {pmp.Teams} civ ids (rgCivID), got {pmp.CivIds.Length}");

        ValidateTopology(pmp);

        var gen = new MissionMapGenerator(new MapGenContext(pmp, now), core);
        switch (pmp.Type)
        {
            case MapGenType.SingleRing:
            case MapGenType.DoubleRing:
            case MapGenType.BigRing:
            case MapGenType.HiLo:
            case MapGenType.HiHigher:
            case MapGenType.LargeSplit:
            case MapGenType.InsideOut:
            case MapGenType.Star:
            case MapGenType.PinWheel:
            case MapGenType.DiamondRing:
            case MapGenType.SnowFlake:
            case MapGenType.Grid:
            case MapGenType.EastWest:
            case MapGenType.SplitBase:
            case MapGenType.Brawl:
                gen.GenerateLayout();    // per-topology cluster setup + link
                gen.PlaceWarps();        // minimal functional 3D placement
                gen.PopulateClusters();
                break;
            default:
                throw new InvalidDataException($"map type '{pmp.Type}' is not implemented yet");
        }

        return MissionMap.FromLists(gen._clusters, gen._warps, gen._stations, gen._asteroids);
    }

    /// <summary>Port of native <c>ImapMakerIGC::IsValid</c> — the per-topology
    /// extra validity gate asserted at the top of <c>GenerateMission</c>
    /// (<c>mapmakerigc.cpp:346</c>, dispatch at 129-166). Only
    /// <see cref="MapGenType.DiamondRing"/> (&gt;2 teams) and
    /// <see cref="MapGenType.EastWest"/> (exactly 2 teams) carry real
    /// constraints; the latter is <c>mapmakerigc.cpp:2416-2427</c>, which
    /// returns an error unless <c>nTeams == 2</c>.
    /// <see cref="MapGenType.PinWheel"/>'s and <see cref="MapGenType.SplitBase"/>'s
    /// own <c>IsValid</c> are vestigial NULL stubs — their only real checks
    /// were commented out in March 2006 (<c>mapmakerigc.cpp:1810-1818,
    /// 2555-2563</c>) — and <c>Star</c>/<c>SnowFlake</c>/<c>Grid</c> return
    /// NULL inline (<c>mapmakerigc.h:360, 496, 545-548</c>); none of those adds
    /// a constraint beyond the base. A config requesting DiamondRing with 2
    /// teams, or EastWest with anything but 2 teams, is rejected exactly as
    /// native would.</summary>
    private static void ValidateTopology(MapGenParams pmp)
    {
        if (pmp.Type == MapGenType.DiamondRing && pmp.Teams < 3)
            throw new InvalidDataException(
                $"map type '{pmp.Type}' requires more than 2 teams (native IsValid), got {pmp.Teams}");
        if (pmp.Type == MapGenType.EastWest && pmp.Teams != 2)
            throw new InvalidDataException(
                $"map type '{pmp.Type}' requires exactly 2 teams (native IsValid), got {pmp.Teams}");
    }

    // ── GenerateLayout: per-topology cluster counts + linking ───────────────
    // Port of each topology's GenerateLayout (which sets cluster counts and
    // calls GenerateRingClusters) + its LinkClusters override. SingleRing uses
    // the base GenerateLayout (mapmakerigc.cpp:879-893) + default ring link;
    // DoubleRing overrides LinkClusters only; the rest override both.
    private void GenerateLayout()
    {
        switch (_type)
        {
            case MapGenType.SingleRing:
            case MapGenType.DoubleRing:
                // Base layout (mapmakerigc.cpp:879-893): 1 neutral per team.
                SetTeamNeutral(neutralPerTeam: 1, otherClusters: 0);
                GenerateRingClusters();
                if (_type == MapGenType.DoubleRing) LinkClustersDoubleRing();
                else LinkClusters();                  // default plain ring
                break;

            case MapGenType.BigRing:
                // mapmakerigc.cpp:1166-1180: 2 neutral sectors per team.
                SetTeamNeutral(neutralPerTeam: 2, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersBigRing();
                break;

            case MapGenType.HiLo:
                // mapmakerigc.cpp:1252-1268: 3 neutral per team + 1 central.
                SetTeamNeutral(neutralPerTeam: 3, otherClusters: 1);
                GenerateRingClusters();
                LinkClustersHiLo();
                break;

            case MapGenType.HiHigher:
            case MapGenType.LargeSplit:
            case MapGenType.InsideOut:
                // mapmakerigc.cpp:1380-1401: 5 neutral per team + 1 central.
                // Team-clusters-per-team differs by id (HiHigher: MapSize; the
                // HiHigher subclasses pin it: LargeSplit=2, InsideOut=1).
                SetTeamNeutral(neutralPerTeam: 5, otherClusters: 1);
                GenerateRingClusters();
                LinkClustersHiHigher();
                break;

            case MapGenType.Star:
                // mapmakerigc.cpp:1555-1569: 5 neutral per team, no central.
                // Team-clusters-per-team from MapSize (base behavior).
                SetTeamNeutral(neutralPerTeam: 5, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersStar();
                break;

            case MapGenType.PinWheel:
                // mapmakerigc.cpp:1821-1838: 1 team cluster per team, then
                // either a single central (other) sector (MapSize 0) or one
                // neutral per team in the centre (MapSize != 0).
                _ctx.SetTeamClustersPerTeam(1);
                if (_ctx.Params.MapSize == 0) _ctx.SetOtherClusters(1);
                else _ctx.SetNeutralClustersPerTeam(1);
                GenerateRingClusters();
                LinkClustersPinWheel();
                break;

            case MapGenType.DiamondRing:
                // mapmakerigc.cpp:1940-1953: 2 neutral per team (one inner
                // ring, one outer), team-per-team from MapSize. Requires >2
                // teams (validated in ValidateTopology).
                SetTeamNeutral(neutralPerTeam: 2, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersDiamondRing();
                break;

            case MapGenType.SnowFlake:
                // mapmakerigc.cpp:2061-2075: 3 neutral per team in a triangle,
                // team-per-team from MapSize.
                SetTeamNeutral(neutralPerTeam: 3, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersSnowFlake();
                break;

            case MapGenType.Grid:
                // mapmakerigc.cpp:2272-2288: 4 neutral per team forming a
                // diamond, team-per-team from MapSize.
                SetTeamNeutral(neutralPerTeam: 4, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersGrid();
                break;

            case MapGenType.EastWest:
                // mapmakerigc.cpp:2436-2450: 2 team + 11 neutral per team
                // (13-sector blocks), exactly 2 teams.
                SetTeamNeutral(neutralPerTeam: 11, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersEastWest();
                break;

            case MapGenType.SplitBase:
                // mapmakerigc.cpp:2569-2580: 2 team + 2 neutral per team.
                SetTeamNeutral(neutralPerTeam: 2, otherClusters: 0);
                GenerateRingClusters();
                LinkClustersSplitBase();
                break;

            case MapGenType.Brawl:
                // mapmakerigc.cpp:1707-1719 — the structural odd one out: ZERO
                // team clusters and exactly one "other" (neutral) cluster, so
                // the whole map is a single shared sector. With TeamClusters 0
                // and OtherClusters 1, TotalClusters = 0·Teams + 0 + 1 = 1 and
                // GenerateRingClusters produces exactly one neutral cluster
                // (id 0) — every side co-located, nothing to link.
                _ctx.SetTeamClustersPerTeam(0);
                _ctx.SetOtherClusters(1);
                GenerateRingClusters();
                LinkClustersBrawl();   // confirmed empty no-op — see method
                break;

            default:
                throw new InvalidDataException($"map type '{_type}' is not implemented yet");
        }
    }

    /// <summary>Port of the base <c>GenerateLayout</c>'s count setup
    /// (<c>mapmakerigc.cpp:883-885</c>): team-per-team from <c>iMapSize</c>
    /// (0 → 1, else 2), the given neutral-per-team and other-cluster counts.
    /// The shared count math (<c>CMapData</c>, <c>mapmakerigc.h:95-99</c>)
    /// derives ClustersPerTeam / TotalClusters from these; the caller then runs
    /// <see cref="GenerateRingClusters"/>.</summary>
    private void SetTeamNeutral(short neutralPerTeam, short otherClusters)
    {
        short teamPerTeam = (short)(_ctx.Params.MapSize != 0 ? 2 : 1);
        // HiHigher-family pins: LargeSplit forces 2 team clusters per team;
        // InsideOut forces 1 (the innermost is the team home, mapmakerigc.cpp:1382-1388).
        // EastWest/SplitBase pin a split base of 2 team sectors per team
        // (mapmakerigc.cpp:2438, 2571).
        if (_type == MapGenType.LargeSplit) teamPerTeam = 2;
        else if (_type == MapGenType.InsideOut) teamPerTeam = 1;
        else if (_type is MapGenType.EastWest or MapGenType.SplitBase) teamPerTeam = 2;

        _ctx.SetTeamClustersPerTeam(teamPerTeam);
        _ctx.SetNeutralClustersPerTeam(neutralPerTeam);
        _ctx.SetOtherClusters(otherClusters);
    }

    // ── GenerateRingClusters (shared by every variant, never overridden) ────
    // mapmakerigc.cpp:647-678
    private void GenerateRingClusters()
    {
        int cTotalRing = _ctx.TotalClusters - _ctx.OtherClusters;
        int id = 0;
        for (; id < cTotalRing; id++)
        {
            if (id % _ctx.ClustersPerTeam < _ctx.TeamClustersPerTeam)
            {
                var cluster = GenerateTeamCluster((short)id);
                _ctx.SetCluster((short)id, cluster, (short)(id / _ctx.ClustersPerTeam));
                _clusters.Add(cluster);
            }
            else
            {
                var cluster = GenerateNeutralCluster((short)id);
                _ctx.SetCluster((short)id, cluster, MapGenContext.NaSide);
                _clusters.Add(cluster);
            }
        }
        // Any "other" clusters (none for SingleRing) are neutral.
        while (id < _ctx.TotalClusters)
        {
            var cluster = GenerateNeutralCluster((short)id);
            _ctx.SetCluster((short)id, cluster, MapGenContext.NaSide);
            _clusters.Add(cluster);
            id++;
        }
    }

    // ── GenerateTeamCluster ────────────────────────────────────────────────
    // mapmakerigc.cpp:550-592
    private DataClusterIGC GenerateTeamCluster(short id)
    {
        var rng = Random.Shared;
        float theta = (float)(2.0 * Math.PI * id / _ctx.TotalClusters);
        return new DataClusterIGC(
            StarSeed: (uint)rng.Next(),
            LightDirection: RandomDirection(),
            LightColor: 0xffffffff,
            ScreenX: MathF.Cos(theta) * 0.8f,
            ScreenY: MathF.Sin(theta) * 0.8f,
            ClusterId: id,
            Debris: (short)rng.Next(250, 751),
            Stars: (short)rng.Next(250, 751),
            Name: _ctx.GetNewClusterName(),
            PosterName: "globe" + rng.Next(1, 9),
            PlanetName: MapGenContext.PlanetNames[rng.Next(MapGenContext.PlanetNames.Length)],
            PlanetSinLatitude: 0,
            PlanetLongitude: 0,
            PlanetRadius: (byte)rng.Next(50, 101),
            Active: true,
            HomeSector: true);
    }

    // ── GenerateNeutralCluster ─────────────────────────────────────────────
    // mapmakerigc.cpp:607-644 (planet name is empty for a neutral cluster)
    private DataClusterIGC GenerateNeutralCluster(short id)
    {
        var rng = Random.Shared;
        float theta = (float)(2.0 * Math.PI * id / _ctx.TotalClusters);
        return new DataClusterIGC(
            StarSeed: (uint)rng.Next(),
            LightDirection: RandomDirection(),
            LightColor: 0xffffffff,
            ScreenX: MathF.Cos(theta) * 0.4f,
            ScreenY: MathF.Sin(theta) * 0.4f,
            ClusterId: id,
            Debris: (short)rng.Next(250, 751),
            Stars: (short)rng.Next(250, 751),
            Name: _ctx.GetNewClusterName(),
            PosterName: "globe" + rng.Next(1, 9),
            PlanetName: "",
            PlanetSinLatitude: 0,
            PlanetLongitude: 0,
            PlanetRadius: (byte)rng.Next(50, 101),
            Active: true,
            HomeSector: false);
    }

    // ── LinkClusters(): the no-arg default ring topology ───────────────────
    // mapmakerigc.cpp:858-876
    private void LinkClusters()
    {
        int cTotal = _ctx.TotalClusters;
        for (int id = 0; id < cTotal - 1; id++)
            LinkClusters((short)id, (short)(id + 1));
        LinkClusters((short)(cTotal - 1), 0);   // wrap last -> first

        // More than 3 teams: link the neutral sectors (mapmakerigc.cpp:873-875).
        CrossLinkClusters(_ctx.TeamClustersPerTeam);
    }

    // ── CrossLinkClusters ──────────────────────────────────────────────────
    // mapmakerigc.cpp:838-855 (no-op for < 4 teams; SingleRing default does not
    // override it)
    private void CrossLinkClusters(short sFirstId)
    {
        int sMax = _ctx.TotalClusters;
        if (_ctx.Teams < 4) sMax = 0;
        else if (_ctx.Teams == 4) sMax /= 2;

        int sFirst = sFirstId;
        while (sFirst < sMax)
        {
            int sWrap = (sFirst + 2 * _ctx.ClustersPerTeam) % _ctx.TotalClusters;
            LinkClusters((short)sFirst, (short)sWrap);
            sFirst += _ctx.ClustersPerTeam;
        }
    }

    // ── LinkClusters(sID1, sID2, alephType) — builds a bidirectional warp pair
    // mapmakerigc.cpp:705-835
    private void LinkClusters(short sId1, short sId2, int alephType = -1)
    {
        const int Na = -1;
        if (alephType == Na)
        {
            short side1 = _ctx.GetClusterSide(sId1);
            short side2 = _ctx.GetClusterSide(sId2);
            if (side1 == Na)
            {
                alephType = side2 == Na ? NeutralAleph : NeutralToPlayerAleph;
            }
            else if (side1 == side2)
            {
                alephType = FriendlyAleph;
            }
            else if (side2 != Na)
            {
                alephType = EnemyAleph;
            }
            else
            {
                alephType = PlayerToNeutralAleph;
            }
        }

        string icon1, icon2, tex1, tex2;
        float sig1 = 1.0f, sig2 = 1.0f, rad1 = 0.6f, rad2 = 0.6f;
        switch (alephType)
        {
            case FriendlyAleph:
                icon1 = icon2 = PlayerIcon; tex1 = tex2 = PlayerTexture;
                break;
            case EnemyAleph:
                icon1 = icon2 = PlayerIcon; tex1 = tex2 = PlayerTexture;
                sig1 = sig2 = 0.33f; rad1 = rad2 = 0.85f;
                break;
            case NeutralToPlayerAleph:
                icon1 = PlayerIcon; tex1 = PlayerTexture; sig1 = 0.67f;
                icon2 = NeutralIcon; tex2 = NeutralTexture;
                break;
            case PlayerToNeutralAleph:
                icon2 = PlayerIcon; tex2 = PlayerTexture; sig2 = 0.67f;
                icon1 = NeutralIcon; tex1 = NeutralTexture;
                break;
            case NeutralAleph:
                icon1 = icon2 = NeutralIcon; tex1 = tex2 = NeutralTexture;
                break;
            case HiddenToPlayerAleph:
                icon2 = PlayerIcon; tex2 = PlayerTexture; sig2 = 0.33f; rad2 = 0.85f;
                icon1 = NeutralIcon; tex1 = NeutralTexture;
                break;
            default:
                throw new InvalidOperationException($"unknown aleph type {alephType}");
        }

        // Warp N in sID1 -> destination warp N+1 (in sID2); warp N+1 -> N.
        int wid1 = _ctx.GetNextWarpId();
        _warps.Add(new DataWarpIGC(
            WarpId: (short)wid1,
            DestinationId: (short)(wid1 + 1),
            Radius: 100,                                   // GenerateWarp radius (mapmakerigc.cpp:694)
            TextureName: tex1,
            IconName: icon1,
            Name: _ctx.GetCluster(sId2).Name,
            Position: [0f, 0f, rad1],                      // placeholder; PlaceWarps rewrites
            Forward: [0f, 0f, 1f],
            Rotation: [0f, 0f, 1f, 0f],
            Signature: sig1,
            ClusterId: sId1));

        int wid2 = _ctx.GetNextWarpId();
        _warps.Add(new DataWarpIGC(
            WarpId: (short)wid2,
            DestinationId: (short)wid1,
            Radius: 100,
            TextureName: tex2,
            IconName: icon2,
            Name: _ctx.GetCluster(sId1).Name,
            Position: [0f, 0f, rad2],
            Forward: [0f, 0f, 1f],
            Rotation: [0f, 0f, 1f, 0f],
            Signature: sig2,
            ClusterId: sId2));
    }

    // ── LinkClustersDoubleRing ─────────────────────────────────────────────
    // mapmakerigc.cpp:1107-1155 — the only DoubleRing override (base layout
    // counts). Two concentric rings: each team's group links to the inner
    // neutral ring and to the wrapped neighbour; then the outer ring links the
    // team (owned) sectors and the inner ring links the neutral sectors.
    private void LinkClustersDoubleRing()
    {
        short cTotal = _ctx.TotalClusters;
        // Link each team's group of sectors.
        for (short sID = 0; sID < cTotal; sID += _ctx.ClustersPerTeam)
        {
            short sWrap = (short)((sID + cTotal - 1) % cTotal);
            for (short sIndex = 0; sIndex < _ctx.TeamClustersPerTeam; sIndex++)
            {
                LinkClusters((short)(sID + sIndex), (short)(sID + _ctx.TeamClustersPerTeam));
                LinkClusters((short)(sID + sIndex), sWrap);
            }
        }
        // Link the ring around the outside (the team sectors).
        short sWrapId = (short)(cTotal - 1 - _ctx.NeutralClustersPerTeam);
        for (short sID = 0; sID < cTotal; sID++)
        {
            if (_ctx.GetClusterSide(sID) != MapGenContext.NaSide)
            {
                LinkClusters(sID, sWrapId);
                sWrapId = sID;
            }
        }
        // Link the ring around the inside (the neutral sectors).
        sWrapId = (short)(cTotal - 1);
        for (short sID = 0; sID < cTotal; sID++)
        {
            if (_ctx.GetClusterSide(sID) == MapGenContext.NaSide)
            {
                LinkClusters(sID, sWrapId);
                sWrapId = sID;
            }
        }
    }

    // ── LinkClustersBigRing ────────────────────────────────────────────────
    // mapmakerigc.cpp:1215-1242 — a single ring (every sector links the
    // previous one), then for >2 teams extra cross links off each team's first
    // neutral sector.
    private void LinkClustersBigRing()
    {
        short cTotal = _ctx.TotalClusters;
        // Link the ring.
        short sWrapId = (short)(cTotal - 1);
        for (short sID = 0; sID < cTotal; sID++)
        {
            LinkClusters(sID, sWrapId);
            sWrapId = sID;
        }

        if (_ctx.Teams > 2)
        {
            short n = _ctx.TeamClustersPerTeam;
            for (int i = 0; i < _ctx.Teams; i++)
            {
                short sID = (short)(i * _ctx.ClustersPerTeam);
                LinkClusters((short)(sID + n), (short)((sID + 3 + 2 * n) % cTotal));
                if (_ctx.Teams > 3)
                    LinkClusters((short)(sID + n + 1), (short)((cTotal + (sID - 2)) % cTotal));
            }
        }
    }

    // ── LinkClustersHiLo ───────────────────────────────────────────────────
    // mapmakerigc.cpp:1328-1369 — one low ring of 3 neutral sectors per team
    // around a single central (other) sector. Each team's neutral block hangs
    // off its home and the centre; outer neutrals chain onward.
    private void LinkClustersHiLo()
    {
        if (_ctx.TeamClustersPerTeam != 1)
        {
            // Link the split base clusters for each team.
            for (int i = 0; i < _ctx.Teams; i++)
            {
                short sID = (short)(i * _ctx.ClustersPerTeam);
                LinkClusters(sID, (short)(sID + 1));
            }
        }

        short sidCenter = (short)(_ctx.TotalClusters - 1);
        for (int i = 0; i < _ctx.Teams; i++)
        {
            short sID = (short)(i * _ctx.ClustersPerTeam + _ctx.TeamClustersPerTeam);
            // Inner ring to center.
            LinkClusters(sID, sidCenter);
            // Inner ring to inner previous team cluster.
            short sidPrevious = (short)(i * _ctx.ClustersPerTeam);
            LinkClusters(sID, sidPrevious);
            // Inner ring to inner next team cluster.
            short sidNext = (short)(((i + 1) % _ctx.Teams) * _ctx.ClustersPerTeam);
            LinkClusters(sID, sidNext);
            // Outer ring 1 to outer ring 2.
            LinkClusters((short)(sID + 1), (short)(sID + 2));
            // Outer ring 1 to previous team.
            LinkClusters((short)(sID + 1), (short)(sidPrevious + _ctx.TeamClustersPerTeam - 1));
            // Outer ring 2 to next team.
            LinkClusters((short)(sID + 2), (short)(sidNext + _ctx.TeamClustersPerTeam - 1));
        }
    }

    // ── LinkClustersHiHigher ───────────────────────────────────────────────
    // mapmakerigc.cpp:1483-1544 — the shared layout/link for HiHigher and its
    // zero-override subclasses LargeSplit and InsideOut (the port's _type is
    // native's mMMID). Five neutral sectors per team around a central (other)
    // sector; inside-out additionally flags the home→centre warp with the
    // hidden-to-player aleph kind and LargeSplit skips the intra-team split
    // link.
    private void LinkClustersHiHigher()
    {
        bool isLargeSplit = _type == MapGenType.LargeSplit;
        bool isInsideOut = _type == MapGenType.InsideOut;

        if (_ctx.TeamClustersPerTeam != 1 && !isLargeSplit)
        {
            // Link the split base clusters for each team.
            for (int i = 0; i < _ctx.Teams; i++)
            {
                short sID = (short)(i * _ctx.ClustersPerTeam);
                LinkClusters(sID, (short)(sID + 1));
            }
        }

        short sidCenter = (short)(_ctx.TotalClusters - 1);
        for (int i = 0; i < _ctx.Teams; i++)
        {
            short sIDp = (short)(i * _ctx.ClustersPerTeam);
            short sID0 = (short)(sIDp + _ctx.TeamClustersPerTeam);
            short sID1 = (short)(sID0 + 1);
            short sID2 = (short)(sID0 + 2);
            short sID3 = (short)(sID0 + 3);
            short sID4 = (short)(sID0 + 4);
            short sIDn = (short)(((i + 1) % _ctx.Teams) * _ctx.ClustersPerTeam);

            if (isInsideOut)
            {
                // Native asserts teamClustersPerTeam == 1 here (mapmakerigc.cpp:1513);
                // SetTeamNeutral pins InsideOut to 1, so the invariant holds.
                sID1 = sIDp;
                sIDp = (short)(sID0 + 1);
                sIDn = (short)(((i + 1) % _ctx.Teams) * _ctx.ClustersPerTeam + 1 + 1);
            }

            // Inner ring to center (inside-out uses a hidden-to-player aleph).
            LinkClusters(sID1, sidCenter, isInsideOut ? HiddenToPlayerAleph : -1);

            LinkClusters(sID0, sID1);
            LinkClusters(sID1, sID2);
            LinkClusters(sID0, sID3);
            LinkClusters(sID2, sID4);
            LinkClusters(sID3, sID4);
            LinkClusters(sID0, sIDp);
            LinkClusters(sID2, sIDn);
            LinkClusters(sID3, (short)(sIDp + _ctx.TeamClustersPerTeam - 1));
            LinkClusters(sID4, (short)(sIDn + _ctx.TeamClustersPerTeam - 1));

            // The iRandomEncounters branch (mapmakerigc.cpp:1537-1541) is
            // skipped: iRandomEncounters is not modelled (documented in P397).
        }
    }

    // ── LinkClustersStar ───────────────────────────────────────────────────
    // mapmakerigc.cpp:1631-1683 — a spoked star: 5 neutral sectors radiate
    // from each team's home. Each team's inner neutrals link to its home and
    // chain outward; the far-spoke neutrals cross-link to the previous team's
    // spoke and (via a halfway-opposite team) to an opposite spoke. Faithfully
    // reproduces native's behaviour even for even team counts, where the
    // opposing-spoke cross link is issued twice (creating a duplicate warp
    // pair) — native does not dedupe.
    private void LinkClustersStar()
    {
        for (int i = 0; i < _ctx.Teams; i++)
        {
            short sID = (short)(i * _ctx.ClustersPerTeam + _ctx.TeamClustersPerTeam);

            // Inner starting sectors to neutral sectors.
            if (_ctx.TeamClustersPerTeam == 1)
            {
                LinkClusters((short)(sID), (short)(sID - 1));
                LinkClusters((short)(sID + 1), (short)(sID - 1));
            }
            else
            {
                LinkClusters((short)(sID - 1), (short)(sID - 2));
                LinkClusters((short)(sID), (short)(sID - 2));
                LinkClusters((short)(sID + 1), (short)(sID - 1));
            }

            LinkClusters((short)(sID), (short)(sID + 2));
            LinkClusters((short)(sID + 1), (short)(sID + 2));
            LinkClusters((short)(sID + 1), (short)(sID + 3));
            LinkClusters((short)(sID + 2), (short)(sID + 3));
            LinkClusters((short)(sID + 2), (short)(sID + 4));

            // Far-spoke link to the previous team's spoke.
            short s = (short)((sID + _ctx.TotalClusters - _ctx.ClustersPerTeam) % _ctx.TotalClusters);
            LinkClusters((short)(s + 3), (short)(sID));
            LinkClusters((short)(s + 3), (short)(sID + 2));

            // Opposite-team spoke link.
            int j = (i + _ctx.Teams / 2) % _ctx.Teams;
            short sj = (short)(j * _ctx.ClustersPerTeam + _ctx.TeamClustersPerTeam);
            if (_ctx.Teams % 2 == 0)
            {
                LinkClusters((short)(sID + 4), (short)(sj + 4));
            }
            else
            {
                LinkClusters((short)(sID + 4), (short)(sj + 4));
                LinkClusters((short)(sID + 4), (short)((sj + 4 + _ctx.ClustersPerTeam) % _ctx.TotalClusters));
            }
        }
    }

    // ── LinkClustersPinWheel ───────────────────────────────────────────────
    // mapmakerigc.cpp:1859-1899 — with a multi-sector centre (neutral per team
    // set) each team cluster links to its following neutral and the centre
    // neutrals chain into a ring (closed when >2 teams, then cross-linked);
    // with a single central sector every team cluster links straight to the
    // centre (the last cluster id).
    private void LinkClustersPinWheel()
    {
        short cTotal = _ctx.TotalClusters;
        if (_ctx.NeutralClustersPerTeam != 0)
        {
            // Multiple sectors in the centre.
            short sWrapId = MapGenContext.NaSide;
            short sFirstNa = 0;
            for (short sID = 0; sID < cTotal; sID++)
            {
                if (_ctx.GetClusterSide(sID) != MapGenContext.NaSide)
                {
                    LinkClusters(sID, (short)(sID + 1));
                }
                else
                {
                    if (sWrapId != MapGenContext.NaSide)
                        LinkClusters(sID, sWrapId);
                    else
                        sFirstNa = sID;
                    sWrapId = sID;
                }
            }

            if (_ctx.Teams > 2)
                LinkClusters(sWrapId, sFirstNa);
            CrossLinkClusters(_ctx.TeamClustersPerTeam);
        }
        else
        {
            // Single sector in the middle: every team cluster links to it.
            for (short sID = 0; sID < cTotal; sID++)
            {
                if (_ctx.GetClusterSide(sID) != MapGenContext.NaSide)
                    LinkClusters(sID, (short)(cTotal - 1));
            }
        }
    }

    // ── LinkClustersDiamondRing ────────────────────────────────────────────
    // mapmakerigc.cpp:2012-2051 — one inner ring of neutral sectors and one
    // outer; each team's sector(s) link to both of its two neutral sectors,
    // the neutral sectors chain into the two rings, and (for 4 teams) the
    // centre sectors cross-link. Requires >2 teams (native IsValid).
    private void LinkClustersDiamondRing()
    {
        short cTotal = _ctx.TotalClusters;

        // Link each team's group of sectors.
        for (short sID = 0; sID < cTotal; sID += _ctx.ClustersPerTeam)
        {
            short sNeutralId = (short)(sID + _ctx.TeamClustersPerTeam);
            for (short si = 0; si < _ctx.TeamClustersPerTeam; si++)
            {
                LinkClusters((short)(sID + si), sNeutralId);
                LinkClusters((short)(sID + si), (short)(sNeutralId + 1));
            }
        }

        // Link the rings (the neutral sectors).
        for (short sID = 0; sID < cTotal; sID++)
        {
            if (_ctx.GetClusterSide(sID) == MapGenContext.NaSide)
            {
                short sNeutralId = (short)((sID + _ctx.ClustersPerTeam) % cTotal);
                LinkClusters(sID, sNeutralId);
            }
        }

        // Link the centre sectors.
        CrossLinkClusters(_ctx.TeamClustersPerTeam);
    }

    // ── LinkClustersSnowFlake ──────────────────────────────────────────────
    // mapmakerigc.cpp:2138-2236 — 3 neutral sectors per team form a triangle;
    // each team's home sector(s) attach to its triangle's first two neutrals,
    // and the triangles chain into a (double) ring. For fewer than 4 teams the
    // neutral cross-link is a no-op so the third neutral of each team is
    // ring-connected explicitly (making it a full ring even at 2 teams).
    private void LinkClustersSnowFlake()
    {
        short cTotal = _ctx.TotalClusters;
        short cTeam = _ctx.TeamClustersPerTeam;
        short cPerTeam = _ctx.ClustersPerTeam;

        for (short sID = 0; sID < cTotal; sID += cPerTeam)
        {
            // Link friendly clusters.
            if (cTeam > 1)
            {
                short si = 0;
                for (; si < (short)(cTeam - 1); si++)
                    LinkClusters((short)(sID + si), (short)(sID + si + 1));
                LinkClusters((short)(sID + si), sID);
            }

            // Link neutral clusters (the triangle).
            short sIndex = (short)(sID + cTeam);
            LinkClusters(sIndex, (short)(sIndex + 2));
            LinkClusters((short)(sIndex + 1), (short)(sIndex + 2));

            // Link the friendly to the neutral.
            switch (cTeam)
            {
                case 3:
                    LinkClusters((short)(sID + 1), sIndex);
                    LinkClusters((short)(sID + 1), (short)(sIndex + 1));
                    LinkClusters((short)(sID + 2), sIndex);
                    LinkClusters((short)(sID + 2), (short)(sIndex + 1));
                    break;
                case 2:
                    LinkClusters(sID, sIndex);
                    LinkClusters(sID, (short)(sIndex + 1));
                    LinkClusters((short)(sID + 1), sIndex);
                    LinkClusters((short)(sID + 1), (short)(sIndex + 1));
                    break;
                default:
                    LinkClusters(sID, sIndex);
                    LinkClusters(sID, (short)(sIndex + 1));
                    break;
            }
        }

        // Link the second neutral ring.
        short sIndex2 = MapGenContext.NaSide;
        for (short sID = cTeam; sID < cTotal; sID += cPerTeam)
        {
            LinkClusters(sID, (short)(sID + 1));
            if (sIndex2 != MapGenContext.NaSide)
                LinkClusters(sIndex2, sID);
            sIndex2 = (short)(sID + 1);
        }
        LinkClusters(sIndex2, cTeam);

        // Cross link the centre sectors.
        CrossLinkClusters((short)(cTeam + 2));

        // If less than 4 teams, cross linking does nothing — make a ring.
        if (_ctx.Teams < 4)
        {
            short sIndex3 = MapGenContext.NaSide;
            for (short sID = (short)(cTeam + 2); sID < cTotal; sID += cPerTeam)
            {
                if (sIndex3 != MapGenContext.NaSide)
                    LinkClusters(sID, sIndex3);
                sIndex3 = sID;
            }
            if (_ctx.Teams > 2)
                LinkClusters(sIndex3, (short)(cTeam + 2));
        }
    }

    // ── LinkClustersGrid ──────────────────────────────────────────────────
    // mapmakerigc.cpp:2364-2414 — 4 neutral sectors per team form a diamond
    // hanging off the team's home(s), and each team's neutral block
    // cross-links to the next team's block, giving (at 2 teams / iMapSize 0) a
    // fully 4-regular grid. The split-base link runs only when a team has >1
    // sector. The iRandomEncounters tail link is skipped (not modelled).
    private void LinkClustersGrid()
    {
        if (_ctx.TeamClustersPerTeam != 1)
        {
            // Link the split base clusters for each team.
            for (int i = 0; i < _ctx.Teams; i++)
            {
                short sID = (short)(i * _ctx.ClustersPerTeam);
                LinkClusters(sID, (short)(sID + 1));
            }
        }

        for (int i = 0; i < _ctx.Teams; i++)
        {
            short sIDh = (short)(i * _ctx.ClustersPerTeam);
            short sID0 = (short)(sIDh + _ctx.TeamClustersPerTeam);
            short sID1 = (short)(sID0 + 1);
            short sID2 = (short)(sID0 + 2);
            short sID3 = (short)(sID0 + 3);
            short sIDn = (short)(((i + 1) % _ctx.Teams) * _ctx.ClustersPerTeam);
            short sIDn0 = (short)(sIDn + _ctx.TeamClustersPerTeam);

            LinkClusters(sID0, sID1);
            LinkClusters(sID1, sID2);
            LinkClusters(sID2, sID3);
            LinkClusters(sID3, sID0);

            LinkClusters(sID2, (short)(sIDn0 + 1));
            LinkClusters(sID3, sIDn0);

            LinkClusters(sID0, (short)(sIDh + _ctx.TeamClustersPerTeam - 1));
            LinkClusters(sID1, sIDh);

            LinkClusters(sID2, sIDn);
            LinkClusters(sID3, (short)(sIDn + _ctx.TeamClustersPerTeam - 1));

            // iRandomEncounters tail link (cpp 2407-2411) is skipped.
        }
    }

    // ── LinkClustersEastWest ──────────────────────────────────────────────
    // mapmakerigc.cpp:2489-2539 — a mirrored two-team layout (asserts teams==2
    // and 13 sectors per team, both guaranteed by the topology's layout +
    // IsValid). Each team's two home sectors (0,1) anchor its 11 neutral
    // sectors: home 0 links to neutrals 0..3, home 1 to neutrals 4..7, the
    // neutrals form two chains, and the far-end neutrals (8,9,10) cross the
    // midline to the other team's far end (10/11/12 reversed → 25/24/23).
    private void LinkClustersEastWest()
    {
        const int Teams = 2;
        const int SectorsPerTeam = 13;   // 2 team + 11 neutral
        for (int i = 0; i < Teams; i++)
        {
            short sIDf0 = (short)(i * SectorsPerTeam);
            short sIDf1 = (short)(sIDf0 + 1);
            short sID0 = (short)(sIDf1 + 1);

            LinkClusters(sIDf0, sID0);
            LinkClusters(sIDf0, (short)(sID0 + 1));
            LinkClusters(sIDf0, (short)(sID0 + 2));
            LinkClusters(sIDf0, (short)(sID0 + 3));

            LinkClusters(sIDf1, (short)(sID0 + 4));
            LinkClusters(sIDf1, (short)(sID0 + 5));
            LinkClusters(sIDf1, (short)(sID0 + 6));
            LinkClusters(sIDf1, (short)(sID0 + 7));

            LinkClusters(sID0, (short)(sID0 + 1));
            LinkClusters((short)(sID0 + 1), (short)(sID0 + 3));
            LinkClusters((short)(sID0 + 3), (short)(sID0 + 2));
            LinkClusters((short)(sID0 + 2), sID0);

            LinkClusters((short)(sID0 + 4), (short)(sID0 + 5));
            LinkClusters((short)(sID0 + 5), (short)(sID0 + 7));
            LinkClusters((short)(sID0 + 7), (short)(sID0 + 6));
            LinkClusters((short)(sID0 + 6), (short)(sID0 + 4));

            LinkClusters((short)(sID0 + 4), (short)(sID0 + 1));

            LinkClusters((short)(sID0 + 8), (short)(sID0 + 2));
            LinkClusters((short)(sID0 + 8), (short)(sID0 + 3));

            LinkClusters((short)(sID0 + 9), (short)(sID0 + 3));
            LinkClusters((short)(sID0 + 9), (short)(sID0 + 6));

            LinkClusters((short)(sID0 + 10), (short)(sID0 + 6));
            LinkClusters((short)(sID0 + 10), (short)(sID0 + 7));
        }

        // Cross-link the two halves' far ends.
        LinkClusters(10, 25);
        LinkClusters(11, 24);
        LinkClusters(12, 23);
    }

    // ── LinkClustersSplitBase ─────────────────────────────────────────────
    // mapmakerigc.cpp:2606-2625 — every team has a 2-sector split home with 2
    // neutral sectors hanging between teams; for >2 (>3) teams extra cross
    // links connect the neutral blocks.
    private void LinkClustersSplitBase()
    {
        short cTotal = _ctx.TotalClusters;
        for (int i = 0; i < _ctx.Teams; i++)
        {
            short sID = (short)(i * _ctx.ClustersPerTeam);
            LinkClusters((short)(sID + 1), (short)(sID + 2));
            LinkClusters((short)(sID + 2), (short)(sID + 3));
            LinkClusters((short)(sID + 3), (short)((sID + 4) % cTotal));
            LinkClusters((short)(sID + 2), (short)((cTotal + (sID - 1)) % cTotal));

            if (_ctx.Teams > 2)
            {
                LinkClusters((short)(sID + 2), (short)((sID + 7) % cTotal));
                if (_ctx.Teams > 3)
                    LinkClusters((short)(sID + 3), (short)((cTotal + (sID - 2)) % cTotal));
            }
        }
    }

    // ── LinkClustersBrawl — a real, confirmed EMPTY no-op ──────────────────
    // mapmakerigc.cpp:1732-1734 — the native CmapMakerBrawlIGC::LinkClusters
    // body is completely empty. This is CORRECT, not an accidental gap: Brawl
    // has exactly one cluster (the single shared sector), so there is nothing
    // to link — the port intentionally calls this method (rather than simply
    // omitting a link step) so the no-op is explicit and provable by the test
    // that asserts a Brawl map has zero warps.
    private void LinkClustersBrawl()
    {
    }

    // ── Minimal functional 3D warp placement ───────────────────────────────
    // The native cosmetic warp-randomization block (mapmakerigc.cpp:362-424) is
    // out of scope (no map-editor screen); but the transit sim consumes real
    // warp positions, so each sector's warps are laid on a ring around the
    // sector origin at native's majorRadius-derived distance, spaced evenly in
    // angle. Kept deterministic (even angles, no shuffle).
    private void PlaceWarps()
    {
        float r = 0.6f * MajorRadius;   // ~ position.z (0.6) * majorRadius
        var byCluster = new Dictionary<short, List<int>>();
        for (int i = 0; i < _warps.Count; i++)
        {
            if (!byCluster.TryGetValue(_warps[i].ClusterId, out var list))
                byCluster[_warps[i].ClusterId] = list = [];
            list.Add(i);
        }

        foreach (var (_, idxs) in byCluster)
        {
            for (int k = 0; k < idxs.Count; k++)
            {
                float angle = (float)(2.0 * Math.PI * k / idxs.Count);
                float cx = MathF.Cos(angle) * r;
                float cy = MathF.Sin(angle) * r;
                int idx = idxs[k];
                var w = _warps[idx];
                _warps[idx] = w with
                {
                    Position = [cx, cy, 0f],
                    Forward = Normalize([-cx, -cy, 0f]),
                };
            }
        }
    }

    // ── PopulateClusters ───────────────────────────────────────────────────
    // mapmakerigc.cpp:952-967 — home sectors get a starbase, every sector gets
    // asteroids. Runtime treasures are populated by Mission.PopulateTreasures
    // after assembly; they do not belong in the static map record lists.
    private void PopulateClusters()
    {
        foreach (var cluster in _clusters)
        {
            if (cluster.HomeSector)
                GenerateStarbase(cluster.ClusterId);
            PopulateCluster(cluster.ClusterId, cluster.HomeSector);
        }
    }

    // ── GenerateStarbase (port of mapmakerigc.cpp:896-941) ─────────────────
    private void GenerateStarbase(short clusterId)
    {
        short sideId = _ctx.GetClusterSide(clusterId);
        if (sideId == MapGenContext.NaSide)
            throw new InvalidOperationException($"home sector {clusterId} has no owning side");

        var civ = _core.Civilizations[(short)_ctx.Params.CivIds[(int)sideId]];
        short typeId = civ.InitialStationTypeId;
        if (!_core.StationTypes.TryGetValue(typeId, out var st))
            throw new InvalidDataException(
                $"side {sideId} civilization '{civ.Name}' initial station type {typeId} is absent from core {_core.Version}");

        // Native allows the successor-upgrade station if developed
        // (pst->GetSuccessorStationType, mapmakerigc.cpp:929); a fresh game has
        // no development, so the initial type is the faithful no-dev choice and
        // the successor leg is skipped (documented simplification).
        _stations.Add(new DataStationIGC(
            Position: [0f, 0f, 0f],
            Up: [1f, 0f, 0f],
            Forward: [0f, 0f, 1f],
            Rotation: [0f, 0f, 1f, 0f],
            ClusterId: clusterId,
            SideId: sideId,
            StationId: (short)(_nextStationId++),
            StationTypeId: typeId,
            BpHull: 1,
            BpShield: 1,
            Name: st.Name));
    }

    // ── PopulateCluster: asteroids (port of common.cpp:2963-3109, treasure
    // branch populated after mission assembly) ──────────────────────────────────────────────────────
    // Three topologies override population. PinWheel's (mapmakerigc.cpp:1901-
    // 1917) multiplies every asteroid count by k = 1 for a home sector or a
    // multi-sector centre, else k = 2 — i.e. the single central sector of a
    // MapSize-0 (single-centre) PinWheel gets double the asteroids of every
    // other sector. EastWest's (mapmakerigc.cpp:2541-2553) drives the mineable
    // and special counts from two per-sector tables (cMineable/cSpecial above)
    // indexed by the sector's position in its 13-sector team block — 0 (none),
    // 1 (base) or 2 (double) — with the generic multiplier left at 1. Brawl's
    // (mapmakerigc.cpp:1737-1798) is the most substantial: it places every
    // side's home station plus 3-5 random asteroids in its single shared
    // sector, then runs the standard 3-kind population with multipliers
    // (1, -3·(teams/2), 1). All other topologies use the base multiplier 1
    // (as the shared CmapMakerIGC::PopulateCluster does, mapmakerigc.cpp:944-949).
    //
    // Multipliers follow the free ::PopulateCluster semantics
    // (common.cpp:3024-3032, 3042-3050, 3063-3071) via ApplyMultiplier: a
    // non-negative multiplier scales the home-or-neutral base count; a NEGATIVE
    // multiplier is an absolute count (n = -multiplier) independent of base.
    // Every topology before P401 passed only non-negative values, so the
    // negative branch was unreachable (P400 stated it plainly as such); Brawl
    // passes -3·(teams/2) for special asteroids — the first real use.
    private void PopulateCluster(short clusterId, bool home)
    {
        short multMine = 1, multSpecial = 1, multGeneric = 1;
        if (_type == MapGenType.PinWheel)
        {
            short k = (home || _ctx.NeutralClustersPerTeam != 0) ? (short)1 : (short)2;
            multMine = multSpecial = multGeneric = k;
        }
        else if (_type == MapGenType.EastWest)
        {
            short local = (short)(clusterId % _ctx.ClustersPerTeam);
            multMine = EastWestMineable[local];
            multSpecial = EastWestSpecial[local];
            // multGeneric stays 1 (free-function default).
        }
        else if (_type == MapGenType.Brawl)
        {
            // Brawl's PopulateCluster runs in place of the base population for
            // its single shared cluster: the per-side home stations and the
            // 3-5 random asteroids come FIRST (mirroring mapmakerigc.cpp:1744-
            // 1793), then the standard 3-kind population below uses Brawl's
            // multipliers (1, -3·(teams/2), 1) — the special multiplier is the
            // negative absolute-count form handled by ApplyMultiplier.
            PlaceBrawlStations(clusterId);
            AddBrawlScatterAsteroids(clusterId);
            multMine = 1;
            multSpecial = (short)(-3 * (_ctx.Teams / 2));   // integer division, as native
            multGeneric = 1;
        }

        // He3 minable (c_aabmMineHe3) — the only genuinely depletable ore.
        int nMine = ApplyMultiplier(home ? _ctx.Params.PlayerSectorMineableAsteroids
                                         : _ctx.Params.NeutralSectorMineableAsteroids, multMine);
        for (int i = 0; i < nMine; i++)
            AddAsteroid(clusterId, DataAsteroidIGC.AabmMineHe3, AmountHe3);

        // Special asteroids (c_aabmSpecial).
        int nSpecial = ApplyMultiplier(home ? _ctx.Params.PlayerSectorSpecialAsteroids
                                            : _ctx.Params.NeutralSectorSpecialAsteroids, multSpecial);
        for (int i = 0; i < nSpecial; i++)
            AddAsteroid(clusterId, DataAsteroidIGC.AabmSpecial, 0f);

        // Generic buildable (c_aabmBuildable).
        int nGeneric = ApplyMultiplier(home ? _ctx.Params.PlayerSectorAsteroids
                                            : _ctx.Params.NeutralSectorAsteroids, multGeneric);
        for (int i = 0; i < nGeneric; i++)
            AddAsteroid(clusterId, DataAsteroidIGC.AabmBuildable, 0f);
    }

    /// <summary>Port of the free <c>::PopulateCluster</c> multiplier semantics
    /// (<c>common.cpp:3024-3032, 3042-3050, 3063-3071</c>): a non-negative
    /// multiplier scales the base count; a NEGATIVE multiplier is an absolute
    /// count (<c>n = -multiplier</c>) independent of the home/neutral base.
    /// This branch went unrepresented through P400 because every topology then
    /// passed only non-negative values; Brawl's special multiplier
    /// <c>-3·(teams/2)</c> finally exercises it (giving exactly <c>3·(teams/2)</c>
    /// special rocks regardless of the neutral-special base).</summary>
    private static int ApplyMultiplier(int baseCount, short mult)
        => mult >= 0 ? baseCount * mult : -mult;

    /// <summary>Brawl's per-side home stations — the real content of its
    /// <c>PopulateCluster</c> override (mapmakerigc.cpp:1744-1788). One
    /// station per side, all in the single shared cluster, evenly spaced on a
    /// circle of radius <c>c_startRadius = 2500.0f</c> at
    /// <c>angle = 2π·sideID/teams</c>. Each uses that side's own civilization's
    /// initial station type (<c>pSide-&gt;GetCivilization()-&gt;GetInitialStationType()</c>,
    /// the same per-side lookup <see cref="GenerateStarbase"/> uses — the port
    /// resolves it from the side's <c>CivIds[sideId]</c> civilization), full
    /// hull/shield, owned by that side, named after the station type.</summary>
    private void PlaceBrawlStations(short clusterId)
    {
        int teams = _ctx.Teams;
        for (int sideId = 0; sideId < teams; sideId++)
        {
            var civ = _core.Civilizations[(short)_ctx.Params.CivIds[sideId]];
            short typeId = civ.InitialStationTypeId;
            if (!_core.StationTypes.TryGetValue(typeId, out var st))
                throw new InvalidDataException(
                    $"side {sideId} civilization '{civ.Name}' initial station type {typeId} is absent from core {_core.Version}");

            float angle = 2.0f * MathF.PI * sideId / teams;
            float x = 2500.0f * MathF.Cos(angle);
            float y = 2500.0f * MathF.Sin(angle);

            _stations.Add(new DataStationIGC(
                Position: [x, y, 0f],
                Up: [1f, 0f, 0f],
                Forward: [0f, 0f, 1f],
                Rotation: [0f, 0f, 1f, 0f],
                ClusterId: clusterId,
                SideId: (short)sideId,
                StationId: (short)(_nextStationId++),
                StationTypeId: typeId,
                BpHull: 1,
                BpShield: 1,
                Name: st.Name));
        }
    }

    /// <summary>Brawl's 3-5 random "huge" neutral asteroids
    /// (mapmakerigc.cpp:1790-1793). Native <c>randomInt(3, 5)</c> is inclusive
    /// of both bounds (zmath.h:125-127), so the port draws <c>[3, 6)</c>; each
    /// is <c>IasteroidIGC::GetRandomType(0)</c> — a "huge" neutral asteroid
    /// (types 0/1, capabilities 0, ore 0, asteroidigc.cpp:238-240) — placed
    /// with zero ore via the shared <see cref="AddAsteroid"/> port.</summary>
    private void AddBrawlScatterAsteroids(short clusterId)
    {
        int n = Random.Shared.Next(3, 6);   // randomInt(3,5) inclusive
        for (int i = 0; i < n; i++)
            AddAsteroid(clusterId, 0, 0f);
    }


    // Port of native CreateAsteroid (common.cpp:2844-2893), but the port has no
    // OT_asteroidType catalog, so GetTypeDefaults is a small constructed record
    // keyed on the requested capability mask. See the P397 report for the
    // simplification.
    private void AddAsteroid(short clusterId, short capabilities, float amountHe3)
    {
        var rng = Random.Shared;
        // Base radius per kind (native varies by type definition).
        int baseRadius = capabilities == DataAsteroidIGC.AabmMineHe3 ? 120
                       : capabilities == DataAsteroidIGC.AabmSpecial ? 100
                       : 140;
        short radius = (short)rng.Next(baseRadius, 2 * baseRadius + 1);   // randomInt(def.radius, 2*def.radius)

        float ore = 0f;
        if (capabilities == DataAsteroidIGC.AabmMineHe3 && amountHe3 > 0f)
            ore = 1.0f * amountHe3;      // base type ore (1.0) * per-map He3 scale
        float oreMax = ore;              // da.asteroidDef.oreMax = da.asteroidDef.ore (common.cpp:2875)

        float[] fwd = RandomDirection();
        float[] up = Orthogonalize(fwd);
        float signature = radius / 100.0f;
        int id = _nextAsteroidId++;

        _asteroids.Add(new DataAsteroidIGC(
            Signature: signature,
            Position: RandomShellPosition(radius),
            Up: up,
            Forward: fwd,
            Rotation: [fwd[0], fwd[1], fwd[2], 0.2f / signature],
            Ore: ore,
            OreMax: oreMax,
            AabmCapabilities: capabilities,
            AsteroidId: (short)id,
            HitPoints: 1000f,
            Radius: radius,
            ModelName: capabilities == DataAsteroidIGC.AabmMineHe3 ? "rock_mine" : "rock_build",
            TextureName: "",
            IconName: "",
            ClusterId: clusterId,
            Name: $"Ast{id}-{_ctx.GetCluster(clusterId).Name}",
            Fraction: 255));
    }

    /// <summary>The per-map He3 amount each mineable rock's base ore is scaled
    /// by — the port of <c>PopulateClusters</c>' <c>amountHe3</c> computation
    /// (<c>mapmakerigc.cpp:954-956</c>): <c>GetFloatConstant(c_fcidAmountHe3) *
    /// fHe3Density * teams / GetMinableAsteroids()</c>. A zero minable count is
    /// guarded to avoid a division by zero (yields 0 → He3 rocks carry no ore).</summary>
    private float AmountHe3
    {
        get
        {
            int minable = _ctx.Params.PlayerSectorMineableAsteroids * _ctx.TeamClusters
                        + _ctx.Params.NeutralSectorMineableAsteroids * (_ctx.OtherClusters + _ctx.NeutralClusters);
            if (minable == 0) return 0f;
            float baseAmt = FloatConst(9);   // c_fcidAmountHe3
            return baseAmt * _ctx.Params.He3Density * _ctx.Teams / minable;
        }
    }

    private float FloatConst(int idx)
    {
        var fc = _core.Constants.FloatConstants;
        return fc is { Length: > 0 } && idx >= 0 && idx < fc.Length ? fc[idx] : 0f;
    }

    // ── geometry / randomness helpers ──────────────────────────────────────
    private static float[] RandomDirection()
    {
        double u = Random.Shared.NextDouble() * 2.0 - 1.0;   // z ∈ [-1, 1]
        double phi = Random.Shared.NextDouble() * 2.0 * Math.PI;
        double s = Math.Sqrt(1.0 - u * u);
        return [(float)(s * Math.Cos(phi)), (float)(s * Math.Sin(phi)), (float)u];
    }

    private static float[] Orthogonalize(float[] v)
    {
        // cross(v, xAxis); fall back to cross(v, zAxis) if nearly parallel
        // (CreateAsteroid, common.cpp:2858-2863)
        var c = Cross(v, [1f, 0f, 0f]);
        if (Dot(c, c) <= 0.1f) c = Cross(v, [0f, 0f, 1f]);
        return Normalize(c);
    }

    private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static float[] Cross(float[] a, float[] b) =>
        [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static float[] Normalize(float[] v)
    {
        float len = MathF.Sqrt(Dot(v, v));
        return len < 1e-6f ? [0f, 0f, 1f] : [v[0] / len, v[1] / len, v[2] / len];
    }

    /// <summary>A random point in a shell well away from the sector origin (the
    /// starbase sits at the origin) — the simplified stand-in for native's
    /// <c>RandomPosition(pcluster-&gt;GetModels(), …)</c>
    /// (<c>common.cpp:2882-2884</c>), which avoids overlapping existing models.
    /// The shell keeps rocks off the base and off the ~3000-unit warp ring.</summary>
    private static float[] RandomShellPosition(float radius)
    {
        const float inner = 600f, outer = 2400f;
        // random direction, random distance in [inner, outer]
        var dir = RandomDirection();
        float dist = (float)(inner + Random.Shared.NextDouble() * (outer - inner));
        return [dir[0] * dist, dir[1] * dist, dir[2] * dist * 0.5f];
    }
}
