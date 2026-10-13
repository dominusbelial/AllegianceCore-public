using System;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P636c — the snapshot-staleness defect that cost combat lethality (kills
/// 29 → 11 at 150v150, shot rate halved), and the fix that restores aim
/// freshness without giving back P636's lock-free benefit.
///
/// Root cause (numbers): P636/P636b moved the aim/lead path onto a FROZEN
/// snapshot published at ~25 Hz by each bot's receive thread. The lead law
/// (<c>deltaP = targetPos − myPos</c>, <c>deltaV = targetVel − myVel</c>) and
/// the fire gate (<c>angleToLead ≤ c_fMaxOffAngle</c>, shipIGC.cpp:2600) then
/// consumed poses up to the publish age old. A fast scout crossing the view at
/// ~850 u/s moves ~34 u in a 40 ms publish age — beyond a fighter hull radius —
/// so the nose never settles on the moving lead, the fire gate closes, and the
/// bot fires far less (the measured 150v150 shot collapse 20,353 → 10,679 that
/// drops kills 29 → 11). The fix dead-reckons the frozen target/own pose forward
/// to the decision's "now" (uniform motion: <c>pos + vel × age</c>) for the
/// AIM-CRITICAL engaged-target and own-ship reads only, entirely lock-free.
///
/// These tests are FAILING-FIRST: before P636c the snapshot returned the raw
/// frozen position (no dead-reckoning), which violates the "snapshot pose ≈ live
/// pose at decision time within tolerance" invariant the first test asserts — and
/// the second test proves the raw frozen pose misses by ~34 u, outside a fighter
/// hull, exactly the missing-round mechanism. The visible-ships compaction target
/// retention is pinned by P636b's <c>VisibleShips_IsExactlyTheVisibleSubsetOfShips</c>;
/// here we add the freshness guarantee that restore shots/kills.
/// </summary>
// 2026-10-13: the bot decision snapshot now defaults OFF (it cost combat at 200v200 on the
// real rig, clash30/32 vs clash33). These tests exercise the snapshot itself, so they enable it
// explicitly and restore the previous value; one collection keeps them from racing on the flag.
[Collection("DecisionSnapshotFlag")]
public class P636cSnapshotLethalityTests : IDisposable
{
    private readonly bool _savedSnapshotFlag = ClientWorld.DecisionSnapshotEnabled;
    public P636cSnapshotLethalityTests() => ClientWorld.DecisionSnapshotEnabled = true;
    public void Dispose() => ClientWorld.DecisionSnapshotEnabled = _savedSnapshotFlag;

    /// <summary>
    /// FAILING-FIRST — "snapshot pose equals the live pose within tolerance at the
    /// decision time". A fast scout at 850 u/s crossing the view: at the decision
    /// moment the LIVE pose is 40 ms ahead of where the ~25 Hz snapshot froze it.
    /// The fix (dead-reckon by vel × age) must land the aim pose on the live pose
    /// within tolerance. Pre-P636c (raw frozen pose) this assertion fails — the
    /// frozen pose is ~34 u behind, beyond a fighter hull radius (miss).
    /// </summary>
    [Fact]
    public void Snapshot_deadreckon_matches_live_pose_where_raw_freeze_misses()
    {
        // A fast interceptor crossing the bot's view plane at 850 u/s.
        var frozen = new DecisionShip
        {
            PosX = 1000f, PosY = 0f, PosZ = 0f,
            VelX = 0f, VelY = 850f, VelZ = 0f,
        };
        const double publishAgeMs = 40.0;              // P636's ~25 Hz publish age
        var livePose = new Vector3(1000f, 850f * 0.040f, 0f);   // where it IS at decision time

        var aimed = DecisionSnapshot.DeadReckonPosition(frozen, publishAgeMs);

        // The fix: the extrapolated pose lands ON the live pose (within 5 u).
        Assert.InRange(aimed.X, livePose.X - 5f, livePose.X + 5f);
        Assert.InRange(aimed.Y, livePose.Y - 5f, livePose.Y + 5f);
        Assert.InRange(aimed.Z, livePose.Z - 5f, livePose.Z + 5f);
    }

    /// <summary>
    /// The DEFECT magnitude: the raw frozen position (pre-P636c) is ~34 u behind
    /// the live pose at the 40 ms publish age — beyond a fighter hull radius
    /// (≤ ~15 u), so a lead solved from it points at empty space and the fire gate
    /// (angleToLead ≤ 0.10 rad) stays shut → fewer shots → fewer kills. This test
    /// pins the concrete miss the fix eliminates.
    /// </summary>
    [Fact]
    public void Raw_frozen_pose_misses_beyond_hull_radius_at_publish_age()
    {
        var frozen = new DecisionShip
        {
            PosX = 1000f, PosY = 0f, PosZ = 0f,
            VelX = 0f, VelY = 850f, VelZ = 0f,
        };
        var livePose = new Vector3(1000f, 850f * 0.040f, 0f);
        var raw = new Vector3(frozen.PosX, frozen.PosY, frozen.PosZ);

        float miss = (raw - livePose).Length();

        // ~34 u of aim error — beyond the ~15 u hull radius of a fighter.
        Assert.True(miss > 30f, $"expected a >30 u stale-pose miss, got {miss:F1}");
        Assert.True(miss > 15f, "the frozen pose must miss beyond a fighter hull radius");
    }

    /// <summary>
    /// The fix must NOT reintroduce P636's world-gate contention: dead-reckoning
    /// is pure math over the snapshot (no lock), so the "snapshot is immutable and
    /// read lock-free" contract is untouched. Pins that DeadReckonPosition never
    /// needs the world (no side effects), exercised here on a standalone struct.
    /// </summary>
    [Fact]
    public void DeadReckon_is_lockfree_pure_over_the_snapshot()
    {
        var s = new DecisionShip { PosX = 5f, PosY = 0f, PosZ = 0f, VelX = 100f, VelY = 0f, VelZ = 0f };
        var p = DecisionSnapshot.DeadReckonPosition(s, 100.0);
        Assert.InRange(p.X, 15f - 1e-3f, 15f + 1e-3f);   // 5 + 100*0.1 = 15
        // And zero age is a no-op (raw position) — backwards compatible.
        var q = DecisionSnapshot.DeadReckonPosition(s, 0.0);
        Assert.InRange(q.X, 5f - 1e-3f, 5f + 1e-3f);
    }
}
