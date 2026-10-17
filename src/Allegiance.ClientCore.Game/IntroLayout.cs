namespace Allegiance.ClientCore.MonoGame;

/// <summary>A rectangle in viewport pixels (float), for the intro layout.</summary>
public readonly record struct IntroRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
}

/// <summary>
/// P731 — pure layout model for the intro screen's main menu, expressed as
/// viewport-proportional geometry so it is unit-testable at any resolution
/// (the task's render test exercises 720p/1080p/1440p; the real client renders
/// the same geometry headless-proofed at those sizes). Kept free of MonoGame
/// types so it runs with no graphics device.
/// </summary>
public static class IntroLayout
{
    public const float RowHeight = 34f;
    public const float BoxWidth = 260f;
    public const float TitleMarginTop = 28f;

    /// <summary>Compute the main-menu geometry for a viewport. Vertically it
    /// starts at 24% height, one row per button; the pilot-name field sits 70px
    /// above the bottom; the footer 40px above the bottom.</summary>
    public static IntroLayoutResult Compute(float w, float h, int rowCount)
    {
        float startY = h * 0.24f;
        float boxH = RowHeight - 6f;
        float boxLeft = w / 2f - BoxWidth / 2f;
        float pilotY = h - 70f;
        float footerY = h - 40f;
        var rows = new IntroRect[rowCount];
        for (int i = 0; i < rowCount; i++)
            rows[i] = new IntroRect(boxLeft, startY + i * RowHeight - 4f, BoxWidth, boxH);
        return new IntroLayoutResult(rows, pilotY, footerY, RowHeight);
    }
}

/// <summary>Result of <see cref="IntroLayout.Compute"/>: the button-row rects,
/// the pilot-field anchor y, and the footer y.</summary>
public readonly record struct IntroLayoutResult(
    IntroRect[] Rows, float PilotY, float FooterY, float RowHeight)
{
    public float LastRowBottom => Rows.Length > 0 ? Rows[^1].Bottom : 0f;
}
