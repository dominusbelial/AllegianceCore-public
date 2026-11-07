using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P949 — the one-time keys hint a fresh profile sees when it first enters
/// flight: <c>Keys: W thrust | Up/Down/Left/Right steer | F6 controls (Classic
/// Allegiance keys available)</c>, every key taken from the LIVE labels
/// (<see cref="ActionMap.LabelOf"/>), so a rebind or preset switch is reflected.
///
/// <para>The text is ASCII-only because the Classic HUD's spritefonts bake
/// ASCII 32..126 (the same rule as <see cref="KeyBinding.Label"/> and the help
/// manual), so the separators are <c>|</c> rather than a typographic dot.</para>
///
/// <para>Lifetime: shown for <see cref="DurationSeconds"/> (60 s) or until the
/// player presses any key, whichever comes first; the caller then marks it seen
/// persistently and it never returns for that profile (a saved keymap also
/// suppresses it). Pure state — no clock, no graphics device — so the lifetime
/// rules are headless-testable.</para>
/// </summary>
public sealed class ControlHint
{
    /// <summary>How long the hint stays up when the player presses nothing.</summary>
    public const float DurationSeconds = 60f;

    /// <summary>The tail that names the other, native preset (P949).</summary>
    public const string NativeReminder = "Classic Allegiance keys available";

    /// <summary>The hint line for <paramref name="map"/>'s LIVE bindings, or null
    /// when the map has no thrust/steer/controls label to name (nothing useful to
    /// say). Never throws for an unbound action: a missing label is skipped.</summary>
    public static string? TextFor(ActionMap map)
    {
        var parts = new List<string>();
        string Label(string action) => map.LabelOf(action);

        string thrust = Label("ThrustForward");
        if (thrust.Length > 0) parts.Add(thrust + " thrust");

        // The four steering commands, de-duplicated in key order so the Port
        // layout reads Up/Down/Left/Right and a rebound/classic map shows what it
        // actually has.
        var steer = new List<string>();
        foreach (var action in new[] { "PitchUp", "PitchDown", "YawLeft", "YawRight" })
        {
            string label = Label(action);
            if (label.Length > 0 && !steer.Contains(label)) steer.Add(label);
        }
        if (steer.Count > 0) parts.Add(string.Join("/", steer) + " steer");

        string controls = Label("OpenKeyBindings");
        if (controls.Length > 0) parts.Add(controls + " controls");

        if (parts.Count == 0) return null;
        return "Keys: " + string.Join(" | ", parts) + " (" + NativeReminder + ")";
    }

    /// <summary>Whether the hint is due: only a fresh profile (no readable
    /// keymap) that has never dismissed it.</summary>
    public static bool ShouldShow(bool freshProfile, bool alreadySeen)
        => freshProfile && !alreadySeen;

    private readonly float _durationSeconds;

    /// <param name="text">The resolved hint line (see <see cref="TextFor"/>).</param>
    /// <param name="durationSeconds">Lifetime in seconds; defaults to 60.</param>
    public ControlHint(string text, float durationSeconds = DurationSeconds)
    {
        Text = text;
        _durationSeconds = durationSeconds > 0 ? durationSeconds : DurationSeconds;
    }

    /// <summary>The line the HUD draws (null once dismissed).</summary>
    public string Text { get; }

    /// <summary>True while the hint is still up.</summary>
    public bool Active { get; private set; } = true;

    /// <summary>Seconds the hint has been up.</summary>
    public float ElapsedSeconds { get; private set; }

    /// <summary>Why it went away (test/report evidence), null while active.</summary>
    public string? DismissReason { get; private set; }

    /// <summary>The text to draw, or null when the hint is over.</summary>
    public string? VisibleText => Active ? Text : null;

    /// <summary>
    /// Advance the hint by one frame. <paramref name="anyKeyPressed"/> dismisses
    /// it immediately (the player is already flying); otherwise it expires at
    /// <see cref="DurationSeconds"/>. Returns true when this call ended it.
    /// </summary>
    public bool Update(double dtSeconds, bool anyKeyPressed)
    {
        if (!Active) return false;
        ElapsedSeconds += (float)Math.Max(0.0, dtSeconds);
        if (anyKeyPressed) return Dismiss("key");
        if (ElapsedSeconds >= _durationSeconds) return Dismiss("timeout");
        return false;
    }

    /// <summary>End the hint (also the "the player saved a keymap" path).</summary>
    public bool Dismiss(string reason)
    {
        if (!Active) return false;
        Active = false;
        DismissReason = reason;
        return true;
    }
}
