namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P785 — the training HUD callouts: the pure port of native
/// <c>RenderTrainingOverlay</c>'s per-overlay geometry table
/// (<c>WinTrek/indicator.cpp:461-701</c>). Each overlay draws two red lines in an
/// angular formation from an anchor point (<c>pointA</c>) to a second point
/// (<c>pointB</c> — <c>pointA + pointDelta*len</c> for the gauge callouts,
/// <c>pointA + (5,0)</c> for the bottom row, indicator.cpp:676-690 builds the third
/// vertex from these), a hilite dot at the anchor, and description text hung off the
/// second point; Roll/Fuel/Throttle carry a second description line drawn above the
/// first (indicator.cpp:723-733).
/// Native screen space is y-up; this table is expressed in the port's top-left screen
/// space as offsets from the view centre, so every native Y sign is flipped
/// (port = −native Y; cases Speed:489-495 … Cloak:693-700). Native anchors Sector
/// (:561-568), Inventory (:570-576) and Target (:578-584) in absolute coordinates; the
/// port recomposes those relative to the centre (visual parity). Pure data — no fonts
/// or graphics device.
/// </summary>
public static class HudTrainingCallout
{
    /// <summary>One overlay's geometry and text (indicator.cpp switch cases). All
    /// offsets are port-space pixels relative to the view centre.</summary>
    public readonly record struct Callout(
        float AnchorX, float AnchorY, float SecondX, float SecondY,
        string Description, string? Description2);

    private static Callout Gauge(float ax, float ay, float dx, float dy, float len,
        string desc, string? desc2 = null) => new(ax, ay, dx * len, dy * len, desc, desc2);

    private static Callout Bottom(float ax, float ay, string desc) =>
        new(ax, ay, 5f, 0f, desc, null);   // indicator.cpp bottom row: pointB = pointA + Point(5,0)

    /// <summary>Resolve a <c>TrainingOverlay</c> id (the port's TrainingMissionIds
    /// Overlay* constants, mirroring native's TrainingOverlay enum, indicator.h:17-42)
    /// to its callout; null for id 0 (NoTrainingOverlay returns immediately,
    /// indicator.cpp:486-488) and for ids with no native case.</summary>
    public static Callout? For(int overlayId) => overlayId switch
    {
        1 => Gauge(-25f, 105.5f, -1f, 1f, 100f, "SPEED (meters per second)"),                 // Speed (indicator.cpp:489-495)
        2 => Gauge(-10.5f, -10.5f, -1f, -1f, 150f, "MOTION INDICATOR"),                       // MotionIndicator (:497-502)
        3 => Gauge(-10.5f, -10.5f, -1f, -1f, 150f, "RETICULE"),                               // Reticule (:504-509)
        4 => Gauge(-126.5f, -93.5f, -1f, -1f, 50f, "THROTTLE GAUGE",
                "SCROLL WHEEL ALSO CONTROLS THROTTLE"),                                       // Throttle (:511-517)
        5 => Gauge(126.5f, -93.5f, 1f, -1f, 100f, "FUEL GAUGE", "PUSH R TO RELOAD"),          // Fuel (:519-525)
        6 => Gauge(126.5f, 112.5f, 1f, 1f, 50f, "ENERGY GAUGE"),                              // Energy (:527-532)
        7 => Gauge(-126.5f, 112.5f, -1f, 1f, 50f, "AMMUNITION GAUGE"),                        // Ammo (:534-539)
        8 => Gauge(-65.5f, -35.5f, -1f, -1f, 100f, "ARMOR STRENGTH GAUGE"),                   // Hull (:541-546)
        9 => Gauge(65.5f, -35.5f, 1f, -1f, 100f, "SHIELD STRENGTH GAUGE"),                    // Shield (:548-553)
        10 => Gauge(-140f, 50f, -1f, 1f, 100f, "CHAT PANE"),                                  // Chat (:555-560)
        11 => new(99f, -190f, 99f + 20f, -190f, "SECTOR MAP", null),                          // Sector (:561-568, absolute — recomposed)
        12 => new(-150f, -265f, -150f - 50f, -265f, "INVENTORY DISPLAY", null),               // Inventory (:570-576, absolute — recomposed)
        13 => Gauge(-70f, 120f, -1f, 1f, 100f, "TARGET HUD"),                                 // Target (:578-584, absolute — recomposed)
        14 => Gauge(-100f, -85f, 1f, -1f, 100f, "CURRENT COMMAND"),                           // Command (:586-592)
        15 => Gauge(140.5f, 140.5f, 1f, 1f, 50f, "DETECTION INDICATOR"),                      // Eyeball (:594-600)
        16 => Gauge(-15f, 160.5f, -1f, 0.375f, 70f, "KILL BONUS"),                            // KillBonus (:602-608)
        17 => Bottom(-110f, 170f, "PUSH Q AND E TO ROLL LEFT AND RIGHT")
              with { Description2 = "PUSH V TO TOGGLE MOUSE CONTROL" },                       // Roll (:634-641)
        18 => Bottom(-70f, 170f, "SPACE TO BOOST"),                                           // Booster (:643-649)
        19 => Bottom(-144f, 170f, "CLICK RIGHT MOUSE BUTTON TO TARGET CENTER"),               // TargetCenter (:651-657)
        20 => Bottom(-75f, 170f, "LEFT CLICK TO FIRE GUNS"),                                  // FireGuns (:659-665)
        21 => Bottom(-72f, 170f, "PUSH F TO TARGET ENEMY"),                                   // TargetEnemy (:667-673)
        22 => Bottom(-140f, 170f, "PRESS 5 OR MIDDLE CLICK TO FIRE MISSILE"),                 // Missile (:675-681)
        23 => Bottom(-75f, 170f, "PUSH G TO TOGGLE CLOAK"),                                   // Cloak (:693-700)
        _ => null,
    };
}
