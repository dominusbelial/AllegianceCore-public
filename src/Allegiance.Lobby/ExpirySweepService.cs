namespace Allegiance.Lobby;

/// <summary>
/// Hosted background service that periodically calls <see cref="ServerRegistry.PruneExpired"/>
/// so stale listings (servers that stopped heartbeating) fall off the public list.
///
/// The cadence (how often we sweep) and the staleness window (how old an entry must be
/// before it is pruned) are both injected, along with the <see cref="TimeProvider"/>. The
/// <see cref="PeriodicTimer"/> is constructed with that same time provider (an in-box .NET 8
/// capability), so in tests a fake clock drives both "when is it now" and "when does the
/// sweep fire" together — no real wall-clock sleeps required.
/// </summary>
public sealed class ExpirySweepService : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _maxAge;

    public ExpirySweepService(ServerRegistry registry, TimeProvider time, TimeSpan interval, TimeSpan maxAge)
    {
        _registry = registry;
        _time = time;
        _interval = interval;
        _maxAge = maxAge;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _registry.PruneExpired(_maxAge);
        }
    }
}
