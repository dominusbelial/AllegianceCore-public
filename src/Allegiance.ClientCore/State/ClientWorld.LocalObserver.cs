using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>Apply a local embedded-host observation without assigning an own ship or side.
    /// The caller owns Gate. Complete snapshots remove departed ships and destroyed map objects.</summary>
    public void ApplyLocalObservation(IEnumerable<IFedMessage> objects, IEnumerable<ProjectileEntity> projectiles, IEnumerable<MissileEntity>? missiles = null)
    {
        if (MyShipId is not null) throw new InvalidOperationException("A seated pilot cannot consume observer snapshots");
        _observerTerrainVersion = null;
        _ships.Clear();
        _scoreRosterDirty = true;   // P774 — the observer frame replaces the ship table; MarkApplied rebuilds
        _probes.Clear();
        _mines.Clear();
        _missiles.Clear();
        _constructionVisuals.Clear();
        _stations.Clear();
        _stationLive.Clear();
        _warps.Clear();
        _asteroids.Clear();
        foreach (var item in objects) Apply(item);
        FinishObservation(projectiles, missiles);
    }

    private object? _observerTerrainVersion;

    // A dictionary enumerates its physical slots. Removing a departed id and then
    // appending a new arrival can reuse a hole ahead of a survivor. Rebuild only
    // the tables whose order changed, before applying records in export order.
    private struct ObserverOrder<T>(Dictionary<short, T> table)
    {
        private Dictionary<short, T>.KeyCollection.Enumerator _keys = table.Keys.GetEnumerator();
        private bool _changed;

        public void Visit(short id)
        {
            if (!_changed) _changed = !_keys.MoveNext() || _keys.Current != id;
        }

        public bool Changed => _changed || _keys.MoveNext();

        public void Prepare()
        {
            if (Changed) table.Clear();
        }
    }

    /// <summary>Apply a complete ordered embedded skirmish sample. The general local
    /// observation API also accepts partial wire sequences and construction effects.</summary>
    public void ApplySkirmishObservation(IFedMessage[] objects, IFedMessage[] dynamicObjects, object? terrainVersion,
        ProjectileEntity[] projectiles, MissileEntity[] missiles)
    {
        if (terrainVersion is null || terrainVersion != _observerTerrainVersion || _botWorld is not null || MyShipId is not null)
        {
            ApplyLocalObservation(objects, projectiles, missiles);
            _observerTerrainVersion = terrainVersion;
            return;
        }
        var ships = new ObserverOrder<ShipEntity>(_ships.Shared);
        var probes = new ObserverOrder<ProbeEntity>(_probes);
        var mines = new ObserverOrder<MineEntity>(_mines);
        var missileOrder = new ObserverOrder<MissileEntity>(_missiles);
        foreach (var item in dynamicObjects)
        {
            switch (item)
            {
                case ExportShip ship: ships.Visit(ship.ShipId); break;
                case ExportProbe probe: probes.Visit(probe.Probe.ProbeId); break;
                case ExportMine mine: mines.Visit(mine.Mine.MineId); break;
            }
        }
        foreach (var missile in missiles) missileOrder.Visit(missile.MissileId);
        if (ships.Changed) _ships.Clear();
        else
        {
            // Fresh entities formerly began each three-message sequence without
            // a confirmed pose or sighting. In particular, a status in a new
            // sector must not retire the previous interpolation history here.
            foreach (var ship in _ships.Shared.Values)
            {
                ship.PoseSectorId = null;
                ship.NewContactSounded = false;
            }
        }
        probes.Prepare();
        mines.Prepare();
        missileOrder.Prepare();
        _constructionVisuals.Clear();
        _scoreRosterDirty = true;
        // A stable ship's empty exported inventory and identity are already installed.
        // Status and pose still flow through both normal reducers on every sample.
        foreach (var item in dynamicObjects)
        {
            if (item is ExportShip export && _ships.TryGetValue(export.ShipId, out var ship)
                && ship.HullId == export.HullId && ship.SideId == export.SideId && ship.CharacterName == export.CharacterName)
            {
                AnnounceNewContactIfNew(ship);
                continue;
            }
            Apply(item);
        }
        FinishObservation(projectiles, missiles);
    }

    private void FinishObservation(IEnumerable<ProjectileEntity> projectiles, IEnumerable<MissileEntity>? missiles)
    {
        foreach (var ship in _ships.Values) ship.PoseSectorId = ship.SectorId;
        _projectiles.Clear();
        _projectiles.AddRange(projectiles);
        if (missiles is not null) foreach (var missile in missiles) _missiles[missile.MissileId] = missile;
        MarkApplied();
    }
}
