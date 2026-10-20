using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Coordinates relative to the view centre, +X right and +Y down.
/// Gauge offsets are native tutorial landmarks (WinTrek/indicator.cpp:514-566),
/// not bitmap sizes. A renderer chooses its own scale and artwork.</summary>
public readonly record struct HudPoint(float X, float Y);

public readonly record struct HudGauge(
    GaugeKind Kind, float Fraction, HudPoint NativeOffset, string Label,
    HudColor Color, bool DamageFlash, bool Visible);

public readonly record struct HudLock(HudPoint Center, MissileLockFeedback.LockState State, double NowSeconds, float Fraction = 1, float? ScreenRadius = null);
public readonly record struct HudLead(HudPoint Center, HudPoint TargetCenter, bool InRange, float Green, double NowSeconds);
/// <summary>Accepted/current order turn-arrow directions, normalized in
/// centre-relative screen space (+X right, +Y down). SameTarget collapses the
/// pair to the native single yellow arrow (indicator.cpp:786-791).</summary>
public readonly record struct HudOrderArrows(HudPoint? AcceptedDirection, HudPoint? CurrentDirection, bool SameTarget);
public readonly record struct HudTarget(HudPoint Center, bool Engaged, string? Name,
    float? HullFraction, float? ShieldFraction, bool? IsEnemy, float? OreFraction, float? Distance = null, float? ClosingSpeed = null, HudColor? SideColor = null, float? ScreenRadius = null);

/// <summary>The native bottom target pane (WinTrek/consoledata.cpp:40-199,
/// Artwork/dialog.mdl:4040-4242). Unlike <see cref="HudTarget"/> (the projected
/// in-world selection bracket), this pane stays present whenever a target is
/// selected even if it does not project into the current view. Unknown readings
/// stay null and the renderer leaves the corresponding native slot empty.</summary>
public readonly record struct HudTargetPane(
    bool Present,
    string? Name,
    string? TypeDescription,
    string? SideName,
    HudColor SideColor,
    float? HullFraction,
    float? ShieldFraction,
    float? Distance,
    float? Speed,
    float? OreFraction,
    float? KillBonusPercent,
    bool InRange,
    bool IsShip);
public readonly record struct HudNotice(string Text, bool Critical, float Opacity);

/// <summary>HUD-6 resolved own-ship centre-HUD status (the
/// <c>DefaultCenterHudOn</c> group, Artwork/dialog.mdl:2604-2623). Unknown
/// telemetry stays null; renderers present only the available fields.</summary>
public readonly record struct HudOwnStatus(
    float? KillBonusPercent,
    float? SignaturePercent,
    bool DetectionEyeVisible,
    bool CloakActive,
    bool CloakDrained,
    bool AutopilotEngaged,
    bool VectorLockEngaged,
    bool CarriedFlag,
    bool CarriedArtifact,
    bool Visible);

public enum HudContactKind { Ship, Station, Warp, Asteroid, Missile, Probe, Mine, Treasure, Tech }
/// <summary>One radar contact plus its overlay/label payload. The radar
/// snapshot fills the raw readings native <c>radarimage.cpp:590-1120</c> uses;
/// <see cref="RadarLabelModel"/> then applies the per-kind LOD/visibility rules
/// before any renderer draws a bracket or label.</summary>
public readonly record struct HudContact(
    HudContactKind Kind, HudPoint Position,
    bool OnScreen, float Opacity, HudColor? SideColor, bool Selected, bool BuildSpot,
    // HUD-5 radar overlays and labels (radarimage.cpp:334-357,590-1120,1199-1416).
    string? Name = null, int? Range = null,
    float? Shield = null, float? Hull = null, float? Fill = null,
    bool IsEnemy = false, bool IsSelf = false, bool IsSameSide = false,
    bool AcceptedOrder = false, bool QueuedOrder = false,
    bool Threat = false, bool ThreatFlash = false,
    bool Subject = false, bool CarriedFlag = false, bool CarriedArtifact = false,
    bool Ripcord = false, bool Highlight = false, bool NearestEnemy = false,
    int? CommandId = null, bool CommandAccepted = false,
    int? RipcordSeconds = null, bool DetectionEye = false,
    // HUD-4 treasure contact that resolves to the native tech icon
    // (radarimage.cpp:506-511).
    bool IsTech = false);
public sealed record HudChat(IReadOnlyList<string> Lines, int Offset, string Input,
    bool Composing, double NowSeconds, string Recipient, string? FilterLabel = null);

/// <summary>Presentation context supplied by the client; no textures, fonts,
/// graphics device or classic skin settings belong here.</summary>
public sealed record HudContext
{
    public CameraMode Camera { get; init; } = CameraMode.Cockpit;
    public bool Hidden { get; init; }
    public bool Docked { get; init; }
    public bool Dead { get; init; }
    public bool CommandView { get; init; }
    public bool Turret { get; init; }
    public bool HitConfirmation { get; init; }
    public string? PodReturnInstruction { get; init; }
    public string? CameraLabel { get; init; }
    public HudLock? Lock { get; init; }
    public HudLead? Lead { get; init; }
    /// <summary>Projected velocity-direction marker (screen px), cockpit only.
    /// Native directionbmp at indicator.cpp:130-148.</summary>
    public HudPoint? VelocityMarker { get; init; }
    /// <summary>Projected weapon-range aim point when in chase/missile view
    /// with no target (indicator.cpp:367-405).</summary>
    public HudPoint? ChaseAim { get; init; }
    /// <summary>Accepted/current order turn arrows (indicator.cpp:411-457).</summary>
    public HudOrderArrows? OrderArrows { get; init; }
    /// <summary>Native ShowJoystickIndicator preference, default on
    /// (WinTrek.cpp:2739,3059).</summary>
    public bool ShowJoystickIndicator { get; init; } = true;
    public HudTarget? Target { get; init; }
    public HudTargetPane? TargetPane { get; init; }
    public HudPoint? TargetBearing { get; init; }
    public LeaderBotCommand? LeaderCommand { get; init; }
    public string? Ripcord { get; init; }
    public string? Commandee { get; init; }
    public string? Diagnostics { get; init; }
    public IReadOnlyList<HudNotice> Notices { get; init; } = Array.Empty<HudNotice>();
    /// <summary>P745b last-restart-base objectives (attack/defend lines, bracket, bearing).</summary>
    public IReadOnlyList<LastBaseHudView> LastBaseObjectives { get; init; } = Array.Empty<LastBaseHudView>();
    public IReadOnlyList<HudContact> Contacts { get; init; } = Array.Empty<HudContact>();
    public float RadarPulse { get; init; }
    public RadarMode RadarMode { get; init; } = RadarMode.Default;
    /// <summary>Native optional round radar border (WinTrek.cpp:3832;
    /// radarimage.cpp:1130-1177). Defaults to square radar (WinTrek.cpp:2715).</summary>
    public bool RoundRadar { get; init; }
    public HudChat? Chat { get; init; }
    /// <summary>HUD-9 console text and banner frame (sector, carrier, orders,
    /// tips, vote). Null when the client did not supply one.</summary>
    public HudConsoleText? Console { get; init; }
    public string? CargoLine { get; init; }
    public string? FlightPartsLine { get; init; }
    public IReadOnlyList<string> CommandLines { get; init; } = Array.Empty<string>();
    public bool InventoryVisible { get; init; }
}

/// <summary>A renderer-independent snapshot of one HUD frame. HudFrame is the
/// raw telemetry input; this model resolves visibility, resource values, damage
/// flash state, and native layout landmarks before drawing. Classic and future
/// renderers consume the same model. Unknown telemetry stays null.</summary>
public sealed class HudModel
{
    public HudFrame? Telemetry { get; }
    public HudContext Context { get; }
    public IReadOnlyList<HudGauge> Gauges { get; }
    public HudOwnStatus Status { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Complete native inventory, including explicitly unavailable
    /// fields. The classic renderer uses the strongly typed groups above;
    /// another renderer can enumerate these semantic elements as well.</summary>
    public IReadOnlyList<HudElement> Elements => HudElements.Build(this);
    public bool Visible => !Context.Hidden;
    public bool FlightVisible => Visible && !Context.Docked && !Context.Dead;
    public bool InstrumentsVisible => FlightVisible && !Context.CommandView;
    // Native indicator.cpp:763 permits internal/chase/missile cameras.
    public bool IndicatorsVisible => InstrumentsVisible && Context.Camera is CameraMode.Cockpit or CameraMode.Chase or CameraMode.ExternalMissile;
    public bool ReticleVisible => IndicatorsVisible && Context.Camera == CameraMode.Cockpit;
    public bool MouseStickVisible => IndicatorsVisible && Telemetry?.MouseStick is not null;
    // Native RenderDirectionIndicator draws the velocity marker only in the
    // cockpit and only when the rounded source speed is non-zero
    // (indicator.cpp:130-148).
    public bool VelocityMarkerVisible => ReticleVisible && Context.VelocityMarker is not null
        && Telemetry is { } velocityTelemetry && (int)(velocityTelemetry.Speed + 0.5f) != 0;
    // Native RenderJoystickIndicator (indicator.cpp:154-177) is shown in every
    // allowed indicator camera when the ShowJoystickIndicator preference is on
    // (default on, WinTrek.cpp:2739).
    public bool JoystickIndicatorVisible => IndicatorsVisible && Telemetry is not null && Context.ShowJoystickIndicator;
    // Native draws the ballistic lead surface only in the cockpit branch
    // (indicator.cpp:320-330); chase/missile views use the projected aim point.
    public bool LeadMarkerVisible => IndicatorsVisible && Context.Camera == CameraMode.Cockpit && Context.Lead is not null;
    // Native cockpit predictor (centre crosshair, indicator.cpp:334-362) shows
    // only while the intercept is in range; chase/missile views always draw the
    // projected aim predictor (green ramp or out-of-range grey).
    public bool AimPredictorVisible => IndicatorsVisible && Context.Lead is { } aimLead
        && (Context.Camera != CameraMode.Cockpit || aimLead.InRange);
    public bool ChaseAimVisible => IndicatorsVisible
        && Context.Camera is CameraMode.Chase or CameraMode.ExternalMissile
        && Context.ChaseAim is not null && Context.Lead is null;
    public bool OrderArrowsVisible => IndicatorsVisible && Context.OrderArrows is { } arrows
        && (arrows.AcceptedDirection is not null || arrows.CurrentDirection is not null);
    // Native TargetDisplay pane mask is ofInCockpit | ofInChase | ofGloatCam
    // (Artwork/dialog.mdl:5402); ofGloatCam is SetTargetGeo's
    // cmExternalOverride (WinTrek/WinTrek.cpp:6844) — the port's
    // CameraMode.ExternalOverride. Command view and external turret/missile
    // views use their own summaries.
    public bool TargetPaneVisible => InstrumentsVisible
        && Context.TargetPane is { Present: true }
        && Context.Camera is CameraMode.Cockpit or CameraMode.Chase or CameraMode.ExternalOverride;
    public bool PodVisible => FlightVisible && Telemetry is { IsAdriftLifepod: true };
    public bool DeathVisible => Visible && Context.Dead && !Context.Docked;
    public string SpeedUnit => "mps"; // Artwork/dialog.mdl:2546-2554.
    public string CameraLabel => Context.CameraLabel ?? (Context.Camera switch
    {
        CameraMode.Cockpit => "COCKPIT",
        CameraMode.ExternalTurret => "EXT TURRET",
        CameraMode.ExternalMissile => "EXT MISSILE",
        _ => "CHASE"
    });

    private HudModel(HudFrame? telemetry, HudContext context)
    {
        // Copy collections so a subsequent world/loadout update cannot alter a
        // snapshot that a second renderer or an evidence capture still owns.
        Telemetry = telemetry is { } t
            ? t with
            {
                WeaponSystems = Array.AsReadOnly(t.WeaponSystems.ToArray()),
                AmmoFraction = t.IsAdriftLifepod ? 0 : t.AmmoFraction,
                EnergyFraction = t.IsAdriftLifepod ? 0 : t.EnergyFraction,
                Throttle = context.Turret ? -1f
                    : t.AfterburnerActive || (t.ControlState & ShipControlState.MoveButtonsMask) != 0
                        ? 1f : t.Throttle
            }
            : null;
        Context = context with
        {
            Notices = Array.AsReadOnly(context.Notices.ToArray()),
            Contacts = Array.AsReadOnly(context.Contacts.ToArray()),
            Chat = context.Chat is { } chat ? chat with { Lines = Array.AsReadOnly(chat.Lines.ToArray()) } : null,
            Console = context.Console is { } console ? console with
            {
                OrdinaryTips = Array.AsReadOnly(console.OrdinaryTips.ToArray()),
                CriticalTips = Array.AsReadOnly(console.CriticalTips.ToArray())
            } : null,
            CommandLines = Array.AsReadOnly(context.CommandLines.ToArray())
        };
        var gauges = new List<HudGauge>();
        if (Telemetry is { } f)
        {
            Add(GaugeKind.Thrust, (f.Throttle + 1f) / 2f, -126.5f, 93.5f, "THRUST");
            Add(GaugeKind.Fuel, f.FuelFraction, 126.5f, 93.5f, f.IsAdriftLifepod ? "OXY" : "FUEL");
            Add(GaugeKind.Energy, f.IsAdriftLifepod ? 0f : f.EnergyFraction, 126.5f, -112.5f, "ENERGY");
            Add(GaugeKind.Ammo, f.IsAdriftLifepod ? 0f : f.AmmoFraction, -126.5f, -112.5f, "AMMO");
            Add(GaugeKind.Hull, f.HullFraction, -65.5f, 35.5f, "ARMOR");
            Add(GaugeKind.Shield, f.ShieldFraction, 65.5f, 35.5f, "SHIELD");

            void Add(GaugeKind kind, float value, float x, float y, string label)
            {
                float fraction = HudFormat.Clamp01(value);
                // Native uses the damage timer, not a low-resource threshold,
                // to alternate hull/shield red (Artwork/dialog.mdl:2403-2506).
                bool flash = f.Flashing && kind is GaugeKind.Hull or GaugeKind.Shield;
                HudColor color = flash ? new(1f, 0f, 0f)
                    : kind != GaugeKind.Thrust && fraction <= 0.5f
                        ? new(0.95f, 0.30f, 0.22f) : HudColor.TealAccent;
                gauges.Add(new(kind, fraction, new(x, y), label, color, flash, InstrumentsVisible));
            }
        }
        Gauges = gauges.AsReadOnly();
        Status = BuildStatus(Telemetry, InstrumentsVisible);
        var warnings = new List<string>();
        if (FlightVisible && Telemetry is { } own)
        {
            if ((own.WarningMask & ShipWarningMask.OutOfBounds) != 0) warnings.Add("OUT OF BOUNDS");
            if ((own.WarningMask & ShipWarningMask.CrowdedSector) != 0) warnings.Add("SECTOR CROWDED");
        }
        Warnings = warnings.AsReadOnly();
    }

    /// <summary>Resolve the own-ship centre-HUD status with the native rules
    /// (visibility is the centre console mask <c>ofInCockpit | ofInChase</c>,
    /// dialog.mdl:5397 — approximated by <paramref name="instrumentsVisible"/>
    /// which the whole flight-instrument group already shares).</summary>
    private static HudOwnStatus BuildStatus(HudFrame? f, bool instrumentsVisible)
    {
        if (f is not { } own)
            return new(null, null, false, false, false, false, false, false, false, false);

        bool visible = instrumentsVisible;
        // Native eye shows when the Detected binding is 0, i.e. NOT detected
        // (WinTrek/console.cpp:811; dialog.mdl:2527-2533). HUD-6b: the port's
        // own-ship SHIP_STATUS always carries Detected=true (Server.Broadcast.cs:1667,
        // no scanning system yet), so this gate is unreachable and the row stays
        // partial; it is retained to match the native binding if detection lands.
        bool eye = visible && !own.Detected;
        // Native IsCloaked = GetCloaking() < 1 (WinTrek/consoledata.cpp:563-575);
        // the port's CloakActive StateM bit is the engaged-cloak analogue.
        bool cloak = visible && own.Cloaked;
        // Native GlassHUD drained icon at energy <= .2 (dialog.mdl:651-656).
        bool drained = cloak && own.EnergyFraction <= 0.2f;
        return new(
            own.KillBonusPercent,
            own.SignaturePercent,
            eye,
            cloak,
            drained,
            visible && own.AutopilotEngaged,
            visible && own.VectorLockEngaged,
            visible && own.CarriedFlag,
            visible && own.CarriedArtifact,
            visible);
    }

    public static HudModel Create(HudFrame? telemetry, HudContext? context = null) => new(telemetry, context ?? new());
}
