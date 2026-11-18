using System.Net.WebSockets;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Transport;

/// <summary>Direct binary FedPackets, shared by desktop and single-threaded WASM.
/// Client publication/loss mirrors TcpTransport.cs:480-563,1377-1391;
/// serialized receive mirrors :1397-1455. No greeting is used by the WS protocol.</summary>
public sealed class WebSocketTransport(ITransportSite site, WebSocketEndpoint endpoint, string? origin = null) : ITransport, IDisposable, IAsyncDisposable
{
    public const string Subprotocol = "allegiance-fed-v1";
    public const int MessageCap = 64 * 1024;
    private readonly object _gate = new();
    private readonly WebSocketClientSendQueue _queue = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ClientWebSocket? _socket;
    private bool _used, _intentional, _lost, _connected;
    private Task _receive = Task.CompletedTask, _send = Task.CompletedTask;
    public string ProtocolName => endpoint.Uri.Scheme;
    public bool IsConnected { get { lock (_gate) return _connected; } }
    public ConnectionId LocalId => ConnectionId.Me;
    public ushort BoundPort => 0;
    public void Host(ushort port = 0) => throw new NotSupportedException("WebSocketTransport is client only");
    public void Join(string address, ushort port, string playerName)
    {
        if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Use JoinAsync in the browser");
        JoinAsync(address, port, playerName).GetAwaiter().GetResult();
    }
    public async Task JoinAsync(string address, ushort port, string playerName, CancellationToken cancellationToken = default)
    {
        ClientWebSocket socket;
        lock (_gate)
        {
            if (_used || _intentional) throw new InvalidOperationException("transport already used");
            _used = true;
            socket = _socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(Subprotocol);
            if (origin is not null)
            {
                if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Browser supplies Origin");
                socket.Options.SetRequestHeader("Origin", origin);
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
#if BROWSER
            // The browser WebSocket API uses redirect mode "error" for its
            // opening handshake; it never follows HTTP redirects.
            await socket.ConnectAsync(endpoint.Uri, deadline.Token).ConfigureAwait(false);
#else
            using var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
            await socket.ConnectAsync(endpoint.Uri, invoker, deadline.Token).ConfigureAwait(false);
#endif
            lock (_gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (socket.SubProtocol != Subprotocol) throw new WebSocketException("Server did not select allegiance-fed-v1");
                _connected = true;
                _receive = ReceiveAsync(socket);
                _send = SendAsync(socket);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { socket.Dispose(); throw new TimeoutException("timed out connecting to the WebSocket server"); }
        catch { socket.Dispose(); throw; }
    }
    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => Enqueue(data, 0, guaranteed);
    public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Enqueue(data, coalesceKey, false);
    public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Enqueue(data, coalesceKey, true);
    private void Enqueue(ReadOnlySpan<byte> data, int key, bool guaranteed)
    {
        if (data.Length < FedPacket.CrcWidth || data.Length > MessageCap) throw new ArgumentOutOfRangeException(nameof(data));
        lock (_gate)
        {
            if (!_connected) throw new InvalidOperationException("not connected");
            if (_queue.Enqueue(data, key, guaranteed, Environment.TickCount64))
            { if (_ready.CurrentCount == 0) _ready.Release(); return; }
        }
        Abort();
    }
    private async Task SendAsync(ClientWebSocket socket)
    {
        await Task.Yield(); // publish task ownership before any socket continuation
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _ready.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    WebSocketClientSendQueue.Frame? frame;
                    lock (_gate) frame = _queue.Take();
                    if (frame is null) break;
                    if (frame.Guaranteed && Environment.TickCount64 - frame.Since > 60_000) throw new IOException("WebSocket guaranteed queue expired");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    await socket.SendAsync(frame.Data.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
                    lock (_gate) _queue.Complete();
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
#if BROWSER
            if (!_lifetime.IsCancellationRequested) Console.WriteLine($"[browser] WS send stopped: {ex.GetType().Name}: {ex.Message}");
#endif
        }
        finally { Abort(); }
    }
    private async Task ReceiveAsync(ClientWebSocket socket)
    {
        await Task.Yield(); // no callback while Join holds the transport gate
        var buffer = new byte[MessageCap + 1];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                int length = 0, fragments = 0;
                using var message = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                // First read may idle; subsequent reads also have a progress deadline.
                message.CancelAfter(TimeSpan.FromSeconds(30));
                while (true)
                {
                    using var progress = CancellationTokenSource.CreateLinkedTokenSource(message.Token);
                    if (fragments != 0) progress.CancelAfter(TimeSpan.FromSeconds(5));
                    var part = await socket.ReceiveAsync(buffer.AsMemory(length), progress.Token).ConfigureAwait(false);
                    if (part.MessageType != WebSocketMessageType.Binary || ++fragments > 256) return;
                    length += part.Count;
                    if (length > MessageCap) return;
                    if (part.EndOfMessage) break;
                }
                if (length < FedPacket.CrcWidth) return;
                // The sole reader serializes receive then loss, as TCP does.
                if (!IsConnected) return;
                site.OnReceive(ConnectionId.Server, buffer.AsSpan(0, length));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or ArgumentException)
        {
#if BROWSER
            if (!_lifetime.IsCancellationRequested) Console.WriteLine($"[browser] WS receive stopped: {ex.GetType().Name}: {ex.Message}");
#endif
        }
        finally { LoseSession(); }
    }
    private void Abort()
    {
        lock (_gate) { _connected = false; _lifetime.Cancel(); _socket?.Abort(); _queue.Clear(); }
    }
    private void LoseSession()
    {
        lock (_gate)
        {
            if (_intentional || _lost) return;
            _lost = true; _connected = false;
            _lifetime.Cancel(); _socket?.Abort(); _queue.Clear();
        }
        site.OnSessionLost();
    }
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_intentional) return;
            _intentional = true; _connected = false;
            _lifetime.Cancel(); _socket?.Abort(); _socket?.Dispose(); _queue.Clear();
        }
    }
    /// <summary>Allows async owners to drain socket continuations without blocking WASM.</summary>
    public async ValueTask DisposeAsync() { Shutdown(); await Task.WhenAll(_receive, _send).ConfigureAwait(false); }
    public void Dispose() => Shutdown();
}
