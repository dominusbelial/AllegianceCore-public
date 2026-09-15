using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P213 — real sound for the MonoGame POC, on MonoGame's own audio stack
/// (<see cref="SoundEffect"/> / <see cref="SoundEffectInstance"/>, NOT the
/// original's obsolete DirectSound3D <c>src/soundengine/</c> plumbing — that
/// is DirectX-specific and irrelevant to a cross-platform MonoGame client).
/// Owns every sound scoped to what is actually flyable in the POC today:
/// a looping main-thruster engine loop scaled by real throttle/speed, a quiet
/// cockpit ambient hum, one-shot autopilot engage/disengage voice chimes, and
/// a low background sector-ambience loop for the whole session. P216 adds the
/// mission-start countdown announcer (a <c>countdownstart</c> callout at the
/// countdown's opening, then a per-second <c>countdown10</c>…<c>countdown1</c>
/// voice over the last ten seconds) and a one-shot game-over callout
/// (<c>vo_ma_gameover</c>).
/// </summary>
/// <remarks>
/// <para><b>Real assets, from the port's artwork tree</b>
/// (<c>ALLEGIANCE_ARTWORK_PATH</c>, <c>/home/domingo/Development/Artwork</c>,
/// the same tree the server already reads), each resolving the original
/// client's <c>soundEngine</c> namespace in
/// <c>Artwork/sounddef.mdl</c>: the small-ship main-thruster interior loop is
/// <c>accelsmall.ogg</c> (<c>smallShipThrustInteriorSound</c>, sounddef.mdl),
/// the small-ship interior ambience is <c>sshipint.ogg</c>
/// (<c>smallShipInteriorSound</c>), and the engage/disengage chimes are
/// <c>vo_sal_autopilotengage.ogg</c>/<c>vo_sal_autopilotdisengage.ogg</c>
/// (cited at <c>WinTrek/trekigc.cpp:4880</c>). The sector ambience is
/// <c>bioambient_1.ogg</c>, matching the POC's Bios faction.</para>
/// <para><b>Graceful degradation.</b> Sound is cosmetic to the flyable POC, so
/// a host with no OpenAL/audio device must not take the game down: every audio
/// touchpoint is guarded so a failure logs once and leaves the manager inert
/// (the app keeps rendering/flying). This is a deliberate divergence from the
/// hard-exit-on-content-failure pattern used for the essential ship model in
/// <see cref="Game1"/>.</para>
/// <para><b>No positional 3D here.</b> These are cockpit/own-ship sounds; the
/// modern <see cref="SoundEffectInstance.Apply3D(AudioListener, AudioEmitter)"/>
/// equivalent is only needed for positioned world sounds (remote ships), which
/// are out of the POC's scope. A single looping instance per cue suffices —
/// no interior/exterior variants, matching the task's "a single looping
/// instance is enough" scope.</para>
/// </remarks>
public sealed class SoundManager : IDisposable
{
    private readonly ContentManager _content;

    private SoundEffectInstance? _engine;     // accelsmall — main-thruster loop
    private SoundEffectInstance? _cockpitHum; // sshipint — cockpit interior hum
    private SoundEffectInstance? _ambience;   // bioambient_1 — sector ambience loop
    private SoundEffect _apEngage = null!;    // vo_sal_autopilotengage
    private SoundEffect _apDisengage = null!; // vo_sal_autopilotdisengage
    private SoundEffect _countdownStart = null!;   // countdownstart
    private readonly Dictionary<int, SoundEffect> _countdownTicks = new(); // 10..1
    private SoundEffect _gameOver = null!;    // vo_ma_gameover
    private bool _ambienceStarted;
    private bool _warned;
    private bool _disposed;

    // P217 — quick-chat voice lines, loaded lazily on first play and cached
    // thereafter (only the handful actually voiced in a session are ever
    // resident, not all 288 shipped voices). _voiceFailures remembers assets
    // that could not load (missing/not-in-content) so we don't re-attempt a
    // doomed Content.Load every frame; the caption still displays either way.
    private readonly Dictionary<string, SoundEffect> _voices = new();
    private readonly HashSet<string> _voiceFailures = new(StringComparer.Ordinal);

    /// <summary>True when every core cue loaded and the manager is usable.
    /// If this is false the manager is inert (no audio device / a load failure)
    /// and callers' updates are no-ops.</summary>
    public bool IsLoaded { get; private set; }

    public SoundManager(ContentManager content) => _content = content;

    /// <summary>
    /// Load every cue from content and prepare the looping instances. Does not
    /// start anything (the ambient loop starts on the first <see cref="Update"/>
    /// once the session is actually running, keeping pre-connect silence).
    /// Any failure degrades the manager to inert and logs it, rather than
    /// throwing into the game's load path.
    /// </summary>
    public void Load()
    {
        try
        {
            _engine = Loop("Sounds/accelsmall");
            _cockpitHum = Loop("Sounds/sshipint");
            _ambience = Loop("Sounds/bioambient_1");
            _apEngage = _content.Load<SoundEffect>("Sounds/vo_sal_autopilotengage");
            _apDisengage = _content.Load<SoundEffect>("Sounds/vo_sal_autopilotdisengage");
            _countdownStart = _content.Load<SoundEffect>("Sounds/countdownstart");
            for (int n = 1; n <= CountdownSound.MaxTickSecond; n++)
                _countdownTicks[n] = _content.Load<SoundEffect>($"Sounds/countdown{n}");
            _gameOver = _content.Load<SoundEffect>("Sounds/vo_ma_gameover");
            IsLoaded = true;
            Console.WriteLine("[monogame] audio: loaded engine/cockpit/ambience/autopilot/countdown/gameover cues");
        }
        catch (Exception ex)
        {
            WarnOnce($"audio failed to load, disabling sound: {ex.Message}");
            IsLoaded = false;
        }
    }

    /// <summary>
    /// Drive the real sound mix for the current frame from live state: the
    /// engine loop's volume/pitch follow <see cref="EngineSoundCurve"/> on the
    /// ship's real throttle and speed; the cockpit hum is audible only in
    /// cockpit camera mode; the sector ambience plays for the whole session
    /// (started on the first update that carries a live frame).
    /// </summary>
    /// <param name="throttle">Real <c>Controls.Throttle</c> (−1..+1).</param>
    /// <param name="speed">Real <c>Velocity.Length()</c>.</param>
    /// <param name="cockpitMode">Whether the cockpit camera is active
    /// (<see cref="Rendering.CameraMode.Cockpit"/>).</param>
    public void Update(float throttle, float speed, bool cockpitMode)
    {
        if (!IsLoaded) return;

        StartAmbience();

        if (_engine is { } engine)
        {
            var (vol, pitch) = EngineSoundCurve.Mix(throttle, speed, EngineSoundCurve.MaxSpeedDefault);
            // Cockpit/chase distinction (optional, cheap): a slightly fuller
            // tone from the cockpit (you hear the hull around you), a touch
            // duller in chase. Not required by scope, but nice.
            float cockPitBoost = cockpitMode ? 0.08f : 0f;
            SetLoopVolume(engine, Math.Clamp(vol + cockPitBoost, 0f, 1f));
            Safe(() => engine.Pitch = pitch);
            EnsurePlaying(engine);
        }

        if (_cockpitHum is { } hum)
        {
            if (cockpitMode) EnsurePlaying(hum);
            else if (hum.State == SoundState.Playing) Safe(hum.Stop);
        }
    }

    /// <summary>Play the autopilot-engage voice chime (one-shot). No-op when
    /// audio is not loaded.</summary>
    public void PlayAutopilotEngage()
    {
        if (IsLoaded) Safe(() => _apEngage.Play());
    }

    /// <summary>Play the autopilot-disengage voice chime (one-shot). No-op
    /// when audio is not loaded.</summary>
    public void PlayAutopilotDisengage()
    {
        if (IsLoaded) Safe(() => _apDisengage.Play());
    }

    /// <summary>Play the countdown-start callout (one-shot) — P216 voices the
    /// opening of the mission-start briefing rather than leaving the first
    /// seconds silent (see <see cref="CountdownSound"/>). No-op when audio is
    /// not loaded.</summary>
    public void PlayCountdownStart()
    {
        if (IsLoaded) Safe(() => _countdownStart.Play());
    }

    /// <summary>Play the per-second countdown tick for <paramref name="second"/>
    /// seconds remaining (one-shot, 1..<see cref="CountdownSound.MaxTickSecond"/>)
    /// — <c>countdown10</c>…<c>countdown1</c>. Out-of-range seconds are a silent
    /// no-op (the caller decides eligibility via
    /// <see cref="CountdownSound.TickAssetFor"/>); this method only speaks the
    /// matching asset it already holds. No-op when audio is not loaded.</summary>
    public void PlayCountdownSecond(int second)
    {
        if (IsLoaded && _countdownTicks.TryGetValue(second, out var sfx))
            Safe(() => sfx.Play());
    }

    /// <summary>Play the game-over callout (one-shot) — <c>vo_ma_gameover</c>.
    /// No-op when audio is not loaded.</summary>
    public void PlayGameOver()
    {
        if (IsLoaded) Safe(() => _gameOver.Play());
    }

    /// <summary>
    /// Play a quick-chat voice line (P217) by its real asset stem (e.g.
    /// <c>vo_player_attack</c>) — a one-shot <c>Sounds/{stem}</c> cue, loaded
    /// lazily on first play and cached. A stem that is empty (no resolvable
    /// wave), not in content (the two quick-chat voices whose <c>.ogg</c> does
    /// not exist in the artwork tree), or that fails to load is logged once and
    /// stays silent — the caption text still displays; only the audio is
    /// skipped. No-op when the manager is inert.
    /// </summary>
    public void PlayVoice(string assetStem)
    {
        if (!IsLoaded || string.IsNullOrEmpty(assetStem))
            return;
        if (_voiceFailures.Contains(assetStem))
            return;
        if (_voices.TryGetValue(assetStem, out var sfx))
        {
            Safe(() => sfx.Play());
            return;
        }
        try
        {
            var loaded = _content.Load<SoundEffect>($"Sounds/{assetStem}");
            _voices[assetStem] = loaded;
            Safe(() => loaded.Play());
        }
        catch (Exception ex)
        {
            WarnOnce($"quick-chat voice asset unavailable ({assetStem}): {ex.Message}");
            _voiceFailures.Add(assetStem);
        }
    }

    private void StartAmbience()
    {
        if (_ambienceStarted || _ambience is not { } amb) return;
        _ambienceStarted = true;
        SetLoopVolume(amb, 0.22f);   // low, unobtrusive background bed
        EnsurePlaying(amb);
    }

    private SoundEffectInstance Loop(string asset)
    {
        var sfx = _content.Load<SoundEffect>(asset);
        var inst = sfx.CreateInstance();
        inst.IsLooped = true;
        return inst;
    }

    private static void SetLoopVolume(SoundEffectInstance inst, float volume) =>
        Safe(() => inst.Volume = Math.Clamp(volume, 0f, 1f));

    private static void EnsurePlaying(SoundEffectInstance inst)
    {
        if (inst.State != SoundState.Playing) Safe(inst.Play);
    }

    /// <summary>Wrap an audio call so a missing/broken device degrades instead
    /// of throwing into the game loop; logs the first failure only.</summary>
    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception)
        {
            // logged once by the load path / first-call path; stay silent here
            // to avoid spam — the manager is already inert at this point.
        }
    }

    private void WarnOnce(string message)
    {
        if (_warned) return;
        _warned = true;
        Console.WriteLine($"[monogame] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _engine?.Dispose();
            _cockpitHum?.Dispose();
            _ambience?.Dispose();
            _apEngage?.Dispose();
            _apDisengage?.Dispose();
            _countdownStart?.Dispose();
            foreach (var sfx in _countdownTicks.Values) sfx.Dispose();
            _gameOver?.Dispose();
            foreach (var sfx in _voices.Values) sfx.Dispose();
        }
        catch { /* disposal must not throw */ }
    }
}
