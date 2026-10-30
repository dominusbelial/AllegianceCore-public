using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 3 — "Basic Weaponry" — a faithful translation of <c>src/training/mission3.cpp</c> into the
/// ported goal/condition/action framework. The narrative voice-over ordering, the constraint guards,
/// the weapon/energy/ammo tutorials and the completion checks all match native: thirteen sub-goals that
/// cover the shield/armour gauges, targeting derelict hulls, the firing reticule, the energy weapon,
/// target-enemy selection, the machine gun (ammo), the missile launcher, the inventory display, the
/// kill bonus, and finally flying home to land. Pure data: it builds a <see cref="GoalList"/> and runs
/// against an injected <see cref="ITrainingSite"/>, with no rendering or host.
/// </summary>
public class Mission3 : TrainingMission
{
    /// <summary>Native <c>GetStartSectorID</c> returns 1010 (<c>mission3.cpp:88-91</c>).</summary>
    private const int StartSector = 1010;

    /// <summary>Number of derelict hulls spawned as target practice (<c>TM_NUMBER_OF_DERELICTS</c>).</summary>
    private const int NumberOfDerelicts = 10;

    /// <summary>First ship id for the derelicts — the native <c>GenerateNewShipID</c> is replaced with a
    /// stable 1000..1009 range so the C# test site can register them (<c>mission3.cpp:221</c>).</summary>
    private const int FirstDerelictId = 1000;

    /// <summary>Player pilot type (native <c>c_ptPlayer</c>).</summary>
    private const int PilotPlayer = 10;

    private readonly TargetRef _station = new(ObjectType.Station, StartSector);

    /// <summary>Hull ids of the derelicts, populated by <see cref="CreateUniverse"/> (mission3.cpp:201-229)
    /// and consumed by <see cref="CreateGotTargetCondition"/>.</summary>
    private readonly int[] _derelictHulls = new int[NumberOfDerelicts];

    /// <summary>Fixed PRNG seed for the derelict sphere (<c>Vector::RandomDirection</c>, mission3.cpp:228).
    /// Native uses the platform's unspecified <c>randomInt</c> seed; the port pins a fixed seed so test runs
    /// stay deterministic across machines and reruns (the brief's "say how the seed is chosen": a constant —
    /// chosen arbitrarily but fixed, so CI is reproducible).</summary>
    private const int DerelictSeed = 8917;

    /// <summary>Native branches on joystick presence for several prompts (<c>mission3.cpp:629,842,929</c>);
    /// the keyboard branch is the test path (no joystick).</summary>
    protected virtual bool HasJoystick => false;

    // TrekKey ids used by Mission 3 all come from the native igc.h table (TrainingMissionIds):
    // TK_TargetEnemy (80, the F key), TK_FireBooster (50, SPACEBAR) and TK_TargetCenter (90).
    // The real input gate delivers those native numbers, so no mission-local ids are invented.

    // Equipment types (igc.h:531-538). Only EtMagazine is referenced by Mission 3 (SwapWeaponAction).
    private const int EtWeapon = 1;      // ET_Weapon
    private const int EtMagazine = 2;    // ET_Magazine
    private const int EtShield = 4;      // ET_Shield
    private const int EtAfterburner = 7; // ET_Afterburner

    // ---------------------------------------------------------------------------------------------
    // Mission 3 voice-over / commentary sound ids. Native uses symbolic tm_3_XXSound ids bound in the
    // trainingsounddef asset; exact values are catalog-specific, so the C# port assigns a stable
    // unique sequential integer per distinct native sound (starting at 3001), exactly like Mission 2.
    // Every distinct sound referenced by the native file gets a constant; '_r' repeat variants get
    // their own unique id. Mapping is stable per-sound (TR-2 binds them to the asset catalog).
    // ---------------------------------------------------------------------------------------------
    private const int Tm3_01 = 3001;   // tm_3_01 — "This ship is a fighter..."
    private const int Tm3_02 = 3002;   // tm_3_02 — "In this mission we're going to go over targeting..."
    private const int Tm3_03 = 3003;   // tm_3_03 — "As before, your controls will be disabled..."
    private const int Tm3_04 = 3004;   // tm_3_04 — "First, notice the two gauges..."
    private const int Tm3_05 = 3005;   // tm_3_05 — "The shield gauge shows how much damage..."
    private const int Tm3_06 = 3006;   // tm_3_06 — "Now notice that the shield regenerates."
    private const int Tm3_07 = 3007;   // tm_3_07 — "The armor gauge shows how much more damage..."
    private const int Tm3_08 = 3008;   // tm_3_08 — "Notice that the armor does not regenerate."
    private const int Tm3_09 = 3009;   // tm_3_09 — "If you're in a battle and you get damaged..."
    private const int Tm3_10 = 3010;   // tm_3_10 — "There are a number of derelict hulls..."
    private const int Tm3_11 = 3011;   // tm_3_11 — "As your first introduction to targeting..."
    private const int Tm3_12 = 3012;   // tm_3_12 — "Good! I'm enabling your flight controls now..."
    private const int Tm3_12r = 3013;  // tm_3_12r — "When you are comfortable with targeting, press SPACEBAR."
    private const int Tm3_13 = 3014;   // tm_3_13 — "All ships have a scan range..."
    private const int Tm3_14 = 3015;   // tm_3_14 — "Now examine the portion of your heads-up display..."
    private const int Tm3_15 = 3016;   // tm_3_15 — "Press the SPACEBAR when you are ready to proceed."
    private const int Tm3_16 = 3017;   // tm_3_16 — "When you target an enemy craft, you will see an aiming reticule..."
    private const int Tm3_17 = 3018;   // tm_3_17 — "Okay. There are basically three weapon types..."
    private const int Tm3_18 = 3019;   // tm_3_18 — joystick fire-main-weapon prompt (unused, no joystick)
    private const int Tm3_19 = 3020;   // tm_3_19 — keyboard fire-main-weapon prompt
    private const int Tm3_20 = 3021;   // tm_3_20 — "Good!"
    private const int Tm3_21 = 3022;   // tm_3_21 — "Notice the energy gauge..."
    private const int Tm3_22 = 3023;   // tm_3_22 — "One benefit of using energy-based weapons..."
    private const int Tm3_23 = 3024;   // tm_3_23 — "Notice how the shield and armor strength of your target..."
    private const int Tm3_23r = 3025;  // tm_3_23r — "Keep firing until your target is destroyed."
    private const int Tm3_24 = 3026;   // tm_3_24 — "Great. As you can see, stationary objects are easy pickings."
    private const int Tm3_25 = 3027;   // tm_3_25 — "Notice that your radar has targeted another derelict hull."
    private const int Tm3_26 = 3028;   // tm_3_26 — "You can selectively target the nearest friendly/enemy craft..."
    private const int Tm3_28 = 3029;   // tm_3_28 — "Notice your radar. The targeted object has square brackets..."
    private const int Tm3_29 = 3030;   // tm_3_29 — "Turn to face your new target."
    private const int Tm3_30 = 3031;   // tm_3_30 — "Excellent! You're getting the hang of this!"
    private const int Tm3_31 = 3032;   // tm_3_31 — "I am activating your machine gun now."
    private const int Tm3_31r = 3033;  // tm_3_31r — "Go ahead and fire."
    private const int Tm3_32 = 3034;   // tm_3_32 — "Good!"
    private const int Tm3_33 = 3035;   // tm_3_33 — "The machine gun uses ammunition..."
    private const int Tm3_34 = 3036;   // tm_3_34 — "Blow up your target with the machine guns."
    private const int Tm3_35 = 3037;   // tm_3_35 — "Now turn to face the next targeted derelict hull."
    private const int Tm3_36 = 3038;   // tm_3_36 — "Perfect! I'm going to load a missile launcher from cargo."
    private const int Tm3_37 = 3039;   // tm_3_37 — joystick fire-missiles prompt (unused, no joystick)
    private const int Tm3_38 = 3040;   // tm_3_38 — keyboard fire-missiles prompt
    private const int Tm3_39 = 3041;   // tm_3_39 — "Watch how these affect the target!"
    private const int Tm3_40 = 3042;   // tm_3_40 — "Good job."
    private const int Tm3_41 = 3043;   // tm_3_41 — "This is the Inventory display..."
    private const int Tm3_42 = 3044;   // tm_3_42 — "Check that out and make sure you are comfortable with it."
    private const int Tm3_43 = 3045;   // tm_3_43 — "One more thing you'll need to know about is your craft's kill bonus."
    private const int Tm3_44 = 3046;   // tm_3_44 — "This number represents the percentage of extra damage..."
    private const int Tm3_45 = 3047;   // tm_3_45 — "You've done well, Cadet! Here's a chance to put your learning to the test..."
    private const int Tm3_46 = 3048;   // tm_3_46 — "When you are ready to go home, press the END key."
    private const int Tm3_47 = 3049;   // tm_3_47 — "All right, you are going to land the ship yourself this time, Cadet..."
    private const int Tm3_48 = 3050;   // tm_3_48 — "Press the B key to target your nearest base."
    private const int Tm3_49 = 3051;   // tm_3_49 — "Good! Fly toward the base, and locate and land in a green hangar..."
    private const int Tm3_49r = 3052;  // tm_3_49r — "Land the ship in a green hangar bay to complete the mission."

    /// <summary>Shared intro placeholder (salCommenceScanSound), identical to Mission 2's goal 01.</summary>
    private const int SalCommenceScanSound = 2000;

    public Mission3(ITrainingSite site)
        : base(site)
    {
    }

    public override int GetMissionID() => Tm3BasicWeaponry;

    public override int GetStartSectorId() => StartSector;

    /// <summary>Native <c>Mission3::ShipLanded</c> (<c>mission3.cpp:154-158</c>) — landing ends the mission.</summary>
    public override bool ShipLanded()
    {
        Terminate();
        return false;
    }

    /// <summary>
    /// Populates the 10 derelict hulls that serve as target practice — the C# port of
    /// <c>Mission3::CreateUniverse</c> (<c>mission3.cpp:161-230</c>). The native host hands the mission a
    /// pre-built universe; here we create the derelicts through the site (stable ids 1000+i instead of
    /// <c>GenerateNewShipID</c>), placing each 400m..580m out along a rotating unit direction and facing
    /// away from the player (native <c>mission3.cpp:204-229</c>).
    /// </summary>
    protected override void CreateUniverse()
    {
        // mission3.cpp:201-229 — native rotates a running unit direction via Vector::RandomDirection()
        // (uniform random direction) and picks hull 800 (1/3) or 801 (2/3) via randomInt(0,2). The port
        // pins the PRNG to a fixed seed (DerelictSeed) so test runs stay deterministic; direction 0 starts
        // at +X like native's (1,0,0), and each derelict faces away from the player along -direction.
        var rng = new Random(DerelictSeed);
        Vector3 direction = Vector3.UnitX;                                  // mission3.cpp:204
        for (int i = 0; i < NumberOfDerelicts; i++)
        {
            float radius = 400.0f + i * 20.0f;                              // mission3.cpp:206
            int hullId = rng.Next(0, 3) == 0 ? 800 : 801;                   // randomInt(0,2) (209-218)
            string name = $"Derelict {i + 1:D2}";                           // mission3.cpp:220
            int id = FirstDerelictId + i;                                   // stable instead of GenerateNewShipID (221)
            _derelictHulls[i] = id;

            Vector3 position = direction * radius;                          // mission3.cpp:207
            Vector3 forward = -direction;                                   // face away from the player (226)
            Vector3 up = Vector3.UnitZ;

            Site.CreateDrone(name, id, hullId, 1, PilotPlayer);             // mission3.cpp:222
            Site.PlaceDrone(id, StartSector, position, forward, up, null, CmdDoNothing); // 224-227
            direction = RandomUnitSphere(rng);                              // mission3.cpp:228
        }
    }

    /// <summary>A uniformly-random unit direction on the sphere — the C# equivalent of the native
    /// <c>Vector::RandomDirection()</c> used at mission3.cpp:228 (nav/common, random spherical point).</summary>
    private static Vector3 RandomUnitSphere(Random rng)
    {
        double theta = 2.0 * Math.PI * rng.NextDouble();                    // longitude
        double v = rng.NextDouble();                                        // cosine latitude in [-1,1]
        double phi = Math.Acos(2.0 * v - 1.0);
        double sp = Math.Sin(phi);
        return Vector3.Normalize(new Vector3(
            (float)(sp * Math.Cos(theta)),
            (float)(sp * Math.Sin(theta)),
            (float)Math.Cos(phi)));
    }

    /// <summary>
    /// TRUE when the player has targeted any one of the derelict hulls — the C# port of
    /// <c>Mission3::CreateGotTargetCondition</c> (<c>mission3.cpp:269-283</c>): an OR chain over ten
    /// <see cref="GetTargetCondition"/> leaves, one per derelict ship id.
    /// </summary>
    private Condition CreateGotTargetCondition()
    {
        Condition cond = new GetTargetCondition(Site, Site.PlayerShip.Ref, new TargetRef(ObjectType.Ship, _derelictHulls[0]));
        for (int i = 1; i < NumberOfDerelicts; i++)
            cond = new OrCondition(cond, new GetTargetCondition(Site, Site.PlayerShip.Ref, new TargetRef(ObjectType.Ship, _derelictHulls[i])));
        return cond;
    }


    protected override Condition CreateMission()
    {
        var goalList = new GoalList();
        var root = new Goal(goalList);

        TargetRef ship = Site.PlayerShip.Ref;

        // constraint: turn the ship back in when it strays beyond 7500m (then force-stop beyond 7000m)
        // — identical to Mission 2 (mission3.cpp:239-247).
        var turnHome = new TurnToAction(Site, ship, _station);
        var tooFar = new ObjectWithinRadiusCondition(Site, ship, _station, 7500.0f);
        var tooFar2 = new ObjectWithinRadiusCondition(Site, ship, _station, 7000.0f);
        root.AddConstraintCondition(new ConditionalAction(NotCondition.Of(tooFar), turnHome));
        root.AddConstraintCondition(new ConditionalAction(
            new AndCondition(NotCondition.Of(tooFar2), new ProxyCondition(tooFar)),
            new SetControlsAction()));

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
        goalList.AddGoal(CreateGoal11());
        goalList.AddGoal(CreateGoal12());
        goalList.AddGoal(CreateGoal13());

        return root;
    }

    // Goal 01: intro audio; controls locked; cockpit zoom (mission3.cpp:286-335)
    private Goal CreateGoal01()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(SalCommenceScanSound);
            var lockAll = new SetControlConstraintsAction();
            lockAll.DisableInputAction(unchecked((int)0xffffffff) ^ SelectedWeaponMask);
            lockAll.ScaleInputControl(ControlAxis.Yaw, 0.0f);
            lockAll.ScaleInputControl(ControlAxis.Pitch, 0.0f);
            lockAll.ScaleInputControl(ControlAxis.Roll, 0.0f);
            lockAll.ScaleInputControl(ControlAxis.Throttle, 0.0f);
            goal.AddStartAction(lockAll);
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(2.0f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_01));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.7f, Site));
            goal.AddStartAction(new SetDisplayModeAction(Site, CameraCockpit));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm3_02));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_03));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 02: the shield & armour gauges; demonstrate shield/hull damage (mission3.cpp:338-422)
    private Goal CreateGoal02()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm3_04));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_05);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayShield));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), new DoDamageAction(Site, false)));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_06));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(3.0f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_07);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayHull));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(ElapsedTimeCondition.Create(1.0f, Site));
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), new DoDamageAction(Site, true)));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(1.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_08));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.3f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_09);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 03: target the derelicts; enable flight controls; SPACEBAR to proceed (mission3.cpp:425-492)
    private Goal CreateGoal03()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm3_10));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_11));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(CreateGotTargetCondition());
            goal.AddStartAction(new MessageAction(Site, "Press the right mouse button to target the objects in the center of your display."));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayTargetCenter));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_11));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_12);
            var constraints = new SetControlConstraintsAction();
            constraints.ScaleInputControl(ControlAxis.Yaw, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 1.0f);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_12r));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_12r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 04: scan range explanation; target detail overlay; SPACEBAR to proceed (mission3.cpp:495-544)
    private Goal CreateGoal04()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm3_13));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_14);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayTarget));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_15));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_15));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 05: the aiming reticule; auto-turn demo; enable weapons; fire the main weapon (mission3.cpp:547-662)
    private Goal CreateGoal05()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm3_16);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayReticule));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        // turn the craft until it lines up with the current target
        {
            var pointing = new ObjectPointingAtCondition(Site, Site.PlayerShip.Ref, new PlayerCurrentTarget(Site));
            pointing.SetMinimumAngle(1.0f);
            var goal = new Goal(pointing);
            var turnTo = new TurnToAction(Site, Site.PlayerShip.Ref, new PlayerCurrentTarget(Site));
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), turnTo));
            list.AddGoal(goal);
        }

        // wait half second, and turn to the right
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var controls = new SetControlsAction();
            controls.SetInputControl(ControlAxis.Yaw, -0.5f);
            goal.AddStartAction(controls);
            list.AddGoal(goal);
        }

        // turn the craft until it lines up again
        {
            var pointing = new ObjectPointingAtCondition(Site, Site.PlayerShip.Ref, new PlayerCurrentTarget(Site));
            pointing.SetMinimumAngle(1.0f);
            var goal = new Goal(pointing);
            var turnTo = new TurnToAction(Site, Site.PlayerShip.Ref, new PlayerCurrentTarget(Site));
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), turnTo));
            list.AddGoal(goal);
        }

        // wait half second, and reset the controls
        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            goal.AddStartAction(new SetControlsAction());
            list.AddGoal(goal);
        }

        {
            var goal = CreatePlaySoundGoal(Tm3_17);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.2f, Site));
            var constraints = new SetControlConstraintsAction();
            constraints.EnableInputAction(WeaponsMask);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        // wait for weapons to fire (mission3.cpp:627-649)
        {
            var goal = new Goal(new GetStateBitsCondition(Site, Site.PlayerShip.Ref, WeaponsMask));
            if (HasJoystick)
            {
                goal.AddStartAction(new PlaySoundAction(Site, Tm3_18));
                goal.AddStartAction(new SetHudOverlayAction(Site, OverlayFireGuns));
                goal.AddStartAction(new MessageAction(Site, "Fire the main weapon by pressing the trigger button on your joystick, or left click if using a mouse"));
                goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_18));
            }
            else
            {
                goal.AddStartAction(new SetHudOverlayAction(Site, OverlayFireGuns));
                goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_19));
                goal.AddStartAction(new MessageAction(Site, "Left click to fire your main weapon."));
            }
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_20));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 06: the energy gauge; destroy the current target (mission3.cpp:665-742)
    private Goal CreateGoal06()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm3_21);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayEnergy));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_22));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_23);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayTarget));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetShipIsDestroyedCondition(Site, new PlayerCurrentTarget(Site, true)));
            goal.AddStartAction(new MessageAction(Site, "Destroy current target."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_23r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_23r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_24);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 07: automatic re-targeting; select nearest enemy; face the new target (mission3.cpp:745-820)
    private Goal CreateGoal07()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm3_25));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_26));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyTargetEnemy));
            goal.AddStartAction(new MessageAction(Site, "Target an enemy using the F key. Target a friendly using the T key."));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayTargetEnemy));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_28));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var pointing = new ObjectPointingAtCondition(Site, Site.PlayerShip.Ref, new PlayerCurrentTarget(Site));
            pointing.SetMinimumAngle(1.0f);
            var goal = new Goal(pointing);
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_29));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_29));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_30));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.1f, Site));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 08: activate the machine gun; ammo gauge; destroy target; face next hull (mission3.cpp:823-902)
    private Goal CreateGoal08()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm3_31);
            goal.AddStartAction(new ToggleWeaponAction(Site, 1));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetStateBitsCondition(Site, Site.PlayerShip.Ref, WeaponsMask));
            if (HasJoystick)
                goal.AddStartAction(new MessageAction(Site, "Fire the main weapon by pressing the trigger button on your joystick."));
            else
                goal.AddStartAction(new MessageAction(Site, "Left click to fire your main weapon."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_31r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_31r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_32));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_33);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayAmmo));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetShipIsDestroyedCondition(Site, new PlayerCurrentTarget(Site, true)));
            goal.AddStartAction(new MessageAction(Site, "Destroy current target."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_34));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_34));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var pointing = new ObjectPointingAtCondition(Site, Site.PlayerShip.Ref, new PlayerCurrentTarget(Site));
            pointing.SetMinimumAngle(1.0f);
            var goal = new Goal(pointing);
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_35));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_35));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 09: swap in the missile launcher; missile overlay; destroy target; recaps (mission3.cpp:905-989)
    private Goal CreateGoal09()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm3_36);
            goal.AddStartAction(new SwapWeaponAction(Site, EtMagazine, 0));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        // wait for missiles to fire (mission3.cpp:927-951)
        {
            var goal = new Goal(new GetStateBitsCondition(Site, Site.PlayerShip.Ref, MissileFire));
            if (HasJoystick)
            {
                goal.AddStartAction(new MessageAction(Site, "Fire missiles by pressing the #2 button on your joystick."));
                goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_37));
            }
            else
            {
                goal.AddStartAction(new MessageAction(Site, "Press 5 or Middle click to fire your secondary weapon."));
                goal.AddStartAction(new SetHudOverlayAction(Site, OverlayMissile));
                goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_38));
            }
            var constraints = new SetControlConstraintsAction();
            constraints.EnableInputAction(MissileFire);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_39));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetShipIsDestroyedCondition(Site, new PlayerCurrentTarget(Site, true)));
            goal.AddStartAction(new MessageAction(Site, "Destroy current target."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_23r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_23r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_40));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.1f, Site));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 10: the inventory display; kill bonus gauge (mission3.cpp:992-1060)
    private Goal CreateGoal10()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm3_41);
            goal.AddStartAction(new ShowPaneAction(Site, PaneInventory));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayInventory));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_42);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACE key when you are comfortable with the Inventory Display."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_15));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_15));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm3_43);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayKillBonus));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm3_44));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 11: enable all controls; full ammo/missiles; ready to go home (mission3.cpp:1063-1102)
    private Goal CreateGoal11()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm3_45);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            var constraints = new SetControlConstraintsAction();
            constraints.EnableInputAction(unchecked((int)0xffffffff));
            constraints.ScaleInputControl(ControlAxis.Throttle, 1.0f);
            goal.AddStartAction(constraints);
            goal.AddStartAction(new ToggleWeaponAction(Site, 1));
            goal.AddStartAction(new RepairRearmAction(Site));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACE key when you are ready to go home."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_46));
            goal.AddConstraintCondition(CreateTooLongCondition(120.0f, Tm3_46));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 12: target the home base (mission3.cpp:1105-1132)
    private Goal CreateGoal12()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm3_47));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(new GetTargetCondition(Site, Site.PlayerShip.Ref, _station));
            goal.AddStartAction(new MessageAction(Site, "Press the B key to target the outpost."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm3_48));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_48));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 13: fly home and land (mission3.cpp:1135-1156)
    private Goal CreateGoal13()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm3_49));

        {
            var goal = new Goal(new FalseCondition());
            goal.AddStartAction(new MessageAction(Site, "Land in a green hangar to complete the mission."));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm3_49r));
            list.AddGoal(goal);
        }

        return new Goal(list);
    }
}
