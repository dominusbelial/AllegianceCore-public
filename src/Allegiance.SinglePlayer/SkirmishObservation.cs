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
    internal object? TerrainVersion { get; init; }
    internal IFedMessage[] DynamicObjects { get; init; } = [];

    public void ApplyTo(ClientWorld world) =>
        world.ApplySkirmishObservation(Objects, DynamicObjects, TerrainVersion, Projectiles, Missiles);

    public MissileEntity[] Missiles { get; init; } = [];
    public IReadOnlyDictionary<short, double> RecentKills { get; init; } = new Dictionary<short, double>();
}
public sealed record SkirmishSideView(short Id, string Name, int Kills, int Deaths, string Research);

public sealed partial class SkirmishHost
{
    private readonly SkirmishObservationSampler _observer = new();
    public short? PlayerSide { get; private set; }
    private readonly SemaphoreSlim _seatChanges = new(1);

    /// <summary>Exchange exactly one pilot slot through the owned bot driver's private protocol.
    /// The caller disconnects its old pilot before changing seats, then uses normal POSITION_REQ.</summary>
    public async Task SetPlayerSideAsync(short? side, CancellationToken ct = default)
    {
        if (Options.HostLan && side != PlayerSide) throw new InvalidOperationException("LAN hosts change teams through the ordinary multiplayer flow.");
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
        lock (_observer)
        {
            lock (_simGate) _observer.Capture(_server.Missions[0], sectorId);
            return _observer.Build();
        }
    }
}

internal sealed record SkirmishSeatCommand(long Id, string Command, short? Side);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SkirmishSeatCommand))]
[JsonSerializable(typeof(SkirmishFocusCommand))]
internal sealed partial class SkirmishProtocolContext : JsonSerializerContext;

internal sealed record SkirmishFocusCommand(long Id, string Command, short? Sector);
