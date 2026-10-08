using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P580 — tests for the non-native "director" b-roll camera
/// (<see cref="Director"/>): dwell bounds, subject choice favouring interest,
/// sector-change re-acquisition (survives warps/docking), shot-kind cycling, the
/// "any flight input returns control" rule, and seeded repeatability.
/// </summary>
public class DirectorTests
{
    private static readonly IReadOnlyList<DirectorSubject> SomeSubjects = new List<DirectorSubject>
    {
        new(new Vector3(0f, 0f, 0f), SubjectCategory.Explosion, 100f, "boom"),
        new(new Vector3(0f, 0f, 500f), SubjectCategory.Station, 40f, "base"),
        new(new Vector3(500f, 0f, 0f), SubjectCategory.Warp, 30f, "aleph"),
    };

    private static DirectorSnapshot Snap(double sec, bool sectorChanged = false, bool docked = false)
        => new(SomeSubjects, new Vector3(0f, 0f, 0f), sectorChanged, docked);

    [Fact]
    public void ProducesFiniteViewImmediately()
    {
        var d = new Director(seed: 1234);
        var (view, proj, eye, _) = d.Update(1.0 / 60.0, Snap(0), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
        Assert.True(float.IsFinite(eye.X) && float.IsFinite(eye.Y) && float.IsFinite(eye.Z));
        Assert.True(float.IsFinite(view.M11) && float.IsFinite(proj.M11));
    }

    [Fact]
    public void ShotsChangeWithinDwellBounds_AndNotEarlier()
    {
        var d = new Director(seed: 42, minDwell: 4f, maxDwell: 8f);
        // Advance well past maxDwell at fine steps; count shot changes.
        int changes = 0;
        double t = 0.0;
        const double dt = 0.033;
        for (int i = 0; i < 600; i++)   // ~20 simulated seconds
        {
            t += dt;
            var (_, _, _, changed) = d.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
            if (changed) changes++;
        }
        Assert.True(changes >= 2, $"expected several shot changes, got {changes}");
        // Dwell times are 4-8s: over ~20s we should see roughly 3-5 changes.
        Assert.InRange(changes, 2, 6);
    }

    [Fact]
    public void SubjectChoice_FavoursHigherInterest_AcrossManyPicks()
    {
        // With the explosion at interest 100 and others lower, a weighted random
        // pick over many shots should land on the explosion most of the time.
        var d = new Director(seed: 7, minDwell: 0.05f, maxDwell: 0.051f);   // near-immediate re-picks
        var counts = new Dictionary<SubjectCategory, int>
        {
            [SubjectCategory.Explosion] = 0,
            [SubjectCategory.Station] = 0,
            [SubjectCategory.Warp] = 0,
        };
        double t = 0.0;
        const double dt = 0.002;
        for (int i = 0; i < 20000; i++)
        {
            t += dt;
            d.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
            if (d.SubjectIndex >= 0 && d.SubjectIndex < SomeSubjects.Count)
                counts[SomeSubjects[d.SubjectIndex].Category]++;
        }
        int total = counts[SubjectCategory.Explosion] + counts[SubjectCategory.Station] + counts[SubjectCategory.Warp];
        Assert.True(total > 0);
        // Interest 100 vs 40 vs 30 -> explosion should dominate the picks.
        Assert.True(counts[SubjectCategory.Explosion] > counts[SubjectCategory.Station],
            $"expected explosion to out-pick station ({counts[SubjectCategory.Explosion]} vs {counts[SubjectCategory.Station]})");
        Assert.True(counts[SubjectCategory.Station] > 0 && counts[SubjectCategory.Warp] > 0,
            "even low-interest subjects must occasionally win (variety)");
    }

    [Fact]
    public void SectorChange_ForcesReAcquisition()
    {
        var d = new Director(seed: 99, minDwell: 1e6f, maxDwell: 1e6f);   // dwell >> any test span
        // First frame picks a subject (initial).
        var (_, _, _, first) = d.Update(0.001, Snap(1.0), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
        Assert.True(first);
        int before = d.ShotChanges;
        // Many frames with no sector change: no new shot (dwell is effectively infinite).
        for (int i = 0; i < 1000; i++)
            d.Update(0.001, Snap(2.0 + i * 0.001), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
        Assert.Equal(before, d.ShotChanges);
        // A sector change NOW re-acquires a subject immediately.
        var (_, _, _, reacquired) = d.Update(0.001, Snap(200.0, sectorChanged: true), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
        Assert.True(reacquired, "sector change must force a fresh shot pick");
        Assert.True(d.ShotChanges > before);
    }

    [Fact]
    public void Docked_FilmsTheStation()
    {
        var d = new Director(seed: 5);
        d.Update(0.1, Snap(0.1, docked: true), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
        // While docked the director should be framing the station subject.
        Assert.True(d.SubjectIndex >= 0 && d.SubjectIndex < SomeSubjects.Count);
        Assert.Equal(SubjectCategory.Station, SomeSubjects[d.SubjectIndex].Category);
    }

    [Fact]
    public void SameSeed_ProducesSameShotSequence()
    {
        var a = new Director(seed: 31337);
        var b = new Director(seed: 31337);
        var kindsA = new List<ShotKind>();
        var kindsB = new List<ShotKind>();
        double t = 0.0;
        const double dt = 0.1;
        for (int i = 0; i < 300; i++)
        {
            t += dt;
            a.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
            b.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
            kindsA.Add(a.Kind);
            kindsB.Add(b.Kind);
        }
        Assert.Equal(kindsA, kindsB);
    }

    [Fact]
    public void Reseed_ReproducesTheOriginalSequence()
    {
        var original = new Director(seed: 555);
        var reseeded = new Director(seed: 999);
        // Collect the reseeded one's earlier (wrong-seed) sequence partly, then
        // reseed it to match the original and compare.
        double t = 0.0;
        const double dt = 0.1;
        for (int i = 0; i < 100; i++) { t += dt; original.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f); }
        var origKinds = new List<ShotKind>();
        for (int i = 0; i < 100; i++) { t += dt; original.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f); origKinds.Add(original.Kind); }

        reseeded.Reseed(555);
        var reKinds = new List<ShotKind>();
        double u = 0.0;
        for (int i = 0; i < 200; i++) { u += dt; reseeded.Update(dt, Snap((float)u), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f); reKinds.Add(reseeded.Kind); }
        Assert.Equal(origKinds, reKinds.GetRange(100, 100));
    }

    [Fact]
    public void AnyFlightControl_ReturnsTrueWhenAnyControlPressed()
    {
        Assert.True(Director.AnyFlightControl(true, false, false, false, false, false, false, false));
        Assert.True(Director.AnyFlightControl(false, false, false, false, false, true, false, false));
        Assert.False(Director.AnyFlightControl(false, false, false, false, false, false, false, false));
    }

    [Fact]
    public void DoesNotJumpAround_TransitionsSmoothly_NoSnapOnPose()
    {
        // The rendered pose converges toward the target; consecutive frames'
        // eyes should not be wildly separated (no teleport/snap). With a modest
        // step the eye moves smoothly even right after a shot change.
        var d = new Director(seed: 1, minDwell: 0.050f, maxDwell: 0.055f);
        Vector3? prev = null;
        float maxMove = 0f;
        double t = 0.0;
        const double dt = 1.0 / 60.0;
        for (int i = 0; i < 400; i++)
        {
            t += dt;
            var (_, _, eye, _) = d.Update(dt, Snap((float)t), new Vector3(0f, 0f, -1f), 30f, 60f, 16f / 9f);
            if (prev is { } p)
                maxMove = MathF.Max(maxMove, (eye - p).Length());
            prev = eye;
        }
        // With a transition tau of ~1.2s and a 60 Hz step, a single frame's eye
        // movement stays modest even through a re-framing (no hard cut/teleport).
        Assert.True(maxMove < 2000f, $"eye moved {maxMove} in one frame — snapped, not smoothed");
    }
}
