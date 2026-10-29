using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission : IDisposable
{
    private StaticCoreCache.Lease? _coreLease;
    public bool IsDisposed { get; private set; }

    /// <summary>Retire this world's callbacks and ownership. Native dynamic
    /// termination is separate from ~CmissionIGC's last-core-consumer release
    /// (Igc/missionigc.cpp:3033-3053,3073-3125).</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Stage = MissionStage.Over;
        // Retained managed models must not keep a live route into a retired
        // world. Native Terminate moves ships to the NULL mission first
        // (missionigc.cpp:3085-3095), before retiring clusters and sides.
        foreach (var ship in GetShips())
        {
            ship.ReleaseGotoBuoy();
            DetachThingSite(ship);
            if (ReferenceEquals(ship.Mission, this))
            {
                ship.Sector?.Remove(ship);
                ship.Sector = null;
                ship.Mission = null;
            }
        }
        foreach (var warp in GetWarps()) DetachThingSite(warp);
        foreach (var station in GetStations()) { DetachThingSite(station); station.Mission = null; }
        foreach (var asteroid in GetAsteroids()) { DetachThingSite(asteroid); asteroid.Mission = null; asteroid.Sector = null; }
        foreach (var side in Sides) side.Mission = null;
        if (_teamLobby is not null) _teamLobby.Mission = null;
        foreach (var sector in Sectors)
        {
            foreach (var model in sector.Models) DetachThingSite(model);
            sector.Active = false; sector.Mission = null; sector.MissionSides = null;
        }
        _warpRegistry.Clear(); _stationRegistry.Clear(); _treasureRegistry.Clear();
        _asteroidRegistry.Clear(); _probeRegistry.Clear(); _mineRegistry.Clear();
        _sideRegistry.Clear(); _clusterRegistry.Clear(); _shipRegistry.Clear(); _buoyRegistry.Clear();
        _teamLobby = null;
        _firstGotoBuoy = _lastGotoBuoy = null;
        _coreLease?.Dispose();
        _coreLease = null;
    }
}
