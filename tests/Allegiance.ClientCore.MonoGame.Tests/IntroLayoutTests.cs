namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P731 — the intro screen's main-menu layout at 720p / 1080p / 1440p: every
/// button row must sit within the viewport, above the pilot-name field and
/// footer, horizontally centred, without overlapping. Pure geometry
/// (<see cref="IntroLayout"/>), no graphics device — the visual render proof
/// of the same geometry at these sizes is the live real-client screenshot.
/// </summary>
public class IntroLayoutTests
{
    public static TheoryData<int, int> Resolutions => new()
    {
        { 1280, 720 },   // 720p
        { 1920, 1080 },  // 1080p
        { 2560, 1440 },  // 1440p
        { 3440, 1440 },  // ultrawide
        { 800, 480 },    // SDL's offscreen proof surface (P946)
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void All_rows_inside_viewport_and_above_pilot_and_footer(int w, int h)
    {
        var lay = IntroLayout.Compute(w, h, IntroScreenModel.MainLabels.Length);
        Assert.Equal(IntroScreenModel.MainLabels.Length, lay.Rows.Length);

        foreach (var r in lay.Rows)
        {
            Assert.True(r.X >= 0 && r.Right <= w, $"row {r} not horizontal-bounded in {w}x{h}");
            Assert.True(r.Y >= 0 && r.Bottom < lay.PilotY, $"row {r} overlaps pilot field in {w}x{h}");
            Assert.True(r.Bottom > 0);
        }

        Assert.True(lay.PilotY > lay.LastRowBottom, "pilot field must be below the last button");
        Assert.True(lay.FooterY > lay.PilotY, "footer must be below the pilot field");
        Assert.True(lay.FooterY < h, "footer inside viewport");
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Rows_are_centred_and_uniform(int w, int h)
    {
        var lay = IntroLayout.Compute(w, h, IntroScreenModel.MainLabels.Length);
        float expectedLeft = w / 2f - IntroLayout.BoxWidth / 2f;
        for (int i = 0; i < lay.Rows.Length; i++)
        {
            Assert.Equal(expectedLeft, lay.Rows[i].X, 3);
            Assert.Equal(lay.Rows[0].Width, lay.Rows[i].Width, 3);
            if (i > 0)
            {
                // Next row starts just after the previous one ends (a small
                // visual gap, never an overlap).
                Assert.True(lay.Rows[i].Y >= lay.Rows[i - 1].Bottom,
                    $"row {i} overlaps row {i - 1}");
                Assert.True(lay.Rows[i].Y - lay.Rows[i - 1].Bottom < lay.RowHeight,
                    $"rows {i - 1},{i} spaced too far apart");
            }
        }
    }

    // ── P946 — the hint line, the pilot field and short viewports ──────────

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Identity_line_never_overlaps_the_button_column(int w, int h)
    {
        var lay = IntroLayout.Compute(w, h, IntroScreenModel.MainLabels.Length);
        if (lay.IdentityY <= IntroLayout.CompactIdentityY + 0.01f)
        {
            // Compressed column: the status line sits above the first row.
            Assert.True(lay.IdentityY + 19f <= lay.Rows[0].Y,
                $"status line runs into row 0 in {w}x{h}");
        }
        else
        {
            Assert.True(lay.IdentityY >= lay.LastRowBottom,
                $"identity line {lay.IdentityY} inside the column (bottom {lay.LastRowBottom}) in {w}x{h}");
            Assert.True(lay.IdentityY + 19f <= lay.PilotY - 6f,
                $"identity line {lay.IdentityY} runs into the pilot field in {w}x{h}");
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]
    public void Design_pitch_is_kept_on_720p_and_taller(int w, int h)
    {
        var lay = IntroLayout.Compute(w, h, IntroScreenModel.MainLabels.Length);
        Assert.Equal(IntroLayout.RowHeight, lay.RowHeight, 3);
        Assert.Equal(1f, lay.RowTextScale, 3);
    }

    [Fact]
    public void Short_viewport_compresses_the_column_so_menu_and_hint_fit()
    {
        // SDL's offscreen surface is 800x480; 13 rows at the 34 px pitch would
        // run off the bottom. P946 compresses them and moves the status line
        // under the subtitle.
        var lay = IntroLayout.Compute(800, 480, IntroScreenModel.MainLabels.Length);
        Assert.True(lay.RowHeight < IntroLayout.RowHeight);
        Assert.True(lay.RowTextScale < 1f);
        Assert.True(lay.LastRowBottom < lay.PilotY);
        Assert.True(lay.IdentityY + 19f <= lay.Rows[0].Y, "status line must end above the first row");
        Assert.True(lay.FooterY < 480f);
    }

    [Fact]
    public void Pilot_field_box_is_centred_between_the_menu_and_the_hint()
    {
        for (int h = 480; h <= 1440; h += 240)
        {
            var lay = IntroLayout.Compute(1280, h, IntroScreenModel.MainLabels.Length);
            var field = IntroLayout.PilotField(1280, h);
            Assert.Equal(1280f / 2f, field.CenterX, 3);
            Assert.True(field.Y >= lay.LastRowBottom, "pilot field must start below the column");
            Assert.True(field.Bottom <= lay.FooterY, "pilot field must end above the hint line");
        }
    }
}
