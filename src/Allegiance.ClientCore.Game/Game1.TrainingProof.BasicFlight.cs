using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    // P940: this opt-in pilot only supplies keyboard/mouse state. Assertions
    // below observe the command and local physics AFTER the shipping input path.
    private string _proofM2Stage = "";
    private string _proofM2Probe = "";
    private int _proofM2Step, _proofM2Samples, _proofM2Wheel;
    private bool _proofM2WheelSent;
    private readonly HashSet<string> _proofM2Observed = [];
    private readonly HashSet<string> _proofM2Stages = [];
    private double _proofM2WaitAt;
    private string _proofM2WaitGoal = "";
    private bool _proofM2Reminders = false; // tool selects the ordinary waiting-player policy
    private string _proofM2Audio = "normal";
    private readonly HashSet<string> _proofM2Voices = [];
    private readonly List<string> _proofM2Receipts = [];

    private static string BasicFlightStage(string goal)
    {
        var path = goal.Split('.');
        if (path.Length != 2 || !int.TryParse(path[0], out int major) || !int.TryParse(path[1], out int step)) return "";
        return major switch
        {
            1 => "intro-locked", // mission2.cpp:157-163
            3 when step >= 2 => "turn-only", // :298-301
            4 when step >= 2 => "throttle-only", // :391-396
            5 => "throttle-only",
            6 when step <= 3 => "forced-throttle", // :516-521
            6 when step is >= 9 and <= 12 => "cleared-locked", // :585-590
            6 when step >= 13 => "all-axes", // :608-613
            7 when step is >= 2 and <= 7 => "scripted-booster", // :662-699
            7 when step is 13 or 14 => "player-booster", // :725-756
            7 when step >= 15 => "booster-disabled", // :761-765
            8 when step < 6 => "booster-disabled",
            _ => ""
        };
    }

    private static readonly string[] BasicFlightProbes =
        ["keyboard-turn", "mouse-turn", "throttle-zero", "throttle-up", "throttle-down", "wheel-up", "wheel-down", "full-iso", "reset-between-backslashes", "full-us", "restore-stop", "space"];

    private void DriveMission2(ClientTrainingSite site, List<Keys> keys)
    {
        string goal = _trainingSession!.Mission.ActiveGoalPath;
        string stage = BasicFlightStage(goal);
        if (stage == "intro-locked" && _cameraMode != Rendering.CameraMode.Cockpit) stage = "";
        // Carry the cumulative wheel counter through every frame; resetting it
        // would invent a reverse notch on the next read.
        _proofMouse = BasicFlightMouse(0, 0);
        keys.Clear();
        _proofTapKey = null;

        if (stage.Length > 0 && stage != _proofM2Stage)
        {
            _proofM2Stage = stage; _proofM2Step = _proofM2Samples = 0; _proofM2WheelSent = false;
        }
        _proofM2Probe = "";
        bool prompt = goal is "3.6" or "4.7" or "5.3" or "6.16" or "7.13";
        if (prompt && _proofM2WaitGoal != goal)
        { _proofM2WaitGoal = goal; _proofM2WaitAt = _trainingClock; }
        if (prompt && _proofM2Reminders && _trainingClock - _proofM2WaitAt < 32) return;
        bool simple = stage is "scripted-booster" or "player-booster" or "booster-disabled";
        // Muted/missing-clip runs investigate sound liveness separately. The
        // narrated default run requires the complete stage/input cross product.
        int limit = stage is "intro-locked" or "forced-throttle" or "cleared-locked" || _proofM2Audio != "normal" ? 2 : BasicFlightProbes.Length;
        if (stage.Length > 0 && (simple ? _proofM2Step < 1 : _proofM2Step < limit))
        {
            string probe = simple ? "space" : BasicFlightProbes[_proofM2Step];
            // Full throttle is the success condition at 4.7. Test it last there;
            // exercise both physical backslash variants at 6.16 as well.
            if (stage == "throttle-only" && probe is "full-iso" or "full-us")
            {
                if (goal.StartsWith("5.", StringComparison.Ordinal)) { _proofM2Step++; return; }
                probe = "restore-stop";
            }
            // SPACE acknowledges these goals. Sending it before their prompt
            // would pre-satisfy the queued GetKeyCondition and hide the prompt.
            if (probe == "space" && (stage == "turn-only" && goal != "3.6"
                || stage == "all-axes" && goal != "6.16"))
            { keys.Add(Keys.Back); return; }
            bool mouse = probe == "mouse-turn";
            if (mouse != _mouseFlightEnabled)
            {
                keys.Add(Keys.M); // ordinary mouse-flight toggle, including first-delta discard
                _proofM2Samples = 0;
                return;
            }
            // Release every key for one input sample between probes so presets,
            // step keys and M all get their normal up/down edges.
            if (_proofM2Samples == 0) { _proofM2Probe = "release"; return; }
            _proofM2Probe = probe;
            switch (probe)
            {
                case "keyboard-turn":
                    keys.AddRange([Keys.Left, Keys.Up, Keys.A]);
                    if (stage != "throttle-only") keys.Add(Keys.OemBackslash);
                    break;
                case "mouse-turn":
                    _proofMouse = BasicFlightMouse(200, -160); keys.Add(Keys.A);
                    if (stage != "throttle-only") keys.Add(Keys.OemPipe);
                    break;
                case "throttle-zero": case "restore-stop": case "reset-between-backslashes": keys.Add(Keys.Back); break;
                case "throttle-up": keys.Add(Keys.OemPlus); break;
                case "throttle-down": keys.Add(Keys.OemMinus); break;
                case "full-iso": keys.Add(Keys.OemBackslash); break;
                case "full-us": keys.Add(Keys.OemPipe); break;
                case "space": keys.Add(Keys.Space); break;
                case "wheel-up": case "wheel-down":
                    if (!_proofM2WheelSent)
                    {
                        // Follow the platform normalizer, exactly as physical
                        // wheel events do, while requesting one logical notch.
                        _proofM2Wheel += (probe == "wheel-up" ? 120 : -120) * (int)Flight.FlightInput.WheelSignNormalizer;
                        _proofM2WheelSent = true;
                    }
                    _proofMouse = BasicFlightMouse(0, 0);
                    break;
            }
            return;
        }
        if (_mouseFlightEnabled) { keys.Add(Keys.M); return; }
        if (stage is "intro-locked" or "turn-only" or "forced-throttle" or "cleared-locked" or "all-axes")
            keys.Add(Keys.Back);

        switch (goal)
        {
            case "3.6": case "6.16": keys.Clear(); keys.Add(Keys.Space); break;
            case "4.7": keys.Add(Keys.OemBackslash); break;
            case "5.3":
                // Actual scroll-down, rather than the previous OemMinus shortcut.
                if (!_proofM2WheelSent) { _proofM2Wheel -= 120 * (int)Flight.FlightInput.WheelSignNormalizer; _proofM2WheelSent = true; }
                _proofMouse = BasicFlightMouse(0, 0); break;
            case "7.13": keys.Add(Keys.Space); break;
        }
    }

    private MouseState BasicFlightMouse(int dx, int dy) => new(
        GraphicsDevice.Viewport.Width / 2 + dx, GraphicsDevice.Viewport.Height / 2 + dy, _proofM2Wheel,
        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);

    private void ObserveBasicFlightProof(double seconds)
    {
        if (!_proofEnabled || _trainingSession is not { Started: true, Mission: Mission2 } session) return;
        string goal = session.Mission.ActiveGoalPath;
        string stage = BasicFlightStage(goal);
        if (stage != _proofM2Stage || _proofM2Probe.Length == 0 || seconds <= 0) return;
        if (_proofM2Probe == "release") { _proofM2Samples++; return; }
        _proofM2Samples++;
        if (_proofM2Samples < (stage == "cleared-locked" || _proofM2Probe == "space" ? 2 : 4)) return; // actual held input, after enable/edge/physics settling
        // Wheel notches step at most every WheelStepIntervalMs; at fast frame
        // rates four frames can end before a queued notch is applied.
        if (_proofM2Probe is "wheel-up" or "wheel-down" && _wheelQueue != 0) return;
        var raw = TrainingControlState.InputControls;
        var sent = TrainingOwnState.Controls;
        int state = TrainingOwnState.StateM;
        bool turns = stage is "turn-only" or "all-axes" or "scripted-booster" or "player-booster" or "booster-disabled";
        bool throttle = stage is "throttle-only" or "all-axes" or "scripted-booster" or "player-booster" or "booster-disabled";
        void Check(bool okay, string message)
        {
            if (!okay) throw new InvalidOperationException($"P940 {goal} {stage}/{_proofM2Probe}: {message}; raw={raw}; sent={sent}; state={state}");
        }
        if (_proofM2Probe is "keyboard-turn" or "mouse-turn")
        {
            Check(MathF.Abs(raw.Yaw) > .001f && MathF.Abs(raw.Pitch) > .001f && MathF.Abs(raw.Roll) > .001f, "turn input did not reach flight translation");
            if (turns)
            {
                Check(MathF.Abs(sent.Yaw) > .001f && MathF.Abs(sent.Pitch) > .001f && MathF.Abs(sent.Roll) > .001f, "allowed turn axis did not respond");
                Check(_ownSim.TurnRates.All(rate => MathF.Abs(rate) > .001f), "turn controls did not rotate the actual local ship");
            }
        }
        if (!turns) Check(sent.Yaw == 0 && sent.Pitch == 0 && sent.Roll == 0, "locked turn axis responded");
        if (_proofM2Probe is "throttle-zero" or "restore-stop" or "reset-between-backslashes" or "throttle-down" or "wheel-down")
            Check(MathF.Abs(raw.Throttle + 1) < .0001f, "stop/down input failed to change persistent throttle");
        if (_proofM2Probe is "throttle-up" or "wheel-up")
            Check(MathF.Abs(raw.Throttle + .8f) < .0001f, "up input failed to step persistent throttle");
        if (_proofM2Probe is "full-us" or "full-iso")
            Check(raw.Throttle == 1, "backslash variant did not reach persistent throttle");
        if (!throttle)
            Check(MathF.Abs(sent.Throttle - (stage == "forced-throttle" ? -.34f : -1)) < .0001f, "locked/forced throttle differs from native");
        else
        {
            Check(MathF.Abs(raw.Throttle - sent.Throttle) < .0001f, "allowed throttle remained forced");
            if (_proofM2Probe is "full-us" or "full-iso") Check(sent.Throttle == 1, "backslash did not set full throttle");
            if (sent.Throttle > -.9f) Check(_ownSim.Velocity.LengthSquared() > .001f, "throttle did not move actual local ship");
        }
        bool booster = (state & ShipControlState.AfterburnerButton) != 0;
        Check(booster == (stage is "scripted-booster" or "player-booster"), "SPACE-only booster disagrees with native mask/forced action");
        string key = stage + "/" + _proofM2Probe;
        if (_proofM2Observed.Add(key))
        {
            _proofM2Stages.Add(stage);
            string receipt = FormattableString.Invariant($"[p940] CONTROL {goal} {key} raw={raw.Yaw:F4}|{raw.Pitch:F4}|{raw.Roll:F4}|{raw.Throttle:F4} applied={sent.Yaw:F4}|{sent.Pitch:F4}|{sent.Roll:F4}|{sent.Throttle:F4} booster={booster} speed={_ownSim.Velocity.Length():F4} turn={MathF.Sqrt(_ownSim.TurnRates.Sum(rate => rate * rate)):F4}");
            _proofM2Receipts.Add(receipt); Console.WriteLine(receipt);
        }
        _proofM2Step++; _proofM2Samples = 0; _proofM2WheelSent = false;
    }
}
