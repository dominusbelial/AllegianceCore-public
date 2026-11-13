namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1005 — the clock the per-rock spin is integrated on: the client's <b>game</b>
/// clock (simulation seconds, TiDi-scaled — <c>Game1._gameSeconds</c>), read as a
/// delta so a rock advances <em>exactly once</em> per elapsed simulation second
/// no matter how many render passes or draws a frame runs.
/// </summary>
/// <remarks>
/// <para><b>Why the game clock and not the wall clock.</b> Native advances a
/// model's rotation inside its <em>simulation</em> update: <c>CmodelIGC::Update</c>
/// takes <c>Time now</c> and uses <c>dt = now - m_lastUpdate</c>
/// (<c>Igc/modelIGC.cpp:76-99</c>), where <c>Time</c> is the mission clock and
/// <c>Time::operator-</c> divides by <c>fResolution() = 1000</c> to yield
/// <b>seconds</b> (<c>zlib/ztime.h:172</c>). Rotating artwork is <em>not</em>
/// window-time like native's aleph/build-effect geometry
/// (<c>effect/efgeo.cpp:209-222</c>, <c>GetTime()->GetValue()</c>); it is world
/// state and therefore follows the mission clock. The port's equivalent of that
/// clock is <c>_gameSeconds</c> (wall time × the server's live sim scale, P618) —
/// the clock every other world animation uses. P252 advanced the rocks from
/// <c>Draw</c>'s <c>ElapsedGameTime</c> instead, so under time dilation (the port
/// only — native has no TiDi; scale range [0.1, 1.0],
/// <c>ServerConfig.MinTimeScale</c>) the world slowed while the rocks kept their
/// full real-time rate, i.e. up to 10× everything else: "asteroids spinning super
/// fast" (v0.1.6 tester report).</para>
/// <para><b>Why a delta and not a per-draw step.</b> The rock's world matrix is
/// composed in three places (linear-depth gather, colour pass, HUD target
/// preview). Advancing on a <em>render</em> delta lets a frame that draws more
/// often than it updates (or a second pass) advance the spin again with no
/// elapsed world time; taking the difference of the game clock makes every extra
/// pass yield exactly <c>0</c>, which is native's "one model update per tick"
/// cadence (<c>CclusterIGC::Update</c> calls <c>l->data()->Update(now)</c> once per
/// model per cluster update, <c>Igc/clusterIGC.cpp:243-251</c>).</para>
/// <para><b>Step clamp.</b> A single step is clamped to native's own per-step
/// ceiling <c>dwMaxDeltaTime = fResolution() * 0.25</c> (4 FPS,
/// <c>zlib/ztime.h:57</c>, applied by the <c>Time</c> arithmetic at
/// <c>:146-166</c>), so a stall, a menu round-trip or a paused host cannot jump a
/// rock by seconds of rotation in one frame.</para>
/// <para><b>First step.</b> The first observation only seeds the stamp (returns
/// 0), so a rock enters at the wire base orientation instead of inheriting the
/// clock's absolute value (native seeds <c>m_lastUpdate = now</c> at creation,
/// <c>Igc/modelIGC.h:86-89</c>).</para>
/// </remarks>
public struct AsteroidSpinClock
{
    /// <summary>Largest rotation step a single frame may apply, in sim seconds —
    /// native <c>Time::dwMaxDeltaTime = fResolution() * 0.25f</c> (4 FPS cap,
    /// <c>zlib/ztime.h:57</c>).</summary>
    public const float MaxStepSeconds = 0.25f;

    private double _lastGameSeconds;
    private bool _started;

    /// <summary>True once <see cref="Advance"/> has seen a game-clock value.</summary>
    public readonly bool Started => _started;

    /// <summary>The game-clock stamp the last step was measured from.</summary>
    public readonly double LastGameSeconds => _lastGameSeconds;

    /// <summary>
    /// Advance to <paramref name="gameSeconds"/> and return the sim-second step
    /// since the previous call, clamped to [0, <see cref="MaxStepSeconds"/>].
    /// A repeated (or backwards, or non-finite) value yields 0 — the extra render
    /// pass / stalled clock case.
    /// </summary>
    public float Advance(double gameSeconds)
    {
        if (!double.IsFinite(gameSeconds)) return 0f;
        if (!_started)
        {
            _started = true;
            _lastGameSeconds = gameSeconds;
            return 0f;
        }

        double delta = gameSeconds - _lastGameSeconds;
        _lastGameSeconds = gameSeconds;
        if (delta <= 0.0) return 0f;
        return delta < MaxStepSeconds ? (float)delta : MaxStepSeconds;
    }

    /// <summary>Forget the stamp (new world / new session): the next
    /// <see cref="Advance"/> seeds it and returns 0.</summary>
    public void Reset()
    {
        _lastGameSeconds = 0.0;
        _started = false;
    }
}
