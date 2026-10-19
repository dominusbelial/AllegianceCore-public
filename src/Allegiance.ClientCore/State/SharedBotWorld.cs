using System.Collections;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>One worker/side's decoded spatial state. Never share this pool between
/// sides or servers. Session state, own ship, inventory, wallet and orders stay private.
/// All views use Gate, including snapshot publication and reads of live tables.</summary>
public sealed class SharedBotWorlds
{
    public SharedBotWorlds() : this(null) { }
    public SharedBotWorlds(short? sideId) { SideId = sideId; }
    public short? SideId { get; }
    public object Gate { get; } = new();
    private long _decoded, _reused;
    public long DecodedMessages => Interlocked.Read(ref _decoded);
    public long ReusedMessages => Interlocked.Read(ref _reused);
    internal void CountDecode(bool reused)
    {
        if (reused) Interlocked.Increment(ref _reused);
        else Interlocked.Increment(ref _decoded);
    }
    private readonly Dictionary<uint, WeakReference<SharedBotWorld>> _missions = new();
    internal SharedBotWorld Get(uint cookie)
    {
        if (_missions.TryGetValue(cookie, out var weak) && weak.TryGetTarget(out var world)) return world;
        foreach (var key in _missions.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToArray()) _missions.Remove(key);
        world = new() { Cookie = cookie };
        _missions[cookie] = new(world);
        return world;
    }
    internal void Retire(SharedBotWorld world)
    {
        if (_missions.TryGetValue(world.Cookie, out var weak) && weak.TryGetTarget(out var current) && ReferenceEquals(current, world))
            _missions.Remove(world.Cookie);
    }
}

internal sealed class SharedBotWorld
{
    internal uint Cookie;
    internal readonly ClientWorld Spatial = new();
    internal readonly HashSet<BotShipTable> Views = new();
    internal readonly Dictionary<ulong, (byte[] Wire, IFedMessage Message)> Decoded = new();
    internal DecisionSnapshot Snapshot = DecisionSnapshot.Empty;
    internal bool Dirty = true;
    internal long LastPublish;
    internal long Version;
}

/// <summary>A side table with one private own-ship overlay per pilot. A teammate's
/// coarser EXPORT/HEAVY/status can never overwrite that pilot's own status or inventory.</summary>
internal sealed class BotShipTable : IReadOnlyDictionary<short, ShipEntity>
{
    internal readonly Dictionary<short, ShipEntity> Shared;
    private readonly SharedBotWorld? _world;
    private ShipEntity? _own;
    internal short? OwnId { get; set; }
    internal BotShipTable() { Shared = new(); }
    internal BotShipTable(SharedBotWorld world)
    {
        _world = world;
        Shared = world.Spatial.ShipStorage;
        world.Views.Add(this);
    }
    public ShipEntity this[short key] => TryGetValue(key, out var ship) ? ship : throw new KeyNotFoundException();
    public int Count => Shared.Count + (_own is not null && !Shared.ContainsKey(_own.ShipId) ? 1 : 0);
    public IEnumerable<short> Keys => this.Select(p => p.Key);
    public IEnumerable<ShipEntity> Values => this.Select(p => p.Value);
    public bool ContainsKey(short key) => TryGetValue(key, out _);
    public bool TryGetValue(short key, out ShipEntity value)
    {
        if (_world is not null && key == OwnId)
        {
            if (_own?.ShipId == key) { value = _own; return true; }
            value = null!; return false;
        }
        return Shared.TryGetValue(key, out value!);
    }
    internal void Add(short key, ShipEntity value)
    {
        if (_world is not null && key == OwnId) _own = value;
        else Shared.Add(key, value);
    }
    internal bool Remove(short key)
    {
        if (_own?.ShipId == key) { _own = null; return true; }
        return Shared.Remove(key);
    }
    internal void Clear() { Shared.Clear(); _own = null; }
    public IEnumerator<KeyValuePair<short, ShipEntity>> GetEnumerator()
    {
        foreach (var pair in Shared)
            yield return _own?.ShipId == pair.Key ? new(pair.Key, _own) : pair;
        if (_own is not null && !Shared.ContainsKey(_own.ShipId)) yield return new(_own.ShipId, _own);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal ICollection<ShipEntity> SnapshotEntities
    {
        get
        {
            if (_world is null) return Shared.Values;
            var entities = new Dictionary<short, ShipEntity>(Shared);
            foreach (var view in _world.Views)
                if (view._own is { } own && own.ShipId == view.OwnId) entities[own.ShipId] = own;
            return entities.Values;
        }
    }
}
