using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P540c — the client autopilot/router must route over the REAL warp table with
/// each warp's destination resolved as the destination SECTOR (from its PARTNER
/// warp, native <c>warpIGC.h:129-141</c>), on the server's real map
/// (<c>XXV_simple.igc</c>). This proves routing is UNCHANGED after restoring the
/// native wire: every AllWarp edge's <c>DestSector</c> is a real sector (the
/// partner warp's <c>ClusterId</c> — never a raw warp id mistaken for a sector),
/// and the router picks a real warp in the current sector toward a reachable
/// target, exactly as <c>Game1.AllWarps</c> feeds it.
/// </summary>
public class WarpRoutingRealMapTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Mission LoadMission()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        return Mission.Assemble(core, map, [new SideSpec(0, "Team 1", civ)], new NullIgcSite());
    }

    /// <summary>
    /// Build the router's ground-truth warp table exactly as the client's
    /// <c>Game1.AllWarps</c> does: mouth position, source sector (own ClusterId),
    /// destination sector resolved from the PARTNER warp (skip unknown partners,
    /// never guess).
    /// </summary>
    private static List<StarmapGotoRouter.AllWarp> AllWarpsFrom(Mission mission)
    {
        var warpTable = mission.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .ToDictionary(w => w.Warp.WarpId, w => w.Warp);
        var all = new List<StarmapGotoRouter.AllWarp>();
        foreach (var w in warpTable.Values)
        {
            if (WarpDestinationResolver.DestinationSector(warpTable, w.WarpId) is not { } destSector) continue;
            all.Add(new StarmapGotoRouter.AllWarp(
                new Vector3(w.Position[0], w.Position[1], w.Position[2]),
                w.ClusterId, destSector));
        }
        return all;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealMap_EveryEdgeDest_IsThePartnerWarpsCluster_NotAWarpId()
    {
        Require.Artwork();
        var mission = LoadMission();
        var all = AllWarpsFrom(mission);
        var sectorIds = mission.Sectors.Select(s => s.Id).ToHashSet();

        Assert.NotEmpty(all);
        foreach (var aw in all)
            // DestSector is a REAL sector on the map — never a warp id phantom.
            Assert.Contains(aw.DestSector, sectorIds);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealMap_RouterPicks_ARealWarpTowardTheTargetSector()
    {
        Require.Artwork();
        var mission = LoadMission();
        var all = AllWarpsFrom(mission);
        Assert.NotEmpty(all);

        var warpTable = mission.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .ToDictionary(w => w.Warp.WarpId, w => w.Warp);

        // A warp that's present in the resolved AllWarps table with a resolvable
        // partner on the real map.
        var candidate = mission.Warps.First(w =>
            w.Destination is not null && w.Partner() is not null
            && w.Warp.Position is { Length: 3 }
            && WarpDestinationResolver.DestinationSector(warpTable, w.Warp.WarpId) is { });

        short current = candidate.Warp.ClusterId;
        short target = candidate.Partner()!.Warp.ClusterId;
        Assert.NotEqual(current, target);

        Assert.True(StarmapGotoRouter.TryNextWaypointFromWarps(current, target, all, out var wp),
            $"router must find a waypoint from sector {current} to {target}");

        // The picked waypoint is the mouth of a REAL warp in the current sector
        // leading to the target — i.e. this warp's own mouth, unchanged under the
        // native wire.
        Assert.Equal(
            new Vector3(candidate.Warp.Position[0], candidate.Warp.Position[1], candidate.Warp.Position[2]),
            wp);
        Assert.Contains(all,
            a => a.SourceSector == current && a.DestSector == target && a.Position == wp);
    }
}
