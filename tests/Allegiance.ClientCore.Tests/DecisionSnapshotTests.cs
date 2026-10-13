using System;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P636 — the lock-free decision snapshot published by a <see cref="ClientWorld"/>'s
/// receive thread and read by the bot decision loop. These pin the immutability /
/// publish-swap contract that lets a decision pass read the latest world state WITHOUT
/// taking the world gate (the P632/P633 transit-collapse decoupling):
///   • PublishDecisionSnapshot rebuilds a fresh, sorted-by-ShipId ships array and
///     atomically swaps the published reference (Volatile.Read in Decision);
///   • a previously captured snapshot is IMMUTABLE — later publishes must not alter it;
///   • Version bumps monotonically and each publish swaps a fresh Ships array;
///   • before seating / after Reset the published snapshot is the empty sentinel.
/// Failing-first: the Decision/PublishDecisionSnapshot/DecisionSnapshot surface did not
/// exist before P636, so these could not compile (the P633-style "new API" red), and
/// the immutability/throttle/empty assertions were newly-added behavior guards.
/// </summary>
// 2026-10-13: the bot decision snapshot now defaults OFF (87d3501b). These tests exercise the
// snapshot itself, so they enable it explicitly and restore it; the shared collection keeps
// them from racing other tests on the static flag (GitHub CI ran them in a different order).
[Collection("DecisionSnapshotFlag")]
public class DecisionSnapshotTests : IDisposable
{
    private readonly bool _savedSnapshotFlag = ClientWorld.DecisionSnapshotEnabled;
    public DecisionSnapshotTests() => ClientWorld.DecisionSnapshotEnabled = true;
    public void Dispose() => ClientWorld.DecisionSnapshotEnabled = _savedSnapshotFlag;

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 999, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Orientation Yawed(float theta)
    {
        var o = Orientation.Identity;
        o.Yaw(theta);
        return o;
    }

    /// <summary>A world with MY ship at sector 1 (SetCluster assigns MyShipId).</summary>
    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), new Vector3(10f, 20f, 30f), Vector3.Zero, Yawed(0.1f)),
                1u),
            Cookie = 0x1,
        });
        return world;
    }

    /// <summary>Add a remote ship with a real identity (ExportShip) + pose (HeavyShipsUpdate).</summary>
    private static void AddRemote(ClientWorld world, short id, short side, short hull, Vector3 pos)
    {
        world.Apply(new ExportShip { ShipId = id, HullId = hull, SideId = side, CharacterName = $"s{id}", PilotType = ExportShip.PilotTypePlayer });
        world.Apply(HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(100u, Vector3.Zero, new[]
            {
                new Ship(id, MakeHull(), pos, Vector3.Zero, Orientation.Identity),
            }).Encode()));
    }

    /// <summary>Before seating the published snapshot is the empty sentinel.</summary>
    [Fact]
    public void EmptyBeforeSeating()
    {
        var world = new ClientWorld();
        Assert.Same(DecisionSnapshot.Empty, world.Decision);
        Assert.Null(world.Decision.MyShipId);
        Assert.Empty(world.Decision.Ships);
    }

    /// <summary>Publish reflects applied ships (mine + a remote), sorted, with MyCluster set.</summary>
    [Fact]
    public void PublishedSnapshotReflectsAppliedWorld_AndSetsMyCluster()
    {
        const short me = 10;
        var world = WorldWithMyShip(me);
        AddRemote(world, 50, side: 7, hull: 800, pos: new Vector3(500f, 0f, 600f));

        world.RequestDecisionRefresh();
        var snap = world.PublishDecisionSnapshot();

        Assert.Equal(me, snap.MyShipId);
        Assert.Equal((short)1, snap.MyCluster);
        Assert.Equal(2, snap.Ships.Length);
        // sorted ascending by ShipId
        Assert.True(snap.Ships[0].ShipId <= snap.Ships[1].ShipId);
        // my ship present with a pose
        Assert.True(DecisionSnapshot.TryFind(snap.Ships, me, out var mine));
        Assert.True(mine.HasLatest);
        Assert.InRange(mine.Position.X, 8f, 12f);
        // remote present with side + hull + pose (compact-position quantised)
        Assert.True(DecisionSnapshot.TryFind(snap.Ships, 50, out var remote));
        Assert.Equal((short)7, remote.SideId);
        Assert.Equal((short)800, remote.HullId);
        Assert.InRange(remote.Position.Z, 590f, 610f);
    }

    /// <summary>An already-captured snapshot is IMMUTABLE: later applies + publishes
    /// must not mutate it (no torn reads for a reader holding the old snapshot).</summary>
    [Fact]
    public void CapturedSnapshotIsImmutableAcrossLaterPublishes()
    {
        const short me = 20;
        var world = WorldWithMyShip(me);
        AddRemote(world, 60, side: 9, hull: 801, pos: new Vector3(1f, 0f, 0f));

        world.RequestDecisionRefresh();
        var first = world.PublishDecisionSnapshot();
        long firstVersion = first.Version;
        int firstCount = first.Ships.Length;

        // later updates + publishes (each requests a fresh snapshot first)
        AddRemote(world, 70, side: 9, hull: 802, pos: new Vector3(2f, 0f, 0f));
        world.RequestDecisionRefresh();
        world.PublishDecisionSnapshot();
        world.RequestDecisionRefresh();
        world.PublishDecisionSnapshot();

        // the earlier snapshot object is untouched
        Assert.Equal(firstVersion, first.Version);
        Assert.Equal(firstCount, first.Ships.Length);
    }

    /// <summary>Each publish bumps Version and swaps a fresh Ships array (double-buffer
    /// style publish: readers holding the old array are always safe).</summary>
    [Fact]
    public void PublishBumpsVersion_AndSwapsFreshShipsArray()
    {
        var world = WorldWithMyShip(30);
        world.RequestDecisionRefresh();
        var a = world.PublishDecisionSnapshot();
        Thread.Sleep(DecisionSnapPublishIntervalMs + 10);   // clear the throttle window
        // a world CHANGE between publishes is required under lazy publish
        AddRemote(world, 55, side: 9, hull: 804, pos: new Vector3(4f, 0f, 0f));
        world.RequestDecisionRefresh();
        var b = world.PublishDecisionSnapshot();
        Assert.True(b.Version > a.Version);
        Assert.NotSame(a.Ships, b.Ships);
        Assert.True(b.Version > 0);
    }

    /// <summary>P636 — the publish throttle interval (ms) mirrored from ClientWorld
    /// so tests can wait past it deterministically.</summary>
    private const int DecisionSnapPublishIntervalMs = 40;

    /// <summary>Publishing twice within the throttle window returns the SAME snapshot
    /// (no per-batch rebuild — bounds the publish cost to ~25 Hz).</summary>
    [Fact]
    public void PublishThrottlesWithinInterval()
    {
        var world = WorldWithMyShip(40);
        world.RequestDecisionRefresh();
        var a = world.PublishDecisionSnapshot();
        var b = world.PublishDecisionSnapshot();   // no new request → lazy publish does nothing
        Assert.Same(a, b);
    }

    /// <summary>Reset returns the published snapshot to the empty sentinel.</summary>
    [Fact]
    public void ResetClearsPublishedSnapshot()
    {
        var world = WorldWithMyShip(41);
        world.RequestDecisionRefresh();
        world.PublishDecisionSnapshot();
        Assert.NotSame(DecisionSnapshot.Empty, world.Decision);
        world.Reset();
        Assert.Same(DecisionSnapshot.Empty, world.Decision);
    }

    /// <summary>
    /// Lock-freedom invariant under concurrent publish: a reader sampling the snapshot
    /// while a writer applies+publishes must always observe a self-consistent,
    /// sorted, non-negative ships array (never a torn half-built snapshot — guaranteed
    /// by immutability of the published object). Bounded iterations keep it non-flaky.
    /// </summary>
    [Fact]
    public void ConcurrentPublish_NeverYieldsTornSnapshot()
    {
        var world = WorldWithMyShip(42);
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            short n = 0;
            while (!stop.IsCancellationRequested)
            {
                AddRemote(world, (short)(100 + (n % 5)), side: 3, hull: 810, pos: new Vector3(n, 0f, 0f));
                world.RequestDecisionRefresh();
                world.PublishDecisionSnapshot();
                n++;
            }
        })
        { IsBackground = true };

        var reader = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var s = world.Decision;
                if (s.Ships.Length > 0)
                {
                    for (int i = 1; i < s.Ships.Length; i++)
                        Assert.True(s.Ships[i - 1].ShipId <= s.Ships[i].ShipId, "snapshot ships must stay sorted");
                }
            }
        })
        { IsBackground = true };

        writer.Start();
        reader.Start();
        Thread.Sleep(150);
        stop.Cancel();
        writer.Join(2000);
        reader.Join(2000);
    }
}
