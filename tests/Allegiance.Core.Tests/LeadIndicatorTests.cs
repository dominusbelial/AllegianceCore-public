using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P266 — deterministic unit tests for the two pure pieces the predictive lead
/// indicator is built from: <see cref="Ship.LeadHitProbability"/> (the green→red
/// hit-probability ramp, ported from native <c>indicator.cpp:341-357</c>) and the
/// public <see cref="Ship.SolveLead"/> wrapper over P169's faithful
/// <c>solveForLead</c> port (<c>common.cpp:121-149</c>). The ramp is pure; the
/// <c>SolveLead</c> test is corpus-geometric (real hull/weapon/projectile) but
/// runs NO live server, so both are fully deterministic. The full server → client
/// stack is exercised separately by the live visual-proof harness and the wire
/// codec is covered in <c>Wire.LeadIndicatorUpdateWireTests</c>.
/// </summary>
public class LeadIndicatorTests
{
    private static Vector3 Fwd => new(1f, 0f, 0f);

    // ── LeadHitProbability: native indicator.cpp:341-357 piecewise ───────────
    // g = (f<1) ? 1-0.75f : (f<2) ? 0.5-f/4 : 0, with f = (θ/angularRadius)².

    private static float RampAt(float f)
    {
        float angRadius = 0.010f;
        float theta = MathF.Sqrt(f) * angRadius;
        var dir = new Vector3(MathF.Cos(theta), MathF.Sin(theta), 0f);
        return Ship.LeadHitProbability(Fwd, dir, angRadius);
    }

    [Fact]
    public void LeadHitProbability_zero_deviation_is_full_green()
    {
        // f = 0 → branch 1: g = 1 - 0.75*0 = 1 (perfectly-aligned aim = pure green).
        Assert.Equal(1.0f, RampAt(0f), 4);
    }

    [Fact]
    public void LeadHitProbability_first_branch_is_green_dominant()
    {
        // f = 0.25 (< 1) → g = 1 - 0.75*0.25 = 0.8125 (indicator.cpp:344-345).
        // Assert a range: the acos-reconstructed angle carries ~1e-3 float noise
        // that this 0.75× slope amplifies, so pin it as "solidly green-dominant"
        // rather than an exact 0.8125.
        Assert.InRange(RampAt(0.25f), 0.79f, 0.83f);
    }

    [Fact]
    public void LeadHitProbability_exactly_one_radius_is_second_branch()
    {
        // f = 1 → branch 2: g = 0.5 - 1/4 = 0.25 (indicator.cpp:346).
        Assert.Equal(0.25f, RampAt(1f), 3);
    }

    [Fact]
    public void LeadHitProbability_between_one_and_two_radii_is_amber()
    {
        // f = 1.5 → g = 0.5 - 1.5/4 = 0.125 (indicator.cpp:346).
        Assert.InRange(RampAt(1.5f), 0.11f, 0.14f);
    }

    [Fact]
    public void LeadHitProbability_beyond_two_radii_is_red()
    {
        // f well past the f>=2 boundary → branch 3, g = 0 (indicator.cpp:347-348).
        // f=4/9 (not the f=2 boundary) so float acos-noise can't dip below 2.
        Assert.Equal(0f, RampAt(4f), 3);
        Assert.Equal(0f, RampAt(9f), 3);
    }

    [Fact]
    public void LeadHitProbability_zero_direction_is_not_green()
    {
        Assert.Equal(0f, Ship.LeadHitProbability(Fwd, Vector3.Zero, 0.004f));
    }

    // ── SolveLead wrapper: faithful intercept solve on a moving target ───────

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    /// <summary>First fightable (dispersion-0, power>0) hull+weapon+projectile in
    /// the representative core — any sane projectile works for the geometry check.</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC proj) FirstFightable(StaticCore core)
    {
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p, pt);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SolveLead_swings_toward_a_laterally_moving_target_and_intercepts()
    {
        Require.Artwork();
        var core = Core();
        var (hull, weaponType, proj) = FirstFightable(core);

        // Target at the origin drifting +Y at 150 u/s; shooter 500 units out on the
        // −X axis, stationary. Raw line-of-sight is exactly +X; a real lead must
        // swing toward +Y so the shot meets the target as it crosses.
        var shooter = new Ship((short)1, hull, new Vector3(-500f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        var target = new Ship((short)2, hull, Vector3.Zero, new Vector3(0f, 150f, 0f), Orientation.Identity);
        var weapon = new Weapon(shooter, weaponType, proj, 0);

        float t = shooter.SolveLead(target, weapon, skill: 1.0f, out Vector3 dir);

        var raw = target.Position - shooter.Position; raw /= raw.Length();   // +X
        Console.WriteLine($"P266-GEO: t={t:F3}, projSpeed={proj.Speed:F1}, projAbs={proj.AbsoluteF}, dir=({dir.X:F3},{dir.Y:F3},{dir.Z:F3})");

        // (a) The lead must swing toward the target's lateral motion.
        Assert.True(dir.Y > 0.030f,
            $"lead direction must swing toward the target's lateral motion: dir.Y={dir.Y:F4}");
        // (b) It must differ measurably from the raw line-of-sight.
        float dot = dir.X * raw.X + dir.Y * raw.Y + dir.Z * raw.Z;
        Assert.True(dot < 0.9995f,
            $"lead direction must differ from raw LOS: dot={dot:F4}");

        // (c) True intercept: a round fired along `dir` at `proj.Speed` reaches
        //     the target's radius at the solved time (common.cpp:108 semantics).
        Vector3 shotPos = shooter.Position + dir * (proj.Speed * t);
        Vector3 targetFuture = target.Position + target.Velocity * t;
        float miss = (shotPos - targetFuture).Length();
        Assert.True(miss <= hull.Length * 0.5f,
            $"fired round must meet the target's radius: miss={miss:F3} (radius={hull.Length * 0.5f:F3})");
    }
}
