using System.Collections.Generic;
using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;
using Coll = Allegiance.Core.Collision;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// TR-3 — proves Missions 3 (Basic Weaponry) and 4 (Enemy Engagement) run to completion against the
/// real embedded training host through the real input path, exactly as TR-2b proved Mission 2. The
/// missions are pure data (Mission3.cs / Mission4.cs); the site reads/writes the real embedded-server
/// universe (<see cref="TrainingHost"/>), and the driver scripts the interactive inputs the training
/// gates on — target picks, key presses, weapon state bits, chat sends and kill outcomes — the same
/// way the TR-2b Mission-2 test scripts SPACEBAR/throttle/afterburner.
/// </summary>
[Collection("LiveNetwork")]
public sealed class TR3TrainingMissions34Tests
{
    private const int KeySpace = KeyFireBooster;    // TK_FireBooster — the SPACEBAR key
    private const int KeyTargetEnemy = 111;         // Mission3/4's picked id for TK_TargetEnemy
    private const int KeyToggleCloak = 112;         // Mission4's picked id for TK_ToggleCloak

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 3 — Basic Weaponry
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission3_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        var site = new TrainingHostSite(host);
        var mission = new Mission3(site);
        TrainingController.BeginMission();
        mission.Start();

        // The 10 derelicts are real drones on the host (Mission3::CreateUniverse, mission3.cpp:201-229).
        Assert.True(host.Drones.Count >= 10, "Mission 3 should seed 10 derelict hulls");

        const int MaxSteps = 40000;
        var seen = new HashSet<string>();
        int nextDerelict = 1000;          // FirstDerelictId (Mission3.cs:25)
        int spacebarPrompts = 0;          // "Press the SPACEBAR" occurrences → one press each
        int spaceKeyPrompts = 0;          // "Press the SPACE key" occurrences → one press each
        int destroyPrompts = 0;           // "Destroy current target." occurrences → one kill each
        int retargetDelay = 0;            // frames before the radar re-targets a live hull after a kill
        int firePrompts = 0;
        int sampledFire = 0;
        bool missilePrompt = false;
        bool reachedLand = false;
        bool completed = false;

        for (int i = 0; i < MaxSteps && !completed && !reachedLand; i++)
        {
            OrientPlayerToTarget(host);

            // Radar re-target: after killing the locked hull, wait a couple frames so the
            // GetShipIsDestroyedCondition(CurrentTarget, lock) goal observes the dead victim (it
            // re-anchors on a target change), then point at the next live hull for the following
            // "turn to face your target" gates (radar auto-target, mission3.cpp:750-753).
            if (retargetDelay > 0 && --retargetDelay == 0)
                host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, nextDerelict));

            host.StepLight();
            // Headless input sample: pulse fire only at the lesson's fire prompts
            // (training/mission3.cpp:627-649,838-848,927-951). The script reads StateM at Frame;
            // kill outcomes below are driven separately. Holding fire through Mission.Update
            // now fires real P882a guns and destroys the aiming target before goal 5.
            host.PlayerShip.StateM |= sampledFire;
            completed = mission.Frame();
            host.PlayerShip.StateM &= ~(WeaponsMask | MissileFire);
            sampledFire = 0;
            if (completed) break;

            // Repeated-identical prompts (several goals share the exact same message text), so the
            // count of each distinct prompt is the reliable signal: press/kill once per new occurrence.
            int totalSpacebar = site.Messages.Count(m => m.Contains("Press the SPACEBAR", StringComparison.Ordinal));
            int totalSpaceKey = site.Messages.Count(m => m.Contains("Press the SPACE key", StringComparison.Ordinal));
            int totalDestroy = site.Messages.Count(m => m.Contains("Destroy current target.", StringComparison.Ordinal));
            int totalFire = site.Messages.Count(m => m.Contains("Left click to fire your main weapon.", StringComparison.Ordinal));
            if (totalFire > firePrompts)
            {
                Assert.False(host.Drones[nextDerelict].Dead, "aiming target must survive until the fire prompt");
                sampledFire = OneWeapon;
                firePrompts = totalFire;
            }
            while (totalSpacebar > spacebarPrompts) { Assert.True(mission.RecordKeyPress(KeySpace)); spacebarPrompts++; }
            while (totalSpaceKey > spaceKeyPrompts) { Assert.True(mission.RecordKeyPress(KeySpace)); spaceKeyPrompts++; }
            while (totalDestroy > destroyPrompts)
            {
                host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, nextDerelict));
                host.DestroyDrone(nextDerelict);       // the radar-locked target is killed
                nextDerelict++;
                retargetDelay = 2;
                destroyPrompts++;
            }

            // One-shot prompts (distinct text) → act once, dedupe by message text.
            foreach (var m in site.Messages)
            {
                if (!seen.Add(m)) continue;
                if (m.Contains("Land in a green hangar", StringComparison.Ordinal))
                    reachedLand = true;   // goal 13's landing prompt → land to finish
                else if (m.Contains("Press 5 or Middle click", StringComparison.Ordinal))
                {
                    sampledFire = MissileFire;
                    missilePrompt = true;
                }
                else if (m.Contains("right mouse button to target", StringComparison.Ordinal))
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, nextDerelict));
                else if (m.Contains("Target an enemy using the F key", StringComparison.Ordinal))
                    mission.RecordKeyPress(KeyTargetEnemy); // TK_TargetEnemy (goal 7)
                else if (m.Contains("target the outpost.", StringComparison.Ordinal))
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Station, 1010)); // goal 12
            }
        }

        Assert.True(spacebarPrompts >= 2, $"both SPACEBAR prompts not reached (spacebar={spacebarPrompts})");
        Assert.Equal(2, firePrompts); // native main-weapon gates in goals 5 and 8
        Assert.True(missilePrompt, "missile-fire prompt (goal 9) never reached");
        Assert.True(spaceKeyPrompts >= 2, $"both SPACE-key prompts not reached (spacekey={spaceKeyPrompts}, goal={mission.ActiveGoalPath})");
        Assert.True(nextDerelict >= 1003, $"three derelict kills expected, killed to {nextDerelict}");
        Assert.True(reachedLand, "landing prompt (goal 13) never reached");

        // Goal 13 ends only by landing (ShipLanded → Terminate, mission3.cpp:154-158).
        mission.ShipLanded();
        Assert.True(mission.Frame(), "Mission 3 should report completion after landing");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mission 4 — Enemy Engagement
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission4_RunsToCompletion_AgainstTheRealEmbeddedHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.EnemyEngagement);
        var site = new TrainingHostSite(host);
        var mission = new Mission4(site);
        TrainingController.BeginMission();
        mission.Start();

        // The training_3 universe: sector Neptune 1030 is the start; Mars 1031 and 1032 are the
        // transit/build targets the mission uses (mission4.cpp:92-95,526,875).
        Assert.Contains(host.Mission.Sectors, s => s.Id == 1030);
        Assert.Contains(host.Mission.Sectors, s => s.Id == 1031);

        // Marker for the OT_warp stand-in: the aleph (Ship-type target carrying id 1030) must resolve
        // for the ObjectWithinRadius "fly to the aleph" gate (mission4.cpp:497). Park it beside the player.
        int alephMarker = host.CreateDrone("Aleph", 1030, 412, 0, 2);
        host.PlaceDrone(alephMarker, 1030, new Vector3(100f, 0f, 0f), Vector3.UnitX, Vector3.UnitZ, null, CmdDoNothing);

        const int MaxSteps = 40000;
        int insertBracketPrompts = 0;      // "Press the INSERT or '[' key" (goals 3,4)
        int acceptPrompts = 0;             // "Press the INSERT key" (goals 8,9)
        int spacebarSeen = 0;
        bool chatSent = false;
        bool transited = false;
        bool cloakPressed = false;
        bool targetedMiner = false;
        bool builderGone = false;
        bool reachedLand = false;
        bool completed = false;

        for (int i = 0; i < MaxSteps && !completed && !reachedLand; i++)
        {
            OrientPlayerToTarget(host);
            host.StepLight();
            completed = mission.Frame();
            if (completed) break;

            // Repeated-identical prompts → count-based, once per new occurrence.
            int totalSpacebar = site.Messages.Count(m => m.Contains("Press the SPACEBAR", StringComparison.Ordinal));
            for (int s = totalSpacebar; s > spacebarSeen; s--) mission.RecordKeyPress(KeySpace);
            spacebarSeen = Math.Max(spacebarSeen, totalSpacebar);

            int totalInsertBracket = site.Messages.Count(m => m.Contains("INSERT or '[' key to accept the command.", StringComparison.Ordinal));
            while (totalInsertBracket > insertBracketPrompts)
            {
                insertBracketPrompts++;                    // goals 3 & 4: accept the Goto
                host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, 1030), CmdGoto);
            }
            int totalInsert = site.Messages.Count(m => m.Contains("Press the INSERT key to accept the command.", StringComparison.Ordinal));
            while (totalInsert > acceptPrompts)
            {
                acceptPrompts++;
                if (acceptPrompts == 1)
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, mission.BuilderId), CmdGoto);   // goal 8: protect the constructor
                else if (acceptPrompts == 2)
                {
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, mission.EnemyScoutId), CmdAttack); // goal 8: intercept the scout
                    // after the interception, the scout flees (native AI gives it a run command —
                    // mission4.cpp:941-948 gate on GetCommandCondition(scout, c_cidGoto))
                    host.SetCommandTarget(mission.EnemyScoutId, null, CmdGoto);
                }
                else if (acceptPrompts == 3)
                {
                    // goal 9: build completes → the builder ship is gone (GetShipIsDestroyedCondition2)
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, mission.BuilderId), CmdGoto);
                    if (!builderGone)
                    {
                        host.DestroyDrone(mission.BuilderId);
                        builderGone = true;
                    }
                }
            }

            // Distinct-text prompts → act once, dedupe by message text.
            foreach (var m in site.Messages)
            {
                if (m.Contains("Land in the expansion complex.", StringComparison.Ordinal))
                {
                    reachedLand = true;    // goal 10: land to finish
                    if (!builderGone)
                    {
                        host.DestroyDrone(mission.BuilderId); // goal 9 build-complete outcome
                        builderGone = true;
                    }
                }
                else if (m.Contains("Press the ENTER key to begin a chat.", StringComparison.Ordinal) && !chatSent)
                {
                    site.SendTrainingChat(ChatEveryone);      // goal 2: chat to All
                    chatSent = true;
                }
                else if (m.Contains("Fly to the aleph", StringComparison.Ordinal))
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, 1030));
                else if (m.Contains("Flying through the aleph", StringComparison.Ordinal) && !transited)
                {
                    var mars = host.Mission.Sectors.First(s => s.Id == 1031); // transit gate (goal 5)
                    host.Mission.PlaceShip(host.PlayerShip, mars);
                    transited = true;
                }
                else if (m.Contains("activate your cloak.", StringComparison.Ordinal) && !cloakPressed)
                {
                    mission.RecordKeyPress(KeyToggleCloak);    // goal 6: the K key
                    cloakPressed = true;
                }
                else if (m.Contains("Target an enemy miner", StringComparison.Ordinal) && !targetedMiner)
                {
                    mission.RecordKeyPress(KeyTargetEnemy);    // goal 7: E key
                    host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, mission.EnemyMiner1Id));
                    host.DestroyDrone(mission.EnemyMiner1Id);  // kill the locked miner
                    targetedMiner = true;
                }
            }
        }

        Assert.True(chatSent, "goal 2 chat to All never happened");
        Assert.True(insertBracketPrompts >= 2, $"goal 3/4 accept prompts not both reached ({insertBracketPrompts})");
        Assert.True(transited, "intersector transit to Mars (goal 5) never happened");
        Assert.True(cloakPressed, "cloak key (goal 6) never pressed");
        Assert.True(targetedMiner, "enemy miner engagement (goal 7) never happened");
        Assert.True(acceptPrompts >= 1, "goal 8/9 accept prompt never reached");
        Assert.True(builderGone, "builder build-complete (goal 9) never resolved");
        Assert.True(reachedLand, "landing prompt (goal 10) never reached");

        mission.ShipLanded();      // Mission4::ShipLanded -> Terminate (mission4.cpp:98-105)
        Assert.True(mission.Frame(), "Mission 4 should report completion after landing");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Host-level: the new M3/M4 seams land real effects on the embedded host
    // ─────────────────────────────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Mission34_Seams_LandRealEffects_OnTheHost()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);

        // CreateDrone carries the wingman behaviour / station type / tech level (createdroneaction.cpp:91-198).
        int drone = host.CreateDrone("Scout", 2200, 310, 1, 2, behaviour: 0x50, stationType: -1, techLevel: 1);
        Assert.True(host.Drones.ContainsKey(2200));

        // IsDestroyed: a live drone is not destroyed; DestroyDrone makes it so (armour fraction -> 0).
        Assert.False(host.IsDestroyed(2200));
        host.DestroyDrone(2200);
        Assert.True(host.IsDestroyed(2200));
        Assert.True(new TrainingHostSite(host).IsShipDestroyed(new TargetRef(ObjectType.Ship, 2200)));

        // Command-target + sector + scanner seams resolve against the real host.
        host.SetCommandTarget(host.PlayerShipId, new TargetRef(ObjectType.Ship, host.CommanderShipId), CmdAttack);
        Assert.Equal(CmdAttack, host.GetCommandId(host.PlayerShipId));
        Assert.Equal(new TargetRef(ObjectType.Ship, host.CommanderShipId), host.GetCommandTarget(host.PlayerShipId));
        Assert.NotEqual(-1, host.GetSectorId(host.PlayerShipId));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // driver helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Point the player ship at its current command target so the ObjectPointingAtCondition
    /// turn-to-face gates (M3 goals 5/7/8) resolve — the same trick the TR-2b Mission-2 test uses.</summary>
    private static void OrientPlayerToTarget(TrainingHost host)
    {
        var t = host.GetCommandTarget(host.PlayerShipId);
        if (t is not { } tr) return;
        var resolved = new TrainingHostSite(host).Resolve(tr);
        if (resolved is null) return;
        var d = resolved.Position - ToSys(host.PlayerShip.Position);
        if (d.LengthSquared() < 1e-6f) return;
        var o = host.PlayerShip.Orientation;
        var dn = d / d.Length();
        o.Set(new Coll.Vector3(dn.X, dn.Y, dn.Z), new Coll.Vector3(0f, 0f, 1f));
    }

    private static Vector3 ToSys(Coll.Vector3 v) => new(v.X, v.Y, v.Z);
}
