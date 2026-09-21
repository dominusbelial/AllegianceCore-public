using Allegiance.Core.Transport;
using SrvHost = global::Allegiance.Server.ServerHost;
using Limiter = global::Allegiance.Server.PerPeerRateLimiter;
using Recv = global::Allegiance.Server.ReceivedEvt;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P315 (stability review finding H3) — deterministic (non-live) proofs of the
/// two in-bound flow-control mechanisms added to remove a flood/freeze vector:
/// (1) <see cref="SrvHost"/> bounds the shared receive queue per-peer, dropping
/// a flooding peer's excess instead of growing the queue unbounded; and
/// (2) <see cref="Limiter"/> bounds per-peer diagnostic console output so a
/// flood of messages cannot force unbounded synchronous console I/O in a tick.
/// </summary>
public sealed class InboundFlowControlTests
{
    private sealed class FakeSite : ITransportSite
    {
        public int Joined, Left, Received;
        public void OnPeerJoined(ConnectionId id, string name) => Joined++;
        public void OnPeerLeft(ConnectionId id) => Left++;
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Received++;
    }

    [Fact]
    public void ServerHost_Inbound_flood_from_one_peer_is_bounded_by_budget()
    {
        long old = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;   // shrink the test seam
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(7);
            byte[] frame = new byte[600];

            // Two 600-byte frames against a 1000-byte budget: the first is
            // accepted, the second overflows and must be dropped (not enqueued).
            ((ITransportSite)host).OnReceive(peer, frame);
            ((ITransportSite)host).OnReceive(peer, frame);

            int received = 0;
            host.Drain(_ => { if (_ is Recv) received++; });
            Assert.Equal(1, received);   // only the first survived the budget

            // Draining releases the budget, so the same peer can send again.
            ((ITransportSite)host).OnReceive(peer, frame);
            int received2 = 0;
            host.Drain(_ => { if (_ is Recv) received2++; });
            Assert.Equal(1, received2);
        }
        finally
        {
            SrvHost.MaxBufferedBytesPerPeer = old;
        }
    }

    [Fact]
    public void ServerHost_Multiple_peers_do_not_starve_each_other()
    {
        long old = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;
        try
        {
            var host = new SrvHost();
            var a = new ConnectionId(1);
            var b = new ConnectionId(2);
            byte[] frame = new byte[600];

            // Peer A floods over its own budget; peer B stays within its own.
            for (int i = 0; i < 10; i++) ((ITransportSite)host).OnReceive(a, frame);
            ((ITransportSite)host).OnReceive(b, frame);

            int fromA = 0, fromB = 0;
            host.Drain(e => { if (e is Recv r) { if (r.From == a) fromA++; else if (r.From == b) fromB++; } });
            Assert.Equal(1, fromA);   // A's excess dropped, its own budget held
            Assert.Equal(1, fromB);   // B never collaterally starved
        }
        finally
        {
            SrvHost.MaxBufferedBytesPerPeer = old;
        }
    }

    [Fact]
    public void RateLimiter_EmitsMaxThenSuppressesWithPeriodicSummaries()
    {
        var rl = new Limiter { MaxPerWindow = 5, SummaryEvery = 10 };
        int emit = 0, summaries = 0;
        for (int i = 0; i < 100; i++)
        {
            var t = rl.Ticket(1);
            if (t.Emit) emit++;
            if (t.Summarize) summaries++;
        }
        // At most 5 emissions in the first (5 s) window; the rest suppressed,
        // with a summary line every 10 suppressions (10,20,...,90 => 9).
        Assert.Equal(5, emit);
        Assert.Equal(9, summaries);
    }

    [Fact]
    public void RateLimiter_WindowBoundary_ResetsBudget()
    {
        // Very short window so a small time gap crosses it deterministically.
        var rl = new Limiter { MaxPerWindow = 5, WindowMs = 1 };
        for (int i = 0; i < 20; i++)
        {
            var t = rl.Ticket(1);
            if (t.Emit) continue;   // burn the first window's budget
        }
        Thread.Sleep(10);            // cross the 1 ms window boundary
        int emit = 0;
        for (int i = 0; i < 20; i++)
        {
            var t = rl.Ticket(1);
            if (t.Emit) emit++;
        }
        Assert.True(emit >= 1, "a fresh window must grant budget again");
    }
}
