using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Boundary adapter for the existing projection helpers. MonoGame math
/// ends here; the model itself contains only values and semantic state.</summary>
public static class HudModelAdapter
{
    public static HudModel Create(HudFrame? frame, CameraMode mode,
        LockBracketView? lockBracket = null, LeadIndicatorView? lead = null,
        string? cameraLabel = null, TargetSelectionView? target = null,
        Vector2? bearing = null, LeaderBotCommand? leaderCommand = null,
        string? ripcord = null, string? commandee = null, bool turret = false,
        string? diagnostics = null, bool hidden = false, bool docked = false,
        bool dead = false, bool commandView = false, bool ghost = false, IReadOnlyList<HudNotice>? notices = null,
        RadarMode radarMode = RadarMode.Default, HudChat? chat = null,
        string? cargoLine = null, string? flightPartsLine = null,
        IReadOnlyList<string>? commandLines = null, bool inventoryVisible = false,
        IReadOnlyList<LastBaseHudView>? lastBaseObjectives = null, bool hitConfirmation = false, string? podReturnInstruction = null,
        IReadOnlyList<HudContact>? contacts = null, float radarPulse = 0f,
        HudConsoleText? console = null,
        Vector2? velocityMarker = null, Vector2? chaseAim = null, HudOrderArrows? orderArrows = null,
        HudInventoryPane? inventoryPane = null,
        HudTargetPane? targetPane = null, bool roundRadar = false, float inventorySlide = 0f,
        HudSnapshotBuffers? buffers = null,
        bool showJoystickIndicator = true)
        => HudModel.Create(frame, new HudContext
        {
            PodReturnInstruction = podReturnInstruction, HitConfirmation = hitConfirmation, Camera = mode, CameraLabel = cameraLabel, Hidden = hidden,
            Docked = docked, Dead = dead, CommandView = commandView, Ghost = ghost, Turret = turret, RoundRadar = roundRadar,
            ShowJoystickIndicator = showJoystickIndicator,
            VelocityMarker = velocityMarker is { } velocity ? Point(velocity) : null,
            ChaseAim = chaseAim is { } aimPoint ? Point(aimPoint) : null,
            OrderArrows = orderArrows,
            Lock = lockBracket is { } lb ? new(Point(lb.Center), lb.State, lb.NowSeconds, lb.Fraction, lb.ScreenRadius) : null,
            Lead = lead is { } li ? new(Point(li.LeadCenter), Point(li.TargetCenter), li.InRange, li.Green, li.NowSeconds) : null,
            Target = target is { } ts ? new(Point(ts.Center), ts.Engaged, ts.CharacterName,
                ts.HullFraction, ts.ShieldFraction, ts.IsEnemy, ts.OreFraction, ts.Distance, ts.ClosingSpeed, ts.SideColor, ts.ScreenRadius) : null,
            TargetPane = targetPane,
            TargetBearing = bearing is { } b ? Point(b) : null,
            LeaderCommand = leaderCommand, Ripcord = ripcord, Commandee = commandee,
            Diagnostics = diagnostics, Notices = notices ?? Array.Empty<HudNotice>(),
            LastBaseObjectives = lastBaseObjectives ?? Array.Empty<LastBaseHudView>(),
            RadarPulse = radarPulse, RadarMode = radarMode, Chat = chat,
            CargoLine = cargoLine, FlightPartsLine = flightPartsLine,
            CommandLines = commandLines ?? Array.Empty<string>(), InventoryVisible = inventoryVisible,
            Console = console,
            InventoryPane = inventoryPane,
            Contacts = contacts ?? Array.Empty<HudContact>(),
            InventorySlide = inventorySlide
        }, buffers);

    private static HudPoint Point(Vector2 v) => new(v.X, v.Y);
}
