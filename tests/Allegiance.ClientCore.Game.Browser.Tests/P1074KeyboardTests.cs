using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1074KeyboardTests
{
    [Fact]
    public void Every_catalog_default_native_default_alternate_and_modifier_has_a_DOM_code()
    {
        var keys = new HashSet<Keys>();
        void Add(KeyBinding binding)
        {
            if (binding.Key != Keys.None) keys.Add(binding.Key);
            if (binding.Modifiers.HasFlag(InputModifiers.Control)) { keys.Add(Keys.LeftControl); keys.Add(Keys.RightControl); }
            if (binding.Modifiers.HasFlag(InputModifiers.Shift)) { keys.Add(Keys.LeftShift); keys.Add(Keys.RightShift); }
            if (binding.Modifiers.HasFlag(InputModifiers.Alt)) { keys.Add(Keys.LeftAlt); keys.Add(Keys.RightAlt); }
        }
        foreach (var def in InputActionCatalog.Definitions)
        {
            Add(def.Default);
            if (def.NativeDefault is { } native) Add(native);
            foreach (var alternate in def.Alternate ?? []) Add(alternate);
            foreach (var alternate in InputLayouts.Alternates(def.Name)) Add(alternate);
        }
        foreach (var layout in Enum.GetValues<InputLayout>())
            foreach (var binding in InputLayouts.Defaults(layout).Values) Add(binding);
        foreach (var key in keys)
            Assert.True(BrowserKeyboard.Codes.Any(pair => pair.Value == key), $"No browser code for catalog key {key}");
        Assert.Equal(Keys.None, BrowserKeyboard.Parse("OemComma"));
        Assert.Equal(Keys.None, BrowserKeyboard.Parse("keyW"));
        Assert.Equal(Keys.None, BrowserKeyboard.Parse("KeyNotAKey"));
        Assert.Equal(Keys.Enter, BrowserKeyboard.Parse("NumpadEnter"));
    }

    [Theory]
    [InlineData("Comma", Keys.OemComma)]
    [InlineData("Period", Keys.OemPeriod)]
    [InlineData("Slash", Keys.OemQuestion)]
    [InlineData("Semicolon", Keys.OemSemicolon)]
    [InlineData("Quote", Keys.OemQuotes)]
    [InlineData("Backslash", Keys.OemPipe)]
    [InlineData("IntlBackslash", Keys.OemBackslash)]
    [InlineData("NumpadAdd", Keys.Add)]
    [InlineData("NumpadSubtract", Keys.Subtract)]
    [InlineData("NumpadMultiply", Keys.Multiply)]
    [InlineData("NumpadDivide", Keys.Divide)]
    [InlineData("NumpadDecimal", Keys.Decimal)]
    [InlineData("NumpadEnter", Keys.Enter)]
    public void Actual_Game1_FlightKeys_uses_the_physical_table(string code, Keys key)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        game.FlightKeys([code]);
        var state = (KeyboardState)typeof(Game1).GetField("_flightKeys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        Assert.Equal([key], state.GetPressedKeys());
    }

    [Fact]
    public void All_numpad_digits_and_left_right_modifiers_remain_distinct()
    {
        for (int i = 0; i < 10; i++) Assert.Equal((Keys)((int)Keys.NumPad0 + i), BrowserKeyboard.Parse("Numpad" + i));
        Assert.NotEqual(BrowserKeyboard.Parse("ShiftLeft"), BrowserKeyboard.Parse("ShiftRight"));
        Assert.NotEqual(BrowserKeyboard.Parse("ControlLeft"), BrowserKeyboard.Parse("ControlRight"));
        Assert.NotEqual(BrowserKeyboard.Parse("AltLeft"), BrowserKeyboard.Parse("AltRight"));
    }

    [Theory]
    [InlineData(InputLayout.Port)]
    [InlineData(InputLayout.Native)]
    public void Browser_defaults_avoid_reserved_chords_and_help_names_the_live_map(InputLayout layout)
    {
        var map = new ActionMap(InputActionCatalog.Definitions, layout);
        foreach (var def in map.Definitions)
            Assert.Null(BrowserKeyboardBindings.ReservedReason(map.BindingOf(def.Name)));
        foreach (var rule in BrowserKeyboardBindings.ReservedDefaults)
        {
            var binding = map.BindingOf(rule.Action);
            if (binding != rule.Replacement) continue;
            Assert.Contains("(browser)", map.LabelOf(rule.Action));
            Assert.False(string.IsNullOrWhiteSpace(rule.Reason));
            // The replacement is free among the desktop defaults in this layout.
            foreach (var other in InputActionCatalog.Definitions.Where(d => d.Name != rule.Action))
            {
                var original = layout == InputLayout.Port ? other.Default : other.NativeDefault ?? other.Default;
                Assert.NotEqual(original, rule.Replacement);
            }
        }
        var groups = HelpManualModel.ControlsGroups(map, "hlpControls")
            .Concat(HelpManualModel.ControlsGroups(map, "hlpControls2")).ToArray();
        foreach (var def in map.Definitions.Where(d => map.BindingOf(d.Name).Key != Keys.None))
            Assert.Contains(groups.SelectMany(g => g.Entries), row => row.Key == map.LabelOf(def.Name) && row.Description == def.Description);
        map.SetBinding("FireMissile", new(Keys.NumPad0, 0), force: true);
        Assert.Equal("NumPad0", map.LabelOf("FireMissile"));
        Assert.Contains(map.ToHelpGroups().SelectMany(g => g.Entries), row => row.Key == "NumPad0" && row.Description.Contains("TK_FireMissile"));
        map.ResetToDefaults();
        Assert.Null(BrowserKeyboardBindings.ReservedReason(map.BindingOf("FireMissile")));
    }

    [Fact]
    public void Native_keyboard_override_preserves_other_axes_buttons_and_throttle_for_every_key_combination()
    {
        foreach (bool mouse in new[] { false, true })
        foreach (bool modifier in new[] { false, true })
        for (int mask = 0; mask < 64; mask++)
        foreach (float axis in new[] { -1f, 0f, 1f })
        {
            bool Bit(int i) => (mask & (1 << i)) != 0;
            var input = FlightInputState.None with { YawLeft = Bit(0), YawRight = Bit(1), PitchUp = Bit(2), PitchDown = Bit(3), RollLeft = Bit(4), RollRight = Bit(5), RollModifier = modifier, Throttle = .4f, Fire = true, Afterburner = true };
            var command = FlightInput.Translate(input);
            var actual = BrowserFlightSteering.Combine(command, input, mouse, axis, -axis);
            float yaw = mouse ? modifier ? 0 : axis : command.Controls.Yaw;
            float pitch = mouse ? -axis : command.Controls.Pitch;
            float roll = mouse && modifier ? axis : command.Controls.Roll;
            if (Bit(5) || Bit(4)) roll = Bit(5) ? -1 : 1;
            if (Bit(1) || Bit(0)) { if (modifier) { roll = Bit(1) ? -1 : 1; yaw = 0; } else yaw = Bit(1) ? -1 : 1; }
            if (Bit(3) || Bit(2)) pitch = Bit(3) ? -1 : 1;
            Assert.Equal(yaw, actual.Controls.Yaw); Assert.Equal(pitch, actual.Controls.Pitch); Assert.Equal(roll, actual.Controls.Roll);
            Assert.Equal(command.StateM, actual.StateM); Assert.Equal(.4f, actual.Controls.Throttle);
        }
    }

    [Fact]
    public void Browser_missile_does_not_turn_held_thrust_into_Ctrl_W()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var keys = BrowserKeyboard.State(["CapsLock", "KeyW"]);
        Assert.True(map.IsDownMostSpecific(keys, "FireMissile"));
        Assert.True(map.IsDownMostSpecific(keys, "ThrustForward"));
        Assert.False(keys.IsKeyDown(Keys.LeftControl));
    }

    [Fact]
    public void Target_projection_uses_the_rendered_eye_and_clips_to_the_actual_frustum()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Game1).GetField("_browserTargetEye", flags)!.SetValue(game, new Microsoft.Xna.Framework.Vector3(10, 0, 0));
        var frustum = new Microsoft.Xna.Framework.BoundingFrustum(Microsoft.Xna.Framework.Matrix.Identity);
        typeof(Game1).GetField("_rockFrustum", flags)!.SetValue(game, frustum);
        var project = typeof(Game1).GetMethod("ProjectBrowserTarget", flags)!;
        object? Project(float x) => project.Invoke(game, [new BrowserTargeting.Choice(IgcObjectType.Ship, 1, "target", new(x, .25f, 0), default, 1)]);
        Assert.Equal((.25f, .25f), Project(10.25f));
        Assert.Null(Project(12));
        var behind = Microsoft.Xna.Framework.Matrix.Identity;
        behind.M44 = -1;
        frustum.Matrix = behind;
        Assert.Null(Project(10.25f));
    }

    [Fact]
    public void Target_center_cycle_and_friendly_home_filter_have_observable_selections()
    {
        var choices = new[]
        {
            new BrowserTargeting.Choice(IgcObjectType.Ship, 1, "front", new(0,0,-20), default, 1, 1),
            new BrowserTargeting.Choice(IgcObjectType.Ship, 2, "next", new(2,0,-20), default, 1, 1),
            new BrowserTargeting.Choice(IgcObjectType.Ship, 3, "behind", new(0,0,10), default, 1, 1),
            new BrowserTargeting.Choice(IgcObjectType.Station, 4, "home", new(10,0,0), default, 1, 0, DataStationTypeIGC.SabmLand),
            new BrowserTargeting.Choice(IgcObjectType.Station, 5, "enemy", new(1,0,0), default, 1, 1, DataStationTypeIGC.SabmLand),
        };
        BrowserTargeting.Choice? Pick(string action, BrowserTargeting.Choice? selected = null) => BrowserTargeting.Select(action, choices, selected,
            default, 1, 0, DataHullTypeIGC.HabmFighter, (_, _) => false,
            c => c.Position.Z >= 0 ? null : (c.Position.X / -c.Position.Z, c.Position.Y / -c.Position.Z));
        var center = Pick("TargetCenter"); Assert.Equal((short)1, center?.Id);
        Assert.Equal((short)2, Pick("Target", center)?.Id);
        Assert.Equal((short)1, Pick("Target", Pick("Target", center))?.Id);
        Assert.Equal((short)4, Pick("TargetFriendlyBaseNearest")?.Id);
        Assert.Equal((short)5, Pick("TargetEnemyBaseNearest")?.Id);
        Assert.Null(Pick("TargetNothing"));
    }
}
