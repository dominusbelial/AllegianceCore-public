namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P945 — the native launch button's frame animation, ported from
/// <c>FrameImageButtonPane</c> (<c>engine/frameimage.cpp:330-500</c>, created by
/// <c>hangar.mdl:12</c>: <c>FrameImageButtonPane("animlaunch", .7, false,
/// false)</c> = 0.7 s per loop, no loop, no run-to-last-frame).
///
/// <para><b>Native semantics</b> (<c>Evaluate</c>,
/// <c>engine/frameimage.cpp:420-456</c>): the pane owns a
/// <c>ModifiableNumber</c> in [0,1] that the <c>FrameImage</c> maps to a frame
/// with <c>bound((int)(value * nFrame), 0, nFrame - 1)</c>
/// (<c>engine/frameimage.cpp:110-122</c>). Entering the mouse starts a ramp
/// forward from the current value over <c>duration</c>; leaving starts the same
/// ramp backwards. With <c>loop = false</c> and <c>finish = false</c> the value
/// is clamped to [0,1], so the door opens while the pointer rests on it and
/// closes again when it leaves. The idle value is 0 → frame 0 (the closed door).
/// </para>
///
/// <para>Pure arithmetic (no graphics types), so the headless tests pin the
/// timing, the direction change and the frame mapping.</para>
/// </summary>
public sealed class HangarLaunchAnimation
{
    /// <summary>Native <c>.7</c> seconds per full ramp (<c>hangar.mdl:12</c>).</summary>
    public const float Duration = 0.7f;

    private float _value;
    private float _valueStart;
    private float _timeStart;
    private bool _inside;
    private bool _started;

    /// <summary>The current animation value in [0,1].</summary>
    public float Value => _value;

    /// <summary>Whether the pointer is on the launch pane (mirrors native
    /// <c>m_bInside</c>).</summary>
    public bool Inside => _inside;

    /// <summary>Advance to <paramref name="now"/> (seconds, any monotonic clock).
    /// An inside/outside edge latches the new ramp's start value and time exactly
    /// like native <c>MouseEnter</c>/<c>MouseLeave</c>
    /// (<c>engine/frameimage.cpp:470-489</c>).</summary>
    public void Update(bool inside, float now)
    {
        if (!_started)
        {
            _started = true;
            _inside = inside;
            _timeStart = now;
            _valueStart = _value;
        }
        else if (inside != _inside)
        {
            _inside = inside;
            _valueStart = _value;
            _timeStart = now;
        }

        float delta = (now - _timeStart) / Duration;
        // hangar.mdl passes loop = false and finish = false, so the two branches
        // those flags add in native's Evaluate are dead here; the value simply
        // ramps toward the pointer state and clamps to [0,1].
        float value = _inside ? _valueStart + delta : _valueStart - delta;
        _value = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>The frame the <c>FrameImage</c> shows for the current value
    /// (<c>engine/frameimage.cpp:110-122</c>).</summary>
    public int Frame(int frameCount) =>
        frameCount <= 0 ? 0 : Math.Clamp((int)(_value * frameCount), 0, frameCount - 1);

    /// <summary>Reset to the idle closed-door state (frame 0).</summary>
    public void Reset()
    {
        _value = 0f;
        _valueStart = 0f;
        _timeStart = 0f;
        _inside = false;
        _started = false;
    }
}
