using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Native command grid visibility (WinTrek.cpp:7293-7294) and the Debris menu
/// cycle (WinTrek.cpp:4633-4653, labels :5539-5557, default "1.0" :3093).</summary>
public class CommandGridDebrisParityTests
{
    [Theory]
    [InlineData(false, false, false)]   // flight, grid toggled off: hidden
    [InlineData(true, false, true)]     // flight, grid toggled on: grid + drop lines in flight
    [InlineData(false, true, true)]     // command view always shows it, toggle or not
    [InlineData(true, true, true)]
    public void Grid_shows_when_toggled_in_any_camera_and_always_in_command_view(bool showGrid, bool commandCamera, bool expected)
        => Assert.Equal(expected, CommandGridRenderer.IsVisible(showGrid, commandCamera));

    [Fact]
    public void Debris_cycle_matches_native_order_and_labels()
    {
        float d = 1.0f;                                     // native default: Medium
        Assert.Equal("Debris Medium", DebrisGeo.MenuLabel(d));
        d = DebrisGeo.NextDensity(d); Assert.Equal(0.8f, d); Assert.Equal("Debris High", DebrisGeo.MenuLabel(d));
        d = DebrisGeo.NextDensity(d); Assert.Equal(0f, d); Assert.Equal("Debris Off", DebrisGeo.MenuLabel(d));
        d = DebrisGeo.NextDensity(d); Assert.Equal(1.5f, d); Assert.Equal("Debris Low", DebrisGeo.MenuLabel(d));
        d = DebrisGeo.NextDensity(d); Assert.Equal(1.0f, d);
        Assert.Equal(1.5f, DebrisGeo.NextDensity(2.3f));    // a --debris override steps to Low (native's else)
    }

    [Fact]
    public void Debris_preference_defaults_to_native_medium_and_roundtrips()
    {
        Assert.Equal(1.0f, new UpscalingSettings().Debris);
        string dir = Path.Combine(Path.GetTempPath(), "debris-" + Guid.NewGuid());
        string path = Path.Combine(dir, "graphics.json");
        try
        {
            Directory.CreateDirectory(dir);
            var s = new UpscalingSettings { Debris = 0.8f };
            s.Save(path);
            Assert.Equal(0.8f, UpscalingSettings.Load(path).Debris);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
