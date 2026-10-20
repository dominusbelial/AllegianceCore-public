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
        bool dead = false, bool commandView = false, IReadOnlyList<HudNotice>? notices = null,
        IReadOnlyList<RadarBracketRenderer.RadarContact>? contacts = null, HudContact[]? contactsBuffer = null, float radarPulse = 0f,
        RadarMode radarMode = RadarMode.Default, HudChat? chat = null,
        string? cargoLine = null, string? flightPartsLine = null,
        IReadOnlyList<string>? commandLines = null, bool inventoryVisible = false,
        IReadOnlyList<LastBaseHudView>? lastBaseObjectives = null, bool hitConfirmation = false, string? podReturnInstruction = null,
        HudTargetPane? targetPane = null, bool roundRadar = false)
        => HudModel.Create(frame, new HudContext
        {
            PodReturnInstruction = podReturnInstruction, HitConfirmation = hitConfirmation, Camera = mode, CameraLabel = cameraLabel, Hidden = hidden,
            Docked = docked, Dead = dead, CommandView = commandView, Turret = turret, RoundRadar = roundRadar,
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
            Contacts = BuildContacts(contacts, contactsBuffer)
        });

    private static HudPoint Point(Vector2 v) => new(v.X, v.Y);

    /// <summary>HUD-4b — fill a caller-reused <see cref="HudContact"/> buffer
    /// from the radar contact list instead of a per-frame LINQ
    /// <c>Select…ToArray</c>. When no buffer is supplied the old one-shot path
    /// still applies (menu/spectate callers pass no contacts).</summary>
    private static IReadOnlyList<HudContact> BuildContacts(
        IReadOnlyList<RadarBracketRenderer.RadarContact>? contacts, HudContact[]? buffer)
    {
        if (contacts is not { Count: > 0 }) return Array.Empty<HudContact>();
        if (buffer is null || buffer.Length < contacts.Count)
            buffer = new HudContact[contacts.Count];
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            buffer[i] = new HudContact((HudContactKind)c.Type,
                new(c.ScreenPoint.X, c.ScreenPoint.Y), c.OnScreen, c.Opacity,
                c.SideColour is { } color ? new HudColor(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f) : null,
                c.IsSelectedTarget, c.IsInvestBuildSpot, c.IsSelf, c.IsTech);
        }
        return new ArraySegment<HudContact>(buffer, 0, contacts.Count);
    }
}
