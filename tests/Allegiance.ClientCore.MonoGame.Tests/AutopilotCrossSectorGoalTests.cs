using System.Collections.Generic;
using System.Numerics;
using Allegiance.ClientCore.MonoGame.Flight;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P540b — the cross-sector autopilot steering-goal decision
/// (<see cref="Game1.TryResolveAutopilotGoal"/>), the single pure decision that
/// fixes the P540 eject bug. When the own ship and the autopilot target live in
/// DIFFERENT sectors, the steering goal must be the NEXT WARP MOUTH toward the
/// target's sector (routed through the real warp table), NOT the target's
/// foreign-sector coordinates — previously the P524 law steered at the station's
/// Ganymede coordinates from inside Alderan, i.e. "the middle of nowhere".
/// Once the ship reaches the target's sector, the goal is the target's own local
/// position (the pre-P540b law, unchanged), and same-sector targets are
/// untouched.
/// </summary>
public class AutopilotCrossSectorGoalTests
{
    // A tiny three-sector warp network: 100 --W1--> 101 --W2--> 102.
    // The rescue station is in 102, two real warp hops from the eject sector 100.
    private static readonly Vector3 W1 = new(100f, 0f, 0f);   // warp mouth in 100 -> 101
    private static readonly Vector3 W2 = new(200f, 0f, 0f);   // warp mouth in 101 -> 102
    private static readonly SimVec Station102 = new(987f, 654f, 321f); // the station's coordinates IN 102

    private static readonly List<StarmapGotoRouter.AllWarp> Network =
    [
        new(W1, SourceSector: 100, DestSector: 101),
        new(W2, SourceSector: 101, DestSector: 102),
    ];

    [Fact]
    public void CrossSector_TwoWarpsAway_GoalIsNextWarpMouth_NotForeignCoordinates()
    {
        // Own ship in 100, station target in 102 (two warps away). The station's
        // RAW coordinates (Station102) are inside 102 — steering at them from 100
        // was the exact P540 "middle of nowhere" bug.
        bool ok = Game1.TryResolveAutopilotGoal(
            ownSector: 100, targetSector: 102, targetLocalPos: Station102,
            Network, out var goal, out var routed);

        Assert.True(ok, "cross-sector target must yield a routing goal");
        Assert.True(routed, "cross-sector target must be routed through warps");
        Assert.Equal(new SimVec(W1.X, W1.Y, W1.Z), goal);          // the warp in 100 leading toward 102
        Assert.NotEqual(new SimVec(Station102.X, Station102.Y, Station102.Z), goal);  // never the foreign coords
    }

    [Fact]
    public void OnceInTargetSector_GoalIsTheStationItself()
    {
        // The pod has transited both warps and is now IN 102: the goal flips to
        // the station's real local position (the P524 arrival law takes over).
        bool ok = Game1.TryResolveAutopilotGoal(
            ownSector: 102, targetSector: 102, targetLocalPos: Station102,
            Network, out var goal, out var routed);

        Assert.True(ok);
        Assert.False(routed, "same-sector target is not routed");
        Assert.Equal(new SimVec(Station102.X, Station102.Y, Station102.Z), goal);
    }

    [Fact]
    public void SameSector_TargetIsUnchanged()
    {
        // A same-sector station target behaves exactly as before P540b: goal is
        // its local position, not routed (the router is not even consulted — an
        // empty warp table is fine).
        var local = new SimVec(50f, 0f, 0f);
        bool ok = Game1.TryResolveAutopilotGoal(
            ownSector: 100, targetSector: 100, targetLocalPos: local,
            new List<StarmapGotoRouter.AllWarp>(), out var goal, out var routed);

        Assert.True(ok);
        Assert.False(routed);
        Assert.Equal(local, goal);
    }

    [Fact]
    public void UnknownTargetSector_FallsBackToLocalPosition()
    {
        // When the target's sector cannot be resolved, behave conservatively and
        // use its local position (the pre-P540b behaviour) rather than guessing a
        // route.
        var local = new SimVec(7f, 8f, 9f);
        bool ok = Game1.TryResolveAutopilotGoal(
            ownSector: 100, targetSector: null, targetLocalPos: local,
            new List<StarmapGotoRouter.AllWarp>(), out var goal, out var routed);

        Assert.True(ok);
        Assert.False(routed);
        Assert.Equal(local, goal);
    }

    [Fact]
    public void CrossSector_Unreachable_ReturnsFalse()
    {
        // Target sector 999 is not reachable from 100 over this network: no goal.
        bool ok = Game1.TryResolveAutopilotGoal(
            ownSector: 100, targetSector: 999, targetLocalPos: null,
            Network, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void CrossSector_MovesThroughIntermediateWarp_EachLeg()
    {
        // Walk the journey leg by leg to prove the goal ADVANCES as the pod
        // transits: in 100 the goal is W1 (toward 101); once in 101 the goal is
        // W2 (toward 102); in 102 it is the station. This is the full multi-warp
        // route the rescue pod takes.
        Game1.TryResolveAutopilotGoal(100, 102, null, Network, out var leg1, out var r1);
        Assert.True(r1);
        Assert.Equal(new SimVec(W1.X, W1.Y, W1.Z), leg1);

        Game1.TryResolveAutopilotGoal(101, 102, null, Network, out var leg2, out var r2);
        Assert.True(r2);
        Assert.Equal(new SimVec(W2.X, W2.Y, W2.Z), leg2);

        Game1.TryResolveAutopilotGoal(102, 102, Station102, Network, out var leg3, out var r3);
        Assert.False(r3);
        Assert.Equal(new SimVec(Station102.X, Station102.Y, Station102.Z), leg3);
    }
}
