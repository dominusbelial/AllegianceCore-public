using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Collision;
using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P915ShipEffectsTests
{
    [Theory]
    [InlineData(4, 13.4f, 1f / 3f)] // PCore001 utl100 lifepod
    [InlineData(13, 24.7f, 1)]      // PCore001 wc_bifig fighter
    [InlineData(200, 380, 1)]      // capital hull
    [InlineData(4, 6, .25f)]       // close camera limits size independently
    [InlineData(4, 100, 1f / 3f)] // distant camera still respects small hull
    public void Own_external_smoke_uses_real_length_and_camera_distance(float length, float distance, float expected)
        => Assert.Equal(expected, ShipEffectRules.DamageSmokeScale(true, true, length, distance), 5);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Other_ships_and_cockpit_keep_native_size(bool own, bool external)
        => Assert.Equal(1, ShipEffectRules.DamageSmokeScale(own, external, 4, 13));

    [Theory]
    [InlineData(0, 13)]
    [InlineData(4, 0)]
    [InlineData(float.NaN, 13)]
    [InlineData(4, float.PositiveInfinity)]
    public void Unknown_dimensions_keep_native_size(float length, float distance)
        => Assert.Equal(1, ShipEffectRules.DamageSmokeScale(true, true, length, distance));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(.91f, 0)]
    [InlineData(.9f, 1)]
    [InlineData(.8f, 1)] // native float truncation: (1-.8f)*10 is below 2
    [InlineData(.45f, 5)]
    [InlineData(.1f, 9)]
    [InlineData(0, 0)]
    public void Scaling_preserves_thresholds_vent_counts_and_lifetime(float fraction, int expected)
    {
        int vents = DamageDebrisTracker.DamageFrameCount(fraction);
        Assert.Equal(expected, vents);
        var native = new DamageDebrisParticles();
        var scaled = new DamageDebrisParticles();
        native.Emit(Vector3.Zero, Vector3.Zero, Orientation.Identity, 2, 7, vents, .02f, new MidpointRandom());
        scaled.Emit(Vector3.Zero, Vector3.Zero, Orientation.Identity, 2, 7, vents, .02f, new MidpointRandom(), 1f / 3f);
        Assert.Equal(vents, native.ActiveCount);
        Assert.Equal(native.ActiveCount, scaled.ActiveCount);
        var original = native.Enumerate().ToArray();
        var smaller = scaled.Enumerate().ToArray();
        for (int i = 0; i < vents; i++)
        {
            Assert.Equal(original[i].Size / 3, smaller[i].Size, 5);
            Assert.Equal(original[i].RemainingFrac, smaller[i].RemainingFrac);
            Assert.Equal(original[i].Velocity.X / 3, smaller[i].Velocity.X, 5);
        }
        native.Update(DamageDebrisParticles.DurationSeconds);
        scaled.Update(DamageDebrisParticles.DurationSeconds);
        Assert.Equal(0, native.ActiveCount);
        Assert.Equal(0, scaled.ActiveCount);
    }

    [Theory]
    [InlineData(true, false, false, false)] // owner has no part
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]   // stale loadout after ejection
    [InlineData(false, false, true, false)] // remote pod
    [InlineData(false, false, false, true)] // remote loadout unavailable
    public void Afterburner_gate_respects_available_client_knowledge(bool own, bool mounted, bool pod, bool expected)
        => Assert.Equal(expected, ShipEffectRules.HasAfterburner(own, mounted, pod));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_afterburner_clears_ramp_even_with_button_and_previous_power(bool button)
    {
        Assert.Equal(0, ShipEffectRules.ThrustPower(false, button, 1, .02f, 6, 3));
        Assert.Equal(0, ShipEffectRules.NozzleGlow(false, button, -1));
        Assert.Equal(.5f, ShipEffectRules.NozzleGlow(false, button, 0));
        Assert.Equal(1, ShipEffectRules.NozzleGlow(false, button, 1));
    }

    [Fact]
    public void Mounted_afterburner_keeps_existing_ramp_glow_and_jiggle_gate()
    {
        Assert.Equal(.12f, ShipEffectRules.ThrustPower(true, true, 0, .02f, 6, 3), 5);
        Assert.Equal(.94f, ShipEffectRules.ThrustPower(true, false, 1, .02f, 6, 3), 5);
        Assert.Equal(1, ShipEffectRules.NozzleGlow(true, true, -1));
        var jiggle = new FlightJiggle(new MidpointRandom());
        Assert.Equal((0f, 0f), jiggle.Step(.02f, 1, afterburnerMounted: false));
    }

    private sealed class MidpointRandom : ITrailRandom
    {
        public float Range(float min, float max) => (min + max) / 2;
        public Vector3 Direction() => new Vector3(1, 0, 0);
    }
}
