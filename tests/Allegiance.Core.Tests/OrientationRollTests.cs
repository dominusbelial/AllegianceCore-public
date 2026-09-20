using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P298 — <see cref="Orientation.GetRollDegrees"/>: the client-side roll-angle
/// extraction that feeds the new Roll HUD gauge, derived from the real ship
/// attitude (the same orientation this client already tracks for the
/// chase/cockpit cameras). Pure math over <see cref="Orientation"/>; nothing
/// here boots a server.
/// </summary>
public class OrientationRollTests
{
    /// <summary>⭐ Identity attitude (forward (0,0,-1), up (0,1,0)) is level —
    /// roll 0°.</summary>
    [Fact]
    public void Identity_is_level()
    {
        Assert.Equal(0f, Orientation.Identity.GetRollDegrees(), 3);
    }

    /// <summary>⭐ A pure <c>Roll(+θ)</c> on a level ship is recovered exactly —
    /// the gauge reads back the real applied bank. Sweeps both directions and
    /// a steep angle.</summary>
    [Theory]
    [InlineData(10f)]
    [InlineData(30f)]
    [InlineData(75f)]
    [InlineData(150f)]
    [InlineData(-10f)]
    [InlineData(-45f)]
    [InlineData(-170f)]
    public void Roll_is_recovered_from_applied_bank(float theta)
    {
        var o = Orientation.Identity.Copy();
        o.Roll(theta * MathF.PI / 180f);
        Assert.Equal(theta, o.GetRollDegrees(), 1);
    }

    /// <summary>⭐ Yawing or pitching a ship to a level (un-rolled) attitude
    /// still reports ~0° roll — pitch/yaw do not bleed into the roll axis.</summary>
    [Fact]
    public void Pitch_and_yaw_do_not_induce_false_roll()
    {
        var o = Orientation.Identity.Copy();
        o.Yaw(0.6f);
        Assert.Equal(0f, o.GetRollDegrees(), 1);

        o = Orientation.Identity.Copy();
        o.Pitch(0.4f);
        Assert.Equal(0f, o.GetRollDegrees(), 1);

        o = Orientation.Identity.Copy();
        o.Yaw(-0.8f);
        o.Pitch(0.5f);
        Assert.Equal(0f, o.GetRollDegrees(), 1);
    }

    /// <summary>⭐ A real bank applied on top of a pitched attitude is still
    /// recovered (within tolerance) — the extraction is not gimbal-fragile for
    /// typical flight attitudes.</summary>
    [Fact]
    public void Roll_survives_pitched_attitude()
    {
        var o = Orientation.Identity.Copy();
        o.Pitch(0.3f);          // nose up a little
        o.Roll(25f * MathF.PI / 180f);
        Assert.Equal(25f, o.GetRollDegrees(), 2);
    }

    /// <summary>⭐ Vertical climb (forward parallel to world-up) is degenerate —
    /// roll is undefined, so it reports level (0°) rather than NaNs/garbage.</summary>
    [Fact]
    public void Vertical_climb_reports_level()
    {
        var o = Orientation.Identity.Copy();
        o.Set(new Vector3(0f, 1f, 0f));   // straight up
        Assert.Equal(0f, o.GetRollDegrees(), 3);

        o.Set(new Vector3(0f, -1f, 0f));  // straight down
        Assert.Equal(0f, o.GetRollDegrees(), 3);
    }
}
