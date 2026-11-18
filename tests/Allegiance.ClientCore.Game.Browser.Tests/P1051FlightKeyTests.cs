using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1051FlightKeyTests
{
    [Theory]
    [InlineData("Backslash", "ThrottleFull")]
    [InlineData("IntlBackslash", "ThrottleFull")]
    [InlineData("Backspace", "ThrottleZero")]
    [InlineData("BracketLeft", "PrevWeapon")]
    [InlineData("BracketRight", "NextWeapon")]
    public void Physical_DOM_key_reaches_the_existing_desktop_action(string code, string action)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        game.FlightKeys([code]);
        var keys = (KeyboardState)typeof(Game1).GetField("_flightKeys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.True(map.IsDownMostSpecific(keys, action));
    }
}
