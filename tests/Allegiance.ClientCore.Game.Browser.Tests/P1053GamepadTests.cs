using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1053GamepadTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Game1 Game()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_gamePadFlight", new JoystickFlightController());
        Set(game, "_mouseFlight", new MouseFlightController());
        Set(game, "_groupFire", true); Set(game, "_throttle", -1f);
        return game;
    }
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, Private)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Private)!.GetValue(game)!;
    private static FlightCommand Apply(Game1 game) => (FlightCommand)typeof(Game1).GetMethod("ApplyGamePadFlight", Private)!
        .Invoke(game, [new FlightCommand(ControlData.Neutral, 0)])!;

    [Fact]
    public void Standard_pad_turn_and_fire_equal_keyboard_and_native_throttle_latch()
    {
        var game = Game();
        game.FlightGamepad([0,0,0,0], new float[17]);
        Assert.Equal(-1, Apply(game).Controls.Throttle); // first sample retains keyboard authority
        var buttons = new float[17]; buttons[0] = 1;
        game.FlightGamepad([0,1,-1,1], buttons);
        var pad = Apply(game);
        Assert.Equal(1, pad.Controls.Yaw); Assert.Equal(1, pad.Controls.Pitch); Assert.Equal(1, pad.Controls.Throttle);
        Assert.Equal(FlightInput.FireContribution(true, true, 0), pad.StateM);
        game.FlightGamepad([0,0,0,0], new float[17]);
        Assert.Equal(0, Apply(game).Controls.Throttle); // native centered analog throttle after the latch
    }

    [Theory]
    [InlineData(.05f, 0f)]
    [InlineData(.1f, 0f)]
    [InlineData(.55f, -.5f)]
    public void Turn_uses_desktop_point_one_deadzone_and_rescale(float raw, float expected)
    {
        var game = Game(); game.FlightGamepad([0,0,raw,0], []);
        Assert.Equal(expected, Apply(game).Controls.Yaw, .00001f);
    }

    [Fact]
    public void Shoulder_edges_trigger_thresholds_and_release_share_desktop_rules()
    {
        var game = Game(); var buttons = new float[17]; buttons[5] = 1; buttons[6] = .51f; buttons[7] = .51f;
        game.FlightGamepad([0,0,0,0], buttons);
        var cmd = Apply(game);
        Assert.Equal(ShipControlState.AfterburnerButton | FlightInput.FireContribution(true, true, 0), cmd.StateM);
        Assert.Equal(FlightInput.StepThrottleUp(-1), Get<float>(game, "_throttle"));
        Apply(game); Assert.Equal(FlightInput.StepThrottleUp(-1), Get<float>(game, "_throttle"));
        game.ReleaseFlightInput();
        Assert.Null(Get<GamePadState?>(game, "_browserPad")); Assert.False(Get<bool>(game, "_padRbWasDown"));
        Assert.Equal(ControlData.Neutral, Apply(game).Controls);
        game.FlightGamepad([0,0,0,0], []); Assert.Equal(-1, Apply(game).Controls.Throttle);
    }
}
