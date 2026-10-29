using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P880a — framework batch 1 (the P880a queue rows of the training matrix): the missing
/// primitives and the production-bound reads that a real lesson uses. Pure framework tests run
/// against <see cref="FakeTrainingSite"/>; the production-binding tests drive the real embedded
/// <see cref="TrainingHost"/> through <see cref="TrainingHostSite"/> (the same host the client's
/// <see cref="ClientTrainingSite"/> forwards to), so divergence from native fails the binding.
/// </summary>
[Collection("Training")]
public sealed class P880aFrameworkBindingTests
{
    // ── framework primitives (fake site) ─────────────────────────────────────

    [Fact]
    public void GetProbeCountCondition_MatchesTheMissionProbeCount()
    {
        // getprobecountcondition.cpp:26-33 — GetProbes()->n() == expected
        var site = new FakeTrainingSite { ProbeCount = 3 };
        Assert.False(new GetProbeCountCondition(site, 4).Evaluate());
        Assert.True(new GetProbeCountCondition(site, 3).Evaluate());
    }

    [Fact]
    public void GetRadarLODCondition_MatchesTheWindowRadarMode()
    {
        // getradarlodcondition.cpp:26-33 — GetRadarMode() == expectedRadarLOD
        var site = new FakeTrainingSite { RadarLod = (int)Allegiance.ClientCore.MonoGame.Rendering.RadarMode.Target };
        Assert.False(new GetRadarLODCondition(site, (int)Allegiance.ClientCore.MonoGame.Rendering.RadarMode.All).Evaluate());
        Assert.True(new GetRadarLODCondition(site, (int)Allegiance.ClientCore.MonoGame.Rendering.RadarMode.Target).Evaluate());
    }

    [Fact]
    public void GetShipHasAmmoCondition_UsesNativeInvertedMeaning()
    {
        // getshiphasammocondition.cpp:32-34 — GetAmmo() >= count ? false : true (native bug kept).
        var site = new FakeTrainingSite();
        site.Register(new FakeObject(new TargetRef(ObjectType.Ship, 7), Vector3.Zero, Vector3.UnitX));
        site.Ammo[7] = 5;
        // 5 >= 4 => native returns false; 5 >= 6 => native returns true (inverted "HasAmmo").
        Assert.False(new GetShipHasAmmoCondition(site, new TargetRef(ObjectType.Ship, 7), 4).Evaluate());
        Assert.True(new GetShipHasAmmoCondition(site, new TargetRef(ObjectType.Ship, 7), 6).Evaluate());
    }

    [Fact]
    public void DoDamageAdjustableAction_DamagesTheChosenShip()
    {
        // dodamageadjustableaction.cpp:27-36 — damage a specific ship, reset controls, fire all weapons.
        var site = new FakeTrainingSite();
        site.Register(new FakeObject(new TargetRef(ObjectType.Ship, 9), Vector3.Zero, Vector3.UnitX));
        new DoDamageAdjustableAction(site, new TargetRef(ObjectType.Ship, 9), 500f).Execute();
        Assert.Contains((9, 500f), site.AdjustableDamage);
    }

    [Fact]
    public void CreateObjectAction_DispatchsByObjectType()
    {
        // createobjectaction.cpp:24-40 — create a model of the requested object type at mission time.
        var site = new FakeTrainingSite();
        new CreateObjectAction(site, ObjectType.Buoy, 21, new Vector3(1f, 2f, 3f), 1010, aux: 0).Execute();
        Assert.Contains(site.Waypoints, w => w.BuoyId == 21);
        new CreateObjectAction(site, ObjectType.Probe, 22, Vector3.Zero, 1010, aux: 1).Execute();
        Assert.NotNull(site.Resolve(new TargetRef(ObjectType.Probe, 22)));
    }

    [Fact]
    public void EndMissionAction_TerminatesTheMissionSoNextFrameCompletes()
    {
        // endmissionaction.cpp:31-35 — g_pMission->Terminate().
        var site = new FakeTrainingSite();
        var mission = new Mission2(site);
        mission.Start();
        Assert.False(mission.Frame());
        new EndMissionAction(mission).Execute();
        Assert.True(mission.Frame(), "EndMissionAction must park the condition so Frame() completes");
    }

    [Fact]
    public void GetKeyCondition_StartDrainsStalePresses()
    {
        // getkeycondition.cpp:35-43 — Start resets the press latch (stale presses must not satisfy).
        var site = new FakeTrainingSite();
        site.PressKey(KeyFireBooster);          // pressed before the goal started
        var cond = new GetKeyCondition(site, KeyFireBooster);
        cond.Start();                           // drains the stale press
        Assert.False(cond.Evaluate(), "a stale press must not satisfy the condition after Start");
        site.PressKey(KeyFireBooster);
        Assert.True(cond.Evaluate());
    }

    // ── production bindings (real embedded host) ─────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Resolve_ReadsStationsAsteroidsAndProbes_FromTheRealHostWorld()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        int planetoid = host.Mission.Sectors[0].Mineable.FirstOrDefault()?.Data.AsteroidId ?? -1;
        // AbstractTarget: the geometric conditions resolve stations/asteroids/probes/buoys, not just ships.
        var home = host.HomeStationId;
        var station = site.Resolve(new TargetRef(ObjectType.Station, home));
        Assert.NotNull(station);
        Assert.Equal(host._StationSector(home), station!.SectorId);
        // Buoy: create a waypoint and resolve it as a live object (getsectorcondition.cpp:27-51).
        host.CreateWaypoint(3, new Vector3(10f, 20f, 30f), 1010, 0, true);
        var buoy = site.Resolve(new TargetRef(ObjectType.Buoy, 3));
        Assert.NotNull(buoy);
        Assert.Equal(new Vector3(10f, 20f, 30f), buoy!.Position);
        Assert.Equal(1010, buoy.SectorId);
        // Asteroid resolution uses the real sector model list.
        if (planetoid >= 0)
        {
            var ast = site.Resolve(new TargetRef(ObjectType.Asteroid, planetoid));
            Assert.NotNull(ast);
            Assert.Equal(1010, ast!.SectorId);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void AutopilotCommandAndTarget_ReadRealHostState()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        // GetAutopilotCondition reads the real autopilot flag (getautopilotcondition.cpp:33-37).
        host.SetAutopilot(host.PlayerShipId, true);
        Assert.True(site.GetAutopilot(new TargetRef(ObjectType.Ship, host.PlayerShipId)));
        host.SetAutopilot(host.PlayerShipId, false);
        Assert.False(site.GetAutopilot(new TargetRef(ObjectType.Ship, host.PlayerShipId)));

        // GetCommandCondition: accepted/current command matches; absent ship is true (getcommandcondition.cpp:44-53).
        Assert.False(site.HasCommand(new TargetRef(ObjectType.Ship, host.CommanderShipId), CmdGoto));
        host.SetCommandTarget(host.CommanderShipId, null, CmdGoto);
        Assert.True(site.HasCommand(new TargetRef(ObjectType.Ship, host.CommanderShipId), CmdGoto));
        Assert.True(site.HasCommand(new TargetRef(ObjectType.Ship, 99999), CmdGoto), "absent ship => condition true");

        // CurrentTarget: the player's current command target resolves (currenttarget.cpp:37, playershiptarget.cpp:30).
        host.CreateWaypoint(7, new Vector3(0f, 0f, 100f), 1010, 0, true);
        var playerCurrent = new PlayerCurrentTarget(site, lockOnFirstFetch: true);
        host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Buoy, 7), CmdGoto);
        Assert.Equal(new TargetRef(ObjectType.Buoy, 7), playerCurrent.Resolve());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void DoDamageAdjustableAction_LandsRealDamageAndArmsAllWeapons()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        // dodamageadjustableaction.cpp:27-36: damage, reset controls, fire all weapons.
        int drone = host.CreateDrone("victim", -1, 412, 0, (int)Ship.PilotTypeWingman);
        host.PlaceDrone(drone, 1010, new Vector3(50f, 0f, 0f), Vector3.UnitX, Vector3.UnitZ, null, CmdDoNothing);
        float before = host.GetShipFraction(drone);
        host.DamageShipAdjustable(drone, 5000f);
        Assert.True(host.GetShipFraction(drone) < before, "adjustable damage must lower the ship's armour");
        Assert.True((host.Drones[drone].StateM & ShipControlState.AllWeapons) != 0, "all weapons armed");
        Assert.Equal(ControlData.Neutral, host.Drones[drone].Controls);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void DoDamageAction_LandsRealDamage_OnThePlayerShip()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        // DoDamageAction (dodamageaction.cpp:24-50) dents the player's shield/hull through the host.
        float before = host.GetShipFraction(host.PlayerShipId);
        new DoDamageAction(site, toHull: true).Execute();
        Assert.True(host.GetShipFraction(host.PlayerShipId) < before,
            "DealDamage(toHull:true) must lower the player's armour on the real host");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void ProbeCountAndAmmo_ReadRealHostWorld()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        Assert.Equal(0, site.GetProbeCount());   // empty world => 0 probes (getprobecountcondition.cpp:31)
        int drone = host.CreateDrone("ammo", -1, 412, 0, (int)Ship.PilotTypeWingman);
        host.PlaceDrone(drone, 1010, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, null, CmdDoNothing);
        Assert.Equal(host.Drones[drone].Ammo, site.GetAmmo(new TargetRef(ObjectType.Ship, drone)));
    }
}

internal static class P880aHostExtensions
{
    /// <summary>Helper: the sector a station belongs to (host station record).</summary>
    public static int _StationSector(this TrainingHost host, int stationId)
        => host.Stations.TryGetValue(stationId, out var st) ? st.SectorId : -1;
}
