using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P501 — a device-free floating-point rectangle used by the touch layout. A
/// plain value struct (not <c>Microsoft.Xna.Framework.Rectangle</c>) so the whole
/// touch module — layout + finger-ownership + stick math — is testable without a
/// graphics device, the same discipline as <see cref="GamePadFlightRoles"/> /
/// <see cref="FlightInput"/>. Coordinates are viewport pixels (top-left origin,
/// Y grows downward), matching MonoGame's <c>TouchPanel</c> report space.
/// </summary>
public readonly record struct TouchRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;

    public bool Contains(float px, float py) =>
        px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>The two virtual sticks: LEFT drives roll (X) + throttle (Y), RIGHT
/// drives yaw (X) + pitch (Y) — the exact Mode-2 mapping
/// <see cref="GamePadFlightRoles"/> already uses, so a touch gesture feels like
/// the gamepad stick it mirrors.</summary>
public enum TouchStick { Left, Right }

/// <summary>The touch action buttons. <see cref="Fire"/> and
/// <see cref="Afterburner"/> are HELD (continuous while the owning finger stays
/// down — the same semantics as <see cref="FlightInputState.Fire"/> /
/// Afterburner, i.e. Space/Shift); the rest are EDGE (fire once on finger-down,
/// like the keys <c>Game1</c> edge-detects: missile = LeftCtrl, target = T,
/// autopilot = G, throttle steps = =/− (P445), dock-or-menu = the in-flight
/// "menu" surface).</summary>
public enum TouchButton
{
    Fire,
    Afterburner,
    Missile,
    TargetCycle,
    Autopilot,
    ThrottleUp,
    ThrottleDown,
    DockOrMenu,
}

/// <summary>A single raw touch sample read from <c>TouchPanel.GetState()</c> in a
/// frame: the finger's id plus its viewport position (Y grows downward). Purged
/// of MonoGame types so the processor is headless-testable.</summary>
public readonly record struct TouchSample(int FingerId, float X, float Y);

/// <summary>
/// P501 — the pure touch LAYOUT: given a viewport size (phone or tablet, any
/// aspect), computes the two virtual-stick zones and the action-button rects,
/// scaled by the smaller viewport dimension so controls are a sensible thumb
/// size on every screen. Stick zones sit in the bottom corners (clear of the
/// central HUD); the buttons arc around them. Monotonic for a fixed viewport;
/// callers rebuild on a resize.
/// </summary>
public sealed class TouchLayout
{
    /// <summary>Rectangular touch-capture zone for a stick. A finger that lands
    /// anywhere inside claims the stick; the touch point becomes the
    /// floating-stick origin.</summary>
    public TouchRect LeftStickZone { get; }
    public TouchRect RightStickZone { get; }

    /// <summary>The floating-stick deflection clamp radius in pixels: a raw drag
    /// is divided by this for the −1..+1 axis value, so dragging further than the
    /// radius saturates at full deflection. The shared joystick controller then
    /// applies native's exact deadzone/rescale (see the wiring in <c>Game1</c>).</summary>
    public float StickRadius { get; }

    /// <summary>Every action button's capture rect, keyed by kind.</summary>
    public IReadOnlyDictionary<TouchButton, TouchRect> Buttons { get; }

    /// <summary>Centre of the LEFT virtual-stick resting ring (the knob starts /
    /// returns here when the stick is idle).</summary>
    public (float X, float Y) LeftStickBase { get; }
    public (float X, float Y) RightStickBase { get; }

    private TouchLayout(
        TouchRect leftZone, TouchRect rightZone, float stickRadius,
        IReadOnlyDictionary<TouchButton, TouchRect> buttons,
        (float X, float Y) leftBase, (float X, float Y) rightBase)
    {
        LeftStickZone = leftZone;
        RightStickZone = rightZone;
        StickRadius = stickRadius;
        Buttons = buttons;
        LeftStickBase = leftBase;
        RightStickBase = rightBase;
    }

    public TouchRect ButtonRect(TouchButton b) => Buttons[b];

    /// <summary>
    /// Build the layout for a <paramref name="width"/>×<paramref name="height"/>
    /// viewport (pixels, top-left origin). <paramref name="unit"/> defaults to
    /// <c>0.037 × min(width,height)</c> — a control sizing that reads at thumb
    /// size on phones and scales up on tablets. Buttons' centres are fractions of
    /// width/height so aspect-ratio scaling is automatic; this is the single
    /// place to retune the feel.
    /// </summary>
    public static TouchLayout Compute(float width, float height, float? unit = null)
    {
        float mn = MathF.Min(width, height);
        float u = unit ?? 0.037f * mn;

        // Stick zones: bottom-left / bottom-right — clear of the central HUD.
        float zoneHalf = 1.6f * u;
        var leftZone = new TouchRect(
            0.20f * width - zoneHalf, 0.82f * height - zoneHalf, 2f * zoneHalf, 2f * zoneHalf);
        var rightZone = new TouchRect(
            0.80f * width - zoneHalf, 0.82f * height - zoneHalf, 2f * zoneHalf, 2f * zoneHalf);

        var buttons = new Dictionary<TouchButton, TouchRect>
        {
            // Primary held buttons in the lower-centre "thumb lane", clear of the
            // two stick zones.
            [TouchButton.Fire] = CentreSquare(0.60f * width, 0.68f * height, 2.6f * u),
            [TouchButton.Afterburner] = CentreSquare(0.40f * width, 0.72f * height, 1.9f * u),

            // Left-upper cluster: combat / state edge actions above the left stick.
            [TouchButton.Missile] = CentreSquare(0.077f * width, 0.21f * height, 1.8f * u),
            [TouchButton.TargetCycle] = CentreSquare(0.083f * width, 0.39f * height, 1.8f * u),
            [TouchButton.Autopilot] = CentreSquare(0.089f * width, 0.53f * height, 1.8f * u),

            // Right-upper column: throttle steps + dock/menu, stacked with a
            // height-fraction gap (robust on narrow/portrait screens) and clear of
            // the right stick zone below.
            [TouchButton.DockOrMenu] = CentreSquare(0.925f * width, 0.055f * height, 1.7f * u),
            [TouchButton.ThrottleUp] = CentreSquare(0.925f * width, 0.150f * height, 1.7f * u),
            [TouchButton.ThrottleDown] = CentreSquare(0.925f * width, 0.245f * height, 1.7f * u),
        };

        return new TouchLayout(
            leftZone, rightZone, 1.30f * u, buttons,
            (0.20f * width, 0.82f * height), (0.80f * width, 0.82f * height));
    }

    private static TouchRect CentreSquare(float cx, float cy, float size) =>
        new(cx - size / 2f, cy - size / 2f, size, size);
}

/// <summary>
/// P501 — the result of one <see cref="TouchFlightInput.Update"/>: the two
/// sticks folded into the same role-ordered <see cref="JoystickAxes"/> the pad
/// path produces (via <see cref="GamePadFlightRoles.Gather"/>; <c>Game1</c> then
/// runs it through the shared joystick controller's deadzone/rescale), the held
/// button flags, and the edge-pressed buttons for actions that fire once.
/// </summary>
public readonly record struct TouchFlightResult(
    JoystickAxes Axes,
    bool FireHeld,
    bool AfterburnerHeld,
    bool MissileEdge,
    bool TargetCycleEdge,
    bool AutopilotEdge,
    bool ThrottleUpEdge,
    bool ThrottleDownEdge,
    bool DockOrMenuEdge,
    bool AnyActive)
{
    public static TouchFlightResult None => new();
}

/// <summary>
/// P501 — the stateful per-frame touch processor. It owns the per-finger
/// ownership bookkeeping the task requires (a finger that starts on a control
/// keeps it until lifted), converts each stick's drag into −1..+1 deflections,
/// and reports held/edge button presses. Free of any MonoGame type on the public
/// surface: <c>Game1</c> reads <c>TouchPanel.GetState()</c>, maps the raw touches
/// to <see cref="TouchSample"/>s, and hands them in — so ownership + stick math
/// are unit-testable without a graphics device or a real multitouch screen.
/// </summary>
/// <remarks>
/// <para><b>Floating sticks.</b> The first finger to land inside a stick zone
/// becomes that stick's origin (the knob starts under the finger); dragging
/// deflects the knob relative to that origin, clamped to
/// <see cref="TouchLayout.StickRadius"/>. Lifting releases the stick (knob
/// springs back, deflection → 0).</para>
/// <para><b>Sign convention.</b> Sticks report deflections in the space the pad
/// path expects (Y up-positive after converting screen-down): LEFT X → roll, LEFT
/// Y → throttle; RIGHT X → yaw, RIGHT Y → pitch. Passed through
/// <see cref="GamePadFlightRoles.Gather"/> so the same sign handling (incl. the
/// default pitch inversion cancelling MapJoystick's negation) applies — a
/// drag-up pitches up, a drag-right yaws right, exactly like the right stick.</para>
/// </remarks>
public sealed class TouchFlightInput
{
    private readonly Dictionary<int, TouchStick> _stickByFinger = new();
    private readonly Dictionary<TouchStick, int> _fingerByStick = new();
    private readonly Dictionary<int, TouchButton> _buttonByFinger = new();
    private readonly Dictionary<int, (float X, float Y)> _stickOrigin = new();
    private readonly Dictionary<TouchStick, (float X, float Y)> _knob = new();
    private readonly Dictionary<TouchButton, bool> _buttonDown = new();
    private readonly HashSet<int> _prevFingers = new();

    /// <summary>Consume one frame of touches and fold them into a
    /// <see cref="TouchFlightResult"/>. Ownership is sticky across frames: a
    /// finger that claimed a control keeps it until it is lifted.</summary>
    public TouchFlightResult Update(IReadOnlyList<TouchSample> touches, TouchLayout layout)
    {
        var current = new HashSet<int>(touches.Count);
        foreach (var t in touches) current.Add(t.FingerId);

        // 1) Release fingers that were down last frame but lifted this frame.
        foreach (var fid in _prevFingers)
        {
            if (current.Contains(fid)) continue;
            if (_stickByFinger.Remove(fid, out var stick))
            {
                _fingerByStick.Remove(stick);
                _stickOrigin.Remove(fid);
                _knob.Remove(stick);          // knob springs back -> deflection 0
            }
            if (_buttonByFinger.Remove(fid, out var btn))
                _buttonDown[btn] = false;
        }

        // 2) New fingers claim a control (a finger "starts on" a control here).
        foreach (var t in touches)
        {
            if (_prevFingers.Contains(t.FingerId)) continue;   // continuing finger
            if (_stickByFinger.ContainsKey(t.FingerId) || _buttonByFinger.ContainsKey(t.FingerId))
                continue;
            if (TryClaimButton(t, layout)) continue;
            if (layout.LeftStickZone.Contains(t.X, t.Y))
                AssignStick(t.FingerId, TouchStick.Left, t.X, t.Y);
            else if (layout.RightStickZone.Contains(t.X, t.Y))
                AssignStick(t.FingerId, TouchStick.Right, t.X, t.Y);
        }

        // 3) Fold the live knobs + held buttons into a result.
        var edges = new HashSet<TouchButton>();
        foreach (var t in touches)
        {
            if (_stickByFinger.TryGetValue(t.FingerId, out var stick))
            {
                _knob[stick] = (t.X, t.Y);
            }
            else if (_buttonByFinger.TryGetValue(t.FingerId, out var btn))
            {
                _buttonDown[btn] = true;
                if (!_prevFingers.Contains(t.FingerId))
                    edges.Add(btn);            // freshly claimed this frame
            }
        }

        float leftX = 0f, leftYUp = 0f, rightX = 0f, rightYUp = 0f;
        if (TryAxes(TouchStick.Left, layout, out var lx, out var ly))
        {
            leftX = lx;
            leftYUp = ly;
        }
        if (TryAxes(TouchStick.Right, layout, out var rx, out var ry))
        {
            rightX = rx;
            rightYUp = ry;
        }

        var axes = GamePadFlightRoles.Gather(leftX, leftYUp, rightX, rightYUp);

        _prevFingers.Clear();
        foreach (var f in current) _prevFingers.Add(f);

        return new TouchFlightResult(
            Axes: axes,
            FireHeld: Held(TouchButton.Fire),
            AfterburnerHeld: Held(TouchButton.Afterburner),
            MissileEdge: edges.Contains(TouchButton.Missile),
            TargetCycleEdge: edges.Contains(TouchButton.TargetCycle),
            AutopilotEdge: edges.Contains(TouchButton.Autopilot),
            ThrottleUpEdge: edges.Contains(TouchButton.ThrottleUp),
            ThrottleDownEdge: edges.Contains(TouchButton.ThrottleDown),
            DockOrMenuEdge: edges.Contains(TouchButton.DockOrMenu),
            AnyActive: _stickByFinger.Count + _buttonByFinger.Count > 0);
    }

    /// <summary>Pure stick-deflection helper: given a screen-space drag delta
    /// (X right-positive, Y down-positive) and the clamp radius, return the
    /// (roll/yaw) X and (pitch/throttle) Y-up deflections in −1..+1. Exposed so
    /// the math is unit-tested directly.</summary>
    public static (float X, float Y) DragToAxes(float dxScreen, float dyScreen, float radius) =>
        (Math.Clamp(dxScreen / radius, -1f, 1f),
         Math.Clamp(-dyScreen / radius, -1f, 1f));

    private void AssignStick(int fingerId, TouchStick stick, float x, float y)
    {
        _stickByFinger[fingerId] = stick;
        _fingerByStick[stick] = fingerId;
        _stickOrigin[fingerId] = (x, y);
        _knob[stick] = (x, y);
    }

    private bool TryAxes(TouchStick stick, TouchLayout layout, out float x, out float yUp)
    {
        x = 0f;
        yUp = 0f;
        if (!_knob.TryGetValue(stick, out var knob) ||
            !_fingerByStick.TryGetValue(stick, out var fid) ||
            !_stickOrigin.TryGetValue(fid, out var origin))
            return false;
        (x, yUp) = DragToAxes(knob.X - origin.X, knob.Y - origin.Y, layout.StickRadius);
        return true;
    }

    private bool TryClaimButton(TouchSample t, TouchLayout layout)
    {
        foreach (var kv in layout.Buttons)
        {
            if (kv.Value.Contains(t.X, t.Y))
            {
                _buttonByFinger[t.FingerId] = kv.Key;
                _buttonDown[kv.Key] = true;
                return true;
            }
        }
        return false;
    }

    private bool Held(TouchButton b) => _buttonDown.TryGetValue(b, out var d) && d;

    /// <summary>Whether a button is currently held by a finger this frame (for
    /// the overlay's active-highlight).</summary>
    public bool IsButtonDown(TouchButton b) => _buttonDown.TryGetValue(b, out var d) && d;

    /// <summary>Knob position (viewport px) for drawing the stick knob, or
    /// <c>null</c> when that stick is not active this frame.</summary>
    public (float X, float Y)? Knob(TouchStick stick)
        => _knob.TryGetValue(stick, out var p) ? p : null;
}

/// <summary>
/// P501 — a small helper for the non-flight (menu) screens: given each frame's
/// touch samples, report the positions of fingers that were NOT present in the
/// previous frame (a fresh finger-down = a tap). <c>Game1</c> uses it to
/// hit-test side-select / ready-check / docked panes without per-control
/// ownership. Device-free.
/// </summary>
public sealed class TouchTaps
{
    private readonly HashSet<int> _prev = new();

    /// <summary>Return the freshly-pressed finger positions for
    /// <paramref name="touches"/> and advance the previous-frame set. A finger
    /// already down last frame yields nothing.</summary>
    public List<(int Finger, float X, float Y)> NewTaps(IReadOnlyList<TouchSample> touches)
    {
        var current = new HashSet<int>(touches.Count);
        foreach (var t in touches) current.Add(t.FingerId);

        var taps = new List<(int, float, float)>();
        foreach (var t in touches)
        {
            if (!_prev.Contains(t.FingerId))
                taps.Add((t.FingerId, t.X, t.Y));
        }

        _prev.Clear();
        foreach (var f in current) _prev.Add(f);
        return taps;
    }
}
