using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P431 — prove the accept-and-translate decision in isolation, with a synthetic
/// pending <see cref="QueuedCommandState"/> and NO live server. This is the same
/// pure function the MCP bot loop's order-handling is built from
/// (<see cref="BotManager.DecideAcceptTranslate"/>), so "given a real pending
/// queued order, the bot's behavior state changes to the correct commanded
/// target" is proven here deterministically. Must-fail-if-removed pairs are
/// spelled out per test.
///
/// Semantics under test: a fresh ship-target order is accepted and ADOPTS exactly
/// the commanded target (never "nearest enemy"), superseding any prior target. An
/// adopted order PERSISTS (it is not dropped on a transient cross-side visibility
/// gap — the documented P429 reality) until a NEW order supersedes it.
/// </summary>
public class OrderAcceptTranslateTests
{
    private const short None = OrderChange.CidNone;

    // A representative "engage that enemy ship" order's action id. The port has no
    // dedicated attack constant; the bot's translation keys on TargetType == Ship,
    // so any real CommandID the wire carries is honored (see the P431 report).
    private const short AttackCmd = OrderChange.CidGoto;

    private static OrderDecision Decide(QueuedCommandState? pending, short accepted = None, short? current = null)
        => BotManager.DecideAcceptTranslate(pending, accepted, current);

    private static QueuedCommandState ShipOrder(short targetId, short cmd = AttackCmd)
        => new(cmd, IgcObjectType.Ship, targetId);

    /// <summary>⭐ The core requirement: given a synthetic pending ship-target
    /// order, the bot's behavior state (its ordered target) becomes THAT specific
    /// commanded enemy — never a guess at "nearest enemy". It must also send the
    /// real ACCEPT for the fresh order.</summary>
    [Fact]
    public void FreshShipTargetOrder_AdoptsExactlyTheCommandedTarget_AndAccepts()
    {
        var d = Decide(pending: ShipOrder(707));
        Assert.True(d.ShouldSendAccept);
        Assert.Equal((short)707, d.OrderedTarget);   // the ACTUALLY NAMED target
    }

    /// <summary>A fresh ship-target order SUPERSEDES whatever the bot was already
    /// engaging (a newer order wins over an older engagement).</summary>
    [Fact]
    public void FreshShipTargetOrder_SupersedesPriorTarget()
    {
        var d = Decide(pending: ShipOrder(909), accepted: None, current: 111);
        Assert.True(d.ShouldSendAccept);
        Assert.Equal((short)909, d.OrderedTarget);
    }

    /// <summary>No pending order and no ordered target → nothing to do: no accept,
    /// no target. A bot that is doing its own thing stays doing its own thing.</summary>
    [Fact]
    public void NoPendingOrder_NoCurrentTarget_NothingToDo()
    {
        var d = Decide(pending: null);
        Assert.False(d.ShouldSendAccept);
        Assert.Null(d.OrderedTarget);
    }

    /// <summary>No NEW pending order but a current ordered target → the bot keeps
    /// obeying it (no re-accept; the engagement persists). Cross-side visibility is
    /// unreliable, so a transient gap in seeing the target does NOT drop the order.</summary>
    [Fact]
    public void NoPendingOrder_KeepsOrderedTargetUntilSuperseded()
    {
        var d = Decide(pending: null, accepted: None, current: 42);
        Assert.False(d.ShouldSendAccept);
        Assert.Equal((short)42, d.OrderedTarget);
    }

    /// <summary>While the server is still clearing the slot (the same pending order
    /// still showing but already accepted), the bot does NOT re-accept; it keeps
    /// engaging the ordered target.</summary>
    [Fact]
    public void AlreadyAcceptedOrder_DoesNotReaccept_KeepsEngaging()
    {
        // pending still shows the order we've already accepted (accepted == cmd).
        var d = Decide(pending: ShipOrder(707), accepted: AttackCmd, current: 707);
        Assert.False(d.ShouldSendAccept);
        Assert.Equal((short)707, d.OrderedTarget);
    }

    /// <summary>⭐ The real order-type finding: a NON-ship / targetless queued order
    /// (OT_invalid) carries NO steerable position on this wire, so there is no
    /// goto/waypoint semantic to translate — the bot accepts it but adopts no
    /// ship-target (and keeps a prior target).</summary>
    [Fact]
    public void NonShipTargetlessOrder_IsAccepted_ButAdoptsNoShipTarget()
    {
        var d = Decide(pending: new QueuedCommandState(AttackCmd, (IgcObjectType)(-1), -1));
        Assert.True(d.ShouldSendAccept);      // it's still a real order — obey it by accepting
        Assert.Null(d.OrderedTarget);          // but no ship to engage
    }

    /// <summary>A non-ship order does not disturb a prior ship-target.</summary>
    [Fact]
    public void NonShipOrder_KeepsPriorTarget()
    {
        var d = Decide(pending: new QueuedCommandState(AttackCmd, (IgcObjectType)(-1), -1), accepted: None, current: 42);
        Assert.True(d.ShouldSendAccept);
        Assert.Equal((short)42, d.OrderedTarget);
    }

    /// <summary>A ship-target order naming an invalid/negative id is not a real
    /// target — no ship-target adopted (defensive).</summary>
    [Fact]
    public void ShipOrderWithInvalidTargetId_AdoptsNoShipTarget()
    {
        var d = Decide(pending: new QueuedCommandState(AttackCmd, IgcObjectType.Ship, -1));
        Assert.False(d.OrderedTarget is not null);
    }
}
