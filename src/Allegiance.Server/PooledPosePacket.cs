using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

// A lease has exactly one delivery. Projection seals it before submission;
// the publisher returns it only after sending, superseding, or dropping it.
// The transport copies the span synchronously before this storage is reused.
internal sealed class PooledPosePacket : BroadcastPacket
{
    private static readonly ConcurrentBag<PooledPosePacket>[] Pools =
        Enumerable.Range(0, 18).Select(_ => new ConcurrentBag<PooledPosePacket>()).ToArray();
    private int _bucket;
    private byte[] _buffer = [];
    private int _length;
    private bool _encoded;

    private PooledPosePacket() : base(Array.Empty<byte>()) { }

    internal static PooledPosePacket Rent(int capacity)
    {
        int bucket = Math.Max(6, System.Numerics.BitOperations.Log2((uint)Math.Max(1, capacity - 1)) + 1);
        if (bucket >= Pools.Length) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (!Pools[bucket].TryTake(out var packet))
            packet = new() { _bucket = bucket, _buffer = new byte[1 << bucket] };
        packet._encoded = false;
        return packet;
    }

    internal Span<byte> Buffer => _buffer;
    internal void Seal(int messageLength) => _length = messageLength + FedPacket.CrcWidth;
    internal override ReadOnlyMemory<byte> EncodeMemory()
    {
        if (!_encoded)
        {
            int messageLength = _length - FedPacket.CrcWidth;
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(messageLength),
                FedMsgCrc.Compute(_buffer.AsSpan(0, messageLength)));
            _encoded = true;
        }
        return _buffer.AsMemory(0, _length);
    }
    internal override void Release() => Pools[_bucket].Add(this);
}

// Inline bytes make copying a selected record allocation-free. No live state
// (including Orientation) crosses the producer/publisher boundary.
[InlineArray(HeavyShipUpdateEntry.Width)]
internal struct HeavyPoseBytes { private byte _first; }

internal struct HeavyPoseRecord
{
    internal HeavyPoseBytes Bytes;
    internal Vector3 Position;

    internal static HeavyPoseRecord Capture(Ship ship, uint tick)
    {
        HeavyPoseRecord record = new() { Position = ship.Position };
        HeavyShipUpdateEntry.WriteModel(record.Bytes, tick, ship.Position, ship, tick);
        return record;
    }

    internal readonly void WriteRelative(Span<byte> destination, Vector3 reference)
    {
        ((ReadOnlySpan<byte>)Bytes).CopyTo(destination);
        HeavyShipUpdateEntry.WriteRelativePosition(destination.Slice(10, 6), reference, Position);
    }
}
