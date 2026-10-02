namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// Pure (MonoGame-free) logic for the warp-transit screen flash — the short,
/// full-screen flash-to-black-and-back that visually acknowledges a sector
/// transit (P245).
///
/// <b>This is original UX design, not a native port.</b> An exhaustive grep of
/// the native <c>WinTrek/</c> tree found no fade/flash/blackscreen transition
/// coded on warp transit — the only transit-adjacent client feedback there is
/// a <c>jumpSound</c> play (<c>trekigc.cpp:3998,4415</c>). Domingo's memory of
/// a "quick blackscreen and then you were thrown into the other system"
/// plausibly arises from the real velocity-fling mechanic (server
/// <c>ServerHost.HitWarpEvent</c>, ServerHost.cs:675-702, vs native
/// <c>fsship.cpp:126-127</c>) read together with the instantaneous scene swap
/// this port performs — there is no coded native screen effect to reproduce.
/// This stage therefore fills a real, confirmed usability gap (P244 fixed the
/// "aleph vanishes" bug but transit itself still feels instantaneous) with
/// our own design: a brief whole-frame black pulse at the moment the local
/// ship's sector changes. It is feedback, not a loading screen — short enough
/// that the player immediately sees and reacts to their new velocity /
/// orientation (the fling is never masked for long).
///
/// Detection is the same one-shot the transit itself produces on the wire:
/// the server sends exactly one <c>FM_S_SET_CLUSTER(7)</c> per transit to the
/// transiting peer (<c>Server.OnShipWarped</c>, Server.cs:11582-11611), and the
/// client reflects that as the local ship's <c>SectorId</c> changing in
/// <see cref="State.ClientWorld"/> (ClientWorld.cs:202-212). So the trigger here
/// is the local ship's <c>SectorId</c> differing from the previously observed
/// value — a strict edge, not a distance check — which structurally fires
/// <b>exactly once per transit</b> (subsequent frames see the same new sector,
/// so no re-arm; the only way to arm again is another real sector change).
/// The very first <c>SET_CLUSTER</c> at join (the "here is your ship"
/// assignment) establishes the baseline without flashing, so an initial spawn
/// never false-triggers.
/// </summary>
/// <remarks>
/// The caller feeds the local ship's sector once per frame (Game1 gates on
/// <c>world.MyShipId</c>). When it changes for the first time, the flash is
/// armed and <see cref="Opacity"/> returns a triangular pulse 0 → 1 (fully
/// black) → 0 over <see cref="FlashDurationSeconds"/>, peaking at the
/// midpoint. After the window the tracker is inert until the next genuine
/// sector change.
/// </remarks>
public sealed class WarpTransitFlash
{
    /// <summary>
    /// Total flash duration in seconds (a few hundred ms — a brief blink, not
    /// a loading screen). Deliberately kept short so the velocity-fling is
    /// visible again almost immediately (requirement 2 of P245).
    /// </summary>
    public const float FlashDurationSeconds = 0.36f;

    /// <summary>
    /// The <see cref="Opacity"/> pulse peaks at fully black (1.0) here, midway
    /// through the window, giving the symmetric "to black and back" shape.
    /// </summary>
    public const float MidpointSeconds = FlashDurationSeconds * 0.5f;

    private bool _havePrevious;      // a sector baseline exists
    private short _previous;         // the last sector fed in
    private bool _armed;             // the flash window is active
    private float _armedAt;          // absolute "now" when the flash armed
    private float _now;              // latest clock value supplied

    /// <summary>
    /// True on the single frame where a sector change is first detected — the
    /// arm instant. Useful for logging / diagnostics to prove once-per-transit.
    /// Cleared each <see cref="Update"/>.
    /// </summary>
    public bool JustTransited { get; private set; }

    /// <summary>
    /// Current flash opacity 0..1 at the last fed clock (<see cref="_now"/>):
    /// 0 = no flash; 1 = fully black. Triangular pulse peaked at
    /// <see cref="MidpointSeconds"/>. 0 outside the flash window.
    /// </summary>
    public float Opacity { get; private set; }

    /// <summary>Whether the flash window is currently active.</summary>
    public bool Active => _armed;

    /// <summary>
    /// Feed the local ship's current sector (<see langword="null"/> / unassigned
    /// = unknown) once per frame. Detects the first change from a known previous
    /// sector and arms the flash at <paramref name="now"/> (seconds, monotonic).
    /// Re-arms only on a genuine later change; the very first sector seen just
    /// becomes the baseline (no flash on initial spawn). Advances the pulse and
    /// returns the current <see cref="Opacity"/> (0 outside the window) so a
    /// caller can read it directly, mirroring <see cref="DamageFlashTracker.Update"/>.
    /// </summary>
    public float Update(float now, short? sector)
    {
        _now = now;
        JustTransited = false;

        if (_armed)
        {
            Opacity = ComputeOpacity(now - _armedAt);
            if (now - _armedAt >= FlashDurationSeconds)
            {
                _armed = false;
                Opacity = 0f;
            }
            return Opacity;
        }

        if (!sector.HasValue)
        {
            _havePrevious = false;
            _previous = 0;
            Opacity = 0f;
            return Opacity;
        }

        var cur = sector.Value;
        if (_havePrevious && cur != _previous)
        {
            // A real transit: a known previous sector changed. Arm once.
            _armed = true;
            _armedAt = now;
            JustTransited = true;
        }

        _previous = cur;
        _havePrevious = true;
        Opacity = _armed ? ComputeOpacity(0f) : 0f;
        return Opacity;
    }

    /// <summary>
    /// Reset all state (used by tests to start clean).
    /// </summary>
    public void Reset()
    {
        _havePrevious = false;
        _previous = 0;
        _armed = false;
        _armedAt = 0f;
        _now = 0f;
        JustTransited = false;
        Opacity = 0f;
    }

    /// <summary>
    /// Triangular pulse: 0 at t=0, rising linearly to 1 at
    /// <see cref="MidpointSeconds"/>, falling back to 0 at
    /// <see cref="FlashDurationSeconds"/>. 0 outside [0, duration].
    /// </summary>
    private static float ComputeOpacity(float t)
    {
        if (t < 0f || t >= FlashDurationSeconds) return 0f;
        if (t <= MidpointSeconds) return t / MidpointSeconds;                 // rise to black
        return 1f - (t - MidpointSeconds) / (FlashDurationSeconds - MidpointSeconds); // back to clear
    }
}
