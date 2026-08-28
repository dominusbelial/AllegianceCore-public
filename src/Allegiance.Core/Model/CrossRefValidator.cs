namespace Allegiance.Core.Model;

/// <summary>One validated cross-reference finding.</summary>
/// <param name="Kind">A short, stable classifier, e.g. <c>station-type</c>.</param>
/// <param name="Detail">Human-readable specifics: which record, which dangling id.</param>
public sealed record CrossRefIssue(string Kind, string Detail);

/// <summary>
/// Non-throwing cross-reference validation over a loaded type universe. Reports
/// what dangles; does <b>not</b> refuse to load anything. Some cross-references
/// legitimately dangle (a hull may succeed an ID that only appears in another
/// core), and a loader that rejects real shipped data is useless — so this
/// reports, it never throws, and it is deliberately <b>not</b> called from
/// <c>Load</c>.
/// </summary>
public static class CrossRefValidator
{
    /// <summary>
    /// Validate the internal cross-references of <paramref name="core"/>, and —
    /// when a map is supplied — the map→core and map-internal references.
    /// Returns every dangling reference found; an empty list is a clean bill.
    /// </summary>
    public static IReadOnlyList<CrossRefIssue> Validate(StaticCore core, MissionMap? map = null)
    {
        var issues = new List<CrossRefIssue>();

        var hullIds = new HashSet<short>(core.HullTypes.Keys);
        var partIds = new HashSet<short>(core.PartTypes.Keys);
        partIds.UnionWith(core.Launchers.Keys);
        var projectileIds = new HashSet<short>(core.ProjectileTypes.Keys);
        var expendableIds = new HashSet<short>(core.MissileTypes.Keys);
        expendableIds.UnionWith(core.MineTypes.Keys);
        expendableIds.UnionWith(core.ProbeTypes.Keys);
        expendableIds.UnionWith(core.ChaffTypes.Keys);
        var stationTypeIds = new HashSet<short>(core.StationTypes.Keys);
        var devIds = new HashSet<short>(core.Developments.Keys);

        void Check(bool dangling, string kind, string detail)
        {
            if (dangling) issues.Add(new CrossRefIssue(kind, detail));
        }

        // Hull successor chain.
        foreach (var (id, h) in core.HullTypes)
            Check(h.SuccessorHullId != -1 && !hullIds.Contains(h.SuccessorHullId),
                "hull-successor",
                $"core {core.Version}: hull {id} ('{h.Name}') successor {h.SuccessorHullId} missing");

        // Part / launcher successor chain (both buckets share the id space).
        foreach (var (id, p) in core.PartTypes)
            Check(p.SuccessorPartId != -1 && !partIds.Contains(p.SuccessorPartId),
                "part-successor",
                $"core {core.Version}: part {id} ('{p.Name}') successor {p.SuccessorPartId} missing");
        foreach (var (id, l) in core.Launchers)
            Check(l.SuccessorPartId != -1 && !partIds.Contains(l.SuccessorPartId),
                "part-successor",
                $"core {core.Version}: launcher {id} successor {l.SuccessorPartId} missing");

        // Civilization bootstrap references.
        foreach (var (id, c) in core.Civilizations)
        {
            Check(c.Lifepod != -1 && !hullIds.Contains(c.Lifepod),
                "civilization-lifepod",
                $"core {core.Version}: civilization {id} ('{c.Name}') lifepod hull {c.Lifepod} missing");
            Check(c.InitialStationTypeId != -1 && !stationTypeIds.Contains(c.InitialStationTypeId),
                "civilization-initial-station",
                $"core {core.Version}: civilization {id} ('{c.Name}') initial station type {c.InitialStationTypeId} missing");
        }

        // Weapon → projectile and launcher → expendable.
        foreach (var (id, p) in core.PartTypes)
            if (p.Type is DataPartTypeIGC.Weapon w)
                Check(w.ProjectileTypeId != -1 && !projectileIds.Contains(w.ProjectileTypeId),
                    "weapon-projectile",
                    $"core {core.Version}: part {id} weapon projectile {w.ProjectileTypeId} missing");
        foreach (var (id, l) in core.Launchers)
            Check(l.ExpendabletypeId != -1 && !expendableIds.Contains(l.ExpendabletypeId),
                "launcher-expendable",
                $"core {core.Version}: launcher {id} expendable {l.ExpendabletypeId} missing");

        // Treasure-set entry targets.
        foreach (var (tsId, ts) in core.TreasureSets)
        for (var idx = 0; idx < ts.Entries.Length; idx++)
        {
            var e = ts.Entries[idx];
            var where = $"treasure-set {tsId} ('{ts.Name}') entry #{idx}";
            switch (e.Code)
            {
                case TreasureCode.Part:
                    Check(!partIds.Contains((short)e.TreasureId),
                        "treasure-part", $"core {core.Version}: {where} part id {e.TreasureId} missing");
                    break;
                case TreasureCode.Powerup:
                    // PowerupCode bitmap: valid 1..255 (bits 0..7); 0 or ≥256 is invalid.
                    Check(e.TreasureId == 0 || e.TreasureId >= 256,
                        "treasure-powerup", $"core {core.Version}: {where} invalid powerup bitmap 0x{e.TreasureId:X}");
                    break;
                case TreasureCode.Cash:
                    Check(e.TreasureId == 0,
                        "treasure-cash", $"core {core.Version}: {where} cash $0");
                    break;
                case TreasureCode.Development:
                    Check(!devIds.Contains((short)e.TreasureId),
                        "treasure-development", $"core {core.Version}: {where} development id {e.TreasureId} missing");
                    break;
            }
        }

        if (map is not null) ValidateMap(map, core, issues);
        return issues;
    }

    private static void ValidateMap(MissionMap map, StaticCore core, List<CrossRefIssue> issues)
    {
        var clusterIds = new HashSet<short>(map.Clusters.Select(c => c.ClusterId));
        var warpIds = new HashSet<short>(map.Warps.Select(w => w.WarpId));

        void Check(bool dangling, string kind, string detail)
        {
            if (dangling) issues.Add(new CrossRefIssue(kind, detail));
        }

        foreach (var s in map.Stations)
        {
            Check(!core.StationTypes.ContainsKey(s.StationTypeId),
                "station-type",
                $"map '{mapLabel(map)}': station {s.StationId} ('{s.Name}') in cluster {s.ClusterId} " +
                $"has station type {s.StationTypeId} not in core {core.Version}");
            Check(!clusterIds.Contains(s.ClusterId),
                "station-cluster",
                $"map '{mapLabel(map)}': station {s.StationId} ('{s.Name}') names missing cluster {s.ClusterId}");
        }

        foreach (var w in map.Warps)
        {
            Check(!clusterIds.Contains(w.ClusterId),
                "warp-cluster",
                $"map '{mapLabel(map)}': warp {w.WarpId} ('{w.Name}') names missing cluster {w.ClusterId}");
            // destinationID resolves to a warp OR a sector (cluster) in the same map.
            Check(!warpIds.Contains(w.DestinationId) && !clusterIds.Contains(w.DestinationId),
                "warp-destination",
                $"map '{mapLabel(map)}': warp {w.WarpId} ('{w.Name}') destination {w.DestinationId} " +
                "is neither a warp nor a sector in this map");
        }

        foreach (var a in map.Asteroids)
        {
            Check(!clusterIds.Contains(a.ClusterId),
                "asteroid-cluster",
                $"map '{mapLabel(map)}': asteroid {a.AsteroidId} ('{a.Name}') names missing cluster {a.ClusterId}");
        }
    }

    private static string mapLabel(MissionMap map) =>
        map.Clusters.Count > 0 ? map.Clusters[0].Name : "(unnamed map)";
}
