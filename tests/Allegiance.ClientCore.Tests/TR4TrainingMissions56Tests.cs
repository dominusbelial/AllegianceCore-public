using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// TR-4 — proves Missions 5 (Command View) and 6 (Practice Arena) run to completion against the real
/// embedded training host through the real input path. The missions are pure data (Mission5.cs /
/// Mission6.cs); the site reads/writes the real embedded-server universe (<see cref="TrainingHost"/>),
/// and the driver scripts the interactive inputs the training gates on — command-view picks, view-
/// sector changes, ordered commands and (for Mission 6) real kill-credit outcomes — the same way the
/// TR-3 proofs script target picks / kills.
/// </summary>
[Collection("LiveNetwork")]
public sealed class TR4TrainingMissions56Tests
{
    private const int KeySpace = KeyFireBooster; // TK_FireBooster — the SPACEBAR key

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 5 — Command View
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission5_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.CommandView);
        var site = new TrainingHostSite(host);
        var mission = new Mission5(site);
        TrainingController.BeginMission();
        mission.Start();

        // Mission5 seeds 3 tower builders + 4 miners + 1 stealth fighter as root start actions
        // (mission5.cpp:181-247); the enemy scout spawns later in goal 3.
        Assert.True(host.Drones.Count >= 8, $"Mission 5 should seed 8 friendly drones, had {host.Drones.Count}");

        const int MaxSteps = 40000;
        var seen = new HashSet<string>();
        bool spacebar = false, pickedTower = false, deployed = false, allTowers = false,
             viewedMars = false, pickedMiner = false, mined = false, allMining = false,
             minersHome = false, attacked = false, scoutDead = false, viewedHome = false;
        bool completed = false;

        for (int i = 0; i < MaxSteps && !completed; i++)
        {
            host.StepLight();
            completed = mission.Frame();
            if (completed) break;

            foreach (var m in site.Messages)
            {
                if (!seen.Add(m)) continue;
                if (m.Contains("Press the SPACEBAR", System.StringComparison.Ordinal) && !spacebar)
                { mission.RecordKeyPress(KeySpace); spacebar = true; }
                else if (m.Contains("Left click on a Tower Layer", System.StringComparison.Ordinal) && !pickedTower)
                { host.SetPicked(new TargetRef(ObjectType.Ship, mission.Builder1Id)); pickedTower = true; }
                else if (m.Contains("Right click in space to deploy tower", System.StringComparison.Ordinal) && !deployed)
                { host.SetCommandTarget(mission.Builder1Id, null, CmdBuild); deployed = true; }
                else if (m.Contains("Order all towers to defend", System.StringComparison.Ordinal) && !allTowers)
                {
                    host.SetCommandTarget(mission.Builder1Id, null, CmdBuild);
                    host.SetCommandTarget(mission.Builder2Id, null, CmdBuild);
                    host.SetCommandTarget(mission.Builder3Id, null, CmdBuild);
                    allTowers = true;
                }
                else if (m.Contains("Left click on 'Mars' in the Sector Map", System.StringComparison.Ordinal) && !viewedMars)
                { host.SetViewSector(1051); viewedMars = true; }
                else if (m.Contains("Left click on a Miner to select it", System.StringComparison.Ordinal) && !pickedMiner)
                { host.SetPicked(new TargetRef(ObjectType.Ship, mission.Miner2Id)); pickedMiner = true; }
                else if (m.Contains("Left click on 'Mine' command", System.StringComparison.Ordinal) && !mined)
                { host.SetCommandTarget(mission.Miner2Id, null, CmdMine); mined = true; }
                else if (m.Contains("Order all the miners to work", System.StringComparison.Ordinal) && !allMining)
                {
                    host.SetCommandTarget(mission.Miner2Id, null, CmdMine);
                    host.SetCommandTarget(mission.Miner3Id, null, CmdMine);
                    host.SetCommandTarget(mission.Miner4Id, null, CmdMine);
                    allMining = true;
                }
                else if (m.Contains("Order all three miners back to base", System.StringComparison.Ordinal) && !minersHome)
                {
                    host.SetCommandTarget(mission.Miner2Id, null, CmdGoto);
                    host.SetCommandTarget(mission.Miner3Id, null, CmdGoto);
                    host.SetCommandTarget(mission.Miner4Id, null, CmdGoto);
                    minersHome = true;
                }
                else if (m.Contains("Order stealth fighter to attack", System.StringComparison.Ordinal) && !attacked)
                {
                    host.SetCommandTarget(mission.ScoutId, new TargetRef(ObjectType.Ship, mission.EnemyScoutId), CmdAttack);
                    attacked = true;
                }
                else if (m.Contains("Select Miner 01 in the Team Display", System.StringComparison.Ordinal) && !viewedHome)
                { host.SetViewSector(1050); viewedHome = true; }
            }

            // Goal 4 waits for the enemy scout to be destroyed (GetShipIsDestroyedCondition2,
            // mission5.cpp:568) — script that outcome once the drone exists and the attack order is in.
            if (attacked && host.Drones.ContainsKey(mission.EnemyScoutId) && !scoutDead)
            { host.DestroyDrone(mission.EnemyScoutId); scoutDead = true; }
        }

        Assert.True(spacebar, "SPACEBAR prompt (goal 1) never reached");
        Assert.True(pickedTower, "tower pick (goal 1) never reached");
        Assert.True(deployed, "build command (goal 1) never issued");
        Assert.True(allTowers, "all-towers-build (goal 1) never reached");
        Assert.True(viewedMars, "view-Mars (goal 2) never reached");
        Assert.True(pickedMiner, "miner pick (goal 2) never reached");
        Assert.True(mined, "mine command (goal 2) never issued");
        Assert.True(allMining, "all-miners-working (goal 2) never reached");
        Assert.True(minersHome, "miners-to-base (goal 3) never reached");
        Assert.True(attacked, "attack order (goal 4) never issued");
        Assert.True(scoutDead, "enemy scout never destroyed (goal 4)");
        Assert.True(viewedHome, "Take-Me-To home view (goal 5) never reached");
        Assert.True(completed, "Mission 5 did not report completion");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 6 — Practice Arena
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission6_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        var site = new TrainingHostSite(host);
        var mission = new Mission6(site);
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

            // The kill goals (KillsReachedCondition 2..49) read the mission's real kill credit. Drive
            // it once the first enemy wingman exists (it spawns via the respawn mechanism ~10 s in);
            // the waves then fire in sequence as the goal list advances.
            if (!credited && host.Drones.ContainsKey(mission.Enemy1Id))
            {
                for (int k = 0; k < 49; k++)
                    host.RecordKillCredit(host.PlayerShipId, mission.Enemy1Id);
                credited = true;
            }

            // The "bomber in home or dead" / "scout in home or dead" gates (mission6.cpp:723-731)
            // treat an absent ship as dead (getshipisdamagedcondition.cpp:58-59). The bomber never
            // spawns while the scout (enemy1) stays alive to feed the kill counter, so the bomber gate
            // resolves trivially; the scout gate needs the fighter gone — destroy enemy2 as soon as it
            // exists (it stays dead while enemy1 lives, so the spawn group never revives it).
            if (host.Drones.ContainsKey(mission.Enemy2Id))
                host.DestroyDrone(mission.Enemy2Id);
            if (host.Drones.ContainsKey(mission.Enemy3Id))
                host.DestroyDrone(mission.Enemy3Id);
        }

        Assert.True(credited, "Mission 6 kill goals never started (enemy wingman never spawned)");
        Assert.True(completed, "Mission 6 did not report completion (49 kills not all counted?)");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 6 kill credit is the REAL kill credit, not a separate counter
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission6_KillCredit_IsRealCredit_NotASeparateCounter()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        var site = new TrainingHostSite(host);
        var mission = new Mission6(site);
        host.SetActiveMission(mission);
        TrainingController.BeginMission();
        mission.Start();

        // Create a live enemy wingman so the credit resolves a real victim (Enemy1 hasn't spawned yet
        // at test time — its respawn arm is ~10 s in).
        int wingman = host.CreateDrone("Wingman", 9850, 412, 1, (int)Ship.PilotTypeWingman);

        // A real kill credit of an enemy wingman by the player's side increments the count (native
        // TrainingMission::ShipKilled, trainingmission.cpp:515-524).
        host.RecordKillCredit(host.PlayerShipId, wingman);
        Assert.Equal(1, mission.GetKillCount());

        // A wingman destroyed WITHOUT a kill credit does NOT increment the count — the count comes
        // from the real credit path, never from a generic "something died" counter.
        host.DestroyDrone(wingman);
        Assert.Equal(1, mission.GetKillCount());

        // An ejection-style non-credit (no launcher on the player's side) does not count.
        host.RecordKillCredit(-1, wingman);
        Assert.Equal(1, mission.GetKillCount());

        // A non-wingman victim (a miner is c_ptMiner) does not count.
        int miner = host.CreateDrone("Miner", 9860, 438, 1, (int)Ship.PilotTypeMiner);
        host.RecordKillCredit(host.PlayerShipId, miner);
        Assert.Equal(1, mission.GetKillCount());

        // The count still reflects only the one real credit, not any of the above.
        Assert.Equal(1, mission.GetKillCount());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 5/6 command-view seams land real effects on the host
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission56_CommandViewSeams_LandRealEffects_OnTheHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.CommandView);

        // GetPicked / GetViewSector / GetFraction / GetCommandAcknowledged resolve real host state.
        host.SetPicked(new TargetRef(ObjectType.Ship, 9800));
        Assert.True(new TrainingHostSite(host).GetPicked(new TargetRef(ObjectType.Ship, 9800)));
        Assert.False(new TrainingHostSite(host).GetPicked(new TargetRef(ObjectType.Ship, 9799)));

        host.SetViewSector(1051);
        Assert.Equal(1051, new TrainingHostSite(host).GetViewSector());

        host.AcknowledgeCommand(CmdBuild);
        Assert.Equal(CmdBuild, new TrainingHostSite(host).GetCommandAcknowledged());

        int wingman = host.CreateDrone("Wingman", 9810, 412, 1, (int)Ship.PilotTypeWingman);
        Assert.Equal(1f, new TrainingHostSite(host).GetFraction(new TargetRef(ObjectType.Ship, wingman)));
        host.DestroyDrone(wingman);
        Assert.Equal(0f, new TrainingHostSite(host).GetFraction(new TargetRef(ObjectType.Ship, wingman)));
        Assert.True(new TrainingHostSite(host).IsShipDestroyed(new TargetRef(ObjectType.Ship, wingman)));

        // GetShipIsDamagedCondition(ship, 0.0) is true while alive (fraction != 0) and false once gone —
        // the reading M6's Not(GetShipIsDamaged(…,0.0)) "dead" gates rely on.
        int foe = host.CreateDrone("Foe", 9820, 412, 1, (int)Ship.PilotTypeWingman);
        var site = new TrainingHostSite(host);
        Assert.True(new GetShipIsDamagedCondition(site, ObjectType.Ship, foe, 0.0f).Evaluate());
        host.DestroyDrone(foe);
        Assert.False(new GetShipIsDamagedCondition(site, ObjectType.Ship, foe, 0.0f).Evaluate());
    }
}
