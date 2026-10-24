using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P786 (PAR-5) — the client-state plumbing the HUD wire-data consumers read:
/// the probe/mine EXPORT <c>m_time0</c> anchors (the radar lifetime fill) and
/// the own dispenser-deploy counter (the arming-clock fire event). Pure domain
/// tests fed through <see cref="ClientWorld.Apply"/>. These fail on master:
/// before P786 the entity dropped the export's Time0 and no deploy counter
/// existed.
/// </summary>
public class P786ClientWorldWireTests
{
    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static ClientWorld WorldWithMyShip(short myShipId = 7)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        return world;
    }

    [Fact]
    public void Probe_export_keeps_the_wire_time0()
    {
        // probeigc.cpp:542 — the export carries m_time0; probeigc.h:271-279 is
        // the GetTimeFraction that reads it. The entity must preserve it in
        // server seconds (the wire uint is a server millisecond tick).
        var world = new ClientWorld();
        world.Apply(new ExportProbe
        {
            Probe = new DataProbeExport(
                P0: new Vector3(10f, 0f, 0f), Time0: 123_456u, ProbeId: 3, ExportF: true,
                ProbetypeId: 2, SideId: 1, ClusterId: 5, ShipId: 7, OtTarget: 0, OidTarget: 0,
                CreateNow: true),
        });
        Assert.Equal(123.456, world.Probes[3].Time0ServerSeconds, 3);
    }

    [Fact]
    public void Mine_export_keeps_the_wire_time0()
    {
        // mineigc.cpp:167 — the export carries m_time0; mineigc.h:183-191 is
        // the GetTimeFraction that reads it.
        var world = new ClientWorld();
        world.Apply(new ExportMine
        {
            Mine = new DataMineExport(
                P0: new Vector3(0f, 0f, 0f), Time0: 61_250u, MineId: 4, ExportF: true,
                ClusterId: 5, MinetypeId: 6, LauncherId: 7, SideId: 1, Fraction: 240,
                CreateNow: true),
        });
        Assert.Equal(61.250, world.Mines[4].Time0ServerSeconds, 3);
    }

    [Fact]
    public void Own_dispenser_deploys_count_but_foreign_ones_do_not()
    {
        // The own deploy is the native client's launcher-fire clock event for
        // the dispenser (magazineigc.cpp:100-121 semantics); another ship's
        // minefield must not reset the own arming bar.
        var world = WorldWithMyShip();
        Assert.Equal(0, world.OwnDispenserDeployed);

        world.Apply(new ExportMine
        {
            Mine = new DataMineExport(new Vector3(), 1000u, 4, true, 5, 6, 7, 1, 240, true),
        });
        Assert.Equal(1, world.OwnDispenserDeployed);

        world.Apply(new ExportProbe
        {
            Probe = new DataProbeExport(new Vector3(), 2000u, 3, true, 2, 1, 5, 7, 0, 0, true),
        });
        Assert.Equal(2, world.OwnDispenserDeployed);

        world.Apply(new ExportMine
        {
            Mine = new DataMineExport(new Vector3(), 3000u, 9, true, 5, 6, 8, 1, 240, true),
        });
        Assert.Equal(2, world.OwnDispenserDeployed);   // launcher 8 ≠ own 7
    }
}
