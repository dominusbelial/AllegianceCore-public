using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Scene.Tests;

// Compiled against both the desktop MonoGame and browser KNI scene helpers.
public sealed class P1066NativeSceneTests
{
    [Fact]
    public void Cluster_light_switches_every_component_and_unknown_cluster_uses_desktop_fallback()
    {
        var cluster = default(DataClusterIGC) with { LightDirection = [3, 0, 4], LightColor = 0xFF804020 };
        var light = NativeScene.Light(cluster);
        Assert.Equal(SectorLighting.FromCluster(cluster.LightDirection, cluster.LightColor), light);
        Assert.Equal(new SimVec(-.6f, 0, -.8f), light.KeyDirection);
        Assert.Equal(new SimVec(128 / 255f, 64 / 255f, 32 / 255f), light.KeyColor);
        Assert.Equal(new SimVec(0, .5f, 1), light.AltColor);
        Assert.Equal(new SimVec(.25f, .25f, .25f), light.Ambient);
        Assert.Equal(SectorLighting.FromCluster(null, 0xFFFFFFFF), NativeScene.Light(null));
        Assert.NotEqual(light, NativeScene.Light(cluster with { LightDirection = [0, 1, 0], LightColor = 0xFF0080FF }));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(.05f, .5f)]
    [InlineData(.10f, 1f)]
    [InlineData(.125f, .5f)]
    [InlineData(.15f, 0f)]
    [InlineData(.5f, 0f)]
    public void Strobe_envelope_matches_native_ramp_size_and_constant_color(float time, float pulse)
    {
        var output = new List<NavStrobeView>();
        var anchor = new NavStrobeAnchor(new(2, 3, 4), NavStrobeTiming.Native, .8f, .2f, .1f);
        NativeScene.AppendStrobe(anchor, 10, time, output);
        Assert.Equal(pulse, NavStrobeMath.Pulse(time, anchor.Timing), 5);
        if (pulse == 0) Assert.Empty(output);
        else
        {
            var view = Assert.Single(output);
            Assert.Equal(pulse * 10 * NavStrobeLayout.SizeFactor, view.HalfExtent, 5);
            Assert.Equal(new Vector3(.8f, .2f, .1f), view.Rgb);
            Assert.Equal(anchor.Position, view.Position);
        }
    }

    [Fact]
    public void Shared_ship_strobes_are_bit_exact_to_desktop_layout_across_full_periods_and_orientation()
    {
        var orientation = new Orientation(); orientation.Set(new SimVec(1, 2, -3));
        var position = new SimVec(4, 5, 6);
        var anchors = NavStrobeLayout.ShipAnchors(position, orientation, 17);
        var expected = new List<NavStrobeView>(); var actual = new List<NavStrobeView>();
        for (int i = 0; i < 1000; i++)
        {
            float seconds = i * .007f;
            expected.Clear(); actual.Clear();
            foreach (var anchor in anchors)
            {
                float pulse = NavStrobeMath.Pulse(seconds, anchor.Timing);
                if (pulse > 0) expected.Add(new(anchor.Position, pulse * 17 * NavStrobeLayout.SizeFactor, new(anchor.R, anchor.G, anchor.B)));
            }
            NativeScene.AppendShipStrobes(position, orientation, 17, true, seconds, actual);
            Assert.Equal(expected, actual);
        }
        actual.Clear();
        NativeScene.AppendShipStrobes(position, orientation, 17, false, .1f, actual);
        NativeScene.AppendShipStrobes(position, orientation, 0, true, .1f, actual);
        Assert.Empty(actual);
    }

    [Fact]
    public void Cloak_opacity_uses_the_desktop_shimmer_and_never_exposes_a_fully_solid_cloaked_hull()
    {
        for (int i = 0; i < 100; i++)
        {
            double seconds = i * .13;
            Assert.Equal(1, NativeScene.Opacity(false, seconds));
            Assert.Equal(CloakVisual.Opacity(seconds), NativeScene.Opacity(true, seconds));
            Assert.InRange(NativeScene.Opacity(true, seconds), .275f, .365f);
        }
    }

    [Fact]
    public void Dust_uses_the_same_default_shell_and_browser_culling_only_removes_invisible_grains()
    {
        var options = new VisualOptions(); var dust = new SubtleDust(); var debris = new DebrisGeo(1, new RandomTrailSource(1));
        var all = new List<AmbientDustView>(); var visible = new List<AmbientDustView>();
        var projection = Matrix.CreatePerspectiveFieldOfView(1, 1.5f, 1, 10000);
        var frustum = new BoundingFrustum(projection);
        NativeScene.GatherDust(options, dust, debris, default, Vector3.Zero, Matrix.Identity, projection, 0, all);
        NativeScene.GatherDust(options, dust, debris, default, Vector3.Zero, Matrix.Identity, projection, 0, visible, frustum);
        Assert.Equal(SubtleDust.Budget(1), all.Count);
        Assert.NotEmpty(visible); Assert.True(visible.Count < all.Count);
        Assert.All(visible, v => Assert.Contains(v, all));
    }

    [Fact]
    public void Warm_shared_light_strobes_and_dust_preparation_allocate_zero_bytes()
    {
        var options = new VisualOptions(); var dust = new SubtleDust(); var debris = new DebrisGeo(1, new RandomTrailSource(1));
        var output = new List<AmbientDustView>(300); var strobes = new List<NavStrobeView>(3);
        var orientation = Orientation.Identity; var projection = Matrix.CreatePerspectiveFieldOfView(1, 1.5f, 1, 10000);
        for (int i = 0; i < 100; i++) Prepare();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Prepare();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        void Prepare()
        {
            _ = NativeScene.Light(null);
            strobes.Clear(); NativeScene.AppendShipStrobes(SimVec.Zero, orientation, 12, true, .1f, strobes);
            NativeScene.GatherDust(options, dust, debris, default, Vector3.Zero, Matrix.Identity, projection, 0, output);
        }
    }
}
