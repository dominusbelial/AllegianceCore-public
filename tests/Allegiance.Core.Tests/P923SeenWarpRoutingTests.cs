using System.Buffers.Binary;
using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P923SeenWarpRoutingTests
{
    private sealed class Site : IIgcSite { }

    // A->D is hidden; A->B->C->D is known. Paired, far-away exits keep
    // personal scanning from accidentally revealing the graph fixture.
    internal static Mission Graph(bool enabled = true)
    {
        var body = new byte[DataConstantsIGC.Size + DataCivilizationIGC.Size];
        for (int i = 0; i < DataCivilizationIGC.GaCount; i++)
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(DataConstantsIGC.Size + 160 + 4 * i), 1);
        var core = StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize, Version = 1,
            Body = body, BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, DataConstantsIGC.Size, 0),
                new(IgcObjectType.Civilization, DataCivilizationIGC.Size, DataConstantsIGC.Size)] });
        var clusters = Enumerable.Range(0, 4).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with
            { ClusterId = (short)i, Name = $"P923-{i}", Active = true }).ToArray();
        DataWarpIGC W(short id, short from, short partner) => DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with
        { WarpId = id, ClusterId = from, DestinationId = partner, Radius = 10, Signature = 1,
            Position = [10000 + id * 100, 0, 0], Forward = [1, 0, 0] };
        var mission = Mission.Assemble(core, MissionMap.FromLists(clusters,
            [W(10, 0, 11), W(11, 3, 10), W(20, 0, 21), W(21, 1, 20),
             W(30, 1, 31), W(31, 2, 30), W(40, 2, 41), W(41, 3, 40)], [], []),
            [new(0, "Alpha", 0), new(1, "Bravo", 0)], new Site());
        mission.SeenWarpRoutingEnabled = enabled;
        return mission;
    }

    internal static Ship Place(Mission m, bool native = false, byte pilot = Ship.PilotTypeMiner)
    {
        m.NativeGotoPlanEnabled = native;
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        { HitPoints = 100, Length = 10, Mass = 1, Speed = 100, Thrust = 100, ScannerRange = 100,
            SideMultiplier = 1, BackMultiplier = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1] };
        var ship = new Ship(100, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = true, PilotType = pilot, GotoWaypoint = new(GotoKind.Cluster, 3, Vector3.Zero) };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    private static object? Call(Ship ship, string method, params object[] args) => typeof(Ship)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args);
    private static T Field<T>(Ship ship, string field) => (T)typeof(Ship)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ship)!;
    private static void KnownLong(Mission m)
    {
        foreach (var warp in m.Warps.Where(w => w.Warp.WarpId >= 20)) warp.SideVisibility.SetSideVisibility(0, true);
    }
    private static void LegacyGoal(Ship ship)
    {
        ship.IntendedNextWarpId = -1; // ApplyNpcAutopilot resets this before resolving a leg.
        Call(ship, "CrossClusterWarpGoal", (short)3, "P923");
    }
    private static void NativeGoal(Ship ship) => Call(ship, "ApplyNativeNpcAutopilot", 1d);

    [Theory]
    [InlineData(false, Ship.PilotTypeMiner)]
    [InlineData(true, Ship.PilotTypeMiner)]
    [InlineData(false, Ship.PilotTypeBuilder)]
    [InlineData(true, Ship.PilotTypeBuilder)]
    [InlineData(false, Ship.PilotTypeLayer)]
    [InlineData(true, Ship.PilotTypeLayer)]
    [InlineData(false, Ship.PilotTypeWingman)]
    [InlineData(true, Ship.PilotTypeWingman)]
    [InlineData(false, Ship.PilotTypeCarrier)]
    [InlineData(true, Ship.PilotTypeCarrier)]
    public void No_route_preserves_pending_orders_without_inventing_a_fallback(bool native, byte pilot)
    {
        using var m = Graph(); var ship = Place(m, native, pilot);
        var order = ship.GotoWaypoint;
        var previous = new ControlData(.2f, .3f, .4f, 1);
        ship.Controls = previous;
        Call(ship, "ApplyNpcAutopilot", 1d);
        Assert.Equal(-1, ship.IntendedNextWarpId);
        Assert.Equal(order, ship.GotoWaypoint);
        // Existing legacy no-goal code leaves controls alone; native waits
        // with neutral controls (and may Dodge when obstacles are present).
        Assert.Equal(native ? ControlData.Neutral : previous, ship.Controls);
    }

    [Fact]
    public void Flag_defaults_off_and_off_preserves_unseen_shortcut_and_cached_result()
    {
        Assert.False(new LiveSettingsConfig().SeenWarpRouting);
        using var m = Graph(false); var ship = Place(m);
        Assert.Same(m.GetWarp(10), m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        Assert.Same(m.GetWarp(10), m.Sectors[0].FindPathToward(m.Sectors[3]));
        LegacyGoal(ship); Assert.Equal(10, ship.IntendedNextWarpId);
        KnownLong(m); LegacyGoal(ship);
        Assert.Equal(10, ship.IntendedNextWarpId);
        Assert.Equal(0, Field<long>(ship, "_npcNavViewVersion"));
    }

    // common.cpp:920 AND :987: gating just the initial frontier is insufficient.
    [Fact]
    public void Every_hop_is_gated_and_discovery_makes_a_shorter_route_available()
    {
        using var m = Graph(); var ship = Place(m);
        Assert.Null(m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        m.GetWarp(20)!.SideVisibility.SetSideVisibility(0, true);
        Assert.Null(m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        KnownLong(m);
        Assert.Same(m.GetWarp(20), m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        m.GetWarp(10)!.SideVisibility.SetSideVisibility(0, true);
        Assert.Same(m.GetWarp(10), m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        Assert.Null(m.Sectors[0].FindPathToward(m.Sectors[0], ship));
        var dangling = new ResolvedWarp(m.GetWarp(10)!.Warp, null);
        var list = (List<ResolvedWarp>)typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m.Sectors[0])!;
        list.Insert(0, dangling);
        Assert.Same(m.GetWarp(10), m.Sectors[0].FindPathToward(m.Sectors[3], ship));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cached_miss_and_longer_route_refresh_only_when_own_side_view_changes(bool native)
    {
        using var m = Graph(); var ship = Place(m, native);
        void Goal() { if (native) NativeGoal(ship); else LegacyGoal(ship); }
        Goal(); Assert.Equal(-1, ship.IntendedNextWarpId);
        long first = Field<long>(ship, "_warpViewVersion");
        for (int i = 0; i < 20; i++) Goal();
        Assert.Equal(first, Field<long>(ship, "_warpViewVersion"));
        m.GetWarp(10)!.SideVisibility.SetSideVisibility(1, true);
        Goal(); Assert.Equal(first, Field<long>(ship, "_warpViewVersion"));
        KnownLong(m); Goal(); Assert.Equal(20, ship.IntendedNextWarpId);
        long longer = Field<long>(ship, "_warpViewVersion");
        m.GetWarp(10)!.SideVisibility.RevealToSide(0);
        Goal(); Assert.Equal(10, ship.IntendedNextWarpId);
        Assert.True(Field<long>(ship, "_warpViewVersion") > longer);
        long shorter = Field<long>(ship, "_warpViewVersion");
        m.GetWarp(10)!.SideVisibility.SetSideVisibility(0, false);
        m.GetWarp(10)!.SideVisibility.RevealToSide(0);
        Goal(); Assert.Equal(shorter, Field<long>(ship, "_warpViewVersion"));
        Assert.True(m.GetWarp(10)!.SeenBySide(0));
        Assert.False(m.GetWarp(10)!.GetCurrentEye(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Personal_scanner_can_recover_a_cached_miss_before_side_discovery(bool native)
    {
        using var m = Graph(); var ship = Place(m, native);
        void Goal() { if (native) NativeGoal(ship); else LegacyGoal(ship); }
        Goal(); Assert.Equal(-1, ship.IntendedNextWarpId);
        var warp = m.GetWarp(10)!;
        ship.Position = warp.Position + new Vector3(30, 0, 0);
        Assert.False(warp.SeenBySide(0));
        Assert.Same(warp, m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        Goal(); Assert.Equal(10, ship.IntendedNextWarpId);
        Assert.False(warp.SeenBySide(0));
        ship.Position = Vector3.Zero;
        Goal(); Assert.Equal(-1, ship.IntendedNextWarpId);
    }

    [Fact]
    public void Personal_scanner_obeys_rear_attenuation_occlusion_and_cluster_identity()
    {
        using var m = Graph(); var ship = Place(m); var warp = m.GetWarp(10)!;
        ship.Position = warp.Position + new Vector3(0, 0, -40);
        Assert.Null(m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        ship.Position = warp.Position + new Vector3(0, 0, 40);
        Assert.Same(warp, m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        { AsteroidId = 900, ClusterId = 0, Position = [warp.Position.X, 0, 20], Radius = 30, HitPoints = 100 });
        m.PlaceAsteroid(rock, m.Sectors[0]);
        Assert.Null(m.Sectors[0].FindPathToward(m.Sectors[3], ship));
        ship.Position = m.GetWarp(30)!.Position;
        m.GetWarp(20)!.SideVisibility.SetSideVisibility(0, true);
        m.GetWarp(40)!.SideVisibility.SetSideVisibility(0, true);
        Assert.Null(m.Sectors[0].FindPathToward(m.Sectors[3], ship)); // remote edge cannot use our scanner
    }

    [Fact]
    public void Partner_reveal_from_real_visibility_sweep_makes_reverse_route_usable_and_latches()
    {
        using var m = Graph(); var ship = Place(m);
        var warp = m.GetWarp(10)!;
        warp.SideVisibility.SetSideVisibility(0, true);
        Assert.False(warp.Partner()!.SeenBySide(0));
        m.Sectors[0].ResolveCrossSideVisibility();
        Assert.True(warp.Partner()!.SeenBySide(0));
        m.PlaceShip(ship, m.Sectors[3]);
        Assert.Same(warp.Partner(), m.Sectors[3].FindPathToward(m.Sectors[0], ship));
        m.Sectors[3].ResolveCrossSideVisibility();
        Assert.True(warp.Partner()!.SeenBySide(0));
        Assert.False(warp.Partner()!.GetCurrentEye(0));
    }
}
