using System.Globalization;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P948 — the persisted mouse-flight options and the two timing helpers the
/// in-flight controls need. Pure (no MonoGame types, no file I/O), so the
/// save/load, startup-apply and key-repeat rules are unit-testable headless.
///
/// <para><b>Native shape.</b> Allegiance stores both values in its single
/// preferences blob and exposes them in Game Options:</para>
/// <list type="bullet">
/// <item><c>FlipY</c> — <c>LoadPreference("FlipY", 0)</c>
/// (<c>WinTrek/WinTrek.cpp:3048</c>), toggled and saved by
/// <c>ToggleFlipY</c> (<c>:5297-5305</c>, menu item <c>idmToggleFlipY</c> 'Y',
/// <c>:4536</c>), applied to the pitch axis at <c>:10849-10851</c>.</item>
/// <item><c>MouseSensitivity</c> — <c>LoadPreference("MouseSensitivity", "0.6")</c>
/// (<c>:2809</c>) handed to the engine mouse (<c>:2811</c>), stepped ±0.01 within
/// [0.1, 2.0] and SAVED by <c>AdjustMouseSens</c> (<c>:5512-5531</c>), menu items
/// <c>idmMouseSensUp</c>/<c>idmMouseSensDown</c> ('U'/'D', <c>:4612-4613</c>).</item>
/// </list>
///
/// <para>This port keeps both in <c>player.json</c> (the same client-options
/// store as Force Feedback, the chat filter and the 3D-sound switch — the
/// analogue of native's one preference blob), applies them at startup, and
/// saves on every change.</para>
/// </summary>
public static class MouseOptions
{
    /// <summary>Apply the persisted pair to a controller. A non-finite stored
    /// sensitivity (hand-edited file) degrades to native's default rather than
    /// poisoning the flight axes; the controller clamps the range itself.</summary>
    public static void Apply(MouseFlightController controller, bool invertY, float sensitivity)
    {
        ArgumentNullException.ThrowIfNull(controller);
        controller.InvertPitch = invertY;
        controller.MouseSensitivity = float.IsFinite(sensitivity)
            ? sensitivity
            : MouseFlightController.DefaultMouseSensitivity;
    }

    /// <summary>Apply the values stored in the player's preferences file.</summary>
    public static void Apply(MouseFlightController controller, IntroPreferences prefs)
        => Apply(controller, prefs.InvertMouseY, prefs.MouseSensitivity);

    /// <summary>Copy the live controller values back onto the preferences
    /// snapshot before it is saved (so a save retains the exact live state).</summary>
    public static void Capture(MouseFlightController controller, IntroPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(prefs);
        prefs.InvertMouseY = controller.InvertPitch;
        prefs.MouseSensitivity = controller.MouseSensitivity;
    }

    /// <summary>One native sensitivity step (<c>c_fMouseSensDelta = 0.01f</c>,
    /// <c>WinTrek.cpp:88</c>) clamped to <c>AdjustMouseSens</c>'s
    /// <c>[0.1, 2.0]</c> (<c>:5514</c>).</summary>
    public static float Step(float current, int steps)
    {
        if (!float.IsFinite(current)) current = MouseFlightController.DefaultMouseSensitivity;
        return Math.Clamp(current + steps * MouseFlightController.MouseSensitivityStep,
            MouseFlightController.MinMouseSensitivity, MouseFlightController.MaxMouseSensitivity);
    }

    /// <summary>The percentage native's menu shows for a value
    /// (<c>(int)(value * 100)</c> with <c>"%.0f"</c>, <c>WinTrek.cpp:5612</c>).</summary>
    public static string Percent(float value) =>
        ((int)MathF.Round(value * 100f)).ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>Native's <c>GetMouseSensMenuString</c> wording
    /// (<c>WinTrek.cpp:5599-5615</c>): "Raise Mouse Sensitivity to NN %",
    /// "Lower Mouse Sensitivity to NN %", and the "(highest)"/"(lowest)" end
    /// stops.</summary>
    public static string StepLabel(float current, int direction)
    {
        string prefix = direction > 0 ? "Raise " : "Lower ";
        if (direction > 0 && current >= MouseFlightController.MaxMouseSensitivity)
            return prefix + "Mouse Sensitivity (highest)";
        if (direction < 0 && current <= MouseFlightController.MinMouseSensitivity)
            return prefix + "Mouse Sensitivity (lowest)";
        return prefix + "Mouse Sensitivity to " + Percent(Step(current, direction > 0 ? 1 : -1));
    }

    /// <summary>The brief on-screen readout shown after a change
    /// ("MOUSE SENSITIVITY 60 %").</summary>
    public static string Notice(float value) => "MOUSE SENSITIVITY " + Percent(value);
}

/// <summary>
/// P948 — held-key auto-repeat for the sensitivity keys, so a held chord is
/// usable. Native's <c>idmMouseSensUp</c>/<c>idmMouseSensDown</c> menu items fire
/// once per activation (<c>WinTrek.cpp:6431-6437</c>); a keyboard in this port
/// has to repeat to reach the same effect, so after
/// <see cref="InitialDelaySeconds"/> it fires every
/// <see cref="RepeatIntervalSeconds"/> (10 steps/s — the whole [0.1, 2.0] range
/// takes ~2 s to cross) until released. The first frame of a press always fires
/// immediately, so a single tap is exactly native's one step.
/// </summary>
public sealed class KeyRepeat
{
    /// <summary>Delay before the first repeat (a deliberate tap never repeats).</summary>
    public const float InitialDelaySeconds = 0.35f;
    /// <summary>Interval between repeats while held → 10 steps per second.</summary>
    public const float RepeatIntervalSeconds = 0.1f;

    private float _untilNext;
    private bool _wasDown;

    /// <summary>P948 — mark the key as already held (or not) WITHOUT firing, so a
    /// key physically held across a screen transition does not register as a
    /// fresh press on the first flight frame (the P523 latch-priming rule).</summary>
    public void Prime(bool down)
    {
        _wasDown = down;
        _untilNext = down ? InitialDelaySeconds : 0f;
    }

    /// <summary>True on the press frame and on every repeat frame while held.</summary>
    public bool Update(bool down, float dtSeconds)
    {
        if (!down)
        {
            _wasDown = false;
            _untilNext = 0f;
            return false;
        }
        if (!_wasDown)
        {
            _wasDown = true;
            _untilNext = InitialDelaySeconds;
            return true;
        }
        if (dtSeconds > 0f) _untilNext -= dtSeconds;
        if (_untilNext > 0f) return false;
        _untilNext += RepeatIntervalSeconds;
        // A very long frame (stall, alt-tab) must not queue a burst of steps.
        if (_untilNext <= 0f) _untilNext = RepeatIntervalSeconds;
        return true;
    }
}

/// <summary>
/// P948 — the transient HUD readout for a mouse-option change: visible for
/// <see cref="HoldSeconds"/> then fading over <see cref="FadeSeconds"/>. Pure,
/// so the HUD test can pin the timing without a graphics device.
/// </summary>
public sealed class MouseFlightNotice
{
    /// <summary>Seconds the readout stays fully legible.</summary>
    public const float HoldSeconds = 1.8f;
    /// <summary>Seconds of fade-out after the hold.</summary>
    public const float FadeSeconds = 0.6f;

    private float _remaining;

    /// <summary>The readout text (empty until something is shown).</summary>
    public string Text { get; private set; } = "";

    /// <summary>Show <paramref name="text"/> and restart the hold timer.</summary>
    public void Show(string text)
    {
        Text = text;
        _remaining = HoldSeconds + FadeSeconds;
    }

    /// <summary>Advance the timer; call once per frame with the frame delta.</summary>
    public void Tick(float dtSeconds)
    {
        if (dtSeconds <= 0f) return;
        _remaining = MathF.Max(0f, _remaining - dtSeconds);
        if (_remaining <= 0f) Text = "";
    }

    /// <summary>Hide immediately (no fade).</summary>
    public void Clear()
    {
        _remaining = 0f;
        Text = "";
    }

    /// <summary>True while the readout should draw.</summary>
    public bool Visible => _remaining > 0f && Text.Length > 0;

    /// <summary>1 while held, ramping to 0 across the fade.</summary>
    public float Opacity => FadeSeconds <= 0f ? (Visible ? 1f : 0f)
        : Math.Clamp(_remaining / FadeSeconds, 0f, 1f);
}
