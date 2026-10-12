using Allegiance.Core.Collision;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P610 — prove the pure bot-fleet-traffic-spacing decision helpers against
/// synthetic geometry (no server):
/// 1. aleph ENTRY STAGGERING: per-bot aim points are deterministic, distinct
///    across a fleet, and lie INSIDE the aleph's capture radius (a uniform-disc
///    sample across the mouth so a big fleet streams through instead of
///    converging on the centre point);
/// 2. aleph EXIT CLEAR-OUT: a bot keeps flying along the exit direction until
///    it is PAST the arrival aleph's radius + margin before regrouping;
/// 3. TRAVEL SPACING: the soft separation term pushes a teammate's goal
///    laterally (never stalling forward progress) and is exactly zero beyond
///    its influence range.
/// </summary>
public class P610AlephTrafficSpacingDecisionTests
{
    // ── 1. Entry staggering: distinct, deterministic, inside the capture radius ──

    [Fact]
    public void EntryAim_DistinctAcrossAFleet_AndInsideCaptureRadius()
    {
        var center = new Vector3(1000f, 2000f, 500f);
        const float radius = 100f;
        const float spreadFraction = 0.7f;
        float maxAllowed = radius * spreadFraction + 1e-3f;

        var pts = new List<Vector3>();
        for (short id = 940; id < 962; id++)   // a 22-bot fleet
            pts.Add(BotManager.EntryAimPoint(center, radius, id, spreadFraction));

        Assert.Equal(22, pts.Count);
        // Distinct: no two bots aim at (nearly) the same point.
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
                Assert.True((pts[i] - pts[j]).Length() > 1e-3f,
                    $"bots {i},{j} must not converge on the same aim point ({pts[i]} vs {pts[j]})");
        // Inside the capture radius (a fraction of it — never outside the disc).
        foreach (var p in pts)
            Assert.True((p - center).Length() <= maxAllowed,
                $"aim point {p} must lie within {maxAllowed:F1} of the aleph centre");
    }

    [Fact]
    public void EntryAim_IsDeterministic_PerShip()
    {
        var center = new Vector3(0f, 0f, 0f);
        var a = BotManager.EntryAimPoint(center, 90f, (short)777, 0.7f);
        var b = BotManager.EntryAimPoint(center, 90f, (short)777, 0.7f);
        Assert.True((a - b).Length() < 1e-4f, "the same ship must always get the same aim point");
    }

    [Fact]
    public void EntryAim_ZeroFraction_AimsAtCentre()
    {
        var center = new Vector3(50f, -20f, 30f);
        var p = BotManager.EntryAimPoint(center, 80f, (short)5, 0f);
        Assert.True((p - center).Length() < 1e-3f, "spread fraction 0 must aim at the aleph centre");
    }

    // ── 2. Exit clear-out: ends once past radius + margin ──

    [Fact]
    public void IsClearOfAleph_FalseInside_TrueBeyond_RadiusPlusMargin()
    {
        var aleph = new Vector3(0f, 0f, 0f);
        const float radius = 100f;
        const float margin = 140f;
        // Just inside the cleared boundary → NOT clear.
        Assert.False(BotManager.IsClearOfAleph(new Vector3(radius + margin - 5f, 0f, 0f), aleph, radius, margin),
            "a ship just inside radius+margin must NOT be considered clear");
        // Exactly on / beyond the boundary → clear.
        Assert.True(BotManager.IsClearOfAleph(new Vector3(radius + margin, 0f, 0f), aleph, radius, margin),
            "a ship at radius+margin IS clear");
        Assert.True(BotManager.IsClearOfAleph(new Vector3(0f, 0f, radius + margin + 60f), aleph, radius, margin),
            "a ship well past radius+margin IS clear");
        // Right at the aleph → definitely NOT clear.
        Assert.False(BotManager.IsClearOfAleph(aleph, aleph, radius, margin),
            "a ship at the aleph centre is not clear");
    }

    [Fact]
    public void ExitClearGoal_LiesAlongExitRay_AndBeyondRadiusPlusMargin()
    {
        var aleph = new Vector3(0f, 0f, 0f);
        const float radius = 100f;
        const float margin = 140f;
        // Emergent ship out along +Z.
        var myPos = new Vector3(0f, 0f, radius + 10f);
        var goal = BotManager.ExitClearGoal(myPos, aleph, radius, margin);
        // The goal stays on the exit ray (through the aleph and myPos).
        var exit = myPos - aleph; exit /= exit.Length();
        var g = goal - aleph; float gl = g.Length(); g /= gl;
        Assert.True((g - exit).Length() < 1e-3f, $"goal {goal} must lie along the exit ray (+Z), got dir {g}");
        // And it is beyond the cleared radius, so pursuing it completes the clear-out.
        Assert.True(gl > radius + margin, $"goal must be beyond radius+margin, got {gl:F1}");
    }

    [Fact]
    public void EntryQueue_YieldsToAMateJustAheadInTheFunnel()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var aleph = new Vector3(0f, 0f, 500f);
        var aim = new Vector3(0f, 0f, 500f);   // this bot's staggered aim on the disc
        // A mate just ahead in the funnel (on my line, near my aim) → yield.
        var mate = (Pos: new Vector3(0f, 0f, 480f), Id: (short)55);
        Assert.True(BotManager.EntryQueueYield(myPos, aim, aleph, alephRadius: 100f,
            new[] { mate }, lookahead: 340f, funnelWidth: 90f),
            "a teammate just ahead in the funnel near my aim must make this bot yield");
    }

    [Fact]
    public void EntryQueue_DoesNotYield_ForBehindOrDistantMates()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var aleph = new Vector3(0f, 0f, 500f);
        var aim = new Vector3(0f, 0f, 500f);
        // A mate BEHIND me → not blocking the funnel ahead.
        Assert.False(BotManager.EntryQueueYield(myPos, aim, aleph, 100f,
            new[] { (Pos: new Vector3(0f, 0f, -50f), Id: (short)56) }, 340f, 90f),
            "a mate behind the bot is not in the funnel ahead");
        // A mate far out of the funnel (well short of the aleph, off my aim) → no yield.
        Assert.False(BotManager.EntryQueueYield(myPos, aim, aleph, 100f,
            new[] { (Pos: new Vector3(0f, 0f, 200f), Id: (short)57) }, 340f, 90f),
            "a mate far from my aim / the aleph is not converging on my point");
        // No mates at all → no yield.
        Assert.False(BotManager.EntryQueueYield(myPos, aim, aleph, 100f,
            Array.Empty<(Vector3, short)>(), 340f, 90f), "no mates → no yield");
    }

    // ── 3. Travel spacing: pushes apart, zero beyond range ──

    [Fact]
    public void TravelSeparation_PushesGoalAwayFromANearbyMate()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var goal = new Vector3(0f, 0f, 500f);        // travelling along +Z
        // A mate off to the +X side within range.
        var mate = (Pos: new Vector3(40f, 0f, 0f), Id: (short)9);
        var sep = BotManager.ApplyTravelSeparation(myPos, goal, new[] { mate }, range: 120f, strength: 60f);
        // Away from the mate (+X) is −X; the deflected goal must have a smaller X than the raw goal.
        Assert.True(sep.X < -1e-3f,
            $"separation must push the goal away from the mate at +X (got sep.X={sep.X:F2})");
        // And it must NOT stall forward progress: the Z (travel) component is preserved.
        Assert.True(MathF.Abs(sep.Z - goal.Z) < 1e-3f,
            "separation must not pull the goal off the travel axis (no forward-component loss)");
    }

    [Fact]
    public void TravelSeparation_IsZero_BeyondRange_And_ForNothingNear()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var goal = new Vector3(0f, 0f, 500f);
        // A mate just beyond range → no deflection.
        var farMate = (Pos: new Vector3(200f, 0f, 0f), Id: (short)9);
        var sep = BotManager.ApplyTravelSeparation(myPos, goal, new[] { farMate }, range: 120f, strength: 60f);
        Assert.True((sep - goal).Length() < 1e-4f, "a mate beyond range must leave the goal untouched");
        // No mates at all → unchanged.
        var none = BotManager.ApplyTravelSeparation(myPos, goal, Array.Empty<(Vector3, short)>(), 120f, 60f);
        Assert.True((none - goal).Length() < 1e-4f, "no nearby mates → no deflection");
        // A mate directly ahead/behind (no lateral component to push) → unchanged.
        var straightMate = (Pos: new Vector3(0f, 0f, 50f), Id: (short)9);
        var sa = BotManager.ApplyTravelSeparation(myPos, goal, new[] { straightMate }, 120f, 60f);
        Assert.True((sa - goal).Length() < 1e-4f, "a mate dead ahead offers no lateral push");
    }
}
