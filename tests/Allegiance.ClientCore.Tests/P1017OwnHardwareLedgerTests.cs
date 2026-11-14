using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>P1017 — the client's record of which of its hardware the server
/// will refund, kept from <c>BUY_LOADOUT_ACK</c> alone (no wire change).</summary>
public sealed class P1017OwnHardwareLedgerTests
{
    private static BuyLoadoutAck Ack(short hull, bool all = true, bool launch = false, params (short Part, short Amount)[] parts) => new()
    {
        HullId = hull, FBoughtEverything = all, FLaunch = launch,
        Parts = parts.Select((p, i) => new ExpandedPartData(p.Part, p.Amount, (byte)i, ExpandedPartData.FullyMounted)).ToArray(),
    };

    [Fact]
    public void Everything_told_before_the_first_buy_is_free()
    {
        var ledger = new OwnHardwareLedger();
        ledger.Acknowledged(Ack(10));                               // the seat acknowledgement: bare hull
        ledger.Acknowledged(Ack(10, parts: [(7, 3), (9, 1)]));      // the dock sync: the spawn kit
        Assert.False(ledger.HullPaid(10));
        Assert.Equal([new FreeHardware.Unit(7, 3), new FreeHardware.Unit(9, 1)], ledger.FreeParts);
        Assert.Equal(2, ledger.Revision);
        Assert.Equal(0, ledger.LastAnswer.Serial);                  // neither answered a request of ours
    }

    [Fact]
    public void A_buy_keeps_the_kit_free_only_while_it_is_still_on_the_ship()
    {
        var ledger = new OwnHardwareLedger();
        ledger.Acknowledged(Ack(10, parts: [(7, 3), (9, 1)]));

        ledger.RequestSent(10);                                     // same hull, one kit part dropped, one bought
        ledger.Acknowledged(Ack(10, parts: [(7, 3), (8, 1)]));
        Assert.False(ledger.HullPaid(10));                          // the spawn hull stays free
        Assert.Equal([new FreeHardware.Unit(7, 3)], ledger.FreeParts);
        Assert.Equal(new OwnHardwareLedger.Answer(1, 10, 10, true, false), ledger.LastAnswer);

        ledger.RequestSent(10);                                     // buying type 9 again: it is paid for now
        ledger.Acknowledged(Ack(10, parts: [(7, 3), (9, 1)]));
        Assert.Equal([new FreeHardware.Unit(7, 3)], ledger.FreeParts);

        ledger.RequestSent(10);                                     // a fuller rack than the free one held
        ledger.Acknowledged(Ack(10, parts: [(7, 5)]));
        Assert.Empty(ledger.FreeParts);
    }

    [Fact]
    public void A_refused_buy_changes_nothing()
    {
        var ledger = new OwnHardwareLedger();
        ledger.Acknowledged(Ack(10, parts: [(7, 3)]));
        ledger.RequestSent(10);
        ledger.Acknowledged(Ack(10, all: false, parts: [(7, 3)]));  // the ship as it was
        Assert.False(ledger.HullPaid(10));
        Assert.Equal([new FreeHardware.Unit(7, 3)], ledger.FreeParts);
        Assert.False(ledger.LastAnswer.BoughtEverything);
        Assert.Equal(0, ledger.Pending);
    }

    [Fact]
    public void A_hull_is_paid_when_a_buy_of_ours_changed_it_and_free_again_after_a_pod()
    {
        var ledger = new OwnHardwareLedger();
        ledger.Acknowledged(Ack(10));
        ledger.RequestSent(10);
        ledger.Acknowledged(Ack(20, parts: [(7, 1)]));
        Assert.True(ledger.HullPaid(20));
        Assert.False(ledger.HullPaid(10));
        Assert.Empty(ledger.FreeParts);

        ledger.RequestSent(20);                                     // same hull again: still the paid one
        ledger.Acknowledged(Ack(20));
        Assert.True(ledger.HullPaid(20));

        ledger.Acknowledged(Ack(99));                               // docked in a lifepod: no buy of ours
        Assert.False(ledger.HullPaid(99));
        Assert.False(ledger.HullPaid(20));

        ledger.RequestSent(99);                                     // a new ship bought from the pod
        ledger.Acknowledged(Ack(20, launch: true));
        Assert.True(ledger.HullPaid(20));
        Assert.True(ledger.LastAnswer.Launch);
        Assert.Equal(99, ledger.LastAnswer.HullBefore);
    }

    [Fact]
    public void Reset_forgets_the_session()
    {
        var ledger = new OwnHardwareLedger();
        ledger.Acknowledged(Ack(10));
        ledger.RequestSent(10);
        ledger.Acknowledged(Ack(20));
        ledger.Reset();
        Assert.False(ledger.HullPaid(20));
        Assert.Equal(0, ledger.Revision);
        Assert.Equal(default, ledger.LastAnswer);
        ledger.Acknowledged(Ack(20, parts: [(7, 1)]));
        Assert.Single(ledger.FreeParts);                            // a fresh session: free again
    }
}
