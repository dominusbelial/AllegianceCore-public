using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>P985 — sender-time ordering, bounded storage, hard cuts, clock
/// mapping, allocation and the unchanged flag-off reckoner path.</summary>
public sealed class P985RemoteTimelineTests
{
    private static readonly Orientation Identity = new();

    [Fact]
    public void Late_samples_insert_by_server_time_and_duplicates_are_discarded()
    {
        var timeline = new RemoteShipTimeline();
        Assert.True(timeline.Feed(P(0), V(), Identity, 1.00, 1.01));
        Assert.True(timeline.Feed(P(10), V(), Identity, 1.10, 1.11));
        Assert.True(timeline.Feed(P(20), V(), Identity, 1.20, 1.21));
        Assert.True(timeline.Feed(P(5), V(), Identity, 1.05, 1.22));  // late, still in-window
        Assert.False(timeline.Feed(P(999), V(), Identity, 1.05, 1.23));
        Assert.Equal(4, timeline.SampleCount);

        double now = 1.075 + timeline.RenderDelay;
        Assert.True(timeline.TryGetPose(now, out var pose, out _));
        Assert.Equal(7.5f, pose.X, 4);
        Assert.True(timeline.Feed(P(15), V(), Identity, 1.15, 1.24));
        Assert.False(timeline.Feed(P(15), V(), Identity, 1.15, 1.25));
        Assert.Equal(5, timeline.SampleCount);
    }

    [Fact]
    public void Ring_is_bounded_and_expired_packets_never_return()
    {
        var timeline = new RemoteShipTimeline();
        for (int i = 0; i < 40; i++)
            Assert.True(timeline.Feed(P(i), V(), Identity, 10 + i * 0.05, 10 + i * 0.05));
        Assert.Equal(RemoteShipTimeline.History, timeline.SampleCount);
        Assert.False(timeline.Feed(P(-100), V(), Identity, 10.0, 12.1));
        Assert.Equal(RemoteShipTimeline.History, timeline.SampleCount);
    }

    [Fact]
    public void Stall_and_teleport_reset_history_instead_of_blending_across_a_cut()
    {
        var stalled = new RemoteShipTimeline();
        stalled.Feed(P(0), V(), Identity, 1, 1);
        stalled.Feed(P(10), V(), Identity, 1.1, 1.1);
        Assert.False(stalled.TryGetPose(3.1, out _, out _));
        Assert.Equal(0, stalled.SampleCount);

        var teleported = new RemoteShipTimeline();
        teleported.Feed(P(0), V(), Identity, 1, 1);
        teleported.Feed(P(10), V(), Identity, 1.1, 1.1);
        Assert.True(teleported.Feed(new Vector3(5000, 0, 0), V(), Identity, 1.2, 1.2));
        Assert.Equal(1, teleported.SampleCount);
        Assert.False(teleported.TryGetPose(1.2, out _, out _), "one post-cut sample cannot interpolate");
        Assert.False(teleported.Feed(P(15), V(), Identity, 1.15, 1.21), "late pre-cut pose must not bridge the teleport");
        Assert.Equal(1, teleported.SampleCount);
    }

    [Fact]
    public void Delay_is_smoothed_and_bounded_under_extreme_cadence_and_jitter()
    {
        var timeline = new RemoteShipTimeline();
        var random = new Random(985);
        float previous = timeline.RenderDelay;
        float largestStep = 0;
        for (int i = 0; i < 1000; i++)
        {
            double t = 1 + i * 0.05;
            timeline.Feed(P(i), V(), Identity, t, t + random.NextDouble() * 0.150);
            Assert.InRange(timeline.RenderDelay, RemoteShipTimeline.MinDelay, RemoteShipTimeline.MaxDelay);
            largestStep = MathF.Max(largestStep, MathF.Abs(timeline.RenderDelay - previous));
            previous = timeline.RenderDelay;
        }
        Assert.True(largestStep < 0.05f, $"unsmoothed delay step {largestStep}");
    }

    [Fact]
    public void Pose_queries_allocate_no_managed_bytes()
    {
        var timeline = new RemoteShipTimeline();
        timeline.Feed(P(0), V(), Identity, 1, 1);
        timeline.Feed(P(10), V(), Identity, 1.1, 1.1);
        for (int i = 0; i < 100; i++) Assert.True(timeline.TryGetPose(1.15, out _, out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Assert.True(timeline.TryGetPose(1.15, out _, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ClientWorld_maps_server_ticks_not_arrival_order()
    {
        double now = 1.0;
        var world = new ClientWorld { RemoteTimelineRemotes = true };
        world.NowSeconds = () => now;
        world.ObserveServerTime(1000, 1000);
        world.Apply(Update(5, 20, P(0)));
        now = 1.10;
        world.Apply(Update(5, 22, P(10)));
        now = 1.20;
        world.Apply(Update(5, 21, P(5))); // late server tick 1.05, arrives after tick 1.10

        var timeline = Assert.Single(world.Caches.Timelines.Values);
        Assert.Equal(3, timeline.SampleCount);
        double query = 1.075 + timeline.RenderDelay;
        Assert.True(world.TryGetTimelineRenderPose(5, query, out var pose, out _));
        Assert.Equal(7.5f, pose.X, 4);

        world.BeginNextMission();
        Assert.Empty(world.Caches.Timelines);
        Assert.False(world.TryGetTimelineRenderPose(5, query, out _, out _));
    }

    [Fact]
    public void Flag_off_allocates_no_timeline_and_preserves_dead_reckoner_bytes()
    {
        double now = 4;
        var world = new ClientWorld
        {
            NowSeconds = () => now,
            RemoteShipHullResolver = (_, _) => (ShipDeadReckoner.HullStats.FromHull(Hull), 0f),
        };
        var update = Update(7, 80, new Vector3(100, 20, -30));
        world.Apply(update);
        Assert.False(world.RemoteTimelineRemotes);
        Assert.Empty(world.Caches.Timelines);
        Assert.False(world.TryGetTimelineRenderPose(7, now, out _, out _));
        Assert.True(world.TryGetRenderPose(7, now + 0.2, out var actual, out var actualOri));

        var expectedReckoner = new ShipDeadReckoner();
        expectedReckoner.Seed(update.Ship, ShipDeadReckoner.HullStats.FromHull(Hull), 0, now);
        expectedReckoner.PoseAt(now + 0.2, out var expected, out var expectedOri);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedOri.GetForward(), actualOri.GetForward());
        Assert.Equal(expectedOri.GetUp(), actualOri.GetUp());
        Assert.Equal(0, world.Caches.CorrectionCount);
    }

    [Fact]
    public void Sector_departure_death_and_respawn_are_hard_cache_cuts()
    {
        double now = 1;
        var world = new ClientWorld { RemoteTimelineRemotes = true, NowSeconds = () => now };
        world.ObserveServerTime(1000, 1000);
        world.Apply(Update(5, 20, P(0)));
        world.Apply(Update(6, 20, P(0)));
        Assert.Equal(2, world.Caches.Timelines.Count);

        world.Apply(new ShipDelete { ShipId = 5, Sdr = ShipDelete.SdrLeftSector });
        world.Apply(new KillShip { ShipId = 6, IdCredit = -1, BDeathCredit = true });
        Assert.Empty(world.Caches.Timelines);

        now = 1.1;
        world.Apply(Update(5, 22, P(100)));
        var timeline = Assert.Single(world.Caches.Timelines.Values);
        Assert.Equal(1, timeline.SampleCount);
        Assert.False(world.TryGetTimelineRenderPose(5, now, out _, out _),
            "a respawn/re-entry must not interpolate from the retired life");
    }

    [Fact]
    public void Own_ship_never_gets_a_remote_timeline()
    {
        double now = 1;
        var world = new ClientWorld { RemoteTimelineRemotes = true, NowSeconds = () => now };
        world.ObserveServerTime(1000, 1000);
        world.Apply(new SetCluster { SectorId = 1, Ship = Update(9, 20, P(0)).Ship });
        Assert.Equal((short)9, world.MyShipId);
        Assert.Empty(world.Caches.Timelines);
        Assert.False(world.TryGetTimelineRenderPose(9, now, out _, out _));
    }

    [Fact]
    public void Full_ring_rejects_a_late_packet_older_than_every_retained_sample()
    {
        var timeline = new RemoteShipTimeline();
        for (int i = 0; i < RemoteShipTimeline.History; i++)
            Assert.True(timeline.Feed(P(i), V(), Identity, 10 + i * 0.1, 10 + i * 0.1));
        // Older than slot 0 but inside the age window: it must not evict a newer sample.
        Assert.False(timeline.Feed(P(-1), V(), Identity, 9.95, 10.75));
        Assert.True(timeline.TryGetPose(10.65 + timeline.RenderDelay, out var pose, out _));
        Assert.Equal(6.5f, pose.X, 3);
    }

    [Fact]
    public void Loop_ticks_map_on_the_local_clock_independent_of_time_dilation()
    {
        // The wire Time slot is the server LOOP tick (20/s wall), while PING carries
        // sim time that auto time dilation slows. Ten seconds at TiDi 0.5 put the
        // sim clock 5 s behind tick/20; the timeline must still interpolate.
        double now = 100;
        var world = new ClientWorld { RemoteTimelineRemotes = true, NowSeconds = () => now };
        world.ObserveServerTime(1000, 100_000);
        uint tick = 200;                        // 10 s of loop ticks since the sim epoch
        for (int i = 0; i < 6; i++)
        {
            world.ObserveServerTime((uint)(1000 + i * 50 * 0.5), (long)(now * 1000)); // sim at half rate
            world.Apply(Update(5, tick, P(i * 10)));
            tick += 2;                          // 10 Hz updates
            now += 0.1;
        }
        var timeline = Assert.Single(world.Caches.Timelines.Values);
        Assert.Equal(6, timeline.SampleCount);
        // Render 0.25 s behind the newest arrival's playout time: between samples 2 and 3.
        double query = 100 + 0.25 + timeline.RenderDelay;
        Assert.True(world.TryGetTimelineRenderPose(5, query, out var pose, out _));
        Assert.Equal(25f, pose.X, 2);
    }

    [Fact]
    public void Timeline_clock_tracks_the_least_delayed_arrival_and_resets_on_a_step()
    {
        var clock = new RemoteTimelineClock();
        Assert.Equal(10.0, clock.Map(200, 10.0, out bool s0), 9);   // offset 0
        Assert.False(s0);
        Assert.Equal(10.1, clock.Map(202, 10.25, out _), 9);        // late arrival keeps the min offset
        Assert.Equal(0.0, clock.Offset, 9);
        // A tick counter reset (server restart) steps the offset: histories must clear.
        clock.Map(20, 10.4, out bool stepped);
        Assert.True(stepped);
        Assert.Equal(10.4 - 1.0, clock.Offset, 9);
    }

    private static Vector3 P(float x) => new(x, 0, 0);
    private static Vector3 V() => new(100, 0, 0);

    private static SingleShipUpdate Update(short id, uint tick, Vector3 position)
    {
        var ship = new Ship(id, Hull, position, V(), Identity);
        return new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.FromModel(id, ship, tick) };
    }

    private static readonly Allegiance.Core.DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f], TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 985, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 511f, HardpointOffset: 580,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);
}
