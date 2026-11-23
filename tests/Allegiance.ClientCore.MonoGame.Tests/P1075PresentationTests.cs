using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1075PresentationTests
{
    [Theory]
    [InlineData(HudStyle.Classic, true, 0f, false)]
    [InlineData(HudStyle.Modern, true, 0f, false)]
    [InlineData(HudStyle.Classic, true, .6f, true)]
    [InlineData(HudStyle.Modern, false, .1f, true)]
    public void Afterburner_readout_uses_actual_power_in_both_styles(HudStyle style, bool button, float power, bool active)
    {
        _ = new HudSettings { Style = style };
        var frame = new HudFrame(1, 1, 1, 1, 1, power, 0, -1, button, false, true, 0,
            false, 0, false, false, 0, 0, [], false, 0);
        Assert.Equal(active, HudModel.Create(frame).Telemetry!.Value.AfterburnerActive);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(800, 480)]
    [InlineData(800, 600)]
    public void Supplemental_hangar_cells_are_inside_art_without_covering_native_hotspots(int width, int height)
    {
        var art = HangarScreen.ScreenTransform.For(width, height).Map(0, 0, 800, 600);
        foreach (bool second in new[] { false, true })
        {
            var cell = HangarScreen.PortVerbRect(width, height, second);
            Assert.True(art.Contains(cell));
            Assert.DoesNotContain(HangarScreen.Controls, c => cell.Intersects(HangarScreen.Rect(width, height, c)));
            Assert.False(cell.Intersects(HangarScreen.HoverTextRect(width, height)));
            Assert.False(cell.Intersects(HangarScreen.InTurretRect(width, height)));
            Assert.Equal(second ? HangarAction.Default : HangarAction.Buy,
                HangarScreen.Hit(width, height, cell.Center.X, cell.Center.Y, true));
        }
        Assert.False(HangarScreen.PortVerbRect(width, height, false).Intersects(HangarScreen.PortVerbRect(width, height, true)));
    }

    [Fact]
    public void Help_footer_names_the_platform_navigation()
    {
        if (typeof(HudModel).Assembly.GetName().Name!.EndsWith(".Browser", StringComparison.Ordinal))
        {
        Assert.Contains("LEFT/RIGHT or PGUP/PGDN pages", HelpOverlayRenderer.NavigationFooter);
        Assert.Contains("UP/DOWN scroll", HelpOverlayRenderer.NavigationFooter);
        }
        else
        {
        Assert.Contains("UP/DOWN topics", HelpOverlayRenderer.NavigationFooter);
        Assert.Contains("PGUP/PGDN scroll", HelpOverlayRenderer.NavigationFooter);
        }
    }
}
