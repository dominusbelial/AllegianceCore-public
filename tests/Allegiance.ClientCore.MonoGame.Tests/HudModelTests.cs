using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class HudModelTests
{
    private static HudFrame Frame => new(0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 1f,
        120f, -1f, false, false, true, 0, false, 0f, false, false,
        0f, 0, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    [Fact]
    public void Damage_flash_changes_only_hull_and_shield_and_restores_between_pulses()
    {
        var normal = HudModel.Create(Frame);
        var flash = HudModel.Create(Frame with { Flashing = true });
        Assert.Equal(2, flash.Gauges.Count(g => g.DamageFlash));
        foreach (var gauge in flash.Gauges)
        {
            var before = normal.Gauges.Single(g => g.Kind == gauge.Kind);
            if (gauge.Kind is GaugeKind.Hull or GaugeKind.Shield)
                Assert.Equal(new HudColor(1, 0, 0), gauge.Color);
            else Assert.Equal(before, gauge);
        }
        Assert.Equal(normal.Gauges, HudModel.Create(Frame).Gauges);
    }

    [Fact]
    public void Idle_throttle_is_healthy_and_does_not_inherit_resource_warning()
    {
        var model = HudModel.Create(Frame with { Throttle = -2, HullFraction = -1, ShieldFraction = 2 });
        Assert.Equal(0, model.Gauges.Single(g => g.Kind == GaugeKind.Thrust).Fraction);
        Assert.Equal(HudColor.TealAccent, model.Gauges.Single(g => g.Kind == GaugeKind.Thrust).Color);
        Assert.Equal(0, model.Gauges.Single(g => g.Kind == GaugeKind.Hull).Fraction);
        Assert.Equal(1, model.Gauges.Single(g => g.Kind == GaugeKind.Shield).Fraction);
    }

    [Theory]
    [InlineData(false, 0, false, 0.25f)]
    [InlineData(true, 0, false, 1f)]
    [InlineData(false, ShipControlState.LeftButton, false, 1f)]
    [InlineData(false, ShipControlState.BackwardButton, false, 1f)]
    [InlineData(true, ShipControlState.ForwardButton, true, 0f)]
    public void Thrust_matches_native_button_override_and_turret_gate(bool afterburner, int state, bool turret, float expected)
    {
        var model = HudModel.Create(Frame with { Throttle = -0.5f, AfterburnerActive = afterburner, ControlState = state }, new() { Turret = turret });
        Assert.Equal(expected, model.Gauges.Single(g => g.Kind == GaugeKind.Thrust).Fraction);
        Assert.Equal(expected, (model.Telemetry!.Value.Throttle + 1f) / 2f);
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    public void Instruments_obey_view_and_lifecycle(bool hidden, bool docked, bool dead, bool command, bool expected)
    {
        var model = HudModel.Create(Frame, new() { Hidden = hidden, Docked = docked, Dead = dead, CommandView = command });
        Assert.Equal(expected, model.InstrumentsVisible);
        Assert.All(model.Gauges, g => Assert.Equal(expected, g.Visible));
    }

    [Theory]
    [InlineData(CameraMode.Cockpit, true, true)]
    [InlineData(CameraMode.Chase, true, false)]
    [InlineData(CameraMode.ExternalMissile, true, false)]
    [InlineData(CameraMode.ExternalTurret, false, false)]
    [InlineData(CameraMode.ExternalOrbit, false, false)]
    [InlineData(CameraMode.Command, false, false)]
    public void Indicators_follow_native_camera_gate(CameraMode camera, bool indicators, bool reticle)
    {
        var model = HudModel.Create(Frame with { MouseStick = new(.4f, .3f, .1f, false) }, new() { Camera = camera });
        Assert.Equal(indicators, model.IndicatorsVisible);
        Assert.Equal(reticle, model.ReticleVisible);
        Assert.Equal(indicators, model.MouseStickVisible);
        Assert.Equal(indicators, model.Elements.Single(e => e.Id == "Joystick turning indicator").Visible);
    }

    [Fact]
    public void Pod_uses_oxygen_without_inventing_ammo_or_energy_and_rescue_hides_panel()
    {
        var pod = Frame with { IsAdriftLifepod = true, OxygenSecondsRemaining = 90 };
        var model = HudModel.Create(pod);
        Assert.True(model.PodVisible);
        Assert.Equal("OXY", model.Gauges.Single(g => g.Kind == GaugeKind.Fuel).Label);
        Assert.Equal(pod.FuelFraction, model.Gauges.Single(g => g.Kind == GaugeKind.Fuel).Fraction);
        Assert.Equal(0, model.Gauges.Single(g => g.Kind == GaugeKind.Ammo).Fraction);
        Assert.Equal(0, model.Gauges.Single(g => g.Kind == GaugeKind.Energy).Fraction);
        Assert.False(HudModel.Create(pod, new() { Docked = true }).PodVisible);
        Assert.True(HudModel.Create(pod, new() { Dead = true }).DeathVisible);
        Assert.False(HudModel.Create(pod, new() { Dead = true }).PodVisible);
    }

    [Fact]
    public void Boundary_warnings_are_independent_of_the_renderers_texture_availability()
    {
        var frame = Frame with { WarningMask = ShipWarningMask.OutOfBounds | ShipWarningMask.CrowdedSector };
        Assert.Equal(new[] { "OUT OF BOUNDS", "SECTOR CROWDED" }, HudModel.Create(frame).Warnings);
        Assert.Empty(HudModel.Create(Frame).Warnings);
        Assert.Empty(HudModel.Create(frame, new() { Dead = true }).Warnings);
    }

    [Fact]
    public void Snapshot_owns_mutable_lists_and_keeps_unknown_telemetry_unknown()
    {
        var notices = new List<HudNotice> { new("hit", true, 1) };
        var contacts = new List<HudContact> { new(HudContactKind.Ship, new(12, 34), false, .8f, null, true, false) };
        var commands = new List<string> { "COMMAND: TOP DOWN" };
        var model = HudModel.Create(null, new() { Notices = notices, Contacts = contacts, CommandLines = commands });
        notices.Clear(); contacts.Clear(); commands.Clear();
        Assert.Single(model.Context.Notices);
        Assert.Single(model.Context.Contacts);
        Assert.Single(model.Context.CommandLines);
        Assert.Null(model.Telemetry);
        Assert.Empty(model.Gauges);
        Assert.Equal("mps", model.SpeedUnit);
    }

    [Fact]
    public void Native_landmarks_are_resolution_and_art_independent()
    {
        var model = HudModel.Create(Frame);
        Assert.Equal(new HudPoint(-126.5f, 93.5f), model.Gauges.Single(g => g.Kind == GaugeKind.Thrust).NativeOffset);
        Assert.Equal(new HudPoint(126.5f, -112.5f), model.Gauges.Single(g => g.Kind == GaugeKind.Energy).NativeOffset);
        Assert.Equal(new HudPoint(-65.5f, 35.5f), model.Gauges.Single(g => g.Kind == GaugeKind.Hull).NativeOffset);
    }

    [Fact]
    public void Native_inventory_keeps_unavailable_fields_unknown_and_target_bars_need_real_data()
    {
        var model = HudModel.Create(Frame, new()
        {
            Target = new(new(300, 400), false, "rock", null, null, null, .5f),
            Lock = new(new(100, 200), MissileLockFeedback.LockState.None, 0)
        });
        var missing = model.Elements.Single(e => e.Id == "Signature percentage");
        Assert.False(missing.Available);
        Assert.False(missing.Visible);
        Assert.Null(missing.Value);
        Assert.False(model.Elements.Single(e => e.Id == "Missile lock flower").Visible);
        Assert.False(model.Elements.Single(e => e.Id == "Equipment part identity and key").Available);
        Assert.Equal(Frame.HullFraction, model.Elements.Single(e => e.Id == "Hull integrity").Value);
        Assert.False(model.Elements.Single(e => e.Id == "Target hull").Available);
        Assert.False(model.Elements.Single(e => e.Id == "Target range or sector").Available);
        var pane = new HudTargetPane(true, "Enemy fighter", "Fighter", "Rixian", new(1, 0, 0), .6f, .3f, 1250f, 43f, null, null, true, true);
        var ranged = HudModel.Create(Frame, model.Context with { TargetPane = pane });
        Assert.Equal(1250, ranged.Elements.Single(e => e.Id == "Target range or sector").Value);
        Assert.True(ranged.Elements.Single(e => e.Id == "Target range or sector").Visible);
        Assert.Equal(43f, ranged.Elements.Single(e => e.Id == "Target speed").Value);
        Assert.Equal("Rixian", ranged.Elements.Single(e => e.Id == "Target side identity").Text);
        Assert.True(ranged.Elements.Single(e => e.Id == "Target in weapon range").Visible);
        var ore = model.Elements.Single(e => e.Id == "Target ore");
        Assert.True(ore.Visible);
        Assert.Equal(.5f, ore.Value);
        Assert.Equal(new HudPoint(300, 400), ore.ScreenPosition);
        Assert.Equal(new HudPlacement(HudAnchor.Center, -39, -88), model.Elements.Single(e => e.Id == "Speed").Placement);
        Assert.Equal(new HudPlacement(HudAnchor.Bottom, -85, 58), model.Elements.Single(e => e.Id == "Target hull").Placement);
    }

    [Fact]
    public void Chat_is_a_snapshot_and_obeys_the_shared_HUD_visibility()
    {
        var lines = new List<string> { "incoming" };
        var context = new HudContext { Chat = new(lines, 0, "hello", true, 1, "TEAM") };
        var model = HudModel.Create(Frame, context);
        lines.Clear();
        Assert.Single(model.Context.Chat!.Lines);
        Assert.True(model.Elements.Single(e => e.Id == "Chat composition and recipient").Visible);
        var hidden = HudModel.Create(Frame, context with { Hidden = true });
        Assert.False(hidden.Elements.Single(e => e.Id == "Chat composition and recipient").Visible);
    }

    [Fact]
    public void Radar_overlay_and_label_rows_follow_contact_data_and_are_snapshotted()
    {
        var contacts = new List<HudContact>
        {
            new(HudContactKind.Ship, new(100, 100), true, 1f, null, true, false,
                "Scout", 700, .5f, .6f, null, false, false, true,
                AcceptedOrder: true, CommandId: 2, CommandAccepted: true, NearestEnemy: true),
        };
        var model = HudModel.Create(Frame, new HudContext { Contacts = contacts, RadarMode = RadarMode.All });
        Assert.True(model.Elements.Single(e => e.Id == "Radar name labels").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Radar range labels").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Radar hull and shield bars").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Radar accepted order brackets").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Radar nearest enemy bracket").Visible);
        Assert.Equal(1f, model.Elements.Single(e => e.Id == "Radar name labels").Value);
        contacts.Clear();
        Assert.Single(model.Context.Contacts);
    }

    [Fact]
    public void Own_status_shows_the_detection_eye_on_the_raw_detected_bit()
    {
        // Native own-ship eye visible ⇔ RAW ShipStatus.Detected == true
        // (console.cpp:811 shows RadOn for the inverted PlayerInfo binding 0;
        // clintlib.h:438 is that inversion; dialog.mdl:206-207,2526-2534).
        var model = HudModel.Create(Frame with { Detected = true, CarriedFlag = true, CarriedArtifact = false });
        Assert.True(model.Status.DetectionEyeVisible);
        Assert.True(model.Status.CarriedFlag);
        Assert.False(model.Status.CarriedArtifact);
        Assert.True(model.Elements.Single(e => e.Id == "Detection eye").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Carried flag").Visible);
        Assert.False(model.Elements.Single(e => e.Id == "Carried artifact").Visible);

        var detected = HudModel.Create(Frame with { Detected = false, CarriedFlag = false, CarriedArtifact = true });
        Assert.False(detected.Status.DetectionEyeVisible);
        Assert.True(detected.Status.CarriedArtifact);
        Assert.False(detected.Status.CarriedFlag);
        Assert.False(detected.Elements.Single(e => e.Id == "Detection eye").Visible);
    }

    [Fact]
    public void Own_status_surfaces_autopilot_vector_lock_flag_and_artifact()
    {
        var model = HudModel.Create(Frame with
        {
            AutopilotEngaged = true,
            VectorLockEngaged = true,
            CarriedFlag = true,
            CarriedArtifact = false
        });
        Assert.True(model.Status.AutopilotEngaged);
        Assert.True(model.Status.VectorLockEngaged);
        Assert.True(model.Status.CarriedFlag);
        Assert.False(model.Status.CarriedArtifact);
        Assert.True(model.Elements.Single(e => e.Id == "Autopilot status").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Vector lock status").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Carried flag").Visible);
        Assert.False(model.Elements.Single(e => e.Id == "Carried artifact").Visible);
    }

    [Fact]
    public void Cloak_active_and_drained_follow_energy_and_instruments()
    {
        var drained = HudModel.Create(Frame with { Cloaked = true, EnergyFraction = 0.1f });
        Assert.True(drained.Status.CloakActive);
        Assert.True(drained.Status.CloakDrained);
        Assert.True(drained.Elements.Single(e => e.Id == "Cloak strength and drained state").Visible);

        var fullEnergy = HudModel.Create(Frame with { Cloaked = true, EnergyFraction = 0.9f });
        Assert.True(fullEnergy.Status.CloakActive);
        Assert.False(fullEnergy.Status.CloakDrained);

        var docked = HudModel.Create(Frame with { Cloaked = true, EnergyFraction = 0.1f }, new() { Docked = true });
        Assert.False(docked.Status.Visible);
        Assert.False(docked.Status.CloakActive);
    }

    [Fact]
    public void Signature_and_kill_bonus_surface_native_units_and_stay_null_when_unknown()
    {
        var model = HudModel.Create(Frame with { SignaturePercent = 37.5f, KillBonusPercent = 12f });
        Assert.Equal(37.5f, model.Status.SignaturePercent);
        Assert.Equal(12f, model.Status.KillBonusPercent);
        Assert.True(model.Elements.Single(e => e.Id == "Signature percentage").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Kill bonus").Visible);

        var unknown = HudModel.Create(Frame);
        Assert.Null(unknown.Status.SignaturePercent);
        Assert.False(unknown.Elements.Single(e => e.Id == "Signature percentage").Visible);
    }

    [Fact]
    public void Gauge_labels_match_native_dialog_mdl_strings()
    {
        var model = HudModel.Create(Frame);
        Assert.Equal("ARMOR", model.Gauges.Single(g => g.Kind == GaugeKind.Hull).Label);
        Assert.Equal("SHIELD", model.Gauges.Single(g => g.Kind == GaugeKind.Shield).Label);
        Assert.Equal("ENERGY", model.Gauges.Single(g => g.Kind == GaugeKind.Energy).Label);
        Assert.Equal("FUEL", model.Gauges.Single(g => g.Kind == GaugeKind.Fuel).Label);
        Assert.Equal("AMMO", model.Gauges.Single(g => g.Kind == GaugeKind.Ammo).Label);
        Assert.Equal("THRUST", model.Gauges.Single(g => g.Kind == GaugeKind.Thrust).Label);
    }

    [Fact]
    public void Console_text_is_a_snapshot_and_its_elements_follow_the_native_gates()
    {
        var tips = new List<string> { "tip a", "tip b", "tip c" };
        var critical = new List<HudNotice> { new("critical", true, 1) };
        var console = new HudConsoleText
        {
            SectorName = "Alystra", CarrierName = "Pilot", BoardState = 2,
            AcceptedOrder = "defend Garrison", QueuedOrder = "attack Fighter",
            VoteText = "Mutiny?", OrdinaryTips = tips, CriticalTips = critical
        };
        var model = HudModel.Create(Frame, new() { Console = console });
        tips.Clear(); critical.Clear();
        Assert.Equal(3, model.Context.Console!.OrdinaryTips.Count);
        Assert.Single(model.Context.Console.CriticalTips);
        Assert.Equal("Alystra", model.Context.Console.SectorName);
        Assert.Equal(2, model.Context.Console.BoardState);

        Assert.True(model.Elements.Single(e => e.Id == "Sector location").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Turret carrier identity").Visible);
        Assert.Equal("Alystra", model.Elements.Single(e => e.Id == "Sector location").Text);
        Assert.True(model.Elements.Single(e => e.Id == "Accepted order text").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Queued order text").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Vote banner").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Tip text lines").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Critical tip text lines").Visible);
        Assert.Equal("tip a\ntip b\ntip c", model.Elements.Single(e => e.Id == "Tip text lines").Text);

        // Board state text is loadout-only in native, so it is exposed but not drawn in flight.
        var board = model.Elements.Single(e => e.Id == "Observer and turret board state");
        Assert.True(board.Available);
        Assert.False(board.Visible);
        Assert.Equal(2, board.Value);
        Assert.Equal("Turret of Pilot", board.Text);

        // Unknown readings stay null, never fabricated zeroes.
        var noLives = HudModel.Create(Frame);
        Assert.False(noLives.Elements.Single(e => e.Id == "Lives remaining").Available);
        Assert.False(noLives.Elements.Single(e => e.Id == "Mission kills").Available);
    }

    [Theory]
    [InlineData(CameraMode.Cockpit, 120f, true)]
    [InlineData(CameraMode.Cockpit, 0f, false)]
    [InlineData(CameraMode.Chase, 120f, false)]
    public void Velocity_marker_is_cockpit_only_and_hidden_at_zero_speed(CameraMode camera, float speed, bool expected)
    {
        var model = HudModel.Create(Frame with { Speed = speed }, new() { Camera = camera, VelocityMarker = new(100, 200) });
        Assert.Equal(expected, model.VelocityMarkerVisible);
        Assert.Equal(expected, model.Elements.Single(e => e.Id == "Velocity direction marker").Visible);
    }

    [Theory]
    [InlineData(CameraMode.Cockpit, true)]
    [InlineData(CameraMode.Chase, true)]
    [InlineData(CameraMode.ExternalTurret, false)]
    [InlineData(CameraMode.Command, false)]
    public void Joystick_indicator_follows_native_camera_gate(CameraMode camera, bool expected)
    {
        var model = HudModel.Create(Frame with { TurnYaw = .6f, TurnPitch = .8f }, new() { Camera = camera });
        Assert.Equal(expected, model.JoystickIndicatorVisible);
        Assert.Equal(expected, model.Elements.Single(e => e.Id == "Joystick turning indicator").Visible);
    }

    [Fact]
    public void Joystick_indicator_magnitude_is_clamped_to_one()
    {
        var model = HudModel.Create(Frame with { TurnYaw = 2f, TurnPitch = 2f });
        Assert.Equal(1f, model.Elements.Single(e => e.Id == "Joystick turning indicator").Value);
    }

    [Theory]
    [InlineData(CameraMode.Cockpit, true, true, true)]
    [InlineData(CameraMode.Cockpit, false, true, false)]
    [InlineData(CameraMode.Chase, true, false, true)]
    [InlineData(CameraMode.Chase, false, false, true)]
    public void Lead_marker_and_predictor_follow_the_native_split(CameraMode camera, bool inRange, bool leadVisible, bool aimVisible)
    {
        var model = HudModel.Create(Frame, new() { Camera = camera, Lead = new(new(300, 400), new(350, 420), inRange, .8f, 1) });
        Assert.Equal(leadVisible, model.LeadMarkerVisible);
        Assert.Equal(aimVisible, model.AimPredictorVisible);
        Assert.Equal(leadVisible, model.Elements.Single(e => e.Id == "Ballistic lead marker").Visible);
        Assert.Equal(aimVisible, model.Elements.Single(e => e.Id == "Aim hit-probability predictor").Visible);
    }

    [Theory]
    [InlineData(CameraMode.Chase, true)]
    [InlineData(CameraMode.ExternalMissile, true)]
    [InlineData(CameraMode.Cockpit, false)]
    public void Chase_aim_is_only_in_chase_or_missile_without_a_target(CameraMode camera, bool expected)
    {
        var model = HudModel.Create(Frame, new() { Camera = camera, ChaseAim = new(400, 300) });
        Assert.Equal(expected, model.ChaseAimVisible);
        Assert.Equal(expected, model.Elements.Single(e => e.Id == "Chase weapon aim without target").Visible);
    }

    [Fact]
    public void Chase_aim_hides_when_a_target_gives_lead()
    {
        var model = HudModel.Create(Frame, new() { Camera = CameraMode.Chase, ChaseAim = new(400, 300), Lead = new(new(300, 400), new(350, 420), true, .8f, 1) });
        Assert.False(model.ChaseAimVisible);
    }

    [Fact]
    public void Order_arrows_are_visible_in_indicator_cameras_when_a_direction_exists()
    {
        var arrows = new HudOrderArrows(new(1, 0), new(0, 1), false);
        Assert.True(HudModel.Create(Frame, new() { OrderArrows = arrows }).OrderArrowsVisible);
        Assert.False(HudModel.Create(Frame, new() { OrderArrows = new HudOrderArrows(null, null, false) }).OrderArrowsVisible);
        Assert.False(HudModel.Create(Frame, new() { Camera = CameraMode.ExternalTurret, OrderArrows = arrows }).OrderArrowsVisible);
    }

    [Theory]
    [InlineData(CameraMode.Cockpit, true)]
    [InlineData(CameraMode.Chase, true)]
    [InlineData(CameraMode.ExternalOverride, true)]   // ofGloatCam (WinTrek/WinTrek.cpp:6844)
    [InlineData(CameraMode.ExternalTurret, false)]
    [InlineData(CameraMode.Command, false)]
    public void Target_pane_follows_native_cockpit_chase_mask(CameraMode camera, bool expected)
    {
        var pane = new HudTargetPane(true, "Enemy fighter", "Fighter", null, new(1, 0, 0), .6f, .3f, 1250f, 43f, null, null, true, true);
        var model = HudModel.Create(Frame, new() { Camera = camera, TargetPane = pane });
        Assert.Equal(expected, model.TargetPaneVisible);
        Assert.Equal(expected, model.Elements.Single(e => e.Id == "Target name").Visible);
    }

    [Fact]
    public void Target_pane_hides_when_docked_dead_or_hidden_but_not_for_null_telemetry()
    {
        var pane = new HudTargetPane(true, "rock", "asteroid", null, new(1, 1, 1), null, null, null, 0f, null, null, false, false);
        Assert.False(HudModel.Create(Frame, new() { Docked = true, TargetPane = pane }).TargetPaneVisible);
        Assert.False(HudModel.Create(Frame, new() { Dead = true, TargetPane = pane }).TargetPaneVisible);
        Assert.False(HudModel.Create(Frame, new() { Hidden = true, TargetPane = pane }).TargetPaneVisible);
        // The pane is telemetry-independent: a selected target still shows it.
        Assert.True(HudModel.Create(null, new() { TargetPane = pane }).TargetPaneVisible);
    }
}
