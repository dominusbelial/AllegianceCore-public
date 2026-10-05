using Allegiance.Core.Collision;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539d — prove the pure fair-contact decision helpers against synthetic
/// geometry (no server):
/// 1. staging HOLD: a side releases only once its whole living complement is
///    staged, or the timeout fires (never before);
/// 2. stand-off-on-approach: the approach goal never targets inside the
///    collision ring of the target (no ramming);
/// 3. collision deaths are counted: a death with no credited ship shooter is
///    classified collision/other, a credited ship kill is gunfire;
/// 4. arena placements are mirrored about the centre and equidistant from it;
///    plus cross-side approach avoidance deflects around a ship in the path.
/// </summary>
public class P539dFairContactDecisionTests
{
    // ── 1. Staging: hold until assembled or timeout ──

    [Fact]
    public void Staging_HoldsUntilWholeSideAssembled()
    {
        // 5 alive, only 3 staged → NOT ready; assembly is the ONLY release path.
        Assert.False(BotManager.SideReadyToAdvance(aliveCount: 5, stagedCount: 3, now: 100.0, firstStagedAt: 90.0, timeoutSeconds: 15.0),
            "must NOT release while the side is not yet assembled and the timeout hasn't fired");
        // Fully assembled → release immediately.
        Assert.True(BotManager.SideReadyToAdvance(5, 5, now: 100.0, 90.0, 15.0),
            "a fully-assembled side releases");
    }

    [Fact]
    public void Staging_TimeoutBoundsABlockingStraggler()
    {
        // One bot still en route (4 of 5 staged), but the first-staged bot has
        // already waited >= the timeout → release so a straggler can't freeze the group.
        Assert.True(BotManager.SideReadyToAdvance(aliveCount: 5, stagedCount: 4, now: 110.0, firstStagedAt: 90.0, timeoutSeconds: 15.0),
            "the timeout must release a side blocked by a single straggler");
        // Just before the timeout → still holds.
        Assert.False(BotManager.SideReadyToAdvance(5, 4, now: 104.0, 90.0, 15.0),
            "must still hold just before the timeout");
    }

    [Fact]
    public void Staging_NoLivingBotsReleasesTrivially()
    {
        // A wiped side must never deadlock the surviving side's advance.
        Assert.True(BotManager.SideReadyToAdvance(0, 0, now: 100.0, null, 15.0),
            "a side with no living bots releases trivially");
        // Null first-staged with a live complement and not assembled → holds.
        Assert.False(BotManager.SideReadyToAdvance(4, 2, now: 100.0, null, 15.0),
            "a partially-staged side with no first-hold timestamp holds until assembled");
    }

    // ── 2. Stand-off-on-approach: never inside the collision ring ──

    [Fact]
    public void StandOffOnApproach_NeverTargetsInsideTheRing()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var aimDir = new Vector3(0f, 0f, 1f);
        float floor = 39f;                    // DogfightStandoffFloor + myR + targetR
        float standOff = 150f;                // a ring larger than the floor
        // Approaching from OUTSIDE the ring (300 u away): the goal must sit exactly
        // stand-off short of the target — at least the collision floor away from the
        // target's body, so the approach brakes at the ring and never rams it.
        foreach (float distance in new[] { 300f, 200f })
        {
            var goal = BotManager.StandOffLeadGoal(myPos, aimDir, distance, standOff);
            float goalToTarget = (goal - aimDir * distance).Length();
            // The goal sits `standOff` short of the target (the ring), i.e. it is
            // `distance − standOff` ahead of us — never AT or past the target's body.
            Assert.True(MathF.Abs(goalToTarget - standOff) < 1e-2f,
                $"approach goal must hold the stand-off ring ({standOff} short of the target), got {goalToTarget:F1}");
            Assert.True(goalToTarget >= floor - 1e-3f,
                $"goal must sit at least the collision floor ({floor}) short of the target (got {goalToTarget:F1})");
        }
    }

    [Fact]
    public void StandOffOnApproach_InsideRingHoldsRatherThanClosing()
    {
        // When the target is ALREADY inside the stand-off ring (we overshot), the
        // goal collapses to our own position: the bot holds rather than driving any
        // closer to the target's body. The goal must never be AT the target.
        var myPos = new Vector3(0f, 0f, 0f);
        var aimDir = new Vector3(0f, 0f, 1f);
        float standOff = 150f;
        float distance = 60f;                 // inside the ring
        var goal = BotManager.StandOffLeadGoal(myPos, aimDir, distance, standOff);
        Assert.True((goal - myPos).Length() < 1e-3f,
            $"an inside-ring target must collapse the goal to our position (hold, not close), got {goal}");
        Assert.True((goal - aimDir * distance).Length() >= distance - 1e-3f,
            "the goal must not bring us closer to the target than we already are");
    }

    // ── 3. Collision deaths are counted (gunfire vs collision/other) ──

    [Fact]
    public void DeathClassification_GunfireKillRequiresCreditedShipShooter()
    {
        // A death with a KILL_SHIP crediting a DISTINCT ship (TypeCredit 0 = OT_ship)
        // is a gunfire kill.
        Assert.Equal(BotManager.DeathCause.GunfireKill, BotManager.ClassifyDeath(50,
            new[] { (VictimId: (short)50, KillerId: (short)41, KillerType: (short)0) }));
        Assert.Equal(BotManager.DeathCause.GunfireKill, BotManager.ClassifyDeath(50,
            new[] { (VictimId: (short)3, KillerId: (short)2, KillerType: (short)0),
                    (VictimId: (short)50, KillerId: (short)41, KillerType: (short)0) }));
    }

    [Fact]
    public void DeathClassification_RamAndUnknownAreCollisionOther()
    {
        // No credited ship shooter (a ram / a crash) → collision/other.
        Assert.Equal(BotManager.DeathCause.CollisionOrOther, BotManager.ClassifyDeath(50,
            Array.Empty<(short, short, short)>()));
        // A kill credited to a NON-ship (probe/asteroid, TypeCredit != 0) is not a gunfire kill.
        Assert.Equal(BotManager.DeathCause.CollisionOrOther, BotManager.ClassifyDeath(50,
            new[] { (VictimId: (short)50, KillerId: (short)9, KillerType: (short)3) }));
        // A "kill" whose killer IS the victim (suicide) is not credited to another ship.
        Assert.Equal(BotManager.DeathCause.CollisionOrOther, BotManager.ClassifyDeath(50,
            new[] { (VictimId: (short)50, KillerId: (short)50, KillerType: (short)0) }));
    }

    // ── 4. Arena placements mirrored + equidistant ──

    [Fact]
    public void ArenaPlacements_AreMirroredAndEquidistantFromCentre()
    {
        var center = new Vector3(100f, 200f, 50f);
        var axis = new Vector3(0f, 0f, 1f);
        float offset = 650f;
        var (a, b, fa, fb) = BotManager.ComputeArenaPlacements(5, center, axis, offset, 220f);

        Assert.Equal(5, a.Length);
        Assert.Equal(5, b.Length);
        for (int i = 0; i < 5; i++)
        {
            // Mirrored about the centre: b[i] is the reflection of a[i].
            Vector3 reflectedB = 2f * center - a[i];
            Assert.True((reflectedB - b[i]).Length() < 1e-3f, $"bot {i} must be mirrored about the centre");
            // Both sides EQUIDISTANT from the centre (the lane spread shifts both equally).
            float da = (a[i] - center).Length();
            float db = (b[i] - center).Length();
            Assert.True(MathF.Abs(da - db) < 1e-3f, $"side-A and side-B bot {i} must be equidistant from the centre ({da:F1} vs {db:F1})");
            // Each is at least the axial offset from the centre (the offset dominates).
            Assert.True(da >= offset - 1e-3f, $"bot {i} must be at least the arena offset ({offset}) from the centre, got {da:F1}");
        }
        // Facing each other: side A (at +axis) faces −axis, side B (at −axis) faces +axis.
        Assert.True((fa + axis).Length() < 1e-4f, "side A faces toward side B (−axis)");
        Assert.True((fb - axis).Length() < 1e-4f, "side B faces toward side A (+axis)");
    }

    [Fact]
    public void ArenaPlacements_SameSideBotsSpreadApart()
    {
        var (a, b, _, _) = BotManager.ComputeArenaPlacements(4, Vector3.Zero, new Vector3(0f, 0f, 1f), 650f, 220f);
        for (int i = 0; i < a.Length; i++)
            for (int j = i + 1; j < a.Length; j++)
                Assert.True((a[i] - a[j]).Length() > 50f, $"same-side bots {i},{j} must not stack on one point");
    }

    // ── Cross-side approach avoidance: steer around a ship in the path ──

    [Fact]
    public void ApproachAvoidance_DeflectsAroundAShipInThePath()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var target = new Vector3(0f, 0f, 500f);
        // A ship sitting almost directly in the forward path at 60 u.
        var blocker = new Vector3(8f, 0f, 60f);
        bool ok = BotManager.AvoidShipsInPath(myPos, target, horizon: 200f, cosHalfAngle: 0.860f, clearance: 30f,
            new[] { (Pos: blocker, Id: (short)77) }, engagedTargetId: null, out var goal);
        Assert.True(ok, "a ship in the path cone must trigger avoidance");
        // The goal must be deflected LATERALLY, not along the straight line.
        var rawDir = target - myPos; rawDir /= rawDir.Length();
        var straight = myPos + rawDir * (target - myPos).Length();
        Assert.True((goal - straight).Length() > 5f, $"goal must be deflected laterally (straight {straight}, goal {goal})");
        // It must not drive through the blocker's lateral position.
        Assert.True(MathF.Abs(goal.Y) > 1e-3f || MathF.Abs(goal.X) > 20f, "goal clears the blocker laterally");
    }

    [Fact]
    public void ApproachAvoidance_IgnoredForEngagedTargetOutsideCone()
    {
        var myPos = new Vector3(0f, 0f, 0f);
        var target = new Vector3(0f, 0f, 500f);
        // A ship far outside the path cone is not avoided.
        bool ok = BotManager.AvoidShipsInPath(myPos, target, 200f, 0.860f, 30f,
            new[] { (Pos: new Vector3(200f, 200f, 10f), Id: (short)77) }, engagedTargetId: null, out _);
        Assert.False(ok, "a ship outside the forward cone must not trigger avoidance");
        // The actively-engaged target is excluded (the stand-off ring handles it).
        var engaged = new Vector3(0f, 0f, 80f);
        ok = BotManager.AvoidShipsInPath(myPos, target, 200f, 0.860f, 30f,
            new[] { (Pos: engaged, Id: (short)88) }, engagedTargetId: (short)88, out _);
        Assert.False(ok, "the bot's own engaged target must not be steered around (stand-off ring governs it)");
    }
}
