using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 2 — "Basic Flight" — a faithful translation of <c>src/training/mission2.cpp</c> into the
/// ported goal/condition/action framework. The narrative voice-over ordering, the constraint guards and
/// the completion checks all match native: nine sub-goals that teach radar, turning, throttle, the
/// speed/motion/fuel gauges, the booster, and finally autopilot home. Pure data: it builds a
/// <see cref="GoalList"/> and runs against an injected <see cref="ITrainingSite"/>, with no rendering or
/// host.
/// </summary>
public class Mission2 : TrainingMission
{
    /// <summary>Native <c>GetStartSectorID</c> returns 1010 (<c>mission2.cpp:84-87</c>).</summary>
    private const int StartSector = 1010;

    /// <summary>Hull id of the interceptor the player flies (<c>mission2.cpp:99</c>).</summary>
    private const int InterceptorHull = 412;

    /// <summary>The commander drone id, created in native <c>LoadUniverse</c> (<c>trainingmission.cpp:797</c>).
    /// TR-1 uses a stable id the test site registers.</summary>
    private const int CommanderShipId = 990;

    private readonly TargetRef _commander = new(ObjectType.Ship, CommanderShipId);
    private readonly TargetRef _station = new(ObjectType.Station, StartSector);
    private int _nextBuoyId = 950;

    /// <summary>Native branches on joystick presence for several prompts (<c>mission2.cpp:403</c>); the
    /// keyboard branch is the test path (no joystick).</summary>
    protected virtual bool HasJoystick => false;

    public Mission2(ITrainingSite site)
        : base(site)
    {
    }

    public override int GetMissionID() => Tm2BasicFlight;

    public override int GetStartSectorId() => StartSector;

    /// <summary>Native <c>Mission2::ShipLanded</c> (<c>mission2.cpp:90-94</c>) — landing ends the mission.</summary>
    public override bool ShipLanded()
    {
        Terminate();
        return false;
    }

    protected override void CreateUniverse()
    {
        // Host-side universe construction (LoadUniverse / commander placement,
        // trainingmission.cpp:651-807 + mission2.cpp:97-115) is TR-2's job once the embedded host lands.
    }

    protected override Condition CreateMission()
    {
        var goalList = new GoalList();
        var root = new Goal(goalList);

        TargetRef ship = Site.PlayerShip.Ref;

        // constraint: turn the ship back in when it strays beyond 7500m (then force-stop beyond 7000m)
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

        return root;
    }

    // Goal 01: intro audio; controls locked; radar explanation (mission2.cpp:150-204)
    private Goal CreateGoal01()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(2000); // salCommenceScanSound placeholder
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
        list.AddGoal(CreatePlaySoundGoal(Tm2_01));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.7f, Site));
            goal.AddStartAction(new SetDisplayModeAction(Site, CameraCockpit));
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm2_02));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_03));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 02: the commander flies overhead to demo the radar icon (mission2.cpp:207-280)
    private Goal CreateGoal02()
    {
        var list = new GoalList();
        int buoyId = NewBuoyId();

        list.AddGoal(CreatePlaySoundGoal(Tm2_04));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_05);
            goal.AddStartAction(new CreateWaypointAction(Site, buoyId, new Vector3(-200f, -200f, 400f), StartSector));
            goal.AddStartAction(new SetCommandAction(Site, _commander, CommandCurrent, new TargetRef(ObjectType.Buoy, buoyId), CmdGoto));
            goal.AddStartAction(new SetCommandAction(Site, _commander, CommandAccepted, new TargetRef(ObjectType.Buoy, buoyId), CmdGoto));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(new ObjectWithinRadiusCondition(Site, _commander, new TargetRef(ObjectType.Buoy, buoyId), 100.0f)));

        {
            var goal = CreatePlaySoundGoal(Tm2_06);
            goal.AddStartAction(new SetCommandAction(Site, _commander, CommandCurrent, null, CmdDoNothing));
            goal.AddStartAction(new SetCommandAction(Site, _commander, CommandAccepted, null, CmdDoNothing));
            list.AddGoal(goal);
        }

        {
            var pointing = new ObjectPointingAtCondition(Site, Site.PlayerShip.Ref, _commander);
            pointing.SetMinimumAngle(1.0f);
            var goal = new Goal(pointing);
            var turnTo = new TurnToAction(Site, Site.PlayerShip.Ref, _commander);
            goal.AddConstraintCondition(new ConditionalAction(new TrueCondition(), turnTo));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            goal.AddStartAction(new SetControlsAction());
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm2_07));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 03: release roll controls; practice turning; spacebar to continue (mission2.cpp:283-374)
    private Goal CreateGoal03()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm2_08));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.1f, Site));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayRoll));
            var constraints = new SetControlConstraintsAction();
            constraints.ScaleInputControl(ControlAxis.Yaw, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 1.0f);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        // (joystick-vs-keyboard flight-coaching voice-overs commented out in native — mission2.cpp:307-331)

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_11));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm2_11r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm2_11r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_12));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.01f, Site));
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        return new Goal(list);
    }

    // Goal 04: throttle practice (mission2.cpp:377-455)
    private Goal CreateGoal04()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm2_13));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var constraints = new SetControlConstraintsAction();
            constraints.ScaleInputControl(ControlAxis.Yaw, 0.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 0.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 0.0f);
            constraints.ScaleInputControl(ControlAxis.Throttle, 1.0f);
            goal.AddStartAction(constraints);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayThrottle));
            list.AddGoal(goal);
        }

        if (!HasJoystick)
            list.AddGoal(CreatePlaySoundGoal(Tm2_15));
        else
            list.AddGoal(CreatePlaySoundGoal(Tm2_14Safe)); // joystick throttle variant

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_16));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetControlActiveCondition(Site, Site.PlayerShip.Ref, ControlAxis.Throttle, 1.0f));
            goal.AddStartAction(new MessageAction(Site, "Press BACKSLASH (\\) or set your joystick throttle to maximum for full thrust."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm2_16r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm2_16r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_17));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 05: read the throttle gauge; controls disabled again (mission2.cpp:458-502)
    private Goal CreateGoal05()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm2_18));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = new Goal(NotCondition.Of(new GetControlActiveCondition(Site, Site.PlayerShip.Ref, ControlAxis.Throttle, 1.0f)));
            goal.AddStartAction(new MessageAction(Site, "Use your scroll wheel or joystick throttle to adjust thrust."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm2_18r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm2_18r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_19);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 06: velocity/motion gauges; practice turning + throttle (mission2.cpp:505-649)
    private Goal CreateGoal06()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm2_20);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlaySpeed));
            var constraints = new SetControlConstraintsAction();
            constraints.ScaleInputControl(ControlAxis.Throttle, 0.0f);
            goal.AddStartAction(constraints);
            var controls = new SetControlsAction();
            controls.SetInputControl(ControlAxis.Throttle, 0.33f);
            goal.AddStartAction(controls);
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_21);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayMotion));
            list.AddGoal(goal);
        }

        ThrottledSpin(list, 0.5f, yaw: -0.5f);
        ThrottledSpin(list, 0.5f, yaw: 0.0f);
        ThrottledSpin(list, 0.5f, pitch: -0.5f);
        ThrottledSpin(list, 0.5f, yaw: 0.0f);
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        ThrottledSpin(list, 0.5f, yaw: 0.0f);

        list.AddGoal(CreatePlaySoundGoal(Tm2_22));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.2f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_23));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.5f, Site));
            var constraints = new SetControlConstraintsAction();
            constraints.ScaleInputControl(ControlAxis.Yaw, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Pitch, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Roll, 1.0f);
            constraints.ScaleInputControl(ControlAxis.Throttle, 1.0f);
            goal.AddStartAction(constraints);
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm2_24));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetKeyCondition(Site, KeyFireBooster));
            goal.AddStartAction(new MessageAction(Site, "Press the SPACEBAR when you are ready to proceed."));
            goal.AddStartAction(new PlaySoundAction(Site, Tm2_24r));
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm2_24r));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_25));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 07: the booster (mission2.cpp:652-773)
    private Goal CreateGoal07()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm2_26));

        {
            var goal = new Goal(ElapsedTimeCondition.Create(0.25f, Site));
            var controls = new SetControlsAction();
            controls.SetInputAction(AfterburnerButton);
            goal.AddStartAction(controls);
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm2_27));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.25f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_28);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlaySpeed));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_29);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayFuel));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(ElapsedTimeCondition.Create(1.75f, Site));
            goal.AddStartAction(new SetControlsAction());
            list.AddGoal(goal);
        }

        list.AddGoal(CreatePlaySoundGoal(Tm2_30));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_31);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayBooster));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        {
            var goal = new Goal(new GetStateBitsCondition(Site, Site.PlayerShip.Ref, AfterburnerButton));
            var constraints = new SetControlConstraintsAction();
            constraints.EnableInputAction(AfterburnerButton);
            goal.AddStartAction(constraints);
            if (HasJoystick)
            {
                goal.AddStartAction(new PlaySoundAction(Site, Tm2_32));
                goal.AddStartAction(new MessageAction(Site, "Press and hold button #3 on your joystick to fire the booster."));
            }
            else
            {
                goal.AddStartAction(new MessageAction(Site, "Press and hold the SPACE key to fire the booster."));
            }
            goal.AddConstraintCondition(CreateTooLongCondition(30.0f, Tm2_33));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(5.0f, Site)));

        {
            var goal = CreatePlaySoundGoal(Tm2_34);
            var constraints = new SetControlConstraintsAction();
            constraints.DisableInputAction(AfterburnerButton);
            goal.AddStartAction(constraints);
            goal.AddStartAction(new SetHudOverlayAction(Site, OverlayNone));
            list.AddGoal(goal);
        }

        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));

        return new Goal(list);
    }

    // Goal 08: recap + autopilot home (mission2.cpp:776-821)
    private Goal CreateGoal08()
    {
        var list = new GoalList();

        list.AddGoal(CreatePlaySoundGoal(Tm2_35));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_36));
        list.AddGoal(new Goal(ElapsedTimeCondition.Create(0.5f, Site)));
        list.AddGoal(CreatePlaySoundGoal(Tm2_37));

        {
            var goal = new Goal(new AndCondition(
                new ObjectWithinRadiusCondition(Site, Site.PlayerShip.Ref, _station, 500.0f),
                ElapsedTimeCondition.Create(0.5f, Site)));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandCurrent, _station, CmdGoto));
            goal.AddStartAction(new SetCommandAction(Site, Site.PlayerShip.Ref, CommandAccepted, _station, CmdGoto));
            goal.AddStartAction(new SetAutopilotAction(Site, Site.PlayerShip.Ref));
            var constraints = new SetControlConstraintsAction();
            constraints.DisableInputAction(AfterburnerButton);
            goal.AddStartAction(constraints);
            goal.AddConstraintCondition(new PeriodicCondition(new ConditionalAction(
                NotCondition.Of(new GetAutopilotCondition(Site, Site.PlayerShip.Ref)),
                new SetAutopilotAction(Site, Site.PlayerShip.Ref)), 2.0f, Site));
            list.AddGoal(goal);
        }

        return new Goal(list);
    }

    // Goal 09: land (mission2.cpp:824-850)
    private Goal CreateGoal09()
    {
        var list = new GoalList();

        {
            var goal = CreatePlaySoundGoal(Tm2_38);
            goal.AddConstraintCondition(new PeriodicCondition(new ConditionalAction(
                NotCondition.Of(new GetAutopilotCondition(Site, Site.PlayerShip.Ref)),
                new SetAutopilotAction(Site, Site.PlayerShip.Ref)), 2.0f, Site));
            list.AddGoal(goal);
        }

        {
            var goal = new Goal(new FalseCondition());
            goal.AddStartAction(new SetDisplayModeAction(Site, CameraExternalStation));
            goal.AddConstraintCondition(new PeriodicCondition(new ConditionalAction(
                NotCondition.Of(new GetAutopilotCondition(Site, Site.PlayerShip.Ref)),
                new SetAutopilotAction(Site, Site.PlayerShip.Ref)), 2.0f, Site));
            list.AddGoal(goal);
        }

        return new Goal(list);
    }

    /// <summary>Helper for the native's repeated "half-second, set throttle 0.33 (+turn)" goals.</summary>
    private void ThrottledSpin(GoalList list, float seconds, float yaw = 0f, float pitch = 0f)
    {
        var goal = new Goal(ElapsedTimeCondition.Create(seconds, Site));
        var controls = new SetControlsAction();
        controls.SetInputControl(ControlAxis.Throttle, 0.33f);
        if (yaw != 0f)
            controls.SetInputControl(ControlAxis.Yaw, yaw);
        if (pitch != 0f)
            controls.SetInputControl(ControlAxis.Pitch, pitch);
        goal.AddStartAction(controls);
        list.AddGoal(goal);
    }

    private int NewBuoyId() => _nextBuoyId++;

    // Placeholder sound for the native salCommenceScanSound in goal 01; distinct from TM2 ids.
    private const int SalCommenceScanSound = 2000;

    // Joystick-only throttle-explanation variant sound (native tm_2_14).
    private const int Tm2_14Safe = 9802;
}

/// <summary>Axis index constants for control actions/conditions (c_axisYaw=0..c_axisThrottle=3, igc.h:855-858).</summary>
internal static class ControlAxis
{
    public const int Yaw = 0;
    public const int Pitch = 1;
    public const int Roll = 2;
    public const int Throttle = 3;
}
