using System;
using System.Collections.Generic;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-10 — native geometry for the remaining HUD rows. These are pure values
/// (no MonoGame types) so every rule is headless-testable. Coordinates are
/// expressed the way the native <c>Artwork/dialog.mdl</c> dialogs list does:
/// a lower-left origin with +Y up (dialog.mdl:5259-5268). The renderers convert
/// them to their top-left screen space; the conversion is spelled out per pane.
/// </summary>
public static class ChatPaneLayout
{
    /// <summary><c>ChatListPane(355, ...)</c> (dialog.mdl:180).</summary>
    public const int Width = 355;

    /// <summary><c>UpdateSize</c>: <c>m_pnumLines * itemHeight + 8</c>
    /// (chatpane.cpp:374-380).</summary>
    public const int HeightBase = 8;

    /// <summary>Default <c>ChatLines</c> preference (WinTrek.cpp:3317,
    /// <c>LoadPreference("ChatLines", 7)</c>).</summary>
    public const int DefaultLines = 7;

    /// <summary>Line height of native <c>largeFont</c> at the default
    /// <c>ChatFontSize</c> 2 (WinTrek.cpp:2912/3319; chatpane.cpp:376 uses
    /// <c>GetFont(fontSize, true)-&gt;GetHeight()</c>). The port substitutes its
    /// HUD spritefont for that font, so this is the native item height the pane
    /// rule is pinned against.</summary>
    public const int NativeFontHeight = 14;

    /// <summary><c>CommPane</c> is placed at <c>SideTop(-160,-126)</c>
    /// (dialog.mdl:5394): 160 px left of centre and its lower-left edge 126 px
    /// below the top (y-up, so the pane's screen top is
    /// <see cref="BottomFromTop"/> - height).</summary>
    public const int LeftFromCentre = 160;
    public const int BottomFromTop = 126;

    /// <summary><c>ChatListPane(..., false)</c> — the scrollbar is hidden
    /// (dialog.mdl:180; chatpane.cpp:122-130).</summary>
    public const bool ScrollbarHidden = true;

    /// <summary>Native pane height for <paramref name="lines"/> chat lines
    /// (dialog.mdl:180).</summary>
    public static int Height(int lines) => HeightBase + NativeFontHeight * Math.Max(0, lines);

    /// <summary>Top edge in top-left screen space (y down).</summary>
    public static int Top(int lines) => BottomFromTop - Height(lines);

    /// <summary>Left edge in screen space.</summary>
    public static int Left(int viewportWidth) => viewportWidth / 2 - LeftFromCentre;

    /// <summary>Visible history lines for the pane's native height.</summary>
    public static int VisibleLines => DefaultLines;

    /// <summary>HUD-11 — the native pane height in lines for a configured
    /// <c>ChatLines</c> preference: the configured value in flight, capped at 6
    /// in the loadout view (WinTrek.cpp:6694-6700), range 1-10
    /// (SetChatLines, WinTrek.cpp:5262-5280).</summary>
    public static int ConfiguredLines(int configured, bool loadoutView = false)
    {
        int lines = Math.Clamp(configured, 1, 10);
        return loadoutView ? Math.Min(lines, 6) : lines;
    }

    /// <summary>HUD-11 — the port's chat glyph scale that keeps the substitute
    /// font inside the native 14 px item slot (native largeFont at ChatFontSize
    /// 2; chatpane.cpp:374-380). A line drawn at this scale never overlaps the
    /// row below, however tall the port font is.</summary>
    public static float TextScale(float fontLineSpacing)
        => fontLineSpacing <= NativeFontHeight ? 1f : NativeFontHeight / fontLineSpacing;
}

/// <summary>Native compose states (<c>ConsoleDataImpl::ComposeState</c>,
/// console.cpp:524-529).</summary>
public enum NativeComposeState
{
    NotComposing = 0,
    MouseCommand = 1,
    Chat = 2,
    Command = 3,
    Shell = 4,
    Quick = 5,
}

/// <summary>
/// One <c>ComposingPane</c> presentation variant (dialog.mdl:89-170). The
/// panel art is <see cref="ArtStem"/>; inner text boxes are the exact native
/// offsets inside that panel. <see cref="TextWidth"/> 0 means the variant has
/// no ChatText box.
/// </summary>
public readonly record struct ComposeVariant(
    NativeComposeState State,
    string ArtStem,
    int PanelWidth,
    int PanelHeight,
    int RecipientLeft,
    int RecipientTop,
    int RecipientWidth,
    int TextLeft,
    int TextTop,
    int TextWidth,
    int VerbLeft,
    int VerbTop)
{
    /// <summary>Every native recipient/inner text box is 17 px high
    /// (StringPane Point(...,17), dialog.mdl:91-167).</summary>
    public const int InnerTextHeight = 17;
    public bool HasRecipient => RecipientWidth > 0;
    public bool HasText => TextWidth > 0;
    public int RecipientHeight => InnerTextHeight;
    public bool HasVerbIcon => VerbLeft >= 0;
}

/// <summary>
/// Native <c>ChatCompositionImage</c> geometry (dialog.mdl:89-170, 5355).
/// The dialog entry is <c>SideBottom</c>, off <c>(-170,-78)</c>, on
/// <c>(-150,148)</c>, slide <c>.5</c> (dialog.mdl:5355) with a lower-left
/// origin, so the on-pane's bottom edge is 148 px above the screen bottom.
/// </summary>
public static class ComposePaneLayout
{
    public const int OffLeftFromCentre = -170;
    public const int OnLeftFromCentre = -150;
    public const int OffBottomFromScreenBottom = -78;
    public const int OnBottomFromScreenBottom = 148;
    public const float SlideSeconds = 0.5f;

    private static readonly ComposeVariant Mouse = new(
        NativeComposeState.MouseCommand, "chatmousebmp", 73, 23, 3, 3, 66, 0, 0, 0, -1, -1);
    private static readonly ComposeVariant Chat = new(
        NativeComposeState.Chat, "chattextbmp", 266, 22, 3, 3, 100, 112, 3, 154, -1, -1);
    private static readonly ComposeVariant Command = new(
        NativeComposeState.Command, "chatcommandbmp", 236, 22, 3, 3, 66, 104, 3, 128, 78, 2);
    private static readonly ComposeVariant Shell = new(
        NativeComposeState.Shell, "chatshellbmp", 236, 22, 0, 0, 0, 3, 3, 229, -1, -1);
    private static readonly ComposeVariant Quick = new(
        NativeComposeState.Quick, "chatmousebmp", 73, 23, 3, 3, 66, 0, 0, 0, -1, -1);

    /// <summary>The native variant for a compose state (dialog.mdl:150-170).</summary>
    public static ComposeVariant Variant(NativeComposeState state) => state switch
    {
        NativeComposeState.MouseCommand => Mouse,
        NativeComposeState.Chat => Chat,
        NativeComposeState.Command => Command,
        NativeComposeState.Shell => Shell,
        NativeComposeState.Quick => Quick,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a composing state"),
    };

    /// <summary>Left edge at slide progress <paramref name="progress"/> (0 =
    /// fully off screen, 1 = fully on), in screen space.</summary>
    public static float Left(int viewportWidth, float progress) =>
        viewportWidth / 2f + OffLeftFromCentre + (OnLeftFromCentre - OffLeftFromCentre) * Math.Clamp(progress, 0f, 1f);

    /// <summary>Top edge at slide progress, in top-left screen space.</summary>
    public static float Top(int viewportHeight, int panelHeight, float progress)
    {
        float bottom = OffBottomFromScreenBottom
            + (OnBottomFromScreenBottom - OffBottomFromScreenBottom) * Math.Clamp(progress, 0f, 1f);
        return viewportHeight - bottom - panelHeight;
    }
}

/// <summary>
/// Native <c>CommandPane</c> (dialog.mdl:73-87) and its display entry
/// <c>SideBottom(-135,-190)→(-135,40)</c>, .5 s (dialog.mdl:5416). Children:
/// <c>RecipientsBB</c> at (4,8) with columns [53,106,159,212], <c>WingCombo</c>
/// at (216,8), and <c>VerbsBB</c> at (2,38) with columns
/// [32,62,92,122,152,182,212,242,268] (dialog.mdl:38-42,73-87).
/// </summary>
public static class CommandBarLayout
{
    public const int PanelWidth = 274;
    public const int PanelHeight = 92;
    public const int OffLeftFromCentre = -135;
    public const int OnLeftFromCentre = -135;
    public const int OffBottomFromScreenBottom = -190;
    public const int OnBottomFromScreenBottom = 40;
    public const float SlideSeconds = 0.5f;

    public const int RecipientsLeft = 4;
    public const int RecipientsTop = 8;
    public const int RecipientsWidth = 212;
    public const int RecipientsHeight = 84;
    public static readonly int[] RecipientColumns = [53, 106, 159, 212];

    public const int WingLeft = 216;
    public const int WingTop = 8;
    public const int WingWidth = 54;
    public const int WingHeight = 42;

    public const int VerbsLeft = 2;
    public const int VerbsTop = 38;
    public const int VerbsWidth = 268;
    public const int VerbsHeight = 54;
    public static readonly int[] VerbColumns = [32, 62, 92, 122, 152, 182, 212, 242, 268];

    /// <summary>HUD-11 — native <c>WingCombo</c> is an
    /// <c>ImageComboPane(btnchangewing3.png, smallFont)</c> whose face is a
    /// <c>ImageComboFacePane</c>; its <c>SetString</c> is a no-op, so the native
    /// button shows ONLY the baked "WING ▾" art and never the selected wing name
    /// (effect/combopane.cpp:88-99; dialog.mdl:47-49). The port keeps a small
    /// caption BELOW the panel (a documented extension) so the selected wing is
    /// still legible without overprinting the native art.
    /// </summary>
    public const int WingCaptionBelowPanel = 3;

    /// <summary>Native wing names <c>c_pszWingName</c> (Igc/common.cpp:23-33).</summary>
    public static readonly string[] WingNames =
        ["command", "attack", "defend", "escort", "search", "alpha", "bravo", "charlie", "delta"];

    public static float Left(int viewportWidth, float progress) =>
        viewportWidth / 2f + OffLeftFromCentre + (OnLeftFromCentre - OffLeftFromCentre) * Math.Clamp(progress, 0f, 1f);

    public static float Top(int viewportHeight, float progress)
    {
        float bottom = OffBottomFromScreenBottom
            + (OnBottomFromScreenBottom - OffBottomFromScreenBottom) * Math.Clamp(progress, 0f, 1f);
        return viewportHeight - bottom - PanelHeight;
    }

    /// <summary>HUD-11 — screen Y of the port's selected-wing caption, below the
    /// native panel art (never inside the baked "WING" glyphs).</summary>
    public static float WingCaptionTop(float panelTop) => panelTop + PanelHeight + WingCaptionBelowPanel;

    /// <summary>Recipient column (0..3) at a panel-local point, or -1.</summary>
    public static int RecipientColumn(int x, int y)
    {
        if (x < RecipientsLeft || x >= RecipientsLeft + RecipientsWidth
            || y < RecipientsTop || y >= RecipientsTop + RecipientsHeight) return -1;
        int local = x - RecipientsLeft;
        for (int c = 0; c < RecipientColumns.Length; c++)
            if (local < RecipientColumns[c]) return c;
        return -1;
    }

    /// <summary>Verb column (0..8 = attack..build) at a panel-local point, or -1.</summary>
    public static int VerbColumn(int x, int y)
    {
        if (x < VerbsLeft || x >= VerbsLeft + VerbsWidth
            || y < VerbsTop || y >= VerbsTop + VerbsHeight) return -1;
        int local = x - VerbsLeft;
        for (int c = 0; c < VerbColumns.Length; c++)
            if (local < VerbColumns[c]) return c;
        return -1;
    }

    /// <summary>Wing combo box hit test (panel-local).</summary>
    public static bool InWingCombo(int x, int y) =>
        x >= WingLeft && x < WingLeft + WingWidth && y >= WingTop && y < WingTop + WingHeight;

    /// <summary>Native CommandID from a verb column (c_cidAttack + column,
    /// console.cpp:1276).</summary>
    public static short VerbCommandId(int column) =>
        column is >= 0 and <= 8 ? (short)(column + 1) : (short)0;

    /// <summary>P784 — the chat target a recipient column maps to, mirroring
    /// native OnRecipientButton's switch (console.cpp:1140-1175) with the
    /// native column constants (console.cpp:13-18): 0 = Me (CHAT_INDIVIDUAL),
    /// 1 = Sector (CHAT_FRIENDLY_SECTOR), 2 = Team (CHAT_TEAM), 3 = All
    /// (CHAT_EVERYONE), ≥4 = Wing (CHAT_WING). Out-of-range columns fall back
    /// to the no-selection sentinel rather than a wrong channel.</summary>
    public static ChatTarget RecipientChatTarget(int column) => column switch
    {
        0 => ChatTarget.Individual,
        1 => ChatTarget.FriendlySector,
        2 => ChatTarget.Team,
        3 => ChatTarget.Everyone,
        >= 4 => column - 4 < WingNames.Length ? ChatTarget.Wing : ChatTarget.NoSelection,
        _ => ChatTarget.NoSelection,
    };
}

/// <summary>
/// Native <c>moneyPane</c> (dialog.mdl:1885-1908), embedded in
/// <c>MergedInvestmentsPaneImage</c> at <c>TranslateImage(MoneyPaneImage,
/// Point(33,0))</c> (dialog.mdl:5199-5203) and shown only under
/// <c>ofInvestment</c> at <c>SideRight Point(-490,-265)</c> (dialog.mdl:5368).
/// </summary>
public static class MoneyCreditsLayout
{
    public const string ArtStem = "creditboxbmp";
    public const int ArtWidth = 154;
    public const int ArtHeight = 43;

    /// <summary>Value box: <c>StringPane(ConcatinatedString(NumberString(Money)," cr"),
    /// White, Point(90,13), JustifyCenter, smallFont, true)</c> at
    /// <c>Point(58,0)</c> (dialog.mdl:1890-1897).</summary>
    public const int ValueLeft = 58;
    public const int ValueTop = 0;
    public const int ValueWidth = 90;
    public const int ValueHeight = 13;

    /// <summary><c>CURRENT CREDITS</c> caption at <c>Point(31,13)</c>
    /// (dialog.mdl:1898-1905).</summary>
    public const int LabelLeft = 31;
    public const int LabelTop = 13;
    public const int LabelWidth = 160;
    public const int LabelHeight = 13;
    public const string Label = "CURRENT CREDITS";

    /// <summary>Native group/SideRight anchor: the merged investments pane's
    /// lower-left sits at <c>SideRight(-490,-265)</c> and the money pane is 33 px
    /// right of the group origin (dialog.mdl:5368,5200). In top-left screen
    /// space the group's bottom is <c>height/2 + 265</c>, so the money art's top
    /// is that minus <see cref="ArtHeight"/>.</summary>
    public static int Left(int viewportWidth) => viewportWidth - 490 + 33;
    public static int Top(int viewportHeight) => viewportHeight / 2 + 265 - ArtHeight;

    /// <summary>Native value string: <c>NumberString(Money) + " cr"</c>
    /// (dialog.mdl:1892).</summary>
    public static string Value(int money) => $"{money} cr";
}

/// <summary>
/// Native mission clock (WinTrek.cpp:8128-8143). The game-state container is
/// fed the countdown-game remaining time or, otherwise, the elapsed mission
/// time. The standalone <c>CountdownOn</c>/<c>timebmp</c> badge is never fed
/// (dialog.mdl:5091-5106) and is deliberately not invented here.
/// </summary>
public static class MissionClock
{
    /// <summary><c>MissionParams::IsCountdownGame()</c> is
    /// <c>dtGameLength != 0.0f</c> (igc.h:1526-1529).</summary>
    public static bool IsCountdownGame(float gameLengthSeconds) => gameLengthSeconds != 0f;

    /// <summary>Native value: remaining seconds in a countdown game
    /// (<c>int(GetCountDownTime() - dt)</c>) or elapsed seconds otherwise
    /// (<c>int(dt)</c>), where <c>dt = time - timeStart</c> (WinTrek.cpp:8131-8141).</summary>
    public static int DisplaySeconds(float gameLengthSeconds, double elapsedSeconds)
    {
        double dt = Math.Max(0d, elapsedSeconds);
        return IsCountdownGame(gameLengthSeconds)
            ? Math.Max(0, (int)(gameLengthSeconds - dt))
            : (int)dt;
    }

    /// <summary>The game-state container's label for the same rule.</summary>
    public static string Label(float gameLengthSeconds) =>
        IsCountdownGame(gameLengthSeconds) ? "TIME REMAINING" : "TIME ELAPSED";

    /// <summary><c>mm:ss</c> formatting (NumberString/clock convention).</summary>
    public static string Format(int seconds)
    {
        seconds = Math.Max(0, seconds);
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }
}

/// <summary>
/// Native <c>DefaultEjectPodDisplayOn</c> / <c>GHEjectPodDisplayOn</c>
/// (dialog.mdl:1922-1983) and <c>EjectPodImage</c> placement
/// <c>SideCenter Point(-86,-87)</c> (dialog.mdl:5436). The classic group is a
/// 172x44 <c>conejectpoddisplaybmp</c> with the horizontal
/// <c>softgaugebmp</c> endurance bar at native offset (66,10)
/// (dialog.mdl:1927-1935); the port draws the classic variant.
/// </summary>
public static class LifepodPanelLayout
{
    public const string PanelStem = "conejectpoddisplaybmp";
    public const int PanelWidth = 172;
    public const int PanelHeight = 44;
    public const string GaugeStem = "softgaugebmp";
    public const int GaugeWidth = 52;
    public const int GaugeHeight = 4;
    public const int GaugeLeft = 66;
    public const int GaugeBottom = 10;
    public const int AnchorLeftFromCentre = -86;
    public const int AnchorBottomFromCentre = -87;
    public const float SlideSeconds = 0.5f;

    /// <summary>Panel rect in top-left screen space: the lower-left origin sits
    /// at <c>(w/2-86, h/2-87)</c> in y-up, i.e. the screen bottom is
    /// <c>h/2+87</c>.</summary>
    public static (int X, int Y) PanelTopLeft(int viewportWidth, int viewportHeight) =>
        (viewportWidth / 2 + AnchorLeftFromCentre,
         viewportHeight / 2 - AnchorBottomFromCentre - PanelHeight);

    /// <summary>Endurance bar rect (native <c>GaugeImage(JustifyLeft,
    /// softgaugebmp, GetEndurance)</c> at (66,10), dialog.mdl:1927-1935).</summary>
    public static (int X, int Y) GaugeTopLeft(int viewportWidth, int viewportHeight) =>
        (viewportWidth / 2 + AnchorLeftFromCentre + GaugeLeft,
         viewportHeight / 2 - AnchorBottomFromCentre - GaugeBottom - GaugeHeight);
}

/// <summary>
/// Native <c>DeathBannerPane</c> (dialog.mdl:1998-2003): the unscaled
/// 136x25 <c>deathbannerbmp</c> at <c>SideCenter Point(-105,15)</c>
/// (dialog.mdl:5439). The y-up on-point puts the banner's bottom 15 px above
/// the screen centre.
/// </summary>
public static class DeathBannerLayout
{
    public const string ArtStem = "deathbannerbmp";
    public const int Width = 136;
    public const int Height = 25;

    public static (int X, int Y) TopLeft(int viewportWidth, int viewportHeight) =>
        (viewportWidth / 2 - 105, viewportHeight / 2 - 15 - Height);
}

/// <summary>Native overlay-mask bits (dialog.mdl:5213-5243).</summary>
[Flags]
public enum ConsoleOverlayMask : uint
{
    None = 0,
    InHangar = 0x000001,
    InFlightCommand = 0x000002,
    InStationCommand = 0x000004,
    InFlight = 0x000008,
    InCockpit = 0x000010,
    InChase = 0x000020,
    InLoadout = 0x000040,
    TeleportPane = 0x000100,
    Investment = 0x000200,
    GameState = 0x000400,
    Team = 0x000800,
    ExpandedTeam = 0x001000,
    Inventory = 0x002000,
    CommandPane = 0x004000,
    SectorPane = 0x008000,
    GloatCam = 0x010000,
}

/// <summary>Port panes that participate in the native slide animation
/// (dialog.mdl:5355,5362,5368,5371,5379,5387 — every one is a .5 s slide).</summary>
public enum SlidingPane
{
    ChatComposition,
    CommandPane,
    Inventory,
    Investment,
    TeleportPane,
    TeamPane,
    GameState,
    SectorPane,
    /// <summary>P805 (row 471) — the native bottom target pane
    /// (TargetDisplayImage, dialog.mdl:5404).</summary>
    TargetPane,
}

/// <summary>
/// HUD-10 — the native <c>AnimatedTranslateTransform2</c> slide tracker
/// (WinTrek/trekctrls.cpp:996-1060): a pane's offset animates between its
/// off-screen and on-screen points over the dialog's transition time whenever
/// its overlay-mask bit changes. Every ported sliding pane uses the native
/// 0.5 s duration (dialog.mdl:5355-5387).
/// </summary>
public sealed class PaneSlideTracker
{
    public const float NativeSlideSeconds = 0.5f;

    private readonly record struct Slide(double Start, float From, float To);

    private readonly Dictionary<SlidingPane, Slide> _slides = new();

    /// <summary>Target progress for a pane: 1 while its mask bit is set, else 0.</summary>
    public static float Target(ConsoleOverlayMask mask, SlidingPane pane) => (mask & Bit(pane)) != 0 ? 1f : 0f;

    public static ConsoleOverlayMask Bit(SlidingPane pane) => pane switch
    {
        SlidingPane.ChatComposition => ConsoleOverlayMask.InFlight | ConsoleOverlayMask.InCockpit
            | ConsoleOverlayMask.InChase | ConsoleOverlayMask.InFlightCommand | ConsoleOverlayMask.InStationCommand
            | ConsoleOverlayMask.InHangar | ConsoleOverlayMask.InLoadout | ConsoleOverlayMask.GloatCam,
        SlidingPane.CommandPane => ConsoleOverlayMask.CommandPane,
        SlidingPane.Inventory => ConsoleOverlayMask.Inventory,
        SlidingPane.Investment => ConsoleOverlayMask.Investment,
        SlidingPane.TeleportPane => ConsoleOverlayMask.TeleportPane,
        SlidingPane.TeamPane => ConsoleOverlayMask.Team | ConsoleOverlayMask.ExpandedTeam,
        SlidingPane.GameState => ConsoleOverlayMask.GameState,
        SlidingPane.SectorPane => ConsoleOverlayMask.SectorPane,
        // P805 (row 471) — the target pane's own slide (dialog.mdl:5404,
        // TargetDisplayImage SideBottom(-320,-183)→(-320,25), .5 s, mask
        // ofInCockpit | ofInChase | ofGloatCam).
        SlidingPane.TargetPane => ConsoleOverlayMask.InCockpit | ConsoleOverlayMask.InChase,
        _ => ConsoleOverlayMask.None,
    };

    /// <summary>Advance every pane toward its target at <paramref name="now"/>.
    /// Returns true when any pane's target changed (the overlay-mask change
    /// that natively plays <c>paneSlideSound</c>, console.cpp:3272-3274).</summary>
    public bool Update(double now, ConsoleOverlayMask mask)
    {
        bool changed = false;
        foreach (SlidingPane pane in Enum.GetValues<SlidingPane>())
        {
            // The compose panel is a SwitchPane inside an always-visible display
            // image (dialog.mdl:150-170,5355); its slide is driven by the compose
            // state, not by the overlay mask, so UpdateConsoleOverlay feeds it
            // through SetTarget.
            if (pane == SlidingPane.ChatComposition) continue;
            float target = Target(mask, pane);
            if (SetTarget(pane, now, target)) changed = true;
        }
        return changed;
    }

    /// <summary>Set one pane's target directly (the compose panel's state-driven
    /// slide). Returns true when the target changed.</summary>
    public bool SetTarget(SlidingPane pane, double now, float target)
    {
        target = Math.Clamp(target, 0f, 1f);
        float from = Progress(pane, now);
        if (_slides.TryGetValue(pane, out var s))
        {
            if (Math.Abs(s.To - target) < 0.0001f) return false;
        }
        else if (Math.Abs(from - target) < 0.0001f) return false;
        _slides[pane] = new Slide(now, from, target);
        return true;
    }

    /// <summary>Pane progress at <paramref name="now"/> (0 = off, 1 = on).
    /// A pane that has never been updated reads 0 (native starts hidden).</summary>
    public float Progress(SlidingPane pane, double now)
    {
        if (!_slides.TryGetValue(pane, out var s)) return 0f;
        float t = (float)Math.Clamp((now - s.Start) / Math.Max(NativeSlideSeconds, 1e-6f), 0d, 1d);
        return s.From + (s.To - s.From) * t;
    }

    /// <summary>Whether the pane is fully off (nothing to draw).</summary>
    public bool IsOff(SlidingPane pane, double now) => Progress(pane, now) <= 0f;

    /// <summary>Reset every pane to a settled state (used on scene changes where
    /// native calls Show() without an animation).</summary>
    public void Reset()
    {
        _slides.Clear();
    }
}
