using System.Text.Json.Serialization;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

/// <summary>A detached local snapshot. No model references escape the simulation lock.</summary>
public sealed record SkirmishObservation(string Epoch, double Seconds, MissionStage Stage,
    IFedMessage[] Objects, ProjectileEntity[] Projectiles, SkirmishSideView[] Sides)
{
    public MissileEntity[] Missiles { get; init; } = [];
    public IReadOnlyDictionary<short, double> RecentKills { get; init; } = new Dictionary<short, double>();
}
public sealed record SkirmishSideView(short Id, string Name, int Kills, int Deaths, string Research);

public sealed partial class SkirmishHost
{
    private string _observerEpoch = "";
    private readonly Dictionary<short, (bool Pod, short Sector)> _observerDeaths = new();
    private readonly Dictionary<short, double> _observerKills = new();
    public short? PlayerSide { get; private set; }
    private readonly SemaphoreSlim _seatChanges = new(1);

    /// <summary>Exchange exactly one pilot slot through the owned bot driver's private protocol.
    /// The caller disconnects its old pilot before changing seats, then uses normal POSITION_REQ.</summary>
    public async Task SetPlayerSideAsync(short? side, CancellationToken ct = default)
    {
        if (side is not (null or 0 or 1)) throw new ArgumentOutOfRangeException(nameof(side));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = lifetime.Token;
        await _seatChanges.WaitAsync(ct);
        try
        {
            if (side == PlayerSide) return;
            // A slot exchange needs normal simulation dispatch. Resume a paused game;
            // choices made during intermission wait for the next playable mission.
            if (Paused) await SetPausedAsync(false, ct);
            while (OnSimulation(s => s.Missions[0].Stage) is MissionStage.Over or MissionStage.NotStarted or MissionStage.Starting)
            {
                if (Failure is { } failure) throw new IOException(failure);
                await Task.Delay(100, ct);
            }
            long id = Interlocked.Increment(ref _request);
            var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            try
            {
                await _writes.WaitAsync(ct);
                try
                {
                    await _driver!.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new SkirmishSeatCommand(id, "seat", side), SkirmishProtocolContext.Default.SkirmishSeatCommand).AsMemory(), ct);
                    await _driver.StandardInput.FlushAsync(ct);
                }
                finally { _writes.Release(); }
                var error = await reply.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
                if (error is not null) throw new IOException(error);
                PlayerSide = side;
            }
            finally { _pending.TryRemove(id, out _); }
        }
        finally { _seatChanges.Release(); }
    }

    // In-process only: dedicated servers gain no spectator privilege or remote endpoint.
    public SkirmishObservation Observe(short sectorId)
    {
        SetFocusSector(sectorId);
        lock (_simGate)
        {
            var mission = _server.Missions[0];
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
                objects.Add(StationsUpdate.FromStations(sector.StationRuntimes.Where(st => !st.Dead)));
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
                    ReceivedLocalMs = Environment.TickCount64 }).ToArray();
            var sides = mission.Sides.Select(s => new SkirmishSideView(s.Id, s.Name ?? $"Side {s.Id}", s.Kills, s.Deaths,
                string.Join("; ", s.Buckets.Where(b => !b.Completed && b.InvestedMoney > 0)
                    .Select(b => $"{b.Name} {b.PercentBought}% funded / {b.PercentComplete}% built")))).ToArray();
            var missiles = mission.Sectors.SelectMany(s => s.Models.OfType<Missile>()).Select(m => new MissileEntity
            {
                MissileId = m.Id, TypeId = m.MissileType.ExpendableTypeId, ClusterId = m.OwnerSector.Id,
                LauncherId = m.Launcher?.Id ?? -1, Position = m.Position, Forward = m.Forward, LastUpdatedLocalMs = Environment.TickCount64
            }).ToArray();
            return new(mission.ResearchEpoch, seconds, mission.Stage, objects.ToArray(), projectiles, sides)
                { Missiles = missiles, RecentKills = new Dictionary<short, double>(_observerKills) };
        }
    }
}

internal sealed record SkirmishSeatCommand(long Id, string Command, short? Side);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SkirmishSeatCommand))]
[JsonSerializable(typeof(SkirmishFocusCommand))]
internal sealed partial class SkirmishProtocolContext : JsonSerializerContext;

internal sealed record SkirmishFocusCommand(long Id, string Command, short? Sector);
