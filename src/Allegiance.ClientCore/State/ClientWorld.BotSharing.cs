using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    private readonly SharedBotWorlds? _botWorlds;
    private SharedBotWorld? _botWorld;
    private DecisionSnapshot? _sharedSource;
    internal Dictionary<short, ShipEntity> ShipStorage => _ships.Shared;
    public ClientWorld() { }
    internal ClientWorld(SharedBotWorlds worlds) { _botWorlds = worlds; ReckonRemotes = false; CountOnlyProjectiles = true; }

    private void AttachBotWorld(uint cookie)
    {
        if (_botWorlds is null) return;
        if (_botWorlds.SideId is { } expected && (MySideId != expected || (MyShipId ?? JoinedShipId) is null))
        {
            // Auto seating can choose a different side. Keep that peer independent;
            // it must never consume or contribute the requested side's knowledge.
            if (_botWorld is not null)
            {
                short? id = MyShipId ?? JoinedShipId;
                ShipEntity? own = id is { } ship && _ships.TryGetValue(ship, out var entity) ? entity : null;
                DetachBotWorld();
                if (own is not null) _ships.Add(own.ShipId, own);
            }
            return;
        }
        if (_botWorld is { } previous && previous.Cookie != cookie) DetachBotWorld();
        var world = _botWorlds.Get(cookie);
        if (ReferenceEquals(world, _botWorld)) return;
        var receivedShips = _ships;
        var clusters = _clusters; var stations = _stations; var warps = _warps;
        var asteroids = _asteroids; var asteroidLive = _asteroidLive; var stationLive = _stationLive;
        var probes = _probes; var mines = _mines; var effects = _buildingEffects;
        DetachBotWorld();
        _botWorld = world;
        _ships = new(world) { OwnId = MyShipId ?? JoinedShipId ?? receivedShips.OwnId };
        foreach (var pair in receivedShips)
            if (pair.Key == _ships.OwnId) _ships.Add(pair.Key, pair.Value);
            else _ships.Shared.TryAdd(pair.Key, pair.Value);
        var spatial = world.Spatial;
        _clusters = Merge(spatial._clusters, clusters); _stations = Merge(spatial._stations, stations); _warps = Merge(spatial._warps, warps);
        _asteroids = Merge(spatial._asteroids, asteroids); _asteroidLive = Merge(spatial._asteroidLive, asteroidLive); _stationLive = Merge(spatial._stationLive, stationLive);
        _probes = Merge(spatial._probes, probes); _mines = Merge(spatial._mines, mines); _buildingEffects = Merge(spatial._buildingEffects, effects);
        world.Dirty = true;
        // Treasures and effects are sector-relative; they remain private.
    }

    private static Dictionary<short, T> Merge<T>(Dictionary<short, T> shared, Dictionary<short, T> received)
    {
        foreach (var pair in received) shared.TryAdd(pair.Key, pair.Value);
        return shared;
    }

    private void ConfirmBotSide()
    {
        if (_mission is { } mission) AttachBotWorld(mission.Cookie);
    }

    internal void ReleaseBotView() => DetachBotWorld();

    private void DetachBotWorld()
    {
        if (_botWorld is null) return;
        _botWorld.Views.Remove(_ships);
        _botWorld.Dirty = true;
        _botWorld = null;
        _sharedSource = null;
        _publishedDecision = DecisionSnapshot.Empty;
        _ships = new();
        _clusters = new(); _stations = new(); _warps = new(); _asteroids = new();
        _asteroidLive = new(); _stationLive = new(); _probes = new(); _mines = new(); _buildingEffects = new();
    }

    private bool OtherBotObserves(short sector) => _botWorld is not null && _botWorld.Views.Any(view =>
        !ReferenceEquals(view, _ships) && view.OwnId is { } id && view.TryGetValue(id, out var own)
        && own.Latest is not null && own.StationId < 0 && own.SectorId == sector);

    private DecisionSnapshot PublishSharedDecision()
    {
        var world = _botWorld!;
        if (!DecisionSnapshotEnabled || System.Threading.Volatile.Read(ref _decisionRefreshPending) == 0) return _publishedDecision;
        long now = (long)(NowSeconds() * 1000);
        if (world.Dirty && now - world.LastPublish >= DecisionSnapPublishIntervalMs)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var snapshot = new DecisionSnapshot { Version = ++world.Version, PublishedTick64 = now };
            FillShipSnapshot(snapshot); FillStationSnapshot(snapshot); FillWarpSnapshot(snapshot);
            world.Snapshot = snapshot;
            world.LastPublish = now;
            world.Dirty = false;
            System.Threading.Interlocked.Increment(ref _snapPublishCount);
            System.Threading.Interlocked.Add(ref _snapPublishTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
        var shared = world.Snapshot;
        if (!ReferenceEquals(shared, _sharedSource) || _publishedDecision.MyShipId != MyShipId
            || _publishedDecision.MySideId != MySideId || _publishedDecision.MissionGeneration != MissionGeneration)
        {
            _sharedSource = shared;
            short cluster = MyShipId is { } me && DecisionSnapshot.TryFind(shared.Ships, me, out var own) ? own.SectorId : (short)-1;
            System.Threading.Volatile.Write(ref _publishedDecision, new DecisionSnapshot
            {
                Version = shared.Version, PublishedTick64 = shared.PublishedTick64, MissionGeneration = MissionGeneration,
                MyShipId = MyShipId, MySideId = MySideId, MyCluster = cluster,
                Ships = shared.Ships, VisibleShips = shared.VisibleShips, Stations = shared.Stations, Warps = shared.Warps
            });
        }
        return _publishedDecision;
    }

    internal delegate T WorldDecoder<T>(ReadOnlySpan<byte> data) where T : class, IFedMessage;
    internal T DecodeBotMessage<T>(byte[] data, WorldDecoder<T> decode) where T : class, IFedMessage
    {
        if (_botWorld is null) return decode(data);
        // Bounded exact-byte cache. Hash collisions are checked, never trusted.
        // Only immutable telemetry/map records use it; never player/wallet/events.
        ulong hash = 14695981039346656037;
        foreach (byte b in data) hash = (hash ^ b) * 1099511628211;
        var cache = _botWorld.Decoded;
        if (cache.TryGetValue(hash, out var entry) && data.AsSpan().SequenceEqual(entry.Wire))
        { _botWorlds!.CountDecode(true); return (T)entry.Message; }
        _botWorlds!.CountDecode(false);
        var message = decode(data);
        if (cache.Count >= 2048) cache.Clear();
        cache[hash] = (data, message);
        return message;
    }
}

