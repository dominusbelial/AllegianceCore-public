using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

// A message is owned by the broadcast projection after capture. No caller may
// mutate it (or its arrays) afterwards. Shared messages/packets memoize encoding
// under a lock, including when several encode workers encounter the same pose.
internal sealed class BroadcastMessage
{
    private readonly IFedMessage? _message;
    private byte[]? _bytes;
    internal BroadcastMessage(IFedMessage message, bool deferred)
    {
        if (deferred)
        {
            BroadcastSnapshotGuard.Check(message);
            _message = message;
        }
        else _bytes = message.Encode();
    }
    internal BroadcastMessage(byte[] bytes) => _bytes = bytes;
    internal byte[] Encode()
    {
        lock (this) return _bytes ??= _message!.Encode();
    }
}

internal class BroadcastPacket
{
    private readonly BroadcastMessage[]? _messages;
    private byte[]? _bytes;
    internal BroadcastPacket(BroadcastMessage[] messages) => _messages = messages;
    internal BroadcastPacket(byte[] bytes) => _bytes = bytes;
    internal byte[] Encode()
    {
        lock (this)
        {
            if (_bytes is not null) return _bytes;
            var frames = new byte[_messages!.Length][];
            for (int i = 0; i < frames.Length; i++) frames[i] = _messages[i].Encode();
            return _bytes = FedPacket.Pack(frames.AsSpan());
        }
    }
    internal virtual ReadOnlyMemory<byte> EncodeMemory() => Encode();
    internal virtual void Release() { }

}

internal readonly record struct BroadcastDelivery(ConnectionId Peer, BroadcastPacket Packet,
    int Key = 0, bool Guaranteed = true);

internal sealed class BroadcastSnapshot
{
    internal long Tick;
    internal readonly List<BroadcastDelivery> Deliveries = new();
    // Run on the publisher thread after every delivery of this snapshot was
    // handed to the transport (e.g. LOGOFF's close-after-echo), so a socket
    // close can never overtake a message captured before it in the same tick.
    internal readonly List<Action> AfterPublish = new();
    internal void Reset(long tick) { Tick = tick; Deliveries.Clear(); AfterPublish.Clear(); }
}

/// <summary>One active publication and at most one pending snapshot. Encoding
/// uses a bounded pool; the coordinator enqueues in captured order. Only the
/// transport sink is callable here: this class has no Server/world reference.</summary>
internal sealed class BroadcastPublisher : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentBag<BroadcastSnapshot> _pool = new();
    private readonly Action<BroadcastDelivery, ReadOnlyMemory<byte>> _send;
    private readonly Thread _thread;
    private readonly ParallelOptions _options;
    private BroadcastSnapshot? _pending;
    private bool _active, _stopping;
    internal long CoalescedSnapshots, DroppedStreamFrames, PublishedSnapshots, PublishTimestampTicks;
    internal long SnapshotContainersCreated, EncodeFailures, PublishAllocatedBytes;
    internal Action? BeforeEncode; // deterministic stalled-worker test seam
    internal Action<Exception>? OnError;

    internal BroadcastPublisher(Action<BroadcastDelivery, byte[]> send, int workers)
        : this((delivery, bytes) => send(delivery, bytes.ToArray()), workers, true) { }

    internal BroadcastPublisher(Action<BroadcastDelivery, ReadOnlyMemory<byte>> send, int workers, bool memorySink)
    {
        _send = send;
        _options = new() { MaxDegreeOfParallelism = Math.Max(1, workers) };
        // Triple buffer: sim capture, pending publication, active publication.
        for (int i = 0; i < 3; i++) { _pool.Add(new()); SnapshotContainersCreated++; }
        _thread = new Thread(Run) { IsBackground = true, Name = "broadcast-publish" };
        _thread.Start();
    }

    internal BroadcastSnapshot Rent(long tick)
    {
        if (!_pool.TryTake(out var snapshot))
        {
            snapshot = new();
            Interlocked.Increment(ref SnapshotContainersCreated);
        }
        snapshot.Reset(tick);
        return snapshot;
    }

    internal void Submit(BroadcastSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(BroadcastPublisher));
            if (_pending is { } previous)
            {
                // Never discard ordered reliable traffic: its latches already
                // advanced on the sim thread. Coalesce only keyed streams,
                // including guaranteed latest-wins own-ship poses (P608).
                // P788c: pre-update ships and post-update events/statics are
                // complementary phases of ONE tick. Keep unsuperseded streams
                // from that tick; a later tick retains the existing shedding.
                bool sameTick = previous.Tick == snapshot.Tick;
                int keep = 0;
                var replaced = new HashSet<(ConnectionId, int)>();
                foreach (var delivery in snapshot.Deliveries)
                    if (delivery.Key != 0) replaced.Add((delivery.Peer, delivery.Key));
                for (int i = 0; i < previous.Deliveries.Count; i++)
                {
                    var delivery = previous.Deliveries[i];
                    if (delivery.Key == 0 || ((delivery.Guaranteed || sameTick) && !replaced.Contains((delivery.Peer, delivery.Key))))
                        previous.Deliveries[keep++] = delivery;
                    else
                    {
                        delivery.Packet.Release();
                        Interlocked.Increment(ref DroppedStreamFrames);
                    }
                }
                previous.Deliveries.RemoveRange(keep, previous.Deliveries.Count - keep);
                previous.Deliveries.AddRange(snapshot.Deliveries);
                previous.AfterPublish.AddRange(snapshot.AfterPublish);
                previous.Tick = snapshot.Tick;
                snapshot.Deliveries.Clear(); // ownership transferred to previous
                snapshot.AfterPublish.Clear();
                Return(snapshot);
                Interlocked.Increment(ref CoalescedSnapshots);
            }
            else _pending = snapshot;
            Monitor.PulseAll(_gate);
        }
    }

    private void Return(BroadcastSnapshot snapshot)
    {
        foreach (var delivery in snapshot.Deliveries) delivery.Packet.Release();
        snapshot.Deliveries.Clear();
        snapshot.AfterPublish.Clear();
        _pool.Add(snapshot);
    }

    private static bool HasNewerStream(BroadcastSnapshot snapshot, BroadcastDelivery previous)
    {
        foreach (var delivery in snapshot.Deliveries)
            if (delivery.Peer == previous.Peer && delivery.Key == previous.Key) return true;
        return false;
    }

    private void Failed(Exception error)
    {
        Interlocked.Increment(ref EncodeFailures);
        try { OnError?.Invoke(error); }
        catch { /* Diagnostics cannot terminate the publishing thread. */ }
    }

    private void Run()
    {
        while (true)
        {
            BroadcastSnapshot snapshot;
            lock (_gate)
            {
                while (_pending is null && !_stopping) Monitor.Wait(_gate);
                if (_pending is null) return;
                snapshot = _pending;
                _pending = null;
                _active = true;
            }
            long start = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                BeforeEncode?.Invoke();
                // Each packet is immutable; duplicate references encode once.
                Parallel.For(0, snapshot.Deliveries.Count, _options, i =>
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    try { snapshot.Deliveries[i].Packet.EncodeMemory(); }
                    catch (Exception e) { Failed(e); }
                    finally
                    {
                        // The coordinator can participate in Parallel.For. Its
                        // allocations are already included in the outer delta.
                        if (Thread.CurrentThread != _thread)
                            Interlocked.Add(ref PublishAllocatedBytes, GC.GetAllocatedBytesForCurrentThread() - before);
                    }
                });
                foreach (var delivery in snapshot.Deliveries)
                {
                    // If sim has advanced again, no stale streaming state may
                    // jump ahead of its newer pending snapshot. Reliable events
                    // retain their exact original order, even during overload.
                    bool stale;
                    lock (_gate)
                    {
                        stale = _pending is { } pending && pending.Tick > snapshot.Tick + 1
                            && (!delivery.Guaranteed || HasNewerStream(pending, delivery));
                    }
                    if (stale && delivery.Key != 0)
                    {
                        Interlocked.Increment(ref DroppedStreamFrames);
                        continue;
                    }
                    try { _send(delivery, delivery.Packet.EncodeMemory()); }
                    catch (Exception e) { Failed(e); }
                }
                foreach (var after in snapshot.AfterPublish)
                {
                    try { after(); }
                    catch (Exception e) { Failed(e); }
                }
                Interlocked.Increment(ref PublishedSnapshots);
            }
            catch (Exception e) { Failed(e); }
            finally
            {
                Interlocked.Add(ref PublishTimestampTicks, Stopwatch.GetTimestamp() - start);
                Return(snapshot);
                Interlocked.Add(ref PublishAllocatedBytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
                lock (_gate) { _active = false; Monitor.PulseAll(_gate); }
            }
        }
    }

    internal void Flush()
    {
        lock (_gate)
            while (_active || _pending is not null) Monitor.Wait(_gate);
    }

    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
        _thread.Join(); // drains active + pending before transport teardown
    }
}

internal static class BroadcastSnapshotGuard
{
    [Conditional("DEBUG")]
    internal static void Check(object data) => AssertDetached(data);

    // Test-callable in Release too. Inspect fields, never getters (which might
    // materialize a lazy pose). Unknown reference types fail closed.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug/test guard inspects untrimmed wire DTO fields; release production call is compiled out.")]
    internal static void AssertDetached(object data)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object? value)
        {
            if (value is null) return;
            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return;
            if (!type.IsValueType && !visited.Add(value)) return;
            if (value is Array array) { foreach (var item in array) Visit(item); return; }
            // Orientation in ShipWireSnapshot is an owned deep copy. ControlData
            // and Vector3 are values; no other simulation types are permitted.
            if (type.Namespace != "Allegiance.Core.Wire"
                && type != typeof(Allegiance.Core.Sim.Orientation)
                && type != typeof(Allegiance.Core.Sim.ControlData)
                && type != typeof(Allegiance.Core.Collision.Vector3)
                && type != typeof(Server.InterestShipState)
                && type != typeof(Server.InterestCell))
                throw new InvalidOperationException($"Broadcast snapshot retained live/unknown type {type.FullName}");
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Visit(field.GetValue(value));
        }
        Visit(data);
    }
}
