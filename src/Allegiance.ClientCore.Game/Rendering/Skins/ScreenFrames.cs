using Allegiance.ClientCore.MonoGame;
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
