using System;
using System.Collections.Generic;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P501 — the touch-input slice of <see cref="Game1"/> (partial class, same
/// <c>Game1</c> type as the other slices). Reads <c>TouchPanel.GetState()</c> once
/// per frame, folds the touches through the pure
/// <see cref="TouchFlightInput"/>/<see cref="TouchLayout"/> module, and merges the
/// result into the per-frame <see cref="FlightCommand"/> EXACTLY like
/// <see cref="Game1.ApplyGamePadFlight"/> does (sticks through the shared
/// <see cref="JoystickFlightController"/>'s deadzone/rescale; held fire/afterburner
/// OR'd into stateM; edge buttons firing the same actions as their keys). Also
/// owns the semi-transparent overlay drawing. Desktop with no touch is unchanged:
/// <see cref="TouchAvailable"/> is false there, so the touch path never runs.
/// </summary>
public sealed partial class Game1
{
    // ── P501 — touch flight controls ──────────────────────────────────────────
    //
    // Precedence (documented at the call site in Game1.FlightInput.UpdateFlying):
    // an ACTIVE gamepad wins (overlay hidden + touch ignored); otherwise TOUCH
    // takes the analog axes over the keyboard base layer; on desktop with no touch
    // the keyboard-only path is untouched. The pure layout is rebuilt whenever the
    // viewport size changes; the ownership-bearing processor is persistent.

    private bool _touchChecked;
    private bool _touchEnabled;
    private TouchLayout? _touchLayout;
    private (int W, int H) _touchLayoutViewport;
    private readonly TouchFlightInput _touchInput = new();

    /// <summary>P501 — the live investment-pane model for the docked touch layer
    /// (assigned each frame the invest pane rebuilds; see Game1.Panes.cs).</summary>
    private Rendering.InvestmentPaneModel? _investModelLive;

    /// <summary>P501 — whether this platform reports touch input (true on the
    /// Android head via <c>TouchPanel</c>, false on DesktopGL), cached after the
    /// first query.</summary>
    private bool TouchAvailable()
    {
        if (!_touchChecked)
        {
            _touchEnabled = TouchPanel.GetCapabilities().IsConnected;
            _touchChecked = true;
        }
        return _touchEnabled;
    }

    /// <summary>Snapshot the raw touches for this frame as device-free samples.</summary>
    private static List<TouchSample> TouchSamplesNow()
    {
        var ts = TouchPanel.GetState();
        var list = new List<TouchSample>(ts.Count);
        foreach (var t in ts)
            list.Add(new TouchSample(t.Id, t.Position.X, t.Position.Y));
        return list;
    }

    /// <summary>P501 — the positions of touches that were RELEASED this frame (a
    /// finger lifted = a tap), for the menu-screen tap targets (side-select,
    /// ready-check, docked). Matches the P500 connect-screen convention of acting
    /// on <see cref="TouchLocationState.Released"/>.</summary>
    private static List<(float X, float Y)> TouchReleasedPointsNow()
    {
        var ts = TouchPanel.GetState();
        var pts = new List<(float, float)>(ts.Count);
        foreach (var t in ts)
            if (t.State == TouchLocationState.Released)
                pts.Add((t.Position.X, t.Position.Y));
        return pts;
    }

    /// <summary>Rebuild the touch layout when the viewport size changes (a resize /
    /// rotation picks up a fresh layout; the processor state is retained).</summary>
    private void EnsureTouchLayout()
    {
        var vp = GraphicsDevice.Viewport;
        if (_touchLayout is null || _touchLayoutViewport != (vp.Width, vp.Height))
        {
            _touchLayout = TouchLayout.Compute(vp.Width, vp.Height);
            _touchLayoutViewport = (vp.Width, vp.Height);
        }
    }

    /// <summary>
    /// P501 — post-Translate touch override over the keyboard-based command, the
    /// same shape as <see cref="ApplyGamePadFlight"/>. Called only when no gamepad
    /// is connected. Sticks override the turn axes (through the SAME
    /// <see cref="JoystickFlightController"/> deadzone/rescale the pad uses) and
    /// latch the throttle; held fire/afterburner OR into stateM; edge buttons
    /// fire the actions their keys would (missile, target-cycle T, autopilot G,
    /// P445 throttle steps, dock-or-menu → help overlay). Returns the (possibly
    /// unchanged) command.
    /// </summary>
    private FlightCommand ApplyTouchFlight(FlightCommand cmd)
    {
        var samples = TouchSamplesNow();
        EnsureTouchLayout();
        if (_touchLayout is not { } layout) return cmd;

        var tr = _touchInput.Update(samples, layout);
        if (!tr.AnyActive) return cmd;

        // Sticks through the shared joystick controller (deadzone/rescale + P445
        // throttle latch) — identical to ApplyGamePadFlight's stick folding.
        var gpr = _gamePadFlight.Update(tr.Axes);
        cmd = cmd with
        {
            Controls = cmd.Controls with
            {
                Yaw = gpr.YawActive ? gpr.Yaw : cmd.Controls.Yaw,
                Pitch = gpr.PitchActive ? gpr.Pitch : cmd.Controls.Pitch,
                Roll = gpr.RollActive ? gpr.Roll : cmd.Controls.Roll,
                Throttle = gpr.ThrottleOverride ?? cmd.Controls.Throttle,
            },
        };

        // Held buttons (continuous while the finger stays down).
        if (tr.FireHeld)
            cmd = cmd with { StateM = cmd.StateM | FlightInput.FireContribution(true, _groupFire, _selectedMount) };
        if (tr.AfterburnerHeld)
            cmd = cmd with { StateM = cmd.StateM | ShipControlState.AfterburnerButton };

        // Edge buttons — fire once per tap, reusing the exact key actions.
        if (tr.MissileEdge)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastMissileSendMs >= 250)
            {
                _lastMissileSendMs = nowMs;
                _session.SendFireMissile();
                Console.WriteLine("[monogame] touch fire missile -> FIRE_MISSILE(70)");
            }
        }
        if (tr.TargetCycleEdge)
        {
            Console.WriteLine("[monogame] touch target-cycle (T)");
            HandleTargetCycleKey();
        }
        if (tr.AutopilotEdge)
        {
            Console.WriteLine("[monogame] touch autopilot (G)");
            ToggleAutopilotForTarget();
        }
        if (tr.ThrottleUpEdge)
        {
            _throttle = FlightInput.StepThrottleUp(_throttle);
            Console.WriteLine($"[monogame] touch throttle UP -> {_throttle:F2} ({FlightInput.CruiseFraction(_throttle) * 100f:F0}%)");
        }
        if (tr.ThrottleDownEdge)
        {
            _throttle = FlightInput.StepThrottleDown(_throttle);
            Console.WriteLine($"[monogame] touch throttle DOWN -> {_throttle:F2} ({FlightInput.CruiseFraction(_throttle) * 100f:F0}%)");
        }
        if (tr.DockOrMenuEdge)
        {
            // In flight the "menu" surface is the H reference overlay; docked
            // panes are reached by docking (which handles their own taps).
            _helpVisible = !_helpVisible;
            Console.WriteLine($"[monogame] touch menu -> help {(_helpVisible ? "ON" : "OFF")}");
        }

        return cmd;
    }

    // ── P501 — semi-transparent overlay (sticks + action buttons) ─────────────

    private static readonly Color TouchBaseFill = new(0.10f, 0.12f, 0.18f, 0.45f);
    private static readonly Color TouchBaseOutline = new(0.55f, 0.62f, 0.72f, 0.55f);
    private static readonly Color TouchKnobIdle = new(0.55f, 0.62f, 0.75f, 0.55f);
    private static readonly Color TouchKnobActive = new(0.72f, 0.82f, 0.98f, 0.85f);
    private static readonly Color TouchButtonBg = new(0.08f, 0.10f, 0.14f, 0.5f);
    private static readonly Color TouchButtonActive = new(0.34f, 0.48f, 0.74f, 0.78f);

    /// <summary>Draw the transparent sticks + buttons over the HUD. Hidden when a
    /// gamepad is connected (touch input is inactive then anyway) and skipped on
    /// platforms with no touch. Called from the Flying HUD pass.</summary>
    private void DrawTouchOverlay()
    {
        if (!TouchAvailable()) return;
        if (CurrentPadState() is { IsConnected: true }) return;
        EnsureTouchLayout();
        if (_touchLayout is not { } layout) return;

        var vp = GraphicsDevice.Viewport;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            DrawStickRing(layout.LeftStickBase, layout.StickRadius,
                _touchInput.Knob(TouchStick.Left) is not null);
            DrawStickRing(layout.RightStickBase, layout.StickRadius,
                _touchInput.Knob(TouchStick.Right) is not null);

            // Knobs: the live knob when active, the idle base disc otherwise.
            DrawKnob(_touchInput.Knob(TouchStick.Left) ?? layout.LeftStickBase,
                _touchInput.Knob(TouchStick.Left) is not null);
            DrawKnob(_touchInput.Knob(TouchStick.Right) ?? layout.RightStickBase,
                _touchInput.Knob(TouchStick.Right) is not null);

            foreach (var b in System.Enum.GetValues<TouchButton>())
                DrawTouchButton(layout.ButtonRect(b), b);
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    private void DrawStickRing((float X, float Y) centre, float radius, bool active)
    {
        var r = new Rectangle((int)(centre.X - radius), (int)(centre.Y - radius),
            (int)(2f * radius), (int)(2f * radius));
        _spriteBatch.Draw(_whitePixel, r, active ? TouchButtonActive : TouchBaseFill);
        Outline(r, TouchBaseOutline);
    }

    private void DrawKnob((float X, float Y) pos, bool active)
    {
        float s = 0.45f * (_touchLayout?.StickRadius ?? 40f);
        var r = new Rectangle((int)(pos.X - s / 2f), (int)(pos.Y - s / 2f), (int)s, (int)s);
        _spriteBatch.Draw(_whitePixel, r, active ? TouchKnobActive : TouchKnobIdle);
    }

    private void DrawTouchButton(TouchRect rc, TouchButton kind)
    {
        var r = new Rectangle((int)rc.X, (int)rc.Y, (int)rc.Width, (int)rc.Height);
        bool down = _touchInput.IsButtonDown(kind);
        _spriteBatch.Draw(_whitePixel, r, down ? TouchButtonActive : TouchButtonBg);
        Outline(r, TouchBaseOutline);

        var label = TouchButtonLabel(kind);
        if (label.Length == 0) return;
        var sz = _hudSmall.MeasureString(label);
        _spriteBatch.DrawString(_hudSmall, label,
            new Vector2(rc.CenterX - sz.X / 2f, rc.CenterY - sz.Y / 2f),
            down ? Color.White : new Color(0.90f, 0.93f, 0.97f, 0.9f));
    }

    private static string TouchButtonLabel(TouchButton kind) => kind switch
    {
        TouchButton.Fire => "FIRE",
        TouchButton.Afterburner => "AB",
        TouchButton.Missile => "MSL",
        TouchButton.TargetCycle => "TRG",
        TouchButton.Autopilot => "AP",
        TouchButton.ThrottleUp => "+",
        TouchButton.ThrottleDown => "\u2212",
        TouchButton.DockOrMenu => "MENU",
        _ => "",
    };

    private void Outline(Rectangle r, Color color)
    {
        _spriteBatch.Draw(_whitePixel, new Rectangle(r.X, r.Y, r.Width, 1), color);
        _spriteBatch.Draw(_whitePixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), color);
        _spriteBatch.Draw(_whitePixel, new Rectangle(r.X, r.Y, 1, r.Height), color);
        _spriteBatch.Draw(_whitePixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), color);
    }
}
