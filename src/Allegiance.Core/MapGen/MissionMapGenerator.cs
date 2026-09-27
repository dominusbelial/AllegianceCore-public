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

    private readonly MapGenContext _ctx;
    private readonly StaticCore _core;

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
    }

    /// <summary>
    /// Generate a whole mission map for <paramref name="pmp"/>. Dispatches on
    /// <see cref="MapGenParams.Type"/> exactly like native
    /// <c>ImapMakerIGC::Create</c>'s switch (<c>mapmakerigc.cpp:182-201</c>);
    /// today only <see cref="MapGenType.SingleRing"/> is implemented, which is
    /// the native base-class default and thus shares this generator verbatim.
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

        var gen = new MissionMapGenerator(new MapGenContext(pmp, now), core);
        switch (pmp.Type)
        {
            case MapGenType.SingleRing:
                gen.GenerateLayout();      // the base-class default = SingleRing
                gen.PlaceWarps();          // minimal functional 3D placement
                gen.PopulateClusters();
                break;
            default:
                throw new InvalidDataException($"map type '{pmp.Type}' is not implemented yet");
        }

        return MissionMap.FromLists(gen._clusters, gen._warps, gen._stations, gen._asteroids);
    }

    // ── GenerateLayout (base default; SingleRing overrides nothing) ─────────
    // mapmakerigc.cpp:879-893
    private void GenerateLayout()
    {
        // KGJV: team-clusters-per-team from iMapSize (0 => 1, else 2); neutral = 1.
        short teamPerTeam = (short)(_ctx.Params.MapSize != 0 ? 2 : 1);
        _ctx.SetTeamClustersPerTeam(teamPerTeam);
        _ctx.SetNeutralClustersPerTeam(1);
        GenerateRingClusters();
        LinkClusters();      // the no-arg default = a plain ring (mapmakerigc.cpp:858)
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
    // asteroids. Treasure creation is deferred (the port's map universe has no
    // treasure list) — documented simplification.
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

    // ── PopulateCluster: asteroids only (port of common.cpp:2963-3109,
    // treasure branch deferred) ─────────────────────────────────────────────
    private void PopulateCluster(short clusterId, bool home)
    {
        // He3 minable (c_aabmMineHe3) — the only genuinely depletable ore.
        short nMine = home ? _ctx.Params.PlayerSectorMineableAsteroids
                           : _ctx.Params.NeutralSectorMineableAsteroids;
        for (short i = 0; i < nMine; i++)
            AddAsteroid(clusterId, DataAsteroidIGC.AabmMineHe3, AmountHe3);

        // Special asteroids (c_aabmSpecial).
        short nSpecial = home ? _ctx.Params.PlayerSectorSpecialAsteroids
                              : _ctx.Params.NeutralSectorSpecialAsteroids;
        for (short i = 0; i < nSpecial; i++)
            AddAsteroid(clusterId, DataAsteroidIGC.AabmSpecial, 0f);

        // Generic buildable (c_aabmBuildable).
        short nGeneric = home ? _ctx.Params.PlayerSectorAsteroids
                              : _ctx.Params.NeutralSectorAsteroids;
        for (short i = 0; i < nGeneric; i++)
            AddAsteroid(clusterId, DataAsteroidIGC.AabmBuildable, 0f);
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
