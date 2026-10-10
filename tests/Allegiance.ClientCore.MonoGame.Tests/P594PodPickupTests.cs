using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P594 — pure decision logic behind the player-autopilot pod rescue: the
/// native "mmf pod pickup" goto (common.cpp:1430-1446) and the "offered the
/// rescue" order flow (native AcceptQueuedCommand bExecute branch,
/// WinTrek.cpp:10626-10712). The shared <see cref="NavMath.SolveForImpact"/>,
/// <see cref="AutopilotSteering.PodPickup"/> and the server diverter are tested
/// in the Core / AutopilotSteering suites; here we cover the pure client
/// accept→engage decision.
/// </summary>
public class P594PodPickupTests
{
    /// <summary>A pending <c>CidPickup</c> order targeting a SHIP (the pod) is
    /// the rescue offer: accepting it must engage the autopilot toward that
    /// pod. Failing-first: before P594 the accept handler promoted the order but
    /// never selected/engaged the pod, so the player never flew to it.</summary>
    [Fact]
    public void PickupOfferTargetingShip_ReturnsPodId()
    {
        var qc = new QueuedCommandState(OrderChange.CidPickup, IgcObjectType.Ship, 808);
        Assert.Equal((short)808, Game1.PickupPodIdForQueued(qc));
    }

    /// <summary>A queued order that is NOT a pickup (e.g. an attack order) is
    /// not a rescue offer — accepting it must not yank the autopilot at a pod.</summary>
    [Fact]
    public void NonPickupOrder_ReturnsNull()
    {
        var qc = new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 555);
        Assert.Null(Game1.PickupPodIdForQueued(qc));
    }

    /// <summary>A pickup order whose target is NOT a ship (e.g. a warp/station —
    /// there is no pod to converge on) must not engage the intercept autopilot.</summary>
    [Fact]
    public void PickupOrderTargetingNonShip_ReturnsNull()
    {
        var qc = new QueuedCommandState(OrderChange.CidPickup, IgcObjectType.Station, 777);
        Assert.Null(Game1.PickupPodIdForQueued(qc));
    }

    /// <summary>No pending order (CidNone) — nothing to accept, no pod target.</summary>
    [Fact]
    public void NoPendingOrder_ReturnsNull()
    {
        Assert.Null(Game1.PickupPodIdForQueued(null));
        Assert.Null(Game1.PickupPodIdForQueued(new QueuedCommandState(OrderChange.CidNone, IgcObjectType.Ship, -1)));
    }
}
