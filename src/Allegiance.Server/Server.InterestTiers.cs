using System.Buffers.Binary;
using Allegiance.Core.Profiling;
using Allegiance.Core.Wire;
using Allegiance.Core.Sim;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly Dictionary<Ship, HeavyPoseRecord> _tierHeavy = new();
    private Func<Ship, Ship, int>? _interestClassifier;

    private void BroadcastTierPose(ConnectionId peer, Ship self, Ship? target, Sector sector,
        bool admin, uint tick, double now, byte targetHull, byte targetShield)
    {
        var candidates = sector.GetShipUpdateCandidates();
        var cadence = _viewerPoseCadence[peer];
        cadence.Begin(candidates, self, sector, admin, now, TickProfiler.Instance is not null,
            _interestClassifier ??= ClassifyInterest, cloakOnly: true, tierServer: this);
        var selected = cadence.SelectTierCandidates(self, target, now, _tier1Hz, _tier2Hz,
            _config.UpdatesPerSecond, tick);
        bool seesTarget = target is not null && (admin || !target.IsCloaked
            || self.SideId is not { } side || target.SeenBySide(side));
        int count = selected.Length + (seesTarget ? 1 : 0);
        int length = HeavyShipsUpdate.FixedSize + count * HeavyShipUpdateEntry.Width;
        if (length > ushort.MaxValue) throw new InvalidOperationException("Heavy pose message exceeds wire length");
        var packet = PooledPosePacket.Rent(length + FedPacket.CrcWidth);
        var bytes = packet.Buffer;
        bytes.Slice(0, HeavyShipsUpdate.FixedSize).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(2), HeavyShipsUpdate.Fmid);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(4), HeavyShipsUpdate.FixedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(8), HeavyShipsUpdate.FixedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(10), (ushort)(count * HeavyShipUpdateEntry.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(12), tick);
        WritePoseVector(bytes.Slice(16), self.Position);
        bytes[28] = targetHull; bytes[29] = targetShield;
        bytes[30] = Percent(self.Fraction); bytes[31] = Percent(self.ShieldFraction);
        bytes[32] = bytes[33] = bytes[34] = 240;
        bytes[35] = Percent(self.EffectiveMineCapacity() <= 0 ? 0 : self.Ore / self.EffectiveMineCapacity());
        int offset = HeavyShipsUpdate.FixedSize;
        if (seesTarget) WriteTierRecord(target!, self.Position, tick, bytes, ref offset, cadence, now);
        foreach (ref readonly var candidate in selected)
        {
            WriteTierRecord(candidate.Ship, self.Position, tick, bytes, ref offset, cadence, now);
            candidate.Ship.Deviation = 0;
        }
        packet.Seal(length);
        SendPeerCoalescing(peer, packet, CoalesceShipsUpdate);
        cadence.PacketBytes += length + FedPacket.CrcWidth;
        Interlocked.Add(ref ShipEncodingCounters.RelativePositions, count);
    }

    private void WriteTierRecord(Ship ship, Vector3 reference, uint tick, Span<byte> bytes,
        ref int offset, ViewerPoseCadence cadence, double now)
    {
        HeavyPoseRecord record;
        lock (_snapshotCaptureGate)
        {
            if (!_tierHeavy.TryGetValue(ship, out record))
            {
                _tierHeavy.Add(ship, record = HeavyPoseRecord.Capture(ship, tick));
                Interlocked.Increment(ref ShipEncodingCounters.HeavyRecords);
            }
        }
        record.WriteRelative(bytes.Slice(offset, HeavyShipUpdateEntry.Width), reference);
        offset += HeavyShipUpdateEntry.Width;
        cadence.MarkPose(ship.Id, now);
    }

    private static void WritePoseVector(Span<byte> bytes, Vector3 position)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, position.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(4), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(8), position.Z);
    }

    internal bool _broadcastInterestTiersEnabled;
    private double _tier1Hz = 5, _tier2Hz = 2;
    private float _engagementRange = 1500, _midDistanceRange = 6000;
    private readonly PoseAgeWindow _retiredTierAges = new();
    // Only the projection owns these identity keys. Workers classify copied
    // scalar values; no live Ship or Mission is part of the immutable input.
    private readonly Dictionary<Ship, InterestShipState> _interestSnapshot = new();

    internal readonly record struct InterestShipState(short Id, Vector3 Position,
        short? Side, sbyte? Alliance, short? Target, float WeaponRange, InterestCell Cell);

    internal readonly record struct InterestCell(int X, int Y, int Z)
    {
        internal const float Width = 500;
        internal bool IsUsable => X != int.MinValue;
        internal static InterestCell At(Vector3 position)
        {
            double x = Math.Floor((double)position.X / Width);
            double y = Math.Floor((double)position.Y / Width);
            double z = Math.Floor((double)position.Z / Width);
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || x <= int.MinValue || x >= int.MaxValue || y <= int.MinValue || y >= int.MaxValue
                || z <= int.MinValue || z >= int.MaxValue)
                return new(int.MinValue, int.MinValue, int.MinValue);
            return new((int)x, (int)y, (int)z);
        }
    }

    internal struct InterestMembership
    {
        internal InterestCell ViewerCell, SubjectCell;
        internal short? ViewerSide, SubjectSide;
        internal sbyte? ViewerAlliance, SubjectAlliance;
        internal float Engagement;
        internal int Tier, LastEvaluation;
        internal bool Certified;
    }

    internal long TierExactClassifications, TierCellClassifications, TierCachedClassifications;
    internal InterestShipState InterestStateFor(Ship ship)
        => _interestSnapshot.TryGetValue(ship, out var state) ? state : CaptureInterestShip(ship);

    internal int ClassifyInterestCached(InterestShipState viewer, Ship ship, ref InterestMembership cache)
    {
        var subject = InterestStateFor(ship);
        cache.LastEvaluation = 3; // own/target/alliance requires no distance
        if (viewer.Id == subject.Id || viewer.Target == subject.Id) return 0;
        bool ally = viewer.Side is not null && subject.Side is not null
            && (viewer.Side == subject.Side || (viewer.Alliance is not null && viewer.Alliance == subject.Alliance));
        if (ally) return 1;
        if (!viewer.Cell.IsUsable || !subject.Cell.IsUsable)
        {
            cache.Certified = false;
            cache.LastEvaluation = 0;
            return ClassifyInterest(viewer, subject);
        }
        float engagement = Math.Max(_engagementRange, Math.Max(viewer.WeaponRange, subject.WeaponRange));
        if (cache.Certified && cache.ViewerCell == viewer.Cell && cache.SubjectCell == subject.Cell
            && cache.ViewerSide == viewer.Side && cache.SubjectSide == subject.Side
            && cache.ViewerAlliance == viewer.Alliance && cache.SubjectAlliance == subject.Alliance
            && cache.Engagement == engagement)
        {
            cache.LastEvaluation = 2;
            return cache.Tier;
        }
        // Conservative cell bounds are a spatial broad phase. Only contacts in
        // cells crossing a radius need exact per-pair distance evaluation. A
        // membership certificate survives motion inside the same cells; it is
        // invalidated immediately on cell, alliance, weapon or target changes.
        double dx = Math.Abs((double)viewer.Cell.X - subject.Cell.X);
        double dy = Math.Abs((double)viewer.Cell.Y - subject.Cell.Y);
        double dz = Math.Abs((double)viewer.Cell.Z - subject.Cell.Z);
        double nearX = Math.Max(0, dx - 1), nearY = Math.Max(0, dy - 1), nearZ = Math.Max(0, dz - 1);
        double cellSquared = InterestCell.Width * InterestCell.Width;
        double min = (nearX * nearX + nearY * nearY + nearZ * nearZ) * cellSquared;
        double max = ((dx + 1) * (dx + 1) + (dy + 1) * (dy + 1) + (dz + 1) * (dz + 1)) * cellSquared;
        float reachSquared = engagement * engagement, midSquared = _midDistanceRange * _midDistanceRange;
        // Cover the legacy single-precision subtraction/square/sum rounding,
        // including configured weapon reaches much larger than normal maps.
        min = Math.Max(0, min - Math.Max(1, min * 1e-6));
        max += Math.Max(1, max * 1e-6);
        int tier = max < reachSquared ? 0
            : min > reachSquared && max < midSquared ? 1
            : min > Math.Max(reachSquared, midSquared) ? 2 : -1;
        cache = new() { ViewerCell = viewer.Cell, SubjectCell = subject.Cell,
            ViewerSide = viewer.Side, SubjectSide = subject.Side,
            ViewerAlliance = viewer.Alliance, SubjectAlliance = subject.Alliance,
            Engagement = engagement, Tier = tier, Certified = tier >= 0 };
        if (tier >= 0) { cache.LastEvaluation = 1; return tier; }
        cache.LastEvaluation = 0;
        return ClassifyInterest(viewer, subject);
    }

    internal static InterestShipState CaptureInterestShip(Ship ship) => new(ship.Id,
        ship.Position, ship.SideId, AllianceFor(ship),
        (ship.CommandTarget as Ship)?.Id, WeaponReach(ship), InterestCell.At(ship.Position));

    private static sbyte? AllianceFor(Ship ship)
    {
        if (ship.Mission is { } mission)
            for (int i = 0; i < mission.Sides.Count; i++)
                if (mission.Sides[i].Id == ship.SideId) return mission.Sides[i].AllyGroup;
        return null;
    }

    private void CaptureInterestSnapshot(KeyValuePair<ConnectionId, LiveShip>[] peers)
    {
        _interestSnapshot.Clear();
        _tierHeavy.Clear();
        foreach (var mission in _missions)
            foreach (var sector in mission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship ship) _interestSnapshot[ship] = CaptureInterestShip(ship);
        foreach (var peer in peers)
            if (!_interestSnapshot.ContainsKey(peer.Value.Ship))
                _interestSnapshot[peer.Value.Ship] = CaptureInterestShip(peer.Value.Ship);
    }

    internal static bool ResolveInterestTiersFlag(bool configured, string? environment) => environment?.Trim().ToLowerInvariant() switch
    {
        null or "" => configured,
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw new InvalidDataException("ALLEGIANCE_BROADCAST_TIERS must be 0/1 or false/true"),
    };

    private void InitInterestTiers()
    {
        var config = _config.Broadcast ?? new BroadcastConfig();
        if (!double.IsFinite(config.Tier1Hz) || config.Tier1Hz <= 0
            || !double.IsFinite(config.Tier2Hz) || config.Tier2Hz <= 0
            || !float.IsFinite(config.EngagementRange) || config.EngagementRange <= 0
            || !float.IsFinite(config.MidDistanceRange) || config.MidDistanceRange < config.EngagementRange)
            throw new InvalidDataException("broadcast tier rates must be finite and positive; midDistanceRange must be >= engagementRange > 0");
        _tier2Hz = Math.Min(Math.Max(config.Tier2Hz, _minUpdateHz), Math.Max(1, _config.UpdatesPerSecond));
        _tier1Hz = Math.Min(Math.Max(config.Tier1Hz, _tier2Hz), Math.Max(1, _config.UpdatesPerSecond));
        _engagementRange = config.EngagementRange;
        _midDistanceRange = config.MidDistanceRange;
        _retiredTierAges.Clear();
    }

    internal int ClassifyInterest(Ship viewer, Ship subject)
        => ClassifyInterest(_interestSnapshot.TryGetValue(viewer, out var v) ? v : CaptureInterestShip(viewer),
            _interestSnapshot.TryGetValue(subject, out var s) ? s : CaptureInterestShip(subject));

    internal int ClassifyInterest(InterestShipState viewer, InterestShipState subject)
    {
        if (viewer.Id == subject.Id || viewer.Target == subject.Id) return 0;
        float distanceSquared = (viewer.Position - subject.Position).LengthSquared();
        bool ally = viewer.Side is not null && subject.Side is not null
            && (viewer.Side == subject.Side || (viewer.Alliance is not null && viewer.Alliance == subject.Alliance));
        // The configured radius is a lower bound. Long-range guns on either
        // combatant must not leave an active firing contact in a slow tier.
        float engagement = Math.Max(_engagementRange, Math.Max(viewer.WeaponRange, subject.WeaponRange));
        if (!ally && distanceSquared <= engagement * engagement) return 0;
        return ally || distanceSquared <= _midDistanceRange * _midDistanceRange ? 1 : 2;
    }

    private static float WeaponReach(Ship ship)
    {
        float reach = 0;
        for (int i = 0; i < ship.Weapons.Count; i++)
        {
            var weapon = ship.Weapons[i];
            reach = Math.Max(reach, weapon.GetLifespan() * weapon.ProjectileType.Speed);
        }
        return reach;
    }

    internal double InterestRate(int tier) => tier == 0 ? Math.Max(1, _config.UpdatesPerSecond)
        : tier == 1 ? _tier1Hz : _tier2Hz;

    private static string FormatTierAges(PoseAgeWindow window)
        => string.Concat(Enumerable.Range(0, 3).Select(tier =>
        {
            var band = window.Band(tier);
            return FormattableString.Invariant($" T{tier}[n={band.Count},p50={band.P50:0.###},p95={band.P95:0.###},max={band.Max:0.###}s]");
        }));
}
