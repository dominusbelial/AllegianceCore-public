using System.Diagnostics;
using Allegiance.Core.Transport;
using SrvHost = global::Allegiance.Server.ServerHost;
using Recv = global::Allegiance.Server.ReceivedEvt;
using PeerLeft = global::Allegiance.Server.PeerLeftEvt;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P646 (GPT_SOL review findings H4 and M2) — the receive-side hardening of
/// <see cref="SrvHost"/>'s event funnel:
///  (H4) <see cref="SrvHost.Drain"/> is BOUNDED — it pulls at most the events
///       present when it began plus a per-tick budget, and services peers
///       round-robin with a per-peer fair share, so a self-replenishing flood
///       can neither run the drain forever (deferring simulation/broadcast) nor
///       starve other peers.
///  (M2) releasing a peer's byte reservation never INSERTS an accounting entry
///       (the old AddOrUpdate re-created an entry for a departed peer whose
///       receive was queued just before its leave); the entry is finally
///       removed when the ordered PeerLeftEvt drains (ReleasePeer →
///       RemovePeerAccounting).
/// </summary>
public sealed class ServerHostBoundedDrainTests
{
    [Fact]
    public void SelfReplenishing_flood_is_bounded_and_returns_within_budget()
    {
        long ob = SrvHost.DrainBudgetEvents;
        long os = SrvHost.DrainFairShareEvents;
        SrvHost.DrainBudgetEvents = 50;
        SrvHost.DrainFairShareEvents = int.MaxValue;
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(9);
            byte[] frame = new byte[8];
            // 100 events queued, each handled one re-enqueues a fresh receive
            // (a self-replenishing flood — the reviewer's reproduction).
            for (int i = 0; i < 100; i++) ((ITransportSite)host).OnReceive(peer, frame);

            long beforeDrained = host.EventsDrained;
            long beforeDeferred = host.EventsDeferred;
            int handled = 0;
            // Stop re-enqueuing after the budget so even the OLD (unbounded)
            // code terminates — it returns 150 (> 50) and fails the assertion
            // cleanly instead of hanging the suite.
            int processed = host.Drain(e =>
            {
                if (e is Recv && handled < 50)
                {
                    handled++;
                    ((ITransportSite)host).OnReceive(peer, frame);
                }
            });

            // Bounded: no more than the per-tick budget, never the whole flood.
            Assert.Equal(50, processed);
            Assert.Equal(50, host.EventsDrained - beforeDrained);
            // Over-budget work is counted as deferred and carried to next tick.
            Assert.True(host.EventsDeferred - beforeDeferred > 0, "over-budget events must be counted as deferred");
            // The simulation stage can run: Drain returned promptly (no spin).
        }
        finally { SrvHost.DrainBudgetEvents = ob; SrvHost.DrainFairShareEvents = (int)os; }
    }

    [Fact]
    public void Flooding_peer_does_not_starve_a_normal_peer()
    {
        long ob = SrvHost.DrainBudgetEvents;
        long os = SrvHost.DrainFairShareEvents;
        SrvHost.DrainBudgetEvents = 6;      // tight per-tick budget
        SrvHost.DrainFairShareEvents = 2;   // a peer may not hog the budget
        try
        {
            var host = new SrvHost();
            var flood = new ConnectionId(1);
            var norm = new ConnectionId(2);
            byte[] frame = new byte[8];
            for (int i = 0; i < 200; i++) ((ITransportSite)host).OnReceive(flood, frame);
            ((ITransportSite)host).OnReceive(norm, frame);

            int normProcessed = 0;
            int tries = 0;
            // Round-robin must reach the normal peer in a handful of drains,
            // nowhere near the 200-deep flood backlog.
            while (normProcessed == 0 && tries < 200)
            {
                host.Drain(e => { if (e is Recv r && r.From == norm) normProcessed++; });
                tries++;
            }
            Assert.Equal(1, normProcessed);
            Assert.True(tries <= 2, $"normal peer starved behind flood (took {tries} drains)");
        }
        finally { SrvHost.DrainBudgetEvents = ob; SrvHost.DrainFairShareEvents = (int)os; }
    }

    [Fact]
    public void Drain_release_never_recreates_a_departed_peers_accounting_entry()
    {
        long ob = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(3);
            byte[] frame = new byte[600];

            // Peer receives (reserves 600 B), then leaves. The peer's accounting
            // entry is removed by ReleasePeer (simulated here) BEFORE the queued
            // receive is drained.
            ((ITransportSite)host).OnReceive(peer, frame);
            ((ITransportSite)host).OnPeerLeft(peer);
            host.RemovePeerAccounting(peer);   // ReleasePeer on the PeerLeftEvt

            int received = 0;
            host.Drain(e => { if (e is Recv) received++; });

            Assert.Equal(1, received);
            // The receive's release must NOT have re-created the entry (the old
            // AddOrUpdate re-inserted it for a departed peer — review finding M2).
            Assert.Equal(0, host.BufferedPeerCount);
        }
        finally { SrvHost.MaxBufferedBytesPerPeer = ob; }
    }

    [Fact]
    public void Churn_of_1000_peers_leaves_zero_retained_accounting_entries()
    {
        long ob = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;
        try
        {
            var host = new SrvHost();
            byte[] frame = new byte[600];
            for (int i = 0; i < 1000; i++)
            {
                var p = new ConnectionId((uint)(i + 1));
                ((ITransportSite)host).OnReceive(p, frame);
                ((ITransportSite)host).OnPeerLeft(p);
            }
            // Drain: each receive is released (decrement-only), and each
            // PeerLeftEvt removes its peer's entry for good — mimicking
            // Server.ReleasePeer → ServerHost.RemovePeerAccounting.
            host.Drain(e => { if (e is PeerLeft pl) host.RemovePeerAccounting(pl.Id); });
            Assert.Equal(0, host.BufferedPeerCount);
        }
        finally { SrvHost.MaxBufferedBytesPerPeer = ob; }
    }
}
