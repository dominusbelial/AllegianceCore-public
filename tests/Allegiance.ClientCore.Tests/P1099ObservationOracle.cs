using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.Tests;

// Observation body from master 28debb3c. Both readers receive the same mission under the
// gate. The local arrival clock is injectable for an exact deterministic comparison.
internal sealed class P1099ObservationOracle
{
    private string _observerEpoch = "";
    private readonly Dictionary<short, (bool Pod, short Sector)> _observerDeaths = new();
    private readonly Dictionary<short, double> _observerKills = new();

    private static byte Percent(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 240.0 + .5);

    internal SkirmishObservation Read(Mission mission, short sectorId, Func<long> clock)
    {
        double seconds = mission.Stage is MissionStage.NotStarted or MissionStage.Starting
            ? 0 : Math.Max(0, mission.LastUpdate - mission.CountdownStart);
        if (_observerEpoch != mission.ResearchEpoch)
        {
            _observerEpoch = mission.ResearchEpoch;
            _observerDeaths.Clear(); _observerKills.Clear();
        }
        foreach (var ship in mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()))
        {
            bool pod = ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
            if (_observerDeaths.TryGetValue(ship.Id, out var previous) && pod && !previous.Pod)
                _observerKills[previous.Sector] = seconds;
            _observerDeaths[ship.Id] = (pod, ship.Sector?.Id ?? -1);
        }
        var objects = new List<IFedMessage>
        {
            new SideRoster { Sides = mission.Sides.Select(s => new SideRoster.SideRecord(s.Id,
                s.Civilization.CivilizationId, 0, s.Name ?? "", s.ColorR, s.ColorG, s.ColorB, s.ColorA)).ToArray(),
                LobbyPlayerCount = 0 }
        };
        foreach (var sector in mission.Sectors)
        {
            objects.Add(ExportCluster.FromSector(sector));
            objects.AddRange(sector.Warps.Select(ExportWarp.FromWarp));
            objects.AddRange(sector.StationRuntimes.Where(st => !st.Dead).Select(st => ExportStation.FromStation(st.Data)));
            // Freeze the base FromStations conversion as well as the caller.
            objects.Add(new StationsUpdate
            {
                Entries = sector.StationRuntimes.Where(st => !st.Dead).Select(st => new StationsUpdateEntry(
                    st.StationId, Percent(st.ArmorFraction),
                    Percent(st.ShieldFraction))).ToArray()
            });
            foreach (var probe in sector.Models.OfType<Probe>())
                objects.Add(ExportProbe.FromProbe(probe, (uint)(probe.DeployTime * 1000), probe.Launcher?.Id ?? -1, probe.SideId ?? -1));
            foreach (var mine in sector.Models.OfType<Mine>())
                objects.Add(ExportMine.FromMine(mine, (uint)(mine.DeployTime * 1000), mine.Launcher?.Id ?? -1, mine.SideId ?? -1));
            objects.AddRange(sector.Asteroids.Select(ExportAsteroid.FromAsteroid));
            foreach (var ship in sector.Models.OfType<Ship>())
            {
                if (ship.Fraction <= 0 || ship.StationId is not null || ship.ParentShip is not null) continue;
                objects.Add(new ExportShip { ShipId = ship.Id, HullId = ship.HullType.HullId,
                    SideId = ship.SideId ?? -1, CharacterName = $"Ship {ship.Id}" });
                objects.Add(new ShipStatusMessage { ShipId = ship.Id, HullId = ship.HullType.HullId,
                    SectorId = sector.Id, StationId = -1, ParentId = -1 });
                objects.Add(new SingleShipUpdate { OidTarget = ship.Id,
                    Ship = ServerShipUpdate.FromModel(ship.Id, ship, (uint)(mission.LastUpdate * 1000)) });
            }
        }
        var projectiles = mission.Sectors.Where(s => s.Id == sectorId).SelectMany(s => s.Models.OfType<Projectile>())
            .Select(p => new ProjectileEntity { ProjectileTypeId = p.ProjectileType.ProjectileTypeId,
                LauncherId = p.Launcher?.Id ?? -1, Position = p.Position, Velocity = p.Velocity,
                Forward = p.Forward, Lifespan = (float)Math.Max(0, p.TimeExpire - mission.LastUpdate),
                ReceivedLocalMs = clock() }).ToArray();
        var sides = mission.Sides.Select(s => new SkirmishSideView(s.Id, s.Name ?? $"Side {s.Id}", s.Kills, s.Deaths,
            string.Join("; ", s.Buckets.Where(b => !b.Completed && b.InvestedMoney > 0)
                .Select(b => $"{b.Name} {b.PercentBought}% funded / {b.PercentComplete}% built")))).ToArray();
        var missiles = mission.Sectors.SelectMany(s => s.Models.OfType<Missile>()).Select(m => new MissileEntity
        {
            MissileId = m.Id, TypeId = m.MissileType.ExpendableTypeId, ClusterId = m.OwnerSector.Id,
            LauncherId = m.Launcher?.Id ?? -1, Position = m.Position, Forward = m.Forward, LastUpdatedLocalMs = clock()
        }).ToArray();
        return new(mission.ResearchEpoch, seconds, mission.Stage, objects.ToArray(), projectiles, sides)
            { Missiles = missiles, RecentKills = new Dictionary<short, double>(_observerKills) };
    }
}
