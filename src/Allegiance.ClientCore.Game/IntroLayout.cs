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
/// <para>
/// P946 — the menu compresses on short viewports: the shipping 13-row menu at
/// the 34 px design pitch needs 442 px of column, which does not fit above the
/// pilot field on SDL's 800×480 offscreen surface (nor on a 480p window). When
/// the design pitch does not fit, the rows shrink to the space available and
/// the identity status line moves under the subtitle (above the column)
/// instead of between the column and the pilot field. 720p and taller keep
/// the design geometry exactly.
/// </para>
/// </summary>
public static class IntroLayout
{
    public const float RowHeight = 34f;
    public const float BoxWidth = 260f;
    public const float TitleMarginTop = 28f;
    /// <summary>Smallest row pitch used when a short viewport compresses the column.</summary>
    public const float MinRowHeight = 16f;
    /// <summary>P946 — the pilot-name field's highlight box (centred).</summary>
    public const float PilotBoxWidth = 360f;
    public const float PilotBoxHeight = 26f;
    /// <summary>P946 — y of the identity status line when it moves above a compressed column.</summary>
    public const float CompactIdentityY = 104f;

    /// <summary>Compute the main-menu geometry for a viewport. Vertically it
    /// starts at 24% height, one row per button; the pilot-name field sits 70px
    /// above the bottom; the footer 40px above the bottom.</summary>
    public static IntroLayoutResult Compute(float w, float h, int rowCount)
    {
        float startY = h * 0.24f;
        float pilotY = h - 70f;
        float footerY = h - 40f;
        float identityY = pilotY - 48f;
        float rowHeight = RowHeight;
        float textScale = 1f;
        bool compressed = false;

        // P946 — keep the whole column above the pilot field. At 720p+ the
        // design pitch fits and nothing below changes.
        float available = pilotY - 12f - startY;
        if (rowCount > 0 && rowCount * RowHeight > available)
        {
            // Short viewport: give the column the band the identity line held,
            // and start below the subtitle so nothing overlaps it.
            compressed = true;
            startY = MathF.Max(startY, CompactIdentityY + 24f);
            available = pilotY - 12f - startY;
            rowHeight = MathF.Max(MinRowHeight, available / rowCount);
            textScale = MathF.Min(1f, rowHeight / RowHeight);
            identityY = CompactIdentityY;
        }

        float boxH = MathF.Max(6f, rowHeight - 6f);
        float boxLeft = w / 2f - BoxWidth / 2f;
        var rows = new IntroRect[rowCount];
        for (int i = 0; i < rowCount; i++)
            rows[i] = new IntroRect(boxLeft, startY + i * rowHeight - 4f, BoxWidth, boxH);
        // At the design pitch the status line sits 48 px above the pilot
        // field; on 720p the selection box reaches past that, so nudge it
        // below the box (never up into the rows, never onto the field).
        if (!compressed && rows.Length > 0)
            identityY = MathF.Min(pilotY - 30f, MathF.Max(identityY, rows[^1].Bottom + 4f));
        return new IntroLayoutResult(rows, pilotY, footerY, rowHeight, identityY, textScale);
    }

    /// <summary>P946 — the pilot-name field's highlight box for a viewport.</summary>
    public static IntroRect PilotField(float w, float h)
    {
        float y = h - 70f;
        return new IntroRect(w / 2f - PilotBoxWidth / 2f, y - 6f, PilotBoxWidth, PilotBoxHeight);
    }
}

/// <summary>Result of <see cref="IntroLayout.Compute"/>: the button-row rects,
/// the pilot-field anchor y, the footer y, the row pitch/text scale actually
/// used, and where the identity status line goes.</summary>
public readonly record struct IntroLayoutResult(
    IntroRect[] Rows, float PilotY, float FooterY, float RowHeight, float IdentityY, float RowTextScale)
{
    public float LastRowBottom => Rows.Length > 0 ? Rows[^1].Bottom : 0f;
}
