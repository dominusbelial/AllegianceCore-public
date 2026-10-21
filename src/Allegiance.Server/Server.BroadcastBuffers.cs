using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private sealed record ExportCache(Ship Ship, long Version, short Hull, short Side, string? PeerName,
        BroadcastPacket Packet)
    {
        internal int Generation;
    }
    private readonly Dictionary<short, ExportCache> _crossSideExportPackets = new();
    private int _crossSideExportGeneration;

    internal BroadcastPacket CrossSideExportPacket(Ship ship, short seeingSide)
    {
        string? peerName = PeerName(ship);
        short side = ship.SideId ?? seeingSide;
        if (ReuseBroadcastBuffers && _crossSideExportPackets.TryGetValue(ship.Id, out var cached)
            && cached.Ship == ship && cached.Version == ship.LoadoutVersion
            && cached.Hull == ship.HullType.HullId && cached.Side == side && cached.PeerName == peerName)
            return cached.Packet;
        var export = new ExportShip { ObjectType = ExportShip.OtShip,
            CharacterName = peerName ?? $"Ship {ship.Id}", HullId = ship.HullType.HullId,
            ShipId = ship.Id, SideId = side, PilotType = ExportShip.PilotTypePlayer,
            Parts = EnsureLoadoutSnapshot(ship) };
        var packet = PackBroadcast(Message(export));
        if (ReuseBroadcastBuffers)
            _crossSideExportPackets[ship.Id] = new(ship, ship.LoadoutVersion, ship.HullType.HullId, side, peerName, packet)
                { Generation = _crossSideExportGeneration };
        return packet;
    }

    // Sim-thread lifecycle boundary: discard projection identities immediately.
    // Submitted snapshots own detached values/leases and remain ordered in the
    // publisher; clearing scratch caches must not discard those deliveries.
    private void RetireBroadcastProjectionCaches()
    {
        _sharedHeavy.Clear();
        _sharedLight.Clear();
        _tierHeavy.Clear();
        _interestSnapshot.Clear();
        _crossSideExportPackets.Clear();
        Array.Clear(_broadcastPeerBuffer);
        _broadcastMates.Clear();
        foreach (var list in _broadcastMateBuffers.Values) list.Clear();
        foreach (var sectors in _broadcastSideSectors.Values) sectors.Clear();
        _sideConnections.Clear();
        foreach (var list in _sideConnectionBuffers.Values) list.Clear();
    }

    private bool ReuseBroadcastBuffers => _broadcastEncodeOnceEnabled && _buildingBroadcastSnapshot is not null;
    private KeyValuePair<ConnectionId, LiveShip>[] _broadcastPeerBuffer = [];
    private KeyValuePair<ConnectionId, LiveShip>[] SnapshotBroadcastPeers()
    {
        if (!ReuseBroadcastBuffers) return _peerShips.ToArray();
        if (_broadcastPeerBuffer.Length != _peerShips.Count)
            _broadcastPeerBuffer = new KeyValuePair<ConnectionId, LiveShip>[_peerShips.Count];
        ((ICollection<KeyValuePair<ConnectionId, LiveShip>>)_peerShips).CopyTo(_broadcastPeerBuffer, 0);
        return _broadcastPeerBuffer;
    }

    private readonly Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>> _broadcastMates = new();
    private readonly Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>> _broadcastMateBuffers = new();
    private Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>> GatherBroadcastMates(KeyValuePair<ConnectionId, LiveShip>[] peers)
    {
        _broadcastMates.Clear();
        foreach (var list in _broadcastMateBuffers.Values) list.Clear();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is not { } side) continue;
            if (!_broadcastMates.TryGetValue(side, out var list))
            {
                if (!_broadcastMateBuffers.TryGetValue(side, out list))
                    _broadcastMateBuffers.Add(side, list = new());
                _broadcastMates.Add(side, list);
            }
            list.Add(kv);
        }
        return _broadcastMates;
    }

    private readonly Dictionary<short, HashSet<Sector>> _broadcastSideSectors = new();
    private readonly Dictionary<short, List<ConnectionId>> _sideConnections = new();
    private readonly Dictionary<short, List<ConnectionId>> _sideConnectionBuffers = new();
    private Dictionary<short, List<ConnectionId>> GatherSideConnections()
    {
        _sideConnections.Clear();
        foreach (var list in _sideConnectionBuffers.Values) list.Clear();
        foreach (var sectors in _broadcastSideSectors.Values) sectors.Clear();
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.SideId is not { } sid) continue;
            if (!_sideConnections.TryGetValue(sid, out var list))
            {
                if (!_sideConnectionBuffers.TryGetValue(sid, out list))
                    _sideConnectionBuffers.Add(sid, list = new());
                _sideConnections.Add(sid, list);
            }
            list.Add(kv.Key);
            if (!_broadcastSideSectors.TryGetValue(sid, out var sectors))
                _broadcastSideSectors.Add(sid, sectors = new());
            sectors.Add(kv.Value.Sector);
        }
        return _sideConnections;
    }

    private PooledPosePacket SmallPacket(ushort fmid, int size)
    {
        var packet = PooledPosePacket.Rent(size + FedPacket.CrcWidth);
        var bytes = packet.Buffer.Slice(0, size);
        bytes.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(2), fmid);
        packet.Seal(size);
        return packet;
    }

    private BroadcastPacket PackStatus(short id, LiveShip live)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(MakeOwnShipStatus(id, live)));
        var ship = live.Ship;
        var packet = SmallPacket(ShipStatusMessage.Fmid, ShipStatusMessage.FixedSize);
        var bytes = packet.Buffer;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(4), id);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(8), ship.ParentShip is null ? ship.HullType.HullId : (short)-1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(10), live.Sector.Id);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(12), ship.StationId ?? (short)-1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(14), ship.ParentShip?.Id ?? (short)-1);
        byte state = ship.ParentShip is not null
            ? ship.TurretId is null ? ShipStatusMessage.StateObserver : ShipStatusMessage.StateTurret
            : ship.StationId is not null ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying;
        bytes[16] = (byte)(state | 0x80);
        return packet;
    }

    private BroadcastPacket PackCrossSideStatus(Ship ship, short sector, bool spotted)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(MakeCrossSideShipStatus(ship, sector, spotted)));
        var packet = SmallPacket(ShipStatusMessage.Fmid, ShipStatusMessage.FixedSize);
        var bytes = packet.Buffer;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(4), ship.Id);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(8), ship.HullType.HullId);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(10), sector);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(12), ship.StationId ?? (short)-1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(14), -1);
        bytes[16] = (byte)((ship.StationId is null ? ShipStatusMessage.StateFlying : ShipStatusMessage.StateDocked)
            | (spotted ? 0 : 0x40));
        return packet;
    }

    private BroadcastPacket PackWarning(short id, ShipWarningMask mask)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(new ShipWarningUpdate { ShipId = id, Mask = mask }));
        var packet = SmallPacket(ShipWarningUpdate.Fmid, ShipWarningUpdate.FixedSize);
        BinaryPrimitives.WriteInt16LittleEndian(packet.Buffer.Slice(4), id);
        packet.Buffer[6] = (byte)mask;
        return packet;
    }

    private BroadcastPacket PackMining(Asteroid? rock)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(OwnShipMiningUpdate.FromMining(rock)));
        var packet = SmallPacket(OwnShipMiningUpdate.Fmid, OwnShipMiningUpdate.FixedSize);
        BinaryPrimitives.WriteInt16LittleEndian(packet.Buffer.Slice(4), rock?.Data.ClusterId ?? (short)-1);
        BinaryPrimitives.WriteInt16LittleEndian(packet.Buffer.Slice(6), rock?.Data.AsteroidId ?? (short)-1);
        return packet;
    }

    private BroadcastPacket PackCloak(Ship ship)
    {
        if (!ReuseBroadcastBuffers)
            return PackBroadcast(Message(new OwnShipCloakUpdate { Cloaking = OwnShipCloaking(ship) }));
        var packet = SmallPacket(OwnShipCloakUpdate.Fmid, OwnShipCloakUpdate.FixedSize);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Buffer.Slice(4), OwnShipCloaking(ship));
        return packet;
    }

    private BroadcastPacket PackKillBonus(Ship ship)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(OwnShipKillBonusUpdate.FromShip(ship)));
        var packet = SmallPacket(OwnShipKillBonusUpdate.Fmid, OwnShipKillBonusUpdate.FixedSize);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Buffer.Slice(4), ship.KillBonusPercent);
        BinaryPrimitives.WriteInt16LittleEndian(packet.Buffer.Slice(8), (short)ship.KillBonus);
        return packet;
    }

    private BroadcastPacket PackMissileTrack(bool tracked, float bestLock)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(MissileTrackWarningUpdate.From(tracked, bestLock)));
        var packet = SmallPacket(MissileTrackWarningUpdate.Fmid, MissileTrackWarningUpdate.FixedSize);
        packet.Buffer[4] = tracked ? (byte)1 : (byte)0;
        packet.Buffer[5] = tracked ? Percent(Math.Clamp(bestLock, 0, 1)) : (byte)0;
        return packet;
    }

    private BroadcastPacket PackLead(short ship, short target, bool inRange, float green,
        Vector3 lead, Vector3 targetPosition)
    {
        if (!ReuseBroadcastBuffers) return PackBroadcast(Message(LeadIndicatorUpdate.From(ship, target, inRange, green, lead, targetPosition)));
        var packet = SmallPacket(LeadIndicatorUpdate.Fmid, LeadIndicatorUpdate.Width);
        BinaryPrimitives.WriteInt16LittleEndian(packet.Buffer.Slice(4), ship);
        BinaryPrimitives.WriteInt16LittleEndian(packet.Buffer.Slice(6), target);
        packet.Buffer[8] = inRange ? (byte)1 : (byte)0;
        packet.Buffer[9] = Percent(Math.Clamp(green, 0, 1));
        WritePoseVector(packet.Buffer.Slice(10), lead);
        WritePoseVector(packet.Buffer.Slice(22), targetPosition);
        return packet;
    }
}
