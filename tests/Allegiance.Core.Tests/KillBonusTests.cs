using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P278 — Kill Bonus: pure, server-free unit tests of the per-ship kill-streak
/// mechanic (native <c>CshipIGC::m_experience</c> + <c>GetExperienceMultiplier</c>,
/// <c>shipIGC.h:1812-1817,2610</c>; <c>AddExperience</c> <c>shipIGC.h:1829</c>;
/// reset on real death <c>FedSrv.CPP:11783-11787</c>). Distinct from
/// <c>PlayerScore.CombatRating</c> (P215). Tests the formula/counters directly
/// on a <see cref="Ship"/> (scope = per-ship, matching native) plus the damage
/// scaling in <see cref="Ship.ReceiveDamage"/> (native <c>shipIGC.cpp:1238-1242</c>).
/// These run in parallel (nothing here boots a server or opens a socket).
/// </summary>
public class KillBonusTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        var file = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    });

    // A high-HP hull so the modest test damage never risks a death that would
    // complicate the measured-drop ratio.
    private static Ship NewShip()
    {
        var hull = RepresentativeCore.Value.HullTypes.Values
            .OrderByDescending(h => h.HitPoints).First();
        return new Ship(9900, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
    }

    /// <summary>⭐ A fresh ship starts at zero bonus (counter 0 → multiplier
    /// 1.0) — the wiki's "KB starts at 0; 10 after your first kill". (Native's
    /// ctor seeds m_experience at 1.0, shipIGC.cpp:52, which would give a brand
    /// new ship an immediate 10% tier — a documented seed divergence; see the
    /// field's SEED DIVERGENCE note. The port matches the wiki/task semantics.)</summary>
    [Fact]
    public void Fresh_ship_starts_at_zero_bonus()
    {
        var ship = NewShip();
        Assert.Equal(0f, ship.KillBonus);
        Assert.Equal(0f, ship.KillBonusPercent);
        Assert.Equal(1f, ship.ExperienceMultiplier, 4);
    }

    /// <summary>⭐ The early climb matches native hand-derivation exactly:
    /// counter c → KB = 50·c/(c+4) → multiplier = 1 + 0.5·c/(c+4). After a
    /// reset (counter 0) the FIRST kill gives counter 1 → KB 10, matching the
    /// task's "1 kill → 10".</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 10f)]
    [InlineData(2f, 16.666666f)]
    [InlineData(3f, 21.428571f)]
    [InlineData(4f, 25f)]
    [InlineData(9f, 34.615385f)]
    public void Kill_bonus_percent_matches_native_formula(float counter, float expectedPercent)
    {
        var ship = NewShip();
        ship.ResetKillBonus();
        for (int i = 0; i < counter; i++) ship.AddKillBonus();

        Assert.Equal(counter, ship.KillBonus);
        Assert.Equal(expectedPercent, ship.KillBonusPercent, 3);
        Assert.Equal(1f + 0.005f * counter * 100f / (counter + 4f), ship.ExperienceMultiplier, 3);
    }

    /// <summary>⭐ Monotonic climb toward — never at — the 50% cap. Every extra
    /// kill strictly increases the multiplier; the KB percent stays ≤ 50 and
    /// the multiplier stays &lt; 1.5 no matter how long the streak.</summary>
    [Fact]
    public void Kill_bonus_climbs_monotonically_and_never_exceeds_the_cap()
    {
        var ship = NewShip();
        ship.ResetKillBonus();
        float prev = -1f;
        for (int i = 0; i < 200; i++)
        {
            Assert.True(ship.KillBonusPercent <= 50f, $"KB {ship.KillBonusPercent} exceeded 50");
            Assert.True(ship.ExperienceMultiplier < 1.5f, "multiplier reached the asymptote");
            Assert.True(ship.ExperienceMultiplier > prev, "multiplier must climb with each kill");
            prev = ship.ExperienceMultiplier;
            ship.AddKillBonus();
        }
        // A long streak approaches the cap from below (50·200/204 ≈ 49.0).
        Assert.InRange(ship.KillBonusPercent, 49f, 50f);
    }

    /// <summary>⭐ Reset clears the streak back to zero bonus (native
    /// <c>SetExperience(0.0f)</c> on a real death, FedSrv.CPP:11783-11787).</summary>
    [Fact]
    public void Reset_clears_the_streak_to_zero_bonus()
    {
        var ship = NewShip();
        ship.AddKillBonus(); ship.AddKillBonus(); ship.AddKillBonus();
        Assert.True(ship.KillBonusPercent > 0f);

        ship.ResetKillBonus();
        Assert.Equal(0f, ship.KillBonus);
        Assert.Equal(0f, ship.KillBonusPercent);
        Assert.Equal(1f, ship.ExperienceMultiplier, 4);
    }

    /// <summary>⭐ The real damage path scales a genuine hit by the LAUNCHER's
    /// Kill Bonus: <see cref="Ship.ReceiveDamage"/> multiplies the incoming
    /// amount by the launcher's <see cref="Ship.ExperienceMultiplier"/>
    /// (native shipIGC.cpp:1238-1242). Same damage amount, same target hull →
    /// the measured hull-fraction drop at a non-zero KB is exactly
    /// ExperienceMultiplier× the drop at KB 0. (A mission-less ship's
    /// DamageConstant is 1.0, constant across both calls, so it cancels in the
    /// ratio.)</summary>
    [Fact]
    public void ReceiveDamage_scales_a_real_hit_by_the_launcher_kill_bonus()
    {
        var hull = RepresentativeCore.Value.HullTypes.Values
            .OrderByDescending(h => h.HitPoints).First();
        float amount = hull.HitPoints * 0.025f;   // modest, no death risk

        float Drop(Ship launcher)
        {
            var t = new Ship(9901, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            float before = t.Fraction;
            t.ReceiveDamage(0, amount, 0.0, Vector3.Zero, Vector3.Zero, launcher);
            return before - t.Fraction;
        }

        // Launcher at KB 0 (post-death / fresh) → multiplier 1.0.
        var launcher0 = NewShip();
        launcher0.ResetKillBonus();

        // Launchers mid-streak: counter 1 (one kill) and 5 (five kills).
        var launcher1 = NewShip();
        launcher1.AddKillBonus();
        var launcher5 = NewShip();
        for (int i = 0; i < 5; i++) launcher5.AddKillBonus();

        float d0 = Drop(launcher0);
        Assert.True(d0 > 0f, "baseline hit must actually damage the hull");

        float d1 = Drop(launcher1);
        float d5 = Drop(launcher5);

        // Measured drop ratio must equal the multiplier ratio exactly.
        Assert.Equal(launcher1.ExperienceMultiplier, d1 / d0, 3);
        Assert.Equal(launcher5.ExperienceMultiplier, d5 / d0, 3);

        // Sanity: more KB ⇒ measurably more damage on the same hit.
        Assert.True(d5 > d1 && d1 > d0, "higher Kill Bonus must deal more measured damage");
    }
}
