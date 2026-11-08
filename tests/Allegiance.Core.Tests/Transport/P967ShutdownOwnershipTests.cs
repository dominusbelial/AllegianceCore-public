using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P967 (review findings P963-04 and P963-08) — transport shutdown ownership and
/// the bounded removal of the ended-peer tombstone.
///
/// (a) A connection that accepts before Shutdown and greets only afterwards must
///     NOT join: the greeting socket and thread are Shutdown's, it closes and
///     joins them before tearing the sender pool down, and HandlePeer re-checks
///     the shutdown generation before inserting the peer and before
///     <c>OnPeerJoined</c>. After Shutdown returns: no join, no leave, no live
///     peer, no tracked thread, no per-connection state.
/// (b) Connection churn must leave NO per-historical-connection storage behind
///     (the old <c>_peerEnded</c> table grew by one entry per departed
///     connection), while every departed id still reports closed so late queued
///     inbound packets stay suppressed (P311 exactly-once guard, P654 H-14
///     "queued inbound packets must not rearm it").
/// </summary>
[Collection("LiveNetwork")]
public sealed class P967ShutdownOwnershipTests
{
    private sealed class Site : ITransportSite
    {
        public readonly ConcurrentQueue<ConnectionId> Joined = new();
        public readonly ConcurrentQueue<ConnectionId> Left = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.Enqueue(id);
        public void OnPeerLeft(ConnectionId id) => Left.Enqueue(id);
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static byte[] GreetFrame(string name)
    {
        var body = Encoding.UTF8.GetBytes(name);
        var frame = new byte[5 + body.Length];
        frame[0] = 1;   // FrameType.Greet
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), body.Length);
        body.CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static TcpClient ConnectSilent(ushort port)
    {
        var raw = new TcpClient { NoDelay = true, ReceiveTimeout = 30_000 };
        raw.Connect("127.0.0.1", port);
        return raw;
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer)]
    [InlineData(TransportMode.Pool)]
    public void Greeting_that_arrives_after_shutdown_never_joins_and_leaves_no_state(TransportMode mode)
    {
        var site = new Site();
        var host = new TcpTransport(site, mode, poolWorkers: 2);
        TcpClient? raw = null;
        try
        {
            host.Host(0);
            raw = ConnectSilent(host.BoundPort);
            // The accept loop must own this connection as a pending greeting
            // before Shutdown is asked to close it.
            Assert.True(SpinWait.SpinUntil(() => host.LiveConnectionCount() == 1, Bound),
                "the accept loop must register the pending greeting");

            host.Shutdown();
            Assert.Empty(site.Joined);
            Assert.Equal(0, host.LiveConnectionCount());
            Assert.Equal(0, host.PerConnectionStateCount());
            Assert.True(SpinWait.SpinUntil(() => host.TrackedThreadCount() == 0, TimeSpan.FromSeconds(10)),
                $"greeting threads outlived Shutdown: {host.TrackedThreadCount()}");

            // Greet only now — the transport is already down.
            try { raw.GetStream().Write(GreetFrame("late-greeter")); }
            catch (IOException) { /* the closed socket is the point */ }
            catch (ObjectDisposedException) { /* ditto */ }
            catch (SocketException) { /* ditto */ }

            // Give a late HandlePeer every chance to publish the join.
            Thread.Sleep(500);
            Assert.Empty(site.Joined);                 // no join callback at all
            Assert.Empty(site.Left);                   // and no orphaned leave either
            Assert.Equal(0, host.LiveConnectionCount());
            Assert.Equal(0, host.PerConnectionStateCount());
            Assert.Equal(0, host.TrackedThreadCount());
        }
        finally
        {
            try { raw?.Dispose(); } catch { }
            host.Shutdown();
        }
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer)]
    [InlineData(TransportMode.Pool)]
    public void Shutdown_closes_and_joins_an_in_flight_greeting_socket(TransportMode mode)
    {
        var site = new Site();
        var host = new TcpTransport(site, mode, poolWorkers: 2);
        TcpClient? raw = null;
        try
        {
            host.Host(0);
            raw = ConnectSilent(host.BoundPort);   // connected, never greets
            Assert.True(SpinWait.SpinUntil(() => host.LiveConnectionCount() == 1, Bound),
                "the accept loop must register the pending greeting");

            host.Shutdown();

            // The greeting socket is ours: Shutdown closed it, so the socket is
            // dead from the client side and the greeting thread was joined.
            Assert.True(SpinWait.SpinUntil(() => host.TrackedThreadCount() == 0, TimeSpan.FromSeconds(10)),
                $"greeting threads outlived Shutdown: {host.TrackedThreadCount()}");
            Assert.Equal(0, host.PerConnectionStateCount());
            bool dead = false;
            for (int i = 0; i < 10 && !dead; i++)
            {
                try
                {
                    raw.GetStream().Write(GreetFrame("probe"));
                    Thread.Sleep(50);
                }
                catch (IOException) { dead = true; }
                catch (SocketException) { dead = true; }
                catch (ObjectDisposedException) { dead = true; }
            }
            Assert.True(dead, "the pending greeting socket must be closed by Shutdown");
            Thread.Sleep(300);
            Assert.Empty(site.Joined);
        }
        finally
        {
            try { raw?.Dispose(); } catch { }
            host.Shutdown();
        }
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer)]
    [InlineData(TransportMode.Pool)]
    public void Connection_churn_leaves_no_per_connection_state_and_keeps_departed_ids_closed(TransportMode mode)
    {
        var site = new Site();
        var host = new TcpTransport(site, mode, poolWorkers: 2);
        try
        {
            host.Host(0);
            const int cycles = 24;
            var departed = new List<ConnectionId>();
            for (int i = 0; i < cycles; i++)
            {
                using var raw = ConnectSilent(host.BoundPort);
                raw.GetStream().Write(GreetFrame($"p967-churn-{i}"));
                Assert.True(SpinWait.SpinUntil(() => site.Joined.Count > i, Bound),
                    $"cycle {i}: peer never joined");
                var id = site.Joined.ElementAt(i);
                departed.Add(id);
                raw.Dispose();   // abrupt drop, no LOGOFF
                Assert.True(SpinWait.SpinUntil(() => site.Left.Contains(id), Bound),
                    $"cycle {i}: peer {id} never raised OnPeerLeft");
            }

            // Exactly one leave callback per peer: the exactly-once guard must not
            // have been lost with the tombstone table.
            Assert.Equal(cycles, site.Joined.Count);
            Assert.Equal(cycles, site.Left.Distinct().Count());
            Assert.Equal(cycles, site.Left.Count);

            Assert.True(SpinWait.SpinUntil(() => host.PerConnectionStateCount() == 0, Bound),
                $"per-connection state outlived the churn: {host.PerConnectionStateCount()}");
            Assert.Equal(0, host.LiveConnectionCount());
            // Just the accept thread; every greeting and reader thread exited.
            Assert.True(SpinWait.SpinUntil(() => host.TrackedThreadCount() == 1, Bound),
                $"reaped peer threads still tracked: {host.TrackedThreadCount()}");

            // The ended-peer tombstones are now a bounded RECENT window: every
            // departure of this churn is still inside it, so each departed id
            // reports closed (late-packet suppression) while the table itself can
            // never exceed the bound.
            Assert.True(SpinWait.SpinUntil(() => host.EndedMarkerCount == cycles, Bound),
                $"ended markers not retained for the recent window: {host.EndedMarkerCount}");
            Assert.True(host.EndedMarkerCount <= TcpTransport.MaxEndedRetention);
            foreach (var id in departed) Assert.True(host.IsPeerClosed(id), $"departed id {id} must stay closed");
        }
        finally { host.Shutdown(); }
    }

    [Fact]
    public void Ended_marker_table_retires_the_oldest_drained_departures_past_its_bound()
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, poolWorkers: 2);
        try
        {
            int bound = TcpTransport.MaxEndedRetention;
            Assert.True(bound > 0 && bound <= 65536, $"unexpected ended-marker bound {bound}");
            for (int i = 1; i <= bound * 2; i++) host.AddEndedMarkerForTesting(new ConnectionId((uint)i));

            Assert.Equal(bound, host.EndedMarkerCount);
            // The oldest departures were retired; the newest window is retained.
            Assert.False(host.IsPeerClosed(new ConnectionId(1)), "the oldest arrival must be retired");
            Assert.True(host.IsPeerClosed(new ConnectionId((uint)(bound * 2))), "the newest arrival must be retained");
            Assert.True(host.IsPeerClosed(new ConnectionId((uint)(bound * 2 - bound + 1))), "the window's first id must be retained");
        }
        finally { host.Shutdown(); }
    }

    [Fact]
    public void A_marker_whose_receive_side_is_still_draining_is_never_retired()
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, poolWorkers: 2);
        try
        {
            // A marker added without the receive side draining is not eligible for
            // retirement, however far past the window the table is pushed: its
            // read thread may still deliver queued frames for that id.
            var draining = new ConnectionId(1);
            host.PendingMarkerForTesting(draining);          // state 0: still draining
            for (int i = 2; i <= TcpTransport.MaxEndedRetention * 2 + 2; i++)
                host.AddEndedMarkerForTesting(new ConnectionId((uint)i));

            Assert.True(host.IsPeerClosed(draining), "a still-draining id must never be retired");
            Assert.True(host.EndedMarkerCount <= TcpTransport.MaxEndedRetention + 1);
        }
        finally { host.Shutdown(); }
    }
}
