using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using Allegiance.Core.Wire;
namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1042 — the semantic inks a screen skin paints with. Classic/Modern fill these from
/// the exact colours their drawing used before the boundary (so a decoded-pixel compare
/// is zero); Jet maps them to its palette and ignores the values.
/// </summary>
public readonly record struct ScreenInks(Color Title, Color Body, Color Dim, Color Warning);

/// <summary>
/// P1042 — the intro main menu as a read-only screen model. <paramref name="Layout"/> is
/// the resolved geometry (<see cref="IntroLayout.Compute"/>), the very rectangles a hit
/// test resolves: the renderer draws every row at <c>Layout.Rows[i]</c>, never a rectangle
/// of its own. <paramref name="Labels"/> are the native command labels in menu order; the
/// pilot field, identity line and hint keep their existing anchors.
/// </summary>
public readonly record struct IntroMainFrame(
    Viewport Viewport,
    string Title,
    string Subtitle,
    IReadOnlyList<string> Labels,
    IntroLayoutResult Layout,
    int Selected,
    string PilotLabel,
    string PilotText,
    bool PilotFocused,
    IntroRect PilotField,
    /// <summary>The caret's x offset inside the field, measured once by the frame
    /// builder (<c>"Pilot: " + typed text</c>). The classic painter and the Jet chrome
    /// share it, so neither formats a string during Draw.</summary>
    float PilotCaretOffset,
    string IdentityLine,
    float IdentityScale,
    string Hint,
    float HintScale,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042 — the docked hangar screen as a read-only model: the hover verb, the launch
/// pane's animation state, the native BoardState (2 = turret gunner) and the
/// developments gate that hides Invest (<c>WinTrek/hangarscreen.cpp:129-130</c>).
/// Every verb rectangle is <see cref="HangarScreen.Rect"/> — the same call the pointer
/// hit test makes (<see cref="HangarScreen.Hit"/>).
/// </summary>
public readonly record struct HangarScreenFrame(
    Viewport Viewport,
    HangarAction Hover,
    HangarLaunchAnimation Launch,
    int? BoardState,
    bool Developments,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042b — the semantic ink a screen text run uses. Classic/Modern map every role to the
/// exact colour their drawing used before the boundary (so a decoded-pixel compare is
/// zero); Jet maps them onto its five-role palette.
/// </summary>
public enum ScreenInkRole
{
    /// <summary>The screen title accent.</summary>
    Title,
    /// <summary>Normal body text.</summary>
    Body,
    /// <summary>Secondary/hint text.</summary>
    Dim,
    /// <summary>A warning/error/status message (the port's orange).</summary>
    Warning,
    /// <summary>A plain white run (the options/benchmark lists).</summary>
    Plain,
    /// <summary>A highlighted selection run (the benchmark menu's yellow).</summary>
    Highlight,
}

/// <summary>P1042b — the fill a screen plate uses.</summary>
public enum ScreenPlateRole
{
    /// <summary>The band behind a selected row.</summary>
    Selection,
    /// <summary>The band behind a focused text field.</summary>
    Focus,
    /// <summary>The active field's 2 px underline (the title ink).</summary>
    UnderlineActive,
    /// <summary>The idle field's 2 px underline (the port's muted teal).</summary>
    UnderlineIdle,
}

/// <summary>P1042b — one non-selectable text run at its resolved anchor.
/// <paramref name="ClassicInk"/> is the exact colour the pre-boundary drawing used (so
/// Classic/Modern are byte-identical); <paramref name="Role"/> is what a vector skin
/// paints with.</summary>
public readonly record struct ScreenTextRun(string Text, Vector2 Position, float Scale,
    ScreenInkRole Role, Color ClassicInk);

/// <summary>
/// P1042b — one selectable list row. <paramref name="Label"/> is the semantic label (no
/// selection prefix); <paramref name="Text"/> is the exact run the classic painter draws
/// (prefix/fit included); <paramref name="Position"/> is that run's anchor;
/// <paramref name="Rect"/> is the row's own band — the single rectangle the chrome frames
/// and the audit compares with the drawn control; <paramref name="ClassicInk"/> is the
/// exact pre-boundary colour.
/// </summary>
public readonly record struct ScreenRow(string Label, string Text, Vector2 Position,
    RectangleF Rect, float Scale, bool Selected, ScreenInkRole Role, Color ClassicInk);

/// <summary>P1042b — a plate at its resolved rectangle. <paramref name="ClassicFill"/> is
/// the exact pre-boundary fill.</summary>
public readonly record struct ScreenPlate(RectangleF Rect, ScreenPlateRole Role, Color ClassicFill);

/// <summary>
/// P1042b — one pre-game intro sub-view as a read-only model: the view id, the classic
/// title/subtitle, the selectable rows and their resolved bands, extra text runs at their
/// existing anchors, and the selection/focus plates. Every value is the model's own and
/// every rectangle is the one the drawing code already used, so a skin can only paint.
/// </summary>
public readonly record struct IntroViewFrame(
    Viewport Viewport,
    string View,
    string Title,
    string Subtitle,
    IReadOnlyList<ScreenRow> Rows,
    IReadOnlyList<ScreenTextRun> Lines,
    IReadOnlyList<ScreenPlate> Plates,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042b — the career/squads/events screen as a read-only model: the P808c models, the
/// active mode, the cursor and the two text-entry fields. It can only be drawn; every
/// action (tab, move, view, create, search) stays in the game's own dispatchers.
/// </summary>
public readonly record struct CareerScreenFrame(
    Viewport Viewport,
    Career.CharacterInfoModel? Info,
    Career.LeaderboardModel? Board,
    Career.SquadScreenModel? Squads,
    Career.EventsScreenModel? Events,
    bool BoardMode,
    bool SquadsMode,
    bool EventsMode,
    int Cursor,
    bool EditingDescription,
    string DescriptionBuffer,
    string Status,
    string ViewedAccountId,
    HudStyle Style,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042 — the loadout/shop screen as a read-only model. <paramref name="V2Layout"/> and
/// <paramref name="V2Frame"/> are the P1013 geometry+snapshot (the layout's leaves ARE the
/// hit targets — <see cref="LoadoutV2Layout.HitTest"/>); when they are null the legacy
/// native-layout shop rows are drawn. Nothing here can buy: the transactions stay in
/// <see cref="LoadoutBuyFlow"/> and the keyboard/pointer dispatchers.
/// </summary>
public readonly record struct LoadoutScreenFrame(
    Viewport Viewport,
    IReadOnlyList<ShopLayout.Row> Rows,
    IReadOnlyList<ShopLayout.Row>? DetailRows,
    int? Money,
    float BackdropAlpha,
    float FooterHeight,
    bool SplitDetailPanels,
    Texture2D? CreditBox,
    bool NativeMoneyPanel,
    LoadoutV2Presentation? V2Frame,
    LoadoutV2Layout? V2Layout,
    HudStyle Style,
    LoadoutV2Layout.Element? Hover,
    LoadoutV2HardpointMarker? Marker,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042c — the inks the in-mission leaderboard paints with. Null = the P639 Classic/Modern
/// constants exactly as they were; Jet supplies its palette so the roster cells are re-inked
/// without touching the panel/row geometry.
/// </summary>
public readonly record struct ScoreboardPalette(
    Color Panel, Color Border, Color Title, Color Name, Color Stat, Color Self, Color Rank);

/// <summary>
/// P1042c — the leaderboard as a read-only frame: the already-ordered rows (P639/P774b), the
/// self name the renderer highlights, and the resolved panel/header/row bands that the
/// renderer's own <see cref="ScoreboardLayout.Geometry"/> produces. The bands are the single
/// rectangle source the Jet chrome frames.
/// </summary>
public readonly record struct ScoreboardFrame(
    Viewport Viewport,
    IReadOnlyList<ScoreboardLayout.Row> Rows,
    string? SelfName,
    RectangleF Panel,
    RectangleF HeaderBand,
    IReadOnlyList<RectangleF> RowBands,
    ScoreboardPalette? Palette,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042c — the starmap screen as a read-only frame. <paramref name="Sites"/>,
/// <paramref name="Edges"/> and the owner/colour maps are the exact data
/// <see cref="StarmapRenderer"/> already draws (the map itself stays native content); the two
/// lookups are pure read-only queries, exactly the ones the renderer takes. The panel and
/// info-pane rectangles are resolved by the same arithmetic the renderers use, so a skin can
/// frame them.
/// </summary>
public readonly record struct StarmapScreenFrame(
    Viewport Viewport,
    IReadOnlyList<StarmapLayout.ClusterSite> Sites,
    IReadOnlyList<ClusterInfo.WarpEdge> Edges,
    short? CurrentSector,
    short? SelectedSector,
    Func<short, short>? Warnings,
    bool FlashOn,
    float PanelW,
    float PanelH,
    IReadOnlyDictionary<short, SectorMapQueries.ClusterOwners>? Owners,
    Func<short, Color?>? SideColor,
    IReadOnlyDictionary<short, int>? KnownEnemySectors,
    IReadOnlySet<short>? HighlightedClusters,
    short? ChatCluster,
    SectorInfoPaneLayout.Data? InfoPane,
    Color? OwnSideColor,
    bool Expanded,
    RectangleF Panel,
    RectangleF InfoBox,
    RectangleF InfoExpandButton,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

#if !BROWSER
/// <summary>
/// P1042c — the game-over result screen as a read-only frame: the resolved panel text
/// (<see cref="GameOverPanelInfo"/>) and the two result tables the unchanged
/// <see cref="GameOverScreenRenderer"/> paints. The renderer owns the table geometry (its
/// header hit rectangles are exactly what the pointer sorts on), so the chrome frames the
/// rectangles it reports rather than duplicating them.
/// </summary>
public readonly record struct GameOverScreenFrame(
    Viewport Viewport,
    GameOverPanelInfo Info,
    GameOverScreenRenderer.Tables Tables,
    int? NextMatchSeconds,
    GameOverPerfPanel? Perf,
    RectangleF Panel,
    ScreenInks Inks)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}
#endif
