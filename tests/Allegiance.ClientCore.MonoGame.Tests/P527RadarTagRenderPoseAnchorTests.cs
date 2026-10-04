using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P527 — Domingo's live playtest (2026-10-04): "why the miner and the hud
/// icon position would not match?" The HUD radar-contact bracket and its
/// in-world side tag (P372) were projected from each ship's RAW 20 Hz
/// <c>ServerShipUpdate</c> pose while the hull is drawn from the per-frame
/// RENDER pose (P508 local-sim for self, P448b dead-reckoned for remotes), so
/// the icon trailed a moving hull by roughly velocity × time-since-last-update
/// — very visible on big, steadily-moving miners. This is P523's exact bug
/// (which anchored the target/lock brackets to the render pose via
/// <see cref="Game1.ResolveShipAnchor"/>) left half-fixed: the radar contact
/// loop was the "third path" P523's doc comment said didn't exist.
/// <para/>
/// The fix routes every radar-contact/side-tag anchor (and the loop's own-ship
/// eye) through the same shared render-pose accessor. These tests assert that
/// precedence: render pose when present, raw only as a fallback. Failing-first:
/// <see cref="Game1.ResolveRadarContactAnchor"/> was initially a raw-pose
/// passthrough mirroring the pre-fix loop, and these tests failed against it.
/// </summary>
public class P527RadarTagRenderPoseAnchorTests
{
    private const short Sector = 1050;

    private static ServerShipUpdate PoseAt(short id, float x, float y, float z) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new SimVec(x, y, z), Orientation.Identity, SimVec.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    /// <summary>A remote ship (id=<paramref name="rem"/>) alongside the own ship
    /// (<paramref name="me"/>). Raw remote pose is (100,0,0) — a contact whose
    /// last ServerShipUpdate lags behind where the hull is rendered.</summary>
    private static ClientWorld WorldWithOwnAndRemote(short me, short rem)
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = PoseAt(me, 720f, 730f, 920f) });
        w.Apply(new SingleShipUpdate { OidTarget = rem, Ship = PoseAt(rem, 100f, 0f, 0f) });
        w.Apply(new ShipStatusMessage { ShipId = rem, SectorId = Sector });
        return w;
    }

    /// <summary>⭐ Regression: the radar contact bracket and in-world side tag
    /// must anchor to the RENDER pose, not the raw ServerShipUpdate pose. Take
    /// a moving remote between updates: render pose has advanced to (150,0,0)
    /// while its last raw update is still (100,0,0). Pre-fix the loop used
    /// <c>up.Position</c> (100) — the icon trailing the hull — and this failed.</summary>
    [Fact]
    public void RadarContactAnchor_ReturnsRenderPose_NotRaw_ForMovingRemote()
    {
        const short me = 1, rem = 2;
        var w = WorldWithOwnAndRemote(me, rem);
        var render = new Dictionary<short, Flight.PredictedPose>
        {
            [rem] = new(new SimVec(150f, 0f, 0f), Orientation.Identity),  // dead-reckoned, past raw 100
        };

        Assert.True(Game1.ResolveRadarContactAnchor(render, w.Ships, rem, out var pos));
        Assert.Equal(150f, pos.X);   // render pose, NOT the raw 100 the icon trailed
    }

    /// <summary>Fallback: a contact that wasn't rendered this frame (absent from
    /// the render-pose table) anchors to its raw last-known pose — there is no
    /// smoother pose to glue to.</summary>
    [Fact]
    public void RadarContactAnchor_FallsBackToRaw_WhenNotRendered()
    {
        const short me = 1, rem = 2;
        var w = WorldWithOwnAndRemote(me, rem);
        var empty = new Dictionary<short, Flight.PredictedPose>();

        Assert.True(Game1.ResolveRadarContactAnchor(empty, w.Ships, rem, out var pos));
        Assert.Equal(100f, pos.X);   // fell back to the raw ServerShipUpdate pose
    }

    /// <summary>⭐ The radar loop's OWN-ship eye must also use the render pose
    /// (P508 local-sim, (10,0,0)) rather than the raw own update ((720,730,920)),
    /// so the distance fade and projection share the frame's own position.</summary>
    [Fact]
    public void RadarContactAnchor_UsesLocalSimRenderPose_ForOwnShipEye()
    {
        const short me = 1;
        var w = WorldWithOwnAndRemote(me, /*rem*/ 2);
        var render = new Dictionary<short, Flight.PredictedPose>
        {
            [me] = new(new SimVec(10f, 0f, 0f), Orientation.Identity),  // local-sim pose, far from raw
        };

        Assert.True(Game1.ResolveRadarContactAnchor(render, w.Ships, me, out var pos));
        Assert.Equal(10f, pos.X);   // local-sim render pose, NOT the raw 720
    }
}
