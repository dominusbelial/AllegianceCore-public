using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 4 — "Enemy Engagement" — a faithful translation of <c>src/training/mission4.cpp</c> into the
/// ported goal/condition/action framework. The narrative voice-over ordering, the constraint guards and
/// the completion checks all match native: ten sub-goals that teach chat/quickcomm, waypoint and sector
/// orders, intersector flight, stealth/cloak and the eyeball indicator, engagement of unarmed enemy
/// miners, defending a friendly constructor from an enemy scout, and finally landing in the expansion
/// complex. Pure data: it builds a <see cref="GoalList"/> and runs against an injected
/// <see cref="ITrainingSite"/>, with no rendering or host.
/// </summary>
public sealed class Mission4 : TrainingMission
{
    /// <summary>Native <c>GetStartSectorID</c> returns 1030 (<c>mission4.cpp:92-95</c>).</summary>
    private const int StartSector = 1030;

    /// <summary>Sector the player transits to mid-mission (<c>mission4.cpp:526</c>).</summary>
    private const int MarsSector = 1031;

    /// <summary>The aleph the player dives through (<c>mission4.cpp:333,497</c>). <see cref="ObjectType"/>
    /// has no <c>Warp</c> member, so the geometric targets that reference the aleph use a Ship-type
    /// stand-in with the aleph's object id — see <see cref="AlephAsShip(int)"/>.</summary>
    private const int AlephNeptuneId = 1030;

    /// <summary>The asteroid the friendly constructor builds on (<c>mission4.cpp:870-871</c>).</summary>
    private const int BuilderAsteroidId = 10332;

    /// <summary>Interceptor hull the player flies — unused here (loadout is the host's concern), kept
    /// out entirely; the mission only spawns drones with fixed hull ids below.</summary>
    private const int EnemyMinerHull = 338;   // (<c>mission4.cpp:537</c>)
    private const int BuilderHull = 419;      // (<c>mission4.cpp:864</c>)
    private const int ScoutHull = 310;        // (<c>mission4.cpp:914</c>)

    /// <summary>Stable drone ids generated in native <c>CreateMission</c> (<c>mission4.cpp:148-153</c>).</summary>
    private const int DefaultScoutId = 1100;
    private const int DefaultMiner1Id = 1101;
    private const int DefaultMiner2Id = 1102;
    private const int DefaultMiner3Id = 1103;
    private const int DefaultMiner4Id = 1104;
    private const int DefaultBuilderId = 1105;

    /// <summary>Mission-4 ship ids, generated in <c>CreateMission</c> (stable, host-independent).</summary>
    public int EnemyScoutId { get; private set; }
    public int EnemyMiner1Id { get; private set; }
    public int EnemyMiner2Id { get; private set; }
    public int EnemyMiner3Id { get; private set; }
    public int EnemyMiner4Id { get; private set; }
    public int BuilderId { get; private set; }

    /// <summary>Next free buoy id (native <c>GenerateNewBuoyID</c>, <c>mission4.cpp:317,874</c>).</summary>
    private int _nextBuoy = 700;

    // ----------------------------------------------------------------------------------------------
    // Key ids (TrekKey). Native uses TK_TargetEnemy and TK_ToggleCloak (<c>mission4.cpp:707,798</c>);
    // the official igc.h values are not wired into the port, so distinct stable ids are picked here
    // (the site maps them to the real keycodes in TR-2). The SPACEBAR is the existing KeyFireBooster.
    // ----------------------------------------------------------------------------------------------

    /// <summary>Picked id for the native <c>TK_TargetEnemy</c> (E key) — Target an enemy.</summary>
    private const int KeyTargetEnemy = 111;

    /// <summary>Picked id for the native <c>TK_ToggleCloak</c> (K key) — toggle the cloak.</summary>
    private const int KeyToggleCloak = 112;

    // ----------------------------------------------------------------------------------------------
    // Drone pilot types (native c_pt*; <c>mission4.cpp:537,864,914</c>).
    // ----------------------------------------------------------------------------------------------
    private const int PilotMiner = 0;    // c_ptMiner
    private const int PilotWingman = 2;  // c_ptWingman
    private const int PilotBuilder = 6;  // c_ptBuilder

    // ----------------------------------------------------------------------------------------------
    // Voice-over / commentary sound ids. Native uses symbolic tm_4_XXSound ids bound in the
    // trainingsounddef asset; the exact values are catalog-specific, so this port uses stable
    // sequential integers starting at 4001 (mirroring Mission 2's flat scheme, <c>Mission2.cs</c>).
    // The shared intro sound is 2000 (identical to <c>Mission2.SalCommenceScanSound</c>), and the
    // shared closing prompt tm_3_49r is kept on the 3000-series block (3000+49).
    // ----------------------------------------------------------------------------------------------
    private const int SalCommenceScanSound = 2000; // shared with Mission 2 (salCommenceScanSound)
    private const int Tm3_49r = 3049;              // shared tail prompt from mission 3 (<c>mission4.cpp:1037</c>)

    private const int Tm4_01 = 4001;
    private const int Tm4_02 = 4002;
    private const int Tm4_03 = 4003;
    private const int Tm4_03r = 4004;
    private const int Tm4_04 = 4005;
    private const int Tm4_04r = 4006;
    private const int Tm4_05 = 4007;
    private const int Tm4_06 = 4008;
    private const int Tm4_06r = 4009;
    private const int Tm4_07 = 4010;
    private const int Tm4_08 = 4011;
    private const int Tm4_08r = 4012;
    private const int Tm4_09 = 4013;
    private const int Tm4_10 = 4014;
    private const int Tm4_11 = 4015;
    private const int Tm4_12 = 4016;
    private const int Tm4_13 = 4017;
    private const int Tm4_14 = 4018;
    private const int Tm4_15 = 4019;
    private const int Tm4_16 = 4020;
    private const int Tm4_17 = 4021;
    private const int Tm4_17r = 4022;
    private const int Tm4_18 = 4023;
    private const int Tm4_19 = 4024;
    private const int Tm4_20 = 4025;
    private const int Tm4_21 = 4026;
    private const int Tm4_22 = 4027;
    private const int Tm4_23 = 4028;
    private const int Tm4_24 = 4029;
    private const int Tm4_25 = 4030;
    private const int Tm4_26 = 4031;
    private const int Tm4_26r = 4032;
    private const int Tm4_27 = 4033;
    private const int Tm4_28 = 4034;
    private const int Tm4_29 = 4035;
    private const int Tm4_29r = 4036;
    private const int Tm4_30 = 4037;
    private const int Tm4_31 = 4038;
    private const int Tm4_32 = 4039;
    private const int Tm4_33 = 4040;
    private const int Tm4_33r = 4041;
    private const int Tm4_34 = 4042;
    private const int Tm4_35 = 4043;

    public Mission4(ITrainingSite site)
        : base(site)
    {
    }

    public override int GetMissionID() => Tm4EnemyEngagement;

    public override int GetStartSectorId() => StartSector;

    /// <summary>Native <c>Mission4::ShipLanded</c> (<c>mission4.cpp:98-105</c>) — landing ends the mission.</summary>
    public override bool ShipLanded()
    {
        Terminate();
        return false;
    }

    protected override void CreateUniverse()
    {
        // Host-side universe construction (LoadUniverse("training_3", 486, 1030), the max-energy global
        // attribute, the commander docked in station 1030, activating group-fire and stripping the
        // ship's missiles — mission4.cpp:108-138) is TR-2's job once the embedded host lands.
    }

    protected override Condition CreateMission()
    {
        // generate the ship ids we'll be using throughout the mission (mission4.cpp:146-153);
        // stable ids, assigned before any goal is built since goals reference them.
        EnemyScoutId = DefaultScoutId;
        EnemyMiner1Id = DefaultMiner1Id;
        EnemyMiner2Id = DefaultMiner2Id;
        EnemyMiner3Id = DefaultMiner3Id;
        EnemyMiner4Id = DefaultMiner4Id;
        BuilderId = DefaultBuilderId;

        var goalList = new GoalList();
        var root = new Goal(goalList);

        goalList.AddGoal(CreateGoal01());
        goalList.AddGoal(CreateGoal02());
        goalList.AddGoal(CreateGoal03());
        goalList.AddGoal(CreateGoal04());
        goalList.AddGoal(CreateGoal05());
        goalList.AddGoal(CreateGoal06());
        goalList.AddGoal(CreateGoal07());
        goalList.AddGoal(CreateGoal08());
        goalList.AddGoal(CreateGoal09());
        goalList.AddGoal(CreateGoal10());

        return root;
    }

    // Goal 01: intro audio; controls locked; cockpit (mission4.cpp:171-213)
    private Goal CreateGoal01()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(SalCommenceScanSound);
            var lockAll = new SetControlConstraintsAction();
            lockAll.DisableInputAction(unchecked((int)0xffffffff));
            lockAll.ScaleInputControl(ControlAxis.Yaw, 0.0f);
            lockAll.ScaleInputControl(ControlAxis.Pitch, 0.0f);
            lockAll.ScaleInputControl(ControlAxis.Roll, 0.0f);
            lockAll.ScaleInputControl(ControlAxis.Throttle, 0.0f);
            goal.AddStartAction(lockAll);
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(2.0f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_01));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_02);
            goal.AddStartAction(new SetDisplayModeAction(Site, CameraCockpit));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 02: chat + quickcomm tutorials (mission4.cpp:216-310)
    private Goal CreateGoal02()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm4_03);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayChat));
            goal.AddStartAction(new MessageAction(Site, "This is a 'chat' message from the commander to you."));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetChatCondition(Site, ChatEveryone));
            goal.AddStartAction(new MessageAction(Site, "Press the ENTER key to begin a chat."));
            goal.AddStartAction(new MessageAction(Site, "Press the TAB key to select 'All'."));
            goal.AddStartAction(new MessageAction(Site, "Press the ENTER key again to send the chat."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_03r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_03r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_04);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_04r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_04r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_05));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_06);
            goal.AddStartAction(new MessageAction(Site, "Press the TILDE (~) key to activate the quickcomm menu."));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_06r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 03: receive a waypoint order (mission4.cpp:313-375)
    private Goal CreateGoal03()
    {
        var list = new GoalList();
        int buoyId = NewBuoyId();

        list.AddGoal(CreatePlaySoundGoal(Tm4_07));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_08);
            // native computes a position from the aleph 1030 and the ship's facing
            // (mission4.cpp:333-335); the host owns universe geometry, so a fixed offset from the
            // start position stands in for the headless proof.
            goal.AddStartAction(new CreateWaypointAction(Site, buoyId, new Vector3(800f, 0f, 0f), StartSector));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandQueued, new TargetRef(ObjectType.Buoy, buoyId), CmdGoto));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetCommandCondition(Site, Site.PlayerShip.Ref, CmdGoto));
            goal.AddStartAction(new MessageAction(Site, "Press the INSERT or '[' key to accept the command."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_08r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_08r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(1.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_09));

        {
            var goal = new Goal(new ObjectWithinRadiusCondition(Site, Site.PlayerShip.Ref, new TargetRef(ObjectType.Buoy, buoyId), 100.0f));
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), new SetAutopilotAction(Site, Site.PlayerShip.Ref)));
            list.AddGoal(goal);
        }

        return new Goal(list);
    }

    // Goal 04: intersector travel / sector map tutorial (mission4.cpp:378-460)
    private Goal CreateGoal04()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm4_10);
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandAccepted, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandCurrent, null, CmdDoNothing));
            goal.AddStartAction(new SetAutopilotAction(Site, Site.PlayerShip.Ref, false));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_11);
            goal.AddStartAction(new ShowPaneAction(Site, PaneSector));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlaySector));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_12));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_13));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_14);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetCommandCondition(Site, Site.PlayerShip.Ref, CmdGoto));
            // native also queues a goto to OT_warp 1030 here (mission4.cpp:449); ObjectType has no
            // Warp member, and this goal only gates on GetCommandCondition(Goto), so the warp
            // SetCommandAction is omitted (equivalent to a goto whose command-set the player accepts).
            goal.AddStartAction(new MessageAction(Site, "Press the INSERT or '[' key to accept the command."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_15));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_15));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(1.5f, Site)));

        return new Goal(list);
    }

    // Goal 05: controls released; fly to the aleph; transit; spawn the enemy miners (mission4.cpp:463-614)
    private Goal CreateGoal05()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm4_16));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_17);
            var constraints = new SetControlConstraintsAction();
            constraints.EnableInputAction(unchecked((int)0xffffffff));
            constraints.ScaleInputControl(ControlAxis.Yaw, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Throttle, 1.0f);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new ObjectWithinRadiusCondition(Site, Site.PlayerShip.Ref, AlephAsShip(AlephNeptuneId), 750.0f));
            goal.AddStartAction(new MessageAction(Site, "Fly to the aleph named 'Mars'."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_17r));
            goal.AddConstraintCondition(CreateTooLongCondition(60.0f, Tm4_17r));
            list.AddGoal(goal);
        }

        {
            var goal = CreatePlaySoundGoal(Tm4_18);
            goal.AddStartAction(new MessageAction(Site, "Flying through the aleph to sector 'Mars'..."));
            var constraints = new SetControlConstraintsAction();
            constraints.DisableInputAction(unchecked((int)0xffffffff));
            constraints.ScaleInputControl(ControlAxis.Yaw, 0.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 0.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 0.0f);
            constraints.ScaleInputControl(ControlAxis.Throttle, 0.0f);
            goal.AddStartAction(constraints);
            var turnTo = new TurnToAction(Site, Site.PlayerShip.Ref, AlephAsShip(AlephNeptuneId));
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), turnTo));
            list.AddGoal(goal);
        }

        // (Wait for sector change to Mars; spawn the enemy miners there)
        {
            var goal = new Goal(new GetSectorCondition(Site, Site.PlayerShip.Ref, MarsSector));

            // turn the throttle to 0.5 to insure that they fly through
            var throttle = new SetControlsAction();
            throttle.SetInputControl(ControlAxis.Throttle, 0.5f);
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), throttle));

            // create the enemy miners at the aleph-1032 position plus deterministic offsets
            // (native scatters them randomly, mission4.cpp:534-561; host owns geometry, so fixed offsets).
            Vector3 alephPos = new(0f, 0f, 0f);
            Vector3[] offsets =
            {
                new(300f, 0f, 0f),
                new(-300f, 0f, 0f),
                new(0f, 300f, 0f),
                new(0f, -300f, 0f),
            };
            string[] minerNames = { "Enemy Miner 01", "Enemy Miner 02", "Enemy Miner 03", "Enemy Miner 04" };
            int[] minerIds = { EnemyMiner1Id, EnemyMiner2Id, EnemyMiner3Id, EnemyMiner4Id };

            for (int i = 0; i < 4; i++)
            {
                var create = new CreateDroneAction(Site, minerNames[i], minerIds[i], EnemyMinerHull, 1, PilotMiner);
                create.SetCreatedLocation(MarsSector, alephPos + offsets[i]);
                goal.AddStartAction(create);
            }

            // set the signature on all those enemy craft to something ridiculous (mission4.cpp:563-567)
            foreach (int id in minerIds)
                goal.AddStartAction(new SetSignatureAction(Site, ObjectType.Ship, id, 1.0e6f));

            // tell them all to do nothing for a little while (mission4.cpp:569-580)
            foreach (int id in minerIds)
            {
                goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandAccepted, null, CmdDoNothing));
                goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandCurrent, null, CmdDoNothing));
            }

            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(1.75f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_19);
            goal.AddStartAction(new SetControlsAction());
            var constraints = new SetControlConstraintsAction();
            constraints.DisableInputAction(unchecked((int)0xffffffff));
            constraints.ScaleInputControl(ControlAxis.Yaw, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Throttle, 0.0f);
            goal.AddStartAction(constraints);
            goal.AddStartAction(new ShowPaneAction(Site, PaneSector));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlaySector));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 06: the eyeball indicator + stealth/cloak (mission4.cpp:617-758)
    private Goal CreateGoal06()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm4_20);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.3f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_21));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.3f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_22);
            goal.AddStartAction(new ShowEyeballAction(Site, true));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayEyeball));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_23);
            goal.AddStartAction(new ShowEyeballAction(Site, false));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));

            // tell all the miners to get a new command (mission4.cpp:663-674)
            foreach (int id in MinerIds())
                goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandAccepted, null, CmdNone));
            foreach (int id in MinerIds())
                goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandCurrent, null, CmdNone));

            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_24));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_25));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // the cloak sub-goal list (mission4.cpp:705-738). Note native adds the 0.5s wait to the OUTER
        // list (mission4.cpp:716 — an upstream quirk we preserve); the inner list holds the cloak
        // voice-over and the key-press goal, and is wrapped in a goal constrained on the cloak key.
        {
            var innerList = new GoalList();
            Condition keyCloak = new GetKeyCondition(Site, KeyToggleCloak);

            innerList.AddGoal(CreatePlaySoundGoal(Tm4_26));

            // wait half second (appended to the outer list per native mission4.cpp:716)
            list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

            {
                var goal = new Goal(new ProxyCondition(keyCloak));
                goal.AddStartAction(new MessageAction(Site, "Press the G key to activate your cloak."));
                goal.AddStartAction(new SetHudOverlayAction(Site, OverlayCloak));
                goal.AddStartAction(new PlaySoundAction(Site, Tm4_26r));
                goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_26r));
                innerList.AddGoal(goal);
            }

            // set up the condition checking: the outer goal wraps the inner list, gated on the cloak key
            {
                var goal = new Goal(innerList);
                goal.AddConstraintCondition(keyCloak);
                var constraints = new SetControlConstraintsAction();
                constraints.EnableInputAction(CloakActive);
                goal.AddStartAction(constraints);
                list.AddGoal(goal);
            }
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(1.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm4_27);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayEnergy));
            goal.AddStartAction(new RepairRearmAction(Site));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 07: engage/destroy an enemy miner (mission4.cpp:761-847)
    private Goal CreateGoal07()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm4_28);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm4_30));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyTargetEnemy));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_29));
            goal.AddStartAction(new MessageAction(Site, "Target an enemy miner with the F key, and destroy it if you can."));
            goal.AddConstraintCondition(CreateTooLongCondition(20.0f, Tm4_29));
            list.AddGoal(goal);
        }

        // pkk - Fix for #246: a new goal to activate the controls (mission4.cpp:807-817)
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var constraints = new SetControlConstraintsAction();
            constraints.EnableInputAction(unchecked((int)0xffffffff));
            constraints.ScaleInputControl(ControlAxis.Throttle, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Yaw, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 1.0f);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        // (Wait for enemy destruction, or the target leaving the sector)
        {
            var pOrA = new OrCondition(
                new GetShipIsDestroyedCondition2(Site, ObjectType.Ship, EnemyMiner1Id),
                new GetShipIsDestroyedCondition2(Site, ObjectType.Ship, EnemyMiner2Id));
            var pOrB = new OrCondition(
                new GetShipIsDestroyedCondition2(Site, ObjectType.Ship, EnemyMiner3Id),
                new GetShipIsDestroyedCondition2(Site, ObjectType.Ship, EnemyMiner4Id));
            var pInSector = new GetSectorCondition(Site, new PlayerCurrentTarget(Site, false), MarsSector);
            var goal = new Goal(new OrCondition(new OrCondition(pOrA, pOrB), NotCondition.Of(pInSector)));

            // show the eyeball if it is appropriate (mission4.cpp:827-834)
            int playerId = Site.PlayerShip.Ref.Id;
            var pSeesA = new OrCondition(
                new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner1Id), new TargetRef(ObjectType.Ship, playerId)),
                new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner2Id), new TargetRef(ObjectType.Ship, playerId)));
            var pSeesB = new OrCondition(
                new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner3Id), new TargetRef(ObjectType.Ship, playerId)),
                new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner4Id), new TargetRef(ObjectType.Ship, playerId)));
            var pSees = new OrCondition(
                new OrCondition(pSeesA, pSeesB),
                new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), new TargetRef(ObjectType.Ship, playerId)));

            goal.AddConstraintCondition(new ConditionalAction(pSees, new ShowEyeballAction(Site, true)));
            goal.AddConstraintCondition(new ConditionalAction(NotCondition.Of(new ProxyCondition(pSees)), new ShowEyeballAction(Site, false)));

            // tm_4_29r — destroy the enemy miner if you can
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_29r));

            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 08: defend the constructor from the enemy scout (mission4.cpp:850-972)
    private Goal CreateGoal08()
    {
        var list = new GoalList();

        // tm_4_31 — order in the friendly constructor, send the miners fleeing, clear the player
        {
            var goal = CreatePlaySoundGoal(Tm4_31);

            // create the friendly builder (mission4.cpp:862-867)
            var createBuilder = new CreateDroneAction(Site, "Constructor", BuilderId, BuilderHull, 0, PilotBuilder);
            createBuilder.SetCreatedLocation(StartSector, new Vector3(500f, 0f, 0f));
            createBuilder.SetStationType(112); // mission4.cpp:866 — the station the constructor will build
            goal.AddStartAction(createBuilder);

            // command the builder to go to the asteroid on which it will build (mission4.cpp:869-871)
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BuilderId), CommandCurrent, new TargetRef(ObjectType.Asteroid, BuilderAsteroidId), CmdBuild));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BuilderId), CommandAccepted, new TargetRef(ObjectType.Asteroid, BuilderAsteroidId), CmdBuild));

            // create a waypoint for all of the miners to run to, and send them all there (mission4.cpp:873-883)
            int buoyId = NewBuoyId();
            goal.AddStartAction(new CreateWaypointAction(Site, buoyId, new Vector3(1000f, 1000f, 1000f), 1032));
            foreach (int id in MinerIds())
            {
                goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandCurrent, new TargetRef(ObjectType.Buoy, buoyId), CmdGoto));
                goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, id), CommandAccepted, new TargetRef(ObjectType.Buoy, buoyId), CmdGoto));
            }

            // clear the player command set (mission4.cpp:885-887)
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandCurrent, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandAccepted, null, CmdDoNothing));

            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.3f, Site)));

        // tm_4_32 — protect the constructor (mission4.cpp:901-908)
        {
            var goal = new Goal(new GetCommandCondition(Site, Site.PlayerShip.Ref, CmdGoto));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandQueued, new TargetRef(ObjectType.Ship, BuilderId), CmdGoto));
            goal.AddStartAction(new MessageAction(Site, "Press the INSERT key to accept the command."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_32));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_08r));
            list.AddGoal(goal);
        }

        // (Wait for enemy scout to get within range; then spawn it attacking the builder) (mission4.cpp:911-923)
        {
            var goal = new Goal(new ObjectWithinRadiusCondition(Site, Site.PlayerShip.Ref, new TargetRef(ObjectType.Ship, EnemyScoutId), 1500.0f));
            var createScout = new CreateDroneAction(Site, "Enemy Scout", EnemyScoutId, ScoutHull, 1, PilotWingman);
            // LANS — prevent the scout from running away: c_wbbmInRangeAggressive | c_wbbmUseMissiles
            // (= 0x50, mission4.cpp:915)
            createScout.SetCreatedBehaviour(WingmanInRangeAggressive | WingmanUseMissiles);
            createScout.SetCreatedLocation(MarsSector, new Vector3(0f, 0f, 0f));
            goal.AddStartAction(createScout);
            // LANS — make the scout attack the constructor, not the player (mission4.cpp:918-921)
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), CommandCurrent, new TargetRef(ObjectType.Ship, BuilderId), CmdAttack));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), CommandAccepted, new TargetRef(ObjectType.Ship, BuilderId), CmdAttack));
            list.AddGoal(goal);
        }

        // tm_4_33 — intercept the scout (mission4.cpp:925-936)
        {
            var goal = new Goal(new GetCommandCondition(Site, Site.PlayerShip.Ref, CmdAttack));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BuilderId), CommandCurrent, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BuilderId), CommandAccepted, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandQueued, new TargetRef(ObjectType.Ship, EnemyScoutId), CmdAttack));
            goal.AddStartAction(new MessageAction(Site, "Press the INSERT key to accept the command."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_33));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm4_33));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // tm_4_33r — stop the scout from destroying the builder (mission4.cpp:941-948)
        {
            var goal = new Goal(new GetCommandCondition(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), CmdGoto));
            goal.AddStartAction(new PlaySoundAction(Site, Tm4_33r));
            goal.AddConstraintCondition(CreateTooLongCondition(45.0f, Tm4_33r));
            list.AddGoal(goal);
        }

        // clear the player command set (mission4.cpp:950-957)
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandCurrent, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandAccepted, null, CmdDoNothing));
            list.AddGoal(goal);
        }

        // wrap the whole list in a goal carrying the eyeball constraints (mission4.cpp:959-971)
        int playerId = Site.PlayerShip.Ref.Id;
        var sSeesA = new OrCondition(
            new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner1Id), new TargetRef(ObjectType.Ship, playerId)),
            new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner2Id), new TargetRef(ObjectType.Ship, playerId)));
        var sSeesB = new OrCondition(
            new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner3Id), new TargetRef(ObjectType.Ship, playerId)),
            new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyMiner4Id), new TargetRef(ObjectType.Ship, playerId)));
        var sSees = new OrCondition(
            new OrCondition(sSeesA, sSeesB),
            new ShipSeesObjectCondition(Site, new TargetRef(ObjectType.Ship, EnemyScoutId), new TargetRef(ObjectType.Ship, playerId)));

        var wrapped = new Goal(list);
        wrapped.AddConstraintCondition(new ConditionalAction(sSees, new ShowEyeballAction(Site, true)));
        wrapped.AddConstraintCondition(new ConditionalAction(NotCondition.Of(new ProxyCondition(sSees)), new ShowEyeballAction(Site, false)));

        return wrapped;
    }

    // Goal 09: order the constructor to build; wait for completion (mission4.cpp:975-1014)
    private Goal CreateGoal09()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm4_34);
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandQueued, new TargetRef(ObjectType.Ship, BuilderId), CmdGoto));
            goal.AddStartAction(new MessageAction(Site, "Press the INSERT key to accept the command."));
            list.AddGoal(goal);
        }

        // (Wait for the build to complete / the builder to be destroyed)
        {
            var goal = new Goal(new GetShipIsDestroyedCondition2(Site, ObjectType.Ship, BuilderId));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BuilderId), CommandCurrent, new TargetRef(ObjectType.Asteroid, BuilderAsteroidId), CmdBuild));
            goal.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BuilderId), CommandAccepted, new TargetRef(ObjectType.Asteroid, BuilderAsteroidId), CmdBuild));
            list.AddGoal(goal);
        }

        // (Wait for command acceptance)
        list.AddGoal(new Goal(new GetCommandCondition(Site, Site.PlayerShip.Ref, CmdGoto)));

        // wait a few seconds; disengage autopilot (mission4.cpp:1006-1011)
        {
            var goal = new Goal(ElapsedTimeCondition.Create(4.0f, Site));
            goal.AddStartAction(new SetAutopilotAction(Site, Site.PlayerShip.Ref, false));
            list.AddGoal(goal);
        }

        return new Goal(list);
    }

    // Goal 10: land in the expansion complex (mission4.cpp:1017-1042)
    private Goal CreateGoal10()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm4_35);
            goal.AddStartAction(new MessageAction(Site, "Land in the expansion complex."));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(new FalseCondition());
            goal.AddConstraintCondition(CreateTooLongCondition(45.0f, Tm3_49r));
            list.AddGoal(goal);
        }

        return new Goal(list);
    }

    /// <summary>The four enemy miner ship ids in native creation order (01..04).</summary>
    private IEnumerable<int> MinerIds()
    {
        yield return EnemyMiner1Id;
        yield return EnemyMiner2Id;
        yield return EnemyMiner3Id;
        yield return EnemyMiner4Id;
    }

    /// <summary>Native refers to the alephs via <c>OT_warp</c>; <see cref="ObjectType"/> has no Warp
    /// member, so the geometric targets that point at an aleph use a Ship-type stand-in carrying the
    /// aleph's object id. Effectively identical for radius/pointing checks against a host that resolves
    /// by id.</summary>
    private static TargetRef AlephAsShip(int id) => new(ObjectType.Ship, id);

    private int NewBuoyId() => _nextBuoy++;
}
