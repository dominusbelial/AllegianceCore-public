using System;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P636d — the LAZY decision-snapshot publish contract, and the fix for the
/// receive-thread PUBLISH cost at ~390 bots (this task). Prior to P636d the
/// receive thread republished a full ~400-ship snapshot on a fixed ~25 Hz timer
/// REGARDLESS of the A/B switch — and because the switch only reverted the
/// decision READ path, `off` still published at full rate, so turning the
/// snapshot "off" did not stop the receive-thread starvation (measured at
/// 195v195 on this host: OFF ≈ ON, both ~3.3 cores and ~140 ms decision-pass
/// p95). These tests pin the new contract:
///   • no publish without an explicit decision-loop request (REQUEST-gated);
///   • publish skips when the world is unchanged since the last publish
///     (skip-unchanged);
///   • `off` performs ZERO publishes (the switch now stops the WRITE too);
///   • the lock-free read and P636c aim freshness are preserved.
/// Failing-first: every publish path below is a no-op on the old fixed-timer
/// code for a different reason — the new API surface (RequestDecisionRefresh,
/// SnapshotPublishCount, DecisionSnapshotEnabled-as-a-property) does not exist on
/// HEAD, so these tests could not even compile (red); with the P636d fix they
/// compile and pass (green).
/// </summary>
// 2026-10-13: the bot decision snapshot now defaults OFF (it cost combat at 200v200 on the
// real rig, clash30/32 vs clash33). These tests exercise the snapshot itself, so they enable it
// explicitly and restore the previous value; one collection keeps them from racing on the flag.
[Collection("DecisionSnapshotFlag")]
public class P636dSnapshotPublishCostTests : IDisposable
{
    private readonly bool _savedSnapshotFlag = ClientWorld.DecisionSnapshotEnabled;
    public P636dSnapshotPublishCostTests() => ClientWorld.DecisionSnapshotEnabled = true;
    public void Dispose() => ClientWorld.DecisionSnapshotEnabled = _savedSnapshotFlag;

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 777, SuccessorHullId: -1,
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

    private static void AddRemote(ClientWorld world, short id, short side, short hull, Vector3 pos)
    {
        world.Apply(new ExportShip { ShipId = id, HullId = hull, SideId = side, CharacterName = $"s{id}", PilotType = ExportShip.PilotTypePlayer });
        world.Apply(HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(100u, Vector3.Zero, new[]
            {
                new Ship(id, MakeHull(), pos, Vector3.Zero, Orientation.Identity),
            }).Encode()));
    }

    /// <summary>LAZY: applying world changes and calling PublishDecisionSnapshot
    /// with NO pending request performs ZERO rebuilds (returns the current snapshot,
    /// Version unchanged, publish counter 0). On the old fixed-timer code every
    /// call rebuilt and bumped Version, so this is the failing-first red.</summary>
    [Fact]
    public void NoPublish_WithoutARequest()
    {
        var world = WorldWithMyShip(10);
        AddRemote(world, 50, side: 7, hull: 880, pos: new Vector3(500f, 0f, 600f));

        var snap = world.PublishDecisionSnapshot();   // no request → no-op
        Assert.Same(DecisionSnapshot.Empty, world.Decision);
        Assert.Equal(0L, snap.Version);
        Assert.Equal(0L, world.SnapshotPublishCount);
    }

    /// <summary>LAZY: after a request-driven publish, a SECOND request with NO
    /// world change returns the SAME snapshot (Version unchanged, no extra
    /// rebuild) — skip-unchanged. Failing-first: on the fixed-timer code the
    /// second call (past the throttle) rebuilt and bumped Version.</summary>
    [Fact]
    public void Request_WithUnchangedWorld_SkipsRebuild()
    {
        var world = WorldWithMyShip(20);
        AddRemote(world, 60, side: 9, hull: 881, pos: new Vector3(1f, 0f, 0f));

        world.RequestDecisionRefresh();
        var first = world.PublishDecisionSnapshot();
        Assert.True(first.Version > 0);
        long firstCount = world.SnapshotPublishCount;

        Thread.Sleep(60);                             // clear the defensive interval floor

        world.RequestDecisionRefresh();               // request again, world UNCHANGED
        var second = world.PublishDecisionSnapshot();
        Assert.Same(first, second);                   // no rebuild
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(firstCount, world.SnapshotPublishCount);
    }

    /// <summary>LAZY: a request WITH a world change DOES rebuild and bumps the
    /// Version (the happy path is preserved).</summary>
    [Fact]
    public void Request_WithChangedWorld_Rebuilds()
    {
        var world = WorldWithMyShip(30);
        world.RequestDecisionRefresh();
        var a = world.PublishDecisionSnapshot();

        Thread.Sleep(60);
        AddRemote(world, 70, side: 9, hull: 882, pos: new Vector3(2f, 0f, 0f));
        world.RequestDecisionRefresh();
        var b = world.PublishDecisionSnapshot();

        Assert.True(b.Version > a.Version);
        Assert.Equal(2, b.Ships.Length);
    }

    /// <summary>OFF mode performs ZERO publishes: with the snapshot disabled,
    /// even a pending request + world change produces no rebuild (returns Empty,
    /// publish counter 0). Failing-first: on HEAD the publish ran unconditionally
    /// (the switch only touched the read side), so a rebuild always happened; and
    /// the API surface here (DecisionSnapshotEnabled as a settable property +
    /// SnapshotPublishCount) did not exist at all (compile-red).</summary>
    [Fact]
    public void OffMode_PerformsZeroPublishes()
    {
        var world = WorldWithMyShip(40);
        AddRemote(world, 80, side: 9, hull: 883, pos: new Vector3(3f, 0f, 0f));
        bool saved = ClientWorld.DecisionSnapshotEnabled;
        try
        {
            ClientWorld.DecisionSnapshotEnabled = false;
            world.RequestDecisionRefresh();
            var snap = world.PublishDecisionSnapshot();
            Assert.Same(DecisionSnapshot.Empty, world.Decision);
            Assert.Equal(0L, snap.Version);
            Assert.Equal(0L, world.SnapshotPublishCount);
        }
        finally
        {
            ClientWorld.DecisionSnapshotEnabled = saved;
        }
    }

    /// <summary>Publish stays fresh while the active level is HELD: once requested,
    /// world changes keep republishing at the ≤~25 Hz cadence (no per-call request
    /// needed) so snapshots stay fresh for aim; revoking the request stops publish
    /// entirely (idle → zero). Guards against both a return to the unconditional
    /// fixed-timer publish AND a freshness-killing once-per-request consume.</summary>
    [Fact]
    public void ActiveLevel_PublishesAtCadence_WhileHeld_AndCancelStopsIt()
    {
        var world = WorldWithMyShip(45);
        world.RequestDecisionRefresh();
        world.PublishDecisionSnapshot();                       // request held → publishes
        Assert.Equal(1L, world.SnapshotPublishCount);

        // while the active level is HELD, a world change republishes at cadence
        AddRemote(world, 911, side: 9, hull: 886, pos: new Vector3(1f, 0f, 0f));
        Thread.Sleep(60);                                      // past the interval floor
        world.PublishDecisionSnapshot();                       // no new request needed (sticky)
        Assert.Equal(2L, world.SnapshotPublishCount);

        // revoke → zero further publishes even with changes (idle → zero)
        world.CancelDecisionRefresh();
        AddRemote(world, 912, side: 9, hull: 887, pos: new Vector3(2f, 0f, 0f));
        Thread.Sleep(60);
        world.PublishDecisionSnapshot();
        Assert.Equal(2L, world.SnapshotPublishCount);          // unchanged after cancel
        Assert.Same(world.Decision, world.PublishDecisionSnapshot());
    }

    /// <summary>P636c aim freshness is preserved under lazy publish: a freshly
    /// published snapshot carries a monotonic PublishedTick64 and the pure
    /// dead-reckon still extrapolates a frozen pose forward (the no-gate read and
    /// the aim-critical freshness survive the P636d publish change unchanged).</summary>
    [Fact]
    public void FreshSnapshot_KeepsPublishStamp_AndDeadReckon()
    {
        var world = WorldWithMyShip(1);
        AddRemote(world, 2, side: 8, hull: 885, pos: new Vector3(100f, 0f, 0f));
        world.RequestDecisionRefresh();
        var snap = world.PublishDecisionSnapshot();

        Assert.True(snap.PublishedTick64 > 0);
        // dead-reckon moves a frozen target by vel × age, uniformly (P636c)
        var target = snap.Ships.FirstOrDefault(s => s.ShipId == 2);
        Assert.True(target.HasLatest);
        var dr = DecisionSnapshot.DeadReckonPosition(target, 1000.0);   // 1 s later
        Assert.True(dr.X >= target.PosX - 1e-3);                        // +vel × 1 s (vel.z here)
        Assert.Equal(target.PosX, dr.X);
    }
}
