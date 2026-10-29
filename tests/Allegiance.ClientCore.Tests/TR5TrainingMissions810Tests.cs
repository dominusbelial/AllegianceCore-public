using System;
using System.Collections.Generic;
using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// TR-5 — proves Missions 8 (Nanite) and 10 (Free Flight) run to completion against the real
/// embedded training host through the real input path, plus the nanite/station seams land real
/// effects. Both missions are pure data; the site reads/writes the real embedded-server universe
/// (<see cref="TrainingHost"/>), and the driver scripts the interactive outcomes the missions gate
/// on — mounting the nanite, repairing the home station, destroying the enemy fighters, moving the
/// escorted bomber, and knocking the enemy outpost down — the same way the TR-3/TR-4 proofs script
/// target picks and kills.
/// </summary>
[Collection("LiveNetwork")]
public sealed class TR5TrainingMissions810Tests
{
    private const int NanitePart = 190;

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 8 — Nanite
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission8_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.Nanite);
        var site = new TrainingHostSite(host);
        var mission = new Mission8(site);
        TrainingController.BeginMission();
        mission.Start();

        const int MaxSteps = 200000;
        var seen = new HashSet<string>();
        bool mounted = false, repaired = false, fightersDead = false,
             bomberNear = false, bomberAtAleph = false, bomberDamaged = false, stationDestroyed = false,
             waitSignal = false, completed = false;

        for (int i = 0; i < MaxSteps && !completed; i++)
        {
            host.StepLight(1.0f);
            completed = mission.Frame();
            if (completed) break;

            foreach (var m in site.Messages)
            {
                if (!seen.Add(m)) continue;

                // Goal 1: mount the Nanite Repair System, then repair the knocked-down home station.
                if (m.Contains("Swap the Nanite Repair System", StringComparison.Ordinal) && !mounted)
                { host.MountPart(host.PlayerShipId, NanitePart); mounted = true; }
                else if (m.Contains("Repair the station.", StringComparison.Ordinal) && !repaired)
                { host.RepairStation(Mission8.HomeStationId); repaired = true; }
                // "Wait for signal." fires once the within-500 m-of-the-bomber gate has passed.
                else if (m.Contains("Wait for signal.", StringComparison.Ordinal))
                { waitSignal = true; }
            }

            // Goal 2: both stealth fighters destroyed once they exist.
            if (!fightersDead && host.Drones.ContainsKey(mission.SolapId) && host.Drones.ContainsKey(mission.ToastId))
            { host.DestroyDrone(mission.SolapId); host.DestroyDrone(mission.ToastId); fightersDead = true; }

            // Goal 3: keep the bomber within 500 m of the player (the player drifts from the
            // commander-collision jiggle, so pin the bomber to the player's live position) until the
            // within-radius gate passes, then park it in sector 1052 for the crossing gate.
            // Pin it on the player's far side from the home station: inside the station's radius
            // an idle drone's Dodge thrusts straight away from the centre every tick
            // (shipIGC.cpp:2334, common.cpp:1745-1747), so the "bomber stopped" gate never holds.
            if (host.Drones.ContainsKey(mission.BomberId))
            {
                if (!waitSignal)
                {
                    var pp = site.PlayerShip.Position;
                    var away = site.Resolve(new TargetRef(ObjectType.Station, Mission8.HomeStationId)) is { } home
                        && Vector3.DistanceSquared(pp, home.Position) > 1f
                        ? Vector3.Normalize(pp - home.Position) : Vector3.UnitX;
                    host.PlaceDrone(mission.BomberId, host.GetSectorId(host.PlayerShipId), pp + away * 100f, Vector3.UnitX, Vector3.UnitZ, null, 0);
                    host.IssueCommand(mission.BomberId, CommandAccepted, null, null, null, CmdDoNothing);
                    bomberNear = true;
                }
                else if (!bomberAtAleph)
                {
                    host.PlaceDrone(mission.BomberId, 1052, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, null, 0);
                    host.IssueCommand(mission.BomberId, CommandAccepted, null, null, null, CmdDoNothing);
                    bomberAtAleph = true;
                }
            }

            // "Defend bombers." fires when goal 3's "bomber hull damaged" gate starts — script the
            // bomber being damaged (not destroyed), then knock the enemy outpost down so the
            // station-or-bomber-destroyed gate resolves to the win path.
            if (seen.Contains("Defend bombers.") && !bomberDamaged)
            { host.DamageShip(mission.BomberId, 8000f); bomberDamaged = true; }
            if (bomberDamaged && !stationDestroyed)
            { host.DamageStation(Mission8.EnemyOutpostStationId, 200000f); stationDestroyed = true; }
        }

        Assert.True(mounted, "goal 1: player never mounted the Nanite Repair System");
        Assert.True(repaired, "goal 1: home station never repaired");
        Assert.True(fightersDead, "goal 2: enemy stealth fighters never destroyed");
        Assert.True(bomberNear, "goal 3: bomber never brought within range");
        Assert.True(bomberAtAleph, $"goal 3: bomber never crossed into sector 1052 (goal={mission.ActiveGoalPath})");
        Assert.True(bomberDamaged, "goal 3: bomber-hull-damaged gate never started");
        Assert.True(stationDestroyed, "goal 3: enemy outpost never destroyed");
        Assert.True(seen.Contains("Enemy outpost destroyed."), "goal 3: outpost-destroyed message never fired");
        Assert.True(completed, "Mission 8 did not report completion");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 10 — Free Flight
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission10_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.FreeFlight);
        var site = new TrainingHostSite(host);
        var mission = new Mission10(site);
        host.SetActiveMission(mission);   // route real kill credit into the mission's counter
        TrainingController.BeginMission();
        mission.Start();

        const int MaxSteps = 200000;
        bool credited = false, completed = false;

        for (int i = 0; i < MaxSteps && !completed; i++)
        {
            host.StepLight(1.0f);
            completed = mission.Frame();
            if (completed) break;

            // Same escalating kill-goal waves as Mission 6 (2..49); drive the real kill credit once
            // the first enemy wingman exists, and clear the "bomber/scout in home or dead" gates.
            if (!credited && host.Drones.ContainsKey(mission.Enemy1Id))
            {
                for (int k = 0; k < 49; k++)
                    host.RecordKillCredit(host.PlayerShipId, mission.Enemy1Id);
                credited = true;
            }
            if (host.Drones.ContainsKey(mission.Enemy2Id))
                host.DestroyDrone(mission.Enemy2Id);
            if (host.Drones.ContainsKey(mission.Enemy3Id))
                host.DestroyDrone(mission.Enemy3Id);
        }

        Assert.True(credited, "Mission 10 kill goals never started (enemy wingman never spawned)");
        Assert.True(completed, "Mission 10 did not report completion (49 kills not all counted?)");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 8 nanite / repair / station seams land real effects on the host
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission8_Seams_LandRealEffects_OnTheHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.Nanite);
        var site = new TrainingHostSite(host);

        // GetShipHasMountedCondition resolves real mounted-part state.
        Assert.False(host.HasPartMounted(host.PlayerShipId, NanitePart));
        host.MountPart(host.PlayerShipId, NanitePart);
        Assert.True(host.HasPartMounted(host.PlayerShipId, NanitePart));
        Assert.True(new GetShipHasMountedCondition(site, new TargetRef(ObjectType.Ship, host.PlayerShipId), NanitePart).Evaluate());

        // SetDroneNanAction mounts the nanite on a drone (mission8.cpp:465).
        int nan = host.CreateDrone("bahdohday", 9870, 685, 0, (int)Ship.PilotTypeWingman);
        new SetDroneNanAction(site, new TargetRef(ObjectType.Ship, nan)).Execute();
        Assert.True(host.HasPartMounted(nan, NanitePart));

        // DamageStationAction / station fraction / repair (mission8.cpp:203, stationIGC.cpp:147-156).
        Assert.Equal(1f, site.GetFraction(new TargetRef(ObjectType.Station, Mission8.HomeStationId)));
        new DamageStationAction(site, Mission8.HomeStationId, 12500f).Execute();
        float knocked = site.GetFraction(new TargetRef(ObjectType.Station, Mission8.HomeStationId));
        Assert.True(knocked < 1f, $"home station should be damaged, was {knocked}");
        Assert.True(new GetShipIsDamagedCondition(site, ObjectType.Station, Mission8.HomeStationId, 1.0f).Evaluate());
        host.RepairStation(Mission8.HomeStationId);
        Assert.Equal(1f, site.GetFraction(new TargetRef(ObjectType.Station, Mission8.HomeStationId)));
        Assert.False(new GetShipIsDamagedCondition(site, ObjectType.Station, Mission8.HomeStationId, 1.0f).Evaluate());

        // The enemy outpost's destroyed gate: a big hit drops it to 0 (mission8.cpp:600 Not(GetShipIsDamaged(…,0.0))).
        host.DamageStation(Mission8.EnemyOutpostStationId, 200000f);
        Assert.Equal(0f, site.GetFraction(new TargetRef(ObjectType.Station, Mission8.EnemyOutpostStationId)));
        Assert.False(new GetShipIsDamagedCondition(site, ObjectType.Station, Mission8.EnemyOutpostStationId, 0.0f).Evaluate());

        // DamageShip lowers a live ship's fraction without destroying it (mission8.cpp:586 gate).
        int bomber = host.CreateDrone("Commander", 9880, 604, 0, (int)Ship.PilotTypeWingman);
        Assert.Equal(1f, host.GetShipFraction(bomber));
        host.DamageShip(bomber, 8000f);
        float fr = host.GetShipFraction(bomber);
        Assert.True(fr > 0f && fr < 1f, $"bomber should be damaged but alive, was {fr}");
        Assert.True(new GetShipIsDamagedCondition(site, ObjectType.Ship, bomber, 1.0f).Evaluate());
        Assert.False(site.IsShipDestroyed(new TargetRef(ObjectType.Ship, bomber)));
    }
}
