using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

/// <summary>Single producer. Capture reads the mission under the tick gate. Build consumes only
/// captured values. Published arrays never serve as scratch for a subsequent observation.</summary>
internal sealed class SkirmishObservationSampler
{
    internal Func<long> LocalMilliseconds { get; init; } = static () => Environment.TickCount64;
    private readonly List<SectorSample> _sectors = [];
    private readonly List<SideRoster.SideRecord> _roster = [];
    private readonly List<(string? Name, int Kills, int Deaths, int End)> _sides = [];
    private readonly List<(string Name, int Bought, int Complete)> _research = [];
    private readonly List<ProjectileEntity> _projectiles = [];
    private readonly List<MissileEntity> _missiles = [];
    private readonly List<(short Id, bool Pod, short Sector)> _hulls = [];
    private readonly Dictionary<short, (bool Pod, short Sector)> _deaths = [];
    private readonly Dictionary<short, double> _kills = [];
    private readonly Dictionary<short, string> _shipNames = [];
    private readonly List<IFedMessage> _objects = [];
    private readonly List<IFedMessage> _dynamicObjects = [];
    private object _terrainVersion = new();
    private string _epoch = "";
    private MissionStage _stage;
    private double _seconds;

    private sealed class SectorSample
    {
        // Identity only. Build never follows this reference back into the simulation.
        internal Sector? Source;
        internal long Revision;
        internal DataClusterIGC Cluster;
        internal DataWarpIGC[] Warps = [];
        internal DataAsteroidIGC[] Asteroids = [];
        internal readonly List<DataStationIGC> Stations = [];
        internal readonly List<StationsUpdateEntry> Health = [];
        internal readonly List<ExportProbe> Probes = [];
        internal readonly List<ExportMine> Mines = [];
        internal readonly List<(short Hull, short Side, ServerShipUpdate Pose)> Ships = [];

        internal bool CaptureTerrain(Sector sector)
        {
            if (Source == sector && Revision == sector.TerrainRevision) return false;
            Source = sector;
            Revision = sector.TerrainRevision;
            Cluster = sector.Cluster;
            Warps = sector.Warps.Select(w => w.Warp).ToArray();
            Asteroids = sector.Asteroids.ToArray();
            return true;
        }

        // The ordered live station records are their own version. Capture, retype,
        // rename and transient death can all change them between topology changes.
        internal bool CaptureStations(Sector sector)
        {
            bool changed = false;
            Health.Clear();
            int count = 0;
            foreach (var station in sector.StationRuntimes)
            {
                if (station.Dead) continue;
                var data = station.Data;
                if (count == Stations.Count)
                {
                    Stations.Add(data);
                    changed = true;
                }
                else if (Stations[count] != data)
                {
                    Stations[count] = data;
                    changed = true;
                }
                count++;
                Health.Add(StationsUpdateEntry.FromStation(station));
            }
            if (count == Stations.Count) return changed;
            Stations.RemoveRange(count, Stations.Count - count);
            return true;
        }
    }

    internal void Capture(Mission mission, short focus)
    {
        bool terrainChanged = _epoch != mission.ResearchEpoch;
        if (terrainChanged)
        {
            _sectors.Clear();
            _deaths.Clear();
            _kills.Clear();
            _shipNames.Clear();
            _epoch = mission.ResearchEpoch;
        }
        _stage = mission.Stage;
        _seconds = _stage is MissionStage.NotStarted or MissionStage.Starting
            ? 0 : Math.Max(0, mission.LastUpdate - mission.CountdownStart);
        _hulls.Clear();
        _projectiles.Clear();
        _missiles.Clear();
        int index = 0;
        uint tick = (uint)(mission.LastUpdate * 1000);
        foreach (var sector in mission.Sectors)
        {
            if (index == _sectors.Count) _sectors.Add(new());
            var sample = _sectors[index++];
            terrainChanged |= sample.CaptureTerrain(sector);
            terrainChanged |= sample.CaptureStations(sector);
            sample.Probes.Clear();
            sample.Mines.Clear();
            sample.Ships.Clear();
            foreach (var model in sector.Models)
            {
                switch (model)
                {
                    case Ship ship:
                        bool pod = ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
                        _hulls.Add((ship.Id, pod, ship.Sector?.Id ?? -1));
                        if (ship.Fraction <= 0 || ship.StationId is not null || ship.ParentShip is not null) break;
                        sample.Ships.Add((ship.HullType.HullId, ship.SideId ?? -1,
                            ServerShipUpdate.CaptureModel(ship.Id, ship, tick)));
                        break;
                    case Probe p:
                        sample.Probes.Add(ExportProbe.FromProbe(p, (uint)(p.DeployTime * 1000), p.Launcher?.Id ?? -1, p.SideId ?? -1));
                        break;
                    case Mine m:
                        sample.Mines.Add(ExportMine.FromMine(m, (uint)(m.DeployTime * 1000), m.Launcher?.Id ?? -1, m.SideId ?? -1));
                        break;
                    case Projectile p when sector.Id == focus:
                        _projectiles.Add(new() { ProjectileTypeId = p.ProjectileType.ProjectileTypeId,
                            LauncherId = p.Launcher?.Id ?? -1, Position = p.Position, Velocity = p.Velocity,
                            Forward = p.Forward, Lifespan = (float)Math.Max(0, p.TimeExpire - mission.LastUpdate),
                            ReceivedLocalMs = LocalMilliseconds() });
                        break;
                    case Missile m:
                        _missiles.Add(new() { MissileId = m.Id, TypeId = m.MissileType.ExpendableTypeId,
                            ClusterId = m.OwnerSector.Id, LauncherId = m.Launcher?.Id ?? -1,
                            Position = m.Position, Forward = m.Forward, LastUpdatedLocalMs = LocalMilliseconds() });
                        break;
                }
            }
        }
        if (index < _sectors.Count)
        {
            _sectors.RemoveRange(index, _sectors.Count - index);
            terrainChanged = true;
        }
        if (terrainChanged) _terrainVersion = new object();

        _roster.Clear();
        _sides.Clear();
        _research.Clear();
        foreach (var side in mission.Sides)
        {
            _roster.Add(new(side.Id, side.Civilization.CivilizationId, 0, side.Name ?? "",
                side.ColorR, side.ColorG, side.ColorB, side.ColorA));
            foreach (var bucket in side.Buckets)
                if (!bucket.Completed && bucket.InvestedMoney > 0)
                    _research.Add((bucket.Name, bucket.PercentBought, bucket.PercentComplete));
            _sides.Add((side.Name, side.Kills, side.Deaths, _research.Count));
        }
    }

    private void AddDynamic(IFedMessage message)
    {
        _objects.Add(message);
        _dynamicObjects.Add(message);
    }

    internal SkirmishObservation Build()
    {
        foreach (var hull in _hulls)
        {
            if (_deaths.TryGetValue(hull.Id, out var previous) && hull.Pod && !previous.Pod)
                _kills[previous.Sector] = _seconds;
            _deaths[hull.Id] = (hull.Pod, hull.Sector);
        }
        _objects.Clear();
        _dynamicObjects.Clear();
        AddDynamic(new SideRoster { Sides = _roster.ToArray(), LobbyPlayerCount = 0 });
        foreach (var sector in _sectors)
        {
            _objects.Add(new ExportCluster { Cluster = sector.Cluster });
            foreach (var warp in sector.Warps) _objects.Add(new ExportWarp { Warp = warp });
            foreach (var station in sector.Stations) _objects.Add(ExportStation.FromStation(station));
            AddDynamic(new StationsUpdate { Entries = sector.Health.ToArray() });
            foreach (var probe in sector.Probes) AddDynamic(probe);
            foreach (var mine in sector.Mines) AddDynamic(mine);
            foreach (var rock in sector.Asteroids) _objects.Add(ExportAsteroid.FromAsteroid(rock));
            foreach (var ship in sector.Ships)
            {
                // Reading the captured pose materializes its canonical bytes off the tick gate.
                short id = ship.Pose.ShipId;
                if (!_shipNames.TryGetValue(id, out var name)) _shipNames[id] = name = $"Ship {id}";
                AddDynamic(new ExportShip { ShipId = id, HullId = ship.Hull, SideId = ship.Side, CharacterName = name });
                AddDynamic(new ShipStatusMessage { ShipId = id, HullId = ship.Hull,
                    SectorId = sector.Cluster.ClusterId, StationId = -1, ParentId = -1 });
                AddDynamic(new SingleShipUpdate { OidTarget = id, Ship = ship.Pose });
            }
        }
        var sides = new SkirmishSideView[_sides.Count];
        int start = 0;
        for (int i = 0; i < sides.Length; i++)
        {
            var side = _sides[i];
            string[] research = side.End == start ? [] : new string[side.End - start];
            for (int j = 0; j < research.Length; j++)
            {
                var bucket = _research[start++];
                research[j] = $"{bucket.Name} {bucket.Bought}% funded / {bucket.Complete}% built";
            }
            short id = _roster[i].SideId;
            sides[i] = new(id, side.Name ?? $"Side {id}", side.Kills, side.Deaths, string.Join("; ", research));
        }
        return new(_epoch, _seconds, _stage, _objects.ToArray(), _projectiles.ToArray(), sides)
            {
                Missiles = _missiles.ToArray(), RecentKills = new Dictionary<short, double>(_kills),
                TerrainVersion = _terrainVersion, DynamicObjects = _dynamicObjects.ToArray()
            };
    }
}
