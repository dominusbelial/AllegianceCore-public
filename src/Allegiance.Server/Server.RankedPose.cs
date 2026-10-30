using System.Buffers.Binary;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly Dictionary<Ship, HeavyPoseRecord> _rankedHeavy = new();

    private static Ship? FlyingParent(Ship child, Sector sector) =>
        child.ParentShip is { Dead: false, StationId: null } parent
            && ReferenceEquals(parent.Sector, sector) ? parent : null;

    // Native FedSrv/FedSrv.CPP:3923-3950,4141-4188: retain sequential deviation
    // selection, target promotion and HEAVY/LIGHT order. Encode selected poses
    // into one owned lease, avoiding a relative DTO per viewer/ship. Only bytes
    // leave projection; PooledPosePacket has exactly one delivery/return.
    private void BroadcastRankedPose(KeyValuePair<ConnectionId, LiveShip> peer, uint tick,
        bool trackCadence, double now)
    {
        var self = peer.Value.Ship;
        var sector = BroadcastSectorFor(peer.Key, peer.Value.Sector);
        var source = self.ParentShip ?? self;
        var parent = FlyingParent(self, sector);
        bool admin = HasAdminView(peer.Key);
        ResolveTargetFractions(self, self.CommandTarget, sector, out Ship? target,
            out byte targetHull, out byte targetShield);
        var ranking = _shipRankings.GetValue(sector, static _ => new());
        var heavy = ranking.Heavy;
        var light = ranking.Light;
        heavy.Clear(); light.Clear();
        if (target is not null && ShipPoseVisible(self, target, admin)) heavy.Add(target);
        var all = sector.GetShipUpdateCandidates();
        ViewerPoseCadence? cadence = trackCadence ? _viewerPoseCadence[peer.Key] : null;
        cadence?.Begin(all, self, sector, admin, now, TickProfiler.Instance is not null);
        if (all.Count <= 2)
        {
            foreach (var ship in all)
            {
                if (ship == self || ship == target || !ShipPoseVisible(self, ship, admin)) continue;
                heavy.Add(ship); ship.Deviation = 0;
            }
        }
        else
        {
            var others = ranking.Candidates;
            others.Clear();
            foreach (var ship in all)
                if (ship != self && ship != target && ShipPoseVisible(self, ship, admin)) others.Add(ship);
            int targetSlot = target is not null ? 1 : 0;
            int scaledHeavy = Math.Max(2,
                (int)Math.Ceiling(Math.Max(1, others.Count) / (double)DeadReckonExtrapolationTicks) + targetSlot);
            int heavyCount = Math.Max(0, scaledHeavy - targetSlot);
            int servedLimit = heavyCount + 2;
            int i = 0;
            foreach (var ship in ranking.Rank(servedLimit))
            {
                if (i < heavyCount) { heavy.Add(ship); ship.Deviation = 0; }
                else if (i < servedLimit) { light.Add(CaptureLightUpdate(ship)); ship.Deviation += 0.70f; }
                else ship.Deviation += 1.00f;
                i++;
            }
        }
        // P891: source telemetry has its own slot, outside the ranked remote
        // budget. Native launch supplies SET_CLUSTER (fsship.cpp:918-929), then
        // its local IGC predicts the parent between ranked HEAVY updates
        // (clintlib/appmsg.cpp:805-814). Our renderer needs a fresh wire source.
        if (parent is not null && !heavy.Contains(parent))
        {
            // Only child peers inspect their already selected (bounded) lists;
            // ordinary peers retain the exact ranked scan and quotas.
            for (int i = 0; i < light.Count; i++)
                if (light[i].ShipId == parent.Id) { light.RemoveAt(i); break; }
            heavy.Add(parent);
        }
        if (cadence is not null)
        {
            foreach (var ship in heavy) cadence.MarkPose(ship.Id, now);
            foreach (var ship in cadence.SelectFloor(now, _minUpdateHz, Math.Max(1, _config.UpdatesPerSecond)))
                heavy.Add(ship);
        }
        var active = CollectActiveTurretRelay(sector, self, tick);
        var inactive = CollectInactiveTurretRelay(sector);
        byte[]? lightBytes = light.Count == 0 && inactive.Length == 0 ? null
            : new LightShipsUpdate { LightShipEntries = light.ToArray(), InactiveTurretShipIds = inactive }.Encode();
        int turretBytes = active.Length * ServerActiveTurretUpdate.Width;
        int length = HeavyShipsUpdate.FixedSize + turretBytes + heavy.Count * HeavyShipUpdateEntry.Width;
        if (length > ushort.MaxValue) throw new InvalidOperationException("Heavy pose message exceeds wire length");
        var packet = PooledPosePacket.Rent(length + (lightBytes?.Length ?? 0) + FedPacket.CrcWidth);
        var bytes = packet.Buffer;
        bytes.Slice(0, HeavyShipsUpdate.FixedSize).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(2), HeavyShipsUpdate.Fmid);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(4), HeavyShipsUpdate.FixedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(6), (ushort)turretBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(8), (ushort)(HeavyShipsUpdate.FixedSize + turretBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(10), (ushort)(heavy.Count * HeavyShipUpdateEntry.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(12), tick);
        WritePoseVector(bytes.Slice(16), source.Position);
        bytes[28] = targetHull; bytes[29] = targetShield;
        bytes[30] = Percent(source.Fraction); bytes[31] = Percent(source.ShieldFraction);
        bytes[32] = bytes[33] = bytes[34] = 240;
        bytes[35] = Percent(source.EffectiveMineCapacity() <= 0 ? 0 : source.Ore / source.EffectiveMineCapacity());
        var writer = new WireWriter(bytes.Slice(HeavyShipsUpdate.FixedSize));
        foreach (var turret in active) turret.EncodeBody(ref writer);
        int offset = HeavyShipsUpdate.FixedSize + turretBytes;
        foreach (var ship in heavy)
        {
            HeavyPoseRecord record;
            lock (_snapshotCaptureGate)
            {
                if (!_rankedHeavy.TryGetValue(ship, out record))
                {
                    _rankedHeavy.Add(ship, record = HeavyPoseRecord.Capture(ship, tick));
                    Interlocked.Increment(ref ShipEncodingCounters.HeavyRecords);
                }
            }
            record.WriteRelative(bytes.Slice(offset, HeavyShipUpdateEntry.Width), source.Position);
            offset += HeavyShipUpdateEntry.Width;
        }
        if (lightBytes is not null) lightBytes.CopyTo(bytes.Slice(length));
        int packetLength = length + (lightBytes?.Length ?? 0);
        packet.Seal(packetLength);
        SendPeerCoalescing(peer.Key, packet, CoalesceShipsUpdate);
        if (cadence is not null) cadence.PacketBytes += packetLength + FedPacket.CrcWidth;
        Interlocked.Add(ref ShipEncodingCounters.RelativePositions, heavy.Count);
        // Scratch lists must not hold dead/replaced ships between passes.
        heavy.Clear(); light.Clear(); ranking.Candidates.Clear();
    }
}
