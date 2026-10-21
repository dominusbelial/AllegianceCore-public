using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>Apply a local embedded-host observation without assigning an own ship or side.
    /// The caller owns Gate. Complete snapshots remove departed ships and destroyed map objects.</summary>
    public void ApplyLocalObservation(IEnumerable<IFedMessage> objects, IEnumerable<ProjectileEntity> projectiles, IEnumerable<MissileEntity>? missiles = null)
    {
        if (MyShipId is not null) throw new InvalidOperationException("A seated pilot cannot consume observer snapshots");
        _ships.Clear();
        _scoreRosterDirty = true;   // P774 — the observer frame replaces the ship table; MarkApplied rebuilds
        _probes.Clear();
        _mines.Clear();
        _missiles.Clear();
        _stations.Clear();
        _stationLive.Clear();
        _warps.Clear();
        _asteroids.Clear();
        foreach (var item in objects) Apply(item);
        foreach (var ship in _ships.Values) ship.PoseSectorId = ship.SectorId;
        _projectiles.Clear();
        _projectiles.AddRange(projectiles);
        if (missiles is not null) foreach (var missile in missiles) _missiles[missile.MissileId] = missile;
        MarkApplied();
    }
}
