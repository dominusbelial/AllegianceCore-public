using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P729BenchmarkProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void View_distance_preserves_camera_fov_aspect_near_plane_and_projection_kind(bool orthographic)
    {
        var original = orthographic ? Matrix.CreateOrthographic(2400, 1600, 10, 6000)
            : Matrix.CreatePerspectiveFieldOfView(1.2f, 1.6f, 10, 6000);
        var expected = orthographic ? Matrix.CreateOrthographic(2400, 1600, 10, 3500)
            : Matrix.CreatePerspectiveFieldOfView(1.2f, 1.6f, 10, 3500);
        var changed = Game1.WithBenchmarkViewDistance(original, 3500);
        Assert.Equal(original.M11, changed.M11); Assert.Equal(original.M22, changed.M22);
        Assert.Equal(original.M34, changed.M34); Assert.Equal(original.M44, changed.M44);
        Assert.InRange(MathF.Abs(expected.M33 - changed.M33), 0, 1e-6f);
        Assert.InRange(MathF.Abs(expected.M43 - changed.M43), 0, 1e-5f);
    }
}
