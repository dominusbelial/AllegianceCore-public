using System.Net;
using System.Net.Sockets;

namespace Allegiance.Core.Transport;

public sealed partial class UdpTransport
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly SocketAsyncEventArgs _receiveArgs = new();
    private bool _receivePending;
    private volatile bool _receiveReady;
    private long _receiveSignals, _enqueueSignals, _timerWakes;
    private Timer? _clientTimer;

    // Clients receive on their existing worker with a real blocking syscall.
    // Producers send immediately under the state lock; a one-shot timer handles
    // pacing, delayed ACKs, retransmissions and keepalive/idle deadlines. This
    // avoids an I/O-completion-to-worker context switch for every incoming pose.
    private void RunClient()
    {
        // Shutdown from a timer callback can dispose/clear the field without
        // joining this worker (the callback owns the delivery lock). Retain the
        // socket object so that boundary races report disposal, never null access.
        var socket = _socket;
        if (socket is null) return;
        try
        {
            lock (_gate) ServiceClient(Environment.TickCount64);
            while (_running)
            {
                EndPoint ep = new IPEndPoint(IPAddress.IPv6Any, 0);
                int count;
                try { count = socket.ReceiveFrom(_receive, ref ep); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
                lock (_gate)
                {
                    if (!_running) break;
                    _workerTurns++;
                    _receiveSignals++;
                    Interlocked.Add(ref _received, count);
                    long now = Environment.TickCount64;
                    if (count <= Mtu) Receive(_receive.AsSpan(0, count), Normalize((IPEndPoint)ep), now);
                    ServiceClient(now);
                }
                DrainCallbacks();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            lock (_gate)
            {
                if (_running)
                    foreach (var p in _peers.Values.ToArray()) Drop(p, $"UDP socket: {ex.Message}");
                _running = false;
            }
        }
        finally { DrainCallbacks(); }
    }

    private void ServiceClient(long now)
    {
        if (!_running) return;
        try
        {
            if (!IsConnected && _hello is not null && now - _lastHandshake >= 200)
            { RawSend(_hello, _remote!); _lastHandshake = now; }
            _tickPeers.Clear(); _tickPeers.AddRange(_peers.Values);
            foreach (var p in _tickPeers) Tick(p, now);
            _clientTimer!.Change(NextWait(Environment.TickCount64), Timeout.Infinite);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            // Producer/timer sends share the receiver's failure semantics. A
            // socket error on a ThreadPool timer must never escape that callback.
            foreach (var p in _peers.Values.ToArray()) Drop(p, $"UDP socket: {ex.Message}");
            _running = false;
            _socket?.Dispose(); // Wake the receiver, which drains loss callbacks.
        }
    }

    private void ClientTimer(object? state)
    {
        lock (_gate)
        {
            if (!_running) return;
            _workerTurns++;
            _timerWakes++;
            ServiceClient(Environment.TickCount64);
        }
        DrainCallbacks();
    }

    private void ReceiveCompleted(object? sender, SocketAsyncEventArgs args)
    {
        _receiveReady = true;
        Interlocked.Increment(ref _receiveSignals);
        // Socket cancellation can finish after an idempotent Shutdown/Dispose.
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    // One outstanding receive, one buffer and one owner of protocol state/callbacks.
    // Completion only signals; no application callback runs on the socket I/O thread.
    private void ReceiveAvailable(long now)
    {
        for (int i = 0; i < 256 && _running; i++)
        {
            if (!_receivePending)
            {
                _receiveReady = false;
                _receiveArgs.RemoteEndPoint = _socket!.AddressFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
                _receivePending = true;
                if (_socket.ReceiveFromAsync(_receiveArgs)) return;
                _receiveReady = true;
            }
            if (!_receiveReady) return;
            _receivePending = false;
            if (_receiveArgs.SocketError is SocketError.ConnectionReset or SocketError.MessageSize) continue;
            if (_receiveArgs.SocketError != SocketError.Success) throw new SocketException((int)_receiveArgs.SocketError);
            int count = _receiveArgs.BytesTransferred;
            Interlocked.Add(ref _received, count);
            if (count <= Mtu) Receive(_receive.AsSpan(0, count), Normalize((IPEndPoint)_receiveArgs.RemoteEndPoint!), now);
        }
        // A bounded receive turn under continuous traffic must resume without a timer poll.
        _wake.Set();
    }

    private readonly HashSet<Peer> _readyPeers = new();
    private readonly PriorityQueue<(Peer Peer, long Version), long> _deadlines = new();

    private void SchedulePeer(Peer p, long now)
    {
        p.Due = PeerNextDue(p, now);
        _deadlines.Enqueue((p, ++p.DeadlineVersion), p.Due);
        // Lazy invalidation stays bounded under continuous input/queue replacement.
        if (_deadlines.Count > Math.Max(1024, _peers.Count * 4))
        {
            _deadlines.Clear();
            foreach (var peer in _peers.Values) _deadlines.Enqueue((peer, peer.DeadlineVersion), peer.Due);
        }
    }

    private void CollectDuePeers(long now)
    {
        DiscardStaleDeadlines();
        while (_deadlines.TryPeek(out var item, out long due) && due <= now)
        {
            _deadlines.Dequeue();
            _readyPeers.Add(item.Peer);
            DiscardStaleDeadlines();
        }
    }

    private void DiscardStaleDeadlines()
    {
        while (_deadlines.TryPeek(out var item, out _) &&
            (item.Version != item.Peer.DeadlineVersion || !_peers.ContainsKey(item.Peer.Id))) _deadlines.Dequeue();
    }

    private int NextWait(long now)
    {
        if (!_running) return Timeout.Infinite;
        long due = long.MaxValue;
        if (_host)
        {
            if (_readyPeers.Count != 0) return 0;
            DiscardStaleDeadlines();
            if (_deadlines.TryPeek(out _, out long first)) due = first;
            if (_retired.Count != 0) due = Math.Min(due, _lastRetiredSweep + 1000);
        }
        else
        {
            if (!IsConnected && _hello is not null) due = _lastHandshake + 200;
            foreach (var p in _peers.Values) due = Math.Min(due, PeerNextDue(p, now));
        }
        return due == long.MaxValue ? Timeout.Infinite : (int)Math.Clamp(due - now, 0, int.MaxValue);
    }

    private long PeerNextDue(Peer p, long now)
    {
        long due = p.LastReceive + (long)IdleTimeout.TotalMilliseconds + 1;
        due = Math.Min(due, p.LastSend + Math.Max(1, (long)KeepAliveInterval.TotalMilliseconds));
        if (_host && !p.Published) due = Math.Min(due, p.Created + (long)HandshakeTimeout.TotalMilliseconds + 1);
        if (p.Closing) due = Math.Min(due, p.CloseDeadline + 1);
        if (p.Closing && p.Ordered.Count == 0 && !p.Streams.Values.Any(f => f.Reliable) && p.Flight.Count == 0)
            due = Math.Min(due, p.NextClose);
        if (p.PendingAcks.Count != 0) due = Math.Min(due, p.AckDue);
        double rate = Math.Min(MaxBytesPerSecond / 1000.0, p.Cwnd / Math.Max(10, p.Srtt));
        double budget = Math.Min(2 * Mtu, p.Budget + Math.Max(0, now - p.LastPace) * rate);
        foreach (var sent in p.Flight.Values)
        {
            long retry = sent.LastSent + (long)Math.Ceiling(p.Rto);
            if (retry <= now && sent.Fragments.Any(f => f.Frame is { Reliable: true }))
                retry = now + Math.Max(1, (long)Math.Ceiling((sent.Length - budget) / rate));
            due = Math.Min(due, retry);
        }
        int length = int.MaxValue;
        if (p.Ordered.TryPeek(out var f)) length = Header + RecordSize(f, f.NextFragment) + Math.Min(Payload, f.Data.Length - f.NextFragment * Payload);
        foreach (var stream in p.Streams.Values)
        {
            if (!stream.Reliable) due = Math.Min(due, stream.Created + 501);
            if (stream.NextFragment < stream.Count)
                length = Math.Min(length, Header + RecordSize(stream, stream.NextFragment) + Math.Min(Payload, stream.Data.Length - stream.NextFragment * Payload));
        }
        foreach (var assembly in p.Assemblies.Values)
            if (!assembly.Reliable) due = Math.Min(due, assembly.Created + 501);
        if (length != int.MaxValue && p.InFlight + length <= p.Cwnd)
            due = Math.Min(due, now + Math.Max(0, (long)Math.Ceiling((length - budget) / rate)));
        return due;
    }
}
