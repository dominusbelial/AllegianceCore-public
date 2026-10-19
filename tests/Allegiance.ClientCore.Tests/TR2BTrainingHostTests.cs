using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// TR-2b — proves the embedded-server training host is real (not the logged seams): it boots the
/// actual <see cref="TrainingHost"/> (real Server + PCore014 core + training_1 map, one Iron-League
/// side), runs Mission 2 (Basic Flight) to completion against that host with inputs scripted through
/// the real input path (<see cref="TrainingMission.RecordKeyPress"/> + the applied ship controls),
/// and asserts every previously-seamed creation/command/autopilot action now lands a real effect on
/// the host's mission.
///
/// Driven like <see cref="TrainingMission2Tests"/> (phased, so the native "skip a goal whose success
/// condition is already true" rule — goal.cpp:40 — cannot elide a prompt we are observing), but the
/// site reads/writes the <b>real</b> embedded-host universe and sim instead of a fake.
/// </summary>
[Collection("LiveNetwork")]
public sealed class TrainingHostMission2Tests
{
    private const int KeySpace = KeyFireBooster;      // TK_FireBooster = the SPACEBAR key (mission2.cpp)
    private const int Afterburner = AfterburnerButton; // 512

    private static TrainingHost StartHost()
    {
        Require.Artwork();
        return TrainingHost.Start();
    }

    private static int CountMessages(TrainingHostSite site, string fragment)
        => site.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    /// <summary>One simulation frame: point the player at the commander (goal 2's turn demo),
    /// advance the sim + autopilot, then evaluate the mission. Returns true once
    /// <paramref name="until"/> holds.</summary>
    private static bool Frame(TrainingHost host, TrainingHostSite site, Mission2 mission,
        Func<bool> until, float throttle, bool afterburner)
    {
        var commander = host.CommanderShip;
        // Point the player at the commander so the ObjectPointingAtCondition (goal 2) resolves.
        var dir = commander.Position - host.PlayerShip.Position;
        if (dir.LengthSquared() > 1e-6f)
        {
            var o = host.PlayerShip.Orientation;
            var d = dir / dir.Length();
            o.Set(new Allegiance.Core.Collision.Vector3(d.X, d.Y, d.Z),
                new Allegiance.Core.Collision.Vector3(0f, 0f, 1f));
        }
        host.PlayerShip.StateM &= ~Afterburner;
        site.SetAppliedControls(new ControlData(0f, 0f, 0f, throttle));
        if (afterburner) host.PlayerShip.StateM |= Afterburner;
        host.Step();
        mission.Frame();
        return until();
    }

    [Fact]
    [Trait("Tier", "Live")]
    public void Mission2_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        using var host = StartHost();
        var site = new TrainingHostSite(host);
        var mission = new Mission2(site);
        TrainingController.BeginMission();
        mission.Start();

        const int MaxSteps = 30000;
        float throttle = 0f;   // not full-forward until goal 4
        bool afterburner = false;
        int i;

        // Phase 1 — goal 3: press SPACEBAR at the first prompt.
        for (i = 0; i < MaxSteps && CountMessages(site, "Press the SPACEBAR") < 1; i++)
            Frame(host, site, mission, () => false, throttle, afterburner);
        Assert.True(CountMessages(site, "Press the SPACEBAR") >= 1, "goal 3 prompt never appeared");
        mission.RecordKeyPress(KeySpace);

        // Phase 2 — goal 4: full throttle at the BACKSLASH prompt.
        for (; i < MaxSteps && CountMessages(site, "BACKSLASH") < 1; i++)
            Frame(host, site, mission, () => false, throttle, afterburner);
        Assert.True(CountMessages(site, "BACKSLASH") >= 1, "goal 4 prompt never appeared");
        throttle = 1.0f;

        // Phase 3 — goal 5: drop the throttle off full at the scroll-wheel prompt.
        for (; i < MaxSteps && CountMessages(site, "scroll wheel") < 1; i++)
            Frame(host, site, mission, () => false, throttle, afterburner);
        Assert.True(CountMessages(site, "scroll wheel") >= 1, "goal 5 prompt never appeared");
        throttle = 0.3f;

        // Phase 4 — goal 6: second SPACEBAR.
        for (; i < MaxSteps && CountMessages(site, "Press the SPACEBAR") < 2; i++)
            Frame(host, site, mission, () => false, throttle, afterburner);
        Assert.True(CountMessages(site, "Press the SPACEBAR") >= 2, "goal 6 prompt never appeared");
        mission.RecordKeyPress(KeySpace);

        // Phase 5 — goal 7: hold SPACE to fire the booster (afterburner state bit).
        for (; i < MaxSteps && CountMessages(site, "SPACE key") < 1; i++)
            Frame(host, site, mission, () => false, throttle, afterburner);
        Assert.True(CountMessages(site, "SPACE key") >= 1, "goal 7 prompt never appeared");
        afterburner = true;

        // Phase 6 — goal 8: the mission engages autopilot and flies the player home (real host
        // autopilot pass moves the player within 500 m of station 1010); then landing ends the mission.
        for (; i < MaxSteps && !host.IsAutopilot(host.PlayerShipId); i++)
            Frame(host, site, mission, () => false, throttle, afterburner);
        Assert.True(host.IsAutopilot(host.PlayerShipId), "autopilot never engaged in goal 8");

        // Let the autopilot bring the player within 500 m of the station so goal 8 completes.
        for (; i < MaxSteps && !CompletedNearStation(host); i++)
            Frame(host, site, mission, () => false, throttle, afterburner);

        mission.ShipLanded();      // Mission2::ShipLanded -> Terminate
        Assert.True(mission.Frame(), "mission should report completion after landing");
        Assert.True(host.IsAutopilot(host.PlayerShipId), "autopilot remained engaged to the end");

        // The narrative milestones appeared in order.
        var messages = site.Messages.ToList();
        int idxSpace = messages.FindIndex(m => m.Contains("Press the SPACEBAR"));
        int idxBackslash = messages.FindIndex(m => m.Contains("BACKSLASH"));
        Assert.True(idxBackslash > idxSpace, "goal 3 prompt must precede goal 4 prompt");
    }

    private static bool CompletedNearStation(TrainingHost host)
    {
        var p = host.PlayerShip.Position;
        var player = new Vector3(p.X, p.Y, p.Z);
        var dist = (player - host.StationPosition).Length();
        // Goal 8: ObjectWithinRadiusCondition(player, station 1010, 500) (mission2.cpp:353)
        return dist <= 500f;
    }

    [Fact]
    [Trait("Tier", "Live")]
    public void HostActions_LandRealEffects_OnTheMission()
    {
        using var host = StartHost();

        // The training universe is real: player + commander are real Ship models in real sector 1010.
        var sector = host.Mission.Sectors.First(s => s.Id == 1010);
        Assert.Contains(host.PlayerShip, sector.Models);
        Assert.Contains(host.CommanderShip, sector.Models);
        Assert.Equal((short)412, host.PlayerShip.HullType.HullId);  // interceptor
        Assert.Equal(Ship.PilotTypeWingman, host.CommanderShip.PilotType);  // commander is a wingman drone

        // CreateWaypoint — real waypoint registered and resolvable by the site.
        host.CreateWaypoint(7, new Vector3(-200f, -200f, 400f), 1010, 0, true);
        Assert.True(host.Waypoints.ContainsKey(7));
        var resolved = new TrainingHostSite(host).Resolve(new TargetRef(ObjectType.Buoy, 7));
        Assert.NotNull(resolved);
        Assert.Equal(new Vector3(-200f, -200f, 400f), resolved!.Position);

        // IssueCommand(Goto buoy) — the commander now carries a real Goto order toward the waypoint.
        host.IssueCommand(host.CommanderShipId, CommandCurrent, null, 7, null, CmdGoto);
        Assert.NotNull(host.CommanderShip.GotoWaypoint);
        Assert.Equal(GotoKind.Waypoint, host.CommanderShip.GotoWaypoint!.Value.Kind);

        // SetAutopilot — real autopilot flag, cleared by the no-autopilot command.
        host.SetAutopilot(host.PlayerShipId, true);
        Assert.True(host.IsAutopilot(host.PlayerShipId));
        host.SetAutopilot(host.PlayerShipId, false);
        Assert.False(host.IsAutopilot(host.PlayerShipId));

        // CreateDrone + PlaceDrone — a real ship created and placed in the mission.
        int drone = host.CreateDrone("Target", -1, 412, 0, (int)Ship.PilotTypeWingman);
        Assert.True(host.Drones.ContainsKey(drone));
        Assert.Contains(host.Drones[drone], host.Mission.Sectors[0].Models);
        host.PlaceDrone(drone, 1010, new Vector3(10f, 0f, 0f), Vector3.UnitX, Vector3.UnitZ, null, CmdDoNothing);
        Assert.Equal(new Allegiance.Core.Collision.Vector3(10f, 0f, 0f), host.Drones[drone].Position);
    }
}
