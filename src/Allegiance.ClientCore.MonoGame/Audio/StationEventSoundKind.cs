namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P267 — which station event one-shot to play, resolved by the client from the
/// real wire event and the local viewer's relation to the station (same
/// semantics as native <c>clintlib/appmsg.cpp</c>: the friendly/own case plays
/// the "our" variant, the enemy case the "enemy" variant). The "our"-vs-"enemy"
/// discrimination is collapsed to same-side here because the port client has no
/// alliance graph — only <c>MySideId</c> (see ClientWorld remarks).
/// </summary>
public enum StationEventSoundKind
{
    /// <summary>An own/friendly station was destroyed → <c>DestroyedSound</c>
    /// (native <c>appmsg.cpp:548-551</c>, <c>GetDestroyedSound()</c>).</summary>
    Destroyed,

    /// <summary>An enemy station was destroyed → <c>EnemyDestroyedSound</c>
    /// (native <c>appmsg.cpp:551</c>, <c>GetEnemyDestroyedSound()</c>).</summary>
    EnemyDestroyed,

    /// <summary>An own/friendly station was captured by the enemy →
    /// <c>CapturedSound</c> (native <c>appmsg.cpp:674-675</c>,
    /// <c>GetCapturedSound()</c>).</summary>
    Captured,

    /// <summary>An enemy station was captured by our side →
    /// <c>EnemyCapturedSound</c> (native <c>appmsg.cpp:676-677</c>,
    /// <c>GetEnemyCapturedSound()</c>).</summary>
    EnemyCaptured,
}
