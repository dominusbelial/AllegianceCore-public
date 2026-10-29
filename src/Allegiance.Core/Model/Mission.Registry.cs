using Allegiance.Core.Sim;
using System.Collections;

namespace Allegiance.Core.Model;

// Native Igc/igc.h:5130-5197: append strong references, remove the first
// pointer-identical member, and look up the first matching ObjectID.
internal sealed class MissionRegistry<T> where T : class
{
    private sealed class Node(T value)
    {
        internal readonly T Value = value;
        internal Node? Previous, Next, SameNext;
    }
    private readonly Dictionary<T, (Node First, Node Last)> _members = new(ReferenceEqualityComparer.Instance);
    private Node? _first, _last;
    private int _count;
    private readonly Func<T, short> _id;
    private sealed class RegistryView(MissionRegistry<T> owner) : IReadOnlyList<T>
    {
        public int Count => owner._count;
        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                var node = owner._first!;
                while (index-- > 0) node = node.Next!;
                return node.Value;
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            for (var node = owner._first; node is not null; node = node.Next) yield return node.Value;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal IReadOnlyList<T> View { get; }
    internal MissionRegistry(Func<T, short> id) { _id = id; View = new RegistryView(this); }
    internal void Add(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = new Node(value) { Previous = _last };
        if (_last is null) _first = node; else _last.Next = node;
        _last = node;
        if (_members.TryGetValue(value, out var same))
        {
            same.Last.SameNext = node;
            _members[value] = (same.First, node);
        }
        else _members.Add(value, (node, node));
        _count++;
    }
    internal void Delete(T value)
    {
        if (!_members.TryGetValue(value, out var same)) return;
        var node = same.First;
        if (node.SameNext is { } next) _members[value] = (next, same.Last);
        else _members.Remove(value);
        if (node.Previous is null) _first = node.Next; else node.Previous.Next = node.Next;
        if (node.Next is null) _last = node.Previous; else node.Next.Previous = node.Previous;
        _count--;
    }
    internal T? Get(short objectId)
    {
        for (var node = _first; node is not null; node = node.Next)
            if (_id(node.Value) == objectId) return node.Value;
        return null;
    }
    internal bool Contains(T value) => _members.ContainsKey(value);
    internal void Clear() { _first = _last = null; _members.Clear(); _count = 0; }
}

public sealed partial class Mission
{
    private readonly MissionRegistry<ResolvedWarp> _warpRegistry = new(v => v.Warp.WarpId);
    private readonly MissionRegistry<Station> _stationRegistry = new(v => v.StationId);
    private readonly MissionRegistry<Treasure> _treasureRegistry = new(v => v.Id);
    private readonly MissionRegistry<Asteroid> _asteroidRegistry = new(v => v.Data.AsteroidId);
    private readonly MissionRegistry<Probe> _probeRegistry = new(v => v.Id);
    private readonly MissionRegistry<Mine> _mineRegistry = new(v => v.Id);
    private readonly MissionRegistry<Side> _sideRegistry = new(v => v.Id);
    private readonly MissionRegistry<Sector> _clusterRegistry = new(v => v.Id);
    private readonly MissionRegistry<Ship> _shipRegistry = new(v => v.Id);
    private readonly MissionRegistry<Buoy> _buoyRegistry = new(v => v.Id);
    // Igc/missionigc.h:389,852-855: buoy IDs use their own mission counter.
    private short _nextBuoyId;
    internal short GenerateNewBuoyID() => _nextBuoyId++;
    private Side? _teamLobby;
    // Native modelIGC.h:149-170 owns the site on the model. Its constructor
    // only stores flags/owner (FedSrv.CPP:10692-10709); visibility already lives
    // on each model. Materialize the wrapper only when a host asks for it.
    public ThingSite? GetThingSite(IModelIgc model)
    {
        if (IsDisposed) return null;
        ThingSite? site;
        switch (model)
        {
            case Sim.Model m when ReferenceEquals(m.ThingSiteMission, this):
                site = m.ThingSite ??= Site.CreateThingSite(model); break;
            case Station m when ReferenceEquals(m.ThingSiteMission, this):
                site = m.ThingSite ??= Site.CreateThingSite(model); break;
            case Asteroid m when ReferenceEquals(m.ThingSiteMission, this):
                site = m.ThingSite ??= Site.CreateThingSite(model); break;
            case ResolvedWarp m when ReferenceEquals(m.ThingSiteMission, this):
                site = m.ThingSite ??= Site.CreateThingSite(model); break;
            default: return null;
        }
        if (model is ISideVisible visible) site.BindVisibility(visible.SideVisibility);
        return site;
    }
    internal void DetachThingSite(IModelIgc model)
    {
        switch (model)
        {
            case Sim.Model m: m.ThingSiteMission = null; m.ThingSite = null; break;
            case Station m: m.ThingSiteMission = null; m.ThingSite = null; break;
            case Asteroid m: m.ThingSiteMission = null; m.ThingSite = null; break;
            case ResolvedWarp m: m.ThingSiteMission = null; m.ThingSite = null; break;
        }
    }
    private void AttachThingSite(IModelIgc model)
    {
        switch (model)
        {
            case Sim.Model m: m.ThingSiteMission = this; break;
            case Station m: m.ThingSiteMission = this; break;
            case Asteroid m: m.ThingSiteMission = this; break;
            case ResolvedWarp m: m.ThingSiteMission = this; break;
        }
    }


    public void AddWarp(ResolvedWarp value) { AttachThingSite(value); _warpRegistry.Add(value); }
    public void DeleteWarp(ResolvedWarp value)
    {
        _warpRegistry.Delete(value);
    }
    public ResolvedWarp? GetWarp(short id) => _warpRegistry.Get(id);
    public IReadOnlyList<ResolvedWarp> GetWarps() => _warpRegistry.View;

    public void AddStation(Station value) { AttachThingSite(value); _stationRegistry.Add(value); }
    public void DeleteStation(Station value)
    {
        _stationRegistry.Delete(value);
    }
    public Station? GetStation(short id) => _stationRegistry.Get(id);
    public IReadOnlyList<Station> GetStations() => _stationRegistry.View;

    public void AddTreasure(Treasure value) { AttachThingSite(value); _treasureRegistry.Add(value); }
    public void DeleteTreasure(Treasure value)
    {
        _treasureRegistry.Delete(value);
    }
    public Treasure? GetTreasure(short id) => _treasureRegistry.Get(id);
    public IReadOnlyList<Treasure> GetTreasures() => _treasureRegistry.View;

    public void AddAsteroid(Asteroid value) { AttachThingSite(value); _asteroidRegistry.Add(value); }
    public void DeleteAsteroid(Asteroid value)
    {
        _asteroidRegistry.Delete(value);
    }
    public Asteroid? GetAsteroid(short id) => _asteroidRegistry.Get(id);
    public IReadOnlyList<Asteroid> GetAsteroids() => _asteroidRegistry.View;

    public void AddProbe(Probe value) { AttachThingSite(value); _probeRegistry.Add(value); }
    public void DeleteProbe(Probe value)
    {
        _probeRegistry.Delete(value);
    }
    public Probe? GetProbe(short id) => _probeRegistry.Get(id);
    public IReadOnlyList<Probe> GetProbes() => _probeRegistry.View;

    public void AddMine(Mine value) { AttachThingSite(value); _mineRegistry.Add(value); }
    public void DeleteMine(Mine value)
    {
        _mineRegistry.Delete(value);
    }
    public Mine? GetMine(short id) => _mineRegistry.Get(id);
    public IReadOnlyList<Mine> GetMines() => _mineRegistry.View;

    public void AddSide(Side value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_teamLobby is null && value.Id != -2 && Core.Civilizations.Count > 0)
        {
            // Igc/missionigc.cpp:3608-3637: separate, active white lobby side.
            _teamLobby = new Side(-2, "Team Lobby", Core.Civilizations.Values.First())
            {
                Mission = this, GlobalAttributes = GlobalAttributeSet.Identity,
                DevelopmentTechs = TechTreeBitMask.Empty, InitialTechs = TechTreeBitMask.Empty,
                ColorR = 1, ColorG = 1, ColorB = 1, Active = true
            };
        }
        value.Mission = this;
        _sideRegistry.Add(value);
    }
    public void DeleteSide(Side value) => _sideRegistry.Delete(value);
    public Side? GetSide(short id) => id == -2 ? _teamLobby : _sideRegistry.Get(id);
    public IReadOnlyList<Side> GetSides() => _sideRegistry.View;

    public void AddCluster(Sector value) { _clusterRegistry.Add(value); }
    public void DeleteCluster(Sector value) => _clusterRegistry.Delete(value);
    public Sector? GetCluster(short id) => _clusterRegistry.Get(id);
    public IReadOnlyList<Sector> GetClusters() => _clusterRegistry.View;

    public void AddShip(Ship value) { AttachThingSite(value); _shipRegistry.Add(value); }
    public void DeleteShip(Ship value)
    {
        _shipRegistry.Delete(value);
    }
    public Ship? GetShip(short id) => _shipRegistry.Get(id);
    public IReadOnlyList<Ship> GetShips() => _shipRegistry.View;

    public void AddBuoy(Buoy value) { ObserveGotoBuoys(); AttachThingSite(value); _buoyRegistry.Add(value); }
    public void DeleteBuoy(Buoy value)
    {
        _buoyRegistry.Delete(value);
    }
    public Buoy? GetBuoy(short id) { ObserveGotoBuoys(); return _buoyRegistry.Get(id); }
    public IReadOnlyList<Buoy> GetBuoys() { ObserveGotoBuoys(); return _buoyRegistry.View; }

    // Goto orders already hold all waypoint data. Until the registry is read,
    // keep only intrusive ship links and the reserved native ID, with no Buoy,
    // position array or registry node allocation. Once observed, preserve the
    // live view's eager mutation semantics and exact append order.
    private Ship? _firstGotoBuoy, _lastGotoBuoy;
    private bool _observesGotoBuoys;
    internal void RegisterGotoBuoy(Ship ship)
    {
        ship.GotoBuoyPrevious = _lastGotoBuoy;
        if (_lastGotoBuoy is null) _firstGotoBuoy = ship; else _lastGotoBuoy.GotoBuoyNext = ship;
        _lastGotoBuoy = ship;
        if (_observesGotoBuoys) MaterializeGotoBuoy(ship);
    }
    internal void UnregisterGotoBuoy(Ship ship, Buoy? buoy)
    {
        if (ship.GotoBuoyPrevious is { } previous) previous.GotoBuoyNext = ship.GotoBuoyNext;
        else _firstGotoBuoy = ship.GotoBuoyNext;
        if (ship.GotoBuoyNext is { } next) next.GotoBuoyPrevious = ship.GotoBuoyPrevious;
        else _lastGotoBuoy = ship.GotoBuoyPrevious;
        ship.GotoBuoyPrevious = ship.GotoBuoyNext = null;
        if (buoy is not null) { DeleteBuoy(buoy); DetachThingSite(buoy); }
    }
    private void ObserveGotoBuoys()
    {
        if (_observesGotoBuoys || IsDisposed) return;
        _observesGotoBuoys = true;
        for (var ship = _firstGotoBuoy; ship is not null; ship = ship.GotoBuoyNext) MaterializeGotoBuoy(ship);
    }
    private void MaterializeGotoBuoy(Ship ship)
    {
        var buoy = ship.MaterializeGotoBuoy();
        AttachThingSite(buoy);
        _buoyRegistry.Add(buoy);
    }

    internal void RegisterModel(Sim.Model model)
    {
        AttachThingSite(model);
        // Cluster membership can change without ending mission ownership.
        switch (model)
        {
            case Ship value when !_shipRegistry.Contains(value): AddShip(value); break;
            case Treasure value when !_treasureRegistry.Contains(value): AddTreasure(value); break;
            case Probe value when !_probeRegistry.Contains(value): AddProbe(value); break;
            case Mine value when !_mineRegistry.Contains(value): AddMine(value); break;
            case Buoy value when !_buoyRegistry.Contains(value): AddBuoy(value); break;
        }
    }

    internal void UnregisterModel(Sim.Model model)
    {
        DetachThingSite(model);
        switch (model)
        {
            case Ship value: value.ReleaseGotoBuoy(); DeleteShip(value); break;
            case Treasure value: DeleteTreasure(value); break;
            case Probe value: DeleteProbe(value); break;
            case Mine value: DeleteMine(value); break;
            case Buoy value: DeleteBuoy(value); break;
        }
    }
}
