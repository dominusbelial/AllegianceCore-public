using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// MSL-1 — the batch-16 482 s sector stall. An unlocked missile (lock 0 ⇒ the
/// lead solve sees dv = −Velocity) that overshot its target had its lead point
/// directly behind it; the port's slerp heading (old Missile.RotateToward)
/// blew |Forward| up (exactly opposite → 2.125, then ×~4e5 per tick), velocity
/// exploded, and MissileIntercept.Solve's native unit-step late search
/// (missileigc.cpp:339-341) ran ~|dv|·√2/a iterations — effectively forever.
/// The fix steers through native <c>Orientation::TurnTo(target, maxTurn)</c>
/// (orientation.cpp:233-304) and bounds the late search (non-native guard).
/// </summary>
public class Msl1MissileHeadingStallTests
{
    // ---------- (b) Orientation.TurnTo unit tests ----------

    private static float AngleBetween(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(a.Normalize() * b.Normalize(), -1f, 1f));

    private static void AssertOrthonormal(Orientation o)
    {
        Assert.InRange(o.GetForward().Length(), 1f - 1e-5f, 1f + 1e-5f);
        Assert.InRange(o.GetUp().Length(), 1f - 1e-5f, 1f + 1e-5f);
        Assert.InRange(o.GetRight().Length(), 1f - 1e-5f, 1f + 1e-5f);
        Assert.InRange(MathF.Abs(o.GetForward() * o.GetUp()), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(o.GetForward() * o.GetRight()), 0f, 1e-5f);
    }

    [Theory]
    [InlineData(0f)]        // exactly opposite: native dcos <= -1 → angle = pi, plain Yaw
    [InlineData(1e-4f)]     // the incident regime: old slerp gave |fwd| ≈ 114
    [InlineData(1e-2f)]     // still dcos < -0.999 → Yaw(maxTurn)
    public void TurnTo_near_opposite_yaws_by_maxTurn_and_stays_unit(float offset)
    {
        var o = Orientation.Identity;                       // forward (0,0,-1)
        var target = new Vector3(offset, 0f, 1f) * 37f;     // ~directly behind, arbitrary length
        float maxTurn = 0.1f;
        float before = AngleBetween(o.GetForward(), target);
        float remaining = o.TurnTo(target, maxTurn);
        AssertOrthonormal(o);
        Assert.Equal(before - maxTurn, remaining, 3);
        // Yaw(maxTurn) moves the nose exactly maxTurn off its old line.
        Assert.Equal(maxTurn, AngleBetween(o.GetForward(), new Vector3(0f, 0f, -1f)), 4);
    }

    [Fact]
    public void TurnTo_wide_angle_twists_toward_target_by_maxTurn()
    {
        var o = Orientation.Identity;
        var target = new Vector3(1f, 0.5f, 0.2f);           // ~well off the nose (cos² < 0.96)
        float maxTurn = 0.2f;
        float before = AngleBetween(o.GetForward(), target);
        float remaining = o.TurnTo(target, maxTurn);
        AssertOrthonormal(o);
        Assert.Equal(before - maxTurn, remaining, 4);
        Assert.Equal(before - maxTurn, AngleBetween(o.GetForward(), target), 3);
    }

    [Fact]
    public void TurnTo_small_angle_uses_yaw_pitch_and_closes_by_maxTurn()
    {
        var o = Orientation.Identity;
        var target = new Vector3(0.06f, -0.04f, -1f);       // ~4° off the nose
        float maxTurn = 0.02f;
        float before = AngleBetween(o.GetForward(), target);
        float remaining = o.TurnTo(target, maxTurn);
        AssertOrthonormal(o);
        Assert.True(remaining > 0f);
        Assert.Equal(before - maxTurn, AngleBetween(o.GetForward(), target), 3);
    }

    [Fact]
    public void TurnTo_within_maxTurn_snaps_onto_target()
    {
        var o = Orientation.Identity;
        var target = new Vector3(0.3f, 0.1f, -1f) * 5f;
        Assert.Equal(0f, o.TurnTo(target, 1f));
        AssertOrthonormal(o);
        Assert.InRange(AngleBetween(o.GetForward(), target), 0f, 1e-3f);
    }

    [Fact]
    public void TurnTo_repeated_from_behind_converges_and_stays_unit()
    {
        var o = Orientation.Identity;
        var target = new Vector3(0f, 0f, 1f);
        for (int i = 0; i < 200; i++)
        {
            o.TurnTo(target, 0.05f);
            AssertOrthonormal(o);
        }
        Assert.InRange(AngleBetween(o.GetForward(), target), 0f, 1e-3f);
    }

    // ---------- (c) bounded intercept late search ----------

    /// <summary>The literal native late search (missileigc.cpp:335-341) for
    /// cases small enough to run — the bounded Solve must equal it exactly.</summary>
    private static double NativeLateStart(double b, double c, double d, double guess, double inflection)
    {
        double late = guess;
        if (late < inflection) late = inflection + 1;
        while (b + 2 * c * late - 4 * d * late * late * late > -.001) late += 1;
        return late;
    }

    [Fact]
    public void Solve_with_huge_dv_returns_quickly()
    {
        // |dv| = 1e12 at a = 100: the native unit-step search would need
        // ~1.4e10 iterations (minutes); at the incident's 1e15+ it never ends.
        var dp = new Vector3(30f, -20f, 10f);
        foreach (var speed in new[] { 1e9f, 1e12f, 1e15f, 1e18f })
        {
            var dv = new Vector3(0.6f, 0.0f, 0.8f) * -speed;
            float result = float.NaN;
            Assert.True(RunBounded(() => result = MissileIntercept.Solve(dp, dv, 100f, 1f), TimeSpan.FromSeconds(5)),
                $"Solve did not return within 5 s for |dv|={speed:G3}");
            Assert.True(float.IsFinite(result) && result > 0f, $"bad result {result} for |dv|={speed:G3}");
        }
    }

    [Fact]
    public void Solve_bounded_late_search_matches_native_in_normal_cases()
    {
        // The cap only engages after MissileIntercept.LateStepCap literal steps,
        // so every result whose native search finishes sooner is bit-identical.
        var rnd = new Random(1234);
        int checkedLate = 0;
        for (int i = 0; i < 20000; i++)
        {
            Vector3 R(float s) => new((float)(rnd.NextDouble() * 2 - 1) * s, (float)(rnd.NextDouble() * 2 - 1) * s, (float)(rnd.NextDouble() * 2 - 1) * s);
            var dp = R(2000f); var dv = R(400f);
            float accel = 20f + (float)rnd.NextDouble() * 200f, guess = (float)rnd.NextDouble() * 20f;
            double a = dp * dp, c = dv * dv, d = accel * accel / 4.0, b = 2.0 * (dp * dv);
            if (c == 0) continue;
            double inflection = Math.Sqrt(c / (6 * d));
            double late = NativeLateStart(b, c, d, guess, inflection);
            Assert.True(late - Math.Max(guess, inflection + 1) < MissileIntercept.LateStepCap);
            checkedLate++;
            // Same inputs through the real Solve: it must not throw / hang and
            // must agree with a fully-native reference Solve.
            Assert.Equal(NativeSolve(dp, dv, accel, guess), MissileIntercept.Solve(dp, dv, accel, guess));
        }
        Assert.True(checkedLate > 1000);
    }

    // Pre-MSL-1 Solve verbatim (native missileigc.cpp:171-352) for equality checks.
    private static float NativeSolve(Vector3 dp, Vector3 dv, float acceleration, float guess)
    {
        static double Newton(double a, double b, double c, double d, double guess, double? inflection = null)
        {
            double t = guess;
            for (int cycle = 0; cycle < 20; cycle++)
            {
                double t2 = t * t, t3 = t2 * t;
                double f = a + b * t + c * t2 - d * t3 * t;
                if (Math.Abs(f) < .1) return t;
                double slope = b + 2 * c * t - 4 * d * t3;
                if (inflection.HasValue && slope >= 0) return -1;
                double next = t - f / slope;
                if (inflection.HasValue && next > inflection.Value) return -1;
                if (cycle == 19) return t;
                t = next;
            }
            return t;
        }
        double a = dp * dp, c = dv * dv;
        double d = acceleration * acceleration / 4.0;
        if (c == 0) return (float)Math.Pow(a / d, .25);
        double b = 2.0 * (dp * dv);
        double inflection = Math.Sqrt(c / (6 * d));
        const double epsilon = -.001;
        double Slope(double t) => b + 2 * c * t - 4 * d * t * t * t;
        if (b + (4.0 / 3.0) * c * inflection <= epsilon) return (float)Newton(a, b, c, d, guess);
        if (b < 0)
        {
            double early = guess;
            if (early < inflection && Slope(early) > epsilon) early = 0;
            early = Newton(a, b, c, d, early, inflection);
            if (early >= 0) return (float)early;
        }
        return (float)Newton(a, b, c, d, NativeLateStart(b, c, d, guess, inflection));
    }

    /// <summary>Run <paramref name="body"/> on a background thread so a
    /// regression that hangs (the pre-fix behaviour) fails the test instead of
    /// the run; rethrows the body's exception.</summary>
    private static bool RunBounded(Action body, TimeSpan limit)
    {
        Exception? error = null;
        var th = new Thread(() => { try { body(); } catch (Exception e) { error = e; } }) { IsBackground = true };
        th.Start();
        if (!th.Join(limit)) return false;
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return true;
    }

    // ---------- (a) the incident: unlocked missile with its target behind ----------

    private static readonly Lazy<(StaticCore Core, MissionMap Map)> Corpus1 = new(() =>
    {
        StaticCore? core = null; MissionMap? map = null;
        foreach (var path in Corpus.Files)
        {
            IgcFile f;
            try { f = IgcReader.ParseFile(path); } catch { continue; }
            if (core is null && f.Wrapper == IgcWrapper.VersionAndDataSize)
            {
                var c = StaticCore.Load(f);
                if (c.MissileTypes.Values.Any(m => m.Acceleration > 0f && m.TurnRate > 0f)) core = c;
            }
            else if (map is null && f.Wrapper == IgcWrapper.DataSize)
            {
                var m = MissionMap.Load(f);
                if (m.Clusters.Count > 0) map = m;
            }
            if (core is not null && map is not null) return (core, map);
        }
        throw new InvalidOperationException("corpus lacks a homing missile core or a map");
    });

    [SkippableTheory]
    [InlineData(0f)]        // target exactly behind the missile's line
    [InlineData(1e-4f)]     // the incident regime (lateral offset ~1e-4)
    [InlineData(1e-2f)]
    public void Unlocked_missile_with_target_behind_keeps_unit_heading_and_fast_ticks(float lateral)
    {
        Require.Artwork();
        var (core, map) = Corpus1.Value;
        var mType = core.MissileTypes.Values.Where(m => m.Acceleration > 0f && m.TurnRate > 0f)
            .OrderBy(m => m.ExpendableTypeId).First() with { ReadyTime = 0f, Lifespan = 30f };
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "T", civ.CivilizationId)], new NullIgcSite());
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];

        // The incident: missile 21652 (lock 0.000) flew past target 9351 —
        // the target now sits directly behind, the missile moving away.
        var anchor = new Vector3(2695.0867f, 2009.8292f, -27.253506f);
        var fwd = new Vector3(-0.3909607f, -0.9154067f, -0.095814645f).Normalize();
        var side = Vector3.CrossProduct(fwd, new Vector3(0f, 0f, 1f)).Normalize();
        var target = new Ship(9351, core.HullTypes.Values.First(), anchor, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(target, sector);
        var missile = new Missile(21652, sector, mType, anchor + fwd * 40f + side * lateral,
            fwd * 126f, fwd, mission.LastUpdate, target: target, lockFraction: 0f);
        sector.Add(missile);

        double t = mission.LastUpdate;
        var worst = TimeSpan.Zero;
        bool finished = RunBounded(() =>
        {
            var sw = new Stopwatch();
            for (int i = 0; i < 200 && sector.Models.Contains(missile); i++)
            {
                target.Position = anchor; target.Velocity = Vector3.Zero;
                sw.Restart(); t += 0.05; mission.Update(t); sw.Stop();
                if (sw.Elapsed > worst) worst = sw.Elapsed;
                float len = missile.Forward.Length();
                if (MathF.Abs(len - 1f) > 1e-4f)
                    throw new Xunit.Sdk.XunitException($"tick {i}: |Forward| = {len:G6} (must stay unit)");
                if (!(missile.Velocity.Length() < mType.InitialSpeed + 126f + mType.Acceleration * 10.1f))
                    throw new Xunit.Sdk.XunitException($"tick {i}: |Velocity| = {missile.Velocity.Length():G6} exceeds the kinematic bound");
            }
        }, TimeSpan.FromSeconds(30));
        Assert.True(finished, "mission.Update stalled (> 30 s for 200 ticks)");
        Assert.True(worst < TimeSpan.FromSeconds(1), $"slowest single tick {worst.TotalMilliseconds:F1} ms");
    }
}
