using System.Diagnostics;
using Allegiance.Core.Collision;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    internal bool _broadcastSnapshotEnabled;
    internal BroadcastPublisher? TestPublisher => _broadcastPublisher;
    private BroadcastPublisher? _broadcastPublisher;
    private BroadcastSnapshot? _buildingBroadcastSnapshot;
    private readonly object _snapshotCaptureGate = new(); // P603 sector workers may project concurrently
    private long _snapshotBuildStart, _snapshotAllocStart;
    internal long SnapshotBuildTimestampTicks, SnapshotBuildAllocatedBytes, SnapshotsBuilt;

    internal static bool ResolveSnapshotFlag(bool configured, string? environment) => environment?.Trim().ToLowerInvariant() switch
    {
        null or "" => configured,
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw new InvalidDataException("ALLEGIANCE_BROADCAST_SNAPSHOT must be 0/1 or false/true"),
    };

    private void ResolveBroadcastSnapshot()
    {
        _broadcastSnapshotEnabled = ResolveSnapshotFlag(_config.Broadcast?.ImmutableSnapshot ?? false,
            Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_SNAPSHOT"));
        Console.WriteLine($"[boot] broadcast.immutableSnapshot = {_broadcastSnapshotEnabled}");
    }

    private void StartBroadcastPublisher()
    {
        if (!_broadcastSnapshotEnabled) return;
        _broadcastPublisher = new BroadcastPublisher((delivery, bytes) =>
            PublishBytes(delivery.Peer, bytes, delivery.Key, delivery.Guaranteed), _broadcastMaxParallelism)
        {
            OnError = e => Console.Error.WriteLine($"[broadcast-publish] {e}"),
        };
    }

    private BroadcastMessage Message(IFedMessage message) => new(message, _broadcastSnapshotEnabled);
    private BroadcastPacket PackBroadcast(BroadcastMessage message) => PackBroadcast(new[] { message });
    private BroadcastPacket PackBroadcast(byte[] encodedMessage)
        => PackBroadcast(new BroadcastMessage(encodedMessage));
    private BroadcastPacket PackBroadcast(ReadOnlySpan<BroadcastMessage> messages)
    {
        var packet = new BroadcastPacket(messages.ToArray());
        return _broadcastSnapshotEnabled ? packet : new BroadcastPacket(packet.Encode());
    }

    private ServerShipUpdate CaptureShipUpdate(short id, Ship ship, uint tick,
        int extraStateBits = 0, bool carryEndurance = false)
        => _broadcastSnapshotEnabled
            ? ServerShipUpdate.CaptureModel(id, ship, tick, extraStateBits, carryEndurance)
            : ServerShipUpdate.FromModel(id, ship, tick, extraStateBits, carryEndurance);

    private HeavyShipUpdateEntry CaptureHeavyUpdate(uint tick, Vector3 position, Ship ship, uint time)
        => _broadcastSnapshotEnabled ? HeavyShipUpdateEntry.Capture(tick, position, ship, time)
            : HeavyShipUpdateEntry.From(tick, position, ship, time);

    internal void BeginBroadcastSnapshot()
    {
        if (!_broadcastSnapshotEnabled || _broadcastPublisher is null) return;
        if (_buildingBroadcastSnapshot is not null) throw new InvalidOperationException("Snapshot already being built");
        _buildingBroadcastSnapshot = _broadcastPublisher.Rent(TickIndex);
    }

    private void StartBroadcastProjectionMeasurement()
    {
        if (_broadcastPublisher is null) return;
        _snapshotBuildStart = Stopwatch.GetTimestamp();
        _snapshotAllocStart = GC.GetAllocatedBytesForCurrentThread();
    }

    internal void CompleteBroadcastSnapshot()
    {
        if (_buildingBroadcastSnapshot is not { } snapshot) return;
        using (TickProfiler.Enter("broadcast.snapshotPublish"))
        {
            _buildingBroadcastSnapshot = null;
            _broadcastPublisher!.Submit(snapshot);
        }
        if (_snapshotBuildStart != 0)
        {
            SnapshotBuildTimestampTicks += Stopwatch.GetTimestamp() - _snapshotBuildStart;
            SnapshotBuildAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - _snapshotAllocStart;
            _snapshotBuildStart = 0;
        }
        SnapshotsBuilt++;
    }

    private void EmitBroadcastSnapshotMetrics()
    {
        if (_broadcastPublisher is not { } publisher) return;
        long published = Interlocked.Read(ref publisher.PublishedSnapshots);
        Console.WriteLine(FormattableString.Invariant($"[broadcast-snapshot] built={SnapshotsBuilt} buildMeanMs={SnapshotBuildTimestampTicks * 1000d / Stopwatch.Frequency / Math.Max(1, SnapshotsBuilt):0.###} buildBytesPerTick={SnapshotBuildAllocatedBytes / Math.Max(1, SnapshotsBuilt)} publishMeanMs={Interlocked.Read(ref publisher.PublishTimestampTicks) * 1000d / Stopwatch.Frequency / Math.Max(1, published):0.###} published={published} coalesced={Interlocked.Read(ref publisher.CoalescedSnapshots)} droppedStreams={Interlocked.Read(ref publisher.DroppedStreamFrames)} failures={Interlocked.Read(ref publisher.EncodeFailures)} containers={Interlocked.Read(ref publisher.SnapshotContainersCreated)}"));
    }

    internal void FlushBroadcastSnapshots() => _broadcastPublisher?.Flush();

    private void SendPeer(ConnectionId peer, BroadcastPacket packet) => QueueBroadcast(peer, packet, 0, true);
    private void SendPeerCoalescing(ConnectionId peer, BroadcastPacket packet, int key, bool guaranteed = false)
        => QueueBroadcast(peer, packet, key, guaranteed);

    private void QueueBroadcast(ConnectionId peer, BroadcastPacket packet, int key, bool guaranteed)
    {
        if (_buildingBroadcastSnapshot is { } snapshot)
        {
            lock (_snapshotCaptureGate) snapshot.Deliveries.Add(new(peer, packet, key, guaranteed));
            return;
        }
        // Direct test/admin calls outside a sim tick cannot overtake the last
        // queued tick. Production per-tick broadcasts always use the snapshot.
        FlushBroadcastSnapshots();
        PublishBytes(peer, packet.Encode(), key, guaranteed);
    }

    // The sole publishing sink. No sim state, latches, visibility, ranking or
    // cadence objects are read here. Transport queues are already thread-safe.
    private void PublishBytes(ConnectionId peer, byte[] bytes, int key, bool guaranteed)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: true, bytes));
        Interlocked.Add(ref _egressBytesSent, bytes.Length);
        _testSendCapture?.Invoke(peer, bytes);
        TestPublicationCapture?.Invoke(peer, bytes, key, guaranteed);
        if (key == 0) _host.Send(peer, bytes);
        else
        {
            _testSendCoalescingCapture?.Invoke(peer, bytes, key, guaranteed);
            if (guaranteed) _host.SendGuaranteedCoalescing(peer, bytes, key);
            else _host.SendCoalescing(peer, bytes, key);
        }
    }
}
