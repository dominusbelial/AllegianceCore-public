using Allegiance.Core.Sim;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    internal bool _broadcastInterestTiersEnabled;
    private double _tier1Hz = 5, _tier2Hz = 2;
    private float _engagementRange = 1500, _midDistanceRange = 6000;
    private readonly PoseAgeWindow _retiredTierAges = new();
    // Only the projection owns these identity keys. Workers classify copied
    // scalar values; no live Ship or Mission is part of the immutable input.
    private readonly Dictionary<Ship, InterestShipState> _interestSnapshot = new();

    internal readonly record struct InterestShipState(short Id, Vector3 Position,
        short? Side, sbyte? Alliance, short? Target, float WeaponRange);

    internal static InterestShipState CaptureInterestShip(Ship ship) => new(ship.Id,
        ship.Position, ship.SideId, ship.Mission?.Sides.FirstOrDefault(s => s.Id == ship.SideId)?.AllyGroup,
        (ship.CommandTarget as Ship)?.Id, WeaponReach(ship));

    private void CaptureInterestSnapshot(KeyValuePair<ConnectionId, LiveShip>[] peers)
    {
        _interestSnapshot.Clear();
        foreach (var mission in _missions)
            foreach (var sector in mission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship ship) _interestSnapshot[ship] = CaptureInterestShip(ship);
        foreach (var peer in peers) _interestSnapshot[peer.Value.Ship] = CaptureInterestShip(peer.Value.Ship);
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
        foreach (var weapon in ship.Weapons)
            reach = Math.Max(reach, weapon.GetLifespan() * weapon.ProjectileType.Speed);
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
