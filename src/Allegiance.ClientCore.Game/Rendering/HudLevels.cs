using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P836 — the Modern-HUD density level. A data preset over the P828
/// declutter knobs (which labels draw, the nearest-N cap, fade distances, merge)
/// plus the new off-screen contact rule. <see cref="Auto"/> is not a preset —
/// it resolves to Full/Tactical/Combat from the ship count in the player's
/// sector (<see cref="HudAutoLevel"/>). Classic ignores this entirely and keeps
/// native <c>.</c> = <c>TK_CycleRadar</c> (<see cref="RadarLabelDeclutterOptions.Off"/>).</summary>
public enum HudLevel
{
    /// <summary>Native: every contact the radar mode allows is labelled, with
    /// native's own distance fade at 100/1000 m (the P828 off-equivalent apart
    /// from the fade).</summary>
    Full = 0,
    /// <summary>P828 today: priority (target + wingmen/command subjects +
    /// my attackers) plus the nearest-N ordinary labels with overlap merge
    /// tags, fade 100/1000.</summary>
    Tactical = 1,
    /// <summary>Target + attackers + wingmen + the nearest few ENEMIES labelled;
    /// friendlies icon-only; tightened fade (50/600); secondary panes compacted.</summary>
    Combat = 2,
    /// <summary>Minimal: no contact label except the target; reduced chrome.</summary>
    Cinematic = 3,
    /// <summary>Pick Full / Tactical / Combat from the ship count in the player's
    /// sector with hysteresis (never auto-selects <see cref="Cinematic"/>).</summary>
    Auto = 4,
}

/// <summary>How an OFF-SCREEN (edge-clamped) contact block draws. Native pins the
/// full block (icon + label) to the screen edge (<c>WinTrek/WinTrek.cpp:7899-7921</c>);
/// the per-level rule lets a denser level shrink or drop it. Priority contacts
/// (target/wingmen/attackers) always keep <see cref="NativeBlock"/> so they stay
/// readable — the rule governs the ordinary contacts.</summary>
public enum OffscreenRule
{
    /// <summary>Native: pin the full contact block (icon + label) to the edge.</summary>
    NativeBlock = 0,
    /// <summary>A small side-coloured pip on the edge, no label block.</summary>
    EdgeDot = 1,
    /// <summary>Nothing at all for the off-screen contact.</summary>
    None = 2,
}

/// <summary>P836 — the resolved data preset for one concrete (non-Auto) HUD level.
/// Pure values (no MonoGame types), so the table is unit-testable and the
/// renderers only consume the decisions via
/// <see cref="RadarLabelDeclutterOptions"/>.</summary>
public readonly record struct HudLevelPreset(
    /// <summary>Full — label every native-allowed contact (no nearest-N cap, no
    /// merge); still applies the distance fade.</summary>
    bool AllAllowed,
    /// <summary>Friendlies are eligible for an ordinary (nearest-N) label.
    /// Combat sets this false (friendlies icon-only).</summary>
    bool LabelFriendlies,
    /// <summary>Cinematic — only the selected target gets a label.</summary>
    bool OnlyTarget,
    /// <summary>Collapse overlapping ordinary labels into per-side count tags.</summary>
    bool Merge,
    /// <summary>The nearest-N ordinary label budget the level suggests (the
    /// persisted <c>RadarLabelMax</c> override wins for a capped level).</summary>
    int MaxExtraLabels,
    /// <summary>Camera distance at/below which a label is full-alpha.</summary>
    float FadeNear,
    /// <summary>Camera distance at which a label reaches its minimum alpha.</summary>
    float FadeFar,
    /// <summary>The off-screen contact rule for ordinary contacts.</summary>
    OffscreenRule Offscreen);

/// <summary>P836 — the one preset table for the Modern HUD levels. Lives in the
/// shared model (not a renderer) so Classic, Modern and the tests all read the
/// same numbers.</summary>
public static class HudLevels
{
    /// <summary>The tighten fade pair for Combat/Cinematic (the brief's proposed
    /// 50/600 — near fights collapse to a focused set of labels).</summary>
    public const float CombatFadeNear = 50f, CombatFadeFar = 600f;
    /// <summary>Tactical's nearest-N suggestion (= P828's shipped 12).</summary>
    public const int TacticalExtraLabels = RadarLabelDeclutter.DefaultExtraLabels;
    /// <summary>Combat keeps only a handful of enemy labels.</summary>
    public const int CombatExtraLabels = 6;

    /// <summary>The preset for a concrete level. <see cref="HudLevel.Auto"/>
    /// falls back to Tactical (the resolved concrete level should always be fed
    /// in, but this keeps the table total).</summary>
    public static HudLevelPreset Preset(HudLevel level) => level switch
    {
        HudLevel.Full => new(
            AllAllowed: true, LabelFriendlies: true, OnlyTarget: false, Merge: false,
            MaxExtraLabels: 0,
            RadarLabelDeclutter.NativeFadeNear, RadarLabelDeclutter.NativeFadeFar,
            OffscreenRule.NativeBlock),
        HudLevel.Tactical => new(
            AllAllowed: false, LabelFriendlies: true, OnlyTarget: false, Merge: true,
            MaxExtraLabels: TacticalExtraLabels,
            RadarLabelDeclutter.NativeFadeNear, RadarLabelDeclutter.NativeFadeFar,
            OffscreenRule.EdgeDot),
        HudLevel.Combat => new(
            AllAllowed: false, LabelFriendlies: false, OnlyTarget: false, Merge: true,
            MaxExtraLabels: CombatExtraLabels,
            CombatFadeNear, CombatFadeFar,
            OffscreenRule.EdgeDot),
        HudLevel.Cinematic => new(
            AllAllowed: false, LabelFriendlies: false, OnlyTarget: true, Merge: false,
            MaxExtraLabels: 0,
            CombatFadeNear, CombatFadeFar,
            OffscreenRule.None),
        _ => Preset(HudLevel.Tactical),
    };

    /// <summary>The HUD-level display label (also shown on the cycle-key toast).</summary>
    public static string Label(HudLevel level) => level switch
    {
        HudLevel.Full => "Full",
        HudLevel.Tactical => "Tactical",
        HudLevel.Combat => "Combat",
        HudLevel.Cinematic => "Cinematic",
        _ => "Auto",
    };

    /// <summary>The manual cycle order — the five settable levels, including
    /// Auto (cycling from a manual level eventually reaches Auto, then wraps
    /// back to Full).</summary>
    private static readonly HudLevel[] Order =
        { HudLevel.Full, HudLevel.Tactical, HudLevel.Combat, HudLevel.Cinematic, HudLevel.Auto };

    /// <summary>Advance one step through the five levels in <see cref="Order"/>
    /// (the cycle key and the options row).</summary>
    public static HudLevel Next(HudLevel level) => Step(level, 1);

    /// <summary>Step <paramref name="dir"/> places (+,−1) through the five
    /// levels; negative goes the other way.</summary>
    public static HudLevel Step(HudLevel level, int dir)
    {
        int idx = Array.IndexOf(Order, level);
        if (idx < 0) idx = 1;   // unknown → Tactical
        return Order[((idx + dir) % Order.Length + Order.Length) % Order.Length];
    }
}

/// <summary>P836 — the Auto-level resolver: choose Full / Tactical / Combat from
/// the number of ships in the player's sector with hysteresis so it never
/// flickers on a live battle. A pure, stateful, unit-testable controller —
/// <c>Game1</c> feeds it the in-sector ship count once a period and reads the
/// resolved level back into <see cref="HudSettings.ActiveLevel"/>.</summary>
public sealed class HudAutoLevel
{
    /// <summary>At/below this many in-sector ships Auto uses Full.</summary>
    public const int FullMaxShips = 40;
    /// <summary>At/below this many in-sector ships Auto uses Tactical.</summary>
    public const int TacticalMaxShips = 120;
    /// <summary>Above <see cref="TacticalMaxShips"/> Auto uses Combat.</summary>
    public const int CombatMinShips = TacticalMaxShips + 1;
    /// <summary>How long the count must stay in the new band before Auto flips
    /// (a "few seconds", the brief's anti-flicker hold).</summary>
    public const double HoldSeconds = 3.0;

    /// <summary>The resolved concrete level (never Auto). Starts at the level
    /// implied by the first sample so activation is immediate without flicker.</summary>
    public HudLevel Level { get; private set; } = HudLevel.Tactical;

    private HudLevel? _pending;
    private double _pendingSince;

    /// <summary>The band for a ship count (pure, no state). Auto never selects
    /// Cinematic.</summary>
    public static HudLevel Band(int ships)
        => ships <= FullMaxShips ? HudLevel.Full
         : ships <= TacticalMaxShips ? HudLevel.Tactical
         : HudLevel.Combat;

    /// <summary>Feed one periodic sample of the in-sector ship count. Returns
    /// true exactly when the resolved level changed this tick (the caller shows
    /// a brief "AUTO → LEVEL" notice on that edge).</summary>
    public bool Advance(double nowSeconds, int ships)
    {
        var desired = Band(ships);
        if (!_started) { _started = true; _pending = null; if (Level != desired) Level = desired; return true; }
        if (desired == Level) { _pending = null; return false; }
        if (_pending == desired)
        {
            if (nowSeconds - _pendingSince >= HoldSeconds) { Level = desired; _pending = null; return true; }
            return false;
        }
        _pending = desired;
        _pendingSince = nowSeconds;
        return false;
    }

    private bool _started;
}
