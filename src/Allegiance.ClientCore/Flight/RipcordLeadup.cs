using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P781 — the pure ripcord/teleport lead-up timeline: everything Game1 needs to
/// answer "is this ship about to teleport, and how visible should the tell be",
/// kept MonoGame-free and unit-testable. It backs the own-ship HUD expiry
/// (<c>Game1.RipcordStatusLabel</c>), the frozen-flight decision for remote
/// ships (<c>ShipDeadReckoner.Ripcording</c> via
/// <c>ClientWorld.TryGetRenderPose(..., ripcording:)</c>) and the lead-up spark
/// emission (native <c>ThingGeoImpl::AddTeleportParticles</c>,
/// <c>effect/thinggeo.cpp:619-696</c>).
/// </summary>
/// <remarks>
/// <para><b>The TiDi countdown rule is shared, not forked.</b>
/// <see cref="TimeLeft"/> is the same "wall-clock elapsed × server time scale"
/// integral the HUD/voice countdown uses
/// (<see cref="Allegiance.ClientCore.MonoGame.Audio.RipcordCountdownSound.TimeLeft"/>:
/// native measures <c>GetRipcordTimeLeft</c> on the sim timer,
/// <c>shipIGC.cpp:283-289</c>). The audio cue integrates per call because it
/// must also voice second boundaries across sparse frames; the pure form here
/// evaluates the identical expression closed-form, which agrees with the
/// integral whenever the time scale is constant between two reads.</para>
/// <para><b>The 2 s safety grace is port-only.</b> Native pins the countdown at
/// 0 and keeps the ripcord model (frozen flight + full spark spray) until the
/// server actually moves the ship (<c>SetCluster</c> clears it,
/// <c>shipIGC.cpp:286-289</c> + <c>shipIGC.h:200-210</c>); the connected
/// client's <c>UseRipcord</c> returns false so it never teleports locally
/// (<c>trekigc.cpp:3961-4019</c>). The port's server teleport sends the owner
/// no SET_CLUSTER (verified P781: <c>PerformRipcordTeleport</c> only moves the
/// ship; the client learns the new sector from SHIP_STATUS, and Game1 clears
/// the state on that own sector-change edge — native <c>SetCluster</c> →
/// <c>SetRipcordModel(NULL)</c>, shipIGC.h:200-210). The hard expiry
/// <c>duration + 2.0 s</c> is only the backstop for a teleport that never
/// lands (a lost anchor race): the frozen state cannot outlive it by more than
/// the grace window. Native has no such expiry; 2 s is comfortably longer than
/// the 20 Hz status stream needs to deliver the post-teleport sector change.</para>
/// </remarks>
public static class RipcordLeadup
{
    /// <summary>Port-only safety grace, seconds: a ripcord state is expired this
    /// long after its countdown should have landed (see class remarks). Native
    /// pins the countdown at 0 and waits for the server's move instead.</summary>
    public const float SafetyGraceSeconds = 2.0f;

    /// <summary>The native lead-up ramp span — <c>fTimeToShowSparks</c> ramps
    /// 1 → 0 over the final 3 s before a teleport (thinggeo.cpp:650-652).</summary>
    public const float LeadTimeSeconds = 3.0f;

    /// <summary>
    /// The ripcord countdown time left, seconds — the HUD's TiDi rule
    /// (<c>wall elapsed × timeScale</c>, see class remarks), clamped at 0 exactly
    /// like <see cref="Allegiance.ClientCore.MonoGame.Audio.RipcordCountdownSound.TimeLeft"/>.
    /// An unknown duration (≤ 0, core not loaded yet) reads as "not expired":
    /// the HUD's existing convention keeps the tag until a real clear message.
    /// </summary>
    public static float TimeLeft(long startMs, long nowMs, float durationSec, double timeScale)
        => MathF.Max(0f, durationSec - ElapsedSeconds(startMs, nowMs, timeScale));

    /// <summary>
    /// The SIGNED countdown: positive while time remains, negative for this
    /// long after the countdown should have reached 0. This is what the port's
    /// safety expiry measures (native instead pins at 0 and waits for the
    /// server's move — shipIGC.cpp:286-294).
    /// </summary>
    public static float OverrunSeconds(long startMs, long nowMs, float durationSec, double timeScale)
        => durationSec - ElapsedSeconds(startMs, nowMs, timeScale);

    /// <summary>
    /// The lead-up spark timer for a ripcord (native <c>m_fTimeUntilRipcord</c>):
    /// <c>SetTimeUntilRipcord(GetRipcordTimeLeft())</c> on ACTIVATE
    /// (appmsg.cpp:947,961) and then <c>-= fDeltaTime</c> every rendered frame
    /// (thinggeo.cpp:694) with NO clamp, so it goes negative right after the
    /// countdown's zero and the spray stops (thinggeo.cpp:625) even though the
    /// frozen flight model is still set. This is the SIGNED
    /// <see cref="OverrunSeconds"/>; −1 when there is no start or the duration
    /// is unknown (no core yet — no sparks rather than a wrong ramp).
    /// </summary>
    public static float TimeUntilTeleport(long? startMs, long nowMs, float durationSec, double timeScale)
    {
        if (startMs is not { } start || durationSec <= 0f) return -1f;
        return OverrunSeconds(start, nowMs, durationSec, timeScale);
    }

    /// <summary>
    /// Native's "ripcord model set" predicate for the port: a start time exists
    /// (a RIPCORD_ACTIVATE was applied) and the state has not outlived the
    /// countdown by <see cref="SafetyGraceSeconds"/>. True straight through
    /// <c>left == 0</c> — native keeps the model at 0
    /// (<c>m_dtRipcordCountdown = 0.0f</c>, shipIGC.cpp:289) — and false once the
    /// port-only grace is spent. An unknown duration (≤ 0) stays "ripcording"
    /// until a real clear (ABORTED / SET_CLUSTER / SHIP_RESET / reset), matching
    /// the HUD's fail-open convention.
    /// </summary>
    public static bool IsRipcording(long? startMs, long nowMs, float durationSec, double timeScale)
    {
        if (startMs is not { } start) return false;
        if (durationSec <= 0f) return true;
        return OverrunSeconds(start, nowMs, durationSec, timeScale) > -SafetyGraceSeconds;
    }

    /// <summary>
    /// Native's aleph lead-up timer (WinTrek.cpp:7783-7802): the ETA at which a
    /// ship crosses the nearest warp's centre — <c>range / closing</c> where
    /// <c>closing = velocity · unit(warp − ship)</c> — or −1 when the ship is
    /// receding or stationary. Native applies it to every visible ship, own
    /// included, once per frame.
    /// </summary>
    public static float AlephEta(Vector3 shipPos, Vector3 shipVel, Vector3 warpPos)
    {
        Vector3 delta = warpPos - shipPos;
        float range = delta.Length();
        if (range <= 1e-6f) return -1f;
        float closing = shipVel * (delta / range);   // Vector3's dot operator (Allegiance.Core.Collision)
        return closing > 0f ? range / closing : -1f;
    }

    /// <summary>
    /// Native's teleport-timer selection (thinggeo.cpp:620-623): start from the
    /// ripcord timer and prefer the aleph timer when the ripcord one is idle
    /// (<c>&lt; 0</c>) or the aleph crossing comes first. The caller emits
    /// nothing when the result is negative (native: "run the effect until it
    /// ends", thinggeo.cpp:625).
    /// </summary>
    public static float SelectTeleportTime(float ripcordTimeLeft, float alephEta)
    {
        float t = ripcordTimeLeft;
        if (t < 0f || (alephEta >= 0f && alephEta < t))
            t = alephEta;
        return t;
    }

    /// <summary>
    /// Native's per-emission spark count (thinggeo.cpp:650-659):
    /// <c>(int)(floor(TELEPORT_SPARK_COUNT · s_iShowSmoke · (1 − clamp(t/3, 0, 1))) + 0.5f)</c>
    /// — i.e. <c>floor</c> of the ramped count, 0 for <c>t ≥ 3</c> and the full
    /// <c>3 · showSmoke</c> burst at <c>t = 0</c>. With the shipped default
    /// smoke quality 3 (WinTrek.cpp:3293) that is 0/0/4/9 at t = 5/3/1.5/0.
    /// </summary>
    public static int SparkCount(float timeUntilTeleport, float showSmoke)
    {
        if (timeUntilTeleport < 0f) return 0;
        float ramp = Math.Clamp(timeUntilTeleport / LeadTimeSeconds, 0f, 1f);
        float up = 1f - ramp;
        return (int)(MathF.Floor(3f * showSmoke * up) + 0.5f);
    }

    private static float ElapsedSeconds(long startMs, long nowMs, double timeScale)
        => MathF.Max(0f, (nowMs - startMs) / 1000f) * (float)timeScale;
}
