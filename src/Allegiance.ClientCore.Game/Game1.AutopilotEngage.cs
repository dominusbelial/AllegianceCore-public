namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    /// <summary>P540 — engage the player autopilot toward a chosen target,
    /// factoring the engage tail out of <c>ToggleAutopilotForTarget</c>
    /// (P212/P524) so the G-key and the eject rescue path share one engage
    /// (sets the selection, the engaged flag, the mouse baseline, the
    /// stationary-target snapshot, logs, plays the engage sound, refreshes the
    /// title).</summary>
    private void EngageAutopilotToward(AutopilotTarget at, string logPrefix)
    {
        _autoTarget = at;
        _autoPilotEngaged = true;
        // A new order starts its own P862 hold window (see AutopilotHoldClock).
        _autoPilotHold.Reset();
        // M-1 — capture the virtual-joystick baseline at engage (native's
        // trekJoyStick snapshot, WinTrek.cpp:8283-8289) so only a deflection
        // *past 2× deadzone from here* counts as manual input (P501).
        _autoPilotMouseBaseline = _mouseFlight.Current;
        _autoTargetAtEngage = ResolveTargetPosition(at, out var atPos) ? atPos : null;
        Console.WriteLine($"[monogame] {logPrefix} autopilot ON → {at.Name} ({at.Kind}#{at.Id})");
        _sound?.PlayAutopilotEngage();
        RefreshWindowTitle();
    }

}
