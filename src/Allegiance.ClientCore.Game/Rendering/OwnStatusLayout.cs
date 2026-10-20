using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-6 — native centre-HUD anchors for the own-ship status readouts
/// (<c>Artwork/dialog.mdl:2604-2623</c>, the <c>DefaultCenterHudOn</c> group).
/// The pane is 640×480 and is positioned with its TOP-LEFT corner at the
/// viewport centre minus (320,240) (<c>dialog.mdl:5397</c>:
/// <c>(CenterHudDisplayImage, SideCenter, Point(-320,1000), Point(-320,-240), …)</c>),
/// so pane Y increases downward. Each <see cref="HudPlacement"/> below is the
/// native point MINUS (320,240), in screen-space (+Y down) terms.
/// <para>Native <c>TranslateImage</c> sets the child image's own bounds origin
/// (its top-left corner) to that point (<c>engine/image.cpp:834-845</c>) — it
/// does not centre the image there. <see cref="ScreenPos"/> therefore returns
/// the top-left corner where the bitmap/StringImage box is drawn.</para>
/// </summary>
public static class OwnStatusLayout
{
    public static HudPlacement Placement(string id) => id switch
    {
        // dialog.mdl:2617 — ExperienceImage group anchor Point(281,102).
        "Kill bonus" => new(HudAnchor.Center, -39f, -138f, true),
        // dialog.mdl:2621 — SignatureImage Point(281,133).
        "Signature percentage" => new(HudAnchor.Center, -39f, -107f, true),
        // dialog.mdl:2618 — PaneImage(RadarDetectPane) Point(390,55).
        "Detection eye" => new(HudAnchor.Center, 70f, -185f, true),
        // dialog.mdl:2609 — CloakOn Point(120,80).
        "Cloak active" => new(HudAnchor.Center, -200f, -160f, true),
        // Port extension marker beside the native cloak art.
        "Cloak strength and drained state" => new(HudAnchor.Center, -200f, -140f, true),
        // dialog.mdl:2608 — cortAutopilotOnImage Point(177,342).
        "Autopilot status" => new(HudAnchor.Center, -143f, 102f, true),
        // dialog.mdl:2607 — cortVectorLockOn Point(442,337).
        "Vector lock status" => new(HudAnchor.Center, 122f, 97f, true),
        // dialog.mdl:2619 — CaptureFlag Point(427,350).
        "Carried flag" => new(HudAnchor.Center, 107f, 110f, true),
        // dialog.mdl:2620 — Artifact Point(159,350).
        "Carried artifact" => new(HudAnchor.Center, -161f, 110f, true),
        _ => new(HudAnchor.Center, 0f, 0f, true),
    };

    /// <summary>The viewport-relative spread the classic gauge cluster uses,
    /// kept identical so status icons stay on the same native-anchor grid.</summary>
    public static float Spread(float viewportHeight) => Math.Clamp(viewportHeight / 680f, 1.15f, 2.1f);

    /// <summary>The native top-left corner of a status element's bitmap/box in
    /// screen coordinates, using the gauge cluster's spread.</summary>
    public static Vector2 ScreenPos(string id, int viewportWidth, int viewportHeight)
    {
        var p = Placement(id);
        float spread = Spread(viewportHeight);
        return new(viewportWidth / 2f + p.X * spread,
            viewportHeight / 2f + p.Y * spread);
    }

    /// <summary>Native inner offsets of the <c>ExperienceImage</c> group
    /// members, in pane points (dialog.mdl:2563-2567).</summary>
    public static Vector2 KillCounterBitmapOffset(float viewportHeight)
        => new(-23f * Spread(viewportHeight), -65f * Spread(viewportHeight));

    public static Vector2 KillCounterNumberOffset(float viewportHeight)
        => new(0f, 5f * Spread(viewportHeight));

    /// <summary>Native <c>StringImage(JustifyCenter, 80, …)</c> box width
    /// (dialog.mdl:2595-2602). The text is centred inside this box, whose
    /// top-left is the TranslateImage anchor.</summary>
    public const float NativeTextWidth = 80f;
}
