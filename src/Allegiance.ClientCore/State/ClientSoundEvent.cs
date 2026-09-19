namespace Allegiance.ClientCore.State;

/// <summary>
/// P267 — a one-shot sound event the client world has observed on the wire,
/// recorded for <c>Game1</c> to drain into the audio manager. Currently the
/// station destroyed/captured notices and the development-completion chime —
/// the real triggers native wires to <c>DestroyedSound</c>/<c>CapturedSound</c>
/// (<c>clintlib/appmsg.cpp:548-551, 674-677</c>) and a development's
/// <c>CompletionSound</c> (<c>appmsg.cpp:3326</c>).
/// </summary>
public enum ClientSoundEventKind
{
    /// <summary>An own/friendly station was destroyed (STATION_DESTROYED, 59).</summary>
    StationDestroyed,

    /// <summary>An enemy station was destroyed (STATION_DESTROYED, 59).</summary>
    StationEnemyDestroyed,

    /// <summary>An own/friendly station was captured (STATION_CAPTURE, 58).</summary>
    StationCaptured,

    /// <summary>An enemy station was captured (STATION_CAPTURE, 58).</summary>
    StationEnemyCaptured,

    /// <summary>A development completed (DEV_COMPLETED, 8).</summary>
    DevCompleted,
}

/// <summary>A single observed sound event.</summary>
/// <param name="Kind"><see cref="ClientSoundEventKind"/>.</param>
/// <param name="Id">The subject's id — the station id for station events, the
/// development id for a dev completion.</param>
public readonly record struct ClientSoundEvent(ClientSoundEventKind Kind, short Id);
