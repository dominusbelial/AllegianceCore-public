using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P945 — the <c>--auto-hangar</c> driver: scripted hover / click / screenshot
/// steps over the native hangar screen, so a real-window proof can photograph
/// the idle screen, the launch and loadout hovers and the loadout → hangar →
/// launch sequence without an operator moving the pointer
/// (<see cref="HangarProofScript"/> parses the script).
///
/// <para>It only ever feeds the production paths: the pointer position comes from
/// the same <see cref="HangarScreen"/> rectangles the renderer draws, a click is
/// the ordinary <see cref="HangarControls.Hit"/> → <c>ActivateHangar</c>
/// dispatch, "back" sets the same <c>_hangarOnly</c> flag Escape does
/// (<c>Game1.FlightInput.cs:548</c>), and a shot is the P572 autoshot. Nothing
/// here runs unless the flag was passed.</para>
/// </summary>
public sealed partial class Game1
{
    private int _hangarScriptIndex;
    private long _hangarScriptStartMs = -1;
    private MouseState? _hangarScriptMouse;

    /// <summary>The scripted pointer position, or null when the driver is inert
    /// (the real pointer then applies).</summary>
    private MouseState? HangarScriptMouse => _hangarScriptMouse;

    /// <summary>Advance the script while the hangar screen is on screen. Called
    /// once per docked frame from <see cref="UpdateHangarControls"/>.</summary>
    private void StepHangarScript()
    {
        var steps = _autoPlan.HangarScript;
        // Runs while DOCKED (hangar or the loadout screen the script may open)
        // so a click that leaves the hangar does not stop the remaining steps.
        if (steps.Length == 0 || !_dockedActive) return;

        long now = Environment.TickCount64;
        if (_hangarScriptStartMs < 0)
        {
            _hangarScriptStartMs = now;
            _hangarScriptMouse = new MouseState(1, 1, 0, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released);
            Console.WriteLine($"[autoproof] --auto-hangar: {steps.Length} scripted step(s) from the hangar appearing");
        }

        double elapsed = (now - _hangarScriptStartMs) / 1000.0;
        var vp = GraphicsDevice.Viewport;
        while (_hangarScriptIndex < steps.Length && elapsed >= steps[_hangarScriptIndex].AtSeconds)
        {
            // Only one screenshot may be pending at a time (the autoshot slot is
            // single) — a long frame that makes two steps due keeps the later one
            // for the next frame instead of dropping it.
            if (steps[_hangarScriptIndex].Action == HangarScriptAction.Shot && _autoShotScheduled) break;
            var step = steps[_hangarScriptIndex++];
            switch (step.Action)
            {
                case HangarScriptAction.Idle:
                    _hangarScriptMouse = new MouseState(1, 1, 0, ButtonState.Released, ButtonState.Released,
                        ButtonState.Released, ButtonState.Released, ButtonState.Released);
                    Console.WriteLine($"[autoproof] hangar idle at {elapsed:0.0}s");
                    break;
                case HangarScriptAction.Hover:
                {
                    var rect = HangarScreen.Rect(vp.Width, vp.Height, step.Verb);
                    _hangarScriptMouse = new MouseState(rect.Center.X, rect.Center.Y, 0,
                        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released,
                        ButtonState.Released);
                    Console.WriteLine($"[autoproof] hangar hover {step.Verb} -> {rect.Center.X},{rect.Center.Y} at {elapsed:0.0}s");
                    break;
                }
                case HangarScriptAction.Click:
                {
                    var rect = HangarScreen.Rect(vp.Width, vp.Height, step.Verb);
                    // The production hit test resolves the verb for this point;
                    // the dispatcher then does exactly what a real click does.
                    var hit = HangarControls.Hit(vp.Width, vp.Height, rect.Center.X, rect.Center.Y, HangarDevelopments);
                    Console.WriteLine($"[autoproof] hangar click {step.Verb} -> {rect.Center.X},{rect.Center.Y} (hit {hit}) at {elapsed:0.0}s");
                    _hangarScriptMouse = new MouseState(rect.Center.X, rect.Center.Y, 0,
                        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released,
                        ButtonState.Released);
                    if (hit == step.Verb) ActivateHangar(hit);
                    break;
                }
                case HangarScriptAction.Back:
                    _hangarOnly = true;
                    _investActive = _teleportActive = _teamPaneVisible = false;
                    Console.WriteLine($"[autoproof] hangar back -> hangar screen at {elapsed:0.0}s");
                    break;
                case HangarScriptAction.Shot:
                    string tag = $"hangar{_hangarScriptIndex:00}";
                    ScheduleAutoshot(-1, nameTag: tag);
                    Console.WriteLine($"[autoproof] hangar shot '{tag}' at {elapsed:0.0}s");
                    break;
            }
        }
    }
}
