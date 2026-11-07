using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P945 — the native hangar screen's geometry and strings, in native 800×600
/// screen space. This is the ONE source of draw and hit-test rectangles: the
/// renderer draws each native pane at <see cref="Rect"/> and the pointer hit
/// test uses the same rectangle, so a button can never be drawn somewhere it
/// cannot be clicked.
///
/// <para><b>Coordinates.</b> Native 2-D here is top-left, y-down:
/// <c>ScreenPaneFactory</c> places every child with
/// <c>SetOffset(WinPoint(point.X, point.Y))</c> (<c>effect/efpane.cpp:642-682</c>)
/// and <c>Pane::PaintAll</c> offsets the D3D9 viewport by that value with
/// <c>Y = clip.YMin() + surfOffset.Y()</c> (<c>engine/pane.cpp:472-500</c>), i.e.
/// pixel rows from the top. The screenshots of the native hangar agree: the
/// door the launch button animates sits at y≈162 from the top of
/// <c>hangarbmp</c>, and the hover text at y=561 is the bottom strip.
/// (The y-up convention belongs to HUD <em>images</em>, not screen panes.)</para>
///
/// <para><b>Panes</b> (<c>hangar.mdl:87-102</c>, the <c>screen = ScreenPane(...)</c>
/// list): launch (367,162), command (84,283), loadout (526,247), invest (530,88),
/// help (208,95), teleport (214,248), team (43,102), exit (80,515), the hover
/// text pane (100,561) and the in-turret badge (320,450). The pane SIZES are the
/// artwork's own: <c>ButtonFacePane</c> takes <c>surface.Y / CountBits(faces)</c>
/// (<c>engine/button.cpp:108-140</c>) and <c>FrameImageButtonPane</c> takes the
/// frame image's bounds (<c>engine/frameimage.cpp:361-368</c>). They are pinned
/// here as <see cref="NativeSize"/> and asserted against the decoded artwork by
/// the tests.</para>
/// </summary>
public static class HangarScreen
{
    /// <summary>Native screen size — every screen is laid out at 800×600
    /// (<c>WinTrek/screen.h:14</c>).</summary>
    public const int NativeWidth = 800;
    public const int NativeHeight = 600;

    /// <summary>Native <c>hugeFont</c> cell height (px), parsed from
    /// <c>font.mdl</c> (the font's <c>FontImpl</c> header height).</summary>
    public const int HoverTextHeight = 23;

    /// <summary>Native hover colour <c>Color(0.75, 0.75, 0.75)</c>
    /// (<c>hangar.mdl:26</c>).</summary>
    public static readonly Color HoverTextColor = new(0.75f, 0.75f, 0.75f);

    /// <summary>One native control: its action, artwork stem and top-left point.</summary>
    public readonly record struct Control(HangarAction Action, string Stem, int X, int Y);

    /// <summary>
    /// The native buttons in native draw order (<c>hangar.mdl:87-102</c>; the
    /// pane list also fixes the paint order, bottom of the list painted last).
    /// <c>Invest</c> is hidden when the mission forbids developments
    /// (<c>WinTrek/hangarscreen.cpp:129-130</c>).
    /// </summary>
    public static readonly IReadOnlyList<Control> Controls =
    [
        new(HangarAction.Launch, "animlaunchbmp", 367, 162),
        new(HangarAction.Command, "animradarbmp", 84, 283),
        new(HangarAction.Loadout, "animloadoutbmp", 526, 247),
        new(HangarAction.Invest, "animinvestbmp", 530, 88),
        new(HangarAction.Help, "btnquitbmp", 208, 95),
        new(HangarAction.Teleport, "animsectorbmp", 214, 248),
        new(HangarAction.Team, "animteambmp", 43, 102),
        new(HangarAction.Exit, "btnexitgamebmp", 80, 515),
    ];

    /// <summary>The native pane size of each control, in native pixels. The
    /// button artwork is a vertical strip of <see cref="FaceCount"/> faces, so
    /// the pane is the strip's width × (height / faces).</summary>
    public static readonly IReadOnlyDictionary<HangarAction, (int Width, int Height)> NativeSize =
        new Dictionary<HangarAction, (int, int)>
        {
            [HangarAction.Launch] = (130, 214),      // animlaunch.mdl FrameImage background (130×214)
            [HangarAction.Command] = (104, 104),     // animradarbmp 104×208, 2 faces
            [HangarAction.Loadout] = (160, 160),     // animloadoutbmp 160×320, 2 faces
            [HangarAction.Invest] = (156, 156),      // animinvestbmp 156×312, 2 faces
            [HangarAction.Help] = (116, 116),        // btnquitbmp 116×232, 2 faces
            [HangarAction.Teleport] = (114, 114),    // animsectorbmp 114×228, 2 faces
            [HangarAction.Team] = (156, 156),        // animteambmp 156×312, 2 faces
            [HangarAction.Exit] = (83, 24),          // btnexitgamebmp 83×48, 2 faces
        };

    /// <summary>The native background bitmap (800×600 station interior).</summary>
    public const string BackgroundStem = "hangarbmp";

    /// <summary>The in-turret badge bitmap, 172×30 (<c>hangar.mdl:74-80</c>).</summary>
    public const string InTurretStem = "hangarinturretbmp";

    /// <summary>The native button strips hold two faces, in this order: the
    /// normal face on TOP, the mouse-inside face BELOW. <c>ButtonPane(...,
    /// 5, ...)</c> = <c>ButtonFaceUp | ButtonFaceInside</c>
    /// (<c>engine/button.h:77-80</c>); <c>ImageButtonFacePane</c> counts the
    /// bits (2) and slices the surface into <c>CountBits</c> equal rows
    /// (<c>engine/button.cpp:108-140</c>); <c>GetFaceIndex</c> maps
    /// <c>ButtonFaceUp</c> → row 0 and <c>ButtonFaceInside</c> → row 1
    /// (<c>engine/button.cpp:177-198</c>), and <c>Paint</c> picks
    /// <c>ButtonFaceInside</c> while the pointer is inside
    /// (<c>engine/button.cpp:249-290</c>).</summary>
    public const int FaceCount = 2;

    /// <summary>Face row for a strip: 0 = normal (<c>ButtonFaceUp</c>),
    /// 1 = mouse-inside (<c>ButtonFaceInside</c>). Pressed/down falls back to
    /// <c>ButtonFaceUp</c> because face bit 5 has no down face
    /// (<c>engine/button.cpp:240-256</c>).</summary>
    public static int FaceRow(bool inside) => inside ? 1 : 0;

    /// <summary>The hover text pane: 600×24 at (100,561) in <c>hugeFont</c>,
    /// justified centre (<c>hangar.mdl:26-72</c>).</summary>
    public const int HoverTextPaneX = 100;
    public const int HoverTextPaneY = 561;
    public const int HoverTextPaneWidth = 600;

    /// <summary>The in-turret badge pane: <c>hangarinturretbmp</c> at (320,450),
    /// drawn only while <c>BoardState == 2</c> (<c>hangar.mdl:74-80</c>;
    /// <c>WinTrek/console.cpp:797-804</c> sets 2 only for a turret gunner,
    /// <c>1</c> is a parented observer).</summary>
    public const int InTurretPaneX = 320;
    public const int InTurretPaneY = 450;

    /// <summary>The native hover string table, keyed by hover id
    /// (<c>hangar.mdl:26-72</c>; the ids are assigned in
    /// <c>WinTrek/hangarscreen.cpp:36-48</c>). Native has NO hover text and no
    /// button for <see cref="HangarAction.Buy"/>/<see cref="HangarAction.Default"/>
    /// — both are port-only verbs, so they keep the port's own wording.</summary>
    public static readonly IReadOnlyDictionary<HangarAction, string> HoverText =
        new Dictionary<HangarAction, string>
        {
            [HangarAction.None] = "",
            [HangarAction.Launch] = "Launch ship",
            [HangarAction.Command] = "Command screen",
            [HangarAction.Loadout] = "Ship's loadout",
            [HangarAction.Invest] = "Team investments",
            [HangarAction.Help] = "Help",
            [HangarAction.Teleport] = "Teleport to another station or Board another ship",
            [HangarAction.Team] = "Information about the teams playing",
            [HangarAction.Exit] = "Exit the game.",
            // Port-only verbs (no native hangar button).
            [HangarAction.Buy] = "Buy this loadout (request money if short)",
            [HangarAction.Default] = "Reset to this hull's default layout",
        };

    /// <summary>Whether the native in-turret badge is drawn for a board state —
    /// only the gunner state 2 (<c>hangar.mdl:74-80</c>).</summary>
    public static bool ShowsInTurretBadge(int? boardState) => boardState == 2;

    /// <summary>The native screen → viewport transform: uniform
    /// <c>min(w/800, h/600)</c> scale, centred (<c>WinTrek/WinTrek.cpp:3656-3681</c>
    /// <c>UpdateBackdropCentering</c>), exactly like P943's slides.</summary>
    public readonly record struct ScreenTransform(float Scale, float OffsetX, float OffsetY)
    {
        public static ScreenTransform For(int width, int height)
        {
            float scale = MathF.Min(width / (float)NativeWidth, height / (float)NativeHeight);
            if (!(scale > 0f)) scale = 1f;
            return new ScreenTransform(scale,
                (width - NativeWidth * scale) / 2f,
                (height - NativeHeight * scale) / 2f);
        }

        /// <summary>A native top-left rectangle in viewport pixels (rounded,
        /// never empty).</summary>
        public Rectangle Map(int x, int y, int width, int height) => new(
            (int)MathF.Round(OffsetX + x * Scale),
            (int)MathF.Round(OffsetY + y * Scale),
            Math.Max(1, (int)MathF.Round(width * Scale)),
            Math.Max(1, (int)MathF.Round(height * Scale)));
    }

    /// <summary>The viewport rectangle of a native control (its artwork pane).</summary>
    public static Rectangle Rect(int viewportWidth, int viewportHeight, Control control) =>
        Rect(viewportWidth, viewportHeight, control, NativeSize[control.Action]);

    /// <summary>The viewport rectangle of a native control drawn at a
    /// caller-supplied native size (the artwork's real size when it is
    /// installed; <see cref="NativeSize"/> is the fallback).</summary>
    public static Rectangle Rect(int viewportWidth, int viewportHeight, Control control, (int Width, int Height) size) =>
        ScreenTransform.For(viewportWidth, viewportHeight)
            .Map(control.X, control.Y, size.Width, size.Height);

    /// <summary>The viewport rectangle of a control by action. The port-only
    /// verbs have no native pane and resolve to
    /// <see cref="PortVerbRect"/> instead, so draw and hit-test stay identical
    /// for every action in <see cref="HangarControls.Actions"/>.</summary>
    public static Rectangle Rect(int viewportWidth, int viewportHeight, HangarAction action)
    {
        foreach (var control in Controls)
            if (control.Action == action)
                return Rect(viewportWidth, viewportHeight, control);
        return action is HangarAction.Buy or HangarAction.Default
            ? PortVerbRect(viewportWidth, viewportHeight, action == HangarAction.Default)
            : Rectangle.Empty;
    }

    /// <summary>The native control for an action, if it has one.</summary>
    public static Control? ControlOf(HangarAction action)
    {
        foreach (var control in Controls)
            if (control.Action == action)
                return control;
        return null;
    }

    /// <summary>The hover text pane's viewport rectangle (600×24 at (100,561)).</summary>
    public static Rectangle HoverTextRect(int viewportWidth, int viewportHeight) =>
        ScreenTransform.For(viewportWidth, viewportHeight)
            .Map(HoverTextPaneX, HoverTextPaneY, HoverTextPaneWidth, 24);

    /// <summary>The native in-turret badge size (172×30, <c>hangarinturretbmp</c>).</summary>
    public const int InTurretWidth = 172;
    public const int InTurretHeight = 30;

    /// <summary>The in-turret badge's viewport rectangle (172×30 at (320,450)).</summary>
    public static Rectangle InTurretRect(int viewportWidth, int viewportHeight) =>
        ScreenTransform.For(viewportWidth, viewportHeight)
            .Map(InTurretPaneX, InTurretPaneY, InTurretWidth, InTurretHeight);

    /// <summary>
    /// The port-only verbs' rectangles. Native has no hangar button for
    /// <see cref="HangarAction.Buy"/> / <see cref="HangarAction.Default"/>, so
    /// they are kept OUT of the 800×600 artwork: they sit in the letterbox
    /// margin beside the scaled screen when the viewport has one (any aspect
    /// other than 4:3 — a 16:9 window leaves 240 px per side at 1080p, a letter
    /// 4:3 window leaves a strip below), and when the screen fills the viewport
    /// exactly they fall back to the bottom-right corner inside the screen, past
    /// the hover pane's right edge. Native's lowest/right-most control is the
    /// hover text pane at x 100..700, y 561..585, so no native control is ever
    /// covered; the cells only ever sit over the dark console pixels of the
    /// background.
    /// </summary>
    public const int PortVerbWidth = 46;
    public const int PortVerbHeight = 16;

    public static Rectangle PortVerbRect(int viewportWidth, int viewportHeight, bool second)
    {
        var t = ScreenTransform.For(viewportWidth, viewportHeight);
        int column = second ? PortVerbWidth + 2 : 0;   // 2 native px gutter between the cells
        float marginX = (viewportWidth - NativeWidth * t.Scale) / 2f;
        float marginY = (viewportHeight - NativeHeight * t.Scale) / 2f;
        if (marginX >= 2 * PortVerbWidth * t.Scale + 8f)
            return t.Map(NativeWidth + 2 + column, NativeHeight - PortVerbHeight - 2,
                PortVerbWidth, PortVerbHeight);                            // right letterbox, bottom aligned
        if (marginY >= PortVerbHeight * t.Scale + 6f)
            return t.Map(NativeWidth - 2 * PortVerbWidth - 2 + column, NativeHeight + 2,
                PortVerbWidth, PortVerbHeight);                            // bottom letterbox
        return t.Map(NativeWidth - 2 * PortVerbWidth - 2 + column,
            NativeHeight - PortVerbHeight - 2, PortVerbWidth, PortVerbHeight);   // 4:3: inside, bottom-right
    }

    /// <summary>Whether the given point hits a native control (or a port-only
    /// verb). <paramref name="developments"/> hides <see cref="HangarAction.Invest"/>
    /// exactly like native (<c>WinTrek/hangarscreen.cpp:129-130</c>).</summary>
    public static HangarAction Hit(int viewportWidth, int viewportHeight, int x, int y, bool developments)
    {
        foreach (var action in HangarControls.Actions(developments))
            if (Rect(viewportWidth, viewportHeight, action).Contains(x, y))
                return action;
        return HangarAction.None;
    }
}
