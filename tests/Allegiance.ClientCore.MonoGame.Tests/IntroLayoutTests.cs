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
}
