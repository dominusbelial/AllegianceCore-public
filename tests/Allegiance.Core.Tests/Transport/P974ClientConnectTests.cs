using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

public sealed class P974ClientConnectTests
{
    private sealed class Site : ITransportSite
    {
        internal int Callbacks;
        public void OnPeerJoined(ConnectionId id, string name) => Interlocked.Increment(ref Callbacks);
        public void OnPeerLeft(ConnectionId id) => Interlocked.Increment(ref Callbacks);
        public void OnSessionLost() => Interlocked.Increment(ref Callbacks);
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Interlocked.Increment(ref Callbacks);
    }

    [Fact]
    public async Task Shutdown_during_connect_rejects_a_late_real_socket_before_starting_workers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var site = new Site();
        var transport = new TcpTransport(site);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Socket? socket = null;
        transport.ConnectClientForTesting = async (client, address, port, ct) =>
        {
            socket = client.Client;
            await client.ConnectAsync(address, port, ct);
            entered.SetResult();
            await release.Task; // Deliberately ignore cancellation at this boundary.
        };
        var attempt = transport.JoinAsync("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port, "LATE");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            transport.Shutdown();
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(socket!.SafeHandle.IsClosed);
        Assert.False(transport.IsConnected);
        Assert.Equal(0, site.Callbacks);
        transport.Shutdown();
    }

    [Fact]
    [Trait("Tier", "Live")]
    public async Task A_stalled_connector_has_a_ten_second_deadline_without_a_caller_token()
    {
        var transport = new TcpTransport(new Site());
        Socket? socket = null;
        transport.ConnectClientForTesting = async (client, address, port, ct) =>
        {
            socket = client.Client;
            await Task.Delay(Timeout.Infinite, ct);
        };
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => transport.JoinAsync("127.0.0.1", 1234, "TIMEOUT")
                .WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.True(socket!.SafeHandle.IsClosed);
            Assert.False(transport.IsConnected);
        }
        finally { transport.Shutdown(); }
    }
}
