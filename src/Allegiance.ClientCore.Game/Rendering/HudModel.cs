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

public readonly record struct HudLock(HudPoint Center, MissileLockFeedback.LockState State, double NowSeconds);
public readonly record struct HudLead(HudPoint Center, HudPoint TargetCenter, bool InRange, float Green, double NowSeconds);
public readonly record struct HudTarget(HudPoint Center, bool Engaged, string? Name,
    float? HullFraction, float? ShieldFraction, bool? IsEnemy, float? OreFraction, float? Distance = null, float? ClosingSpeed = null, HudColor? SideColor = null);

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
public enum HudContactKind { Ship, Station, Warp, Asteroid }
public readonly record struct HudContact(HudContactKind Kind, HudPoint Position,
    bool OnScreen, float Opacity, HudColor? SideColor, bool Selected, bool BuildSpot);
public sealed record HudChat(IReadOnlyList<string> Lines, int Offset, string Input,
    bool Composing, double NowSeconds, string Recipient);

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
    public HudChat? Chat { get; init; }
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
            CommandLines = Array.AsReadOnly(context.CommandLines.ToArray())
        };
        var gauges = new List<HudGauge>();
        if (Telemetry is { } f)
        {
            Add(GaugeKind.Thrust, (f.Throttle + 1f) / 2f, -126.5f, 93.5f, "THR");
            Add(GaugeKind.Fuel, f.FuelFraction, 126.5f, 93.5f, f.IsAdriftLifepod ? "OXY" : "FUEL");
            Add(GaugeKind.Energy, f.IsAdriftLifepod ? 0f : f.EnergyFraction, 126.5f, -112.5f, "ENGY");
            Add(GaugeKind.Ammo, f.IsAdriftLifepod ? 0f : f.AmmoFraction, -126.5f, -112.5f, "AMMO");
            Add(GaugeKind.Hull, f.HullFraction, -65.5f, 35.5f, "HULL");
            Add(GaugeKind.Shield, f.ShieldFraction, 65.5f, 35.5f, "SHLD");

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
        var warnings = new List<string>();
        if (FlightVisible && Telemetry is { } own)
        {
            if ((own.WarningMask & ShipWarningMask.OutOfBounds) != 0) warnings.Add("OUT OF BOUNDS");
            if ((own.WarningMask & ShipWarningMask.CrowdedSector) != 0) warnings.Add("SECTOR CROWDED");
        }
        Warnings = warnings.AsReadOnly();
    }

    public static HudModel Create(HudFrame? telemetry, HudContext? context = null) => new(telemetry, context ?? new());
}
