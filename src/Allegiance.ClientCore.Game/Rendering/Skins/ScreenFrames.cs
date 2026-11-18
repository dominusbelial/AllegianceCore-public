using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

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
