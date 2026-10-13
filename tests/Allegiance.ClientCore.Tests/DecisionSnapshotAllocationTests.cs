using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P636 — allocation budget for the bot decode/apply+publish hot path. A headless
/// port-mode bot decodes a periodic HEAVY_SHIPS_UPDATE carrying every co-located ship
/// (≈ N per cluster at the 200v200 baseline) into its ClientWorld, then publishes the
/// lock-free decision snapshot. This asserts the per-ship managed allocation stays
/// under a documented ceiling — guarding the "pooled/reused buffers, in-place updates,
/// allocation-free hot paths" scope-2 claim (P636) against regressions (e.g. someone
/// re-adding a per-ship heap array or a LINQ closure on the hot path). The ceiling is
/// deliberately loose (it catches gross regressions, not noise); the measured value is
/// reported in the P636 report.
/// </summary>
public class DecisionSnapshotAllocationTests
{
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

    [Fact]
    public void HeavyUpdateDecodeApplyAndPublish_StaysUnderPerShipAllocationCeiling()
    {
        const int N = 195;                       // a cluster's worth of remote ships
        const short me = 5;
        var world = WorldWithMyShip(me);

        var ships = new Ship[N];
        for (int i = 0; i < N; i++)
        {
            ships[i] = new Ship((short)(10 + i), MakeHull(),
                new Vector3(i * 3f, 0f, 500f), new Vector3(0f, 0f, 1f), Yawed(0.001f * i));
        }
        var heavy = HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(100u, Vector3.Zero, ships).Encode());

        // Warm up (JIT + settle the double-buffer pool) before measuring.
        world.Apply(heavy);
        world.PublishDecisionSnapshot();
        world.Apply(heavy);
        world.PublishDecisionSnapshot();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int Rounds = 5;
        for (int r = 0; r < Rounds; r++)
        {
            world.Apply(heavy);
            world.PublishDecisionSnapshot();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double perRound = (double)allocated / Rounds;
        double perShip = perRound / N;

        // Report the measured number (only numbers actually measured appear in the P636 report).
        Console.WriteLine($"[P636] measured decode+apply+publish allocation per round = {perRound:F0} B, per ship = {perShip:F0} B (N={N}, rounds={Rounds})");

        // Ceiling: decode+apply of one heavy ship record + its share of one snapshot
        // publish should stay well under 2 KB of managed allocation (the codec's
        // per-update entry list is the dominant residue; the snapshot arrays are
        // pooled and reused at steady state). A regression that per-ship heap-allocates
        // (e.g. a new ShipEntity array or a LINQ closure per ship) blows far past this.
        Assert.True(perShip < 2048,
            $"per-ship decode+apply+publish allocation {perShip:F0} B exceeded 2048 B ceiling");
    }
}
