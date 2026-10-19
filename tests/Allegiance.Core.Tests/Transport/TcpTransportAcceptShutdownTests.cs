using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class TcpTransportAcceptShutdownTests
{
    private sealed class Site : ITransportSite
    {
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer, false)]
    [InlineData(TransportMode.Pool, false)]
    [InlineData(TransportMode.ThreadPerPeer, true)]
    [InlineData(TransportMode.Pool, true)]
    public void Stop_between_running_check_and_accept_exits_even_after_restart(TransportMode mode, bool restart)
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var host = new TcpTransport(new Site(), mode, poolWorkers: 2);
        int accepts = 0;
        host.BeforeAcceptForTesting = () =>
        {
            if (Interlocked.Increment(ref accepts) != 1) return;
            entered.Set();
            resume.Wait();
        };
        Thread? shutdown = null;
        Exception? shutdownError = null;
        try
        {
            host.Host(0);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "accept loop must reach the controlled race window");
            shutdown = new Thread(() =>
            {
                try { host.Shutdown(); }
                catch (Exception ex) { shutdownError = ex; }
            }) { IsBackground = true };
            shutdown.Start();
            // Shutdown bounds its joins. It closes the listener while the old
            // accept loop remains paused after its successful running check.
            Assert.True(shutdown.Join(TimeSpan.FromSeconds(30)), "shutdown must finish with the accept loop paused");
            Assert.Null(shutdownError);
            Assert.False(host.IsConnected);
            if (restart) host.Host(0);

            // The old code now calls AcceptTcpClient on a stopped listener and
            // crashes the test host. On restart _running is true again, so an
            // exception filter must recognize the old listener's identity too.
            resume.Set();
            Assert.True(SpinWait.SpinUntil(() => host.TrackedThreadCount() == (restart ? 1 : 0),
                TimeSpan.FromSeconds(30)), "old accept thread must exit without affecting a restarted host");
            if (restart) Assert.True(host.IsConnected);
        }
        finally
        {
            resume.Set();
            host.Shutdown();
            if (shutdown is not null) Assert.True(shutdown.Join(TimeSpan.FromSeconds(30)));
            Assert.True(SpinWait.SpinUntil(() => host.TrackedThreadCount() == 0,
                TimeSpan.FromSeconds(30)), "no accept thread may outlive final cleanup");
        }
    }
}
