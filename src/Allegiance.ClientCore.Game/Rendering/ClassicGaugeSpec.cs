using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-14 — the direction the native <c>GaugeImageRect</c> reveal grows, from
/// the native <c>JustifyTop</c> / <c>JustifyBottom</c> justifications
/// (<c>engine/image.cpp</c> GaugeImage::Render, dialog.mdl per-gauge entries).
/// A <c>Top</c> reveal anchors the fill to the TOP of the gauge and grows
/// downward; a <c>Bottom</c> reveal anchors to the BOTTOM and grows upward.
/// The native values (<c>dialog.mdl:2173</c> THRUST and <c>:2226</c> FUEL use
/// JustifyTop; AMMO <c>:2278</c>, ENERGY <c>:2331</c>, hull <c>:2404</c> and
/// shield <c>:2474</c> use JustifyBottom) are the ones HUD-6 already
/// validated — the port's reveal direction is unchanged, only the source
/// rect and layer composition now match native.
/// </summary>
public enum GaugeReveal { Top, Bottom }

/// <summary>
/// HUD-14 — one native classic centre-console gauge expressed as a record,
/// NOT hand-tuned constants. Every field cites its <c>dialog.mdl</c> line.
/// Native builds each gauge as a <c>GroupImage</c> of layered images at
/// <c>TranslateImage</c> child points (engine/image.cpp TranslateImage =
/// child bounds origin), assembled into the <c>DefaultCenterHudOn</c> group at
/// <c>dialog.mdl:2604-2623</c>. This record mirrors exactly that structure.
/// </summary>
public readonly record struct ClassicGaugeSpec(
    GaugeKind Kind,
    // Layer texture stems (the real artwork filenames; native lowercase names
    // are cited per field — the capital stems are the same file, 2-D decode).
    string BackStem,      // native back (GaugeImageRect value=1, or plain back art)
    string FillStem,      // native green/white fill, revealed through the rect
    string? RedStem,      // native red fill variant, switched by Flash (hull/shield)
    string? SpineStem,    // native spine art (thrust/fuel/ammo/energy)
    string? FrameStem,    // native CenterHudLeft/Right frame (hull/shield)
    // Native GaugeImageRect source rect in texture pixels (dialog.mdl line cited).
    Rectangle SourceRect,
    GaugeReveal Reveal,
    // Native TranslateImage child points within the gauge group (dialog.mdl).
    Vector2 BackPoint,     // where the back layer sits
    Vector2 FillPoint,     // where the revealed fill GaugeImageRect sits
    Vector2 SpinePoint,
    Vector2 FramePoint,
    Vector2 LabelPoint,
    string Label,
    bool LabelRightAlign,    // native StringImage JustifyRight/Left
    // Console assembly offset, centre-relative, in SCREEN y-DOWN coords.
    // Native places each gauge in the 640x480 DefaultCenterHudOn pane by a
    // TranslateImage point measured in y-UP from the pane's bottom-left
    // (Dialog DrawImage3D lower-left origin, engine/context.cpp:552). The pane's
    // top-left is at centre-(320,240) (dialog.mdl:5397), so subtracting (320,240)
    // and converting y-up to y-down gives X = consoleX-320 and Y = 240-consoleY.
    Vector2 ConsoleOffset,
    // The dialog.mdl line the gauge definition spans (for the report/citations).
    int MdlLine,
    // The native value source binding that drives the fill (for the report).
    string NativeValue);

/// <summary>
/// The six native classic centre-console gauges as a data table. Each row is
/// transcribed from <c>~/Development/Artwork/dialog.mdl</c> (lines cited) and
/// cross-checked against the decoded artwork. Value sources map to the port's
/// existing HUD model fields (HudModel.Gauges → HudFrame): hull=&gt;
/// GetPercentHitPoints→HullFraction, shield=&gt;GetPercentShields→ShieldFraction,
/// energy=&gt;GetPercentEnergy→EnergyFraction, fuel=&gt;GetFuel→FuelFraction,
/// ammo=&gt;GetAmmo→AmmoFraction, thrust=&gt;playerThrottle→(Throttle+1)/2.
/// </summary>
public static class ClassicGaugeTable
{
    /// <summary>The native JustifyTop arc gauges (THRUST, FUEL) reveal from
    /// the top; the JustifyBottom ones (AMMO, ENERGY, HULL, SHIELD) from the
    /// bottom — matching HUD-6's validated direction.</summary>
    public static ClassicGaugeSpec Spec(GaugeKind kind) => kind switch
    {
        GaugeKind.Thrust => new(
            GaugeKind.Thrust,
            "ThrustGaugebmp", "thrustgauge2bmp", null, "thrustspinebmp", null,
            new Rectangle(15, 10, 95, 108), GaugeReveal.Top,
            new Vector2(15, 10), new Vector2(15, 10), new Vector2(4, 2), Vector2.Zero,
            new Vector2(16, 135), "THRUST", LabelRightAlign: true,
            new Vector2(-159, -6), /* dialog.mdl:2173-2223; console (161,246): 240-246=-6 */
            2173,
            "playerThrottle (GetFuel switch note :503)"),
        GaugeKind.Fuel => new(
            GaugeKind.Fuel,
            "FuelGaugebmp", "fuelgauge2bmp", null, "fuelspinebmp", null,
            new Rectangle(17, 10, 96, 108), GaugeReveal.Top,
            new Vector2(17, 10), new Vector2(17, 10), new Vector2(-4, 2), Vector2.Zero,
            new Vector2(23, 135), "FUEL", LabelRightAlign: false,
            new Vector2(41, -6), /* dialog.mdl:2226-2275; console (361,246): 240-246=-6 */
            2226,
            "GetFuel(Me, OnEveryFrame)"),
        GaugeKind.Ammo => new(
            GaugeKind.Ammo,
            "AmmoGaugebmp", "ammogauge2bmp", null, "ammospinebmp", null,
            new Rectangle(29, 18, 69, 92), GaugeReveal.Bottom,
            new Vector2(29, 18), new Vector2(29, 18), new Vector2(5, -4), Vector2.Zero,
            new Vector2(0, 3), "AMMO", LabelRightAlign: true,
            new Vector2(-173, 135), /* dialog.mdl:2278-2321; console (147,105): 240-105=135 */
            2278,
            "GetAmmo(Me, OnEveryFrame)"),
        GaugeKind.Energy => new(
            GaugeKind.Energy,
            "EnergyGaugebmp", "energygauge2bmp", null, "energyspinebmp", null,
            new Rectangle(30, 18, 69, 92), GaugeReveal.Bottom,
            new Vector2(30, 18), new Vector2(30, 18), new Vector2(-5, -4), Vector2.Zero,
            new Vector2(24, 3), "ENERGY", LabelRightAlign: false,
            new Vector2(52, 135), /* dialog.mdl:2331-2374; console (372,105): 240-105=135 */
            2331,
            "GetPercentEnergy(Me, OnEveryFrame)"),
        GaugeKind.Hull => new(
            GaugeKind.Hull,
            "hullgauge2bmp", "HullGaugebmp", "hullgaugeredbmp", null, "centerhudleftbmp",
            new Rectangle(40, 23, 48, 82), GaugeReveal.Bottom,
            new Vector2(-13, 4), new Vector2(27, 27), Vector2.Zero, Vector2.Zero,
            new Vector2(-10, 63), "ARMOR", LabelRightAlign: false,
            new Vector2(-105, 60), /* dialog.mdl:2404-2445; console (215,180): 240-180=60 */
            2404,
            "GetPercentHitPoints(Me, OnEveryFrame)"),
        GaugeKind.Shield => new(
            GaugeKind.Shield,
            "shieldgauge2bmp", "ShieldGaugebmp", "shieldgaugeredbmp", null, "centerhudrightbmp",
            new Rectangle(40, 23, 48, 82), GaugeReveal.Bottom,
            new Vector2(12, 5), new Vector2(52, 28), Vector2.Zero, Vector2.Zero,
            new Vector2(40, 63), "SHIELD", LabelRightAlign: true,
            new Vector2(-20, 60), /* dialog.mdl:2474-2516; console (300,180): 240-180=60 */
            2474,
            "GetPercentShields(Me, OnEveryFrame)"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Native <c>GaugeImageRect</c> reveal rule (engine/image.cpp GaugeImage::Render,
    /// b3D=true path): only <c>yoffset = (int)(rect.Height · value)</c> rows of
    /// the rect band are drawn, taken from the justified edge — the top for
    /// JustifyTop, the bottom for JustifyBottom — and anchored to that edge.
    /// Returns the SOURCE rectangle (texture pixels, row-0-at-top, the 2-D
    /// decode orientation) for a given value. At value 0 the band is empty;
    /// at 1 it is the full rect. Value is clamped to [0,1] like native.
    /// </summary>
    public static Rectangle RevealSource(Rectangle rect, GaugeReveal reveal, float value)
    {
        int yoff = (int)(rect.Height * Math.Clamp(value, 0f, 1f));
        return reveal == GaugeReveal.Top
            ? new Rectangle(rect.X, rect.Y, rect.Width, yoff)
            : new Rectangle(rect.X, rect.Y + rect.Height - yoff, rect.Width, yoff);
    }

    /// <summary>All six specs, in the native console row order.</summary>
    public static readonly IReadOnlyList<ClassicGaugeSpec> All =
    [
        Spec(GaugeKind.Thrust),
        Spec(GaugeKind.Fuel),
        Spec(GaugeKind.Ammo),
        Spec(GaugeKind.Energy),
        Spec(GaugeKind.Hull),
        Spec(GaugeKind.Shield),
    ];

    /// <summary>
    /// The native red-variant switch: a gauge shows its red fill only when the
    /// damage <c>Flash</c> binding is on AND the gauge has a red artwork variant
    /// (native SwitchImage on Flash for hull/shield, dialog.mdl:2418-2440 /
    /// :2498-2510). Thrust/fuel/ammo/energy have no red variant and never switch.
    /// </summary>
    public static bool UseRedFill(bool damageFlash, ClassicGaugeSpec spec)
        => damageFlash && spec.RedStem is not null;
}

/// <summary>
/// HUD-14b — the screen placement of the classic gauges. Native lays the whole
/// <c>DefaultCenterHudOn</c> pane out in 640x480 space with y pointing UP from
/// the bottom-left (Dialog <c>DrawImage3D</c> lower-left origin,
/// engine/context.cpp:552; "Photoshop measures Y from the top, Alleg from the
/// bottom", dialog.mdl:225/231/311/317). HUD-14 treated every y as pointing
/// down, which stuck THRUST/FUEL below centre and AMMO/ENERGY above. This class
/// converts each layer from native y-up console space to screen y-down with ONE
/// pane scale applied to both the console offset and the layer geometry (native
/// zooms the whole pane by a single factor; a mixed spread/0.72 made the layers
/// drift apart).
/// </summary>
public static class ClassicGaugeLayout
{
    /// <summary>
    /// The single pane scale for the classic gauge cluster, applied to BOTH the
    /// console offsets and every layer's local geometry. HUD-14 scaled the
    /// console offsets by <c>OwnStatusLayout.Spread</c> (1.15–2.1) but the layer
    /// geometry by 0.72, so they drifted apart.
    /// HUD-14c — this 0.72 is a PORT DEVIATION, stated honestly: the `.5` at
    /// dialog.mdl:5397 is the whole DefaultCenterHudOn group's OVERLAY
    /// SLIDE-ANIMATION time (every row carries it with a hide point
    /// (−2000,…)), not a zoom, so native has no per-pane scale factor. The port
    /// picks one scale (0.72) to keep the cluster legible on a modern viewport;
    /// the important fix is that a SINGLE factor drives both the console
    /// offsets and the layer geometry so they cannot drift apart.
    /// </summary>
    public const float PaneScale = 0.72f;

    /// <summary>
    /// The gauge group's origin on screen. The centre-relative offset is
    /// <see cref="ClassicGaugeSpec.ConsoleOffset"/> (already y-down:
    /// <c>X = consoleX−320</c>, <c>Y = 240−consoleY</c>).
    /// </summary>
    public static Vector2 Anchor(Vector2 centre, ClassicGaugeSpec spec, float scale)
        => new(centre.X + spec.ConsoleOffset.X * scale,
               centre.Y + spec.ConsoleOffset.Y * scale);

    /// <summary>
    /// The screen Y of a layer's TOP edge, converting native y-up to screen
    /// y-down. Derivation: the layer occupies <c>[cy, cy+layerH]</c> in the
    /// group's y-UP local space; the group origin sits at pane-y-up = consoleY
    /// (DefaultCenterHudOn TranslateImage, dialog.mdl:2610-2615); the pane's
    /// vertical centre is 240 in the 640x480 pane (dialog.mdl:5397). Converting
    /// to screen y-down relative to centre and scaling:
    ///   screenY = centre.Y − ((consoleY − 240)·s + (cy + layerH)·s)
    /// This is the exact form; <paramref name="anchor"/> is already
    /// <c>centre.Y − (consoleY − 240)·s</c>, so equivalently
    /// <c>screenY = anchor.Y − (cy + layerH)·s</c>.
    /// </summary>
    public static float LayerTopY(Vector2 anchor, Vector2 childPoint, float layerH, float scale)
        => anchor.Y - (childPoint.Y + layerH) * scale;

    /// <summary>The screen X of a layer's left edge (x is not flipped).</summary>
    public static float LayerLeftX(Vector2 anchor, Vector2 childPoint, float scale)
        => anchor.X + childPoint.X * scale;

    /// <summary>HUD-14c — the screen TOP of a gauge label whose native y-up
    /// <c>LabelPoint</c> is the text's LOWER-LEFT: the text's BOTTOM is
    /// <c>anchor.Y − LabelPoint.Y·s</c>, so its TOP is that minus the text
    /// height (the HUD-14/14b code used the point as the top, sitting the label
    /// one text height too low). Pure so a headless test pins the correction.</summary>
    public static float LabelTopY(Vector2 anchor, Vector2 labelPoint, float scale, float textHeight)
        => anchor.Y - labelPoint.Y * scale - textHeight;

    /// <summary>
    /// The screen destination rectangle of a revealed <c>GaugeImageRect</c> band
    /// at <paramref name="fraction"/>. The band is placed in native y-up: the
    /// fill's TranslateImage child point (<c>spec.FillPoint</c>) is the band's
    /// lower-left and the band spans up to
    /// <c>FillPoint.Y + SourceRect.Height</c>; in screen y-down the band bottom
    /// is <c>anchor.Y − FillPoint.Y·scale</c>. JustifyTop anchors the reveal to
    /// the band TOP (grows down); JustifyBottom anchors to the band BOTTOM
    /// (grows up) — the native GaugeImageRect rule. Used by the renderer and the
    /// headless tests so they cannot drift apart.
    /// </summary>
    public static Rectangle RevealDest(ClassicGaugeSpec spec, float fraction, Vector2 anchor, float scale)
    {
        var src = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, fraction);
        float bandBottom = anchor.Y - spec.FillPoint.Y * scale;
        float top = spec.Reveal == GaugeReveal.Top
            ? bandBottom - spec.SourceRect.Height * scale
            : bandBottom - src.Height * scale;
        return new Rectangle(
            (int)MathF.Round(LayerLeftX(anchor, spec.FillPoint, scale)),
            (int)MathF.Round(top),
            (int)MathF.Round(src.Width * scale),
            (int)MathF.Round(src.Height * scale));
    }
}
