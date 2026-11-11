using System.Buffers.Binary;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private bool _broadcastGunfireBatch;
    private bool _gunfireTickOpen;
    private uint _gunfireTick;
    // Only the sim thread uses this token/scratch. The audience helpers treat
    // it as an opaque packet; SendPeer copies the encoded record immediately.
    private byte[] _gunfireToken = [];
    private readonly byte[] _gunfireShipToken = new byte[ProjectileInstance.FixedSize + FedPacket.CrcWidth];
    private readonly byte[] _gunfireProbeToken = new byte[ProjectileInstance.FixedSize + 2 + FedPacket.CrcWidth];
    private readonly byte[] _gunfireHitToken = new byte[ProjectileHullHit.FixedSize + FedPacket.CrcWidth];
    private readonly byte[] _gunfireRecord = new byte[GunfireBatch.MaxRecordSize];
    private int _gunfireRecordLength;
    private sealed class GunfireBuilder
    {
        internal byte[] Bytes = new byte[1024];
        internal int Length = GunfireBatch.HeaderSize, Count;
        internal void Clear() { Length = GunfireBatch.HeaderSize; Count = 0; }
    }
    private readonly Dictionary<ConnectionId, GunfireBuilder> _gunfireBuilders = new();
    private readonly Dictionary<ulong, byte[]> _gunfireShared = new();
    private readonly List<ConnectionId> _gunfireDeparted = new();

    internal static bool IsGunfireOrderingBoundary(ushort fmid)
        => fmid is SetCluster.Fmid or MissionStageMessage.Fmid or MissionDef.Fmid or JoinedMission.Fmid
            or QuitMission.Fmid or JoinSide.Fmid or QuitSide.Fmid or KillShip.Fmid or ShipDelete.Fmid
            or Eject.Fmid or EnterLifepod.Fmid or ShipReset.Fmid or DockedNotify.Fmid;

    internal static bool HasGunfireOrderingBoundary(ReadOnlySpan<byte> packet)
    {
        int end = packet.Length - FedPacket.CrcWidth;
        for (int offset = 0; offset + 4 <= end;)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(packet[offset..]);
            if (size < 4 || offset + size > end) return false;
            if (IsGunfireOrderingBoundary(BinaryPrimitives.ReadUInt16LittleEndian(packet[(offset + 2)..]))) return true;
            offset += size;
        }
        return false;
    }

    private bool HasPendingGunfire(ConnectionId peer)
        => _broadcastGunfireBatch && _gunfireTickOpen
            && _gunfireBuilders.TryGetValue(peer, out var builder) && builder.Count != 0;

    private void FlushGunfireBeforeBoundary(ConnectionId peer, ReadOnlySpan<byte> packet)
    {
        if (HasPendingGunfire(peer) && HasGunfireOrderingBoundary(packet))
            SendGunfireBuilder(peer, _gunfireBuilders[peer]);
    }

    // Keep the existing audience API's packet contract: P993 may inspect the
    // legacy fmid/muzzle position before choosing recipients. These reusable
    // tokens are valid legacy frames (including CRC), never sent on the wire;
    // SendPeer substitutes the already-encoded compact record immediately.
    private byte[] GunfireProjectileToken(Projectile p, short launcher, IgcObjectType launcherType)
    {
        _gunfireToken = launcherType == IgcObjectType.Ship ? _gunfireShipToken : _gunfireProbeToken;
        int size = _gunfireToken.Length - FedPacket.CrcWidth;
        var w = new WireWriter(_gunfireToken);
        w.WriteU16((ushort)size); w.WriteU16(ProjectileInstance.Fmid);
        w.WriteU32((uint)TickIndex); w.WriteI16(p.ProjectileType.ProjectileTypeId); w.WriteU16(0);
        WriteGunfireVector(ref w, p.Forward); WriteGunfireVector(ref w, p.Velocity);
        w.WriteU32(BitConverter.SingleToUInt32Bits(p.InitialLifespan));
        WriteGunfireVector(ref w, p.Position); w.WriteI16(launcher);
        if (launcherType != IgcObjectType.Ship) w.WriteI16((short)launcherType);
        WriteGunfireTokenCrc(size);
        return _gunfireToken;
    }

    private byte[] GunfireHitToken(short target, short cluster, short source, bool shield,
        float radius, Vector3 position, Vector3 normal)
    {
        _gunfireToken = _gunfireHitToken;
        var w = new WireWriter(_gunfireToken);
        w.WriteU16(ProjectileHullHit.FixedSize); w.WriteU16(ProjectileHullHit.Fmid);
        w.WriteI16(target); w.WriteI16(cluster); w.WriteByte(shield ? (byte)1 : (byte)0);
        w.WriteByte(0); w.WriteU16(0); w.WriteU32(BitConverter.SingleToUInt32Bits(radius));
        WriteGunfireVector(ref w, position); WriteGunfireVector(ref w, normal);
        w.WriteI16(source); w.WriteU16(0);
        WriteGunfireTokenCrc(ProjectileHullHit.FixedSize);
        return _gunfireToken;
    }

    private static void WriteGunfireVector(ref WireWriter w, Vector3 v)
    {
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Z));
    }

    private void WriteGunfireTokenCrc(int size)
        => BinaryPrimitives.WriteUInt32LittleEndian(_gunfireToken.AsSpan(size), FedMsgCrc.Compute(_gunfireToken.AsSpan(0, size)));

    internal static bool ResolveGunfireBatchFlag(bool configured, string? environment)
        => environment?.Trim().ToLowerInvariant() switch
        {
            null or "" => configured,
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidDataException("ALLEGIANCE_BROADCAST_GUNFIRE_BATCH must be 0/1 or false/true"),
        };

    private void ResolveGunfireBatch()
    {
        _broadcastGunfireBatch = ResolveGunfireBatchFlag(_config.Broadcast?.GunfireBatch ?? true,
            Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_GUNFIRE_BATCH"));
        Console.WriteLine($"[boot] broadcast.gunfireBatch = {_broadcastGunfireBatch.ToString().ToLowerInvariant()}");
    }

    private void BeginGunfireTick()
    {
        if (!_broadcastGunfireBatch) return;
        _gunfireTickOpen = true;
        _gunfireTick = (uint)TickIndex;
        _gunfireShared.Clear();
        _gunfireDeparted.Clear();
        foreach (var peer in _gunfireBuilders.Keys)
            if (!_peerShips.ContainsKey(peer)) _gunfireDeparted.Add(peer);
        foreach (var peer in _gunfireDeparted) _gunfireBuilders.Remove(peer);
    }

    private void QueueGunfireRecord(ConnectionId peer)
    {
        if (!_gunfireBuilders.TryGetValue(peer, out var builder))
            _gunfireBuilders.Add(peer, builder = new());
        // cbmsg is ushort: exceptionally large bursts split on record boundaries.
        if (builder.Length + _gunfireRecordLength > ushort.MaxValue)
            SendGunfireBuilder(peer, builder);
        int end = builder.Length + _gunfireRecordLength;
        if (end > builder.Bytes.Length)
            Array.Resize(ref builder.Bytes, Math.Min(ushort.MaxValue, Math.Max(end, builder.Bytes.Length * 2)));
        _gunfireRecord.AsSpan(0, _gunfireRecordLength).CopyTo(builder.Bytes.AsSpan(builder.Length));
        builder.Length = end;
        builder.Count++;
        // Admin/test calls outside a sim tick still produce a valid frame.
        if (!_gunfireTickOpen) { SendGunfireBuilder(peer, builder); _gunfireShared.Clear(); }
    }

    private void SendGunfireBuilder(ConnectionId peer, GunfireBuilder builder)
    {
        if (builder.Count == 0) return;
        GunfireBatch.WriteHeader(builder.Bytes, builder.Length, _gunfireTickOpen ? _gunfireTick : (uint)TickIndex, builder.Count);
        var body = builder.Bytes.AsSpan(0, builder.Length);
        ulong hash = 14695981039346656037UL;
        foreach (byte b in body) hash = unchecked((hash ^ b) * 1099511628211UL);
        // Equal sector/audience histories share the entire encoded packet, CRC
        // and snapshot wrapper. Always compare bytes too: hashes are not identity.
        if (!_gunfireShared.TryGetValue(hash, out var packet)
            || packet.Length != body.Length + FedPacket.CrcWidth
            || !body.SequenceEqual(packet.AsSpan(0, body.Length)))
        {
            packet = new byte[body.Length + FedPacket.CrcWidth];
            body.CopyTo(packet);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(body.Length), FedMsgCrc.Compute(body));
            _gunfireShared[hash] = packet;
        }
        SendPeer(peer, packet);
        builder.Clear();
    }

    private void CompleteGunfireTick()
    {
        if (!_broadcastGunfireBatch) return;
        foreach (var (peer, builder) in _gunfireBuilders) SendGunfireBuilder(peer, builder);
        TestGunfireSharingCapture?.Invoke(_gunfireShared.Count);
        _gunfireShared.Clear();
        _gunfireTickOpen = false;
    }
}
