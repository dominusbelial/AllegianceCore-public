using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 10 — "Free Flight" — a faithful translation of <c>src/training/mission10.cpp</c>, which the
/// native header describes as "Cloned mission 6 without intro and with less constraints"
/// (<c>mission10.cpp:6</c>). Same <c>training_6</c>/488/2080 universe and the same escalating
/// kill-goal waves as <see cref="Mission6"/>, but the friendly seed is placed forward to draw aggro and
/// the long intro audio is skipped. Natively Free Flight has no button on the training screen — it is a
/// dev/debug mission launched through the <c>bStartTraining</c> flag (<c>trainingscreen.cpp:105-108</c>),
/// and <c>TrainingMission.RecordKeyPress</c> already lets every hard-disabled key through for it
/// (<c>trainingmission.cpp:388-396</c>). The kill count is the mission's real kill credit
/// (<c>TrainingMission::ShipKilled</c>, trainingmission.cpp:515-524), fed by the server's kill-credit path.
/// </summary>
public class Mission10 : TrainingMission
{
    // training/mission10.cpp:56-63: same death-camera termination as M6.
    public override bool RestoreShip() { Terminate(); return false; }
    private const int StartSector = 2080;   // Home (mission10.cpp:52)
    private const int MarsSector = 2081;    // "Mars" sector
    private const int EnemySector = 2082;   // "Enemy" sector
    private const int PlayerHull = 488;     // Adv. IC fighter

    private const int PilotMiner = 0;
    private const int PilotWingman = 2;
    private const int PilotCarrier = 9;

    public int Miner01Id { get; private set; }
    public int FriendlyCarrierId { get; private set; }
    public int EnemyCarrierId { get; private set; }
    public int Enemy1Id { get; private set; }   // "Enemy Scout"
    public int Enemy2Id { get; private set; }   // "Enemy Fighter"
    public int Enemy3Id { get; private set; }   // "Enemy Bomber"
    public int Enemy4Id { get; private set; }   // "Enemy Hvy Bomber"

    private const int DefaultMiner01 = 4001;
    private const int DefaultCarrier = 4002;
    private const int DefaultEnemyCarrier = 4003;
    private const int DefaultEnemy1 = 4004;
    private const int DefaultEnemy2 = 4005;
    private const int DefaultEnemy3 = 4006;
    private const int DefaultEnemy4 = 4007;

    private CreateDroneAction _enemy1Action = null!;
    private CreateDroneAction _enemy2Action = null!;
    private CreateDroneAction _enemy3Action = null!;
    private CreateDroneAction _enemy4Action = null!;

    private const int SalCommenceScanSound = 2000;
    private const int Tm6_02 = 6002, Tm6_03 = 6003;
    private const int DroneInTransitSound = 6004;
    private const int VoAwesomeSound = 6005;

    public Mission10(ITrainingSite site) : base(site) { }

    public override int GetMissionID() => Tm10FreeFlight;
    public override int GetStartSectorId() => StartSector;
    public override bool CommandViewEnabled => true;   // mission10.cpp:372

    protected override void CreateUniverse()
    {
        // Host-side universe construction (LoadUniverse("training_6",488,2080), group fire, tech and
        // seeker-2 loadout — mission10.cpp:288-366) is realised by the embedded host from the
        // FreeFlight variant (same training_6/488/2080 as Practice Arena).
    }

    protected override Condition CreateMission()
    {
        Miner01Id = DefaultMiner01;
        FriendlyCarrierId = DefaultCarrier;
        EnemyCarrierId = DefaultEnemyCarrier;
        Enemy1Id = DefaultEnemy1;
        Enemy2Id = DefaultEnemy2;
        Enemy3Id = DefaultEnemy3;
        Enemy4Id = DefaultEnemy4;

        var goalList = new GoalList();
        var home = Site.Resolve(new TargetRef(ObjectType.Station, StartSector))?.Position ?? Vector3.Zero;

        // Friendly Miner 01 — forward to draw aggro, then respawns at base (mission10.cpp:393-412).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.1f, Site));
            var miner = new CreateDroneAction(Site, "Miner 01", Miner01Id, 438, 0, PilotMiner);
            miner.SetCreatedLocation(StartSector, new Vector3(0f, 2500f, 100f));
            goal.AddStartAction(new AddRespawnAction(this, miner, 300.0f));
            goal.AddStartAction(new MessageAction(Site, "Hint: Press F1 to see an overview of your ship's controls."));
            var modMiner = new ModifyCreateDroneActionAction(miner);
            modMiner.ModifyCreatedLocation(StartSector, new Vector3(home.X + 250f, home.Y + 200f, home.Z));
            goal.AddStartAction(modMiner);
            goalList.AddGoal(goal);
        }

        // Friendly carrier (mission10.cpp:414-423).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.1f, Site));
            var cda = new CreateDroneAction(Site, "Carrier", FriendlyCarrierId, 425, 0, PilotCarrier);
            cda.SetCreatedLocation(StartSector, new Vector3(-3000f, 1500f, 250f));
            cda.SetCreatedOrientation(new Vector3(0.3f, 1.0f, 0.0f), new Vector3(0f, 0f, 1f));
            goal.AddStartAction(cda);
            goalList.AddGoal(goal);
        }

        // Introductory audio + cockpit camera (mission10.cpp:425-430).
        {
            var g = CreatePlaySoundGoal(SalCommenceScanSound);
            g.AddStartAction(new SetDisplayModeAction(Site, CameraCockpit));
            goalList.AddGoal(g);
        }

        // Friendly Miner 02 (mission10.cpp:433-447).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.2f, Site));
            var cda = new CreateDroneAction(Site, "Miner 02", GenId(4101), 438, 0, PilotMiner);
            cda.SetCreatedLocation(StartSector, new Vector3(home.X + 250f, home.Y + 200f, home.Z));
            goal.AddStartAction(cda);
            goalList.AddGoal(goal);
        }

        goalList.AddGoal(new Goal(ElapsedTimeCondition.Create(1.0f, Site)));

        // tm_6_02 — watch the chat (mission10.cpp:451-459).
        {
            var g = CreatePlaySoundGoal(Tm6_02);
            g.AddStartAction(new MessageAction(Site, "Watch the chat to see what the enemy craft are after."));
            goalList.AddGoal(g);
        }

        // Friendly Miner 03 (mission10.cpp:461-475).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.2f, Site));
            var cda = new CreateDroneAction(Site, "Miner 03", GenId(4102), 438, 0, PilotMiner);
            cda.SetCreatedLocation(StartSector, new Vector3(home.X - 250f, home.Y - 200f, home.Z));
            goal.AddStartAction(cda);
            goalList.AddGoal(goal);
        }

        // Enemy carrier (mission10.cpp:477-487).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            var cda = new CreateDroneAction(Site, "Enemy Carrier", EnemyCarrierId, 325, 1, PilotCarrier);
            cda.SetCreatedLocation(MarsSector, new Vector3(3560f, 4880f, 850f));
            goal.AddStartAction(cda);
            goalList.AddGoal(goal);
        }

        // Enemy spawn actions (mission10.cpp:489-504): scout + fighter that respawn near the carrier.
        int nearNeptuneAlephBuoy = GenId(5001);
        int homeSectorBuoy = GenId(5002);
        _enemy1Action = new CreateDroneAction(Site, "Enemy Scout", Enemy1Id, 310, 1, PilotWingman);
        _enemy1Action.SetCreatedLocation(EnemySector, new Vector3(-140f, 280f, -20f));
        _enemy1Action.SetCreatedBehaviour(0);
        _enemy2Action = new CreateDroneAction(Site, "Enemy Fighter", Enemy2Id, 315, 1, PilotWingman);
        _enemy2Action.SetCreatedLocation(EnemySector, new Vector3(-170f, 280f, -30f));
        _enemy2Action.SetCreatedBehaviour(0);

        // Start spawning enemies (mission10.cpp:506-520).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var carrierAction = new CreateDroneAction(Site, "Enemy Carrier", EnemyCarrierId, 325, 1, PilotCarrier);
            carrierAction.SetCreatedLocation(EnemySector, new Vector3(-1000f, 280f, -50f));
            carrierAction.SetCreatedCommand(nearNeptuneAlephBuoy, ObjectType.Buoy, CmdGoto);
            goal.AddStartAction(new CreateWaypointAction(Site, homeSectorBuoy, Vector3.Zero, StartSector, 0 /*c_buoyCluster*/, false));
            goal.AddStartAction(new AddRespawnAction(this, _enemy1Action, 10.0f, 1));
            goal.AddStartAction(new AddRespawnAction(this, _enemy2Action, 10.0f, 1));
            goal.AddStartAction(new CreateWaypointAction(Site, nearNeptuneAlephBuoy, new Vector3(3560f, 4880f, 850f), MarsSector, 1 /*c_buoyWaypoint*/, false));
            goal.AddStartAction(new AddRespawnAction(this, carrierAction, 60.0f, -1, false));
            goalList.AddGoal(goal);
        }

        // tm_6_03 — "DEFEND THE MINERS!"
        goalList.AddGoal(CreatePlaySoundGoal(Tm6_03));
        goalList.AddGoal(new Goal(ElapsedTimeCondition.Create(8.0f, Site)));

        // Enemy miners (mission10.cpp:529-547).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(4.0f, Site));
            var em1 = new CreateDroneAction(Site, "Enemy Miner 01", GenId(4103), 338, 1, PilotMiner);
            em1.SetCreatedLocation(EnemySector, new Vector3(3080f, -440f, -700f));
            goal.AddStartAction(new AddRespawnAction(this, em1, 300.0f));
            var em2 = new CreateDroneAction(Site, "Enemy Miner 02", GenId(4104), 338, 1, PilotMiner);
            em2.SetCreatedLocation(EnemySector, new Vector3(-1760f, -3240f, -700f));
            goal.AddStartAction(new AddRespawnAction(this, em2, 300.0f));
            goalList.AddGoal(goal);
        }

        // Friendly Miner 04 (mission10.cpp:548-562).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            var cda = new CreateDroneAction(Site, "Miner 04", GenId(4105), 438, 0, PilotMiner);
            cda.SetCreatedLocation(StartSector, new Vector3(home.X + 250f, home.Y + 200f, home.Z));
            goal.AddStartAction(cda);
            goalList.AddGoal(goal);
        }

        // Wave 1 (mission10.cpp:565): 2 kills.
        goalList.AddGoal(new Goal(new KillsReachedCondition(this, 2)));

        // Wave 2 (mission10.cpp:569-579).
        {
            var goal = new Goal(new KillsReachedCondition(this, 4));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedBehaviour(WingmanUseMissiles);
            f.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanRunAt30Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 3 (mission10.cpp:583-594).
        {
            var goal = new Goal(new KillsReachedCondition(this, 6));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedTechLevel(2);
            s.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanRunAt30Hull);
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanRunAt60Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 4 (mission10.cpp:598-607): fighter -> interceptor.
        {
            var goal = new Goal(new KillsReachedCondition(this, 8));
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            f.ModifyCreatedHullType(324);
            f.ModifyCreatedName("Enemy Lt Interceptor");
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 5 (mission10.cpp:611-621): strafe.
        {
            var goal = new Goal(new KillsReachedCondition(this, 10));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanStrafe | WingmanRunAt60Hull);
            f.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanRunAt30Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 6 (mission10.cpp:626-637).
        {
            var goal = new Goal(new KillsReachedCondition(this, 12));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            f.ModifyCreatedTechLevel(2);
            s.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe | WingmanRunAt60Hull);
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe | WingmanRunAt30Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 7 (mission10.cpp:641-655): two fighters.
        {
            var goal = new Goal(new KillsReachedCondition(this, 14));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedHullType(315); s.ModifyCreatedName("Enemy Fighter");
            f.ModifyCreatedHullType(315); f.ModifyCreatedName("Enemy Fighter 2");
            s.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanStrafe | WingmanRunAt60Hull);
            f.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanRunAt30Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 8 (mission10.cpp:659-671).
        {
            var goal = new Goal(new KillsReachedCondition(this, 16));
            var s = new ModifyCreateDroneActionAction(_enemy2Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            f.ModifyCreatedTechLevel(2);
            f.ModifyCreatedHullType(314); f.ModifyCreatedName("Enemy Enh Fighter");
            s.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe | WingmanRunAt60Hull);
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanRunAt30Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 9 (mission10.cpp:675-685).
        {
            var goal = new Goal(new KillsReachedCondition(this, 18));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            s.ModifyCreatedTechLevel(2);
            s.ModifyCreatedHullType(314); s.ModifyCreatedName("Enemy Enh Fighter 2");
            s.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe);
            goal.AddStartAction(s);
            goalList.AddGoal(goal);
        }

        // Bomb run 1 (mission10.cpp:690-717): enemy bomber with repairing scouts.
        _enemy3Action = new CreateDroneAction(Site, "Enemy Bomber", Enemy3Id, 304, 1, PilotWingman);
        _enemy3Action.SetCreatedLocation(EnemySector, new Vector3(-170f, 280f, 0f));
        _enemy3Action.SetCreatedCommand(StartSector, ObjectType.Station, CmdAttack);
        _enemy3Action.SetCreatedBehaviour(WingmanUseMissiles);
        {
            var goal = new Goal(ElapsedTimeCondition.Create(15.0f, Site));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedHullType(385); s.ModifyCreatedName("Enemy Adv Scout");
            f.ModifyCreatedHullType(385); f.ModifyCreatedName("Enemy Adv Scout 2");
            s.ModifyCreatedCommand(Enemy3Id, ObjectType.Ship, CmdRepair);
            f.ModifyCreatedCommand(Enemy3Id, ObjectType.Ship, CmdRepair);
            s.ModifyCreatedTechLevel(-1);
            f.ModifyCreatedTechLevel(-2);
            s.ModifyCreatedBehaviour(0);
            f.ModifyCreatedBehaviour(0);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(new AddRespawnAction(this, _enemy3Action, 10.0f, 1, false));
            goalList.AddGoal(goal);
        }

        // Bomber in home or dead (mission10.cpp:719-720).
        goalList.AddGoal(new Goal(new OrCondition(
            new GetSectorCondition(Site, new TargetRef(ObjectType.Ship, Enemy3Id), StartSector),
            NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, Enemy3Id, 0.0f)))));

        // Scout in home or dead, with a "protect the base" warning (mission10.cpp:722-727).
        {
            var goal = new Goal(new OrCondition(
                new GetSectorCondition(Site, new TargetRef(ObjectType.Ship, Enemy2Id), StartSector),
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, Enemy2Id, 0.0f))));
            goal.AddConstraintCondition(new ConditionalAction(
                new GetShipIsDamagedCondition(Site, ObjectType.Ship, Enemy3Id, 0.0f),
                new MessageAction(Site, "They are trying to destroy our base! Stop them!"), false, true));
            goalList.AddGoal(goal);
        }

        // 21 kills (mission10.cpp:728-732).
        {
            var goal = new Goal(new KillsReachedCondition(this, 21));
            goal.AddConstraintCondition(new ConditionalAction(
                new GetShipIsDamagedCondition(Site, ObjectType.Ship, Enemy2Id, 0.0f),
                new MessageAction(Site, "The scouts can repair the bomber. Shoot them first."), false, true));
            goalList.AddGoal(goal);
        }

        // Wave 11 (mission10.cpp:736-747).
        {
            var goal = new Goal(new KillsReachedCondition(this, 24));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedTechLevel(-2);
            s.ModifyCreatedBehaviour(WingmanUseMines);
            f.ModifyCreatedBehaviour(WingmanUseMines | WingmanStrafe);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 12 (mission10.cpp:751-771): three scouts.
        {
            var goal = new Goal(new KillsReachedCondition(this, 27));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            var b = new ModifyCreateDroneActionAction(_enemy3Action);
            s.ModifyCreatedCommand(-1, ObjectType.Invalid, CmdNone);
            f.ModifyCreatedCommand(-1, ObjectType.Invalid, CmdNone);
            b.ModifyCreatedCommand(-1, ObjectType.Invalid, CmdNone);
            s.ModifyCreatedTechLevel(2);
            f.ModifyCreatedTechLevel(2);
            s.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles);
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe);
            b.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanRunAt30Hull);
            b.ModifyCreatedHullType(385); b.ModifyCreatedName("Enemy Adv Scout 3");
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(b);
            goalList.AddGoal(goal);
        }

        // Wave 13 (mission10.cpp:775-793).
        {
            var goal = new Goal(new KillsReachedCondition(this, 30));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            s.ModifyCreatedTechLevel(2);
            f.ModifyCreatedTechLevel(3);
            var b = new ModifyCreateDroneActionAction(_enemy3Action);
            b.ModifyCreatedTechLevel(3);
            b.ModifyCreatedHullType(86); b.ModifyCreatedName("Enemy Hvy Scout");
            s.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe | WingmanRunAt30Hull);
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe);
            b.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(b);
            goalList.AddGoal(goal);
        }

        // Wave 14 (mission10.cpp:797-806).
        {
            var goal = new Goal(new KillsReachedCondition(this, 33));
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            f.ModifyCreatedTechLevel(1);
            f.ModifyCreatedHullType(312); f.ModifyCreatedName("Enemy Interceptor");
            goal.AddStartAction(f);
            goalList.AddGoal(goal);
        }

        // Wave 15 (mission10.cpp:810-831).
        {
            var goal = new Goal(new KillsReachedCondition(this, 36));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            var b = new ModifyCreateDroneActionAction(_enemy3Action);
            s.ModifyCreatedName("Enemy Adv Scout");
            s.ModifyCreatedTechLevel(2);
            f.ModifyCreatedHullType(312); f.ModifyCreatedName("Enemy Interceptor"); f.ModifyCreatedTechLevel(1);
            b.ModifyCreatedHullType(312); b.ModifyCreatedName("Enemy Interceptor 2"); b.ModifyCreatedTechLevel(2);
            s.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanStrafe | WingmanRunAt30Hull);
            f.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanStrafe);
            b.ModifyCreatedBehaviour(WingmanUseMissiles);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(b);
            goalList.AddGoal(goal);
        }

        // Wave 16 (mission10.cpp:835-857): move carrier to home.
        {
            var goal = new Goal(new KillsReachedCondition(this, 39));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            var b = new ModifyCreateDroneActionAction(_enemy3Action);
            s.ModifyCreatedTechLevel(-2);
            s.ModifyCreatedCommand(Enemy2Id, ObjectType.Ship, CmdRepair);
            f.ModifyCreatedTechLevel(2);
            s.ModifyCreatedBehaviour(WingmanUseMissiles | WingmanStrafe | WingmanRunAt30Hull);
            f.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles);
            b.ModifyCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles | WingmanStrafe | WingmanRunAt30Hull);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(b);
            int insideHomeBuoy = GenId(5003);
            goal.AddStartAction(new CreateWaypointAction(Site, insideHomeBuoy, new Vector3(1500f, 2500f, 300f), StartSector, 1, false));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, EnemyCarrierId), CommandAccepted,
                new TargetRef(ObjectType.Buoy, insideHomeBuoy), CmdGoto));
            goalList.AddGoal(goal);
        }

        // Wave 17 (mission10.cpp:861-873).
        {
            var goal = new Goal(new KillsReachedCondition(this, 42));
            var i = new ModifyCreateDroneActionAction(_enemy2Action);
            var i2 = new ModifyCreateDroneActionAction(_enemy3Action);
            i.ModifyCreatedHullType(381); i.ModifyCreatedName("Enemy Hvy Interceptor");
            i2.ModifyCreatedName("Enemy Interceptor");
            goal.AddStartAction(i);
            goal.AddStartAction(i2);
            goalList.AddGoal(goal);
        }

        // Wave 18 (mission10.cpp:877-897).
        {
            var goal = new Goal(new KillsReachedCondition(this, 45));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            var b = new ModifyCreateDroneActionAction(_enemy3Action);
            s.ModifyCreatedHullType(388); s.ModifyCreatedName("Enemy Adv Fighter"); s.ModifyCreatedTechLevel(3);
            s.ModifyCreatedCommand(-1, ObjectType.Invalid, CmdNone);
            f.ModifyCreatedHullType(388); f.ModifyCreatedName("Enemy Adv Fighter 2"); f.ModifyCreatedTechLevel(3);
            b.ModifyCreatedHullType(388); b.ModifyCreatedName("Enemy Adv Fighter 3"); b.ModifyCreatedTechLevel(3);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(b);
            goalList.AddGoal(goal);
        }

        // Bomb run 2 (mission10.cpp:901-936): heavy bomber with repairing/hvy-int escort.
        _enemy4Action = new CreateDroneAction(Site, "Enemy Hvy Bomber", Enemy4Id, 389, 1, PilotWingman);
        _enemy4Action.SetCreatedLocation(EnemySector, new Vector3(-170f, 280f, 0f));
        _enemy4Action.SetCreatedCommand(StartSector, ObjectType.Station, CmdAttack);
        _enemy4Action.SetCreatedBehaviour(WingmanUseMissiles | WingmanStrafe);
        _enemy4Action.SetCreatedTechLevel(3);
        {
            var goal = new Goal(ElapsedTimeCondition.Create(13.0f, Site));
            var s = new ModifyCreateDroneActionAction(_enemy1Action);
            var f = new ModifyCreateDroneActionAction(_enemy2Action);
            var b = new ModifyCreateDroneActionAction(_enemy3Action);
            s.ModifyCreatedHullType(86); s.ModifyCreatedName("Enemy Hvy Scout");
            f.ModifyCreatedHullType(86); f.ModifyCreatedName("Enemy Hvy Scout 2");
            s.ModifyCreatedCommand(Enemy4Id, ObjectType.Ship, CmdRepair);
            f.ModifyCreatedCommand(Enemy4Id, ObjectType.Ship, CmdRepair);
            s.ModifyCreatedTechLevel(-3);
            f.ModifyCreatedTechLevel(-3);
            s.ModifyCreatedBehaviour(WingmanUseMines | WingmanStrafe | WingmanRunAt30Hull);
            f.ModifyCreatedBehaviour(WingmanUseMines | WingmanStrafe);
            b.ModifyCreatedHullType(381); b.ModifyCreatedName("Enemy Hvy Interceptor"); b.ModifyCreatedBehaviour(0);
            b.ModifyCreatedCommand(Enemy4Id, ObjectType.Ship, CmdDefend);
            goal.AddStartAction(s);
            goal.AddStartAction(f);
            goal.AddStartAction(b);
            goal.AddStartAction(new AddRespawnAction(this, _enemy4Action, 10.0f, 1, false));
            goalList.AddGoal(goal);
        }

        // 49 kills — "Incoming bomber!" (mission10.cpp:937-948).
        {
            var goal = new Goal(new KillsReachedCondition(this, 49));
            goal.AddStartAction(new MessageAction(Site, "Incoming bomber!"));
            goalList.AddGoal(goal);
        }

        // Victory message (mission10.cpp:950-957).
        {
            var goal = new Goal(ElapsedTimeCondition.Create(9.0f, Site));
            var msg = new MessageAction(Site);
            msg.AddMessage("Congratulations, you managed to defeat all waves!!", VoAwesomeSound);
            goal.AddStartAction(msg);
            goalList.AddGoal(goal);
        }

        return new Goal(goalList);
    }

    private int GenId(int baseId) => baseId;
}
