using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 8 — "Nanite" — a faithful translation of <c>src/training/mission8.cpp</c> (TheBored 2007)
/// into the ported goal/condition/action framework. Three sub-missions: (1) mount the Nanite Repair
/// System (part 190) and repair the knocked-down home station; (2) defend the miners against two
/// enemy stealth fighters; (3) escort a bomber to destroy the enemy outpost while repairing it with
/// a nanite wingman. The port reuses the framework's existing seams (GetShipIsDamagedCondition,
/// SetCommandAction, CreateWaypointAction, GetShipIsStoppedCondition, ObjectWithinRadiusCondition)
/// plus the TR-5 nanite/station seams (<c>GetShipHasMountedCondition</c>, <c>SetDroneNanAction</c>,
/// <c>DamageStationAction</c>); the server's negative-damage repair (Ship.cs:4509) and station
/// repair (Station.cs:147-156) are the real-client realisation, reused rather than duplicated.
/// </summary>
public class Mission8 : TrainingMission
{
    private const int StartSector = 1051;      // mission8.cpp:62-64 (GetStartSectorID)
    private const int EnemySector = 1052;      // bomber run sector (mission8.cpp:507)
    private const int PlayerHull = 685;        // Adv Rix Scout (mission8.cpp:89)
    private const int PartNanite = 190;        // Nanite Repair System (mission8.cpp:133)

    /// <summary>The player's home station — probed from training_nan.igc: station id 1052 in
    /// cluster 1051, side 0 (native <c>pStation-&gt;GetObjectID()</c>, mission8.cpp:93/226).</summary>
    public const int HomeStationId = 1052;
    /// <summary>The enemy outpost the bomber destroys — station id 1051 in cluster 1052, side 1
    /// (native <c>GetModel(OT_station, 1051)</c>, mission8.cpp:414/600).</summary>
    public const int EnemyOutpostStationId = 1051;

    // Pilot types (igc.h:686-691).
    private const int PilotMiner = 0;
    private const int PilotWingman = 2;

    // Stable drone ids (native GenerateNewShipID) so the headless driver and host reference the same ships.
    public int Miner01Id { get; private set; }     // friendly miner (goal 2)
    public int SolapId { get; private set; }       // enemy stealth fighter 1 (goal 2)
    public int ToastId { get; private set; }       // enemy stealth fighter 2 (goal 2)
    public int Defender1Id { get; private set; }   // TheBored (goal 2)
    public int BomberId { get; private set; }      // bomber (goal 3)
    public int NanId { get; private set; }         // nan wingman bahdohday (goal 3)
    public int TkelaId { get; private set; }       // enemy defender scout (goal 3)
    public int RibskiId { get; private set; }      // enemy defender scout (goal 3)
    public int DontmessId { get; private set; }    // enemy defender fighter (goal 3)

    private const int DefaultMiner = 8001, DefaultSolap = 8002, DefaultToast = 8003, DefaultDef1 = 8004;
    private const int DefaultBomber = 8007, DefaultNan = 8008, DefaultTkela = 8009, DefaultRibski = 8010, DefaultDontmess = 8011;

    // Sound ids (native symbolic, mission8.cpp); voices/gun-sounds don't gate the script, stable ids suffice.
    private const int SalCommenceScanSound = 8000;
    private const int Tm8_01 = 8101, Tm8_02 = 8102, Tm8_03 = 8103, Tm8_04 = 8104, Tm8_05 = 8105,
        Tm8_06 = 8106, Tm8_07 = 8107, Tm8_08 = 8108, Tm8_10 = 8110, Tm8_11 = 8111, Tm8_12 = 8112,
        Tm8_13 = 8113, Tm8_14 = 8114, Tm8_15 = 8115, Tm8_16 = 8116, Tm8_17 = 8117, Tm8_18 = 8118,
        Tm8_19 = 8119, Tm8_20 = 8120, Tm8_21 = 8121;
    private const int VoRepairStation = 8201, VoNiceWork = 8202, VoDefendMiner = 8203, VoMinersDead = 8204,
        VoYeeHa = 8205, VoWaitForSignal = 8206, VoEveryoneReady = 8207, VoGoGoGo = 8208, VoDefendBomber = 8209,
        VoStayOnTarget = 8210, VoArgh = 8211;

    // The player's home-station door offset (mission8.cpp:95-98 homeRedDoor).
    private Vector3 _homeRedDoor;

    /// <summary>A message with an optional accompanying voice-over sound (native MessageAction +
    /// sound, messageaction.cpp:27-70 / TheBored edit).</summary>
    private MessageAction Msg(string text, int soundId = SoundId.None)
    {
        var m = new MessageAction(Site);
        m.AddMessage(text, soundId);
        return m;
    }

    public Mission8(ITrainingSite site) : base(site) { }

    public override int GetMissionID() => Tm8Nanite;
    public override int GetStartSectorId() => StartSector;
    public override bool CommandViewEnabled => true;   // mission8.cpp:150

    protected override void CreateUniverse()
    {
        // Host-side universe construction (LoadUniverse("training_nan",685,1051), group fire, tech
        // and loadout — mission8.cpp:87-146) is realised by the embedded host from the Nanite variant.
        var homePos = Site.Resolve(new TargetRef(ObjectType.Station, HomeStationId))?.Position ?? Vector3.Zero;
        _homeRedDoor = homePos + new Vector3(50f, -200f, -20f);   // mission8.cpp:95-98
    }

    protected override Condition CreateMission()
    {
        Miner01Id = DefaultMiner;
        SolapId = DefaultSolap;
        ToastId = DefaultToast;
        Defender1Id = DefaultDef1;
        BomberId = DefaultBomber;
        NanId = DefaultNan;
        TkelaId = DefaultTkela;
        RibskiId = DefaultRibski;
        DontmessId = DefaultDontmess;

        var goalList = new GoalList();
        goalList.AddGoal(CreateGoal01());
        goalList.AddGoal(CreateGoal02());
        goalList.AddGoal(CreateGoal03());
        return new Goal(goalList);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Goal 1 — mount the nanite and repair the damaged home station (mission8.cpp:162-237)
    // ─────────────────────────────────────────────────────────────────────────────
    private Goal CreateGoal01()
    {
        var gl = new GoalList();
        var player = Site.PlayerShip.Ref;

        gl.AddGoal(CreatePlaySoundGoal(SalCommenceScanSound));

        {   // SetDisplayModeAction(cmCockpit) after a beat (mission8.cpp:172-176)
            var g = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            g.AddStartAction(new SetDisplayModeAction(Site, CameraCockpit));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_01));

        {   // tm_8_02 — skip if the player already has the nanite; remind about F4 (mission8.cpp:179-184)
            var g = CreatePlaySoundGoal(Tm8_02);
            g.SetSkipGoalCondition(new GetShipHasMountedCondition(Site, player, PartNanite));
            g.AddStartAction(new MessageAction(Site, "F4 toggles the inventory. You will want it to be open."));
            gl.AddGoal(g);
        }

        {   // tm_8_03 — skip if already mounted (mission8.cpp:186-190)
            var g = CreatePlaySoundGoal(Tm8_03);
            g.SetSkipGoalCondition(new GetShipHasMountedCondition(Site, player, PartNanite));
            gl.AddGoal(g);
        }

        {   // wait for the player to mount the Nanite Repair System in slot 1 (mission8.cpp:192-197)
            var g = new Goal(new GetShipHasMountedCondition(Site, player, PartNanite));
            g.AddStartAction(new MessageAction(Site, "Swap the Nanite Repair System into slot 1 by pressing Ctrl+1."));
            g.AddConstraintCondition(CreateTooLongCondition(40.0f, Tm8_03));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_04));

        {   // knock the home station down so the player can practise repairing it (mission8.cpp:201-205)
            var g = new Goal(ElapsedTimeCondition.Create(3.0f, Site));
            g.AddStartAction(new DamageStationAction(Site, HomeStationId, 12500.0f)); // mission8.cpp:203
            gl.AddGoal(g);
        }

        {   // target the base with a repair order (mission8.cpp:207-212)
            var g = new Goal(ElapsedTimeCondition.Create(8.0f, Site));
            g.AddStartAction(new PlaySoundAction(Site, Tm8_05));
            g.AddStartAction(new SetCommandAction(Site, player, CommandCurrent,
                new TargetRef(ObjectType.Station, HomeStationId), CmdRepair));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_06));
        gl.AddGoal(CreatePlaySoundGoal(Tm8_07));
        gl.AddGoal(CreatePlaySoundGoal(Tm8_08));

        {   // "Repair the station." (mission8.cpp:219-223)
            var g = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            g.AddStartAction(Msg("Repair the station.", VoRepairStation));
            gl.AddGoal(g);
        }

        {   // wait until the home station's armour is back to full (mission8.cpp:225-228)
            gl.AddGoal(new Goal(NotCondition.Of(
                new GetShipIsDamagedCondition(Site, ObjectType.Station, HomeStationId, 1.0f))));
        }

        {   // "Nice work." (mission8.cpp:230-234)
            var g = new Goal(ElapsedTimeCondition.Create(6.0f, Site));
            g.AddStartAction(Msg("Nice work.", VoNiceWork));
            gl.AddGoal(g);
        }

        return new Goal(gl);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Goal 2 — defend the miners (mission8.cpp:240-404)
    // ─────────────────────────────────────────────────────────────────────────────
    private Goal CreateGoal02()
    {
        var gl = new GoalList();
        var player = Site.PlayerShip.Ref;

        {   // friendly miner (mission8.cpp:264-273)
            var g = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var miner = new CreateDroneAction(Site, "Miner 01", Miner01Id, 437, 0, PilotMiner);
            miner.SetCreatedLocation(StartSector, _homeRedDoor);
            g.AddStartAction(miner);
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_10));

        {   // target the miner (mission8.cpp:276-280)
            var g = CreatePlaySoundGoal(Tm8_11);
            g.AddStartAction(new SetCommandAction(Site, player, CommandCurrent,
                new TargetRef(ObjectType.Ship, Miner01Id), CmdGoto));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_12));
        gl.AddGoal(new Goal(ElapsedTimeCondition.Create(16.0f, Site)));

        {   // two enemy stealth fighters (mission8.cpp:287-306)
            var g = new Goal(ElapsedTimeCondition.Create(1.5f, Site));
            var pos = new Vector3(-2600f, 2600f, 0f);
            var solap = new CreateDroneAction(Site, "solap", SolapId, 386, 1, PilotWingman);
            solap.SetCreatedLocation(StartSector, pos);
            solap.SetCreatedBehaviour(WingmanUseMissiles);
            g.AddStartAction(solap);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, SolapId), CommandAccepted,
                new TargetRef(ObjectType.Ship, Miner01Id), CmdAttack));

            var pos2 = new Vector3(-2600f, 4000f, 0f);
            var toast = new CreateDroneAction(Site, "toast", ToastId, 386, 1, PilotWingman);
            toast.SetCreatedLocation(StartSector, pos2);
            toast.SetCreatedBehaviour(WingmanUseMissiles);
            g.AddStartAction(toast);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, ToastId), CommandAccepted,
                new TargetRef(ObjectType.Ship, Miner01Id), CmdAttack));
            gl.AddGoal(g);
        }

        {   // defender TheBored (mission8.cpp:309-322)
            var g = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            var def = new CreateDroneAction(Site, "TheBored", Defender1Id, 615, 0, PilotWingman);
            def.SetCreatedLocation(StartSector, _homeRedDoor + new Vector3(100f, 0f, 10f));
            def.SetCreatedOrientation(new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f));
            def.SetCreatedBehaviour(WingmanRunAt60Hull);
            g.AddStartAction(def);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, Defender1Id), CommandAccepted,
                new TargetRef(ObjectType.Ship, SolapId), CmdAttack));
            gl.AddGoal(g);
        }

        {   // defender AEM (mission8.cpp:324-336)
            var g = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var def = new CreateDroneAction(Site, "AEM", 8005, 615, 0, PilotWingman);
            def.SetCreatedLocation(StartSector, _homeRedDoor + new Vector3(-20f, 0f, 10f));
            def.SetCreatedOrientation(new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f));
            def.SetCreatedBehaviour(WingmanRunAt60Hull);
            g.AddStartAction(def);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, 8005), CommandAccepted,
                new TargetRef(ObjectType.Ship, SolapId), CmdAttack));
            gl.AddGoal(g);
        }

        {   // defender lawson (mission8.cpp:338-350)
            var g = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            var def = new CreateDroneAction(Site, "lawson", 8006, 615, 0, PilotWingman);
            def.SetCreatedLocation(StartSector, _homeRedDoor + new Vector3(0f, 0f, -10f));
            def.SetCreatedOrientation(new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f));
            def.SetCreatedBehaviour(0);
            g.AddStartAction(def);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, 8006), CommandAccepted,
                new TargetRef(ObjectType.Ship, SolapId), CmdAttack));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_13));
        gl.AddGoal(new Goal(ElapsedTimeCondition.Create(2.0f, Site)));

        {   // both stealth fighters destroyed (mission8.cpp:355-383)
            var g = new Goal(new AndCondition(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, SolapId, 0.0f)),
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, ToastId, 0.0f))));
            g.AddStartAction(Msg("Defend miners.", VoDefendMiner));

            // if the miner is destroyed, the stealth fighters idle (mission8.cpp:361-362)
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, Miner01Id, 0.0f)),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, SolapId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, Miner01Id), CmdDoNothing), true, true));
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, Miner01Id, 0.0f)),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, ToastId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, Miner01Id), CmdDoNothing), true, true));
            // miners dead warning (mission8.cpp:365-366)
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, Miner01Id, 0.0f)),
                Msg("Our miners are dead!", VoMinersDead), false, true));
            // if solap is destroyed, switch the defenders to toast (mission8.cpp:369-380)
            var attackerDead = NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, SolapId, 0.0f));
            g.AddConstraintCondition(new ConditionalAction(attackerDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, Defender1Id), CommandAccepted,
                    new TargetRef(ObjectType.Ship, ToastId), CmdAttack), false, true));
            g.AddConstraintCondition(new ConditionalAction(attackerDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, 8005), CommandAccepted,
                    new TargetRef(ObjectType.Ship, ToastId), CmdAttack), false, true));
            g.AddConstraintCondition(new ConditionalAction(attackerDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, 8006), CommandAccepted,
                    new TargetRef(ObjectType.Ship, ToastId), CmdAttack), false, true));
            gl.AddGoal(g);
        }

        {   // "Yee-haaaaaw!" if the miner survived (mission8.cpp:385-392)
            var g = new Goal(ElapsedTimeCondition.Create(3.0f, Site));
            g.AddConstraintCondition(new ConditionalAction(
                new GetShipIsDamagedCondition(Site, ObjectType.Ship, Miner01Id, 0.0f),
                Msg("Yee-haaaaaw!", VoYeeHa), false, true));
            gl.AddGoal(g);
        }

        {   // miner-alive/dead verdict (mission8.cpp:394-401)
            var g = new Goal(ElapsedTimeCondition.Create(8.0f, Site));
            g.AddConstraintCondition(new ConditionalAction(
                new GetShipIsDamagedCondition(Site, ObjectType.Ship, Miner01Id, 0.0f),
                new PlaySoundAction(Site, Tm8_14), false, true));
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, Miner01Id, 0.0f)),
                new PlaySoundAction(Site, Tm8_15), false, true));
            gl.AddGoal(g);
        }

        return new Goal(gl);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Goal 3 — the bomber run (mission8.cpp:407-655)
    // ─────────────────────────────────────────────────────────────────────────────
    private Goal CreateGoal03()
    {
        var gl = new GoalList();
        var player = Site.PlayerShip.Ref;
        var enemyStation = new TargetRef(ObjectType.Station, EnemyOutpostStationId);

        int alephBuoy1 = 8801, alephBuoy2 = 8802;

        {   // spawn the bomber (mission8.cpp:434-440)
            var g = new Goal(ElapsedTimeCondition.Create(2.0f, Site));
            var bomber = new CreateDroneAction(Site, "Commander", BomberId, 604, 0, PilotWingman);
            bomber.SetCreatedLocation(StartSector, _homeRedDoor);
            g.AddStartAction(bomber);
            gl.AddGoal(g);
        }

        {   // order the bomber to the aleph waypoint (mission8.cpp:442-448)
            var g = new Goal(ElapsedTimeCondition.Create(2.0f, Site));
            g.AddStartAction(new CreateWaypointAction(Site, alephBuoy1, new Vector3(-2500f, 2500f, 0f), EnemySector));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BomberId), CommandCurrent,
                new TargetRef(ObjectType.Buoy, alephBuoy1), CmdGoto));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BomberId), CommandAccepted,
                new TargetRef(ObjectType.Buoy, alephBuoy1), CmdGoto));
            gl.AddGoal(g);
        }

        {   // meet the bomber in front of the aleph (mission8.cpp:450-456)
            var g = new Goal(ElapsedTimeCondition.Create(10.0f, Site));
            g.AddStartAction(new PlaySoundAction(Site, Tm8_16));
            g.AddStartAction(new MessageAction(Site, "Meet me in front of the aleph."));
            g.AddStartAction(new SetCommandAction(Site, player, CommandQueued,
                new TargetRef(ObjectType.Buoy, alephBuoy1), CmdGoto));
            gl.AddGoal(g);
        }

        {   // spawn the nan wingman and equip it with the nanite (mission8.cpp:458-471)
            var g = new Goal(ElapsedTimeCondition.Create(3.0f, Site));
            var nan = new CreateDroneAction(Site, "bahdohday", NanId, 685, 0, PilotWingman);
            nan.SetCreatedLocation(StartSector, _homeRedDoor);
            g.AddStartAction(nan);
            g.AddStartAction(new SetDroneNanAction(Site, new TargetRef(ObjectType.Ship, NanId)));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandCurrent,
                new TargetRef(ObjectType.Buoy, alephBuoy1), CmdGoto));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandAccepted,
                new TargetRef(ObjectType.Buoy, alephBuoy1), CmdGoto));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_17));

        {   // wait until the bomber is stopped at the aleph (mission8.cpp:475-479)
            var g = new Goal(new GetShipIsStoppedCondition(Site, new TargetRef(ObjectType.Ship, BomberId)));
            g.AddStartAction(new PlaySoundAction(Site, Tm8_18));
            gl.AddGoal(g);
        }

        {   // wait for the player to be within 500 m of the bomber (mission8.cpp:482-486)
            var g = new Goal(new ObjectWithinRadiusCondition(Site, player, new TargetRef(ObjectType.Ship, BomberId), 500.0f));
            g.AddConstraintCondition(CreateTooLongCondition(40.0f, Tm8_16));
            gl.AddGoal(g);
        }

        {   // "Wait for signal." (mission8.cpp:488-492)
            var g = new Goal(ElapsedTimeCondition.Create(4.0f, Site));
            g.AddStartAction(Msg("Wait for signal.", VoWaitForSignal));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_19));

        {   // "Everybody ready?" (mission8.cpp:496-500)
            var g = new Goal(ElapsedTimeCondition.Create(5.0f, Site));
            g.AddStartAction(Msg("Everybody ready?", VoEveryoneReady));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_20));

        {   // bomber crosses into sector 1052; re-issue Goto every 30 s (mission8.cpp:504-523)
            var g = new Goal(new GetSectorCondition(Site, new TargetRef(ObjectType.Ship, BomberId), EnemySector));
            g.AddStartAction(new CreateWaypointAction(Site, alephBuoy2, Vector3.Zero, EnemySector));
            var p1 = new PeriodicCondition(new TrueCondition(), 30.0f, Site);
            var p2 = new PeriodicCondition(new TrueCondition(), 30.0f, Site);
            var p3 = new PeriodicCondition(new TrueCondition(), 30.0f, Site);
            var p4 = new PeriodicCondition(new TrueCondition(), 30.0f, Site);
            g.AddConstraintCondition(new ConditionalAction(p1, Msg("Go Go Go!", VoGoGoGo)));
            g.AddConstraintCondition(new ConditionalAction(p2, new CreateWaypointAction(Site, alephBuoy2, Vector3.Zero, EnemySector)));
            g.AddConstraintCondition(new ConditionalAction(p3, new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BomberId), CommandCurrent,
                new TargetRef(ObjectType.Buoy, alephBuoy2), CmdGoto)));
            g.AddConstraintCondition(new ConditionalAction(p4, new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BomberId), CommandAccepted,
                new TargetRef(ObjectType.Buoy, alephBuoy2), CmdGoto)));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandAccepted,
                new TargetRef(ObjectType.Ship, BomberId), CmdGoto));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandCurrent,
                new TargetRef(ObjectType.Ship, BomberId), CmdGoto));
            gl.AddGoal(g);
        }

        {   // bomber attacks the enemy outpost (mission8.cpp:525-530)
            var g = new Goal(ElapsedTimeCondition.Create(2.0f, Site));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BomberId), CommandCurrent, enemyStation, CmdAttack));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, BomberId), CommandAccepted, enemyStation, CmdAttack));
            gl.AddGoal(g);
        }

        {   // spawn enemy defenders who go for the nan/bomber (mission8.cpp:536-561)
            var g = new Goal(ElapsedTimeCondition.Create(3.0f, Site));
            var tkela = new CreateDroneAction(Site, "Tkela", TkelaId, 385, 1, PilotWingman);
            tkela.SetCreatedLocation(EnemySector, new Vector3(0f, -300f, 400f));
            tkela.SetCreatedBehaviour(WingmanRunAt30Hull);
            tkela.SetCreatedCommand(NanId, ObjectType.Ship, CmdAttack);
            g.AddStartAction(tkela);
            var ribski = new CreateDroneAction(Site, "ribski", RibskiId, 385, 1, PilotWingman);
            ribski.SetCreatedLocation(EnemySector, new Vector3(0f, 100f, 300f));
            ribski.SetCreatedBehaviour(WingmanRunAt30Hull);
            ribski.SetCreatedCommand(NanId, ObjectType.Ship, CmdAttack);
            g.AddStartAction(ribski);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandAccepted,
                new TargetRef(ObjectType.Ship, BomberId), CmdGoto));
            gl.AddGoal(g);
        }

        {   // spawn the enemy fig dontmess (mission8.cpp:563-575)
            var g = new Goal(ElapsedTimeCondition.Create(2.0f, Site));
            var dm = new CreateDroneAction(Site, "dontmess", DontmessId, 314, 1, PilotWingman);
            dm.SetCreatedLocation(EnemySector, new Vector3(0f, 0f, -400f));
            g.AddStartAction(dm);
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, DontmessId), CommandAccepted,
                new TargetRef(ObjectType.Ship, BomberId), CmdAttack));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, DontmessId), CommandCurrent,
                new TargetRef(ObjectType.Ship, BomberId), CmdAttack));
            gl.AddGoal(g);
        }

        gl.AddGoal(CreatePlaySoundGoal(Tm8_21));
        gl.AddGoal(new Goal(ElapsedTimeCondition.Create(4.0f, Site)));

        {   // until the bomber's hull is damaged — defenders retarget if the nan dies (mission8.cpp:581-593)
            var g = new Goal(new GetShipIsDamagedCondition(Site, ObjectType.Ship, BomberId, 1.0f));
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, NanId, 0.0f)),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, RibskiId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdAttack), false, true));
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, NanId, 0.0f)),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdAttack), false, true));
            g.AddStartAction(Msg("Defend bombers.", VoDefendBomber));
            gl.AddGoal(g);
        }

        {   // until the bomber or the enemy outpost is destroyed (mission8.cpp:595-617)
            var g = new Goal(new OrCondition(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Station, EnemyOutpostStationId, 0.0f)),
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, BomberId, 0.0f))));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandAccepted, player, CmdAttack));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandCurrent, player, CmdAttack));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandAccepted,
                new TargetRef(ObjectType.Ship, BomberId), CmdRepair));
            g.AddStartAction(new SetCommandAction(Site, new TargetRef(ObjectType.Ship, NanId), CommandCurrent,
                new TargetRef(ObjectType.Ship, BomberId), CmdRepair));
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, NanId, 0.0f)),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, RibskiId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdAttack), false, true));
            g.AddConstraintCondition(new ConditionalAction(
                NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, NanId, 0.0f)),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, RibskiId), CommandCurrent,
                    new TargetRef(ObjectType.Ship, BomberId), CmdAttack), false, true));
            g.AddConstraintCondition(new ConditionalAction(
                new GetShipIsDamagedCondition(Site, ObjectType.Ship, player.Id, 0.0f),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdAttack), false, true));
            g.AddConstraintCondition(new ConditionalAction(
                new GetShipIsDamagedCondition(Site, ObjectType.Ship, player.Id, 0.0f),
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandCurrent,
                    new TargetRef(ObjectType.Ship, BomberId), CmdAttack), false, true));
            g.AddStartAction(Msg("Stay on target.", VoStayOnTarget));
            gl.AddGoal(g);
        }

        gl.AddGoal(new Goal(ElapsedTimeCondition.Create(1.0f, Site)));

        {   // outcome announcements (mission8.cpp:626-643)
            var g = new Goal(ElapsedTimeCondition.Create(1.4f, Site));
            var outpostDead = NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Station, EnemyOutpostStationId, 0.0f));
            var bomberDead = NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, BomberId, 0.0f));
            g.AddConstraintCondition(new ConditionalAction(outpostDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdDoNothing), true, true));
            g.AddConstraintCondition(new ConditionalAction(outpostDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, RibskiId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdDoNothing), true, true));
            g.AddConstraintCondition(new ConditionalAction(outpostDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, DontmessId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdDoNothing), true, true));
            var msg = new MessageAction(Site);
            msg.AddMessage("Enemy outpost destroyed.", 901);
            g.AddConstraintCondition(new ConditionalAction(outpostDead, msg, false, true));
            g.AddConstraintCondition(new ConditionalAction(bomberDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, TkelaId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdDoNothing), true, true));
            g.AddConstraintCondition(new ConditionalAction(bomberDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, RibskiId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdDoNothing), true, true));
            g.AddConstraintCondition(new ConditionalAction(bomberDead,
                new SetCommandAction(Site, new TargetRef(ObjectType.Ship, DontmessId), CommandAccepted,
                    new TargetRef(ObjectType.Ship, BomberId), CmdDoNothing), true, true));
            g.AddConstraintCondition(new ConditionalAction(new AndCondition(bomberDead,
                new GetShipIsDamagedCondition(Site, ObjectType.Station, EnemyOutpostStationId, 0.0f)),
                Msg("The bomber got destroyed, better luck next time.", VoArgh), false, true));
            gl.AddGoal(g);
        }

        {   // final verdict (mission8.cpp:645-652)
            var g = new Goal(ElapsedTimeCondition.Create(5.0f, Site));
            var outpostDead = NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Station, EnemyOutpostStationId, 0.0f));
            var bomberDead = NotCondition.Of(new GetShipIsDamagedCondition(Site, ObjectType.Ship, BomberId, 0.0f));
            g.AddConstraintCondition(new ConditionalAction(outpostDead,
                Msg("Yee-haaaaaw!", VoYeeHa), false, true));
            g.AddConstraintCondition(new ConditionalAction(new AndCondition(bomberDead, outpostDead),
                new MessageAction(Site, "Our bomber got destroyed too, but that is an acceptable price to pay."), false, true));
            gl.AddGoal(g);
        }

        return new Goal(gl);
    }
}
