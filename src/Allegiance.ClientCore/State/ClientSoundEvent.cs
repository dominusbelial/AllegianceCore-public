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

    /// <summary>P414 — this ship's ripcord countdown was aborted
    /// (RIPCORD_ABORTED, 187 — our cancel request or a mid-countdown legality
    /// failure). Queued only when the aborted ship is the local player's own,
    /// so Game1 plays the single "ripcord aborted" VO cue exactly once.</summary>
    RipcordAborted,

    /// <summary>P638 — this ship's teleport dock request was accepted
    /// (TELEPORT_ACK, 102, with a real station id): the player relocated to a
    /// station and native plays the <c>personalJumpSound</c> (appmsg.cpp:1901).
    /// Mapped to the <c>jumpin</c> one-shot, the same wave native's jumpSound
    /// plays.</summary>
    TeleportArrived,

    /// <summary>Native newCommandMsgSound, appmsg.cpp:1757.</summary>
    SectorHighlighted,

    /// <summary>POSITIONREQ: salPlayerWaitingSound (appmsg.cpp:2481).</summary>
    PlayerWaiting,
}

/// <summary>A single observed sound event.</summary>
/// <param name="Kind"><see cref="ClientSoundEventKind"/>.</param>
/// <param name="Id">The subject's id — the station id for station events, the
/// development id for a dev completion.</param>
/// <param name="StationTypeId">Station type at receipt time, before destruction
/// removes the station or subsequent exports replace it. Other events use NA.</param>
public readonly record struct ClientSoundEvent(ClientSoundEventKind Kind, short Id, short StationTypeId = -1);
