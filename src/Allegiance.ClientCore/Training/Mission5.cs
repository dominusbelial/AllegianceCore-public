using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 5 — "Command View" — a faithful translation of <c>src/training/mission5.cpp</c> into the
/// ported goal/condition/action framework. Teaches the command map: selecting and ordering drones
/// (builders, miners, a stealth-fighter wingman), the Build / Mine / Go-To / Attack commands
/// (<c>GetCommandCondition</c> on <c>c_cidBuild</c>/<c>c_cidMine</c>/<c>c_cidGoto</c>/<c>c_cidAttack</c>),
/// the sector map view (<c>GetViewSectorCondition</c>), picking objects (<c>GetPickedCondition</c>) and
/// the Team display "Take Me To" (<c>ShowPaneAction</c>). Five sub-goals, matching native
/// (<c>mission5.cpp:174-178</c>). Pure data against an injected <see cref="ITrainingSite"/>; the
/// embedded host realises the drones and command/view state.
/// </summary>
public class Mission5 : TrainingMission
{
    private const int StartSector = 1050;   // Neptune (mission5.cpp:80)
    private const int MarsSector = 1051;    // GetStartSectorID()+1 — where miners/scout patrol
    private const int PlayerHull = 412;     // interceptor

    // Pilot types (igc.h:686-691).
    private const int PilotMiner = 0;
    private const int PilotWingman = 2;
    private const int PilotLayer = 5;

    // Stable ship ids — native GenerateNewShipID (mission5.cpp:163-171); fixed so the test driver
    // and the host can reference the same drones.
    public int EnemyScoutId { get; private set; }
    public int Miner1Id { get; private set; }
    public int Miner2Id { get; private set; }
    public int Miner3Id { get; private set; }
    public int Miner4Id { get; private set; }
    public int ScoutId { get; private set; }
    public int Builder1Id { get; private set; }
    public int Builder2Id { get; private set; }
    public int Builder3Id { get; private set; }

    private const int DefaultEnemyScoutId = 3001;
    private const int DefaultMiner1Id = 3002;
    private const int DefaultMiner2Id = 3003;
    private const int DefaultMiner3Id = 3004;
    private const int DefaultMiner4Id = 3005;
    private const int DefaultScoutId = 3006;
    private const int DefaultBuilder1Id = 3007;
    private const int DefaultBuilder2Id = 3008;
    private const int DefaultBuilder3Id = 3009;

    // Mission 5 voice-over / commentary sound ids (tm_5_XX / repeat variants, mission5.cpp).
    private const int Tm5_01 = 5001, Tm5_02 = 5002, Tm5_03 = 5003, Tm5_03r = 5004,
        Tm5_04 = 5005, Tm5_04r = 5006, Tm5_05 = 5007, Tm5_05r = 5008,
        Tm5_06 = 5009, Tm5_06r = 5010, Tm5_07 = 5011, Tm5_07r = 5012,
        Tm5_08 = 5013, Tm5_08r = 5014, Tm5_09 = 5015, Tm5_10 = 5016,
        Tm5_11 = 5017, Tm5_12 = 5018, Tm5_13 = 5019, Tm5_13r = 5020,
        Tm5_14 = 5021, Tm5_15 = 5022, Tm5_15r = 5023, Tm5_16 = 5024,
        Tm5_17 = 5025, Tm5_18 = 5026, Tm5_18r = 5027, Tm5_19 = 5028, Tm5_20 = 5029;

    public Mission5(ITrainingSite site) : base(site) { }

    public override int GetMissionID() => Tm5CommandView;
    public override int GetStartSectorId() => StartSector;

    /// <summary>M5 enables the command view (mission5.cpp:160 <c>m_commandViewEnabled = true</c>), so the
    /// command-map keys pass the <see cref="TrainingMission.RecordKeyPress"/> gate (trainingmission.cpp:397-401).</summary>
    public override bool CommandViewEnabled => true;

    protected override void CreateUniverse()
    {
        // Host-side universe construction (LoadUniverse("training_5", 412, 1050) + the commander docked
        // in station 1050, mission5.cpp:134-146) is realised by the embedded host from the CommandView variant.
    }

    protected override Condition CreateMission()
    {
        EnemyScoutId = DefaultEnemyScoutId;
        Miner1Id = DefaultMiner1Id;
        Miner2Id = DefaultMiner2Id;
        Miner3Id = DefaultMiner3Id;
        Miner4Id = DefaultMiner4Id;
        ScoutId = DefaultScoutId;
        Builder1Id = DefaultBuilder1Id;
        Builder2Id = DefaultBuilder2Id;
        Builder3Id = DefaultBuilder3Id;

        var goalList = new GoalList();
        var root = new Goal(goalList);

        // Seed the drones as start actions on the top goal, exactly as native CreateMission does
        // (mission5.cpp:181-247): three tower builders + four miners + the stealth-fighter scout,
        // then "do nothing" commands on every drone but Miner 01.
        var neptune = Site.Resolve(new TargetRef(ObjectType.Station, StartSector))?.Position ?? Vector3.Zero;
        var mars = Site.Resolve(new TargetRef(ObjectType.Station, MarsSector))?.Position ?? Vector3.Zero;

        root.AddStartAction(BuildDrone($"Tower Layer 01", Builder1Id, 418, PilotLayer, StartSector, neptune, 120f));
        root.AddStartAction(BuildDrone($"Tower Layer 02", Builder2Id, 418, PilotLayer, StartSector, neptune, -120f));
        root.AddStartAction(BuildDrone($"Tower Layer 03", Builder3Id, 418, PilotLayer, StartSector, neptune, 240f));

        root.AddStartAction(BuildDrone($"Miner 01", Miner1Id, 438, PilotMiner, StartSector, neptune, 300f));
        root.AddStartAction(BuildDrone($"Miner 02", Miner2Id, 438, PilotMiner, MarsSector, mars, 300f));
        root.AddStartAction(BuildDrone($"Miner 03", Miner3Id, 438, PilotMiner, MarsSector, mars, -300f));
        root.AddStartAction(BuildDrone($"Miner 04", Miner4Id, 438, PilotMiner, MarsSector, mars, 500f));

        // Scout placed between Mars station and the Mars aleph (mission5.cpp:221-224).
        var marsWarp = Site.Resolve(new TargetRef(ObjectType.Ship, 1052))?.Position
                       ?? mars + new Vector3(400f, 0f, 0f);
        root.AddStartAction(BuildDrone($"Stealth Fighter", ScoutId, 411, PilotWingman, MarsSector,
            mars + (marsWarp - mars) * 0.6f, 0f));

        // Tell everyone except Miner 01 to do nothing (mission5.cpp:227-246).
        foreach (int id in new[] { ScoutId, Miner2Id, Miner3Id, Miner4Id, Builder1Id, Builder2Id, Builder3Id })
        {
            root.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandAccepted, null, CmdDoNothing));
            root.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandCurrent, null, CmdDoNothing));
        }

        goalList.AddGoal(CreateGoal01());
        goalList.AddGoal(CreateGoal02());
        goalList.AddGoal(CreateGoal03());
        goalList.AddGoal(CreateGoal04());
        goalList.AddGoal(CreateGoal05());

        return root;
    }

    private CreateDroneAction BuildDrone(string name, int shipId, int hullId, int pilotType, int sector,
        Vector3 anchor, float offset)
    {
        var cda = new CreateDroneAction(Site, name, shipId, hullId, 0, pilotType);
        cda.SetCreatedLocation(sector, anchor + new Vector3(offset, offset * 0.5f, offset * 0.25f));
        return cda;
    }

    // Goal 01 (mission5.cpp:252-355): command-view intro, SPACEBAR, pick a tower layer, Build command,
    // deploy, and order all three towers to defend.
    private Goal CreateGoal01()
    {
        var list = new GoalList();

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(1.0f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm5_01));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_02 — "Press the SPACEBAR when you are ready to proceed."
        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_02));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_02));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm5_03));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_03r — left-click a tower layer (GetPickedCondition, mission5.cpp:294-296).
        {
            var orA = new OrCondition(new GetPickedCondition(Site, ObjectType.Ship, Builder1Id),
                                      new GetPickedCondition(Site, ObjectType.Ship, Builder2Id));
            var orB = new GetPickedCondition(Site, ObjectType.Ship, Builder3Id);
            var goal = new Goal(new OrCondition(orA, orB));
            goal.AddStartAction(new MessageAction(Site, "Left click on a Tower Layer to select it."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_03r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_03r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_04 — "Left click on 'Build' command."
        {
            var goal = CreatePlaySoundGoal(Tm5_04);
            goal.AddStartAction(new MessageAction(Site, "Left click on 'Build' command."));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_04r — right-click to deploy (GetCommandCondition c_cidBuild on any builder).
        {
            var orA = new OrCondition(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Builder1Id), CmdBuild),
                                      new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Builder2Id), CmdBuild));
            var orB = new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Builder3Id), CmdBuild);
            var goal = new Goal(new OrCondition(orA, orB));
            goal.AddStartAction(new MessageAction(Site, "Right click in space to deploy tower."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_04r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_04r));
            list.AddGoal(goal);
        }

        // tm_5_05 — "Order all towers to defend aleph 'Mars'."
        {
            var goal = CreatePlaySoundGoal(Tm5_05);
            goal.AddStartAction(new MessageAction(Site, "Order all towers to defend aleph 'Mars'."));
            list.AddGoal(goal);
        }

        // tm_5_05r — all three builders carry a Build order (mission5.cpp:344-346).
        {
            var andA = new AndCondition(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Builder1Id), CmdBuild),
                                        new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Builder2Id), CmdBuild));
            var andB = new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Builder3Id), CmdBuild);
            var goal = new Goal(new AndCondition(andA, andB));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_05r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        return new Goal(list);
    }

    // Goal 02 (mission5.cpp:358-465): view sector Mars, pick a miner, Mine command, all miners working.
    private Goal CreateGoal02()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm5_06));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_06r — left-click Mars in the sector map to change view (GetViewSectorCondition 1051).
        {
            var goal = new Goal(new GetViewSectorCondition(Site, MarsSector));
            goal.AddStartAction(new MessageAction(Site, "Left click on 'Mars' in the Sector Map to change view."));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlaySector));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_06r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_06r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_07 — pick a miner (GetPickedCondition on miner 2/3/4).
        {
            var goal = CreatePlaySoundGoal(Tm5_07);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            goal.AddStartAction(new MessageAction(Site, "Left click on a Miner to select it."));
            list.AddGoal(goal);
        }

        {
            var orA = new OrCondition(new GetPickedCondition(Site, ObjectType.Ship, Miner2Id),
                                      new GetPickedCondition(Site, ObjectType.Ship, Miner3Id));
            var orB = new GetPickedCondition(Site, ObjectType.Ship, Miner4Id);
            var goal = new Goal(new OrCondition(orA, orB));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_07r));
            list.AddGoal(goal);
        }

        // tm_5_08 — Mine command + right-click the He3 asteroid.
        {
            var goal = CreatePlaySoundGoal(Tm5_08);
            goal.AddStartAction(new MessageAction(Site, "Left click on 'Mine' command, and right click on the He3 asteroid to begin mining."));
            list.AddGoal(goal);
        }

        // tm_5_08r — any miner carries a Mine order (c_cidMine).
        {
            var orA = new OrCondition(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner2Id), CmdMine),
                                      new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner3Id), CmdMine));
            var orB = new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner4Id), CmdMine);
            var goal = new Goal(new OrCondition(orA, orB));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_08r));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm5_09));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_10 — "Order all the miners to work." (all three carry Mine).
        {
            var andA = new AndCondition(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner2Id), CmdMine),
                                        new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner3Id), CmdMine));
            var andB = new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner4Id), CmdMine);
            var goal = new Goal(new AndCondition(andA, andB));
            goal.AddStartAction(new MessageAction(Site, "Order all the miners to work."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_10));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_10));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm5_11));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        return new Goal(list);
    }

    // Goal 03 (mission5.cpp:468-532): enemy scout appears; send the three miners back to base (c_cidGoto).
    private Goal CreateGoal03()
    {
        var list = new GoalList();

        // tm_5_12 — create the enemy scout (side 1), tell it to do nothing, set its signature to infinity.
        {
            var goal = CreatePlaySoundGoal(Tm5_12);
            var mars = Site.Resolve(new TargetRef(ObjectType.Station, MarsSector))?.Position ?? Vector3.Zero;
            var cda = new CreateDroneAction(Site, "Enemy Scout", EnemyScoutId, 310, 1, PilotWingman);
            cda.SetCreatedLocation(MarsSector, mars + new Vector3(400f, 100f, 0f)); // near the Mars aleph
            goal.AddStartAction(cda);
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), CommandAccepted, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), CommandCurrent, null, CmdDoNothing));
            goal.AddStartAction(new SetSignatureAction(Site, ObjectType.Ship, EnemyScoutId, 1.0e6f)); // mission5.cpp:495
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm5_13));

        // tm_5_13r — "Order all three miners back to base for safety." (all three carry c_cidGoto).
        {
            var andA = new AndCondition(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner2Id), CmdGoto),
                                        new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner3Id), CmdGoto));
            var andB = new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, Miner4Id), CmdGoto);
            var goal = new Goal(new AndCondition(andA, andB));
            goal.AddStartAction(new MessageAction(Site, "Order all three miners back to base for safety."));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_13r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm5_14));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        return new Goal(list);
    }

    // Goal 04 (mission5.cpp:535-573): order the stealth fighter to attack the enemy scout; it destroys it.
    private Goal CreateGoal04()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm5_15));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_15r — "Order stealth fighter to attack enemy craft." (GetCommandCondition c_cidAttack on the scout).
        {
            var goal = new Goal(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, ScoutId), CmdAttack));
            goal.AddStartAction(new MessageAction(Site, "Order stealth fighter to attack enemy craft."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_15r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_15r));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm5_16));

        // Wait for the enemy scout to be destroyed (GetShipIsDestroyedCondition2, mission5.cpp:568).
        list.AddGoal(new Goal(new GetShipIsDestroyedCondition2(Site, ObjectType.Ship, EnemyScoutId)));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        return new Goal(list);
    }

    // Goal 05 (mission5.cpp:577-633): Team display, "Take Me To" Miner 01 (view back to Neptune).
    private Goal CreateGoal05()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm5_17));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_18 — Show the Team (expanded) pane.
        {
            var goal = CreatePlaySoundGoal(Tm5_18);
            goal.AddStartAction(new ShowPaneAction(Site, PaneExpandedTeam));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_5_18r — select Miner 01 and "Take Me To" (view returns to Neptune 1050).
        {
            var goal = new Goal(new GetViewSectorCondition(Site, StartSector));
            goal.AddStartAction(new MessageAction(Site, "Select Miner 01 in the Team Display, and click on the 'Take Me To' button."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm5_18r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm5_18r));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm5_19));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm5_20));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        return new Goal(list);
    }
}
