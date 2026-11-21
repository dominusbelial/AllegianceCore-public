using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P1069 B2 — the desktop's P358/P365/P363 preparation was extracted into
/// Rendering/NativeScene without changing a value. This test recomputes the
/// pre-extraction desktop formulas on the desktop compilation and requires
/// bit-identical results for the same inputs.</summary>
public sealed class P1069EngineExtractionTests
{
    [Fact]
    public void Desktop_ramp_rates_keep_their_documented_values()
    {
        Assert.Equal(6f, NativeScene.ThrustPowerOnRate);
        Assert.Equal(3f, NativeScene.ThrustPowerOffRate);
        Assert.Equal(.25f, NativeScene.TrailWidthFraction);
    }

    [Fact]
    public void Thrust_ramp_is_bit_identical_to_the_pre_extraction_desktop_rule()
    {
        // Game1.Rendering.cs UpdateEngineTrail before P1069:
        //   ShipEffectRules.ThrustPower(hasAfterburner, thrusting, previous, dtSec, 6f, 3f)
        foreach (bool mounted in new[] { true, false })
        foreach (bool button in new[] { true, false })
        {
            float previous = 0f;
            for (int i = 0; i < 500; i++)
            {
                float dt = (i % 7 + 1) / 240f;
                float expected = mounted
                    ? button ? Math.Min(1f, previous + 6f * dt) : Math.Max(0f, previous - 3f * dt)
                    : 0f;
                Assert.Equal(expected, NativeScene.ThrustPower(mounted, button, previous, dt));
                previous = expected;
            }
        }
    }

    [Fact]
    public void Nozzle_glow_value_is_bit_identical_to_the_extracted_desktop_rule()
    {
        // UpdateNozzleGlow before P1069: ShipEffectRules.NozzleGlow(hasAfterburner,
        // (stateM & ShipControlState.AfterburnerButton) != 0, throttle).
        foreach (bool mounted in new[] { true, false })
        foreach (float throttle in new[] { -1f, -0.75f, -0.25f, 0f, 0.1f, 0.5f, 1f })
        foreach (int stateM in new[] { 0, ShipControlState.AfterburnerButton, ShipControlState.WeaponsMask })
            Assert.Equal(ShipEffectRules.NozzleGlow(mounted, (stateM & ShipControlState.AfterburnerButton) != 0, throttle),
                NativeScene.NozzleGlow(mounted, stateM, throttle));
    }

    [Fact]
    public void Glow_decal_anchor_tint_and_size_are_bit_identical_to_the_desktop_draw_block()
    {
        var orientation = new Orientation(); orientation.Set(new SimVec(.3f, -.2f, -.9f));
        for (int i = 0; i <= 1000; i++)
        {
            float value = i / 1000f;
            foreach (float radius in new[] { 0.5f, 12f, 90f })
            {
                var position = new SimVec(i, -i, i * .5f);
                // Game1.Rendering.cs glow block before P1069 (no native frame).
                var (nr, ng, nb, scale) = EngineNozzleGlow.Compute(value);
                var backward = orientation.GetBackward();
                var expectedPos = position + (backward * (radius * EngineNozzleGlowRenderer.AftCenterFraction));
                float expectedHalf = scale * radius * EngineNozzleGlowRenderer.GlowSizeFactor;
                var actual = NativeScene.GlowView(position, orientation, radius, value, null);
                Assert.Equal(new EngineGlowView(expectedPos, expectedHalf, new Vector3(nr, ng, nb)), actual);

                // Authored nozzle frame: position + offset, same tint/size.
                var offset = new SimVec(1.5f, -2f, 3f);
                var anchored = NativeScene.GlowView(position, orientation, radius, value, offset);
                Assert.Equal(position + offset, anchored.Position);
                Assert.Equal(actual.HalfExtent, anchored.HalfExtent);
                Assert.Equal(actual.Rgb, anchored.Rgb);
            }
        }
    }

    [Fact]
    public void Motion_trail_right_vector_and_colour_are_bit_identical_to_the_desktop_set_anchor()
    {
        var orientation = new Orientation(); orientation.Set(new SimVec(-.4f, .1f, -.9f));
        var pool = new ShipMotionTrailRibbons();
        for (short id = 1; id <= 20; id++)
        {
            var position = new SimVec(id * 10, id % 3, -id);
            float radius = 4 + id;
            // Game1.Rendering.cs UpdateMotionTrail before P1069:
            //   _motionTrail.SetAnchor(id, position, orientation.GetRight() * (radius * 0.25f), r, g, b)
            NativeScene.AppendMotionTrail(pool, id, position, orientation, radius, .1f, .2f, .3f);
            var strip = pool.Enumerate().SingleOrDefault(s => s.ShipId == id);
            Assert.Equal(default, strip);   // no section advanced yet
        }
        pool.Advance(.6f);
        for (short id = 1; id <= 20; id++)
            NativeScene.AppendMotionTrail(pool, id, new SimVec(id * 10 + 1, id % 3, -id), orientation, 4 + id, .1f, .2f, .3f);
        foreach (var strip in pool.Enumerate())
        {
            float radius = 4 + strip.ShipId;
            Assert.Equal(orientation.GetRight() * (radius * .25f), strip.Rights[0]);
            Assert.Equal((.1f, .2f, .3f), (strip.R, strip.G, strip.B));
            Assert.Equal(1, strip.Sections);
        }
    }

    [Fact]
    public void Aft_nozzle_offset_reproduces_the_pre_extraction_native_transform()
    {
        var frames = new[]
        {
            new MdlFrame { Name = "rocket0", Position = [-0.1f, 0.25f, -2.9f], Forward = [0, 0, 1], Up = [0, 1, 0] },
        };
        var orientation = new Orientation(); orientation.Set(new SimVec(.6f, .2f, -.75f));
        // Game1.Rendering.cs NativeAftWorldOffset before P1069.
        var local = NativeShipResolver.AftNozzleLocalOffset(frames, 0f);
        var flipped = new Vector3(-local.X, local.Y, -local.Z) * 3.5f;
        var x = Vector3.Transform(flipped, PoseConverter.ToXnaRotation(orientation));
        var expected = new SimVec(x.X, x.Y, x.Z);
        Assert.Equal(expected, NativeScene.AftWorldOffset(frames, 3.5f, orientation));
    }
}
