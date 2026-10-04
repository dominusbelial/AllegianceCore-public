using System.Numerics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P541 — pure-logic tests for the three new starmap helpers:
/// <see cref="StarmapHitTest"/> (screen point → cluster), <see cref="StarmapClick"/>
/// (the native left-click branch decision + the native cluster-goto buoy) and
/// <see cref="StarmapGotoRouter"/> (the client autopilot's next warp waypoint).
/// No graphics device — pure System.Numerics inputs throughout, the same
/// discipline as the P249/P272 layout tests.
/// </summary>
public class StarmapClickGotoTests
{
    private static StarmapLayout.ClusterSite Site(short id, float x, float y) =>
        new(id, "S" + id, x, y);

    // ── StarmapHitTest ────────────────────────────────────────────────────────

    private static StarmapLayout ThreeDotLayout()
        => StarmapLayout.Fit(
            [Site(1, 0f, 0f), Site(2, 100f, 50f), Site(3, 50f, 25f)],
            100f, 50f, 400f, 200f, 20f);

    [Fact]
    public void HitTest_MapsAClickOnTopOfAClusterToThatCluster()
    {
        var layout = ThreeDotLayout();
        layout.TryGetPoint(2, out var p2);
        // Exactly on cluster 2's centre.
        Assert.Equal((short)2, StarmapHitTest.HitTest(layout, p2));
    }

    [Fact]
    public void HitTest_MapsNearbyPoint_WithinRadius_ToNearestCluster()
    {
        // Well-separated sites so the nearest is unambiguous.
        var layout = StarmapLayout.Fit(
            [Site(1, 0f, 0f), Site(2, 1000f, 0f), Site(3, 0f, 1000f)],
            100f, 50f, 400f, 200f, 20f);
        layout.TryGetPoint(2, out var p2);
        // 5 px from cluster 2's centre (well inside 15) and far from 1/3.
        var near = p2 + new Vector2(5f, 0f);
        Assert.Equal((short)2, StarmapHitTest.HitTest(layout, near));
    }

    [Fact]
    public void HitTest_OutsideEveryCluster_ReturnsNone()
    {
        var layout = ThreeDotLayout();
        // Far corner of the usable panel, well beyond any 15px radius.
        var far = new Vector2(470f, 220f);
        Assert.Equal((short)(-1), StarmapHitTest.HitTest(layout, far));
    }

    [Fact]
    public void HitTest_BeyondClickRadius_ReturnsNone()
    {
        var layout = ThreeDotLayout();
        layout.TryGetPoint(3, out var p3);
        // 100 px from cluster 3's centre — beyond the 15px native radius.
        var away = p3 + new Vector2(100f, 0f);
        Assert.Equal((short)(-1), StarmapHitTest.HitTest(layout, away));
    }

    [Fact]
    public void HitTest_JustInsideRadius_Selects()
    {
        var layout = ThreeDotLayout();
        layout.TryGetPoint(3, out var p3);
        // 10 px (inside 15) → selected.
        var inside = p3 + new Vector2(10f, 0f);
        Assert.Equal((short)3, StarmapHitTest.HitTest(layout, inside));
    }

    [Fact]
    public void HitTest_OnEmptyLayout_ReturnsNone()
    {
        var layout = StarmapLayout.Fit([], 0f, 0f, 100f, 100f, 10f);
        Assert.Equal((short)(-1), StarmapHitTest.HitTest(layout, Vector2.Zero));
    }

    // ── StarmapClick.Decide (native branch order, sectormap.cpp:1521-1571) ────

    [Fact]
    public void Decide_NoClusterFound_IsNone()
        => Assert.Equal(StarmapClick.Action.None, StarmapClick.Decide(clusterFound: false, ripcordActive: false, autopilotEngaged: false, teleportPaneOpen: false, docked: false, isCurrentCluster: false));

    [Fact]
    public void Decide_RipcordActive_WithoutAutopilot_IsRipcord()
        => Assert.Equal(StarmapClick.Action.Ripcord, StarmapClick.Decide(true, ripcordActive: true, autopilotEngaged: false, teleportPaneOpen: true, docked: false, isCurrentCluster: false));

    [Fact]
    public void Decide_RipcordOnAutopilot_FallsThroughToTeleport()
        // native: ripcord && !autopilot → ripcord; on autopilot it falls to the
        // next branch (teleport pane open → teleport).
        => Assert.Equal(StarmapClick.Action.Teleport, StarmapClick.Decide(true, ripcordActive: true, autopilotEngaged: true, teleportPaneOpen: true, docked: false, isCurrentCluster: false));

    [Fact]
    public void Decide_TeleportPaneOpen_IsTeleport()
        => Assert.Equal(StarmapClick.Action.Teleport, StarmapClick.Decide(true, false, false, teleportPaneOpen: true, docked: false, isCurrentCluster: false));

    [Fact]
    public void Decide_Docked_IsViewCluster()
        => Assert.Equal(StarmapClick.Action.ViewCluster, StarmapClick.Decide(true, false, false, false, docked: true, isCurrentCluster: false));

    [Fact]
    public void Decide_CurrentCluster_IsNone()
        => Assert.Equal(StarmapClick.Action.None, StarmapClick.Decide(true, false, false, false, docked: false, isCurrentCluster: true));

    [Fact]
    public void Decide_OtherSector_IsGoto()
        => Assert.Equal(StarmapClick.Action.Goto, StarmapClick.Decide(true, false, false, false, docked: false, isCurrentCluster: false));

    [Fact]
    public void BuildClusterGotoBuoy_MatchesNative()
    {
        // native sectormap.cpp:1553-1557 — position (0,0,0), clusterID = clicked
        // sector, type = c_buoyCluster.
        var b = StarmapClick.BuildClusterGotoBuoy(1052);
        Assert.Equal([0f, 0f, 0f], b.Position);
        Assert.Equal((short)1052, b.ClusterId);
        Assert.Equal(DataBuoyIGC.CBuoyCluster, b.Type);
        Assert.Equal((short)0, b.BuoyId);
        Assert.False(b.Visible);
    }

    // ── StarmapGotoRouter (client autopilot cross-cluster waypoint) ──────────

    private static readonly Vector3 W1 = new(100f, 200f, 300f);
    private static readonly Vector3 W2 = new(400f, 500f, 600f);

    private static readonly StarmapGotoRouter.SectorWarp[] Warps = new[]
    {
        new StarmapGotoRouter.SectorWarp(W1, 1053),   // current sector → 1053
        new StarmapGotoRouter.SectorWarp(W2, 1054),   // current sector → 1054
    };

    [Fact]
    public void Router_AlreadyInTargetSector_ReportsArrival()
    {
        Assert.False(StarmapGotoRouter.TryNextWaypoint(1052, 1052, [1052], Warps, out _));
    }

    [Fact]
    public void Router_PicksTheWarpTowardTheNextRouteHop()
    {
        // Route 1052 → 1053 → 1055. In 1052, next hop is 1053 → warp W1.
        Assert.True(StarmapGotoRouter.TryNextWaypoint(1052, 1055, [1052, 1053, 1055], Warps, out var wp));
        Assert.Equal(W1, wp);
    }

    [Fact]
    public void Router_AdvancesToLaterRouteHop()
    {
        // Already transited into 1053; route now 1053 → 1055. 1053's "warps"
        // lead onward; here we model the route with current=1053 and a warp to 1055.
        var w = new[] { new StarmapGotoRouter.SectorWarp(W1, 1055) };
        Assert.True(StarmapGotoRouter.TryNextWaypoint(1053, 1055, [1053, 1055], w, out var wp));
        Assert.Equal(W1, wp);
    }

    [Fact]
    public void Router_EmptyOrNullRoute_ReturnsFalse()
    {
        Assert.False(StarmapGotoRouter.TryNextWaypoint(1052, 1055, [], Warps, out _));
        Assert.False(StarmapGotoRouter.TryNextWaypoint(1052, 1055, null!, Warps, out _));
    }

    [Fact]
    public void Router_MissingConnectingWarp_ReturnsFalse()
    {
        // Route wants to hop 1052→1055 but no warp in Warps leads to 1055.
        Assert.False(StarmapGotoRouter.TryNextWaypoint(1052, 1055, [1052, 1055], Warps, out _));
    }

    [Fact]
    public void Router_CurrentSectorNotOnRoute_ReturnsFalse()
    {
        Assert.False(StarmapGotoRouter.TryNextWaypoint(1099, 1055, [1052, 1055], Warps, out _));
    }

    // ── StarmapGotoRouter.TryNextWaypointFromWarps (route over REAL warps) ────

    private static readonly StarmapGotoRouter.AllWarp[] AllWarps = new[]
    {
        new StarmapGotoRouter.AllWarp(W1, 1052, 1053),   // 1052 → 1053
        new StarmapGotoRouter.AllWarp(W2, 1053, 1055),   // 1053 → 1055
    };

    [Fact]
    public void FromWarps_PicksTheRealWarpTowardTheTarget()
    {
        // 1052 → 1055 must go 1052→1053→1055; the next hop is 1053's warp (W1).
        Assert.True(StarmapGotoRouter.TryNextWaypointFromWarps(1052, 1055, AllWarps, out var wp));
        Assert.Equal(W1, wp);
    }

    [Fact]
    public void FromWarps_AlreadyInTarget_ReportsArrival()
    {
        Assert.False(StarmapGotoRouter.TryNextWaypointFromWarps(1055, 1055, AllWarps, out _));
    }

    [Fact]
    public void FromWarps_UnreachableTarget_ReturnsFalse()
    {
        Assert.False(StarmapGotoRouter.TryNextWaypointFromWarps(1052, 1099, AllWarps, out _));
        Assert.False(StarmapGotoRouter.TryNextWaypointFromWarps(1052, 1055, Array.Empty<StarmapGotoRouter.AllWarp>(), out _));
    }

    [Fact]
    public void FromWarps_NeverChoosesAHopWithNoRealWarp()
    {
        // A hypothetical pre-computed path might claim a direct 1052→1055 hop, but
        // there is NO real warp 1052→1055 — only 1052→1053 and 1053→1055. Routing
        // over the real warp table must pick the 1053 warp (W1), never a phantom.
        Assert.True(StarmapGotoRouter.TryNextWaypointFromWarps(1052, 1055, AllWarps, out var wp));
        Assert.Equal(W1, wp);
        Assert.NotEqual(W2, wp);
    }

    [Fact]
    public void FromWarps_NextHopWarp_AlwaysExistsInCurrentSector()
    {
        // Every hop the router picks must be backed by a warp sourced in the
        // current sector — the invariant that protects against graph/export drift.
        foreach (var (from, to) in new[] { ((short)1052, (short)1053), ((short)1053, (short)1055) })
        {
            Assert.True(StarmapGotoRouter.TryNextWaypointFromWarps(from, to, AllWarps, out var wp));
            Assert.True(AllWarps.Any(w => w.SourceSector == from && w.DestSector == to && w.Position == wp),
                $"router picked a waypoint for hop {from}->{to} with no matching real warp");
        }
    }
}
