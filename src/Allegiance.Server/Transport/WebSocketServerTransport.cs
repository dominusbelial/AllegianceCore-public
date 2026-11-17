using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Allegiance.Core.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Allegiance.Server;

/// <summary>Direct server transport: one binary WS message is one unchanged FedPacket.
/// ASP.NET lives in Server so Core and its browser variant remain host independent.
/// Only immutable lifecycle/receive callbacks cross into ServerHost's tick queue.</summary>
public sealed class WebSocketServerTransport : IServerTransport, IDisposable
{
    public const string Subprotocol = "allegiance-fed-v1";
    public const int InboundMessageCap = 64 * 1024;
    private readonly ITransportSite _site;
    private readonly WebSocketConfig _settings;
    private readonly WebSocketAdmission _physical, _sources;
    private readonly ConcurrentDictionary<ConnectionId, Peer> _peers = new();
    private readonly ConcurrentDictionary<DisconnectReason, int> _disconnects = new();
    private WebApplication? _app;
    private long _nextId, _written, _enqueued;
    private volatile bool _running;
    // Deterministic test seam: block a real socket's single writer, without OS
    // buffer saturation or synthetic CPU load. Not exposed through configuration.
    internal Func<CancellationToken, Task>? BeforeWriteForTesting { get; set; }
    internal (int Live, int Pending, int Sources) PhysicalAdmission => _physical.Snapshot;
    internal (int Live, int Pending, int Sources) SourceAdmission => _sources.Snapshot;

    public WebSocketServerTransport(ITransportSite site, WebSocketConfig settings)
    {
        if (!settings.Enabled) throw new InvalidOperationException("WebSocket listener is disabled");
        settings.Validate(); _site = site; _settings = settings;
        _physical = new(settings); _sources = new(settings);
    }
    public string ProtocolName => "websocket";
    public bool IsConnected => _running;
    public ConnectionId LocalId => ConnectionId.Me;
    public ushort BoundPort { get; private set; }
    public long TotalWrittenBytes => Interlocked.Read(ref _written);
    public long TotalEnqueuedBytes => Interlocked.Read(ref _enqueued);
    public EgressAccounting? Egress { get; init; }
    public ReliableTelemetry? Reliable { get; init; }
    public ServerCounters? ServerCounters { get; init; }
    public void Join(string address, ushort port, string playerName) => throw new NotSupportedException("Server listener only");
    public void Host(ushort port = 0) => Host(port, null);
    public void Host(ushort port, IPAddress? bindAddress)
    {
        if (_app is not null) throw new InvalidOperationException("Already hosted");
        // Slim host: no reflection-based routing, MVC, configuration binding,
        // static files, forwarded-host trust or runtime ASP.NET dependency under AOT.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // An inherited ASPNETCORE_FORWARDEDHEADERS_ENABLED=true must not install
        // the framework's permissive automatic forwarding ahead of our policy.
        builder.WebHost.UseSetting("forwardedHeaders_enabled", "false");
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = _settings.MaxConnections;
            options.Limits.MaxConcurrentUpgradedConnections = _settings.MaxConnections;
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(_settings.HandshakeTimeoutSeconds);
            options.Listen(bindAddress ?? IPAddress.Parse(_settings.BindAddress), port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.Use(next => async connection =>
                {
                    var address = (connection.RemoteEndPoint as IPEndPoint)?.Address;
                    if (address is null || !_physical.TryAttempt(address, Environment.TickCount64)) { connection.Abort(); return; }
                    using var lease = _physical.TryEnter(address);
                    if (lease is null) { connection.Abort(); return; }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.HandshakeTimeoutSeconds));
                    using var registration = deadline.Token.Register(() => connection.Abort());
                    var admission = new UpgradeAdmission(lease, registration, Environment.TickCount64);
                    connection.Features.Set(admission);
                    await next(connection).ConfigureAwait(false);
                });
            });
        });
        var forwarding = new ForwardedHeadersOptions
        {
            ForwardedHeaders = _settings.KnownProxies.Length == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1, RequireHeaderSymmetry = true,
        };
        forwarding.KnownProxies.Clear(); forwarding.KnownIPNetworks.Clear();
        foreach (string proxy in _settings.KnownProxies) forwarding.KnownProxies.Add(IPAddress.Parse(proxy));
        var app = builder.Build(); _app = app;
        app.UseForwardedHeaders(forwarding);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Run(HandleRequest);
        try
        {
            _running = true;
            app.StartAsync().GetAwaiter().GetResult();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            BoundPort = checked((ushort)new Uri(address).Port);
        }
        catch { Shutdown(); throw; }
    }

    private sealed class UpgradeAdmission(WebSocketAdmission.Lease lease, CancellationTokenRegistration deadline, long accepted)
    {
        internal long Accepted => accepted;
        internal void Complete() { deadline.Dispose(); lease.Promoted(); }
    }

    private async Task HandleRequest(HttpContext context)
    {
        // Every HTTP upgrade attempt is charged, including failed Origin/protocol
        // requests on a persistent connection. Header strings are never logged.
        var address = context.Connection.RemoteIpAddress;
        context.Response.Headers.Connection = "close";
        if (!_running || address is null || !_sources.TryAttempt(address, Environment.TickCount64))
        { context.Response.StatusCode = 429; return; }
        if (context.Request.Path != _settings.Path || context.Request.QueryString.HasValue || !HttpMethods.IsGet(context.Request.Method))
        { context.Response.StatusCode = 404; return; }
        if (!context.Request.IsHttps && !(_settings.AllowLoopbackDevelopment && IPAddress.IsLoopback(address)
            && _settings.KnownProxies.Length == 0 && IPAddress.IsLoopback(context.Connection.LocalIpAddress!)))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Headers.Origin.Count != 1 || !_settings.AllowedOrigins.Contains(context.Request.Headers.Origin.ToString(), StringComparer.Ordinal))
        { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest || context.WebSockets.WebSocketRequestedProtocols.Count != 1
            || context.WebSockets.WebSocketRequestedProtocols[0] != Subprotocol)
        { context.Response.StatusCode = 400; return; }
        var upgrade = context.Features.Get<UpgradeAdmission>();
        if (upgrade is not null && Environment.TickCount64 - upgrade.Accepted >= _settings.HandshakeTimeoutSeconds * 1000)
        { context.Response.StatusCode = 408; return; }
        using var lease = _sources.TryEnter(address);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        long value = Interlocked.Increment(ref _nextId);
        if (value > uint.MaxValue) { context.Response.StatusCode = 503; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(Subprotocol).ConfigureAwait(false);
        context.Features.Get<UpgradeAdmission>()?.Complete(); lease.Promoted();
        var peer = new Peer(new ConnectionId((uint)value), WebSocketAdmission.Canonical(address), socket,
            context.Features.Get<UpgradeAdmission>()?.Accepted ?? Environment.TickCount64);
        peer.Queue = new WebSocketSendQueue(_settings, (data, key, guaranteed, ev) =>
        {
            Egress?.RecordFedPacket(data, !peer.Human, key != 0, ev);
            var cls = key == 0 ? ReliableClass.OneShot : guaranteed ? ReliableClass.ReliableStream : ReliableClass.BestEffortStream;
            if (ev == EgressEvent.Coalesced) Reliable?.RecordCoalesced(cls);
            else Reliable?.RecordShed(cls);
        });
        // Shutdown cannot miss a socket adopted after its snapshot.
        lock (_peers)
        {
            if (!_running) { socket.Abort(); peer.Dispose(); return; }
            _peers.TryAdd(peer.Id, peer);
            _site.OnPeerJoined(peer.Id, ""); // name comes only from validated LOGONREQ
        }
        using var aborted = context.RequestAborted.Register(() => Close(peer, DisconnectReason.ReadErrorOrEof));
        Task writer = WriteLoop(peer), monitor = MonitorPeer(peer);
        try { await ReadLoop(peer).ConfigureAwait(false); }
        finally
        {
            Close(peer, DisconnectReason.ReadErrorOrEof);
            await Task.WhenAll(writer, monitor).ConfigureAwait(false);
            _peers.TryRemove(peer.Id, out _);
            _site.OnPeerLeft(peer.Id); // read callbacks ended; exactly once, and last
            peer.Dispose();
        }
    }
    private sealed class Peer(ConnectionId id, IPAddress address, WebSocket socket, long accepted) : IDisposable
    {
        internal readonly ConnectionId Id = id;
        internal readonly IPAddress Address = address;
        internal readonly WebSocket Socket = socket;
        internal readonly object Gate = new();
        internal readonly CancellationTokenSource Stop = new();
        internal WebSocketSendQueue Queue = null!;
        internal readonly long Accepted = accepted;
        internal bool Authenticated, Human, Closed, Flush;
        public void Dispose() { Queue.Ready.Dispose(); Stop.Dispose(); }
    }
    private async Task ReadLoop(Peer peer)
    {
        // Never allocate the peer's declared length. Actual arrival uses a fixed
        // capped buffer, including one overflow byte so a cap-sized final frame works.
        int maximum = (int)Math.Min(InboundMessageCap, TcpTransport.ServerInboundMaxFrame);
        byte[] data = ArrayPool<byte>.Shared.Rent(maximum + 1);
        try
        {
            while (!peer.Stop.IsCancellationRequested)
            {
                int used = 0, fragments = 0; long start = 0;
                while (true)
                {
                    int cap; lock (peer.Gate) cap = peer.Authenticated ? maximum : Math.Min(maximum, _settings.PreLogonMaxMessageBytes);
                    double seconds = fragments == 0 ? _settings.IdleTimeoutSeconds : Math.Min(_settings.FragmentProgressTimeoutSeconds,
                        _settings.MessageTimeoutSeconds - (Environment.TickCount64 - start) / 1000d);
                    if (seconds <= 0) { Close(peer, DisconnectReason.ProtocolError); return; }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(peer.Stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                    ValueWebSocketReceiveResult result;
                    try
                    {
                        result = await peer.Socket.ReceiveAsync(data.AsMemory(used, Math.Min(4096, cap + 1 - used)), timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { Close(peer, DisconnectReason.ReadErrorOrEof); return; }
                    long now = Environment.TickCount64;
                    bool authExpired;
                    lock (peer.Gate) authExpired = !peer.Authenticated && now - peer.Accepted >= _settings.AuthenticationTimeoutSeconds * 1000;
                    if (authExpired) { Close(peer, DisconnectReason.PreLogonTimeout); return; }
                    if (fragments != 0 && now - start >= _settings.MessageTimeoutSeconds * 1000)
                    { Close(peer, DisconnectReason.ProtocolError); return; }
                    if (fragments == 0) start = now;
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Binary || ++fragments > _settings.MaxFragments || (used += result.Count) > cap)
                    { Close(peer, DisconnectReason.ProtocolError); return; }
                    if (result.EndOfMessage) break;
                }
                // ServerHost reserves event + byte capacity, validates CRC/cbmsg/
                // message count and applies commands only in its fair tick drain.
                if (!IsPeerClosed(peer.Id)) _site.OnReceive(peer.Id, data.AsSpan(0, used));
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { }
        finally { ArrayPool<byte>.Shared.Return(data); }
    }
    private async Task WriteLoop(Peer peer)
    {
        try
        {
            while (!peer.Stop.IsCancellationRequested)
            {
                await peer.Queue.Ready.WaitAsync(peer.Stop.Token).ConfigureAwait(false);
                while (true)
                {
                    WebSocketSendQueue.Frame? frame;
                    bool flush;
                    lock (peer.Gate) { frame = peer.Queue.Take(); flush = peer.Flush; }
                    if (frame is null)
                    {
                        if (!flush) break;
                        // This same writer sends the final close frame after all
                        // data. Abort only after the bounded close handshake, so
                        // Kestrel cannot discard the final reply still in its pipe.
                        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(peer.Stop.Token);
                        closeTimeout.CancelAfter(TimeSpan.FromSeconds(_settings.SendTimeoutSeconds));
                        await peer.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeTimeout.Token).ConfigureAwait(false);
                        peer.Stop.CancelAfter(TimeSpan.FromSeconds(Math.Min(2, _settings.SendTimeoutSeconds)));
                        return;
                    }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(peer.Stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(_settings.SendTimeoutSeconds));
                    if (BeforeWriteForTesting is { } before) await before(timeout.Token).ConfigureAwait(false);
                    await peer.Socket.SendAsync(frame.Data, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
                    Egress?.RecordFedPacket(frame.Data, !peer.Human, frame.Key != 0, EgressEvent.Sent);
                    Interlocked.Add(ref _written, WireLength(frame.Data.Length));
                    lock (peer.Gate) peer.Queue.Complete();
                }
            }
        }
        catch (OperationCanceledException) { Close(peer, DisconnectReason.SendTimeout); }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { Close(peer, DisconnectReason.SendError); }
    }
    private async Task MonitorPeer(Peer peer)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Math.Min(_settings.AuthenticationTimeoutSeconds, _settings.MaxGuaranteedAgeSeconds) / 4, .05, 1)), peer.Stop.Token).ConfigureAwait(false);
                DisconnectReason reason = DisconnectReason.None;
                lock (peer.Gate)
                {
                    long now = Environment.TickCount64;
                    if (!peer.Authenticated && now - peer.Accepted >= _settings.AuthenticationTimeoutSeconds * 1000)
                        reason = DisconnectReason.PreLogonTimeout;
                    else if (peer.Queue.Sample(now).Age >= _settings.MaxGuaranteedAgeSeconds * 1000)
                        reason = DisconnectReason.NoWriteProgress;
                }
                if (reason != DisconnectReason.None) { Close(peer, reason); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Close(Peer peer, DisconnectReason reason)
    {
        lock (peer.Gate)
        {
            if (peer.Closed) return;
            peer.Closed = true; peer.Queue.Close();
            if (peer.Flush && reason == DisconnectReason.ReadErrorOrEof) reason = DisconnectReason.Shutdown;
            _disconnects.AddOrUpdate(reason, 1, (_, n) => n + 1);
            peer.Stop.Cancel(); peer.Socket.Abort();
        }
    }
    public IPAddress? PeerAddress(ConnectionId id) => _peers.GetValueOrDefault(id)?.Address;
    public bool IsPeerLoopback(ConnectionId id) => false; // includes direct loopback and trusted proxies
    public bool IsPeerHuman(ConnectionId id) => _peers.TryGetValue(id, out var p) && ReadFlag(p, human: true);
    public bool IsPeerClosed(ConnectionId id)
    {
        if (!_peers.TryGetValue(id, out var p)) return true;
        bool expired;
        lock (p.Gate)
        {
            if (p.Closed || p.Flush) return true;
            expired = !p.Authenticated && Environment.TickCount64 - p.Accepted >= _settings.AuthenticationTimeoutSeconds * 1000;
        }
        if (expired) Close(p, DisconnectReason.PreLogonTimeout);
        return expired;
    }
    private static bool ReadFlag(Peer p, bool human) { lock (p.Gate) return human ? p.Human : p.Closed; }
    public void SetPeerHuman(ConnectionId id, bool human) { if (_peers.TryGetValue(id, out var p)) lock (p.Gate) p.Human = human; }
    public void MarkPeerAuthenticated(ConnectionId id)
    {
        if (!_peers.TryGetValue(id, out var p)) return;
        bool expired;
        lock (p.Gate)
        {
            expired = !p.Authenticated && Environment.TickCount64 - p.Accepted >= _settings.AuthenticationTimeoutSeconds * 1000;
            if (!expired) p.Authenticated = true;
        }
        if (expired) Close(p, DisconnectReason.PreLogonTimeout);
    }
    public void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown) { if (_peers.TryGetValue(id, out var p)) Close(p, reason); }
    public void ClosePeerAfterFlush(ConnectionId id)
    {
        if (!_peers.TryGetValue(id, out var p)) return;
        lock (p.Gate)
        {
            if (p.Closed) return;
            p.Flush = true;
            if (p.Queue.Ready.CurrentCount == 0) p.Queue.Ready.Release();
        }
    }
    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => Enqueue(target, data, 0, guaranteed);
    public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Enqueue(target, data, coalesceKey, false);
    public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Enqueue(target, data, coalesceKey, true);
    private void Enqueue(ConnectionId id, ReadOnlySpan<byte> data, int key, bool guaranteed)
    {
        if (!_peers.TryGetValue(id, out var p)) return;
        DisconnectReason reason;
        lock (p.Gate)
        {
            if (p.Closed || p.Flush) return;
            Egress?.RecordFedPacket(data, !p.Human, key != 0, EgressEvent.Offered);
            ServerCounters?.RecordPacket(data, guaranteed);
            if (key == 0 && guaranteed) Reliable?.RecordOneShotParked(false);
            reason = p.Queue.Enqueue(data, key, guaranteed, Environment.TickCount64);
            Interlocked.Add(ref _enqueued, WireLength(data.Length));
        }
        if (reason != DisconnectReason.None) Close(p, reason);
    }
    private static long WireLength(int bytes) => (long)bytes + (bytes < 126 ? 2 : bytes <= ushort.MaxValue ? 4 : 10);
    public IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts()
    {
        var result = new Dictionary<DisconnectReason, int>();
        foreach (var reason in _disconnects.Keys)
            if (_disconnects.TryRemove(reason, out int count)) result[reason] = count;
        return result;
    }
    public (int Peers, int MaxOrderedPending, int MaxStreamPending, long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs) SamplePeerQueues()
    {
        int peers = 0, ordered = 0, stream = 0; long maxBytes = 0, bytes = 0, age = 0;
        foreach (var p in _peers.Values)
        {
            lock (p.Gate)
            {
                if (p.Closed) continue;
                var q = p.Queue.Sample(Environment.TickCount64); peers++;
                ordered = Math.Max(ordered, q.Ordered); stream = Math.Max(stream, q.Stream);
                maxBytes = Math.Max(maxBytes, q.Bytes); bytes += q.Bytes; age = Math.Max(age, q.Age);
            }
        }
        return (peers, ordered, stream, maxBytes, bytes, age);
    }
    public void Shutdown()
    {
        WebApplication? app;
        lock (_peers) { _running = false; app = _app; _app = null; foreach (var p in _peers.Values) Close(p, DisconnectReason.Shutdown); }
        if (app is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { app.StopAsync(deadline.Token).GetAwaiter().GetResult(); }
        finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    public void Dispose() => Shutdown();
}
