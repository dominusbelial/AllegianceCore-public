using System.Reflection;
using System.Net;
using System.Net.Sockets;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P974SessionOwnershipTests
{
    private static void Factory(ConnectionSession session, Func<ITransportSite, ITransport> factory)
        => typeof(ConnectionSession).GetProperty("TransportFactoryForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, factory);

    [Fact]
    public async Task Dispose_during_real_socket_establishment_cancels_and_drains_the_connector()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); // Deliberately never Accept.
        ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        using var session = new ConnectionSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Socket? socket = null;
        TcpTransport? transport = null;
        Factory(session, site =>
        {
            transport = new TcpTransport(site);
            // Hold the actual connector at its post-ConnectAsync boundary. This
            // gives a deterministic stalled call with a real owned TCP socket.
            Func<TcpClient, string, ushort, CancellationToken, ValueTask> connect = async (client, address, p, ct) =>
            {
                socket = client.Client;
                await client.ConnectAsync(address, p, ct);
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            };
            typeof(TcpTransport).GetProperty("ConnectClientForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(transport, connect);
            return transport;
        });
        var attempt = session.ConnectAsync(port, "STALLED");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Run(session.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.True(socket!.SafeHandle.IsClosed);
        Assert.False(transport!.IsConnected);
        Assert.False(session.Connected);
        Assert.Null(typeof(ConnectionSession).GetField("_keepAliveThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
    }

    [Fact]
    public async Task Cancelling_identity_never_sends_logon_and_releases_the_transport()
    {
        using var release = new ManualResetEventSlim(true);
        using var session = new ConnectionSession();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DelayedTransport? transport = null;
        Factory(session, site => transport = new DelayedTransport(site, null, release));
        var attempt = session.JoinAsync(1234, "IDENTITY", new LogonRequest(), identityTicket: async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "unreachable";
        }, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, transport!.Sent);
        Assert.False(transport.IsConnected);
        Assert.Null(session.Ack);
    }

    [Fact]
    public async Task Overall_deadline_includes_the_join_answer_and_never_accepts_silence()
    {
        using var release = new ManualResetEventSlim(true);
        using var session = new ConnectionSession();
        DelayedTransport? transport = null;
        Factory(session, site => transport = new DelayedTransport(site, null, release));
        // Connector answers LOGONREQ with a valid ACK, but no join admission.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => session.JoinAsync(1234, "NO-ANSWER", new LogonRequest(),
            timeout: TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.NotNull(session.Ack);
        Assert.False(transport!.IsConnected);
        Assert.False(session.Connected);
    }

    [Fact]
    public async Task Cancelling_udp_handshake_never_opens_a_fallback_tcp_socket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); // No UDP listener: this handshake stalls.
        using var session = new ConnectionSession { TransportProtocol = "udp" };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(
            (ushort)((IPEndPoint)listener.LocalEndpoint).Port, "UDP", cancellationToken: cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(listener.Pending());
        Assert.False(session.Connected);
        Assert.False(session.Lost);
    }

    [Fact]
    public async Task Mission_departure_drains_without_holding_the_world_lock_and_loss_can_dispose()
    {
        using var release = new ManualResetEventSlim(true);
        using var session = new ConnectionSession();
        DelayedTransport? transport = null;
        Factory(session, site => transport = new DelayedTransport(site, null, release));
        session.Connect(1234, "DEPART");
        P718InterfaceMessageTests.Seed(session);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SessionLost += () => { session.Dispose(); notified.TrySetResult(); };
        var receive = Task.Run(() => transport!.Receive(FedPacket.Pack(new QuitMission
            { ShipId = 10, Reason = QuitSideReason.LinkDead, Message = "departed" }.Encode())));
        await receive.WaitAsync(TimeSpan.FromSeconds(3));
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.IsDisposed);
        Assert.True(session.Lost);
        Assert.False(transport!.IsConnected);
        Assert.False(session.Connected);
    }

    [Fact]
    public async Task Reconnect_expiry_notifies_outside_the_world_lock_so_loss_can_dispose()
    {
        using var release = new ManualResetEventSlim(true);
        using var session = new ConnectionSession();
        DelayedTransport? transport = null;
        Factory(session, site => transport is null ? transport = new DelayedTransport(site, null, release)
            : throw new IOException("endpoint unavailable"));
        session.EnableAutoReconnect(0.1);
        session.Connect(1234, "EXPIRE");
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SessionLost += () => { session.Dispose(); notified.TrySetResult(); };
        transport!.Lose();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.IsDisposed);
        Assert.True(session.Lost);
        Assert.False(session.Reconnecting);
        Assert.False(session.Connected);
    }

    [Fact]
    public async Task Explicit_connect_drains_late_reconnect_before_publishing_a_replacement()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new ConnectionSession();
        var transports = new List<DelayedTransport>();
        Factory(session, site =>
        {
            var transport = new DelayedTransport(site, transports.Count == 1 ? entered : null, release);
            transports.Add(transport);
            return transport;
        });
        session.EnableAutoReconnect(8);
        session.Connect(1234, "OLD");
        transports[0].Lose();
        Assert.True(entered.Wait(3000), "reconnect did not enter connect");
        var next = Task.Run(() => session.Connect(1234, "NEW"));
        try
        {
            // Longer than the base's 1500 ms timed thread join. A deliberately
            // uncooperative connector must remain owned until it actually exits.
            await Task.Delay(1800);
            Assert.False(next.IsCompleted, "replacement published while the old connector still runs");
            Assert.Equal(2, transports.Count);
        }
        finally { release.Set(); await next.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(3, transports.Count);
        Assert.True(transports[2].IsConnected);
        Assert.False(transports[1].IsConnected);
        long generation = session.ConnectionGeneration;
        transports[1].Ack(71);
        transports[1].Lose();
        Assert.Null(session.Ack);
        Assert.Equal(generation, session.ConnectionGeneration);
        Assert.False(session.Lost);
        Assert.True(session.Connected);
    }

    [Fact]
    public async Task Dispose_drains_a_stalled_reconnect_and_closes_its_late_socket()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new ConnectionSession();
        int created = 0;
        DelayedTransport? stalled = null;
        Factory(session, site => ++created == 1 ? new DelayedTransport(site, null, release)
            : stalled = new DelayedTransport(site, entered, release));
        session.EnableAutoReconnect(8);
        session.Connect(1234, "DISPOSE");
        ((ITransportSite)session).OnSessionLost();
        Assert.True(entered.Wait(3000));
        var dispose = Task.Run(session.Dispose);
        try
        {
            await Task.Delay(1800);
            Assert.False(dispose.IsCompleted, "Dispose returned before the connector ended");
        }
        finally { release.Set(); await dispose.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.False(stalled!.IsConnected);
        Assert.False(session.Connected);
        Assert.False(session.Reconnecting);
        Assert.Throws<ObjectDisposedException>(() => session.Connect(1234, "AFTER"));
        Assert.Equal(2, created);
        session.Dispose();
    }

    private sealed class DelayedTransport(ITransportSite site, ManualResetEventSlim? entered, ManualResetEventSlim release) : ITransport
    {
        public bool IsConnected { get; private set; }
        public ConnectionId LocalId => ConnectionId.Me;
        public ushort BoundPort => 0;
        public int Sent { get; private set; }
        public void Host(ushort port = 0) => throw new NotSupportedException();
        public void Join(string address, ushort port, string playerName)
        {
            if (entered is not null)
            {
                entered.Set();
                if (!release.Wait(6000)) throw new TimeoutException("test did not release connect");
            }
            IsConnected = true;
        }
        public void Shutdown() => IsConnected = false;
        public void Lose() { IsConnected = false; site.OnSessionLost(); }
        public void Receive(byte[] packet) => site.OnReceive(ConnectionId.Server, packet);
        public void Ack(short ship) => site.OnReceive(ConnectionId.Server,
            FedPacket.Pack([new LogonAck { FValidated = true, ShipID = ship }.Encode()]));
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) { Sent++; Ack(71); }
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) { }
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) { }
    }
}
