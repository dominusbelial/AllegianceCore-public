using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-6/FONT-1c — native centre-HUD anchors for the own-ship status readouts
/// (<c>Artwork/dialog.mdl:2604-2623</c>, the <c>DefaultCenterHudOn</c> group).
/// The pane is 640×480 and is centred on the viewport: its lower-left (y-up)
/// origin is the viewport centre minus (320,240) (<c>dialog.mdl:5397</c>:
/// <c>(CenterHudDisplayImage, SideCenter, Point(-320,1000), Point(-320,-240), …)</c>).
/// <para>Native 2-D image space is y-UP: every <c>TranslateImage</c> point is
/// measured from the 640×480 pane's BOTTOM-left (<c>engine/context.cpp:552</c>,
/// the lower-left origin; the same rule HUD-14b applies to the gauges,
/// <see cref="ClassicGaugeSpec.ConsoleOffset"/>). The port's screen space is
/// y-DOWN, so the centre-relative offset is
/// <c>X = consoleX − 320</c>, <c>Y = 240 − consoleY</c> (FONT-1c fix 1 — the
/// first round wrote <c>consoleY − 240</c>, which mirrored the whole cluster
/// about the viewport centre).</para>
/// <para>Native <c>TranslateImage</c> moves the child image's own bounds
/// origin to that point (<c>engine/image.cpp:834-845</c>); it does not centre
/// the image there. A bitmap's bounds are <c>Rect(Point(0,0), size)</c>
/// (<c>engine/image.cpp:184-190</c>; <c>PaneImage</c> the same,
/// <c>engine/paneimage.cpp:91-97</c>), so in y-up the point is the bitmap's
/// LOWER-left corner: <see cref="StatusIconTopLeft"/> draws the art upward from
/// it. A <c>StringImage</c>'s bounds are <c>Rect(0,-h,w,0)</c>
/// (<c>engine/image.cpp:353</c>), so for text the same point is the box's
/// UPPER-left and the text hangs below it. <see cref="ScreenPos"/> is the
/// anchor point itself in y-down screen space.</para>
/// </summary>
public static class OwnStatusLayout
{
    public static HudPlacement Placement(string id) => id switch
    {
        // dialog.mdl:2617 — ExperienceImage Point(281,102): (281-320, 240-102).
        "Kill bonus" => new(HudAnchor.Center, -39f, 138f, true),
        // dialog.mdl:2621 — SignatureImage Point(281,133): (281-320, 240-133).
        "Signature percentage" => new(HudAnchor.Center, -39f, 107f, true),
        // dialog.mdl:2618 — PaneImage(RadarDetectPane) Point(390,55): (390-320, 240-55).
        "Detection eye" => new(HudAnchor.Center, 70f, 185f, true),
        // dialog.mdl:2609 — CloakOn Point(120,80): (120-320, 240-80).
        "Cloak active" => new(HudAnchor.Center, -200f, 160f, true),
        // Port extension (no native art). The cloak anchor is concloakbmp's
        // LOWER-left (the art rises ABOVE +160), so the text hangs 20 native px
        // BELOW the art's bottom edge — never on the art, and 20 below the
        // "CLOAK" label in Modern (whose status text hangs from the anchor).
        "Cloak strength and drained state" => new(HudAnchor.Center, -200f, 180f, true),
        // dialog.mdl:2608 — cortAutopilotOnImage Point(177,342): (177-320, 240-342).
        "Autopilot status" => new(HudAnchor.Center, -143f, -102f, true),
        // dialog.mdl:2607 — cortVectorLockOn Point(442,337): (442-320, 240-337).
        "Vector lock status" => new(HudAnchor.Center, 122f, -97f, true),
        // dialog.mdl:2619 — CaptureFlag Point(427,350): (427-320, 240-350).
        "Carried flag" => new(HudAnchor.Center, 107f, -110f, true),
        // dialog.mdl:2620 — Artifact Point(159,350): (159-320, 240-350).
        "Carried artifact" => new(HudAnchor.Center, -161f, -110f, true),
        _ => new(HudAnchor.Center, 0f, 0f, true),
    };

    /// <summary>The viewport-relative spread the classic gauge cluster uses,
    /// kept identical so status icons stay on the same native-anchor grid.</summary>
    public static float Spread(float viewportHeight) => Math.Clamp(viewportHeight / 680f, 1.15f, 2.1f);

    /// <summary>The native TranslateImage point of a status element in y-down
    /// screen coordinates, using the gauge cluster's spread: a bitmap's
    /// LOWER-left corner, or a StringImage box's UPPER-left corner.</summary>
    public static Vector2 ScreenPos(string id, int viewportWidth, int viewportHeight)
    {
        var p = Placement(id);
        float spread = Spread(viewportHeight);
        return new(viewportWidth / 2f + p.X * spread,
            viewportHeight / 2f + p.Y * spread);
    }

    /// <summary>Draw scale of an own-status bitmap: the full-bleed 32 px CortUI
    /// PNGs (autopilot/vector lock) at 1, the large MDL canvases at the 0.72
    /// gauge art scale.</summary>
    public static float StatusIconScale(int textureWidth) => textureWidth <= 64 ? 1f : 0.72f;

    /// <summary>FONT-1c review — screen top-left at which a status bitmap is
    /// drawn so its LOWER-left corner sits on the native anchor (y-up bitmap
    /// bounds <c>Rect(Point(0,0), size)</c>, <c>engine/image.cpp:184-190</c>;
    /// <c>engine/paneimage.cpp:91-97</c>). The art therefore occupies
    /// <c>[anchor.Y − H·scale, anchor.Y]</c>, rising from the anchor.</summary>
    public static Vector2 StatusIconTopLeft(string id, int textureWidth, int textureHeight,
        int viewportWidth, int viewportHeight)
    {
        var anchor = ScreenPos(id, viewportWidth, viewportHeight);
        return new(anchor.X, anchor.Y - textureHeight * StatusIconScale(textureWidth));
    }

    /// <summary>FONT-1c review — screen origin of the <c>ExperienceImage</c>
    /// group (kill-bonus medal + number) drawn at group scale
    /// <paramref name="groupScale"/>. Native SIG and the kill-bonus number are
    /// both 80-wide JustifyCenter boxes at x=281 (dialog.mdl:2559-2564,
    /// 2595-2602, 2617, 2621), so they share one centre. The port lays the SIG
    /// box out at the pane spread but the medal group at its 0.72 art scale, so
    /// the group is shifted to put its number-box centre (40·g) on the SIG box
    /// centre (anchor.X + 40·spread). The group y stays on the anchor.</summary>
    public static Vector2 KillBonusGroupOrigin(Vector2 anchor, float spread, float groupScale)
        => new(anchor.X + NativeTextWidth / 2f * (spread - groupScale), anchor.Y);

    /// <summary>Native inner offset of the <c>killcounterbmp</c> bitmap
    /// member of the <c>ExperienceImage</c> group (dialog.mdl:2565:
    /// <c>TranslateImage(ImportImage3D("killcounterbmp", …), Point(-23,-65))</c>).
    /// Native is y-up and the bitmap origin is its LOWER-left
    /// (<c>engine/image.cpp:834-845</c>), so the screen top-left is
    /// <c>(-23, 65 − H)·g</c> for an H-tall bitmap at group scale g — the same
    /// ONE scale the bitmap is drawn at (FONT-1c fix 2; the old <c>Spread</c>
    /// base was 2.2× the art's 0.72).</summary>
    public static Vector2 KillCounterBitmapOffset(float artScale, int bitmapHeight)
        => new(-23f * artScale, (65f - bitmapHeight) * artScale);

    /// <summary>FONT-1c — the kill-bonus integer's box top-left RELATIVE TO the
    /// killcounterbmp bitmap top, at the same group scale g. Native puts the
    /// bitmap's LOWER-left at Point(-23,-65) and the integer StringImage's box
    /// top-left at Point(0,5) inside the same group (dialog.mdl:2559-2566), so
    /// in y-down screen terms the integer is
    /// <c>(0-(-23), (-5)-(65-H))·g = (23, H−70)·g</c> from the bitmap's
    /// top-left (FONT-1c; H=128 gives (23,58)·g, which lands the black digits
    /// on the decoded medal disc at rows ~52-72 instead of below it).</summary>
    public static Vector2 KillCounterNumberFromBitmap(float artScale, int bitmapHeight)
        => new(23f * artScale, (bitmapHeight - 70f) * artScale);

    /// <summary>Native <c>StringImage(JustifyCenter, 80, …)</c> box width
    /// (dialog.mdl:2595-2602). The text is centred inside this box, whose
    /// top-left is the TranslateImage anchor.</summary>
    public const float NativeTextWidth = 80f;
}
