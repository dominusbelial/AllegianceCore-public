namespace Allegiance.Lobby;

/// <summary>
/// Configuration knobs for the lobby, bound from the <c>Lobby</c> config section with
/// sane defaults baked in. Tests override the sweep cadence/staleness via
/// <c>WebApplicationFactory</c> configuration so expiry can be observed quickly.
/// </summary>
public sealed class LobbySettings
{
    /// <summary>Hard cap on distinct listings ("a few hundred"). Heartbeats to existing ids are never evicted by it.</summary>
    public int MaxEntries { get; set; } = 384;

    /// <summary>How often the background sweep prunes stale listings.</summary>
    public double SweepIntervalSeconds { get; set; } = 30;

    /// <summary>How old an entry must be (since its last heartbeat) before the sweep prunes it.</summary>
    public double MaxAgeSeconds { get; set; } = 90;
}
